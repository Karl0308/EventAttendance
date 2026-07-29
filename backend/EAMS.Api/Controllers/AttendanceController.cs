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
    public async Task<ActionResult<TapResult>> Tap([FromBody] TapRequest req, CancellationToken ct)
    {
        var response = await _attendance.TapAsync(req, ct);
        return StatusCode(StatusCodeFor(response.Outcome), response.Result);
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
        return StatusCode(StatusCodeFor(response.Outcome), response.Result);
    }

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

        // Both are the caller's payload, wrong under any circumstances. DeviceMismatch is a bug on the
        // client's side (D-26) and is reported rather than absorbed, so it is discoverable.
        TapOutcome.EventNotOpen
            or TapOutcome.DeviceMismatch => StatusCodes.Status400BadRequest,

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
