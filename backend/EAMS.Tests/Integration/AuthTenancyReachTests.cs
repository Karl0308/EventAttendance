using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Authentication;
using EAMS.Api.Authorization;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Phase 6d — how far a valid token reaches across tenants.
///
/// <para>
/// <b>The specific hazard.</b> <c>AuthService</c> takes <c>IgnoreQueryFilters()</c> in exactly two
/// private methods, because the login that reads <c>Users</c> is what DETERMINES the tenant and a
/// filtered lookup there would refuse every operator outside whatever school happened to be resolved
/// first. <c>AuthTenantContainmentTests</c> proves the exemption is confined to those two methods by
/// reading the source. What it cannot prove is that no request can steer its way into one of them —
/// that is a question about the wire, and it is what this file answers.
/// </para>
///
/// <para>
/// The attack is run with a token minted by <see cref="ForgedJwt"/> using the real signing key. That
/// models a compromised key, which is the only way to obtain a token whose <c>sub</c> and
/// <c>school_id</c> disagree — and it is the right threat model for this question, because the value
/// of the tenant filter is precisely that it still holds when the claims are chosen by an attacker.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AuthTenancyReachTests : IntegrationTest
{
    public AuthTenancyReachTests(SqlServerFixture sql) : base(sql) { }

    private const string Password = "correct-horse-battery-staple";

    private sealed record TwoSchools(
        Guid FirstId, Guid FirstUserId, Guid SecondId, Guid SecondUserId);

    /// <summary>
    /// Two schools, each with one user and one student. "AAA" sorts first, so it is the school
    /// <c>DevelopmentSchoolContext</c> pins — a result that matched it could be the pin rather than
    /// the claim, so the assertions below are all about "ZZZ".
    /// </summary>
    private async Task<TwoSchools> ArrangeAsync()
    {
        Guid firstId, secondId;

        await using (var db = NewDbContext())
        {
            var first = TestData.NewSchool("AAA");
            var second = TestData.NewSchool("ZZZ");
            db.Schools.AddRange(first, second);
            await db.SaveChangesAsync();

            db.Students.Add(TestData.NewStudent(first.Id, "2023-1111", lastName: "Alpha"));
            db.Students.Add(TestData.NewStudent(second.Id, "2023-9999", lastName: "Omega"));
            await db.SaveChangesAsync();

            firstId = first.Id;
            secondId = second.Id;
        }

        return new TwoSchools(
            firstId, await CreateUserAsync(firstId, "first@usa.edu.ph", Password),
            secondId, await CreateUserAsync(secondId, "second@usa.edu.ph", Password));
    }

    // -------------------------------------------------------------- reading rows across a tenant

    private static async Task<List<string?>> LastNamesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/students");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return [.. body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("lastName").GetString())];
    }

    /// <summary>
    /// <b>A gated route serves the token's school, not the development pin.</b> This used to be a
    /// tripwire asserting the opposite: while the roster was open, the authorization middleware never
    /// ran the Bearer scheme on it, so <c>HttpContext.User</c> stayed empty and
    /// <see cref="ClaimsSchoolContext"/> fell through to the pin — the lowest school <c>Code</c>, "AAA" —
    /// and a signed-in operator of "ZZZ" read "AAA"'s students. The roster now demands a Bearer token,
    /// so the tenant is the claim, and each operator reads their own school and only their own.
    /// </summary>
    [Fact]
    public async Task A_gated_endpoint_resolves_its_tenant_from_the_token_and_not_from_the_pin()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var second = new AuthApiClient(factory);
        using var first = new AuthApiClient(factory);

        var login = await second.LoginAsync("second@usa.edu.ph", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.LoginAsync("first@usa.edu.ph", Password)).StatusCode);

        // The token really does name "ZZZ" — so what follows is about the route, not the claim.
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(world.SecondId, body.GetProperty("user").GetProperty("schoolId").GetGuid());

        using var secondClient = factory.CreateClient().WithBearer(second.AccessToken!);
        using var firstClient = factory.CreateClient().WithBearer(first.AccessToken!);

        Assert.Equal(["Omega"], await LastNamesAsync(secondClient));
        Assert.Equal(["Alpha"], await LastNamesAsync(firstClient));
    }

    // ------------------------------------------------- reaching another tenant's user row via /me

    [Fact]
    public async Task A_token_naming_another_schools_user_reads_no_profile()
    {
        var world = await ArrangeAsync();

        // sub names the SECOND school's user; school_id names the FIRST school. A token like this is
        // only obtainable with the signing key, and that is the point: the tenant filter has to be
        // what stops it, not the difficulty of minting it.
        var crossed = ForgedJwt.Mint(
            ForgedJwt.ClaimsFor(world.SecondUserId, world.FirstId, EamsPermissions.StudentsRead));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithBearer(crossed);

        var response = await client.GetAsync("/api/v1/auth/me");

        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"A token whose school_id and sub name different schools read a profile " +
            $"({(int)response.StatusCode}). GetProfileAsync must stay tenant-filtered: it is the " +
            $"method that exists specifically so that /auth/me cannot reach the IgnoreQueryFilters " +
            $"exemption LoadProfileAsync takes for login.");
    }

    /// <summary>
    /// The control for the test above. The same forged token with a <em>consistent</em> tenant reads
    /// the profile fine — so the 404 was the query filter refusing a cross-tenant read, and not a
    /// forged token being rejected, a missing user, or <c>/auth/me</c> being broken.
    /// </summary>
    [Fact]
    public async Task The_same_token_with_a_consistent_tenant_reads_the_profile()
    {
        var world = await ArrangeAsync();

        var consistent = ForgedJwt.Mint(
            ForgedJwt.ClaimsFor(world.SecondUserId, world.SecondId, EamsPermissions.StudentsRead));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithBearer(consistent);

        var response = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("second@usa.edu.ph", body.GetProperty("email").GetString());
        Assert.Equal(world.SecondId, body.GetProperty("schoolId").GetGuid());
    }

    [Fact]
    public async Task A_token_naming_another_schools_user_cannot_change_that_users_password()
    {
        var world = await ArrangeAsync();

        var crossed = ForgedJwt.Mint(
            ForgedJwt.ClaimsFor(world.SecondUserId, world.FirstId, EamsPermissions.StudentsRead));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithBearer(crossed);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/change-password",
            new { currentPassword = Password, newPassword = "a-completely-different-password" });

        // 401, because ChangePasswordAsync opens with the same tenant-filtered GetProfileAsync. The
        // status matters less than the row: the second school's password must be unchanged.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var session = new AuthApiClient(factory);
        Assert.Equal(
            HttpStatusCode.OK,
            (await session.LoginAsync("second@usa.edu.ph", Password)).StatusCode);
    }

    // --------------------------------------------------------------- the login exemption, contained

    /// <summary>
    /// The exemption itself, at the wire. Both schools' operators can sign in — which is what
    /// <c>LoadProfileAsync</c>'s <c>IgnoreQueryFilters</c> is FOR — and each one's token then resolves
    /// its own tenant. The pair is what distinguishes "the exemption works" from "the exemption is a
    /// hole": it is reachable only before a tenant exists, and never after one does.
    /// </summary>
    [Fact]
    public async Task Both_schools_can_sign_in_and_each_token_resolves_its_own_tenant()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        foreach (var (email, expected) in new[]
                 {
                     ("first@usa.edu.ph", world.FirstId),
                     ("second@usa.edu.ph", world.SecondId),
                 })
        {
            using var session = new AuthApiClient(factory);
            var login = await session.LoginAsync(email, Password);

            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(expected, body.GetProperty("user").GetProperty("schoolId").GetGuid());

            var probe = await session.ProbeAsync();
            var seams = await probe.Content.ReadFromJsonAsync<AuthProbeController.Seams>();

            Assert.NotNull(seams);
            Assert.Equal(expected, seams.SchoolId);
        }
    }

    [Fact]
    public async Task A_users_own_sessions_are_not_visible_to_another_schools_logout()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using var victim = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await victim.LoginAsync("second@usa.edu.ph", Password)).StatusCode);
        var victimCookie = victim.Cookie(AuthCookies.RefreshCookieName)!;

        using var attacker = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await attacker.LoginAsync("first@usa.edu.ph", Password)).StatusCode);

        // The attacker's own access token, with the victim's refresh cookie attached. LogoutAsync
        // checks the presented token's user id against the caller's before revoking anything —
        // without that check, holding any valid session would be a way to end a stranger's.
        attacker.SetCookie(AuthCookies.RefreshCookieName, victimCookie);

        Assert.Equal(HttpStatusCode.NoContent, (await attacker.LogoutAsync()).StatusCode);

        await using var db = NewDbContext();
        var victimTokens = await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == world.SecondUserId)
            .ToListAsync();

        Assert.All(victimTokens, t => Assert.Null(t.RevokedAt));
    }
}
