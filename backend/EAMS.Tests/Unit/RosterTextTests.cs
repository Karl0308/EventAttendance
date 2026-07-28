using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The normalization rules every roster value passes through before it becomes a display value or a
/// key. Pure, no database — the same reasoning as <see cref="AcademicKeyTests"/>: these are the rules
/// the importer, a manual-entry screen and a fixture must all derive identically, so they are tested
/// where they live rather than through the pipeline that happens to call them first.
/// </summary>
public class RosterTextTests
{
    // Spelled numerically. A literal zero-width character in a test file is invisible in the diff, so
    // a reviewer cannot tell the test still covers what its name says.
    private const string ZeroWidthSpace = "\u200B";
    private const string ZeroWidthNonJoiner = "\u200C";
    private const string WordJoiner = "\u2060";
    private const string ByteOrderMark = "\uFEFF";
    private const string NonBreakingSpace = "\u00A0";

    // ------------------------------------------------------------------------- whitespace collapse

    /// <summary>
    /// <c>'CA  2'</c> is in the real export. The key rule already absorbs it (<c>AcademicKey</c> throws
    /// away spaces entirely), but <c>Courses.Code</c> stores the display form, and a course rendered
    /// "CA  2" beside another rendered "CA 2" is two courses to every human reading the screen.
    /// </summary>
    [Theory]
    [InlineData("CA  2", "CA 2")]
    [InlineData("  SSCI 7  ", "SSCI 7")]
    [InlineData("MARISOL  VILLAREAL", "MARISOL VILLAREAL")]
    [InlineData("A\t\tB", "A B")]
    [InlineData("A\r\nB", "A B")]
    public void Runs_of_whitespace_collapse_to_one_space(string input, string expected) =>
        Assert.Equal(expected, RosterText.Clean(input));

    // ------------------------------------------------------------------------ invisible characters

    /// <summary>
    /// Excel exports carry these. Left in, they make two values a human reads as identical compare
    /// unequal — which for <c>SECTION_NAME</c> means a student silently alone in a section of one.
    /// </summary>
    [Theory]
    [InlineData(ByteOrderMark + "Lucia", "Lucia")]
    [InlineData("Vergara" + ZeroWidthSpace, "Vergara")]
    [InlineData("Ver" + ZeroWidthNonJoiner + "gara", "Vergara")]
    [InlineData("Ver" + WordJoiner + "gara", "Vergara")]
    [InlineData("BSCRIM" + NonBreakingSpace + "2-A", "BSCRIM 2-A")]
    public void Zero_width_characters_are_removed_and_nbsp_becomes_a_space(
        string input, string expected) =>
        Assert.Equal(expected, RosterText.Clean(input));

    /// <summary>
    /// The order in <see cref="RosterText.Clean"/> is load-bearing, and this is the case that proves
    /// it: a non-breaking space adjacent to an ordinary one is a run of two <em>different</em>
    /// characters. Folding NBSP to a space only after collapsing would leave both.
    /// </summary>
    [Fact]
    public void A_non_breaking_space_next_to_an_ordinary_space_collapses_to_one()
    {
        Assert.Equal("A B", RosterText.Clean("A" + NonBreakingSpace + " B"));
        Assert.Equal("A B", RosterText.Clean("A " + NonBreakingSpace + "B"));
    }

    /// <summary>
    /// Two spellings of the same Filipino surname — one composed, one decomposed — must become one
    /// string. Composed on Windows, decomposed on macOS; without this a Mac export of the same roster
    /// produces a second copy of every student whose name carries a diacritic.
    /// </summary>
    [Fact]
    public void Decomposed_characters_are_composed_so_two_exports_agree()
    {
        const string composed = "Pe\u00F1a";          // U+00F1, precomposed
        const string decomposed = "Pen\u0303a";       // n + U+0303 combining tilde

        Assert.NotEqual(composed, decomposed);
        Assert.Equal(RosterText.Clean(composed), RosterText.Clean(decomposed));
        Assert.Equal(composed, RosterText.Clean(decomposed));
    }

    // --------------------------------------------------------------------------------- emptiness

    /// <summary>
    /// Null rather than the empty string, so a nullable column gets NULL and a blank cell is the same
    /// thing as an absent one to every caller.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(ZeroWidthSpace)]
    [InlineData(NonBreakingSpace + NonBreakingSpace)]
    public void Nothing_becomes_null_rather_than_empty(string? input) =>
        Assert.Null(RosterText.Clean(input));

    // ------------------------------------------------------------------------ name placeholders

