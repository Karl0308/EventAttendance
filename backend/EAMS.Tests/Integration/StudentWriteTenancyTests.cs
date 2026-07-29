using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Which school a manually written row is filed under — the <b>write</b> side of §11, which
/// <see cref="MultiTenantFilterTests"/> does not cover because until Phase 3b-1 nothing outside the §10
/// importer wrote a student at all.
///
/// <para>
/// The read side is a query filter: forget it and a tenant sees another tenant's rows, which is a leak
/// with a test. The write side has no filter to forget. <c>SchoolResolution</c> answers "which school?"
/// from the pinned tenant or from there being exactly one, and <b>every one of its wrong answers is
/// silent</b>: a row filed under the wrong school is a valid row, it satisfies every constraint, it is
/// invisible to the tenant who created it and visible to one who did not, and nothing anywhere logs
/// that a choice was made. So the three cardinalities — zero schools, one, several — are each pinned
/// explicitly rather than left to the one seeded school every other test happens to have.
/// </para>
///
/// <para>
/// <c>RfidCards</c> gets its own attention because it is the one table carrying a <em>denormalized</em>
/// <c>SchoolId</c> (ADR-001 D-3). That column is not decoration: <c>UX_RfidCards_SchoolId_CardUid_Active</c>
/// is built on it, so a card whose school disagrees with its owner's would sit in the wrong tenant's
/// uniqueness scope — and the failure would be a UID that can be issued twice, in a table where that is
/// the one thing the index exists to prevent.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentWriteTenancyTests : IntegrationTest
{
    public StudentWriteTenancyTests(SqlServerFixture sql) : base(sql) { }

    private const string Uid = "04A7B8C9";

    private async Task<Guid> AddSchoolAsync(string code)
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> AddStudentAsync(Guid schoolId, string studentNumber)
    {
        // Written on an unpinned context so the arrange step can place rows in schools the test then
        // pins away from — the whole point of several of these.
        await using var db = NewDbContext(new TestSchoolContext());
        var student = TestData.NewStudent(schoolId, studentNumber);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private static StudentWriteRequest Request(string studentNumber = "2023-0001") =>
        new(studentNumber, "Juan", null, "Dela Cruz", null, null, null, null);

    // -------------------------------------------------------------------- resolving a school

    /// <summary>
    /// Zero schools. There is nothing to file a student under, so the create is refused with a message
    /// that names the rule rather than a foreign-key violation from SQL Server.
    ///
    /// <para>
    /// Not a theoretical state: it is exactly what a freshly migrated Production database looks like,
    /// because seeding is skipped outside Development (<c>CLAUDE.md</c>). The first thing anyone does
    /// against a new deployment is create a student.
    /// </para>
    /// </summary>
    [Fact]
    public async Task With_no_school_at_all_a_create_is_refused_by_name()
    {
        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request());

        Assert.Equal(StudentWriteOutcome.NoSchoolResolved, response.Outcome);
        Assert.Null(response.Student);

        await using var read = NewDbContext();
        Assert.Empty(await read.Students.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Exactly one school and no pinned tenant — the pre-auth build's ordinary state — files the student
    /// under it. This is the fallback <c>SchoolResolution</c> documents as agreeing with what
    /// <c>PinDevelopmentSchoolAsync</c> announced at startup, and the assertion is on the stored
    /// <c>SchoolId</c> rather than on the outcome, because "saved" says nothing about where.
    /// </summary>
    [Fact]
    public async Task With_exactly_one_school_and_no_pinned_tenant_the_student_is_filed_under_it()
    {
        var schoolId = await AddSchoolAsync("USA");

        await using var db = NewDbContext();
        Assert.Equal(StudentWriteOutcome.Saved, (await StudentsOn(db).CreateAsync(Request())).Outcome);

        await using var read = NewDbContext();
        Assert.Equal(schoolId, (await read.Students.AsNoTracking().SingleAsync()).SchoolId);
    }

    /// <summary>
    /// <b>Several schools and no pinned tenant is refused, not guessed.</b>
    ///
    /// <para>
    /// This is the assertion that makes <c>StudentWriteTests.The_same_student_number_in_another_school
    /// _is_not_a_conflict</c> mean something: that test pins a tenant and notes in a comment that
    /// resolution would otherwise have two candidates, but nothing proves the two-candidate case is a
    /// refusal rather than "whichever school sorts first by <c>Code</c>". A guess here is the silent
    /// failure this file exists for — the student lands in a real school, just not the operator's, and
    /// then vanishes from their grid.
    /// </para>
    /// </summary>
    [Fact]
    public async Task With_several_schools_and_no_pinned_tenant_a_create_is_refused_rather_than_guessed()
    {
        await AddSchoolAsync("AAA");
        await AddSchoolAsync("ZZZ");

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request());

        Assert.Equal(StudentWriteOutcome.NoSchoolResolved, response.Outcome);

        await using var read = NewDbContext();
        Assert.Empty(await read.Students.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A pinned tenant wins over the only-school fallback, and it is pinned to the school that would
    /// <em>lose</em> an alphabetical tie-break — so a resolution that silently ignored the pin and fell
    /// through to <c>OrderBy(Code).Take(2)</c> would produce the wrong school rather than the right one
    /// by luck.
    /// </summary>
    [Fact]
    public async Task A_pinned_tenant_decides_the_school_even_when_several_exist()
    {
        await AddSchoolAsync("AAA");
        var pinned = await AddSchoolAsync("ZZZ");

        School.CurrentSchoolId = pinned;

        await using var db = NewDbContext();
        Assert.Equal(StudentWriteOutcome.Saved, (await StudentsOn(db).CreateAsync(Request())).Outcome);

        await using var read = NewDbContext(new TestSchoolContext());
        Assert.Equal(pinned, (await read.Students.AsNoTracking().SingleAsync()).SchoolId);
    }

    // ---------------------------------------------------------------- reaching another tenant

    /// <summary>
    /// Every write addressed at another school's student is a 404, on all four routes.
    ///
    /// <para>
    /// Asserted as one test rather than four because the guarantee is a single one — <c>FindAsync</c>
    /// and the <c>SchoolId</c> query filter — and the risk is that one of the four write methods
    /// stops going through it. A cross-tenant <em>edit</em> is worse than a cross-tenant read: the read
    /// discloses, the write changes somebody else's roster.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_write_addressed_at_another_schools_student_is_not_found()
    {
        var mine = await AddSchoolAsync("USA");
        var theirs = await AddSchoolAsync("CICSS");
        var foreignStudentId = await AddStudentAsync(theirs, "2023-0001");

        Guid foreignCardId;
        await using (var db = NewDbContext(new TestSchoolContext()))
        {
            var card = TestData.NewCard(theirs, foreignStudentId, Uid);
            db.RfidCards.Add(card);
            await db.SaveChangesAsync();
            foreignCardId = card.Id;
        }

        School.CurrentSchoolId = mine;

        await using var scoped = NewDbContext();
        var students = StudentsOn(scoped);

        Assert.Equal(StudentWriteOutcome.NotFound,
            (await students.UpdateAsync(foreignStudentId, Request("2023-0009"))).Outcome);
        Assert.Equal(StudentWriteOutcome.NotFound,
            (await students.DeleteAsync(foreignStudentId)).Outcome);
        Assert.Equal(StudentWriteOutcome.NotFound,
            (await students.AddCardAsync(foreignStudentId, new StudentCardRequest("AABBCCDD", null))).Outcome);
        Assert.Equal(StudentWriteOutcome.NotFound,
            (await students.DeactivateCardAsync(foreignStudentId, foreignCardId)).Outcome);

        // Nothing moved in the other tenant.
        await using var read = NewDbContext(new TestSchoolContext());
        Assert.False((await read.Students.AsNoTracking().SingleAsync(s => s.Id == foreignStudentId)).IsDeleted);
        Assert.True((await read.RfidCards.AsNoTracking().SingleAsync(c => c.Id == foreignCardId)).IsActive);
    }

    // --------------------------------------------------------------- tenant-scoped uniqueness

    /// <summary>
    /// One UID, two schools, both active — legal, because ADR-001 D-3 scopes the index to
    /// <c>(SchoolId, CardUid)</c>.
    ///
    /// <para>
    /// The interesting half is the <em>pre-check</em>, not the index. <c>AddCardAsync</c> looks for a
    /// clash with an explicit <c>SchoolId</c> predicate rather than relying on the global filter,
    /// precisely because the filter is inert when no tenant is pinned — which is the state this test
    /// runs in. Drop that predicate and the second assignment is refused with a message naming
    /// <em>another tenant's</em> card, which is both a wrong answer and a disclosure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task One_uid_can_be_active_in_two_schools_at_once()
    {
        var first = await AddSchoolAsync("USA");
        var second = await AddSchoolAsync("CICSS");
        var hereId = await AddStudentAsync(first, "2023-0001");
        var thereId = await AddStudentAsync(second, "2023-0001");

        // Unpinned on purpose: the global filter is doing nothing, so anything that works does so
        // because the service asked for it explicitly.
        Assert.Null(School.CurrentSchoolId);

        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.Saved,
                (await StudentsOn(db).AddCardAsync(hereId, new StudentCardRequest(Uid, null))).Outcome);
        }

        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.Saved,
                (await StudentsOn(db).AddCardAsync(thereId, new StudentCardRequest(Uid, null))).Outcome);
        }

        await using var read = NewDbContext();
        var cards = await read.RfidCards.AsNoTracking().Where(c => c.CardUid == Uid).ToListAsync();

        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.True(c.IsActive));

        // Each card's denormalized school is its owner's, which is what makes the index tenant-scoped
        // rather than merely two-rows-wide.
        Assert.Equal(first, cards.Single(c => c.StudentId == hereId).SchoolId);
        Assert.Equal(second, cards.Single(c => c.StudentId == thereId).SchoolId);
    }

    /// <summary>
    /// The same rule on the student number, exercised through the <b>update</b> path, which has its own
    /// duplicate pre-check and its own school argument.
    ///
    /// <para>
    /// The create path's version of this is covered. The update path takes its school from the loaded
    /// entity rather than from <c>SchoolResolution</c>, so it is a different value arriving from a
    /// different place — and getting it wrong fails in the direction nobody notices: a legitimate rename
    /// refused as a duplicate, citing a student the operator cannot see and does not have.
    /// </para>
    ///
    /// <para>
    /// <b>Run unpinned, which is what makes it test that.</b> It used to pin the tenant first, and a
    /// pinned tenant means the global query filter scopes <c>TakenStudentNumberAsync</c> on its own —
    /// so the explicit <c>SchoolId</c> predicate in the service could be deleted and this still passed.
    /// It asserted the outcome without isolating the mechanism its own doc named.
    /// <see cref="One_uid_can_be_active_in_two_schools_at_once"/> is the model: with no tenant pinned
    /// the filter is provably inert, so anything that works does so because the service asked for it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Renaming_a_student_onto_a_number_used_only_by_another_school_is_allowed()
    {
        var mine = await AddSchoolAsync("USA");
        var theirs = await AddSchoolAsync("CICSS");
        var mineId = await AddStudentAsync(mine, "2023-0001");
        await AddStudentAsync(theirs, "2023-0002");

        // Unpinned on purpose: the global filter is doing nothing, so the pre-check's own SchoolId
        // predicate is the only thing that can keep the other school's row out of this rename.
        Assert.Null(School.CurrentSchoolId);

        await using var db = NewDbContext();
        var response = await StudentsOn(db).UpdateAsync(mineId, Request("2023-0002"));

        Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);

        await using var read = NewDbContext(new TestSchoolContext());
        Assert.Equal(2, await read.Students.AsNoTracking()
            .CountAsync(s => s.StudentNumber == "2023-0002"));
    }

    /// <summary>
    /// A card is filed under its <em>owner's</em> school, not under whatever
    /// <c>SchoolResolution</c> would have answered.
    ///
    /// <para>
    /// The two are the same value in every ordinary case, which is what makes this worth an explicit
    /// test: with two schools and no pinned tenant, <c>SchoolResolution</c> returns null and a create is
    /// refused — yet assigning a card must still work, because the school is taken from the student and
    /// never resolved again. A future refactor that "tidies" the two call sites into one would turn
    /// every card assignment on a multi-school instance into <c>NoSchoolResolved</c>, and nothing else
    /// would notice.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_card_is_filed_under_its_owners_school_even_when_no_school_can_be_resolved()
    {
        await AddSchoolAsync("AAA");
        var owning = await AddSchoolAsync("ZZZ");
        var studentId = await AddStudentAsync(owning, "2023-0001");

        await using (var db = NewDbContext())
        {
            // The control: resolution genuinely cannot answer in this state.
            Assert.Equal(StudentWriteOutcome.NoSchoolResolved,
                (await StudentsOn(db).CreateAsync(Request("2023-0002"))).Outcome);
        }

        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.Saved,
                (await StudentsOn(db).AddCardAsync(studentId, new StudentCardRequest(Uid, null))).Outcome);
        }

        await using var read = NewDbContext();
        Assert.Equal(owning, (await read.RfidCards.AsNoTracking().SingleAsync()).SchoolId);
    }
}
