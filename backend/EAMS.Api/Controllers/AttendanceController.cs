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

    private readonly IAttendanceService _attendance;
    public AttendanceController(IAttendanceService attendance) => _attendance = attendance;

    [HttpGet]
    [HasPermissionNotEnforced("attendance.read")]
    public async Task<ActionResult<IEnumerable<AttendanceDto>>> List(
        [FromQuery] Guid? eventId, [FromQuery] Guid? studentId, [FromQuery] string? status,
        CancellationToken ct)
        => Ok(await _attendance.ListAsync(eventId, studentId, status, ct));

    // POST /attendance/tap — the core capture path (Technical Plan §6.4).
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
    [HttpPost("manual")]
    [HasPermissionNotEnforced("attendance.write")]
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
    /// errors had nothing to quote. It now goes through <see cref="ProblemDetailsFactory"/> like every
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
        _ => "The tap could not be recorded.",
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
        TapOutcome.EventNotOpen
            or TapOutcome.DeviceMismatch
            or TapOutcome.TappedAtOutOfRange
            or TapOutcome.TappedAtOutsideEventWindow => StatusCodes.Status400BadRequest,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(TapOutcome)}. Every outcome must be mapped " +
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
