using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <see cref="AcademicKey"/> is the single rule that decides whether two spreadsheet cells are the
/// same course, the same section, or the same teacher. Every case below is taken from the real CICSS
/// export rather than invented, because the failure mode is not "the function is wrong" — it is "the
/// function is right about a kind of dirt the file does not contain".
/// </summary>
public class AcademicKeyTests
{
    /// <summary>
    /// The pair that motivates the whole design: <c>'SSCI 7'</c> and <c>'SSci7'</c> both appear in the
    /// source and are one course. If these produced two keys, the roster would carry two course rows,
    /// two sets of offerings, and two disjoint halves of one class's enrollment — and nothing would
    /// look broken.
    /// </summary>
    [Theory]
    [InlineData("SSCI 7", "SSci7")]        // casing and a space
    [InlineData("CA  2", "CA 2")]          // the double space in the source
    [InlineData("CA  2", "ca2")]           // both at once
    [InlineData("BSFS 2-A", "bsfs2a")]     // section punctuation
    [InlineData("GE Elect 2", "GE ELECT 2")]
    [InlineData(" Trailing ", "Trailing")]
    public void Two_spellings_of_the_same_value_produce_one_key(string first, string second) =>
        Assert.Equal(AcademicKey.Normalize(first), AcademicKey.Normalize(second));

    [Theory]
    [InlineData("SSCI 7", "SSCI7")]
    [InlineData("CA  2", "CA2")]
    [InlineData("BSFS 2-A", "BSFS2A")]
    [InlineData("College of Criminal Justice", "COLLEGEOFCRIMINALJUSTICE")]
    [InlineData("TO BE ANNOUNCE", "TOBEANNOUNCE")]
    public void The_key_is_letters_and_digits_upper_cased(string source, string expected) =>
        Assert.Equal(expected, AcademicKey.Normalize(source));

    /// <summary>
    /// Genuinely different codes must stay different. The stripping is aggressive, so this is the
    /// assertion that keeps it from being <em>too</em> aggressive.
    /// </summary>
    [Theory]
    [InlineData("GE Elect 2", "GE Elect 3")]
    [InlineData("CA 2", "CA 21")]
    [InlineData("BSFS 2-A", "BSFS 2-B")]
    public void Different_values_keep_different_keys(string first, string second) =>
        Assert.NotEqual(AcademicKey.Normalize(first), AcademicKey.Normalize(second));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- ")]
    public void A_value_with_nothing_in_it_normalizes_to_nothing(string? blank) =>
        Assert.Equal("", AcademicKey.Normalize(blank));

    /// <summary>
    /// 39 rows of the sample have a blank <c>SECTION_NAME</c>, and <c>CourseOfferings.SectionKey</c> is
    /// part of a unique index. A SQL Server unique index permits exactly one NULL row, so the blank
    /// has to become a value.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_key_component_becomes_the_sentinel(string? blank) =>
        Assert.Equal(AcademicKey.Unspecified, AcademicKey.NormalizeOrUnspecified(blank));

    /// <summary>
    /// The sentinel is unforgeable, which is what makes it safe to store in the same column as real
    /// keys: <see cref="AcademicKey.Normalize"/> discards every non-alphanumeric character, and the
    /// sentinel is nothing but non-alphanumerics and letters arranged so it can never be produced. A
    /// section actually named "unspecified" therefore gets its own distinct key rather than silently
    /// joining the blank ones.
    /// </summary>
    [Theory]
    [InlineData("unspecified")]
    [InlineData("(unspecified)")]
    [InlineData("UNSPECIFIED")]
    public void No_source_value_can_normalize_onto_the_sentinel(string source) =>
        Assert.NotEqual(AcademicKey.Unspecified, AcademicKey.NormalizeOrUnspecified(source));

    /// <summary>
    /// Normalization is a projection: normalizing an already-normalized key must not change it, or a
    /// re-import would produce a different key from the first import and duplicate every row.
    /// </summary>
    [Theory]
    [InlineData("SSCI 7")]
    [InlineData("CA  2")]
    [InlineData("College of Criminal Justice")]
    public void Normalizing_a_key_twice_is_the_same_as_normalizing_it_once(string source)
    {
        var once = AcademicKey.Normalize(source);
        Assert.Equal(once, AcademicKey.Normalize(once));
    }

    /// <summary>
    /// Nothing is truncated. Merging two different courses because their codes share a long prefix
    /// would be undetectable; a write that SQL Server rejects is not.
    /// </summary>
    [Fact]
    public void An_over_length_value_is_returned_whole_and_reported_as_too_long()
    {
        var source = new string('A', AcademicKey.MaxLength + 10);

        var key = AcademicKey.Normalize(source);

        Assert.Equal(AcademicKey.MaxLength + 10, key.Length);
        Assert.False(AcademicKey.IsWithinLength(key));
        Assert.True(AcademicKey.IsWithinLength(new string('A', AcademicKey.MaxLength)));
    }

    /// <summary>
    /// The composed group name is the one place a length <em>is</em> enforced by trimming, and the cut
    /// has to be visible — see <see cref="GroupName"/> for why that is safe there and not in a key.
    /// </summary>
    [Fact]
    public void A_composed_group_name_is_clamped_visibly_rather_than_silently()
    {
        var clamped = GroupName.Clamp(new string('x', GroupName.MaxLength + 50));

        Assert.Equal(GroupName.MaxLength, clamped.Length);
        Assert.EndsWith("…", clamped);
        Assert.Equal("short (2025-2026-1)", GroupName.Clamp("short (2025-2026-1)"));
    }
}
