using EAMS.Application.Dtos;
using EAMS.Domain;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// <b>The one implementation of an event's summary arithmetic, as a seam other services in this
/// assembly may call.</b> Implemented by <see cref="EventService"/> alone.
///
/// <para>
/// <b>Internal, and deliberately not on <c>IEventService</c>.</b> It takes an <see cref="Event"/>
/// entity and returns the raw <c>attended</c> count beside the published summary, neither of which
/// belongs in the Application contract controllers speak. It exists so <c>ReportService</c> can pool
/// several events' figures without copying the denominator, which ADR-003 D-12/D-13 record as failing
/// silently, and without depending on the concrete <see cref="EventService"/>.
/// </para>
///
/// <para>
/// Registered by <c>AddEamsInfrastructure</c> as a forward to the same scoped <see cref="EventService"/>
/// instance <c>IEventService</c> resolves to — one instance and one <c>DbContext</c> per request.
/// </para>
/// </summary>
internal interface IEventSummaryFigures
{
    /// <summary>
    /// One event's summary together with the <c>attended</c> count behind its rate. See
    /// <see cref="EventService.FiguresForAsync"/>.
    /// </summary>
    /// <param name="e">The event, already read, tenant-filtered and not soft-deleted.</param>
    /// <param name="ceiling">
    /// The live cursor's <c>RowVersion</c> ceiling, or <c>null</c> for every committed row. A report
    /// passes <c>null</c>, exactly as <c>GET /events/{id}/summary</c> does.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<EventSummaryFigures> FiguresForAsync(Event e, long? ceiling, CancellationToken ct);
}

/// <summary>
/// A summary plus the numerator of its rate: expected students with a <c>Present</c> or <c>Late</c>
/// record. Internal, and never serialized — the public contract stays <see cref="EventSummaryDto"/>.
/// </summary>
/// <param name="Summary">The figures <c>GET /events/{id}/summary</c> publishes.</param>
/// <param name="Attended">The numerator of <see cref="EventSummaryDto.AttendanceRate"/>.</param>
internal readonly record struct EventSummaryFigures(EventSummaryDto Summary, int Attended)
{
    /// <summary>
    /// <c>(invited students who attended) / (invited students)</c>, to one decimal place — the one
    /// rounding rule for every rate this API publishes, per event and pooled.
    ///
    /// <para>
    /// <b>The numerator is a count of people drawn from the denominator's own set</b>, produced by
    /// <c>Intersect</c> — SQL <c>INTERSECT</c>, distinct on both sides — so it cannot exceed
    /// <paramref name="expected"/> however many rows exist or how they are shaped. That is why there is
    /// no clamp here: a <c>Math.Min</c> would have capped a number that was still being computed wrongly
    /// and hidden the walk-ins that made it wrong. They are reported separately instead.
    /// </para>
    /// </summary>
    public static double RateOf(int attended, int expected) =>
        expected == 0 ? 0 : Math.Round((double)attended / expected * 100, 1);
}
