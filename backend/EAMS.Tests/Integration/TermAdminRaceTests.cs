using System.Diagnostics;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The one race on D-53's write surface, and the reason it is a separate file from
/// <see cref="TermAdminApiTests"/>.
///
/// <para>
/// <b><c>TermAdminService.CreateAsync</c> refuses a duplicate code twice</b> — a pre-check that reads
/// <c>Terms</c> before staging the row, and a <c>catch</c> on the unique violation from
/// <c>UX_Terms_SchoolId_Code</c>. Every sequential duplicate test in the suite is resolved by the
/// first, so the second is unreached code that no amount of ordinary coverage touches. Deleting it
/// would leave the whole suite green and turn the first genuine collision — two operators setting up
/// next semester in the same second — into an unhandled <c>DbUpdateException</c>, i.e. the 500 that
/// D-53 specifically promises this route will not return.
/// </para>
///
/// <para>
/// <b>So the test asserts that the handler ran, not merely that the outcome was right.</b> Both paths
/// produce an identical <see cref="TermWriteOutcome.TermCodeExists"/>, which is exactly what makes an
/// outcome-shaped assertion worthless here: it passes with the <c>catch</c> removed. The service logs
/// when it recovers, and that log entry is the only observable difference between the two — which is
/// what <see cref="CapturingLogger{T}"/> exists for.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TermAdminRaceTests : IntegrationTest
{
    public TermAdminRaceTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Six, not two. Every contender that passes its pre-check before the winner commits ends up
    /// blocked on the winner's exclusive key lock and then handed a 2627 — so raising the count raises
    /// the number of callers that reach the handler, rather than only the chance that one does.
    /// </summary>
    private const int Contenders = 6;

    /// <summary>
    /// <b>Why the race is attempted repeatedly instead of once.</b> Whether a given contender is
    /// refused by the pre-check or by the index depends on thread scheduling, so a single attempt that
    /// happened to serialize would fail an assertion about the handler having run — a flake, and the
    /// kind that gets a real test deleted. Re-attempting on a fresh code makes "the handler is never
    /// reached" the only way to exhaust the loop, which is the fact worth failing on.
    /// </summary>
    private const int MaxAttempts = 8;

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private static TermWriteRequest Request(string code) =>
        new(code, "2025-2026", "1st Semester", null, null);

    /// <summary>
    /// Starts every task at once and returns their results. The gate is released only after all of them
    /// are parked on it, so the first database call each one makes happens within microseconds of the
    /// others'.
    ///
    /// <para>
    /// The same helper <c>StudentWriteRaceTests</c> carries, and duplicated rather than shared for the
    /// reason that file records: without the gate the tasks start in sequence as the enumerable is
    /// materialized, the first finishes comfortably before the second begins, and the test passes green
    /// having never produced the race it is named for.
    /// </para>
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

    /// <summary>
    /// <b>Six simultaneous creates of one term code leave one term, and the losers are refused by the
    /// index rather than by the read that preceded it.</b>
    ///
    /// <para>
    /// Three things have to hold on every attempt, and they fail in three different ways:
    /// <list type="bullet">
    ///   <item>Nothing throws. With the <c>catch</c> deleted this is where it goes — the losing
    ///   <c>SaveChangesAsync</c> raises <c>DbUpdateException</c> straight out of
    ///   <see cref="RaceAsync"/>, which is a 500 at the controller.</item>
    ///   <item>Exactly one caller is told it saved, and the rest are told the code is taken. A
    ///   handler that swallowed the violation and reported success would leave five operators
    ///   believing they had created a term.</item>
    ///   <item>Exactly one row exists. The index is what guarantees it; this is the assertion that
    ///   would object if it were ever dropped or made filtered.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Each contender gets its own logger rather than sharing one:
    /// <see cref="CapturingLogger{T}"/> appends to a plain list, and six threads writing to it is a
    /// corrupted assertion rather than a shared observation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_creates_of_one_code_are_refused_by_the_index_not_only_by_the_pre_check()
    {
        await ArrangeSchoolAsync();

        var recovered = 0;

        for (var attempt = 0; attempt < MaxAttempts && recovered == 0; attempt++)
        {
            var code = $"2025-2026-{attempt}";

            var loggers = Enumerable.Range(0, Contenders)
                .Select(_ => new CapturingLogger<TermAdminService>())
                .ToArray();

            var responses = await RaceAsync(loggers.Select(logger =>
                new Func<Task<TermWriteResponse>>(async () =>
                {
                    await using var db = NewDbContext();
                    return await TermsOn(db, logger).CreateAsync(Request(code));
                })).ToArray());

            Assert.Single(responses, r => r.Outcome == TermWriteOutcome.Saved);
            Assert.Equal(
                Contenders - 1,
                responses.Count(r => r.Outcome == TermWriteOutcome.TermCodeExists));

            await using (var read = NewDbContext())
            {
                Assert.Single(await read.Terms.AsNoTracking().Where(t => t.Code == code).ToListAsync());
            }

            recovered = loggers.Count(l => l.Entries.Count > 0);
        }

        Assert.True(
            recovered > 0,
            $"In {MaxAttempts} attempts of {Contenders} simultaneous creates, no caller was ever " +
            "refused by UX_Terms_SchoolId_Code — every duplicate was caught by CreateAsync's " +
            "pre-check, so the unique-violation handler behind it is untested by this run. Either the " +
            "contenders are being serialized (check that RaceAsync's gate still releases them " +
            "together) or the handler is unreachable, in which case a real collision returns a 500.");
    }

    // ------------------------------------------------------- the rename race (UpdateAsync's catch)

    /// <summary>
    /// <b>Six simultaneous renames onto one free code leave one term holding it, and the losers are
    /// refused by the index rather than by the read that preceded it.</b>
    ///
    /// <para>
    /// The create race above proves <c>CreateAsync</c>'s handler is reachable. <c>UpdateAsync</c> carries
    /// the identical pre-check-plus-<c>catch</c> pair on the rename path and nothing reached its
    /// <c>catch</c>: every sequential rename test in <see cref="TermAdminApiTests"/> is resolved by the
    /// pre-check, so deleting the handler left the whole suite green. That is the same unreached-code
    /// shape the create race was written for, and it fails the same way — the loser's
    /// <c>SaveChangesAsync</c> raises <c>DbUpdateException</c> and the operator gets a 500 where D-53
    /// promises a 409.
    /// </para>
    ///
    /// <para>
    /// <b>A free code, not a taken one.</b> Renaming onto a code some other term already holds is decided
    /// by the pre-check every time and can never reach the index; the collision that can only be decided
    /// by <c>UX_Terms_SchoolId_Code</c> is two renames aiming at the same <em>unused</em> code, where
    /// both pre-checks are truthfully answered "free" before either row is written.
    /// </para>
    ///
    /// <para>
    /// The terms are arranged once and reused across attempts on a fresh target code each time. A
    /// contender that lost still holds its original code, and the winner's row simply carries the
    /// previous attempt's target — neither state makes the next attempt's pre-check answer differently,
    /// which is what lets the retry loop stay this short.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_renames_onto_one_free_code_are_refused_by_the_index_not_only_by_the_pre_check()
    {
        var schoolId = await ArrangeSchoolAsync();
        var termIds = await ArrangeTermsAsync(schoolId, "seed", isCurrent: false);

        var recovered = 0;

        for (var attempt = 0; attempt < MaxAttempts && recovered == 0; attempt++)
        {
            var code = $"2026-2027-{attempt}";

            var loggers = Enumerable.Range(0, Contenders)
                .Select(_ => new CapturingLogger<TermAdminService>())
                .ToArray();

            var results = await RaceCapturingAsync(termIds.Select((id, i) =>
                new Func<Task<TermWriteResponse>>(async () =>
                {
                    await using var db = NewDbContext();
                    return await TermsOn(db, loggers[i]).UpdateAsync(id, Request(code));
                })).ToArray());

            AssertNothingThrew(results, $"renaming {Contenders} terms onto the free code '{code}'");

            var responses = results.Select(r => r.Response!).ToArray();

            Assert.Single(responses, r => r.Outcome == TermWriteOutcome.Saved);
            Assert.Equal(
                Contenders - 1,
                responses.Count(r => r.Outcome == TermWriteOutcome.TermCodeExists));

            await using (var read = NewDbContext())
            {
                Assert.Single(await read.Terms.AsNoTracking().Where(t => t.Code == code).ToListAsync());
            }

            recovered = loggers.Count(l => l.Entries.Count > 0);
        }

        Assert.True(
            recovered > 0,
            $"In {MaxAttempts} attempts of {Contenders} simultaneous renames onto one free code, no " +
            "caller was ever refused by UX_Terms_SchoolId_Code — every collision was caught by " +
            "UpdateAsync's pre-check, so the unique-violation handler behind it is untested by this " +
            "run. Either the contenders are being serialized (check that RaceCapturingAsync's gate " +
            "still releases them together) or the handler is unreachable, in which case a real " +
            "collision on a rename returns a 500.");
    }

    // ------------------------------------------------ the current-flag race (UX_Terms_SchoolId_Current)

    /// <summary>
    /// <b>Six simultaneous <c>PATCH /current</c> calls onto six different terms of one school leave
    /// exactly one current term, and none of them throws.</b>
    ///
    /// <para>
    /// <c>TermAdminService</c>'s class remark states that <c>UX_Terms_SchoolId_Current</c> "is never
    /// allowed to reject anything… there is no violation to recover from", on the grounds that the flag
    /// moves in a single <c>UPDATE</c>. <b>That is established for one statement in isolation, not for
    /// two concurrent ones</b>, and this file's whole existence is the argument that those are different
    /// questions: for the <em>unfiltered</em> index the same class argues at length that a pre-check is
    /// not a guard because two statements race, and then argues the opposite for the filtered one
    /// without a test either way.
    /// </para>
    ///
    /// <para>
    /// <b>The mechanism the test is aimed at.</b> Under READ COMMITTED each contender's statement scans
    /// <c>WHERE SchoolId = @s AND (Id = @id OR IsCurrent = 1)</c>. Whether a contender that blocked on
    /// the winner's exclusive key lock re-qualifies the winner's newly-current row once it unblocks is a
    /// property of the plan and of lock-release timing, not of anything this code states. If it does not
    /// re-qualify, both rows land <c>IsCurrent = 1</c> and the filtered index raises 2627 — on the one
    /// route whose entire justification is that the move is index-safe.
    /// </para>
    ///
    /// <para>
    /// <b>Note what such a failure would look like, because it is not the shape the create path
    /// handles.</b> <c>ExecuteUpdateAsync</c> does not go through the change tracker's save pipeline, so
    /// a 2627 from it surfaces as a raw <c>SqlException</c> rather than wrapped in
    /// <c>DbUpdateException</c>. <see cref="AssertNothingThrew"/> therefore reports the exception type it
    /// saw instead of asserting one, and the assertion is "nothing threw" rather than "no
    /// <c>DbUpdateException</c> threw" — a test written to the create path's shape would miss this
    /// entirely.
    /// </para>
    ///
    /// <para>
    /// <b>Overlap is asserted, not assumed</b>, for the reason the create race states about its own
    /// recovery counter: a run whose contenders happened to serialize proves nothing about concurrency
    /// and would sit here green forever. There is no log entry to count on this path — the service
    /// recovers from nothing, which is the claim under test — so the observable is that at least two
    /// calls were genuinely in flight at once. That is weaker than "their <c>UPDATE</c> statements
    /// interleaved at the index", and deliberately stated as what it is: it is the strongest fact
    /// obtainable without reading the server's own lock state, and it is enough to distinguish a run that
    /// exercised concurrency from one that did not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_current_flag_moves_leave_exactly_one_current_term_and_throw_nothing()
    {
        var schoolId = await ArrangeSchoolAsync();

        // Every attempt runs, unlike the two duplicate-code races above, and the difference is not an
        // oversight. Those stop as soon as a contender has been refused by the index, because that is
        // the fact they exist to observe and one occurrence proves it. Here there is no such event to
        // wait for — the claim under test is that a violation *never* happens — so an early exit would
        // reduce the test to a single race, and a defect that appears on one interleaving in ten would
        // sit green. Eight attempts of six is 48 concurrent moves per run, for about half a second.
        var overlapped = false;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // A fresh set per attempt, none of them current. Previous attempts' terms are left in
            // place: one of them holds the flag, so every attempt after the first also has an
            // incumbent to displace, which is the production shape.
            var termIds = await ArrangeTermsAsync(schoolId, $"2027-2028-{attempt}", isCurrent: false);

            var results = await RaceCapturingAsync(termIds.Select(id =>
                new Func<Task<TermWriteResponse>>(async () =>
                {
                    await using var db = NewDbContext();
                    return await TermsOn(db).SetCurrentAsync(id, isCurrent: true);
                })).ToArray());

            AssertNothingThrew(
                results,
                $"making {Contenders} terms of one school current simultaneously");

            // Every caller is told it saved: each asked for a legal move of a flag it did not hold, and
            // losing the ordering to another caller is not a refusal — the last one to commit simply
            // wins. An outcome other than Saved here would mean a contender was refused by something.
            Assert.All(results, r => Assert.Equal(TermWriteOutcome.Saved, r.Response!.Outcome));

            List<Guid> current;
            await using (var read = NewDbContext(new TestSchoolContext()))
            {
                current = await read.Terms.AsNoTracking()
                    .Where(t => t.SchoolId == schoolId && t.IsCurrent)
                    .Select(t => t.Id)
                    .ToListAsync();
            }

            Assert.True(
                current is [var only] && termIds.Contains(only),
                $"After {Contenders} simultaneous current-flag moves the school has {current.Count} " +
                "current term(s), expected exactly one of the six that were asked for. Two would mean " +
                "the filtered index UX_Terms_SchoolId_Current was left holding a state it is supposed " +
                "to forbid; zero would mean every term-defaulting read in the system now answers empty.");

            overlapped |= AnyOverlap(results);
        }

        Assert.True(
            overlapped,
            $"In {MaxAttempts} attempts of {Contenders} simultaneous current-flag moves, no two calls " +
            "were ever in flight at the same time — the contenders serialized, so this run says " +
            "nothing about whether UX_Terms_SchoolId_Current can be broken by concurrency. Check that " +
            "RaceCapturingAsync's gate still releases them together and that nothing has introduced an " +
            "ambient transaction or a shared DbContext across the contenders. This is not a claim that " +
            "the production code is wrong; it is a claim that the test stopped testing it.");
    }

    // --------------------------------------------------------------------------------- plumbing

    /// <summary>Adds <see cref="Contenders"/> terms under one school and returns their ids, in order.</summary>
    private async Task<Guid[]> ArrangeTermsAsync(Guid schoolId, string codePrefix, bool isCurrent)
    {
        await using var db = NewDbContext();

        var terms = Enumerable.Range(0, Contenders)
            .Select(i => TestData.NewTerm(schoolId, $"{codePrefix}-{i}", isCurrent))
            .ToArray();

        db.Terms.AddRange(terms);
        await db.SaveChangesAsync();

        return terms.Select(t => t.Id).ToArray();
    }

    /// <summary>
    /// One contender's run: what it answered <em>or</em> what it threw, and the window it occupied.
    ///
    /// <para>
    /// Timestamps are <see cref="Stopwatch"/> ticks rather than wall-clock: the only question asked of
    /// them is whether two windows overlapped, and a monotonic source cannot answer it wrongly because
    /// the system clock stepped mid-race.
    /// </para>
    /// </summary>
    private readonly record struct Timed(
        TermWriteResponse? Response, Exception? Error, long Started, long Ended);

    /// <summary>
    /// <see cref="RaceAsync{T}"/> with two additions the current-flag race needs and the create race did
    /// not: the failure is captured instead of propagated, and each call is timed.
    ///
    /// <para>
    /// <b>Capturing rather than propagating is the point.</b> <c>Task.WhenAll</c> surfaces one
    /// exception out of six and discards which contender it belonged to and what the other five did — and
    /// "one of six threw a <c>SqlException</c> while the remaining five saved and the table was left
    /// with two current rows" is the entire finding this test exists to produce. It is not a silent
    /// catch: every captured exception is re-reported, with its type and message, by
    /// <see cref="AssertNothingThrew"/>.
    /// </para>
    /// </summary>
    private static async Task<Timed[]> RaceCapturingAsync(
        params Func<Task<TermWriteResponse>>[] contenders)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = contenders.Select(contender => Task.Run(async () =>
        {
            await gate.Task;

            var started = Stopwatch.GetTimestamp();
            try
            {
                var response = await contender();
                return new Timed(response, null, started, Stopwatch.GetTimestamp());
            }
            catch (Exception ex)
            {
                return new Timed(null, ex, started, Stopwatch.GetTimestamp());
            }
        })).ToArray();

        gate.SetResult();
        return await Task.WhenAll(running);
    }

    /// <summary>
    /// Fails with every exception that was raised, named by type, rather than with the first one.
    /// </summary>
    private static void AssertNothingThrew(IReadOnlyList<Timed> results, string what)
    {
        var thrown = results.Where(r => r.Error is not null).Select(r => r.Error!).ToList();
        if (thrown.Count == 0) return;

        Assert.Fail(
            $"{thrown.Count} of {results.Count} callers threw while {what}. A caller that throws here " +
            "is a 500 at the controller on a route D-53 promises will not return one. What was " +
            "raised:" + Environment.NewLine +
            string.Join(
                Environment.NewLine,
                thrown.Select(e => $"  - {e.GetType().FullName}: {e.Message}")));
    }

    /// <summary>
    /// Whether any two calls were in flight at the same moment. Half-open intervals, so two calls that
    /// merely abutted do not count as overlapping.
    /// </summary>
    private static bool AnyOverlap(IReadOnlyList<Timed> results)
    {
        for (var i = 0; i < results.Count; i++)
        {
            for (var j = i + 1; j < results.Count; j++)
            {
                if (results[i].Started < results[j].Ended && results[j].Started < results[i].Ended)
                    return true;
            }
        }

        return false;
    }
}
