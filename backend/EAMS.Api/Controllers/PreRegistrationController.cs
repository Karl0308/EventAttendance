using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Api.Controllers;

/// <summary>
/// The Pre-Registration surface (PreRegistration.docx) — sessions opened against an event audience, into
/// which attendees are pre-registered by RFID tap or manual selection, up to a capacity.
/// </summary>
/// <remarks>
/// <para>
/// Its own route rather than a branch of <c>/event-audiences</c>: a session is a row with its own lifecycle
/// (opened, filled, closed) that references an audience. It reuses the <c>events.read</c> /
/// <c>events.write</c> pair — this lives in the Event Audience module — so no permission is minted and the
/// RBAC grant matrix is unchanged.
/// </para>
///
/// <para>
/// Tapping here is a back-office administrator action (a <c>Bearer</c> token), not a capture device: the
/// operator taps cards into the pre-registration screen. That is why the register route is on the JWT scheme
/// like the rest of this controller, not the device-key scheme the five capture routes use.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/pre-registration")]
public class PreRegistrationController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on every other controller.</summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IPreRegistrationService _prereg;

    public PreRegistrationController(IPreRegistrationService prereg) => _prereg = prereg;

    // -------------------------------------------------------------------------------------- sessions

    /// <summary><c>GET /pre-registration/sessions</c> — the sessions, newest first, with live counters.</summary>
    /// <response code="200">The sessions, possibly empty.</response>
    [HttpGet("sessions")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsRead)]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(IReadOnlyList<PreRegistrationSessionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PreRegistrationSessionDto>>> ListSessions(CancellationToken ct)
        => Ok(await _prereg.ListSessionsAsync(ct));

    /// <summary><c>GET /pre-registration/sessions/{id}</c> — one session with its counter.</summary>
    /// <response code="200">The session.</response>
    /// <response code="404">No such session in this school.</response>
    [HttpGet("sessions/{id:guid}", Name = nameof(GetSession))]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsRead)]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(PreRegistrationSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreRegistrationSessionDto>> GetSession(Guid id, CancellationToken ct)
    {
        var row = await _prereg.GetSessionAsync(id, ct);
        return row is null
            ? SessionFailure(new PreRegistrationSessionWriteResult(
                PreRegistrationSessionOutcome.NotFound, "Pre-registration session not found.", null))
            : Ok(row);
    }

    /// <summary><c>POST /pre-registration/sessions</c> — open a session against an active audience.</summary>
    /// <response code="201">The created session. <c>Location</c> names it.</response>
    /// <response code="400">The name or capacity breaks a rule.</response>
    /// <response code="409">The audience is unavailable, or no school could be resolved.</response>
    [HttpPost("sessions")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(PreRegistrationSessionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PreRegistrationSessionDto>> CreateSession(
        [FromBody] PreRegistrationSessionCreateRequest request, CancellationToken ct)
    {
        var result = await _prereg.CreateSessionAsync(request, ct);
        if (result.Outcome != PreRegistrationSessionOutcome.Saved) return SessionFailure(result);

        return CreatedAtRoute(nameof(GetSession), new { id = result.Session!.Id }, result.Session);
    }

    /// <summary><c>PATCH /pre-registration/sessions/{id}/close</c> — close or reopen a session.</summary>
    /// <response code="200">The session, with its new flag.</response>
    /// <response code="400"><c>isClosed</c> was not supplied.</response>
    /// <response code="404">No such session.</response>
    [HttpPatch("sessions/{id:guid}/close")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(PreRegistrationSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreRegistrationSessionDto>> SetClosed(
        Guid id, [FromBody] PreRegistrationCloseRequest request, CancellationToken ct)
    {
        if (request.IsClosed is not { } isClosed)
        {
            return SessionFailure(new PreRegistrationSessionWriteResult(
                PreRegistrationSessionOutcome.ValidationFailed,
                "isClosed is required. Send {\"isClosed\": true} to close this session or " +
                "{\"isClosed\": false} to reopen it.",
                null));
        }

        var result = await _prereg.SetClosedAsync(id, isClosed, ct);
        return result.Outcome == PreRegistrationSessionOutcome.Saved
            ? Ok(result.Session)
            : SessionFailure(result);
    }

    // ---------------------------------------------------------------------------------- registration

    /// <summary>
    /// <c>POST /pre-registration/sessions/{id}/register</c> — register one attendee by tap or manual choice.
    /// </summary>
    /// <response code="200">The attendee was registered; the body carries the live counter.</response>
    /// <response code="400">A malformed request — unknown method/type, or a missing card/id.</response>
    /// <response code="404">No such session, or a manually chosen attendee that does not exist.</response>
    /// <response code="409">The attendee is already registered, the session is full, or the session is closed.</response>
    /// <response code="422">A tapped card matched no Academic Community record.</response>
    [HttpPost("sessions/{id:guid}/register")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(PreRegisterResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PreRegisterResult>> Register(
        Guid id, [FromBody] PreRegisterRequest request, CancellationToken ct)
    {
        var result = await _prereg.RegisterAsync(id, request, ct);
        return result.Outcome == PreRegisterOutcome.Registered ? Ok(result) : RegisterFailure(result);
    }

    /// <summary><c>GET /pre-registration/sessions/{id}/registrants</c> — the registered attendees.</summary>
    /// <response code="200">The registrants, possibly empty.</response>
    /// <response code="404">No such session.</response>
    [HttpGet("sessions/{id:guid}/registrants")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsRead)]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(IReadOnlyList<PreRegistrantDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PreRegistrantDto>>> ListRegistrants(Guid id, CancellationToken ct)
    {
        var result = await _prereg.ListRegistrantsAsync(id, ct);
        return result.Outcome == PreRegistrationSessionOutcome.Saved
            ? Ok(result.Registrants)
            : SessionFailure(new PreRegistrationSessionWriteResult(
                result.Outcome, "Pre-registration session not found.", null));
    }

    /// <summary>
    /// <c>DELETE /pre-registration/sessions/{id}/registrants/{registrantId}</c> — remove one registration.
    /// </summary>
    /// <response code="200">Removed; the body carries the updated counter.</response>
    /// <response code="404">No such session, or no such registration in it.</response>
    [HttpDelete("sessions/{id:guid}/registrants/{registrantId:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(PreRegisterResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreRegisterResult>> RemoveRegistrant(
        Guid id, Guid registrantId, CancellationToken ct)
    {
        var result = await _prereg.RemoveRegistrantAsync(id, registrantId, ct);
        return result.Outcome == PreRegisterOutcome.Registered ? Ok(result) : RegisterFailure(result);
    }

    // -------------------------------------------------------------------------------- translation

    internal static int StatusCodeForSession(PreRegistrationSessionOutcome outcome) => outcome switch
    {
        PreRegistrationSessionOutcome.Saved => StatusCodes.Status200OK,
        PreRegistrationSessionOutcome.NotFound => StatusCodes.Status404NotFound,
        PreRegistrationSessionOutcome.ValidationFailed => StatusCodes.Status400BadRequest,
        PreRegistrationSessionOutcome.NoSchoolResolved
            or PreRegistrationSessionOutcome.AudienceUnavailable => StatusCodes.Status409Conflict,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(PreRegistrationSessionOutcome)}."),
    };

    internal static int StatusCodeForRegister(PreRegisterOutcome outcome) => outcome switch
    {
        PreRegisterOutcome.Registered => StatusCodes.Status200OK,
        PreRegisterOutcome.ValidationFailed => StatusCodes.Status400BadRequest,
        PreRegisterOutcome.AttendeeNotFound
            or PreRegisterOutcome.SessionNotFound => StatusCodes.Status404NotFound,
        PreRegisterOutcome.Duplicate
            or PreRegisterOutcome.CapacityReached
            or PreRegisterOutcome.SessionClosed => StatusCodes.Status409Conflict,
        PreRegisterOutcome.UnrecognizedCard => StatusCodes.Status422UnprocessableEntity,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(PreRegisterOutcome)}."),
    };

    private ObjectResult SessionFailure(PreRegistrationSessionWriteResult result)
    {
        var status = StatusCodeForSession(result.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: "The request could not be processed.",
            detail: result.Message);
        problem.Extensions[ErrorCodeProperty] = result.Outcome.ToString();
        return StatusCode(status, problem);
    }

    private ObjectResult RegisterFailure(PreRegisterResult result)
    {
        var status = StatusCodeForRegister(result.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: "The registration could not be processed.",
            detail: result.Message);
        problem.Extensions[ErrorCodeProperty] = result.Outcome.ToString();
        return StatusCode(status, problem);
    }
}
