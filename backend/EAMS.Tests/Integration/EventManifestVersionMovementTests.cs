using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// What moves the D-46 manifest version and what must not — the half of the ETag contract that only a
/// real database can answer.
///
/// <para>
/// <b>Every case here is a write, not a constructed DTO.</b> The unit suite already proves the hash is
/// sensitive to each published field; what it cannot prove is that a given <em>write path</em> reaches
/// those fields at all. The two failure modes this file exists for are exactly that gap: a change the
/// version does not notice (a device 304s forever against a manifest that no longer describes the
/// event — silent, and only discovered when a student's card stops resolving on the door) and a
/// non-change the version does notice (every device re-downloads the roster on every pull, which is
/// invisible until it is a data bill).
/// </para>
///
/// <para>
/// Through <see cref="IEventService"/> rather than over HTTP: the version is the same value either way
/// — <c>EventManifestTests</c> proves the body and the <c>ETag</c> carry it — and a service call keeps
/// each case to one write and two reads, which is what makes twenty of them affordable.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventManifestVersionMovementTests : IntegrationTest
{
    public EventManifestVersionMovementTests(SqlServerFixture sql) : base(sql) { }

    private sealed record World(
        Guid SchoolId, Guid EventId, Guid GroupA, Guid GroupB,
        Guid Spare, Guid Listed, Guid Individual, Guid Outsider);

    /// <summary>
    /// <b>Two attached sections with a student in both, and that overlap is the load-bearing part of
    /// the fixture.</b> It is what lets a membership change be made whose attendee <em>count</em> does
    /// not move — see
    /// <see cref="A_bulk_membership_write_that_touches_no_timestamp_still_moves_the_version"/>, which
    /// is worthless without it.
    ///
    /// <para>
    /// Plus a hand-attached student, a spare unattached section, and a student outside the audience
    /// entirely, so the "must not move" cases have something real to change that the manifest genuinely
    /// does not publish.
    /// </para>
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
        db.Schools.Add(school);

        var listed = TestData.NewStudent(school.Id, "2023-0001", lastName: "Santos");
        var individual = TestData.NewStudent(school.Id, "2023-0002", lastName: "Cruz");
        var outsider = TestData.NewStudent(school.Id, "2023-0003", lastName: "Outside");
        db.Students.AddRange(listed, individual, outsider);

        db.RfidCards.Add(TestData.NewCard(school.Id, listed.Id, "0012503301"));

        var groupA = TestData.NewGroup(school.Id, "BSCRIM 2-A");
        var groupB = TestData.NewGroup(school.Id, "BSCRIM 2-B");
        var spare = TestData.NewGroup(school.Id, "BSIT 4-C");
        db.StudentGroups.AddRange(groupA, groupB, spare);

        db.StudentGroupMembers.AddRange(
            new StudentGroupMember { StudentGroupId = groupA.Id, StudentId = listed.Id },
            new StudentGroupMember { StudentGroupId = groupB.Id, StudentId = listed.Id },
            new StudentGroupMember { StudentGroupId = spare.Id, StudentId = outsider.Id });

        // Server-stamped taps land in the "must not move" half, so the D-36 window has to contain the
        // real clock.
        var ev = TestData.NewLiveEvent(school.Id);
        db.Events.Add(ev);

        db.EventGroups.AddRange(
            new EventGroup { EventId = ev.Id, StudentGroupId = groupA.Id },
            new EventGroup { EventId = ev.Id, StudentGroupId = groupB.Id },
            new EventGroup { EventId = ev.Id, StudentId = individual.Id });

        await db.SaveChangesAsync();

        return new World(
            school.Id, ev.Id, groupA.Id, groupB.Id, spare.Id,
            listed.Id, individual.Id, outsider.Id);
    }

    private async Task<string> VersionAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        var response = await EventsOn(db).GetManifestAsync(eventId);

        Assert.Equal(ManifestOutcome.Ok, response.Outcome);
        return response.Manifest!.Version;
    }

    /// <summary>
    /// Runs <paramref name="write"/> between two pulls and asserts the version moved, naming the
    /// consequence rather than the values — a diff of two 25-character opaque strings tells a reader
    /// nothing about what broke.
    /// </summary>
    private async Task AssertMovesAsync(Guid eventId, string what, Func<Task> write)
    {
        var before = await VersionAsync(eventId);
        await write();
        var after = await VersionAsync(eventId);

        Assert.True(before != after,
            $"{what} did not move the manifest version. Every device holding the old version will " +
            "304 against it forever — the change reaches the server and never reaches the door.");
    }

    private async Task AssertDoesNotMoveAsync(Guid eventId, string what, Func<Task> write)
    {
        var before = await VersionAsync(eventId);
        await write();
        var after = await VersionAsync(eventId);

        Assert.True(before == after,
            $"{what} moved the manifest version, and it publishes nothing. Every device re-downloads " +
            "the whole roster on its next pull, and nothing anywhere reports an error.");
    }

    // ------------------------------------------------------------- the write with no timestamp at all

    /// <summary>
    /// <b>The case the rejected <c>max(UpdatedAt)</c>-plus-row-counts watermark could not have seen,
    /// and the reason the version is a content hash.</b> A student is added to an attached section by a
    /// bulk SQL insert — the shape of the SIS import — and afterwards <em>none</em> of the three things
    /// a watermark can observe has changed:
    ///
    /// <list type="number">
    ///   <item>no <c>UpdatedAt</c> moved, because <c>StudentGroupMembers</c> is a junction and carries
    ///   no timestamp column at all (§4.7), and nothing went through the change tracker;</item>
    ///   <item>the attendee count did not move, because the student was <em>already</em> in the
    ///   audience — hand-attached through <c>EventGroups.StudentId</c>;</item>
    ///   <item>the group count did not move either.</item>
    /// </list>
    ///
    /// <para>
    /// What did change is the student's <c>groupIds</c>, which is published — so a device filtering by
    /// section shows them under the wrong one, or under none, indefinitely. All three watermark inputs
    /// are captured before and after and asserted <b>unchanged</b>, so this test proves the hole is
    /// real rather than merely that the hash works: it is the failing half of the negative control,
    /// written down.
    /// </para>
    ///
    /// <para>
    /// <b>The count-invariance is not incidental and must not be "simplified" away.</b> An earlier
    /// draft of this test added a brand-new student instead, and it passed against a watermark
    /// implementation — the row count moved and covered for it. A test that stays green when you break
    /// the thing it claims to protect is worse than no test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_bulk_membership_write_that_touches_no_timestamp_still_moves_the_version()
    {
        var world = await ArrangeAsync();

        var watermarkBefore = await WatermarkAsync();
        var (attendeesBefore, groupsBefore) = await CountsAsync(world.EventId);
        var before = await VersionAsync(world.EventId);

        await using (var db = NewDbContext())
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO StudentGroupMembers (Id, StudentGroupId, StudentId, SourceType)
                 VALUES ({Guid.NewGuid()}, {world.GroupA}, {world.Individual}, 'Manual')
                 """);
        }

        var after = await VersionAsync(world.EventId);
        var (attendeesAfter, groupsAfter) = await CountsAsync(world.EventId);

        Assert.Equal(watermarkBefore, await WatermarkAsync());
        Assert.Equal(attendeesBefore, attendeesAfter);
        Assert.Equal(groupsBefore, groupsAfter);

        Assert.True(before != after,
            "A student was added to an attached section by a bulk SQL write and the manifest version " +
            "did not move — while max(UpdatedAt), the attendee count and the group count all stayed " +
            "exactly where they were, which is the whole point. A watermark cannot see this write. " +
            "Every device would 304 forever against a manifest that files the student under the " +
            "wrong section.");

        await using var read = NewDbContext();
        var manifest = (await EventsOn(read).GetManifestAsync(world.EventId)).Manifest!;
        var attendee = Assert.Single(manifest.Attendees, a => a.StudentId == world.Individual);
        Assert.Contains(world.GroupA, attendee.GroupIds);
    }

    /// <summary>
    /// The same hole in the other direction, and the harder half: <b>a delete.</b>
    /// <c>max(UpdatedAt)</c> never decreases, and removing a student from a section deletes the row
    /// that would have carried the timestamp — so there is nothing left to notice.
    ///
    /// <para>
    /// Count-invariant for the same reason as above: the student sits in both attached sections, so
    /// dropping one membership takes a <c>groupIds</c> entry away and leaves the denominator alone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_bulk_membership_delete_still_moves_the_version()
    {
        var world = await ArrangeAsync();

        var watermarkBefore = await WatermarkAsync();
        var (attendeesBefore, groupsBefore) = await CountsAsync(world.EventId);
        var before = await VersionAsync(world.EventId);

        await using (var db = NewDbContext())
        {
            var deleted = await db.Database.ExecuteSqlAsync(
                $"""
                 DELETE FROM StudentGroupMembers
                 WHERE StudentGroupId = {world.GroupB} AND StudentId = {world.Listed}
                 """);

            Assert.Equal(1, deleted);
        }

        var after = await VersionAsync(world.EventId);
        var (attendeesAfter, groupsAfter) = await CountsAsync(world.EventId);

        Assert.Equal(watermarkBefore, await WatermarkAsync());
        Assert.Equal(attendeesBefore, attendeesAfter);
        Assert.Equal(groupsBefore, groupsAfter);

        Assert.True(before != after,
            "A student was removed from an attached section and the manifest version did not move, " +
            "with every watermark input unchanged. This is the delete case: the row that carried the " +
            "timestamp is the row that went away, so no watermark can ever see it.");
    }

    /// <summary>
    /// The watermark the rejected design would have used: the newest <c>UpdatedAt</c> across the two
    /// audited tables the manifest reads from. Captured only so the tests above can assert it did
    /// <em>not</em> move — it is not used to decide anything.
    /// </summary>
    private async Task<DateTime?> WatermarkAsync()
    {
        await using var db = NewDbContext();

        var students = await db.Students.MaxAsync(s => (DateTime?)s.UpdatedAt);
        var events = await db.Events.MaxAsync(e => (DateTime?)e.UpdatedAt);

        return students > events ? students : events;
    }

    /// <summary>The other half of the rejected watermark: the row counts it would have composed in.</summary>
    private async Task<(int Attendees, int Groups)> CountsAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        var manifest = (await EventsOn(db).GetManifestAsync(eventId)).Manifest!;
        return (manifest.Attendees.Count, manifest.Groups.Count);
    }

    // ------------------------------------------------------------------------------ must move it

    /// <summary>
    /// A card issued to a listed student. <b>Touches <c>RfidCards</c> and never <c>Students</c></b> —
    /// the second write path a watermark over the student table would miss, and the one whose symptom
    /// is a brand-new card that never resolves offline.
    /// </summary>
    [Fact]
    public async Task Issuing_a_card_to_a_listed_student_moves_the_version()
    {
        var world = await ArrangeAsync();

        await AssertMovesAsync(world.EventId, "Issuing a card", async () =>
        {
            await using var db = NewDbContext();
            var issued = await StudentsOn(db).AddCardAsync(
                world.Listed, new StudentCardRequest("0012509999", "Replacement"));
            Assert.Equal(StudentWriteOutcome.Saved, issued.Outcome);
        });
    }

    /// <summary>
    /// Deactivating a card. A deactivated card must stop resolving on the device the moment it stops
    /// resolving on the server, or a card revoked because it was lost keeps opening the door offline.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_card_moves_the_version()
    {
        var world = await ArrangeAsync();

        Guid cardId;
        await using (var db = NewDbContext())
            cardId = await db.RfidCards.Where(c => c.StudentId == world.Listed).Select(c => c.Id).FirstAsync();

        await AssertMovesAsync(world.EventId, "Deactivating a card", async () =>
        {
            await using var db = NewDbContext();
            var deactivated = await StudentsOn(db).DeactivateCardAsync(world.Listed, cardId);
            Assert.Equal(StudentWriteOutcome.Saved, deactivated.Outcome);
        });
    }

    [Fact]
    public async Task Attaching_a_group_moves_the_version()
    {
        var world = await ArrangeAsync();

        await AssertMovesAsync(world.EventId, "Attaching a group", async () =>
        {
            await using var db = NewDbContext();
            db.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentGroupId = world.Spare });
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Detaching_a_group_moves_the_version()
    {
        var world = await ArrangeAsync();

        await AssertMovesAsync(world.EventId, "Detaching a group", async () =>
        {
            await using var db = NewDbContext();
            var row = await db.EventGroups.FirstAsync(
                eg => eg.EventId == world.EventId && eg.StudentGroupId == world.GroupA);
            db.EventGroups.Remove(row);
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Renaming_an_attached_group_moves_the_version()
    {
        var world = await ArrangeAsync();

        await AssertMovesAsync(world.EventId, "Renaming an attached group", async () =>
        {
            await using var db = NewDbContext();
            var group = await db.StudentGroups.FindAsync(world.GroupA);
            group!.Name = "BSCRIM 2-A (renamed)";
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Attaching_a_student_individually_moves_the_version()
    {
        var world = await ArrangeAsync();

        await AssertMovesAsync(world.EventId, "Attaching a student", async () =>
        {
            await using var db = NewDbContext();
            db.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentId = world.Outsider });
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Detaching_an_individually_attached_student_moves_the_version()
    {
        var world = await ArrangeAsync();

        await AssertMovesAsync(world.EventId, "Detaching a student", async () =>
        {
            await using var db = NewDbContext();
            var row = await db.EventGroups.FirstAsync(
                eg => eg.EventId == world.EventId && eg.StudentId == world.Individual);
            db.EventGroups.Remove(row);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>
    /// A listed student's soft-delete. They leave the manifest, which is the same population change
    /// <c>/summary</c> and <c>/roster</c> see (ADR-003 D-15).
    /// </summary>
    [Fact]
    public async Task Soft_deleting_a_listed_student_moves_the_version()
    {
        var world = await ArrangeAsync();

        await AssertMovesAsync(world.EventId, "Soft-deleting a listed student", async () =>
        {
            await using var db = NewDbContext();
            Assert.Equal(
                StudentWriteOutcome.Saved, (await StudentsOn(db).DeleteAsync(world.Listed)).Outcome);
        });
    }

    /// <summary>
    /// The two published student fields. Both are display-or-identity on the device, and a device
    /// holding a stale one shows the wrong name against a real tap.
    /// </summary>
    [Theory]
    [InlineData("studentNumber")]
    [InlineData("fullName")]
    public async Task Editing_a_listed_students_published_fields_moves_the_version(string field)
    {
        var world = await ArrangeAsync();

        await AssertMovesAsync(world.EventId, $"Editing {field}", async () =>
        {
            await using var db = NewDbContext();
            var student = await db.Students.FindAsync(world.Listed);

            if (field == "studentNumber") student!.StudentNumber = "2023-8888";
            else student!.LastName = "Santos-Reyes";

            await db.SaveChangesAsync();
        });
    }

    /// <summary>Every published event field, one write each.</summary>
    [Theory]
    [InlineData("name")]
    [InlineData("startAt")]
    [InlineData("endAt")]
    [InlineData("graceMinutes")]
    [InlineData("attendanceMode")]
    public async Task Editing_a_published_event_field_moves_the_version(string field)
    {
        var world = await ArrangeAsync();

        await AssertMovesAsync(world.EventId, $"Editing event.{field}", async () =>
        {
            await using var db = NewDbContext();
            var ev = await db.Events.FindAsync(world.EventId);

            switch (field)
            {
                case "name": ev!.Name = "Renamed Convocation"; break;
                case "startAt": ev!.StartAt = ev.StartAt.AddMinutes(-1); break;
                case "endAt": ev!.EndAt = ev.EndAt.AddMinutes(1); break;
                case "graceMinutes": ev!.GraceMinutes += 5; break;
                case "attendanceMode": ev!.AttendanceMode = AttendanceMode.TimeInOut; break;
                default: throw new ArgumentOutOfRangeException(nameof(field), field, "Unmapped field.");
            }

            await db.SaveChangesAsync();
        });
    }

    // -------------------------------------------------------------------------- must NOT move it

    /// <summary>
    /// <b>An attendance record must not move it.</b> The manifest is the invitation, not the roster —
    /// and this is the highest-frequency write in the system. A version that moved on every tap would
    /// make the conditional GET worthless precisely when the network is busiest, and would do it
    /// silently.
    /// </summary>
    [Fact]
    public async Task Recording_a_tap_does_not_move_the_version()
    {
        var world = await ArrangeAsync();

        await AssertDoesNotMoveAsync(world.EventId, "Recording a tap", async () =>
        {
            await using var db = NewDbContext();
            var tap = await AttendanceOn(db).TapAsync(
                new TapRequest(world.EventId, "0012503301", null, "manifest-tap-0001", null));

            Assert.True(
                tap.Outcome is TapOutcome.Recorded,
                $"The arrange did not produce a recordable tap ({tap.Outcome}: {tap.Result.Message}). " +
                "This test asserts a non-change and would pass vacuously if nothing were written.");
        });
    }

    /// <summary>
    /// A student outside the audience, edited. Nothing about them is published, so nothing about them
    /// may move the version — otherwise a roster import in an unrelated programme re-downloads every
    /// device's manifest.
    /// </summary>
    [Fact]
    public async Task Editing_a_student_outside_the_audience_does_not_move_the_version()
    {
        var world = await ArrangeAsync();

        await AssertDoesNotMoveAsync(world.EventId, "Editing a student outside the audience", async () =>
        {
            await using var db = NewDbContext();
            var student = await db.Students.FindAsync(world.Outsider);
            student!.LastName = "Renamed";
            student.StudentNumber = "2023-7777";
            await db.SaveChangesAsync();

            db.RfidCards.Add(TestData.NewCard(world.SchoolId, world.Outsider, "0099999999"));
            await db.SaveChangesAsync();
        });
    }

    /// <summary>A group the event does not invite, renamed and given a member.</summary>
    [Fact]
    public async Task Changing_a_group_outside_the_audience_does_not_move_the_version()
    {
        var world = await ArrangeAsync();

        await AssertDoesNotMoveAsync(world.EventId, "Changing an unattached group", async () =>
        {
            await using var db = NewDbContext();
            var group = await db.StudentGroups.FindAsync(world.Spare);
            group!.Name = "BSIT 4-C (renamed)";
            db.StudentGroupMembers.Add(
                new StudentGroupMember
                {
                    StudentGroupId = world.Spare,
                    StudentId = world.Individual,
                });
            await db.SaveChangesAsync();
        });
    }

    /// <summary>
    /// <b>The ADR-001 D-2 display cache is not published, so it must not move the version.</b>
    /// <c>Students.Course/YearLevel/Section</c> are denormalized convenience columns; a re-import that
    /// refreshes them across the school would otherwise invalidate every device's manifest for a change
    /// no device can see.
    ///
    /// <para>
    /// Written by SQL because <c>EamsDbContext.GuardDerivedAcademicFields</c> refuses a direct
    /// <c>SaveChanges</c> on these three columns — which is the D-2 guard working, and is also exactly
    /// the shape of write the real refresh performs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Changing_the_display_cache_columns_does_not_move_the_version()
    {
        var world = await ArrangeAsync();

        await AssertDoesNotMoveAsync(world.EventId, "Changing Course/YearLevel/Section", async () =>
        {
            await using var db = NewDbContext();
            var updated = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE Students
                 SET Course = 'BSCRIM', YearLevel = '4th Year', Section = 'Z'
                 WHERE Id = {world.Listed}
                 """);

            Assert.Equal(1, updated);
        });
    }

    /// <summary>
    /// <b>Two pulls with nothing in between agree.</b> <c>serverTime</c> differs and the version does
    /// not — which is what makes the conditional GET able to answer 304 at all, and is the property a
    /// <c>serverTime</c> accidentally folded into the hash would destroy.
    /// </summary>
    [Fact]
    public async Task Two_pulls_with_no_write_between_them_share_a_version()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var first = (await EventsOn(db).GetManifestAsync(world.EventId)).Manifest!;
        var second = (await EventsOn(db).GetManifestAsync(world.EventId)).Manifest!;

        Assert.Equal(first.Version, second.Version);
        Assert.True(second.ServerTime >= first.ServerTime);
    }

    /// <summary>
    /// <b>The requesting device does not appear in the version.</b> Two devices in the same school pull
    /// the same event and get the same value — which is what would make a server-side hash cache safe
    /// if one is ever needed, and what stops a fleet of handsets each holding a private manifest.
    /// </summary>
    [Fact]
    public async Task The_version_is_the_same_for_every_device()
    {
        var world = await ArrangeAsync();

        var firstKey = await IssueDeviceKeyAsync(world.SchoolId, "Kiosk One");
        var secondKey = await IssueDeviceKeyAsync(world.SchoolId, "Kiosk Two");
        Assert.NotEqual(firstKey, secondKey);

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        var route = $"/api/v1/events/{world.EventId}/manifest";
        using var first = factory.CreateClient().WithDeviceKey(firstKey);
        using var second = factory.CreateClient().WithDeviceKey(secondKey);

        var one = await first.GetAsync(route);
        var two = await second.GetAsync(route);

        Assert.Equal(System.Net.HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, two.StatusCode);
        Assert.Equal(one.Headers.ETag?.ToString(), two.Headers.ETag?.ToString());
    }
}
