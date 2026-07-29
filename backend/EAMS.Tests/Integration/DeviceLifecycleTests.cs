using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Technical Plan §6.6 — device registration and the key lifecycle (Phase 4a design, D-24 / D-25).
///
/// <para>
/// The invariant every test here circles is the same one: <b>a plaintext key leaves the process
/// exactly twice</b> — at issue and at rotation — and is never retrievable afterwards. The server
/// stores <c>SHA-256(secret)</c> and nothing else, so "show me the key again" is not a refused
/// operation, it is an operation with no possible implementation.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DeviceLifecycleTests : IntegrationTest
{
    public DeviceLifecycleTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/devices";

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();

        School.CurrentSchoolId = school.Id;
        return school.Id;
    }

    private static DeviceWriteRequest ValidDevice(
        string name = "Gym Kiosk 1", string? type = "Kiosk", bool isActive = true) =>
        new(name, type, "ACR122U", isActive);

    // ------------------------------------------------------------------------------ issue

    [Fact]
    public async Task Registering_a_device_issues_a_key_and_stores_only_its_hash()
    {
        await ArrangeSchoolAsync();

        DeviceWriteResponse response;
        await using (var db = NewDbContext())
            response = await DevicesOn(db).RegisterAsync(ValidDevice());

        Assert.Equal(DeviceWriteOutcome.Saved, response.Outcome);
        Assert.NotNull(response.IssuedKey);

        var apiKey = response.IssuedKey!.ApiKey;
        Assert.True(DeviceKey.TryParse(apiKey, out var keyId, out var secret));

        await using var read = NewDbContext();
        var stored = await read.Devices.AsNoTracking().SingleAsync();

        Assert.Equal(keyId, stored.ApiKeyId);
        Assert.Equal(DeviceKey.HashSecret(secret), stored.ApiKeyHash);
        Assert.NotNull(stored.ApiKeyIssuedAt);
        Assert.Null(stored.ApiKeyRevokedAt);
        Assert.Null(stored.ApiKeyLastUsedAt);

        // The one that matters: the secret is nowhere in the row.
        Assert.DoesNotContain(secret, stored.ApiKeyHash!, StringComparison.Ordinal);
        Assert.NotEqual(secret, stored.ApiKeyHash);
    }

    /// <summary>
    /// §4.10's <c>ApiKey</c> column stays permanently NULL and is not dropped (global no-data-loss
    /// rule, and it is the plan's own column). This is the pin: a future edit that "puts the key where
    /// the plan says it goes" reintroduces a plaintext credential column, and fails here.
    /// </summary>
    [Fact]
    public async Task Registering_and_rotating_never_write_the_plan_s_original_ApiKey_column()
    {
        await ArrangeSchoolAsync();

        Guid deviceId;
        await using (var db = NewDbContext())
        {
            var registered = await DevicesOn(db).RegisterAsync(ValidDevice());
            deviceId = registered.Device!.Id;
        }

        await using (var db = NewDbContext())
            await DevicesOn(db).RegenerateKeyAsync(deviceId);

        await using var read = NewDbContext();
        var stored = await read.Devices.AsNoTracking().SingleAsync();

        Assert.Null(stored.ApiKey);
        Assert.NotNull(stored.ApiKeyId);
        Assert.NotNull(stored.ApiKeyHash);
    }

    [Theory]
    [InlineData("", "Kiosk")]
    [InlineData("   ", "Kiosk")]
    [InlineData("Gym Kiosk", "Teleporter")]
    public async Task A_device_outside_section_4_10_s_rules_is_refused(string name, string type)
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await DevicesOn(db).RegisterAsync(ValidDevice(name, type));

        Assert.Equal(DeviceWriteOutcome.ValidationFailed, response.Outcome);
        Assert.Null(response.IssuedKey);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.Devices.CountAsync());
    }

    /// <summary>
    /// A blank device type takes §4.10's most common reader rather than failing — the field is
    /// optional on the wire and a fixed kiosk is what an operator registering one from the back office
    /// almost always has.
    /// </summary>
    [Fact]
    public async Task A_blank_device_type_defaults_to_kiosk()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await DevicesOn(db).RegisterAsync(ValidDevice(type: null));

        Assert.Equal(DeviceWriteOutcome.Saved, response.Outcome);
        Assert.Equal(DeviceTypes.Kiosk, response.Device!.DeviceType);
    }

    // ----------------------------------------------------------------------------- rotate

    /// <summary>
    /// Rotation is a <b>hard cut</b>: the new key replaces the old in the same row, so the previous
    /// token stops working the instant this commits. There is no overlap window, deliberately — an
    /// overlap means a compromised key stays valid for as long as the window lasts, which is the one
    /// thing rotating is supposed to stop.
    /// </summary>
    [Fact]
    public async Task Rotating_issues_a_new_key_and_the_old_one_stops_working_immediately()
    {
        var schoolId = await ArrangeSchoolAsync();

        Guid deviceId;
        string firstKey;
        await using (var db = NewDbContext())
        {
            var registered = await DevicesOn(db).RegisterAsync(ValidDevice());
            deviceId = registered.Device!.Id;
            firstKey = registered.IssuedKey!.ApiKey;
        }

        // Control: the first key works before the rotation. Without this the assertion below could
        // pass because the key never worked at all.
        await using (var db = NewDbContext())
        {
            var before = await DeviceAuthOn(db).AuthenticateAsync(firstKey);
            Assert.Equal(DeviceAuthenticationOutcome.Authenticated, before.Outcome);
            Assert.Equal(deviceId, before.DeviceId);
            Assert.Equal(schoolId, before.SchoolId);
        }

        string secondKey;
        await using (var db = NewDbContext())
        {
            var rotated = await DevicesOn(db).RegenerateKeyAsync(deviceId);
            Assert.Equal(DeviceWriteOutcome.Saved, rotated.Outcome);
            secondKey = rotated.IssuedKey!.ApiKey;
        }

        Assert.NotEqual(firstKey, secondKey);

        await using (var db = NewDbContext())
        {
            Assert.Equal(
                DeviceAuthenticationOutcome.UnknownKey,
                (await DeviceAuthOn(db).AuthenticateAsync(firstKey)).Outcome);

            Assert.Equal(
                DeviceAuthenticationOutcome.Authenticated,
                (await DeviceAuthOn(db).AuthenticateAsync(secondKey)).Outcome);
        }
    }

    /// <summary>
    /// Rotating clears <c>ApiKeyRevokedAt</c>, because the row now holds a key that has not been
    /// revoked. That is how an operator recovers a device whose credential they burned.
    /// </summary>
    [Fact]
    public async Task Rotating_a_revoked_device_restores_it()
    {
        await ArrangeSchoolAsync();

        Guid deviceId;
        await using (var db = NewDbContext())
            deviceId = (await DevicesOn(db).RegisterAsync(ValidDevice())).Device!.Id;

        await using (var db = NewDbContext())
            await DevicesOn(db).RevokeKeyAsync(deviceId);

        string replacement;
        await using (var db = NewDbContext())
            replacement = (await DevicesOn(db).RegenerateKeyAsync(deviceId)).IssuedKey!.ApiKey;

        await using (var db = NewDbContext())
        {
            Assert.Equal(
                DeviceAuthenticationOutcome.Authenticated,
                (await DeviceAuthOn(db).AuthenticateAsync(replacement)).Outcome);
        }

        await using var read = NewDbContext();
        Assert.Null((await read.Devices.AsNoTracking().SingleAsync()).ApiKeyRevokedAt);
    }

    // ----------------------------------------------------------------------------- revoke

    /// <summary>
    /// <b>Revoking a credential and retiring a device are two different statements, and the columns
    /// stay distinct.</b> A lost handset's key is burned while the device it belonged to is still in
    /// the gym; a kiosk taken out of service keeps a perfectly good credential it is simply not using.
    /// Collapsing them would make "reactivate this device" silently mean "trust its old key again".
    /// </summary>
    [Fact]
    public async Task Revoking_a_key_leaves_the_device_active()
    {
        await ArrangeSchoolAsync();

        Guid deviceId;
        string apiKey;
        await using (var db = NewDbContext())
        {
            var registered = await DevicesOn(db).RegisterAsync(ValidDevice());
            deviceId = registered.Device!.Id;
            apiKey = registered.IssuedKey!.ApiKey;
        }

        await using (var db = NewDbContext())
        {
            var revoked = await DevicesOn(db).RevokeKeyAsync(deviceId);
            Assert.Equal(DeviceWriteOutcome.Saved, revoked.Outcome);
            Assert.True(revoked.Device!.IsActive);
            Assert.False(revoked.Device.HasActiveKey);
            Assert.NotNull(revoked.Device.ApiKeyRevokedAt);
        }

        await using (var db = NewDbContext())
        {
            // Identified, not permitted — which is what makes it a 403 rather than a 401 on the wire.
            var authentication = await DeviceAuthOn(db).AuthenticateAsync(apiKey);
            Assert.Equal(DeviceAuthenticationOutcome.Revoked, authentication.Outcome);
            Assert.Equal(deviceId, authentication.DeviceId);
        }
    }

    /// <summary>
    /// The other half of the split: deactivating through <c>PUT</c> must not touch the key columns.
    /// A device that is reactivated later still holds the credential it had, and an operator who
    /// wanted the credential gone has to say so.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_device_does_not_revoke_its_key()
    {
        await ArrangeSchoolAsync();

        Guid deviceId;
        await using (var db = NewDbContext())
            deviceId = (await DevicesOn(db).RegisterAsync(ValidDevice())).Device!.Id;

        await using (var db = NewDbContext())
        {
            var updated = await DevicesOn(db).UpdateAsync(deviceId, ValidDevice(isActive: false));
            Assert.Equal(DeviceWriteOutcome.Saved, updated.Outcome);
        }

        await using var read = NewDbContext();
        var stored = await read.Devices.AsNoTracking().SingleAsync();

        Assert.False(stored.IsActive);
        Assert.Null(stored.ApiKeyRevokedAt);
        Assert.True(stored.HasKey);
    }

    /// <summary>
    /// Revocation is idempotent: a client that retries after a timeout must not get a conflict on the
    /// one operation an operator runs when something has already gone wrong.
    /// </summary>
    [Fact]
    public async Task Revoking_twice_is_not_an_error()
    {
        await ArrangeSchoolAsync();

        Guid deviceId;
        await using (var db = NewDbContext())
            deviceId = (await DevicesOn(db).RegisterAsync(ValidDevice())).Device!.Id;

        DateTime firstRevokedAt;
        await using (var db = NewDbContext())
            firstRevokedAt = (await DevicesOn(db).RevokeKeyAsync(deviceId)).Device!.ApiKeyRevokedAt!.Value;

        await using (var db = NewDbContext())
        {
            var second = await DevicesOn(db).RevokeKeyAsync(deviceId);
            Assert.Equal(DeviceWriteOutcome.Saved, second.Outcome);
            Assert.Equal(firstRevokedAt, second.Device!.ApiKeyRevokedAt);
        }
    }

    // ------------------------------------------------------------------------- disclosure

    /// <summary>
    /// <b>No key-shaped value appears in any list or get response — asserted on the serialized bytes,
    /// not on the DTO's shape.</b>
    ///
    /// <para>
    /// Asserting the record has no <c>ApiKey</c> property would be a weaker test that passes against a
    /// future anonymous projection, a <c>[JsonExtensionData]</c> bag, or a controller that returns the
    /// entity. What must hold is that the secret is not in the response, and the response is a string.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_read_response_ever_carries_the_key()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidDevice());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var issuedBody = await created.Content.ReadAsStringAsync();
        using var issued = JsonDocument.Parse(issuedBody);
        var apiKey = issued.RootElement.GetProperty("apiKey").GetString()!;
        var deviceId = issued.RootElement.GetProperty("device").GetProperty("id").GetGuid();

        Assert.True(DeviceKey.TryParse(apiKey, out var keyId, out var secret));

        foreach (var route in new[] { Route, $"{Route}/{deviceId}" })
        {
            var response = await client.GetAsync(route);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain(apiKey, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("eams_dk_", body, StringComparison.OrdinalIgnoreCase);

            // And no stored hash either — it is not a credential, but publishing it turns an offline
            // guess into a verifiable one, which is the only thing the entropy argument relies on
            // nobody being able to do.
            var hash = DeviceKey.HashSecret(secret);
            Assert.DoesNotContain(hash, body, StringComparison.OrdinalIgnoreCase);

            // The public half *is* published, deliberately: it is how a log line and a row are matched
            // up. This assertion is the control that proves the three above are not passing because the
            // response is empty.
            Assert.Contains(keyId, body, StringComparison.Ordinal);
        }

        Assert.NotEqual(Guid.Empty, schoolId);
    }

    /// <summary>
    /// Rotation over HTTP returns the new key once. The 200 body is the second and last time a
    /// plaintext key crosses the wire for a given device.
    /// </summary>
    [Fact]
    public async Task Rotation_over_http_returns_the_key_once_and_the_get_afterwards_does_not()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidDevice());
        using var issued = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var deviceId = issued.RootElement.GetProperty("device").GetProperty("id").GetGuid();

        var rotated = await client.PostAsync($"{Route}/{deviceId}/regenerate-key", content: null);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        using var rotatedBody = JsonDocument.Parse(await rotated.Content.ReadAsStringAsync());
        var newKey = rotatedBody.RootElement.GetProperty("apiKey").GetString()!;
        Assert.StartsWith("eams_dk_", newKey, StringComparison.Ordinal);

        var afterwards = await client.GetAsync($"{Route}/{deviceId}");
        Assert.DoesNotContain(newKey, await afterwards.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_device_is_404_on_every_write()
    {
        await ArrangeSchoolAsync();
        var unknown = Guid.NewGuid();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{unknown}", ValidDevice())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"{Route}/{unknown}/regenerate-key", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"{Route}/{unknown}/revoke-key", content: null)).StatusCode);
    }
}
