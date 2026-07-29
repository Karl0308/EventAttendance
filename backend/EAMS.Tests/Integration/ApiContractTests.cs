using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The HTTP surface of §6: which outcome becomes which status code, and what an unauthenticated
/// caller can reach.
///
/// <para>
/// The service returns an outcome enum and the controller translates it; that translation is the
/// part Phase 4's external mobile developer codes against, and it is invisible to every test that
/// calls <c>IAttendanceService</c> directly. A 404 quietly becoming a 200-with-success-false would
/// break a client's error handling without failing a single service-level test.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ApiContractTests : IntegrationTest
{
    public ApiContractTests(SqlServerFixture sql) : base(sql) { }

    private const string StoredUid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

    private async Task<World> ArrangeAsync(string eventStatus = "Open")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StoredUid));
        var ev = TestData.NewEvent(school.Id, eventStatus);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id);
    }

    /// <summary>
    /// <c>POST /attendance/tap</c> is one of the four endpoints a device key gates (Phase 4a design,
    /// D-28), so every call here now presents one. That fixture cost is the correct cost: these tests
    /// exist to assert what the mobile client sees, and the mobile client presents a key.
    /// </summary>
    private static Task<HttpResponseMessage> TapAsync(HttpClient client, object payload) =>
        client.PostAsJsonAsync("/api/v1/attendance/tap", payload);

    // ---------------------------------------------------------------- tap status mapping

    [Fact]
    public async Task A_recorded_tap_is_200()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await TapAsync(client, new { eventId = world.EventId, cardUid = StoredUid });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
    }

    /// <summary>
    /// A replay is a success, not a conflict. §8.2's retry-on-timeout depends on it: a client that
    /// received a 409 would treat its own successful tap as a failure and surface an error to a
    /// student who is in fact checked in.
    /// </summary>
    [Fact]
    public async Task A_replayed_tap_is_also_200()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);
        var payload = new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = "replay-0001" };

        await TapAsync(client, payload);
        var replay = await TapAsync(client, payload);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task A_tap_on_an_unknown_event_is_404()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await TapAsync(client, new { eventId = Guid.NewGuid(), cardUid = StoredUid });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_tap_with_an_unknown_card_is_404()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await TapAsync(client, new { eventId = world.EventId, cardUid = "DEADBEEF" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_tap_on_an_event_that_is_not_open_is_400()
    {
        var world = await ArrangeAsync(eventStatus: "Closed");
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await TapAsync(client, new { eventId = world.EventId, cardUid = StoredUid });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------- lookup endpoints

    [Fact]
    public async Task An_unknown_card_lookup_is_404_and_a_known_one_normalizes_the_uid()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/students/by-card/DEADBEEF")).StatusCode);

        var found = await client.GetAsync("/api/v1/students/by-card/04a7b8c9");
        found.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync());
        Assert.Equal(world.StudentId, body.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_unknown_event_is_404_on_both_the_detail_and_the_summary_route()
    {
        await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/events/{unknown}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/events/{unknown}/summary")).StatusCode);
    }

    // ---------------------------------------------------------------- manual override (§6.4)

    [Fact]
    public async Task A_manual_override_records_attendance_and_marks_the_capture_method()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/v1/attendance/manual?eventId={world.EventId}&studentId={world.StudentId}" +
            "&status=Excused&notes=Medical", content: null);
        response.EnsureSuccessStatusCode();

        await using var db = NewDbContext();
        var record = await db.AttendanceRecords.AsNoTracking().SingleAsync();
        Assert.Equal("Excused", record.Status);
        Assert.Equal("Manual", record.CaptureMethod);
        Assert.Equal("Medical", record.Notes);
    }

    /// <summary>
    /// §4.9's value set, enforced at the boundary the admin SPA and any future client actually use.
    /// <c>status=Banana</c> used to come back 200 / "Manual entry saved." with the row persisted,
    /// and a status longer than the column came back 500 from a SQL truncation — the same missing
    /// check reported as two unrelated-looking failures.
    /// </summary>
    [Theory]
    [InlineData("Banana")]
    [InlineData("a-status-far-longer-than-the-twenty-character-column-allows")]
    public async Task A_manual_override_with_an_undocumented_status_is_400(string status)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/v1/attendance/manual?eventId={world.EventId}&studentId={world.StudentId}" +
            $"&status={Uri.EscapeDataString(status)}", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = NewDbContext();
        Assert.Equal(0, await db.AttendanceRecords.CountAsync());
    }

    [Fact]
    public async Task A_manual_override_for_an_unknown_student_is_404()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/v1/attendance/manual?eventId={world.EventId}&studentId={Guid.NewGuid()}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- authorization seam (ADR-001 D-6)

    /// <summary>
    /// The empirical half of ADR-001 D-6's "a test asserting it denies nothing": a real request
    /// through the real pipeline, with no credentials, against an action carrying the attribute.
    /// It reaches the action body.
    ///
    /// <para>
    /// Like the unit-level seam tests, <b>this is meant to be deleted by Phase 6</b> rather than made
    /// to pass. When §11 lands, an uncredentialed request here must start returning 401 — and the
    /// failure is the reminder to replace this file's expectations with denial assertions.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/test-only/permission-probe")]
    [InlineData("/test-only/permission-probe/stacked")]
    public async Task An_endpoint_decorated_with_HasPermissionNotEnforced_denies_nothing(string route)
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PermissionProbeController.ReachedBody, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// And the state that makes the above unsurprising: every real endpoint is open too. Recorded as
    /// a test rather than a comment because ADR-001 D-6 makes "do not expose this build" a condition
    /// someone has to be able to check, not a thing they have to remember.
    /// </summary>
    [Fact]
    public async Task Every_api_endpoint_is_reachable_without_credentials()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        foreach (var route in new[]
                 {
                     "/api/v1/students",
                     $"/api/v1/students/{world.StudentId}",
                     "/api/v1/events",
                     $"/api/v1/events/{world.EventId}",
                     $"/api/v1/events/{world.EventId}/summary",
                     $"/api/v1/events/{world.EventId}/roster",
                     "/api/v1/attendance",
                 })
        {
            var response = await client.GetAsync(route);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{route} returned {(int)response.StatusCode}. If this is a 401 or 403, Technical " +
                "Plan §11 has landed — delete the authorization-seam tests and assert denial instead.");
        }
    }
}
