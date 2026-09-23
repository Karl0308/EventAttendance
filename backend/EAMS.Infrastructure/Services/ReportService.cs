using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// §6.7's reports — one event, and several (QA Q15/Q16, MDVault #463 Part D).
///
/// <para>
/// <b>Every per-event number comes from <see cref="IEventSummaryFigures.FiguresForAsync"/></b> —
/// <c>EventService</c>'s one implementation, reached through an interface. ADR-003 D-12/D-13 record that the summary's denominator fails <em>silently</em> when it is copied — a second
/// copy drifts and every number stays plausible — so this class owns no attendance arithmetic at all.
/// It reads the events, hands each to the one implementation, and sums what comes back. A closed
/// event therefore reports its frozen audience for the same reason <c>GET /events/{id}/summary</c>
/// does: it is the same code.
/// </para>
///
/// <para>
/// <b>Tenancy is the global <c>SchoolId</c> query filter,</b> exactly as on the events reads: another
/// school's event is not found, indistinguishable from one that does not exist.
/// </para>
/// </summary>
internal sealed class ReportService : IReportService
{
    private readonly EamsDbContext _db;
    private readonly IEventSummaryFigures _events;

    public ReportService(EamsDbContext db, IEventSummaryFigures events)
    {
        _db = db;
        _events = events;
    }

    /// <inheritdoc/>
    public async Task<EventReportRowDto?> GetEventSummaryAsync(
        Guid eventId, CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && !e.IsDeleted, ct);
        if (ev is null) return null;

