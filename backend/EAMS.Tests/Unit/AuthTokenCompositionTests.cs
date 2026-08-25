using System.Security.Claims;
using EAMS.Api.Authentication;
using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// What <see cref="TokenIssuer"/> actually puts in a token, and what it deliberately does not.
///
/// <para>
/// <b>Every claim type is asserted against a constant on <see cref="EamsClaimTypes"/>, never against
/// a string literal.</b> A literal here would pass while the issuer and the readers drifted apart:
/// the whole reason that class exists is that a policy written against one spelling and a token
/// minted with another fail silently — a token whose tenant claim is <c>schoolId</c> instead of
/// <c>school_id</c> mints, validates, authorizes, and then falls back to the pinned development
/// tenant, which in a one-school database is indistinguishable from working.
/// </para>
///
/// <para>
/// Pure logic, no database and no host: minting is a function of the key and the principal.
/// </para>
/// </summary>
public class AuthTokenCompositionTests
{
    private const string Key =
        "3d9a71fc4b0e28d65a17ff9c02b4e78d61aa3c05e9f742b8dc10a6e3f5b920874c1de6af";

    private static readonly Guid UserId = Guid.Parse("2f3d6c8a-9b41-4e57-8a02-71c6d5e4b930");
    private static readonly Guid SchoolId = Guid.Parse("8ac41f60-72b5-4d13-9e08-5c3a1b76f204");

