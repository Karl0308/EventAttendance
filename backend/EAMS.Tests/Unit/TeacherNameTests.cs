using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The roster's five teacher columns, and the three rules that stop them producing the wrong number of
/// instructors.
///
/// <para>
/// The source has <b>no teacher id</b> — nineteen names matched on the name and nothing else — so every
/// stray token that reaches <c>Instructors.NameKey</c> is a second copy of a real person, and every
/// name that reaches it when it should not is a person who does not exist.
/// </para>
/// </summary>
public class TeacherNameTests
{
    // ------------------------------------------------------------------- 'TO BE ANNOUNCE' sentinel

    /// <summary>
    /// 66 of the real file's 536 rows name this "teacher", and it also fills the other four teacher
    /// columns. Turned into an instructor row it would be the largest teacher in the institution by a
    /// factor of three, would top every per-instructor report, and would sit in the teacher picker
    /// above every real person.
    /// </summary>
    [Theory]
    [InlineData("TO BE ANNOUNCE")]
    [InlineData("to be announce")]
    [InlineData("TO  BE  ANNOUNCE")]
    [InlineData("TOBEANNOUNCE")]
    public void The_placeholder_is_recognised_however_it_is_spelled(string value)
    {
        Assert.True(TeacherNames.IsPlaceholder(value));

        var parsed = TeacherNames.Parse(value, value, value, value);

        Assert.True(parsed.IsPlaceholder);
        Assert.Null(parsed.DisplayName);
        Assert.Null(parsed.Honorific);
    }

    [Theory]
    [InlineData("THERESA GALANG NAVARRO")]
    [InlineData("TO BE ANNOUNCED LATER")]
    [InlineData("")]
    [InlineData(null)]
    public void A_real_name_is_not_the_placeholder(string? value) =>
        Assert.False(TeacherNames.IsPlaceholder(value));

    // --------------------------------------------------------------- TEACHER SUFFIX is an honorific

