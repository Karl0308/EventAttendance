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
/// <b>The branched remedy in <c>SisImportService.WarnOnUnavailableCategory</c>.</b> A roster that names
/// a merged-away category still warns <c>ClassificationUnavailable</c> — that part is unchanged and
/// deliberate, it warns on every run — but the remedy sentence now names the survivor by name and tells
/// the operator to edit the source rather than to add or reactivate a row that both
/// <c>ClassificationService.SetActiveAsync</c> and the unique index on <c>NameKey</c> would refuse.
///
/// <para>
/// The two negative controls are the point: a plain-retired category and a category absent from the
/// vocabulary altogether must still say "Add or reactivate it" — if they didn't, the branch under test
/// would not be doing anything.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportMergedCategoryWarningTests : IntegrationTest
{
    public SisImportMergedCategoryWarningTests(SqlServerFixture sql) : base(sql) { }

    private sealed record World(Guid SchoolId, Guid TermId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        db.Classifications.AddRange(TestData.ClassificationVocabulary(school.Id));
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    private async Task<SisImportBatchDto> ImportAsync(Guid termId)
    {
        await using var file = SyntheticRoster.Build();
        await using var db = NewDbContext();
        var service = SisImportOn(db);
        var preview = await service.UploadAsync(
            new SisImportUploadRequest(file, "Copy-of-CCJ.xlsx", termId));
        return await service.RunAsync(preview.Batch.Id, termId);
    }

    private async Task<List<SisImportRowDto>> RowsOfAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return (await SisImportOn(db).GetRowsAsync(batchId, result: null)).ToList();
    }

    /// <summary>Pedro (row 8) is the fixture's only NAP-column-name row not already announced by
    /// another test's arrangement, and holds nothing on the Personnel axis before the first import.</summary>
    private static SisImportRowDto PedroRow(IEnumerable<SisImportRowDto> rows) =>
        rows.Single(r => r.RowNumber == 8);

    /// <summary>
    /// <b>THE case.</b> ACAD is merged into ANT, then the fixture roster — which names Pedro under
    /// <c>NAP</c>, not ACAD — is re-pointed so his row names <c>ACAD</c> instead. Importing after the
    /// merge must warn <c>ClassificationUnavailable</c>, name <c>'ANT'</c> by name, tell the operator to
    /// point the export at it, and must NOT tell them to add or reactivate anything (both are refused
    /// downstream, per the branch's own reasoning).
    /// </summary>
    [Fact]
    public async Task A_merged_away_category_is_still_warned_and_now_names_the_survivor()
    {
        var world = await ArrangeAsync();

        Guid acadId, antId;
        await using (var db = NewDbContext())
        {
            acadId = await db.Classifications.Where(c => c.Name == "ACAD").Select(c => c.Id).SingleAsync();
            antId = await db.Classifications.Where(c => c.Name == "ANT").Select(c => c.Id).SingleAsync();

            var merge = await ClassificationsOn(db).MergeAsync(acadId, antId);
            Assert.Equal(ClassificationWriteOutcome.Saved, merge.Outcome);
        }

        // Re-point the roster at the now-merged-away category.
        var rows = SyntheticRoster.Rows();
        rows[6][SyntheticRoster.ColumnIndex(SisRosterColumns.PersonnelCategory)] = "ACAD";
        await using var file = SyntheticRoster.Build(rows);

        await using var db2 = NewDbContext();
        var service = SisImportOn(db2);
        var preview = await service.UploadAsync(
            new SisImportUploadRequest(file, "Copy-of-CCJ.xlsx", world.TermId));
        var batch = await service.RunAsync(preview.Batch.Id, world.TermId);

        var row = PedroRow(await RowsOfAsync(batch.Id));

        Assert.Equal(SisImportWarningCode.ClassificationUnavailable, row.WarningCode);
        Assert.Contains("Point the export at 'ANT'", row.WarningMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("Add or reactivate it", row.WarningMessage!, StringComparison.Ordinal);

        // Not left quoting a GUID either, which the branch's own remarks call out as the earlier defect.
        Assert.DoesNotContain(antId.ToString(), row.WarningMessage!, StringComparison.Ordinal);

        // Pedro imported and holds nothing on the Personnel axis — the person is not silently filed
        // under the survivor by the import; only an explicit merge (or a re-pointed export) moves him.
        await using var read = NewDbContext();
        var pedro = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.PedroRegNo);
        Assert.False(await read.StudentClassifications.AnyAsync(
            a => a.StudentId == pedro.Id && a.Axis == ClassificationAxis.Personnel));
    }

    /// <summary>
    /// <b>Negative control, branch 1: a PLAIN-RETIRED category (never merged) still says "Add or
    /// reactivate it".</b> If this said "Point the export at" too, the merged-away branch would not be
    /// doing anything — every unavailable category would say the same thing regardless of why.
    /// </summary>
    [Fact]
    public async Task A_plain_retired_category_still_says_add_or_reactivate()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var nap = await db.Classifications.SingleAsync(c => c.Name == "NAP");
            nap.IsActive = false;
            nap.RetiredAt = TestData.Now;
            await db.SaveChangesAsync();
        }

        var batch = await ImportAsync(world.TermId);
        var row = PedroRow(await RowsOfAsync(batch.Id));

        Assert.Equal(SisImportWarningCode.ClassificationUnavailable, row.WarningCode);
        Assert.Contains("Add or reactivate it", row.WarningMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("Point the export at", row.WarningMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Negative control, branch 2: a category absent from the vocabulary altogether ("NA") also
    /// still says "Add or reactivate it".</b> <c>found is null</c> here, so there is no survivor to
    /// name and no merged branch to take — the general remedy is the only one that applies.
    /// </summary>
    [Fact]
    public async Task A_category_not_in_the_vocabulary_still_says_add_or_reactivate()
    {
        var world = await ArrangeAsync();

        var rows = SyntheticRoster.Rows();
        rows[10][SyntheticRoster.ColumnIndex(SisRosterColumns.StudentCategory)] = "NA";
        await using var file = SyntheticRoster.Build(rows);

        await using var db = NewDbContext();
        var service = SisImportOn(db);
        var preview = await service.UploadAsync(
            new SisImportUploadRequest(file, "Copy-of-CCJ.xlsx", world.TermId));
        var batch = await service.RunAsync(preview.Batch.Id, world.TermId);

        var row = (await RowsOfAsync(batch.Id)).Single(r => r.RowNumber == 12);

        Assert.Equal(SisImportWarningCode.ClassificationUnavailable, row.WarningCode);
        Assert.Contains("Add or reactivate it", row.WarningMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("Point the export at", row.WarningMessage!, StringComparison.Ordinal);
    }
}
