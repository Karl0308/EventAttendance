using System.Net;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>GET /events/{id}/attendees</c> — the read half of the audience write surface
/// <see cref="EventRosterTests"/> owns.
///
/// <para>
/// <b>The property under test is not "the join works".</b> It is that this endpoint cannot become a
/// second opinion about anything already published: its <c>expected</c> is the number the summary and
/// the roster report, and its <c>memberCount</c> is the number <c>GET /student-groups</c> reports for
/// the same group. Both are asserted by reading the other endpoint in the same test rather than by
/// hard-coding an integer — a hard-coded expectation passes just as happily when the two have drifted
/// apart, which is the failure ADR-003 D-19 spends a page on.
/// </para>
///
/// <para>
/// The audience is built through <see cref="StudentGroupProjection"/> for the reason
/// <see cref="EventRosterTests"/> gives: a hand-made group proves the join and nothing else.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventAudienceReadTests : IntegrationTest
{
    public EventAudienceReadTests(SqlServerFixture sql) : base(sql) { }

    private const string SectionA = "BSCRIM 2-A";
    private const string SectionB = "BSCRIM 2-B";

    private sealed record World(
        Guid SchoolId, Guid TermId, Guid EventId,
        Guid GroupA, Guid GroupB,
        Guid OnlyInA, Guid OnlyInB, Guid InBoth);

    /// <summary>
    /// Two sections and three students, one of them in both — so a test that attaches both sections is
    /// asserting against a de-duplicated denominator rather than a sum.
    /// </summary>
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
            var second = TestData.NewCourse(school.Id, "MS 32", "Military Science 32");
            db.Courses.AddRange(course, second);

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

    // --------------------------------------------------------------------------------- groups

    /// <summary>
    /// An attached group comes back with its name, its provenance, its term, and a member count that is
    /// <b>the same number the picker showed</b> — read from <c>GET /student-groups</c> in this test
    /// rather than written down here, because the failure worth catching is the two disagreeing, not
    /// either one being wrong in isolation.
    /// </summary>
    [Fact]
    public async Task An_attached_group_reports_the_member_count_the_group_list_reports()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));

        await using var read = NewDbContext();

        var audience = await EventsOn(read).GetAudienceAsync(world.EventId);
        var group = Assert.Single(audience!.Groups);

        Assert.Equal(world.GroupA, group.StudentGroupId);
        Assert.Contains(SectionA, group.Name, StringComparison.Ordinal);
        Assert.Equal(GroupSourceType.Derived, group.SourceType);
        Assert.Equal(world.TermId, group.TermId);
        Assert.Equal("2025-2026-1", group.TermCode);

        var listed = (await StudentGroupsOn(read).ListAsync(null, null, PageRequest.From(1, 50)))
            .Items.Single(g => g.Id == world.GroupA);

        Assert.Equal(listed.MemberCount, group.MemberCount);
        Assert.Equal(2, group.MemberCount);
    }

    /// <summary>
    /// And the half of "the same definition" that a naive <c>Members.Count</c> would get wrong. A
    /// soft-deleted student leaves the group's count in both places, together — an organizer comparing
    /// the picker with the attached panel must not see 2 in one and 1 in the other.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_member_leaves_the_member_count_in_both_places()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));

            var student = await db.Students.FirstAsync(s => s.Id == world.OnlyInA);
            student.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();

        var group = Assert.Single((await EventsOn(read).GetAudienceAsync(world.EventId))!.Groups);
        var listed = (await StudentGroupsOn(read).ListAsync(null, null, PageRequest.From(1, 50)))
            .Items.Single(g => g.Id == world.GroupA);

        Assert.Equal(1, group.MemberCount);
        Assert.Equal(listed.MemberCount, group.MemberCount);
    }

    // ------------------------------------------------------------------------------- students

    /// <summary>
    /// The live case: individually-attached students are the handful an organizer named by hand, and
    /// enumerating them is both cheap and the point — each id here is one the
    /// <c>DELETE .../attendees/students/{studentId}</c> sub-resource accepts.
    /// </summary>
    [Fact]
    public async Task An_individually_attached_student_is_listed_on_a_live_event()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest([world.GroupA], [world.OnlyInB]));
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        await using var read = NewDbContext();
        var audience = await EventsOn(read).GetAudienceAsync(world.EventId);

        Assert.False(audience!.IsFrozen);
        Assert.Equal(EventStatus.Open, audience.Status);

        var student = Assert.Single(audience.Students);
        Assert.Equal(world.OnlyInB, student.StudentId);
        Assert.Equal("2023-0002", student.StudentNumber);
        Assert.Equal("Maria Reyes Cruz", student.FullName);

        // The group member reached through GroupA is deliberately *not* here — that is the group's
        // membership, not an attachment, and listing it would make this response the roster.
        Assert.DoesNotContain(audience.Students, s => s.StudentId == world.OnlyInA);
    }

    /// <summary>
    /// <b>A soft-deleted individually-attached student stays in <c>Students</c> and leaves
    /// <c>Expected</c>, and the divergence <em>is</em> the contract.</b>
    ///
    /// <para>
    /// This query deliberately carries no <c>IsDeleted</c> filter while nearly every other read in the
    /// system does, so <i>"why does this one not filter soft-deleted students?"</i> is a reasonable
    /// review comment with a wrong answer — the same shape ADR-003 D-17 flags about adding a status
    /// guard to <c>ManualAsync</c>. The answer: this list is the <em>attachment</em>, not the
    /// denominator. The §4.8 row exists and <c>DELETE .../attendees/students/{studentId}</c> addresses
    /// it, so filtering it out would leave a row no client can see and no organizer can remove, while a
    /// re-post naming that student would report it already attached against a panel showing nothing.
    /// </para>
    ///
    /// <para>
    /// <c>Expected</c> excludes them on a live event under D-15 — a student the roster says does not
    /// exist cannot be expected to attend — so the two numbers legitimately differ. Both halves are
    /// asserted together because either one alone is satisfied by the wrong implementation: filtering
    /// the list keeps <c>Expected</c> right, and dropping D-15's filter keeps the list right.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_attached_student_is_still_listed_but_is_not_expected()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            // GroupA (OnlyInA + InBoth) plus OnlyInB by hand — three expected attendees.
            var response = await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest([world.GroupA], [world.OnlyInB]));
            Assert.Equal(3, response.Result!.Expected);

            var student = await db.Students.FirstAsync(s => s.Id == world.OnlyInB);
            student.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var audience = await EventsOn(read).GetAudienceAsync(world.EventId);

        Assert.False(audience!.IsFrozen);

        // Still attached, still detachable, still shown.
        var listed = Assert.Single(audience.Students);
        Assert.Equal(world.OnlyInB, listed.StudentId);

        // And no longer expected — the denominator dropped to GroupA's two members.
        Assert.Equal(2, audience.Expected);

        // The divergence stated as the assertion it is: one student is attached and not counted.
        Assert.Equal(3, audience.Groups.Sum(g => g.MemberCount) + audience.Students.Count);
        Assert.NotEqual(audience.Groups.Sum(g => g.MemberCount) + audience.Students.Count,
            audience.Expected);

        // And the shared denominator agrees, so this is D-15 working rather than this endpoint
        // disagreeing with the two that already publish the number.
        Assert.Equal((await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected, audience.Expected);
        Assert.Equal((await EventsOn(read).GetRosterAsync(world.EventId))!.Expected, audience.Expected);
    }

    // ------------------------------------------------------------------------------- the freeze

    /// <summary>
    /// <b>ADR-003 D-13's historical record, asserted on the wire.</b> Closing writes the whole resolved
    /// audience down as individual §4.8 rows and keeps the group rows; the group rows are the only
    /// answer to "which cohort was invited?" once nothing resolves them. A freeze that dropped them
    /// would break no number, which is exactly why this needs a test rather than a comment.
    ///
    /// <para>
    /// The paired assertion is the one this endpoint's contract turns on: <c>Students</c> is empty and
    /// <c>IsFrozen</c> is what says why. There are three frozen student rows in the table underneath —
    /// asserted directly, so "empty" is proven to be the endpoint's contract rather than an audience
    /// that never got written.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_closed_events_groups_still_name_the_invited_section_and_its_students_are_empty()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA, world.GroupB));

            var closed = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, closed.Outcome);
        }

        await using var read = NewDbContext();
        var audience = await EventsOn(read).GetAudienceAsync(world.EventId);

        Assert.True(audience!.IsFrozen);
        Assert.Equal(EventStatus.Closed, audience.Status);

        Assert.Equal(
            new[] { world.GroupA, world.GroupB }.OrderBy(g => g),
            audience.Groups.Select(g => g.StudentGroupId).OrderBy(g => g));

        Assert.Empty(audience.Students);

        // The snapshot exists — so the empty list above is the contract, not a missing write.
        Assert.Equal(3, await read.EventGroups.CountAsync(eg => eg.EventId == world.EventId
                                                            && eg.StudentId != null));
        Assert.Equal(3, audience.Expected);
    }

    /// <summary>
    /// <c>Cancelled</c> is the other terminal status, and ADR-003 D-16 is the record of it having been
    /// missed once already. Its audience is snapshotted too, so <c>IsFrozen</c> is true and
    /// <c>Students</c> is empty for exactly the same reason — a flag keyed to <c>Closed</c> alone would
    /// pass every assertion above and fail here.
    /// </summary>
    [Fact]
    public async Task A_cancelled_event_is_frozen_too()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Cancelled);
        }

        await using var read = NewDbContext();
        var audience = await EventsOn(read).GetAudienceAsync(world.EventId);

        Assert.True(audience!.IsFrozen);
        Assert.Empty(audience.Students);
        Assert.Single(audience.Groups);
        Assert.Equal(2, audience.Expected);
    }

    // ----------------------------------------------------------------------------- the denominator

    /// <summary>
    /// <b>The reason <c>Expected</c> is not computed here.</b> Four endpoints now publish this number;
    /// ADR-003 D-19 records that a second implementation of it drifts while every copy stays plausible.
    /// Asserted against the summary on a live event and again after the close, because the denominator
    /// is answered by two different queries on either side of that line.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_is_the_number_the_summary_reports(bool close)
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest([world.GroupA], [world.OnlyInB]));

            if (close) await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
        }

        await using var read = NewDbContext();

        var audience = await EventsOn(read).GetAudienceAsync(world.EventId);
        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);
        var roster = await EventsOn(read).GetRosterAsync(world.EventId);

        Assert.Equal(3, audience!.Expected);
        Assert.Equal(summary!.Expected, audience.Expected);
        Assert.Equal(roster!.Expected, audience.Expected);
        Assert.Equal(roster.IsFrozen, audience.IsFrozen);
    }

    /// <summary>An event with nothing attached is an empty audience, not a 404 and not a null field.</summary>
    [Fact]
    public async Task An_event_with_no_audience_reports_empty_lists_and_a_zero_denominator()
    {
        var world = await ArrangeAsync();

        await using var read = NewDbContext();
        var audience = await EventsOn(read).GetAudienceAsync(world.EventId);

        Assert.Empty(audience!.Groups);
        Assert.Empty(audience.Students);
        Assert.Equal(0, audience.Expected);
    }

    [Fact]
    public async Task An_unknown_event_has_no_audience()
    {
        await using var db = NewDbContext();
        Assert.Null(await EventsOn(db).GetAudienceAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// A soft-deleted event is invisible to this read for the same reason it is to every other one:
    /// 404 rather than an audience for a row the caller cannot otherwise see.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_event_has_no_audience()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(world.EventId, Groups(world.GroupA));
            await EventsOn(db).DeleteAsync(world.EventId);
        }

        await using var read = NewDbContext();
        Assert.Null(await EventsOn(read).GetAudienceAsync(world.EventId));
    }

    // -------------------------------------------------------------------------------- tenancy

    /// <summary>
    /// <b>The one security control in this endpoint, exercised by writing the row it exists to stop.</b>
    ///
    /// <para>
    /// <c>EventGroups</c> carries no <c>SchoolId</c> and reaches tenancy through <c>Event</c> (ADR-003
    /// D-12), and the §11 global filter is inert whenever no tenant is pinned — design time, most of
    /// this suite, any unseeded start. So <c>GetAudienceAsync</c> predicates <c>SchoolId</c> explicitly
    /// on both joins, exactly as <c>AttachAudienceAsync</c> does. <c>POST /events/{id}/attendees</c>
    /// refuses a cross-school reference, so the row below is one only a hand-write can produce — which
    /// is precisely the argument for writing it here rather than trusting the prose.
    /// </para>
    ///
    /// <para>
    /// <b>Without this test the predicates are complete in code and absent from the suite</b>, which is
    /// the shape ADR-003's Consequences section names as the phase's recurring failure: a rule that
    /// exists correctly in prose and incompletely in code, surviving a green run. Drop either predicate
    /// and every other test in this file still passes; this one turns that into a build failure. What
    /// leaks without them is another school's group names and another school's students' names and
    /// numbers, through a route with no way to know it is doing so.
    /// </para>
    ///
    /// <para>
    /// <b><c>Expected</c> is deliberately not asserted here, and the reason is worth reading.</b> It
    /// comes from <c>ExpectedStudentIds</c> — the one denominator shared with <c>GET /summary</c> and
    /// <c>GET /roster</c> — which carries no such predicate, so the foreign student <em>does</em> reach
    /// it. That is not this endpoint's behaviour to assert or to fix: pinning a number here would either
    /// bless it or fail for a reason that has nothing to do with the control under test. It is reported
    /// as a finding instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_hand_written_cross_school_row_is_not_disclosed()
    {
        var world = await ArrangeAsync();

        Guid foreignGroupId, foreignStudentId;

        await using (var db = NewDbContext())
        {
            // A second tenant, entire and legitimate — its own school, its own group, its own student.
            var other = TestData.NewSchool("OTH");
            db.Schools.Add(other);

            var group = TestData.NewGroup(other.Id, "OTHER SCHOOL 1-A");
            db.StudentGroups.Add(group);

            var student = TestData.NewStudent(
                other.Id, "9999-0001", firstName: "Jose", middleName: null, lastName: "Rizal");
            db.Students.Add(student);

            await db.SaveChangesAsync();

            foreignGroupId = group.Id;
            foreignStudentId = student.Id;

            // The rows POST /events/{id}/attendees refuses to write, written anyway. Attached to an
            // event belonging to the *first* school.
            db.EventGroups.AddRange(
                new EventGroup { EventId = world.EventId, StudentGroupId = foreignGroupId },
                new EventGroup { EventId = world.EventId, StudentId = foreignStudentId });

            await db.SaveChangesAsync();
        }

        // The rows really are there — so "empty" below is the predicate working, not an arrange that
        // silently wrote nothing.
        await using var read = NewDbContext();
        Assert.Equal(2, await read.EventGroups.CountAsync(eg => eg.EventId == world.EventId));

        var audience = await EventsOn(read).GetAudienceAsync(world.EventId);

        Assert.Empty(audience!.Groups);
        Assert.Empty(audience.Students);
    }

    // ------------------------------------------------------------------------------- over HTTP

    /// <summary>
    /// The JSON property names the SPA binds, and the 404. Service-level tests own the behaviour; this
    /// owns the parts invisible from there — <c>EventsApiTests</c> makes the same argument.
    /// </summary>
    [Fact]
    public async Task The_endpoint_serves_the_audience_as_camel_cased_json()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest([world.GroupA], [world.OnlyInB]));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/events/{world.EventId}/attendees");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(world.EventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal(EventStatus.Open, root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(3, root.GetProperty("expected").GetInt32());

        var group = Assert.Single(root.GetProperty("groups").EnumerateArray().ToList());
        Assert.Equal(world.GroupA, group.GetProperty("studentGroupId").GetGuid());
        Assert.Equal(2, group.GetProperty("memberCount").GetInt32());

        var student = Assert.Single(root.GetProperty("students").EnumerateArray().ToList());
        Assert.Equal(world.OnlyInB, student.GetProperty("studentId").GetGuid());
        Assert.Equal("Maria Reyes Cruz", student.GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task An_unknown_event_is_404_over_http()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/events/{Guid.NewGuid()}/attendees");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
