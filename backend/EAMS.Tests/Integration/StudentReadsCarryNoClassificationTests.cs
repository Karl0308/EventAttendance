using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>QA AC3 (ADR-009, D-76): the Student data model neither requires nor displays classification.</b>
/// Every read that returns a student, and the write response, publish no <c>classifications</c>
/// property.
///
/// <para>
/// <b>Asserted on the raw JSON, not on a deserialized DTO.</b> A test that deserialized into
/// <c>StudentDto</c> could not fail when the property came back: a record with no such member would
/// ignore it. The text the client actually receives is the contract, and a property that crept back
/// would reach the mobile developer and the admin SPA as a field they were told is gone. Each test
/// pairs the absence with the presence of an ordinary property of the same object, so "no
/// classifications" can never be satisfied by an empty or wrong response.
/// </para>
///
/// <para>
/// <b>The person under test holds classifications.</b> The tables and the assignment routes are kept
/// dormant, so a database can still contain held assignments; reads must ignore them, not trip on them.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentReadsCarryNoClassificationTests : IntegrationTest
{
    public StudentReadsCarryNoClassificationTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/students";
    private const string StudentNumber = "2023-0001";
    private const string CardUid = "04A7B8C9";
    private const string ForbiddenProperty = "classifications";

    private sealed record World(Guid SchoolId, Guid StudentId);

    /// <summary>A school with its vocabulary, and one student with a card who holds two classifications.</summary>
    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var vocabulary = TestData.ClassificationVocabulary(school.Id);
        db.Classifications.AddRange(vocabulary);

        var student = TestData.NewStudent(school.Id, StudentNumber);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, CardUid));
        await db.SaveChangesAsync();

        db.StudentClassifications.AddRange(
            TestData.NewStudentClassification(student.Id, vocabulary.Single(c => c.Name == "STUDENT")),
            TestData.NewStudentClassification(student.Id, vocabulary.Single(c => c.Name == "NAP")));
        await db.SaveChangesAsync();

        return new World(school.Id, student.Id);
    }

    private async Task<int> HeldCountAsync()
    {
        await using var db = NewDbContext();
        return await db.StudentClassifications.CountAsync();
    }

    /// <summary>
    /// The failure message for a body that carries the property, naming the whole body so the report
    /// shows what leaked rather than only that something did.
    /// </summary>
    private static void AssertNoClassifications(JsonElement student, string body)
    {
        Assert.Equal(JsonValueKind.Object, student.ValueKind);

        foreach (var property in student.EnumerateObject())
            Assert.False(
                string.Equals(property.Name, ForbiddenProperty, StringComparison.OrdinalIgnoreCase),
                $"The student object published a '{property.Name}' property. Body: {body}");

        // And not as a substring of the raw text either, which also catches a nested occurrence.
        Assert.DoesNotContain(ForbiddenProperty, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_list_publishes_no_classifications_property()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);

        // Positive control: the student is in the page, with the properties a list row always has.
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(StudentNumber, item.GetProperty("studentNumber").GetString());
        Assert.Equal(CardUid, Assert.Single(item.GetProperty("cards").EnumerateArray()).GetProperty("cardUid").GetString());

        AssertNoClassifications(item, raw);
    }

    [Fact]
    public async Task The_detail_read_publishes_no_classifications_property()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync($"{Route}/{world.StudentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);

        Assert.Equal(world.StudentId, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(StudentNumber, body.RootElement.GetProperty("studentNumber").GetString());

        AssertNoClassifications(body.RootElement, raw);
    }

    [Fact]
    public async Task The_by_card_read_publishes_no_classifications_property()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await client.GetAsync($"{Route}/by-card/{CardUid}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);

        Assert.Equal(world.StudentId, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(StudentNumber, body.RootElement.GetProperty("studentNumber").GetString());

        AssertNoClassifications(body.RootElement, raw);
    }

    [Fact]
    public async Task The_write_response_publishes_no_classifications_property()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var update = await client.PutAsJsonAsync($"{Route}/{world.StudentId}", new
        {
            studentNumber = StudentNumber, firstName = "Maricel", middleName = (string?)null,
            lastName = "Santos", email = (string?)null,
        });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(updated))
        {
            Assert.Equal("Maricel", body.RootElement.GetProperty("firstName").GetString());
            AssertNoClassifications(body.RootElement, updated);
        }

        // The create response is the other write that returns a student.
        var create = await client.PostAsJsonAsync(Route, new
        {
            studentNumber = "2023-0100", firstName = "Juan", middleName = (string?)null,
            lastName = "Dela Cruz", email = (string?)null,
        });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(created))
        {
            Assert.Equal("2023-0100", body.RootElement.GetProperty("studentNumber").GetString());
            AssertNoClassifications(body.RootElement, created);
        }
    }

    /// <summary>
    /// <b>The no-data-loss half of AC3.</b> The tables are kept; a person who holds classifications
    /// reads and saves like anyone else, and neither the reads nor the full-replacement <c>PUT</c> that
    /// used to carry the collection back touches what they hold.
    /// </summary>
    [Fact]
    public async Task A_student_who_holds_classifications_reads_and_saves_normally_and_keeps_them()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        // Positive control: the two assignments are really there to be lost.
        Assert.Equal(2, await HeldCountAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Route}/{world.StudentId}")).StatusCode);
        Assert.Equal(2, await HeldCountAsync());

        // A client built against the old contract may still send the collection back on a PUT. It is
        // ignored, not rejected: the save succeeds and the held assignments are untouched.
        var update = await client.PutAsJsonAsync($"{Route}/{world.StudentId}", new
        {
            studentNumber = StudentNumber, firstName = "Maricel", middleName = (string?)null,
            lastName = "Santos", email = (string?)null,
            classifications = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        await using var read = NewDbContext();
        Assert.Equal("Maricel", (await read.Students.SingleAsync(s => s.Id == world.StudentId)).FirstName);
        Assert.Equal(2, await read.StudentClassifications.CountAsync(a => a.StudentId == world.StudentId));
    }
}
