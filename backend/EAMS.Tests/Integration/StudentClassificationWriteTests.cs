using System.Net;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>Assigning a classification to a person</b> — the write surface the vocabulary phase deliberately
/// shipped without, and the one the Add/Edit form's classification field is built on.
///
/// <para>
/// <b>Every assertion that matters counts rows in <c>StudentClassifications</c>, not fields in the
/// response.</b> The response is composed by the code under test: an assign that duplicated a row
/// instead of replacing one, or a clear that took the classification with it, would return an entirely
/// correct-looking payload. Only the table can tell the difference — the same rule
/// <c>ClassificationAssignmentTests</c> states for the retire and merge halves.
/// </para>
///
/// <para>
/// <b>On SQL Server, never EF InMemory.</b> Two of the invariants under test exist only in the schema:
/// <c>UX_StudentClassifications_Student_Axis</c> (one classification per person per axis) and the
/// composite foreign key <c>(ClassificationId, Axis)</c> → <c>Classifications(Id, Axis)</c> that makes a
/// junction row disagreeing with its parent unwritable. Under a provider that ignores both, a replace
/// that inserted a second row would pass.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentClassificationWriteTests : IntegrationTest
{
    public StudentClassificationWriteTests(SqlServerFixture sql) : base(sql) { }

    private static string Route(Guid studentId) =>
        $"/api/v1/students/{studentId}/classifications";

    /// <summary>
    /// A school, a student, and the four vocabulary entries the assertions below use: two on the
    /// Personnel axis (so one can replace the other), one on Student and one on Friars (so the
    /// several-axes case is reachable).
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);

        var nap = TestData.NewClassification(school.Id, "NAP", ClassificationAxis.Personnel);
        var acad = TestData.NewClassification(school.Id, "ACAD", ClassificationAxis.Personnel);
        var enrolled = TestData.NewClassification(school.Id, "STUDENT", ClassificationAxis.Student);
        var friars = TestData.NewClassification(school.Id, "USA FRIARS", ClassificationAxis.Friars);

        db.Classifications.AddRange(nap, acad, enrolled, friars);

        await db.SaveChangesAsync();

        return new World(school.Id, student.Id, nap.Id, acad.Id, enrolled.Id, friars.Id);
    }

    private sealed record World(
        Guid SchoolId, Guid StudentId, Guid NapId, Guid AcadId, Guid StudentAxisId, Guid FriarsId);

    /// <summary>Every junction row in the database, read on a fresh context.</summary>
    private async Task<List<(Guid StudentId, Guid ClassificationId, string Axis)>> AssignmentsAsync()
    {
        await using var read = NewDbContext();

        return await read.StudentClassifications.AsNoTracking()
            .Select(sc => new ValueTuple<Guid, Guid, string>(sc.StudentId, sc.ClassificationId, sc.Axis))
            .ToListAsync();
    }

    private async Task<StudentClassificationWriteResponse> AssignAsync(Guid studentId, Guid classificationId)
    {
        await using var db = NewDbContext();
        return await StudentClassificationsOn(db).AssignAsync(studentId, classificationId);
    }

    private async Task<StudentClassificationWriteResponse> ClearAsync(Guid studentId, Guid classificationId)
    {
        await using var db = NewDbContext();
        return await StudentClassificationsOn(db).ClearAsync(studentId, classificationId);
    }

    // ---------------------------------------------------------------- several axes, one value on each

    /// <summary>
    /// <b>One person, two axes, two classifications — the case the junction table exists for.</b>
    ///
    /// <para>
    /// Three people in the sampled export carry two at once. A scalar <c>Students.ClassificationId</c>
    /// would have answered such a person with one of the two, non-empty and plausible, which is ADR-001
    /// D-2's failure repeated on a new column. This asserts that assigning the second leaves the first
    /// standing — separately from asserting that both come back, because a write that clobbered the
    /// first would still return a perfectly coherent one-entry list.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Assigning_two_axes_leaves_a_person_holding_both()
    {
        var world = await ArrangeAsync();

        var first = await AssignAsync(world.StudentId, world.StudentAxisId);
        Assert.Equal(StudentClassificationWriteOutcome.Saved, first.Outcome);
        Assert.Null(first.ReplacedClassificationId);

        var second = await AssignAsync(world.StudentId, world.NapId);
        Assert.Equal(StudentClassificationWriteOutcome.Saved, second.Outcome);

        Assert.True(
            second.ReplacedClassificationId is null,
            "Assigning a Personnel classification reported that it replaced something. The person's " +
            "only other classification is on the Student axis, and the axes are independent — a " +
            "replacement here means the slot was found by something other than the axis.");

        var assignments = await AssignmentsAsync();

        Assert.Equal(2, assignments.Count);
        Assert.Contains(assignments, a => a.ClassificationId == world.StudentAxisId
                                       && a.Axis == ClassificationAxis.Student);
        Assert.Contains(assignments, a => a.ClassificationId == world.NapId
                                       && a.Axis == ClassificationAxis.Personnel);

        // And the read surface agrees, which is what the edit form populates itself from.
        Assert.Equal(2, second.Classifications!.Classifications.Count);
    }

    // ------------------------------------------------------------------- replacement within one axis

    /// <summary>
    /// <b>Re-assigning within an axis replaces. It does not duplicate, and it does not error.</b>
    ///
    /// <para>
    /// Three claims, asserted separately because they fail separately: the person ends up on the new
    /// classification, there is still exactly one Personnel row (a duplicate would have been refused by
    /// the index — as a 500 through a service with no handler — so "it did not error" is not on its own
    /// evidence of anything), and the displaced classification is named in the response rather than
    /// vanishing quietly.
    /// </para>
    ///
    /// <para>
    /// <b>The axis is re-read from the row afterwards.</b> The replacement writes only
    /// <c>ClassificationId</c>, and the denormalized <c>Axis</c> is only meaningful while it equals the
    /// parent's — the composite foreign key enforces that, so this assertion is what would notice if a
    /// future version started writing the axis too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Re_assigning_within_an_axis_replaces_rather_than_duplicating()
    {
        var world = await ArrangeAsync();

        await AssignAsync(world.StudentId, world.NapId);
        var replaced = await AssignAsync(world.StudentId, world.AcadId);

        Assert.Equal(StudentClassificationWriteOutcome.Saved, replaced.Outcome);

        Assert.Equal(world.NapId, replaced.ReplacedClassificationId);

        var assignments = await AssignmentsAsync();

        Assert.True(
            assignments.Count == 1,
            $"The person holds {assignments.Count} classifications after two assignments on ONE axis. " +
            "A person holds at most one per axis, so the second was a replacement — two rows here is a " +
            "duplicate the index should have refused, and one row on the wrong classification is a " +
            "replacement that did not take.");

        var held = Assert.Single(assignments);
        Assert.Equal(world.AcadId, held.ClassificationId);
        Assert.Equal(ClassificationAxis.Personnel, held.Axis);

        // The denormalized axis still equals its parent's — the invariant the composite FK backs.
        await using var read = NewDbContext();
        var parentAxis = (await read.Classifications.AsNoTracking()
            .SingleAsync(c => c.Id == world.AcadId)).Axis;

        Assert.Equal(parentAxis, held.Axis);

        // And the vocabulary is untouched: replacing somebody's classification deletes no category.
        Assert.Equal(4, await read.Classifications.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// <b>Assigning a classification somebody already holds is a no-op, and does not bump an audit
    /// column.</b>
    ///
    /// <para>
    /// The idempotency is what makes a double-submitted form safe. The audit half is the one worth
    /// pinning: re-writing the row on every save would turn <c>UpdatedAt</c> into a record of clicks
    /// rather than of edits, which is the same reading <c>ClassificationService.SetActiveAsync</c> and
    /// <c>TermAdminService.SetCurrentAsync</c> record for their own no-ops.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Assigning_a_classification_the_person_already_holds_changes_nothing()
    {
        var world = await ArrangeAsync();

        await AssignAsync(world.StudentId, world.NapId);

        DateTime stampedAt;
        await using (var read = NewDbContext())
        {
            stampedAt = (await read.StudentClassifications.AsNoTracking().SingleAsync()).UpdatedAt;
        }

        var again = await AssignAsync(world.StudentId, world.NapId);

        Assert.Equal(StudentClassificationWriteOutcome.Saved, again.Outcome);
        Assert.Null(again.ReplacedClassificationId);

        await using var after = NewDbContext();
        var row = await after.StudentClassifications.AsNoTracking().SingleAsync();

        Assert.Equal(world.NapId, row.ClassificationId);
        Assert.Equal(
            stampedAt, row.UpdatedAt);
    }

    /// <summary>
    /// <b><c>assignedAt</c> describes the classification that is actually there, so a replacement resets
    /// it.</b>
    ///
    /// <para>
    /// The row is re-pointed rather than re-created, so leaving <c>CreatedAt</c> alone reported somebody
    /// as <c>ACAD</c> "since" the date they were made <c>NAP</c> — years earlier, for a category that had
    /// touched them seconds ago. A plausible, non-empty, wrong date, which is the failure this project
    /// keeps a register of; two doc blocks already promised the reset and nothing asserted it, so the
    /// field had zero references outside its own declaration.
    /// </para>
    ///
    /// <para>
    /// The first assignment is back-dated in the database rather than by waiting, so the assertion is
    /// arithmetic on a known instant instead of a race against clock resolution.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Replacing_within_an_axis_resets_when_the_classification_was_assigned()
    {
        var world = await ArrangeAsync();

        await AssignAsync(world.StudentId, world.NapId);

        var backdated = new DateTime(2024, 3, 1, 8, 0, 0, DateTimeKind.Utc);

        await using (var db = NewDbContext())
        {
            var row = await db.StudentClassifications.SingleAsync();
            row.CreatedAt = backdated;
            await db.SaveChangesAsync();
        }

        // The back-dating took, and the read surface reports it — otherwise the assertion below would
        // pass against a value that was never there to change.
        await using (var read = NewDbContext())
        {
            var before = await StudentClassificationsOn(read).GetAsync(world.StudentId);
            Assert.Equal(backdated, Assert.Single(before!.Classifications).AssignedAt);
        }

        var replaced = await AssignAsync(world.StudentId, world.AcadId);
        Assert.Equal(StudentClassificationWriteOutcome.Saved, replaced.Outcome);

        var held = Assert.Single(replaced.Classifications!.Classifications);

        Assert.Equal(world.AcadId, held.ClassificationId);

        Assert.True(
            held.AssignedAt > backdated,
            $"After replacing NAP with ACAD, assignedAt is {held.AssignedAt:o} — the date the person " +
            $"was made NAP ({backdated:o}). The row is re-pointed rather than re-created, so a " +
            "replacement that does not rewrite CreatedAt reports a classification as held since a date " +
            "it did not exist on this person, and there is no history table to correct it from.");

        // And it is genuinely now, not merely later than the back-dated value.
        Assert.True(
            held.AssignedAt > DateTime.UtcNow.AddMinutes(-5),
            $"assignedAt is {held.AssignedAt:o}, which is not recent. The replacement happened during " +
            "this test.");
    }

    // ------------------------------------------------------------------------------------- clearing

    /// <summary>
    /// <b>Clearing removes the assignment and leaves the classification — and everybody else on it —
    /// exactly where they were.</b>
    ///
    /// <para>
    /// This is the no-data-loss criterion at the level a form reaches it. The classification is shared
    /// by everyone filed under it, so a clear that reached the vocabulary row would uncategorise every
    /// one of them; a second student holding the same classification is arranged precisely so that a
    /// clear which over-reached has somebody to lose.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Clearing_removes_the_assignment_and_not_the_classification()
    {
        var world = await ArrangeAsync();

        Guid otherStudentId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewStudent(world.SchoolId, "2024-0002", lastName: "Cruz");
            db.Students.Add(other);
            await db.SaveChangesAsync();
            otherStudentId = other.Id;
        }

        await AssignAsync(world.StudentId, world.NapId);
        await AssignAsync(world.StudentId, world.StudentAxisId);
        await AssignAsync(otherStudentId, world.NapId);

        var cleared = await ClearAsync(world.StudentId, world.NapId);

        Assert.Equal(StudentClassificationWriteOutcome.Saved, cleared.Outcome);

        var assignments = await AssignmentsAsync();

        // The one row went. The person's other axis and the other person's row did not.
        Assert.Equal(2, assignments.Count);
        Assert.DoesNotContain(assignments,
            a => a.StudentId == world.StudentId && a.ClassificationId == world.NapId);
        Assert.Contains(assignments,
            a => a.StudentId == world.StudentId && a.ClassificationId == world.StudentAxisId);
        Assert.Contains(assignments,
            a => a.StudentId == otherStudentId && a.ClassificationId == world.NapId);

        await using var read = NewDbContext();

        Assert.True(
            await read.Classifications.AsNoTracking().AnyAsync(c => c.Id == world.NapId),
            "Clearing a person's classification deleted the classification itself. It is shared by " +
            "everybody filed under it — this is the cascade DELETE /classifications/{id} is guarded " +
            "against, arriving one student at a time.");

        Assert.Equal(4, await read.Classifications.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// Clearing something the person does not hold is a 404, including on a second delete — the same
    /// answer <c>DELETE /students/{id}</c> gives, and for the same reason.
    /// </summary>
    [Fact]
    public async Task Clearing_a_classification_the_person_does_not_hold_is_not_found()
    {
        var world = await ArrangeAsync();

        await AssignAsync(world.StudentId, world.NapId);

        var never = await ClearAsync(world.StudentId, world.FriarsId);
        Assert.Equal(StudentClassificationWriteOutcome.NotAssigned, never.Outcome);

        Assert.Equal(StudentClassificationWriteOutcome.Saved,
            (await ClearAsync(world.StudentId, world.NapId)).Outcome);

        var again = await ClearAsync(world.StudentId, world.NapId);
        Assert.Equal(StudentClassificationWriteOutcome.NotAssigned, again.Outcome);

        Assert.Empty(await AssignmentsAsync());
    }

    // -------------------------------------------------------- the vocabulary's state, at the write path

    /// <summary>
    /// <b>A retired classification cannot be newly assigned — but somebody who already holds it can
    /// re-send it, and can always clear it.</b>
    ///
    /// <para>
    /// The ordering of those three is the whole of this test. Refusing a re-send would mean an edit form
    /// that round-trips what it read starts failing the moment an administrator retires an unrelated
    /// category; refusing the clear would leave a person permanently stuck holding a category nobody can
    /// see in a picker.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_retired_classification_refuses_new_holders_but_keeps_and_releases_its_own()
    {
        var world = await ArrangeAsync();

        await AssignAsync(world.StudentId, world.NapId);

        Guid otherStudentId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewStudent(world.SchoolId, "2024-0002", lastName: "Cruz");
            db.Students.Add(other);

            var nap = await db.Classifications.SingleAsync(c => c.Id == world.NapId);
            nap.IsActive = false;
            nap.RetiredAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            otherStudentId = other.Id;
        }

        // A new holder is refused.
        var refused = await AssignAsync(otherStudentId, world.NapId);
        Assert.Equal(StudentClassificationWriteOutcome.ClassificationRetired, refused.Outcome);

        // The existing holder still holds it, and re-sending is still a no-op rather than a 409.
        var resent = await AssignAsync(world.StudentId, world.NapId);
        Assert.Equal(StudentClassificationWriteOutcome.Saved, resent.Outcome);

        var stillHeld = Assert.Single(await AssignmentsAsync());
        Assert.Equal(world.StudentId, stillHeld.StudentId);
        Assert.Equal(world.NapId, stillHeld.ClassificationId);

        // And they can be released from it.
        Assert.Equal(StudentClassificationWriteOutcome.Saved,
            (await ClearAsync(world.StudentId, world.NapId)).Outcome);

        Assert.Empty(await AssignmentsAsync());
    }

    /// <summary>
    /// <b>A merged-away classification is refused with its own code, and the message names the
    /// survivor.</b>
    ///
    /// <para>
    /// It is not a near-miss of the retired case even though the row is retired: everyone that
    /// classification described already sits under the survivor, so the remedy is to assign <em>that</em>
    /// — which a client can do without asking anybody, provided it is told which one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_merged_away_classification_is_refused_and_names_its_survivor()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var merged = await ClassificationsOn(db).MergeAsync(world.NapId, world.AcadId);
            Assert.Equal(ClassificationWriteOutcome.Saved, merged.Outcome);
        }

        var refused = await AssignAsync(world.StudentId, world.NapId);

        Assert.Equal(StudentClassificationWriteOutcome.ClassificationMerged, refused.Outcome);
        Assert.Contains("ACAD", refused.Message);
        Assert.Contains(world.AcadId.ToString(), refused.Message);

        Assert.Empty(await AssignmentsAsync());

        // The survivor is assignable, which is what the refusal told the caller to do.
        Assert.Equal(StudentClassificationWriteOutcome.Saved,
            (await AssignAsync(world.StudentId, world.AcadId)).Outcome);
    }

    /// <summary>
    /// <b>A classification from another school is refused.</b>
    ///
    /// <para>
    /// Both lookups are by primary key, and a primary-key lookup is scoped by nothing while no tenant is
    /// pinned (ADR-001 D-6) — which is the state this whole suite runs in, deliberately, so that a
    /// tenancy failure is reachable rather than hidden by a filter. The composite foreign key would
    /// happily allow this row: it constrains the axis and says nothing about the school.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_classification_belonging_to_another_school_is_refused()
    {
        var world = await ArrangeAsync();

        Guid foreignClassificationId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("OTHER");
            db.Schools.Add(other);

            var theirs = TestData.NewClassification(other.Id, "NAP", ClassificationAxis.Personnel);
            db.Classifications.Add(theirs);

            await db.SaveChangesAsync();
            foreignClassificationId = theirs.Id;
        }

        var refused = await AssignAsync(world.StudentId, foreignClassificationId);

        Assert.Equal(StudentClassificationWriteOutcome.CrossSchool, refused.Outcome);
        Assert.Empty(await AssignmentsAsync());
    }

    /// <summary>A soft-deleted student is a 404 on every operation here, like everywhere else.</summary>
    [Fact]
    public async Task A_soft_deleted_student_cannot_be_classified()
    {
        var world = await ArrangeAsync();

        await AssignAsync(world.StudentId, world.NapId);

        await using (var db = NewDbContext())
        {
            var student = await db.Students.SingleAsync(s => s.Id == world.StudentId);
            student.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal(StudentClassificationWriteOutcome.StudentNotFound,
            (await AssignAsync(world.StudentId, world.AcadId)).Outcome);

        Assert.Equal(StudentClassificationWriteOutcome.StudentNotFound,
            (await ClearAsync(world.StudentId, world.NapId)).Outcome);

        await using var read = NewDbContext();
        Assert.Null(await StudentClassificationsOn(read).GetAsync(world.StudentId));

        // The assignment survives the soft delete — nothing here cascaded.
        Assert.Single(await AssignmentsAsync());
    }

    /// <summary>An unknown classification is a 404 and writes nothing.</summary>
    [Fact]
    public async Task An_unknown_classification_is_not_found()
    {
        var world = await ArrangeAsync();

        var refused = await AssignAsync(world.StudentId, Guid.NewGuid());

        Assert.Equal(StudentClassificationWriteOutcome.ClassificationNotFound, refused.Outcome);
        Assert.Empty(await AssignmentsAsync());
    }

    // ------------------------------------------------------------------------------------- the read

    /// <summary>
    /// <b>The read returns retired classifications the person still holds.</b>
    ///
    /// <para>
    /// Retiring withdraws a category from pickers and leaves every assignment standing, so filtering it
    /// out here would make an edit form show fewer classifications than the person has — and then save
    /// that. The <c>isActive</c> flag is what lets the form render it as held-but-not-offered instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_read_includes_a_retired_classification_the_person_still_holds()
    {
        var world = await ArrangeAsync();

        await AssignAsync(world.StudentId, world.NapId);
        await AssignAsync(world.StudentId, world.FriarsId);

        await using (var db = NewDbContext())
        {
            var nap = await db.Classifications.SingleAsync(c => c.Id == world.NapId);
            nap.IsActive = false;
            nap.RetiredAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var held = await StudentClassificationsOn(read).GetAsync(world.StudentId);

        Assert.NotNull(held);
        Assert.Equal(2, held.Classifications.Count);

        var retired = Assert.Single(held.Classifications, c => c.ClassificationId == world.NapId);
        Assert.False(retired.IsActive);
        Assert.Equal(ClassificationAxis.Personnel, retired.Axis);
        Assert.Equal("NAP", retired.Name);

        // Ordered by axis, so a picker's groups come back in a stable order rather than interleaved.
        Assert.Equal(
            held.Classifications.Select(c => c.Axis).Order(),
            held.Classifications.Select(c => c.Axis));
    }

    // ------------------------------------------------------------------------------------- over HTTP

    /// <summary>
    /// The published shape end to end: assign on two axes, replace on one, clear one — over the routes
    /// the SPA will call, asserting the payload a form binds to.
    /// </summary>
    [Fact]
    public async Task The_endpoints_assign_replace_and_clear()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var assigned = await client.PutAsync($"{Route(world.StudentId)}/{world.NapId}", null);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        using (var body = JsonDocument.Parse(await assigned.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                JsonValueKind.Null,
                body.RootElement.GetProperty("replacedClassificationId").ValueKind);

            var one = Assert.Single(body.RootElement.GetProperty("classifications").EnumerateArray());
            Assert.Equal(world.NapId, one.GetProperty("classificationId").GetGuid());
            Assert.Equal(ClassificationAxis.Personnel, one.GetProperty("axis").GetString());
            Assert.True(one.GetProperty("isActive").GetBoolean());
        }

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsync($"{Route(world.StudentId)}/{world.StudentAxisId}", null)).StatusCode);

        // The replacement, on the Personnel axis only.
        var replaced = await client.PutAsync($"{Route(world.StudentId)}/{world.AcadId}", null);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        using (var body = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                world.NapId,
                body.RootElement.GetProperty("replacedClassificationId").GetGuid());

            var ids = body.RootElement.GetProperty("classifications").EnumerateArray()
                .Select(c => c.GetProperty("classificationId").GetGuid())
                .ToList();

            Assert.Equal(2, ids.Count);
            Assert.Contains(world.AcadId, ids);
            Assert.Contains(world.StudentAxisId, ids);
            Assert.DoesNotContain(world.NapId, ids);
        }

        // The read the edit form populates itself from agrees.
        using (var body = JsonDocument.Parse(
                   await (await client.GetAsync(Route(world.StudentId))).Content.ReadAsStringAsync()))
        {
            Assert.Equal(2, body.RootElement.GetProperty("classifications").GetArrayLength());
        }

        var cleared = await client.DeleteAsync($"{Route(world.StudentId)}/{world.AcadId}");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        using (var body = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync()))
        {
            var one = Assert.Single(body.RootElement.GetProperty("classifications").EnumerateArray());
            Assert.Equal(world.StudentAxisId, one.GetProperty("classificationId").GetGuid());
        }

        var assignments = await AssignmentsAsync();
        Assert.Equal(world.StudentAxisId, Assert.Single(assignments).ClassificationId);
    }

    /// <summary>
    /// Every refusal is §6's RFC 7807 body carrying the stable <c>code</c> token a client branches on,
    /// at the status the outcome maps to — the same contract every other controller publishes.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_a_problem_body_carrying_its_code()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var nap = await db.Classifications.SingleAsync(c => c.Id == world.NapId);
            nap.IsActive = false;
            nap.RetiredAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var retired = await client.PutAsync($"{Route(world.StudentId)}/{world.NapId}", null);

        Assert.Equal(HttpStatusCode.Conflict, retired.StatusCode);

        using (var body = JsonDocument.Parse(await retired.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                nameof(StudentClassificationWriteOutcome.ClassificationRetired),
                body.RootElement
                    .GetProperty(StudentClassificationsController.ErrorCodeProperty).GetString());
        }

        var unknownStudent = await client.PutAsync(
            $"/api/v1/students/{Guid.NewGuid()}/classifications/{world.AcadId}", null);

        Assert.Equal(HttpStatusCode.NotFound, unknownStudent.StatusCode);

        using (var body = JsonDocument.Parse(await unknownStudent.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                nameof(StudentClassificationWriteOutcome.StudentNotFound),
                body.RootElement
                    .GetProperty(StudentClassificationsController.ErrorCodeProperty).GetString());
        }

        var notHeld = await client.DeleteAsync($"{Route(world.StudentId)}/{world.FriarsId}");

        Assert.Equal(HttpStatusCode.NotFound, notHeld.StatusCode);

        using (var body = JsonDocument.Parse(await notHeld.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                nameof(StudentClassificationWriteOutcome.NotAssigned),
                body.RootElement
                    .GetProperty(StudentClassificationsController.ErrorCodeProperty).GetString());
        }
    }
}
