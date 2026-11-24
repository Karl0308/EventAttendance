using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The roster import at go-live scale (2026-11-24, Phase B: ~70,000 people, target 100,000).
///
/// <para>
/// A local end-to-end run of a 100,000-row roster against a compatibility-110 database found three
/// failures, each invisible at the 12-row fixture size every other import test uses:
/// <list type="number">
///   <item>The retry clean-up's <c>rowIds.Contains(...)</c> inlined 100,000 GUID literals into one
///   DELETE, compiled for 68 seconds and failed with error 8623 — the first statement of every run.
///   It now deletes by batch through a subquery.</item>
///   <item>Every other roster-sized lookup (student numbers, card serials, card holders, existing
///   classifications, the cache refresh) had the same shape. They now go through
///   <see cref="InListChunks"/>.</item>
///   <item>The fan-out's ~1,600 chunked saves each re-scanned the whole change tracker. It now runs
///   with automatic detection off and scans only what it writes.</item>
/// </list>
/// The tests below run at a size that crosses one lookup chunk — enough to exercise the chunk boundary
/// on a real SQL Server without a 100,000-row fixture in the suite. The 100,000-row timings are a local
/// measurement, not a test.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportScaleTests : IntegrationTest
{
    public SisImportScaleTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>One more than a lookup chunk, so every chunked lookup takes two statements.</summary>
    private const int RowsAcrossOneChunk = InListChunks.Size + 1;

    private async Task<Guid> ArrangeTermAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        db.Classifications.AddRange(TestData.ClassificationVocabulary(school.Id));
        await db.SaveChangesAsync();

        // The section-only mapping SisImportSaveBoundaryTests uses: a clean row is one student, one card,
        // one section enrollment, and no warning, so the arithmetic below stays exact.
        await SectionOnlyMappingProfile.SeedSectionOnlyProfileAsync(db, school.Id);
        return term.Id;
    }

    /// <summary>
    /// Every import here runs through a context shaped like production's — retrying, and at compatibility
    /// level 110 — because <see cref="IntegrationTest.NewDbContext()"/> has neither, and without 110 EF sends
    /// every collection <c>Contains</c> as OPENJSON: the very IN lists this class exists to bound never exist.
    /// </summary>
    private readonly InListSizeGuard _inLists = new(InListChunks.Size);

    private EamsDbContext NewImportContext(params IInterceptor[] more) =>
        Sql.NewProductionShapedDbContext(School, [_inLists, .. more]);

    private async Task<(Guid BatchId, string Status)> UploadAndRunAsync(Guid termId, List<string[]> rows)
    {
        await using var db = NewImportContext();
        var service = SisImportOn(db);

        Guid batchId;
        await using (var content = SyntheticRoster.Build(rows))
        {
            batchId = (await service.UploadAsync(new SisImportUploadRequest(content, "scale.xlsx", termId)))
                .Batch.Id;
        }

        var finished = await service.RunAsync(batchId, termId);

        // The fan-out turns automatic change detection off for its saves; a context handed back with it
        // still off would silently stop saving ordinary assignments for whatever used it next.
        Assert.True(db.ChangeTracker.AutoDetectChangesEnabled,
            "The import left AutoDetectChangesEnabled off on the context it ran on.");

        return (batchId, finished.Status);
    }

    [Fact]
    public async Task A_roster_larger_than_one_lookup_chunk_imports_then_re_imports_every_row_exactly_once()
    {
        var termId = await ArrangeTermAsync();
        var rows = CleanRows(RowsAcrossOneChunk);

        var first = await UploadAndRunAsync(termId, rows);
        Assert.Equal(SisImportStatus.Completed, first.Status);

        // The re-import is the case that crosses the chunk boundary with hits on both sides: every
        // student number and every card serial now exists, so a chunk that lost or duplicated a match
        // would insert a second student or card here, or throw on the importer's ToDictionary.
        var second = await UploadAndRunAsync(termId, rows);
        Assert.Equal(SisImportStatus.Completed, second.Status);

        // THE 8623 ASSERTION. One more key than a chunk means a single un-chunked Contains would put
        // InListChunks.Size + 1 literals in one statement. The guard must have seen full chunks (so it is
        // looking at real lists, not at OPENJSON) and nothing past the ceiling.
        Assert.Equal(InListChunks.Size, _inLists.Largest);
        Assert.True(_inLists.Breaches.Count == 0,
            $"Statements exceeded {InListChunks.Size} IN-list values: {string.Join(" | ", _inLists.Breaches)}");

        await using var read = NewDbContext();
        Assert.Equal(RowsAcrossOneChunk, await read.Students.IgnoreQueryFilters().CountAsync());
        Assert.Equal(RowsAcrossOneChunk, await read.RfidCards.IgnoreQueryFilters().CountAsync());

        var results = await read.SisImportRows.IgnoreQueryFilters()
            .Where(r => r.BatchId == first.BatchId || r.BatchId == second.BatchId)
            .GroupBy(r => new { r.BatchId, r.Result })
            .Select(g => new { g.Key.BatchId, g.Key.Result, Count = g.Count() })
            .ToListAsync();

        Assert.Equal(RowsAcrossOneChunk,
            results.Single(r => r.BatchId == first.BatchId && r.Result == SisImportRowResult.Inserted).Count);
        Assert.Equal(RowsAcrossOneChunk,
            results.Single(r => r.BatchId == second.BatchId && r.Result == SisImportRowResult.Skipped).Count);
    }

    /// <summary>
    /// Pins the scope of the retry clean-up, which moved from an id list to a subquery. It must clear this
    /// batch's previous fan-out — or the trail lists every touch twice — and nothing belonging to any
    /// other batch. A subquery that lost its batch predicate would pass the first half and wipe the second.
    /// </summary>
    [Fact]
    public async Task A_retry_replaces_its_own_previous_fan_out_and_leaves_another_batchs_untouched()
    {
        var termId = await ArrangeTermAsync();
        var rows = CleanRows(3);

        var other = await UploadAndRunAsync(termId, rows);
        var retried = await UploadAndRunAsync(termId, rows);

        int otherFanOut, retriedFanOut;
        await using (var read = NewDbContext())
        {
            otherFanOut = await FanOutOfAsync(read, other.BatchId);
            retriedFanOut = await FanOutOfAsync(read, retried.BatchId);
        }

        Assert.True(otherFanOut > 0 && retriedFanOut > 0, "The arrange recorded no fan-out to protect.");

        await using (var arrange = NewDbContext())
        {
            var batch = await arrange.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == retried.BatchId);
            batch.Status = SisImportStatus.Failed;
            await arrange.SaveChangesAsync();
        }

        await using (var run = NewImportContext())
        {
            Assert.Equal(SisImportStatus.Completed, (await SisImportOn(run).RunAsync(retried.BatchId, termId)).Status);
        }

        await using var after = NewDbContext();
        Assert.Equal(retriedFanOut, await FanOutOfAsync(after, retried.BatchId));
        Assert.Equal(otherFanOut, await FanOutOfAsync(after, other.BatchId));
    }

    /// <summary>
    /// The fan-out runs with automatic change detection off, so a row's Result and warning are saved only
    /// because <c>RowLedger</c> scans the row itself. Were that scan lost, the rows would not stay Pending:
    /// the batch save after the fold scans normally and would quietly carry every row's result — the same
    /// final state, written by the wrong save. This pins the boundary rather than the read-back.
    /// </summary>
    [Fact]
    public async Task Every_rows_result_is_saved_by_a_fan_out_chunk_and_none_by_the_batch_save()
    {
        const int rows = 200;
        var termId = await ArrangeTermAsync();
        var saves = new CapturingSaveInterceptor();
        await using var db = NewImportContext(saves);
        var service = SisImportOn(db);

        Guid batchId;
        await using (var content = SyntheticRoster.Build(CleanRows(rows)))
        {
            batchId = (await service.UploadAsync(new SisImportUploadRequest(content, "boundary.xlsx", termId)))
                .Batch.Id;
        }

        saves.Reset();
        Assert.Equal(SisImportStatus.Completed, (await service.RunAsync(batchId, termId)).Status);

        var fanOutSaves = saves.Saves.Where(s => s.Count<SisImportRowEntity>(EntityState.Added) > 0).ToList();
        Assert.True(fanOutSaves.Count > 1, $"The fixture fit one chunk ({fanOutSaves.Count}); it proves nothing about chunks.");
        Assert.Equal(rows, fanOutSaves.Sum(s => s.Count<SisImportRow>(EntityState.Modified)));
        Assert.False(
            saves.AnySaveWith<SisImportBatch, SisImportRow>(EntityState.Modified, EntityState.Modified),
            "Row results rode along on the batch save: the fan-out's own saves did not carry them.");
    }

    private static Task<int> FanOutOfAsync(EamsDbContext db, Guid batchId) =>
        db.SisImportRowEntities.IgnoreQueryFilters()
            .CountAsync(e => db.SisImportRows.IgnoreQueryFilters()
                .Any(r => r.Id == e.SisImportRowId && r.BatchId == batchId));

    /// <summary>
    /// <paramref name="count"/> rows differing only in REGNO and card serial — the clean-row construction
    /// <see cref="SisImportSaveBoundaryTests"/> documents, so each row is one student and one card.
    /// </summary>
    private static List<string[]> CleanRows(int count)
    {
        var template = SyntheticRoster.Rows()[0];
        var regNo = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.RegNo);
        var rfid = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.RfidCardSerial);

        var rows = new List<string[]>(count);
        for (var i = 0; i < count; i++)
        {
            var row = (string[])template.Clone();
            row[regNo] = $"USA{i + 1:D5}";
            row[rfid] = $"{2_000_000_000 + i}";
            rows.Add(row);
        }

        return rows;
    }
}
