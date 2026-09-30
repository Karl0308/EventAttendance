using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// Manual ID entry for unrecognized RFID scans (UnrecognizedRFIDScans.docx) — the operator records the
/// Student or Employee ID for a card the reader could not place, and administrators review the records
/// for reconciliation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gated by <see cref="EamsPermissions.AttendanceWrite"/> to record and
/// <see cref="EamsPermissions.AttendanceRead"/> to review</b>, reusing the attendance codes: an unresolved
/// scan is an attendance concern, and this is the organizer's deliberate entry (like the manual override),
/// not a device capture. The recognized-RFID flow is untouched.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/scans")]
public class ScansController : ControllerBase
{
    internal const string ErrorCodeProperty = "code";

    private readonly IUnrecognizedScanService _scans;

    public ScansController(IUnrecognizedScanService scans) => _scans = scans;

    /// <summary>
    /// <c>POST /scans/manual-id</c> — record the ID an operator entered for an unrecognized scan.
    /// </summary>
    /// <response code="201">The recorded entry.</response>
    /// <response code="400">The card UID or ID number is blank, or the person type is not Student/Employee.</response>
    /// <response code="409">No school could be resolved.</response>
    [HttpPost("manual-id")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.AttendanceWrite)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceWrite)]
    [ProducesResponseType(typeof(ManualIdEntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ManualIdEntryDto>> RecordManualId(
        [FromBody] ManualIdEntryRequest request, CancellationToken ct)
    {
        var response = await _scans.RecordAsync(request, ct);
        if (response.Outcome != ManualIdEntryOutcome.Saved) return Failure(response);

        // No by-id read on this surface, so the 201 names the collection rather than a resolving Location.
        return StatusCode(StatusCodes.Status201Created, response.Entry);
    }

    /// <summary>
    /// <c>GET /scans/manual-id</c> — the recorded manual ID entries for this school, newest first.
    /// </summary>
    /// <response code="200">One page of manual ID entries.</response>
    [HttpGet("manual-id")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.AttendanceRead)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceRead)]
    [ProducesResponseType(typeof(PagedResult<ManualIdEntryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ManualIdEntryDto>>> ListManualId(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _scans.ListAsync(PageRequest.From(page, pageSize), ct));

    private ObjectResult Failure(ManualIdEntryResponse response)
    {
        var status = response.Outcome switch
        {
            ManualIdEntryOutcome.ValidationFailed => StatusCodes.Status400BadRequest,
            ManualIdEntryOutcome.NoSchoolResolved => StatusCodes.Status409Conflict,
            _ => throw new ArgumentOutOfRangeException(
                nameof(response), response.Outcome,
                "No HTTP status is mapped for this ManualIdEntryOutcome."),
        };

        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: "The manual ID entry could not be recorded.",
            detail: response.Message);
        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();
        return StatusCode(status, problem);
    }
}
