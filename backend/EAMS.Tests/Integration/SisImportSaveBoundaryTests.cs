using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Where <c>SisImportService.ExecuteAsync</c> cuts its units of work, observed through
/// <see cref="CapturingSaveInterceptor"/>.
///
/// <para>
/// <b>History — this file was a characterization test and has been inverted, not replaced.</b> It
/// originally pinned the shape as it stood: one save carried the whole <c>SisImportRowEntity</c> fan-out
/// <em>and</em> the <c>SisImportBatch</c> update together, which left the batch row X-locked for the
/// length of the longest phase — exactly the interval a progress poller needs to read it, and under
/// READ COMMITTED (no RCSI here) exactly the interval its read would block for. The detached-run work
/// split the fan-out write off and chunked it, so the two assertions that pinned the old shape now
/// assert the opposite: no save carries both, and no save carries more than
/// <see cref="ExpectedFanOutChunkSize"/> fan-out inserts. Deleting them was the wrong edit; a test that
/// passes both before and after a change proves nothing about the change.
/// </para>
///
/// <para>
/// The progress assertion points the other way: it pins an <em>absence</em> that must survive. Progress
/// belongs in <c>ExecuteUpdateAsync</c>, which bypasses the change tracker and therefore never reaches
/// the interceptor at all; the day a progress column shows up in a tracked save is the day somebody
/// mutated the tracked batch instead, and the poll target went back under the run's lock.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportSaveBoundaryTests : IntegrationTest
{
    public SisImportSaveBoundaryTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// The fan-out ceiling one save may carry, stated here <b>independently of the production
    /// constant</b> rather than read from it.
    ///
    /// <para>
    /// <c>SisImportService.FanOutSaveChunkSize</c> is deliberately not exposed to this assembly.
    /// Asserting against the very constant the code under test uses would make the bound unfalsifiable —
    /// it would pass at 5 and at 500,000 alike. Duplicating the number means changing it is a two-place
    /// edit, which is the intended friction: the value is chosen against SQL Server's lock-escalation
    /// threshold and moving it is a decision, not a tidy-up.
    /// </para>
    /// </summary>
    private const int ExpectedFanOutChunkSize = 500;

    /// <summary>
    /// How many fan-out rows one clean roster row produces: College, Program, Course, CourseOffering,
    /// Student, RfidCard, StudentTermRecord, Instructor, CourseOfferingInstructor, Enrollment.
    ///
    /// <para>
    /// Ten regardless of whether those entities were created or merely referenced — an <c>Unchanged</c>
    /// touch is still a touch — which is what makes the fan-out of an <c>n</c>-row clean roster exactly
    /// <c>n * 10</c> and therefore something a chunk-boundary test can check against arithmetic rather
    /// than against whatever the implementation happened to write.
    /// </para>
    /// </summary>
    private const int FanOutRowsPerCleanRow = 10;

    /// <summary>
    /// The §4.12 progress columns, by name rather than by string literal so a rename cannot leave this
    /// test quietly checking for a column that no longer exists.
    /// </summary>
    private static readonly string[] ProgressColumns =
    [
        nameof(SisImportBatch.ProgressPhase),
        nameof(SisImportBatch.ProgressPhaseNumber),
        nameof(SisImportBatch.ProgressPhaseCount),
        nameof(SisImportBatch.ProgressUnitsDone),
        nameof(SisImportBatch.ProgressUnitsTotal),
        nameof(SisImportBatch.ProgressUpdatedAt),
    ];

    private async Task<Guid> ArrangeTermAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        await db.SaveChangesAsync();
        return term.Id;
    }

    /// <summary>
    /// Uploads and runs <paramref name="content"/> through a context whose saves are recorded, and
    /// returns the recording. The upload's own saves are dropped before the run: they are arrange, and
    /// leaving them in would let the batch's own INSERT — which writes every column, progress ones
    /// included — answer questions this file asks about the run.
    /// </summary>
    private async Task<CapturingSaveInterceptor> RecordRunAsync(
        Guid termId, Stream content, string fileName, string expectedStatus)
    {
        var saves = new CapturingSaveInterceptor();

        // The same construction the cancellation test in SisImportPipelineTests uses. There is one way
        // to get an intercepted context in this suite and this is it.
        var options = new DbContextOptionsBuilder<EamsDbContext>()
            .UseSqlServer(Sql.ConnectionString)
            .AddInterceptors(saves)
            .Options;

        await using var db = new EamsDbContext(options, School);
        var service = SisImportOn(db);

        var preview = await service.UploadAsync(new SisImportUploadRequest(content, fileName, termId));

        saves.Reset();

        var finished = await service.RunAsync(preview.Batch.Id, termId);

        // The run has to have actually run. A pipeline that threw early would leave a short, tidy
        // recording that could still satisfy a carelessly written boundary assertion.
        Assert.Equal(expectedStatus, finished.Status);

        return saves;
    }

    /// <summary>
    /// <b>Was the characterization test; inverted by the fan-out split — see the type remarks.</b>
    ///
    /// <para>
    /// Four things are asserted:
    /// <list type="number">
    ///   <item>The double recorded something. A recording test double that silently records nothing
    ///   turns every assertion built on it into a vacuous pass, so this is checked before anything is
    ///   concluded from it.</item>
    ///   <item>No tracked save touches a progress column. Permanent — see the type remarks.</item>
    ///   <item><b>No</b> save carries the fan-out inserts and the batch update together. This is the
    ///   inversion, and it is the assertion the whole change exists to satisfy.</item>
    ///   <item>No save carries more than <see cref="ExpectedFanOutChunkSize"/> fan-out inserts, and the
    ///   chunks still add up to every fan-out row on disk — so the split moved boundaries and lost
    ///   nothing.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_save_carries_both_the_fan_out_and_the_batch_update_and_the_fan_out_is_chunked()
    {
        var termId = await ArrangeTermAsync();

        CapturingSaveInterceptor saves;
        await using (var content = SyntheticRoster.Build())
        {
            saves = await RecordRunAsync(
                termId, content, "save-boundary.xlsx", SisImportStatus.CompletedWithWarnings);
        }

        // ---- 1. the double is not silently empty ------------------------------------------------
        Assert.NotEmpty(saves.Saves);

        var fanOutPerSave = saves.LargestSaveOf<SisImportRowEntity>(EntityState.Added);
        Assert.True(
            fanOutPerSave > 0,
            "The interceptor recorded no SisImportRowEntity inserts at all, so every boundary " +
            $"assertion below would pass vacuously. Saves seen: [{string.Join(" | ", saves.Saves)}]");

        // ---- 2. permanent: progress never rides along on a tracked save --------------------------
        var progressWritten = saves
            .PropertiesWrittenTo<SisImportBatch>(EntityState.Modified)
            .Intersect(ProgressColumns, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            progressWritten.Count == 0,
            "A tracked SaveChanges carried a progress column on SisImportBatch: " +
            $"[{string.Join(", ", progressWritten)}]. Progress is written with ExecuteUpdateAsync, " +
            "which bypasses the change tracker; a tracked write puts the poll target back inside the " +
            "run's own transaction, which is the thing the detached run exists to avoid.");

        // ---- 3. INVERTED: the batch row is no longer written alongside the fan-out ---------------
        Assert.False(
            saves.AnySaveWith<SisImportRowEntity, SisImportBatch>(EntityState.Added, EntityState.Modified),
            "One SaveChanges carried both the SisImportRowEntity fan-out and the SisImportBatch " +
            "update. That save's transaction holds an X lock on the batch row for as long as the " +
            "fan-out takes to write, which is the interval GET /sis/import/{batchId} has to read it " +
            "in. Tally and FinishedAt belong in a save of their own. Saves seen: " +
            $"[{string.Join(" | ", saves.Saves)}]");

        // ---- 4. INVERTED: the fan-out is written in chunks, and the chunks account for all of it --
        Assert.True(
            fanOutPerSave <= ExpectedFanOutChunkSize,
            $"One SaveChanges carried {fanOutPerSave} fan-out inserts, over the " +
            $"{ExpectedFanOutChunkSize} ceiling. Saves seen: [{string.Join(" | ", saves.Saves)}]");

        await using (var read = NewDbContext())
        {
            var total = await read.SisImportRowEntities.IgnoreQueryFilters().CountAsync();

            Assert.True(total > 0, "The run wrote no fan-out rows, so there is nothing to assert about.");

            // Every fan-out row on disk was staged by exactly one recorded save. Chunking is allowed to
            // move the boundaries; it is not allowed to drop a row on one of them.
            var staged = saves.Saves.Sum(s => s.Count<SisImportRowEntity>(EntityState.Added));
            Assert.Equal(total, staged);
        }
    }

    /// <summary>
    /// A roster whose fan-out lands either side of a chunk boundary imports exactly what an unchunked
    /// one would.
    ///
    /// <para>
    /// <b>The sizes are chosen so the fan-out straddles the boundary, not the row count.</b> The chunk
    /// is counted in fan-out rows and a clean roster row produces
    /// <see cref="FanOutRowsPerCleanRow"/> of them, so fifty rows fill a chunk exactly: 499 rows is the
    /// last size that is short of a boundary by a whole row, 500 lands on one, 501 is one row past it,
    /// and 1001 is one past the twentieth. Off-by-one lives at exactly these four places, and none of
    /// them is reachable through <see cref="SyntheticRoster"/>'s twelve-row default — which is why this
    /// test builds its own roster rather than reusing it.
    /// </para>
    ///
    /// <para>
    /// The expectation is arithmetic rather than a recorded golden value: <c>n</c> clean rows insert
    /// <c>n</c> students and produce <c>n * 10</c> fan-out rows whatever the saves were grouped into. A
    /// chunk boundary that dropped, duplicated or reordered a row breaks that product, and the counters
    /// — which <c>Tally</c> derives from the in-memory staged list rather than from the database —
    /// would disagree with the rows actually on disk.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(499)]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(1001)]
    public async Task A_roster_straddling_the_chunk_boundary_imports_what_an_unchunked_one_would(int rows)
    {
        var termId = await ArrangeTermAsync();

        CapturingSaveInterceptor saves;
        await using (var content = SyntheticRoster.Build(CleanRows(rows)))
        {
            saves = await RecordRunAsync(
                termId, content, $"chunk-boundary-{rows}.xlsx", SisImportStatus.Completed);
        }

        var expectedFanOut = rows * FanOutRowsPerCleanRow;

        await using var read = NewDbContext();

        // ---- the counters, which are what an operator reads ---------------------------------------
        var batch = await read.SisImportBatches.IgnoreQueryFilters().SingleAsync();

        Assert.Equal(rows, batch.TotalRows);
        Assert.Equal(rows, batch.InsertedRows);
        Assert.Equal(0, batch.UpdatedRows);
        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(0, batch.SkippedRows);
        Assert.Equal(0, batch.WarningRows);

        // ---- and the rows the counters claim to describe ------------------------------------------
        var results = await read.SisImportRows.IgnoreQueryFilters()
            .GroupBy(r => r.Result)
            .Select(g => new { Result = g.Key, Count = g.Count() })
            .ToListAsync();

        var onlyResult = Assert.Single(results);
        Assert.Equal(SisImportRowResult.Inserted, onlyResult.Result);
        Assert.Equal(rows, onlyResult.Count);

        Assert.Equal(rows, await read.Students.IgnoreQueryFilters().CountAsync());
        Assert.Equal(
            expectedFanOut, await read.SisImportRowEntities.IgnoreQueryFilters().CountAsync());

        // ---- the chunking itself, which no read-back above can see --------------------------------
        var fanOutPerSave = saves.LargestSaveOf<SisImportRowEntity>(EntityState.Added);

        Assert.True(
            fanOutPerSave > 0,
            "The interceptor recorded no fan-out inserts, so the two assertions below are vacuous. " +
            $"Saves seen: [{string.Join(" | ", saves.Saves)}]");

        Assert.True(
            fanOutPerSave <= ExpectedFanOutChunkSize,
            $"At {rows} rows one SaveChanges carried {fanOutPerSave} fan-out inserts, over the " +
            $"{ExpectedFanOutChunkSize} ceiling.");

        Assert.Equal(
            expectedFanOut, saves.Saves.Sum(s => s.Count<SisImportRowEntity>(EntityState.Added)));

        Assert.False(
            saves.AnySaveWith<SisImportRowEntity, SisImportBatch>(EntityState.Added, EntityState.Modified),
            $"At {rows} rows a save still carried the fan-out and the batch update together. Saves " +
            $"seen: [{string.Join(" | ", saves.Saves)}]");
    }

    /// <summary>
    /// <paramref name="count"/> rows that differ only in identity: one REGNO and one card serial each,
    /// every other cell copied from <see cref="SyntheticRoster"/>'s ordinary row.
    ///
    /// <para>
    /// Built from that row rather than composed here so the shape stays the one the pipeline tests
    /// already agree is clean — same college, programme, section, course code and course title, and a
    /// real named teacher — which is what makes every row insert a student and warn about nothing. Vary
    /// any of those and the per-row fan-out stops being a constant and the arithmetic above stops
    /// meaning anything.
    /// </para>
    ///
    /// <para>
    /// The serials are ten digits with no leading zero, distinct per row because
    /// <c>UX_RfidCards_SchoolId_CardUid</c> says so. The REGNOs keep the <c>USA</c> prefix so
    /// <see cref="SyntheticRoster.Build(IReadOnlyList{string[]})"/> writes them as text — an all-digit
    /// REGNO is deliberately written as a number there, and that is a different case with its own test.
    /// </para>
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
