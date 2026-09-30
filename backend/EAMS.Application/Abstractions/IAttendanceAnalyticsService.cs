using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>The outcome of an analytics query.</summary>
public enum AttendanceAnalyticsOutcome
{
    Ok,

    /// <summary>An unknown <c>groupBy</c>, or a From later than To. 400.</summary>
    ValidationFailed,
}

/// <summary>The result of an analytics query. The report is null unless the outcome is Ok.</summary>
public record AttendanceAnalyticsResult(
    AttendanceAnalyticsOutcome Outcome, string Message, AttendanceAnalyticsReportDto? Report);

/// <summary>
/// <b>Attendance analytics (Reports-Module-Enhancement.docx RPT-01, scoped).</b> Groups the recorded
/// student attendance by course, year level or section over a date range, with a Present / Late / Absent /
/// Excused breakdown and an attendance rate, and drills down to the event level.
///
/// <para>
/// <b>Student attendance only.</b> Attendance is recorded against students (there is no personnel
/// attendance in the model), so the population filter and personnel grouping the spec describes do not
/// apply here; the Eligible / Registered denominators and Academic-Year grouping need event-to-roster and
/// event-to-term links that are follow-ons.
/// </para>
/// </summary>
public interface IAttendanceAnalyticsService
{
    /// <summary><c>GET /reports/attendance-analytics</c> — the grouped report for the query.</summary>
    Task<AttendanceAnalyticsResult> GetReportAsync(
        AttendanceAnalyticsQuery query, CancellationToken ct = default);
}