        return RowFor(ev, await _events.FiguresForAsync(ev, ceiling: null, ct));
    }

    /// <inheritdoc/>
    public async Task<MultiEventReportResponse> GetMultiEventSummaryAsync(
        IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default)
    {
        var distinct = eventIds.Distinct().ToList();

        if (distinct.Count == 0)
            return Refused(
                MultiEventReportOutcome.SelectionEmpty,
                "Pick at least one event to report on.");

        if (distinct.Count > ReportLimits.MaxEventsPerReport)
            return Refused(
                MultiEventReportOutcome.SelectionTooLarge,
                $"A report can cover at most {ReportLimits.MaxEventsPerReport} events; " +
                $"{distinct.Count} were picked.");

        // One round trip for every event, bounded by the check above — which matters on SQL Server
        // 2012, where this Contains becomes an inline literal list (see ReportLimits).
        var events = await _db.Events.AsNoTracking()
            .Where(e => distinct.Contains(e.Id) && !e.IsDeleted)
            .OrderBy(e => e.StartAt).ThenBy(e => e.Id)
            .ToListAsync(ct);

        if (events.Count != distinct.Count)
        {
            var found = events.Select(e => e.Id).ToHashSet();
            var missing = distinct.Where(id => !found.Contains(id)).ToList();
            return new MultiEventReportResponse(
                MultiEventReportOutcome.EventNotFound,
                $"{missing.Count} of the picked events could not be found.",
                null,
                missing);
        }

        // PERFORMANCE TRADEOFF, accepted by JJ (P3): a bounded, sequential loop of roughly four
        // aggregate queries per event — at most ReportLimits.MaxEventsPerReport events, so about 200
        // round trips in the worst case. No attendance row is materialized; each query is a COUNT.
        // The set-based alternative would rewrite the shared denominator (live vs frozen audience per
        // event) into a second implementation, which ADR-003 D-12/D-13 record as drifting silently.
        // Sequential because one DbContext cannot run queries concurrently. Revisit only if a
        // measured report is slow — and then by making FiguresForAsync set-based for every caller,
        // never by copying it here.
        var rows = new List<EventReportRowDto>(events.Count);
        var attended = 0;
        foreach (var ev in events)
        {
            var figures = await _events.FiguresForAsync(ev, ceiling: null, ct);
            rows.Add(RowFor(ev, figures));
            attended += figures.Attended;
        }

        var expected = rows.Sum(r => r.Expected);
        var totals = new EventReportTotalsDto(
            rows.Count,
            expected,
            attended,
            rows.Sum(r => r.Present),
            rows.Sum(r => r.Late),
            rows.Sum(r => r.Absent),
            rows.Sum(r => r.Excused),
            rows.Sum(r => r.Unexpected),
            // Pooled: every attendee over every expected seat, rounded by the one rule each event's
            // own rate uses. Not the mean of the rows' rates.
            EventSummaryFigures.RateOf(attended, expected));

        return new MultiEventReportResponse(
            MultiEventReportOutcome.Ready, "", new MultiEventReportDto(rows, totals), []);
    }

    // ------------------------------------------------------------------ Task 9.6 — one event in detail

    /// <inheritdoc/>
    public async Task<EventDetailReportDto?> GetEventDetailAsync(
        Guid eventId, CancellationToken ct = default)
    {
        var ev = await FindEventAsync(eventId, ct);
        return ev is null ? null : await DetailForAsync(ev, ct);
    }

    /// <inheritdoc/>
    public async Task<PagedResult<EventReportAttendeeDto>?> GetEventAttendeesAsync(
        Guid eventId, PageRequest page, CancellationToken ct = default)
    {
        var ev = await FindEventAsync(eventId, ct);
        if (ev is null) return null;

        var timeInOut = IsTimeInOut(ev);
        return await TappedIn(ev.Id).ToPageAsync(rows => AttendeesInOrder(rows, timeInOut), page, ct);
    }

    /// <inheritdoc/>
    public async Task<EventReportExport?> GetEventExportAsync(
        Guid eventId, CancellationToken ct = default)
    {
        var ev = await FindEventAsync(eventId, ct);
        if (ev is null) return null;

        var report = await DetailForAsync(ev, ct);

        // The same filter and the same order-and-projection the paged read uses, unpaged. That is the
        // whole of "the CSV agrees with the screen row for row": there is no second query to drift.
        // Materialized rather than streamed — one event's attendees are thousands of short rows, and
        // holding them lets the controller answer a clean 404 before any byte of the file is written.
        var attendees = await AttendeesInOrder(TappedIn(ev.Id), IsTimeInOut(ev)).ToListAsync(ct);

        return new EventReportExport(report, attendees);
    }

    private Task<Event?> FindEventAsync(Guid eventId, CancellationToken ct) =>
        _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && !e.IsDeleted, ct);

    private async Task<EventDetailReportDto> DetailForAsync(Event ev, CancellationToken ct)
    {
        var summary = RowFor(ev, await _events.FiguresForAsync(ev, ceiling: null, ct));

        var details = new EventReportDetailsDto(
            ev.Id, ev.Name, ev.Location, ev.StartAt, ev.EndAt,
            ev.AttendanceMode, ev.GraceMinutes, ev.Status);

        // QA Q12: a single-tap event is unchanged and carries no time-out statistics at all.
        var timeOut = IsTimeInOut(ev) ? await TimeOutTotalsAsync(ev.Id, ct) : null;

        return new EventDetailReportDto(details, summary, timeOut);
    }

    private static bool IsTimeInOut(Event ev) => ev.AttendanceMode == AttendanceMode.TimeInOut;

    /// <summary>
    /// The population every Task 9.6 figure and row is drawn from: this event's attendance rows with a
    /// Time In. Tenant-scoped by the <c>AttendanceRecord</c> query filter, as every attendance read is.
    ///
    /// <para>
    /// <b>No student soft-delete filter, deliberately — the same rows the summary's status buckets
    /// count.</b> A report beside those buckets that silently dropped a since-deleted student's tap
    /// would describe a different population from the numbers next to it, and a past event's report
    /// would shrink whenever the roster was tidied.
    /// </para>
    ///
    /// <para>
    /// An <c>Absent</c> row written when the event closed carries no Time In, so it is not here. A row
    /// carrying a Time Out and <em>no</em> Time In — which the tap path can produce on a manually
    /// created <c>Absent</c>/<c>Excused</c> row in a <c>TimeInOut</c> event — is not here either: it
    /// is not a pair, and counting its Time Out would break <c>tappedIn = withTimeOut +
    /// withoutTimeOut</c>.
    /// </para>
    /// </summary>
    private IQueryable<AttendanceRecord> TappedIn(Guid eventId) =>
        _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.EventId == eventId && a.CheckInAt != null);

    /// <summary>
    /// Task 9.6's totals as <b>one aggregate statement</b> — no attendance row is materialized.
    ///
    /// <para>
    /// <b>SQL Server 2012 safety (compatibility level 110), and the overflow reasoning.</b>
    /// <c>DATEDIFF_BIG</c> is 2016+, so each pair is <c>DATEDIFF(SECOND, CheckInAt, CheckOutAt)</c>,
    /// which returns <c>int</c> and overflows only past 2^31 seconds — about 68 years. A tap is only
    /// accepted inside its event's window plus the D-36 slack, so a pair that long needs an event that
    /// long. Seconds rather than milliseconds because <c>DATEDIFF(MILLISECOND)</c> overflows at 24.8
    /// days, which a multi-week event could reach. The <em>sum</em> is the real hazard:
    /// <c>AVG(int)</c> accumulates in <c>int</c>, so 10,000 attendees averaging three days
    /// (259,200 s) would total 2.6 billion and fail. The average is therefore taken over the value cast
    /// to <c>float</c> (EF's translation of <c>Average</c> over <c>double?</c>), whose range is not a
    /// concern at any attendance size.
    /// </para>
    ///
    /// <para>
    /// <c>AVG</c> ignores <c>NULL</c>, and the duration is <c>NULL</c> for a row with no Time Out — so
    /// the mean is over complete pairs only, never with a missing Time Out counted as zero (QA A6). It
    /// is <c>NULL</c> when there is no complete pair.
    /// </para>
    /// </summary>
    private async Task<EventTimeOutTotalsDto> TimeOutTotalsAsync(Guid eventId, CancellationToken ct)
    {
        var aggregate = await TappedIn(eventId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TappedIn = g.Count(),
                WithTimeOut = g.Count(a => a.CheckOutAt != null),
                AverageSeconds = g.Average(a => (double?)EF.Functions.DateDiffSecond(a.CheckInAt, a.CheckOutAt)),
            })
            .FirstOrDefaultAsync(ct);

        // GroupBy(_ => 1) yields no row when nobody tapped in: an honest set of zeros and no average.
        var tappedIn = aggregate?.TappedIn ?? 0;
        var withTimeOut = aggregate?.WithTimeOut ?? 0;

        return new EventTimeOutTotalsDto(
            TappedIn: tappedIn,
            TappedOut: withTimeOut,
            WithTimeOut: withTimeOut,
            WithoutTimeOut: tappedIn - withTimeOut,
            AverageDurationSeconds: aggregate?.AverageSeconds is { } mean
                ? (long)Math.Round(mean, MidpointRounding.AwayFromZero)
                : null);
    }

    /// <summary>
    /// The student list's one ordering and one projection, shared by the paged read and the export.
    /// The order is total — the record id breaks every remaining tie — so a page boundary, a printout
    /// and the CSV all put the same row in the same place.
    /// </summary>
    private static IQueryable<EventReportAttendeeDto> AttendeesInOrder(
        IQueryable<AttendanceRecord> rows, bool timeInOut) =>
        rows
            .OrderBy(a => a.Student!.LastName)
            .ThenBy(a => a.Student!.FirstName)
            .ThenBy(a => a.Student!.MiddleName)
            .ThenBy(a => a.Student!.StudentNumber)
            .ThenBy(a => a.CheckInAt)
            .ThenBy(a => a.Id)
            .Select(a => new EventReportAttendeeDto(
                a.StudentId,
                a.Student!.StudentNumber,
                a.Student!.LastName,
                a.Student!.FirstName,
                a.Student!.MiddleName,
                a.CheckInAt!.Value,
                // QA Q12: a Single event shows no Time Out whatever the row holds.
                timeInOut ? a.CheckOutAt : null,
                timeInOut && a.CheckOutAt != null
                    ? (long?)EF.Functions.DateDiffSecond(a.CheckInAt, a.CheckOutAt)
                    : null));

    private static EventReportRowDto RowFor(Event ev, EventSummaryFigures figures)
    {
        var s = figures.Summary;
        return new EventReportRowDto(
            s.EventId, s.EventName, ev.Status, ev.StartAt,
            s.Expected, figures.Attended,
            s.Present, s.Late, s.Absent, s.Excused, s.Unexpected,
            s.AttendanceRate);
    }

    private static MultiEventReportResponse Refused(MultiEventReportOutcome outcome, string message) =>
        new(outcome, message, null, []);
}
