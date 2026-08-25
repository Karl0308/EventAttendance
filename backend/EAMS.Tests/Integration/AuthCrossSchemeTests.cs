using System.Net;
using System.Net.Http.Json;
using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Phase 6d — the attack that matters most for this product: making one of the two credentials do
/// the other's job.
///
/// <para>
/// <b>The product rule these tests defend.</b> A kiosk holds <c>attendance.capture</c> and nothing
/// else; no human role holds <c>attendance.capture</c> at all (<c>EamsRoles</c> excludes it from
/// every grant). A tap is therefore a statement that a card was physically presented to a device —
/// if a signed-in person could post one, that statement stops being true and the whole attendance
/// record becomes an assertion nobody can audit. Manual attendance exists for the human case and it
/// is a different endpoint with a different capture method.
/// </para>
///
/// <para>
/// <b>Why the gate is the SCHEME and not only the permission.</b> The capture endpoints declare
/// <c>[Authorize(AuthenticationSchemes = DeviceKey, Policy = attendance.capture)]</c>, and the policy
/// itself also names the DeviceKey scheme. That is belt and braces on purpose, and
/// <see cref="A_token_forged_with_the_capture_permission_still_cannot_capture"/> is the test that
/// says why: even in the worst case — the signing key compromised, an attacker minting whatever
/// claims they like — a Bearer credential is not admitted by these endpoints at all, so a forged
/// <c>perm</c> claim buys nothing here.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AuthCrossSchemeTests : IntegrationTest
{
    public AuthCrossSchemeTests(SqlServerFixture sql) : base(sql) { }

    private const string Email = "organizer@usa.edu.ph";
    private const string Password = "correct-horse-battery-staple";
    private const string StoredUid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid UserId, Guid EventId, Guid StudentId, Guid DeviceId);

    private async Task<World> ArrangeAsync()
    {
        Guid schoolId, eventId, studentId, deviceId;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);

            var student = TestData.NewStudent(school.Id);
            db.Students.Add(student);
            db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StoredUid));

            var ev = TestData.NewLiveEvent(school.Id);
            db.Events.Add(ev);

            var device = TestData.NewDevice(school.Id, "Lobby Kiosk");
            db.Devices.Add(device);

            await db.SaveChangesAsync();

            schoolId = school.Id;
            eventId = ev.Id;
            studentId = student.Id;
            deviceId = device.Id;
        }

        return new World(
            schoolId, await CreateUserAsync(schoolId, Email, Password, EamsRoleNames.Organizer),
            eventId, studentId, deviceId);
    }

    /// <summary>
    /// The five capture endpoints, as (method, route-template) pairs. Spelled here rather than read
    /// from the routing table on purpose: this list is the contract a kiosk is built against, and a
    /// list derived from the code under test would silently follow the code if an endpoint were
    /// quietly moved out from behind the device gate.
    /// </summary>
    public static TheoryData<string, string> CaptureEndpoints => new()
    {
        { "POST", "/api/v1/attendance/tap" },
        { "POST", "/api/v1/attendance/tap/batch" },
        { "GET", "/api/v1/students/by-card/" + StoredUid },
        { "POST", "/api/v1/devices/{deviceId}/heartbeat" },
        { "GET", "/api/v1/events/{eventId}/manifest" },
    };

    private static Task<HttpResponseMessage> CallAsync(
        HttpClient client, string method, string route, World world)
    {
        var path = route
            .Replace("{deviceId}", world.DeviceId.ToString(), StringComparison.Ordinal)
            .Replace("{eventId}", world.EventId.ToString(), StringComparison.Ordinal);

        return method == "GET"
            ? client.GetAsync(path)
            : client.PostAsJsonAsync(path, TapBodyFor(path, world));
    }

    private static object TapBodyFor(string path, World world) =>
        path.EndsWith("/batch", StringComparison.Ordinal)
            ? new
            {
                taps = new[]
                {
                    new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = Guid.NewGuid().ToString("N") },
                },
            }
            : new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = Guid.NewGuid().ToString("N") };

    // ------------------------------------------------- a device key against the human-only surface

    /// <summary>
    /// <c>AuthApiTests.A_device_key_cannot_reach_the_bearer_routes</c> already covers
    /// <c>/auth/me</c>. This is the complete statement over all three Bearer-gated actions —
    /// <c>/auth/me</c> included, because an allow-list that omits the route somebody later removes
    /// from the other test is not an allow-list.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/v1/auth/me")]
    [InlineData("POST", "/api/v1/auth/logout")]
    [InlineData("POST", "/api/v1/auth/change-password")]
    public async Task A_device_key_reaches_none_of_the_bearer_routes(string method, string route)
    {
        var world = await ArrangeAsync();
        var deviceKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(deviceKey);

        var response = method == "GET"
            ? await client.GetAsync(route)
            : await client.PostAsJsonAsync(route, new { currentPassword = Password, newPassword = "another-long-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Bearer, not DeviceKey: the route asked for a person and got a kiosk, and the challenge has
        // to name the credential the route actually wants.
        Assert.Equal("Bearer", response.Challenge()?.Scheme);
    }

    [Fact]
    public async Task A_device_key_cannot_change_anybodys_password()
    {
        var world = await ArrangeAsync();
        var deviceKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(deviceKey);

        await client.PostAsJsonAsync(
            "/api/v1/auth/change-password",
            new { currentPassword = Password, newPassword = "a-brand-new-long-password" });

        // The status is asserted above; what this adds is that nothing happened. A 401 that had
        // already written the new hash would be a far worse bug than a 200.
        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);
    }

    // ------------------------------------------- a human's token against the capture-only surface

    [Theory]
    [MemberData(nameof(CaptureEndpoints))]
    public async Task A_signed_in_human_reaches_none_of_the_capture_endpoints(string method, string route)
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);

        using var client = factory.CreateClient().WithBearer(session.AccessToken!);

        var response = await CallAsync(client, method, route, world);

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized,
            $"{method} {route} answered {(int)response.StatusCode} to a valid human Bearer token. " +
            $"attendance.capture is granted to no human role on purpose — a person who can post a " +
            $"kiosk-attributed tap can mark anyone present from anywhere.");

        // DeviceKey, and this is not cosmetic: the offline mobile client branches on the challenge
        // scheme, and a Bearer challenge would send a queued-tap retry looking for a login endpoint
        // it has no credential for.
        Assert.Equal("DeviceKey", response.Challenge()?.Scheme);
    }

    [Fact]
    public async Task A_refused_human_tap_leaves_no_attendance_row_behind()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);

        using var client = factory.CreateClient().WithBearer(session.AccessToken!);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap",
            new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = Guid.NewGuid().ToString("N") });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The authorization middleware runs before the action, so there is nothing to write — which
        // is exactly the kind of "obviously true" that stops being true the day someone moves the
        // gate into the controller body.
        await using var db = NewDbContext();
        Assert.Empty(await db.AttendanceRecords.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_token_forged_with_the_capture_permission_still_cannot_capture()
    {
        var world = await ArrangeAsync();

        // The worst case: the signing key is in the attacker's hands, so the token is genuinely
        // valid and carries whatever claims they chose. The scheme restriction is the layer that
        // still holds — the capture endpoints do not admit a Bearer credential at all, whatever it
        // says about itself.
        var token = ForgedJwt.Mint(ForgedJwt.ClaimsFor(
            world.UserId, world.SchoolId, EamsPermissions.AttendanceCapture));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithBearer(token);

        // Proof the token itself is good, so the 401 below is about the scheme and not the token.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap",
            new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = Guid.NewGuid().ToString("N") });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("DeviceKey", response.Challenge()?.Scheme);

        await using var db = NewDbContext();
        Assert.Empty(await db.AttendanceRecords.IgnoreQueryFilters().ToListAsync());
    }

    // ------------------------------------------------------------------------- both at once

    [Fact]
    public async Task Presenting_both_credentials_to_a_capture_endpoint_attributes_the_tap_to_the_device_and_to_no_person()
    {
        var world = await ArrangeAsync();
        var deviceKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);

        using var client = factory.CreateClient().WithBoth(session.AccessToken!, deviceKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap",
            new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = Guid.NewGuid().ToString("N") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The device key wins, and that is the SAFE answer rather than merely the deterministic one:
        // the row is attributed to the kiosk and to nobody, so a person who slipped their own token
        // alongside a borrowed device key gains no attribution they could later point at. If this
        // ever records a user id, a tap becomes forgeable evidence of who was standing at a door.
        await using var db = NewDbContext();
        var record = Assert.Single(await db.AttendanceRecords.IgnoreQueryFilters().ToListAsync());

        Assert.Null(record.RecordedByUserId);
        Assert.Equal("Rfid", record.CaptureMethod);
    }

    [Fact]
    public async Task Presenting_both_credentials_to_a_bearer_route_authenticates_neither()
    {
        var world = await ArrangeAsync();
        var deviceKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);

        using var client = factory.CreateClient().WithBoth(session.AccessToken!, deviceKey);

        var response = await client.GetAsync("/api/v1/auth/me");

        // Fail-closed, and worth pinning even though it inconveniences nobody: the Bearer handler
        // reads the Authorization header as one joined value, so a second header value spoils the
        // token rather than being ignored. A handler that instead scanned for the first "Bearer "
        // among several values would make header injection a way to swap credentials mid-request.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Challenge()?.Scheme);
    }

    // -------------------------------------------------------------- the staged cutover, unchanged

    [Fact]
    public async Task A_valid_bearer_token_does_not_close_an_endpoint_that_is_still_open()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);

        using var authenticated = factory.CreateClient().WithBearer(session.AccessToken!);
        using var anonymous = factory.CreateClient();

        // Both must succeed. AuthStagedCutoverTests proves the anonymous half; this adds the half
        // that only appears once a Bearer credential exists — presenting one must not start
        // enforcing anything, because ADR-001 D-6's cutover is a later phase's decision.
        Assert.True((await anonymous.GetAsync("/api/v1/students")).IsSuccessStatusCode);
        Assert.True((await authenticated.GetAsync("/api/v1/students")).IsSuccessStatusCode);

        Assert.NotEqual(Guid.Empty, world.SchoolId);
    }
}
