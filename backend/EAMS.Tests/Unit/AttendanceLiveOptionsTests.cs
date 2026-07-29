using EAMS.Application.Abstractions;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The D-29 poll interval's configuration read. Unit-testable because it is a pure function of a
/// string, which is exactly why it is written as one rather than inline in
/// <c>AddEamsInfrastructure</c>.
/// </summary>
public class AttendanceLiveOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("five")]
    [InlineData("5.5")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("301")]
    [InlineData("5000")] // milliseconds pasted into a seconds field — the typo the ceiling is for.
    public void Anything_unusable_falls_back_to_the_default(string? configured) =>
        Assert.Equal(
            AttendanceLiveOptions.DefaultPollAfterSeconds,
            AttendanceLiveOptions.Resolve(configured).PollAfterSeconds);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("15", 15)]
    [InlineData(" 30 ", 30)]
    [InlineData("300", 300)]
    public void A_value_in_range_is_taken(string configured, int expected) =>
        Assert.Equal(expected, AttendanceLiveOptions.Resolve(configured).PollAfterSeconds);

    /// <summary>
    /// Both bounds are inclusive, matching every other bound in this codebase (the D-36 tap window says
    /// so explicitly). Pinned separately because an off-by-one on a clamp is invisible in behaviour.
    /// </summary>
    [Fact]
    public void The_bounds_are_inclusive()
    {
        Assert.Equal(
            AttendanceLiveOptions.MinPollAfterSeconds,
            AttendanceLiveOptions.Resolve($"{AttendanceLiveOptions.MinPollAfterSeconds}").PollAfterSeconds);

        Assert.Equal(
            AttendanceLiveOptions.MaxPollAfterSeconds,
            AttendanceLiveOptions.Resolve($"{AttendanceLiveOptions.MaxPollAfterSeconds}").PollAfterSeconds);
    }

    /// <summary>
    /// The default an unconfigured host resolves to is the one the record's own <c>Default</c> names.
    /// Two ways of saying the same number is how they drift.
    /// </summary>
    [Fact]
    public void The_unconfigured_default_is_the_declared_default() =>
        Assert.Equal(AttendanceLiveOptions.Default, AttendanceLiveOptions.Resolve(null));
}