    /// <summary>
    /// 200 of the real file's 536 rows carry a literal <c>'-'</c> as the middle name and 175 are blank;
    /// both mean "no middle name". Stored as written, <c>Student.FullName</c> renders "Maria - Santos".
    /// </summary>
    [Theory]
    [InlineData("-")]
    [InlineData(" - ")]
    [InlineData("--")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData(null)]
    public void A_punctuation_only_name_is_null(string? input) =>
        Assert.Null(RosterText.CleanName(input));

    /// <summary>
    /// The other half, and the reason the rule is "does anything survive AcademicKey" rather than a
    /// list of known placeholders: a real initial keeps its period.
    /// </summary>
    [Theory]
    [InlineData("E.", "E.")]
    [InlineData("N", "N")]
    [InlineData("DELA CRUZ", "DELA CRUZ")]
    [InlineData("  Jaculina  ", "Jaculina")]
    public void A_real_name_survives_intact(string input, string expected) =>
        Assert.Equal(expected, RosterText.CleanName(input));

    // ---------------------------------------------------------------------------------- e-mail

    [Theory]
    [InlineData("Maria.Santos@USA.edu.ph", "maria.santos@usa.edu.ph")]
    [InlineData("  ana@gmail.com ", "ana@gmail.com")]
    public void An_email_is_cleaned_and_lower_cased(string input, string expected) =>
        Assert.Equal(expected, RosterText.CleanEmail(input));

    [Fact]
    public void A_blank_email_is_null() => Assert.Null(RosterText.CleanEmail("  "));

    // ------------------------------------------------------------------------- numeric cells

    /// <summary>
    /// <b>The single most consequential formatting rule in the pipeline.</b> One of the 52 REGNOs is
    /// the ten-digit <c>2021005781</c>, which Excel stores as a number. Rendered with .NET's default
    /// double formatting it becomes <c>2.021005781E+09</c> — and REGNO is not only the student number,
    /// it is the RFID card UID. That student's card would be stored as a UID no reader can produce, so
    /// they would simply never register a tap, with no error anywhere to explain it.
    /// </summary>
    [Fact]
    public void The_legacy_ten_digit_regno_never_becomes_scientific_notation()
    {
        Assert.Equal("2021005781", RosterText.FormatNumericCell(2021005781d));
        Assert.DoesNotContain("E", RosterText.FormatNumericCell(2021005781d));
    }

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(1d, "1")]
    [InlineData(2021005781d, "2021005781")]
    [InlineData(-42d, "-42")]
    public void An_integral_number_formats_without_a_decimal_point_or_separators(
        double value, string expected) =>
        Assert.Equal(expected, RosterText.FormatNumericCell(value));

    /// <summary>
    /// A genuinely fractional value keeps its fraction rather than being silently truncated to a
    /// different number. No roster column is fractional; the rule matters because a column that
    /// becomes one must not lose data quietly.
    /// </summary>
    [Fact]
    public void A_fractional_number_keeps_its_fraction() =>
        Assert.Equal("1.5", RosterText.FormatNumericCell(1.5d));

    // -------------------------------------------------------------------------------- fingerprint

    /// <summary>
    /// Deterministic across calls, and sensitive to which column a value sits in — otherwise two rows
    /// that swapped a first and last name would hash identically.
    /// </summary>
    [Fact]
    public void A_row_fingerprint_is_stable_and_order_sensitive()
    {
        string[] row = ["USA00001", "Maria", "Santos"];

        Assert.Equal(RosterText.Fingerprint(row), RosterText.Fingerprint(row));
        Assert.NotEqual(
            RosterText.Fingerprint(row),
            RosterText.Fingerprint(["USA00001", "Santos", "Maria"]));
    }

    /// <summary>
    /// A null and an empty cell fingerprint the same — they are the same absence — but a value moving
    /// between adjacent columns does not, which is what the unit separator is for.
    /// </summary>
    [Fact]
    public void A_value_cannot_forge_a_neighbouring_rows_fingerprint()
    {
        Assert.Equal(RosterText.Fingerprint([null, "A"]), RosterText.Fingerprint(["", "A"]));
        Assert.NotEqual(RosterText.Fingerprint(["A", "B"]), RosterText.Fingerprint(["AB", ""]));
    }

    [Fact]
    public void A_fingerprint_is_a_lower_case_sha256_in_hex()
    {
        var fingerprint = RosterText.Fingerprint(["USA00001"]);

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.True(char.IsAsciiDigit(c) || (c is >= 'a' and <= 'f')));
    }
}
