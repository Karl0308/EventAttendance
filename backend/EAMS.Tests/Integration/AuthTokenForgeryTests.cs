using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EAMS.Api.Authorization;
using EAMS.Api.Controllers;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Phase 6d — the adversarial pass over token validation. Every test here presents a token the
/// system never issued and asserts the host refuses it.
///
/// <para>
/// <b><c>GET /auth/me</c> is the oracle, and it distinguishes two things a single status code would
/// blur.</b> A <c>401</c> means the Bearer scheme refused the token. A <c>404</c> means the token was
/// <em>accepted</em> and the account it named could not be read. So every assertion below checks for
/// 401 explicitly rather than "not 200" — a forgery answered 404 has already won, because the
/// signature check is what was supposed to stop it.
/// </para>
///
/// <para>
/// <b>The positive control is the first test in the file and it is not decoration.</b> A dozen tests
/// that all assert 401 would pass just as well if the forger emitted rubbish, if the host's issuer
/// were not "eams", or if <c>/auth/me</c> were unreachable. The control mints a token through the
/// same forger with nothing wrong with it and requires a 200, which is what makes the rest of the
/// file evidence rather than a tautology.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AuthTokenForgeryTests : IntegrationTest
{
    public AuthTokenForgeryTests(SqlServerFixture sql) : base(sql) { }

    private const string Email = "dean@usa.edu.ph";
    private const string Password = "correct-horse-battery-staple";

    private sealed record World(Guid SchoolId, Guid UserId);

    private async Task<World> ArrangeAsync()
    {
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        return new World(schoolId, await CreateUserAsync(schoolId, Email, Password));
    }

    private async Task<HttpResponseMessage> MeWithAsync(string token)
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithBearer(token);

        return await client.GetAsync("/api/v1/auth/me");
    }

    private async Task AssertRefusedAsync(string token, string what)
    {
        var response = await MeWithAsync(token);

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized,
            $"{what} was answered {(int)response.StatusCode}. Only 401 means the Bearer scheme " +
            $"refused it — a 404 means the token was ACCEPTED and only the account lookup failed, " +
            $"and a 200 means it authenticated outright.");

        // The challenge shape matters as much as the status: a client that gets a 401 with no
        // WWW-Authenticate cannot tell "your token is bad" from "this route is broken".
        Assert.Equal("Bearer", response.Challenge()?.Scheme);
    }

    // ------------------------------------------------------------------------ the positive control

    [Fact]
    public async Task A_correctly_forged_token_is_accepted_which_is_what_makes_every_refusal_below_mean_something()
    {
        var world = await ArrangeAsync();

        var token = ForgedJwt.Mint(
            ForgedJwt.ClaimsFor(world.UserId, world.SchoolId, EamsPermissions.EventsRead));

        var response = await MeWithAsync(token);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"A token minted by this file's forger — real key, the host's issuer and audience, a live " +
            $"user's subject, nothing wrong with it — was answered {(int)response.StatusCode}. Until " +
            $"this is 200, every 401 in this file is equally consistent with the forger simply " +
            $"producing something the host cannot parse.");
    }

    // ------------------------------------------------------------------------------- the signature

    [Fact]
    public async Task A_token_whose_signature_is_stripped_is_refused()
    {
        var world = await ArrangeAsync();

        // alg: none with the third segment present and empty — the canonical CVE-2015-9235 shape.
        var token = ForgedJwt.Mint(
            ForgedJwt.ClaimsFor(world.UserId, world.SchoolId, EamsPermissions.EventsRead),
            ForgedJwt.None);

        Assert.EndsWith(".", token, StringComparison.Ordinal);
        await AssertRefusedAsync(token, "A token with alg:none and an empty signature");
    }

    [Fact]
    public async Task A_token_with_only_two_segments_is_refused()
    {
        var world = await ArrangeAsync();

        var full = ForgedJwt.Mint(ForgedJwt.ClaimsFor(world.UserId, world.SchoolId));
        var headerAndPayload = string.Join('.', full.Split('.')[..2]);

        await AssertRefusedAsync(headerAndPayload, "A token with no signature segment at all");
    }

    [Theory]
    [InlineData(ForgedJwt.Hs384)]
    [InlineData(ForgedJwt.Hs512)]
    public async Task A_token_signed_with_a_different_algorithm_is_refused(string algorithm)
    {
        var world = await ArrangeAsync();

        // The REAL key, correctly applied — only the algorithm differs. This is the token a host that
        // verifies the signature but trusts the header's `alg` accepts, and it is why Program.cs pins
        // ValidAlgorithms instead of letting the key type narrow the choice.
        var token = ForgedJwt.Mint(
            ForgedJwt.ClaimsFor(world.UserId, world.SchoolId, EamsPermissions.EventsRead),
            algorithm, ForgedJwt.RealKey);

        await AssertRefusedAsync(token, $"A token correctly signed with {algorithm} instead of HS256");
    }

    [Fact]
    public async Task A_token_signed_with_the_wrong_key_is_refused()
    {
        var world = await ArrangeAsync();

        var token = ForgedJwt.Mint(
            ForgedJwt.ClaimsFor(world.UserId, world.SchoolId, EamsPermissions.EventsRead),
            ForgedJwt.Hs256, ForgedJwt.WrongKey);

        await AssertRefusedAsync(token, "A token signed with a different key of the same length");
    }

    [Fact]
    public async Task A_token_signed_with_a_truncation_of_the_real_key_is_refused()
    {
        var world = await ArrangeAsync();

        // A prefix of the secret is what actually leaks: a fixed-width log column, a truncated console
        // echo, a configuration screen that shows the first N characters. It is still long enough to
        // sign with, so the forgery is well-formed and only the HMAC differs.
        var token = ForgedJwt.Mint(
            ForgedJwt.ClaimsFor(world.UserId, world.SchoolId, EamsPermissions.EventsRead),
            ForgedJwt.Hs256, ForgedJwt.TruncatedKey);

        await AssertRefusedAsync(token, "A token signed with the first 40 bytes of the real key");
    }

    // ------------------------------------------------------------------------------------ lifetime

    [Fact]
    public async Task An_expired_token_is_refused_with_no_skew_to_hide_behind()
    {
        var world = await ArrangeAsync();

        var claims = ForgedJwt.ClaimsFor(world.UserId, world.SchoolId, EamsPermissions.EventsRead);
        var now = DateTimeOffset.UtcNow;

        // Ten seconds past. The library's DEFAULT ClockSkew is five minutes, so a host that left it
        // alone would accept this — which is the whole assertion, and why Program.cs sets zero.
        claims["iat"] = now.AddMinutes(-16).ToUnixTimeSeconds();
        claims["nbf"] = now.AddMinutes(-16).ToUnixTimeSeconds();
        claims["exp"] = now.AddSeconds(-10).ToUnixTimeSeconds();

        await AssertRefusedAsync(ForgedJwt.Mint(claims), "A token that expired ten seconds ago");
    }

    [Fact]
    public async Task A_token_whose_not_before_is_in_the_future_is_refused()
    {
        var world = await ArrangeAsync();

        var claims = ForgedJwt.ClaimsFor(world.UserId, world.SchoolId, EamsPermissions.EventsRead);
        var now = DateTimeOffset.UtcNow;

        claims["nbf"] = now.AddMinutes(5).ToUnixTimeSeconds();
        claims["exp"] = now.AddMinutes(20).ToUnixTimeSeconds();

        await AssertRefusedAsync(
            ForgedJwt.Mint(claims), "A token that does not become valid for another five minutes");
    }

    [Fact]
    public async Task A_token_with_no_expiry_at_all_is_refused()
    {
        var world = await ArrangeAsync();

        var claims = ForgedJwt.ClaimsFor(world.UserId, world.SchoolId, EamsPermissions.EventsRead);
        claims.Remove("exp");

        // A token with no `exp` never expires, so accepting one turns a fifteen-minute credential into
        // a permanent one — the worst outcome available in this file, and the one with no revocation
        // story at all. The library requires an expiration time by default; this asserts nobody
        // switched that default off while chasing a clock-skew bug.
        await AssertRefusedAsync(ForgedJwt.Mint(claims), "A token carrying no exp claim");
    }

    [Theory]
    [InlineData("iss", "some-other-idp")]
    [InlineData("aud", "some-other-api")]
    public async Task A_token_minted_for_somewhere_else_is_refused(string claim, string value)
    {
        var world = await ArrangeAsync();

        var claims = ForgedJwt.ClaimsFor(world.UserId, world.SchoolId, EamsPermissions.EventsRead);
        claims[claim] = value;

        await AssertRefusedAsync(ForgedJwt.Mint(claims), $"A token whose '{claim}' is '{value}'");
    }

    // ----------------------------------------------------------------------------- tampered claims

    [Fact]
    public async Task Editing_the_tenant_without_resigning_is_refused()
    {
        await ArrangeAsync();

        // A real token from a real login, edited afterwards and shipped with its original signature.
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);

        var tampered = ForgedJwt.TamperUnsigned(
            session.AccessToken!, p => p[EamsClaimTypes.SchoolId] = Guid.NewGuid().ToString());

        await AssertRefusedAsync(tampered, "A genuine token whose school_id was edited in place");
    }

    [Fact]
    public async Task Adding_a_permission_and_resigning_with_the_wrong_key_is_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);

        var tampered = ForgedJwt.TamperAndResign(
            session.AccessToken!,
            p =>
            {
                p[EamsClaimTypes.SchoolId] = Guid.NewGuid().ToString();
                p[EamsClaimTypes.Permission] = new JsonArray(
                    EamsPermissions.AttendanceCapture, EamsPermissions.StudentsWrite);
            },
            ForgedJwt.WrongKey);

        await AssertRefusedAsync(tampered, "A genuine token re-signed with the wrong key after editing");
    }

    // ------------------------------------------------------------ a valid signature over a dead user

    [Fact]
    public async Task A_token_naming_a_user_that_never_existed_authenticates_and_reads_nothing()
    {
        var world = await ArrangeAsync();

        var token = ForgedJwt.Mint(ForgedJwt.ClaimsFor(Guid.NewGuid(), world.SchoolId));

        var response = await MeWithAsync(token);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // 404, not 401 and not 500. The signature IS valid — this scenario is not a key compromise,
        // the row simply is not there — so the honest answer is that the principal is unavailable,
        // and it must not surface as a server error.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(AuthController.PrincipalUnavailableCode, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_live_token_stops_reading_a_profile_the_moment_the_account_is_deactivated()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var session = new AuthApiClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(Email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.MeAsync()).StatusCode);

        await using (var db = NewDbContext())
        {
            await db.Users.IgnoreQueryFilters()
                .Where(u => u.Id == world.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        var after = await session.MeAsync();

        // The token is still cryptographically valid and still inside its fifteen minutes — there is
        // no deny list, by design. What must not survive deactivation is the READ: GetProfileAsync
        // filters on IsActive, so the answer becomes 404 straight away rather than at expiry.
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }
}
