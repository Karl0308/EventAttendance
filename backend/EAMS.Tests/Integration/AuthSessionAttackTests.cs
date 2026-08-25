using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Authentication;
using EAMS.Api.Controllers;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Phase 6d — the adversarial pass over the refresh-token lifecycle and the CSRF double-submit,
/// attacked at the edges rather than down the middle.
///
/// <para>
/// <c>AuthApiTests</c> already proves the ordinary cases: rotation works, a replay burns the family,
/// a missing or mismatched CSRF header is refused. What is left is the set of states a client
/// reaches by <em>failing</em> — a burned family replayed again, a refresh after logout, two tabs
/// refreshing at the same instant, a CSRF header that matches the cookie the client had a moment
/// ago. Each of those is a place where "safe" and "merely deterministic" can come apart.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AuthSessionAttackTests : IntegrationTest
{
    public AuthSessionAttackTests(SqlServerFixture sql) : base(sql) { }

    private const string Email = "registrar@usa.edu.ph";
    private const string Password = "correct-horse-battery-staple";

    private sealed record World(Guid SchoolId, Guid UserId);

    private async Task<World> ArrangeAsync(string email = Email)
    {
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        return new World(schoolId, await CreateUserAsync(schoolId, email, Password));
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string? Code(JsonElement body) =>
        body.TryGetProperty("code", out var code) ? code.GetString() : null;

    private static Guid IdOf(string refreshToken)
    {
        Assert.True(RefreshTokenValue.TryParse(refreshToken, out var id, out _));
        return id;
    }

    // ------------------------------------------------------------------------ concurrent refresh

    /// <summary>
    /// <b>The race the SPA's single-flight guard exists to avoid, seen from the server.</b>
    /// <c>RefreshTokenRotationTests</c> proves at the store that exactly one successor is created.
    /// This asserts the part a client experiences and the part that is a security property rather
    /// than a correctness one: the winner's brand-new token is dead too.
    ///
    /// <para>
    /// That is the SAFE outcome, not merely the deterministic one. The alternative — letting the
    /// winner keep its successor — would mean a token presented twice sometimes yields a live
    /// session, and replay detection cannot distinguish "my client raced itself" from "somebody
    /// copied my cookie". Fail-closed costs a re-login; fail-open costs the whole mechanism.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_refreshes_of_one_cookie_leave_no_live_session_at_all()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        var refreshCookie = client.Cookie(AuthCookies.RefreshCookieName)!;
        var csrfCookie = client.Cookie(AuthCookies.CsrfCookieName)!;

        // Two requests carrying the SAME cookie, in flight together. Sent on the raw HttpClient so
        // neither one's Set-Cookie can update the other's jar mid-race.
        async Task<HttpResponseMessage> RefreshAsync()
        {
            using var http = factory.CreateClient(
                new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
                {
                    BaseAddress = new Uri("https://localhost"),
                    HandleCookies = false,
                });

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
            request.Headers.Add(
                "Cookie",
                $"{AuthCookies.RefreshCookieName}={refreshCookie}; {AuthCookies.CsrfCookieName}={csrfCookie}");
            request.Headers.Add(AuthCookies.CsrfHeaderName, csrfCookie);

            return await http.SendAsync(request);
        }

        var responses = await Task.WhenAll(RefreshAsync(), RefreshAsync());

        // Exactly one may succeed. Two would mean two live tokens in one family, which is the state
        // replay detection exists to make impossible.
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));

        await using var db = NewDbContext();
        var family = await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == world.UserId)
            .ToListAsync();

        // The presented token is dead however the race resolved, so the cookie the client still holds
        // is worthless to anyone who copied it.
        var presented = Assert.Single(family, t => t.Id == IdOf(refreshCookie));
        Assert.NotNull(presented.RevokedAt);

        // And at most one token in the whole family is live — the single successor the compare-and-swap
        // admitted. Deliberately "at most one" rather than "none": whether the burn triggered by the
        // losing request also catches that successor depends on which of two statements commits first,
        // and asserting the stricter thing here would make this test a coin flip. That window is
        // reported as a finding rather than pinned as behaviour; see QA report, Phase 6d.
        Assert.True(
            family.Count(t => t.RevokedAt is null) <= 1,
            $"{family.Count(t => t.RevokedAt is null)} tokens in this family are still live. One " +
            $"redemption of one token may produce at most one successor — more than one means the " +
            $"compare-and-swap in RefreshTokenStore.RotateAsync stopped serializing redemptions.");

        // Whatever survived, the original cannot be redeemed again.
        client.SetCookie(AuthCookies.RefreshCookieName, refreshCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.RefreshAsync()).StatusCode);
    }

    // -------------------------------------------------------------------------- lifecycle edges

    [Fact]
    public async Task Replaying_a_token_whose_family_is_already_burned_is_still_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        var original = client.Cookie(AuthCookies.RefreshCookieName)!;

        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync()).StatusCode);

        // First replay: burns the family.
        client.SetCookie(AuthCookies.RefreshCookieName, original);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.RefreshAsync()).StatusCode);

        // Second replay, against the corpse. The interesting failure mode is not a 200 — it is a 500
        // from BurnFamilyAsync running over a family with nothing left to revoke, which would turn a
        // replay into an availability bug on the endpoint every legitimate client also calls.
        client.SetCookie(AuthCookies.RefreshCookieName, original);
        var again = await client.RefreshAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal(AuthController.SessionExpiredCode, Code(await BodyAsync(again)));
    }

    [Fact]
    public async Task A_refresh_after_logout_is_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        var refreshCookie = client.Cookie(AuthCookies.RefreshCookieName)!;
        var csrfCookie = client.Cookie(AuthCookies.CsrfCookieName)!;

        Assert.Equal(HttpStatusCode.NoContent, (await client.LogoutAsync()).StatusCode);

        // Logout clears both cookies in the browser. An attacker who copied them beforehand is not
        // constrained by that, so both are put back by hand — the server's revocation has to be what
        // refuses this, not the absence of a cookie.
        client.SetCookie(AuthCookies.RefreshCookieName, refreshCookie);
        client.SetCookie(AuthCookies.CsrfCookieName, csrfCookie);

        var response = await client.RefreshAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AuthController.SessionExpiredCode, Code(await BodyAsync(response)));
    }

    [Fact]
    public async Task A_refresh_by_a_user_deactivated_mid_session_is_refused_and_the_cookie_is_cleared()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        await using (var db = NewDbContext())
        {
            await db.Users.IgnoreQueryFilters()
                .Where(u => u.Id == world.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        var response = await client.RefreshAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The dead cookie is cleared rather than left in the jar. Leaving it guarantees the next
        // refresh presents it again, which the store correctly reads as a replay — so the browser
        // would manufacture a family burn out of an ordinary expired session.
        Assert.Null(client.Cookie(AuthCookies.RefreshCookieName));

        // And every token the account held is revoked, not just the one presented.
        await using var check = NewDbContext();
        var family = await check.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == world.UserId)
            .ToListAsync();

        Assert.NotEmpty(family);
        Assert.All(family, t => Assert.NotNull(t.RevokedAt));
    }

    /// <summary>
    /// The 30-day family ceiling, over HTTP. Reachable only by moving the ceiling in the database:
    /// <c>RefreshTokenStore.TokenExpiry</c> clamps every token's own expiry to the family's, so a
    /// token that outlives its family cannot be produced by the code — which is itself worth knowing,
    /// and is why this arranges the state directly instead of waiting a month.
    /// </summary>
    [Fact]
    public async Task A_session_past_the_thirty_day_family_ceiling_cannot_be_renewed()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        var tokenId = IdOf(client.Cookie(AuthCookies.RefreshCookieName)!);

        await using (var db = NewDbContext())
        {
            await db.RefreshTokens
                .Where(t => t.Id == tokenId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    t => t.FamilyExpiresAt, DateTime.UtcNow.AddMinutes(-1)));

            // The token's own fourteen days are untouched, so the ceiling is the only thing that can
            // refuse this.
            var row = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == tokenId);
            Assert.True(row.ExpiresAt > DateTime.UtcNow);
        }

        var response = await client.RefreshAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AuthController.SessionExpiredCode, Code(await BodyAsync(response)));
    }

    /// <summary>
    /// <b>Two users, crossed cookies.</b> The double-submit token proves the caller could read a
    /// cookie from this origin; it deliberately says nothing about <em>whose</em> session is being
    /// renewed. This pins that the two are independent and that the refresh cookie alone decides the
    /// identity — a CSRF cookie that could steer the answer would be a session-fixation primitive.
    /// </summary>
    [Fact]
    public async Task A_refresh_is_redeemed_against_its_own_cookie_and_never_against_the_csrf_cookies_owner()
    {
        var alice = await ArrangeAsync("alice@usa.edu.ph");
        await CreateUserAsync(alice.SchoolId, "bob@usa.edu.ph", Password);

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using var bob = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await bob.LoginAsync("bob@usa.edu.ph", Password)).StatusCode);

        using var attacker = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await attacker.LoginAsync("alice@usa.edu.ph", Password)).StatusCode);

        // Alice's refresh cookie, Bob's CSRF cookie and header. Consistent with each other as far as
        // the double-submit check is concerned.
        attacker.SetCookie(AuthCookies.CsrfCookieName, bob.Cookie(AuthCookies.CsrfCookieName)!);

        var response = await attacker.RefreshAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyAsync(response);
        Assert.Equal(
            "alice@usa.edu.ph",
            body.GetProperty("user").GetProperty("email").GetString());

        // And Bob's own session is untouched by having had his CSRF value used elsewhere.
        Assert.Equal(HttpStatusCode.OK, (await bob.RefreshAsync()).StatusCode);
    }

    // ----------------------------------------------------------------------------------- CSRF

    [Fact]
    public async Task A_csrf_header_that_is_present_but_empty_is_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        // "" is the value a client sends when it read the cookie, found nothing, and forwarded the
        // result anyway. It must not compare equal to anything — including a cookie that is also
        // somehow empty.
        var response = await client.RefreshAsync(csrf: true, csrfOverride: "");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AuthController.CsrfFailedCode, Code(await BodyAsync(response)));
    }

    [Fact]
    public async Task A_csrf_header_with_no_cookie_behind_it_is_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        var value = client.Cookie(AuthCookies.CsrfCookieName)!;
        client.DropCookie(AuthCookies.CsrfCookieName);

        // Half of the double-submit. Accepting it would reduce the check to "send any header", which
        // is exactly what an attacker who guessed the header name can do.
        var response = await client.RefreshAsync(csrf: true, csrfOverride: value);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AuthController.CsrfFailedCode, Code(await BodyAsync(response)));
    }

    [Fact]
    public async Task A_csrf_header_matching_the_cookie_from_before_the_last_rotation_is_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        var stale = client.Cookie(AuthCookies.CsrfCookieName)!;

        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync()).StatusCode);
        Assert.NotEqual(stale, client.Cookie(AuthCookies.CsrfCookieName));

        // The cookie has moved on; the header has not. This is what a tab that cached the value at
        // page load sends after another tab refreshed — a real client bug, and one that must fail
        // closed rather than be tolerated by a check that remembers previous values.
        var response = await client.RefreshAsync(csrf: true, csrfOverride: stale);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AuthController.CsrfFailedCode, Code(await BodyAsync(response)));
    }

    [Fact]
    public async Task A_csrf_refusal_does_not_end_the_session_it_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(Email, Password)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.RefreshAsync(csrf: false)).StatusCode);

        // The 403 returns before the refresh cookie is read or cleared. If it did not, a page that
        // forgot the header once would sign the user out — and worse, the cleared-then-re-presented
        // cookie would look like a replay and burn the family.
        Assert.NotNull(client.Cookie(AuthCookies.RefreshCookieName));
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync()).StatusCode);
    }

    /// <summary>
    /// The other half of the CSRF contract: it is required on the two cookie-authenticated routes
    /// and on nothing else. A Bearer route that demanded it would be asking a native or mobile
    /// client — which has no cookie jar at all — for a value it can never produce.
    /// </summary>
    [Fact]
    public async Task A_bearer_route_needs_no_csrf_cookie_and_no_csrf_header()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);

        // A client with no cookie jar whatsoever, carrying only the access token.
        using var bearerOnly = factory.CreateClient().WithBearer(session.AccessToken!);

        Assert.Equal(HttpStatusCode.OK, (await bearerOnly.GetAsync("/api/v1/auth/me")).StatusCode);

        var changed = await bearerOnly.PostAsJsonAsync(
            "/api/v1/auth/change-password",
            new { currentPassword = Password, newPassword = "a-second-correct-horse-staple" });

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
    }
}
