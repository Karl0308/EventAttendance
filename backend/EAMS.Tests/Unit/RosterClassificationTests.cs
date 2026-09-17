using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// Task 5's derivation: what kind of person a roster row describes.
///
/// <para>
/// <b>In the unit suite, and the 21,497-row tally with it.</b> The rule is a pure function of four
/// cells and a registration number, so a database proves nothing about it that a loop does not — and
/// the acceptance criterion is a set of <em>counts over the whole export</em>, which is 21,497 rows
/// and, put through the importer, several hundred thousand fan-out inserts and minutes of wall clock.
/// Pinned here it runs in milliseconds and stays in the loop a developer actually runs.
/// <c>SisImportClassificationTests</c> proves the pipeline calls this function; this proves the
/// function is right.
/// </para>
///
/// <para>
/// <b>Every expected number below was counted off <c>Personnel.xlsx</c> sheet <c>Report</c> by hand</b>
/// — a cross-tabulation of the four category columns against the <c>720000</c> prefix, re-derived twice
/// and independent of anything in this repository. An expectation computed by the implementation's own
/// rules would prove only that the code agrees with itself, which is the exact failure mode of a
/// classifier whose output is 21,497 rows nobody counts.
/// </para>
/// </summary>
public class RosterClassificationTests
{
    private const string Student = "STUDENT";
    private const string Nap = "NAP";
    private const string Acad = "ACAD";
    private const string Ant = "ANT";
    private const string Supervisory = "SUPERVISORY/MANAGERIAL";
    private const string Friars = "USA FRIARS";
    private const string C2B2 = "C2B2";
    private const string Cfi = "CFI";

    /// <summary>A registration number carrying QA's Q3 personnel marker.</summary>
    private const string PrefixedNumber = "7200004273";

    /// <summary>
    /// One of the 21 NAP staff whose number carries no prefix — the real export's <c>0020255</c>. The
    /// single most load-bearing constant in this file.
    /// </summary>
    private const string UnprefixedStaffNumber = "0020255";

    private const string StudentNumber = "2022231181";

