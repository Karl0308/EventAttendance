using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The B6 minimum-tap-interval rule (client QA #470), as a pure function. Every interval here is taken
/// from <see cref="TapInterval"/> rather than written as a literal, so a change to the published figure
/// moves these tests with it instead of leaving them asserting the old one.
/// </summary>
public class TapIntervalTests
{
    private static readonly DateTime CheckIn = new(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Interval = TapInterval.Default;

    private static bool TooSoon(DateTime when, DateTime? checkIn, DateTime? checkOut, out TapAnchor hit) =>
        TapInterval.IsTooSoon(when, TapAnchor.From(checkIn, checkOut), Interval, out hit);

    [Fact]
    public void under_the_interval_after_check_in_is_too_soon()
    {
        Assert.True(TooSoon(CheckIn + Interval / 2, CheckIn, null, out var hit));
        Assert.Equal(new TapAnchor(TapAnchorKind.CheckIn, CheckIn), hit);
    }

    /// <summary>Strict: "at least N seconds apart" is a closed bound, so exactly N counts.</summary>
    [Fact]
    public void exactly_the_interval_is_not_too_soon() =>
        Assert.False(TooSoon(CheckIn + Interval, CheckIn, null, out _));

    [Fact]
    public void one_tick_under_is_too_soon() =>
        Assert.True(TooSoon(CheckIn + Interval - TimeSpan.FromTicks(1), CheckIn, null, out _));

    /// <summary>
    /// An offline queue delivers out of capture order, so a tap captured just <em>before</em> the counted
    /// one is the same double tap as one captured just after it.
    /// </summary>
    [Fact]
    public void judged_by_distance_not_arrival_order()
    {
        Assert.True(TooSoon(CheckIn - Interval / 2, CheckIn, null, out var before));
        Assert.True(TooSoon(CheckIn + Interval / 2, CheckIn, null, out var after));
        Assert.Equal(before, after);

        Assert.False(TooSoon(CheckIn - Interval, CheckIn, null, out _));
    }

    [Fact]
    public void the_check_out_is_also_an_anchor()
    {
        var checkOut = CheckIn.AddHours(2);

        Assert.True(TooSoon(checkOut + Interval / 2, CheckIn, checkOut, out var hit));
        Assert.Equal(new TapAnchor(TapAnchorKind.CheckOut, checkOut), hit);
    }

    [Fact]
    public void zero_disables_the_rule()
    {
        Assert.False(TapInterval.IsTooSoon(
            CheckIn, TapAnchor.From(CheckIn, null), TimeSpan.Zero, out _));

        Assert.True(TapInterval.TryParse("0", out var seconds));
        Assert.Equal(0, seconds);
    }

    [Fact]
    public void above_the_cap_falls_through()
    {
        var aboveTheCap = (TapInterval.MaxMinTapIntervalSeconds + 1).ToString();
        Assert.False(TapInterval.TryParse(aboveTheCap, out _));
        Assert.True(TapInterval.TryParse(TapInterval.MaxMinTapIntervalSeconds.ToString(), out _));

        var unreadable = new List<TapIntervalSettingRow>();
        var resolved = TapInterval.Resolve(
            [new TapIntervalSettingRow(IsSchoolScope: true, aboveTheCap)], unreadable.Add);

        Assert.Equal(TapInterval.Default, resolved);
        Assert.Single(unreadable);
    }

    /// <summary>
    /// Precedence has to survive a malformed row: a bad school value must not skip a readable global one
    /// on its way to the default.
    /// </summary>
    [Theory]
    [InlineData("three")]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("")]
    public void a_malformed_school_value_falls_through_to_the_global_row(string malformed)
    {
        var unreadable = new List<TapIntervalSettingRow>();

        var resolved = TapInterval.Resolve(
        [
            new TapIntervalSettingRow(IsSchoolScope: false, "7"),
            new TapIntervalSettingRow(IsSchoolScope: true, malformed),
        ], unreadable.Add);

        Assert.Equal(TimeSpan.FromSeconds(7), resolved);
        Assert.Equal(malformed, Assert.Single(unreadable).Value);
    }

    [Fact]
    public void a_school_can_narrow_its_interval()
    {
        var resolved = TapInterval.Resolve(
        [
            new TapIntervalSettingRow(IsSchoolScope: false, TapInterval.DefaultMinTapIntervalSeconds.ToString()),
            new TapIntervalSettingRow(IsSchoolScope: true, "1"),
        ]);

        Assert.Equal(TimeSpan.FromSeconds(1), resolved);
        Assert.False(TapInterval.IsTooSoon(
            CheckIn + TimeSpan.FromSeconds(1), TapAnchor.From(CheckIn, null), resolved, out _));
    }

    [Fact]
    public void no_rows_is_the_default() =>
        Assert.Equal(TimeSpan.FromSeconds(TapInterval.DefaultMinTapIntervalSeconds), TapInterval.Resolve([]));

    /// <summary>A row with no timestamps (an organizer's Absent) offers nothing to be too soon against.</summary>
    [Fact]
    public void a_row_with_no_taps_has_no_anchors() =>
        Assert.False(TooSoon(CheckIn, null, null, out _));
}
