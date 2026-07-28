using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The persistence half of the UTC invariant, tested without a database. The converter is the only
/// thing that knows a <c>datetime2</c> column holds UTC — SQL Server does not — so its two
/// directions are asserted independently. The end-to-end proof (that a real round-trip through SQL
/// Server comes back <see cref="DateTimeKind.Utc"/>) is in the integration suite; this pins the
/// conversion logic so a failure there points at wiring rather than at arithmetic.
/// </summary>
public class UtcDateTimeConverterTests
{
    private static readonly ValueConverter<DateTime, DateTime> Converter = new UtcDateTimeConverter();
    private static readonly ValueConverter<DateTime?, DateTime?> NullableConverter =
        new NullableUtcDateTimeConverter();

    private static DateTime ToProvider(DateTime value) => (DateTime)Converter.ConvertToProvider(value)!;
    private static DateTime FromProvider(DateTime value) => (DateTime)Converter.ConvertFromProvider(value)!;

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Reading_always_yields_Utc(DateTimeKind storedKind)
    {
        // SQL Server hands EF a Kind=Unspecified value; the other two are here to prove the read
        // direction does not depend on what it was given.
        var fromDatabase = new DateTime(2026, 7, 28, 2, 17, 11, DateTimeKind.Unspecified);
        var stored = DateTime.SpecifyKind(fromDatabase, storedKind);

        var materialized = FromProvider(stored);

        Assert.Equal(DateTimeKind.Utc, materialized.Kind);
        Assert.Equal(stored.Ticks, materialized.Ticks);
    }

    [Fact]
    public void Writing_a_Local_value_stores_the_same_instant_in_utc()
    {
        var local = new DateTime(2026, 7, 28, 18, 0, 0, DateTimeKind.Local);

        Assert.Equal(local.ToUniversalTime().Ticks, ToProvider(local).Ticks);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Writing_a_non_Local_value_stores_it_untouched(DateTimeKind kind)
    {
        // Unspecified must NOT be shifted on the way in: its origin is unknown, so applying the
        // server's offset would be silently wrong and wrong by a different amount per machine.
        var value = new DateTime(2026, 7, 28, 10, 0, 0, kind);

        Assert.Equal(value.Ticks, ToProvider(value).Ticks);
    }

    [Fact]
    public void Nullable_converter_passes_null_through_in_both_directions()
    {
        Assert.Null(NullableConverter.ConvertToProvider(null));
        Assert.Null(NullableConverter.ConvertFromProvider(null));
    }

    [Fact]
    public void Nullable_converter_labels_a_read_value_Utc()
    {
        DateTime? fromDatabase = new DateTime(2026, 7, 28, 2, 17, 11, DateTimeKind.Unspecified);

        var materialized = (DateTime?)NullableConverter.ConvertFromProvider(fromDatabase);

        Assert.NotNull(materialized);
        Assert.Equal(DateTimeKind.Utc, materialized!.Value.Kind);
        Assert.Equal(fromDatabase.Value.Ticks, materialized.Value.Ticks);
    }
}
