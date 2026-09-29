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
/// The Academic Community's Personnel tab over real HTTP — create, read, list/filter, update, and the
/// soft delete, plus the ID/RFID uniqueness and card-UID normalization. HTTP tests for the reason the
/// sibling admin suites record.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelAdminApiTests : IntegrationTest
{
    public PersonnelAdminApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/personnel";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
        return body.RootElement.GetProperty(PersonnelController.ErrorCodeProperty).GetString();
    }

    private static object NewBody(
        string number = "EMP-0001", string first = "Jose", string last = "Rizal",
        string? rfidUid = null, string? status = "Active") => new
    {
        personnelNumber = number,
        firstName = first,
        middleName = "P",
        lastName = last,
        email = "jose@usa.edu.ph",
        classification = "ACAD",
        department = "CICT",
        organization = "Faculty Union",
        position = "Professor",
        rfidUid,
        status,
    };

    [Fact]
    public async Task An_administrator_can_create_read_update_and_delete_a_personnel_record()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, NewBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Rizal, Jose P", createdBody.RootElement.GetProperty("fullName").GetString());
        Assert.Equal("Professor", createdBody.RootElement.GetProperty("position").GetString());

        Assert.NotNull(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(created.Headers.Location)).StatusCode);

        var updated = await client.PutAsJsonAsync($"{Route}/{id}", NewBody(last: "Rizal-Mercado"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedBody = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal("Rizal-Mercado, Jose P", updatedBody.RootElement.GetProperty("fullName").GetString());

        var deleted = await client.DeleteAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // Soft-deleted: gone from reads, still in the table.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{id}")).StatusCode);
        await using var db = NewDbContext();
        Assert.True((await db.Personnel.AsNoTracking().SingleAsync(p => p.Id == id)).IsDeleted);
    }

    [Fact]
    public async Task A_duplicate_personnel_id_is_a_conflict()
    {
        var schoolId = await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Route, NewBody(number: "EMP-1"))).StatusCode);
        var clash = await client.PostAsJsonAsync(Route, NewBody(number: "EMP-1"));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(nameof(PersonnelWriteOutcome.DuplicateNumber), await ErrorCodeAsync(clash));
    }

    [Fact]
    public async Task A_duplicate_rfid_uid_is_a_conflict_and_the_uid_is_normalized()
    {
        var schoolId = await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, NewBody(number: "EMP-1", rfidUid: "04:A7:B8:C9"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        // Normalized: separators stripped, upper-cased.
        Assert.Equal("04A7B8C9", body.RootElement.GetProperty("rfidUid").GetString());

        // A different punctuation of the same serial collides.
        var clash = await client.PostAsJsonAsync(Route, NewBody(number: "EMP-2", rfidUid: "04-a7-b8-c9"));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(nameof(PersonnelWriteOutcome.DuplicateRfid), await ErrorCodeAsync(clash));
    }

    [Fact]
    public async Task A_blank_id_or_name_is_a_validation_failure()
    {
        var schoolId = await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var noId = await client.PostAsJsonAsync(Route, NewBody(number: "   "));
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
        Assert.Equal(nameof(PersonnelWriteOutcome.ValidationFailed), await ErrorCodeAsync(noId));

        var badStatus = await client.PostAsJsonAsync(Route, NewBody(number: "EMP-9", status: "Retired"));
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
    }

    [Fact]
    public async Task The_list_filters_by_department_and_status()
    {
        var schoolId = await ArrangeSchoolAsync();
        await using (var db = NewDbContext())
        {
            db.Personnel.AddRange(
                TestData.NewPersonnel(schoolId, "EMP-1", lastName: "Alpha", department: "CICT", status: "Active"),
                TestData.NewPersonnel(schoolId, "EMP-2", lastName: "Bravo", department: "COE", status: "Active"),
                TestData.NewPersonnel(schoolId, "EMP-3", lastName: "Charlie", department: "CICT", status: "Inactive"));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        using var byDept = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}?department=CICT")).Content.ReadAsStringAsync());
        Assert.Equal(2, byDept.RootElement.GetProperty("items").GetArrayLength());

        using var byStatus = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}?department=CICT&status=Active")).Content.ReadAsStringAsync());
        Assert.Equal(1, byStatus.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_deleted_id_can_be_reused()
    {
        var schoolId = await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var id = JsonDocument.Parse(
            await (await client.PostAsJsonAsync(Route, NewBody(number: "EMP-1"))).Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{Route}/{id}")).StatusCode);

        // The filtered unique index frees the number once the row is soft-deleted.
        var recreated = await client.PostAsJsonAsync(Route, NewBody(number: "EMP-1"));
        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
    }

    [Fact]
    public async Task Personnel_from_another_school_are_not_visible()
    {
        var mine = await ArrangeSchoolAsync("USA");
        var other = await ArrangeSchoolAsync("XYZ");
        await using (var db = NewDbContext())
        {
            db.Personnel.Add(TestData.NewPersonnel(other, "OTHER-1", lastName: "Outsider"));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, mine);

        using var list = JsonDocument.Parse(await (await client.GetAsync(Route)).Content.ReadAsStringAsync());
        Assert.Equal(0, list.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_id_is_a_404_on_every_verb()
    {
        var schoolId = await ArrangeSchoolAsync();
        var missing = Guid.NewGuid();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}", NewBody())).StatusCode);
    }
}
