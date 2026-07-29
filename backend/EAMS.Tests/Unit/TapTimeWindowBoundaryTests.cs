using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The exact boundaries of <see cref="TapTimeWindow"/> (Phase 4c, D-36).
///
/// <para>
/// <b>Why these are here and not in the integration file that covers everything else.</b> Both bounds
/// are published — "more than 5 minutes in the future", "60 minutes either side" — so the difference
/// between <c>&gt;</c> and <c>&gt;=</c> is a contract change, and it is the difference this file
/// exists to pin. It cannot be pinned through the service: the comparison is against
/// <c>DateTime.UtcNow</c> read inside <c>TapAsync</c>, so an integration test can put a timestamp
/// <em>near</em> the boundary but never <em>on</em> it — by the time the service reads the clock, the
/// margin has already moved by however long the arrange step took. Supplying both operands is the only
/// way to test the boundary itself, and this is the seam where that is possible until a
/// <c>TimeProvider</c> exists.
/// </para>
///
/// <para>
/// <c>TapTimeWindowTests</c> in the integration suite covers everything downstream of these: the
/// outcomes, the §4.13 configuration, the precedence, the ordering.
/// </para>
/// </summary>
public class TapTimeWindowBoundaryTests
{
    private static readonly DateTime ServerTime = new(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- the future tolerance

    /// <summary>
    /// Exactly five minutes ahead is <b>accepted</b>. The contract says "<em>more than</em> 5 minutes
    /// in the future is rejected", so the boundary belongs to the accepted side — and flipping the
    /// comparison to <c>&gt;=</c> would refuse a tap the published text promises to take, silently, on
    /// a client whose clock is within its stated tolerance.
    /// </summary>
    [Fact]
    public void A_tap_exactly_at_the_future_tolerance_is_not_implausible() =>
        Assert.False(TapTimeWindow.IsImplausiblyFuture(
            ServerTime.AddMinutes(TapTimeWindow.FutureToleranceMinutes), ServerTime));

    /// <summary>
    /// One tick past it is rejected. Asserted as a pair with the test above, because either one alone
    /// passes on a build where the check does nothing at all.
    /// </summary>
    [Fact]
    public void A_tap_one_tick_past_the_future_tolerance_is_implausible() =>
        Assert.True(TapTimeWindow.IsImplausiblyFuture(
            ServerTime.AddMinutes(TapTimeWindow.FutureToleranceMinutes).AddTicks(1), ServerTime));

    [Fact]
    public void A_tap_at_the_server_clock_is_not_implausible() =>
        Assert.False(TapTimeWindow.IsImplausiblyFuture(ServerTime, ServerTime));

    /// <summary>
    /// The past has no bound at all, at any age — an old queued tap is the point of §8.2's offline
    /// sync. Stated here as well as in the integration suite because this is where someone adding a
    /// symmetrical "too old" branch would be working.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-60 * 24)]
    [InlineData(-60 * 24 * 365 * 5)]
    public void No_amount_of_lateness_is_implausible(int minutes) =>
        Assert.False(TapTimeWindow.IsImplausiblyFuture(ServerTime.AddMinutes(minutes), ServerTime));

    // ---------------------------------------------------------------- the event window

    private static readonly DateTime StartAt = new(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndAt = StartAt.AddHours(3);

    /// <summary>
    /// Both ends are closed intervals. "60 minutes either side" reads as inclusive to everyone who is
    /// not writing the comparison, and an off-by-one at either end refuses a tap the contract accepts.
    /// </summary>
    [Fact]
    public void The_window_includes_both_of_its_boundaries()
    {
        var window = TapTimeWindow.Default;

        Assert.True(window.Contains(
            StartAt.AddMinutes(-TapTimeWindow.DefaultBeforeStartMinutes), StartAt, EndAt));
        Assert.True(window.Contains(
            EndAt.AddMinutes(TapTimeWindow.DefaultAfterEndMinutes), StartAt, EndAt));
    }

    [Fact]
    public void The_window_excludes_one_tick_outside_either_boundary()
    {
        var window = TapTimeWindow.Default;

        Assert.False(window.Contains(
            StartAt.AddMinutes(-TapTimeWindow.DefaultBeforeStartMinutes).AddTicks(-1), StartAt, EndAt));
        Assert.False(window.Contains(
            EndAt.AddMinutes(TapTimeWindow.DefaultAfterEndMinutes).AddTicks(1), StartAt, EndAt));
    }

    /// <summary>
    /// A zero-minute window still admits the event itself. A school tightening capture to exactly the
    /// scheduled hours must not thereby refuse a tap at the moment the doors open.
    /// </summary>
    [Fact]
    public void A_zero_window_still_admits_the_events_own_span()
    {
        var window = new TapTimeWindow(0, 0);

        Assert.True(window.Contains(StartAt, StartAt, EndAt));
        Assert.True(window.Contains(EndAt, StartAt, EndAt));
        Assert.False(window.Contains(StartAt.AddTicks(-1), StartAt, EndAt));
    }

    [Fact]
    public void The_reported_bounds_are_the_ones_the_check_uses()
    {
        var window = new TapTimeWindow(30, 90);
        var (from, to) = window.BoundsFor(StartAt, EndAt);

        Assert.Equal(StartAt.AddMinutes(-30), from);
        Assert.Equal(EndAt.AddMinutes(90), to);
        Assert.True(window.Contains(from, StartAt, EndAt));
        Assert.True(window.Contains(to, StartAt, EndAt));
    }

    // ---------------------------------------------------------------- settings parsing

    [Theory]
    [InlineData("0", 0)]
    [InlineData("60", 60)]
    [InlineData("  90  ", 90)]
    public void A_settings_value_that_is_a_non_negative_whole_number_parses(string value, int expected)
    {
        Assert.True(TapTimeWindow.TryParseMinutes(value, out var minutes));
        Assert.Equal(expected, minutes);
    }

    /// <summary>
    /// <c>SystemSettings.Value</c> is <c>nvarchar(max)</c> with a free-text <c>DataType</c> beside it,
    /// so anything at all can be in there. A negative is refused rather than clamped: a window that
    /// runs backwards would refuse every tap on the event it was meant to widen.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ninety")]
    [InlineData("90.5")]
    [InlineData("-30")]
    [InlineData("1e3")]
    public void Anything_else_is_refused(string? value) =>
        Assert.False(TapTimeWindow.TryParseMinutes(value, out _));
}
