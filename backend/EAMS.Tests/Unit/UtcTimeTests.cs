using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The inbound-JSON half of the UTC invariant (<see cref="UtcTime"/>); the persistence half is
/// covered by <see cref="UtcDateTimeConverterTests"/> and the end-to-end wire format by the
/// integration suite. Split because they fail for different reasons and a single "timestamps are
/// UTC" test would not say which boundary broke.
/// </summary>
public class UtcTimeTests
{
    [Fact]
    public void Utc_input_passes_through_unchanged()
    {
        var utc = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        var result = UtcTime.Normalize(utc);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(utc.Ticks, result.Ticks);
    }

    /// <summary>
    /// A <c>"+08:00"</c> payload deserializes to <see cref="DateTimeKind.Local"/> on a Manila
    /// machine. Comparing that numerically against a UTC <c>Event.StartAt</c> is how a tap lands
    /// eight hours out — so the offset must be applied, not stripped.
    /// </summary>
    [Fact]
    public void Local_input_is_converted_to_the_same_instant_in_utc()
    {
        var local = new DateTime(2026, 7, 28, 18, 0, 0, DateTimeKind.Local);
        var result = UtcTime.Normalize(local);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(local.ToUniversalTime(), result);
    }

    /// <summary>
    /// The documented contract: a bare <c>"2026-07-28T10:00:00"</c> is *labelled* UTC, not shifted.
    /// Shifting it would apply the server's offset to a value of unknown origin — wrong, and wrong
    /// by a different amount on every machine. Asserting the ticks are untouched is the point of
    /// this test; asserting only the Kind would pass for the broken behaviour too.
    /// </summary>
    [Fact]
    public void Unspecified_input_is_relabelled_utc_without_shifting_the_value()
    {
        var unspecified = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Unspecified);
        var result = UtcTime.Normalize(unspecified);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(unspecified.Ticks, result.Ticks);
    }

    [Fact]
    public void Null_stays_null()
    {
        Assert.Null(UtcTime.Normalize((DateTime?)null));
    }

    [Fact]
    public void Nullable_overload_normalizes_the_value_it_carries()
    {
        DateTime? unspecified = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Unspecified);
        var result = UtcTime.Normalize(unspecified);

        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
    }
}
