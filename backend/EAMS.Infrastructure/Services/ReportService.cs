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
