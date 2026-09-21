using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// How a multi-event report request ended. Every member maps to exactly one status in
/// <c>ReportsController.StatusCodeFor</c>, and only <see cref="Ready"/> is a 200.
/// </summary>
public enum MultiEventReportOutcome
{
    /// <summary>The report was built.</summary>
    Ready,

    /// <summary>No event id was supplied. 400: an empty report would read as "nothing happened".</summary>
    SelectionEmpty,

    /// <summary>
    /// More than <see cref="ReportLimits.MaxEventsPerReport"/> distinct ids. 400, and never truncated:
    /// a report that silently dropped events would total a population nobody picked.
    /// </summary>
    SelectionTooLarge,

    /// <summary>
    /// At least one id names no event in the caller's school — nonexistent, soft-deleted, or another
    /// school's, and deliberately indistinguishable, as every tenant miss on this API is. 404, and
    /// the whole report is refused rather than built over the events that were found: totals over a
    /// subset would be a number for a selection the operator did not make.
    /// </summary>
    EventNotFound,
}

/// <summary>The result of a multi-event report request. <paramref name="Report"/> is null unless ready.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">A sentence for a person; empty when ready.</param>
/// <param name="Report">The report, when <paramref name="Outcome"/> is <see cref="MultiEventReportOutcome.Ready"/>.</param>
/// <param name="MissingEventIds">
/// The requested ids that resolved to no event, when <paramref name="Outcome"/> is
/// <see cref="MultiEventReportOutcome.EventNotFound"/>. They are the caller's own input, so naming them
/// discloses nothing a single-event request would not.
/// </param>
public record MultiEventReportResponse(
    MultiEventReportOutcome Outcome,
    string Message,
    MultiEventReportDto? Report,
    IReadOnlyList<Guid> MissingEventIds);

/// <summary>
/// Technical Plan §6.7's reports module, as QA Q15/Q16 (MDVault #463 Part D) scoped it: a summary of
/// one event and a summary of several. Implemented in EAMS.Infrastructure; controllers see only this.
///
/// <para>
/// <b>It computes nothing of its own.</b> Every per-event figure comes from the same implementation
/// behind <c>GET /events/{id}/summary</c>, so a report and the event page cannot disagree about one
/// event. The only arithmetic here is summing those figures for the pooled totals.
/// </para>
/// </summary>
public interface IReportService
{
    /// <summary>
    /// §6.7 <c>GET /reports/event/{eventId}/summary</c>. Null when the event is not in the caller's
    /// school, or is soft-deleted.
    /// </summary>
    Task<EventReportRowDto?> GetEventSummaryAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /reports/events/summary</c> — one row per distinct event and their pooled totals.
    /// Duplicated ids count once; see <see cref="MultiEventReportOutcome"/> for the refusals.
    /// </summary>
    Task<MultiEventReportResponse> GetMultiEventSummaryAsync(
        IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default);
}
