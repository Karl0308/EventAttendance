using System.Globalization;
using EAMS.Api.Authorization;
using EAMS.Api.OpenApi;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using DeviceKey = EAMS.Domain.DeviceKey;

namespace EAMS.Api.Controllers;

/// <summary>
/// The device-facing half of the events resource: <c>GET /events/{id}/manifest</c>, the offline capture
/// cache a kiosk or handset pulls before it starts scanning (D-46).
///
/// <para>
/// <b>Its own controller rather than one more action on <c>EventsController</c>, and that is a
/// deliberate split.</b> Every action there is <c>events.read</c> or <c>events.write</c> and is open
/// under ADR-001 D-6; this one authenticates a <em>device</em> and carries
/// <c>attendance.capture</c>. One action with a different principal type in the middle of an
/// administrative controller is a review hazard in both directions — a reader skimming
/// <c>EventsController</c> would read the whole file as open, and an action added later to a
/// capture-only controller inherits capture auth rather than landing open by accident. The route is
/// identical either way; only the file is different.
/// </para>
///
/// <para>
/// <b><c>events.read</c> is rejected outright as the permission for this.</b> A device holding it could
/// read every event and every roster in the school, which is exactly the blast radius a stolen kiosk
/// key must not have. §11 scopes device keys to <c>attendance.capture</c> and nothing else, so this
/// endpoint is already pre-authorized by the credential a capture device holds — and minting an
/// <c>events.manifest</c> code would repeat D-45, forcing Phase 6 to grant two codes for one capability.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/events")]
public class EventManifestController : ControllerBase
{
    /// <summary>
    /// The machine-readable half of §6's RFC 7807 body — the same property name every other surface on
    /// this API stamps, so a client has one accessor for every error it can receive.
    /// </summary>
    private const string ErrorCodeProperty = "code";

    /// <summary>
    /// The <c>ManifestTooLarge</c> body's extension carrying <see cref="EventManifestLimits.MaxAttendees"/>,
    /// mirroring <c>BatchTooLarge</c>'s <c>maxBatchRows</c>. Echoed so the operator report says how far
    /// over the ceiling the event is without anyone having to look the number up.
    /// </summary>
    private const string MaxAttendeesProperty = "maxAttendees";

    /// <summary>
    /// The count that was refused, beside the ceiling it exceeded.
    ///
    /// <para>
    /// <b>Named <c>attendeeCount</c> rather than <c>attendees</c>, and the difference is not cosmetic.</b>
    /// This endpoint's 200 body already publishes <c>attendees</c> as an array of objects; reusing the
    /// name here for an integer would give one operation two incompatible JSON types under one field
    /// name. A hand-written client shrugs at that and a typed one cannot — a generated model with an
    /// <c>attendees</c> property is either a list or a number, and whichever it picks, the other
    /// response fails to deserialize. The precedent this mirrors, <c>BatchTooLarge</c>'s
    /// <c>maxBatchRows</c>, echoes only the limit and so never had the collision to avoid. Renaming is
    /// free today and breaking the moment a typed client exists.
    /// </para>
    /// </summary>
    private const string AttendeeCountProperty = "attendeeCount";

    /// <summary>
    /// <c>private, no-cache</c> — <b>may store, must revalidate</b>, which is the exact semantics this
    /// endpoint wants and what makes <c>If-None-Match</c> the mandated flow rather than a suggestion.
    ///
    /// <para>
    /// <b><c>no-store</c> would be wrong here</b>, and it is the reflex on an endpoint that returns a
    /// roster: it forbids the device's own cache, which <em>is</em> the entire feature. <c>private</c>
    /// is what keeps a shared intermediary out of it.
    /// </para>
    /// </summary>
    private const string ManifestCacheControl = "private, no-cache";

