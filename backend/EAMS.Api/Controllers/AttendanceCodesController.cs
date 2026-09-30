using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Api.Controllers;

/// <summary>
/// The Live Attendance attendance-code surface (LiveAttendance.docx §3–§5) — a unique code per attendee per
/// event, listed for the Live Attendance screen and emailed to attendees who have an address on file.
/// </summary>
/// <remarks>
/// <para>
/// Nested under the event (<c>/events/{eventId}/attendance-codes</c>) because a code has no meaning without
/// its event. It reuses the <c>attendance.read</c> / <c>attendance.write</c> pair — reviewing codes is a
/// read, issuing and emailing them are writes — so no new permission is minted and the RBAC grant matrix is
/// unchanged.
/// </para>
///
/// <para>
/// <b>No SMTP is wired in this build</b> (see <see cref="IEmailSender"/>): the email action runs the full
/// recipient-selection, validation and tally flow against a logging no-op, so it is exercisable end to end
/// and a real transport drops in behind the interface.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/events/{eventId:guid}/attendance-codes")]
public class AttendanceCodesController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on every other controller.</summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IAttendanceCodeService _codes;

    public AttendanceCodesController(IAttendanceCodeService codes) => _codes = codes;

    /// <summary>
    /// <c>GET /events/{eventId}/attendance-codes</c> — the codes issued for this event, by attendee name.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The codes, possibly empty.</response>
    /// <response code="404">No such event in this school.</response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.AttendanceRead)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceRead)]
    [ProducesResponseType(typeof(IReadOnlyList<AttendanceCodeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<AttendanceCodeDto>>> List(Guid eventId, CancellationToken ct)
    {
        var result = await _codes.ListAsync(eventId, ct);
        return result.Outcome == AttendanceCodeOutcome.Ok
            ? Ok(result.Codes)
            : Failure(result.Outcome, "Event not found.");
    }

    /// <summary>
    /// <c>POST /events/{eventId}/attendance-codes/generate</c> — issue a code to every attendee that lacks
    /// one. Idempotent unless <paramref name="regenerate"/> is set, which rewrites every attendee's code.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <param name="regenerate">When true, replaces existing codes (the deliberate regeneration of §5).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The full code list, with a tally of what the run did.</response>
    /// <response code="404">No such event in this school.</response>
    [HttpPost("generate")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.AttendanceWrite)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceWrite)]
    [ProducesResponseType(typeof(AttendanceCodeGenerateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AttendanceCodeGenerateResult>> Generate(
        Guid eventId, [FromQuery] bool regenerate, CancellationToken ct)
    {
        var result = await _codes.GenerateAsync(eventId, regenerate, ct);
        return result.Outcome == AttendanceCodeOutcome.Ok
            ? Ok(result)
            : Failure(result.Outcome, "Event not found.");
    }

    /// <summary>
    /// <c>POST /events/{eventId}/attendance-codes/email</c> — email the codes to <c>All</c> eligible
    /// attendees or a <c>Selected</c> subset. Attendees with no address or no code are skipped and tallied.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <param name="request">The recipient mode and, for <c>Selected</c>, the attendee ids.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The send tally — sent, skipped (no email / no code), failed.</response>
    /// <response code="400"><c>Selected</c> with no ids, or an unknown mode.</response>
    /// <response code="404">No such event in this school.</response>
    [HttpPost("email")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.AttendanceWrite)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceWrite)]
    [ProducesResponseType(typeof(AttendanceCodeEmailResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AttendanceCodeEmailResultDto>> Email(
        Guid eventId, [FromBody] AttendanceCodeEmailRequest request, CancellationToken ct)
    {
        var result = await _codes.EmailAsync(eventId, request, ct);
        return result.Outcome == AttendanceCodeOutcome.Ok
            ? Ok(result.Result)
            : Failure(result.Outcome, result.Message);
    }

    // -------------------------------------------------------------------------------- translation

    /// <summary>
    /// The status-code contract for the whole surface, as one total function — every member listed rather
    /// than a fall-through, for the reason every other controller's <c>StatusCodeFor</c> records: a discard
    /// arm ships an unmapped outcome as a success.
    /// </summary>
    internal static int StatusCodeFor(AttendanceCodeOutcome outcome) => outcome switch
    {
        AttendanceCodeOutcome.Ok => StatusCodes.Status200OK,
        AttendanceCodeOutcome.EventNotFound => StatusCodes.Status404NotFound,
        AttendanceCodeOutcome.ValidationFailed => StatusCodes.Status400BadRequest,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(AttendanceCodeOutcome)}. Every outcome must be " +
            "mapped explicitly, or an unmapped one ships as a success."),
    };

    private ObjectResult Failure(AttendanceCodeOutcome outcome, string detail)
    {
        var status = StatusCodeFor(outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(outcome), detail: detail);

        problem.Extensions[ErrorCodeProperty] = outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(AttendanceCodeOutcome outcome) => outcome switch
    {
        AttendanceCodeOutcome.EventNotFound => "Event not found.",
        AttendanceCodeOutcome.ValidationFailed => "The request could not be processed.",
        _ => "The request could not be processed.",
    };
}
