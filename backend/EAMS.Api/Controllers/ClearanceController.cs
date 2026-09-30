using EAMS.Api.Authorization;
using EAMS.Api.Reports;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// The Clearance Checker (Clearance-Checker-Module.docx) — a student's events and their attendance for
/// each. <b>No clearance status is computed</b>; the administrator interprets the results.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gated by <see cref="EamsPermissions.ReportsRead"/></b> — clearance is a cross-event report over
/// attendance data, the same administrators'-only surface §6.7's reports are, so it reuses that code
/// rather than minting a new one. Search is not a route here: the SPA finds the student through the
/// existing roster search and card lookup, then fetches this report by id.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/clearance")]
public class ClearanceController : ControllerBase
{
    internal const string ErrorCodeProperty = "code";

    private readonly IClearanceService _clearance;

    public ClearanceController(IClearanceService clearance) => _clearance = clearance;

    /// <summary>
    /// <c>GET /clearance/students/{studentId}</c> — the clearance report for one student.
    /// </summary>
    /// <param name="studentId">The student.</param>
    /// <param name="dateFrom">Optional inclusive lower bound on event start date.</param>
    /// <param name="dateTo">Optional inclusive upper bound on event start date.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The report.</response>
    /// <response code="404">No such student in this school.</response>
    [HttpGet("students/{studentId:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.ReportsRead)]
    [HasPermissionNotEnforced(EamsPermissions.ReportsRead)]
    [ProducesResponseType(typeof(ClearanceReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClearanceReportDto>> GetReport(
        Guid studentId, [FromQuery] DateTime? dateFrom, [FromQuery] DateTime? dateTo, CancellationToken ct)
    {
        var report = await _clearance.GetReportAsync(studentId, dateFrom, dateTo, ct);
        return report is null ? StudentNotFound() : Ok(report);
    }

    /// <summary>
    /// <c>GET /clearance/students/{studentId}/export</c> — the same report as a CSV download (CLR-02).
    /// PDF is the SPA's Print button, not a server format (JJ decision B).
    /// </summary>
    /// <response code="200">The CSV file.</response>
    /// <response code="404">No such student in this school.</response>
    [HttpGet("students/{studentId:guid}/export")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.ReportsRead)]
    [HasPermissionNotEnforced(EamsPermissions.ReportsRead)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Export(
        Guid studentId, [FromQuery] DateTime? dateFrom, [FromQuery] DateTime? dateTo, CancellationToken ct)
    {
        var report = await _clearance.GetReportAsync(studentId, dateFrom, dateTo, ct);
        if (report is null) return StudentNotFound();

        return File(ClearanceReportCsv.Write(report), ClearanceReportCsv.ContentType,
            ClearanceReportCsv.FileNameFor(report));
    }

    private ObjectResult StudentNotFound()
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: StatusCodes.Status404NotFound,
            title: "Student not found.",
            detail: "No such student in this school, or the student has been removed.");
        problem.Extensions[ErrorCodeProperty] = "NotFound";
        return StatusCode(StatusCodes.Status404NotFound, problem);
    }
}
