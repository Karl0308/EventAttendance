using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Organization normalization over real HTTP + SQL Server (QA ruling MDVault #543, Option A): Organization
/// stays free text, but a whitespace/case variant must never silently drop a person from an Event Audience.
/// Two halves are exercised here — the storage form (trim + collapse internal whitespace, casing preserved,
/// on every write path) and case-insensitive matching/dedupe — against a real database rather than EF
/// InMemory, because the collation behaviour this guards (case-insensitivity, trailing-space padding, and
/// the leading-space gap those leave) only exists on SQL Server.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class OrganizationNormalizationApiTests : IntegrationTest
{
    public OrganizationNormalizationApiTests(SqlServerFixture sql) : base(sql) { }

    private const string PersonnelRoute = "/api/v1/personnel";
    private const string OrganizationsRoute = "/api/v1/personnel/organizations";
    private const string AudienceRoute = "/api/v1/event-audiences";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> ArrangeClassificationAsync(Guid schoolId)
    {
        await using var db = NewDbContext();
        var row = TestData.NewEventClassification(schoolId);
        db.EventClassifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Writes a live personnel row with a stored Organization exactly as given — bypassing the
    /// service so a raw (possibly legacy / un-normalized) value can be placed under test directly.</summary>
    private async Task SeedRawPersonnelAsync(Guid schoolId, string number, string? organization)
    {
        await using var db = NewDbContext();
        var person = TestData.NewPersonnel(schoolId, number);
        person.Organization = organization;
        db.Personnel.Add(person);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateOrganizationAudienceAsync(
        HttpClient client, Guid classificationId, string organizationCriterion)
    {
        var created = await client.PostAsJsonAsync(AudienceRoute, new
        {
            name = $"Members of {organizationCriterion}",
            eventClassificationId = classificationId,
            audienceType = AudienceType.Organization,
            criteria = new { organizations = new[] { organizationCriterion } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<int> PersonnelCountAsync(HttpClient client, Guid audienceId)
    {
        using var resolved = JsonDocument.Parse(
            await (await client.GetAsync($"{AudienceRoute}/{audienceId}/attendees")).Content.ReadAsStringAsync());
        return resolved.RootElement.GetProperty("personnelCount").GetInt32();
    }

    // ------------------------------------------------------------------- AC1: audience matching

    /// <summary>
    /// AC1 — a person stored as "CICT " (trailing space), one as "cict" (different case) and one as " CICT"
    /// (leading space) all resolve into an audience whose Organization criterion is "CICT"; an unrelated
    /// org does not. The leading-space variant is the one that proves the normalization does real work:
    /// SQL Server's default collation would match the trailing-space and the case variants even without it,
    /// but a leading space is significant, so only a trim makes " CICT" resolve (the negative control below
    /// drops exactly that person).
    /// </summary>
    [Fact]
    public async Task Whitespace_and_case_variants_all_resolve_into_the_canonical_organization_audience()
    {
        var schoolId = await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync(schoolId);

        await SeedRawPersonnelAsync(schoolId, "EMP-1", "CICT ");   // trailing space
        await SeedRawPersonnelAsync(schoolId, "EMP-2", "cict");    // different case
        await SeedRawPersonnelAsync(schoolId, "EMP-3", " CICT");   // leading space (collation-significant)
        await SeedRawPersonnelAsync(schoolId, "EMP-4", "CCS");     // unrelated — must not resolve

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var audienceId = await CreateOrganizationAudienceAsync(client, classificationId, "CICT");

        Assert.Equal(3, await PersonnelCountAsync(client, audienceId));
    }

    // ------------------------------------------------------------------- AC2: distinct picker

    /// <summary>
    /// AC2 — GET /personnel/organizations collapses case and whitespace variants of one organization to a
    /// single entry. The representative is the ordinally-first spelling in the case-insensitive group
    /// ("CICT" sorts before "cict"), and the list is ordered case-insensitively.
    /// </summary>
    [Fact]
    public async Task The_organizations_picker_collapses_case_and_whitespace_variants_to_one_entry()
    {
        var schoolId = await ArrangeSchoolAsync();

        await SeedRawPersonnelAsync(schoolId, "EMP-1", "CICT");
        await SeedRawPersonnelAsync(schoolId, "EMP-2", "cict ");    // case + trailing space
        await SeedRawPersonnelAsync(schoolId, "EMP-3", "  CICT");   // leading space
        await SeedRawPersonnelAsync(schoolId, "EMP-4", "SSG");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var orgs = await client.GetFromJsonAsync<string[]>(OrganizationsRoute);

        Assert.Equal(new[] { "CICT", "SSG" }, orgs);
    }

    // ------------------------------------------------------------- Item 1: write-path normalization

    /// <summary>
    /// POST /personnel trims and collapses internal whitespace on save, preserving casing. The stored value
    /// — read straight back from the database, not just the response — is the normalized form.
    /// </summary>
    [Fact]
    public async Task Creating_personnel_stores_the_trimmed_whitespace_collapsed_organization()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(PersonnelRoute, new
        {
            personnelNumber = "EMP-100",
            firstName = "Grace",
            lastName = "Reyes",
            organization = "  Faculty   Union  ",   // leading/trailing + internal run
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("Faculty Union", body.RootElement.GetProperty("organization").GetString());

        await using var db = NewDbContext();
        var stored = await db.Personnel.AsNoTracking()
            .Where(p => p.PersonnelNumber == "EMP-100").Select(p => p.Organization).FirstAsync();
        Assert.Equal("Faculty Union", stored);
    }

    /// <summary>
    /// POST /personnel/import runs the same normalization — the CSV/bulk path cannot store a raw variant the
    /// manual form would have cleaned. A whitespace-only organization becomes null (Organization is optional).
    /// </summary>
    [Fact]
    public async Task Importing_personnel_stores_the_trimmed_whitespace_collapsed_organization()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var import = await client.PostAsJsonAsync($"{PersonnelRoute}/import", new
        {
            rows = new[]
            {
                new { personnelNumber = "IMP-1", firstName = "Ana", lastName = "Lopez", organization = "  Student   Council " },
                new { personnelNumber = "IMP-2", firstName = "Ben", lastName = "Diaz", organization = "   " },
            },
        });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        await using var db = NewDbContext();
        var one = await db.Personnel.AsNoTracking()
            .Where(p => p.PersonnelNumber == "IMP-1").Select(p => p.Organization).FirstAsync();
        var two = await db.Personnel.AsNoTracking()
            .Where(p => p.PersonnelNumber == "IMP-2").Select(p => p.Organization).FirstAsync();

        Assert.Equal("Student Council", one);
        Assert.Null(two);   // whitespace-only normalizes to null, not a blank organization
    }
}
