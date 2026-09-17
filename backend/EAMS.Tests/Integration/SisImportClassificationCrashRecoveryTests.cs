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
/// <b>Second pair of eyes on the D-54.5 recovery / classification-memory interaction (MEDIUM
/// confidence report).</b> <c>StudentClassification.ReportedRosterValue</c> is written inside the
/// fact pass (<c>SisImportService.ResolveClassificationsAsync</c>, via <c>ResolveFactsAsync</c>) and
/// flushed by the fact-pass <c>SaveChangesAsync</c> — <b>before</b> the fan-out that actually records
/// the <c>ClassificationConflict</c> warning on the row is written, in its own later, separately
/// chunked saves (<c>RowLedger.ApplyInChunks</c>).
///
/// <para>
/// <b>The reported chain, and why it holds.</b> A process death in that window — after the fact pass
/// commits, before the fan-out commits — leaves <c>ReportedRosterValue</c> durably set to the file's
/// value while <em>no warning about it was ever written anywhere</em>. ADR-004 D-54.5's sweep then
/// marks the orphaned batch <c>Failed</c>; a retry re-runs <c>ExecuteAsync</c> from scratch
/// (<c>SisImportService.RunAsync</c> allows re-claiming a <c>Failed</c> batch), which deletes and
/// rebuilds only <c>SisImportRowEntities</c> for the batch's own rows — <b>not</b> the
/// <c>StudentClassifications</c> table. So the retry's fact pass reads back the pre-crash
/// <c>ReportedRosterValue</c>, the warn-once comparison at <c>SisImportService.cs</c>'s
/// <c>reportedKey == key</c> check matches, and the disagreement is never announced at all — not
/// "reported once, then quiet" (the documented and tested D-54.5-unrelated behaviour), but
/// <b>reported zero times, forever quiet</b>. The code comment defending the split saves
/// ("the inconsistency is cosmetic... only visible on a batch that already says Failed") is talking
/// about <c>SisImportRow.Result/WarningCode/...</c>, which the retry's row delete-and-rebuild DOES
/// erase and redo unconditionally; it says nothing about <c>StudentClassification.ReportedRosterValue</c>,
/// which lives on a different table the retry never touches. That asymmetry is the defect.
/// </para>
///
/// <para>
/// <b>How this test stands in for an actual crash.</b> Forcibly killing the process between the two
/// <c>SaveChangesAsync</c> calls is not practical from an xUnit test. What is reproduced instead is
/// the exact durable state such a crash leaves behind: <c>ReportedRosterValue</c> already equal to the
/// file's value while the batch has never completed a run that showed the corresponding warning. That
/// state is written directly against the database, deliberately bypassing
/// <see cref="ISisImportService"/> and <see cref="IStudentClassificationService"/> (the only two
/// production writers of that column) — because producing it through them would require the very
/// warning-then-crash sequence this test cannot simulate.
/// </para>
///
/// <para>
/// <b>RE-POINTED (JJ's ruling).</b> The recovery property this class was originally written to assert
/// — "a disagreement whose only trace is an orphaned <c>ReportedRosterValue</c> is still announced" —
/// is not testable and not just hard to test. <c>StudentClassification</c> carries no second durable
/// signal (no timestamp bumped, no row anywhere else) that distinguishes "this disagreement's warning
/// was shown once" from "this disagreement's memory was written and the process died before the
/// warning could be" — the two states are byte-identical on disk. Recovering from that ambiguity would
/// require a second signal this schema does not have. It is also, as far as this codebase is concerned,
/// moot rather than merely inconvenient: the deferral fix below means no run that has ever executed on
/// <c>main</c> could leave that orphaned state behind in the first place, so there is no live database
/// anywhere carrying it to recover. What <em>is</em> tested instead, below, is the property the fix
/// actually delivers — prevention, not recovery: the memory write and the warning it stands for are now
/// forced into the same transaction, so the orphaned state cannot occur on any run going forward.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportClassificationCrashRecoveryTests : IntegrationTest
{
    public SisImportClassificationCrashRecoveryTests(SqlServerFixture sql) : base(sql) { }

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
        using var content = SyntheticRoster.Build();
        await using var db = NewDbContext();
        var service = SisImportOn(db);
        var preview = await service.UploadAsync(
            new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", termId));
        return await service.RunAsync(preview.Batch.Id, termId);
    }

    /// <summary>
    /// The same import, but through an intercepted context so the caller can inspect which
    /// <c>SaveChanges</c> call carried what — the only way to see a transaction boundary a read-back
    /// cannot distinguish. Modeled on <c>SisImportSaveBoundaryTests.RecordRunAsync</c>, the house
    /// precedent for asserting a save split rather than a row's final value.
    /// </summary>
    private async Task<(CapturingSaveInterceptor Saves, Guid BatchId)> RecordImportAsync(
        Guid termId, string expectedStatus)
    {
        var saves = new CapturingSaveInterceptor();
        var options = new DbContextOptionsBuilder<EamsDbContext>()
            .UseSqlServer(Sql.ConnectionString)
            .AddInterceptors(saves)
            .Options;

        using var content = SyntheticRoster.Build();
        await using var db = new EamsDbContext(options, School);
        var service = SisImportOn(db);

        var preview = await service.UploadAsync(
            new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", termId));
        saves.Reset();

        var finished = await service.RunAsync(preview.Batch.Id, termId);
        Assert.Equal(expectedStatus, finished.Status);

        return (saves, preview.Batch.Id);
    }

    private async Task<List<SisImportRowDto>> RowsOfAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return (await SisImportOn(db).GetRowsAsync(batchId, result: null)).ToList();
    }

    private static SisImportRowDto Row(IEnumerable<SisImportRowDto> rows, int rowNumber) =>
        rows.Single(r => r.RowNumber == rowNumber);

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

    /// <summary>
    /// <b>THE PROPERTY THE FIX ACTUALLY DELIVERS, pinned via save boundaries.</b> Pedro's record
    /// disagrees with the file (ACAD vs. the file's NAP) and nobody has been told, so this import is
    /// the "announced" case <c>SisImportClassificationTests.The_same_disagreement_is_reported_once_and_then_stays_quiet</c>
    /// also exercises. What this test adds is <em>where</em> the memory of that announcement is
    /// written: every <c>SaveChanges</c> call that sets <c>StudentClassification.ReportedRosterValue</c>
    /// must be one that <em>also</em> sets the row's <c>WarningCode</c> in the same call — i.e. the same
    /// transaction, per <c>RowLedger.DeferUntilStaged</c>'s contract. A memory written by any save that
    /// does not also carry the warning is exactly the crash window this class exists to close: a
    /// process death between that save and the (never-reached) warning save would again leave the
    /// memory orphaned. This is a stronger, falsifiable stand-in for "no crash window exists" than
    /// reproducing the crash itself, which — per the class remarks — cannot be told apart from the
    /// non-crashed state once it has happened.
    /// </summary>
    [Fact]
    public async Task The_conflict_memory_is_written_in_the_same_save_as_the_rows_warning()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await ReassignPedroAsync("ACAD");

        // The "announced" import: the file (NAP) and the record (ACAD) disagree and nobody has been
        // told yet, so this run writes both the ClassificationConflict warning and the memory that
        // gates its repetition.
        var (saves, batchId) = await RecordImportAsync(world.TermId, SisImportStatus.CompletedWithWarnings);

        var memoryWrites = saves.Saves
            .Where(s => s.PropertiesWrittenTo<StudentClassification>(EntityState.Modified)
                .Contains(nameof(StudentClassification.ReportedRosterValue)))
            .ToList();

        // Sanity: the recording actually saw the write this test is about. A silently-empty list would
        // make the loop below pass vacuously.
        Assert.NotEmpty(memoryWrites);

        // THE ASSERTION. Reverting RowLedger.DeferUntilStaged back to a direct
        // `already.ReportedRosterValue = category.Value;` assignment in the fact pass moves this write
        // into the earlier fact-pass SaveChangesAsync — a save that has not run RowLedger.Stage yet and
        // so cannot carry WarningCode — which fails this exact assertion.
        Assert.All(memoryWrites, save => Assert.Contains(
            nameof(SisImportRow.WarningCode),
            save.PropertiesWrittenTo<SisImportRow>(EntityState.Modified)));

        // And the row-level fact the earlier version of this test asserted, still true: the
        // disagreement is announced.
        var row = Row(await RowsOfAsync(batchId), 8);
        Assert.Equal(SisImportWarningCode.ClassificationConflict, row.WarningCode);
    }

    /// <summary>
    /// <b>Negative control, kept from before the re-point.</b> The plain row-level assertion with no
    /// save-boundary machinery at all: <c>ReportedRosterValue</c> starts <c>null</c>, exactly as
    /// <see cref="IStudentClassificationService.AssignAsync"/> leaves it, and the genuinely-first
    /// disagreement must warn. It still earns its place alongside the save-boundary test above: if the
    /// fixture or the conflict wiring were broken, this simpler test — with no interceptor and no
    /// property-name matching to get subtly wrong — is the one that would say so most plainly.
    /// </summary>
    [Fact]
    public async Task Control_the_same_disagreement_warns_when_memory_was_never_pre_written()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await ReassignPedroAsync("ACAD");

        var announced = await ImportAsync(world.TermId);
        var row = Row(await RowsOfAsync(announced.Id), 8);

        Assert.Equal(SisImportWarningCode.ClassificationConflict, row.WarningCode);
    }
}
