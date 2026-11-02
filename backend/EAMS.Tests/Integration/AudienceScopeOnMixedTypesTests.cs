using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// BACKOFFICE Task 5 — "who it covers" on the mixed audience types (Department, Organization, Custom): the
/// existing criteria <c>scope</c> (Students / Employees / Both) is REQUIRED on create and edit, and the one
/// matcher (<c>AudienceResolution.BuildQueries</c>) applies it as a mask that only ever drops a side.
/// Stored definitions without a scope resolve as Both.
///
/// <para>
/// <b>The shared world.</b> Students <c>S-1</c> and <c>S-2</c>: course BSIT, organization CICT. Personnel
/// <c>EMP-A</c> and <c>EMP-B</c>: department BSIT, organization CICT, classification ACAD. So a Department
/// [BSIT], an Organization [CICT] and a Custom [organizations CICT] audience each reach two of each — a zero
/// on either side can only mean "the scope dropped it", never "nobody matched".
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AudienceScopeOnMixedTypesTests : IntegrationTest
{
    public AudienceScopeOnMixedTypesTests(SqlServerFixture sql) : base(sql) { }

    private const string AudienceRoute = "/api/v1/event-audiences";
    private const string ScopeRefusalText = "who this audience covers";
    private const string CustomSideRefusalText = "A custom audience covering";

    private sealed record World(Guid SchoolId, Guid ClassificationId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var classification = TestData.NewEventClassification(school.Id);
        db.EventClassifications.Add(classification);

        foreach (var number in new[] { "S-1", "S-2" })
        {
            var student = TestData.NewStudent(school.Id, number, lastName: number, course: "BSIT");
            student.Organization = "CICT";
            db.Students.Add(student);
        }

        foreach (var number in new[] { "EMP-A", "EMP-B" })
        {
            var person = TestData.NewPersonnel(school.Id, number, lastName: number, classification: "ACAD", department: "BSIT");
            person.Organization = "CICT";
            db.Personnel.Add(person);
        }

        await db.SaveChangesAsync();
        return new World(school.Id, classification.Id);
    }

    /// <summary>A criteria body that satisfies the type's own criterion rule; <paramref name="scope"/> null omits the key.</summary>
    private static Dictionary<string, object> CriteriaFor(string type, string? scope)
    {
        var criteria = type switch
        {
            AudienceType.Department => new Dictionary<string, object> { ["departments"] = new[] { "BSIT" } },
            AudienceType.Organization or AudienceType.Custom =>
                new Dictionary<string, object> { ["organizations"] = new[] { "CICT" } },
            AudienceType.Program => new Dictionary<string, object> { ["programs"] = new[] { "BSIT" } },
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No criteria fixture for this type."),
        };
        if (scope is not null) criteria["scope"] = scope;
        return criteria;
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, World world, string name, string type, object criteria) =>
        client.PostAsJsonAsync(AudienceRoute, new
        {
            name,
            eventClassificationId = world.ClassificationId,
            audienceType = type,
            criteria,
        });

    private static async Task<Guid> CreateAsync(
        HttpClient client, World world, string name, string type, object criteria)
    {
        var created = await PostAsync(client, world, name, type, criteria);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Asserts a 400 ValidationFailed whose detail carries <paramref name="detailText"/>.</summary>
    private static async Task AssertRefusedAsync(HttpResponseMessage response, string detailText)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(nameof(AudienceWriteOutcome.ValidationFailed),
            body.RootElement.GetProperty(AudienceDefinitionsController.ErrorCodeProperty).GetString());
        Assert.Contains(detailText, body.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    private sealed record Resolved(int StudentCount, int PersonnelCount);

    private static async Task<Resolved> ResolveAsync(HttpClient client, Guid audienceId)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync($"{AudienceRoute}/{audienceId}/attendees"));
        return new Resolved(
            body.RootElement.GetProperty("studentCount").GetInt32(),
            body.RootElement.GetProperty("personnelCount").GetInt32());
    }

    /// <summary>A definition written straight to the table, as rows stored before the scope was required are.</summary>
    private async Task<Guid> SeedDefinitionAsync(World world, string name, string type, AudienceCriteriaDto criteria)
    {
        await using var db = NewDbContext();
        var definition = new AudienceDefinition
        {
            SchoolId = world.SchoolId,
            EventClassificationId = world.ClassificationId,
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

    private static AudienceCriteriaDto StoredCriteriaFor(string type) => type switch
    {
        AudienceType.Department => new AudienceCriteriaDto(Departments: ["BSIT"]),
        _ => new AudienceCriteriaDto(Organizations: ["CICT"]),
    };

    // ============================================================================== validation

    [Theory]
    [InlineData(AudienceType.Department)]
    [InlineData(AudienceType.Organization)]
    [InlineData(AudienceType.Custom)]
    public async Task A_mixed_type_without_a_scope_is_refused_on_create(string type)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await PostAsync(client, world, $"No scope {type}", type, CriteriaFor(type, scope: null));

        await AssertRefusedAsync(response, ScopeRefusalText);
    }

    [Theory]
    [InlineData(AudienceType.Department)]
    [InlineData(AudienceType.Organization)]
    [InlineData(AudienceType.Custom)]
    public async Task A_mixed_type_without_a_scope_is_refused_on_update(string type)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var name = $"Scoped {type}";
        var id = await CreateAsync(client, world, name, type, CriteriaFor(type, AudienceScope.Students));

        var response = await client.PutAsJsonAsync($"{AudienceRoute}/{id}", new
        {
            name,
            eventClassificationId = world.ClassificationId,
            audienceType = type,
            criteria = CriteriaFor(type, scope: null),
        });

        await AssertRefusedAsync(response, ScopeRefusalText);

        // The refused edit left the stored scope alone.
        using var read = JsonDocument.Parse(await client.GetStringAsync($"{AudienceRoute}/{id}"));
        Assert.Equal(AudienceScope.Students, read.RootElement.GetProperty("criteria").GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData("Everyone")]
    [InlineData("Student")]
    [InlineData("")]
    public async Task A_mixed_type_with_an_unknown_scope_is_refused(string scope)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await PostAsync(client, world, "Unknown scope", AudienceType.Organization,
            CriteriaFor(AudienceType.Organization, scope));

        await AssertRefusedAsync(response, ScopeRefusalText);
    }

    /// <summary>JJ ruling 6: a Custom scope that keeps only a side with no criterion would resolve nobody.</summary>
    [Theory]
    [InlineData(AudienceScope.Students, "classifications")]
    [InlineData(AudienceScope.Students, "personnelIds")]
    [InlineData(AudienceScope.Employees, "programs")]
    [InlineData(AudienceScope.Employees, "yearLevels")]
    [InlineData(AudienceScope.Employees, "sections")]
    [InlineData(AudienceScope.Employees, "studentIds")]
    public async Task A_custom_scope_that_leaves_no_side_is_refused(string scope, string onlyCriterion)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        object values = onlyCriterion.EndsWith("Ids", StringComparison.Ordinal)
            ? new[] { Guid.NewGuid() }
            : new[] { "X" };
        var criteria = new Dictionary<string, object> { ["scope"] = scope, [onlyCriterion] = values };

        var response = await PostAsync(client, world, "Empty side", AudienceType.Custom, criteria);

        await AssertRefusedAsync(response, CustomSideRefusalText);
    }

    [Fact]
    public async Task A_program_audience_still_saves_without_a_scope()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var id = await CreateAsync(client, world, "BSIT program", AudienceType.Program,
            CriteriaFor(AudienceType.Program, scope: null));

        Assert.Equal(new Resolved(2, 0), await ResolveAsync(client, id));
    }

    // ============================================================================== resolution

    [Fact]
    public async Task An_organization_audience_scoped_to_students_resolves_no_personnel()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var id = await CreateAsync(client, world, "CICT students", AudienceType.Organization,
            CriteriaFor(AudienceType.Organization, AudienceScope.Students));

        Assert.Equal(new Resolved(2, 0), await ResolveAsync(client, id));
    }

    [Fact]
    public async Task An_organization_audience_scoped_to_employees_resolves_no_students()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var id = await CreateAsync(client, world, "CICT employees", AudienceType.Organization,
            CriteriaFor(AudienceType.Organization, AudienceScope.Employees));

        Assert.Equal(new Resolved(0, 2), await ResolveAsync(client, id));
    }

    [Fact]
    public async Task A_department_audience_scoped_to_students_resolves_no_personnel()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var id = await CreateAsync(client, world, "BSIT students", AudienceType.Department,
            CriteriaFor(AudienceType.Department, AudienceScope.Students));

        Assert.Equal(new Resolved(2, 0), await ResolveAsync(client, id));
    }

    /// <summary>Organizations + classifications reach both sides under Both; Students keeps only the student one.</summary>
    [Fact]
    public async Task A_custom_audience_scoped_to_students_drops_the_personnel_side()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var both = await CreateAsync(client, world, "ACAD in CICT, both", AudienceType.Custom, new
        {
            scope = AudienceScope.Both, organizations = new[] { "CICT" }, classifications = new[] { "ACAD" },
        });
        var students = await CreateAsync(client, world, "ACAD in CICT, students", AudienceType.Custom, new
        {
            scope = AudienceScope.Students, organizations = new[] { "CICT" }, classifications = new[] { "ACAD" },
        });

        Assert.Equal(new Resolved(2, 2), await ResolveAsync(client, both));
        Assert.Equal(new Resolved(2, 0), await ResolveAsync(client, students));
    }

    [Fact]
    public async Task Both_never_widens_a_custom_side_without_a_criterion()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var personnelOnly = await CreateAsync(client, world, "ACAD, both", AudienceType.Custom, new
        {
            scope = AudienceScope.Both, classifications = new[] { "ACAD" },
        });
        var studentsOnly = await CreateAsync(client, world, "BSIT program, both", AudienceType.Custom, new
        {
            scope = AudienceScope.Both, programs = new[] { "BSIT" },
        });

        Assert.Equal(new Resolved(0, 2), await ResolveAsync(client, personnelOnly));
        Assert.Equal(new Resolved(2, 0), await ResolveAsync(client, studentsOnly));
    }

    /// <summary>JJ ruling 4: rows stored before the scope was required resolve as Both — no behaviour change.</summary>
    [Theory]
    [InlineData(AudienceType.Department)]
    [InlineData(AudienceType.Organization)]
    [InlineData(AudienceType.Custom)]
    public async Task A_stored_mixed_definition_without_a_scope_resolves_as_both(string type)
    {
        var world = await ArrangeAsync();
        var id = await SeedDefinitionAsync(world, $"Legacy {type}", type, StoredCriteriaFor(type));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal(new Resolved(2, 2), await ResolveAsync(client, id));
    }

    /// <summary>The read does not invent a scope — the editor sees none and must make the admin choose.</summary>
    [Fact]
    public async Task A_stored_mixed_definition_without_a_scope_is_read_back_with_a_null_scope()
    {
        var world = await ArrangeAsync();
        var id = await SeedDefinitionAsync(world, "Legacy organization", AudienceType.Organization,
            StoredCriteriaFor(AudienceType.Organization));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var read = JsonDocument.Parse(await client.GetStringAsync($"{AudienceRoute}/{id}"));
        var criteria = read.RootElement.GetProperty("criteria");
        Assert.True(
            !criteria.TryGetProperty("scope", out var scope) || scope.ValueKind == JsonValueKind.Null,
            $"Expected no scope, read back {criteria}.");
    }

    /// <summary>
    /// Through the event path: an Employees-scoped definition adds only its personnel to the denominator
    /// (ADR-015 D-102), and its mask still keeps every student out.
    /// </summary>
    [Fact]
    public async Task An_employees_scoped_definition_adds_only_personnel_to_the_event_denominator()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var definitionId = await CreateAsync(client, world, "CICT employees", AudienceType.Organization,
            CriteriaFor(AudienceType.Organization, AudienceScope.Employees));

        Guid eventId;
        await using (var db = NewDbContext())
        {
            var ev = TestData.NewEvent(world.SchoolId, EventStatus.Open);
            ev.EventClassificationId = world.ClassificationId;
            db.Events.Add(ev);
            await db.SaveChangesAsync();
            eventId = ev.Id;
        }

        await using (var db = NewDbContext())
        {
            var attached = await EventsOn(db).AttachAudienceAsync(
                eventId, new EventAudienceRequest(null, null, [definitionId]));
            Assert.Equal(EventWriteOutcome.Saved, attached.Outcome);
        }

        await using var read = NewDbContext();
        var audience = await EventsOn(read).GetAudienceAsync(eventId);
        Assert.Equal((2, 0, 2), (audience!.Expected, audience.ExpectedStudents, audience.ExpectedPersonnel));

        await using var summaryDb = NewDbContext();
        Assert.Equal(2, (await EventsOn(summaryDb).GetSummaryAsync(eventId))!.Expected);
    }
}
