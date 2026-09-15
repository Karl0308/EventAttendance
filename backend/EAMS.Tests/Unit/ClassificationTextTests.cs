using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The <c>Classifications</c> name rules and the key they derive.
///
/// <para>
/// <b>Pure logic, no database</b> — the <c>Unit/</c> half of the split. What the database decides
/// (uniqueness, the check constraints, the delete refusal) is asserted against real SQL Server in
/// <c>Integration/</c>, because a filtered index and a foreign key are precisely what vanishes under
/// an in-memory provider.
/// </para>
/// </summary>
public class ClassificationTextTests
{
    /// <summary>
    /// <b><c>SUPERVISORY/MANAGERIAL</c> is a valid name, slash and all.</b>
    ///
    /// <para>
    /// It is the value in the client's data most likely to break a naive slug, path segment or route
    /// assumption, and it is asserted first for that reason: a validator that rejected punctuation, or
    /// a key derivation that choked on it, would have made the seed fail on its fifth row and the
    /// symptom would have been "the seed does not run" rather than "the slash is the problem".
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("STUDENT")]
    [InlineData("NAP")]
    [InlineData("ACAD")]
    [InlineData("ANT")]
    [InlineData("SUPERVISORY/MANAGERIAL")]
    [InlineData("USA FRIARS")]
    [InlineData("C2B2")]
    [InlineData("CFI")]
    public void Every_seeded_value_is_a_valid_name(string name) =>
        Assert.True(
            ClassificationText.IsValidName(name),
            $"'{name}' is one of the eight values the client's export actually contains, and the " +
            "validator refused it. The seed writes these verbatim, so a rule that rejects one of them " +
            "is a rule that breaks startup on a database nobody has looked at yet.");

    /// <summary>
    /// The list the seed writes is exactly the eight values, in the agreed order, spelled exactly as
    /// the export spells them.
    ///
    /// <para>
    /// <b>Literals here rather than a loop over the same constant the seed reads.</b> An assertion
    /// derived from the code that produced the value agrees with any drift — including a "tidy-up" that
    /// removed the slash or title-cased <c>USA FRIARS</c>. JJ ruled all eight are seeded (QA named
    /// only two), so the count is asserted too: seven would be a silent regression in a list nobody
    /// re-reads.
    /// </para>
    /// </summary>
    [Fact]
    public void The_seed_list_is_the_eight_values_from_the_client_export()
    {
        Assert.Equal(
            ["STUDENT", "NAP", "ACAD", "ANT", "SUPERVISORY/MANAGERIAL", "USA FRIARS", "C2B2", "CFI"],
            ClassificationSeedValues.Names);
    }

    /// <summary>
    /// <b>The slash survives into the display name and disappears from the key.</b> That split is the
    /// whole design: the operator sees what they typed, and the uniqueness index compares something
    /// that cannot be spelled two ways.
    /// </summary>
    [Theory]
    [InlineData("SUPERVISORY/MANAGERIAL", "SUPERVISORYMANAGERIAL")]
    [InlineData("Supervisory / Managerial", "SUPERVISORYMANAGERIAL")]
    [InlineData("supervisory-managerial", "SUPERVISORYMANAGERIAL")]
    [InlineData("USA FRIARS", "USAFRIARS")]
    [InlineData("USA-Friars", "USAFRIARS")]
    [InlineData("C2B2", "C2B2")]
    public void The_key_folds_punctuation_spacing_and_case(string name, string expected) =>
        Assert.Equal(expected, ClassificationText.KeyFor(name));

    /// <summary>
    /// The three spellings of one category collapse to one key — stated as its own assertion because
    /// it is the property an administrator relies on, and the theory above states it only by
    /// coincidence of its expected values.
    /// </summary>
    [Fact]
    public void Three_spellings_of_one_category_are_one_key() =>
        Assert.Single(
            new[] { "SUPERVISORY/MANAGERIAL", "Supervisory / Managerial", "supervisory-managerial" }
                .Select(ClassificationText.KeyFor)
                .Distinct(StringComparer.Ordinal));

    /// <summary>
    /// Blank, whitespace-padded, over-length, and punctuation-only names are refused.
    ///
    /// <para>
    /// <b>The padded cases are refusals rather than silent trims</b>, for the reason <c>TermText</c>
    /// records: trimming accepts two requests that differ and stores one value, so an administrator
    /// who typed a trailing space is told nothing and then wonders why their "second" classification
    /// was reported as a duplicate.
    /// </para>
    ///
    /// <para>
    /// <b>The punctuation-only case is the one that is specific to this type.</b>
    /// <c>AcademicKey.Normalize</c> discards everything that is not a letter or a digit, so
    /// <c>'///'</c> has no key at all — and because the seeded vocabulary genuinely contains a slash,
    /// a validator that merely checked "not blank" would have accepted it and written a row whose
    /// <c>NameKey</c> was the empty string. One such row is possible; the second collides with it on
    /// an index whose message would name neither.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" NAP")]
    [InlineData("NAP ")]
    [InlineData("///")]
    [InlineData("  -  ")]
    public void Names_that_break_a_column_rule_are_refused(string? name) =>
        Assert.False(ClassificationText.IsValidName(name));

    [Fact]
    public void A_name_longer_than_the_column_is_refused()
    {
        Assert.True(ClassificationText.IsValidName(new string('A', ClassificationText.NameMaxLength)));
        Assert.False(
            ClassificationText.IsValidName(new string('A', ClassificationText.NameMaxLength + 1)),
            "A name one character over the column length was accepted. Nothing truncates it on the " +
            "way down, so SQL Server would refuse the write with an error naming neither the field " +
            "nor the limit.");
    }

    /// <summary>
    /// The key is deliberately <em>not</em> <see cref="AcademicKey.NormalizeOrUnspecified"/>.
    ///
    /// <para>
    /// That method exists so a blank <c>SECTION_NAME</c> — 39 rows of the real roster — groups under an
    /// honest placeholder instead of colliding on the empty string. A vocabulary entry is the opposite
    /// case: there is no source row forcing one to exist, so a name with no letters is a typo to refuse
    /// at the boundary rather than a blank to file under a sentinel. Asserted because the two methods
    /// sit next to each other and the wrong one is a one-word edit.
    /// </para>
    /// </summary>
    [Fact]
    public void A_punctuation_only_name_yields_no_key_rather_than_the_unspecified_sentinel()
    {
        Assert.Equal("", ClassificationText.KeyFor("///"));
        Assert.NotEqual(AcademicKey.Unspecified, ClassificationText.KeyFor("///"));
    }

    // ------------------------------------------------------------------------------------- axes

    /// <summary>
    /// <b>Each seeded value sits on the axis of the source column it appears in.</b>
    ///
    /// <para>
    /// Asserted as explicit literals, pair by pair, because this table replaced a <em>wrong</em> one
    /// and the wrong one was arrived at confidently. The first pass reasoned from the strings and
    /// their populations and put <c>ANT</c> and <c>SUPERVISORY/MANAGERIAL</c> under <c>Special</c> —
    /// the latter because a population of <b>one</b> does not look like a personnel rank. Both are
    /// <c>PERSONNEL</c> values. The tally from <c>Personnel.xlsx</c> (sheet <c>Report</c>, 21,497
    /// rows): <c>STUDENTTEMP</c> → STUDENT; <c>PERSONNEL</c> → NAP, ACAD, ANT,
    /// SUPERVISORY/MANAGERIAL; <c>FRIARS</c> → USA FRIARS; <c>SPECIAL</c> → C2B2, CFI.
    /// </para>
    ///
    /// <para>
    /// A derived assertion — looping over the same list the seed reads — would agree with any future
    /// drift, including a well-meaning "tidy-up" that moved the two odd-looking values back where they
    /// look like they belong.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("STUDENT", ClassificationAxis.Student)]
    [InlineData("NAP", ClassificationAxis.Personnel)]
    [InlineData("ACAD", ClassificationAxis.Personnel)]
    [InlineData("ANT", ClassificationAxis.Personnel)]
    [InlineData("SUPERVISORY/MANAGERIAL", ClassificationAxis.Personnel)]
    [InlineData("USA FRIARS", ClassificationAxis.Friars)]
    [InlineData("C2B2", ClassificationAxis.Special)]
    [InlineData("CFI", ClassificationAxis.Special)]
    public void Each_seeded_value_sits_on_the_axis_of_its_source_column(string name, string axis)
    {
        var seeded = ClassificationSeedValues.All.Single(s => s.Name == name);

        Assert.True(
            seeded.Axis == axis,
            $"'{name}' is seeded on the {seeded.Axis} axis; the export puts it in the {axis} column. " +
            "The axis of a value IS the column it appears in — it is not inferred from what the value " +
            "looks like or from how many people carry it. ANT and SUPERVISORY/MANAGERIAL are PERSONNEL " +
            "values; SPECIAL contains only C2B2 and CFI.");
    }

    /// <summary>
    /// Only <c>C2B2</c> and <c>CFI</c> are on the <c>Special</c> axis — stated as a closed set, because
    /// the mistake this replaced was over-populating exactly this axis.
    /// </summary>
    [Fact]
    public void Only_two_seeded_values_are_on_the_special_axis() =>
        Assert.Equal(
            ["C2B2", "CFI"],
            ClassificationSeedValues.All
                .Where(s => s.Axis == ClassificationAxis.Special)
                .Select(s => s.Name));

    /// <summary>
    /// Every seeded axis is one the <c>CHECK</c> constraint admits. The constraint spells its four
    /// values as SQL literals — it cannot reference these constants — so this is what keeps the two
    /// lists from drifting apart into a seed that fails on startup.
    /// </summary>
    [Fact]
    public void Every_seeded_axis_is_a_documented_axis() =>
        Assert.All(
            ClassificationSeedValues.All,
            s => Assert.Contains(s.Axis, ClassificationAxis.All));

    /// <summary>
    /// The axis set is exactly the source's four category columns — no more, and in particular no
    /// fifth added speculatively. A new axis means a new source column, and a migration.
    /// </summary>
    [Fact]
    public void There_are_exactly_four_axes() =>
        Assert.Equal(["Student", "Personnel", "Friars", "Special"], ClassificationAxis.All);

    /// <summary>
    /// Axis parsing is case-insensitive in and canonical out, so a client sending <c>"personnel"</c>
    /// stores a row spelled <c>Personnel</c> — which is what the <c>CHECK</c> constraint and every C#
    /// comparison expect. Same contract as <c>AttendanceStatus.TryNormalize</c>.
    /// </summary>
    [Theory]
    [InlineData("personnel", ClassificationAxis.Personnel)]
    [InlineData("PERSONNEL", ClassificationAxis.Personnel)]
    [InlineData("Student", ClassificationAxis.Student)]
    [InlineData("friars", ClassificationAxis.Friars)]
    public void An_axis_is_matched_case_insensitively_and_stored_canonically(
        string input, string expected)
    {
        Assert.True(ClassificationAxis.TryNormalize(input, out var canonical));
        Assert.Equal(expected, canonical);
    }

    /// <summary>
    /// <b>Anything outside the four is refused, and there is deliberately no default.</b>
    ///
    /// <para>
    /// <c>Student</c> would be the obvious default — 20,861 of 21,497 sampled rows carry it — and it
    /// would be wrong silently: a personnel category created without an axis would land on the student
    /// axis, compete for the one slot a person has there, and say nothing until somebody noticed a
    /// member of staff had stopped being a student.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Staff")]
    [InlineData("STUDENTTEMP")]
    public void An_undocumented_axis_is_refused(string? input) =>
        Assert.False(ClassificationAxis.TryNormalize(input, out _));
}
