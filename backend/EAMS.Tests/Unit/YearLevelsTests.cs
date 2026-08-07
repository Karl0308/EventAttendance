using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// D-47's rule, on its own, with no database in the way: <see cref="YearLevels.Derive"/> reads a year
/// out of the sections a student is enrolled in, or reads out nothing.
///
/// <para>
/// <b>Every case here is about refusing, not about accepting.</b> The reason the rule exists is that
/// <c>NSTP 2</c> and <c>BSCRIM 2-A</c> both contain a <c>2</c> and only one of them means "2nd year",
/// so a digit-scanner produces a plausible, non-empty, wrong answer for every student in a subject
/// block — a wrong denominator nothing downstream can tell apart from a right one. The positive cases
/// are here only so the negative ones cannot pass by the function returning <c>null</c> for
/// everything.
/// </para>
///
/// <para>
/// <b>Keys, not display values.</b> Both arguments arrive already normalized by
/// <see cref="AcademicKey"/> — upper-cased, letters and digits only — so the fixtures spell
/// <c>BSCRIM2A</c> rather than <c>'BSCRIM 2-A'</c>. Feeding this raw display text would fail the
/// ordinal prefix test while looking entirely correct.
/// </para>
///
/// <para>
/// <b>The programme codes below are arranged, and deliberately so.</b> Whether the registrar's real
/// <c>PROGRAM</c> column ever produces a code that prefixes its own section names is an open question
/// (<c>"BSci - Crim"</c> normalizes to <c>BSCICRIM</c> while <c>"BSCRIM 2-A"</c> normalizes to
/// <c>BSCRIM2A</c>, which do not match). Nothing in this file asserts anything about that column: each
/// case states what the rule does with inputs it is given, which stays true however that question is
/// answered.
/// </para>
/// </summary>
public class YearLevelsTests
{
    private const string Criminology = "BSCRIM";
    private const string InformationTechnology = "BSIT";

    /// <summary>
    /// A programme whose code is a strict prefix of another's (<c>BSNED</c>), which is what makes
    /// "the digit must sit immediately after the code" a real rule rather than a tidiness.
    /// </summary>
    private const string Nursing = "BSN";

    // ------------------------------------------------------------------ the tripwire (D-47)

    /// <summary>
    /// <b>The assertion this whole phase exists for.</b> A subject block mixes students from every
    /// year by construction, so the digit in its name is a block number and reading it as a year
    /// mislabels every student in the block. All four of these are real section names from the roster
    /// fixtures.
    /// </summary>
    [Theory]
    [InlineData("NSTP2")]
    [InlineData("GE8")]
    [InlineData("SSCI7")]
    [InlineData("CHEM1")]
    public void A_subject_block_carrying_a_digit_never_becomes_a_year(string block) =>
        Assert.Null(YearLevels.Derive(Criminology, [block]));

    /// <summary>
    /// The sharper form of the same thing: blocks that happen to <em>agree</em> on a digit. A rule that
    /// scanned any section for a digit would not merely guess here — it would return a confident,
    /// unanimous <c>"2"</c> with nothing anywhere to suggest a choice had been made.
    /// </summary>
    [Fact]
    public void Blocks_that_agree_on_a_digit_still_derive_nothing() =>
        Assert.Null(YearLevels.Derive(Criminology, ["NSTP2", "GE2", "ROTC"]));

    /// <summary>
    /// And the converse, which is what keeps the refusals above from being satisfiable by a function
    /// that always returns null: a home section decides the year, and the blocks a student takes
    /// alongside it contribute nothing in either direction — not the year, and not an ambiguity that
    /// would erase it.
    /// </summary>
    [Fact]
    public void A_home_section_decides_the_year_and_the_blocks_beside_it_do_not_disturb_it() =>
        Assert.Equal("2", YearLevels.Derive(Criminology, ["BSCRIM2A", "GE8", "NSTP2", "ROTC"]));

    // ------------------------------------------------------------------ null is a first-class answer

    /// <summary>
    /// <c>ROTC</c> carries no digit at all. There is nothing to misread, and the answer is still an
    /// answer rather than a fallback — the rule has no default year to reach for.
    /// </summary>
    [Fact]
    public void A_section_with_no_digit_at_all_derives_nothing() =>
        Assert.Null(YearLevels.Derive(Criminology, ["ROTC"]));

