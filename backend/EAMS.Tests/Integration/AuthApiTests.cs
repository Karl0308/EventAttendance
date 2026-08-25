using System.Net;
using System.Text.Json;
using EAMS.Api.Authentication;
using EAMS.Api.Authorization;
using EAMS.Api.Controllers;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Identity;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Technical Plan §11's <c>/auth</c> surface, end to end over the real host (Phase 6b).
///
/// <para>
/// Everything here runs through <see cref="EamsApiFactory"/>, which boots in Production so the dev
/// seed is skipped and every row these tests assert on is one they wrote. Cookies are driven by hand
/// — see <see cref="AuthApiClient"/> for why an automatic cookie container would both fail for the
/// wrong reason and be unable to misbehave on purpose.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AuthApiTests : IntegrationTest
{
    public AuthApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Email = "principal@usa.edu.ph";
    private const string Password = "correct-horse-battery-staple";

    private sealed record World(Guid SchoolId, Guid UserId);

    private async Task<World> ArrangeAsync(
        string email = Email, string password = Password,
        string role = EamsRoleNames.Organizer, bool active = true)
    {
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        var userId = await CreateUserAsync(schoolId, email, password, role);

        if (!active)
        {
            await using var deactivate = NewDbContext();
            await deactivate.Users.IgnoreQueryFilters()
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        return new World(schoolId, userId);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string? Code(JsonElement body) =>
        body.TryGetProperty("code", out var code) ? code.GetString() : null;

    // ------------------------------------------------------------------------------- login: happy

    [Fact]
    public async Task Login_returns_an_access_token_and_the_effective_permission_set()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var response = await client.LoginAsync(Email, Password);
        var body = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));

        var user = body.GetProperty("user");
        Assert.Equal(world.UserId, user.GetProperty("id").GetGuid());
        Assert.Equal(world.SchoolId, user.GetProperty("schoolId").GetGuid());
        Assert.Equal(Email, user.GetProperty("email").GetString());

        // The Organizer grant matrix, verbatim from EamsRoles rather than re-listed here — a literal
        // list would pass while the seed and the matrix disagreed, which is the exact drift
        // EamsRoles.ReferenceData exists to make impossible.
        var expected = EamsRoles.ReferenceData.Roles
            .Single(r => r.Name == EamsRoleNames.Organizer).PermissionCodes
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            expected,
            user.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!));
    }

    [Fact]
    public async Task The_login_response_body_never_carries_the_refresh_token()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var response = await client.LoginAsync(Email, Password);
        var raw = await response.Content.ReadAsStringAsync();

        // Asserted on the serialized bytes rather than on the DTO's shape, the way
        // DeviceLifecycleTests asserts a device key never appears in a read response. A field added
        // back to AuthTokenResponse "for convenience" fails here, not at review.
        Assert.DoesNotContain(RefreshTokenValue.TokenPrefix, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", raw, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------------------- login: refusals

    [Theory]
    [InlineData("wrong-password", Email, "not-the-password")]
    [InlineData("unknown-email", "nobody@usa.edu.ph", Password)]
    public async Task A_bad_credential_is_one_401_with_one_code(string _, string email, string password)
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var response = await client.LoginAsync(email, password);
        var body = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AuthController.InvalidCredentialsCode, Code(body));
        Assert.Empty(client.LastSetCookies);
    }

    [Fact]
    public async Task An_inactive_account_is_refused_indistinguishably_from_a_wrong_password()
    {
        await ArrangeAsync(active: false);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var inactive = await BodyAsync(await client.LoginAsync(Email, Password));
        var wrong = await BodyAsync(await client.LoginAsync(Email, "not-the-password"));

        // Title and detail as well as the code: a client that branched on prose would still be an
        // enumeration oracle, and prose is the half most likely to be "improved" into one.
        Assert.Equal(Code(wrong), Code(inactive));
        Assert.Equal(wrong.GetProperty("title").GetString(), inactive.GetProperty("title").GetString());
        Assert.Equal(wrong.GetProperty("detail").GetString(), inactive.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Every_auth_failure_carries_a_trace_id()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var body = await BodyAsync(await client.LoginAsync(Email, "not-the-password"));

        Assert.True(body.TryGetProperty("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));
    }

    // ------------------------------------------------------------------------------- the cookies

    [Fact]
    public async Task The_refresh_cookie_is_http_only_secure_strict_and_path_scoped()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        var header = client.SetCookieHeader(AuthCookies.RefreshCookieName);
        Assert.NotNull(header);

        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"path={AuthCookies.RefreshCookiePathSuffix}", header, StringComparison.OrdinalIgnoreCase);

        // The cookie's value is the token itself, so the token never reaches a body. Its shape is the
        // 6a contract, checked here so a change to RefreshTokenValue that broke the cookie is caught
        // at the wire rather than at the store.
        Assert.StartsWith(
            RefreshTokenValue.TokenPrefix,
            client.Cookie(AuthCookies.RefreshCookieName),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_csrf_cookie_is_readable_secure_strict_and_rooted()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        var header = client.SetCookieHeader(AuthCookies.CsrfCookieName);
        Assert.NotNull(header);

        // NOT httpOnly, deliberately — the SPA has to read it to echo it, and that is the mechanism.
        Assert.DoesNotContain("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);

        // Path=/ and not the refresh cookie's path. Under IIS the SPA is a sibling application, so a
        // cookie scoped to the API's path is invisible to document.cookie on the SPA's page — and a
        // CSRF cookie the client cannot read is a check that always fails. See AuthCookies.Issue.
        Assert.Contains("path=/;", header + ";", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AuthCookies.RefreshCookiePathSuffix, header, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------------- refreshing

    [Fact]
    public async Task Refresh_rotates_the_cookie_and_mints_a_new_access_token()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);
        var firstRefresh = client.Cookie(AuthCookies.RefreshCookieName);
        var firstAccess = client.AccessToken;

        var response = await client.RefreshAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(firstRefresh, client.Cookie(AuthCookies.RefreshCookieName));
        Assert.NotNull(client.AccessToken);
        Assert.NotEqual(firstAccess, client.AccessToken);
    }

    [Fact]
    public async Task Refresh_re_reads_the_permission_set_rather_than_carrying_it_forward()
    {
        var world = await ArrangeAsync(role: EamsRoleNames.Organizer);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        // Demote the account between the login and the refresh, exactly as an administrator would.
        await using (var demote = NewDbContext())
        {
            var viewer = await demote.Roles.SingleAsync(r => r.Name == EamsRoleNames.Viewer);
            await demote.UserRoles.IgnoreQueryFilters()
                .Where(ur => ur.UserId == world.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(ur => ur.RoleId, viewer.Id));
        }

        var body = await BodyAsync(await client.RefreshAsync());

        var expected = EamsRoles.ReferenceData.Roles
            .Single(r => r.Name == EamsRoleNames.Viewer).PermissionCodes
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            expected,
            body.GetProperty("user").GetProperty("permissions").EnumerateArray()
                .Select(p => p.GetString()!));
    }

    [Fact]
    public async Task Replaying_a_rotated_refresh_token_burns_the_whole_family()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);
        var stolen = client.Cookie(AuthCookies.RefreshCookieName)!;

        // The legitimate client rotates. The thief's copy is now the predecessor.
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync()).StatusCode);

        var live = client.Cookie(AuthCookies.RefreshCookieName)!;

        client.SetCookie(AuthCookies.RefreshCookieName, stolen);
        var replay = await client.RefreshAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(AuthController.SessionExpiredCode, Code(await BodyAsync(replay)));

        // And the successor the legitimate client was holding is dead too. That is the point: a
        // replay is indistinguishable from a theft, so the family burns and the real user re-signs in.
        client.SetCookie(AuthCookies.RefreshCookieName, live);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.RefreshAsync()).StatusCode);

        await using var read = NewDbContext();
        Assert.Empty(await read.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == world.UserId && t.RevokedAt == null)
            .ToListAsync());
    }

    [Fact]
    public async Task Refresh_without_a_cookie_says_so_distinctly()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);
        client.DropCookie(AuthCookies.RefreshCookieName);

        var response = await client.RefreshAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AuthController.SessionMissingCode, Code(await BodyAsync(response)));
    }

    // --------------------------------------------------------------------------------------- CSRF

    [Fact]
    public async Task Refresh_without_the_csrf_header_is_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);
        var before = client.Cookie(AuthCookies.RefreshCookieName);

        var response = await client.RefreshAsync(csrf: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AuthController.CsrfFailedCode, Code(await BodyAsync(response)));

        // Refused before anything rotated: the session the cross-site request tried to spend is
        // untouched, so a blocked CSRF attempt does not also log the real user out.
        Assert.Equal(before, client.Cookie(AuthCookies.RefreshCookieName));
    }

    [Fact]
    public async Task Refresh_with_a_mismatched_csrf_header_is_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        var response = await client.RefreshAsync(
            csrfOverride: new string('0', client.Cookie(AuthCookies.CsrfCookieName)!.Length));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AuthController.CsrfFailedCode, Code(await BodyAsync(response)));
    }

    [Fact]
    public async Task Logout_without_the_csrf_header_is_refused_and_revokes_nothing()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        var response = await client.LogoutAsync(csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var read = NewDbContext();
        Assert.NotEmpty(await read.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == world.UserId && t.RevokedAt == null)
            .ToListAsync());
    }

    [Fact]
    public async Task The_csrf_token_is_rotated_on_every_issue()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);
        var first = client.Cookie(AuthCookies.CsrfCookieName);

        await client.RefreshAsync();

        Assert.NotNull(first);
        Assert.NotEqual(first, client.Cookie(AuthCookies.CsrfCookieName));
    }

    // ------------------------------------------------------------------------------------- logout

    [Fact]
    public async Task Logout_revokes_this_session_and_clears_both_cookies()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);
        var response = await client.LogoutAsync();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(client.Cookie(AuthCookies.RefreshCookieName));
        Assert.Null(client.Cookie(AuthCookies.CsrfCookieName));

        await using var read = NewDbContext();
        Assert.Empty(await read.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == world.UserId && t.RevokedAt == null)
            .ToListAsync());
    }

    [Fact]
    public async Task Logout_ends_only_the_session_that_called_it()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var laptop = new AuthApiClient(factory);
        using var phone = new AuthApiClient(factory);

        await laptop.LoginAsync(Email, Password);
        await phone.LoginAsync(Email, Password);

        Assert.Equal(HttpStatusCode.NoContent, (await laptop.LogoutAsync()).StatusCode);

        // The phone is untouched — signing out on one device must not sign a user out of another.
        Assert.Equal(HttpStatusCode.OK, (await phone.RefreshAsync()).StatusCode);

        await using var read = NewDbContext();
        var families = await read.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == world.UserId && t.RevokedAt == null)
            .Select(t => t.FamilyId)
            .Distinct()
            .ToListAsync();

        Assert.Single(families);
    }

    // ----------------------------------------------------------------------------------------- me

    [Fact]
    public async Task Me_answers_with_the_tokens_permissions_not_a_fresh_read()
    {
        var world = await ArrangeAsync(role: EamsRoleNames.Organizer);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        // Demote AFTER the token was minted. /me must still report what the token carries, because
        // that is what the server will enforce for its remaining life — a UI gated on anything else
        // would hide a button the server still honours.
        await using (var demote = NewDbContext())
        {
            var viewer = await demote.Roles.SingleAsync(r => r.Name == EamsRoleNames.Viewer);
            await demote.UserRoles.IgnoreQueryFilters()
                .Where(ur => ur.UserId == world.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(ur => ur.RoleId, viewer.Id));
        }

        var body = await BodyAsync(await client.MeAsync());

        Assert.Equal(world.UserId, body.GetProperty("id").GetGuid());
        Assert.Equal(world.SchoolId, body.GetProperty("schoolId").GetGuid());
        Assert.Equal(Email, body.GetProperty("email").GetString());

        var organizer = EamsRoles.ReferenceData.Roles
            .Single(r => r.Name == EamsRoleNames.Organizer).PermissionCodes
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            organizer,
            body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!));
    }

    [Fact]
    public async Task Me_without_a_token_is_401_with_a_problem_body()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var response = await client.MeAsync();
        var body = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        // The framework's default challenge is a bare 401 with no body at all. §6 declares RFC 7807
        // for every error on this API, and JwtBearerEvents.OnChallenge is what keeps that true.
        Assert.Equal(AuthController.InvalidCredentialsCode, Code(body));
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task A_device_key_cannot_reach_the_bearer_routes()
    {
        var world = await ArrangeAsync();
        var deviceKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(deviceKey);

        // A DeviceKey header is not a Bearer header, so the Bearer scheme finds no credential and
        // challenges. This is the property that keeps a kiosk's key from becoming a person's session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // ------------------------------------------------------------------------------ change password

    [Fact]
    public async Task Changing_the_password_revokes_every_session()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var laptop = new AuthApiClient(factory);
        using var phone = new AuthApiClient(factory);

        await laptop.LoginAsync(Email, Password);
        await phone.LoginAsync(Email, Password);

        var response = await laptop.ChangePasswordAsync(Password, "an-entirely-different-passphrase");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Every device, including the one that asked. Unlike logout, this is the operation a user
        // reaches for when they believe a credential leaked.
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.RefreshAsync()).StatusCode);

        // The caller's own cookies were cleared by the 204, so its session is gone rather than merely
        // rejected — asserted as the absence of a cookie rather than as a status, because a client
        // holding no session cannot produce a meaningful one.
        Assert.Null(laptop.Cookie(AuthCookies.RefreshCookieName));
        Assert.Null(laptop.Cookie(AuthCookies.CsrfCookieName));

        await using var read = NewDbContext();
        Assert.Empty(await read.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == world.UserId && t.RevokedAt == null)
            .ToListAsync());
    }

    [Fact]
    public async Task The_new_password_is_what_signs_in_afterwards()
    {
        await ArrangeAsync();
        const string Next = "an-entirely-different-passphrase";

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);
        await client.ChangePasswordAsync(Password, Next);

        using var again = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.LoginAsync(Email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await again.LoginAsync(Email, Next)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_current_password_changes_nothing()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        var response = await client.ChangePasswordAsync("not-the-password", "a-perfectly-fine-new-one");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AuthController.PasswordRejectedCode, Code(await BodyAsync(response)));

        // Neither the hash nor the sessions moved — a failed change must not be a way to log someone
        // out, which would make "guess the current password" a denial-of-service.
        await using var read = NewDbContext();
        Assert.NotEmpty(await read.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == world.UserId && t.RevokedAt == null)
            .ToListAsync());
    }

    [Fact]
    public async Task A_new_password_below_the_creation_bar_is_422()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        var response = await client.ChangePasswordAsync(Password, "short");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await BodyAsync(response);
        Assert.Equal(AuthController.PasswordInvalidCode, Code(body));

        // The message names the bar. Safe here and nowhere near a login: this caller has already
        // proven who they are, so the number discloses nothing.
        Assert.Contains(
            UserProvisioningService.MinimumPassword.ToString(),
            body.GetProperty("detail").GetString()!,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ rate limiting

    [Fact]
    public async Task The_account_limiter_refuses_repeated_guesses_at_one_address()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        HttpResponseMessage? refused = null;

        // One past the per-(email, ip) budget. Every attempt is a wrong password, so nothing here
        // depends on the account existing — which is the point: the bucket is created either way.
        for (var i = 0; i <= AuthAccountLimiter.PerEmailPerIpPermits; i++)
        {
            var response = await client.LoginAsync(Email, $"guess-{i}");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) { refused = response; break; }
        }

        Assert.NotNull(refused);

        var body = await BodyAsync(refused);
        Assert.Equal(CaptureRateLimiting.RateLimitedCode, Code(body));

        // Retry-After is what turns a 429 from "stop" into "stop until" — without it a client's only
        // strategy is a number it invented.
        Assert.NotNull(refused.Headers.RetryAfter);

        // And it says nothing about whether the account exists. The 401 refuses that question; a
        // chattier 429 would answer it.
        var detail = body.GetProperty("detail").GetString()!;
        Assert.DoesNotContain(Email, detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_correct_password_is_still_refused_once_the_account_budget_is_spent()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        for (var i = 0; i < AuthAccountLimiter.PerEmailPerIpPermits; i++)
            await client.LoginAsync(Email, $"guess-{i}");

        // The limiter runs before the credential is read, so a guesser cannot spend the budget and
        // then land the right answer on the next attempt within the same window.
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.LoginAsync(Email, Password)).StatusCode);
    }

    [Fact]
    public async Task The_ip_limiter_refuses_a_flood_across_different_addresses()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        HttpResponseMessage? refused = null;

        // A DIFFERENT address every time, so the account limiter's (email, ip) and email buckets are
        // never the thing that fires — only the IP anti-flood can refuse this, which is what makes
        // this test about the second limiter rather than a duplicate of the first.
        for (var i = 0; i <= AuthRateLimiting.IpPermitsPerWindow; i++)
        {
            var response = await client.LoginAsync($"flood-{i}@usa.edu.ph", "whatever");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) { refused = response; break; }
        }

        Assert.NotNull(refused);
        Assert.Equal(CaptureRateLimiting.RateLimitedCode, Code(await BodyAsync(refused)));
        Assert.NotNull(refused.Headers.RetryAfter);
    }

    // -------------------------------------------------------------------------------------- audit

    [Fact]
    public async Task A_successful_login_is_audited_against_the_user_and_the_school()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);

        await using var read = NewDbContext();
        var row = await read.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuthService.LoginSucceededAction);

        Assert.Equal(world.UserId, row.UserId);
        Assert.Equal(world.SchoolId, row.SchoolId);
        Assert.DoesNotContain(Password, row.Changes ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_login_against_an_unknown_address_is_audited_with_no_principal()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync("nobody@usa.edu.ph", "whatever");

        await using var read = NewDbContext();
        var row = await read.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuthService.LoginFailedAction);

        // Null on both, and the AuditLogs.SchoolId column added in Phase 6a is what makes the second
        // one expressible. An attempt is real; the principal behind it is not.
        Assert.Null(row.UserId);
        Assert.Null(row.SchoolId);
        Assert.Contains("nobody@usa.edu.ph", row.Changes ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("whatever", row.Changes ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replayed_refresh_is_audited()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);
        var stolen = client.Cookie(AuthCookies.RefreshCookieName)!;

        await client.RefreshAsync();

        client.SetCookie(AuthCookies.RefreshCookieName, stolen);
        await client.RefreshAsync();

        await using var read = NewDbContext();
        Assert.NotEmpty(await read.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuthService.RefreshReplayedAction)
            .ToListAsync());
    }

    [Theory]
    [InlineData(AuthService.PasswordChangedAction)]
    [InlineData(AuthService.LogoutAction)]
    public async Task Each_session_event_writes_its_own_audit_action(string action)
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        await client.LoginAsync(Email, Password);
        await client.LogoutAsync();

        using var second = new AuthApiClient(factory);
        await second.LoginAsync(Email, Password);
        await second.ChangePasswordAsync(Password, "an-entirely-different-passphrase");

        await using var read = NewDbContext();
        Assert.NotEmpty(await read.AuditLogs.AsNoTracking()
            .Where(a => a.Action == action)
            .ToListAsync());
    }
}
