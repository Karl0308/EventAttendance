using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// What closing an event does, and — the part that matters — what a later roster import cannot do to
/// it afterwards.
///
/// <para>
/// <b>The rule being pinned, in the product owner's words:</b> a closed event counts "only those who
/// encoded on that event; the new student added or enrolled after the event was completed is not
/// needed". While an event is <c>Draft</c> or <c>Open</c> its audience is <em>live</em> — it re-reads
/// current section membership every time, so somebody enrolled the day before is correctly expected.
/// On the transition to <c>Closed</c> that answer is written down as <c>Absent</c> attendance rows, and
/// from then on the denominator is rows in a table rather than a join across data that keeps moving.
/// </para>
///
/// <para>
/// <b>Every test here re-imports or re-enrolls after the close</b>, because a freeze that is never
/// pushed on is indistinguishable from a query that happens not to have changed yet.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventCloseFreezeTests : IntegrationTest
{
    public EventCloseFreezeTests(SqlServerFixture sql) : base(sql) { }

    private const string Section = "BSCRIM 2-A";
    private const string TapperUid = "04A7B8C9";

    private sealed record World(
        Guid SchoolId, Guid TermId, Guid OfferingId, Guid EventId, Guid GroupId,
        Guid Tapper, Guid Absentee);

    /// <summary>
    /// One section, two enrolled students, one of whom holds a card. The event is Open so a tap can
    /// land before the close.
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        Guid schoolId, termId, offeringId, eventId, tapper, absentee;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);

            var term = TestData.NewTerm(school.Id);
            db.Terms.Add(term);

            var course = TestData.NewCourse(school.Id);
            db.Courses.Add(course);

            var offering = TestData.NewOffering(term.Id, course.Id, Section);
            db.CourseOfferings.Add(offering);

            var present = TestData.NewStudent(school.Id, "2023-0001", lastName: "Santos");
            var missing = TestData.NewStudent(school.Id, "2023-0002", lastName: "Cruz");
            db.Students.AddRange(present, missing);
            db.RfidCards.Add(TestData.NewCard(school.Id, present.Id, TapperUid));

            db.Enrollments.AddRange(
                TestData.NewEnrollment(present.Id, offering.Id),
                TestData.NewEnrollment(missing.Id, offering.Id));

            var ev = TestData.NewEvent(school.Id, EventStatus.Open);
            db.Events.Add(ev);

            await db.SaveChangesAsync();

            schoolId = school.Id;
            termId = term.Id;
            offeringId = offering.Id;
            eventId = ev.Id;
            tapper = present.Id;
            absentee = missing.Id;

            await ProjectionOn(db).SyncTermAsync(term.Id);
        }

        Guid groupId;
        await using (var read = NewDbContext())
        {
            groupId = await read.StudentGroups.AsNoTracking()
                .Where(g => g.SourceEntityType == GroupSourceEntityType.Section)
                .Select(g => g.Id)
                .SingleAsync();
        }

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).AttachAudienceAsync(
                eventId, new EventAudienceRequest([groupId], null));
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        return new World(schoolId, termId, offeringId, eventId, groupId, tapper, absentee);
    }

    /// <summary>Enrolls a brand-new student into the same section and re-projects — a later import.</summary>
    private async Task<Guid> EnrolAnotherStudentAsync(World world, string studentNumber)
    {
        await using var db = NewDbContext();

        var student = TestData.NewStudent(world.SchoolId, studentNumber, lastName: "Latecomer");
        db.Students.Add(student);
        db.Enrollments.Add(TestData.NewEnrollment(student.Id, world.OfferingId));
        await db.SaveChangesAsync();

        await ProjectionOn(db).SyncTermAsync(world.TermId);
        return student.Id;
    }

    private async Task TapAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(eventId, TapperUid, null, "tap-0001", TestData.Now));
        Assert.Equal(TapOutcome.Recorded, response.Outcome);
    }

    // ------------------------------------------------------------------------ the freeze

    /// <summary>
    /// The close materializes the expected roster: every expected student with no record gets one,
    /// <c>Absent</c> / <c>Import</c> / no check-in time.
    /// </summary>
    [Fact]
    public async Task Closing_an_event_writes_an_absent_record_for_every_expected_student_who_has_none()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId);

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
            Assert.Contains("Absent", response.Message, StringComparison.Ordinal);
        }

        await using var read = NewDbContext();
        var records = await read.AttendanceRecords.AsNoTracking().ToListAsync();

        Assert.Equal(2, records.Count);

        var frozen = Assert.Single(records, r => r.StudentId == world.Absentee);
        Assert.Equal(AttendanceStatus.Absent, frozen.Status);
        // Import, not Manual: no organizer made a judgement about this student, and labelling it Manual
        // would make a real override indistinguishable from a materialized absence in every report.
        Assert.Equal(CaptureMethod.Import, frozen.CaptureMethod);
        // §4.9 defines CheckInAt as "first tap". They did not tap; a timestamp would be a fabricated
        // observation. Same rule AttendanceService.RecordsAnArrival applies to a manual Absent.
        Assert.Null(frozen.CheckInAt);

        // The tap is untouched.
        var tapped = Assert.Single(records, r => r.StudentId == world.Tapper);
        Assert.Equal(AttendanceStatus.Present, tapped.Status);
        Assert.Equal(CaptureMethod.Rfid, tapped.CaptureMethod);
    }

    /// <summary>
    /// The absentee rows are attributed to whoever closed the event. Attribution is the one deferred
    /// thing that cannot be backfilled (<c>ICurrentUser</c>), and a bulk write of this size is exactly
    /// the kind somebody will later want to explain.
    /// </summary>
    [Fact]
    public async Task The_materialized_absentees_record_who_closed_the_event()
    {
        var world = await ArrangeAsync();

        Guid closerId;
        await using (var db = NewDbContext())
        {
            var user = TestData.NewUser(world.SchoolId, "registrar@test.local");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            closerId = user.Id;
        }

        CurrentUser.UserId = closerId;

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        await using var read = NewDbContext();
        Assert.All(await read.AttendanceRecords.AsNoTracking().ToListAsync(),
            r => Assert.Equal(closerId, r.RecordedByUserId));
    }

    /// <summary>
    /// After the close there is no absentee left to derive: they are all rows. That is what turns §12's
    /// Absentee Report from a set difference across three tables into a seek on
    /// <c>IX_Attendance_EventId_Status</c>.
    /// </summary>
    [Fact]
    public async Task After_the_close_the_absentee_report_is_an_indexed_status_query()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        await using var read = NewDbContext();
        var roster = await EventsOn(read).GetRosterAsync(world.EventId);

        Assert.True(roster!.IsFrozen);
        Assert.Equal(0, roster.NotRecorded);
        Assert.Equal(1, roster.Absent);

        // The §12 query, as it will actually be written.
        var absentees = await read.AttendanceRecords.AsNoTracking()
            .Where(a => a.EventId == world.EventId && a.Status == AttendanceStatus.Absent)
            .Select(a => a.StudentId)
            .ToListAsync();

        Assert.Equal([world.Absentee], absentees);
    }

    /// <summary>
    /// <b>The mechanism, asserted directly, because materializing the Absent records is only half of
    /// it and the missing half is invisible in the numbers on the day you write it.</b>
    ///
    /// <para>
    /// Absent rows fix the absentee <em>list</em>. They say nothing about the denominator, which is
    /// computed by walking group membership — so a first implementation that wrote only the Absent rows
    /// passed every "closing marks the absentees" test and still let the expected count drift the
    /// moment the next import touched the section. The close therefore also resolves the audience once
    /// and writes it down as individual §4.8 rows; that snapshot is what a closed event's denominator
    /// is read from.
    /// </para>
    ///
    /// <para>
    /// The group row survives alongside it. It is the only record of <em>which section</em> was
    /// invited — the question §4.8 exists to answer, and the one <c>StudentGroupProjection</c> refuses
    /// to delete groups in order to preserve.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Closing_writes_the_resolved_audience_down_and_keeps_the_group_it_came_from()
    {
        var world = await ArrangeAsync();

        await using (var read = NewDbContext())
        {
            // Before: one group row, and no individual rows at all.
            var rows = await read.EventGroups.AsNoTracking().ToListAsync();
            Assert.Equal(world.GroupId, Assert.Single(rows).StudentGroupId);
        }

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        await using var after = NewDbContext();
        var attachments = await after.EventGroups.AsNoTracking().ToListAsync();

        Assert.Single(attachments, a => a.StudentGroupId == world.GroupId);
        Assert.Equal(
            new HashSet<Guid> { world.Tapper, world.Absentee },
            attachments.Where(a => a.StudentId != null).Select(a => a.StudentId!.Value).ToHashSet());
    }

    // ---------------------------------------------------------------- the freeze holds

    /// <summary>
    /// <b>The central assertion of Phase 3a.</b> A student enrolled into the attached section
    /// <em>after</em> the close does not appear in the closed event's denominator, its absentee list, or
    /// its rate — while the live event's audience does pick them up, which is what proves the freeze is
    /// doing the work rather than the projection simply not having run.
    /// </summary>
    [Fact]
    public async Task A_student_enrolled_after_the_close_does_not_move_the_closed_events_numbers()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        EventSummaryDto before;
        await using (var read = NewDbContext())
            before = (await EventsOn(read).GetSummaryAsync(world.EventId))!;

        Assert.Equal(2, before.Expected);
        Assert.Equal(1, before.Present);
        Assert.Equal(1, before.Absent);
        Assert.Equal(50, before.AttendanceRate);

        // The later import.
        var latecomer = await EnrolAnotherStudentAsync(world, "2023-0099");

        // The projection really did add them to the group — otherwise this test proves nothing.
        await using (var read = NewDbContext())
        {
            Assert.Contains(
                await read.StudentGroupMembers.AsNoTracking()
                    .Where(m => m.StudentGroupId == world.GroupId)
                    .Select(m => m.StudentId)
                    .ToListAsync(),
                id => id == latecomer);
        }

        await using var after = NewDbContext();
        var summary = await EventsOn(after).GetSummaryAsync(world.EventId);

        Assert.Equal(before, summary);

        var roster = await EventsOn(after).GetRosterAsync(world.EventId);
        Assert.Equal(2, roster!.Expected);
        Assert.Equal(0, roster.NotRecorded);
        Assert.DoesNotContain(roster.Entries, e => e.StudentId == latecomer);
    }

    /// <summary>
    /// The control for the test above: the same import, against an event that is still <c>Open</c>,
    /// <em>does</em> move the numbers. Without this the freeze could be passing because nothing ever
    /// changes.
    /// </summary>
    [Fact]
    public async Task The_same_enrollment_does_move_an_open_events_numbers()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId);

        await using (var read = NewDbContext())
            Assert.Equal(2, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

        var latecomer = await EnrolAnotherStudentAsync(world, "2023-0099");

        await using var after = NewDbContext();
        var summary = await EventsOn(after).GetSummaryAsync(world.EventId);

        Assert.Equal(3, summary!.Expected);
        Assert.Equal(33.3, summary.AttendanceRate);

        var roster = await EventsOn(after).GetRosterAsync(world.EventId);
        Assert.Equal(2, roster!.NotRecorded);
        Assert.Contains(roster.Entries, e => e.StudentId == latecomer && e.IsExpected);
        Assert.False(roster.IsFrozen);
    }

    /// <summary>
    /// A student <em>removed</em> from the section after the close does not move the numbers either.
    /// The freeze has to hold in both directions, or a denominator could still shrink — and the import
    /// pipeline is upsert-only, so this arrives as a hand correction rather than a re-import.
    /// </summary>
    [Fact]
    public async Task A_student_unenrolled_after_the_close_still_counts()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        await using (var db = NewDbContext())
        {
            var enrollment = await db.Enrollments.SingleAsync(e => e.StudentId == world.Absentee);
            db.Enrollments.Remove(enrollment);
            await db.SaveChangesAsync();

            await ProjectionOn(db).SyncTermAsync(world.TermId);
        }

        await using var read = NewDbContext();
        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);

        Assert.Equal(2, summary!.Expected);
        Assert.Equal(1, summary.Absent);
        Assert.Equal(50, summary.AttendanceRate);
    }

    /// <summary>
    /// Detaching the group is impossible after a close (the audience is locked), so the denominator
    /// cannot be emptied that way either. Belt and braces on the same guarantee, from the other side.
    /// </summary>
    [Fact]
    public async Task A_closed_events_denominator_cannot_be_emptied_by_detaching_its_audience()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        await using var write = NewDbContext();
        Assert.Equal(EventWriteOutcome.EventLocked,
            (await EventsOn(write).DetachGroupAsync(world.EventId, world.GroupId)).Outcome);

        Assert.Equal(2, (await EventsOn(write).GetSummaryAsync(world.EventId))!.Expected);
    }

    // ------------------------------------------------------------------- idempotency

    /// <summary>
    /// A retried <c>PATCH status=Closed</c> must not re-run the freeze. This is the failure mode the
    /// <c>from != to</c> clause in <c>EventStatusTransition.FreezesRoster</c> exists to prevent: the
    /// second run would insert an <c>Absent</c> row for the student enrolled in between, which is
    /// precisely the movement the freeze exists to stop, arriving through the mechanism meant to stop
    /// it.
    /// </summary>
    [Fact]
    public async Task Re_closing_an_already_closed_event_does_not_re_materialize_its_roster()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        var latecomer = await EnrolAnotherStudentAsync(world, "2023-0099");

        await using (var db = NewDbContext())
        {
            var replay = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, replay.Outcome);
            Assert.Contains("already Closed", replay.Message, StringComparison.Ordinal);
        }

        await using var read = NewDbContext();
        Assert.Equal(2, await read.AttendanceRecords.CountAsync());
        Assert.Equal(0, await read.AttendanceRecords
            .CountAsync(a => a.StudentId == latecomer));
    }

    /// <summary>
    /// Closing an event whose expected students all have records writes nothing. Not an optimization —
    /// evidence that the freeze is a set difference and not a blind insert, which is what makes the
    /// unique index a backstop rather than the mechanism.
    /// </summary>
    [Fact]
    public async Task Closing_an_event_where_everybody_was_recorded_writes_nothing()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId);

        await using (var db = NewDbContext())
        {
            var manual = await AttendanceOn(db).ManualAsync(
                world.EventId, world.Absentee, AttendanceStatus.Excused, "Medical certificate.");
            Assert.Equal(ManualOutcome.Saved, manual.Outcome);
        }

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Contains("0 expected", response.Message, StringComparison.Ordinal);
        }

        await using var read = NewDbContext();
        var records = await read.AttendanceRecords.AsNoTracking().ToListAsync();

        Assert.Equal(2, records.Count);
        // The hand-written Excused survived; it was not overwritten with Absent.
        Assert.Equal(AttendanceStatus.Excused,
            Assert.Single(records, r => r.StudentId == world.Absentee).Status);
    }

    /// <summary>
    /// An event with no audience closes cleanly and writes nothing. Its denominator was already zero
    /// and the close does not invent one.
    /// </summary>
    [Fact]
    public async Task Closing_an_event_with_no_audience_writes_nothing()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await EventsOn(db).DetachGroupAsync(world.EventId, world.GroupId);

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        await using var read = NewDbContext();
        Assert.Equal(0, await read.AttendanceRecords.CountAsync());
        Assert.Equal(0, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);
    }

    /// <summary>
    /// Cancelling materializes no <em>absentees</em>. An event that did not happen has no attendance to
    /// fix, and marking a cohort <c>Absent</c> for it would put people on record as having missed
    /// something nobody held.
    ///
    /// <para>
    /// It does still snapshot the audience — see
    /// <see cref="Cancelling_freezes_the_audience_without_writing_absentees"/> for the other half, and
    /// why the two are not the same thing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Cancelling_an_event_does_not_materialize_a_roster()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Cancelled);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.AttendanceRecords.CountAsync());

        var roster = await EventsOn(read).GetRosterAsync(world.EventId);

        // Frozen, because the denominator can no longer move — not because absentees were written.
        // IsFrozen is the published statement "these numbers are fixed", and after the audience
        // snapshot that is as true of a cancelled event as of a closed one.
        Assert.True(roster!.IsFrozen);
        Assert.Equal(0, roster.Absent);
        // Nobody attended, and every expected student is simply un-recorded. That is the honest shape
        // for an event that did not happen: an invitation list and no attendance.
        Assert.Equal(2, roster.NotRecorded);
    }

    /// <summary>
    /// <b>Cancelling freezes the audience, and that is a correction rather than a refinement.</b>
    ///
    /// <para>
    /// <c>Cancelled</c> is terminal and its audience is already locked against edits, but the
    /// denominator was still being <em>resolved</em> through live group membership — so an
    /// <c>Open → Cancelled</c> event that had already taken taps had an expected count that walked with
    /// every later import. A rate changing month over month for an event that is over, with no absentee
    /// list to explain it: exactly the failure the close freeze exists to prevent, surviving on the
    /// other terminal status.
    /// </para>
    ///
    /// <para>
    /// The invite record is kept rather than nulled. Who was invited to an event that was called off is
    /// a real question — it is who needs telling — so the answer is preserved and simply stops moving.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Cancelling_freezes_the_audience_without_writing_absentees()
    {
        var world = await ArrangeAsync();

        await using (var read = NewDbContext())
        {
            // Before: one group row and no individual rows, same as the close case starts from.
            var rows = await read.EventGroups.AsNoTracking().ToListAsync();
            Assert.Equal(world.GroupId, Assert.Single(rows).StudentGroupId);
        }

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Cancelled);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        await using var after = NewDbContext();
        var attachments = await after.EventGroups.AsNoTracking().ToListAsync();

        // The group row survives — it is the only record of which section was invited.
        Assert.Single(attachments, a => a.StudentGroupId == world.GroupId);
        // And the resolved audience is now written down beside it.
        Assert.Equal(
            new HashSet<Guid> { world.Tapper, world.Absentee },
            attachments.Where(a => a.StudentId != null).Select(a => a.StudentId!.Value).ToHashSet());

        // Absentees, specifically, were not written. That is the whole difference from a close.
        Assert.Equal(0, await after.AttendanceRecords.CountAsync());
    }

    /// <summary>
    /// The freeze proven the only way it can be: by pushing on it. A student enrolled into the attached
    /// section after the cancellation does not move the cancelled event's expected count, while the
    /// projection demonstrably did add them to the group.
    /// </summary>
    [Fact]
    public async Task A_cancelled_events_expected_count_does_not_drift_across_a_later_import()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Cancelled);

        EventSummaryDto before;
        await using (var read = NewDbContext())
            before = (await EventsOn(read).GetSummaryAsync(world.EventId))!;

        Assert.Equal(2, before.Expected);

        var latecomer = await EnrolAnotherStudentAsync(world, "2023-0099");

        // The import really did land, or this test proves nothing.
        await using (var read = NewDbContext())
        {
            Assert.Contains(
                await read.StudentGroupMembers.AsNoTracking()
                    .Where(m => m.StudentGroupId == world.GroupId)
                    .Select(m => m.StudentId)
                    .ToListAsync(),
                id => id == latecomer);
        }

        await using var after = NewDbContext();

        Assert.Equal(before, await EventsOn(after).GetSummaryAsync(world.EventId));

        var roster = await EventsOn(after).GetRosterAsync(world.EventId);
        Assert.Equal(2, roster!.Expected);
        Assert.DoesNotContain(roster.Entries, e => e.StudentId == latecomer);
    }

    // ------------------------------------------------------- the soft-delete boundary

    /// <summary>
    /// <b>The close resolves its audience including soft-deleted students, and the reason is that the
    /// frozen read has no choice but to.</b>
    ///
    /// <para>
    /// A closed event's denominator counts every individual §4.8 row regardless of the student's deleted
    /// flag — it must, because nothing distinguishes a student deleted before the close from one deleted
    /// after, and the second must never shrink a past event's numbers. If the snapshot had resolved
    /// <em>excluding</em> the deleted, an already-deleted individually-attached student would carry a
    /// pre-existing row into the denominator while receiving no <c>Absent</c> record: the totals would
    /// be permanently one short of expected, and <c>NotRecorded</c> would read 1 on a frozen event —
    /// the state the freeze exists to make impossible.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_student_soft_deleted_before_the_close_still_gets_an_absent_row()
    {
        var world = await ArrangeAsync();

        // Attach a third student individually, then soft-delete them before the event closes.
        Guid deleted;
        await using (var db = NewDbContext())
        {
            var student = TestData.NewStudent(world.SchoolId, "2023-0050", lastName: "Departed");
            db.Students.Add(student);
            await db.SaveChangesAsync();
            deleted = student.Id;

            var attach = await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest(null, [student.Id]));
            Assert.Equal(EventWriteOutcome.Saved, attach.Outcome);
        }

        await using (var db = NewDbContext())
        {
            var student = await db.Students.SingleAsync(s => s.Id == deleted);
            student.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        // While live they are correctly not expected: a student the roster says does not exist cannot
        // be expected to attend.
        await using (var read = NewDbContext())
            Assert.Equal(2, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        await using var after = NewDbContext();
        var summary = await EventsOn(after).GetSummaryAsync(world.EventId);

        // The frozen denominator counts their attachment row, so the close had to write their absence.
        Assert.Equal(3, summary!.Expected);
        Assert.Equal(3, summary.Present + summary.Late + summary.Absent + summary.Excused);

        var roster = await EventsOn(after).GetRosterAsync(world.EventId);
        Assert.True(roster!.IsFrozen);
        // The assertion that would have failed before the fix.
        Assert.Equal(0, roster.NotRecorded);
        Assert.Equal(AttendanceStatus.Absent,
            Assert.Single(roster.Entries, e => e.StudentId == deleted).Status);
    }

    /// <summary>
    /// The case the whole soft-delete asymmetry exists for, and which nothing pinned until now: a
    /// student deleted <em>after</em> the close must not shrink the denominator. "Frozen" has to mean
    /// frozen against every later edit, not only against enrollment.
    /// </summary>
    [Fact]
    public async Task A_student_soft_deleted_after_the_close_does_not_shrink_the_denominator()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        EventSummaryDto before;
        await using (var read = NewDbContext())
            before = (await EventsOn(read).GetSummaryAsync(world.EventId))!;

        Assert.Equal(2, before.Expected);
        Assert.Equal(50, before.AttendanceRate);

        await using (var db = NewDbContext())
        {
            var student = await db.Students.SingleAsync(s => s.Id == world.Absentee);
            student.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var after = NewDbContext();

        // Unchanged in every number, including the rate — which would have risen to 100% if the
        // denominator had followed the deletion.
        Assert.Equal(before, await EventsOn(after).GetSummaryAsync(world.EventId));
        Assert.Equal(2, (await EventsOn(after).GetRosterAsync(world.EventId))!.Expected);
    }

    /// <summary>
    /// The race the close has to survive: a tap landing between reading who already has a record and
    /// committing the absentees. Simulated deterministically by writing the row on a second connection
    /// after the diff would have been computed — the pending <c>Absent</c> loses to
    /// <c>UX_Attendance_Event_Student_Occurrence</c>, and losing is the right answer, because the row
    /// that beat it is the truer record.
    ///
    /// <para>
    /// Verified as an outcome rather than by instrumenting the retry: what matters is that the close
    /// succeeds, the tap's <c>Present</c> survives, and no duplicate exists.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tap_landing_during_the_close_wins_and_the_close_still_succeeds()
    {
        var world = await ArrangeAsync();

        // Both expected students are un-recorded, so the close will stage two absentees. One of them
        // gets a real record from another connection first.
        await using (var racing = NewDbContext())
        {
            racing.AttendanceRecords.Add(new AttendanceRecord
            {
                EventId = world.EventId, StudentId = world.Absentee,
                CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
                CaptureMethod = CaptureMethod.Rfid,
            });
            await racing.SaveChangesAsync();
        }

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        await using var read = NewDbContext();
        var records = await read.AttendanceRecords.AsNoTracking().ToListAsync();

        Assert.Equal(2, records.Count);
        Assert.Equal(AttendanceStatus.Present,
            Assert.Single(records, r => r.StudentId == world.Absentee).Status);
        Assert.Equal(AttendanceStatus.Absent,
            Assert.Single(records, r => r.StudentId == world.Tapper).Status);
    }

    /// <summary>
    /// A closed event rejects further taps, which is the other half of why the numbers hold: the freeze
    /// stops the denominator moving, and this stops the numerator moving. Already the behaviour before
    /// Phase 3a; asserted here because the freeze would be worth much less without it.
    /// </summary>
    [Fact]
    public async Task A_closed_event_rejects_further_taps()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        await using var tap = NewDbContext();
        var response = await AttendanceOn(tap).TapAsync(
            new TapRequest(world.EventId, TapperUid, null, "late-0001", TestData.Now));

        Assert.Equal(TapOutcome.EventNotOpen, response.Outcome);
    }
}
