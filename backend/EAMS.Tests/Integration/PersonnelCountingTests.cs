using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 D-102 against real SQL Server: one population per ratio, and every <c>Expected</c> moving
/// together. The rate's bound and <c>unexpected</c> are <c>INTERSECT</c>/<c>EXCEPT</c> over each population,
/// which is what an in-memory store would not prove.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelCountingTests : IntegrationTest
{
    public PersonnelCountingTests(SqlServerFixture sql) : base(sql) { }

    private static readonly CaptureCapabilities PersonnelClient = new(Personnel: true);

    private const string WalkInUid = "06A1B2C3";

    private async Task<PersonnelEventWorld> ArrangeAsync()
    {
        await using var db = NewDbContext();
        return await PersonnelEventWorld.ArrangeAsync(db);
    }

    private async Task<Guid> AddPersonnelAsync(
        Guid schoolId, string number, string? rfidUid = null, string status = PersonnelStatus.Active)
    {
        await using var db = NewDbContext();
        var person = TestData.NewPersonnel(schoolId, number, rfidUid: rfidUid, status: status);
        db.Personnel.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    /// <summary>A student with a card and no place in the audience.</summary>
    private async Task AddWalkInAsync(Guid schoolId)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, "2023-9999");
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(schoolId, student.Id, WalkInUid));
        await db.SaveChangesAsync();
    }

    private async Task AttachStudentAsync(PersonnelEventWorld world, string number)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(world.SchoolId, number);
        db.Students.Add(student);
        db.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentId = student.Id });
        await db.SaveChangesAsync();
    }

    private async Task PointDefinitionAtAsync(PersonnelEventWorld world, params Guid[] personnelIds)
    {
        await using var db = NewDbContext();
        var definition = await db.AudienceDefinitions.SingleAsync(d => d.Id == world.DefinitionId);
        definition.AudienceType = AudienceType.SpecificIndividuals;
        definition.CriteriaJson = System.Text.Json.JsonSerializer.Serialize(
            new AudienceCriteriaDto(PersonnelIds: personnelIds),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await db.SaveChangesAsync();
    }

    private async Task TapAsync(Guid eventId, string uid, string tapId, CaptureCapabilities? capabilities = null)
    {
        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(eventId, uid, null, tapId, DateTime.UtcNow), capabilities ?? PersonnelClient);
        Assert.Equal(TapOutcome.Recorded, response.Outcome);
    }

    private async Task<EventSummaryDto> SummaryAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        return (await EventsOn(db).GetSummaryAsync(eventId))!;
    }

    /// <summary>
    /// D-102's named test, both halves. Live, the person removed from the definition is <c>unexpected</c>
    /// and the rate stays at or below 100. Closed, the same person is frozen in with their row kept (D-97),
    /// so they are expected and only the student walk-in is left <c>unexpected</c>.
    /// </summary>
    [Fact]
    public async Task A_personnel_tapper_removed_while_open_is_unexpected_live_and_expected_after_the_close()
    {
        var world = await ArrangeAsync();
        var stays = await AddPersonnelAsync(world.SchoolId, "EMP-0002", rfidUid: "07B2C3D4");
        await AddWalkInAsync(world.SchoolId);
        await PointDefinitionAtAsync(world, world.PersonnelId, stays);

        await TapAsync(world.EventId, PersonnelEventWorld.StudentUid, "student");
        await TapAsync(world.EventId, WalkInUid, "walk-in");
        await TapAsync(world.EventId, PersonnelEventWorld.PersonnelUid, "removed");
        await TapAsync(world.EventId, "07B2C3D4", "stays");

        await PointDefinitionAtAsync(world, stays);

        var live = await SummaryAsync(world.EventId);
        Assert.Equal((1, 1, 2), (live.ExpectedStudents, live.ExpectedPersonnel, live.Expected));
        Assert.Equal(2, live.Unexpected);
        Assert.Equal(4, live.Present);
        Assert.Equal(100, live.AttendanceRate);

        await using (var db = NewDbContext())
        {
            Assert.Equal(EventWriteOutcome.Saved,
                (await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed)).Outcome);
        }

        var closed = await SummaryAsync(world.EventId);
        Assert.Equal((1, 2, 3), (closed.ExpectedStudents, closed.ExpectedPersonnel, closed.Expected));
        Assert.Equal(1, closed.Unexpected);
        Assert.Equal(4, closed.Present);
        Assert.Equal(100, closed.AttendanceRate);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.AttendanceRecords.AsNoTracking()
            .CountAsync(a => a.EventId == world.EventId && a.PersonnelId == world.PersonnelId));
    }

    [Fact]
    public async Task The_rate_counts_attended_personnel_over_both_populations()
    {
        var world = await ArrangeAsync();
        await AttachStudentAsync(world, "2023-0002");
        await AddPersonnelAsync(world.SchoolId, "EMP-0002");

        await TapAsync(world.EventId, PersonnelEventWorld.PersonnelUid, "personnel");

        // One of four: the student side contributes nothing, so this figure is the personnel term alone.
        var summary = await SummaryAsync(world.EventId);
        Assert.Equal((2, 2, 4), (summary.ExpectedStudents, summary.ExpectedPersonnel, summary.Expected));
        Assert.Equal(25, summary.AttendanceRate);
    }

    /// <summary>
    /// A member marked <c>Absent</c> by hand has a row and did not attend, and being recorded is not being
    /// unexpected. Pins the two personnel terms apart: attended is Present/Late, recorded is any row.
    /// </summary>
    [Fact]
    public async Task An_absent_personnel_row_is_neither_attended_nor_unexpected()
    {
        var world = await ArrangeAsync();
        await using (var db = NewDbContext())
        {
            Assert.Equal(ManualOutcome.Saved, (await AttendanceOn(db).ManualAsync(
                world.EventId, null, world.PersonnelId, AttendanceStatus.Absent, notes: null)).Outcome);
        }

        var summary = await SummaryAsync(world.EventId);
        Assert.Equal(0, summary.AttendanceRate);
        Assert.Equal(0, summary.Unexpected);
        Assert.Equal(1, summary.Absent);
    }

    /// <summary>Every <c>Expected</c> of D-102's list, on one event, agreeing with its own two parts.</summary>
    [Fact]
    public async Task Every_expected_is_the_sum_of_its_two_populations_and_all_of_them_agree()
    {
        var world = await ArrangeAsync();
        await AddPersonnelAsync(world.SchoolId, "EMP-0002");
        await TapAsync(world.EventId, PersonnelEventWorld.PersonnelUid, "personnel");

        await using var db = NewDbContext();
        var events = EventsOn(db);

        var summary = (await events.GetSummaryAsync(world.EventId))!;
        var roster = (await events.GetRosterAsync(world.EventId))!;
        var audience = (await events.GetAudienceAsync(world.EventId))!;
        var attached = (await events.AttachAudienceAsync(
            world.EventId, new EventAudienceRequest(null, null))).Result!;
        var live = (await events.GetLiveAttendanceAsync(world.EventId, since: null)).Live!;

        Assert.Equal((3, 1, 2), (summary.Expected, summary.ExpectedStudents, summary.ExpectedPersonnel));
        Assert.Equal((3, 1, 2), (roster.Expected, roster.ExpectedStudents, roster.ExpectedPersonnel));
        Assert.Equal((3, 1, 2), (audience.Expected, audience.ExpectedStudents, audience.ExpectedPersonnel));
        Assert.Equal((3, 1, 2), (attached.Expected, attached.ExpectedStudents, attached.ExpectedPersonnel));
        Assert.Equal((3, 1, 2), (live.Counters.Expected, live.Counters.ExpectedStudents, live.Counters.ExpectedPersonnel));

        var detached = (await events.DetachDefinitionAsync(world.EventId, world.DefinitionId)).Result!;
        Assert.Equal((1, 1, 0), (detached.Expected, detached.ExpectedStudents, detached.ExpectedPersonnel));
    }

    /// <summary>The roster header and the summary describe one event, though the entries list students.</summary>
    [Fact]
    public async Task The_roster_header_matches_the_summary_on_a_mixed_event()
    {
        var world = await ArrangeAsync();
        var stays = await AddPersonnelAsync(world.SchoolId, "EMP-0002", rfidUid: "07B2C3D4");
        await AddPersonnelAsync(world.SchoolId, "EMP-0003");
        await PointDefinitionAtAsync(world, world.PersonnelId, stays);

        await TapAsync(world.EventId, PersonnelEventWorld.PersonnelUid, "removed");
        await TapAsync(world.EventId, "07B2C3D4", "stays");
        await PointDefinitionAtAsync(world, stays);

        await using var db = NewDbContext();
        var summary = (await EventsOn(db).GetSummaryAsync(world.EventId))!;
        var roster = (await EventsOn(db).GetRosterAsync(world.EventId))!;

        Assert.Equal(
            (summary.Expected, summary.Present, summary.Late, summary.Absent, summary.Excused, summary.Unexpected),
            (roster.Expected, roster.Present, roster.Late, roster.Absent, roster.Excused, roster.Unexpected));
        Assert.Equal(1, roster.Unexpected);

        // The attached student has not tapped, and the personnel member who stayed has.
        Assert.Equal(1, roster.NotRecorded);
        Assert.Equal(world.StudentId, Assert.Single(roster.Entries).StudentId);
    }

    /// <summary>D-102's "what each advisory field becomes", with the figures chosen to be different.</summary>
    [Fact]
    public async Task The_advisory_fields_keep_their_names_and_take_their_new_meanings()
    {
        var world = await ArrangeAsync();
        var inactive = await AddPersonnelAsync(world.SchoolId, "EMP-0002", status: PersonnelStatus.Inactive);

        await using (var db = NewDbContext())
        {
            // One session whose only personnel registrant is Inactive, so it governs nobody.
            var session = new PreRegistrationSession
            {
                SchoolId = world.SchoolId,
                AudienceDefinitionId = world.DefinitionId,
                Name = "Staff session",
                Capacity = 10,
                EventId = world.EventId,
            };
            db.PreRegistrationSessions.Add(session);
            db.PreRegistrations.Add(new PreRegistration
            {
                SchoolId = world.SchoolId,
                Session = session,
                PersonnelId = inactive,
                AttendeeType = PreRegistrantType.Personnel,
                Method = PreRegistrationMethod.Manual,
            });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var audience = (await EventsOn(read).GetAudienceAsync(world.EventId))!;

        // The definition resolves both, but only the Active one is countable.
        Assert.Equal(1, audience.ExpectedPersonnel);
        Assert.Equal(1, audience.AdvisoryPersonnelCount);
        Assert.Equal(2, Assert.Single(audience.Definitions).PersonnelCount);

        // Display counts: distinct not-deleted registrants, any Status. Not the selector.
        Assert.Equal(1, audience.PreRegistration!.TotalAdvisoryPersonnelCount);
        Assert.Equal(1, Assert.Single(audience.PreRegistration.LinkedSessions).AdvisoryPersonnelCount);

        // No student registrant either, so this no longer equals expectedStudents.
        Assert.Equal(0, audience.PreRegistration.TotalPreRegisteredStudentCount);
        Assert.Equal(1, audience.ExpectedStudents);

        Assert.Equal(ExpectedAudienceSource.Audience, audience.ExpectedSource);
        Assert.Equal(ExpectedAudienceSource.Audience, audience.ExpectedPersonnelSource);
    }
}
