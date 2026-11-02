using System.Net;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>GET /events/personnel-picker</c> (ADR-015 D-104): an Organizer's staff picker after the personnel
/// routes moved to <c>personnel.read</c>. Real role grants, real sign-in, real SQL Server, because what this
/// route exists to pin is who gets it and how little they see.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventPersonnelPickerApiTests : IntegrationTest
{
    public EventPersonnelPickerApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/events/personnel-picker";
    private const string PersonnelRoute = "/api/v1/personnel";

    private static readonly string[] PermittedKeys = ["fullName", "id", "personnelNumber"];

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Personnel> AddPersonnelAsync(
        Guid schoolId, string number, string lastName = "Rizal", string status = PersonnelStatus.Active,
        bool isDeleted = false)
    {
        await using var db = NewDbContext();
        var person = TestData.NewPersonnel(schoolId, number, "Jose", "P", lastName, status: status);
        person.IsDeleted = isDeleted;
        db.Personnel.Add(person);
        await db.SaveChangesAsync();
        return person;
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static List<Guid> IdsOf(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    /// <summary>
    /// D-104: an Organizer has no <c>personnel.read</c>, gets this route, and sees exactly three members.
    /// The value is checked too, because a differently named member would pass a check on keys alone.
    /// </summary>
    [Fact]
    public async Task An_organizer_gets_the_picker_with_exactly_the_three_fields_and_no_personnel_read()
    {
        var schoolId = await ArrangeSchoolAsync();
        var person = await AddPersonnelAsync(schoolId, "EMP-0001");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId, EamsRoleNames.Organizer);

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);

        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(PermittedKeys, item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(person.Id, item.GetProperty("id").GetGuid());
        Assert.Equal("EMP-0001", item.GetProperty("personnelNumber").GetString());
        Assert.Equal("Rizal, Jose P", item.GetProperty("fullName").GetString());

        Assert.DoesNotContain(person.Email!, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(person.Department!, text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PersonnelRoute)).StatusCode);
    }

    /// <summary>A Viewer holds <c>events.read</c>, which is not this route's permission.</summary>
    [Fact]
    public async Task A_role_without_events_write_is_403()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId, EamsRoleNames.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task No_token_is_401()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task Another_schools_personnel_never_appear()
    {
        var schoolId = await ArrangeSchoolAsync();
        var otherId = await ArrangeSchoolAsync("OTHER");
        var mine = await AddPersonnelAsync(schoolId, "EMP-0001");
        await AddPersonnelAsync(otherId, "EMP-0002");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId, EamsRoleNames.Organizer);

        var page = await BodyOf(await client.GetAsync(Route));

        Assert.Equal([mine.Id], IdsOf(page));
        Assert.Equal(1, page.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// The contract with the SPA's pickers: the rows and the order of an unfiltered <c>GET /personnel</c>,
    /// so Inactive rows are in, last, and soft-deleted ones are not. Read side by side, as a caller holding
    /// both permissions would see them.
    /// </summary>
    [Fact]
    public async Task The_rows_and_order_match_an_unfiltered_personnel_list()
    {
        var schoolId = await ArrangeSchoolAsync();
        var inactive = await AddPersonnelAsync(schoolId, "EMP-0001", "Aquino", PersonnelStatus.Inactive);
        var second = await AddPersonnelAsync(schoolId, "EMP-0002", "Zamora");
        var first = await AddPersonnelAsync(schoolId, "EMP-0003", "Bonifacio");
        await AddPersonnelAsync(schoolId, "EMP-0004", "Deleted", isDeleted: true);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var picker = IdsOf(await BodyOf(await client.GetAsync(Route)));

        Assert.Equal([first.Id, second.Id, inactive.Id], picker);
        Assert.Equal(IdsOf(await BodyOf(await client.GetAsync(PersonnelRoute))), picker);
    }

    /// <summary>The envelope and the query the SPA's <c>listAll</c> walks.</summary>
    [Fact]
    public async Task It_pages_with_the_same_envelope_and_query_as_the_personnel_list()
    {
        var schoolId = await ArrangeSchoolAsync();
        await AddPersonnelAsync(schoolId, "EMP-0001", "Aquino");
        var second = await AddPersonnelAsync(schoolId, "EMP-0002", "Bonifacio");
        await AddPersonnelAsync(schoolId, "EMP-0003", "Cruz");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId, EamsRoleNames.Organizer);

        var page = await BodyOf(await client.GetAsync($"{Route}?page=2&pageSize=1"));

        Assert.Equal([second.Id], IdsOf(page));
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(1, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, page.GetProperty("total").GetInt32());

        var list = await BodyOf(await (await SignedInClientAsync(factory, schoolId)).GetAsync($"{PersonnelRoute}?page=2&pageSize=1"));
        Assert.Equal(
            list.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
            page.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }
}
