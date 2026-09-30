using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Manual ID entry for unrecognized RFID scans (UnrecognizedRFIDScans.docx): the operator types the ID
/// for a card the reader could not place, it is stored (resolved against the roster best-effort), and it
/// is reviewable — over real HTTP.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class UnrecognizedScanApiTests : IntegrationTest
{
    public UnrecognizedScanApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/scans/manual-id";

    private async Task<Guid> ArrangeSchoolWithStudentAsync(string studentNumber = "2023-7001")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        db.Students.Add(TestData.NewStudent(school.Id, studentNumber, firstName: "Ana", lastName: "Cruz"));
        await db.SaveChangesAsync();
        return school.Id;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty(ScansController.ErrorCodeProperty).GetString();
    }

    [Fact]
    public async Task A_manual_entry_matching_a_student_is_recorded_and_resolved()
    {
        var schoolId = await ArrangeSchoolWithStudentAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(Route, new
        {
            cardUid = "04:AB:CD:EF",
            idNumber = "2023-7001",
            personType = "Student",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // UID normalized (separators stripped, upper-cased).
        Assert.Equal("04ABCDEF", body.RootElement.GetProperty("cardUid").GetString());
        Assert.True(body.RootElement.GetProperty("isResolved").GetBoolean());
        Assert.Equal("Cruz, Ana", body.RootElement.GetProperty("resolvedName").GetString());
    }

    [Fact]
    public async Task A_manual_entry_with_an_unknown_id_is_recorded_unresolved()
    {
        var schoolId = await ArrangeSchoolWithStudentAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(Route, new
        {
            cardUid = "0AAA0001",
            idNumber = "does-not-exist",
            personType = "Employee",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("isResolved").GetBoolean());
    }

    [Fact]
    public async Task A_missing_id_number_is_refused()
    {
        var schoolId = await ArrangeSchoolWithStudentAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(Route, new
        {
            cardUid = "0AAA0001",
            idNumber = "   ",
            personType = "Student",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(ManualIdEntryOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_person_type_is_refused()
    {
        var schoolId = await ArrangeSchoolWithStudentAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(Route, new
        {
            cardUid = "0AAA0001",
            idNumber = "2023-7001",
            personType = "Visitor",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(ManualIdEntryOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Recorded_entries_are_listed_newest_first()
    {
        var schoolId = await ArrangeSchoolWithStudentAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        await client.PostAsJsonAsync(Route, new { cardUid = "0AAA0001", idNumber = "2023-7001", personType = "Student" });
        await client.PostAsJsonAsync(Route, new { cardUid = "0AAA0002", idNumber = "2023-7001", personType = "Student" });

        var list = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("items").GetArrayLength());
    }
}
