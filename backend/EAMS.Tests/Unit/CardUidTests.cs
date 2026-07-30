using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <see cref="CardUid.Normalize"/> is the single point where every UID-shaped string in the system
/// is made comparable — the tap path, the mobile scan lookup, and the seed all route through it.
/// It has no dependencies, so these are pure unit tests; the matching database-level assertion
/// (that a tap normalizes before it queries) lives in the integration suite.
/// </summary>
public class CardUidTests
{
    [Theory]
    // The reader formats CLAUDE.md names: colon, dash, space, and already-canonical.
    [InlineData("04:a7:b8:c9", "04A7B8C9")]
    [InlineData("04-A7-B8-C9", "04A7B8C9")]
    [InlineData("04 a7 b8 c9", "04A7B8C9")]
    [InlineData("04a7b8c9", "04A7B8C9")]
    [InlineData("04A7B8C9", "04A7B8C9")]
    public void Normalize_strips_separators_and_uppercases(string input, string expected) =>
        Assert.Equal(expected, CardUid.Normalize(input));

    [Theory]
    // The CICSS export's shape: decimal digits, no separators, ten wide. A normalizer that only
    // handled hex would break the real roster on day one, so this is the production case rather than
    // an edge one. Until the client corrected us on 2026-07-30 this block used REGNO-shaped input on
    // the premise that REGNO *was* the card UID; the serial is its own column and this is the shape it
    // actually takes.
    [InlineData("0012503326", "0012503326")]
    [InlineData("0012503326 ", "0012503326")]
    [InlineData("00 125 033 26", "0012503326")]
    [InlineData("0012-5033-26", "0012503326")]
    public void Normalize_handles_decimal_serial_input(string input, string expected) =>
        Assert.Equal(expected, CardUid.Normalize(input));

    /// <summary>
    /// <b>Leading zeros are significant and <see cref="CardUid.Normalize"/> must not touch them.</b>
    /// <c>0012503326</c> and <c>12503326</c> are two different cards, and a normalizer that trimmed or
    /// numerically round-tripped the value would silently merge them — or produce a UID no reader can
    /// ever match, which is a student who simply never registers a tap with nothing to say why. Stated
    /// as a non-equality because that is the assertion that fails if anything starts parsing this
    /// string as a number.
    /// </summary>
    [Fact]
    public void Normalize_preserves_significant_leading_zeros()
    {
        Assert.Equal("0012503326", CardUid.Normalize("0012503326"));
        Assert.NotEqual(CardUid.Normalize("12503326"), CardUid.Normalize("0012503326"));
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var once = CardUid.Normalize("04:a7:b8:c9");
        Assert.Equal(once, CardUid.Normalize(once));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::--  ")]
    public void Normalize_reduces_input_with_no_alphanumerics_to_empty(string input) =>
        Assert.Equal("", CardUid.Normalize(input));

    /// <summary>
    /// Pins the Turkish-I hazard. <c>ToUpperInvariant</c> maps <c>i</c> to <c>I</c> on every
    /// culture; <c>ToUpper()</c> under tr-TR maps it to <c>İ</c>, which would make a UID stored on
    /// one machine unmatchable on another. The assertion is on the invariant result, so a change to
    /// culture-sensitive casing fails here rather than in the field.
    /// </summary>
    [Fact]
    public void Normalize_is_culture_invariant()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal("USA00962I", CardUid.Normalize("usa00962i"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
