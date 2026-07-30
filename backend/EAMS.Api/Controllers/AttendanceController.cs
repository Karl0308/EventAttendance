using System.ComponentModel.DataAnnotations;
using EAMS.Api.Authorization;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EAMS.Api.Controllers;

[ApiController]
[Route("api/v1/attendance")]
public class AttendanceController : ControllerBase
{
    /// <summary>
    /// The machine-readable half of §6's RFC 7807 body — the same property name
    /// <c>StudentsController</c>, <c>DevicesController</c>, <c>DeviceKeyHandler</c> and the capture
    /// rate limiter already stamp, so a client has one accessor for every error this API produces.
    ///
    /// <para>
    /// Phase 4c D-37 makes it one accessor for every <em>success</em> too: <c>TapResult.Code</c>
    /// serializes to the same <c>code</c>. That is the reason the success field is not called
    /// <c>outcome</c>.
    /// </para>
    /// </summary>
    internal const string ErrorCodeProperty = "code";

    /// <summary>
    /// The server's clock, on the error bodies as well as the success ones. The published contract
    /// promises <c>serverTime</c> on every response, and the response that needs it most is
    /// <c>TappedAtOutOfRange</c> — a device is being told its clock is wrong, so it has to be told what
    /// the right one is in the same body.
    /// </summary>
    internal const string ServerTimeProperty = "serverTime";

    /// <summary>
    /// The <c>BatchTooLarge</c> problem body's extension carrying <see cref="TapBatchLimits.MaxRows"/>.
    ///
    /// <para>
    /// The published contract tells the mobile client to read the real limit from the response rather
    /// than hard-coding 200, which is the whole reason the number is echoed: raising or lowering the cap
    /// then costs no client release. A client that hard-codes it is not broken by this, it just does not
    /// benefit.
    /// </para>
    /// </summary>
    internal const string MaxBatchRowsProperty = "maxBatchRows";

    private readonly IAttendanceService _attendance;

    /// <summary>
    /// The live endpoint reads through <see cref="IEventService"/>, not <see cref="IAttendanceService"/>,
    /// and the route being <c>/attendance/live/{eventId}</c> is not evidence against that — see
    /// <c>IEventService.GetLiveAttendanceAsync</c>. Its <c>counters</c> block is the summary's own
    /// object, and ADR-003 D-12/D-13 record that the denominator behind it fails <em>silently</em> when
    /// it is duplicated. One extra constructor argument is the cheapest possible way to not duplicate it.
    /// </summary>
    private readonly IEventService _events;

    public AttendanceController(IAttendanceService attendance, IEventService events)
    {
        _attendance = attendance;
        _events = events;
    }

