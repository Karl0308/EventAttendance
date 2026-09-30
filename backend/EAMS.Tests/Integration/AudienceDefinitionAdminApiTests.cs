using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The Event Audience master over real HTTP — create, update, deactivate, delete, the classification and
/// criteria guards, and the resolution of a definition to live attendees. HTTP tests rather than service
/// tests for the reason <c>EventClassificationAdminApiTests</c> records: every promise the module makes is
/// a statement about the wire (a duplicate is a 409 not a 500, a bad criteria set is a 400, resolution
/// counts the roster as it stands), and every destructive assertion counts rows rather than trusting the
/// body.
///
/// <para>
/// The host is <see cref="EamsApiFactory"/>, which runs as Production and therefore does not seed — so
/// every row these tests reason about is one they wrote, under the single arranged school.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AudienceDefinitionAdminApiTests : IntegrationTest
{
    public AudienceDefinitionAdminApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/event-audiences";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> ArrangeClassificationAsync(string name = "Institutional Events", bool isActive = true)
    {
        await using var db = NewDbContext();
        var schoolId = await db.Schools.Select(s => s.Id).FirstAsync();
        var row = TestData.NewEventClassification(schoolId, name, isActive: isActive);
        db.EventClassifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task ArrangeRosterAsync()
    {
        await using var db = NewDbContext();
        var schoolId = await db.Schools.Select(s => s.Id).FirstAsync();

        db.Students.Add(TestData.NewStudent(schoolId, "2023-0001", lastName: "Aquino", course: "BSIT"));
        db.Students.Add(TestData.NewStudent(schoolId, "2023-0002", lastName: "Bautista", course: "BSIT"));
        db.Students.Add(TestData.NewStudent(schoolId, "2023-0003", lastName: "Cruz", course: "BSCS"));

        var faculty = TestData.NewPersonnel(schoolId, "EMP-0001", lastName: "Delgado", department: "BSIT");
        faculty.Organization = "Faculty Union";
        var registrar = TestData.NewPersonnel(schoolId, "EMP-0002", lastName: "Enriquez", department: "Registrar");
        db.Personnel.Add(faculty);
        db.Personnel.Add(registrar);

        await db.SaveChangesAsync();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(
            string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()),
            "The failure body carried no traceId. It is a correct status code and an unsupportable one.");

        return body.RootElement.GetProperty(AudienceDefinitionsController.ErrorCodeProperty).GetString();
    }

    [Fact]
    public async Task An_administrator_can_create_update_deactivate_and_delete_an_audience()
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(Route, new
        {
            name = "All BSIT Students",
            eventClassificationId = classificationId,
            audienceType = AudienceType.Program,
            criteria = new { programs = new[] { "BSIT" } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("id").GetGuid();
        Assert.True(createdBody.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal(classificationId, createdBody.RootElement.GetProperty("eventClassificationId").GetGuid());
        Assert.Equal("Institutional Events", createdBody.RootElement.GetProperty("eventClassificationName").GetString());
        Assert.Equal(AudienceType.Program, createdBody.RootElement.GetProperty("audienceType").GetString());
        Assert.Equal("BSIT",
            createdBody.RootElement.GetProperty("criteria").GetProperty("programs")[0].GetString());

        Assert.NotNull(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(created.Headers.Location)).StatusCode);

        var updated = await client.PutAsJsonAsync($"{Route}/{id}", new
        {
            name = "All BSIT & BSCS Students",
            eventClassificationId = classificationId,
            audienceType = AudienceType.Program,
            criteria = new { programs = new[] { "BSIT", "BSCS" } },
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var updatedBody = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal("All BSIT & BSCS Students", updatedBody.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, updatedBody.RootElement.GetProperty("criteria").GetProperty("programs").GetArrayLength());

        var deactivated = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        using var deBody = JsonDocument.Parse(await deactivated.Content.ReadAsStringAsync());
        Assert.False(deBody.RootElement.GetProperty("isActive").GetBoolean());

        var deleted = await client.DeleteAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        await using var read = NewDbContext();
        Assert.False(await read.AudienceDefinitions.AsNoTracking().AnyAsync(d => d.Id == id));
    }

    [Fact]
    public async Task A_second_spelling_of_one_name_is_refused_as_a_conflict()
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        object Body(string name) => new
        {
            name,
            eventClassificationId = classificationId,
            audienceType = AudienceType.UniversityWide,
            criteria = new { scope = AudienceScope.Both },
        };

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Route, Body("Everyone"))).StatusCode);

        // Same name, different casing — the key normalizes to the same value.
        var clash = await client.PostAsJsonAsync(Route, Body("EVERYONE"));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(nameof(AudienceWriteOutcome.NameExists), await ErrorCodeAsync(clash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Padded")]
    public async Task A_name_that_breaks_a_column_rule_is_refused(string name)
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(Route, new
        {
            name,
            eventClassificationId = classificationId,
            audienceType = AudienceType.UniversityWide,
            criteria = new { scope = AudienceScope.Both },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(AudienceWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_audience_type_is_refused()
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(Route, new
        {
            name = "Nonsense",
            eventClassificationId = classificationId,
            audienceType = "WholeGalaxy",
            criteria = new { },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(AudienceWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Criteria_that_do_not_fit_the_type_are_refused()
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        // Department type with no departments.
        var response = await client.PostAsJsonAsync(Route, new
        {
            name = "Empty department set",
            eventClassificationId = classificationId,
            audienceType = AudienceType.Department,
            criteria = new { },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(AudienceWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_classification_is_refused_as_a_conflict()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(Route, new
        {
            name = "Orphan",
            eventClassificationId = Guid.NewGuid(),
            audienceType = AudienceType.UniversityWide,
            criteria = new { scope = AudienceScope.Both },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(AudienceWriteOutcome.ClassificationUnavailable), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_deactivated_classification_cannot_be_assigned()
    {
        await ArrangeSchoolAsync();
        var retired = await ArrangeClassificationAsync("Retired Category", isActive: false);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(Route, new
        {
            name = "Under a retired category",
            eventClassificationId = retired,
            audienceType = AudienceType.UniversityWide,
            criteria = new { scope = AudienceScope.Both },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(AudienceWriteOutcome.ClassificationUnavailable), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_omitted_isActive_is_refused_rather_than_binding_to_false()
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(Route, new
        {
            name = "Everyone",
            eventClassificationId = classificationId,
            audienceType = AudienceType.UniversityWide,
            criteria = new { scope = AudienceScope.Both },
        });
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        var response = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(AudienceWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        Assert.True(
            (await read.AudienceDefinitions.AsNoTracking().SingleAsync(d => d.Id == id)).IsActive,
            "An omitted isActive left the audience deactivated — the whole hazard the 400 prevents.");
    }

    [Fact]
    public async Task An_unknown_id_is_a_404_on_every_verb()
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();
        var missing = Guid.NewGuid();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{missing}/attendees")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PatchAsJsonAsync($"{Route}/{missing}/active", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}", new
            {
                name = "X",
                eventClassificationId = classificationId,
                audienceType = AudienceType.UniversityWide,
                criteria = new { scope = AudienceScope.Both },
            })).StatusCode);
    }

    [Fact]
    public async Task A_department_audience_resolves_matching_students_and_personnel()
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();
        await ArrangeRosterAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(Route, new
        {
            name = "BSIT department",
            eventClassificationId = classificationId,
            audienceType = AudienceType.Department,
            criteria = new { departments = new[] { "BSIT" } },
        });
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        using var resolved = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}/{id}/attendees")).Content.ReadAsStringAsync());

        // Two BSIT students (Aquino, Bautista) + one BSIT-department employee (Delgado); BSCS + Registrar excluded.
        Assert.Equal(2, resolved.RootElement.GetProperty("studentCount").GetInt32());
        Assert.Equal(1, resolved.RootElement.GetProperty("personnelCount").GetInt32());
        Assert.Equal(3, resolved.RootElement.GetProperty("attendees").GetArrayLength());
    }

    [Fact]
    public async Task A_university_wide_students_scope_resolves_only_students()
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();
        await ArrangeRosterAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(Route, new
        {
            name = "All students",
            eventClassificationId = classificationId,
            audienceType = AudienceType.UniversityWide,
            criteria = new { scope = AudienceScope.Students },
        });
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        using var resolved = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}/{id}/attendees")).Content.ReadAsStringAsync());

        Assert.Equal(3, resolved.RootElement.GetProperty("studentCount").GetInt32());
        Assert.Equal(0, resolved.RootElement.GetProperty("personnelCount").GetInt32());
    }

    [Fact]
    public async Task Specific_individuals_resolves_exactly_the_named_people()
    {
        await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync();
        await ArrangeRosterAsync();

        Guid studentId, personnelId;
        await using (var db = NewDbContext())
        {
            studentId = await db.Students.Where(s => s.StudentNumber == "2023-0001").Select(s => s.Id).FirstAsync();
            personnelId = await db.Personnel.Where(p => p.PersonnelNumber == "EMP-0002").Select(p => p.Id).FirstAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(Route, new
        {
            name = "Two named people",
            eventClassificationId = classificationId,
            audienceType = AudienceType.SpecificIndividuals,
            criteria = new { studentIds = new[] { studentId }, personnelIds = new[] { personnelId } },
        });
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        using var resolved = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}/{id}/attendees")).Content.ReadAsStringAsync());

        Assert.Equal(1, resolved.RootElement.GetProperty("studentCount").GetInt32());
        Assert.Equal(1, resolved.RootElement.GetProperty("personnelCount").GetInt32());
    }

    [Fact]
    public async Task The_options_read_returns_the_distinct_academic_community_values()
    {
        await ArrangeSchoolAsync();
        await ArrangeClassificationAsync();
        await ArrangeRosterAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        using var options = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}/options")).Content.ReadAsStringAsync());

        var programs = options.RootElement.GetProperty("programs").EnumerateArray()
            .Select(x => x.GetString()).ToList();
        Assert.Contains("BSIT", programs);
        Assert.Contains("BSCS", programs);

        var departments = options.RootElement.GetProperty("departments").EnumerateArray()
            .Select(x => x.GetString()).ToList();
        // Departments span both rosters — a student's course and a personnel department.
        Assert.Contains("BSIT", departments);
        Assert.Contains("Registrar", departments);
    }
}
