using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Sis;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>Task 5 — the roster carries Classification.</b> End to end against real SQL Server, where the
/// composite foreign key <c>(ClassificationId, Axis) → Classifications(Id, Axis)</c> and
/// <c>UX_StudentClassifications_Student_Axis</c> are the things that actually exist.
///
/// <para>
/// <b>The derivation's own acceptance criterion — the 21,497-row tally — lives in
/// <c>EAMS.Tests.Unit.RosterClassificationTests</c> and deliberately not here.</b> It is a property of a
/// pure function, and putting a 21,497-row workbook through the pipeline to assert it would cost
/// minutes of wall clock and several hundred thousand fan-out inserts to prove something a loop proves
/// in milliseconds. What this file proves is the other half, which that one cannot: <b>that the
/// pipeline calls that function, writes what it returns, and writes nothing else.</b>
/// </para>
///
/// <para>
/// The fixture's twelve rows carry the two cases that decide the design — see
/// <see cref="SyntheticRoster.PersonnelCategory"/> (NAP staff with no <c>720000</c> number) and
/// <see cref="SyntheticRoster.SpecialCategory"/> (a person who is two things).
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportClassificationTests : IntegrationTest
{
    public SisImportClassificationTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Junction rows one clean import of the twelve fixture rows produces, worked out by hand from
    /// <see cref="SyntheticRoster.Rows"/>: eight people, of whom Ana holds <c>STUDENT</c> + <c>C2B2</c>
    /// on one row and Rosa holds <c>STUDENT</c> + <c>CFI</c> across two.
    /// </summary>
    private const int ExpectedAssignments = 10;

    /// <summary>
    /// The six rows the fixture has always warned on — a blank section, two placeholder teachers, a
    /// title alias and a two-programme section pair. Named so a test can assert a batch is <b>no
    /// noisier than it has always been</b>, which is a sharper claim than "some warning is absent".
    /// </summary>
    private const int ExpectedFixtureWarningRows = 6;

    private sealed record World(Guid SchoolId, Guid TermId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);

        // The vocabulary a booted host seeds and InitializeAsync deletes. Written through
        // ClassificationSeedValues so these tests resolve against production's eight rows rather than
        // an invented list — the drift SeedData.DevelopmentCardUid records having been bitten by.
        db.Classifications.AddRange(TestData.ClassificationVocabulary(school.Id));

        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    private async Task<SisImportBatchDto> ImportAsync(Guid termId, Stream? file = null)
    {
        var owned = file is null ? SyntheticRoster.Build() : null;
        var content = file ?? owned!;

        try
        {
            content.Position = 0;

            await using var db = NewDbContext();
            var service = SisImportOn(db);
            var preview = await service.UploadAsync(
                new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", termId));

            return await service.RunAsync(preview.Batch.Id, termId);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>The classification display names one person holds, in axis order.</summary>
    private async Task<List<string>> CategoriesOfAsync(string regNo)
    {
        await using var db = NewDbContext();

        var student = await db.Students.SingleAsync(s => s.StudentNumber == regNo);

        return await db.StudentClassifications
            .Where(a => a.StudentId == student.Id)
            .OrderBy(a => a.Axis)
            .Select(a => a.Classification!.Name)
            .ToListAsync();
    }

    private async Task<List<SisImportRowDto>> RowsOfAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return (await SisImportOn(db).GetRowsAsync(batchId, result: null)).ToList();
    }

    private static SisImportRowDto Row(IEnumerable<SisImportRowDto> rows, int rowNumber) =>
        rows.Single(r => r.RowNumber == rowNumber);

    /// <summary>
    /// A workbook built from the fixture's rows with <paramref name="mutate"/> applied. Cells are
    /// addressed through <see cref="SyntheticRoster.ColumnIndex"/> rather than by literal position, so
    /// a later column insertion cannot silently retarget one of these tests at the wrong cell.
    /// </summary>
    private static MemoryStream BuiltWith(Action<List<string[]>> mutate)
    {
        var rows = SyntheticRoster.Rows();
        mutate(rows);
        return SyntheticRoster.Build(rows);
    }

    private static void SetCell(List<string[]> rows, int rowIndex, string column, string value) =>
        rows[rowIndex][SyntheticRoster.ColumnIndex(column)] = value;

    private static void ClearCategories(List<string[]> rows, int rowIndex)
    {
        foreach (var (_, column) in SisRosterColumns.ClassificationColumns)
            SetCell(rows, rowIndex, column, "");
    }

    // ================================================================== the thing Task 5 asked for

    /// <summary>
    /// <b>The roster classifies the people it names.</b> The headline: after one import, every person in
    /// the file holds the categories the file gave them, on the axes those columns mean.
    /// </summary>
    [Fact]
    public async Task The_import_files_every_person_under_the_categories_their_row_names()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        Assert.Equal([SyntheticRoster.StudentCategory], await CategoriesOfAsync(SyntheticRoster.MariaRegNo));
        Assert.Equal([SyntheticRoster.StudentCategory], await CategoriesOfAsync(SyntheticRoster.JuanRegNo));
        Assert.Equal([SyntheticRoster.StudentCategory], await CategoriesOfAsync(SyntheticRoster.LuciaRegNo));

        // Rosa's two axes come from two different rows — see
        // A_persons_axes_are_gathered_across_every_row_that_names_them.
        Assert.Equal(
            [SyntheticRoster.SecondSpecialCategory, SyntheticRoster.StudentCategory],
            (await CategoriesOfAsync(SyntheticRoster.RosaRegNo)).Order(StringComparer.Ordinal).ToList());

        await using var read = NewDbContext();

        // Eight students; Ana and Rosa hold two each. Ten junction rows, no more — a person named on
        // four course rows is classified once, not four times.
        Assert.Equal(ExpectedAssignments, await read.StudentClassifications.CountAsync());

        // And the vocabulary is untouched: the import assigns from it and never adds to it.
        Assert.Equal(ClassificationSeedValues.All.Count, await read.Classifications.CountAsync());
    }

    /// <summary>
    /// <b>The named regression test for JJ's ruling, end to end.</b>
    /// <see cref="SyntheticRoster.PedroRegNo"/> is <c>2021005781</c> — no <c>720000</c> prefix — and his
    /// row says <c>NAP</c> in the personnel column.
    ///
    /// <para>
    /// QA's Q3 recognises personnel by the prefix, which misfiles <b>21 of the real export's 292 NAP
    /// staff</b>. Pedro is this fixture's copy of those 21: a derivation that read the number rather
    /// than the column would classify him as nothing at all, and — because he has no student column
    /// either — would leave a member of staff uncategorised with no warning worth the name. If anyone
    /// simplifies the derivation back to a prefix check, this goes red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_staff_member_whose_number_carries_no_personnel_prefix_is_still_classified_NAP()
    {
        Assert.False(
            RosterClassification.SuggestsPersonnel(SyntheticRoster.PedroRegNo),
            "The fixture's staff member must NOT have a personnel-shaped number — that is the case.");

        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        Assert.Equal([SyntheticRoster.PersonnelCategory],
            await CategoriesOfAsync(SyntheticRoster.PedroRegNo));

        await using var read = NewDbContext();
        var student = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.PedroRegNo);

        // On the Personnel axis, which is the column the value came out of — not a judgement about the
        // word NAP. The composite FK makes a row that disagreed with its parent unwritable, so this
        // asserts the axis reached the row rather than that the database allowed it.
        Assert.Equal(ClassificationAxis.Personnel,
            await read.StudentClassifications.Where(a => a.StudentId == student.Id)
                .Select(a => a.Axis).SingleAsync());
    }

    /// <summary>
    /// <b>The person who is two things stays two things.</b> <see cref="SyntheticRoster.AnaRegNo"/> is
    /// <c>STUDENT</c> and <c>C2B2</c> at once, like the real export's <c>0020242</c>. Two category
    /// columns could not have said so and a scalar <c>Students.ClassificationId</c> could not have held
    /// it — which is the whole argument for four columns over QA's two.
    /// </summary>
    [Fact]
    public async Task A_person_carrying_two_categories_is_filed_under_both()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        Assert.Equal(
            [SyntheticRoster.SpecialCategory, SyntheticRoster.StudentCategory],
            (await CategoriesOfAsync(SyntheticRoster.AnaRegNo)).Order(StringComparer.Ordinal).ToList());

        await using var read = NewDbContext();
        var student = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.AnaRegNo);

        Assert.Equal(
            [ClassificationAxis.Special, ClassificationAxis.Student],
            await read.StudentClassifications.Where(a => a.StudentId == student.Id)
                .Select(a => a.Axis).OrderBy(a => a).ToListAsync());
    }

    /// <summary>
    /// <b>A person's axes are gathered across all of their rows, not taken from one of them.</b>
    /// <see cref="SyntheticRoster.RosaRegNo"/> is named on two rows: her <c>STUDENT</c> flag is on the
    /// first and her <c>CFI</c> flag on the second, each row blank where the other speaks.
    ///
    /// <para>
    /// <b>This is the case that had no coverage at all until the gate asked for it.</b> A course
    /// roster's grain is the enrollment, so a registrar's report carries a person's category columns on
    /// whichever row it happened to fill in — and reading one row's set entire silently drops every axis
    /// the others named. Both of the fixture's other multi-axis people carry both axes on a single row,
    /// so replacing the per-axis union with <c>group.First.Categories.Categories</c> left all 1987
    /// tests green. It does not now.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_persons_axes_are_gathered_across_every_row_that_names_them()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        Assert.Equal(
            [SyntheticRoster.SecondSpecialCategory, SyntheticRoster.StudentCategory],
            (await CategoriesOfAsync(SyntheticRoster.RosaRegNo)).Order(StringComparer.Ordinal).ToList());

        await using var read = NewDbContext();
        var rosa = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.RosaRegNo);

        Assert.Equal(
            [ClassificationAxis.Special, ClassificationAxis.Student],
            await read.StudentClassifications.Where(a => a.StudentId == rosa.Id)
                .Select(a => a.Axis).OrderBy(a => a).ToListAsync());
    }

    /// <summary>
    /// <b>Two rows for one REGNO that contradict each other on an axis are reported, and the first
    /// row's value is kept</b> — the rule <c>WarnOnIdentityConflicts</c> already applied to names and
    /// e-mail, extended to the category columns because they describe the person rather than the
    /// enrollment.
    ///
    /// <para>
    /// A blank on one row is <em>not</em> a contradiction — that is the ordinary shape proved by the
    /// test above, and treating it as one would warn on most of the roster. Only two rows both naming
    /// something, and naming different things, count.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_rows_contradicting_each_other_on_one_axis_are_reported()
    {
        var world = await ArrangeAsync();

        // Rosa's two rows, both naming a Personnel category and disagreeing about it.
        await using var file = BuiltWith(rows =>
        {
            SetCell(rows, 7, SisRosterColumns.PersonnelCategory, SyntheticRoster.PersonnelCategory);
            SetCell(rows, 8, SisRosterColumns.PersonnelCategory, "ACAD");
        });

        var batch = await ImportAsync(world.TermId, file);

        // The first row wins, exactly as it does for a contradicted surname.
        Assert.Contains(SyntheticRoster.PersonnelCategory,
            await CategoriesOfAsync(SyntheticRoster.RosaRegNo));
        Assert.DoesNotContain("ACAD", await CategoriesOfAsync(SyntheticRoster.RosaRegNo));

        var row = Row(await RowsOfAsync(batch.Id), 10);
        Assert.Equal(SisImportWarningCode.StudentIdentityConflict, row.WarningCode);
        Assert.Contains($"{ClassificationAxis.Personnel} category", row.WarningMessage!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Every one of the eight seeded values can be assigned by the importer</b> — including
    /// <see cref="SyntheticRoster.SupervisoryCategory"/>, the only one containing a slash and therefore
    /// the one most likely to break a naive key, slug or route assumption anywhere along the path from
    /// cell to junction row.
    ///
    /// <para>
    /// Five of the eight never traversed the importer in any test before the gate asked. They are
    /// exercised here by mutation rather than by being written into
    /// <see cref="SyntheticRoster.Rows"/>, so the twelve fixture rows keep the shape of the real roster
    /// — where 20,861 of 21,497 rows say <c>STUDENT</c> and nothing else.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_seeded_vocabulary_value_survives_the_importer()
    {
        var world = await ArrangeAsync();

        await using var file = BuiltWith(rows =>
        {
            // Maria already holds STUDENT; ACAD joins it on the Personnel axis.
            SetCell(rows, 0, SisRosterColumns.PersonnelCategory, "ACAD");
            SetCell(rows, 4, SisRosterColumns.FriarsCategory, "USA FRIARS");
            SetCell(rows, 9, SisRosterColumns.PersonnelCategory, "ANT");
            SetCell(rows, 10, SisRosterColumns.PersonnelCategory, SyntheticRoster.SupervisoryCategory);
        });

        var batch = await ImportAsync(world.TermId, file);

        await using var read = NewDbContext();

        var assigned = await read.StudentClassifications
            .Select(a => a.Classification!.Name)
            .Distinct()
            .ToListAsync();

        Assert.Equal(
            ClassificationSeedValues.Names.Order(StringComparer.Ordinal).ToList(),
            assigned.Order(StringComparer.Ordinal).ToList());

        // The slash survived the whole path, and landed on the axis its column means.
        var nina = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.NinaRegNo);
        var supervisory = await read.StudentClassifications
            .Where(a => a.StudentId == nina.Id && a.Axis == ClassificationAxis.Personnel)
            .Select(a => a.Classification!.Name)
            .SingleAsync();

        Assert.Equal(SyntheticRoster.SupervisoryCategory, supervisory);

        // None of this cost a warning: eight real categories, resolved.
        Assert.DoesNotContain(await RowsOfAsync(batch.Id), r =>
            r.WarningCode is SisImportWarningCode.ClassificationUnavailable
                or SisImportWarningCode.ClassificationConflict
                or SisImportWarningCode.ClassificationMissing);
    }

    // ============================================================================== idempotency

    /// <summary>
    /// <b>A second import of the same file changes no classification, and says so positively.</b> Every
    /// <c>StudentClassification</c> touch on the second batch is <c>Unchanged</c> — recorded rather than
    /// omitted, which is what makes "this row referenced the assignment and found it already correct"
    /// distinguishable from "this row never mentioned one".
    /// </summary>
    [Fact]
    public async Task A_second_import_reassigns_nothing_and_records_every_touch_as_unchanged()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);
        var second = await ImportAsync(world.TermId);

        await using var read = NewDbContext();
        Assert.Equal(ExpectedAssignments, await read.StudentClassifications.CountAsync());

        var touches = (await RowsOfAsync(second.Id))
            .SelectMany(r => r.Entities)
            .Where(e => e.EntityType == SisImportEntityType.StudentClassification)
            .ToList();

        Assert.NotEmpty(touches);
        Assert.All(touches, t => Assert.Equal(SisImportEntityAction.Unchanged, t.Action));
        Assert.Equal(SisImportRowResult.Skipped, Row(await RowsOfAsync(second.Id), 2).Result);
    }

    /// <summary>
    /// <b>A blank category column on a later import does not clear what is stored.</b> The
    /// <c>AssignIfPresent</c> rule, which matters more on this column than on a middle name: a
    /// classification can now be set by hand, and a file that simply stopped carrying the column would
    /// otherwise uncategorise the whole roster and report it as an ordinary update.
    /// </summary>
    [Fact]
    public async Task A_blank_category_on_a_later_import_leaves_the_stored_one_alone()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var blanked = SyntheticRoster.Build(SyntheticRoster.RowsWithoutCategories());
        var second = await ImportAsync(world.TermId, blanked);

        Assert.Equal([SyntheticRoster.StudentCategory],
            await CategoriesOfAsync(SyntheticRoster.MariaRegNo));
        Assert.Equal([SyntheticRoster.PersonnelCategory],
            await CategoriesOfAsync(SyntheticRoster.PedroRegNo));

        await using var read = NewDbContext();
        Assert.Equal(ExpectedAssignments, await read.StudentClassifications.CountAsync());

        // ---- and the batch report has to agree with all of that -----------------------------------
        //
        // THE PART THAT WAS MISSING, and the reason this test used to pass against a wrong report. It
        // drove exactly this path — a file whose four category columns are present and unfilled, which
        // is what the registrar ships the first time a column is added to a report and nobody
        // populates it — and asserted only the stored values and a count. The warnings went unread, so
        // a batch telling an operator that every correctly classified person "holds NO classification"
        // was indistinguishable from a correct one. Right about the data, silent about the thing a
        // human actually reads.
        var rows = await RowsOfAsync(second.Id);

        Assert.DoesNotContain(rows, r =>
            r.WarningCode is SisImportWarningCode.ClassificationMissing
                or SisImportWarningCode.ClassificationRegNoSuggestsPersonnel);

        Assert.DoesNotContain(rows, r =>
            r.WarningMessage is not null
            && r.WarningMessage.Contains("holds NO classification", StringComparison.Ordinal));

        // Six warned rows, not twelve: the batch is exactly as noisy as the first import was, which is
        // the whole claim. A blank column the file never filled in is not news.
        Assert.Equal(ExpectedFixtureWarningRows, second.WarningRows);
    }

    /// <summary>
    /// <b>A category retired after the first import does not turn its holders into a warning.</b>
    ///
    /// <para>
    /// Somebody holds <c>NAP</c>; an administrator retires <c>NAP</c>, which by design leaves every
    /// holder holding it; the unchanged file is imported again. Nothing has changed, so the batch must
    /// say nothing — and the person must keep <c>NAP</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The first cut got this wrong by asking the two questions in the wrong order</b>, consulting
    /// <see cref="ClassificationAssignment.IsAssignable"/> before "does this person already hold it?".
    /// That helper's own remarks require the opposite: <em>"Re-sending an assignment somebody already
    /// has must stay a no-op even when the row has since been retired. Callers check 'already held'
    /// before they consult this."</em> The result was a warning asserting the person was unclassified
    /// on an axis they were in fact classified on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_category_retired_after_it_was_assigned_is_a_no_op_rather_than_a_warning()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        Assert.Equal([SyntheticRoster.PersonnelCategory],
            await CategoriesOfAsync(SyntheticRoster.PedroRegNo));

        await using (var db = NewDbContext())
        {
            var nap = await db.Classifications.SingleAsync(
                c => c.Name == SyntheticRoster.PersonnelCategory);
            nap.IsActive = false;
            nap.RetiredAt = TestData.Now;
            await db.SaveChangesAsync();
        }

        var second = await ImportAsync(world.TermId);

        // Still held — retiring withdraws a category from pickers, it does not take it off anybody.
        Assert.Equal([SyntheticRoster.PersonnelCategory],
            await CategoriesOfAsync(SyntheticRoster.PedroRegNo));

        var row = Row(await RowsOfAsync(second.Id), 8);
        Assert.NotEqual(SisImportWarningCode.ClassificationUnavailable, row.WarningCode);

        // And the touch is recorded as the no-op it is, rather than withheld.
        Assert.Contains(row.Entities, e =>
            e.EntityType == SisImportEntityType.StudentClassification
            && e.Action == SisImportEntityAction.Unchanged);
    }

    // ======================================================== the absences, and the two kinds of them

    /// <summary>
    /// <b>A row naming no category is imported, enrolled, classified as nothing, and warned about.</b>
    /// Never defaulted to <c>STUDENT</c>: on 21,497 rows that would invent 34 students, and nothing
    /// downstream could tell the invented from the stated.
    /// </summary>
    [Fact]
    public async Task A_row_with_no_category_imports_uncategorised_and_warns()
    {
        var world = await ArrangeAsync();

        // Nina, row 12, who is named on exactly one row — so the warning is unambiguous.
        await using var file = BuiltWith(rows => ClearCategories(rows, 10));
        var batch = await ImportAsync(world.TermId, file);

        Assert.Empty(await CategoriesOfAsync(SyntheticRoster.NinaRegNo));

        var row = Row(await RowsOfAsync(batch.Id), 12);
        Assert.Equal(SisImportWarningCode.ClassificationMissing, row.WarningCode);

        // Imported, not lost: the person and their enrollment are there.
        await using var read = NewDbContext();
        Assert.True(await read.Students.AnyAsync(s => s.StudentNumber == SyntheticRoster.NinaRegNo));
        Assert.NotEqual(SisImportRowResult.Failed, row.Result);
    }

    /// <summary>
    /// <b>Tier 2 gets its own code, and still assigns nothing.</b> A row with no category whose
    /// registration number carries QA's <c>720000</c> marker is very probably personnel — but the prefix
    /// narrows the answer to one of four personnel values, and to <c>USA FRIARS</c> on 12 rows of the
    /// real export, so choosing among them would store a guess where a fact is expected.
    ///
    /// <para>
    /// It is a separate code from <see cref="SisImportWarningCode.ClassificationMissing"/> because the
    /// follow-up differs: this is a personnel record whose category cell the registrar left blank and is
    /// fixable at source.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_personnel_shaped_number_with_no_category_is_warned_under_its_own_code()
    {
        var world = await ArrangeAsync();

        const string personnelNumber = "7200003602";
        await using var file = BuiltWith(rows =>
        {
            ClearCategories(rows, 10);
            SetCell(rows, 10, SisRosterColumns.RegNo, personnelNumber);
        });

        var batch = await ImportAsync(world.TermId, file);

        Assert.Empty(await CategoriesOfAsync(personnelNumber));

        var row = Row(await RowsOfAsync(batch.Id), 12);
        Assert.Equal(SisImportWarningCode.ClassificationRegNoSuggestsPersonnel, row.WarningCode);
        Assert.Contains(RosterClassification.PersonnelNumberPrefix, row.WarningMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A file with none of the four category columns classifies nobody and warns about nothing.</b>
    /// That is every roster that existed before the template changed, and the silence is deliberate: a
    /// warning on 100% of the rows of every legacy batch would make <c>CompletedWithWarnings</c> the
    /// permanent status of every import and bury the genuine warnings underneath it — the identical
    /// argument <c>ParseRows</c> records for a missing RFID column.
    ///
    /// <para>
    /// <b>The contrast with the test above is the whole assertion.</b> An absent column and a
    /// present-but-blank one reach the parse by different routes and must produce different outcomes:
    /// silence versus <c>ClassificationMissing</c>. A pipeline that conflated them would pass one of
    /// these two tests and fail the other.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_roster_carrying_no_category_column_assigns_nothing_and_warns_about_nothing()
    {
        var world = await ArrangeAsync();

        await using var legacy = SyntheticRoster.BuildWithoutCategoryColumns();
        var batch = await ImportAsync(world.TermId, legacy);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.StudentClassifications.CountAsync());
        Assert.Equal(SyntheticRoster.DataRowCount, batch.TotalRows);

        var rows = await RowsOfAsync(batch.Id);
        Assert.DoesNotContain(rows, r =>
            r.WarningCode is SisImportWarningCode.ClassificationMissing
                or SisImportWarningCode.ClassificationRegNoSuggestsPersonnel
                or SisImportWarningCode.ClassificationUnavailable
                or SisImportWarningCode.ClassificationConflict);

        // The batch's other warnings are exactly the six the fixture has always produced, so this is a
        // statement about classification rather than about a quiet batch.
        Assert.Equal(ExpectedFixtureWarningRows, batch.WarningRows);
    }

    // ================================================== the vocabulary is administrator-owned, not ours

    /// <summary>
    /// <b>A category the vocabulary does not have is reported, and is never created.</b>
    ///
    /// <para>
    /// <b>This is the <c>'NA'</c> trap, pinned.</b> A hastily generated export fills every optional
    /// column with the literal string <c>NA</c>, and the required-value check rejects <em>blank</em>
    /// rather than <c>NA</c> — so such a file imports "successfully" and, on a pipeline that minted
    /// vocabulary from cells, would create a classification called NA and file the entire roster under
    /// it. <c>UX_Classifications_SchoolId_NameKey</c> makes that row permanent: there is no delete while
    /// anybody holds it, only a merge somebody has to perform.
    /// </para>
    ///
    /// <para>
    /// It is the deliberate asymmetry with colleges, programmes and courses, which this importer does
    /// mint from the file. Nobody else owns those; QA's Q2 says an administrator owns this list.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_category_the_vocabulary_does_not_have_is_reported_and_not_created()
    {
        var world = await ArrangeAsync();

        await using var file = BuiltWith(rows =>
            SetCell(rows, 10, SisRosterColumns.StudentCategory, "NA"));

        var batch = await ImportAsync(world.TermId, file);

        Assert.Empty(await CategoriesOfAsync(SyntheticRoster.NinaRegNo));

        await using var read = NewDbContext();
        Assert.Equal(ClassificationSeedValues.All.Count, await read.Classifications.CountAsync());
        Assert.False(await read.Classifications.AnyAsync(c => c.Name == "NA"));

        var row = Row(await RowsOfAsync(batch.Id), 12);
        Assert.Equal(SisImportWarningCode.ClassificationUnavailable, row.WarningCode);
        Assert.Contains("NA", row.WarningMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A retired category is not assigned and is not reactivated.</b> Retiring is a decision a person
    /// made for a reason the roster has no column for — the same rule
    /// <see cref="SisImportWarningCode.RfidCardRevoked"/> applies to a revoked card, and the reason the
    /// vocabulary prefetch does not filter on <c>IsActive</c>: filtered, a retired row would look absent
    /// and the operator would be told to add a category they had deliberately withdrawn.
    /// </summary>
    [Fact]
    public async Task A_retired_category_is_neither_assigned_nor_reactivated()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var student = await db.Classifications.SingleAsync(
                c => c.Name == SyntheticRoster.StudentCategory);
            student.IsActive = false;
            student.RetiredAt = TestData.Now;
            await db.SaveChangesAsync();
        }

        var batch = await ImportAsync(world.TermId);

        Assert.Empty(await CategoriesOfAsync(SyntheticRoster.MariaRegNo));

        // Pedro's NAP is untouched: one retired value does not stop the rest of the roster classifying.
        Assert.Equal([SyntheticRoster.PersonnelCategory],
            await CategoriesOfAsync(SyntheticRoster.PedroRegNo));

        await using var read = NewDbContext();
        Assert.False(await read.Classifications
            .Where(c => c.Name == SyntheticRoster.StudentCategory)
            .Select(c => c.IsActive)
            .SingleAsync());

        Assert.Contains(await RowsOfAsync(batch.Id), r =>
            r.WarningCode == SisImportWarningCode.ClassificationUnavailable);
    }

    // ====================================================== import versus the back office (precedence)

    /// <summary>
    /// <b>A classification set in the back office survives a later import, and the disagreement is
    /// reported rather than absorbed.</b>
    ///
    /// <para>
    /// <b>Nothing on the junction row records which writer put it there</b>, so "overwrite unless a
    /// human set it" is not a rule this schema can express. Of the two it can, import-wins silently
    /// reverts every manual correction on every run — which makes
    /// <c>PUT /students/{id}/classifications/{id}</c> a formality — while first-write-wins leaves a
    /// genuine source correction unapplied. The second cost is real, which is why it is a warning
    /// naming both values and not silence.
    /// </para>
    ///
    /// <para>
    /// <b>Flagged for JJ rather than assumed:</b> this is the behaviour implemented, not a ruling. If
    /// the roster should win instead, this test is where that decision changes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_classification_assigned_by_hand_survives_the_next_import()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        Guid studentId;
        Guid acadId;

        await using (var db = NewDbContext())
        {
            studentId = await db.Students
                .Where(s => s.StudentNumber == SyntheticRoster.PedroRegNo)
                .Select(s => s.Id).SingleAsync();

            acadId = await db.Classifications
                .Where(c => c.Name == "ACAD").Select(c => c.Id).SingleAsync();

            // Through the service, not by writing the row: a fixture that minted the assignment itself
            // could not prove the two writers agree about the axis slot they share.
            var response = await StudentClassificationsOn(db).AssignAsync(studentId, acadId);
            Assert.Equal(StudentClassificationWriteOutcome.Saved, response.Outcome);
        }

        var second = await ImportAsync(world.TermId);

        // The file still says NAP. The back office says ACAD. ACAD stands.
        Assert.Equal(["ACAD"], await CategoriesOfAsync(SyntheticRoster.PedroRegNo));

        await using var read = NewDbContext();
        Assert.Equal(acadId, await read.StudentClassifications
            .Where(a => a.StudentId == studentId)
            .Select(a => a.ClassificationId)
            .SingleAsync());

        var row = Row(await RowsOfAsync(second.Id), 8);
        Assert.Equal(SisImportWarningCode.ClassificationConflict, row.WarningCode);
        Assert.Contains(SyntheticRoster.PersonnelCategory, row.WarningMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The same disagreement is announced once and then stays quiet.</b> JJ's ruling: warn the first
    /// time a file-vs-held disagreement appears, stay silent while it is unchanged.
    ///
    /// <para>
    /// <b>Why silence is the correct behaviour and not a swallowed warning.</b> An import never
    /// overwrites a hand-set classification, so the file and the record disagree for as long as the
    /// source is not fixed. A warning with no memory therefore re-announces it on every run, for every
    /// corrected person, for ever — and after a few hundred corrections
    /// <c>CompletedWithWarnings</c> is the permanent status of every import, with the genuine warnings
    /// buried inside the pile. That is the failure <c>ParseRows</c> refuses for a missing RFID column.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing is lost by going quiet.</b> The record still wins, the person still keeps ACAD, and
    /// the disagreement is still visible on the assignment row itself — asserted below, so the test
    /// distinguishes "reported once and remembered" from "stopped noticing".
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_same_disagreement_is_reported_once_and_then_stays_quiet()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        var studentId = await ReassignPedroAsync("ACAD");

        // Import 2 — the disagreement is new, so it is announced.
        var announced = await ImportAsync(world.TermId);
        Assert.Equal(SisImportWarningCode.ClassificationConflict,
            Row(await RowsOfAsync(announced.Id), 8).WarningCode);

        // Import 3 — the file still says NAP and the record still says ACAD. Already reported.
        var quiet = await ImportAsync(world.TermId);
        var row = Row(await RowsOfAsync(quiet.Id), 8);

        Assert.NotEqual(SisImportWarningCode.ClassificationConflict, row.WarningCode);
        Assert.Equal(SisImportRowResult.Skipped, row.Result);

        // Still ACAD, still disagreeing — silence is memory, not surrender.
        Assert.Equal(["ACAD"], await CategoriesOfAsync(SyntheticRoster.PedroRegNo));

        await using var read = NewDbContext();
        Assert.Equal(SyntheticRoster.PersonnelCategory, await read.StudentClassifications
            .Where(a => a.StudentId == studentId)
            .Select(a => a.ReportedRosterValue)
            .SingleAsync());

        // And the batch is no noisier than an ordinary one.
        Assert.Equal(ExpectedFixtureWarningRows, quiet.WarningRows);
    }

    /// <summary>
    /// <b>A disagreement that <em>changes</em> is announced again.</b> The record has been told the file
    /// says <c>NAP</c>; the file now says <c>ANT</c>, which is something nobody has seen. This is the
    /// distinction a boolean could not carry and the reason the column stores the reported value.
    /// </summary>
    [Fact]
    public async Task A_disagreement_that_changes_is_reported_again()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);
        await ReassignPedroAsync("ACAD");

        await ImportAsync(world.TermId);                       // announced
        var quiet = await ImportAsync(world.TermId);           // remembered
        Assert.NotEqual(SisImportWarningCode.ClassificationConflict,
            Row(await RowsOfAsync(quiet.Id), 8).WarningCode);

        // The source now says something else about the same person.
        await using var changed = BuiltWith(rows =>
            SetCell(rows, 6, SisRosterColumns.PersonnelCategory, "ANT"));

        var reannounced = await ImportAsync(world.TermId, changed);
        var row = Row(await RowsOfAsync(reannounced.Id), 8);

        Assert.Equal(SisImportWarningCode.ClassificationConflict, row.WarningCode);

        // Both halves of the disagreement, by name: the file now says ANT and the record says ACAD.
        // Asserting only the file's value is what let the held side ship as a raw GUID.
        Assert.Contains("'ANT'", row.WarningMessage!, StringComparison.Ordinal);
        Assert.Contains("'ACAD'", row.WarningMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("ClassificationId", row.WarningMessage!, StringComparison.Ordinal);

        // Still not applied — only the announcing changed, never first-write-wins.
        Assert.Equal(["ACAD"], await CategoriesOfAsync(SyntheticRoster.PedroRegNo));
    }

    /// <summary>
    /// <b>Re-classifying somebody by hand resets the memory</b>, so the next import tells them the file
    /// still disagrees. Carrying the reported value across a reassignment would suppress exactly the
    /// announcement an administrator who has just changed their mind needs to hear.
    /// </summary>
    [Fact]
    public async Task Reassigning_by_hand_makes_the_next_disagreement_news_again()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await ReassignPedroAsync("ACAD");
        await ImportAsync(world.TermId);                       // announced, remembered

        var quiet = await ImportAsync(world.TermId);
        Assert.NotEqual(SisImportWarningCode.ClassificationConflict,
            Row(await RowsOfAsync(quiet.Id), 8).WarningCode);

        // A different hand correction — the assignment is now a different one, and nothing has been
        // reported about it.
        await ReassignPedroAsync("ANT");

        var afterReassignment = await ImportAsync(world.TermId);
        Assert.Equal(SisImportWarningCode.ClassificationConflict,
            Row(await RowsOfAsync(afterReassignment.Id), 8).WarningCode);
    }

    /// <summary>
    /// <b>Merging the held category into another one also makes the next disagreement news again.</b>
    ///
    /// <para>
    /// <b>The merge is the fifth writer of the junction table and the one that was missed.</b> It
    /// re-points a person onto the survivor by exactly the measure
    /// <c>StudentClassificationService.ReplaceAsync</c> uses — a different <c>ClassificationId</c> on
    /// the same row — so <see cref="StudentClassification.ReportedRosterValue"/>'s rule applies to it
    /// verbatim: reset whenever the assignment itself changes.
    /// </para>
    ///
    /// <para>
    /// <b>Every step here is one the product invites</b>, which is what makes it worth a test. The
    /// roster says <c>NAP</c>; an administrator puts Pedro on <c>ACAD</c>; the import reports the
    /// disagreement and remembers <c>"NAP"</c>. The administrator then merges <c>ACAD</c> into
    /// <c>ANT</c> — same axis, legal, and the remedy <c>ClassificationUnavailable</c>'s own message
    /// recommends. Pedro now holds <c>ANT</c> while the file still says <c>NAP</c>, and nobody has ever
    /// been told <em>that</em>. Without the reset the stale <c>"NAP"</c> matches the file and the
    /// warn-once check suppresses the one announcement that matters: the record stays correct and the
    /// report goes quiet, which is the failure this round's ruling exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Merging_the_held_category_makes_the_next_disagreement_news_again()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        var studentId = await ReassignPedroAsync("ACAD");
        await ImportAsync(world.TermId);                       // announced, remembers "NAP"

        var quiet = await ImportAsync(world.TermId);
        Assert.NotEqual(SisImportWarningCode.ClassificationConflict,
            Row(await RowsOfAsync(quiet.Id), 8).WarningCode);

        // ACAD is merged into ANT — same axis, and through the service rather than by writing rows.
        await using (var db = NewDbContext())
        {
            var acadId = await db.Classifications
                .Where(c => c.Name == "ACAD").Select(c => c.Id).SingleAsync();
            var antId = await db.Classifications
                .Where(c => c.Name == "ANT").Select(c => c.Id).SingleAsync();

            var merge = await ClassificationsOn(db).MergeAsync(acadId, antId);
            Assert.Equal(ClassificationWriteOutcome.Saved, merge.Outcome);
        }

        // Pedro moved with his category, which is what a merge is for.
        Assert.Equal(["ANT"], await CategoriesOfAsync(SyntheticRoster.PedroRegNo));

        await using (var read = NewDbContext())
        {
            Assert.Null(await read.StudentClassifications
                .Where(a => a.StudentId == studentId)
                .Select(a => a.ReportedRosterValue)
                .SingleAsync());
        }

        // NAP-versus-ANT is a disagreement nobody has been told about, so it is announced.
        var reannounced = await ImportAsync(world.TermId);
        var row = Row(await RowsOfAsync(reannounced.Id), 8);

        Assert.Equal(SisImportWarningCode.ClassificationConflict, row.WarningCode);

        // And it names BOTH sides — the file's value and the category actually held. A message that
        // quoted a GUID for the held side would leave an operator a lookup rather than an answer.
        Assert.Contains($"'{SyntheticRoster.PersonnelCategory}'", row.WarningMessage!,
            StringComparison.Ordinal);
        Assert.Contains("'ANT'", row.WarningMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("ClassificationId", row.WarningMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Puts <see cref="SyntheticRoster.PedroRegNo"/> on <paramref name="classificationName"/> through
    /// <see cref="IStudentClassificationService"/> rather than by writing the row — so a fixture cannot
    /// mint an assignment by a rule the production path does not use, which is the only way these tests
    /// can prove the two writers agree about the axis slot they share.
    /// </summary>
    private async Task<Guid> ReassignPedroAsync(string classificationName)
    {
        await using var db = NewDbContext();

        var studentId = await db.Students
            .Where(s => s.StudentNumber == SyntheticRoster.PedroRegNo)
            .Select(s => s.Id).SingleAsync();

        var classificationId = await db.Classifications
            .Where(c => c.Name == classificationName).Select(c => c.Id).SingleAsync();

        var response = await StudentClassificationsOn(db).AssignAsync(studentId, classificationId);
        Assert.Equal(StudentClassificationWriteOutcome.Saved, response.Outcome);

        return studentId;
    }

    // ============================================================= ADR-001 D-4: the profile decides

    /// <summary>
    /// The built-in profile records a source column for every axis, at
    /// <see cref="SisImportProfileTemplate.BuiltInVersion"/> — and the previous version is superseded
    /// rather than deleted, so the batches that ran under it are still explained by it.
    /// </summary>
    [Fact]
    public async Task The_built_in_profile_maps_one_source_column_per_axis()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var profile = await read.SisImportProfiles.IgnoreQueryFilters()
            .SingleAsync(p => p.SchoolId == world.SchoolId && p.IsActive);

        Assert.Equal(SisImportProfileTemplate.BuiltInVersion, profile.Version);

        var targets = await read.SisImportProfileColumns.IgnoreQueryFilters()
            .Where(c => c.ProfileId == profile.Id
                     && c.TargetField.StartsWith(SisImportProfileTemplate.ClassificationTargetPrefix))
            .Select(c => c.TargetField)
            .ToListAsync();

        Assert.Equal(
            ClassificationAxis.All.Select(SisImportProfileTemplate.ClassificationTargetFor)
                .Order(StringComparer.Ordinal).ToList(),
            targets.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// <b>A batch pinned to the previous profile version classifies nobody, even from a file that
    /// carries all four columns.</b>
    ///
    /// <para>
    /// <b>This is ADR-001 D-4 doing the one thing it was written to do</b>, and it is the first real
    /// exercise of the versioning path since the 2026-07-30 RFID correction created it: a batch executes
    /// the rules it was uploaded under. A roster imported before Task 5 stays explained by pre-Task-5
    /// rules; re-importing the file is what brings it under the new ones. If this went red, D-4 would be
    /// a table nothing reads rather than a mechanism — which is exactly the state its own remarks say it
    /// exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_batch_pinned_to_the_pre_Task_5_profile_version_classifies_nobody()
    {
        var world = await ArrangeAsync();

        Guid batchId;
        await using (var db = NewDbContext())
        {
            await using var content = SyntheticRoster.Build();
            var preview = await SisImportOn(db).UploadAsync(
                new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", world.TermId));
            batchId = preview.Batch.Id;
        }

        // A version 2 profile, mapping the RFID column and no category column — which is what every
        // profile authored before Task 5 looks like — and the batch repointed at it, which is what a
        // re-run of an August batch is.
        await using (var db = NewDbContext())
        {
            var legacy = new SisImportProfile
            {
                SchoolId = world.SchoolId,
                Name = SisImportProfileTemplate.ProfileName,
                NameKey = AcademicKey.NormalizeOrUnspecified(SisImportProfileTemplate.ProfileName),
                Version = SisImportProfileTemplate.BuiltInVersion - 1,
                Source = SisImportSource.Excel,
                // Superseded, exactly as EnsureBuiltInProfileAsync leaves it — two active versions is
                // not a shape UX_SisImportProfiles_School_Name_Active permits.
                IsActive = false,
                Description = "Version 2, kept to explain the batches that ran under it.",
            };
            db.SisImportProfiles.Add(legacy);
            db.SisImportProfileColumns.Add(new SisImportProfileColumn
            {
                Profile = legacy,
                SourceColumn = SisRosterColumns.RfidCardSerial,
                SourceColumnKey = SisRosterColumns.HeaderKey(SisRosterColumns.RfidCardSerial),
                TargetField = SisImportProfileTemplate.RfidCardUidTarget,
                NormalizationRule = "CardUid.Normalize",
                IsRequired = false,
                Ordinal = 0,
            });

            var batch = await db.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == batchId);
            batch.ImportProfileId = legacy.Id;
            await db.SaveChangesAsync();
        }

        await using (var db = NewDbContext())
        {
            await SisImportOn(db).RunAsync(batchId, world.TermId);
        }

        await using var read = NewDbContext();

        Assert.Equal(0, await read.StudentClassifications.CountAsync());

        // And not silently: the columns were never read, so there is nothing to warn about either —
        // the same silence a file with no category columns gets, for the same reason.
        var rows = await RowsOfAsync(batchId);
        Assert.DoesNotContain(rows, r =>
            r.WarningCode is SisImportWarningCode.ClassificationMissing
                or SisImportWarningCode.ClassificationUnavailable);

        // Not vacuous: the rest of the batch imported exactly as always, so this is a statement about
        // the category mapping rather than about a batch that did nothing.
        Assert.Equal(8, await read.Students.CountAsync());
        Assert.Equal(8, await read.RfidCards.CountAsync());
    }
}