    /// <summary>
    /// <c>GET /attendance</c> — recorded attendance rows, every filter optional.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A back-office read rather than something to poll: offset-paged, newest check-in first. The live
    /// dashboard's read is <c>GET /attendance/live/{eventId}</c>, which is cursored.
    /// </para>
    ///
    /// <para>
    /// <b>The two are not variations on one endpoint and must not be made into one.</b> This answers
    /// "give me rows 40 to 60 of what matched"; the live one answers "what changed after this cursor",
    /// and its <c>since</c>/<c>cursor</c>/<c>hasMore</c> shape is frozen published contract
    /// (D-29/D-30/D-42) that a mobile client polls today. Offset paging over a live feed also loses
    /// rows outright — an insert below the offset shifts everything up by one and the next page skips
    /// it — which is exactly why that endpoint was built on a <c>rowversion</c> cursor instead.
    /// </para>
    /// </remarks>
    /// <param name="eventId">Restrict to one event.</param>
    /// <param name="studentId">Restrict to one student.</param>
    /// <param name="status">One of <c>Present</c>, <c>Late</c>, <c>Absent</c>, <c>Excused</c>.</param>
    /// <param name="page">
    /// 1-based page number, default 1. Out-of-range values are clamped, never refused; the response
    /// echoes the page actually served.
    /// </param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200 — a larger value is clamped to the maximum and the
    /// response says so in its own <c>pageSize</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of matching rows, possibly empty. Never a 404 for an empty filter.</response>
    [HttpGet]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceRead)]
    [ProducesResponseType(typeof(PagedResult<AttendanceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AttendanceDto>>> List(
        [FromQuery] Guid? eventId, [FromQuery] Guid? studentId, [FromQuery] string? status,
        [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => Ok(await _attendance.ListAsync(
            eventId, studentId, status, PageRequest.From(page, pageSize), ct));

    /// <summary>
    /// <c>POST /attendance/tap</c> — the core capture path (Technical Plan §6.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One tap, decided by the same function every row of <c>POST /attendance/tap/batch</c> goes
    /// through. Resolve the card UID → check the event and its window → absorb a replay by
    /// <c>deviceTapId</c> → write or update the row → decide Present versus Late from
    /// <c>tappedAt</c> and the event's <c>graceMinutes</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Branch on <c>code</c>, never on <c>message</c>.</b> The token set and the status each one
    /// arrives with are frozen contract — see the <c>TapOutcomeCode</c> schema. A success is a
    /// <c>TapResult</c>; a rejection is an RFC 7807 body carrying the same <c>code</c> and
    /// <c>serverTime</c>, so one accessor reads both.
    /// </para>
    ///
    /// <para>
    /// <b>Always send a <c>deviceTapId</c>, and never regenerate it on retry.</b> It is optional here
    /// and required on the batch endpoint, but it is what makes a lost response recoverable: a replay
    /// comes back <c>DuplicateIgnored</c> instead of writing a second row.
    /// </para>
    ///
    /// <para>
    /// <b>Send an explicit <c>tappedAt</c> too.</b> With <c>null</c> the instant being validated is
    /// re-derived from our clock on <em>every</em> attempt, so a tap that landed at 10:00 and is
    /// retried at 12:30 — after the event's window closed — comes back
    /// <c>400 TappedAtOutsideEventWindow</c> rather than the <c>DuplicateIgnored</c> you would expect.
    /// The stored row is correct either way; only reconciliation by code is wrong for it.
    /// </para>
    /// </remarks>
    /// <param name="req">The tap. See <c>TapRequest</c> for the per-field rules.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">
    /// <c>Recorded</c>, <c>DuplicateIgnored</c>, <c>CheckedOut</c> or <c>AlreadyRecorded</c>. All four
    /// mean the client can drop the tap from its queue.
    /// </response>
    /// <response code="400">
    /// <c>EventNotOpen</c>, <c>DeviceMismatch</c>, <c>TappedAtOutOfRange</c> or
    /// <c>TappedAtOutsideEventWindow</c>. Stop retrying — the same request will be refused forever.
    /// </response>
    /// <response code="401">No device key, or one that is malformed or unknown.</response>
    /// <response code="403">The key was revoked, or the device was retired.</response>
    /// <response code="404">
    /// <c>EventNotFound</c>, <c>CardNotFound</c> or <c>DeviceNotRegistered</c>. The last also covers a
    /// device belonging to another school — deliberately indistinguishable.
    /// </response>
    /// <response code="429">Capture rate limit. Honour <c>Retry-After</c>.</response>
    // The workflow lives in IAttendanceService; this maps its outcome to an HTTP status.
    //
    // One of the four endpoints a device key gates (Phase 4a design, D-28). [HasPermissionNotEnforced]
    // stays alongside [Authorize] deliberately: the inert attribute is the audit trail Phase 6's rename
    // walks, and removing it here because "this one is real now" would put a hole in exactly the list
    // ADR-001 D-6 created the attribute to keep complete. AuthorizationSeamTests asserts the two never
    // disagree about which permission this endpoint demands, and that a gated endpoint always names a
    // policy — a bare [Authorize] would fall back to RequireAuthenticatedUser(), which a revoked key
    // satisfies by design.
    [HttpPost("tap")]
    [Authorize(AuthenticationSchemes = DeviceKey.AuthenticationScheme, Policy = EamsPermissions.AttendanceCapture)]
    [EnableRateLimiting(CaptureRateLimiting.PolicyName)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceCapture)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(TapResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TapResult>> Tap([FromBody] TapRequest req, CancellationToken ct)
    {
        var response = await _attendance.TapAsync(req, ct);
        return Respond(StatusCodeFor(response.Outcome), response.Result, TitleFor(response.Outcome));
    }

    /// <summary>
    /// <c>POST /attendance/tap/batch</c> — §8.2's offline queue flush (Phase 4d, D-31).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A well-formed batch is always 200, never 207.</b> The transport status describes the batch;
    /// each row's own <c>status</c> describes that tap. 207 Multi-Status is handled inconsistently by
    /// proxies and client libraries, and — the part that bites — a client seeing any non-2xx is liable
    /// to retry the entire batch, which is safe but pure waste when almost all of it landed.
    /// </para>
    ///
    /// <para>
    /// <b>The fourth and last endpoint a device key gates</b> (D-28). <c>[HasPermissionNotEnforced]</c>
    /// stays alongside <c>[Authorize]</c> for the reason the tap endpoint records: the inert attribute is
    /// the list Phase 6's rename walks, and the enforced-first endpoints are the ones a reader is least
    /// likely to check. The <c>Policy</c> is named rather than left to the default — a bare
    /// <c>[Authorize]</c> falls back to <c>RequireAuthenticatedUser()</c>, which a revoked key and a
    /// deactivated device both satisfy by design, so it would admit exactly the credentials the 403
    /// exists to refuse. <c>AuthorizationSeamTests</c> fails the build on either mistake.
    /// </para>
    ///
    /// <para>
    /// <b>Two size guards, and neither replaces the other.</b> <c>[RequestSizeLimit]</c> refuses an
    /// oversized body before it is buffered; <see cref="TapBatchLimits.MaxRows"/> is checked in the service
    /// after deserialization, when the row count is knowable. A body of 200 enormous rows passes the
    /// first and fails nothing; a body of one row and four megabytes of whitespace passes the second.
    /// </para>
    ///
    /// <para>
    /// <b>It shares <c>/tap</c>'s rate-limit policy, so one flush of 200 taps spends one permit where
    /// 200 single taps would spend 200. That is intended, and it is recorded because nothing else would
    /// say so.</b> The limiter's stated purpose is friction and detection against a runaway client, not
    /// a contractual quota on taps — and a device that batches is being <em>well behaved</em>, so
    /// charging it per row would penalise the shape §8.2 asks for and make the published
    /// {PermitsPerWindow}-per-minute figure mean two different things depending on which endpoint was
    /// used. What it does mean, stated plainly: the effective per-device write ceiling on this endpoint
    /// is <c>PermitsPerWindow × MaxRows</c> — 120,000 taps a minute — so the limiter is not what bounds
    /// batch write volume. The row cap and the body cap are. If a per-row budget is ever wanted, it
    /// needs a token-bucket policy that charges by row count, which the fixed-window limiter cannot
    /// express.
    /// </para>
    /// </remarks>
    /// <param name="req">The flush. See <c>TapBatchRequest</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">
    /// <b>Every well-formed batch, including an empty one.</b> <c>results</c> is dense — one entry per
    /// submitted tap, in the order you sent them, so <c>results[i].index == i</c> and
    /// <c>accepted + rejected == results.length</c>. Each row's own <c>status</c> is the status that tap
    /// would have received as a single <c>POST /attendance/tap</c>.
    ///
    /// <para>
    /// On a <c>5xx</c>, retry the whole batch — that is always safe. Safe <b>because every row is
    /// idempotent</b>, not because the batch is atomic: rows commit individually, so a server error
    /// partway through leaves the earlier rows written and the retry absorbs them as
    /// <c>DuplicateIgnored</c>.
    /// </para>
    /// </response>
    /// <response code="400">
    /// Transport or size only: a malformed body, or <c>BatchTooLarge</c>. The refusal echoes the limit
    /// as <c>maxBatchRows</c> in the problem body, so chunk from the response rather than hard-coding
    /// the number. Nothing about the content of an individual row can produce a batch-level 4xx.
    /// </response>
    /// <response code="401">No device key, or one that is malformed or unknown.</response>
    /// <response code="403">The key was revoked, or the device was retired.</response>
    /// <response code="429">Capture rate limit. One flush spends one permit, whatever its row count.</response>
    [HttpPost("tap/batch")]
    [Authorize(AuthenticationSchemes = DeviceKey.AuthenticationScheme, Policy = EamsPermissions.AttendanceCapture)]
    [EnableRateLimiting(CaptureRateLimiting.PolicyName)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceCapture)]
    [RequestSizeLimit(TapBatchLimits.MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(TapBatchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TapBatchResult>> TapBatch(
        [FromBody] TapBatchRequest req, CancellationToken ct)
    {
        var response = await _attendance.TapBatchAsync(req, ct);

        if (response.Refusal is { } refusal)
        {
            var status = StatusCodeFor(refusal.Outcome);
            var problem = ProblemDetailsFactory.CreateProblemDetails(
                HttpContext, statusCode: status, title: TitleFor(refusal.Outcome),
                detail: refusal.Result.Message);

            problem.Extensions[ErrorCodeProperty] = refusal.Result.Code;
            problem.Extensions[ServerTimeProperty] = refusal.Result.ServerTime;
            problem.Extensions[MaxBatchRowsProperty] = TapBatchLimits.MaxRows;

            return StatusCode(status, problem);
        }

        // The same outcome→status map the single endpoint uses, applied per row. That is what makes a
        // row's `status` mean exactly what the status line of an individual POST /attendance/tap would
        // have meant — there is one projection of outcome→status in the system, and this is a second
        // caller of it rather than a second copy.
        var results = response.Rows
            .Select(row => new TapBatchRowResult(
                row.Index, row.DeviceTapId,
                row.Response.Result.Code,
                StatusCodeFor(row.Response.Outcome),
                row.Response.Result.Record,
                row.Response.Result.Message))
            .ToList();

        return Ok(new TapBatchResult(
            Accepted: results.Count(r => r.Status < StatusCodes.Status400BadRequest),
            Rejected: results.Count(r => r.Status >= StatusCodes.Status400BadRequest),
            response.ServerTime,
            results));
    }

    /// <summary>
    /// <c>GET /attendance/live/{eventId}</c> — the D-29 cursor-delta poll that stands in for §5/§6.4's
    /// SignalR hub. Omit <c>since</c> for a snapshot; send back the previous response's <c>cursor</c>
    /// for the changes since it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The permission is <c>attendance.read</c> and there is deliberately no <c>[Authorize]</c>.</b>
    /// This is a dashboard read, not a capture, and the only authentication scheme that exists today is
    /// the device key — which §11 scopes to <c>attendance.capture</c> and nothing else. Gating this
    /// endpoint with it would force the admin SPA to hold a capture-scoped credential in a browser in
    /// order to <em>watch</em> attendance, which inverts the split §6.4 draws between capturing and
    /// reading and would hand a page that can only display data a key that can write it. It stays open
    /// under ADR-001 D-6 with the rest of the admin surface, declares the permission Phase 6 will
    /// enforce, and is listed alongside <c>GET /attendance</c> — the endpoint it is a live view of —
    /// rather than alongside the tap.
    /// </para>
    ///
    /// <para>
    /// <b><c>Cache-Control: no-store</c>.</b> A cached delta is worse than a slow one: an intermediary
    /// replaying a 200 for a cursor the client has already advanced past would make the dashboard
    /// silently stop updating, and the client has no way to tell that from an event where nothing is
    /// happening. <c>no-store</c> rather than <c>no-cache</c> because there is nothing here worth
    /// revalidating — every response is keyed to a cursor that will never be asked for again.
    /// </para>
    /// </remarks>
    /// <param name="eventId">The event to watch.</param>
    /// <param name="since">
    /// Omit for a snapshot; otherwise the <c>cursor</c> from your previous response, <b>verbatim</b>.
    /// Opaque — store it, send it back, never parse it. A blank value is treated as absent.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">
    /// Exactly one of <c>entries</c> (you sent no cursor — <b>replace</b> your state) or <c>changes</c>
    /// (you sent one — <b>merge</b>). The other key is omitted entirely rather than sent as null.
    ///
    /// <para>
    /// <b>Do not ignore <c>hasMore</c>.</b> A response carries at most 500 rows; when it is true the
    /// page was truncated and you should poll again <em>immediately</em> rather than waiting
    /// <c>pollAfterSeconds</c>. Otherwise a 5,000-attendee event takes ten polls to fill a dashboard and
    /// looks broken rather than paging.
    /// </para>
    ///
    /// <para>
    /// <c>counters</c> is <c>GET /events/{id}/summary</c>'s own object — the same numbers from the same
    /// query, bounded by the same cursor ceiling as the rows beside them.
    /// </para>
    /// </response>
    /// <response code="400">
    /// <c>InvalidCursor</c>. Re-poll with no <c>since</c>. Refused rather than silently downgraded to a
    /// snapshot, which would look like "nothing changed" forever while your cursor stayed broken.
    /// </response>
    /// <response code="404"><c>EventNotFound</c>.</response>
    /// <response code="429">
    /// 240 polls per minute per client address — about one every 250 ms, far above the 5-second
    /// default. Partitioned separately from device-key capture limits, so a dashboard tab can never
    /// spend a kiosk's tap budget.
    /// </response>
    [HttpGet("live/{eventId:guid}")]
    [EnableRateLimiting(CaptureRateLimiting.LivePolicyName)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceRead)]
    [ProducesResponseType(typeof(AttendanceLiveDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AttendanceLiveDto>> Live(
        Guid eventId, [FromQuery] string? since, CancellationToken ct)
    {
        // Set before the outcome is known, so it covers the 400 and the 404 as well as the 200. A
        // cacheable InvalidCursor is the nastier of the two: an intermediary replaying it would pin a
        // dashboard at "your cursor is invalid" long after the client had been handed a good one.
        Response.Headers.CacheControl = NoStore;

        var response = await _events.GetLiveAttendanceAsync(eventId, since, ct);

        if (response.Outcome != LiveOutcome.Ok)
        {
            var status = StatusCodeFor(response.Outcome);
            var problem = ProblemDetailsFactory.CreateProblemDetails(
                HttpContext, statusCode: status, title: TitleFor(response.Outcome),
                detail: response.Message);

            problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

            return StatusCode(status, problem);
        }

        return Ok(response.Live);
    }

    /// <summary>
    /// The one place the live endpoint's cache policy is written. Named rather than inlined so the
    /// header and the reasoning above it stay in the same file as each other.
    /// </summary>
    private const string NoStore = "no-store";

    // POST /attendance/manual — organizer override (Technical Plan §6.4).
    //
    // `notes` is bounded here as well as in the service, and both are deliberate. The attribute
    // rejects an over-length value at the boundary with a validation ProblemDetails, before any
    // work happens; the service guard is what protects the next caller that is not HTTP (the
    // planned §4.12 import path). Neither is redundant — dropping the attribute costs a round trip
    // through two queries first, dropping the service guard reopens the truncation 500 for anyone
    // who does not come through this controller.
    // `attendance.write`, distinct from the tap's `attendance.capture` (§6.4): a kiosk's device key is
    // scoped to capture only, and an override is the organizer action the plan calls audited — the
    // two must not collapse into one permission or a reader could rewrite a status.
    /// <summary>
    /// <c>POST /attendance/manual</c> — the organizer override (Technical Plan §6.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a device path.</b> It carries <c>attendance.write</c> rather than the tap's
    /// <c>attendance.capture</c>: a kiosk's device key is scoped to capture only, and an override is
    /// the audited organizer action §6.4 describes. Collapsing the two permissions would let a reader
    /// rewrite a status.
    /// </para>
    ///
    /// <para>
    /// <b>It keeps working on a <c>Closed</c> event, deliberately (ADR-003 D-17).</b> Closing is
    /// terminal, and this is the only route back from a mistake — adding the tap path's status guard
    /// here reads as a safety improvement in review and turns that terminal status into a dead end.
    /// </para>
    ///
    /// <para>
    /// Its <c>code</c> values are their own frozen table — see the <c>ManualOutcomeCode</c> schema —
    /// and they travel through the identical <c>code</c> field a tap uses, on success bodies and RFC
    /// 7807 problem bodies alike.
    /// </para>
    /// </remarks>
    /// <param name="eventId">The event to record against.</param>
    /// <param name="studentId">The student.</param>
    /// <param name="status">
    /// One of §4.9's values — <c>Present</c>, <c>Late</c>, <c>Absent</c>, <c>Excused</c>. Anything else
    /// is <c>400 InvalidStatus</c> rather than a stored value no summary can count.
    /// </param>
    /// <param name="notes">
    /// Free text, at most <see cref="AttendanceNotes.MaxLength"/> characters. Over-length is
    /// <c>400 InvalidNotes</c> — it used to reach SQL Server and come back as a truncation 500.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Recorded. <c>code</c> is <c>Saved</c>.</response>
    /// <response code="400">
    /// <c>InvalidStatus</c> or <c>InvalidNotes</c>, as an RFC 7807 body carrying <c>code</c> and
    /// <c>serverTime</c>. <b>Not</b> a <c>TapResult</c> with <c>success: false</c> — that shape left
    /// the 4xx responses in Phase 4c.
    /// </response>
    /// <response code="404"><c>EventNotFound</c> or <c>StudentNotFound</c>, same body shape.</response>
    [HttpPost("manual")]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceWrite)]
    // Without these the document infers TapResult for every status this action produces, which 4c made
    // untrue: a generated client would deserialize a problem body into TapResult and read `success` off
    // a field that is not there. The <response> tags above supply the prose, not the schema.
    [ProducesResponseType(typeof(TapResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TapResult>> Manual(
        [FromQuery] Guid eventId, [FromQuery] Guid studentId,
        [FromQuery] string status = "Present",
        [FromQuery][StringLength(AttendanceNotes.MaxLength)] string? notes = null,
        CancellationToken ct = default)
    {
        var response = await _attendance.ManualAsync(eventId, studentId, status, notes, ct);
        return Respond(StatusCodeFor(response.Outcome), response.Result, TitleFor(response.Outcome));
    }

    /// <summary>
    /// §6's two body shapes, chosen by the status code rather than by the outcome (Phase 4c, D-37).
    ///
    /// <para>
    /// <b>Why the failure body changed at all.</b> A rejected tap used to return <c>TapResult</c> with
    /// <c>success: false</c>, which contradicted §6's "Errors: RFC 7807" and the published handoff
    /// document's own claim that every error body carries a <c>traceId</c> a client can quote back —
    /// so the one endpoint an external developer was told to build against was the one endpoint whose
    /// errors had nothing to quote. It now goes through
    /// <see cref="ControllerBase.ProblemDetailsFactory"/> like every
    /// other failure in this API, which is where the <c>traceId</c> is stamped exactly once
    /// (<c>TracedProblemDetailsFactory</c>).
    /// </para>
    ///
    /// <para>
    /// <b>The status code decides, not <c>result.Success</c>.</b> They agree today, and keeping the
    /// choice on the code means they cannot stop agreeing: an outcome mapped to a 4xx is a failure to
    /// the offline queue (§8.2 branches on the status), so it must be a problem body whatever a bool
    /// in the payload says.
    /// </para>
    ///
    /// <para>
    /// <c>code</c> is copied from <see cref="TapResult.Code"/> rather than re-derived from the outcome,
    /// so the success body and the failure body cannot disagree about a token — there is one
    /// projection of outcome→token in the system and it lives in <c>TapResponse.For</c>.
    /// </para>
    /// </summary>
    private ActionResult<TapResult> Respond(int status, TapResult result, string title)
    {
        if (status < StatusCodes.Status400BadRequest) return StatusCode(status, result);

        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: title, detail: result.Message);

        problem.Extensions[ErrorCodeProperty] = result.Code;
        problem.Extensions[ServerTimeProperty] = result.ServerTime;

        return StatusCode(status, problem);
    }

    /// <summary>
    /// The human half of the problem body. Prose, and deliberately so — a client branches on
    /// <c>code</c>; this is what an operator reads in a log or a support ticket.
    /// </summary>
    private static string TitleFor(TapOutcome outcome) => outcome switch
    {
        TapOutcome.EventNotFound => "Event not found.",
        TapOutcome.EventNotOpen => "That event is not open for capture.",
        TapOutcome.CardNotFound => "No active card matches that UID.",
        TapOutcome.DeviceNotRegistered => "That device is not registered.",
        TapOutcome.DeviceMismatch => "That deviceId is not the authenticated device.",
        TapOutcome.TappedAtOutOfRange => "That tappedAt is in the future.",
        TapOutcome.TappedAtOutsideEventWindow => "That tappedAt is outside the event's window.",
        TapOutcome.DeviceTapIdRequired => "That batch row carries no deviceTapId.",
        TapOutcome.BatchTooLarge => "That batch is too large.",
        _ => "The tap could not be recorded.",
    };

    /// <inheritdoc cref="TitleFor(TapOutcome)"/>
    private static string TitleFor(LiveOutcome outcome) => outcome switch
    {
        LiveOutcome.EventNotFound => "Event not found.",
        LiveOutcome.InvalidCursor => "That cursor was not issued by this API.",
        _ => "Live attendance could not be read.",
    };

    /// <inheritdoc cref="TitleFor(TapOutcome)"/>
    private static string TitleFor(ManualOutcome outcome) => outcome switch
    {
        ManualOutcome.EventNotFound => "Event not found.",
        ManualOutcome.StudentNotFound => "Student not found.",
        ManualOutcome.InvalidStatus => "That attendance status is not one of the documented values.",
        ManualOutcome.InvalidNotes => "Those notes are too long.",
        _ => "The manual entry could not be saved.",
    };

    /// <summary>
    /// The §6 status-code contract for <c>POST /attendance/tap</c>, as one total function over
    /// <see cref="TapOutcome"/>.
    ///
    /// <para>
    /// <b>Why every member is listed instead of a <c>_ =&gt; Ok(...)</c> fall-through.</b> The
    /// discard arm made "an outcome nobody mapped" indistinguishable from "an outcome that means
    /// success", so <see cref="TapOutcome.DeviceNotRegistered"/> — a rejection — would have shipped
    /// as HTTP 200 carrying <c>success: false</c>. §8.2's offline client branches on the status
    /// code, so it would have deleted the tap from its queue as delivered. The discard also
    /// suppressed the CS8509 non-exhaustiveness warning that would have pointed at this method the
    /// moment the outcome was added.
    /// </para>
    ///
    /// <para>
    /// The final arm still has to exist — C# does not treat a fully enumerated enum switch as
    /// exhaustive, because the underlying integer can hold an undeclared value — but it throws
    /// rather than guessing. An unmapped outcome is then a loud 500 naming the outcome, and
    /// <c>AttendanceControllerMappingTests</c> turns it into a test failure instead: it enumerates
    /// the enum and asserts every member maps, so adding one without deciding its status code fails
    /// the build's test gate.
    /// </para>
    /// </summary>
    internal static int StatusCodeFor(TapOutcome outcome) => outcome switch
    {
        TapOutcome.Recorded
            or TapOutcome.DuplicateIgnored
            or TapOutcome.CheckedOut
            or TapOutcome.AlreadyRecorded => StatusCodes.Status200OK,

        // All three name something the request referred to that does not exist.
        TapOutcome.EventNotFound
            or TapOutcome.CardNotFound
            or TapOutcome.DeviceNotRegistered => StatusCodes.Status404NotFound,

        // All four are the caller's payload, wrong under any circumstances. DeviceMismatch is a bug on
        // the client's side (D-26) and is reported rather than absorbed, so it is discoverable.
        //
        // The two D-36 timestamp refusals are 400 rather than 409 or 422 for the reason §8.2 cares
        // about: a queued tap that names a time we will never accept must be dropped by the client, not
        // retried, and 400 is the code its published table already binds to "stop retrying". Neither is
        // a conflict with the state of the resource — the same event would refuse the same tappedAt
        // forever — so 409 would be a lie about whether resending later could help.
        // The two Phase 4d additions are 400 for the same §8.2 reason, arrived at from opposite ends.
        // DeviceTapIdRequired is a row this API will refuse identically forever, so the client must
        // stop rather than retry — a bug on its side, and named so it is findable. BatchTooLarge is the
        // one refusal in the whole table whose guidance is "chunk and retry": resending the same bytes
        // fails again, but resending them in two halves succeeds, which is still a 4xx and not a 5xx
        // because the request as sent is the thing that was wrong.
        TapOutcome.EventNotOpen
            or TapOutcome.DeviceMismatch
            or TapOutcome.TappedAtOutOfRange
            or TapOutcome.TappedAtOutsideEventWindow
            or TapOutcome.DeviceTapIdRequired
            or TapOutcome.BatchTooLarge => StatusCodes.Status400BadRequest,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(TapOutcome)}. Every outcome must be mapped " +
            "explicitly — see AttendanceControllerMappingTests."),
    };

    /// <summary>
    /// The same contract for <c>GET /attendance/live/{eventId}</c>. See
    /// <see cref="StatusCodeFor(TapOutcome)"/> for why the fall-through arm throws rather than
    /// defaulting to 200 — the failure it prevents (a rejection returned as a success) is worse on a
    /// read than on a write only in that it is quieter.
    /// </summary>
    internal static int StatusCodeFor(LiveOutcome outcome) => outcome switch
    {
        LiveOutcome.Ok => StatusCodes.Status200OK,
        LiveOutcome.EventNotFound => StatusCodes.Status404NotFound,

        // The caller sent something that is not a cursor. 400 rather than falling back to a snapshot:
        // see LiveOutcome.InvalidCursor for why the friendlier behaviour is the wrong one.
        LiveOutcome.InvalidCursor => StatusCodes.Status400BadRequest,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(LiveOutcome)}. Every outcome must be mapped " +
            "explicitly — see AttendanceControllerMappingTests."),
    };

    /// <summary>
    /// The same contract for <c>POST /attendance/manual</c>. See <see cref="StatusCodeFor(TapOutcome)"/>
    /// for why the fall-through arm throws.
    /// </summary>
    internal static int StatusCodeFor(ManualOutcome outcome) => outcome switch
    {
        ManualOutcome.Saved => StatusCodes.Status200OK,

        ManualOutcome.EventNotFound
            or ManualOutcome.StudentNotFound => StatusCodes.Status404NotFound,

        // Syntactically valid requests that failed a §4.9 column rule — 400, not 500, and not a
        // cheerful 200 with the bad value persisted.
        ManualOutcome.InvalidStatus
            or ManualOutcome.InvalidNotes => StatusCodes.Status400BadRequest,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(ManualOutcome)}. Every outcome must be " +
            "mapped explicitly — see AttendanceControllerMappingTests."),
    };
}