    /// <summary>
    /// <b>The column is called SUFFIX and it holds an honorific.</b> Its four values in the real file
    /// are <c>Mr.</c>, <c>Mrs.</c>, <c>Ms.</c> and the placeholder. Appended to the name — which is what
    /// "suffix" invites — <c>'THERESA GALANG NAVARRO'</c> would key as <c>THERESAGALANGNAVARROMS</c>, and the
    /// same teacher recorded once without a title would become a second instructor.
    /// </summary>
    [Theory]
    [InlineData("Ms.")]
    [InlineData("Mr.")]
    [InlineData("Mrs.")]
    public void The_suffix_column_becomes_an_honorific_and_never_touches_the_name(string suffix)
    {
        var parsed = TeacherNames.Parse("THERESA GALANG NAVARRO", "THERESA", "NAVARRO", suffix);

        Assert.Equal("THERESA GALANG NAVARRO", parsed.DisplayName);
        Assert.Equal(suffix, parsed.Honorific);
        Assert.DoesNotContain(suffix, parsed.DisplayName!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The key is what the honorific must not reach, and this states it directly: the same teacher with
    /// and without a title is one instructor.
    /// </summary>
    [Fact]
    public void The_same_teacher_with_and_without_an_honorific_shares_one_key()
    {
        var titled = TeacherNames.Parse("THERESA GALANG NAVARRO", "THERESA", "NAVARRO", "Ms.");
        var untitled = TeacherNames.Parse("THERESA GALANG NAVARRO", "THERESA", "NAVARRO", "");

        Assert.Equal(
            AcademicKey.NormalizeOrUnspecified(titled.DisplayName),
            AcademicKey.NormalizeOrUnspecified(untitled.DisplayName));
    }

    /// <summary>
    /// An unrecognised value in the suffix column is discarded rather than promoted to a name suffix on
    /// the strength of the column's title. Nothing is lost — the raw row is kept verbatim in
    /// <c>SisImportRows.RawData</c> — whereas guessing would corrupt the key.
    /// </summary>
    [Fact]
    public void An_unrecognised_suffix_is_neither_an_honorific_nor_part_of_the_name()
    {
        var parsed = TeacherNames.Parse("THERESA GALANG NAVARRO", "THERESA", "NAVARRO", "XYZ");

        Assert.Null(parsed.Honorific);
        Assert.Equal("THERESA GALANG NAVARRO", parsed.DisplayName);
    }

    [Fact]
    public void The_placeholder_in_the_suffix_column_is_not_an_honorific() =>
        Assert.Null(TeacherNames
            .Parse("THERESA GALANG NAVARRO", "THERESA", "NAVARRO", "TO BE ANNOUNCE").Honorific);

    // ------------------------------------------------------------------ honorifics inside the name

    /// <summary>
    /// <c>'SR. CLARA BENITEZ MORALES'</c> has the shape the real file uses: a religious title inside the name column
    /// itself, where no "suffix" rule would ever look for it.
    /// </summary>
    [Fact]
    public void A_leading_honorific_inside_the_name_is_stripped()
    {
        var parsed = TeacherNames.Parse("SR. CLARA BENITEZ MORALES", "SR. CLARA", "MORALES", "Mrs.");

        Assert.Equal("CLARA BENITEZ MORALES", parsed.DisplayName);
        Assert.False(parsed.IsPlaceholder);
    }

    [Theory]
    [InlineData("Rev. Fr. Juan Cruz", "Juan Cruz")]
    [InlineData("Dr. Maria Santos", "Maria Santos")]
    [InlineData("Atty. Jose Rizal", "Jose Rizal")]
    [InlineData("Engr. Pedro Lim", "Pedro Lim")]
    public void Stacked_leading_honorifics_are_all_stripped(string fullName, string expected) =>
        Assert.Equal(expected, TeacherNames.Parse(fullName, "", "", "").DisplayName);

    /// <summary>
    /// Stripping stops while a token remains. A teacher recorded as nothing but a title would otherwise
    /// strip to nothing and take their offering's only teacher reference with them — a silently
    /// unstaffed section, where a visibly wrong name is something someone can see and fix.
    /// </summary>
    [Fact]
    public void An_honorific_only_name_is_kept_rather_than_stripped_to_nothing() =>
        Assert.Equal("Ms.", TeacherNames.Parse("Ms.", "", "", "").DisplayName);

    // ---------------------------------------------------------------- generational suffixes

    /// <summary>
    /// <c>'ROBERTO III MENDOZA SALCEDO'</c> has the shape the real file uses, and the <c>III</c> is inside the
    /// <em>first-name</em> token — second of four in the full name, so a trailing-token rule finds
    /// nothing. Unlike an honorific it is part of the person's name and must stay in it.
    /// </summary>
    [Fact]
    public void A_generational_suffix_is_recognised_inside_the_first_name_token_and_kept()
    {
        var parsed = TeacherNames.Parse(
            "ROBERTO III MENDOZA SALCEDO", "ROBERTO III", "SALCEDO", "Mr.");

        Assert.Equal("III", parsed.GenerationalSuffix);
        Assert.Equal("ROBERTO III MENDOZA SALCEDO", parsed.DisplayName);
        Assert.Contains("III", parsed.DisplayName!, StringComparison.Ordinal);
        Assert.Equal("Mr.", parsed.Honorific);
    }

    [Theory]
    [InlineData("Juan Cruz Jr.", "Jr")]
    [InlineData("Juan Cruz Sr.", "Sr")]
    [InlineData("Juan Cruz II", "II")]
    [InlineData("Juan Cruz IV", "IV")]
    public void Trailing_generational_suffixes_are_recognised(string fullName, string expected) =>
        Assert.Equal(expected, TeacherNames.Parse(fullName, "", "", "").GenerationalSuffix);

    /// <summary>
    /// <c>SR</c> is both an honorific (Sister, leading) and a generational suffix (Senior, trailing),
    /// and position is the only thing that tells them apart. A single membership test would get one of
    /// the two real shapes in this file wrong.
    /// </summary>
    [Fact]
    public void Sr_leading_is_a_title_and_Sr_trailing_is_a_generation()
    {
        var sister = TeacherNames.Parse("SR. CLARA BENITEZ MORALES", "SR. CLARA", "MORALES", "");
        Assert.Equal("CLARA BENITEZ MORALES", sister.DisplayName);
        Assert.Null(sister.GenerationalSuffix);

        var senior = TeacherNames.Parse("Juan Cruz Sr.", "Juan", "Cruz", "");
        Assert.Equal("Juan Cruz Sr.", senior.DisplayName);
        Assert.Equal("Sr", senior.GenerationalSuffix);
    }

    /// <summary>
    /// A bare <c>V</c> between two names is a middle initial in a Filipino roster, not a regnal number.
    /// The set deliberately under-detects rather than mislabel real initials — nothing is keyed on this
    /// field, so the cheap error is the right one.
    /// </summary>
    [Fact]
    public void A_middle_initial_is_not_mistaken_for_a_generational_suffix() =>
        Assert.Null(TeacherNames.Parse("Juan V Cruz", "Juan", "Cruz", "").GenerationalSuffix);

    // -------------------------------------------------------------------------- composition

    /// <summary>
    /// <c>UA_FULLNAME</c> is authoritative — it is the column with nineteen distinct values, which is
    /// the count the registrar recognises. The parts are the fallback, not the source.
    /// </summary>
    [Fact]
    public void The_full_name_column_wins_over_the_parts() =>
        Assert.Equal(
            "THERESA GALANG NAVARRO",
            TeacherNames.Parse("THERESA GALANG NAVARRO", "SOMEONE", "ELSE", "Ms.").DisplayName);

    [Fact]
    public void The_parts_are_composed_when_the_full_name_is_blank() =>
        Assert.Equal("THERESA NAVARRO", TeacherNames.Parse("", "THERESA", "NAVARRO", "Ms.").DisplayName);

    [Fact]
    public void A_wholly_blank_teacher_is_neither_a_placeholder_nor_a_name()
    {
        var parsed = TeacherNames.Parse("", "", "", "");

        Assert.False(parsed.IsPlaceholder);
        Assert.Null(parsed.DisplayName);
    }

    /// <summary>
    /// <c>'MARISOL  VILLAREAL'</c> and <c>'EDUARDO  PANGILINAN'</c> both carry a double space in the real
    /// file — the same defect as <c>'CA  2'</c>, in the teacher column.
    /// </summary>
    [Fact]
    public void A_double_spaced_teacher_name_is_folded() =>
        Assert.Equal(
            "MARISOL VILLAREAL",
            TeacherNames.Parse("MARISOL  VILLAREAL", "MARISOL", "VILLAREAL", "Ms.").DisplayName);
}
