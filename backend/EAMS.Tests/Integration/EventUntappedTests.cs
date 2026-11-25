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
/// <c>GET /events/{id}/untapped</c>: the manual-attendance picker's candidates, answered in SQL. Real SQL
/// Server, because the read is a UNION ALL of two populations paged with OFFSET/FETCH over NOT EXISTS, and
/// an in-memory store would prove none of that.
///
/// <para>
/// The world reaches the expected audience by every live source at once — a section, a direct
/// attachment, a definition's student side and its personnel side — and puts one tapped person and one
/// outsider beside them, so each assertion can only pass if every source resolves and the tapped and the
/// unexpected stay out.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventUntappedTests : IntegrationTest
{
    public EventUntappedTests(SqlServerFixture sql) : base(sql) { }

    private const string Section = "BSCRIM 2-A";
    private const string SectionCard = "0A0B0C0D";
    private const string PersonnelCard = "05D4E5F6";

    private sealed record World(
        Guid SchoolId, Guid EventId,
        Guid Section, Guid Direct, Guid Definition, Guid TappedStudent, Guid Outsider,
        Guid Personnel, Guid TappedPersonnel);

    private async Task<World> ArrangeAsync()
    {
        World world;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
            db.Schools.Add(school);

            var term = TestData.NewTerm(school.Id);
            db.Terms.Add(term);
            var course = TestData.NewCourse(school.Id);
            db.Courses.Add(course);
            var offering = TestData.NewOffering(term.Id, course.Id, Section);
            db.CourseOfferings.Add(offering);

            var section = TestData.NewStudent(school.Id, "2023-0002", "Ana", null, "Cruz");
            var direct = TestData.NewStudent(school.Id, "2023-0001", "Maria", "Reyes", "Santos");
            var definition = TestData.NewStudent(school.Id, "2023-0003", "Ben", null, "Lim");
            var tapped = TestData.NewStudent(school.Id, "2023-0004", "Carl", null, "Abad");
            var outsider = TestData.NewStudent(school.Id, "2023-0005", "Dan", null, "Zamora");
            db.Students.AddRange(section, direct, definition, tapped, outsider);
            db.Enrollments.Add(TestData.NewEnrollment(section.Id, offering.Id));

            // An inactive card issued first, then the active one: only the active one may be offered.
            db.RfidCards.Add(TestData.NewCard(school.Id, section.Id, "0F0F0F0F", isActive: false));
            db.RfidCards.Add(TestData.NewCard(school.Id, section.Id, SectionCard));

            var person = TestData.NewPersonnel(school.Id, "EMP-0001", rfidUid: PersonnelCard);
            var tappedPerson = TestData.NewPersonnel(school.Id, "EMP-0002", "Andres", null, "Bonifacio");
            db.Personnel.AddRange(person, tappedPerson);

            var ev = TestData.NewLiveEvent(school.Id, EventStatus.Open);
            db.Events.Add(ev);
            db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = direct.Id });
            db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = tapped.Id });

            var classification = TestData.NewEventClassification(school.Id);
            db.EventClassifications.Add(classification);
            var audience = new AudienceDefinition
            {
                SchoolId = school.Id,
                EventClassificationId = classification.Id,
                Name = "Named people",
                NameKey = AudienceText.KeyFor("Named people"),
                AudienceType = AudienceType.SpecificIndividuals,
                CriteriaJson = JsonSerializer.Serialize(
                    new AudienceCriteriaDto(StudentIds: [definition.Id], PersonnelIds: [person.Id, tappedPerson.Id]),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };
            db.AudienceDefinitions.Add(audience);
            db.EventAudienceDefinitions.Add(new EventAudienceDefinition
            {
                EventId = ev.Id,
                AudienceDefinitionId = audience.Id,
            });

            await db.SaveChangesAsync();
            await ProjectionOn(db).SyncTermAsync(term.Id);

            world = new World(school.Id, ev.Id, section.Id, direct.Id, definition.Id, tapped.Id, outsider.Id,
                person.Id, tappedPerson.Id);
        }

        await using (var db = NewDbContext())
        {
            var group = await db.StudentGroups.AsNoTracking()
                .Where(g => g.SchoolId == world.SchoolId && g.SourceEntityType == GroupSourceEntityType.Section)
                .Select(g => g.Id)
                .SingleAsync();
            var attached = await EventsOn(db).AttachAudienceAsync(world.EventId, new EventAudienceRequest([group], null));
            Assert.Equal(EventWriteOutcome.Saved, attached.Outcome);
        }

        await using (var db = NewDbContext())
        {
            var attendance = AttendanceOn(db);
            Assert.Equal(ManualOutcome.Saved, (await attendance.ManualAsync(
                world.EventId, world.TappedStudent, null, AttendanceStatus.Present, null)).Outcome);
            Assert.Equal(ManualOutcome.Saved, (await attendance.ManualAsync(
                world.EventId, null, world.TappedPersonnel, AttendanceStatus.Present, null)).Outcome);
        }

        return world;
    }

    private async Task<PagedResult<UntappedPersonDto>> UntappedAsync(
        Guid eventId, string? search = null, string? personType = null, int? page = null, int? pageSize = null)
    {
        await using var db = NewDbContext();
        var result = await EventsOn(db).GetUntappedAsync(
            eventId, search, personType, PageRequest.From(page, pageSize));
        Assert.NotNull(result);
        return result;
    }

    [Fact]
    public async Task Untapped_resolves_every_audience_source_and_excludes_the_tapped_and_the_unexpected()
    {
        var world = await ArrangeAsync();

        var result = await UntappedAsync(world.EventId);

        // Cruz (section), Lim (definition), Rizal (personnel), Santos (direct) — by last name. Abad and
        // Bonifacio tapped; Zamora was never expected.
        Assert.Equal(4, result.Total);
        Assert.False(result.HasMore);
        Assert.Equal(["Cruz", "Lim", "Rizal", "Santos"], result.Items.Select(i => i.Name.Split(' ')[^1]));

        var cruz = result.Items[0];
        Assert.Equal((AttendeePersonType.Student, world.Section, (Guid?)null, "2023-0002", "Ana Cruz", SectionCard),
            (cruz.PersonType, cruz.StudentId, cruz.PersonnelId, cruz.Number, cruz.Name, cruz.CardUid));

        var rizal = result.Items[2];
        Assert.Equal((AttendeePersonType.Personnel, (Guid?)null, world.Personnel, "EMP-0001", "Jose P Rizal", PersonnelCard),
            (rizal.PersonType, rizal.StudentId, rizal.PersonnelId, rizal.Number, rizal.Name, rizal.CardUid));

        Assert.Equal(world.Definition, result.Items[1].StudentId);
        Assert.Null(result.Items[1].CardUid);
        Assert.Equal(world.Direct, result.Items[3].StudentId);
    }

    [Fact]
    public async Task Untapped_search_matches_a_name_or_number_fragment_and_person_type_narrows()
    {
        var world = await ArrangeAsync();

        Assert.Equal([world.Personnel], (await UntappedAsync(world.EventId, search: "riz")).Items.Select(i => i.PersonnelId));
        Assert.Equal([world.Section], (await UntappedAsync(world.EventId, search: "2023-0002")).Items.Select(i => i.StudentId));
        Assert.Equal([world.Direct], (await UntappedAsync(world.EventId, search: "Maria")).Items.Select(i => i.StudentId));
        // A LIKE wildcard is a literal character, not "match anything": EF escapes it.
        Assert.Equal(0, (await UntappedAsync(world.EventId, search: "%")).Total);
        // A tapped person never surfaces through search.
        Assert.Equal(0, (await UntappedAsync(world.EventId, search: "Abad")).Total);

        var personnel = await UntappedAsync(world.EventId, personType: AttendeePersonType.Personnel);
        Assert.Equal([world.Personnel], personnel.Items.Select(i => i.PersonnelId));

        var students = await UntappedAsync(world.EventId, personType: AttendeePersonType.Student);
        Assert.Equal(3, students.Total);
        Assert.All(students.Items, i => Assert.Equal(AttendeePersonType.Student, i.PersonType));
    }

    [Fact]
    public async Task Untapped_pages_partition_the_set_and_clamp_out_of_range_values()
    {
        var world = await ArrangeAsync();

        var first = await UntappedAsync(world.EventId, page: 1, pageSize: 3);
        var second = await UntappedAsync(world.EventId, page: 2, pageSize: 3);

        Assert.Equal((4, 3, true), (first.Total, first.Items.Count, first.HasMore));
        Assert.Equal((4, 1, false), (second.Total, second.Items.Count, second.HasMore));
        Assert.Empty(first.Items.Select(i => i.Number).Intersect(second.Items.Select(i => i.Number)));

        var clamped = await UntappedAsync(world.EventId, page: 0, pageSize: 0);
        // PageRequest.From's semantics: a size below the minimum falls back to the default.
        Assert.Equal((1, Paging.DefaultPageSize, 4), (clamped.Page, clamped.PageSize, clamped.Total));

        var capped = await UntappedAsync(world.EventId, pageSize: 100_000);
        Assert.Equal(Paging.MaxPageSize, capped.PageSize);

        var past = await UntappedAsync(world.EventId, page: 99);
        Assert.Equal((0, 4, false), (past.Items.Count, past.Total, past.HasMore));
    }

    /// <summary>
    /// The choice: a closed event answers from its <em>frozen</em> audience, the one the summary counts.
    /// The close gave every frozen attendee an <c>Absent</c> row, so nobody is left untapped.
    /// </summary>
    [Fact]
    public async Task Untapped_on_a_closed_event_reads_the_frozen_set_which_the_close_has_fully_recorded()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            Assert.Equal(EventWriteOutcome.Saved,
                (await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed)).Outcome);
        }

        var result = await UntappedAsync(world.EventId);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Untapped_for_an_unknown_event_is_null()
    {
        await using var db = NewDbContext();
        Assert.Null(await EventsOn(db).GetUntappedAsync(Guid.NewGuid(), null, null, PageRequest.From(null, null)));
    }

    [Fact]
    public async Task Untapped_route_serves_the_paged_envelope_to_attendance_write_and_403s_without_it()
    {
        var world = await ArrangeAsync();
        var route = $"/api/v1/events/{world.EventId}/untapped";

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using (var viewer = await SignedInClientAsync(factory, world.SchoolId, EamsRoleNames.Viewer))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(route)).StatusCode);
        }

        using var organizer = await SignedInClientAsync(factory, world.SchoolId, EamsRoleNames.Organizer);

        var response = await organizer.GetAsync($"{route}?search=riz&personType=personnel&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(["hasMore", "items", "page", "pageSize", "total"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, root.GetProperty("total").GetInt32());

        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(["cardUid", "name", "number", "personType", "personnelId", "studentId"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Personnel", item.GetProperty("personType").GetString());
        Assert.Equal(world.Personnel, item.GetProperty("personnelId").GetGuid());

        var badType = await organizer.GetAsync($"{route}?personType=staff");
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        using var problem = JsonDocument.Parse(await badType.Content.ReadAsStringAsync());
        Assert.Equal("UnknownPersonType", problem.RootElement.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await organizer.GetAsync($"/api/v1/events/{Guid.NewGuid()}/untapped")).StatusCode);
    }
}
