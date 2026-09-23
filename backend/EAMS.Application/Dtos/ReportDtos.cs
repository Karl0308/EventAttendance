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

// ============================================================================ Task 9.6 — one event in detail

/// <summary>
/// The event's own particulars, as a Task 9.6 report prints them above the figures (client QA,
/// MDVault #470 / #463): name, location, date and time, mode and grace.
/// </summary>
/// <param name="EventId">The event.</param>
/// <param name="Name">Its name, as stored.</param>
/// <param name="Location">Where it is held; null when none was entered.</param>
/// <param name="StartAt">When it starts, UTC.</param>
/// <param name="EndAt">When it ends, UTC.</param>
/// <param name="AttendanceMode"><c>Single</c> or <c>TimeInOut</c>.</param>
/// <param name="GraceMinutes">Minutes after <paramref name="StartAt"/> a first tap still counts as <c>Present</c>.</param>
/// <param name="Status"><c>Draft</c>, <c>Open</c>, <c>Closed</c> or <c>Cancelled</c>.</param>
public record EventReportDetailsDto(
    Guid EventId,
    string Name,
    string? Location,
    DateTime StartAt,
    DateTime EndAt,
    string AttendanceMode,
    int GraceMinutes,
    string Status);

/// <summary>
/// Task 9.6's time-out totals for a <c>TimeInOut</c> event. <b>Never present for a <c>Single</c>
/// event</b> (client QA Q12: single-tap events are unchanged and carry no time-out statistics) — the
/// whole object is <c>null</c> there, rather than a set of zeros that would read as "nobody left".
///
/// <para>
/// <b>Which rows count.</b> Every attendance row of the event with a Time In (<c>CheckInAt</c>), in
/// any status — the same rows, soft-deleted students included, that the <c>present</c>/<c>late</c>/
/// <c>absent</c>/<c>excused</c> buckets beside it count, so the two never describe different
/// populations. An <c>Absent</c> row written when the event closed has no Time In and is therefore not
/// "tapped in". A row whose status was later overridden (to <c>Absent</c>, say) keeps the Time In its
/// tap recorded and is still counted: these figures describe taps observed, the buckets describe the
/// status an operator settled on.
/// </para>
///
/// <para>
/// <b>By construction <see cref="TappedIn"/> = <see cref="WithTimeOut"/> + <see cref="WithoutTimeOut"/>.</b>
/// A Time Out counts only on a row that also has a Time In (a complete pair); see
/// <see cref="TappedOut"/>.
/// </para>
/// </summary>
/// <param name="TappedIn">Rows with a Time In.</param>
/// <param name="TappedOut">
/// Rows with both a Time In and a Time Out. <b>Always equal to <paramref name="WithTimeOut"/></b>:
/// QA's 9.6 lists both headings, and in this system they are one count — Time Out is the student's
/// last accepted tap, forward only, so a row either has one or does not. It is published twice so each
/// heading has its own field; a test pins the equality so a QA correction that separates them shows up
/// as a red test rather than as a silently duplicated number.
/// </param>
/// <param name="WithTimeOut">Rows with a Time In and a Time Out — the complete pairs.</param>
/// <param name="WithoutTimeOut">
/// Rows with a Time In and no Time Out: <paramref name="TappedIn"/> − <paramref name="WithTimeOut"/>.
/// Published beside <paramref name="AverageDurationSeconds"/> so the average is never read without
/// the number of people it leaves out (QA recorded assumption A6).
/// </param>
/// <param name="AverageDurationSeconds">
/// The mean of (Time Out − Time In) over the complete pairs <b>only</b> — a missing Time Out is left
/// out, never counted as zero (A6). Whole seconds, rounded half away from zero, computed by SQL
/// Server as <c>DATEDIFF(SECOND, …)</c> — so each pair is measured in second boundaries crossed, the
/// same way every row's <see cref="EventReportAttendeeDto.DurationSeconds"/> is, and this is exactly
/// the rounded mean of those. <c>null</c> when there is no complete pair.
/// </param>
public record EventTimeOutTotalsDto(
    int TappedIn,
    int TappedOut,
    int WithTimeOut,
    int WithoutTimeOut,
    long? AverageDurationSeconds);

/// <summary>
/// <c>GET /reports/event/{eventId}/detail</c> — Task 9.6's single-event report: the event's
/// particulars, its summary figures, and (for a <c>TimeInOut</c> event) its time-out totals. The
/// student list is paged separately (<c>GET /reports/event/{eventId}/attendees</c>) and the whole thing
/// exports as one CSV (<c>GET /reports/event/{eventId}/export.csv</c>).
/// </summary>
/// <param name="Event">Name, location, date and time, mode, grace.</param>
/// <param name="Summary">
/// Exactly the object <c>GET /reports/event/{eventId}/summary</c> returns, from the same code.
/// </param>
/// <param name="TimeInOut">
/// The time-out totals for a <c>TimeInOut</c> event; <b><c>null</c> for a <c>Single</c> event</b> (QA Q12).
/// </param>
public record EventDetailReportDto(
    EventReportDetailsDto Event,
    EventReportRowDto Summary,
    EventTimeOutTotalsDto? TimeInOut);

/// <summary>
/// One line of a Task 9.6 student list: a student who tapped in. Ordered by last name, first name,
/// middle name, student number, Time In, then record id — so the order is total and a printout, the
/// CSV and every page agree.
/// </summary>
/// <param name="StudentId">The student.</param>
/// <param name="StudentNumber">The school's student number, as stored.</param>
/// <param name="LastName">Last name, as stored.</param>
/// <param name="FirstName">First name, as stored.</param>
/// <param name="MiddleName">Middle name, as stored; null when there is none.</param>
/// <param name="TimeIn">The Time In (<c>CheckInAt</c>), UTC. Never null: the list is the tapped-in rows.</param>
/// <param name="TimeOut">
/// The Time Out (<c>CheckOutAt</c> — the last accepted tap), UTC; null when the student never timed
/// out, and <b>always null for a <c>Single</c> event</b> (QA Q12).
/// </param>
/// <param name="DurationSeconds">
/// <paramref name="TimeOut"/> − <paramref name="TimeIn"/> in whole seconds (SQL Server
/// <c>DATEDIFF(SECOND, …)</c>); null exactly when <paramref name="TimeOut"/> is.
/// </param>
public record EventReportAttendeeDto(
    Guid StudentId,
    string StudentNumber,
    string LastName,
    string FirstName,
    string? MiddleName,
    DateTime TimeIn,
    DateTime? TimeOut,
    long? DurationSeconds);

/// <summary>
/// The whole of a Task 9.6 report in one value — what the CSV export is written from. Its
/// <see cref="Attendees"/> is the unpaged form of <c>GET /reports/event/{eventId}/attendees</c>,
/// produced by the same query and the same ordering, which is what makes the CSV agree with the screen
/// row for row.
/// </summary>
/// <param name="Report">The same object <c>GET /reports/event/{eventId}/detail</c> returns.</param>
/// <param name="Attendees">Every tapped-in row, in the list's documented order.</param>
public record EventReportExport(
    EventDetailReportDto Report,
    IReadOnlyList<EventReportAttendeeDto> Attendees);
