using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EAMS.Api.RateLimiting;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The HTTP surface of Phase 4d: <c>POST /attendance/tap/batch</c> and
/// <c>GET /attendance/live/{eventId}</c>, asserted on the bytes rather than through the service.
///
/// <para>
/// The batch shape is frozen with an external mobile developer
/// (<c>docs/api/attendance-contract-handoff.md</c> §3), and the parts of it that a service-level test
/// cannot see are exactly the parts that were frozen: the transport status being 200 while a row's own
/// <c>status</c> is a 404, the field names after camelCasing, and which keys are present at all.
/// <c>ApiContractTests</c> records why that gap matters — a 404 quietly becoming a 200 breaks a client
/// without failing a single service-level test.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceBatchApiTests : IntegrationTest
{
    public AttendanceBatchApiTests(SqlServerFixture sql) : base(sql) { }

    private const string StoredUid = "04A7B8C9";
    private const string BatchRoute = "/api/v1/attendance/tap/batch";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
        db.Schools.Add(school);
        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StoredUid));
        // Server-stamped taps, so the event's D-36 window has to contain the real clock.
        var ev = TestData.NewLiveEvent(school.Id);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id);
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------------------------- the batch

    /// <summary>
    /// The frozen envelope, field by field, on a batch that mixes an accepted row with a rejected one.
    ///
    /// <para>
    /// <b>The load-bearing assertion is the first one.</b> A row whose own status is 404 sits inside a
    /// transport 200 — that is the "not 207" decision made observable, and it is what stops a client
    /// retrying 199 good rows because one card was unknown.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_well_formed_batch_is_200_whatever_its_rows_say()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await client.PostAsJsonAsync(BatchRoute, new
        {
            clientClockAt = DateTime.UtcNow,
            taps = new[]
            {
                new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = "api-0001" },
                new { eventId = world.EventId, cardUid = "NOSUCHCARD", deviceTapId = "api-0002" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("rejected").GetInt32());
        Assert.NotEqual(default, body.GetProperty("serverTime").GetDateTime());

        var results = body.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);

        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("api-0001", results[0].GetProperty("deviceTapId").GetString());
        Assert.Equal("Recorded", results[0].GetProperty("code").GetString());
        Assert.Equal(200, results[0].GetProperty("status").GetInt32());
        Assert.Equal(
            world.StudentId,
            results[0].GetProperty("record").GetProperty("studentId").GetGuid());

        Assert.Equal(1, results[1].GetProperty("index").GetInt32());
        Assert.Equal("api-0002", results[1].GetProperty("deviceTapId").GetString());
        Assert.Equal("CardNotFound", results[1].GetProperty("code").GetString());
        Assert.Equal(404, results[1].GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, results[1].GetProperty("record").ValueKind);
    }

    /// <summary>
    /// The fourth endpoint a device key gates (D-28). Without a key it is a 401 before any row is
    /// looked at — a batch is the single largest thing an unauthenticated caller could ask this API to
    /// write.
    /// </summary>
    [Fact]
    public async Task A_batch_without_a_device_key_is_401()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(BatchRoute, new
        {
            clientClockAt = DateTime.UtcNow,
            taps = new[] { new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = "nokey" } },
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var read = NewDbContext();
        Assert.Empty(read.AttendanceRecords);
    }

    /// <summary>
    /// The batch-level refusal: a problem body carrying the frozen <c>code</c>, the <c>serverTime</c>
    /// every response promises, the <c>traceId</c> §6 promises, and the limit the client is told to read
    /// rather than hard-code.
    /// </summary>
    [Fact]
    public async Task An_oversized_batch_is_a_problem_body_that_echoes_the_limit()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var taps = Enumerable.Range(0, TapBatchLimits.MaxRows + 1)
            .Select(i => new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = $"over-{i}" })
            .ToArray();

        var response = await client.PostAsJsonAsync(BatchRoute, new { clientClockAt = DateTime.UtcNow, taps });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await BodyOf(response);
        Assert.Equal("BatchTooLarge", body.GetProperty("code").GetString());
        Assert.Equal(TapBatchLimits.MaxRows, body.GetProperty("maxBatchRows").GetInt32());
        Assert.NotEqual(default, body.GetProperty("serverTime").GetDateTime());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));

        await using var read = NewDbContext();
        Assert.Empty(read.AttendanceRecords);
    }

    /// <summary>An empty flush is a 200 with an empty <c>results</c>, not an error.</summary>
    [Fact]
    public async Task An_empty_batch_is_200_over_the_wire()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await client.PostAsJsonAsync(
            BatchRoute, new { clientClockAt = DateTime.UtcNow, taps = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(0, body.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.GetProperty("rejected").GetInt32());
        Assert.Empty(body.GetProperty("results").EnumerateArray());
    }

    /// <summary>
    /// D-33 on the wire: the batch-only token, with the 400 the frozen table publishes for it, inside a
    /// transport 200.
    /// </summary>
    [Fact]
    public async Task A_row_without_a_deviceTapId_is_a_400_row_inside_a_200_batch()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await client.PostAsJsonAsync(BatchRoute, new
        {
            clientClockAt = DateTime.UtcNow,
            taps = new[] { new { eventId = world.EventId, cardUid = StoredUid } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        var row = Assert.Single(body.GetProperty("results").EnumerateArray().ToList());

        Assert.Equal("DeviceTapIdRequired", row.GetProperty("code").GetString());
        Assert.Equal(400, row.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("deviceTapId").ValueKind);
    }

    // -------------------------------------------------------------------------------------- the poll

    private static string LiveRoute(Guid eventId, string? since = null) =>
        since is null
            ? $"/api/v1/attendance/live/{eventId}"
            : $"/api/v1/attendance/live/{eventId}?since={Uri.EscapeDataString(since)}";

    /// <summary>
    /// <b>The snapshot body has <c>entries</c> and does not have <c>changes</c> at all</b>, and the
    /// delta is the mirror image. Asserted on key <em>presence</em> rather than on null, because a
    /// client that received both keys with one null would have to guess which mode it was in — and the
    /// whole point of the split is that a snapshot replaces state while a delta merges into it.
    /// </summary>
    [Fact]
    public async Task A_snapshot_carries_entries_and_a_delta_carries_changes()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var capture = factory.CreateClient().WithDeviceKey(apiKey);
        using var client = factory.CreateClient();

        await capture.PostAsJsonAsync("/api/v1/attendance/tap", new
        {
            eventId = world.EventId,
            cardUid = StoredUid,
            deviceTapId = "live-0001",
        });

        var snapshotResponse = await client.GetAsync(LiveRoute(world.EventId));
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);

        var snapshot = await BodyOf(snapshotResponse);
        Assert.True(snapshot.TryGetProperty("entries", out var entries));
        Assert.False(
            snapshot.TryGetProperty("changes", out _),
            "A snapshot body carries a 'changes' key. Exactly one of the two collections is present; " +
            "a body with both leaves the client guessing whether to replace its state or merge into it.");

        Assert.Single(entries.EnumerateArray());
        Assert.Equal(world.EventId, snapshot.GetProperty("eventId").GetGuid());
        Assert.Equal(
            world.EventId, snapshot.GetProperty("counters").GetProperty("eventId").GetGuid());
        Assert.True(snapshot.GetProperty("pollAfterSeconds").GetInt32() > 0);

        var cursor = snapshot.GetProperty("cursor").GetString()!;

        var deltaResponse = await client.GetAsync(LiveRoute(world.EventId, cursor));
        Assert.Equal(HttpStatusCode.OK, deltaResponse.StatusCode);

        var delta = await BodyOf(deltaResponse);
        Assert.True(delta.TryGetProperty("changes", out var changes));
        Assert.False(
            delta.TryGetProperty("entries", out _),
            "A delta body carries an 'entries' key. See the snapshot assertion above.");
        Assert.Empty(changes.EnumerateArray());
    }

    /// <summary>
    /// <c>Cache-Control: no-store</c>. An intermediary replaying a 200 for a cursor the client has
    /// already passed makes the dashboard silently stop updating, and the client cannot tell that from
    /// an event where nothing is happening.
    /// </summary>
    [Fact]
    public async Task The_live_endpoint_is_never_cached()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(LiveRoute(world.EventId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    /// <summary>
    /// The live endpoint is a dashboard read and is <b>not</b> gated by a device key — see
    /// <c>AttendanceController.Live</c> for why gating it would hand the admin SPA a capture-scoped
    /// credential. It is open under ADR-001 D-6 like the rest of the admin surface, and this test says
    /// so out loud rather than leaving it to the absence of an assertion.
    /// </summary>
    [Fact]
    public async Task The_live_endpoint_needs_no_device_key()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(LiveRoute(world.EventId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// <c>no-store</c> covers the failures too (Phase 4d review). It used to be set only on the 200
    /// path, and a cacheable <c>InvalidCursor</c> is the nastier of the two: an intermediary replaying
    /// it would pin a dashboard at "your cursor is invalid" long after the client had been handed a
    /// good one.
    /// </summary>
    [Fact]
    public async Task The_live_endpoints_failures_are_never_cached_either()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var badCursor = await client.GetAsync(LiveRoute(world.EventId, "not-a-cursor"));
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
        Assert.True(badCursor.Headers.CacheControl?.NoStore);

        var unknownEvent = await client.GetAsync(LiveRoute(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, unknownEvent.StatusCode);
        Assert.True(unknownEvent.Headers.CacheControl?.NoStore);
    }

    /// <summary>
    /// The live endpoint carries its own IP-partitioned limiter (Phase 4d review). Unauthenticated,
    /// unlimited and loop-shaped was the combination worth refusing: one poll is the ceiling scalar, the
    /// event read, the delta read and four aggregate queries, two of which walk <c>EventGroups</c> into
    /// section membership.
    ///
    /// <para>
    /// Asserted by exhausting the window, which is affordable only because the live budget is a
    /// twentieth of the capture one — the capture policy's own tests assert attachment rather than
    /// behaviour for exactly this reason. It runs against a single event with no attendance, so each
    /// request is the cheapest the endpoint has.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_live_endpoint_refuses_a_client_polling_far_faster_than_it_was_told_to()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        HttpResponseMessage? limited = null;

        for (var i = 0; i <= CaptureRateLimiting.LivePermitsPerWindow; i++)
        {
            var response = await client.GetAsync(LiveRoute(world.EventId));
            if (response.StatusCode != HttpStatusCode.TooManyRequests) continue;

            limited = response;
            break;
        }

        Assert.NotNull(limited);
        Assert.NotNull(limited.Headers.RetryAfter);

        var body = await BodyOf(limited);
        Assert.Equal(CaptureRateLimiting.RateLimitedCode, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));

        // The prose names the right budget. A dashboard told "this device is limited to 600 capture
        // requests" would send its reader looking for a kiosk.
        Assert.Contains(
            $"{CaptureRateLimiting.LivePermitsPerWindow}",
            body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// <c>{"taps": [null]}</c> over the wire. Valid JSON, and ASP.NET's
    /// implicit-required-from-nullable-reference-types does not reach collection elements — so before
    /// the fix this was a batch-level 500 that §8.2's queue retried forever.
    /// </summary>
    [Fact]
    public async Task A_null_row_is_a_rejected_row_rather_than_a_500()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await client.PostAsync(
            BatchRoute,
            new StringContent(
                """{"clientClockAt":"2026-07-29T09:14:03Z","taps":[null]}""",
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(0, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("rejected").GetInt32());

        var row = Assert.Single(body.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("DeviceTapIdRequired", row.GetProperty("code").GetString());
        Assert.Equal(400, row.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task An_unrecognised_cursor_is_a_400_problem_body()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(LiveRoute(world.EventId, "not-a-cursor"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await BodyOf(response);
        Assert.Equal(nameof(LiveOutcomeTokens.InvalidCursor), body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task An_unknown_event_is_a_404_problem_body()
    {
        await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(LiveRoute(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(nameof(LiveOutcomeTokens.EventNotFound), body.GetProperty("code").GetString());
    }

    /// <summary>
    /// The live endpoint's <c>code</c> values, transcribed rather than derived from the enum.
    ///
    /// <para>
    /// Same discipline as <c>TapOutcomeContractTests</c> and for a weaker but real reason: these are not
    /// frozen contract — the live endpoint is published as provisional in detail — but the admin SPA
    /// will branch on them, and asserting against <c>LiveOutcome.ToString()</c> would be asserting that
    /// the enum equals itself.
    /// </para>
    /// </summary>
    private enum LiveOutcomeTokens
    {
        EventNotFound,
        InvalidCursor,
    }
}