    private static Dictionary<string, string?> Cells(
        string? student = null, string? personnel = null, string? friars = null, string? special = null)
    {
        var cells = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ClassificationAxis.Student] = student,
            [ClassificationAxis.Personnel] = personnel,
            [ClassificationAxis.Friars] = friars,
            [ClassificationAxis.Special] = special,
        };

        return cells;
    }

    private static IReadOnlyList<string> ValuesOf(RosterClassification.Resolution resolution) =>
        [.. resolution.Categories.Select(c => c.Value)];

    // ============================================================ the tier the whole design rests on

    /// <summary>
    /// <b>The 21 NAP staff whose registration number is nothing like <c>720000XXXX</c> are still NAP.</b>
    ///
    /// <para>
    /// <b>This is the named regression test for the ruling.</b> QA's Q3 says personnel are recognised by
    /// a <c>720000XXXX</c> number. Measured against the export, 21 of the 292 NAP staff carry
    /// <c>0020255</c>, <c>0020240</c>, <c>0000000</c> and the like — so a derivation that read the
    /// prefix instead of the column would file all 21 as uncategorised or, worse, as students, and
    /// nothing downstream could tell them from the 20,861 the file actually names. If anyone later
    /// "simplifies" the derivation back to a prefix check, this test goes red and names why.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("0020255")]
    [InlineData("0020240")]
    [InlineData("0000000")]
    [InlineData("0020272")]
    public void The_NAP_staff_whose_number_carries_no_personnel_prefix_are_still_NAP(string regNo)
    {
        Assert.False(
            RosterClassification.SuggestsPersonnel(regNo),
            $"'{regNo}' must NOT look like a personnel number — that is the whole point of the case.");

        var resolved = RosterClassification.Resolve(regNo, Cells(personnel: Nap));

        Assert.Equal(RosterClassification.Outcome.Categorised, resolved.Outcome);
        Assert.Equal([Nap], ValuesOf(resolved));
        Assert.Equal(ClassificationAxis.Personnel, resolved.Categories.Single().Axis);
    }

    /// <summary>
    /// <b>The converse, and it is also real: one row in the export carries a <c>720000</c> number and is
    /// flagged <c>STUDENT</c> and nothing else.</b>
    ///
    /// <para>
    /// The column wins outright — the prefix is never consulted for a row that named a category, so
    /// there is no tie to break. Together with the test above, these two pin the demotion in both
    /// directions: 22 rows of the 21,497 are misfiled by a prefix-first rule and none by a column-first
    /// one.
    /// </para>
    /// </summary>
    [Fact]
    public void A_personnel_shaped_number_flagged_STUDENT_resolves_to_STUDENT()
    {
        Assert.True(RosterClassification.SuggestsPersonnel(PrefixedNumber));

        var resolved = RosterClassification.Resolve(PrefixedNumber, Cells(student: Student));

        Assert.Equal(RosterClassification.Outcome.Categorised, resolved.Outcome);
        Assert.Equal([Student], ValuesOf(resolved));
        Assert.Equal(ClassificationAxis.Student, resolved.Categories.Single().Axis);
    }

    /// <summary>
    /// <b>The three people who are two things stay two things.</b> Two of the export's dual rows are
    /// <c>STUDENT</c> + <c>NAP</c> and one is <c>STUDENT</c> + <c>C2B2</c>; a single category column, or
    /// a derivation that stopped at the first hit, would silently drop one of each pair.
    /// </summary>
    [Fact]
    public void A_row_carrying_two_categories_resolves_to_both_on_their_own_axes()
    {
        var staffStudent = RosterClassification.Resolve(
            "7200004273", Cells(student: Student, personnel: Nap));

        Assert.Equal(RosterClassification.Outcome.Categorised, staffStudent.Outcome);
        Assert.Equal([Student, Nap], ValuesOf(staffStudent));
        Assert.Equal(
            [ClassificationAxis.Student, ClassificationAxis.Personnel],
            staffStudent.Categories.Select(c => c.Axis).ToList());

        var specialStudent = RosterClassification.Resolve(
            "0020242", Cells(student: Student, special: C2B2));

        Assert.Equal([Student, C2B2], ValuesOf(specialStudent));
    }

    /// <summary>
    /// The axes come back in <see cref="ClassificationAxis.All"/> order however the caller built its
    /// cells — so the row credited with creating an assignment, and the order of the fan-out entries a
    /// re-import is compared against, are properties of the domain rather than of a dictionary's
    /// insertion order.
    /// </summary>
    [Fact]
    public void Categories_come_back_in_canonical_axis_order()
    {
        var cells = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ClassificationAxis.Special] = C2B2,
            [ClassificationAxis.Friars] = Friars,
            [ClassificationAxis.Personnel] = Nap,
            [ClassificationAxis.Student] = Student,
        };

        Assert.Equal(
            [ClassificationAxis.Student, ClassificationAxis.Personnel,
             ClassificationAxis.Friars, ClassificationAxis.Special],
            RosterClassification.Resolve(StudentNumber, cells).Categories.Select(c => c.Axis).ToList());
    }

    // ================================================================= the two absences, kept distinct

    /// <summary>
    /// <b>A row with no category is never defaulted to <c>STUDENT</c>.</b> 26 of the export's 34
    /// uncategorised rows do look like students — numbers in the <c>2022231XXX</c> block — and guessing
    /// would be right about most of them and would invent the rest, with nothing able to tell the
    /// invented from the stated afterwards.
    /// </summary>
    [Theory]
    [InlineData("2022231181")]
    [InlineData("20222311105")]   // one digit too many
    [InlineData("201910865")]     // one digit too few
    [InlineData("0201-0276-26")]
    [InlineData("1519")]          // junk: Personnel No == Last Name, first name literally 'STUDENT'
    public void A_row_naming_no_category_resolves_to_nothing(string regNo)
    {
        var resolved = RosterClassification.Resolve(regNo, Cells());

        Assert.Equal(RosterClassification.Outcome.None, resolved.Outcome);
        Assert.Empty(resolved.Categories);
    }

    /// <summary>
    /// <b>Tier 2 reports and resolves nothing, deliberately.</b> Among rows carrying the prefix the
    /// personnel column holds NAP (271), ACAD (271), ANT (7) and SUPERVISORY/MANAGERIAL (1), and 12 of
    /// them are USA FRIARS — so the prefix narrows the answer to one of five and is not an answer.
    /// (NAP is 271 and not the 269 of <see cref="SampledExport"/>'s NAP-only stratum: the 2 dual
    /// NAP+STUDENT rows are NAP as well. A partition undercounts every value the 3 dual rows touch.)
    /// Assigning any of them would store a guess where a fact is expected; the outcome says which four
    /// rows a person has to look at instead.
    /// </summary>
    [Theory]
    [InlineData("7200003602")]
    [InlineData("7200002315")]
    [InlineData("7200003629")]
    [InlineData("7200001383")]
    public void A_personnel_shaped_number_with_no_category_reports_but_resolves_nothing(string regNo)
    {
        var resolved = RosterClassification.Resolve(regNo, Cells());

        Assert.Equal(RosterClassification.Outcome.PersonnelNumberOnly, resolved.Outcome);
        Assert.Empty(resolved.Categories);
    }

    /// <summary>
    /// Blank, whitespace and absent cells are all "this column said nothing" — never "clear what is
    /// stored", which is <c>AssignIfPresent</c>'s rule and the reason a later import cannot erase a
    /// classification by shipping an empty column.
    /// </summary>
    [Fact]
    public void Blank_and_absent_cells_say_nothing_rather_than_saying_none()
    {
        Assert.Equal(
            RosterClassification.Outcome.None,
            RosterClassification.Resolve(StudentNumber, Cells(student: "   ")).Outcome);

        Assert.Equal(
            RosterClassification.Outcome.None,
            RosterClassification.Resolve(
                StudentNumber, new Dictionary<string, string?>(StringComparer.Ordinal)).Outcome);

        // A blank on one axis does not suppress a value on another.
        var mixed = RosterClassification.Resolve(StudentNumber, Cells(student: "", special: C2B2));
        Assert.Equal([C2B2], ValuesOf(mixed));
    }

    /// <summary>
    /// The display form survives verbatim, slash and all: matching against the vocabulary is
    /// <see cref="ClassificationText.KeyFor"/>'s job, and a derivation that normalized here would leave
    /// a warning quoting <c>SUPERVISORYMANAGERIAL</c> at an operator looking for
    /// <c>SUPERVISORY/MANAGERIAL</c>.
    /// </summary>
    [Fact]
    public void The_cells_display_form_is_kept_rather_than_normalized()
    {
        var resolved = RosterClassification.Resolve(
            PrefixedNumber, Cells(personnel: $"  {Supervisory}  "));

        Assert.Equal([Supervisory], ValuesOf(resolved));
    }

    // ================================================================ the acceptance criterion itself

    /// <summary>
    /// One stratum of the export: how many rows carry this exact combination of category cells and this
    /// kind of registration number.
    /// </summary>
    private sealed record Stratum(
        int Count, bool PersonnelNumber,
        string? Student = null, string? Personnel = null, string? Friars = null, string? Special = null);

    /// <summary>
    /// <b><c>Personnel.xlsx</c> sheet <c>Report</c>, cross-tabulated by hand.</b> Fifteen strata, and
    /// every one of them is a real population rather than a rounding of one — including the four that
    /// exist to make the prefix rule falsifiable: 21 NAP staff without the prefix, 1 student with it,
    /// 1 friar without it, and the 2 dual NAP+STUDENT rows that carry it.
    ///
    /// <para>
    /// The strata are what was counted; <see cref="ExpectedByValue"/> and
    /// <see cref="ExpectedByOutcome"/> below are what the derivation must produce from them, and they
    /// were written out separately rather than summed from this table — so a mistake in one is not
    /// reproduced in the other.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<Stratum> SampledExport =
    [
        new(20857, PersonnelNumber: false, Student: Student),
        new(1, PersonnelNumber: true, Student: Student),
        new(271, PersonnelNumber: true, Personnel: Acad),
        new(269, PersonnelNumber: true, Personnel: Nap),
        new(21, PersonnelNumber: false, Personnel: Nap),
        new(7, PersonnelNumber: true, Personnel: Ant),
        new(1, PersonnelNumber: true, Personnel: Supervisory),
        new(12, PersonnelNumber: true, Friars: Friars),
        new(1, PersonnelNumber: false, Friars: Friars),
        new(15, PersonnelNumber: false, Special: C2B2),
        new(1, PersonnelNumber: false, Student: Student, Special: C2B2),
        new(2, PersonnelNumber: true, Student: Student, Personnel: Nap),
        new(5, PersonnelNumber: false, Special: Cfi),
        new(4, PersonnelNumber: true),
        new(30, PersonnelNumber: false),
    ];

    private const int ExportRowCount = 21497;

    /// <summary>The population each of the eight values must end up with. Counted, not summed.</summary>
    private static readonly IReadOnlyDictionary<string, int> ExpectedByValue =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [Student] = 20861,
            [Nap] = 292,
            [Acad] = 271,
            [Ant] = 7,
            [Supervisory] = 1,
            [Friars] = 13,
            [C2B2] = 16,
            [Cfi] = 5,
        };

    /// <summary>
    /// 34 rows carry no category at all, and they split 4 / 30 on the prefix. Counted, not summed.
    /// </summary>
    private static readonly IReadOnlyDictionary<RosterClassification.Outcome, int> ExpectedByOutcome =
        new Dictionary<RosterClassification.Outcome, int>
        {
            [RosterClassification.Outcome.Categorised] = 21463,
            [RosterClassification.Outcome.PersonnelNumberOnly] = 4,
            [RosterClassification.Outcome.None] = 30,
        };

    /// <summary>
    /// <b>The acceptance criterion, as an assertion rather than a spreadsheet.</b> Run the derivation
    /// over all 21,497 rows of the sampled export and every value lands on the population the file
    /// actually carries.
    ///
    /// <para>
    /// <b>Why it is a named test and not a note in a report</b> (SOP §8): an acceptance criterion that
    /// lives only in a document can be deleted by any later change that merges cleanly, with nothing
    /// going red anywhere. Pinned here, a derivation that reverts to the prefix rule fails on NAP by 21
    /// and on the uncategorised split by 4, and says so in the message.
    /// </para>
    /// </summary>
    [Fact]
    public void The_sampled_export_resolves_to_the_populations_the_file_carries()
    {
        Assert.Equal(ExportRowCount, SampledExport.Sum(s => s.Count));

        var byValue = new Dictionary<string, int>(StringComparer.Ordinal);
        var byOutcome = new Dictionary<RosterClassification.Outcome, int>();
        var rows = 0;

        foreach (var stratum in SampledExport)
        {
            for (var i = 0; i < stratum.Count; i++)
            {
                // A distinct number per row, so nothing can pass by collapsing duplicates. The prefix
                // is the only property of it the derivation is allowed to care about.
                var regNo = stratum.PersonnelNumber
                    ? $"{RosterClassification.PersonnelNumberPrefix}{i:D4}"
                    : $"2022{i:D6}";

                var resolved = RosterClassification.Resolve(
                    regNo,
                    Cells(stratum.Student, stratum.Personnel, stratum.Friars, stratum.Special));

                rows++;
                byOutcome[resolved.Outcome] = byOutcome.GetValueOrDefault(resolved.Outcome) + 1;

                foreach (var category in resolved.Categories)
                    byValue[category.Value] = byValue.GetValueOrDefault(category.Value) + 1;
            }
        }

        Assert.Equal(ExportRowCount, rows);

        Assert.Equal(ExpectedByValue.Count, byValue.Count);
        foreach (var (value, expected) in ExpectedByValue)
            Assert.True(
                byValue.GetValueOrDefault(value) == expected,
                $"'{value}' resolved on {byValue.GetValueOrDefault(value)} rows; the export carries " +
                $"{expected}. A prefix-first derivation misses NAP by 21 — see " +
                nameof(The_NAP_staff_whose_number_carries_no_personnel_prefix_are_still_NAP) + ".");

        foreach (var (outcome, expected) in ExpectedByOutcome)
            Assert.True(
                byOutcome.GetValueOrDefault(outcome) == expected,
                $"{outcome} on {byOutcome.GetValueOrDefault(outcome)} rows; the export carries " +
                $"{expected}.");

        // Not vacuous about the thing that matters most: 34 people end up with no classification, and
        // not one of them was quietly made a student.
        Assert.Equal(34, byOutcome[RosterClassification.Outcome.None]
                         + byOutcome[RosterClassification.Outcome.PersonnelNumberOnly]);
    }
}
