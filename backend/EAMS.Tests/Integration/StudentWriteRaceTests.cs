using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The races <see cref="StudentWriteTests"/> does not run.
///
/// <para>
/// That file covers the two symmetric cases — several creates of one student number, several card
/// assignments of one UID to <em>different</em> students — and both are handled by the same shape:
/// lose to the index, come back as a 409. The cases below are the asymmetric ones, where the two
/// racers take <b>different code paths</b> and therefore hit different <c>catch</c> blocks, or where
/// one racer's win is supposed to be a <em>success</em> for the loser rather than a conflict.
/// </para>
///
/// <para>
/// <b>Every test here gates its tasks on a <see cref="TaskCompletionSource"/>,</b> for the reason
/// <c>TapFlowTests</c> records and this repo has already been burned by: without it the tasks start in
/// sequence as the enumerable is materialized, the first finishes comfortably before the second
/// begins, and the test passes green having never produced the race it is named for.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentWriteRaceTests : IntegrationTest
{
    public StudentWriteRaceTests(SqlServerFixture sql) : base(sql) { }

    private const string Uid = "04A7B8C9";

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> ArrangeStudentAsync(Guid schoolId, string studentNumber)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, studentNumber);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private static StudentWriteRequest Request(string studentNumber) =>
        new(studentNumber, "Juan", null, "Dela Cruz", null, null, null, null);

    /// <summary>
    /// Starts every task at once and returns their results. The gate is released only after all of them
    /// are parked on it, so the first database call each one makes happens within microseconds of the
    /// others'.
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

    // ------------------------------------------------------------------------------- cards

    /// <summary>
    /// <b>One student, one UID, six simultaneous assignments — every one of them must succeed.</b>
    ///
    /// <para>
    /// <c>Concurrent_assignments_of_one_card_uid_leave_exactly_one_active_card</c> races four
    /// <em>different</em> students, so exactly one caller is entitled to a success and the other three
    /// are conflicts. This races one student against itself, which is what a double-clicked "assign
    /// card" button and a retried mobile request actually produce — and it is the only shape that
    /// exercises <c>AddCardAsync</c>'s recovery branch, the one that re-reads after losing the insert
    /// and decides whether the winner was <em>us</em>.
    /// </para>
    ///
    /// <para>
    /// Getting that branch wrong is not a 500; it is a <c>CardUidInUse</c> 409 telling an operator that
    /// the card they just assigned belongs to somebody else — naming, in the message, the very student
    /// they are looking at. The sequential version of this (<c>Assigning_a_card_a_student_already_has
    /// _writes_no_second_row</c>) cannot reach the branch at all, because its pre-read always finds the
    /// first card.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Six_simultaneous_assignments_of_one_uid_to_one_student_all_succeed_on_one_row()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId, "2023-0001");

        var responses = await RaceAsync(Enumerable.Range(0, 6).Select(_ =>
            new Func<Task<StudentCardResponse>>(async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db).AddCardAsync(studentId, new StudentCardRequest(Uid, null));
            })).ToArray());

        Assert.All(responses, r => Assert.Equal(StudentWriteOutcome.Saved, r.Outcome));

        await using var read = NewDbContext();
        var card = Assert.Single(await read.RfidCards.AsNoTracking().ToListAsync());

        Assert.True(card.IsActive);
        // Every caller was handed the same card, not six ids five of which name nothing.
        Assert.All(responses, r => Assert.Equal(card.Id, r.Card!.Id));
    }

    /// <summary>
    /// An assignment racing the deactivation that would free the UID.
    ///
    /// <para>
    /// The registrar's reissue workflow is two calls — deactivate the old card, assign the new one — and
    /// ADR-001 D-3 makes them two calls on purpose. Two operators doing halves of it at the same
    /// instant is ordinary, and the outcome is legitimately non-deterministic: whether the claimant sees
    /// the old card as still active depends on which transaction commits first.
    /// </para>
    ///
    /// <para>
    /// <b>So the assertion is the invariant rather than the outcome.</b> Either result is correct; what
    /// must never happen is two active rows for one UID in one school, or an unhandled
    /// <c>DbUpdateException</c> from the losing insert. A test asserting a particular outcome here would
    /// be flaky, and a test asserting nothing would be worthless — the invariant is the middle.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_assignment_racing_a_deactivation_never_leaves_two_active_cards_on_one_uid()
    {
        var schoolId = await ArrangeSchoolAsync();
        var ownerId = await ArrangeStudentAsync(schoolId, "2023-0001");
        var claimantId = await ArrangeStudentAsync(schoolId, "2023-0002");

        Guid cardId;
        await using (var arrange = NewDbContext())
        {
            var card = TestData.NewCard(schoolId, ownerId, Uid);
            arrange.RfidCards.Add(card);
            await arrange.SaveChangesAsync();
            cardId = card.Id;
        }

        var results = await RaceAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db).DeactivateCardAsync(ownerId, cardId);
            },
            async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db).AddCardAsync(claimantId, new StudentCardRequest(Uid, null));
            });

        var deactivation = results[0];
        var assignment = results[1];

        Assert.Equal(StudentWriteOutcome.Saved, deactivation.Outcome);
        Assert.True(
            assignment.Outcome is StudentWriteOutcome.Saved or StudentWriteOutcome.CardUidInUse,
            $"An assignment racing a deactivation must resolve to a success or a conflict, never " +
            $"{assignment.Outcome} and never an unhandled exception. Got {assignment.Outcome}.");

        await using var read = NewDbContext();
        var active = await read.RfidCards.AsNoTracking()
            .Where(c => c.CardUid == Uid && c.IsActive).ToListAsync();

        Assert.True(active.Count <= 1,
            $"UX_RfidCards_SchoolId_CardUid_Active permits one active row per UID per school; found " +
            $"{active.Count}.");

        if (assignment.Outcome == StudentWriteOutcome.Saved)
            Assert.Equal(claimantId, Assert.Single(active).StudentId);
    }

    // -------------------------------------------------------------------------- student number

    /// <summary>
    /// Two existing students renamed onto one free number at the same instant.
    ///
    /// <para>
    /// The create-versus-create race is covered; this is the update-versus-update one, and it is a
    /// different code path — <c>UpdateAsync</c> has its own pre-check (with an <c>excluding</c> clause
    /// the create path does not have) and its own <c>catch</c>. Nothing exercised that <c>catch</c>
    /// before: every existing duplicate test on the update path is resolved by the pre-check, so the
    /// handler behind it was unreached code that would have surfaced as a 500 on the first real
    /// collision.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_students_renamed_onto_one_free_number_leave_exactly_one_holder()
    {
        var schoolId = await ArrangeSchoolAsync();
        var firstId = await ArrangeStudentAsync(schoolId, "2023-0001");
        var secondId = await ArrangeStudentAsync(schoolId, "2023-0002");

        const string Contested = "2023-9999";

        var responses = await RaceAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db).UpdateAsync(firstId, Request(Contested));
            },
            async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db).UpdateAsync(secondId, Request(Contested));
            });

        Assert.Single(responses, r => r.Outcome == StudentWriteOutcome.Saved);
        Assert.Single(responses, r => r.Outcome == StudentWriteOutcome.DuplicateStudentNumber);

        await using var read = NewDbContext();
        Assert.Single(await read.Students.AsNoTracking()
            .Where(s => s.StudentNumber == Contested).ToListAsync());
    }

    /// <summary>
    /// A create racing an update onto the same free number — the cross-path case, where the two racers
    /// are running different methods with different pre-checks and different recovery code.
    ///
    /// <para>
    /// Worth its own test rather than assumed from the two same-path races: <c>CreateAsync</c>'s
    /// handler detaches the failed insert before returning (a retained <c>Added</c> row would shadow
    /// every later read of that key through identity resolution) and <c>UpdateAsync</c>'s does not,
    /// because a failed update leaves a <c>Modified</c> entity rather than an <c>Added</c> one. Two
    /// different recoveries, and only a race that can select either one covers both.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_create_racing_a_rename_onto_one_number_leaves_exactly_one_holder()
    {
        var schoolId = await ArrangeSchoolAsync();
        var existingId = await ArrangeStudentAsync(schoolId, "2023-0001");

        const string Contested = "2023-9999";

        var responses = await RaceAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db).CreateAsync(Request(Contested));
            },
            async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db).UpdateAsync(existingId, Request(Contested));
            });

        Assert.Single(responses, r => r.Outcome == StudentWriteOutcome.Saved);
        Assert.Single(responses, r => r.Outcome == StudentWriteOutcome.DuplicateStudentNumber);

        await using var read = NewDbContext();
        Assert.Single(await read.Students.AsNoTracking()
            .Where(s => s.StudentNumber == Contested).ToListAsync());
    }

    /// <summary>
    /// A soft delete racing a re-create of the number it holds.
    ///
    /// <para>
    /// <c>A_student_number_held_by_a_deleted_student_is_still_taken</c> proves the sequential form.
    /// This proves the property that makes it safe rather than merely true: the number is <b>never
    /// briefly free</b>. <c>IX_Students_SchoolId_StudentNumber</c> is unfiltered, so the row keeps its
    /// slot through the delete, and there is no window — however narrow — in which a concurrent create
    /// slips a second student in under the same number and leaves the roster with two.
    /// </para>
    ///
    /// <para>
    /// That window is what an <c>IsDeleted</c>-filtered index would open, and adding one is a plausible
    /// future "fix" for the dead end this same soft delete creates (see
    /// <see cref="StudentSoftDeleteStrandingTests"/>). This test is what would object.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_soft_delete_never_briefly_frees_the_number_for_a_concurrent_create()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId, "2023-0001");

        var results = await RaceAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db).DeleteAsync(studentId);
            },
            async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db).CreateAsync(Request("2023-0001"));
            });

        Assert.Equal(StudentWriteOutcome.Saved, results[0].Outcome);
        Assert.Equal(StudentWriteOutcome.DuplicateStudentNumber, results[1].Outcome);

        await using var read = NewDbContext();
        var holders = await read.Students.AsNoTracking()
            .Where(s => s.StudentNumber == "2023-0001").ToListAsync();

        Assert.Equal(studentId, Assert.Single(holders).Id);
    }
}
