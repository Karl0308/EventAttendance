using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Merging two classifications into one.
///
/// <para>
/// <b>Why the product needs this at all.</b> The seeded vocabulary came out of an access-control export
/// that spells things inconsistently, and the first thing anyone does with an editable list is add a
/// row that turns out to duplicate one already there. Without a merge the only recoveries are "retire
/// one and leave the population split across two rows" or "delete it" — and the delete is correctly
/// refused the moment anything references it.
/// </para>
///
/// <para>
/// <b>The load-bearing assertion in this file is a count of rows, repeated.</b> The promise is that a
/// merge <em>moves</em> and never <em>destroys</em>, and a merge implementation that deleted the losing
/// row would return an identical, entirely correct-looking response. Only the table can tell the
/// difference, so the table is what is asserted.
/// </para>
///
/// <para>
/// <b>Where the "nobody loses their classification" criterion is asserted:</b>
/// <see cref="ClassificationAssignmentTests.A_merge_moves_every_assignment_and_loses_nobody"/>, not
/// here. This file is about the vocabulary rows — which survive, which are retired, which refusals
/// fire; that one is about the people, and it arrived with the assignment junction. The split is
/// deliberate and the cross-reference is here because the phase-close gate reads these summaries as
/// the acceptance-criterion map, so a file that says an AC is unasserted had better be right.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ClassificationMergeTests : IntegrationTest
{
    public ClassificationMergeTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/classifications";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>
    /// Defaults to the <c>Personnel</c> axis because the merge cases below need two classifications
    /// that can legally be merged, and same-axis is that precondition. Tests about axes name theirs.
    /// </summary>
    private async Task<Guid> ArrangeClassificationAsync(
        Guid schoolId, string name, string axis = ClassificationAxis.Personnel, bool isActive = true)
    {
        await using var db = NewDbContext();
        var row = TestData.NewClassification(schoolId, name, axis, isActive);
        db.Classifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty(ClassificationsController.ErrorCodeProperty).GetString();
    }

    /// <summary>
    /// The prose half of an RFC 7807 body — the sentence an operator actually reads and acts on. Worth
    /// asserting on where a refusal makes a specific claim about what to do next: a wrong instruction
    /// there costs more than a wrong status code, because the caller believes it.
    /// </summary>
    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("detail").GetString() ?? "";
    }

    /// <summary>
    /// <b>The happy path, and every one of its four postconditions.</b>
    ///
    /// <para>
    /// Both rows still exist, the loser is retired, the loser names its survivor, and the survivor is
    /// untouched. They are asserted separately because they fail separately: a merge that deleted the
    /// loser, one that forgot the tombstone, and one that retired the wrong row all produce a 200.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_merge_retires_and_tombstones_the_loser_and_deletes_nothing()
    {
        var schoolId = await ArrangeSchoolAsync();
        var survivorId = await ArrangeClassificationAsync(schoolId, "ACAD");
        var loserId = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Route}/{loserId}/merge", new { intoClassificationId = survivorId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Both halves come back. A response carrying only the survivor would look exactly like a merge
        // that had deleted the loser, which is the one thing this operation promises not to do.
        Assert.Equal(survivorId, body.RootElement.GetProperty("survivor").GetProperty("id").GetGuid());
        Assert.Equal(loserId, body.RootElement.GetProperty("merged").GetProperty("id").GetGuid());

        await using var read = NewDbContext();

        var rows = await read.Classifications.AsNoTracking()
            .Where(c => c.SchoolId == schoolId)
            .ToListAsync();

        Assert.True(
            rows.Count == 2,
            $"The merge left {rows.Count} classifications where both were expected to survive. A merge " +
            "moves people between categories; it does not remove the category they came from, because " +
            "the tombstone is what lets an administrator see a mistaken merge and what lets an older " +
            "report still resolve the category it recorded.");

        var loser = rows.Single(c => c.Id == loserId);
        var survivor = rows.Single(c => c.Id == survivorId);

        Assert.False(loser.IsActive);
        Assert.NotNull(loser.RetiredAt);
        Assert.Equal(survivorId, loser.MergedIntoClassificationId);

        // The survivor is genuinely untouched — not merely still present. A merge that retired both
        // rows, or that stamped the survivor as merged into the loser, would pass every assertion
        // above.
        Assert.True(survivor.IsActive);
        Assert.Null(survivor.RetiredAt);
        Assert.Null(survivor.MergedIntoClassificationId);
    }

    /// <summary>
    /// The merged-away row disappears from the picker but stays readable by id and in the full list.
    ///
    /// <para>
    /// This is the same promise retirement makes, asserted on the merge path because the merge sets the
    /// flag by a different code path from <c>PATCH /active</c> and could regress independently.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_merged_classification_leaves_the_picker_and_stays_readable()
    {
        var schoolId = await ArrangeSchoolAsync();
        var survivorId = await ArrangeClassificationAsync(schoolId, "ACAD");
        var loserId = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        await client.PostAsJsonAsync(
            $"{Route}/{loserId}/merge", new { intoClassificationId = survivorId });

        using var picker = JsonDocument.Parse(
            await (await client.GetAsync(Route)).Content.ReadAsStringAsync());

        Assert.DoesNotContain(
            picker.RootElement.GetProperty("items").EnumerateArray(),
            c => c.GetProperty("id").GetGuid() == loserId);

        var byId = await client.GetAsync($"{Route}/{loserId}");
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);

        using var body = JsonDocument.Parse(await byId.Content.ReadAsStringAsync());
        Assert.Equal(
            survivorId,
            body.RootElement.GetProperty("mergedIntoClassificationId").GetGuid());
    }

    /// <summary>
    /// <b>A merged classification cannot be reactivated into an empty category under a familiar
    /// name.</b>
    ///
    /// <para>
    /// The flag alone is not the whole of what a merge did: the population moved. Flipping it back
    /// would offer a category people recognise and that describes nobody, and the next person filed
    /// under it would be silently separated from everyone the category used to mean.
    /// <c>CK_Classifications_MergedIsRetired</c> says the same thing at the database, so this is the
    /// readable half of a refusal that would otherwise arrive as a constraint violation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_merged_classification_cannot_simply_be_reactivated()
    {
        var schoolId = await ArrangeSchoolAsync();
        var survivorId = await ArrangeClassificationAsync(schoolId, "ACAD");
        var loserId = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        await client.PostAsJsonAsync(
            $"{Route}/{loserId}/merge", new { intoClassificationId = survivorId });

        var response = await client.PatchAsJsonAsync(
            $"{Route}/{loserId}/active", new { isActive = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.NotMergeable), await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        Assert.False(
            (await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == loserId)).IsActive);
    }

    /// <summary>
    /// Merging a classification into itself is a 400 — no state of the database makes it meaningful —
    /// and it leaves the row alone.
    ///
    /// <para>
    /// <c>CK_Classifications_NoSelfMerge</c> is the second, independent refusal. Without the service
    /// check this would surface as a 500 carrying a constraint name, which is a correct outcome
    /// delivered in the least usable possible form.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_classification_cannot_be_merged_into_itself()
    {
        var schoolId = await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync(schoolId, "ACAD");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Route}/{id}/merge", new { intoClassificationId = id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(ClassificationWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        var row = await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == id);

        Assert.True(row.IsActive);
        Assert.Null(row.MergedIntoClassificationId);
    }

    /// <summary>
    /// An omitted <c>intoClassificationId</c> is a 400 rather than a 404 about
    /// <c>00000000-0000-0000-0000-000000000000</c>.
    ///
    /// <para>
    /// The distinction is the difference between "you did not say" and "what you said does not exist",
    /// and an administrator sent to look for a classification they never named will not find the bug in
    /// their own request.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_omitted_merge_target_is_refused_as_a_bad_request()
    {
        var schoolId = await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync(schoolId, "ACAD");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync($"{Route}/{id}/merge", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(ClassificationWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    /// <summary>
    /// <b>Merging into a retired classification is refused: it would move a live population onto a
    /// category no picker offers.</b>
    /// </summary>
    [Fact]
    public async Task Merging_into_a_retired_classification_is_refused()
    {
        var schoolId = await ArrangeSchoolAsync();
        var retiredId = await ArrangeClassificationAsync(schoolId, "ACAD", isActive: false);
        var liveId = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Route}/{liveId}/merge", new { intoClassificationId = retiredId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.NotMergeable), await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        Assert.True(
            (await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == liveId)).IsActive,
            "The refused merge retired the loser anyway. A refusal that performed half of the " +
            "operation is worse than one that performed all of it, because nothing says it happened.");
    }

    /// <summary>
    /// <b>Chains are refused in both directions</b> — merging a row that is already a tombstone, and
    /// merging into one.
    ///
    /// <para>
    /// A two-hop tombstone is not merely untidy: anything that later resolves "what absorbed this" has
    /// to decide whether to follow one hop or all of them, and the two answers differ. Refusing the
    /// second hop means the question never has two answers.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_chain_of_merges_is_refused_in_both_directions()
    {
        var schoolId = await ArrangeSchoolAsync();
        var finalId = await ArrangeClassificationAsync(schoolId, "ACAD");
        var middleId = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");
        var thirdId = await ArrangeClassificationAsync(schoolId, "TEACHING STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"{Route}/{middleId}/merge",
                new { intoClassificationId = finalId })).StatusCode);

        // Into a tombstone.
        var intoTombstone = await client.PostAsJsonAsync(
            $"{Route}/{thirdId}/merge", new { intoClassificationId = middleId });

        Assert.Equal(HttpStatusCode.Conflict, intoTombstone.StatusCode);
        Assert.Equal(
            nameof(ClassificationWriteOutcome.NotMergeable), await ErrorCodeAsync(intoTombstone));

        // And out of one.
        var fromTombstone = await client.PostAsJsonAsync(
            $"{Route}/{middleId}/merge", new { intoClassificationId = thirdId });

        Assert.Equal(HttpStatusCode.Conflict, fromTombstone.StatusCode);
        Assert.Equal(
            nameof(ClassificationWriteOutcome.NotMergeable), await ErrorCodeAsync(fromTombstone));

        await using var read = NewDbContext();

        Assert.Equal(
            finalId,
            (await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == middleId))
                .MergedIntoClassificationId);
    }

    /// <summary>
    /// <b>Two classifications on different axes cannot be merged, and the refusal has its own code.</b>
    ///
    /// <para>
    /// It is not a variant of <c>NotMergeable</c>. The other refusals say "this row is in the wrong
    /// state, fix the state"; this one says the request confuses two unrelated questions — merging
    /// <c>NAP</c> (what staff role someone has) into <c>STUDENT</c> (whether they are enrolled) does
    /// not tidy a duplicate, it overwrites one answer with the answer to a different question, for
    /// everybody at once.
    /// </para>
    ///
    /// <para>
    /// <b>And this guard is load-bearing beyond its own message — though not in the way an earlier
    /// version of this comment claimed.</b> The repoint writes only <c>ClassificationId</c>, so
    /// <c>(StudentId, Axis)</c> is unchanged by it and it cannot collide with
    /// <c>UX_StudentClassifications_Student_Axis</c> whether the axes match or not. What this refusal
    /// actually protects is the invariant <c>StudentClassification.Axis == Classification.Axis</c>:
    /// without it, a repointed row would keep describing the axis of the classification it left. The
    /// composite foreign key <c>(ClassificationId, Axis)</c> against <c>Classifications(Id, Axis)</c>
    /// enforces the same thing at the database, so the two are belt and braces rather than one
    /// implying the other.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ClassificationAxis.Personnel, ClassificationAxis.Student)]
    [InlineData(ClassificationAxis.Student, ClassificationAxis.Personnel)]
    [InlineData(ClassificationAxis.Friars, ClassificationAxis.Special)]
    public async Task A_merge_across_two_axes_is_refused(string loserAxis, string survivorAxis)
    {
        var schoolId = await ArrangeSchoolAsync();
        var loserId = await ArrangeClassificationAsync(schoolId, "NAP", loserAxis);
        var survivorId = await ArrangeClassificationAsync(schoolId, "STUDENT", survivorAxis);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Route}/{loserId}/merge", new { intoClassificationId = survivorId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.CrossAxis), await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        var loser = await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == loserId);

        Assert.True(
            loser.IsActive,
            "The refused cross-axis merge retired the loser anyway — a refusal that performed half " +
            "the operation is worse than one that performed all of it, because nothing says so.");

        Assert.Null(loser.MergedIntoClassificationId);
    }

    /// <summary>
    /// The cross-axis refusal is checked <em>before</em> the state guards, and the ordering is a
    /// usability decision rather than an accident.
    ///
    /// <para>
    /// Told "that one is retired" first, an operator would reactivate the survivor and try again — and
    /// the second attempt would fail for the axis reason anyway. Naming the unfixable problem first
    /// saves them a wasted edit to a row they should not have touched.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cross_axis_merge_into_a_retired_row_reports_the_axis_not_the_retirement()
    {
        var schoolId = await ArrangeSchoolAsync();
        var loserId = await ArrangeClassificationAsync(schoolId, "NAP", ClassificationAxis.Personnel);
        var survivorId = await ArrangeClassificationAsync(
            schoolId, "STUDENT", ClassificationAxis.Student, isActive: false);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Route}/{loserId}/merge", new { intoClassificationId = survivorId });

        Assert.Equal(nameof(ClassificationWriteOutcome.CrossAxis), await ErrorCodeAsync(response));
    }

    /// <summary>
    /// <b>A classification that has absorbed another cannot itself be merged away.</b>
    ///
    /// <para>
    /// <b>This gap was reachable with no concurrency at all, and every existing guard passed it.</b>
    /// Merge L into V, then V into W: at the moment the second merge runs, V is live, un-merged and a
    /// perfectly legal survivor — so it was allowed, and the result was L's tombstone naming V while
    /// L's people sat on W. "Where did these people go" then took two hops, through a retired row
    /// invisible in every picker. The sibling refusals (merging INTO a tombstone, merging a tombstone)
    /// were written to prevent exactly that shape and missed this direction.
    /// </para>
    ///
    /// <para>
    /// It surfaced from <see cref="ClassificationMergeRaceTests"/>, which produced the same state
    /// faster — worth recording, because the race test was written for a concurrency defect and found
    /// a design one.
    /// </para>
    ///
    /// <para>
    /// <b>The refusal is permanent, and the message says so rather than prescribing a way out.</b> An
    /// earlier version told the operator to "merge whatever points at this into the survivor first" —
    /// which cannot be done: by <c>CK_Classifications_MergedIsRetired</c> the only thing that can point
    /// at a classification is a tombstone, and a tombstone is refused by the guard one clause earlier
    /// in the same method. <c>SetActiveAsync</c> will not reactivate a merged row and
    /// <c>DeleteAsync</c> will not delete one, so there is no route around it either. Step one of that
    /// instruction was a guaranteed 409 whose message talked about something else — the same
    /// confident-and-untrue defect this round was called for, surviving in the one text an operator
    /// actually reads.
    /// </para>
    ///
    /// <para>
    /// What the refusal now offers is the direction that does work, and the assertions below exercise
    /// it end to end: merge the other classification <em>into</em> this one and rename. That reaches
    /// the same single combined category under the same name, which is what the operator wanted; it is
    /// only the direction of travel that is fixed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_classification_that_has_absorbed_another_cannot_be_merged_away()
    {
        var schoolId = await ArrangeSchoolAsync();
        var l = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");
        var v = await ArrangeClassificationAsync(schoolId, "ACAD");
        var w = await ArrangeClassificationAsync(schoolId, "TEACHING STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"{Route}/{l}/merge", new { intoClassificationId = v })).StatusCode);

        var refused = await client.PostAsJsonAsync(
            $"{Route}/{v}/merge", new { intoClassificationId = w });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.NotMergeable), await ErrorCodeAsync(refused));

        await using var read = NewDbContext();

        // V stayed live, and L still points at it — one hop, still true.
        var survivor = await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == v);
        Assert.True(survivor.IsActive);
        Assert.Null(survivor.MergedIntoClassificationId);

        Assert.Equal(
            v,
            (await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == l))
                .MergedIntoClassificationId);

        // The refusal is stable rather than a first-attempt fluke — retrying it does not eventually
        // let a chain through.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync(
                $"{Route}/{v}/merge", new { intoClassificationId = w })).StatusCode);

        // The message must not prescribe a recovery that does not exist, so it claims exactly one
        // thing: go the other way and rename. Both halves are exercised here, because a specific
        // instruction nobody tests is the thing this whole round was about.
        var detail = await DetailAsync(refused);

        Assert.Contains("permanent", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("first, then merge this one", detail, StringComparison.OrdinalIgnoreCase);

        // Step one of the advice: the other direction works.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"{Route}/{w}/merge", new { intoClassificationId = v })).StatusCode);

        // Step two: and the survivor can then take the name the operator wanted.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                $"{Route}/{v}", new { name = "TEACHING STAFF (combined)" })).StatusCode);

        await using var after = NewDbContext();

        var combined = await after.Classifications.AsNoTracking().SingleAsync(c => c.Id == v);
        Assert.True(combined.IsActive);
        Assert.Equal("TEACHING STAFF (combined)", combined.Name);

        // One live classification, everyone under it, and both losers kept as history.
        var live = await after.Classifications.AsNoTracking()
            .Where(c => c.SchoolId == schoolId && c.IsActive)
            .Select(c => c.Id)
            .ToListAsync();

        Assert.Equal([v], live);
        Assert.Equal(3, await after.Classifications.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// <b>A merged-away classification cannot be deleted, even though nothing references it.</b>
    ///
    /// <para>
    /// This is the case that looks safest and is not. After a merge the losing row holds nobody — they
    /// all moved — and no tombstone points at it, so a delete guard written purely as "does anything
    /// reference this" lets it through and cheerfully reports that "nothing referenced it, which is the
    /// only circumstance in which deleting one destroys nothing". That sentence is false: the row is
    /// the <em>only</em> record that those people were ever somewhere else. Delete it and they become
    /// indistinguishable from people who were always on the survivor, and the merge becomes
    /// unexplainable and unreversible.
    /// </para>
    ///
    /// <para>
    /// The existing delete-refusal test exercises the <em>survivor</em>, which is protected by the
    /// tombstone pointing at it. The loser had no such protection.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_merged_away_classification_cannot_be_deleted()
    {
        var schoolId = await ArrangeSchoolAsync();
        var survivorId = await ArrangeClassificationAsync(schoolId, "ACAD");
        var loserId = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"{Route}/{loserId}/merge",
                new { intoClassificationId = survivorId })).StatusCode);

        // Nothing holds it and nothing points at it — the state a reference-counting guard calls free.
        await using (var check = NewDbContext())
        {
            Assert.Equal(
                0, await check.StudentClassifications.AsNoTracking()
                    .CountAsync(sc => sc.ClassificationId == loserId));

            Assert.Equal(
                0, await check.Classifications.AsNoTracking()
                    .CountAsync(c => c.MergedIntoClassificationId == loserId));
        }

        var refused = await client.DeleteAsync($"{Route}/{loserId}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.InUse), await ErrorCodeAsync(refused));

        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        var detail = body.RootElement.GetProperty("detail").GetString() ?? "";

        // The refusal names what absorbed it, because "cannot be deleted" on a row that visibly has
        // no holders reads as a bug unless it says why.
        Assert.Contains("ACAD", detail, StringComparison.Ordinal);

        await using var read = NewDbContext();

        Assert.True(
            await read.Classifications.AsNoTracking().AnyAsync(c => c.Id == loserId),
            "The merge tombstone was deleted. Both the entity's own remarks and the controller's " +
            "describe it as load-bearing history; this is the guard that makes those claims true.");
    }

    /// <summary>
    /// <b>A merge cannot cross a tenant.</b>
    ///
    /// <para>
    /// Both rows are fetched by primary key, and a primary-key lookup is scoped by nothing when no
    /// tenant is pinned — which is every unauthenticated request in the pre-auth build. Without the
    /// explicit school comparison in the service, this request would move one institution's people onto
    /// another institution's category and answer 200.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_merge_across_two_schools_is_refused()
    {
        var first = await ArrangeSchoolAsync("USA");
        var second = await ArrangeSchoolAsync("OTHER");

        var ours = await ArrangeClassificationAsync(first, "ACAD");
        var theirs = await ArrangeClassificationAsync(second, "ACADEMIC STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Route}/{theirs}/merge", new { intoClassificationId = ours });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var read = NewDbContext();
        var row = await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == theirs);

        Assert.True(row.IsActive);
        Assert.Null(row.MergedIntoClassificationId);
    }

    /// <summary>
    /// The merge is written as one transaction, so a refusal partway through leaves nothing half-done.
    ///
    /// <para>
    /// Asserted through the observable consequence rather than by faulting the connection: after a
    /// refused merge the loser is exactly as it was. That is the property that matters and the only one
    /// a caller can check; a test that injected a failure between the two statements would be asserting
    /// against EF's command ordering rather than against the guarantee.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_refused_merge_leaves_the_loser_exactly_as_it_was()
    {
        var schoolId = await ArrangeSchoolAsync();
        var retiredTarget = await ArrangeClassificationAsync(schoolId, "ACAD", isActive: false);
        var loserId = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");

        await using (var before = NewDbContext())
        {
            var row = await before.Classifications.AsNoTracking().SingleAsync(c => c.Id == loserId);
            Assert.True(row.IsActive);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        await client.PostAsJsonAsync(
            $"{Route}/{loserId}/merge", new { intoClassificationId = retiredTarget });

        await using var read = NewDbContext();
        var after = await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == loserId);

        Assert.True(after.IsActive);
        Assert.Null(after.RetiredAt);
        Assert.Null(after.MergedIntoClassificationId);
    }
}
