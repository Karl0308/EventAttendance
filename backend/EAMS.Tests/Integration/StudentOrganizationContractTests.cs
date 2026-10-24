using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-011 D-81: <c>StudentDto</c> gains a nullable <c>Organization</c>, last in the record, on every
/// response that carries a student; and it is NOT editable through <c>PUT</c> — the column belongs to the
/// import pipeline, and a manual edit must not blank the importer's work.
///
/// <para>
/// <b>Asserted on the raw JSON, not on a deserialized DTO</b>, for the reason
/// <see cref="StudentReadsCarryNoClassificationTests"/> gives: a DTO without the member would ignore it, so
/// a test that deserialized could never fail when the property went missing. A student with no organization
/// is asserted too, because "nullable" means the key is PRESENT with a JSON null (the .NET side serializes
/// null; it is TypeScript's <c>undefined</c> that is omitted), and the FE's strict <c>nullableStr</c> reads
/// an absent key differently from a null one.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentOrganizationContractTests : IntegrationTest
{
    public StudentOrganizationContractTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/students";
    private const string StudentNumber = "2023-0001";
    private const string CardUid = "04A7B8C9";
    private const string StoredOrganization = "CICT Society";
    private const string Property = "organization";

    private sealed record World(Guid SchoolId, Guid StudentId, Guid BareStudentId);

    /// <summary>A school with one student carrying an organization and a card, and one with neither.</summary>
    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var student = TestData.NewStudent(school.Id, StudentNumber, lastName: "Alpha");
        student.Organization = StoredOrganization;
        var bare = TestData.NewStudent(school.Id, "2023-0002", lastName: "Bravo");
        db.Students.AddRange(student, bare);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, CardUid));
        await db.SaveChangesAsync();

        return new World(school.Id, student.Id, bare.Id);
    }

    private static void AssertCarriesOrganization(JsonElement student, string? expected, string body)
    {
        Assert.True(student.TryGetProperty(Property, out var value),
            $"The student object has no '{Property}' key. Body: {body}");
        if (expected is null) Assert.Equal(JsonValueKind.Null, value.ValueKind);
        else Assert.Equal(expected, value.GetString());
    }

    /// <summary>
    /// List, detail, the device-authenticated by-card read, and both write responses all publish
    /// <c>organization</c>: the stored string for a student who has one, an explicit JSON null for one who
    /// does not.
    /// </summary>
    [Fact]
    public async Task Every_student_read_carries_organization()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        // ---- list: both rows, the one with the value and the one without.
        var list = await client.GetAsync($"{Route}?pageSize=50");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listRaw = await list.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(listRaw))
        {
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            AssertCarriesOrganization(
                items.Single(i => i.GetProperty("studentNumber").GetString() == StudentNumber),
                StoredOrganization, listRaw);
            AssertCarriesOrganization(
                items.Single(i => i.GetProperty("studentNumber").GetString() == "2023-0002"), null, listRaw);
        }

        // ---- detail
        var detail = await client.GetAsync($"{Route}/{world.StudentId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailRaw = await detail.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(detailRaw))
            AssertCarriesOrganization(body.RootElement, StoredOrganization, detailRaw);

        var bareDetail = await client.GetAsync($"{Route}/{world.BareStudentId}");
        var bareRaw = await bareDetail.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(bareRaw))
            AssertCarriesOrganization(body.RootElement, null, bareRaw);

        // ---- by-card, under a device key (the mobile client's read)
        using var device = factory.CreateClient().WithDeviceKey(apiKey);
        var byCard = await device.GetAsync($"{Route}/by-card/{CardUid}");
        Assert.Equal(HttpStatusCode.OK, byCard.StatusCode);
        var byCardRaw = await byCard.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(byCardRaw))
        {
            Assert.Equal(StudentNumber, body.RootElement.GetProperty("studentNumber").GetString());
            AssertCarriesOrganization(body.RootElement, StoredOrganization, byCardRaw);
        }

        // ---- the write responses
        var update = await client.PutAsJsonAsync($"{Route}/{world.StudentId}", new
        {
            studentNumber = StudentNumber, firstName = "Maricel", middleName = (string?)null,
            lastName = "Alpha", email = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updatedRaw = await update.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(updatedRaw))
        {
            Assert.Equal("Maricel", body.RootElement.GetProperty("firstName").GetString());
            AssertCarriesOrganization(body.RootElement, StoredOrganization, updatedRaw);
        }

        var create = await client.PostAsJsonAsync(Route, new
        {
            studentNumber = "2023-0100", firstName = "Juan", middleName = (string?)null,
            lastName = "Dela Cruz", email = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var createdRaw = await create.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(createdRaw))
            AssertCarriesOrganization(body.RootElement, null, createdRaw);
    }

    /// <summary>
    /// D-81: Organization is read-only on the API. A <c>PUT</c> that omits it, one that sends a different
    /// value, and one that sends null all leave the imported value exactly as it was, in the response and
    /// in the database; the rest of the body is applied, so the save really happened.
    /// </summary>
    [Fact]
    public async Task A_put_leaves_the_imported_organization_untouched()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        (string Label, object Body)[] bodies =
        [
            ("omits organization", new
            {
                studentNumber = StudentNumber, firstName = "Omitted", middleName = (string?)null,
                lastName = "Alpha", email = (string?)null,
            }),
            ("sends a different organization", new
            {
                studentNumber = StudentNumber, firstName = "Different", middleName = (string?)null,
                lastName = "Alpha", email = (string?)null, organization = "Hijacked Org",
            }),
            ("sends a null organization", new
            {
                studentNumber = StudentNumber, firstName = "Nulled", middleName = (string?)null,
                lastName = "Alpha", email = (string?)null, organization = (string?)null,
            }),
        ];

        foreach (var (label, body) in bodies)
        {
            var response = await client.PutAsJsonAsync($"{Route}/{world.StudentId}", body);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"A PUT that {label} answered {(int)response.StatusCode}.");

            var raw = await response.Content.ReadAsStringAsync();
            using var parsed = JsonDocument.Parse(raw);
            AssertCarriesOrganization(parsed.RootElement, StoredOrganization, raw);

            await using var read = NewDbContext();
            var stored = await read.Students.AsNoTracking().SingleAsync(s => s.Id == world.StudentId);
            Assert.Equal(StoredOrganization, stored.Organization);
            // Positive control: the rest of the body was applied.
            Assert.Equal(parsed.RootElement.GetProperty("firstName").GetString(), stored.FirstName);
        }
    }
}
