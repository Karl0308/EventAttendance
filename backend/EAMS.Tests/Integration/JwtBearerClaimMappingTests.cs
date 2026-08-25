using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Authorization;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b><c>MapInboundClaims</c> must stay <c>false</c>, and this is the file that says why in a form
/// that fails rather than in a comment that does not.</b>
///
/// <para>
/// The default is <c>true</c>. What it does is rewrite well-known short JWT claim names into the
/// WS-Federation URIs <c>ClaimTypes.*</c> uses — <c>sub</c> becomes
/// <c>http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier</c>. It leaves
/// <c>school_id</c> and <c>perm</c> alone, because neither is in its map.
/// </para>
///
/// <para>
/// <b>That asymmetry is the trap.</b> With the default: every permission policy still passes,
/// <c>ClaimsSchoolContext</c> still resolves the right tenant, <c>/auth/me</c> still returns the
/// right permissions — and <c>ClaimsCurrentUser.UserId</c> silently returns <c>null</c>, because it
/// looks for <c>sub</c> and <c>sub</c> is gone. Every audited write on the endpoint Technical Plan
/// §6.4 specifically calls audited would be attributed to nobody, and the entire suite would be
/// green, because nothing else in the system reads that claim.
/// </para>
///
/// <para>
/// So there are two assertions here and both are needed. The first pins the flag, which is what a
/// future edit would change. The second pins its <em>effect</em> through a Bearer-gated probe
/// (<see cref="AuthProbeController"/>), which is what would still catch it if the mapping moved
/// somewhere else — a different handler, a global claims transformation, an upgraded package with a
/// new default.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class JwtBearerClaimMappingTests : IntegrationTest
{
    public JwtBearerClaimMappingTests(SqlServerFixture sql) : base(sql) { }

    private const string Email = "registrar@usa.edu.ph";
    private const string Password = "correct-horse-battery-staple";

    private async Task<(Guid SchoolId, Guid UserId)> ArrangeAsync()
    {
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        return (schoolId, await CreateUserAsync(schoolId, Email, Password));
    }

    [Fact]
    public void The_bearer_scheme_does_not_map_inbound_claims()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var _ = factory.CreateClient(); // Builds the host.

        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.False(
            options.MapInboundClaims,
            "JwtBearerOptions.MapInboundClaims is true. The default rewrites 'sub' to a " +
            "WS-Federation URI while leaving 'school_id' and 'perm' untouched, so every policy still " +
            "passes, every tenant filter still resolves, and ICurrentUser.UserId silently returns " +
            "null — NULL attribution on every audited write, with a fully green suite. Set it back " +
            "to false in Program.cs.");
    }

    [Fact]
    public async Task An_authenticated_request_resolves_a_user_id_a_tenant_and_its_permissions()
    {
        var (schoolId, userId) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        var response = await client.ProbeAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var seams = await response.Content.ReadFromJsonAsync<AuthProbeController.Seams>();
        Assert.NotNull(seams);

        // The one that breaks silently under MapInboundClaims = true.
        Assert.Equal(userId, seams.UserId);

        // The two that do not, asserted beside it so the failure message distinguishes "claims are
        // broken" from "only sub is".
        Assert.Equal(schoolId, seams.SchoolId);
        Assert.NotEmpty(seams.Permissions);
    }

    [Fact]
    public async Task The_tenant_a_token_names_is_the_one_the_query_filter_uses()
    {
        // Two schools, so ClaimsSchoolContext's fallback to the pinned development school cannot be
        // mistaken for the claim working. The pin is the lowest school Code, so the user is created in
        // the OTHER one — a token resolving the pin would answer the wrong school here and be
        // indistinguishable from a correct answer in a one-school database.
        Guid pinnedId, otherId;
        await using (var db = NewDbContext())
        {
            var pinned = TestData.NewSchool("AAA");
            var other = TestData.NewSchool("ZZZ");
            db.Schools.AddRange(pinned, other);
            await db.SaveChangesAsync();
            pinnedId = pinned.Id;
            otherId = other.Id;
        }

        var userId = await CreateUserAsync(otherId, Email, Password);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        var seams = await (await client.ProbeAsync())
            .Content.ReadFromJsonAsync<AuthProbeController.Seams>();

        Assert.NotNull(seams);
        Assert.Equal(otherId, seams.SchoolId);
        Assert.NotEqual(pinnedId, seams.SchoolId);
        Assert.Equal(userId, seams.UserId);
    }

    [Fact]
    public async Task Login_finds_a_user_outside_the_pinned_tenant()
    {
        // The other half of the same hazard, and the one that misbehaves in opposite directions
        // depending on the deployment: `Users` carries a tenant filter, and the login query is what
        // DETERMINES the tenant. Behind the pin a filtered lookup would see only the pinned school's
        // users, so every other school's operator would get a perfectly correct-looking 401 — and once
        // the pin is deleted the same code would start working. In a one-school database the two are
        // indistinguishable. AuthService contains the exemption to exactly two methods; this is what
        // proves the exemption is actually taken.
        Guid otherId;
        await using (var db = NewDbContext())
        {
            db.Schools.AddRange(TestData.NewSchool("AAA"), TestData.NewSchool("ZZZ"));
            await db.SaveChangesAsync();
            otherId = await db.Schools.Where(s => s.Code == "ZZZ").Select(s => s.Id).SingleAsync();
        }

        await CreateUserAsync(otherId, Email, Password);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var response = await client.LoginAsync(Email, Password);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"A user belonging to a school other than the pinned one could not sign in " +
            $"({(int)response.StatusCode}). The login lookup is being tenant-filtered, which means " +
            $"it is filtered by whatever tenant happened to be resolved BEFORE the credential " +
            $"identified one.");
    }

    [Fact]
    public async Task The_token_carries_the_short_claim_names_this_system_names_once()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        var payload = DecodePayload(client.AccessToken!);

        // Read off EamsClaimTypes rather than spelled here — the constants are the contract the
        // DeviceKey scheme also emits against, and a literal in this test would let the two drift
        // while both halves passed their own assertions.
        Assert.True(payload.TryGetProperty(EamsClaimTypes.Subject, out var sub));
        Assert.StartsWith("user:", sub.GetString()!, StringComparison.Ordinal);

        Assert.True(payload.TryGetProperty(EamsClaimTypes.SchoolId, out _));
        Assert.True(payload.TryGetProperty(EamsClaimTypes.Permission, out var perms));
        Assert.Equal(JsonValueKind.Array, perms.ValueKind);

        // And nothing WS-Federation shaped, in either direction. An outbound map would have turned
        // these into URIs at mint time, which the inbound flag would then never see.
        Assert.DoesNotContain("schemas.xmlsoap.org", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("schemas.microsoft.com", payload.ToString(), StringComparison.Ordinal);
    }

    private static JsonElement DecodePayload(string jwt)
    {
        var segment = jwt.Split('.')[1];
        var padded = segment.PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=')
            .Replace('-', '+').Replace('_', '/');

        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement.Clone();
    }
}
