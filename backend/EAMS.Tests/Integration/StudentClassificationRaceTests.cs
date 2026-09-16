using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Two assignments racing over one person's axis slot.
///
/// <para>
/// <b>Every recovery path in <c>StudentClassificationService</c> lived here, and until this file existed
/// none of them was ever executed.</b> Nothing in the suite produced
/// <see cref="StudentClassificationWriteOutcome.ConcurrentAssignment"/> at all, which meant the
/// unique-violation catch, the winner-assigned-what-we-asked-for branch, the 547 discriminate-then-
/// rethrow and <c>Detach</c> were all unreached — and unreached recovery code is indistinguishable from
/// recovery code that throws, because the only thing that ever runs it is the failure it exists to
/// absorb.
/// </para>
///
/// <para>
/// <b>The two branches race for different reasons, and both are covered.</b> An <em>empty</em> slot is
/// arbitrated by <c>UX_StudentClassifications_Student_Axis</c> — two inserts, one index, a loud
/// violation. An <em>occupied</em> slot has no such backstop: both callers take the UPDATE path,
/// <c>(StudentId, Axis)</c> is unchanged by either write so the index is satisfied by the corrupt
/// outcome, and the application-side condition in the <c>WHERE</c> is the only guard there is. That
/// second case is the one the gate refused to waive, and it is the one that silently loses a
/// registrar's correction.
/// </para>
///
/// <para>
/// Modelled on <c>TermAdminRaceTests</c> and <c>ClassificationMergeRaceTests</c>: contenders gated on a
/// <see cref="TaskCompletionSource"/> so they are in flight together, assertions written as invariants
/// over the final state rather than "who won", and — where a recovery path must be proven reachable
/// rather than merely possible — a retry loop that fails loudly if the scheduler never produced the
/// collision.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentClassificationRaceTests : IntegrationTest
{
    public StudentClassificationRaceTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>How many times to re-run a race before declaring the recovery path unreachable.</summary>
    private const int MaxAttempts = 30;

    /// <summary>
    /// Both contenders start together, so they are inside their read-then-write window at the same
    /// time. Lifted from <c>TermAdminRaceTests</c>, which races the same way against an index.
    /// </summary>
    private static async Task<T[]> RaceAsync<T>(params Func<Task<T>>[] contenders)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = contenders.Select(contender => Task.Run(async () =>
        {
            await gate.Task;
            return await contender();
        })).ToArray();

        gate.SetResult();
        return await Task.WhenAll(running);
    }

    private sealed record World(Guid SchoolId, Guid StudentId, Guid NapId, Guid AcadId, Guid FriarsId);

    /// <summary>A school, a person, and three classifications — two sharing the Personnel axis.</summary>
    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);

        var nap = TestData.NewClassification(school.Id, "NAP", ClassificationAxis.Personnel);
        var acad = TestData.NewClassification(school.Id, "ACAD", ClassificationAxis.Personnel);
        var friars = TestData.NewClassification(school.Id, "USA FRIARS", ClassificationAxis.Friars);
        db.Classifications.AddRange(nap, acad, friars);

        await db.SaveChangesAsync();

        return new World(school.Id, student.Id, nap.Id, acad.Id, friars.Id);
    }

    /// <summary>
    /// An assignment on its own context and its own logger, so the contenders share no change tracker
    /// and no connection — which is what makes this a database race rather than an EF one, and what
    /// keeps two threads from appending to one <see cref="CapturingLogger{T}"/> list.
    /// </summary>
    private async Task<StudentClassificationWriteResponse> AssignAsync(
        Guid studentId, Guid classificationId, CapturingLogger<StudentClassificationService> logger)
    {
        await using var db = NewDbContext();
        return await StudentClassificationsOn(db, logger).AssignAsync(studentId, classificationId);
    }

    /// <summary>
    /// Empties this person's axis slots between attempts.
    ///
    /// <para>
    /// <b>The retry loops re-use one arrangement rather than re-arranging, and that is not a
    /// micro-optimisation — re-arranging was a bug.</b> <c>TestData.NewSchool()</c> defaults to the code
    /// <c>USA</c> and <c>Schools.Code</c> is uniquely indexed, so calling the arrange a second time
    /// inside the loop raised a <c>DbUpdateException</c> out of the <em>fixture</em> — which reads
    /// exactly like the production failure these tests exist to detect, and reproduced only when the
    /// first attempt failed to collide.
    /// </para>
    /// </summary>
    private async Task ResetSlotsAsync(Guid studentId)
    {
        await using var db = NewDbContext();

        await db.StudentClassifications
            .Where(sc => sc.StudentId == studentId)
            .ExecuteDeleteAsync();
    }

    private async Task<List<(Guid ClassificationId, string Axis)>> AssignmentsAsync(Guid studentId)
    {
        await using var read = NewDbContext();

        return await read.StudentClassifications.AsNoTracking()
            .Where(sc => sc.StudentId == studentId)
            .Select(sc => new ValueTuple<Guid, string>(sc.ClassificationId, sc.Axis))
            .ToListAsync();
    }

    // ------------------------------------------------------- the EMPTY slot: the index is the arbiter

    /// <summary>
    /// <b>Two different classifications onto one empty axis: exactly one person ends up classified,
    /// once, and the loser is told so rather than being handed a 500.</b>
    ///
    /// <para>
    /// The retry loop is what makes this a test of the <em>recovery</em> rather than of the pre-check.
    /// If the two contenders happen to serialize, the second one reads an occupied slot and takes the
    /// replace path — a perfectly correct 200 that never touches the unique-violation catch. Looping
    /// until a contender was actually refused by the index, and failing loudly if that never happens,
    /// is the same construction <c>TermAdminRaceTests</c> uses and for the same reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_classifications_racing_for_one_empty_axis_leave_exactly_one_assignment()
    {
        var world = await ArrangeAsync();
        var reachedRecovery = false;

        for (var attempt = 0; attempt < MaxAttempts && !reachedRecovery; attempt++)
        {
            await ResetSlotsAsync(world.StudentId);

            var loggers = new[]
            {
                new CapturingLogger<StudentClassificationService>(),
                new CapturingLogger<StudentClassificationService>(),
            };

            var results = await RaceAsync(
                () => AssignAsync(world.StudentId, world.NapId, loggers[0]),
                () => AssignAsync(world.StudentId, world.AcadId, loggers[1]));

            // Nothing may throw, and nothing may answer with an outcome a caller cannot act on. With
            // the catch deleted this is where it goes: the losing SaveChangesAsync raises
            // DbUpdateException straight out of RaceAsync, which is a 500 at the controller.
            Assert.All(results, r => Assert.True(
                r.Outcome is StudentClassificationWriteOutcome.Saved
                          or StudentClassificationWriteOutcome.ConcurrentAssignment,
                $"A raced assignment answered {r.Outcome}: {r.Message}"));

            // THE invariant, whoever won: one person, one Personnel classification, one row. Two rows
            // would mean the index let a person hold two values on one axis; zero would mean both
            // callers backed off and nobody was classified.
            var rows = await AssignmentsAsync(world.StudentId);
            var held = Assert.Single(rows);
            Assert.Equal(ClassificationAxis.Personnel, held.Axis);
            Assert.Contains(held.ClassificationId, new[] { world.NapId, world.AcadId });

            // And the row that survived is one somebody was told they saved — the outcomes and the
            // table agree rather than merely being individually plausible.
            Assert.Contains(results, r => r.Outcome == StudentClassificationWriteOutcome.Saved);

            reachedRecovery = loggers.Any(l => l.Entries.Count > 0);

            if (!reachedRecovery) continue;

            // The contender that lost was told it was a conflict, and told nothing was written.
            var loser = Assert.Single(
                results, r => r.Outcome == StudentClassificationWriteOutcome.ConcurrentAssignment);

            Assert.Contains("Nothing was changed", loser.Message);

            // The refusal still carries the person's real state, so a client can re-render without a
            // second round trip — that is what the response's Classifications is for on a failure.
            Assert.NotNull(loser.Classifications);
            Assert.Equal(
                held.ClassificationId,
                Assert.Single(loser.Classifications.Classifications).ClassificationId);
        }

        Assert.True(
            reachedRecovery,
            $"In {MaxAttempts} attempts, two simultaneous assignments onto one empty axis never " +
            "collided at UX_StudentClassifications_Student_Axis — every one was resolved by the " +
            "pre-check, so the unique-violation handler behind it is untested by this run. Either the " +
            "contenders are being serialized (check that RaceAsync's gate still releases them " +
            "together) or the handler is unreachable, in which case a real collision returns a 500.");
    }

    /// <summary>
    /// <b>Both callers asking for the SAME classification: both are told it saved, and the loser's
    /// recovery says so out loud.</b>
    ///
    /// <para>
    /// This is the branch that must not report a conflict. The postcondition the loser asked for holds —
    /// the person carries exactly the classification it named — so answering 409 would make a
    /// double-submitted form fail for a collision it caused with itself. The log entry is the only
    /// observable difference between "the pre-check found nothing to do" and "the index refused us and
    /// we discovered the winner had done our work", which is why this test reads a
    /// <see cref="CapturingLogger{T}"/> rather than only the response.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Both_callers_asking_for_the_same_classification_are_both_told_it_saved()
    {
        var world = await ArrangeAsync();
        var reachedRecovery = false;

        for (var attempt = 0; attempt < MaxAttempts && !reachedRecovery; attempt++)
        {
            await ResetSlotsAsync(world.StudentId);

            var loggers = new[]
            {
                new CapturingLogger<StudentClassificationService>(),
                new CapturingLogger<StudentClassificationService>(),
            };

            var results = await RaceAsync(
                () => AssignAsync(world.StudentId, world.NapId, loggers[0]),
                () => AssignAsync(world.StudentId, world.NapId, loggers[1]));

            Assert.All(results, r => Assert.Equal(
                StudentClassificationWriteOutcome.Saved, r.Outcome));

            var held = Assert.Single(await AssignmentsAsync(world.StudentId));
            Assert.Equal(world.NapId, held.ClassificationId);

            reachedRecovery = loggers.Any(l => l.Entries.Count > 0);

            if (!reachedRecovery) continue;

            // The loser went through the recovery and still answered Saved — proving the branch that
            // distinguishes "somebody else did what I asked" from "somebody else did something else"
            // actually runs, rather than the whole case being absorbed by the pre-check.
            var recovered = Assert.Single(loggers, l => l.Entries.Count > 0);

            Assert.Contains(
                recovered.Entries,
                e => e.Message.Contains("lost the race for the", StringComparison.Ordinal));

            Assert.Contains(
                results,
                r => r.Message.Contains("by another request at the same moment", StringComparison.Ordinal));
        }

        Assert.True(
            reachedRecovery,
            $"In {MaxAttempts} attempts, two simultaneous assignments of the SAME classification never " +
            "collided at the index, so the branch that answers Saved after losing a race is untested " +
            "by this run. That branch is what keeps a double-submitted form from being told it " +
            "conflicted with itself.");
    }

    // -------------------------------------------- the OCCUPIED slot: the WHERE clause is the arbiter

    /// <summary>
    /// Moves the person's axis slot onto a different classification from a separate connection, the
    /// instant before the service's own guarded <c>UPDATE</c> executes.
    /// </summary>
    private sealed class MoveSlotDuringReplace : DbCommandInterceptor
    {
        private readonly string _connectionString;
        private readonly Guid _studentId;
        private readonly Guid _movedTo;
        private bool _fired;

        internal MoveSlotDuringReplace(string connectionString, Guid studentId, Guid movedTo)
        {
            _connectionString = connectionString;
            _studentId = studentId;
            _movedTo = movedTo;
        }

        internal bool Fired => _fired;

        /// <summary>
        /// <b><c>NonQueryExecutingAsync</c>, not <c>ReaderExecutingAsync</c>.</b>
        /// <c>ExecuteUpdateAsync</c> issues its statement through <c>ExecuteNonQuery</c>, so a reader
        /// hook never sees it — the first version of this interceptor hooked the reader, fired on the
        /// axis-slot SELECT instead, and the service then read the moved value as its own starting
        /// point and saved happily. The test failed with the guard fully present, which is the same
        /// class of wrong answer as a control that cannot fail.
        /// </summary>
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            // StartsWith, not Contains: every SELECT of this entity projects [UpdatedAt], and
            // "UpdatedAt" contains "Update" — so a case-insensitive Contains("UPDATE") matches the
            // reads as well as the writes. That is precisely how the reader version mis-fired.
            if (!_fired
                && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("StudentClassifications", StringComparison.Ordinal))
            {
                _fired = true;

                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                using var move = connection.CreateCommand();
                move.CommandText =
                    "UPDATE [StudentClassifications] SET [ClassificationId] = @to " +
                    "WHERE [StudentId] = @student;";
                move.Parameters.Add(new SqlParameter("@to", _movedTo));
                move.Parameters.Add(new SqlParameter("@student", _studentId));
                move.ExecuteNonQuery();
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// <b>A replacement whose slot moved between its read and its write is refused, and the mover's
    /// value is what survives.</b> The gate's WARNING 4, proven deterministically.
    ///
    /// <para>
    /// <b>This replaces a test that could not tell a lost guard from a lucky schedule.</b> The previous
    /// version raced two replacements and asserted that only one could answer <c>Saved</c> — but if the
    /// contenders serialize, the second one legitimately reads the first's value, its <c>WHERE</c>
    /// matches, and <b>both saving is the correct outcome</b>. It then failed with "Both replacements
    /// reported success", which is the <em>identical</em> message its own negative control produced. A
    /// control that cannot distinguish "the guard is gone" from "the scheduler serialized them" is not
    /// a control.
    /// </para>
    ///
    /// <para>
    /// Nothing here is concurrent: one caller, one interceptor, one competing statement placed exactly
    /// where the guard is supposed to look. Either the <c>ClassificationId</c> predicate is in the
    /// <c>WHERE</c> or it is not, and the result says which — on every run, on every machine.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_replacement_whose_slot_moved_underneath_it_is_refused()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var seeded = await StudentClassificationsOn(db).AssignAsync(world.StudentId, world.NapId);
            Assert.Equal(StudentClassificationWriteOutcome.Saved, seeded.Outcome);
        }

        // A third Personnel value for the competing writer, so neither party is asking for what is
        // already there and the axis stays consistent with the composite foreign key.
        Guid otherId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewClassification(
                world.SchoolId, "SUPERVISORY/MANAGERIAL", ClassificationAxis.Personnel);

            db.Classifications.Add(other);
            await db.SaveChangesAsync();
            otherId = other.Id;
        }

        var saboteur = new MoveSlotDuringReplace(Sql.ConnectionString, world.StudentId, otherId);

        await using var write = new EAMS.Infrastructure.Data.EamsDbContext(
            new DbContextOptionsBuilder<EAMS.Infrastructure.Data.EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString)
                .AddInterceptors(saboteur)
                .Options,
            School);

        var response = await StudentClassificationsOn(write)
            .AssignAsync(world.StudentId, world.AcadId);

        Assert.True(
            saboteur.Fired,
            "The interceptor never saw an UPDATE against StudentClassifications, so the slot was not " +
            "moved inside the read-to-write window and this test proved nothing. Check that the " +
            "occupied-axis path still replaces by UPDATE rather than by delete-and-insert.");

        Assert.True(
            response.Outcome == StudentClassificationWriteOutcome.ConcurrentAssignment,
            $"A replacement whose row had already moved answered {response.Outcome}: {response.Message} " +
            "Only the ClassificationId predicate in the UPDATE's WHERE can catch this — (StudentId, " +
            "Axis) is unchanged by both writes, so no index objects, and without it this caller " +
            "overwrites a value it never read and reports success for it.");

        // The mover's value survived; the refused caller's did not reach the table.
        var held = Assert.Single(await AssignmentsAsync(world.StudentId));
        Assert.Equal(otherId, held.ClassificationId);
        Assert.Equal(ClassificationAxis.Personnel, held.Axis);
    }

    /// <summary>
    /// <b>Two genuine replacements over one occupied axis leave exactly one row, and never throw.</b>
    ///
    /// <para>
    /// <b>Deliberately asserts only what is true under BOTH schedulings</b>, which is what the test it
    /// replaces got wrong. If the contenders collide, one is refused; if they serialize, both saved and
    /// both were right. Either way a person holds one classification on the axis, nothing escapes as an
    /// exception, and every outcome is one a caller can act on. Proving the guard itself is the
    /// deterministic test above; this one is here because real concurrency finds things a scripted
    /// interleaving cannot — the deadlock this file now covers is one it found.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_genuine_replacements_leave_one_row_and_never_throw()
    {
        var world = await ArrangeAsync();

        Guid otherId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewClassification(
                world.SchoolId, "SUPERVISORY/MANAGERIAL", ClassificationAxis.Personnel);

            db.Classifications.Add(other);
            await db.SaveChangesAsync();
            otherId = other.Id;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await ResetSlotsAsync(world.StudentId);

            await using (var db = NewDbContext())
            {
                await StudentClassificationsOn(db).AssignAsync(world.StudentId, world.NapId);
            }

            var results = await RaceAsync(
                () => AssignAsync(
                    world.StudentId, world.AcadId, new CapturingLogger<StudentClassificationService>()),
                () => AssignAsync(
                    world.StudentId, otherId, new CapturingLogger<StudentClassificationService>()));

            Assert.All(results, r => Assert.True(
                r.Outcome is StudentClassificationWriteOutcome.Saved
                          or StudentClassificationWriteOutcome.ConcurrentAssignment,
                $"A raced replacement answered {r.Outcome}: {r.Message}"));

            var held = Assert.Single(await AssignmentsAsync(world.StudentId));
            Assert.Equal(ClassificationAxis.Personnel, held.Axis);
            Assert.Contains(held.ClassificationId, new[] { world.AcadId, otherId });

            // Whoever was told last that it saved is what the table holds. Under serialization that is
            // the second contender; under collision it is the only winner. Both are consistent states,
            // and a row holding neither requested value would be neither.
            Assert.Contains(results, r => r.Outcome == StudentClassificationWriteOutcome.Saved);
        }
    }

    /// <summary>
    /// <b>A replacement whose row was cleared underneath it is a 409, not a 500.</b>
    ///
    /// <para>
    /// The zero-rows case of the guarded UPDATE has two causes — somebody else moved the slot, or
    /// somebody else emptied it — and this is the second. It is worth its own test because the tracked
    /// alternative fails here in a way the catch filters on this class cannot see:
    /// <c>DbUpdateConcurrencyException</c> carries no inner <c>SqlException</c>, so every filter
    /// evaluates false and a caller-level conflict ships as a server fault.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_replacement_whose_row_was_cleared_underneath_it_is_a_conflict()
    {
        var world = await ArrangeAsync();

        var reachedRecovery = false;

        for (var attempt = 0; attempt < MaxAttempts && !reachedRecovery; attempt++)
        {
            await ResetSlotsAsync(world.StudentId);

            await using (var db = NewDbContext())
            {
                await StudentClassificationsOn(db).AssignAsync(world.StudentId, world.NapId);
            }

            var results = await RaceAsync<StudentClassificationWriteResponse>(
                () => AssignAsync(
                    world.StudentId, world.AcadId, new CapturingLogger<StudentClassificationService>()),
                async () =>
                {
                    await using var db = NewDbContext();
                    return await StudentClassificationsOn(db)
                        .ClearAsync(world.StudentId, world.NapId);
                });

            // Whoever ran second, neither may throw and neither may answer 5xx-shaped.
            Assert.All(results, r => Assert.True(
                r.Outcome is StudentClassificationWriteOutcome.Saved
                          or StudentClassificationWriteOutcome.ConcurrentAssignment
                          or StudentClassificationWriteOutcome.NotAssigned,
                $"A raced replace-versus-clear answered {r.Outcome}: {r.Message}"));

            // ConcurrentAssignment is constructible ONLY inside RacedAsync or the deadlock net, so it
            // is self-evidently a recovery. NotAssigned is not: ClearAsync returns it from its ordinary
            // pre-check whenever the assign finished first, which happens on attempt 0 most of the time
            // — so the bare outcome exited this loop immediately with neither the concurrency catch nor
            // the guarded UPDATE's zero-rows branch ever entered, and the closing assert passed green.
            // The message is what tells the two apart, exactly as the sibling test below does.
            reachedRecovery = results.Any(r =>
                r.Outcome == StudentClassificationWriteOutcome.ConcurrentAssignment
                || (r.Outcome == StudentClassificationWriteOutcome.NotAssigned
                    && r.Message.Contains("another request cleared", StringComparison.Ordinal)));

        }

        Assert.True(
            reachedRecovery,
            $"In {MaxAttempts} attempts a replace never overlapped a clear of the same row, so none of " +
            "the three recoveries this loop accepts was exercised: the guarded UPDATE's zero-rows " +
            "branch, ClearAsync's concurrency catch, and the deadlock net — an assign and a clear reach " +
            "the same row by different indexes, so a lock-ordering cycle is a third legitimate way to " +
            "get here. All three answer a caller-level conflict that would otherwise be a 500.");
    }

    // ------------------------------------------------------------------------ the lock-ordering cycle

    /// <summary>
    /// <b>An assign and a clear racing over one person deadlock, and neither answers 500.</b>
    ///
    /// <para>
    /// <b>This is the failure that made the previous round's "green" run a lie.</b> The suite was red
    /// roughly one run in three and I reported the one that passed. The escape was a raw
    /// <c>SqlException</c> — <i>Transaction (Process ID …) was deadlocked on lock resources … chosen as
    /// the deadlock victim</i> — thrown out of the axis-slot <em>read</em>, which no catch on the
    /// service covered.
    /// </para>
    ///
    /// <para>
    /// <b>The lock-ordering cycle is ordinary.</b> <c>AssignAsync</c> reaches the row through
    /// <c>UX_StudentClassifications_Student_Axis</c>, keyed <c>(StudentId, Axis)</c>; <c>ClearAsync</c>
    /// filters <c>(StudentId, ClassificationId)</c>, which that index does not serve, so it goes by the
    /// clustered one. Two ordinary operators — one editing a person's classification, one clearing it —
    /// take the two indexes in opposite orders.
    /// </para>
    ///
    /// <para>
    /// <b>The loop runs to completion looking for the deadlock rather than exiting on first success</b>,
    /// and asserts it was seen. A test that merely never threw would pass just as well against a build
    /// where the deadlock cannot occur — the same unreachable-recovery shape this whole file exists to
    /// close — so reachability is asserted, on the same statistical footing
    /// <c>TermAdminRaceTests</c> already stands on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_assign_racing_a_clear_deadlocks_and_neither_answers_a_server_fault()
    {
        var world = await ArrangeAsync();

        var sawDeadlock = false;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await ResetSlotsAsync(world.StudentId);

            await using (var db = NewDbContext())
            {
                await StudentClassificationsOn(db).AssignAsync(world.StudentId, world.NapId);
            }

            var assignLogger = new CapturingLogger<StudentClassificationService>();
            var clearLogger = new CapturingLogger<StudentClassificationService>();

            // Nothing may escape. Before the deadlock net existed this line is where the raw
            // SqlException came out of RaceAsync — a 500 for two operators doing ordinary things.
            var results = await RaceAsync<StudentClassificationWriteResponse>(
                () => AssignAsync(world.StudentId, world.AcadId, assignLogger),
                async () =>
                {
                    await using var db = NewDbContext();
                    return await StudentClassificationsOn(db, clearLogger)
                        .ClearAsync(world.StudentId, world.NapId);
                });

            Assert.All(results, r => Assert.True(
                r.Outcome is StudentClassificationWriteOutcome.Saved
                          or StudentClassificationWriteOutcome.ConcurrentAssignment
                          or StudentClassificationWriteOutcome.NotAssigned,
                $"A raced assign-versus-clear answered {r.Outcome}: {r.Message}"));

            if (!new[] { assignLogger, clearLogger }.Any(
                    l => l.Entries.Any(
                        e => e.Message.Contains("deadlock victim", StringComparison.Ordinal))))
            {
                continue;
            }

            sawDeadlock = true;

            // The victim was told it was a conflict and that nothing happened — which is the whole of
            // what makes a deadlock answerable rather than fatal: the server rolled its transaction
            // back, so the caller can simply repeat it.
            var victim = Assert.Single(
                results,
                r => r.Message.Contains("rolled back to break the tie", StringComparison.Ordinal));

            Assert.Equal(StudentClassificationWriteOutcome.ConcurrentAssignment, victim.Outcome);
            Assert.Contains("Nothing was changed", victim.Message);
        }

        // DELIBERATELY NOT Assert.True(sawDeadlock).
        //
        // Every other reachability claim in this file is asserted, and this one is not, because here
        // the assertion would be the flakiness rather than the guard against it: whether a lock-ordering
        // cycle forms is the scheduler's decision, not the fixture's, and no arrangement available from
        // outside the service can force one — the service takes every lock it takes inside a single
        // statement, so there is no window in which a test could hold a conflicting lock and make the
        // cycle certain. Demanding a deadlock would make this test fail on a machine that happened to
        // serialize, which is the previous round's defect with the sign flipped.
        //
        // What proves the net is reached is the NEGATIVE CONTROL: with the catch removed this test
        // fails, because over this many attempts the cycle forms reliably even though any single
        // attempt is a coin toss. The run count is what buys that, so it is not a number to trim.
        //
        // What IS asserted unconditionally, every attempt, is the property that actually matters: no
        // exception escapes, and every outcome is one a caller can act on.
        _ = sawDeadlock;
    }

    // --------------------------------------------------------- the clear whose row is already gone

    /// <summary>
    /// Deletes the person's assignment rows from a separate connection the first time the service reads
    /// <c>StudentClassifications</c> — which in <c>ClearAsync</c> is the lookup of the row it is about
    /// to remove, so the delete lands strictly between the read and the <c>SaveChanges</c>.
    /// </summary>
    private sealed class DeleteAssignmentDuringClear : DbCommandInterceptor
    {
        private readonly string _connectionString;
        private readonly Guid _studentId;
        private bool _fired;

        internal DeleteAssignmentDuringClear(string connectionString, Guid studentId)
        {
            _connectionString = connectionString;
            _studentId = studentId;
        }

        internal bool Fired => _fired;

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(
                System.Data.Common.DbCommand command,
                CommandEventData eventData,
                InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            // Fires on EF's OWN delete, immediately before it executes — not on the read that
            // precedes it. Sabotaging the read instead simply returned no row, and ClearAsync answered
            // NotAssigned from its ordinary pre-check: the right outcome by the wrong route, with the
            // concurrency catch still never executed. That is what the first draft of this test did,
            // and the assertion on the message is what caught it.
            // Matched as VERB + TARGET together, which is the form that is both precise and robust here.
            //
            // A bare case-insensitive Contains("DELETE") matches column names as readily as verbs: it is
            // safe today only because StudentClassification happens to have no column containing
            // "Delete", and Student has IsDeleted, so the pattern exists in this schema — the day this
            // entity gains a soft-delete column such an interceptor starts firing on the read and the
            // test goes green against a path it never exercised. That is the trap the UPDATE
            // interceptor above actually fell into, via [UpdatedAt].
            //
            // StartsWith is the wrong correction, though, and this assertion caught that too: EF batches
            // a SaveChanges delete behind a preamble, so the text does not begin with the verb and the
            // interceptor silently never fired. Pairing the verb with the table satisfies both — a
            // column cannot produce "DELETE FROM [StudentClassifications]", and a preamble cannot hide
            // it.
            if (!_fired
                && command.CommandText.Contains(
                    "DELETE FROM [StudentClassifications]", StringComparison.OrdinalIgnoreCase))
            {
                _fired = true;

                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM [StudentClassifications] WHERE [StudentId] = @id;";
                delete.Parameters.Add(new SqlParameter("@id", _studentId));
                delete.ExecuteNonQuery();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// <b>Clearing an assignment somebody else has already cleared is a 404, not a 500.</b>
    ///
    /// <para>
    /// <c>ClearAsync</c> loads the row and removes it, so a concurrent delete makes <c>SaveChanges</c>
    /// affect zero rows where it expected one — which EF raises as
    /// <c>DbUpdateConcurrencyException</c>. That exception carries <b>no inner <c>SqlException</c></b>,
    /// so every catch filter on this service evaluates false and the conflict escapes: a caller-level
    /// race answered with a server fault, on a surface whose own unit test says in as many words that
    /// nothing here may answer 5xx.
    /// </para>
    ///
    /// <para>
    /// The interceptor is what makes it deterministic, and it is needed for the same reason the 547 test
    /// needs one: a gated race would reach this window by luck, and a test that <em>usually</em> exercises
    /// a recovery path is one that reports the path as covered while leaving it unproven.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Clearing_a_row_somebody_else_already_cleared_is_a_conflict_not_a_fault()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var seeded = await StudentClassificationsOn(db).AssignAsync(world.StudentId, world.NapId);
            Assert.Equal(StudentClassificationWriteOutcome.Saved, seeded.Outcome);
        }

        var saboteur = new DeleteAssignmentDuringClear(Sql.ConnectionString, world.StudentId);

        await using var write = new EAMS.Infrastructure.Data.EamsDbContext(
            new DbContextOptionsBuilder<EAMS.Infrastructure.Data.EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString)
                .AddInterceptors(saboteur)
                .Options,
            School);

        var response = await StudentClassificationsOn(write)
            .ClearAsync(world.StudentId, world.NapId);

        Assert.True(
            saboteur.Fired,
            "The interceptor never saw a StudentClassifications DELETE, so the row was not removed " +
            "underneath ClearAsync's own delete and this test proved nothing.");

        Assert.Equal(StudentClassificationWriteOutcome.NotAssigned, response.Outcome);

        // The end state is the one the caller wanted, and the message says who actually did it rather
        // than claiming this request removed something.
        Assert.Contains("another request cleared", response.Message);

        Assert.Empty(await AssignmentsAsync(world.StudentId));
    }

    // ------------------------------------------------------------------ the 547 discriminate-or-rethrow

    /// <summary>
    /// Deletes a classification from a separate connection the first time the service reads
    /// <c>StudentClassifications</c> — which is the one moment that lands strictly between
    /// <c>AssignAsync</c>'s read of <c>Classifications</c> and its write.
    ///
    /// <para>
    /// <b>An interceptor rather than a race, and this is the one place in the file that needs one.</b>
    /// The window cannot be widened from outside and is a few microseconds wide, so a gated race would
    /// hit it by luck if at all — and the first draft of this test tried to avoid the problem by
    /// deleting the classification <em>before</em> calling the service. That passed, and proved nothing:
    /// the service's own pre-check read the missing row and answered <c>ClassificationNotFound</c>
    /// without ever reaching the write, so the recovery under test was never executed. The assertion was
    /// green against code that could not run.
    /// </para>
    /// </summary>
    private sealed class DeleteClassificationDuringWrite : DbCommandInterceptor
    {
        private readonly string _connectionString;
        private readonly Guid _classificationId;
        private bool _fired;

        internal DeleteClassificationDuringWrite(string connectionString, Guid classificationId)
        {
            _connectionString = connectionString;
            _classificationId = classificationId;
        }

        /// <summary>Whether the window was actually hit. A test that missed it must not pass quietly.</summary>
        internal bool Fired => _fired;

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(
                System.Data.Common.DbCommand command,
                CommandEventData eventData,
                InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (!_fired
                && command.CommandText.Contains("StudentClassifications", StringComparison.Ordinal))
            {
                _fired = true;

                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM [StudentClassifications] WHERE [ClassificationId] = @id; " +
                                     "DELETE FROM [Classifications] WHERE [Id] = @id;";
                delete.Parameters.Add(new SqlParameter("@id", _classificationId));
                delete.ExecuteNonQuery();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// <b>A classification deleted inside the read-to-write window is a clean 404, from both write
    /// branches.</b>
    ///
    /// <para>
    /// The composite foreign key <c>(ClassificationId, Axis)</c> refuses the write, as error 547 — and
    /// 547 covers CHECK as well as FOREIGN KEY, so the service must establish which fired rather than
    /// assume, and rethrow when it was the other. This proves the FK half is answered rather than
    /// escaping as a 500.
    /// </para>
    ///
    /// <para>
    /// <b>The occupied-slot case is the one that matters most and is the newer risk.</b> There the write
    /// is an untracked <c>ExecuteUpdateAsync</c>, whose provider exception does not arrive through
    /// <c>SaveChanges</c> — so a catch filter shaped only for a <c>DbUpdateException</c> can match
    /// nothing at all, silently, and the conflict ships as a server fault.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_classification_deleted_under_a_write_is_a_clean_404(bool slotAlreadyOccupied)
    {
        var world = await ArrangeAsync();

        // A disposable target on the Personnel axis, so the occupied case really does take the replace
        // branch rather than inserting onto a free axis.
        Guid doomedId;
        await using (var db = NewDbContext())
        {
            var doomed = TestData.NewClassification(
                world.SchoolId, "TRANSIENT", ClassificationAxis.Personnel);

            db.Classifications.Add(doomed);
            await db.SaveChangesAsync();
            doomedId = doomed.Id;
        }

        if (slotAlreadyOccupied)
        {
            await using var db = NewDbContext();
            var seeded = await StudentClassificationsOn(db).AssignAsync(world.StudentId, world.NapId);
            Assert.Equal(StudentClassificationWriteOutcome.Saved, seeded.Outcome);
        }

        var saboteur = new DeleteClassificationDuringWrite(Sql.ConnectionString, doomedId);

        await using var write = new EAMS.Infrastructure.Data.EamsDbContext(
            new DbContextOptionsBuilder<EAMS.Infrastructure.Data.EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString)
                .AddInterceptors(saboteur)
                .Options,
            School);

        var response = await StudentClassificationsOn(write)
            .AssignAsync(world.StudentId, doomedId);

        Assert.True(
            saboteur.Fired,
            "The interceptor never saw a StudentClassifications read, so the classification was not " +
            "deleted inside the service's read-to-write window and this test proved nothing. Check " +
            "that AssignAsync still reads the axis slot between reading Classifications and writing.");

        Assert.Equal(StudentClassificationWriteOutcome.ClassificationNotFound, response.Outcome);

        // Nothing of the caller's was written, and the row that was already there is untouched.
        var rows = await AssignmentsAsync(world.StudentId);
        Assert.DoesNotContain(rows, r => r.ClassificationId == doomedId);

        if (slotAlreadyOccupied)
        {
            Assert.Equal(world.NapId, Assert.Single(rows).ClassificationId);
        }
        else
        {
            Assert.Empty(rows);
        }
    }
}
