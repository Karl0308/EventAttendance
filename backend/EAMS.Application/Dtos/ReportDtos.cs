namespace EAMS.Application.Dtos;

/// <summary>
/// The named bounds of the §6.7 reports module (QA Q15, MDVault #463 Part D).
/// </summary>
public static class ReportLimits
{
    /// <summary>
    /// <b>The most distinct events one multi-event report may name.</b> A request over this is
    /// refused with <c>400 SelectionTooLarge</c> rather than truncated, because a report that quietly
    /// dropped the fifty-first event would total a population the operator did not pick.
    ///
    /// <para>
    /// <b>Why there is a ceiling at all.</b> The deployed server is SQL Server 2012 (compatibility
    /// level 110 — see <c>DependencyInjection.SqlServerCompatibilityLevel</c>), where EF Core inlines a
    /// <c>.Contains(list)</c> as literals instead of one parameterized <c>OPENJSON</c>: every distinct
    /// list is a distinct SQL text and a fresh plan compile. And each event in the report costs about
    /// four aggregate queries, because the report reuses the one summary implementation rather than
    /// copying its denominator (ADR-003 D-12/D-13). Both costs are linear in this number, so it is a
    /// named bound rather than whatever a query string happens to carry.
    /// </para>
    ///
    /// <para>
    /// Duplicated ids count once: the limit is on distinct events, which is what the work scales with.
    /// </para>
    /// </summary>
    public const int MaxEventsPerReport = 50;
}

/// <summary>
/// One event's line in a §6.7 report — the same figures <c>GET /events/{id}/summary</c> publishes,
/// computed by the same code, plus what a report needs beside them.
/// </summary>
/// <param name="EventId">The event.</param>
/// <param name="EventName">Its name, as stored.</param>
/// <param name="Status"><c>Draft</c>, <c>Open</c>, <c>Closed</c> or <c>Cancelled</c>.</param>
/// <param name="StartAt">When it starts, UTC.</param>
/// <param name="Expected">
/// The invited population (ADR-003 D-19). For a <c>Closed</c> or <c>Cancelled</c> event this is the
/// audience frozen when it closed, so a later roster import cannot move a past event's number.
/// </param>
/// <param name="Attended">
/// Expected students with a <c>Present</c> or <c>Late</c> record — the numerator of
/// <paramref name="AttendanceRate"/>. It is published so a multi-event total can be checked by hand:
/// the pooled rate is the sum of these over the sum of <paramref name="Expected"/>.
/// </param>
/// <param name="Present">Attendance rows in the <c>Present</c> bucket, walk-ins included.</param>
/// <param name="Late">Attendance rows in the <c>Late</c> bucket, walk-ins included.</param>
/// <param name="Absent">Attendance rows in the <c>Absent</c> bucket.</param>
/// <param name="Excused">Attendance rows in the <c>Excused</c> bucket.</param>
/// <param name="Unexpected">Students with a record who were not in the expected population.</param>
/// <param name="AttendanceRate">
/// <paramref name="Attended"/> ÷ <paramref name="Expected"/> × 100, to one decimal place; 0 when
/// nothing is expected. It cannot exceed 100.
/// </param>
public record EventReportRowDto(
    Guid EventId,
    string EventName,
    string Status,
    DateTime StartAt,
    int Expected,
    int Attended,
    int Present,
    int Late,
    int Absent,
    int Excused,
    int Unexpected,
    double AttendanceRate);

/// <summary>
/// The totals of a multi-event report.
///
/// <para>
/// <b>Pooled, not averaged.</b> <see cref="AttendanceRate"/> is every attendee over every expected
/// student across the picked events — so a 500-student assembly weighs 500 times what a one-student
/// event does. The average of per-event rates would let a small event swing the headline number, and
/// it could not be reproduced from the rows.
/// </para>
/// </summary>
/// <param name="EventCount">Distinct events in the report.</param>
/// <param name="Expected">
/// The sum of each event's expected population. A student invited to three events counts three times:
/// the unit is a seat at an event, not a person.
/// </param>
/// <param name="Attended">The sum of each event's <c>attended</c>.</param>
/// <param name="Present">The sum of each event's <c>present</c>.</param>
/// <param name="Late">The sum of each event's <c>late</c>.</param>
/// <param name="Absent">The sum of each event's <c>absent</c>.</param>
/// <param name="Excused">The sum of each event's <c>excused</c>.</param>
/// <param name="Unexpected">The sum of each event's <c>unexpected</c>.</param>
/// <param name="AttendanceRate">
/// <paramref name="Attended"/> ÷ <paramref name="Expected"/> × 100, to one decimal place, rounded the
/// same way each event's rate is; 0 when nothing is expected.
/// </param>
public record EventReportTotalsDto(
    int EventCount,
    int Expected,
    int Attended,
    int Present,
    int Late,
    int Absent,
    int Excused,
    int Unexpected,
    double AttendanceRate);

/// <summary>A summary of several hand-picked events: one row each, and their pooled totals.</summary>
/// <param name="Events">One row per distinct event, ordered by start time, then id.</param>
/// <param name="Totals">The pooled totals over <paramref name="Events"/>.</param>
public record MultiEventReportDto(
    IReadOnlyList<EventReportRowDto> Events, EventReportTotalsDto Totals);
