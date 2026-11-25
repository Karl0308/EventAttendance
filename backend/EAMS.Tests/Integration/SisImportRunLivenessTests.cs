using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Sis;
using EAMS.Tests.Integration.Infrastructure;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-004 D-54.4 and D-54.5: one running import per school, the startup sweep that fails runs which
/// died with their process, and the progress heartbeat both of them read.
///
/// <para>
/// <see cref="SisImportRunClaimTests"/> covers two claims of the <em>same</em> batch, where a row lock
/// settles the race. This file covers two <em>different</em> batches of one school, where no row lock
/// helps. Each claim's "is anything else running?" check reads the other's row before the other has
/// written it, so without serialization both pass.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportRunLivenessTests : IntegrationTest
{
    public SisImportRunLivenessTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// The race is attempted more than once. Every attempt must admit exactly one run, and three
    /// interleavings are stronger evidence than one.
    /// </summary>
    private const int Attempts = 3;

    /// <summary>
    /// Comfortably past <see cref="SisImportRunLiveness.StaleAfter"/>. It is stated as a margin over the
    /// production value rather than as a literal, so the tests follow the threshold if it moves.
    /// </summary>
    private static readonly TimeSpan WellPastStale = SisImportRunLiveness.StaleAfter + TimeSpan.FromHours(1);

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

        // The same frozen mapping SisImportRunClaimTests runs SyntheticRoster under.
        await CourseMappingProfile.SeedCourseMappingProfileAsync(db, school.Id);
        return new World(school.Id, term.Id);
    }

    private async Task<Guid> StageAsync(Guid termId, string fileName)
    {
        await using var db = NewDbContext();
        await using var content = SyntheticRoster.Build();

        var preview = await SisImportOn(db).UploadAsync(
            new SisImportUploadRequest(content, fileName, termId));

        Assert.Equal(SisImportStatus.Pending, preview.Batch.Status);
        return preview.Batch.Id;
    }

    /// <summary>Puts a batch in the state a run in progress, or a dead one, leaves it in.</summary>
    private async Task PlantAsync(
        Guid batchId, string status, DateTime? progressUpdatedAt, string? phase = SisImportPhase.ResolvingFacts)
    {
        await using var db = NewDbContext();
        var batch = await db.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == batchId);
        batch.Status = status;
        batch.StartedAt = progressUpdatedAt ?? DateTime.UtcNow - WellPastStale;
        batch.ProgressPhase = phase;
        batch.ProgressUpdatedAt = progressUpdatedAt;
        await db.SaveChangesAsync();
    }

    private async Task<SisImportBatch> ReadBatchAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return await db.SisImportBatches.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(b => b.Id == batchId);
    }

    private async Task<SisImportBatchDto> RunAsync(Guid batchId, Guid termId)
    {
        await using var db = NewDbContext();
        return await SisImportOn(db).RunAsync(batchId, termId);
    }

    // ============================================================================ one run per school

    /// <summary>
    /// <b>Two different batches of one school, run at the same instant: exactly one runs, and the
    /// other is refused with the running batch's id and is left <c>Pending</c>.</b>
    ///
    /// <para>
    /// "Admitted" counts every caller that was not refused with <see cref="SisImportException"/>. A
    /// caller that got past the claim and then collided with the other import, as a unique violation or
    /// a deadlock, was still let in. Counting only clean finishes would hide the admission that caused
    /// the collision.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_batches_of_one_school_claimed_at_once_admit_exactly_one_run()
    {
        var world = await ArrangeAsync();

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var first = await StageAsync(world.TermId, $"school-race-{attempt}-a.xlsx");
            var second = await StageAsync(world.TermId, $"school-race-{attempt}-b.xlsx");

            var results = await RaceCapturingAsync(
                () => RunAsync(first, world.TermId),
                () => RunAsync(second, world.TermId));

            var admitted = results.Where(r => r.Error is not SisImportException).ToList();

            Assert.True(
                admitted.Count == 1,
                $"Attempt {attempt}: {admitted.Count} of 2 runs of different batches in one school were " +
                "admitted, expected exactly one. Two imports into one school write the same students, " +
                "cards and enrollments at once. What the admitted callers did:" + Environment.NewLine +
                string.Join(Environment.NewLine, admitted.Select(a => a.Error is null
                    ? $"  ran to completion: {a.Batch!.Status}"
                    : $"  {a.Error.GetType().Name}: {a.Error.Message}")));

            Assert.True(
                admitted[0].Error is null,
                $"The admitted run threw {admitted[0].Error?.GetType().Name}: {admitted[0].Error?.Message}");

            var winnerId = admitted[0].Batch!.Id;
            var loserId = winnerId == first ? second : first;

            var refusal = Assert.IsType<SisImportRunInProgressException>(
                results.Single(r => r.Error is SisImportException).Error);
            Assert.Equal(winnerId, refusal.RunningBatchId);
            Assert.Contains(winnerId.ToString(), refusal.Message, StringComparison.Ordinal);

            // The refused batch was not touched, so it can simply be run once the other finishes.
            var loser = await ReadBatchAsync(loserId);
            Assert.Equal(SisImportStatus.Pending, loser.Status);
            Assert.Null(loser.StartedAt);

            // And it does run now that the school's slot is free.
            var later = await RunAsync(loserId, world.TermId);
            Assert.True(
                later.Status is SisImportStatus.Completed or SisImportStatus.CompletedWithWarnings,
                $"The refused batch, run afterwards, finished {later.Status}.");
        }
    }

    /// <summary>
    /// <b>A batch with a fresh progress stamp holds the slot: the refusal is the typed one, names it,
    /// and leaves the refused batch exactly as it was.</b> This is the deterministic form of the race
    /// above, so a failure here is about the predicate and not about scheduling.
    /// </summary>
    [Fact]
    public async Task A_live_run_in_the_same_school_refuses_another_batch_and_names_itself()
    {
        var world = await ArrangeAsync();
        var running = await StageAsync(world.TermId, "live.xlsx");
        var waiting = await StageAsync(world.TermId, "waiting.xlsx");
        await PlantAsync(running, SisImportStatus.Running, DateTime.UtcNow.AddMinutes(-1));

        var refused = await Assert.ThrowsAsync<SisImportRunInProgressException>(
            () => RunAsync(waiting, world.TermId));

        Assert.Equal(running, refused.RunningBatchId);
        Assert.Contains(running.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.Contains("live.xlsx", refused.Message, StringComparison.Ordinal);
        Assert.Equal(SisImportStatus.Pending, (await ReadBatchAsync(waiting)).Status);
        Assert.Equal(SisImportStatus.Running, (await ReadBatchAsync(running)).Status);
    }

    /// <summary>
    /// <b>A <c>Running</c> batch whose stamp is stale, or which has none, does not hold the slot.</b>
    /// It is presumed dead. Letting it block would lock the school out of imports until a restart's
    /// sweep, which is the zombie problem moved somewhere else.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_dead_run_does_not_block_another_batch(bool stampIsNull)
    {
        var world = await ArrangeAsync();
        var dead = await StageAsync(world.TermId, "dead.xlsx");
        var next = await StageAsync(world.TermId, "next.xlsx");
        await PlantAsync(
            dead, SisImportStatus.Running, stampIsNull ? null : DateTime.UtcNow - WellPastStale);

        var finished = await RunAsync(next, world.TermId);

        Assert.True(
            finished.Status is SisImportStatus.Completed or SisImportStatus.CompletedWithWarnings,
            $"A run beside a dead batch finished {finished.Status}.");
    }

    /// <summary>
    /// <b>The slot is per school.</b> A live run in another school does not block this one. The rule
    /// is about two imports writing the same school's rows, and a global lock would make one tenant
    /// wait on another.
    /// </summary>
    [Fact]
    public async Task A_live_run_in_another_school_does_not_block()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("OTH");
            db.Schools.Add(other);
            var otherTerm = TestData.NewTerm(other.Id);
            db.Terms.Add(otherTerm);
            db.SisImportBatches.Add(new SisImportBatch
            {
                SchoolId = other.Id,
                TermId = otherTerm.Id,
                Source = "XLSX",
                Status = SisImportStatus.Running,
                StartedAt = DateTime.UtcNow,
                ProgressPhase = SisImportPhase.ResolvingFacts,
                ProgressUpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var batchId = await StageAsync(world.TermId, "own-school.xlsx");
        var finished = await RunAsync(batchId, world.TermId);

        Assert.True(
            finished.Status is SisImportStatus.Completed or SisImportStatus.CompletedWithWarnings,
            $"A run beside another school's live run finished {finished.Status}.");
    }

    /// <summary>
    /// <b>A run that has saved its outcome but is still in its last two phases holds the slot.</b>
    /// <c>Tally</c> saves <c>Completed</c> before the student cache and group projection run, and those
    /// still write to the school. With a stale stamp the batch is dead and does not block. A completed
    /// batch from before the heartbeat keeps the claim's first phase and never reaches <c>Done</c>, so
    /// it must not block either.
    /// </summary>
    [Theory]
    [InlineData(SisImportStatus.Completed, SisImportPhase.RefreshingStudentCache, true, true)]
    [InlineData(SisImportStatus.CompletedWithWarnings, SisImportPhase.SyncingStudentGroups, true, true)]
    [InlineData(SisImportStatus.CompletedWithErrors, SisImportPhase.RefreshingStudentCache, true, true)]
    [InlineData(SisImportStatus.Completed, SisImportPhase.RefreshingStudentCache, false, false)]
    [InlineData(SisImportStatus.Completed, SisImportPhase.Done, true, false)]
    [InlineData(SisImportStatus.Completed, SisImportPhase.ClearingPreviousRun, true, false)]
    public async Task An_outcome_saved_run_still_in_its_last_phases_holds_the_slot(
        string status, string phase, bool freshStamp, bool blocks)
    {
        var world = await ArrangeAsync();
        var finishing = await StageAsync(world.TermId, "finishing.xlsx");
        var next = await StageAsync(world.TermId, "next.xlsx");
        await PlantAsync(
            finishing, status, freshStamp ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow - WellPastStale, phase);

        if (blocks)
        {
            var refused = await Assert.ThrowsAsync<SisImportRunInProgressException>(
                () => RunAsync(next, world.TermId));
            Assert.Equal(finishing, refused.RunningBatchId);
            Assert.Equal(SisImportStatus.Pending, (await ReadBatchAsync(next)).Status);
            return;
        }

        var finished = await RunAsync(next, world.TermId);
        Assert.True(
            finished.Status is SisImportStatus.Completed or SisImportStatus.CompletedWithWarnings,
            $"A run beside a {status} batch at {phase} finished {finished.Status}.");
    }

    // ================================================================================ lazy reclaim

    /// <summary>
    /// <b>A batch left <c>Running</c> by a dead run can be run again without a restart</b>, as long as
    /// its stamp is stale or missing. A batch with a live stamp is a run in progress and is still
    /// refused, and refused without being touched.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_dead_running_batch_can_be_run_again_without_a_restart(bool stampIsNull)
    {
        var world = await ArrangeAsync();
        var dead = await StageAsync(world.TermId, "dead-self.xlsx");
        var stamp = stampIsNull ? (DateTime?)null : DateTime.UtcNow - WellPastStale;
        await PlantAsync(dead, SisImportStatus.Running, stamp);

        var finished = await RunAsync(dead, world.TermId);

        Assert.True(
            finished.Status is SisImportStatus.Completed or SisImportStatus.CompletedWithWarnings,
            $"Re-running a dead Running batch finished {finished.Status}.");
        Assert.Equal(SisImportPhase.Done, finished.ProgressPhase);
    }

    [Fact]
    public async Task A_live_running_batch_is_not_reclaimed()
    {
        var world = await ArrangeAsync();
        var live = await StageAsync(world.TermId, "live-self.xlsx");
        var stamp = DateTime.UtcNow.AddMinutes(-1);
        await PlantAsync(live, SisImportStatus.Running, stamp);

        await Assert.ThrowsAsync<SisImportException>(() => RunAsync(live, world.TermId));

        var read = await ReadBatchAsync(live);
        Assert.Equal(SisImportStatus.Running, read.Status);
        Assert.Equal(SisImportPhase.ResolvingFacts, read.ProgressPhase);
    }

    /// <summary>
    /// <b>A run that was reclaimed while it was still alive stops, and writes nothing over the run that
    /// took the batch over.</b> The first run is stopped right after its <c>ParsingRows</c> stamp. Its
    /// stamp is forced stale, and a second run reclaims the batch and runs it to completion. Then the
    /// first run continues. With <c>"stamp"</c>, its next stamp finds the batch is no longer its own and
    /// throws <see cref="SisImportRunSupersededException"/>. With <c>"error"</c>, it fails instead, and
    /// its catch must not mark the second run's batch <c>Failed</c>. Either way, the row keeps the
    /// second claim's <c>StartedAt</c>, status, phase and stamp.
    /// </summary>
    [Theory]
    [InlineData("stamp")]
    [InlineData("error")]
    public async Task A_superseded_run_stops_and_leaves_the_reclaiming_run_alone(string mode)
    {
        var world = await ArrangeAsync();
        var batchId = await StageAsync(world.TermId, "superseded.xlsx");
        SisImportBatchDto? second = null;

        var hijack = new ReclaimAfterPhase(SisImportPhase.ParsingRows, async () =>
        {
            await using (var db = NewDbContext())
            {
                DateTime? stale = DateTime.UtcNow - WellPastStale;
                await db.SisImportBatches.IgnoreQueryFilters().Where(b => b.Id == batchId)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.ProgressUpdatedAt, stale));
            }

            second = await RunAsync(batchId, world.TermId);
            if (mode == "error") throw new InjectedRunFault();
        });

        await using (var db = new EamsDbContext(
            new DbContextOptionsBuilder<EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString)
                .AddInterceptors(hijack)
                .Options,
            School))
        {
            var thrown = await Assert.ThrowsAnyAsync<Exception>(
                () => SisImportOn(db).RunAsync(batchId, world.TermId));

            var expected = mode == "stamp" ? typeof(SisImportRunSupersededException) : typeof(InjectedRunFault);
            Assert.True(thrown.GetType() == expected, $"Expected {expected.Name}, the first run threw: {thrown}");
        }

        Assert.True(hijack.Fired, "The reclaim was never staged, so this test proved nothing.");
        Assert.NotNull(second);

        var row = await ReadBatchAsync(batchId);
        Assert.Equal(second!.StartedAt, row.StartedAt);
        Assert.Equal(second.Status, row.Status);
        Assert.Equal(SisImportPhase.Done, row.ProgressPhase);
        Assert.Equal(second.ProgressUpdatedAt, row.ProgressUpdatedAt);
        Assert.Null(row.FailureReason);
    }

    private sealed class InjectedRunFault() : Exception("Injected failure in the superseded run.");

    /// <summary>
    /// Runs <c>onReclaim</c> once, straight after the run's stamp for <c>phase</c> has executed. That
    /// stamp runs outside any transaction, so the callback can use other connections without waiting on
    /// this one.
    /// </summary>
    private sealed class ReclaimAfterPhase(string phase, Func<Task> onReclaim) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired
                && command.Transaction is null
                && command.CommandText.Contains("[ProgressUpdatedAt] =", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, phase)))
            {
                Fired = true;
                await onReclaim();
            }

            return result;
        }
    }

    // ================================================================================ commit replay

    /// <summary>
    /// <b>A transient fault reported after the claim's commit reached the server does not strand the
    /// batch.</b> The commit is real; the interceptor throws afterwards, as a dropped connection would,
    /// and the test's execution strategy retries the claim. Without the replay check the retry finds
    /// its own <c>Running</c> row with a live stamp, refuses it as "claimed by another run", and leaves
    /// a batch <c>Running</c> that nothing is running.
    /// </summary>
    [Fact]
    public async Task A_fault_after_the_claim_commits_is_replayed_as_success()
    {
        var world = await ArrangeAsync();
        var batchId = await StageAsync(world.TermId, "replay.xlsx");
        var fault = new FaultAfterFirstCommit();

        await using (var db = new EamsDbContext(
            new DbContextOptionsBuilder<EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString, sql => sql.ExecutionStrategy(d => new RetryInjectedFaults(d)))
                .AddInterceptors(fault)
                .Options,
            School))
        {
            var finished = await SisImportOn(db).RunAsync(batchId, world.TermId);
            Assert.True(
                finished.Status is SisImportStatus.Completed or SisImportStatus.CompletedWithWarnings,
                $"The run after a replayed claim finished {finished.Status}.");
        }

        Assert.True(fault.Fired, "The fault was never injected, so this test proved nothing about replay.");
        Assert.Equal(SisImportPhase.Done, (await ReadBatchAsync(batchId)).ProgressPhase);
    }

    private sealed class InjectedCommitFault() : Exception("Injected after a successful commit.");

    /// <summary>Throws once, after the first transaction commit in the context has reached the server.</summary>
    private sealed class FaultAfterFirstCommit : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Fired) return Task.CompletedTask;
            Fired = true;
            throw new InjectedCommitFault();
        }
    }

    /// <summary>Retries only the injected fault, at once, so the test is fast and deterministic.</summary>
    private sealed class RetryInjectedFaults(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 2, maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is InjectedCommitFault;

        protected override TimeSpan? GetNextDelay(Exception lastException) =>
            base.GetNextDelay(lastException) is null ? null : TimeSpan.Zero;
    }

    // ================================================================================ startup sweep

    /// <summary>
    /// <b>The sweep fails stale and unstamped <c>Running</c> batches, and nothing else.</b> A fresh
    /// <c>Running</c> batch may belong to another process that is still alive. Terminal and
    /// <c>Pending</c> batches are not runs in progress. A second sweep finds nothing to do.
    /// </summary>
    [Fact]
    public async Task The_sweep_fails_stale_running_batches_and_leaves_everything_else_alone()
    {
        var world = await ArrangeAsync();
        var now = DateTime.UtcNow;
        var old = now - WellPastStale;

        var stale = await StageAsync(world.TermId, "stale.xlsx");
        var unstamped = await StageAsync(world.TermId, "unstamped.xlsx");
        var fresh = await StageAsync(world.TermId, "fresh.xlsx");
        var completed = await StageAsync(world.TermId, "completed.xlsx");
        var failed = await StageAsync(world.TermId, "failed.xlsx");
        var pending = await StageAsync(world.TermId, "pending.xlsx");

        await PlantAsync(stale, SisImportStatus.Running, old);
        await PlantAsync(unstamped, SisImportStatus.Running, null, phase: null);
        await PlantAsync(fresh, SisImportStatus.Running, now.AddMinutes(-2));
        await PlantAsync(completed, SisImportStatus.Completed, old);
        await PlantAsync(failed, SisImportStatus.Failed, old);

        var logger = new CapturingLogger<SisImportRunLivenessTests>();

        await using (var db = NewDbContext())
        {
            Assert.Equal(2, await SisImportRunLiveness.SweepInterruptedRunsAsync(db, logger, now, default));
        }

        foreach (var id in new[] { stale, unstamped })
        {
            var swept = await ReadBatchAsync(id);
            Assert.Equal(SisImportStatus.Failed, swept.Status);
            Assert.Equal(SisImportRunLiveness.InterruptedReason, swept.FailureReason);
            Assert.NotNull(swept.FinishedAt);
        }

        // Where it died is kept. The phase is the evidence an operator reads.
        Assert.Equal(SisImportPhase.ResolvingFacts, (await ReadBatchAsync(stale)).ProgressPhase);

        Assert.Equal(SisImportStatus.Running, (await ReadBatchAsync(fresh)).Status);
        Assert.Equal(SisImportStatus.Completed, (await ReadBatchAsync(completed)).Status);
        Assert.Null((await ReadBatchAsync(failed)).FailureReason);
        Assert.Equal(SisImportStatus.Pending, (await ReadBatchAsync(pending)).Status);

        Assert.Contains(logger.At(LogLevel.Warning), e => e.Message.Contains(stale.ToString()));
        Assert.Contains(logger.At(LogLevel.Warning), e => e.Message.Contains(unstamped.ToString()));

        // Idempotent: nothing left to fail.
        await using (var again = NewDbContext())
        {
            Assert.Equal(0, await SisImportRunLiveness.SweepInterruptedRunsAsync(again, logger, now, default));
        }

        // A swept batch can be run again, which is the remedy its FailureReason gives. The fresh
        // Running batch is finished first, because it would otherwise hold the school's slot.
        await PlantAsync(fresh, SisImportStatus.Completed, now);
        var retried = await RunAsync(stale, world.TermId);
        Assert.True(
            retried.Status is SisImportStatus.Completed or SisImportStatus.CompletedWithWarnings,
            $"A swept batch, run again, finished {retried.Status}.");
    }

    /// <summary>
    /// <b>The sweep is wired into host start.</b> The method's own test cannot show that anything calls
    /// it, and an uncalled sweep leaves every zombie exactly where it was.
    /// </summary>
    [Fact]
    public async Task Starting_the_host_sweeps_an_interrupted_run()
    {
        var world = await ArrangeAsync();
        var stale = await StageAsync(world.TermId, "before-restart.xlsx");
        await PlantAsync(stale, SisImportStatus.Running, DateTime.UtcNow - WellPastStale);

        using (var factory = new EamsApiFactory(Sql.ConnectionString))
        {
            _ = factory.Services.GetRequiredService<IServiceScopeFactory>();
        }

        var swept = await ReadBatchAsync(stale);
        Assert.Equal(SisImportStatus.Failed, swept.Status);
        Assert.Equal(SisImportRunLiveness.InterruptedReason, swept.FailureReason);
    }

    // ==================================================================================== heartbeat

    /// <summary>
    /// <b>A run stamps every working phase in order, then <c>Done</c>, and the stamp only moves
    /// forward.</b> The staleness rule assumes a live run refreshes <c>ProgressUpdatedAt</c> at least
    /// once per phase. A run that stamped only at the claim would look dead to the sweep and the claim
    /// as soon as one phase ran past the threshold.
    /// </summary>
    [Fact]
    public async Task A_run_stamps_each_phase_in_order_and_the_heartbeat_advances()
    {
        var world = await ArrangeAsync();
        var batchId = await StageAsync(world.TermId, "heartbeat.xlsx");
        var recorder = new BatchProgressRecorder(batchId);

        await using (var db = new EamsDbContext(
            new DbContextOptionsBuilder<EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString)
                .AddInterceptors(recorder)
                .Options,
            School))
        {
            var finished = await SisImportOn(db).RunAsync(batchId, world.TermId);

            // The run's own response reports the finished position, not the claim's.
            Assert.Equal(SisImportPhase.Done, finished.ProgressPhase);
        }

        // The claim's stamp first, then one per working phase, then Done. RecordingRowResults is stamped
        // twice: at its start, and again as the fence right before the tracked Tally save.
        var expected = new[] { SisImportPhase.Working[0] }
            .Concat(SisImportPhase.Working.SelectMany(p =>
                p == SisImportPhase.RecordingRowResults ? new[] { p, p } : new[] { p }))
            .Append(SisImportPhase.Done)
            .ToArray();
        Assert.Equal(expected, recorder.Snapshots.Select(s => s.Phase).ToArray());

        var heartbeats = recorder.Snapshots.Skip(1).ToList();
        var count = SisImportPhase.Working.Count;

        foreach (var beat in heartbeats.SkipLast(1))
        {
            Assert.Equal(SisImportPhase.Working.ToList().IndexOf(beat.Phase!) + 1, beat.PhaseNumber);
            Assert.Equal(count, beat.PhaseCount);
            Assert.Null(beat.UnitsDone);
            Assert.Null(beat.UnitsTotal);
        }

        Assert.Equal(count, heartbeats[^1].PhaseNumber);
        Assert.Equal(count, heartbeats[^1].PhaseCount);

        for (var i = 1; i < recorder.Snapshots.Count; i++)
            Assert.True(
                recorder.Snapshots[i].UpdatedAt >= recorder.Snapshots[i - 1].UpdatedAt,
                $"ProgressUpdatedAt went backwards at stamp {i} ({recorder.Snapshots[i].Phase}).");

        Assert.True(
            recorder.Snapshots[^1].UpdatedAt > recorder.Snapshots[0].UpdatedAt,
            "ProgressUpdatedAt never moved past the claim's stamp.");

        // And the final row says the same as the last stamp.
        var read = await ReadBatchAsync(batchId);
        Assert.Equal(SisImportPhase.Done, read.ProgressPhase);
        Assert.Equal(recorder.Snapshots[^1].UpdatedAt, read.ProgressUpdatedAt);
    }

    // ------------------------------------------------------------------------------------- plumbing

    private readonly record struct Attempt(SisImportBatchDto? Batch, Exception? Error);

    /// <summary>
    /// Starts both calls together and records what each one did. This is the same gate as
    /// <see cref="SisImportRunClaimTests"/>. Without it the first run finishes before the second
    /// starts, and the race never happens. Exceptions are captured, not swallowed: every one is
    /// reported by the assertions above.
    /// </summary>
    private static async Task<Attempt[]> RaceCapturingAsync(
        params Func<Task<SisImportBatchDto>>[] contenders)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = contenders.Select(contender => Task.Run(async () =>
        {
            await gate.Task;

            try
            {
                return new Attempt(await contender(), null);
            }
            catch (Exception ex)
            {
                return new Attempt(null, ex);
            }
        })).ToArray();

        gate.SetResult();
        return await Task.WhenAll(running);
    }
}
