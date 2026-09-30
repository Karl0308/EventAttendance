using EAMS.Api.Authorization;
using EAMS.Api.Reports;
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
    private readonly IAttendanceAnalyticsService _analytics;

    public ReportsController(IReportService reports, IAttendanceAnalyticsService analytics)
    {
        _reports = reports;
        _analytics = analytics;
    }

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
    /// Task 9.6 <c>GET /reports/event/{eventId}/detail</c> — one event's report in detail: its
    /// particulars, its summary, and (for a <c>TimeInOut</c> event) its time-out totals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A new route rather than a wider <c>/summary</c>.</b> <c>/summary</c> returns the same
    /// <c>EventReportRowDto</c> each row of <c>GET /reports/events/summary</c> is, so widening it would
    /// widen the multi-event contract too. <c>summary</c> here is that object, from the same code.
    /// </para>
    ///
    /// <para>
    /// <b><c>timeInOut</c> is <c>null</c> for a <c>Single</c> event</b> (client QA Q12: single-tap events
    /// are unchanged and carry no time-out statistics). For a <c>TimeInOut</c> event it counts the rows
    /// with a Time In — any status, the same rows the status buckets count, so an <c>Absent</c> row
    /// written at close is not "tapped in". <c>tappedOut</c> always equals <c>withTimeOut</c>;
    /// <c>tappedIn</c> = <c>withTimeOut</c> + <c>withoutTimeOut</c>; <c>averageDurationSeconds</c> is
    /// the mean over complete In/Out pairs only, in whole seconds, and <c>null</c> when there is none
    /// (QA A6 — read it beside <c>withoutTimeOut</c>).
    /// </para>
    /// </remarks>
    /// <param name="eventId">The event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The report.</response>
    /// <response code="401">No credential, or an invalid one.</response>
    /// <response code="403">Signed in without <c>reports.read</c> — every role except SuperAdmin and SchoolAdmin.</response>
    /// <response code="404">No such event in the caller's school, or it is soft-deleted.</response>
    [HttpGet("event/{eventId:guid}/detail")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.ReportsRead)]
    [HasPermissionNotEnforced(EamsPermissions.ReportsRead)]
    [ProducesResponseType(typeof(EventDetailReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventDetailReportDto>> EventDetail(Guid eventId, CancellationToken ct)
    {
        var report = await _reports.GetEventDetailAsync(eventId, ct);
        return report is null ? NotFound() : Ok(report);
    }

    /// <summary>
    /// Task 9.6 <c>GET /reports/event/{eventId}/attendees</c> — the report's student list, paged: every
    /// student who tapped in, with Time In, Time Out and Duration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Paged like every admin list</b> (<c>?page=</c> 1-based, <c>?pageSize=</c> default
    /// <see cref="Paging.DefaultPageSize"/>, clamped to <see cref="Paging.MaxPageSize"/>), because an
    /// assembly can have thousands of attendees. A client that wants every row walks the pages;
    /// <c>GET /reports/event/{eventId}/export.csv</c> is the unpaged form, written from the same query.
    /// </para>
    ///
    /// <para>
    /// <b>Which rows:</b> the event's attendance rows with a Time In, in any status. <c>total</c> therefore
    /// equals the detail report's <c>timeInOut.tappedIn</c>. <b>Order:</b> last name, first name, middle
    /// name, student number, Time In, record id — total, so pages never overlap or skip on a stable event.
    /// On an <c>Open</c> event a tap landing between two page reads can shift later rows, as on every
    /// offset-paged list.
    /// </para>
    ///
    /// <para>
    /// <b>For a <c>Single</c> event <c>timeOut</c> and <c>durationSeconds</c> are always <c>null</c></b>
    /// (QA Q12). Otherwise <c>durationSeconds</c> is Time Out − Time In in whole seconds, and <c>null</c>
    /// exactly when <c>timeOut</c> is.
    /// </para>
    /// </remarks>
    /// <param name="eventId">The event.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of the list.</response>
    /// <response code="401">No credential, or an invalid one.</response>
    /// <response code="403">Signed in without <c>reports.read</c> — every role except SuperAdmin and SchoolAdmin.</response>
    /// <response code="404">No such event in the caller's school, or it is soft-deleted.</response>
    [HttpGet("event/{eventId:guid}/attendees")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.ReportsRead)]
    [HasPermissionNotEnforced(EamsPermissions.ReportsRead)]
    [ProducesResponseType(typeof(PagedResult<EventReportAttendeeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<EventReportAttendeeDto>>> EventAttendees(
        Guid eventId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        var result = await _reports.GetEventAttendeesAsync(eventId, PageRequest.From(page, pageSize), ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Task 9.6 <c>GET /reports/event/{eventId}/export.csv</c> — the whole single-event report as a CSV
    /// download: particulars, totals, and every row of the student list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same numbers and rows as <c>/detail</c> and <c>/attendees</c>, from the same queries</b>, so
    /// the file agrees with the screen row for row. Under <c>reports.read</c> — no separate export
    /// permission (JJ decision B: CSV from the server now; printing is the SPA's; no PDF).
    /// </para>
    ///
    /// <para>
    /// <c>text/csv; charset=utf-8</c> <b>with a UTF-8 byte-order mark</b> so Excel reads ñ correctly;
    /// RFC 4180 quoting; CRLF records; <c>Content-Disposition: attachment</c> named
    /// <c>EAMS-event-report-{startDate}-{eventId}.csv</c>; <c>Cache-Control: no-store</c>. A cell that
    /// begins with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR is prefixed with <c>'</c> so a
    /// spreadsheet cannot run it as a formula. <b>Times are UTC</b>, ISO 8601 with <c>Z</c>, to the
    /// second, and the headings say so. A <c>Single</c> event's file has no time-out totals and no Time Out
    /// or Duration columns (QA Q12).
    /// </para>
    /// </remarks>
    /// <param name="eventId">The event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The CSV file, as an attachment.</response>
    /// <response code="401">No credential, or an invalid one.</response>
    /// <response code="403">Signed in without <c>reports.read</c> — every role except SuperAdmin and SchoolAdmin.</response>
    /// <response code="404">No such event in the caller's school, or it is soft-deleted.</response>
    [HttpGet("event/{eventId:guid}/export.csv")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.ReportsRead)]
    [HasPermissionNotEnforced(EamsPermissions.ReportsRead)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, EventReportCsv.ContentType)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EventExportCsv(Guid eventId, CancellationToken ct)
    {
        var export = await _reports.GetEventExportAsync(eventId, ct);
        if (export is null) return NotFound();

        // A report is a snapshot of attendance that keeps moving while the event is open, and it names
        // students; neither a browser nor an intermediary should keep a copy.
        Response.Headers.CacheControl = "no-store";

        return File(EventReportCsv.Write(export), EventReportCsv.ContentType,
            EventReportCsv.FileNameFor(export.Report.Event));
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

    // ----------------------------------------------------------------- RPT-01 attendance analytics

    /// <summary>
    /// <c>GET /reports/attendance-analytics</c> — recorded student attendance grouped by course, year level
    /// or section over a date range, with a Present / Late / Absent / Excused breakdown and a rate
    /// (Reports-Module-Enhancement.docx RPT-01, scoped to what the model carries — see
    /// <see cref="IAttendanceAnalyticsService"/>).
    /// </summary>
    /// <remarks>
    /// Drill-down is a re-query, not a second endpoint: ask for a finer <c>groupBy</c> and pass the parent's
    /// value as a filter (Course → <c>groupBy=Section&amp;course=BSIT</c> → <c>groupBy=Event&amp;course=BSIT&amp;section=A</c>).
    /// At the Event level each row carries <c>eventId</c>, so the SPA links into the Event Module.
    /// </remarks>
    /// <param name="groupBy"><c>Course</c> (default), <c>YearLevel</c>, <c>Section</c>, or <c>Event</c>.</param>
    /// <param name="from">Only events starting on/after this UTC instant.</param>
    /// <param name="to">Only events starting on/before this UTC instant.</param>
    /// <param name="includeCancelled">Cancelled events are excluded unless true.</param>
    /// <param name="course">Drill-down filter.</param>
    /// <param name="yearLevel">Drill-down filter.</param>
    /// <param name="section">Drill-down filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The grouped report and its totals.</response>
    /// <response code="400">An unknown <c>groupBy</c>, or <c>from</c> after <c>to</c>.</response>
    /// <response code="401">No credential, or an invalid one.</response>
    /// <response code="403">Signed in without <c>reports.read</c>.</response>
    [HttpGet("attendance-analytics")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.ReportsRead)]
    [HasPermissionNotEnforced(EamsPermissions.ReportsRead)]
    [ProducesResponseType(typeof(AttendanceAnalyticsReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AttendanceAnalyticsReportDto>> AttendanceAnalytics(
        [FromQuery] string? groupBy, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] bool includeCancelled, [FromQuery] string? course, [FromQuery] string? yearLevel,
        [FromQuery] string? section, CancellationToken ct)
    {
        var result = await _analytics.GetReportAsync(
            new AttendanceAnalyticsQuery(groupBy, from, to, includeCancelled, course, yearLevel, section), ct);

        return result.Outcome == AttendanceAnalyticsOutcome.Ok
            ? Ok(result.Report)
            : AnalyticsFailure(result);
    }

    /// <summary>
    /// <c>GET /reports/attendance-analytics/export.csv</c> — the same report as a CSV download.
    /// </summary>
    /// <response code="200">The CSV.</response>
    /// <response code="400">An unknown <c>groupBy</c>, or <c>from</c> after <c>to</c>.</response>
    [HttpGet("attendance-analytics/export.csv")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.ReportsRead)]
    [HasPermissionNotEnforced(EamsPermissions.ReportsRead)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, AttendanceAnalyticsCsv.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AttendanceAnalyticsExportCsv(
        [FromQuery] string? groupBy, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] bool includeCancelled, [FromQuery] string? course, [FromQuery] string? yearLevel,
        [FromQuery] string? section, CancellationToken ct)
    {
        var result = await _analytics.GetReportAsync(
            new AttendanceAnalyticsQuery(groupBy, from, to, includeCancelled, course, yearLevel, section), ct);
        if (result.Outcome != AttendanceAnalyticsOutcome.Ok) return AnalyticsFailure(result);

        // A report names no student here (it is aggregate), but it is still a moving snapshot; do not cache.
        Response.Headers.CacheControl = "no-store";

        return File(
            AttendanceAnalyticsCsv.Write(result.Report!), AttendanceAnalyticsCsv.ContentType,
            AttendanceAnalyticsCsv.FileNameFor(result.Report!));
    }

    private ObjectResult AnalyticsFailure(AttendanceAnalyticsResult result)
    {
        var status = result.Outcome == AttendanceAnalyticsOutcome.ValidationFailed
            ? StatusCodes.Status400BadRequest
            : throw new ArgumentOutOfRangeException(
                nameof(result), result.Outcome,
                $"No HTTP status is mapped for this {nameof(AttendanceAnalyticsOutcome)}.");

        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: "The report could not be produced.",
            detail: result.Message);
        problem.Extensions[ErrorCodeProperty] = result.Outcome.ToString();
        return StatusCode(status, problem);
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
