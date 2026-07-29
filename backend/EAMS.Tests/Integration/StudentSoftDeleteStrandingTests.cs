using System.Net;
using System.Net.Http.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// What <c>DELETE /students/{id}</c> leaves behind, and what an operator can do about it.
///
/// <para>
/// Phase 3b-1 is the first release in which a student can be soft-deleted through the API at all. The
/// flag itself is old (§4.3) and the reads that respect it are old; what is new is that a person can now
/// <em>reach</em> the state — so every consequence of it moved from theoretical to operational in one
/// commit, and none of those consequences had a test.
/// </para>
///
/// <para>
/// <b>This file found two dead ends. One is closed; one is open and deliberately still asserted.</b>
/// Neither was ever data loss — nothing is corrupted and every row survives. The severity was that the
/// API refused an ordinary registrar action and its own error message recommended a recovery it did not
/// implement.
/// </para>
///
/// <list type="bullet">
///   <item><b>DEAD END 1 — the card, CLOSED.</b> <c>DeactivateCardAsync</c> no longer resolves its
///   owner through the <c>!IsDeleted</c> filter, so a deleted student's REGNO card can be released and
///   the UID reissued. <see cref="The_regno_of_a_soft_deleted_student_can_be_released_and_reissued"/>
///   walks the whole sequence.</item>
///   <item><b>DEAD END 2 — the student number, OPEN.</b>
///   <see cref="A_deleted_students_number_names_a_restore_that_no_endpoint_performs"/> still passes,
///   and is meant to: the <c>DuplicateStudentNumber</c> message advises restoring the deleted student,
///   and §6.2 defines no endpoint that restores one. Adding it is new API surface and a scope decision,
///   so the test stands as the live record of the gap rather than being softened.</item>
/// </list>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentSoftDeleteStrandingTests : IntegrationTest
{
    public StudentSoftDeleteStrandingTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/students";

    /// <summary>
    /// REGNO, in both of the shapes ADR-001's accepted context says it takes: verbatim in
    /// <c>Students.StudentNumber</c> and normalized in <c>RfidCards.CardUid</c>. Spelled as one pair
    /// because the whole point of these tests is that <b>one</b> registrar identifier is stranded in
    /// <b>two</b> places at once, and a fixture using unrelated values would make that look like two
    /// unrelated problems.
    /// </summary>
    private const string RegNo = "USA00042";

    private const string RegNoAsCardUid = "USA00042";

    private sealed record World(Guid SchoolId, Guid DeletedStudentId, Guid CardId, Guid SuccessorId);

    /// <summary>
    /// A student holding their REGNO card, then soft-deleted — plus a second student the registrar
    /// wants to hand that REGNO to. This is not a contrived arrangement: REGNO is reissued when a
    /// record is corrected and re-entered, and deleting the bad row is the obvious first step.
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        Guid schoolId, deletedId, cardId, successorId;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        await using (var db = NewDbContext())
        {
            var student = TestData.NewStudent(schoolId, RegNo);
            db.Students.Add(student);
            var successor = TestData.NewStudent(schoolId, "2023-0002", lastName: "Successor");
            db.Students.Add(successor);
            await db.SaveChangesAsync();
            deletedId = student.Id;
            successorId = successor.Id;
        }

        await using (var db = NewDbContext())
        {
            var issued = await StudentsOn(db).AddCardAsync(
                deletedId, new StudentCardRequest(RegNoAsCardUid, "Primary ID"));

            Assert.Equal(StudentWriteOutcome.Saved, issued.Outcome);
            cardId = issued.Card!.Id;
        }

        await using (var db = NewDbContext())
            Assert.Equal(StudentWriteOutcome.Saved, (await StudentsOn(db).DeleteAsync(deletedId)).Outcome);

        return new World(schoolId, deletedId, cardId, successorId);
    }

    // ------------------------------------------------------------------ what actually happens

    /// <summary>
    /// The state a soft delete leaves, asserted as fact.
    ///
    /// <para>
    /// The card is deliberately not deactivated — <c>DeleteAsync</c> documents why, and the reasoning is
    /// sound: deactivating it would rewrite the ADR-001 D-3 issuance history on an operation the caller
    /// did not ask for. So this test is not a complaint. It is the premise the two dead ends below rest
    /// on, pinned separately so that a future change which <em>does</em> cascade the deactivation shows
    /// up here as a deliberate decision rather than silently making the rest of this file vacuous.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_students_card_stays_active_and_keeps_its_slot_in_the_index()
    {
        var world = await ArrangeAsync();

        await using var read = NewDbContext();
        var card = await read.RfidCards.AsNoTracking().SingleAsync(c => c.Id == world.CardId);

        Assert.True(card.IsActive);
        Assert.Null(card.DeactivatedAt);
        Assert.Equal(world.DeletedStudentId, card.StudentId);
    }

    /// <summary>
    /// <b>The refusal's own advice, followed end to end.</b>
    ///
    /// <para>
    /// This test asserted the opposite until the Phase 3b-1 review gate: step 2 answered
    /// <c>NotFound</c>, because <c>DeactivateCardAsync</c> resolved the owner through
    /// <c>FindAsync</c>'s <c>!IsDeleted</c> filter, so the API refused to reissue the REGNO and then
    /// refused the recovery it had just recommended. JJ's decision was to drop that filter from this
    /// one lookup; the sequence below is what the <c>CardUidInUse</c> message has always described.
    /// </para>
    ///
    /// <para>
    /// <b>The asymmetry is the point and is asserted separately in
    /// <c>StudentCardLifecycleTests.A_card_cannot_be_assigned_to_a_soft_deleted_student</c>:</b>
    /// a deleted student's card can be <em>released</em> but no new card can be <em>issued</em> to
    /// them. Releasing is a deactivation, which ADR-001 D-3 designs to destroy nothing; issuing would
    /// be pretending they are on the roster.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_regno_of_a_soft_deleted_student_can_be_released_and_reissued()
    {
        var world = await ArrangeAsync();

        // 1. The successor cannot be given the REGNO while the deleted student's card holds it.
        await using (var db = NewDbContext())
        {
            var refused = await StudentsOn(db).AddCardAsync(
                world.SuccessorId, new StudentCardRequest(RegNoAsCardUid, null));

            Assert.Equal(StudentWriteOutcome.CardUidInUse, refused.Outcome);
            Assert.Contains("deactivate the existing card first", refused.Message, StringComparison.Ordinal);
        }

        // 2. ...and that advice is now actionable, which is the whole fix.
        await using (var db = NewDbContext())
        {
            var released = await StudentsOn(db).DeactivateCardAsync(world.DeletedStudentId, world.CardId);

            Assert.Equal(StudentWriteOutcome.Saved, released.Outcome);
            Assert.False(released.Card!.IsActive);
        }

        // 3. The UID is free, so the successor can be issued it.
        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.Saved,
                (await StudentsOn(db).AddCardAsync(
                    world.SuccessorId, new StudentCardRequest(RegNoAsCardUid, null))).Outcome);
        }

        await using var read = NewDbContext();

        // Both rows survive — the released one deactivated, the reissued one active. That is ADR-001
        // D-3's entire reason for being a filtered index rather than a global one.
        var cards = await read.RfidCards.AsNoTracking().Where(c => c.CardUid == RegNoAsCardUid).ToListAsync();
        Assert.Equal(2, cards.Count);
        Assert.Equal(world.SuccessorId, Assert.Single(cards, c => c.IsActive).StudentId);
        Assert.Equal(world.DeletedStudentId, Assert.Single(cards, c => !c.IsActive).StudentId);

        // The student the card was released from is still deleted. Freeing the card is not a restore,
        // and DEAD END 2 (the student number) is untouched by this fix.
        Assert.True((await read.Students.AsNoTracking()
            .SingleAsync(s => s.Id == world.DeletedStudentId)).IsDeleted);
    }

    /// <summary>
    /// The same sequence over HTTP, because the status codes are what a client actually branches on.
    ///
    /// <para>
    /// They used to read as a contradiction — <b>409 "deactivate the existing card first"</b> followed
    /// by <b>404</b> when you try to, which an SPA implementing the recovery flow could not tell from
    /// "wrong card id". Now the flow the 409 describes is 204 then 201.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Over_http_the_recovery_the_409_recommends_actually_works()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var reissue = await client.PostAsJsonAsync(
            $"{Route}/{world.SuccessorId}/cards", new { cardUid = RegNoAsCardUid });

        Assert.Equal(HttpStatusCode.Conflict, reissue.StatusCode);

        var release = await client.DeleteAsync(
            $"{Route}/{world.DeletedStudentId}/cards/{world.CardId}");

        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);

        var retried = await client.PostAsJsonAsync(
            $"{Route}/{world.SuccessorId}/cards", new { cardUid = RegNoAsCardUid });

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }

    /// <summary>
    /// <b>DEAD END 2 — the student number is held by a row the API cannot restore.</b>
    ///
    /// <para>
    /// <c>StudentWriteTests.A_student_number_held_by_a_deleted_student_is_still_taken</c> proves the
    /// refusal. This proves the part that makes it a problem: the refusal's message tells the caller to
    /// <em>"restore that student rather than creating a second one under the same number"</em>, and
    /// §6.2 defines no endpoint that restores anything. The advice is correct and unactionable.
    /// </para>
    ///
    /// <para>
    /// This is deliberately asserted against the message text, which the codebase elsewhere argues a
    /// client should never branch on. That is the point — <b>the recovery route exists only in prose</b>,
    /// so prose is the only thing there is to assert on, and that is itself the finding.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_deleted_students_number_names_a_restore_that_no_endpoint_performs()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var refused = await StudentsOn(db).CreateAsync(
                new StudentWriteRequest(RegNo, "Maria", null, "Santos", null, null, null, null));

            Assert.Equal(StudentWriteOutcome.DuplicateStudentNumber, refused.Outcome);
            Assert.Contains("restore that student", refused.Message, StringComparison.Ordinal);
        }

        // The two doors a client would try, both closed. `StudentWriteRequest` carries no `isDeleted`
        // by design (one door into the flag), and that door is DELETE, which only ever sets it.
        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.NotFound,
                (await StudentsOn(db).UpdateAsync(
                    world.DeletedStudentId,
                    new StudentWriteRequest(RegNo, "Maria", null, "Santos", null, null, null, null))).Outcome);
        }

        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.NotFound,
                (await StudentsOn(db).DeleteAsync(world.DeletedStudentId)).Outcome);
        }

        await using var read = NewDbContext();
        Assert.True((await read.Students.AsNoTracking()
            .SingleAsync(s => s.Id == world.DeletedStudentId)).IsDeleted);
    }

    // ------------------------------------------------------------- what should happen instead

    /// <summary>
    /// <b>The assertion that closed DEAD END 1.</b>
    ///
    /// <para>
    /// Written to the outcome rather than to a mechanism, because more than one mechanism would satisfy
    /// it and the choice was a product call rather than a QA one: <c>DeactivateCardAsync</c> could
    /// resolve its owner without the <c>!IsDeleted</c> filter; <c>DeleteAsync</c> could cascade the
    /// deactivation; or a restore endpoint could exist. JJ chose the first — the smallest change, and
    /// the only one that does not touch the issuance history or invent API surface.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_uid_held_by_a_soft_deleted_student_can_be_freed_and_reissued()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.Saved,
                (await StudentsOn(db).DeactivateCardAsync(world.DeletedStudentId, world.CardId)).Outcome);
        }

        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.Saved,
                (await StudentsOn(db).AddCardAsync(
                    world.SuccessorId, new StudentCardRequest(RegNoAsCardUid, null))).Outcome);
        }

        await using var read = NewDbContext();
        Assert.Equal(world.SuccessorId,
            (await read.RfidCards.AsNoTracking().SingleAsync(c => c.IsActive)).StudentId);
    }

    /// <summary>
    /// <b>DEFECT — <c>GET /students/by-card/{cardUid}</c> returns a soft-deleted student.</b>
    ///
    /// <para>
    /// <c>GetByCardUidAsync</c> resolves the card and hands back <c>card.Student</c> with no
    /// <c>IsDeleted</c> predicate anywhere in the query, while <c>ListAsync</c> and <c>GetAsync</c> both
    /// filter it. Reproduced: after <c>DELETE /students/{id}</c>, <c>GET /students/{id}</c> is 404,
    /// <c>GET /students</c> omits them, and <c>GET /students/by-card/USA00042</c> is <b>200 with their
    /// full name, e-mail and student number</b>.
    /// </para>
    ///
    /// <para>
    /// <b>This is the surviving half of <c>KnownDefectTests</c> DEFECT 1.</b> That defect was closed by
    /// adding <c>!c.Student!.IsDeleted</c> to <c>AttendanceService.TapAsync</c>'s card lookup, and the
    /// comment left behind at that call site states the reason as <em>"StudentService filters
    /// soft-deleted students on all three of its queries; this one resolved the student through the card
    /// and did not"</em>. That sentence is not true: two of the three filter, and the third is this one —
    /// which resolves the student through the card in exactly the same way the tap path did. The fix
    /// landed on the caller that had a test and not on the one that did not.
    /// </para>
    ///
    /// <para>
    /// <b>Why it matters beyond tidiness.</b> §6.2 assigns this endpoint the <c>attendance.capture</c>
    /// permission specifically so a kiosk or scan screen can resolve a UID <em>without</em> being
    /// trusted to browse the roster — so it is the one student read that will be reachable by a device
    /// API key under §11, and it is the one that ignores the deletion flag. The immediate effect is a
    /// scan screen that displays a deleted student's name and then fails their tap with
    /// <c>CardNotFound</c>; the §11 effect is that the least-trusted caller has the least-filtered read.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_student_is_not_returned_by_the_by_card_lookup()
    {
        var world = await ArrangeAsync();

        await using var read = NewDbContext();
        var students = StudentsOn(read);

        // The two reads that agree with the flag, as a control: whatever this endpoint does, it must
        // not disagree with them.
        Assert.Null(await students.GetAsync(world.DeletedStudentId));
        Assert.DoesNotContain(
            await students.ListAsync(null, null, null), s => s.Id == world.DeletedStudentId);

        Assert.Null(await students.GetByCardUidAsync(RegNoAsCardUid));
    }

    /// <summary>
    /// The same defect at the HTTP boundary, where the disclosure is the whole of the harm: a 200
    /// carrying a deleted student's name and institutional e-mail, from the endpoint §11 will expose to
    /// a device API key.
    /// </summary>
    [Fact]
    public async Task Over_http_the_by_card_lookup_agrees_with_the_student_detail_read()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"{Route}/{world.DeletedStudentId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"{Route}/by-card/{RegNoAsCardUid}")).StatusCode);
    }
}
