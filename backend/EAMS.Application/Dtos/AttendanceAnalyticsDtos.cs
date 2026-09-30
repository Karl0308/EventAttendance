namespace EAMS.Application.Dtos;

/// <summary>
/// The grouping dimensions the attendance-analytics report supports (Reports-Module-Enhancement.docx
/// RPT-01, scoped to the fields the data model actually carries).
///
/// <para>
/// <b>Course / Year level / Section, not Department / Program / College.</b> A <c>Student</c> carries a
/// single <c>Course</c>, <c>YearLevel</c> and <c>Section</c> (the ADR-001 D-2 derived cache); the
/// independent Department / Program / College dimensions the spec draws live in the academic-structure
/// tables and are not linked onto attendance yet, so grouping by them is a follow-on. <c>Event</c> is the
/// last drill-down level.
/// </para>
/// </summary>
public static class AttendanceGroupBy
{
    public const string Course = "Course";
    public const string YearLevel = "YearLevel";
    public const string Section = "Section";
    public const string Event = "Event";

    public static readonly IReadOnlyList<string> All = [Course, YearLevel, Section, Event];

    /// <summary>Case-insensitively maps a value onto its canonical spelling; false for anything else.</summary>
    public static bool TryNormalize(string? value, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var candidate in All)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                canonical = candidate;
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// The query behind <c>GET /reports/attendance-analytics</c>. Drill-down is expressed by narrowing the
/// filters and asking for a finer <see cref="GroupBy"/>: Course → (filter course) Section → (filter section)
/// Event.
/// </summary>
/// <param name="GroupBy">One of <see cref="AttendanceGroupBy"/>. Defaults to Course when unset.</param>
/// <param name="From">Only events starting on/after this instant (UTC).</param>
/// <param name="To">Only events starting on/before this instant (UTC).</param>
/// <param name="IncludeCancelled">Cancelled events are excluded unless this is true (spec default).</param>
/// <param name="Course">Drill-down filter — only this course's students.</param>
/// <param name="YearLevel">Drill-down filter — only this year level.</param>
/// <param name="Section">Drill-down filter — only this section.</param>
public record AttendanceAnalyticsQuery(
    string? GroupBy,
    DateTime? From,
    DateTime? To,
    bool IncludeCancelled,
    string? Course,
    string? YearLevel,
    string? Section);

/// <summary>
/// One row of the attendance-analytics report — a group (course/year/section) or, at the last level, one
/// event.
/// </summary>
/// <param name="Key">The group value, or the event name at the Event level.</param>
/// <param name="EventId">Set only at the Event level, so the SPA can link into the Event Module.</param>
/// <param name="EventDate">Set only at the Event level.</param>
/// <param name="TotalEvents">Distinct events contributing to this row.</param>
/// <param name="People">Distinct students contributing to this row.</param>
/// <param name="AttendanceRate">(Present + Late) / Total, as a percentage 0..100, or 0 when Total is 0.</param>
public record AttendanceAnalyticsRowDto(
    string Key,
    Guid? EventId,
    DateTime? EventDate,
    int TotalEvents,
    int People,
    int Present,
    int Late,
    int Absent,
    int Excused,
    int Total,
    double AttendanceRate);

/// <summary>The whole report — the group rows and a totals row over the same scope.</summary>
public record AttendanceAnalyticsReportDto(
    string GroupBy,
    IReadOnlyList<AttendanceAnalyticsRowDto> Rows,
    AttendanceAnalyticsRowDto Totals);
