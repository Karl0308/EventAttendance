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
        // A live event, not one anchored on the frozen TestData.Now: every tap in this file is posted
        // over HTTP without a `tappedAt`, so the server stamps it with the real clock, and Phase 4c's
        // D-36 window check compares that against this event's own StartAt/EndAt. See
        // TestData.NewLiveEvent.
        var ev = TestData.NewLiveEvent(school.Id, eventStatus);
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

    // ---------------------------------------------------------------- the machine-readable body (D-37)

    /// <summary>
    /// The client's top ask, on the wire. All four tap successes are a 200, so before this the only
    /// thing distinguishing "I wrote a row" from "your retry was absorbed" was a prose <c>message</c>
    /// the same document tells the client not to parse — which made reconciling a flushed queue
    /// against what actually landed impossible.
    /// </summary>
    [Fact]
    public async Task A_recorded_tap_body_carries_its_outcome_code_and_the_server_clock()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await TapAsync(client, new { eventId = world.EventId, cardUid = StoredUid });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("Recorded", body.RootElement.GetProperty("code").GetString());
        Assert.EndsWith("Z", body.RootElement.GetProperty("serverTime").GetString());
    }

    /// <summary>
    /// The distinction the prose could not carry: a replay is a different <c>code</c> from a first
    /// write, at the same status code and with the same <c>success: true</c>.
    /// </summary>
    [Fact]
    public async Task A_replayed_tap_is_distinguishable_from_the_original_by_its_code_alone()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);
        var payload = new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = "replay-0002" };

        var first = await TapAsync(client, payload);
        var replay = await TapAsync(client, payload);

        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.Equal(first.StatusCode, replay.StatusCode);
        Assert.Equal("Recorded", firstBody.RootElement.GetProperty("code").GetString());
        Assert.Equal("DuplicateIgnored", replayBody.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// A rejected tap is now an RFC 7807 body like every other failure in this API, where it used to be
    /// a <c>TapResult</c> with <c>success: false</c>.
    ///
    /// <para>
    /// That was two contradictions at once: §6 says "Errors: RFC 7807", and the published handoff
    /// document tells the mobile developer that every error body carries a <c>traceId</c> he can quote
    /// back to us — so the one endpoint he was told to build against was the one endpoint whose errors
    /// had nothing to quote. The <c>traceId</c> assertion is the point of this test; the <c>code</c>
    /// assertion is what makes the body readable by the same accessor as a success.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rejected_tap_is_a_problem_body_carrying_a_code_and_a_trace_id()
    {
        var world = await ArrangeAsync(eventStatus: "Closed");
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await TapAsync(client, new { eventId = world.EventId, cardUid = StoredUid });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("EventNotOpen", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("title").GetString()));
    }

    /// <summary>
    /// The D-36 refusal a drifted device receives, and the reason the problem body carries
    /// <c>serverTime</c> as well: this response tells a client its clock is wrong, so it has to say in
    /// the same body what the right one is. Without that the client's only recovery is to guess.
    /// </summary>
    [Fact]
    public async Task A_tap_from_the_future_is_a_400_that_tells_the_device_the_server_clock()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await TapAsync(client, new
        {
            eventId = world.EventId,
            cardUid = StoredUid,
            tappedAt = DateTime.UtcNow.AddHours(9),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Asserted here as well as in the test above, because `code` and `serverTime` are present on
        // both body shapes: without this the test would pass just as happily on the old TapResult
        // failure body, and this is the rejection that most needs to be a problem body.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TappedAtOutOfRange", body.RootElement.GetProperty("code").GetString());

        var serverTime = DateTime.Parse(
            body.RootElement.GetProperty("serverTime").GetString()!,
            null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.InRange(serverTime, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
    }

    /// <summary>
    /// The other D-36 refusal, and the one an out-of-date event id produces. Asserted separately from
    /// the future case because the client's published instruction for the two differs — resync the
    /// clock, versus stop retrying.
    /// </summary>
    [Fact]
    public async Task A_tap_outside_the_event_window_is_a_400_naming_the_window()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await TapAsync(client, new
        {
            eventId = world.EventId,
            cardUid = StoredUid,
            tappedAt = DateTime.UtcNow.AddYears(-2),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TappedAtOutsideEventWindow", body.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// The override surface gets the same treatment. Its outcomes are not in the device contract — no
    /// device calls this endpoint — but a second error convention on one controller is exactly how a
    /// client ends up parsing prose after all.
    /// </summary>
    [Fact]
    public async Task A_rejected_manual_override_is_a_problem_body_carrying_a_code()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/v1/attendance/manual?eventId={world.EventId}&studentId={world.StudentId}" +
            "&status=Banana", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("InvalidStatus", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
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
