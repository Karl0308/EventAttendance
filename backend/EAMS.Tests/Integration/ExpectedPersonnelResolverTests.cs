using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 D-95's <c>ExpectedPersonnelIds(event)</c> and the personnel half of D-103's selector, against
/// real SQL Server: the union of definitions and the membership <c>EXISTS</c> are what a provider would
/// get wrong. Each test reads the set both ways the callers do, as a list and as one membership test.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ExpectedPersonnelResolverTests : IntegrationTest
{
    public ExpectedPersonnelResolverTests(SqlServerFixture sql) : base(sql) { }

    private const string EveryEmployee = """{"scope":"Employees"}""";
    private const string EveryStudent = """{"scope":"Students"}""";

    private sealed record World(Guid SchoolId, Guid EventId, Guid ClassificationId);

    private async Task<World> ArrangeAsync(string status = EventStatus.Open)
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var classification = TestData.NewEventClassification(school.Id);
        db.EventClassifications.Add(classification);
        var ev = TestData.NewEvent(school.Id, status);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, classification.Id);
    }

    private async Task<Guid> AddPersonnelAsync(
        Guid schoolId, string number, string status = PersonnelStatus.Active, bool deleted = false)
    {
        await using var db = NewDbContext();
        var person = TestData.NewPersonnel(schoolId, number, status: status);
        person.IsDeleted = deleted;
        db.Personnel.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    private async Task<Guid> AttachDefinitionAsync(
        World world, string name, string criteriaJson, string type = AudienceType.UniversityWide)
    {
        await using var db = NewDbContext();
        var definition = new AudienceDefinition
        {
            SchoolId = world.SchoolId,
            EventClassificationId = world.ClassificationId,
            Name = name,
            NameKey = AudienceText.KeyFor(name),
            AudienceType = type,
            CriteriaJson = criteriaJson,
        };
        db.AudienceDefinitions.Add(definition);
        db.EventAudienceDefinitions.Add(new EventAudienceDefinition
        {
            EventId = world.EventId,
            AudienceDefinitionId = definition.Id,
        });
        await db.SaveChangesAsync();
        return definition.Id;
    }

    private async Task RegisterAsync(World world, Guid definitionId, params Guid[] personnelIds)
    {
        await using var db = NewDbContext();
        var session = new PreRegistrationSession
        {
            SchoolId = world.SchoolId,
            AudienceDefinitionId = definitionId,
            Name = $"Session {Guid.NewGuid():N}"[..20],
            Capacity = 1000,
            EventId = world.EventId,
        };
        db.PreRegistrationSessions.Add(session);

        foreach (var id in personnelIds)
        {
            db.PreRegistrations.Add(new PreRegistration
            {
                SchoolId = world.SchoolId,
                Session = session,
                PersonnelId = id,
                AttendeeType = PreRegistrantType.Personnel,
                Method = PreRegistrationMethod.Manual,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>The set as a list, cross-checked against the membership form the tap uses.</summary>
    private async Task<HashSet<Guid>> ExpectedAsync(World world, string status = EventStatus.Open, params Guid[] probes)
    {
        await using var db = NewDbContext();
        var query = await ExpectedPersonnel.IdsAsync(db, world.EventId, world.SchoolId, status, default);
        var ids = (await query.ToListAsync()).ToHashSet();

        foreach (var probe in probes)
            Assert.Equal(ids.Contains(probe), await query.ContainsAsync(probe));

        return ids;
    }

    private async Task<bool> GovernsAsync(World world)
    {
        await using var db = NewDbContext();
        return await ExpectedPersonnel.PreRegistrationGovernsAsync(db, world.EventId, world.SchoolId, default);
    }

    // ------------------------------------------------------------------------------ audience only

    [Fact]
    public async Task ExpectedPersonnel_audience_only_is_the_personnel_side_of_the_attached_definitions()
    {
        var world = await ArrangeAsync();
        var inDepartment = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        await using (var db = NewDbContext())
        {
            var other = TestData.NewPersonnel(world.SchoolId, "EMP-2", department: "HR");
            db.Personnel.Add(other);
            await db.SaveChangesAsync();
        }

        await AttachDefinitionAsync(
            world, "CICT staff", """{"departments":["CICT"],"scope":"Employees"}""", AudienceType.Department);

        Assert.Equal([inDepartment], await ExpectedAsync(world, probes: inDepartment));
        Assert.False(await GovernsAsync(world));
    }

    [Fact]
    public async Task ExpectedPersonnel_two_definitions_union_to_one_distinct_set()
    {
        var world = await ArrangeAsync();
        var person = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        await AttachDefinitionAsync(world, "Everyone", EveryEmployee);
        await AttachDefinitionAsync(world, "Everyone again", EveryEmployee);

        Assert.Equal([person], await ExpectedAsync(world, probes: person));
    }

    /// <summary>ADR-013: "who it covers" masks the personnel side away; it does not fall through.</summary>
    [Fact]
    public async Task ExpectedPersonnel_a_students_scope_mask_leaves_nobody()
    {
        var world = await ArrangeAsync();
        var person = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        await AttachDefinitionAsync(world, "Students only", EveryStudent);

        Assert.Empty(await ExpectedAsync(world, probes: person));
    }

    [Fact]
    public async Task ExpectedPersonnel_with_no_definition_attached_is_an_empty_set_not_an_error()
    {
        var world = await ArrangeAsync();
        var person = await AddPersonnelAsync(world.SchoolId, "EMP-1");

        Assert.Empty(await ExpectedAsync(world, probes: person));
    }

    // ------------------------------------------------------------------------ countable, live sets

    [Fact]
    public async Task ExpectedPersonnel_audience_excludes_deleted_and_inactive_rows()
    {
        var world = await ArrangeAsync();
        var active = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        var inactive = await AddPersonnelAsync(world.SchoolId, "EMP-2", PersonnelStatus.Inactive);
        var deleted = await AddPersonnelAsync(world.SchoolId, "EMP-3", deleted: true);
        await AttachDefinitionAsync(world, "Everyone", EveryEmployee);

        Assert.Equal([active], await ExpectedAsync(world, probes: [active, inactive, deleted]));
    }

    [Fact]
    public async Task ExpectedPersonnel_preregistration_excludes_deleted_and_inactive_registrants()
    {
        var world = await ArrangeAsync();
        var active = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        var inactive = await AddPersonnelAsync(world.SchoolId, "EMP-2", PersonnelStatus.Inactive);
        var deleted = await AddPersonnelAsync(world.SchoolId, "EMP-3", deleted: true);
        var definition = await AttachDefinitionAsync(world, "Everyone", EveryEmployee);
        await RegisterAsync(world, definition, active, inactive, deleted);

        Assert.True(await GovernsAsync(world));
        Assert.Equal([active], await ExpectedAsync(world, probes: [active, inactive, deleted]));
    }

    // ------------------------------------------------------------------------- D-103's selector

    [Fact]
    public async Task ExpectedPersonnel_preregistration_governs_the_personnel_side()
    {
        var world = await ArrangeAsync();
        var registered = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        var audienceOnly = await AddPersonnelAsync(world.SchoolId, "EMP-2");
        var definition = await AttachDefinitionAsync(world, "Everyone", EveryEmployee);
        await RegisterAsync(world, definition, registered);

        Assert.True(await GovernsAsync(world));
        Assert.Equal([registered], await ExpectedAsync(world, probes: [registered, audienceOnly]));
    }

    /// <summary>Two linked sessions are one set, and a person in both counts once.</summary>
    [Fact]
    public async Task ExpectedPersonnel_two_linked_sessions_are_one_distinct_set()
    {
        var world = await ArrangeAsync();
        var first = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        var both = await AddPersonnelAsync(world.SchoolId, "EMP-2");
        var definition = await AttachDefinitionAsync(world, "Everyone", EveryEmployee);
        await RegisterAsync(world, definition, first, both);
        await RegisterAsync(world, definition, both);

        await using var db = NewDbContext();
        var query = await ExpectedPersonnel.IdsAsync(db, world.EventId, world.SchoolId, EventStatus.Open, default);

        Assert.Equal(2, await query.CountAsync());
    }

    /// <summary>
    /// D-103's whole point: the selector decides on countable registrants, never on a raw count. A session
    /// whose only personnel registrant is Inactive does not replace the audience.
    /// </summary>
    [Fact]
    public async Task ExpectedPersonnel_an_inactive_only_registrant_falls_back_to_the_audience()
    {
        var world = await ArrangeAsync();
        var inAudience = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        var registrant = await AddPersonnelAsync(world.SchoolId, "EMP-2", PersonnelStatus.Inactive);
        var definition = await AttachDefinitionAsync(world, "Everyone", EveryEmployee);
        await RegisterAsync(world, definition, registrant);

        Assert.False(await GovernsAsync(world));
        Assert.Equal([inAudience], await ExpectedAsync(world, probes: [inAudience, registrant]));
    }

    [Fact]
    public async Task ExpectedPersonnel_a_deleted_only_registrant_falls_back_to_the_audience()
    {
        var world = await ArrangeAsync();
        var inAudience = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        var registrant = await AddPersonnelAsync(world.SchoolId, "EMP-2", deleted: true);
        var definition = await AttachDefinitionAsync(world, "Everyone", EveryEmployee);
        await RegisterAsync(world, definition, registrant);

        Assert.False(await GovernsAsync(world));
        Assert.Equal([inAudience], await ExpectedAsync(world, probes: [inAudience, registrant]));
    }

    /// <summary>A linked session with no registrants at all leaves the personnel audience alone (D-103).</summary>
    [Fact]
    public async Task ExpectedPersonnel_a_session_with_no_registrants_falls_back_to_the_audience()
    {
        var world = await ArrangeAsync();
        var inAudience = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        var definition = await AttachDefinitionAsync(world, "Everyone", EveryEmployee);
        await RegisterAsync(world, definition);

        Assert.False(await GovernsAsync(world));
        Assert.Equal([inAudience], await ExpectedAsync(world, probes: inAudience));
    }

    // ------------------------------------------------------------------------------ terminal events

    /// <summary>
    /// D-97: a terminal event's set is the frozen rows, whatever those people have become since and
    /// whatever the audience resolves to now. Empty until B2 writes them, which the second half pins.
    /// </summary>
    [Fact]
    public async Task ExpectedPersonnel_a_terminal_event_reads_frozen_EventPersonnel()
    {
        var world = await ArrangeAsync(EventStatus.Closed);
        var frozen = await AddPersonnelAsync(world.SchoolId, "EMP-1", PersonnelStatus.Inactive);
        var audienceOnly = await AddPersonnelAsync(world.SchoolId, "EMP-2");
        await AttachDefinitionAsync(world, "Everyone", EveryEmployee);

        await using (var db = NewDbContext())
        {
            db.EventPersonnel.Add(new EventPersonnel { EventId = world.EventId, PersonnelId = frozen });
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            [frozen],
            await ExpectedAsync(world, EventStatus.Closed, probes: [frozen, audienceOnly]));
    }

    [Fact]
    public async Task ExpectedPersonnel_a_terminal_event_with_nothing_frozen_is_empty_whatever_the_audience_says()
    {
        var world = await ArrangeAsync(EventStatus.Closed);
        var audienceOnly = await AddPersonnelAsync(world.SchoolId, "EMP-1");
        await AttachDefinitionAsync(world, "Everyone", EveryEmployee);

        Assert.Empty(await ExpectedAsync(world, EventStatus.Closed, probes: audienceOnly));
    }

    // ------------------------------------------------------------------------------------- tenancy

    /// <summary>
    /// D-99 tenancy: a definition's criteria resolve a fresh person query, so a definition that names
    /// another school's id must still resolve nobody when no tenant is pinned.
    /// </summary>
    [Fact]
    public async Task ExpectedPersonnel_a_definition_naming_another_schools_personnel_resolves_nobody()
    {
        var world = await ArrangeAsync();
        Guid foreign;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("OTHER");
            db.Schools.Add(other);
            var person = TestData.NewPersonnel(other.Id, "EMP-9");
            db.Personnel.Add(person);
            await db.SaveChangesAsync();
            foreign = person.Id;
        }

        await AttachDefinitionAsync(
            world, "Picked", $$"""{"personnelIds":["{{foreign}}"]}""", AudienceType.SpecificIndividuals);

        Assert.Empty(await ExpectedAsync(world, probes: foreign));
    }
}