    /// <summary>
    /// The manifest is a function of the credential's tenant, so a cache keyed on the URL alone would
    /// be keyed on the wrong thing. Named for completeness rather than because a shared cache should
    /// ever hold this — <see cref="ManifestCacheControl"/> already says <c>private</c>.
    /// </summary>
    private const string VaryOnAuthorization = "Authorization";

    private readonly IEventService _events;
    private readonly ILogger<EventManifestController> _logger;

    public EventManifestController(IEventService events, ILogger<EventManifestController> logger)
    {
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// <c>GET /events/{id}/manifest</c> — the offline capture cache: who is expected at this event and
    /// which card resolves to whom.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the invitation, not the roster.</b> It carries nothing about who has already tapped —
    /// that is <c>GET /attendance/live/{eventId}</c> — because a manifest carrying attendance state
    /// would read as authoritative on the device, and the one thing this object must never be is
    /// authoritative.
    /// </para>
    ///
    /// <para>
    /// <b>Offline validation against it is display-only and never gating.</b> A tap whose card UID is
    /// absent from the manifest <b>must still be queued and flushed</b>. The server rules on it and it
    /// lands as a walk-in (<c>isExpected: false</c>). A client that refuses to capture an unknown card
    /// turns "my cache is stale" into "that attendance never happened", and afterwards the two are
    /// indistinguishable.
    /// </para>
    ///
    /// <para>
    /// <b>Send the previous <c>version</c> back as <c>If-None-Match</c> on every pull.</b> Unchanged is
    /// a <c>304</c> with no body — keep what you have. The comparison is weak (<c>W/</c>) and lenient
    /// about quoting, so sending the header back verbatim always works. <b>Never parse the version and
    /// never order it</b>; compare it for equality only.
    /// </para>
    ///
    /// <para>
    /// <b>Check <c>serverTime</c> against the device clock before enabling scan mode</b>: more than five
    /// minutes apart, do not scan. Every tap captured past that threshold arrives as
    /// <c>TappedAtOutOfRange</c> — poison, dropped by your own queue, attendance gone. A <c>304</c>
    /// carries no <c>serverTime</c>; take the offset from the HTTP <c>Date</c> header instead, because
    /// the manifest not changing says nothing about the clock.
    /// </para>
    ///
    /// <para>
    /// <b>Two obligations that hold across every response, including the failures.</b> <b>Never clear
    /// the cached manifest on a failure</b> — a client that wipes on error degrades from slightly-stale
    /// names to no names, and does it exactly when the network is worst; the only sanctioned discard is
    /// after the queue drains on a terminal event. And <b>no refusal here ever stops tap capture</b>:
    /// capture and queueing continue whatever this endpoint returns. Only the <em>flush</em> pauses, and
    /// only on <c>401</c> or <c>403</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Read the HTTP status first and <c>code</c> second; never branch on <c>title</c> or
    /// <c>detail</c>.</b> Only <c>EventFrozen</c> and <c>ManifestTooLarge</c> are new tokens — the rest
    /// are the ones the capture path already uses, with the same meanings.
    /// </para>
    ///
    /// <para>
    /// <b>Event-scoped, not occurrence-scoped.</b> §4.8 <c>EventGroups</c> carries no
    /// <c>OccurrenceId</c>, so one manifest serves every occurrence of a recurring event and
    /// <c>startAt</c>/<c>endAt</c> are the template's. Recurrence is unbuilt; an <c>occurrenceId</c>
    /// parameter is additive when it lands.
    /// </para>
    /// </remarks>
    /// <param name="id">The event. Must be <c>Open</c> — see the 409s.</param>
    /// <param name="clientClockAt">
    /// Optional. Your own clock, ISO 8601, when you made this request.
    ///
    /// <para>
    /// <b>Measured, logged, and never validated — it can never cause a refusal</b>, and a value we
    /// cannot parse is simply not measured rather than a <c>400</c>. It mirrors
    /// <c>TapBatchRequest.clientClockAt</c> exactly, and its value is that it is the same quantity as
    /// flush-time skew measured hours earlier: a device that pulls at 08:00 and flushes at 15:00 gives
    /// us no drift signal for seven hours, during which every tap it captured is already unrecoverable.
    /// </para>
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">
    /// The manifest. <b>Replace your cached copy wholesale</b> — this is not a delta and there is no
    /// merge that is correct. Store <c>version</c>, then check <c>serverTime</c> against your clock
    /// before you enable scanning.
    /// </response>
    /// <response code="304">
    /// Unchanged since the <c>version</c> you sent. Keep the cached copy; take your clock offset from
    /// the <c>Date</c> header, since there is no body.
    /// </response>
    /// <response code="400">
    /// <c>{id}</c> is not a GUID. A bug on your side — stop, and do not retry: the same URL is refused
    /// forever. This is the ordinary model-validation body and carries no <c>code</c>.
    /// </response>
    /// <response code="401">
    /// <c>DeviceKeyMissing</c> / <c>DeviceKeyMalformed</c> / <c>DeviceKeyInvalid</c>. Stop and do not
    /// retry: re-check the stored credential, then re-enrol. Keep the cached manifest and the queue, and
    /// pause the flush until it is fixed.
    /// </response>
    /// <response code="403">
    /// <c>DeviceKeyRevoked</c> / <c>DeviceInactive</c>. Stop and re-enrol. Taps fail identically, so
    /// pause the flush too — but keep the cached manifest and keep capturing.
    /// </response>
    /// <response code="404">
    /// <c>EventNotFound</c> — no such event, <b>or</b> one belonging to another school, which is
    /// deliberately indistinguishable (D-27: no cross-tenant existence disclosure). Stop and tell the
    /// operator.
    /// </response>
    /// <response code="409">
    /// <c>EventNotOpen</c> — the event is still <c>Draft</c>. Stop and tell the operator it has not been
    /// opened yet.
    ///
    /// <para>
    /// Or <c>EventFrozen</c> — the event is <c>Closed</c> or <c>Cancelled</c>. <b>Flush the queue
    /// first, then stop</b> and tell the operator the event is over. Do not discard the cached manifest
    /// until that queue is empty.
    /// </para>
    /// </response>
    /// <response code="413">
    /// <c>ManifestTooLarge</c> — the event expects more attendees than a manifest carries. Stop, tell
    /// the operator, and report it to us. Deliberately loud rather than truncated, and unlike
    /// <c>BatchTooLarge</c> there is nothing for you to halve.
    /// </response>
    /// <response code="429">
    /// <c>RateLimited</c>. Not an error — retry, honouring <c>Retry-After</c>. Keep capturing meanwhile.
    /// </response>
    // The route parameter is deliberately NOT constrained `{id:guid}`, which is what every sibling
    // events route uses. A constraint makes a malformed id fail to *match the route*, so
    // `/events/banana/manifest` is a bare 404 — and the frozen table binds 404 to `EventNotFound`,
    // whose published client reaction is "stop and tell the operator". A device that sent a malformed
    // id has a bug on its own side and must be told so: unconstrained, [ApiController]'s model
    // validation answers 400, which is the row the contract actually specifies for it.
    [HttpGet("{id}/manifest")]
    [Authorize(AuthenticationSchemes = DeviceKey.AuthenticationScheme, Policy = EamsPermissions.AttendanceCapture)]
    [EnableRateLimiting(CaptureRateLimiting.ManifestPolicyName)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceCapture)]
    // Publishes If-None-Match, ETag, Cache-Control and Retry-After into the generated document. The
    // prose below already describes the flow; a generated client cannot read prose, and without this
    // it would see a 200 with a body and a 304 with nothing to send back — so it would re-download the
    // whole manifest on every refresh, which is the one thing this endpoint exists not to do.
    [ConditionalGet]
    [ProducesResponseType(typeof(EventManifestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<EventManifestDto>> Manifest(
        Guid id, [FromQuery] string? clientClockAt, CancellationToken ct)
    {
        // Our clock on arrival, which is what the skew measurement is against — deliberately not the
        // manifest's own serverTime, which is read a moment later when the content is composed. The
        // difference is microseconds and the distinction is the point: this measures the request, that
        // one describes the body.
        LogClockSkew(clientClockAt, DateTime.UtcNow);

        var response = await _events.GetManifestAsync(id, ct);

        // Set before the outcome is known so they cover the refusals as well as the 200 and the 304.
        // A cacheable 409 is the nastier case: an intermediary replaying it would pin a device at "that
        // event is not open" long after the organizer had opened it.
        Response.Headers.CacheControl = ManifestCacheControl;
        Response.Headers.Vary = VaryOnAuthorization;

        if (response.Outcome != ManifestOutcome.Ok) return Refusal(response);

        var manifest = response.Manifest!;

        // On the 200 and on the 304 alike. A 304 without an ETag leaves a client that has lost its
        // stored version with nothing to revalidate against next time, which turns every subsequent
        // pull into a full download of a body it already holds.
        Response.Headers.ETag = EventManifestVersion.ETagFor(manifest.Version);

        if (EventManifestVersion.Matches(Request.Headers.IfNoneMatch, manifest.Version))
            return StatusCode(StatusCodes.Status304NotModified);

        return Ok(manifest);
    }

    /// <summary>
    /// §6's declared error shape (RFC 7807), built through
    /// <see cref="ControllerBase.ProblemDetailsFactory"/> so the <c>traceId</c> is stamped once, in
    /// <c>TracedProblemDetailsFactory</c> — the same seam every other failure on this API uses.
    /// </summary>
    private ObjectResult Refusal(EventManifestResponse response)
    {
        var status = StatusCodeFor(response.Outcome);

        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(response.Outcome),
            detail: response.Message);

        // Derived from the outcome in one place, so the token a client branches on cannot drift from
        // the status it arrives with.
        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        if (response.Outcome == ManifestOutcome.ManifestTooLarge)
        {
            problem.Extensions[AttendeeCountProperty] = response.Attendees;
            problem.Extensions[MaxAttendeesProperty] = EventManifestLimits.MaxAttendees;
        }

        return StatusCode(status, problem);
    }

    /// <summary>
    /// The human half of the problem body. Prose, and deliberately so — a client branches on
    /// <c>code</c>; this is what an operator reads in a log or a support ticket.
    /// </summary>
    private static string TitleFor(ManifestOutcome outcome) => outcome switch
    {
        ManifestOutcome.EventNotFound => "Event not found.",
        ManifestOutcome.EventNotOpen => "That event is not open yet.",
        ManifestOutcome.EventFrozen => "That event is over.",
        ManifestOutcome.ManifestTooLarge => "That event's manifest is too large to publish.",
        _ => "The manifest could not be produced.",
    };

    /// <summary>
    /// The §6 status-code contract for <c>GET /events/{id}/manifest</c>, as one total function over
    /// <see cref="ManifestOutcome"/>.
    ///
    /// <para>
    /// <b>Every member is listed rather than a <c>_ =&gt; Ok(...)</c> fall-through</b>, for the reason
    /// <c>AttendanceController.StatusCodeFor</c> records at length: a discard arm makes "an outcome
    /// nobody mapped" indistinguishable from "an outcome that means success", and ships a refusal as a
    /// 200. Here that would be a device caching an empty manifest as authoritative. The final arm has
    /// to exist — C# does not treat a fully enumerated enum switch as exhaustive — but it throws rather
    /// than guessing.
    /// </para>
    ///
    /// <para>
    /// <b>Both refusals about status are 409 rather than 400 or 404.</b> The request is well formed and
    /// would be served against the same event in another state, which is what 409 is for — the same
    /// split <c>EventsController.StatusCodeFor</c> draws between <c>EventLocked</c> and
    /// <c>IllegalTransition</c>. A 404 would additionally tell a device the event does not exist, and it
    /// would then stop asking about an event that is about to open.
    /// </para>
    /// </summary>
    internal static int StatusCodeFor(ManifestOutcome outcome) => outcome switch
    {
        ManifestOutcome.Ok => StatusCodes.Status200OK,

        ManifestOutcome.EventNotFound => StatusCodes.Status404NotFound,

        // The event exists and its state is what refuses. See the method remarks.
        ManifestOutcome.EventNotOpen
            or ManifestOutcome.EventFrozen => StatusCodes.Status409Conflict,

        // The response we would have to send is the thing that is out of bounds, and it is ours rather
        // than the caller's — but it is still a 4xx, because retrying it unchanged cannot help and the
        // client is the only party that can act (by telling someone).
        ManifestOutcome.ManifestTooLarge => StatusCodes.Status413PayloadTooLarge,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(ManifestOutcome)}. Every outcome must be " +
            "mapped explicitly."),
    };

