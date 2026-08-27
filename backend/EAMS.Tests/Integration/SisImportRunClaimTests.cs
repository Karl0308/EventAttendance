using EAMS.Application.Dtos;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-004 D-54.4 — the atomic claim that decides which caller runs a batch, and what it resets on
/// the way in.
///
/// <para>
/// <b>What this file exists for.</b> <c>RunAsync</c>'s re-run guard used to be a read, a check and a
/// write across three statements with no transaction: load the batch, refuse it unless it is
/// <c>Pending</c> or <c>Failed</c>, then write <c>Running</c>. Two callers could both load a
/// <c>Pending</c> batch, both find it re-runnable, and both import the same roster. Nothing in
/// <see cref="SisImportPipelineTests"/> could see it — every re-run test there is sequential, and a
/// sequential re-run is refused by the check whether or not the check is a guard.
/// </para>
///
/// <para>
/// <b>And the window is about to open.</b> While the run was bounded by the HTTP request a second
/// operator had to click inside the milliseconds between the load and the save; once <c>POST /run</c>
/// returns as soon as the batch is claimed (D-54), a double-submit or two tabs reaches it as a matter
/// of course. The fix has to land before the lifecycle change, or the lifecycle change ships a known
/// race.
/// </para>
///
/// <para>
/// <b>The claim is one <c>UPDATE … WHERE Id = @id AND Status IN ('Pending','Failed')</c>.</b> Deciding
/// and writing are the same statement, so the row lock picks the winner: zero rows affected means
/// somebody else got there first. That is what the race test below is written to observe — and
/// observing it means telling the two refusals apart, which is why the claim's refusal and the
/// belt-and-braces guard's refusal say different things.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportRunClaimTests : IntegrationTest
{
    public SisImportRunClaimTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Six, matching <see cref="TermAdminRaceTests"/> and <see cref="StudentWriteRaceTests"/>. Two
    /// would prove the same thing on a good day; six makes it likely that several contenders clear the
    /// pre-check before the winner's claim commits, which is the interleaving the claim exists for.
    /// </summary>
    private const int Contenders = 6;

    /// <summary>
    /// <b>Why the race is attempted more than once.</b> Which contenders reach the claim and which are
    /// turned away earlier by the belt-and-braces guard depends on thread scheduling. A single attempt
    /// that happened to serialize would fail the "somebody was refused by the claim" assertion — a
    /// flake, and the kind that gets a real test deleted. Re-staging a fresh batch and racing again
    /// makes "the claim is never the thing that refuses" the only way to exhaust the loop.
    /// </summary>
    private const int MaxAttempts = 6;

    /// <summary>
    /// The substring that identifies the claim's refusal, and the reason the two refusal messages are
    /// not the same sentence. Both are <see cref="SisImportException"/> and both become a 409, so the
    /// exception type cannot tell a caller refused by the atomic claim from one refused by the read
    /// that precedes it — and that distinction is the entire subject of the race test.
    /// </summary>
    private const string ClaimRefusal = "claimed by another run";

    /// <summary>The substring that identifies the pre-check's refusal. See <see cref="ClaimRefusal"/>.</summary>
    private const string GuardRefusal = "already been run";

    /// <summary>
    /// How many students <see cref="SyntheticRoster"/>'s twelve rows describe, counted by hand from the
    /// fixture — the same number, arrived at the same way, as <c>SisImportPipelineTests</c>. Deriving it
    /// from the roster by the pipeline's own rules would prove only that the code agrees with itself.
    /// </summary>
    private const int ExpectedStudents = 8;

    private sealed record World(Guid SchoolId, Guid TermId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    /// <summary>Stages a roster and returns the <c>Pending</c> batch's id. Runs nothing.</summary>
    private async Task<Guid> StageAsync(Guid termId, string fileName)
    {
        await using var db = NewDbContext();
        await using var content = SyntheticRoster.Build();

        var preview = await SisImportOn(db).UploadAsync(
            new SisImportUploadRequest(content, fileName, termId));

        Assert.Equal(SisImportStatus.Pending, preview.Batch.Status);
        return preview.Batch.Id;
    }

    private async Task<SisImportBatch> ReadBatchAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return await db.SisImportBatches.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(b => b.Id == batchId);
    }

    // ======================================================================================= the race

    /// <summary>
    /// <b>Six simultaneous runs of one <c>Pending</c> batch import it once, and at least one loser is
    /// refused by the claim rather than by the read in front of it.</b>
    ///
    /// <para>
    /// Three separate facts, which fail in three different ways:
    /// <list type="bullet">
    ///   <item><b>Exactly one caller runs the batch.</b> This is the assertion the atomic claim exists
    ///   to satisfy, and the one that fails when the claim is reverted to the read-check-write: several
    ///   callers pass the check on the same <c>Pending</c> row and every one of them starts importing
    ///   the same roster into the same tables.</item>
    ///   <item><b>Every other caller is refused, and refused as a conflict.</b> Not as a 500. A loser
    ///   that surfaces a <c>DbUpdateException</c> or a deadlock instead of
    ///   <see cref="SisImportException"/> is a different defect wearing the same green tick, so the
    ///   unexpected exceptions are reported by type and message rather than counted.</item>
    ///   <item><b>At least one loser was refused by the claim.</b> Without this the test passes with
    ///   the claim deleted, because a contender that reads the batch after the winner has committed
    ///   <c>Running</c> is refused by the pre-check on its own — which is the sequential case
    ///   <see cref="SisImportPipelineTests"/> already covers and says nothing about concurrency.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The state is checked after every attempt, not only at the end: one batch, one set of students,
    /// and a terminal status. Two callers importing the same roster concurrently is not guaranteed to
    /// throw — the pipeline's writes are upserts — so "nothing threw" is not evidence that only one
    /// run happened.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Simultaneous_runs_of_one_pending_batch_import_it_once_and_the_claim_is_what_refuses()
    {
        var world = await ArrangeAsync();
        var refusedByClaim = 0;

        for (var attempt = 0; attempt < MaxAttempts && refusedByClaim == 0; attempt++)
        {
            var batchId = await StageAsync(world.TermId, $"claim-race-{attempt}.xlsx");

            var results = await RaceCapturingAsync(Enumerable.Range(0, Contenders).Select(_ =>
                new Func<Task<SisImportBatchDto>>(async () =>
                {
                    await using var db = NewDbContext();
                    return await SisImportOn(db).RunAsync(batchId, world.TermId);
                })).ToArray());

            // Admitted, not "succeeded" — and the distinction is what makes this assertion say the
            // right thing when it breaks. A caller that gets past the re-run check and then collides
            // with another importer has still been allowed to run the batch; counting only the callers
            // that finished cleanly would report "1 of 6 ran" for a race in which three of them did,
            // and the failure would read as a collision rather than as the admission that caused it.
            // Refusal is the positive signal here, so anything that is not a SisImportException — a
            // clean finish, a DbUpdateException, a deadlock — is a caller that was let through.
            var refused = results.Where(r => r.Error is SisImportException).ToList();
            var admitted = results.Where(r => r.Error is not SisImportException).ToList();

            Assert.True(
                admitted.Count == 1,
                $"{admitted.Count} of {Contenders} simultaneous runs of batch {batchId} were admitted " +
                "past the re-run check and started importing, expected exactly one. Two callers " +
                "importing one roster write the same rows through the same upserts, so most of the " +
                "damage is invisible in the row counts and surfaces as a doubled fan-out, two sets of " +
                "counters written over each other, or a collision between the two. This is what a " +
                "read-check-write re-run guard permits and what the single-statement claim forbids. " +
                "What the admitted callers did:" + Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    admitted.Select(a => a.Error is null
                        ? $"  ran to completion: {a.Batch!.Status}"
                        : $"  {a.Error.GetType().Name}: {a.Error.Message}")));

            // The one admitted caller finished. Separate from the count above so a winner that threw is
            // reported as a fault in the run rather than as a concurrency failure.
            var winner = admitted[0];
            Assert.True(
                winner.Error is null,
                "The one caller admitted to run the batch threw " +
                $"{winner.Error?.GetType().Name}: {winner.Error?.Message}. A run that fails here is a " +
                "500 at the controller, not the 409 this file is about.");

            Assert.Equal(Contenders - 1, refused.Count);

            // The batch finished, and finished as a run rather than as a wreck. Stated as "one of the
            // two clean terminal statuses" rather than as the exact one, because the second and later
            // attempts import a roster whose students already exist and report Updated or Skipped where
            // the first reported Inserted — a difference in the counters, not in whether the run worked.
            var finished = await ReadBatchAsync(batchId);
            Assert.True(
                finished.Status is SisImportStatus.Completed or SisImportStatus.CompletedWithWarnings,
                $"After {Contenders} simultaneous runs the batch is {finished.Status}, expected a " +
                $"clean terminal status. FailureReason: {finished.FailureReason ?? "(none)"}");

            // And it actually imported. Eight students, worked out by hand from SyntheticRoster.Rows
            // exactly as SisImportPipelineTests does — a run that refused everybody would leave the
            // roster unwritten and every assertion above it satisfied.
            await using (var read = NewDbContext())
            {
                Assert.Equal(ExpectedStudents, await read.Students.IgnoreQueryFilters().CountAsync());
            }

            refusedByClaim += refused.Count(
                r => r.Error!.Message.Contains(ClaimRefusal, StringComparison.Ordinal));
        }

        Assert.True(
            refusedByClaim > 0,
            $"In {MaxAttempts} attempts of {Contenders} simultaneous runs, no caller was ever refused " +
            "by the claim — every loser was turned away by the read-then-check in front of it, which " +
            "is the sequential path SisImportPipelineTests already covers. So this run says nothing " +
            "about whether the claim works, and would stay green with the claim deleted. Either the " +
            "contenders are being serialized (check that RaceCapturingAsync's gate still releases them " +
            "together, and that nothing has introduced a shared DbContext across them) or the claim is " +
            "unreachable.");
    }

    // ================================================================= what the claim writes and clears

    /// <summary>
    /// <b>Retrying a <c>Failed</c> batch clears the previous attempt's death notice and its progress,
    /// and stamps the phase the new run starts on.</b>
    ///
    /// <para>
    /// The columns are planted with values a real interrupted run would have left — a failure reason, a
    /// <c>FinishedAt</c>, and a position part-way through the phase list — because that is the state
    /// the operator is looking at when they press Run again. Carried forward, they would show the last
    /// run's cause of death beside the new run's spinner, and a fabricated position: "phase 4 of 8"
    /// against a run that has just started phase 1. D-54.7's rule is that <c>NULL</c> means "nothing to
    /// report" and 0 means "none done", so the stale values are cleared to <c>NULL</c> rather than
    /// zeroed, and the phase number is left for the progress writer to fill rather than guessed here.
    /// </para>
    ///
    /// <para>
    /// <b>Observed after the run rather than during it, and that is what makes the assertion tight.</b>
    /// Nothing in the pipeline writes <c>ProgressPhase</c> or <c>FailureReason</c> on a successful run
    /// — the progress writer is a later phase — so the values read back are the claim's own, still
    /// standing. The timestamps are compared against the planted ones rather than merely checked
    /// non-null: <c>NotNull</c> would pass on a claim that wrote nothing at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Retrying_a_failed_batch_clears_the_previous_attempts_failure_and_stamps_the_first_phase()
    {
        var world = await ArrangeAsync();
        var batchId = await StageAsync(world.TermId, "retry-after-failure.xlsx");

        var stale = DateTime.UtcNow.AddHours(-1);

        await using (var arrange = NewDbContext())
        {
            var batch = await arrange.SisImportBatches.IgnoreQueryFilters()
                .SingleAsync(b => b.Id == batchId);

            batch.Status = SisImportStatus.Failed;
            batch.StartedAt = stale;
            batch.FinishedAt = stale;
            batch.FailureReason = "The run was interrupted before it finished. Run it again.";
            batch.ProgressPhase = SisImportPhase.ResolvingFacts;
            batch.ProgressPhaseNumber = 4;
            batch.ProgressPhaseCount = SisImportPhase.Working.Count;
            batch.ProgressUnitsDone = 1_200;
            batch.ProgressUnitsTotal = 21_497;
            batch.ProgressUpdatedAt = stale;

            await arrange.SaveChangesAsync();
        }

        await using (var run = NewDbContext())
        {
            var finished = await SisImportOn(run).RunAsync(batchId, world.TermId);
            Assert.Equal(SisImportStatus.CompletedWithWarnings, finished.Status);
        }

        var read = await ReadBatchAsync(batchId);

        // The death notice is gone. This is the one an operator sees first, and a retry that carried it
        // forward would report a completed import beside the reason the last one died.
        Assert.Null(read.FailureReason);

        // The claim stamped the first working phase. Read from the list rather than named, for the
        // reason SisImportPhase.Working gives: re-ordering it re-interprets every stored position.
        Assert.Equal(SisImportPhase.Working[0], read.ProgressPhase);

        // Cleared, not carried and not zeroed. Zero would be a claim that the phase has done none of
        // its units; NULL is the absence these columns are documented to mean.
        Assert.Null(read.ProgressPhaseNumber);
        Assert.Null(read.ProgressPhaseCount);
        Assert.Null(read.ProgressUnitsDone);
        Assert.Null(read.ProgressUnitsTotal);

        // Moved, not merely present. Each of these held a value an hour old before the claim.
        Assert.NotNull(read.ProgressUpdatedAt);
        Assert.True(
            read.ProgressUpdatedAt > stale,
            $"ProgressUpdatedAt is {read.ProgressUpdatedAt:O}, still the previous attempt's " +
            $"{stale:O}. The startup sweep reads this column to decide whether a Running batch is " +
            "alive, so a claim that does not move it hands the sweep an hour-old timestamp on a run " +
            "that started a moment ago.");

        Assert.NotNull(read.StartedAt);
        Assert.True(
            read.StartedAt > stale,
            $"StartedAt is {read.StartedAt:O}, still the previous attempt's {stale:O}.");

        // FinishedAt is written twice over this run: cleared by the claim, then set by Tally when the
        // import ends. Only the second is observable here, and it is enough to say the first happened —
        // the value on the row is this run's, not the failed attempt's.
        Assert.NotNull(read.FinishedAt);
        Assert.True(
            read.FinishedAt > stale,
            $"FinishedAt is {read.FinishedAt:O}, still the previous attempt's {stale:O}.");
    }

    /// <summary>
    /// <b>A batch that finished is still refused, with the sentence it was always refused with.</b>
    ///
    /// <para>
    /// All three terminal statuses, because the rule is "not <c>Pending</c> and not <c>Failed</c>"
    /// rather than "not <c>Completed</c>", and it is now written twice — once as a C# pattern and once
    /// as a SQL <c>IN</c> list (D-54.4's recorded negative). A test over one of the three would let the
    /// two drift apart on the other two.
    /// </para>
    ///
    /// <para>
    /// The message is asserted, not just the type. It is what an operator is shown, and it carries the
    /// instruction that makes the refusal actionable — upload the file again — which the status code
    /// alone does not.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(SisImportStatus.Completed)]
    [InlineData(SisImportStatus.CompletedWithWarnings)]
    [InlineData(SisImportStatus.CompletedWithErrors)]
    public async Task A_batch_that_has_already_run_is_refused_whatever_it_finished_as(string status)
    {
        var world = await ArrangeAsync();
        var batchId = await StageAsync(world.TermId, $"finished-{status}.xlsx");

        await using (var arrange = NewDbContext())
        {
            var batch = await arrange.SisImportBatches.IgnoreQueryFilters()
                .SingleAsync(b => b.Id == batchId);

            batch.Status = status;
            batch.FinishedAt = DateTime.UtcNow;
            await arrange.SaveChangesAsync();
        }

        await using (var run = NewDbContext())
        {
            var refused = await Assert.ThrowsAsync<SisImportException>(
                () => SisImportOn(run).RunAsync(batchId, world.TermId));

            Assert.Contains(GuardRefusal, refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Refused before anything ran: the status is untouched and no student was written.
        var read = await ReadBatchAsync(batchId);
        Assert.Equal(status, read.Status);

        await using var check = NewDbContext();
        Assert.Equal(0, await check.Students.IgnoreQueryFilters().CountAsync());
    }

    // ------------------------------------------------------------------------------------- plumbing

    /// <summary>One contender's run: what it returned, or what it threw.</summary>
    private readonly record struct Attempt(SisImportBatchDto? Batch, Exception? Error);

    /// <summary>
    /// Starts every call at once and returns what each one did, failure included.
    ///
    /// <para>
    /// The gate is released only after all of them are parked on it, so the first database call each
    /// one makes happens within microseconds of the others'. Without it the tasks start in sequence as
    /// the enumerable is materialized, the first import finishes comfortably before the second begins,
    /// and the test passes green having never produced the race it is named for — the reason
    /// <see cref="TermAdminRaceTests"/> and <see cref="StudentWriteRaceTests"/> both carry this helper,
    /// and it is duplicated here for the same reason they duplicate it from each other.
    /// </para>
    ///
    /// <para>
    /// <b>Capturing rather than propagating, and it is not a silent catch.</b> <c>Task.WhenAll</c>
    /// surfaces one exception out of six and discards which caller it belonged to and what the other
    /// five did — and "two callers ran the import while three were refused and one deadlocked" is
    /// exactly the finding this file exists to produce. Every captured exception is re-reported, by
    /// type and message, by the assertions above.
    /// </para>
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
