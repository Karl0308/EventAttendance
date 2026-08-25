using System.Security.Cryptography;
using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EAMS.Api.Authentication;

/// <summary>The minted access token and the moment it stops being accepted.</summary>
public readonly record struct IssuedAccessToken(string Token, DateTime ExpiresAt);

/// <summary>
/// Mints §11's access token. <b>The only thing in this system that touches the signing key on the
/// write side</b> (D-74).
///
/// <para>
/// <b>Why it lives in <c>EAMS.Api</c> and not in <c>EAMS.Infrastructure</c>.</b> Infrastructure is the
/// assembly that talks to the database; giving it the signing key would mean the layer holding
/// <c>Users.PasswordHash</c> could also forge a token for any row it can read, and a compromise of
/// either would be a compromise of both. The split costs one interface hop —
/// <c>AuthService</c> returns an <see cref="AuthPrincipal"/>, this turns it into a JWT — and buys a
/// boundary that a reviewer can check by looking at the project references.
/// </para>
///
/// <para>
/// <b>Mint before rotate.</b> <c>AuthController</c> calls this <em>after</em> the refresh row is
/// written on login, but the ordering that matters is on refresh: the successor refresh token is
/// persisted and then the access token is minted from the principal it returned. If minting could
/// fail after the old refresh token was already revoked, the user would be logged out by a
/// successful renewal — the worst possible reading of "your session is fine". Nothing in this class
/// does I/O and the key was validated at startup (<see cref="JwtOptions"/>), so the failure it
/// guards against is narrow; the ordering is still stated because it is free.
/// </para>
/// </summary>
public sealed class TokenIssuer
{
    /// <summary>
    /// The header/claim algorithm. <b>HS256, and named as a constant so that nothing can accept a
    /// token signed with something else.</b> <c>alg: none</c> and algorithm-confusion attacks live
    /// entirely in the gap between "the token says how it was signed" and "we checked"; the
    /// validation side pins this same constant in <c>Program.cs</c>.
    /// </summary>
    internal const string Algorithm = SecurityAlgorithms.HmacSha256;

    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenIssuer(JwtOptions options)
    {
        _options = options;
        _credentials = new SigningCredentials(SigningKey(options), Algorithm);
    }

    /// <summary>
    /// The symmetric key, built once. Shared with the validation side through
    /// <see cref="SigningKey"/> so a token this host mints is a token this host accepts, by
    /// construction rather than by two call sites agreeing.
    /// </summary>
    public static SymmetricSecurityKey SigningKey(JwtOptions options) =>
        new(System.Text.Encoding.UTF8.GetBytes(options.SigningKey));

    /// <summary>
    /// The claim set, and the one place it is decided — the same sentence
    /// <c>DeviceKeyHandler.TicketFor</c> opens with, and deliberately so: the two schemes emit the
    /// same claim <em>types</em>, which is what makes the authorization layer principal-agnostic.
    ///
    /// <para>
    /// <b>Every claim type is a constant from <see cref="EamsClaimTypes"/>, never a literal.</b> A
    /// literal here compiles, mints a token whose <c>school_id</c> is spelled <c>schoolId</c>, and
    /// fails at <c>ClaimsSchoolContext</c> by silently falling back to the pinned tenant — which in a
    /// one-school development database is indistinguishable from working.
    /// </para>
    ///
    /// <para>
    /// <b>No role claim.</b> §11's four roles are how permissions are <em>administered</em>; they are
    /// not how access is decided. Every policy in this system reads <c>perm</c>
    /// (<c>EamsPermissions</c>), so a role claim would be a second vocabulary that no policy consults
    /// and that a future endpoint would eventually be tempted to branch on — at which point the grant
    /// matrix in <c>EamsRoles</c> stops being the single answer to "who can do this".
    /// </para>
    ///
    /// <para>
    /// <b><c>sub</c> is prefixed <c>user:</c></b>, matching <c>EamsClaimTypes.DeviceSubject</c>'s
    /// convention, so a device id and a user id can never be read as each other by anything that
    /// looks only at this claim.
    /// </para>
    /// </summary>
    public IssuedAccessToken Issue(AuthPrincipal principal)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now + _options.AccessTokenLifetime;

        var claims = new Dictionary<string, object>
        {
            [EamsClaimTypes.Subject] = EamsClaimTypes.UserSubject(principal.UserId),
            [EamsClaimTypes.SchoolId] = principal.SchoolId.ToString(),

            // A JSON array, which the handler reads back as one Claim per element — the repeatable
            // shape EamsClaimTypes.Permission documents and the shape RequireClaim matches against.
            [EamsClaimTypes.Permission] = principal.Permissions,

            // A per-token id. Not read by anything today: there is no deny list, because a 15-minute
            // token's lifetime IS its revocation window (see JwtOptions.AccessTokenLifetime). It is
            // minted anyway because a deny list added later needs an id on tokens already in flight,
            // and because it makes two otherwise identical tokens distinguishable in a log.
            [JwtRegisteredClaimNames.Jti] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        };

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAt,
            SigningCredentials = _credentials,
        });

        return new IssuedAccessToken(token, expiresAt);
    }
}
