using System.Net;
using System.Net.Http.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>GET /personnel/organizations</c> over real HTTP — the distinct, non-empty organizations a school's
/// live personnel hold, for the Personnel filter's picker. Tenant-scoped by the global query filter, so a
/// school never sees another's organizations. HTTP tests for the reason the sibling admin suites record.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelOrganizationsApiTests : IntegrationTest
{
    public PersonnelOrganizationsApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/personnel/organizations";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>A live personnel row carrying a specific organization (null/blank are valid here).</summary>
    private static Personnel WithOrg(Guid schoolId, string number, string? organization)
    {
        var person = TestData.NewPersonnel(schoolId, number);
        person.Organization = organization;
        return person;
    }

    [Fact]
    public async Task Returns_distinct_non_empty_organizations_in_order()
    {
        var schoolId = await ArrangeSchoolAsync();
        await using (var db = NewDbContext())
        {
            db.Personnel.AddRange(
                WithOrg(schoolId, "EMP-1", "Zeta Society"),
                WithOrg(schoolId, "EMP-2", "Alpha Club"),
                WithOrg(schoolId, "EMP-3", "Alpha Club"),   // duplicate → collapses
                WithOrg(schoolId, "EMP-4", "Mu Group"));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var orgs = await client.GetFromJsonAsync<string[]>(Route);

        Assert.Equal(new[] { "Alpha Club", "Mu Group", "Zeta Society" }, orgs);
    }

    [Fact]
    public async Task Omits_personnel_with_no_organization()
    {
        var schoolId = await ArrangeSchoolAsync();
        await using (var db = NewDbContext())
        {
            db.Personnel.AddRange(
                WithOrg(schoolId, "EMP-1", "Faculty Union"),
                WithOrg(schoolId, "EMP-2", null),            // no organization
                WithOrg(schoolId, "EMP-3", "   "));          // whitespace-only → not an organization
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var orgs = await client.GetFromJsonAsync<string[]>(Route);

        Assert.Equal(new[] { "Faculty Union" }, orgs);
    }

    [Fact]
    public async Task Returns_only_the_signed_in_schools_organizations()
    {
        var mine = await ArrangeSchoolAsync("USA");
        var other = await ArrangeSchoolAsync("XYZ");
        await using (var db = NewDbContext())
        {
            db.Personnel.Add(WithOrg(mine, "MINE-1", "Mine Org"));
            db.Personnel.Add(WithOrg(other, "OTHER-1", "Other Org"));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, mine);

        var orgs = await client.GetFromJsonAsync<string[]>(Route);

        // Only this school's organization — the other school's is filtered out by the global query filter.
        Assert.Equal(new[] { "Mine Org" }, orgs);
    }
}
