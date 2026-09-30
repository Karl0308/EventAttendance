using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The per-event grace-period eligibility rule (EventGracePeriod.docx), verified without a database. The
/// HTTP/service behaviour it underpins is <c>EventGracePeriodTests</c>' subject.
/// </summary>
public class EventGraceTests
{
    private static readonly DateTime Start = new(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddHours(2);

    [Fact]
    public void Null_bounds_are_unbounded_on_both_sides()
    {
        Assert.Equal(GraceEligibility.Ok, EventGrace.Evaluate(Start.AddHours(-10), Start, End, null, null));
        Assert.Equal(GraceEligibility.Ok, EventGrace.Evaluate(End.AddHours(10), Start, End, null, null));
    }

    [Fact]
    public void A_tap_before_the_before_start_grace_is_too_early()
    {
        Assert.Equal(
            GraceEligibility.TooEarly,
            EventGrace.Evaluate(Start.AddMinutes(-11), Start, End, graceBeforeStartMinutes: 10, null));
    }

    [Fact]
    public void A_tap_within_the_before_start_grace_is_ok()
    {
        Assert.Equal(
            GraceEligibility.Ok,
            EventGrace.Evaluate(Start.AddMinutes(-10), Start, End, graceBeforeStartMinutes: 10, null));
        Assert.Equal(
            GraceEligibility.Ok,
            EventGrace.Evaluate(Start.AddMinutes(-5), Start, End, graceBeforeStartMinutes: 10, null));
    }

    [Fact]
    public void A_tap_after_the_after_end_grace_is_closed()
    {
        Assert.Equal(
            GraceEligibility.Closed,
            EventGrace.Evaluate(End.AddMinutes(11), Start, End, null, graceAfterEndMinutes: 10));
    }

    [Fact]
    public void A_tap_within_the_after_end_grace_is_ok()
    {
        Assert.Equal(
            GraceEligibility.Ok,
            EventGrace.Evaluate(End.AddMinutes(10), Start, End, null, graceAfterEndMinutes: 10));
    }

    [Fact]
    public void Zero_grace_means_the_boundary_is_the_only_edge()
    {
        // beforeStart = 0: a tap even one minute before start is too early.
        Assert.Equal(GraceEligibility.TooEarly, EventGrace.Evaluate(Start.AddMinutes(-1), Start, End, 0, 0));
        Assert.Equal(GraceEligibility.Ok, EventGrace.Evaluate(Start, Start, End, 0, 0));
        Assert.Equal(GraceEligibility.Ok, EventGrace.Evaluate(End, Start, End, 0, 0));
        Assert.Equal(GraceEligibility.Closed, EventGrace.Evaluate(End.AddMinutes(1), Start, End, 0, 0));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(1440, true)]
    [InlineData(1441, false)]
    [InlineData(-1, false)]
    public void Optional_grace_minutes_validation(int? value, bool valid) =>
        Assert.Equal(valid, EventText.IsValidOptionalGraceMinutes(value));
}