    private static JwtOptions Options() => JwtOptions.Resolve(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [JwtOptions.SigningKeyPath] = Key,
                ["Jwt:Issuer"] = "eams-test",
                ["Jwt:Audience"] = "eams-test",
            })
            .Build());

    private static AuthPrincipal Principal(params string[] permissions) =>
        new(UserId, SchoolId, "someone@usa.edu.ph", "Someone Real", permissions);

    /// <summary>
    /// Reads the token back the way the pipeline does — the same handler, the same validation
    /// parameters, and <c>MapInboundClaims = false</c> — so the assertions below are about what a
    /// request will actually see rather than about what the payload happens to contain.
    /// </summary>
    private static async Task<ClaimsIdentity> ReadBackAsync(string token, JwtOptions options)
    {
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };

        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = TokenIssuer.SigningKey(options),
            ValidAlgorithms = [TokenIssuer.Algorithm],
            ClockSkew = TimeSpan.Zero,
            NameClaimType = EamsClaimTypes.Subject,

            // RoleClaimType left at its default, matching Program.cs — the setter refuses null
            // (IDX10103), and the default WS-Federation URI is a claim this system never mints.
        });

        Assert.True(result.IsValid, result.Exception?.Message);
        return result.ClaimsIdentity;
    }

    [Fact]
    public async Task The_subject_is_the_user_id_prefixed_so_it_can_never_read_as_a_device()
    {
        var options = Options();
        var token = new TokenIssuer(options).Issue(Principal(EamsPermissions.StudentsRead));

        var identity = await ReadBackAsync(token.Token, options);
        var subject = identity.FindFirst(EamsClaimTypes.Subject)?.Value;

        Assert.Equal(EamsClaimTypes.UserSubject(UserId), subject);
        Assert.Equal(UserId, EamsClaimTypes.ReadUserId(subject));

        // And the device reader refuses it, which is the half that matters: the two prefixes exist so
        // a row attributed to a kiosk can never be read as a person's act.
        Assert.Null(EamsClaimTypes.ReadUserId(EamsClaimTypes.DeviceSubject(UserId)));
    }

    [Fact]
    public async Task The_tenant_travels_as_the_same_claim_type_the_device_scheme_emits()
    {
        var options = Options();
        var token = new TokenIssuer(options).Issue(Principal(EamsPermissions.EventsRead));

        var identity = await ReadBackAsync(token.Token, options);

        Assert.Equal(
            SchoolId.ToString(),
            identity.FindFirst(EamsClaimTypes.SchoolId)?.Value);
    }

    [Fact]
    public async Task Every_permission_arrives_as_its_own_repeated_claim()
    {
        var options = Options();

        string[] granted =
        [
            EamsPermissions.StudentsRead,
            EamsPermissions.EventsRead,
            EamsPermissions.AttendanceWrite,
        ];

        var token = new TokenIssuer(options).Issue(Principal(granted));
        var identity = await ReadBackAsync(token.Token, options);

        // One Claim per code, not one claim holding a JSON array — that is the shape
        // RequireClaim(Permission, code) matches against, and the shape DeviceKeyHandler emits.
        Assert.Equal(
            granted.Order(StringComparer.Ordinal),
            identity.FindAll(EamsClaimTypes.Permission).Select(c => c.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_token_carries_no_role_claim_of_any_kind()
    {
        var options = Options();
        var token = new TokenIssuer(options).Issue(Principal(EamsPermissions.StudentsRead));

        var identity = await ReadBackAsync(token.Token, options);

        // Authorization here is permission-based. A role claim would be a second vocabulary no policy
        // consults and that a future endpoint would eventually branch on — at which point EamsRoles
        // stops being the single answer to "who can do this".
        foreach (var role in EamsRoleNames.All)
        {
            Assert.DoesNotContain(
                role,
                identity.Claims.Select(c => c.Value),
                StringComparer.Ordinal);
        }

        Assert.DoesNotContain(
            identity.Claims,
            c => c.Type.Contains("role", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_token_carries_no_personal_data()
    {
        var options = Options();
        var principal = Principal(EamsPermissions.StudentsRead);
        var token = new TokenIssuer(options).Issue(principal);

        var identity = await ReadBackAsync(token.Token, options);
        var values = identity.Claims.Select(c => c.Value).ToArray();

        // The name and the address are display fields, answered by GET /auth/me from the database.
        // A token travels in a header on every single request and lands in proxy logs; putting a
        // person's name and e-mail address in one is a disclosure with no access decision behind it.
        Assert.DoesNotContain(principal.Email, values, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(principal.FullName, values, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_lifetime_is_the_fifteen_minutes_the_options_declare()
    {
        var options = Options();
        var before = DateTime.UtcNow;

        var token = new TokenIssuer(options).Issue(Principal(EamsPermissions.StudentsRead));

        Assert.Equal(TimeSpan.FromMinutes(15), options.AccessTokenLifetime);
        Assert.InRange(
            token.ExpiresAt,
            before + options.AccessTokenLifetime - TimeSpan.FromSeconds(5),
            DateTime.UtcNow + options.AccessTokenLifetime + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Two_tokens_for_the_same_principal_are_distinguishable()
    {
        var options = Options();
        var issuer = new TokenIssuer(options);

        var first = await ReadBackAsync(issuer.Issue(Principal("a.read")).Token, options);
        var second = await ReadBackAsync(issuer.Issue(Principal("a.read")).Token, options);

        // The jti. Nothing reads it today — a 15-minute token's lifetime is its revocation window, so
        // there is no deny list — but a deny list added later needs an id on tokens already in flight,
        // and it makes two otherwise identical tokens tellable apart in a log.
        Assert.NotEqual(
            first.FindFirst(JwtRegisteredClaimNames.Jti)?.Value,
            second.FindFirst(JwtRegisteredClaimNames.Jti)?.Value);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        var options = Options();
        var token = new TokenIssuer(options).Issue(Principal("a.read"));

        var forged = JwtOptions.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [JwtOptions.SigningKeyPath] =
                    "0000111122223333444455556666777788889999aaaabbbbccccddddeeeeffff01",
                ["Jwt:Issuer"] = "eams-test",
                ["Jwt:Audience"] = "eams-test",
            })
            .Build());

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = await handler.ValidateTokenAsync(token.Token, new TokenValidationParameters
        {
            ValidIssuer = forged.Issuer,
            ValidAudience = forged.Audience,
            IssuerSigningKey = TokenIssuer.SigningKey(forged),
            ValidAlgorithms = [TokenIssuer.Algorithm],
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void The_signing_algorithm_is_pinned_to_one_value()
    {
        // The whole of the `alg: none` and RS256-to-HS256 confusion families lives in the gap between
        // "the token says how it was signed" and "we checked". Program.cs pins ValidAlgorithms to this
        // same constant, so minting and validation cannot drift.
        Assert.Equal(SecurityAlgorithms.HmacSha256, TokenIssuer.Algorithm);
    }
}
