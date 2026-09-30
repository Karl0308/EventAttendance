namespace EAMS.Domain;

/// <summary>
/// Where a tap falls relative to an event's <b>per-event</b> attendance grace periods
/// (EventGracePeriod.docx). Distinct from <see cref="TapTimeWindow"/>, which is the school-wide
/// clock-skew/forgery bound: this is the organizer's per-event eligibility window, evaluated after the
/// forgery window has already admitted the tap.
/// </summary>
public enum GraceEligibility
{
    /// <summary>Inside the eligibility window — the tap is accepted (Present or Late is decided elsewhere).</summary>
    Ok,

    /// <summary>Before <c>StartAt − GraceBeforeStartMinutes</c>. Rejected with the "Too Early" prompt.</summary>
    TooEarly,

    /// <summary>After <c>EndAt + GraceAfterEndMinutes</c>. Rejected with the "Attendance Closed" prompt.</summary>
    Closed,
}

/// <summary>
/// The per-event attendance grace-period rule (EventGracePeriod.docx). Three configurable periods decide
/// how a tap is handled before, during, and after an event:
/// <list type="number">
/// <item>Grace before start — a tap earlier than <c>StartAt − beforeStart</c> is <see cref="GraceEligibility.TooEarly"/>.</item>
/// <item>Grace after start — a tap at or before <c>StartAt + afterStart</c> is on-time (Present); later, while
/// still eligible, it is Late. That split lives in the tap path (it already computed Present/Late from
/// <c>GraceMinutes</c>); <c>afterStart</c> <em>is</em> <c>Event.GraceMinutes</c>.</item>
/// <item>Grace after end — a tap later than <c>EndAt + afterEnd</c> is <see cref="GraceEligibility.Closed"/>.</item>
/// </list>
///
/// <para>
/// <b>The before/after periods are nullable, and null is load-bearing.</b> Null means "no per-event limit
/// on this side" — the school-wide <see cref="TapTimeWindow"/> still bounds the tap — so an event created
/// before this feature, or one an organizer left unset, behaves exactly as it did. A value (including
/// <c>0</c>, "no grace at all") tightens the window for that event.
/// </para>
/// </summary>
public static class EventGrace
{
    /// <summary>The prompt for a tap that arrives before attendance opens (EventGracePeriod.docx §1).</summary>
    public const string TooEarlyMessage =
        "Too Early for Attendance. Attendance is not yet open for this event. Please try again closer " +
        "to the event start time.";

    /// <summary>The prompt for a tap that arrives after attendance has closed (EventGracePeriod.docx §3).</summary>
    public const string ClosedMessage =
        "Attendance Closed. The attendance period for this event has ended. Your tap can no longer be " +
        "accepted.";

    /// <summary>
    /// Where <paramref name="when"/> falls relative to the event's per-event grace window. All instants
    /// must already be UTC. A null bound means that side is unbounded (see the type remarks).
    /// </summary>
    public static GraceEligibility Evaluate(
        DateTime when, DateTime startAt, DateTime endAt,
        int? graceBeforeStartMinutes, int? graceAfterEndMinutes)
    {
        if (graceBeforeStartMinutes is { } before && when < startAt.AddMinutes(-before))
            return GraceEligibility.TooEarly;

        if (graceAfterEndMinutes is { } after && when > endAt.AddMinutes(after))
            return GraceEligibility.Closed;

        return GraceEligibility.Ok;
    }
}
