using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// A category value typed in ANOTHER group's column — <c>NAP</c> under <c>STUDENT_CATEGORY</c>.
///
/// <para>
/// <b>This used to take the whole batch down.</b> <c>ResolveClassificationsAsync</c> checked what a person
/// already held by the column's axis but inserted on the value's real axis, so any second Personnel value
/// — in the same row, already held, or left behind by the previous import of the same file — violated
/// <c>UX_StudentClassifications_Student_Axis</c> and rolled back the fact pass with no row number. Every
/// test here goes through upload and run on real SQL Server, because the unique index is the failure.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportClassificationWrongAxisTests : IntegrationTest
{
    public SisImportClassificationWrongAxisTests(SqlServerFixture sql) : base(sql) { }

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

    /// <summary>The synthetic roster with every one of Maria's rows given these category cells.</summary>
    private static MemoryStream RosterWithMaria(
        string student = "", string personnel = "", string friars = "", string special = "")
    {
        var rows = SyntheticRoster.Rows();
        var regNo = SyntheticRoster.ColumnIndex(SisRosterColumns.RegNo);
        foreach (var row in rows.Where(r => r[regNo] == SyntheticRoster.MariaRegNo))
        {
            row[SyntheticRoster.ColumnIndex(SisRosterColumns.StudentCategory)] = student;
            row[SyntheticRoster.ColumnIndex(SisRosterColumns.PersonnelCategory)] = personnel;
            row[SyntheticRoster.ColumnIndex(SisRosterColumns.FriarsCategory)] = friars;
            row[SyntheticRoster.ColumnIndex(SisRosterColumns.SpecialCategory)] = special;
        }

        return SyntheticRoster.Build(rows);
    }

    private async Task<SisImportBatchDto> ImportAsync(Guid termId, Stream file)
    {
        await using (file)
        {
            file.Position = 0;
            await using var db = NewDbContext();
            var service = SisImportOn(db);
            var preview = await service.UploadAsync(new SisImportUploadRequest(file, "Copy-of-CCJ.xlsx", termId));
            return await service.RunAsync(preview.Batch.Id, termId);
        }
    }

    private async Task<List<string>> MariaHoldsAsync()
    {
        await using var db = NewDbContext();
        var student = await db.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        return await db.StudentClassifications
            .Where(a => a.StudentId == student.Id)
            .OrderBy(a => a.Axis)
            .Select(a => a.Axis + ":" + a.Classification!.Name)
            .ToListAsync();
    }

    private async Task<List<SisImportRowDto>> MariaRowsAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        var rows = await SisImportOn(db).GetRowsAsync(batchId, result: null);
        return [.. rows.Where(r => r.RawData!.Contains(SyntheticRoster.MariaRegNo, StringComparison.Ordinal))];
    }

    private static void AssertFinished(SisImportBatchDto batch) =>
        Assert.Contains(batch.Status, new[] { SisImportStatus.Completed, SisImportStatus.CompletedWithWarnings });

    [Fact]
    public async Task A_value_in_the_wrong_category_column_is_warned_and_not_applied()
    {
        var world = await ArrangeAsync();

        var batch = await ImportAsync(world.TermId, RosterWithMaria(student: "NAP"));

        AssertFinished(batch);
        Assert.Empty(await MariaHoldsAsync());

        var rows = await MariaRowsAsync(batch.Id);
        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            Assert.Equal(SisImportWarningCode.ClassificationWrongAxis, r.WarningCode);
            Assert.Contains(
                $"'NAP' in the {SisRosterColumns.StudentCategory} column", r.WarningMessage, StringComparison.Ordinal);
            Assert.Contains(
                $"belongs in the {SisRosterColumns.PersonnelCategory} column", r.WarningMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Two_values_that_both_belong_to_one_axis_do_not_fail_the_import()
    {
        var world = await ArrangeAsync();

        var batch = await ImportAsync(world.TermId, RosterWithMaria(student: "NAP", personnel: "ACAD"));

        AssertFinished(batch);
        Assert.Equal(0, batch.FailedRows);
        // The correctly placed value lands; the misplaced one is refused rather than competing with it.
        Assert.Equal([$"{ClassificationAxis.Personnel}:ACAD"], await MariaHoldsAsync());
    }

    /// <summary>
    /// The nastiest of the three: the FIRST import of such a file succeeded (filing NAP as Personnel), so
    /// the failure surfaced on the second import of the same, unchanged file.
    /// </summary>
    [Fact]
    public async Task Re_importing_a_file_with_a_wrong_column_value_does_not_crash()
    {
        var world = await ArrangeAsync();

        var first = await ImportAsync(world.TermId, RosterWithMaria(student: "NAP"));
        var second = await ImportAsync(world.TermId, RosterWithMaria(student: "NAP"));

        AssertFinished(first);
        AssertFinished(second);
        Assert.Empty(await MariaHoldsAsync());
    }

    [Fact]
    public async Task A_wrong_column_value_for_a_person_already_classified_does_not_crash()
    {
        var world = await ArrangeAsync();

        AssertFinished(await ImportAsync(world.TermId, RosterWithMaria(personnel: "ACAD")));
        Assert.Equal([$"{ClassificationAxis.Personnel}:ACAD"], await MariaHoldsAsync());

        var batch = await ImportAsync(world.TermId, RosterWithMaria(student: "NAP"));

        AssertFinished(batch);
        Assert.Equal([$"{ClassificationAxis.Personnel}:ACAD"], await MariaHoldsAsync());
        Assert.Contains(await MariaRowsAsync(batch.Id), r => r.WarningCode == SisImportWarningCode.ClassificationWrongAxis);
    }
}
