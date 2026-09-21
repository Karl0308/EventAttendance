using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>The two acceptance criteria that could not be written until people could actually hold a
/// classification.</b>
///
/// <para>
/// They were deliberately left unwritten in the first pass of this phase, because there was no
/// assignment mechanism and a test asserting "no student lost their classification" over <em>zero</em>
/// students passes by construction — it proves the arrangement is empty and nothing else. That is the
/// exact shape <c>feedback_negative_control_before_claiming_fixed.md</c> exists to catch, and it is
/// why these are owed now rather than merely nice to have:
/// </para>
///
/// <list type="number">
/// <item>Retiring a classification leaves every assignment standing.</item>
/// <item>Merging moves every assignment to the survivor, loses nobody, and deletes no row.</item>
/// </list>
///
/// <para>
/// <b>Every assertion counts rows in <c>StudentClassifications</c>, never the response body.</b> The
/// response is composed by the code under test; a retire that deleted assignments, or a merge that
/// dropped half of them, would return an identical and entirely correct-looking payload. Only the
/// table can tell the difference.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ClassificationAssignmentTests : IntegrationTest
{
    public ClassificationAssignmentTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/classifications";

    /// <summary>
    /// A school, two same-axis classifications, and <paramref name="holders"/> people holding the
    /// first. Returns everything the assertions need.
    ///
    /// <para>
    /// Assignments are written directly rather than through a service, because no assignment write
    /// surface exists — Phase 1b builds it from the roster. That is acceptable here in a way it would
    /// not be for, say, a device key: the rows carry no derived secret and no invariant a service
    /// owns, and <c>TestData.NewStudentClassification</c> copies the axis off the parent so the
    /// fixture cannot build a person production could never produce.
    /// </para>
    /// </summary>
    private async Task<(Guid SchoolId, Guid LoserId, Guid SurvivorId, List<Guid> StudentIds)>
        ArrangeAsync(int holders = 3, string axis = ClassificationAxis.Personnel)
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var loser = TestData.NewClassification(school.Id, "ACADEMIC STAFF", axis);
        var survivor = TestData.NewClassification(school.Id, "ACAD", axis);
        db.Classifications.AddRange(loser, survivor);

        var studentIds = new List<Guid>();

        for (var i = 0; i < holders; i++)
        {
            var student = TestData.NewStudent(school.Id, studentNumber: $"2023-{i:D4}");
            db.Students.Add(student);
            db.StudentClassifications.Add(TestData.NewStudentClassification(student.Id, loser));
            studentIds.Add(student.Id);
        }

        await db.SaveChangesAsync();

        return (school.Id, loser.Id, survivor.Id, studentIds);
    }

    private async Task<List<(Guid StudentId, Guid ClassificationId, string Axis)>> AssignmentsAsync()
    {
        await using var read = NewDbContext();

        return await read.StudentClassifications.AsNoTracking()
            .Select(sc => new ValueTuple<Guid, Guid, string>(sc.StudentId, sc.ClassificationId, sc.Axis))
            .ToListAsync();
    }

    // ------------------------------------------------------------------- AC 1: retire keeps holders

    /// <summary>
    /// <b>A retired classification stops appearing in pickers and every person filed under it keeps
    /// it.</b>
    ///
    /// <para>
    /// Both halves fail independently, so both are asserted: a retire that cascaded to the assignments
    /// would leave three people uncategorised with nothing to restore from, and a retire that left the
    /// row in the picker's default list would not have retired anything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Retiring_a_classification_keeps_every_assignment()
    {
        var (_, loserId, _, studentIds) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var retired = await client.PatchAsJsonAsync($"{Route}/{loserId}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        var assignments = await AssignmentsAsync();

        Assert.True(
            assignments.Count == studentIds.Count,
            $"Retiring left {assignments.Count} assignments where {studentIds.Count} were expected. " +
            "Retiring is the SAFE half of delete: it withdraws the category from pickers and must " +
            "touch nobody's record. An assignment lost here is a person silently uncategorised, with " +
            "nothing to restore from.");

        Assert.All(assignments, a => Assert.Equal(loserId, a.ClassificationId));
        Assert.Equal(
            studentIds.Order(),
            assignments.Select(a => a.StudentId).Order());

        // And it really is out of the picker — otherwise the first half is a claim about a no-op.
        using var picker = JsonDocument.Parse(
            await (await client.GetAsync(Route)).Content.ReadAsStringAsync());

        Assert.DoesNotContain(
            picker.RootElement.GetProperty("items").EnumerateArray(),
            c => c.GetProperty("id").GetGuid() == loserId);
    }

    /// <summary>
    /// The count an operator sees before deciding is the real one, so the retire message and the
    /// delete refusal both say how many people are affected rather than how many rows "reference" it.
    /// </summary>
    [Fact]
    public async Task The_published_student_count_is_the_number_of_holders()
    {
        var (_, loserId, _, studentIds) = await ArrangeAsync(holders: 3);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        using var body = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}/{loserId}")).Content.ReadAsStringAsync());

        Assert.Equal(studentIds.Count, body.RootElement.GetProperty("studentCount").GetInt32());
    }

    // --------------------------------------------------- the delete guard, with a real student behind it

    /// <summary>
    /// <b>A classification somebody holds cannot be deleted — 409, and every assignment survives the
    /// refusal.</b>
    ///
    /// <para>
    /// This is the no-data-loss criterion with the referent it was always about. The first pass could
    /// only prove it against a merge tombstone; a person holding the row is the case an administrator
    /// will actually hit, and the one where a cascade would be catastrophic rather than untidy.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_classification_somebody_holds_cannot_be_deleted()
    {
        var (_, loserId, _, studentIds) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var refused = await client.DeleteAsync($"{Route}/{loserId}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(ClassificationWriteOutcome.InUse),
            body.RootElement.GetProperty(ClassificationsController.ErrorCodeProperty).GetString());

        // The refusal says how many people, because "3 rows reference it" sends an operator looking
        // for rows and "3 person(s) are filed under it" tells them what to do next.
        Assert.Contains(
            $"{studentIds.Count} person(s)",
            body.RootElement.GetProperty("detail").GetString() ?? "");

        await using var read = NewDbContext();

        Assert.True(
            await read.Classifications.AsNoTracking().AnyAsync(c => c.Id == loserId),
            "The delete was answered 409 and removed the classification anyway.");

        Assert.Equal(studentIds.Count, await read.StudentClassifications.AsNoTracking().CountAsync());
    }

    // -------------------------------------------------------------- AC 2: merge moves, never deletes

    /// <summary>
    /// <b>A merge moves every assignment onto the survivor. Nobody loses a classification, and no row
    /// is deleted.</b>
    ///
    /// <para>
    /// Four separate claims, asserted separately because they fail separately: the same people are
    /// still classified (no assignment vanished), they are all on the survivor (none was left behind),
    /// both classifications still exist (the loser is retired, not deleted), and the reported count
    /// matches what actually moved.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_merge_moves_every_assignment_and_loses_nobody()
    {
        var (_, loserId, survivorId, studentIds) = await ArrangeAsync(holders: 3);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Route}/{loserId}/merge", new { intoClassificationId = survivorId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(studentIds.Count, body.RootElement.GetProperty("studentsRepointed").GetInt32());
        Assert.Equal(
            studentIds.Count,
            body.RootElement.GetProperty("survivor").GetProperty("studentCount").GetInt32());

        var assignments = await AssignmentsAsync();

        // 1. Nobody lost a classification.
        Assert.True(
            assignments.Count == studentIds.Count,
            $"The merge left {assignments.Count} assignments where {studentIds.Count} went in. A " +
            "merge moves people between categories; every person who had one before must have one " +
            "after, or the operation has quietly uncategorised somebody.");

        Assert.Equal(studentIds.Order(), assignments.Select(a => a.StudentId).Order());

        // 2. All of them are on the survivor, and none was left behind on the loser.
        Assert.All(assignments, a => Assert.Equal(survivorId, a.ClassificationId));

        // 3. The denormalized axis still agrees with the parent — the invariant that lets
        //    UX_StudentClassifications_Student_Axis mean anything. The merge does not write it,
        //    because a same-axis merge cannot change it; this is what proves that reasoning held.
        await using var read = NewDbContext();
        var survivorAxis = (await read.Classifications.AsNoTracking()
            .SingleAsync(c => c.Id == survivorId)).Axis;

        Assert.All(assignments, a => Assert.Equal(survivorAxis, a.Axis));

        // 4. Nothing was deleted.
        Assert.Equal(2, await read.Classifications.AsNoTracking().CountAsync());

        var loser = await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == loserId);
        Assert.False(loser.IsActive);
        Assert.Equal(survivorId, loser.MergedIntoClassificationId);
    }

    /// <summary>
    /// <b>A person who already holds the survivor is not a case the merge has to handle — the schema
    /// makes it unreachable, and this proves that rather than asserting it.</b>
    ///
    /// <para>
    /// The repoint is a bare <c>UPDATE ... SET ClassificationId</c> with no conflict handling, which is
    /// only safe because a person cannot hold both the loser and the survivor: they share an axis (the
    /// merge refuses otherwise), and <c>UX_StudentClassifications_Student_Axis</c> permits one
    /// classification per axis. So the attempt to arrange the collision is itself the assertion — it
    /// must be rejected by the database.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_person_cannot_hold_two_classifications_on_one_axis()
    {
        var (schoolId, loserId, survivorId, studentIds) = await ArrangeAsync(holders: 1);

        await using var db = NewDbContext();

        var survivor = await db.Classifications.SingleAsync(c => c.Id == survivorId);

        db.StudentClassifications.Add(
            TestData.NewStudentClassification(studentIds[0], survivor));

        var violation = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // 2601 is a unique index, 2627 a unique constraint — the same pair AcademicSchemaTests and
        // RbacSchemaTests match. Asserted on the error number rather than on "it threw", because a
        // truncation or a foreign-key failure would also throw and would mean the index was never
        // reached.
        var sql = Assert.IsType<SqlException>(violation.InnerException);

        Assert.True(
            sql.Number is 2601 or 2627,
            $"Giving one person two Personnel classifications failed with SQL error {sql.Number}, not " +
            $"a unique violation: {sql.Message}. UX_StudentClassifications_Student_Axis is what makes " +
            "the merge's repoint collision-free — without it, every merge needs conflict handling for " +
            "a person who holds both sides.");

        // The other axes remain free — one per axis, not one in total.
        db.ChangeTracker.Clear();

        var friars = TestData.NewClassification(schoolId, "USA FRIARS", ClassificationAxis.Friars);
        db.Classifications.Add(friars);
        await db.SaveChangesAsync();

        db.StudentClassifications.Add(TestData.NewStudentClassification(studentIds[0], friars));
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(
            2,
            await read.StudentClassifications.AsNoTracking()
                .CountAsync(sc => sc.StudentId == studentIds[0]));

        Assert.Equal(
            1, await read.StudentClassifications.AsNoTracking()
                .CountAsync(sc => sc.StudentId == studentIds[0] && sc.ClassificationId == loserId));
    }

    /// <summary>
    /// <b>The case the whole junction exists for: one person, two classifications, two axes.</b>
    ///
    /// <para>
    /// Three real people in the sampled export carry two at once — two are <c>STUDENT</c> +
    /// <c>NAP</c>, one is <c>STUDENT</c> + <c>C2B2</c>. A scalar <c>Students.ClassificationId</c> would
    /// have answered such a person with one of the two, non-empty and plausible, which is ADR-001
    /// D-2's failure on a new column. This is that person, and it is written as an API read because
    /// the property that matters is that a <em>client</em> can see both.
    /// </para>
    /// </summary>
    [Fact]
    public async Task One_person_holds_a_classification_on_each_of_two_axes()
    {
        Guid studentId;
        Guid studentAxisId;
        Guid personnelAxisId;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);

            var student = TestData.NewClassification(school.Id, "STUDENT", ClassificationAxis.Student);
            var nap = TestData.NewClassification(school.Id, "NAP", ClassificationAxis.Personnel);
            db.Classifications.AddRange(student, nap);

            var person = TestData.NewStudent(school.Id);
            db.Students.Add(person);

            db.StudentClassifications.AddRange(
                TestData.NewStudentClassification(person.Id, student),
                TestData.NewStudentClassification(person.Id, nap));

            await db.SaveChangesAsync();

            studentId = person.Id;
            studentAxisId = student.Id;
            personnelAxisId = nap.Id;
        }

        await using var read = NewDbContext();

        var held = await read.StudentClassifications.AsNoTracking()
            .Where(sc => sc.StudentId == studentId)
            .Select(sc => new { sc.ClassificationId, sc.Axis })
            .ToListAsync();

        Assert.Equal(2, held.Count);
        Assert.Contains(held, h => h.ClassificationId == studentAxisId && h.Axis == ClassificationAxis.Student);
        Assert.Contains(held, h => h.ClassificationId == personnelAxisId && h.Axis == ClassificationAxis.Personnel);

        // Both classifications report the holder, which is what a scalar column could not have done:
        // one of the two would have had a count of zero and looked empty.
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        foreach (var id in new[] { studentAxisId, personnelAxisId })
        {
            using var body = JsonDocument.Parse(
                await (await client.GetAsync($"{Route}/{id}")).Content.ReadAsStringAsync());

            Assert.Equal(1, body.RootElement.GetProperty("studentCount").GetInt32());
        }
    }
}
