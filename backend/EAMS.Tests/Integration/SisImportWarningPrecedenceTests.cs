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
/// <b>The join-order fix in <c>RowLedger.Stage</c>.</b> Two or more warnings on one row are joined into
/// <c>WarningMessage</c> and clamped to 1,000 characters; the join is now ranked by the same precedence
/// that decides <c>WarningCode</c>, rather than joined in insertion order and clamped afterwards. The
/// invariant this buys, previously false and untested:
/// <c>WarningMessage.StartsWith($"[{WarningCode}] ")</c> for every warned row.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportWarningPrecedenceTests : IntegrationTest
{
    public SisImportWarningPrecedenceTests(SqlServerFixture sql) : base(sql) { }

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

    private async Task<List<SisImportRowDto>> RowsOfAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return (await SisImportOn(db).GetRowsAsync(batchId, result: null)).ToList();
    }

    private static SisImportRowDto Row(IEnumerable<SisImportRowDto> rows, int rowNumber) =>
        rows.Single(r => r.RowNumber == rowNumber);

    private static void SetCell(List<string[]> rows, int rowIndex, string column, string value) =>
        rows[rowIndex][SyntheticRoster.ColumnIndex(column)] = value;

    private static void ClearCategories(List<string[]> rows, int rowIndex)
    {
        foreach (var (_, column) in SisRosterColumns.ClassificationColumns)
            SetCell(rows, rowIndex, column, "");
    }

    /// <summary>
    /// Pins a profile version whose Student axis is mistyped ('Faculty', not one of the four), while
    /// Personnel/Friars/Special stay correctly mapped — the same shape
    /// <c>SisImportClassificationAxisTests.The_other_three_axes_still_classify_correctly_alongside_a_bad_one</c>
    /// uses. This makes <c>ClassificationAxisUnknown</c> fire batch-wide (rank 0 in
    /// <c>RowLedger.WarningPrecedence</c>) while classification resolution still runs for the other
    /// three axes, which is what lets a second, per-row warning coexist with it.
    /// </summary>
    private async Task<Guid> UploadWithMistypedStudentAxisAsync(World world, Stream file)
    {
        await using var db = NewDbContext();
        var preview = await SisImportOn(db).UploadAsync(
            new SisImportUploadRequest(file, "Copy-of-CCJ.xlsx", world.TermId));
        var batchId = preview.Batch.Id;

        await using var write = NewDbContext();
        var profile = new SisImportProfile
        {
            SchoolId = world.SchoolId,
            Name = SisImportProfileTemplate.ProfileName,
            NameKey = AcademicKey.NormalizeOrUnspecified(SisImportProfileTemplate.ProfileName),
            Version = SisImportProfileTemplate.BuiltInVersion + 1,
            Source = SisImportSource.Excel,
            IsActive = false,
            Description = "Test-authored version with a mistyped Student axis.",
        };
        write.SisImportProfiles.Add(profile);

        var targets = new (string Column, string AxisSuffix)[]
        {
            (SisRosterColumns.StudentCategory, "Faculty"),
            (SisRosterColumns.PersonnelCategory, ClassificationAxis.Personnel),
            (SisRosterColumns.FriarsCategory, ClassificationAxis.Friars),
            (SisRosterColumns.SpecialCategory, ClassificationAxis.Special),
        };

        var ordinal = 0;
        foreach (var (column, axisSuffix) in targets)
        {
            write.SisImportProfileColumns.Add(new SisImportProfileColumn
            {
                Profile = profile,
                SourceColumn = column,
                SourceColumnKey = SisRosterColumns.HeaderKey(column),
                TargetField = SisImportProfileTemplate.ClassificationTargetPrefix + axisSuffix,
                NormalizationRule = "RosterClassification.Resolve",
                IsRequired = false,
                Ordinal = ordinal++,
            });
        }

        var batch = await write.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == batchId);
        batch.ImportProfileId = profile.Id;
        await write.SaveChangesAsync();

        return batchId;
    }

    /// <summary>
    /// <b>THE invariant.</b> A row carrying two classification warnings — a batch-wide
    /// <c>ClassificationAxisUnknown</c> (rank 0) and a per-row <c>ClassificationRegNoSuggestsPersonnel</c>
    /// (rank 6) — files under <c>ClassificationAxisUnknown</c>, and <c>WarningMessage</c> starts with
    /// that same code's bracket, whole. Row 12 (Nina) is retargeted to a <c>720000</c>-prefixed REGNO
    /// with every category cell blank, so she names no category on any of the three correctly mapped
    /// axes and her REGNO carries the personnel marker.
    /// </summary>
    [Fact]
    public async Task WarningMessage_starts_with_the_bracketed_code_that_WarningCode_names()
    {
        var world = await ArrangeAsync();

        var rows = SyntheticRoster.Rows();
        ClearCategories(rows, 10);
        SetCell(rows, 10, SisRosterColumns.RegNo, "7200003602");
        await using var file = SyntheticRoster.Build(rows);

        var batchId = await UploadWithMistypedStudentAxisAsync(world, file);
        var batch = await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);

        var rowDtos = await RowsOfAsync(batchId);
        var doubleWarned = Row(rowDtos, 12);

        Assert.Equal(SisImportWarningCode.ClassificationAxisUnknown, doubleWarned.WarningCode);

        var expectedPrefix = $"[{SisImportWarningCode.ClassificationAxisUnknown}] ";
        Assert.True(
            doubleWarned.WarningMessage!.StartsWith(expectedPrefix, StringComparison.Ordinal),
            $"WarningMessage did not start with '{expectedPrefix}'. Ranked once and used for both " +
            "halves, WarningCode and the WarningMessage join must never disagree about which finding " +
            $"is first. Actual: {doubleWarned.WarningMessage}");

        Assert.Contains(
            $"[{SisImportWarningCode.ClassificationRegNoSuggestsPersonnel}]",
            doubleWarned.WarningMessage!, StringComparison.Ordinal);

        // Every other row of the batch still carries the batch-wide axis warning too, and — carrying
        // only one finding each — is unaffected by ranking.
        foreach (var single in rowDtos.Where(r => r.RowNumber != 12))
        {
            Assert.Equal(SisImportWarningCode.ClassificationAxisUnknown, single.WarningCode);
            Assert.StartsWith(
                $"[{SisImportWarningCode.ClassificationAxisUnknown}] ", single.WarningMessage,
                StringComparison.Ordinal);
        }

        Assert.Equal(batch.TotalRows, batch.WarningRows);
    }

    /// <summary>
    /// <b>The measured case that motivated the fix.</b> Two long classification findings on one row
    /// overflow <c>WarningMessage nvarchar(1000)</c>: the axis clause plus the personnel-prefix clause
    /// together exceed the 1,000-character cap. Pinned as measured, not assumed — a change to either
    /// message's wording would move these numbers and should be caught here rather than silently
    /// invalidating the claim.
    /// </summary>
    [Fact]
    public async Task The_overflowing_row_is_clamped_with_the_axis_message_whole_and_the_tail_cut()
    {
        var world = await ArrangeAsync();

        var rows = SyntheticRoster.Rows();
        ClearCategories(rows, 10);
        SetCell(rows, 10, SisRosterColumns.RegNo, "7200003602");
        await using var file = SyntheticRoster.Build(rows);

        var batchId = await UploadWithMistypedStudentAxisAsync(world, file);
        await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);

        var row = Row(await RowsOfAsync(batchId), 12);
        var message = row.WarningMessage!;

        // The clamp fired at all — otherwise the rest of this test proves nothing.
        Assert.Equal(1000, message.Length);
        Assert.EndsWith("…", message, StringComparison.Ordinal);

        // The axis finding is whole: its own closing sentence survives completely, unclamped.
        Assert.Contains(
            "Author a corrected profile version and run the batch again.",
            message, StringComparison.Ordinal);

        // The lower-ranked finding's own text is present at the point the join happens, but is cut
        // before its own closing sentence survives — proving the truncation fell on ITS tail, not on
        // whichever message the passes happened to record first.
        Assert.Contains(
            $"[{SisImportWarningCode.ClassificationRegNoSuggestsPersonnel}] REGNO 7200003602 names no " +
            "category",
            message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "a later import will fill an empty axis and never overwrite what is set.",
            message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A single-warning row is unchanged by the ranked join</b> — there is nothing to rank, so the
    /// message is exactly <c>"[code] message"</c> with no trailing artefact of a join over one element.
    ///
    /// <para>
    /// Row 7 (Ana), not row 12 (Nina): row 12 already carries the fixture's own
    /// <c>SectionSpansPrograms</c> warning (Nina and Omar share one section name across two
    /// programmes), so clearing her categories produces a two-warning row — exactly what the previous
    /// test needs and exactly what this one must NOT use. Row 7 is one of
    /// <c>SyntheticRoster.RowsWithoutWarnings</c>, so clearing its categories is the only warning it
    /// gets.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_single_warning_row_is_unaffected_by_the_ranked_join()
    {
        var world = await ArrangeAsync();

        var rows = SyntheticRoster.Rows();
        ClearCategories(rows, 5);
        await using var file = SyntheticRoster.Build(rows);

        // Default, correctly mapped profile — no ClassificationAxisUnknown anywhere in this batch.
        await using var db = NewDbContext();
        var preview = await SisImportOn(db).UploadAsync(
            new SisImportUploadRequest(file, "Copy-of-CCJ.xlsx", world.TermId));
        var batch = await SisImportOn(NewDbContext()).RunAsync(preview.Batch.Id, world.TermId);

        var row = Row(await RowsOfAsync(batch.Id), 7);

        Assert.Equal(SisImportWarningCode.ClassificationMissing, row.WarningCode);

        var expectedPrefix = $"[{SisImportWarningCode.ClassificationMissing}] ";
        Assert.StartsWith(expectedPrefix, row.WarningMessage, StringComparison.Ordinal);

        Assert.Equal(
            1,
            row.WarningMessage!.Count(c => c == '['));
    }
}
