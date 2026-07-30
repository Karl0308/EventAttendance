using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The <c>DeviceKey</c> scheme over real HTTP (Phase 4a design, D-23 / D-26 / D-28).
///
/// <para>
/// <b>These run through the real host, not through the handler.</b> The whole of what Phase 4b changes
/// is what a caller on the wire sees — a status code, a <c>WWW-Authenticate</c> header, a problem body
/// — and none of that is visible to a test that calls <c>IDeviceAuthenticator</c> directly. The
/// authentication result, the policy evaluation and the status mapping are three separate mechanisms
/// and only the pipeline puts them together.
/// </para>
///
/// <para>
/// <b>The 401/403 split is published contract</b>, not a preference:
/// <c>docs/api/attendance-contract-handoff.md</c> tells the mobile developer "<c>401</c> missing or bad
/// key, <c>403</c> revoked key", and an offline queue needs to tell "check what you stored" from
/// "re-enrol" to decide what to do with a stalled batch.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DeviceAuthenticationTests : IntegrationTest
{
    public DeviceAuthenticationTests(SqlServerFixture sql) : base(sql) { }

    private const string Uid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId, Guid DeviceId, string ApiKey);

    private async Task<World> ArrangeAsync()
    {
        Guid schoolId, eventId, studentId;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            var student = TestData.NewStudent(school.Id);
            db.Students.Add(student);
            db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, Uid));
            // Live rather than anchored on the frozen TestData.Now — the taps here are posted without a
            // `tappedAt`, so D-36 validates the server's clock against this event's window. See
            // TestData.NewLiveEvent.
            var ev = TestData.NewLiveEvent(school.Id);
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            schoolId = school.Id;
            eventId = ev.Id;
            studentId = student.Id;
        }

        var apiKey = await IssueDeviceKeyAsync(schoolId);

        await using var read = NewDbContext();
        var deviceId = await read.Devices.AsNoTracking().Select(d => d.Id).SingleAsync();

        return new World(schoolId, eventId, studentId, deviceId, apiKey);
    }

    private IEnumerable<(string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)> GatedEndpoints(World world) =>
    [
        ("POST /attendance/tap", client => client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = world.EventId, cardUid = Uid })),
        ("GET /students/by-card", client => client.GetAsync($"/api/v1/students/by-card/{Uid}")),
        ("POST /devices/{id}/heartbeat", client => client.PostAsync(
            $"/api/v1/devices/{world.DeviceId}/heartbeat", content: null)),
    ];

    // ------------------------------------------------------------------------ the happy path

    /// <summary>
    /// A valid key authenticates every one of the gated endpoints. Asserted as a set rather than one
    /// test per endpoint so that gating a fourth endpoint without wiring its scheme is a failure here
    /// rather than a discovery in the field.
    /// </summary>
    [Fact]
    public async Task A_valid_key_authenticates_every_gated_endpoint()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        foreach (var (name, call) in GatedEndpoints(world))
        {
            using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);
            var response = await call(client);

            Assert.True(
                response.IsSuccessStatusCode,
                $"{name} returned {(int)response.StatusCode} for a valid device key.");
        }
    }

    /// <summary>
    /// The negative control for the test above, and the one assertion that makes the whole file mean
    /// something: with the header removed, every one of those endpoints is a 401. Without this, "a
    /// valid key works" would pass identically on a build where nothing was gated at all.
    /// </summary>
    [Fact]
    public async Task Without_a_key_every_gated_endpoint_is_401()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        foreach (var (name, call) in GatedEndpoints(world))
        {
            using var client = factory.CreateClient();
            var response = await call(client);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{name} returned {(int)response.StatusCode} with no credentials; expected 401.");

            // RFC 7235: a 401 must say what scheme would satisfy it.
            Assert.Contains(
                DeviceKey.AuthenticationScheme,
                response.Headers.WwwAuthenticate.Select(h => h.Scheme));
        }
    }

    /// <summary>
    /// And the endpoints ADR-001 D-6 leaves open stay open. Phase 4b is a <em>narrowing</em> of the
    /// open surface, not an authorization rollout, and the difference is only checkable by asserting
    /// both halves.
    /// </summary>
    [Fact]
    public async Task An_endpoint_outside_the_gated_four_is_still_open_without_a_key()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        foreach (var route in new[]
                 {
                     "/api/v1/students",
                     $"/api/v1/students/{world.StudentId}",
                     "/api/v1/events",
                     "/api/v1/attendance",
                     "/api/v1/devices",
                 })
        {
            var response = await client.GetAsync(route);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{route} returned {(int)response.StatusCode} without credentials. If this is a 401, " +
                "the open surface has been narrowed beyond D-28's four endpoints — say so in an ADR.");
        }
    }

    // ------------------------------------------------------------------------------- refusals

    /// <summary>
    /// Malformed and unknown are both 401 and are deliberately given the <em>same</em> outcome to a
    /// caller — telling one apart from the other confirms that a key id exists, which is the only part
    /// of a token that plausibly leaks.
    /// </summary>
    [Theory]
    [InlineData("not-a-key")]
    [InlineData("eams_dk_")]
    [InlineData("eams_dk_0123456789ab_short")]
    [InlineData("eams_dk_0123456789ab_0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task A_malformed_or_unknown_key_is_401(string apiKey)
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = world.EventId, cardUid = Uid });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A real key id with the wrong secret. Distinct from the unknown-key case in the code and
    /// deliberately indistinguishable on the wire.
    /// </summary>
    [Fact]
    public async Task A_valid_key_id_with_the_wrong_secret_is_401()
    {
        var world = await ArrangeAsync();

        Assert.True(DeviceKey.TryParse(world.ApiKey, out var keyId, out var secret));
        var tampered = $"eams_dk_{keyId}_{new string('a', secret.Length)}";

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(tampered);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = world.EventId, cardUid = Uid });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A revoked key is <b>403, not 401</b>: the device was identified, it simply carries no
    /// <c>attendance.capture</c> permission. Published contract.
    /// </summary>
    [Fact]
    public async Task A_revoked_key_is_403()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var revoked = await DevicesOn(db).RevokeKeyAsync(world.DeviceId);
            Assert.Equal(DeviceWriteOutcome.Saved, revoked.Outcome);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = world.EventId, cardUid = Uid });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DeviceKeyRevoked", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }

    /// <summary>
    /// A retired device is also 403 rather than 401, and carries its own <c>code</c> — "reactivate me"
    /// and "re-enrol me" are different instructions to whoever is holding the handset.
    /// </summary>
    [Fact]
    public async Task A_deactivated_device_is_403()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var updated = await DevicesOn(db).UpdateAsync(
                world.DeviceId, new DeviceWriteRequest("Test Kiosk", "Kiosk", null, IsActive: false));
            Assert.Equal(DeviceWriteOutcome.Saved, updated.Outcome);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = world.EventId, cardUid = Uid });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DeviceInactive", body.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// The 401 body is RFC 7807 with a <c>traceId</c>, matching the rest of the §6 surface. The
    /// framework default is a bare 401 with no body at all — nothing for a client to branch on and no
    /// handle for an operator to search logs by, which is the gap <c>HostPipelineTests</c> closed for
    /// 500s and this closes for authentication.
    /// </summary>
    [Fact]
    public async Task An_authentication_failure_is_an_rfc_7807_body()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = world.EventId, cardUid = Uid });

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(401, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("DeviceKeyMissing", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }

    // ---------------------------------------------------------------------- identity on the write

    /// <summary>
    /// D-26: a body with no <c>deviceId</c> is filled from the principal. The recorded row names the
    /// device that actually tapped, which is what makes the attendance trail worth having.
    /// </summary>
    [Fact]
    public async Task A_tap_with_no_device_in_the_body_records_the_authenticated_device()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap",
            new { eventId = world.EventId, cardUid = Uid, deviceTapId = "queued-0001" });

        response.EnsureSuccessStatusCode();

        await using var read = NewDbContext();
        var record = await read.AttendanceRecords.AsNoTracking().SingleAsync();
        Assert.Equal(world.DeviceId, record.DeviceId);
        Assert.Equal(world.SchoolId, record.SchoolId);
    }

    [Fact]
    public async Task A_tap_whose_body_agrees_with_the_principal_is_accepted()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap",
            new { eventId = world.EventId, cardUid = Uid, deviceId = world.DeviceId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// D-26: a mismatch is a <b>400, never a silent ignore</b>. The principal would win on the write
    /// either way, so absorbing the disagreement would be safe and still wrong — the client would
    /// believe it recorded a tap against a device the row does not name, and the idempotency key it
    /// retries on is scoped by device. Same reasoning the students surface applies to a derived field
    /// echoed back on a <c>PUT</c>.
    /// </summary>
    [Fact]
    public async Task A_tap_whose_body_names_another_device_is_400_and_writes_nothing()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap",
            new { eventId = world.EventId, cardUid = Uid, deviceId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.AttendanceRecords.CountAsync());
    }

    /// <summary>
    /// The same rule at the service layer, where the decision is made. The published token is
    /// <c>DeviceMismatch</c> and it is frozen contract — the mobile developer branches on it.
    ///
    /// <para>
    /// <b><c>tappedAt: null</c>, not <c>TestData.Now</c>.</b> This file's fixture is a
    /// <see cref="TestData.NewLiveEvent"/> — the taps around it are posted over HTTP with no
    /// timestamp — so a frozen <c>TestData.Now</c> here would be a day or more outside the event's
    /// D-36 window. It would still pass today, because the D-26 guard is evaluated before the event is
    /// even read, but only for a reason this test is not about and does not assert. A test that
    /// survives on the position of an unrelated guard is one refactor from failing for a reason nobody
    /// will connect to the refactor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_service_reports_a_device_mismatch_as_its_own_outcome()
    {
        var world = await ArrangeAsync();

        Device.DeviceId = world.DeviceId;

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(world.EventId, Uid, Guid.NewGuid(), "queued-0001", null));

        Assert.Equal(TapOutcome.DeviceMismatch, response.Outcome);
        Assert.False(response.Result.Success);
        Assert.Null(response.Result.Record);
    }

    /// <summary>
    /// A device may only heartbeat as itself. <c>HeartbeatAsync</c> writes <c>ApiKeyLastUsedAt</c> as
    /// well as <c>LastSeenAt</c>, and that column is what an operator reads when deciding whether a
    /// credential is idle enough to burn — so one kiosk keeping a retired sibling's key looking active
    /// is a key that never gets revoked, not merely a wrong timestamp.
    /// </summary>
    [Fact]
    public async Task A_device_cannot_heartbeat_as_another_device()
    {
        var world = await ArrangeAsync();
        var otherKey = await IssueDeviceKeyAsync(world.SchoolId, "Second Kiosk");

        Guid otherDeviceId;
        await using (var read = NewDbContext())
        {
            otherDeviceId = await read.Devices.AsNoTracking()
                .Where(d => d.Name == "Second Kiosk").Select(d => d.Id).SingleAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        // 404 rather than 403: a device asking about a device that is not itself learns only that the
        // URL names nothing it can see.
        var foreign = await client.PostAsync($"/api/v1/devices/{otherDeviceId}/heartbeat", content: null);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        await using (var read = NewDbContext())
        {
            var untouched = await read.Devices.AsNoTracking().SingleAsync(d => d.Id == otherDeviceId);
            Assert.Null(untouched.LastSeenAt);
            Assert.Null(untouched.ApiKeyLastUsedAt);
        }

        // The control: the same call against its own id succeeds, so the refusal above is about
        // identity rather than about heartbeats being broken.
        var own = await client.PostAsync($"/api/v1/devices/{world.DeviceId}/heartbeat", content: null);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        Assert.NotEqual(world.ApiKey, otherKey);
    }

    // ---------------------------------------------------------------------------- rate limiting

    /// <summary>
    /// A rejected caller gets §6's declared error shape, not the framework's bare 429.
    ///
    /// <para>
    /// <b>This is the one rate-limiting behaviour worth the cost of proving, and it is cheap for a
    /// reason worth knowing:</b> the limiter sits after <c>UseAuthentication</c> but before
    /// <c>UseAuthorization</c>, so an <em>unauthenticated</em> flood is partitioned by remote address
    /// and rejected without the handler ever touching the database. The requests below are pure
    /// in-memory pipeline. It also incidentally pins that ordering — if the limiter moved after
    /// authorization, every one of these would be a 401 and this test would fail.
    /// </para>
    ///
    /// <para>
    /// <c>AttendanceController</c> declares <c>[ProducesResponseType(429)]</c>, and until this landed
    /// that declaration promised a shape nothing produced.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rate_limited_request_is_a_problem_body_with_a_retry_after()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        HttpResponseMessage? rejected = null;

        for (var attempt = 0; attempt <= CaptureRateLimiting.PermitsPerWindow; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/attendance/tap", new { eventId = Guid.NewGuid(), cardUid = Uid });

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
                break;
            }

            // Everything before the limit is a 401 — no credentials — which is the proof that the
            // limiter is what changed the answer rather than the endpoint behaving differently.
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            response.Dispose();
        }

        Assert.NotNull(rejected);

        using var _ = rejected;
        Assert.Equal("application/problem+json", rejected!.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(rejected.Headers.RetryAfter);

        using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            CaptureRateLimiting.RateLimitedCode,
            body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }

    // ------------------------------------------------------------ the tenant comes from the claim

    /// <summary>
    /// <b>The <c>school_id</c> claim, not the pinned development school, is what scopes a device's
    /// request</b> (D-23). This is the assertion that makes <c>ClaimsSchoolContext</c> worth shipping
    /// early rather than in Phase 6.
    ///
    /// <para>
    /// <b>It needs two schools to say anything, and that is the point.</b> With one school the claim
    /// and the startup pin resolve to the same tenant, so every other test in this file passes just as
    /// happily against an implementation that ignores claims entirely — the negative control proved
    /// exactly that, coming back green when <c>ClaimsSchoolContext</c> was reduced to the pin alone.
    /// Here the host pins the <em>lowest school Code</em>, which is <c>CICSS</c>, while the key
    /// belongs to <c>USA</c>: if the claim is read the event resolves and the tap is recorded; if only
    /// the pin is read, USA's event is filtered out of existence and the tap is a 404.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_device_key_scopes_the_request_to_its_own_school_not_the_pinned_one()
    {
        Guid usaSchoolId, usaEventId;

        await using (var db = NewDbContext())
        {
            // Sorts before "USA", so DependencyInjection.PinDevelopmentSchoolAsync pins this one.
            db.Schools.Add(TestData.NewSchool("CICSS"));

            var usa = TestData.NewSchool("USA");
            db.Schools.Add(usa);
            var student = TestData.NewStudent(usa.Id);
            db.Students.Add(student);
            db.RfidCards.Add(TestData.NewCard(usa.Id, student.Id, Uid));
            var ev = TestData.NewLiveEvent(usa.Id); // server-stamped tap below — see TestData.NewLiveEvent
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            usaSchoolId = usa.Id;
            usaEventId = ev.Id;
        }

        var apiKey = await IssueDeviceKeyAsync(usaSchoolId, "USA Kiosk");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = usaEventId, cardUid = Uid });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"A USA device key returned {(int)response.StatusCode} on a USA event while the host had " +
            "pinned CICSS. A 404 here means ISchoolContext is still answering with the pinned " +
            "development school rather than with the request's school_id claim.");

        await using var read = NewDbContext(new TestSchoolContext());
        var record = await read.AttendanceRecords.AsNoTracking().SingleAsync();
        Assert.Equal(usaSchoolId, record.SchoolId);
    }

    // ------------------------------------------------------------------------ liveness

    /// <summary>
    /// An authenticated request stamps liveness opportunistically, so a tapping kiosk does not have to
    /// heartbeat to look alive. The throttle (once a minute per device) is what keeps that from being
    /// an UPDATE on the capture hot path.
    /// </summary>
    [Fact]
    public async Task An_authenticated_tap_stamps_liveness_without_a_heartbeat()
    {
        var world = await ArrangeAsync();

        await using (var read = NewDbContext())
        {
            var before = await read.Devices.AsNoTracking().SingleAsync();
            Assert.Null(before.LastSeenAt);
            Assert.Null(before.ApiKeyLastUsedAt);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        (await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = world.EventId, cardUid = Uid }))
            .EnsureSuccessStatusCode();

        await using var after = NewDbContext();
        var stored = await after.Devices.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.LastSeenAt);
        Assert.NotNull(stored.ApiKeyLastUsedAt);
    }

    // -------------------------------------------------------------- the seeded development kiosk

    /// <summary>
    /// D-28 chose a seeded development device over a configuration off-switch. This is the half of
    /// that decision that has to be provable: <b>the well-known key cannot exist on a
    /// Production-configured host.</b>
    ///
    /// <para>
    /// The gate is the existing "seed only outside Production" rule rather than anything new — which
    /// is exactly why it was chosen. An off-switch is a setting someone can put in the wrong
    /// environment; this is a credential that is never written there in the first place.
    /// </para>
    /// </summary>
    /// <para>
    /// <b>There is deliberately no arrange step, and that is the whole reason this test works.</b> The
    /// first version of it created a school first — which made <c>SeedData</c> return early on its own
    /// "a school already exists" guard, so nothing was ever seeded and the assertion passed
    /// identically on a build that seeded in Production. The negative control caught it: forcing
    /// <c>seed: true</c> in <c>Program.cs</c> left the test green. Starting from an empty database
    /// means the environment is the <em>only</em> thing that can be suppressing the seed.
    /// </para>
    [Fact]
    public async Task The_seeded_development_key_does_not_exist_on_a_production_host()
    {
        // EamsApiFactory boots the host in Production, so Program.cs passes seed: false. The event id
        // is deliberately nonsense: authentication runs before the endpoint does, so a valid key would
        // reach the action and produce a 404, and only a refused key produces a 401.
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(SeedData.DevelopmentKioskApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = Guid.NewGuid(), cardUid = Uid });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var read = NewDbContext();
        Assert.False(
            await read.Devices.AnyAsync(d => d.Name == SeedData.DevelopmentKioskName),
            "The development kiosk was seeded into a Production-configured host. The well-known key " +
            "in SeedData is only acceptable because that cannot happen.");
        Assert.Equal(0, await read.Schools.CountAsync());
    }

    /// <summary>
    /// <b>And not on Staging either, which is the environment the first version of this gate let
    /// through.</b>
    ///
    /// <para>
    /// The seed was gated on <c>!IsProduction()</c>, so a Staging host — real, network-reachable, and
    /// the one an institution is most likely to point a browser at before go-live — wrote a device row
    /// carrying a working, publicly-known API key. The constant's own docstring staked its
    /// acceptability on "it can only ever exist on a host that is not Production", which is a claim
    /// about the wrong predicate: <c>Program.cs</c> gates Swagger, a strictly lesser exposure, on
    /// <c>IsDevelopment()</c> fifteen lines below.
    /// </para>
    ///
    /// <para>
    /// This is the test that makes the two-environment version of that claim checkable. Neither the
    /// Production test above nor the Development test below can see it — that is exactly how the hole
    /// survived being written.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_seeded_development_key_does_not_exist_on_a_staging_host()
    {
        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, "Staging");
        using var client = factory.CreateClient().WithDeviceKey(SeedData.DevelopmentKioskApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = Guid.NewGuid(), cardUid = Uid });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var read = NewDbContext();
        Assert.False(
            await read.Devices.AnyAsync(d => d.Name == SeedData.DevelopmentKioskName),
            "The development kiosk was seeded into a Staging host. A hard-coded credential is only " +
            "acceptable while it cannot exist anywhere reachable, and Staging is reachable.");
        Assert.Equal(0, await read.Schools.CountAsync());
    }

    /// <summary>
    /// The control for the two tests above: in Development the same key <em>does</em> work. Without it,
    /// "the seeded key is refused in Production" would pass just as happily on a build where the seed
    /// was broken everywhere — which would leave the phase's chosen development workflow silently
    /// unusable.
    /// </summary>
    [Fact]
    public async Task The_seeded_development_key_works_on_a_development_host()
    {
        // No arrange: the Development host seeds its own school, students, cards and open event.
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(SeedData.DevelopmentKioskApiKey);

        Guid eventId;
        await using (var read = NewDbContext())
        {
            eventId = await read.Events.AsNoTracking()
                .Where(e => e.Status == EventStatus.Open)
                .Select(e => e.Id)
                .FirstAsync();
        }

        // Through the constant, not a copy of its value: this test taps a card the SEED owns, and the
        // literal it used to carry went stale the moment the seed's UIDs became decimal serials — a red
        // suite whose cause lived in another project entirely.
        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId, cardUid = SeedData.DevelopmentCardUid });

        Assert.True(
            response.IsSuccessStatusCode,
            $"The seeded development kiosk key returned {(int)response.StatusCode}.");
    }
}
