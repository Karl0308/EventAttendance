using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// §6.3's <c>POST /events/{id}/attendees</c> and <c>GET /events/{id}/roster</c>, and the §4.5/§12
/// denominator they exist to produce.
///
/// <para>
/// <b>The audience is built out of real projected groups, not hand-made ones.</b> A test that attaches
/// a <c>StudentGroup</c> it wrote itself proves the join works and nothing else; the interesting
/// property — that "everyone in BSCRIM 2-A" resolves through <c>Enrollments</c> and therefore includes
/// the 23% of students who sit in more than one section — only exists once the projection has run. So
/// these tests import through <c>StudentGroupProjection</c> the way the product does.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventRosterTests : IntegrationTest
{
    public EventRosterTests(SqlServerFixture sql) : base(sql) { }

    private const string SectionA = "BSCRIM 2-A";
    private const string SectionB = "BSCRIM 2-B";

    /// <summary>
    /// One term, two sections, and a student in both — the shape ADR-001 D-2 says is true of twelve of
    /// the fifty-two real students and which <c>Students.Section</c> cannot represent.
    /// </summary>
    private sealed record World(
        Guid SchoolId, Guid TermId, Guid EventId,
        Guid GroupA, Guid GroupB,
        Guid OnlyInA, Guid OnlyInB, Guid InBoth);

    private async Task<World> ArrangeAsync(string eventStatus = EventStatus.Open)
    {
        Guid schoolId, termId, eventId, onlyInA, onlyInB, inBoth;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);

            var term = TestData.NewTerm(school.Id);
            db.Terms.Add(term);

            var course = TestData.NewCourse(school.Id);
            db.Courses.Add(course);
            var second = TestData.NewCourse(school.Id, "MS 32", "Military Science 32");
            db.Courses.Add(second);

            var offeringA = TestData.NewOffering(term.Id, course.Id, SectionA);
            var offeringB = TestData.NewOffering(term.Id, second.Id, SectionB);
            db.CourseOfferings.AddRange(offeringA, offeringB);

            var a = TestData.NewStudent(school.Id, "2023-0001", lastName: "Santos");
            var b = TestData.NewStudent(school.Id, "2023-0002", lastName: "Cruz");
            var both = TestData.NewStudent(school.Id, "2023-0003", lastName: "Tan");
            db.Students.AddRange(a, b, both);

            db.Enrollments.AddRange(
                TestData.NewEnrollment(a.Id, offeringA.Id),
                TestData.NewEnrollment(b.Id, offeringB.Id),
                TestData.NewEnrollment(both.Id, offeringA.Id),
                TestData.NewEnrollment(both.Id, offeringB.Id));

            var ev = TestData.NewEvent(school.Id, eventStatus);
            db.Events.Add(ev);

            await db.SaveChangesAsync();

            schoolId = school.Id;
            termId = term.Id;
            eventId = ev.Id;
            onlyInA = a.Id;
            onlyInB = b.Id;
            inBoth = both.Id;

            await ProjectionOn(db).SyncTermAsync(term.Id);
        }

        await using var read = NewDbContext();
        var groups = await read.StudentGroups.AsNoTracking()
            .Where(g => g.SourceEntityType == GroupSourceEntityType.Section)
            .ToDictionaryAsync(g => g.SourceKey, g => g.Id);

        return new World(
            schoolId, termId, eventId,
            groups[AcademicKey.NormalizeOrUnspecified(SectionA)],
            groups[AcademicKey.NormalizeOrUnspecified(SectionB)],
            onlyInA, onlyInB, inBoth);
    }

    private static EventAudienceRequest Groups(params Guid[] ids) => new(ids, null);
    private static EventAudienceRequest Students(params Guid[] ids) => new(null, ids);

    // ----------------------------------------------------------------------- attaching

    [Fact]
    public async Task Attaching_a_section_makes_its_enrolled_students_the_expected_attendees()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));

            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
            Assert.Equal(1, response.Result!.GroupsAttached);
            Assert.Equal(2, response.Result.Expected);
            Assert.Empty(response.Result.Warnings);
        }

        await using var read = NewDbContext();
        var roster = await EventsOn(read).GetRosterAsync(world.EventId);

        Assert.Equal(2, roster!.Expected);
        Assert.Contains(roster.Entries, e => e.StudentId == world.OnlyInA);
        Assert.Contains(roster.Entries, e => e.StudentId == world.InBoth);
        Assert.DoesNotContain(roster.Entries, e => e.StudentId == world.OnlyInB);
    }

    /// <summary>
    /// <b>The de-duplication, which is the whole reason the denominator is a <c>UNION</c>.</b> Three
    /// students across two sections, one of them in both — the expected count is three, not four. Under
    /// a naive join it is four, and the attendance rate is depressed by 25% with nothing to explain it.
    /// </summary>
    [Fact]
    public async Task A_student_in_two_attached_sections_is_one_expected_attendee()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).AttachAudienceAsync(
                world.EventId, Groups(world.GroupA, world.GroupB));

            Assert.Equal(2, response.Result!.GroupsAttached);
            Assert.Equal(3, response.Result.Expected);
        }

        await using var read = NewDbContext();
        var roster = await EventsOn(read).GetRosterAsync(world.EventId);

        Assert.Equal(3, roster!.Expected);
        Assert.Single(roster.Entries, e => e.StudentId == world.InBoth);

        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);
        Assert.Equal(3, summary!.Expected);
    }

    /// <summary>
    /// The same student attached twice — once through a section and once individually — is still one
    /// expected attendee. The second half of the same <c>UNION</c>.
    /// </summary>
    [Fact]
    public async Task A_student_attached_both_individually_and_through_a_group_is_counted_once()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest([world.GroupA], [world.OnlyInA]));

            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
            Assert.Equal(2, response.Result!.Expected);
        }

        await using var read = NewDbContext();
        Assert.Equal(2, (await EventsOn(read).GetRosterAsync(world.EventId))!.Expected);
    }

    /// <summary>
    /// Idempotency, which is a database guarantee here rather than a service convention — see
    /// <c>UX_EventGroups_Event_Group</c>. Re-posting is what a slow first response produces.
    /// </summary>
    [Fact]
    public async Task Re_posting_the_same_selection_attaches_nothing_and_says_so()
    {
        var world = await ArrangeAsync();
        var selection = new EventAudienceRequest([world.GroupA, world.GroupB], [world.OnlyInA]);

        await using (var db = NewDbContext())
            await EventsOn(db).AttachAudienceAsync(world.EventId, selection);

        await using (var db = NewDbContext())
        {
            var replay = await EventsOn(db).AttachAudienceAsync(world.EventId, selection);

            Assert.Equal(EventWriteOutcome.Saved, replay.Outcome);
            Assert.Equal(0, replay.Result!.GroupsAttached);
            Assert.Equal(0, replay.Result.StudentsAttached);
            Assert.Equal(2, replay.Result.GroupsAlreadyAttached);
            Assert.Equal(1, replay.Result.StudentsAlreadyAttached);
            Assert.Equal(3, replay.Result.Expected);
        }

        await using var read = NewDbContext();
        Assert.Equal(3, await read.EventGroups.CountAsync());
    }

    /// <summary>
    /// The service de-duplicates within one payload too. A UI that lets an organizer tick a section
    /// twice through two different pickers would otherwise send it twice and hit the unique index as a
    /// 500 instead of doing the obvious thing.
    /// </summary>
    [Fact]
    public async Task A_payload_naming_the_same_group_twice_attaches_it_once()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).AttachAudienceAsync(
            world.EventId, Groups(world.GroupA, world.GroupA));

        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        Assert.Equal(1, response.Result!.GroupsAttached);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.EventGroups.CountAsync());
    }

    /// <summary>
    /// The database's half of the same guarantee, proven by going around the service. Two rows the
    /// service would never write are rejected by <c>UX_EventGroups_Event_Group</c>, which is what makes
    /// idempotency survive two concurrent posts rather than only two sequential ones.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_a_duplicate_attachment_written_around_the_service()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        db.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentGroupId = world.GroupA });
        await db.SaveChangesAsync();

        await using var second = NewDbContext();
        second.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentGroupId = world.GroupA });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    /// <summary>
    /// <b>And the service's half, which is the one that was missing.</b> The index above protects the
    /// data, but the service met the violation it raises with a bare <c>SaveChangesAsync</c> — so the
    /// scenario <c>EamsDbContext</c> names in the index's own comment, an organizer double-submitting a
    /// slow form, produced an unhandled <c>DbUpdateException</c> and a 500 on the losing request.
    ///
    /// <para>
    /// The race is opened deterministically with EF's <c>SavingChanges</c> event, the same technique
    /// <c>ManualOverrideTests</c> uses: it fires after the service has read what is attached and before
    /// the insert reaches SQL Server, so the conflicting row is guaranteed to land inside the window
    /// rather than probably landing there. A timing-based version would pass on a fast machine with the
    /// defect still present.
    /// </para>
    ///
    /// <para>
    /// The assertion that carries the weight is the <em>shape</em> of the response, not merely that it
    /// did not throw: the loser must return the idempotent <c>alreadyAttached</c> answer a sequential
    /// re-post already returns, because that is what makes the two indistinguishable to a caller — which
    /// is the entire claim in <c>IEventService.AttachAudienceAsync</c>'s "two concurrent posts cannot
    /// both win".
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_concurrent_double_submit_is_idempotent_rather_than_a_server_error()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var raced = false;
        db.SavingChanges += (_, _) =>
        {
            if (raced) return; // Only the first save — the retry's own insert must be allowed to land.
            raced = true;

            // The other submission of the same form, committing first.
            using var rival = NewDbContext();
            rival.EventGroups.Add(
                new EventGroup { EventId = world.EventId, StudentGroupId = world.GroupA });
            rival.SaveChanges();
        };

        var response = await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));

        Assert.True(raced, "The race never fired, so this test proved nothing about it.");
        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);

        // Exactly the shape a sequential re-post returns: nothing attached by us, one already there.
        Assert.Equal(0, response.Result!.GroupsAttached);
        Assert.Equal(1, response.Result.GroupsAlreadyAttached);
        Assert.Equal(2, response.Result.Expected);

        // And the rival's row is the only one — no duplicate, and nothing lost.
        await using var read = NewDbContext();
        Assert.Equal(world.GroupA, Assert.Single(await read.EventGroups.AsNoTracking().ToListAsync())
            .StudentGroupId);
    }

    /// <summary>
    /// A partially-overlapping double submit. Because the whole <c>SaveChanges</c> is one transaction,
    /// losing on the shared group rolls back the un-shared one too — so a retry that assumed a total
    /// loss and reported everything as already-attached would silently drop it. The diff is recomputed
    /// against what is now committed instead, and the un-shared group really is attached.
    /// </summary>
    [Fact]
    public async Task A_partially_overlapping_double_submit_still_attaches_the_rest()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var raced = false;
        db.SavingChanges += (_, _) =>
        {
            if (raced) return;
            raced = true;

            using var rival = NewDbContext();
            rival.EventGroups.Add(
                new EventGroup { EventId = world.EventId, StudentGroupId = world.GroupA });
            rival.SaveChanges();
        };

        // We asked for both; the rival took A out from under us.
        var response = await EventsOn(db).AttachAudienceAsync(
            world.EventId, Groups(world.GroupA, world.GroupB));

        Assert.True(raced, "The race never fired, so this test proved nothing about it.");
        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        Assert.Equal(1, response.Result!.GroupsAttached);
        Assert.Equal(1, response.Result.GroupsAlreadyAttached);
        // All three students across both sections, de-duplicated.
        Assert.Equal(3, response.Result.Expected);

        await using var read = NewDbContext();
        Assert.Equal(
            new HashSet<Guid> { world.GroupA, world.GroupB },
            (await read.EventGroups.AsNoTracking().ToListAsync())
                .Select(eg => eg.StudentGroupId!.Value).ToHashSet());
    }

    [Fact]
    public async Task An_empty_payload_is_a_no_op_rather_than_an_error()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).AttachAudienceAsync(
            world.EventId, new EventAudienceRequest(null, null));

        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        Assert.Equal(0, response.Result!.Expected);
    }

    /// <summary>
    /// <b>An all-zero-GUID payload is a rejection, not a no-op.</b>
    ///
    /// <para>
    /// <c>Guid.Empty</c> used to be filtered out silently while de-duplicating, and because the
    /// requested count was taken <em>after</em> the drop, a payload naming nothing but zero GUIDs came
    /// back "Saved, 0 attached" — a success for a request that referenced nothing that exists. It is
    /// now indistinguishable from any other unresolvable id, which is what it is: a client sending one
    /// has a bug, and being told beats being congratulated.
    /// </para>
    ///
    /// <para>
    /// Note this is <em>not</em> the same as the empty-list case above. Sending no ids is an organizer
    /// clearing the form; sending a zero GUID is a caller that lost an id somewhere.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_payload_of_zero_guids_is_refused_rather_than_silently_dropped()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var events = EventsOn(db);

        var groups = await events.AttachAudienceAsync(world.EventId, Groups(Guid.Empty));
        Assert.Equal(EventWriteOutcome.UnknownReference, groups.Outcome);
        Assert.Contains(Guid.Empty.ToString(), groups.Message, StringComparison.Ordinal);

        var students = await events.AttachAudienceAsync(world.EventId, Students(Guid.Empty));
        Assert.Equal(EventWriteOutcome.UnknownReference, students.Outcome);

        // A zero GUID alongside a real id takes the whole payload with it, like any unknown reference.
        var mixed = await events.AttachAudienceAsync(world.EventId, Groups(world.GroupA, Guid.Empty));
        Assert.Equal(EventWriteOutcome.UnknownReference, mixed.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.EventGroups.CountAsync());
    }

    // ----------------------------------------------------------------------- refusals

    /// <summary>
    /// Cross-tenant. Refused outright rather than warned: an audience is the one place a foreign row
    /// would be laundered into a denominator, and the §11 query filter is inert whenever no tenant is
    /// pinned — which is this test.
    /// </summary>
    [Fact]
    public async Task A_group_belonging_to_another_school_cannot_be_attached()
    {
        var world = await ArrangeAsync();

        Guid foreignGroupId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("CICSS");
            db.Schools.Add(other);
            var group = TestData.NewGroup(other.Id, "Other School Section");
            db.StudentGroups.Add(group);
            await db.SaveChangesAsync();
            foreignGroupId = group.Id;
        }

        await using var write = NewDbContext();
        var response = await EventsOn(write).AttachAudienceAsync(
            world.EventId, Groups(foreignGroupId));

        Assert.Equal(EventWriteOutcome.UnknownReference, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.EventGroups.CountAsync());
    }

    [Fact]
    public async Task A_student_belonging_to_another_school_cannot_be_attached()
    {
        var world = await ArrangeAsync();

        Guid foreignStudentId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("CICSS");
            db.Schools.Add(other);
            var student = TestData.NewStudent(other.Id, "9999-0001");
            db.Students.Add(student);
            await db.SaveChangesAsync();
            foreignStudentId = student.Id;
        }

        await using var write = NewDbContext();
        var response = await EventsOn(write).AttachAudienceAsync(
            world.EventId, Students(foreignStudentId));

        Assert.Equal(EventWriteOutcome.UnknownReference, response.Outcome);
    }

    [Fact]
    public async Task An_unknown_id_is_refused_and_takes_the_whole_payload_with_it()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).AttachAudienceAsync(
            world.EventId, Groups(world.GroupA, Guid.NewGuid()));

        Assert.Equal(EventWriteOutcome.UnknownReference, response.Outcome);

        // All-or-nothing on purpose: attaching the half that resolved would leave the organizer with a
        // partially-applied selection and a 400, which is the worst of both.
        await using var read = NewDbContext();
        Assert.Equal(0, await read.EventGroups.CountAsync());
    }

    /// <summary>
    /// <c>AcademicKey.Unspecified</c>. The projection deliberately never creates this group — a union
    /// of every offering whose section cell was blank is not a cohort — so the row here is written by
    /// hand to prove the boundary refuses one that somehow exists.
    /// </summary>
    [Fact]
    public async Task The_unspecified_section_group_cannot_be_attached()
    {
        var world = await ArrangeAsync();

        Guid sentinelGroupId;
        await using (var db = NewDbContext())
        {
            var group = new StudentGroup
            {
                SchoolId = world.SchoolId,
                TermId = world.TermId,
                Name = $"{AcademicKey.Unspecified} (2025-2026-1)",
                Type = StudentGroupType.Section,
                SourceType = GroupSourceType.Derived,
                SourceEntityType = GroupSourceEntityType.Section,
                SourceKey = AcademicKey.Unspecified,
            };
            db.StudentGroups.Add(group);
            await db.SaveChangesAsync();
            sentinelGroupId = group.Id;
        }

        await using var write = NewDbContext();
        var response = await EventsOn(write).AttachAudienceAsync(
            world.EventId, Groups(sentinelGroupId));

        Assert.Equal(EventWriteOutcome.NotACohort, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.EventGroups.CountAsync());
    }

    /// <summary>
    /// And the projection still never produces one, so the guard above is defence in depth rather than
    /// the only thing standing in the way. Pinned here because the two would otherwise be free to drift.
    /// </summary>
    [Fact]
    public async Task The_projection_never_creates_an_unspecified_section_group()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            // A third offering with a blank section — 39 of the real file's rows look like this.
            var course = TestData.NewCourse(world.SchoolId, "CA  2", "Criminalistics 2");
            db.Courses.Add(course);
            var offering = TestData.NewOffering(world.TermId, course.Id, sectionName: null);
            db.CourseOfferings.Add(offering);
            db.Enrollments.Add(TestData.NewEnrollment(world.OnlyInA, offering.Id));
            await db.SaveChangesAsync();

            await ProjectionOn(db).SyncTermAsync(world.TermId);
        }

        await using var read = NewDbContext();
        Assert.Empty(await read.StudentGroups.AsNoTracking()
            .Where(g => g.SourceEntityType == GroupSourceEntityType.Section
                     && g.SourceKey == AcademicKey.Unspecified)
            .ToListAsync());
    }

    /// <summary>
    /// A group from another term <b>warns and attaches</b>. The reasoning is in
    /// <c>IEventService.AttachAudienceAsync</c>: <c>Events</c> has no <c>TermId</c>, so this can only be
    /// measured against a flag that moves under the event's feet, and refusing would make an unchanged
    /// request start failing the day the registrar advances the term.
    /// </summary>
    [Fact]
    public async Task A_group_from_another_term_warns_but_is_still_attached()
    {
        var world = await ArrangeAsync();

        Guid staleGroupId;
        await using (var db = NewDbContext())
        {
            // Last semester, and it is no longer the current one.
            var previous = TestData.NewTerm(world.SchoolId, "2024-2025-2", isCurrent: false);
            db.Terms.Add(previous);

            var course = await db.Courses.FirstAsync();
            var offering = TestData.NewOffering(previous.Id, course.Id, SectionA);
            db.CourseOfferings.Add(offering);
            db.Enrollments.Add(TestData.NewEnrollment(world.OnlyInB, offering.Id));
            await db.SaveChangesAsync();

            await ProjectionOn(db).SyncTermAsync(previous.Id);

            staleGroupId = await db.StudentGroups
                .Where(g => g.TermId == previous.Id
                         && g.SourceEntityType == GroupSourceEntityType.Section)
                .Select(g => g.Id)
                .SingleAsync();
        }

        await using var write = NewDbContext();
        var response = await EventsOn(write).AttachAudienceAsync(world.EventId, Groups(staleGroupId));

        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        Assert.Equal(1, response.Result!.GroupsAttached);
        Assert.Single(response.Result.Warnings);
        Assert.Contains("term", response.Result.Warnings[0], StringComparison.OrdinalIgnoreCase);

        // It really did attach — a warning that silently dropped the row would be the worst of both.
        await using var read = NewDbContext();
        Assert.Equal(1, await read.EventGroups.CountAsync());
    }

    /// <summary>The current term's own groups do not warn, or the warning would mean nothing.</summary>
    [Fact]
    public async Task A_group_from_the_current_term_produces_no_warning()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));

        Assert.Empty(response.Result!.Warnings);
    }

    // ----------------------------------------------------------------------- detaching

    [Fact]
    public async Task A_detached_group_leaves_the_denominator()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA, world.GroupB));

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).DetachGroupAsync(world.EventId, world.GroupB);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
            Assert.Equal(2, response.Result!.Expected);
        }

        await using var read = NewDbContext();
        Assert.Equal(2, (await EventsOn(read).GetRosterAsync(world.EventId))!.Expected);
        Assert.Equal(1, await read.EventGroups.CountAsync());
    }

    /// <summary>
    /// Detaching something that is not attached succeeds. The postcondition holds either way, so a
    /// retried DELETE is safe; a missing <em>event</em> is still a 404 because that one is in the URL.
    /// </summary>
    [Fact]
    public async Task Detaching_something_that_is_not_attached_succeeds_but_an_unknown_event_does_not()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var events = EventsOn(db);

        Assert.Equal(EventWriteOutcome.Saved,
            (await events.DetachGroupAsync(world.EventId, Guid.NewGuid())).Outcome);
        Assert.Equal(EventWriteOutcome.Saved,
            (await events.DetachStudentAsync(world.EventId, Guid.NewGuid())).Outcome);
        Assert.Equal(EventWriteOutcome.NotFound,
            (await events.DetachGroupAsync(Guid.NewGuid(), world.GroupA)).Outcome);
    }

    [Fact]
    public async Task An_individually_attached_student_can_be_detached()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await EventsOn(db).AttachAudienceAsync(world.EventId, Students(world.OnlyInA, world.OnlyInB));

        await using (var db = NewDbContext())
            await EventsOn(db).DetachStudentAsync(world.EventId, world.OnlyInB);

        await using var read = NewDbContext();
        var roster = await EventsOn(read).GetRosterAsync(world.EventId);

        Assert.Equal(1, roster!.Expected);
        Assert.DoesNotContain(roster.Entries, e => e.StudentId == world.OnlyInB);
    }

    /// <summary>
    /// The audience is live in <c>Draft</c> and <c>Open</c> and locked in the two terminal states.
    /// <c>Closed</c> because the roster is materialized; <c>Cancelled</c> because who was invited to an
    /// event that did not happen is a historical fact rather than a working list.
    /// </summary>
    [Theory]
    [InlineData(EventStatus.Closed)]
    [InlineData(EventStatus.Cancelled)]
    public async Task A_terminal_events_audience_cannot_be_changed(string status)
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));
            await EventsOn(db).ChangeStatusAsync(world.EventId, status);
        }

        await using var write = NewDbContext();
        var events = EventsOn(write);

        Assert.Equal(EventWriteOutcome.EventLocked,
            (await events.AttachAudienceAsync(world.EventId, Groups(world.GroupB))).Outcome);
        Assert.Equal(EventWriteOutcome.EventLocked,
            (await events.DetachGroupAsync(world.EventId, world.GroupA)).Outcome);

        await using var read = NewDbContext();

        // The row count is deliberately not asserted, because it legitimately differs between the two
        // statuses: closing snapshots the resolved audience as individual §4.8 rows (that snapshot is
        // what freezes the denominator), while cancelling writes nothing. What must hold for both is
        // the same two facts.
        Assert.True(await read.EventGroups.AnyAsync(eg => eg.StudentGroupId == world.GroupA),
            "The group attachment is the record of which section was invited and must survive.");
        Assert.Equal(2, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);
    }

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    public async Task A_live_events_audience_can_be_changed(string status)
    {
        var world = await ArrangeAsync(status);

        await using var db = NewDbContext();
        Assert.Equal(EventWriteOutcome.Saved,
            (await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA))).Outcome);
    }

    // ----------------------------------------------------------------------- the roster

    /// <summary>
    /// <b>The audience is live while the event is.</b> A student enrolled by a roster import after the
    /// event was created — but before it happened — is correctly expected, with nobody having to
    /// re-attach anything. This is the property the freeze later takes away on purpose.
    /// </summary>
    [Fact]
    public async Task A_student_enrolled_after_the_audience_was_attached_is_still_expected()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));
            Assert.Equal(2, response.Result!.Expected);
        }

        Guid latecomer;
        await using (var db = NewDbContext())
        {
            var student = TestData.NewStudent(world.SchoolId, "2023-0099", lastName: "Late");
            db.Students.Add(student);

            var offering = await db.CourseOfferings
                .FirstAsync(o => o.SectionKey == AcademicKey.NormalizeOrUnspecified(SectionA));
            db.Enrollments.Add(TestData.NewEnrollment(student.Id, offering.Id));
            await db.SaveChangesAsync();

            latecomer = student.Id;
            await ProjectionOn(db).SyncTermAsync(world.TermId);
        }

        await using var read = NewDbContext();
        var roster = await EventsOn(read).GetRosterAsync(world.EventId);

        Assert.Equal(3, roster!.Expected);
        Assert.Contains(roster.Entries, e => e.StudentId == latecomer && e.IsExpected);
        Assert.False(roster.IsFrozen);
    }

    /// <summary>
    /// A soft-deleted student is not expected — the same reasoning that closed <c>KnownDefectTests</c>
    /// DEFECT 1 on the capture path, applied to the denominator so the two cannot disagree about who
    /// counts.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_student_is_not_an_expected_attendee()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));

        await using (var db = NewDbContext())
        {
            var student = await db.Students.SingleAsync(s => s.Id == world.OnlyInA);
            student.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        Assert.Equal(1, (await EventsOn(read).GetRosterAsync(world.EventId))!.Expected);
        Assert.Equal(1, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);
    }

    /// <summary>
    /// The absentee list §12 asks for, before any close: expected students with no record at all.
    /// </summary>
    [Fact]
    public async Task An_expected_student_with_no_record_is_listed_with_a_null_status()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId,
                EventId = world.EventId, StudentId = world.OnlyInA,
                CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
            });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var roster = await EventsOn(read).GetRosterAsync(world.EventId);

        Assert.Equal(2, roster!.Expected);
        Assert.Equal(1, roster.Present);
        Assert.Equal(1, roster.NotRecorded);
        Assert.Equal(0, roster.Absent);

        var absentee = Assert.Single(roster.Entries, e => e.Status is null);
        Assert.Equal(world.InBoth, absentee.StudentId);
        Assert.True(absentee.IsExpected);

        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);
        Assert.Equal(50, summary!.AttendanceRate);
    }

    /// <summary>
    /// A walk-in: recorded but never invited. Listed rather than hidden, because the alternative is a
    /// roster whose lines do not add up to the summary printed beside it.
    /// </summary>
    [Fact]
    public async Task A_student_who_taps_without_being_invited_is_listed_but_not_counted_as_expected()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId,
                EventId = world.EventId, StudentId = world.OnlyInB,
                CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
            });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var roster = await EventsOn(read).GetRosterAsync(world.EventId);

        Assert.Equal(2, roster!.Expected);
        Assert.Equal(3, roster.Entries.Count);

        var walkIn = Assert.Single(roster.Entries, e => e.StudentId == world.OnlyInB);
        Assert.False(walkIn.IsExpected);
        Assert.Equal(AttendanceStatus.Present, walkIn.Status);

        // The roster's buckets and the summary's are the same rows, counted once.
        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);
        Assert.Equal(roster.Present, summary!.Present);
        Assert.Equal(roster.Expected, summary.Expected);
        Assert.Equal(roster.Unexpected, summary.Unexpected);
    }

    /// <summary>
    /// <b>A walk-in cannot push the attendance rate above 100, and is still visible in the totals.</b>
    ///
    /// <para>
    /// The rate used to divide every <c>Present</c> and <c>Late</c> row — walk-ins included — by a
    /// denominator counting only the invited, so two tapping alumni on a fully-attended event of 29
    /// produced 106.9%. Every underlying row was truthful and the headline number was impossible, which
    /// is the same class of defect this phase existed to remove, arriving from the other direction.
    /// </para>
    ///
    /// <para>
    /// The signal is not discarded, it is moved somewhere that can carry it: <c>Unexpected</c> says how
    /// many turned up uninvited, and the roster names them. Folding them into the rate said "something
    /// is off" while corrupting the one number an operator reads first.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_walk_in_cannot_push_the_attendance_rate_above_one_hundred()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            // Everybody invited attends: the two students of section A.
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));

            db.AttendanceRecords.AddRange(
                new AttendanceRecord
                {
                    SchoolId = world.SchoolId,
                    EventId = world.EventId, StudentId = world.OnlyInA,
                    CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
                },
                new AttendanceRecord
                {
                    SchoolId = world.SchoolId,
                    EventId = world.EventId, StudentId = world.InBoth,
                    CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
                },
                // And one who was never invited taps anyway — the alumnus at the turnstile.
                new AttendanceRecord
                {
                    SchoolId = world.SchoolId,
                    EventId = world.EventId, StudentId = world.OnlyInB,
                    CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
                });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);

        Assert.Equal(2, summary!.Expected);
        // Three rows exist and the buckets still count all three, so the summary keeps reconciling
        // with GET /attendance?eventId=. It is the rate that narrowed, not the evidence.
        Assert.Equal(3, summary.Present);
        // Two of two invited students attended. Before the fix this read 150.
        Assert.Equal(100, summary.AttendanceRate);
        // The walk-in is surfaced rather than folded in or dropped.
        Assert.Equal(1, summary.Unexpected);

        // And the roster agrees about the same event — it always computed the denominator correctly,
        // so the two used to disagree.
        var roster = await EventsOn(read).GetRosterAsync(world.EventId);
        Assert.Equal(summary.Expected, roster!.Expected);
        Assert.Equal(summary.Unexpected, roster.Unexpected);
        Assert.False(Assert.Single(roster.Entries, e => e.StudentId == world.OnlyInB).IsExpected);
    }

    /// <summary>
    /// A partially-attended event with a walk-in: the rate reflects only the invited, so a walk-in
    /// cannot inflate it at all — not merely "cannot take it past 100". One expected student of two
    /// attended, and a third person tapping does not make that 100%.
    /// </summary>
    [Fact]
    public async Task A_walk_in_does_not_inflate_a_partial_attendance_rate()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));
            db.AttendanceRecords.AddRange(
                new AttendanceRecord
                {
                    SchoolId = world.SchoolId,
                    EventId = world.EventId, StudentId = world.OnlyInA,
                    CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
                },
                new AttendanceRecord
                {
                    SchoolId = world.SchoolId,
                    EventId = world.EventId, StudentId = world.OnlyInB,
                    CheckInAt = TestData.Now, Status = AttendanceStatus.Late,
                });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);

        Assert.Equal(2, summary!.Expected);
        Assert.Equal(1, summary.Present);
        Assert.Equal(1, summary.Late);
        Assert.Equal(1, summary.Unexpected);
        // One invited attendee of two. The uninvited Late row is counted in the bucket and excluded
        // from the numerator; before the fix this read 100.
        Assert.Equal(50, summary.AttendanceRate);
    }

    /// <summary>
    /// The numerator counts <c>Late</c> as attendance, which is the §12 definition and must survive the
    /// narrowing. Pinned separately because an intersection written against <c>Present</c> alone would
    /// pass every test above.
    /// </summary>
    [Fact]
    public async Task An_invited_student_arriving_late_still_counts_as_attending()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId,
                EventId = world.EventId, StudentId = world.OnlyInA,
                CheckInAt = TestData.Now.AddMinutes(30), Status = AttendanceStatus.Late,
            });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);

        Assert.Equal(50, summary!.AttendanceRate);
        Assert.Equal(0, summary.Unexpected);
    }

    /// <summary>
    /// No audience means no denominator, and nothing falls back to the recorded-row count that GAP 6
    /// was about. Zero is the honest answer and the roster is where the explanation is: the tapping
    /// student is listed, flagged not expected.
    /// </summary>
    [Fact]
    public async Task An_event_with_no_audience_has_a_denominator_of_zero_rather_than_a_fabricated_one()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId,
                EventId = world.EventId, StudentId = world.OnlyInA,
                CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
            });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();

        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);
        Assert.Equal(0, summary!.Expected);
        Assert.Equal(1, summary.Present);
        Assert.Equal(0, summary.AttendanceRate);

        var roster = await EventsOn(read).GetRosterAsync(world.EventId);
        Assert.Equal(0, roster!.Expected);
        Assert.False(Assert.Single(roster.Entries).IsExpected);
    }

    /// <summary>
    /// The four §4.9 buckets, counted in SQL rather than by materializing every row. The rewrite that
    /// moved them there is the reason this asserts all four rather than trusting one.
    /// </summary>
    [Fact]
    public async Task The_summary_buckets_count_every_documented_status()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(
                world.EventId, Students(world.OnlyInA, world.OnlyInB, world.InBoth));

            db.AttendanceRecords.AddRange(
                new AttendanceRecord
                {
                    SchoolId = world.SchoolId,
                    EventId = world.EventId, StudentId = world.OnlyInA,
                    CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
                },
                new AttendanceRecord
                {
                    SchoolId = world.SchoolId,
                    EventId = world.EventId, StudentId = world.OnlyInB,
                    CheckInAt = TestData.Now.AddMinutes(30), Status = AttendanceStatus.Late,
                },
                new AttendanceRecord
                {
                    SchoolId = world.SchoolId,
                    EventId = world.EventId, StudentId = world.InBoth,
                    Status = AttendanceStatus.Excused, CaptureMethod = CaptureMethod.Manual,
                });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);

        Assert.Equal(3, summary!.Expected);
        Assert.Equal(1, summary.Present);
        Assert.Equal(1, summary.Late);
        Assert.Equal(0, summary.Absent);
        Assert.Equal(1, summary.Excused);
        // (Present + Late) / Expected.
        Assert.Equal(66.7, summary.AttendanceRate);
    }

    [Fact]
    public async Task An_unknown_event_has_no_roster()
    {
        await ArrangeAsync();

        await using var read = NewDbContext();
        Assert.Null(await EventsOn(read).GetRosterAsync(Guid.NewGuid()));
    }
}
