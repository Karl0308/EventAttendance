using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// Technical Plan §6.7's reports module, as the client's QA answers Q15/Q16 (MDVault #463 Part D)
/// scoped it: a summary of one event and a summary of several, for administrators only.
///
/// <para>
/// <b>Every action here is <c>reports.read</c>, which only SuperAdmin and SchoolAdmin hold.</b> That
/// narrows §11, which also gives report reading to Organizer and Viewer. Q16 overrides it and JJ
/// approved the override (see <see cref="EamsPermissions.ReportsRead"/>).
/// </para>
///
/// <para>
/// <b>Bearer only.</b> A device key is scoped to <c>attendance.capture</c> and belongs to a kiosk,
/// not a person; a report is an operator's read.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/reports")]
public class ReportsController : ControllerBase
{
    /// <summary>
    /// The machine-readable half of §6's RFC 7807 body, as on the other controllers: the token a
    /// client branches on. <c>title</c> and <c>detail</c> are prose written for a person.
    /// </summary>
    internal const string ErrorCodeProperty = "code";

    /// <summary>
    /// The extension naming the requested ids that resolved to no event, on a
    /// <c>404 EventNotFound</c>. They are the caller's own input, so naming them discloses nothing.
    /// </summary>
    internal const string MissingEventIdsProperty = "missingEventIds";

    private readonly IReportService _reports;
    public ReportsController(IReportService reports) => _reports = reports;

    /// <summary>
    /// §6.7 <c>GET /reports/event/{eventId}/summary</c> — the Event Attendance Summary for one event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same figures as <c>GET /events/{id}/summary</c>, from the same code</b>, so the report
    /// and the event page can never disagree about one event. It adds the event's <c>status</c> and
    /// <c>startAt</c>, and <c>attended</c> — the numerator of <c>attendanceRate</c>.
    /// </para>
    ///
    /// <para>
    /// <b>A closed or cancelled event reports its frozen audience:</b> <c>expected</c> is the roster
    /// written down when the event closed, so a later import cannot move a past event's rate.
    /// </para>
    /// </remarks>
    /// <param name="eventId">The event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The summary.</response>
    /// <response code="401">No credential, or an invalid one.</response>
    /// <response code="403">Signed in without <c>reports.read</c> — every role except SuperAdmin and SchoolAdmin.</response>
    /// <response code="404">No such event in the caller's school, or it is soft-deleted.</response>
    [HttpGet("event/{eventId:guid}/summary")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.ReportsRead)]
    [HasPermissionNotEnforced(EamsPermissions.ReportsRead)]
    [ProducesResponseType(typeof(EventReportRowDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventReportRowDto>> EventSummary(Guid eventId, CancellationToken ct)
    {
        var row = await _reports.GetEventSummaryAsync(eventId, ct);
        return row is null ? NotFound() : Ok(row);
    }

    /// <summary>
    /// <c>GET /reports/events/summary?eventId=…&amp;eventId=…</c> — a summary of several hand-picked
    /// events: one row per event and their pooled totals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The totals are pooled, not averaged:</b> <c>totals.attendanceRate</c> is
    /// <c>totals.attended</c> ÷ <c>totals.expected</c>, so a large event weighs what its audience
    /// weighs. It is reproducible from the rows by summing <c>attended</c> and <c>expected</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The selection is bounded.</b> At least one id and at most
    /// <see cref="ReportLimits.MaxEventsPerReport"/> distinct ids; a repeated id counts once. A
    /// selection outside that range is refused, never truncated.
    /// </para>
    ///
    /// <para>
    /// <b>One missing event refuses the whole report</b> with a 404 naming the ids that were not found
    /// — whether nonexistent, soft-deleted or another school's, which are indistinguishable by design.
    /// Totals over the events that happened to be found would describe a selection nobody made.
    /// </para>
    /// </remarks>
    /// <param name="eventId">
    /// The events to report on, as a repeated query parameter: <c>?eventId=A&amp;eventId=B</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One row per distinct event, ordered by start time, and the pooled totals.</response>
    /// <response code="400">
    /// <c>SelectionEmpty</c> (no id) or <c>SelectionTooLarge</c> (more distinct ids than the maximum),
    /// in the body's <c>code</c>. A malformed id is a validation problem.
    /// </response>
    /// <response code="401">No credential, or an invalid one.</response>
    /// <response code="403">Signed in without <c>reports.read</c> — every role except SuperAdmin and SchoolAdmin.</response>
    /// <response code="404">
    /// <c>EventNotFound</c>: at least one id is not an event in the caller's school. The body's
    /// <c>missingEventIds</c> lists them.
    /// </response>
    [HttpGet("events/summary")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.ReportsRead)]
    [HasPermissionNotEnforced(EamsPermissions.ReportsRead)]
    [ProducesResponseType(typeof(MultiEventReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MultiEventReportDto>> EventsSummary(
        [FromQuery] Guid[]? eventId, CancellationToken ct)
    {
        var response = await _reports.GetMultiEventSummaryAsync(eventId ?? [], ct);

        return response.Outcome == MultiEventReportOutcome.Ready
            ? Ok(response.Report)
            : Failure(response);
    }

    /// <summary>
    /// The status-code contract for the multi-event report, as one total function over
    /// <see cref="MultiEventReportOutcome"/> — no discard arm, for the reason every other
    /// <c>StatusCodeFor</c> on this API records: an unmapped outcome must not ship as a success.
    /// </summary>
    internal static int StatusCodeFor(MultiEventReportOutcome outcome) => outcome switch
    {
        MultiEventReportOutcome.Ready => StatusCodes.Status200OK,
        MultiEventReportOutcome.SelectionEmpty => StatusCodes.Status400BadRequest,
        MultiEventReportOutcome.SelectionTooLarge => StatusCodes.Status400BadRequest,
        MultiEventReportOutcome.EventNotFound => StatusCodes.Status404NotFound,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(MultiEventReportOutcome)}. Every outcome " +
            "must be mapped explicitly, or an unmapped one ships as a success."),
    };

    private static string TitleFor(MultiEventReportOutcome outcome) => outcome switch
    {
        MultiEventReportOutcome.SelectionEmpty => "No events were picked.",
        MultiEventReportOutcome.SelectionTooLarge => "Too many events were picked.",
        MultiEventReportOutcome.EventNotFound => "Some of the picked events were not found.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No problem title is written for this {nameof(MultiEventReportOutcome)}."),
    };

    /// <summary>
    /// §6's declared error shape (RFC 7807), built through <see cref="ProblemDetailsFactory"/> so the
    /// <c>traceId</c> is stamped once, in <c>TracedProblemDetailsFactory</c>, rather than by this action.
    /// </summary>
    private ObjectResult Failure(MultiEventReportResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status,
            title: TitleFor(response.Outcome), detail: response.Message);

        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();
        if (response.MissingEventIds.Count > 0)
            problem.Extensions[MissingEventIdsProperty] = response.MissingEventIds;

        return StatusCode(status, problem);
    }
}
