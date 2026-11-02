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
/// ADR-015 D-103: a linked session replaces a population only if it holds a countable registrant from that
/// population. Every case is read live and again after a real close, because the one selector serving both
/// is the decision, and a freeze that disagreed with the live read is the failure it exists to prevent.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PreRegistrationPerPopulationTests : IntegrationTest
{
    public PreRegistrationPerPopulationTests(SqlServerFixture sql) : base(sql) { }

    private sealed record World(Guid SchoolId, Guid EventId, Guid DefinitionId, List<Guid> Students, List<Guid> Staff);

    /// <summary>An Open event whose audience is everyone in a school of its own, unless a scope narrows it.</summary>
    private async Task<World> ArrangeAsync(int students, int staff, string scope = AudienceScope.Both)
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var classification = TestData.NewEventClassification(school.Id);
        db.EventClassifications.Add(classification);
        var ev = TestData.NewEvent(school.Id, EventStatus.Open);
        db.Events.Add(ev);

        var definition = new AudienceDefinition
        {
            SchoolId = school.Id,
            EventClassificationId = classification.Id,
            Name = "Everyone",
            NameKey = AudienceText.KeyFor("Everyone"),
            AudienceType = AudienceType.UniversityWide,
            CriteriaJson = $$"""{"scope":"{{scope}}"}""",
        };
        db.AudienceDefinitions.Add(definition);
        db.EventAudienceDefinitions.Add(new EventAudienceDefinition
        {
            EventId = ev.Id,
            AudienceDefinitionId = definition.Id,
        });

        var studentRows = Enumerable.Range(1, students)
            .Select(i => TestData.NewStudent(school.Id, $"2023-{i:0000}")).ToList();
        db.Students.AddRange(studentRows);

        var staffRows = Enumerable.Range(1, staff)
            .Select(i => TestData.NewPersonnel(school.Id, $"EMP-{i:0000}")).ToList();
        db.Personnel.AddRange(staffRows);

        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, definition.Id,
            studentRows.Select(s => s.Id).ToList(), staffRows.Select(p => p.Id).ToList());
    }

    private async Task LinkSessionAsync(
        World world, IEnumerable<Guid>? students = null, IEnumerable<Guid>? staff = null)
    {
        await using var db = NewDbContext();
        var session = new PreRegistrationSession
        {
            SchoolId = world.SchoolId,
            AudienceDefinitionId = world.DefinitionId,
            Name = "Registered",
            Capacity = 1000,
            EventId = world.EventId,
        };
        db.PreRegistrationSessions.Add(session);

        foreach (var id in students ?? [])
            db.PreRegistrations.Add(new PreRegistration
            {
                SchoolId = world.SchoolId,
                Session = session,
                StudentId = id,
                AttendeeType = PreRegistrantType.Student,
                Method = PreRegistrationMethod.Manual,
            });

        foreach (var id in staff ?? [])
            db.PreRegistrations.Add(new PreRegistration
            {
                SchoolId = world.SchoolId,
                Session = session,
                PersonnelId = id,
                AttendeeType = PreRegistrantType.Personnel,
                Method = PreRegistrationMethod.Manual,
            });

        await db.SaveChangesAsync();
    }

    private async Task<EventAudienceDto> AudienceAsync(World world)
    {
        await using var db = NewDbContext();
        return (await EventsOn(db).GetAudienceAsync(world.EventId))!;
    }

    private async Task CloseAsync(World world)
    {
        await using var db = NewDbContext();
        Assert.Equal(EventWriteOutcome.Saved,
            (await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed)).Outcome);
    }

    /// <summary>What the freeze actually wrote, read from the tables and not through the resolver.</summary>
    private async Task<(int Students, int Personnel, int AbsentRows)> FrozenAsync(World world)
    {
        await using var db = NewDbContext();
        return (await db.EventGroups.AsNoTracking().CountAsync(eg => eg.EventId == world.EventId && eg.StudentId != null),
                await db.EventPersonnel.AsNoTracking().CountAsync(ep => ep.EventId == world.EventId),
                await db.AttendanceRecords.AsNoTracking()
                    .CountAsync(a => a.EventId == world.EventId && a.Status == AttendanceStatus.Absent));
    }

    /// <summary>QA's example, scaled down: 4 students and 3 staff, 2 students registered and no staff.</summary>
    [Fact]
    public async Task QA_example_registered_students_replace_only_the_student_side()
    {
        var world = await ArrangeAsync(students: 4, staff: 3);
        await LinkSessionAsync(world, students: world.Students.Take(2));

        var live = await AudienceAsync(world);
        Assert.Equal((5, 2, 3), (live.Expected, live.ExpectedStudents, live.ExpectedPersonnel));
        Assert.Equal(ExpectedAudienceSource.PreRegistration, live.ExpectedSource);
        Assert.Equal(ExpectedAudienceSource.Audience, live.ExpectedPersonnelSource);

        await CloseAsync(world);

        Assert.Equal((2, 3, 5), await FrozenAsync(world));
        var closed = await AudienceAsync(world);
        Assert.Equal((5, 2, 3), (closed.Expected, closed.ExpectedStudents, closed.ExpectedPersonnel));
    }

    [Fact]
    public async Task Its_mirror_registered_staff_replace_only_the_personnel_side()
    {
        var world = await ArrangeAsync(students: 4, staff: 3);
        await LinkSessionAsync(world, staff: world.Staff.Take(1));

        var live = await AudienceAsync(world);
        Assert.Equal((5, 4, 1), (live.Expected, live.ExpectedStudents, live.ExpectedPersonnel));
        Assert.Equal(ExpectedAudienceSource.Audience, live.ExpectedSource);
        Assert.Equal(ExpectedAudienceSource.PreRegistration, live.ExpectedPersonnelSource);

        await CloseAsync(world);

        Assert.Equal((4, 1, 5), await FrozenAsync(world));
    }

    [Fact]
    public async Task Both_sides_registered_replace_both_sides()
    {
        var world = await ArrangeAsync(students: 4, staff: 3);
        await LinkSessionAsync(world, students: world.Students.Take(1), staff: world.Staff.Take(1));

        var live = await AudienceAsync(world);
        Assert.Equal((2, 1, 1), (live.Expected, live.ExpectedStudents, live.ExpectedPersonnel));

        await CloseAsync(world);

        Assert.Equal((1, 1, 2), await FrozenAsync(world));
    }

    /// <summary>
    /// The one predicate: a session whose only staff registrant is Inactive does not govern. The raw count
    /// still shows 1 (D-102), and the freeze falls back to the audience exactly as the live read does.
    /// </summary>
    [Fact]
    public async Task A_session_whose_only_staff_registrant_is_Inactive_leaves_the_personnel_side_to_the_audience()
    {
        var world = await ArrangeAsync(students: 1, staff: 2);
        await using (var db = NewDbContext())
        {
            var inactive = TestData.NewPersonnel(world.SchoolId, "EMP-0099", status: PersonnelStatus.Inactive);
            db.Personnel.Add(inactive);
            await db.SaveChangesAsync();
            await LinkSessionAsync(world, staff: [inactive.Id]);
        }

        var live = await AudienceAsync(world);
        Assert.Equal(2, live.ExpectedPersonnel);
        Assert.Equal(1, live.PreRegistration!.TotalAdvisoryPersonnelCount);
        Assert.Equal(ExpectedAudienceSource.Audience, live.ExpectedPersonnelSource);

        await CloseAsync(world);

        Assert.Equal((1, 2, 3), await FrozenAsync(world));
    }

    /// <summary>The student half of the one predicate: not deleted.</summary>
    [Fact]
    public async Task A_session_whose_only_student_registrant_is_soft_deleted_leaves_the_student_side_to_the_audience()
    {
        var world = await ArrangeAsync(students: 2, staff: 0);
        await using (var db = NewDbContext())
        {
            var gone = TestData.NewStudent(world.SchoolId, "2023-0099");
            gone.IsDeleted = true;
            db.Students.Add(gone);
            await db.SaveChangesAsync();
            await LinkSessionAsync(world, students: [gone.Id]);
        }

        var live = await AudienceAsync(world);
        Assert.Equal(2, live.ExpectedStudents);
        Assert.Equal(ExpectedAudienceSource.Audience, live.ExpectedSource);

        await CloseAsync(world);

        // The freeze resolves the audience with includeDeleted:true, so the deleted student is written down
        // by that source, as any soft-deleted student reached through a definition is. The selector still
        // said Audience: the set is the audience's, not the session's.
        Assert.Equal(3, (await FrozenAsync(world)).Students);
    }

    /// <summary>
    /// The personnel twin of the student control in <see cref="PreRegistrationEventLinkControlTests"/>,
    /// under a session that governs: a registrant soft-deleted before the close leaves the live set and
    /// stays in the frozen one, with an <c>Absent</c> row, because the freeze passes
    /// <c>includeDeleted: true</c> (D-97). The other registrant keeps the session governing.
    /// </summary>
    [Fact]
    public async Task A_personnel_registrant_soft_deleted_before_the_close_stays_in_the_frozen_set()
    {
        var world = await ArrangeAsync(students: 0, staff: 3);
        var registered = world.Staff.Take(2).ToList();
        await LinkSessionAsync(world, staff: registered);

        await using (var db = NewDbContext())
        {
            (await db.Personnel.SingleAsync(p => p.Id == registered[1])).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        var live = await AudienceAsync(world);
        Assert.Equal(1, live.ExpectedPersonnel);
        Assert.Equal(ExpectedAudienceSource.PreRegistration, live.ExpectedPersonnelSource);

        await CloseAsync(world);

        // Two, not one (the live set) and not three (the audience): the step up is D-15's accepted one.
        Assert.Equal((0, 2, 2), await FrozenAsync(world));
        Assert.Equal(2, (await AudienceAsync(world)).ExpectedPersonnel);

        await using var read = NewDbContext();
        Assert.True(await read.EventPersonnel.AsNoTracking()
            .AnyAsync(ep => ep.EventId == world.EventId && ep.PersonnelId == registered[1]));
    }

    /// <summary>
    /// The sources on a terminal event describe the sessions as they are now, because nothing stores the
    /// choice at the freeze. So registering into an open session linked to a closed event may move the
    /// labels, and it must not move a single frozen number.
    /// </summary>
    [Fact]
    public async Task Registering_after_the_close_may_move_the_source_labels_and_never_the_frozen_numbers()
    {
        var world = await ArrangeAsync(students: 2, staff: 2);
        await LinkSessionAsync(world);
        await CloseAsync(world);

        var before = await AudienceAsync(world);
        Assert.Equal(ExpectedAudienceSource.Audience, before.ExpectedSource);
        Assert.Equal(ExpectedAudienceSource.Audience, before.ExpectedPersonnelSource);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);
        var session = await SingleSessionIdAsync(world);
        await RegisterAsync(client, session, "Student", world.Students[0]);
        await RegisterAsync(client, session, "Personnel", world.Staff[0]);

        var after = await AudienceAsync(world);
        Assert.Equal(ExpectedAudienceSource.PreRegistration, after.ExpectedSource);
        Assert.Equal(ExpectedAudienceSource.PreRegistration, after.ExpectedPersonnelSource);

        Assert.Equal(
            (before.Expected, before.ExpectedStudents, before.ExpectedPersonnel),
            (after.Expected, after.ExpectedStudents, after.ExpectedPersonnel));
        Assert.Equal((4, 2, 2), (after.Expected, after.ExpectedStudents, after.ExpectedPersonnel));
        Assert.Equal((2, 2, 4), await FrozenAsync(world));
    }

    // ------------------------------------------------------------------------------- from-event capacity

    private async Task<Guid> SingleSessionIdAsync(World world)
    {
        await using var db = NewDbContext();
        return await db.PreRegistrationSessions.AsNoTracking()
            .Where(s => s.EventId == world.EventId).Select(s => s.Id).SingleAsync();
    }

    private static async Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, Guid sessionId, string type, Guid attendeeId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/pre-registration/sessions/{sessionId}/register",
            new { method = "Manual", type, attendeeId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private async Task<int> CreateFromEventAsync(HttpClient client, World world)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/v1/events/{world.EventId}/pre-registration/sessions/from-event", new { name = "Desk" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        await using var db = NewDbContext();
        return await db.PreRegistrationSessions.AsNoTracking()
            .Where(s => s.EventId == world.EventId).Select(s => s.Capacity).SingleAsync();
    }

    /// <summary>The masked student stays out of the seed; a student-only seed would have clamped to 1.</summary>
    [Fact]
    public async Task From_event_on_an_employees_only_definition_seeds_its_personnel_and_a_second_staff_registrant_fits()
    {
        var world = await ArrangeAsync(students: 1, staff: 2, scope: AudienceScope.Employees);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var seeded = await CreateFromEventAsync(client, world);

        var session = await SingleSessionIdAsync(world);
        await RegisterAsync(client, session, "Personnel", world.Staff[0]);
        await RegisterAsync(client, session, "Personnel", world.Staff[1]); // CapacityReached if seeded 1

        Assert.Equal(2, seeded);
    }

    [Fact]
    public async Task From_event_on_a_mixed_event_seeds_the_sum_of_both_populations()
    {
        var world = await ArrangeAsync(students: 2, staff: 3);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal(5, await CreateFromEventAsync(client, world));
    }

    /// <summary>ADR-015 R2: this used to zero the denominator; now the whole audience is expected.</summary>
    [Fact]
    public async Task A_session_with_no_registrants_replaces_nobody_live_or_after_the_close()
    {
        var world = await ArrangeAsync(students: 2, staff: 2);
        await LinkSessionAsync(world);

        var live = await AudienceAsync(world);
        Assert.Equal((4, 2, 2), (live.Expected, live.ExpectedStudents, live.ExpectedPersonnel));
        Assert.Equal(ExpectedAudienceSource.Audience, live.ExpectedSource);
        Assert.Equal(ExpectedAudienceSource.Audience, live.ExpectedPersonnelSource);
        Assert.NotNull(live.PreRegistration);

        await CloseAsync(world);

        Assert.Equal((2, 2, 4), await FrozenAsync(world));
        var closed = await AudienceAsync(world);
        Assert.Equal((4, 2, 2), (closed.Expected, closed.ExpectedStudents, closed.ExpectedPersonnel));
    }
}
