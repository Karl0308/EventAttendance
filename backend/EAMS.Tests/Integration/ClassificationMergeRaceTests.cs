using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Two merges racing over the same classification.
///
/// <para>
/// <b>Every other guard in <c>ClassificationService</c> has a constraint standing behind it; this one
/// does not.</b> A duplicate name loses to <c>UX_Classifications_SchoolId_NameKey</c>, a delete of a
/// held classification loses to a foreign key — in both cases the database is the arbiter and the
/// service merely turns its refusal into a tidy 409. A merge has no such backstop: every row it
/// produces is individually legal, so <c>CK_Classifications_MergedIsRetired</c> and every index in the
/// schema are satisfied by the corrupt outcome. The application-side condition <em>is</em> the guard,
/// which is exactly why it needs a test at this level.
/// </para>
///
/// <para>
/// <b>The corruption being raced for.</b> Merge L into V while somebody else merges V into W. Read
/// outside a transaction, both callers see a live, un-merged survivor and both proceed. The result is
/// the two-hop chain the method exists to refuse — and worse than untidy: if V's population is
/// repointed to W before L's is repointed to V, L's people land on a classification that is by then
/// retired and absent from every picker. Nobody is told.
/// </para>
///
/// <para>
/// <b>Asserted as an invariant over the final state, not as "who won".</b> Either ordering is a correct
/// outcome; a chain is not. A test that demanded a particular winner would be asserting a scheduling
/// accident, and would go red on a fast machine for the wrong reason.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ClassificationMergeRaceTests : IntegrationTest
{
    public ClassificationMergeRaceTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Both contenders start together, so they are inside their transactions at the same time. Lifted
    /// from <c>TermAdminRaceTests</c>, which races the same way against an index.
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
    /// Three same-axis classifications and a person on each of the first two, so a stranding is
    /// visible rather than merely possible.
    /// </summary>
    private async Task<(Guid L, Guid V, Guid W)> ArrangeChainCandidatesAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var l = TestData.NewClassification(school.Id, "ACADEMIC STAFF", ClassificationAxis.Personnel);
        var v = TestData.NewClassification(school.Id, "ACAD", ClassificationAxis.Personnel);
        var w = TestData.NewClassification(school.Id, "TEACHING STAFF", ClassificationAxis.Personnel);
        db.Classifications.AddRange(l, v, w);

        var onL = TestData.NewStudent(school.Id, studentNumber: "2023-0001");
        var onV = TestData.NewStudent(school.Id, studentNumber: "2023-0002");
        db.Students.AddRange(onL, onV);

        db.StudentClassifications.AddRange(
            TestData.NewStudentClassification(onL.Id, l),
            TestData.NewStudentClassification(onV.Id, v));

        await db.SaveChangesAsync();

        return (l.Id, v.Id, w.Id);
    }

    /// <summary>
    /// A merge on its own context, so the two contenders do not share a change tracker or a
    /// connection — which is what makes this a database race rather than an EF one.
    /// </summary>
    private async Task<ClassificationMergeResponse> MergeAsync(Guid loser, Guid survivor)
    {
        await using var db = NewDbContext();
        return await ClassificationsOn(db).MergeAsync(loser, survivor);
    }

    /// <summary>
    /// <b>L into V, racing V into W: no two-hop chain survives, and nobody is stranded.</b>
    /// </summary>
    [Fact]
    public async Task Two_merges_over_one_classification_cannot_build_a_chain()
    {
        var (l, v, w) = await ArrangeChainCandidatesAsync();

        var results = await RaceAsync(
            () => MergeAsync(l, v),
            () => MergeAsync(v, w));

        // Neither contender may fail in a way a caller cannot act on. A loser is answered
        // NotMergeable; what must never happen is an exception escaping to become a 500.
        Assert.All(results, r => Assert.True(
            r.Outcome is ClassificationWriteOutcome.Saved
                      or ClassificationWriteOutcome.NotMergeable,
            $"A raced merge answered {r.Outcome}: {r.Message}"));

        await using var read = NewDbContext();

        var rows = await read.Classifications.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c);

        // THE invariant: no classification may point at a classification that itself points somewhere.
        foreach (var row in rows.Values)
        {
            if (row.MergedIntoClassificationId is not { } target) continue;

            // The message is built only on the failing branch. Assert.True evaluates its message
            // argument eagerly, so composing it inline would dereference the second hop's null on
            // every healthy run — which is exactly what this test did on its first green pass, and a
            // reminder that a failure message is code that runs when nothing is wrong.
            if (rows[target].MergedIntoClassificationId is { } secondHop)
            {
                Assert.Fail(
                    $"'{row.Name}' was merged into '{rows[target].Name}', which has itself been " +
                    $"merged into '{rows[secondHop].Name}'. That is the two-hop chain both the " +
                    "service and this test exist to refuse, and it means one population was moved " +
                    "onto a row that had already been emptied and retired.");
            }
        }

        // And nobody was stranded: every assignment sits on a classification that is still live.
        var assignments = await read.StudentClassifications.AsNoTracking()
            .Select(sc => sc.ClassificationId)
            .ToListAsync();

        Assert.Equal(2, assignments.Count);

        // The message is built only on the failing branch, matching the chain assertion above. Here it
        // cannot throw — `rows` is unfiltered, the foreign key guarantees every assignment's
        // classification is in it, and Name/IsActive are non-nullable — so this is about not composing
        // a paragraph on every healthy run, and about the two assertions in this file reading the same
        // way rather than one of them looking deliberately different.
        foreach (var id in assignments)
        {
            if (rows[id].IsActive) continue;

            Assert.Fail(
                $"An assignment was left on '{rows[id].Name}', which is retired and therefore absent " +
                "from every picker. This is what the chain costs in practice — the people are not " +
                "lost, they are filed under a category nobody can choose or see.");
        }
    }

    /// <summary>
    /// <b>Two merges of the SAME loser into different survivors: exactly one wins, and the tombstone
    /// names the survivor the population actually went to.</b>
    ///
    /// <para>
    /// Without the affected-row check on the conditional retire, both callers' repoints run and the
    /// second overwrites the first's tombstone — leaving a row that names a survivor its people never
    /// went to, which is worse than either merge alone because the audit trail is now actively wrong.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_merges_of_one_loser_leave_a_tombstone_that_tells_the_truth()
    {
        var (l, v, w) = await ArrangeChainCandidatesAsync();

        var results = await RaceAsync(
            () => MergeAsync(l, v),
            () => MergeAsync(l, w));

        Assert.All(results, r => Assert.True(
            r.Outcome is ClassificationWriteOutcome.Saved
                      or ClassificationWriteOutcome.NotMergeable,
            $"A raced merge answered {r.Outcome}: {r.Message}"));

        Assert.Equal(1, results.Count(r => r.Outcome == ClassificationWriteOutcome.Saved));

        await using var read = NewDbContext();

        var loser = await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == l);
        Assert.NotNull(loser.MergedIntoClassificationId);

        // The population and the tombstone must agree. This is the assertion the affected-row check
        // buys: the row says where its people went, and that is where they are.
        var wherePeopleWent = await read.StudentClassifications.AsNoTracking()
            .Where(sc => sc.ClassificationId == v || sc.ClassificationId == w)
            .Select(sc => sc.ClassificationId)
            .Distinct()
            .ToListAsync();

        Assert.Contains(loser.MergedIntoClassificationId!.Value, wherePeopleWent);

        Assert.True(
            await read.Classifications.AsNoTracking().CountAsync() == 3,
            "A raced merge deleted a classification. Merging never deletes.");
    }
}
