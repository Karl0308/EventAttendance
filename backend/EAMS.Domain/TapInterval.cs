using System.Globalization;

namespace EAMS.Domain;

/// <summary>
/// Which recorded tap a later tap was judged against.
/// </summary>
public enum TapAnchorKind
{
    /// <summary>The row's <c>CheckInAt</c>.</summary>
    CheckIn,

    /// <summary>The row's <c>CheckOutAt</c>.</summary>
    CheckOut,
}

/// <summary>A recorded tap time a new tap is measured against.</summary>
public readonly record struct TapAnchor(TapAnchorKind Kind, DateTime At)
{
    /// <summary>
    /// The anchors an attendance row offers: its check-in and its check-out, whichever it has. A row
    /// with neither (an organizer's <c>Absent</c>, say) offers none, and nothing is too soon against it.
    /// </summary>
    public static IReadOnlyList<TapAnchor> From(DateTime? checkInAt, DateTime? checkOutAt)
    {
        var anchors = new List<TapAnchor>(2);
        if (checkInAt is { } checkIn) anchors.Add(new TapAnchor(TapAnchorKind.CheckIn, checkIn));
        if (checkOutAt is { } checkOut) anchors.Add(new TapAnchor(TapAnchorKind.CheckOut, checkOut));
        return anchors;
    }
}

/// <summary>
/// One candidate value for <see cref="TapInterval.MinTapIntervalSecondsSettingKey"/>, from one §4.13
/// scope. <see cref="IsSchoolScope"/> is false for the global (<c>NULL SchoolId</c>) row.
/// </summary>
public readonly record struct TapIntervalSettingRow(bool IsSchoolScope, string? Value);

/// <summary>
/// Client QA #470 B6 — the minimum interval between two counted taps of <em>one card</em>, enforced on
/// the server.
///
/// <para>
/// <b>A tap is too soon when it lands strictly less than the interval from a tap the row already
/// counted</b> — its check-in or its check-out. Exactly the interval is a new tap: "at least 3 seconds
/// apart" reads as a closed bound to everyone who is not writing the comparison, the same reading
/// <see cref="TapTimeWindow.Contains"/> takes of its own boundary.
/// </para>
///
/// <para>
/// <b>The distance is absolute, not signed.</b> An offline queue delivers taps out of the order they
/// were captured, so "later than the anchor" would judge the same pair of taps differently depending on
/// which one reached the server first. Measured both ways, two taps inside the interval are one tap
/// whichever arrives second.
/// </para>
///
/// <para>
/// <b>Per card, by construction.</b> The anchors come from one student's attendance row on one event, so
/// a different card is never judged against them. There is no device term either: the same card on two
/// readers inside the interval is one tap — which is only as exact as the two devices' clocks agree.
/// </para>
/// </summary>
public static class TapInterval
{
    /// <summary>Client QA #470 B6 fixes it at three seconds.</summary>
    public const int DefaultMinTapIntervalSeconds = 3;

    /// <summary>
    /// The largest value a §4.13 row may set. Anything above it is treated as unreadable and falls
    /// through to the next scope. A minute is already twenty times the QA figure; a row saying "3600"
    /// is a typo for something, and honouring it would silently swallow every check-out made within an
    /// hour of arriving.
    /// </summary>
    public const int MaxMinTapIntervalSeconds = 60;

    /// <summary>
    /// §4.13 <c>SystemSettings.Key</c>. School row, then global row, then
    /// <see cref="DefaultMinTapIntervalSeconds"/>. <c>0</c> disables the rule. Not exposed by any route:
    /// QA fixed the value, and this exists so that an operator can change it without a release.
    /// </summary>
    public const string MinTapIntervalSecondsSettingKey = "attendance.minTapIntervalSeconds";

    /// <summary>The interval a school with no rows gets.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(DefaultMinTapIntervalSeconds);

    /// <summary>
    /// Reads a §4.13 value as a whole number of seconds in <c>[0, MaxMinTapIntervalSeconds]</c>.
    /// Invariant culture because a settings row is not localized.
    /// </summary>
    public static bool TryParse(string? value, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;
        if (parsed is < 0 or > MaxMinTapIntervalSeconds) return false;

        seconds = parsed;
        return true;
    }

    /// <summary>
    /// The configured interval: the school's row if it is readable, the global row if <em>that</em> is,
    /// the default otherwise. Every unreadable candidate is handed to <paramref name="onUnreadable"/>
    /// before falling through — the same precedence-survives-a-malformed-row rule the D-36 window read
    /// follows, so a bad school row cannot skip a deliberately narrowed global one.
    /// </summary>
    public static TimeSpan Resolve(
        IEnumerable<TapIntervalSettingRow> candidates, Action<TapIntervalSettingRow>? onUnreadable = null)
    {
        foreach (var row in candidates.OrderByDescending(r => r.IsSchoolScope))
        {
            if (TryParse(row.Value, out var seconds)) return TimeSpan.FromSeconds(seconds);
            onUnreadable?.Invoke(row);
        }

        return Default;
    }

    /// <summary>
    /// Whether <paramref name="when"/> lands strictly inside <paramref name="interval"/> of any anchor,
    /// measured as an absolute distance. <paramref name="hit"/> is the nearest such anchor (the first
    /// listed on a tie). A zero interval never matches, which is what "0 disables the rule" means.
    /// </summary>
    public static bool IsTooSoon(
        DateTime when, IReadOnlyList<TapAnchor> anchors, TimeSpan interval, out TapAnchor hit)
    {
        hit = default;
        var found = false;
        var nearest = TimeSpan.MaxValue;

        foreach (var anchor in anchors)
        {
            var distance = (when - anchor.At).Duration();
            if (distance >= interval || distance >= nearest) continue;

            nearest = distance;
            hit = anchor;
            found = true;
        }

        return found;
    }
}
