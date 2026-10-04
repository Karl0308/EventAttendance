using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The <c>Events.EventClassificationId</c> link over real HTTP — the first half of the Event Audience
/// module's "classification then audience" flow. The write surface persists it, the read surface joins
/// the name back, and an unknown / deactivated / cross-school classification is refused as
/// <c>UnknownReference</c> (400), the same way the audience write surface treats its own classification.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventClassificationLinkApiTests : IntegrationTest
{
    public EventClassificationLinkApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/events";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> ArrangeClassificationAsync(
        Guid schoolId, string name = "Institutional Events", bool isActive = true)
    {
        await using var db = NewDbContext();
        var row = TestData.NewEventClassification(schoolId, name, isActive: isActive);
        db.EventClassifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>A valid create body, carrying the classification id only when the caller names it.</summary>
    private static Dictionary<string, object?> EventBody(
        Guid? eventClassificationId = null, bool sendClassification = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "University Convocation 2026",
            ["description"] = "Annual convocation.",
            ["location"] = "USA Gymnasium",
            ["startAt"] = "2026-08-01T01:00:00Z",
            ["endAt"] = "2026-08-01T04:00:00Z",
            ["attendanceMode"] = "Single",
            ["graceMinutes"] = 15,
            ["requireRegistration"] = false,
        };
        if (sendClassification) body["eventClassificationId"] = eventClassificationId;
        return body;
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetEventAsync(HttpClient client, Guid id)
    {
        var get = await client.GetAsync($"{Route}/{id}");
        get.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    // -------------------------------------------------------------------------------------- create

    [Fact]
    public async Task Creates_an_event_with_a_classification_and_get_exposes_id_and_name()
    {
        var schoolId = await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync(schoolId, "Institutional Events");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(
            Route, EventBody(classificationId, sendClassification: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var dto = await GetEventAsync(client, await CreatedIdAsync(created));
        Assert.Equal(classificationId, dto.GetProperty("eventClassificationId").GetGuid());
        Assert.Equal("Institutional Events", dto.GetProperty("eventClassificationName").GetString());
    }

    [Fact]
    public async Task Creates_an_event_without_a_classification_as_null()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        // Both an omitted field and an explicit null leave the event unclassified.
        foreach (var body in new[] { EventBody(), EventBody(null, sendClassification: true) })
        {
            var created = await client.PostAsJsonAsync(Route, body);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            var dto = await GetEventAsync(client, await CreatedIdAsync(created));
            Assert.Equal(JsonValueKind.Null, dto.GetProperty("eventClassificationId").ValueKind);
            Assert.Equal(JsonValueKind.Null, dto.GetProperty("eventClassificationName").ValueKind);
        }
    }

    // -------------------------------------------------------------------------------------- update

    [Fact]
    public async Task Updates_a_draft_events_classification()
    {
        var schoolId = await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync(schoolId, "Departmental Events");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, EventBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await CreatedIdAsync(created);

        // A full-replacement PUT that is identical to the create body apart from the classification.
        var put = await client.PutAsJsonAsync(
            $"{Route}/{id}", EventBody(classificationId, sendClassification: true));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var dto = await GetEventAsync(client, id);
        Assert.Equal(classificationId, dto.GetProperty("eventClassificationId").GetGuid());
        Assert.Equal("Departmental Events", dto.GetProperty("eventClassificationName").GetString());
    }

    // -------------------------------------------------------------------------------------- closed events

    private async Task DriveToClosedAsync(HttpClient client, Guid id)
    {
        foreach (var status in new[] { "Open", "Closed" })
        {
            var patch = await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status });
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }
    }

    [Fact]
    public async Task A_classification_change_on_a_Closed_event_is_refused_as_locked()
    {
        var schoolId = await ArrangeSchoolAsync();
        var original = await ArrangeClassificationAsync(schoolId, "Institutional Events");
        var other = await ArrangeClassificationAsync(schoolId, "Departmental Events");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(
            Route, EventBody(original, sendClassification: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await CreatedIdAsync(created);
        await DriveToClosedAsync(client, id);

        // Names the certificates flag, so the request qualifies as the one edit a Closed event
        // accepts — unless the classification, the only other difference, counts as a change. Without
        // the flag the request is refused as locked regardless, and the test could not go red.
        var body = EventBody(other, sendClassification: true);
        body["issuesCertificates"] = true;
        var put = await client.PutAsJsonAsync($"{Route}/{id}", body);

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        using var problem = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        // This route carries no machine "code" extension (only the audience resolver does); the
        // EventLocked outcome is identified by its 409 and its fixed problem title.
        Assert.Equal("The event's state forbids this change.",
            problem.RootElement.GetProperty("title").GetString());

        var dto = await GetEventAsync(client, id);
        Assert.Equal(original, dto.GetProperty("eventClassificationId").GetGuid());
    }

    [Fact]
    public async Task A_certificates_only_edit_on_a_Closed_event_with_an_unchanged_since_deactivated_classification_still_succeeds()
    {
        var schoolId = await ArrangeSchoolAsync();
        var classificationId = await ArrangeClassificationAsync(schoolId, "Institutional Events");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(
            Route, EventBody(classificationId, sendClassification: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await CreatedIdAsync(created);
        await DriveToClosedAsync(client, id);

        await using (var db = NewDbContext())
        {
            var row = await db.EventClassifications.SingleAsync(c => c.Id == classificationId);
            row.IsActive = false;
            await db.SaveChangesAsync();
        }

        var body = EventBody(classificationId, sendClassification: true);
        body["issuesCertificates"] = true;
        var put = await client.PutAsJsonAsync($"{Route}/{id}", body);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var dto = await GetEventAsync(client, id);
        Assert.True(dto.GetProperty("issuesCertificates").GetBoolean());
        Assert.Equal(classificationId, dto.GetProperty("eventClassificationId").GetGuid());
    }

    // -------------------------------------------------------------------------------------- refusals

    [Fact]
    public async Task Rejects_an_inactive_classification()
    {
        var schoolId = await ArrangeSchoolAsync();
        var inactiveId = await ArrangeClassificationAsync(
            schoolId, "Retired Category", isActive: false);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(
            Route, EventBody(inactiveId, sendClassification: true));

        // UnknownReference → 400 per the existing mapping.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_cross_school_classification()
    {
        var schoolId = await ArrangeSchoolAsync("USA");
        var otherSchoolId = await ArrangeSchoolAsync("OTH");
        var otherSchoolsClassificationId = await ArrangeClassificationAsync(
            otherSchoolId, "Institutional Events");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        // Another tenant's id is refused identically to an unknown one — telling the two apart would
        // confirm the existence of a row in a school the caller cannot see (EventWriteOutcome docs).
        var response = await client.PostAsJsonAsync(
            Route, EventBody(otherSchoolsClassificationId, sendClassification: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
