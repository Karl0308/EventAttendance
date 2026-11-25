using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// JJ, 2026-11-25, option B: a roster row whose REGNO belongs to a soft-deleted student (for example one
/// moved to Personnel by <c>POST /personnel/migrate-from-students</c>) is skipped whole with
/// <see cref="SisImportWarningCode.StudentRemoved"/>: no field update, no enrollment or term record, no
/// card. The student stays deleted and the rest of the roster imports as before.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportRemovedStudentTests : IntegrationTest
{
    public SisImportRemovedStudentTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>Maria's rows in <see cref="SyntheticRoster"/>.</summary>
    private static readonly int[] MariaRows = [2, 3, 4, 5];

    private const string OldFirstName = "Removed";
    private const string OldCardUid = "0099887766";

    private sealed record World(Guid SchoolId, Guid TermId, Guid MariaId);

    /// <summary>
    /// The synthetic roster's world, with Maria already a soft-deleted student whose card was deactivated
    /// and moved to a personnel record — the state the migration leaves behind.
    /// </summary>
    private async Task<World> ArrangeAsync(bool rosterListsTheSameCard)
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        db.Classifications.AddRange(TestData.ClassificationVocabulary(school.Id));

        var maria = TestData.NewStudent(school.Id, SyntheticRoster.MariaRegNo, firstName: OldFirstName);
        maria.IsDeleted = true;
        db.Students.Add(maria);

        var movedUid = rosterListsTheSameCard ? SyntheticRoster.MariaRfid : OldCardUid;
        db.RfidCards.Add(TestData.NewCard(school.Id, maria.Id, movedUid, isActive: false));
        db.Personnel.Add(TestData.NewPersonnel(school.Id, SyntheticRoster.MariaRegNo, rfidUid: movedUid));

        await db.SaveChangesAsync();
        await CourseMappingProfile.SeedCourseMappingProfileAsync(db, school.Id);
        return new World(school.Id, term.Id, maria.Id);
    }

    private async Task<(SisImportBatchDto Batch, IReadOnlyList<SisImportRowDto> Rows)> ImportAsync(World world)
    {
        Guid batchId;
        await using (var content = SyntheticRoster.Build())
        await using (var db = NewDbContext())
        {
            var preview = await SisImportOn(db).UploadAsync(
                new SisImportUploadRequest(content, "removed-student.xlsx", world.TermId));
            batchId = preview.Batch.Id;
        }

        SisImportBatchDto batch;
        await using (var db = NewDbContext())
            batch = await SisImportOn(db).RunAsync(batchId, world.TermId);

        await using var read = NewDbContext();
        return (batch, await SisImportOn(read).GetRowsAsync(batchId, result: null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SisImport_skips_rows_of_a_removed_student_and_still_imports_the_rest(bool rosterListsTheSameCard)
    {
        var world = await ArrangeAsync(rosterListsTheSameCard);

        var (batch, rows) = await ImportAsync(world);

        Assert.NotEqual(SisImportStatus.Failed, batch.Status);

        foreach (var rowNumber in MariaRows)
        {
            var row = rows.Single(r => r.RowNumber == rowNumber);
            Assert.Equal(SisImportRowResult.Skipped, row.Result);
            Assert.Equal(SisImportWarningCode.StudentRemoved, row.WarningCode);
            Assert.Equal(SisImportSkipReason.StudentRemoved, row.SkipReason);
            Assert.Contains($"Student '{SyntheticRoster.MariaRegNo}' was removed", row.WarningMessage!, StringComparison.Ordinal);
            Assert.Empty(row.Entities);
        }

        await using var db = NewDbContext();
        var maria = await db.Students.IgnoreQueryFilters().SingleAsync(s => s.Id == world.MariaId);
        Assert.True(maria.IsDeleted);
        Assert.Equal(OldFirstName, maria.FirstName);
        Assert.Null(maria.LastSyncedAt);

        Assert.Equal(0, await db.RfidCards.IgnoreQueryFilters().CountAsync(c => c.StudentId == world.MariaId && c.IsActive));
        Assert.Equal(0, await db.RfidCards.IgnoreQueryFilters().CountAsync(c => c.CardUid == SyntheticRoster.MariaRfid && c.IsActive));
        Assert.Equal(0, await db.Enrollments.IgnoreQueryFilters().CountAsync(e => e.StudentId == world.MariaId));
        Assert.Equal(0, await db.StudentTermRecords.IgnoreQueryFilters().CountAsync(r => r.StudentId == world.MariaId));

        // The live students still import, with their cards.
        var juan = await db.Students.IgnoreQueryFilters().SingleAsync(s => s.StudentNumber == SyntheticRoster.JuanRegNo);
        Assert.False(juan.IsDeleted);
        Assert.Equal(1, await db.RfidCards.CountAsync(c => c.StudentId == juan.Id && c.IsActive && c.CardUid == SyntheticRoster.JuanRfid));
        Assert.True(await db.Enrollments.IgnoreQueryFilters().AnyAsync(e => e.StudentId == juan.Id));
        Assert.DoesNotContain(rows, r => !MariaRows.Contains(r.RowNumber) && r.WarningCode == SisImportWarningCode.StudentRemoved);
    }
}
