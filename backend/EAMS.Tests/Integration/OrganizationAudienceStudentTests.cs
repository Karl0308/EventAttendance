using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-011 D-82: an Organization audience matches STUDENTS as well as personnel, with one predicate, in the
/// one matcher (<c>AudienceResolution.BuildQueries</c>), so the resolve read, the live denominator, the
/// terminal freeze and the manifest all follow. Personnel stay advisory (ADR-007 D-70).
///
/// <para>
/// This class REPLACES the three <c>Today_</c> pins that <see cref="OrganizationMeaningPinTests"/> carried
/// before the change; the survivors there still pin the personnel half.
/// </para>
///
/// <para>
/// <b>The shared world.</b> Students (matched by organization key, i.e. trim and case-insensitive):
/// <c>S-1</c> "CICT" and <c>S-2</c> " cict " are in; <c>S-3</c> "CCS" and <c>S-4</c> (none) are out;
/// <c>S-5</c> "CICT" is soft-deleted and must never count. Personnel: <c>EMP-A</c> "CICT" and <c>EMP-B</c>
/// "cict " are in, <c>EMP-C</c> "CCS" is out. Every student carries the organization, so a zero can only
/// mean "the matcher declined students", never "no student has it" (the weakness of the retired
/// <c>Today_an_organization_audience_resolves_no_students</c>).
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class OrganizationAudienceStudentTests : IntegrationTest
{
    public OrganizationAudienceStudentTests(SqlServerFixture sql) : base(sql) { }

    private const string AudienceRoute = "/api/v1/event-audiences";

    private sealed record World(Guid SchoolId, Guid ClassificationId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var classification = TestData.NewEventClassification(school.Id);
        db.EventClassifications.Add(classification);
        await db.SaveChangesAsync();
        return new World(school.Id, classification.Id);
    }

    private async Task<Guid> SeedStudentAsync(
        Guid schoolId, string number, string? organization, string course = "BSIT", bool deleted = false)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, number, lastName: number, course: course);
        student.Organization = organization;
        student.IsDeleted = deleted;
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private async Task SeedPersonnelAsync(
        Guid schoolId, string number, string? organization, string classification = "ACAD")
    {
        await using var db = NewDbContext();
        var person = TestData.NewPersonnel(schoolId, number, classification: classification);
        person.Organization = organization;
        db.Personnel.Add(person);
        await db.SaveChangesAsync();
    }

    /// <summary>The shared world's rosters (see the class summary).</summary>
    private async Task<Dictionary<string, Guid>> SeedRostersAsync(Guid schoolId)
    {
        var ids = new Dictionary<string, Guid>
        {
            ["S-1"] = await SeedStudentAsync(schoolId, "S-1", "CICT"),
            ["S-2"] = await SeedStudentAsync(schoolId, "S-2", " cict "),
            ["S-3"] = await SeedStudentAsync(schoolId, "S-3", "CCS"),
            ["S-4"] = await SeedStudentAsync(schoolId, "S-4", null),
            ["S-5"] = await SeedStudentAsync(schoolId, "S-5", "CICT", deleted: true),
        };
        await SeedPersonnelAsync(schoolId, "EMP-A", "CICT");
        await SeedPersonnelAsync(schoolId, "EMP-B", "cict ");
        await SeedPersonnelAsync(schoolId, "EMP-C", "CCS");
        return ids;
    }

    private static async Task<Guid> CreateAudienceAsync(
        HttpClient client, Guid classificationId, string name, string type, object criteria)
    {
        var created = await client.PostAsJsonAsync(AudienceRoute, new
        {
            name,
            eventClassificationId = classificationId,
            audienceType = type,
            criteria,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private sealed record Resolved(
        int StudentCount, int PersonnelCount, IReadOnlyList<string> StudentNumbers, IReadOnlyList<string> PersonnelNumbers);

    private static async Task<Resolved> ResolveAsync(HttpClient client, Guid audienceId)
    {
        using var body = JsonDocument.Parse(
            await client.GetStringAsync($"{AudienceRoute}/{audienceId}/attendees"));
        var root = body.RootElement;
        List<string> NumbersOf(string type) => root.GetProperty("attendees").EnumerateArray()
            .Where(a => a.GetProperty("type").GetString() == type)
            .Select(a => a.GetProperty("number").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new Resolved(
            root.GetProperty("studentCount").GetInt32(), root.GetProperty("personnelCount").GetInt32(),
            NumbersOf("Student"), NumbersOf("Personnel"));
    }

    private async Task<Guid> SeedDefinitionAsync(Guid schoolId, Guid classificationId, string name, string type, AudienceCriteriaDto criteria)
    {
        await using var db = NewDbContext();
        var definition = new AudienceDefinition
        {
            SchoolId = schoolId,
            EventClassificationId = classificationId,
            Name = name,
            NameKey = AudienceText.KeyFor(name),
            AudienceType = type,
            CriteriaJson = JsonSerializer.Serialize(criteria, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            IsActive = true,
        };
        db.AudienceDefinitions.Add(definition);
        await db.SaveChangesAsync();
        return definition.Id;
    }

    /// <summary>An Open event in the classification with the Organization [CICT] definition attached.</summary>
    private async Task<(Guid EventId, Guid DefinitionId)> ArrangeEventWithOrganizationDefinitionAsync(World world)
    {
        Guid eventId;
        await using (var db = NewDbContext())
        {
            var ev = TestData.NewEvent(world.SchoolId, EventStatus.Open);
            ev.EventClassificationId = world.ClassificationId;
            db.Events.Add(ev);
            await db.SaveChangesAsync();
            eventId = ev.Id;
        }

        var definitionId = await SeedDefinitionAsync(world.SchoolId, world.ClassificationId, "CICT members",
            AudienceType.Organization, new AudienceCriteriaDto(Organizations: ["CICT"]));

        await using (var db = NewDbContext())
        {
            var attached = await EventsOn(db).AttachAudienceAsync(
                eventId, new EventAudienceRequest(null, null, [definitionId]));
            Assert.Equal(EventWriteOutcome.Saved, attached.Outcome);
        }

        return (eventId, definitionId);
    }

    // ==================================================================================== resolve

    /// <summary>
    /// An Organization [CICT] audience resolves S-1 and S-2 (case and whitespace variants of the key) and
    /// EMP-A and EMP-B; not the CCS student, not the student with no organization, not the soft-deleted
    /// CICT student, not the CCS person.
    /// </summary>
    [Fact]
    public async Task An_organization_audience_resolves_students_and_personnel_across_case_and_whitespace()
    {
        var world = await ArrangeAsync();
        await SeedRostersAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var id = await CreateAudienceAsync(client, world.ClassificationId, "Members of CICT",
            AudienceType.Organization, new { scope = AudienceScope.Both, organizations = new[] { "CICT" } });

        var resolved = await ResolveAsync(client, id);

        Assert.Equal(["S-1", "S-2"], resolved.StudentNumbers);
        Assert.Equal(2, resolved.StudentCount);
        Assert.Equal(["EMP-A", "EMP-B"], resolved.PersonnelNumbers);
        Assert.Equal(2, resolved.PersonnelCount);
    }

    /// <summary>
    /// Through the real event path (ADR-007 D-70): an event whose ONLY audience is the Organization
    /// definition expects its two students, and the two personnel appear only as advisory. The audience
    /// read, the summary and the roster agree on the denominator.
    /// </summary>
    [Fact]
    public async Task An_event_with_an_organization_definition_counts_its_students_and_reports_personnel_as_advisory()
    {
        var world = await ArrangeAsync();
        await SeedRostersAsync(world.SchoolId);
        var (eventId, _) = await ArrangeEventWithOrganizationDefinitionAsync(world);

        await using var read = NewDbContext();
        var audience = await EventsOn(read).GetAudienceAsync(eventId);

        Assert.Equal(2, audience!.Expected);               // S-1 and S-2: students are the denominator
        Assert.Equal(2, audience.AdvisoryPersonnelCount);  // EMP-A and EMP-B: surfaced, not counted
        Assert.Equal(2, Assert.Single(audience.Definitions).PersonnelCount);

        await using var summaryDb = NewDbContext();
        Assert.Equal(2, (await EventsOn(summaryDb).GetSummaryAsync(eventId))!.Expected);

        await using var rosterDb = NewDbContext();
        Assert.Equal(2, (await EventsOn(rosterDb).GetRosterAsync(eventId))!.Expected);
    }

    /// <summary>
    /// R1 (JJ accepted): in Custom, <c>organizations</c> is a criterion on BOTH rosters, ANDed with the other
    /// student criteria. Programs [BSIT] AND Organizations [CICT] keeps only the BSIT students in CICT, and
    /// drops the BSIT student in CCS, the BSCS student in CICT and the BSIT student with none.
    /// </summary>
    [Fact]
    public async Task A_custom_organization_criterion_narrows_students_and_personnel()
    {
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "S-IN-1", "CICT", course: "BSIT");
        await SeedStudentAsync(world.SchoolId, "S-IN-2", "cict  ", course: "BSIT");
        await SeedStudentAsync(world.SchoolId, "S-WRONG-ORG", "CCS", course: "BSIT");
        await SeedStudentAsync(world.SchoolId, "S-WRONG-PROGRAM", "CICT", course: "BSCS");
        await SeedStudentAsync(world.SchoolId, "S-NO-ORG", null, course: "BSIT");
        await SeedPersonnelAsync(world.SchoolId, "EMP-A", "CICT");
        await SeedPersonnelAsync(world.SchoolId, "EMP-C", "CCS");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var id = await CreateAudienceAsync(client, world.ClassificationId, "BSIT in CICT",
            AudienceType.Custom, new { scope = AudienceScope.Both, programs = new[] { "BSIT" }, organizations = new[] { "CICT" } });

        var resolved = await ResolveAsync(client, id);

        Assert.Equal(["S-IN-1", "S-IN-2"], resolved.StudentNumbers);
        Assert.Equal(["EMP-A"], resolved.PersonnelNumbers);
    }

    /// <summary>
    /// R2 (JJ accepted): a Custom definition whose other criteria are personnel-only (here a
    /// classification) plus an organization ALSO resolves the students of that organization alone — as
    /// <c>departments</c> + <c>classifications</c> resolves students by department alone today.
    /// </summary>
    [Fact]
    public async Task A_custom_with_personnel_only_criteria_plus_organization_reaches_students_by_organization()
    {
        var world = await ArrangeAsync();
        await SeedRostersAsync(world.SchoolId);
        await SeedPersonnelAsync(world.SchoolId, "EMP-D", "CICT", classification: "NAP"); // right org, wrong class

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var id = await CreateAudienceAsync(client, world.ClassificationId, "ACAD in CICT",
            AudienceType.Custom, new { scope = AudienceScope.Both, classifications = new[] { "ACAD" }, organizations = new[] { "CICT" } });

        var resolved = await ResolveAsync(client, id);

        Assert.Equal(["EMP-A", "EMP-B"], resolved.PersonnelNumbers);   // both criteria on personnel
        Assert.Equal(["S-1", "S-2"], resolved.StudentNumbers);          // students: by organization alone
    }

    /// <summary>
    /// A soft-deleted student never counts: S-5 carries "CICT" and is excluded from the resolve and the
    /// denominator, and deleting a counted student through the service takes them out of both too.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_student_is_not_counted()
    {
        var world = await ArrangeAsync();
        var students = await SeedRostersAsync(world.SchoolId);
        var (eventId, _) = await ArrangeEventWithOrganizationDefinitionAsync(world);

        await using (var read = NewDbContext())
            Assert.Equal(2, (await EventsOn(read).GetSummaryAsync(eventId))!.Expected); // S-5 already excluded

        await using (var db = NewDbContext())
        {
            var deleted = await StudentsOn(db).DeleteAsync(students["S-1"]);
            Assert.Equal(StudentWriteOutcome.Saved, deleted.Outcome);
        }

        await using var after = NewDbContext();
        Assert.Equal(1, (await EventsOn(after).GetSummaryAsync(eventId))!.Expected);
    }

    // ===================================================================================== options

    /// <summary>
    /// <c>GET /event-audiences/options</c> merges the two rosters' organizations through the one display
    /// fold (D-78): "CICT" (students, one stored as " cict ") and "cict" (personnel) are one entry shown
    /// as the ordinally-first spelling; "BSN Society" exists only on a student and "SSG" only on
    /// personnel, so each source is proven present; a soft-deleted student's organization is not offered.
    /// </summary>
    [Fact]
    public async Task The_options_organizations_merge_students_and_personnel_by_match_key()
    {
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "S-1", "CICT");
        await SeedStudentAsync(world.SchoolId, "S-2", " cict ");
        await SeedStudentAsync(world.SchoolId, "S-3", "BSN  Society");          // student-only, internal double space
        await SeedStudentAsync(world.SchoolId, "S-4", "Ghost Club", deleted: true);
        await SeedPersonnelAsync(world.SchoolId, "EMP-A", "cict");
        await SeedPersonnelAsync(world.SchoolId, "EMP-B", "SSG ");               // personnel-only
        await SeedPersonnelAsync(world.SchoolId, "EMP-C", "ssg");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var options = JsonDocument.Parse(await client.GetStringAsync($"{AudienceRoute}/options"));
        var organizations = options.RootElement.GetProperty("organizations").EnumerateArray()
            .Select(e => e.GetString()!).ToList();

        Assert.Equal(["BSN Society", "CICT", "SSG"], organizations);
    }

    /// <summary>
    /// JJ's ruling: <c>GET /personnel/organizations</c> feeds the personnel form's suggestions and stays
    /// sourced from personnel only — a student's organization must not leak into it. It also shows one
    /// deterministic spelling for case/whitespace variants ("SSG " and "ssg" list as "SSG").
    /// A negative control against the old code (SQL <c>Distinct()</c> before the in-memory pass) is only
    /// probabilistic: which spelling SQL <c>DISTINCT</c> keeps depends on the query plan, so this can pass
    /// against the old code on a given run.
    /// </summary>
    [Fact]
    public async Task Personnel_organizations_list_stays_personnel_only()
    {
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "S-1", "Student Only Club");
        await SeedStudentAsync(world.SchoolId, "S-2", "CICT");
        await SeedPersonnelAsync(world.SchoolId, "EMP-A", "CICT");
        await SeedPersonnelAsync(world.SchoolId, "EMP-B", "SSG ");
        await SeedPersonnelAsync(world.SchoolId, "EMP-C", "ssg");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var organizations = await client.GetFromJsonAsync<string[]>("/api/v1/personnel/organizations");

        // "SSG " / "ssg" are two spellings of one organization. The list is deduplicated in memory only
        // (OrganizationText.DistinctForDisplay keeps the ordinally-first spelling, "SSG"), so which row the
        // database returns first cannot change the answer.
        Assert.NotNull(organizations);
        Assert.Equal(["CICT", "SSG"], organizations);
    }

    // =============================================================================== event filter

    /// <summary>
    /// D-82: <c>AudienceField.All</c>, the D-50 event filter, is unchanged — Organization is not a sixth
    /// field. Pinned on the registry and over the wire: a filter row naming it is refused as
    /// <c>UnknownAudienceField</c> in any casing, never a row that quietly stops applying.
    /// </summary>
    [Theory]
    [InlineData("Organization")]
    [InlineData("organization")]
    public async Task Organization_is_not_an_event_filter_field(string field)
    {
        Assert.Equal(["College", "Program", "YearLevel", "Section", "Course"], AudienceField.All);
        Assert.False(AudienceField.TryNormalize(field, out _));

        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.PostAsJsonAsync("/api/v1/events/audience/resolve", new
        {
            termId = (Guid?)null,
            filters = new[] { new { field, values = new[] { "CICT" } } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(AudienceResolveOutcome.UnknownAudienceField.ToString(), body.RootElement.GetProperty("code").GetString());
    }

    // ============================================================================ freeze, manifest

    /// <summary>
    /// Closing freezes the students an Organization definition reached: an <c>Absent</c> row for each (none
    /// tapped), none for personnel. The freeze resolves WITH soft-deleted students (ADR-003 D-15, restated
    /// by ADR-007), so the world's soft-deleted CICT student S-5 is frozen too although the live
    /// denominator excluded it: a student deleted before the close is not retroactively dropped from a past
    /// event. AFTER the close, a student moves into the organization and another leaves it, and the frozen
    /// roster and denominator do not move.
    /// </summary>
    [Fact]
    public async Task Closing_freezes_the_students_an_organization_definition_reaches()
    {
        var world = await ArrangeAsync();
        var students = await SeedRostersAsync(world.SchoolId);
        var (eventId, _) = await ArrangeEventWithOrganizationDefinitionAsync(world);

        await using (var db = NewDbContext())
        {
            var closed = await EventsOn(db).ChangeStatusAsync(eventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, closed.Outcome);
        }

        await using (var read = NewDbContext())
        {
            var frozen = await read.AttendanceRecords.AsNoTracking().Where(a => a.EventId == eventId).ToListAsync();
            var names = students.ToDictionary(kv => kv.Value, kv => kv.Key);
            Assert.Equal(["S-1", "S-2", "S-5"], frozen.Select(a => names[Assert.NotNull(a.StudentId)]).Order().ToList());
            Assert.All(frozen, a => Assert.Equal(AttendanceStatus.Absent, a.Status));
        }

        // A later import: one more student joins the organization, one leaves it.
        await SeedStudentAsync(world.SchoolId, "S-LATE", "CICT");
        await using (var db = NewDbContext())
        {
            var left = await db.Students.SingleAsync(s => s.Id == students["S-1"]);
            left.Organization = "CCS";
            await db.SaveChangesAsync();
        }

        await using var after = NewDbContext();
        var records = await after.AttendanceRecords.AsNoTracking().Where(a => a.EventId == eventId).ToListAsync();
        Assert.Equal(3, records.Count);                                   // S-LATE added nothing
        Assert.Contains(records, r => r.StudentId == students["S-1"]);   // still counted though they left
        Assert.Equal(3, (await EventsOn(after).GetSummaryAsync(eventId))!.Expected);
    }

    /// <summary>
    /// The device's manifest lists the students the definition reaches — S-1 and S-2, by id — and neither
    /// the other students nor any personnel.
    /// </summary>
    [Fact]
    public async Task An_organization_definition_puts_its_students_in_the_manifest()
    {
        var world = await ArrangeAsync();
        var students = await SeedRostersAsync(world.SchoolId);
        var (eventId, _) = await ArrangeEventWithOrganizationDefinitionAsync(world);
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var device = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await device.GetAsync($"/api/v1/events/{eventId}/manifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var attendees = body.RootElement.GetProperty("attendees").EnumerateArray()
            .Select(a => a.GetProperty("studentId").GetGuid()).ToHashSet();

        Assert.Equal(new HashSet<Guid> { students["S-1"], students["S-2"] }, attendees);
    }
}
