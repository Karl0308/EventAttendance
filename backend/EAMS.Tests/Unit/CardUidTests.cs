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
    // ADR-001 "Accepted Context": REGNO *is* the card UID, so REGNO-shaped input is not an edge
    // case here — it is the production shape. A normalizer that only handled hex would break the
    // real roster on day one.
    [InlineData("usa00962", "USA00962")]
    [InlineData("USA00962", "USA00962")]
    [InlineData("usa-00962", "USA00962")]
    [InlineData(" usa 00962 ", "USA00962")]
    public void Normalize_handles_REGNO_shaped_input(string input, string expected) =>
        Assert.Equal(expected, CardUid.Normalize(input));

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