    /// <summary>
    /// Records how far the device's clock is from ours, and nothing else — this never affects the
    /// response.
    ///
    /// <para>
    /// <b>Bound as a <c>string</c> rather than a <c>DateTime?</c>, and that is what keeps the frozen
    /// promise.</b> <c>[ApiController]</c> turns a query value that fails to bind into an automatic
    /// <c>400</c>, so <c>?clientClockAt=banana</c> against a <c>DateTime?</c> parameter would refuse a
    /// pull because of a field the contract says can never cause a refusal — and it would refuse it
    /// before this method ran, where no amount of leniency here could help. Parsed by hand, an
    /// unparseable value is simply not measured, which is what the tap contract already says about a
    /// client that omits it.
    /// </para>
    ///
    /// <para>
    /// The threshold is <see cref="TapTimeWindow.FutureToleranceMinutes"/> — the same number
    /// <c>AttendanceService.LogClockSkew</c> uses, so a drifting device appears in both logs or in
    /// neither. Logging every pull would drown it; logging none of them is why drift is currently only
    /// discovered from an attendance report weeks later.
    /// </para>
    /// </summary>
    private void LogClockSkew(string? clientClockAt, DateTime receivedAt)
    {
        if (string.IsNullOrWhiteSpace(clientClockAt)) return;

        // RoundtripKind alone, and it must be alone: combining it with AdjustToUniversal (or either
        // Assume* flag) is an ArgumentException rather than a parse failure, which would have turned
        // this measurement into a 500 on the one field the contract says can never cause a refusal.
        // It preserves the Kind the text carried; UtcTime.Normalize below is what converts.
        if (!DateTime.TryParse(
                clientClockAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            _logger.LogInformation(
                "A manifest pull carried an unparseable clientClockAt ('{Value}'), so this device's " +
                "clock was not measured. It is not a refusal and nothing about the response changed. " +
                "Send an ISO 8601 timestamp, for example {Example:O}.",
                clientClockAt, receivedAt);
            return;
        }

        var skew = UtcTime.Normalize(parsed) - receivedAt;
        if (Math.Abs(skew.TotalMinutes) <= TapTimeWindow.FutureToleranceMinutes) return;

        _logger.LogWarning(
            "Device clock skew of {SkewSeconds:F0}s on a manifest pull: the client reported " +
            "{ClientClock:O} while the server clock was {ServerTime:O}. This is pure skew — it carries " +
            "no queue dwell — and taps from this device will start being refused as {Token} once it " +
            "exceeds what a single tap's own timestamp can absorb. The client is expected to correct " +
            "itself from the serverTime in this response and to refuse to scan while it cannot.",
            skew.TotalSeconds, UtcTime.Normalize(parsed), receivedAt,
            nameof(TapOutcome.TappedAtOutOfRange));
    }
}