    /// <summary>
    /// A student whose entire enrolment is subject blocks. <c>null</c> is the correct outcome and a
    /// first-class one: they appear in no year group and stay invitable by programme, section, course
    /// and individually.
    /// </summary>
    [Fact]
    public void A_student_whose_only_sections_are_blocks_derives_nothing() =>
        Assert.Null(YearLevels.Derive(Criminology, ["NSTP2", "SSCI7", "CHEM1", "ROTC"]));

    /// <summary>The degenerate case — no enrolments at all — takes the same road, not an exception.</summary>
    [Fact]
    public void A_student_with_no_sections_at_all_derives_nothing() =>
        Assert.Null(YearLevels.Derive(Criminology, []));

    // ------------------------------------------------------------------ a conflict is not a tie

    /// <summary>
    /// <c>BSIT 2-A</c> <em>and</em> <c>BSIT 3-A</c> is a roster error, not a tie to be broken. Both
    /// orderings are asserted because a deterministic pick — first wins, last wins — is still a wrong
    /// answer half the time, and the two orders are what tell "the rule refused" apart from "the rule
    /// picked, and this fixture happened to list the right one first".
    /// </summary>
    [Theory]
    [InlineData("BSIT2A", "BSIT3A")]
    [InlineData("BSIT3A", "BSIT2A")]
    public void Two_home_sections_naming_different_years_derive_nothing(string first, string second) =>
        Assert.Null(YearLevels.Derive(InformationTechnology, [first, second]));

    /// <summary>
    /// The boundary on that refusal, without which "refuse a second home section" would pass the tests
    /// above while being a different and much worse rule. Two sections of the same year — a student in
    /// <c>BSIT 2-A</c> for one course and <c>BSIT 2-B</c> for another — do not disagree about anything.
    /// </summary>
    [Fact]
    public void Two_home_sections_naming_the_same_year_agree_rather_than_conflict() =>
        Assert.Equal("2", YearLevels.Derive(InformationTechnology, ["BSIT2A", "BSIT2B"]));

    // ------------------------------------------- what "a year digit" means (D-47's three narrowings)

    /// <summary>
    /// <b>The three refusals D-47's text does not spell out, and the reason they need pinning.</b>
    /// Each narrows the rule further toward "no answer" — none of them can invent a year — but all
    /// three live in <c>TryReadYearDigit</c> rather than in the decision, so nothing but this theory
    /// stops a later simplification from quietly dropping them.
    ///
    /// <para>
    /// The simplification to guard against is exactly the obvious one: <c>char.IsAsciiDigit</c> on the
    /// first character of the remainder. It reads as a tidy-up, it leaves the programme anchor intact,
    /// and it passes every other test in this file — while re-admitting <c>BSIT12A</c> as year 1 and
    /// <c>BSIT0A</c> as year 0. A year of <c>"0"</c> is the worse half: nothing downstream validates
    /// the key, so it projects a <c>StudentGroup</c> literally named <c>"0 (2025-2026-1)"</c> and an
    /// operator can invite it. That is a guessed year becoming an audience, which is the one outcome
    /// D-47 exists to prevent.
    /// </para>
    ///
    /// <para>
    /// <b><c>BSNED1A</c> against <c>BSN</c> is the subtlest of the three.</b> The section belongs to a
    /// *different* programme whose code merely begins with this student's — so a rule that scanned
    /// past the code for the first digit anywhere in the remainder would read a neighbouring cohort's
    /// year as this student's, confidently and with a section that really does encode a year. The
    /// digit has to sit immediately after the code or the match is not a home section at all.
    /// </para>
    ///
    /// <para>
    /// Note what is <em>not</em> asserted here: that any of these shapes occurs in the real export.
    /// They pin the rule's own boundaries, which hold however the open registrar question about
    /// <c>PROGRAM</c> and the section prefix is eventually answered.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(Nursing, "BSNED1A")]      // a longer programme's cohort, not this student's
    [InlineData(InformationTechnology, "BSIT12A")]  // two digits: not a year, and not year 1
    [InlineData(InformationTechnology, "BSIT0A")]   // there is no year zero
    [InlineData(InformationTechnology, "BSITA")]    // matched the code, then no digit to read
    public void A_remainder_that_is_not_exactly_one_nonzero_digit_derives_nothing(
        string programCodeKey, string sectionKey) =>
        Assert.Null(YearLevels.Derive(programCodeKey, [sectionKey]));
}
