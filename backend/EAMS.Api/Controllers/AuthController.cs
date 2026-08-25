using EAMS.Api.Authentication;
using EAMS.Api.Authorization;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EAMS.Api.Controllers;

/// <summary>
/// Technical Plan §6.1 / §11 — human authentication (Phase 6b).
///
/// <para>
/// <b>This is the first and only surface in the API that a person authenticates against, and adding
/// it changes nothing about any other endpoint.</b> ADR-001 D-6's staged cutover is unchanged: every
/// route outside <c>/auth</c> is still open and unauthenticated, <c>[HasPermissionNotEnforced]</c>
/// still enforces nothing, and <c>[assembly: AuthorizationNotEnforced]</c> is still present. Turning
/// those on is a later phase, and <c>AuthStagedCutoverTests</c> asserts the invariant rather than
/// leaving it to be assumed.
/// </para>
///
/// <para>
/// <b>Two credentials now reach this host and they never mix.</b> A kiosk sends
/// <c>Authorization: DeviceKey …</c> and is scoped to <c>attendance.capture</c>; a person sends
/// <c>Authorization: Bearer …</c> and holds whatever their roles grant. Both emit the same claim
/// <em>types</em> (<see cref="EamsClaimTypes"/>), so the authorization layer cannot tell them apart —
/// which is exactly what <c>DeviceKeyHandler</c> was built for and why this phase is additive.
/// </para>
///
/// <para>
/// <b>Where the tokens live.</b> The access token is returned in the body and held in memory by the
/// SPA; the refresh token is an <c>httpOnly</c> cookie the browser stores and this API never reads
/// from a body. That is drift from §6.1 and the reasoning is in <see cref="AuthCookies"/>.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on every other controller.</summary>
    internal const string ErrorCodeProperty = "code";

    /// <summary>
    /// <b>The one and only <c>code</c> a failed sign-in ever carries.</b>
    ///
    /// <para>
    /// Unknown address, wrong password and deactivated account all produce this exact value, this
    /// exact status and this exact prose. A client has nothing to branch on because there is nothing
    /// it should branch on — see <see cref="AuthLoginOutcome.InvalidCredentials"/> for why any finer
    /// answer is an account-enumeration oracle, and <c>UserCredentialVerifier</c>'s decoy hash for why
    /// the response <em>time</em> does not answer it either.
    /// </para>
    /// </summary>
    internal const string InvalidCredentialsCode = "InvalidCredentials";

    /// <summary>No refresh cookie was presented at all — the caller was never given a session.</summary>
    internal const string SessionMissingCode = "SessionMissing";

    /// <summary>A refresh token was presented and is not usable. Never says which of the six ways.</summary>
    internal const string SessionExpiredCode = "SessionExpired";

    /// <summary>The double-submit check refused the request. See <see cref="AuthCookies"/>.</summary>
    internal const string CsrfFailedCode = "CsrfTokenInvalid";

    /// <summary>The token is fine and the account behind it is not (deleted, deactivated).</summary>
    internal const string PrincipalUnavailableCode = "PrincipalUnavailable";

    /// <summary>The current password did not verify.</summary>
    internal const string PasswordRejectedCode = "PasswordRejected";

    /// <summary>The new password failed the same rules a created account is held to.</summary>
    internal const string PasswordInvalidCode = "PasswordInvalid";

    private readonly IAuthService _auth;
    private readonly TokenIssuer _tokens;
    private readonly AuthAccountLimiter _accountLimiter;

    public AuthController(IAuthService auth, TokenIssuer tokens, AuthAccountLimiter accountLimiter)
    {
        _auth = auth;
        _tokens = tokens;
        _accountLimiter = accountLimiter;
    }

    // -------------------------------------------------------------------------------------- login

    /// <summary>
    /// <c>POST /auth/login</c> — exchange an e-mail address and password for an access token and a
    /// session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The response sets two cookies.</b> <c>eams_rt</c> is the refresh token: <c>httpOnly</c>,
    /// <c>Secure</c>, <c>SameSite=Strict</c>, scoped to this <c>/auth</c> path, and unreadable by any
    /// script. <c>eams_csrf</c> is readable, and the SPA must echo its value in an
    /// <c>X-CSRF-Token</c> header on <c>refresh</c> and <c>logout</c> — those two routes are the only
    /// ones in the API a browser attaches an ambient credential to.
    /// </para>
    /// <para>
    /// <b>Every failure is one 401 with <c>code: InvalidCredentials</c>.</b> Do not branch on
    /// anything finer; there is nothing finer, deliberately.
    /// </para>
    /// </remarks>
    /// <response code="200">Signed in. Body carries the access token and the effective permission set.</response>
    /// <response code="400">The body is not a login request.</response>
    /// <response code="401">Unknown address, wrong password, or a deactivated account — indistinguishably.</response>
    /// <response code="429">Too many attempts, from this address or against this account. Honour <c>Retry-After</c>.</response>
    [HttpPost("login")]
    [EnableRateLimiting(AuthRateLimiting.IpPolicyName)]
    [ProducesResponseType(typeof(AuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthTokenResponse>> Login(
        [FromBody] LoginRequest request, CancellationToken ct)
    {
        // Normalized here as well as inside the verifier, because this value is a rate-limit partition
        // key: `Admin@x` and `admin@x` landing in different buckets would be a limit an attacker steps
        // around by holding shift.
        var email = (request.Email ?? "").Trim().ToLowerInvariant();

        // The tight limiter, before any credential work. A permit is spent even for an address that
        // does not exist — see AuthAccountLimiter for why refusing to create that bucket would make
        // the 429 an existence oracle.
        using var lease = await _accountLimiter.AcquireAsync(email, ClientIp);
        if (!lease.IsAcquired)
        {
            await AuthRateLimitRefusal.WriteAsync(HttpContext, lease);
            return new EmptyResult();
        }

        var result = await _auth.LoginAsync(email, request.Password ?? "", UserAgent, ClientIp, ct);

        if (result.Outcome != AuthLoginOutcome.Succeeded || result.Session is null)
        {
            return Failure(
                StatusCodes.Status401Unauthorized,
                InvalidCredentialsCode,
                "Sign-in failed.",
                "That e-mail address and password combination was not accepted. This answer is the " +
                "same whether the address is unknown, the password is wrong, or the account is " +
                "deactivated — the API does not disclose which.");
        }

        return Issue(result.Session);
    }

    // ------------------------------------------------------------------------------------ refresh

    /// <summary>
    /// <c>POST /auth/refresh</c> — rotate the session cookie and mint a new access token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The request body is empty and the request is not anonymous.</b> The refresh token is read
    /// from the <c>eams_rt</c> cookie; sending it in a body is no longer supported (drift from §6.1,
    /// see <see cref="AuthCookies"/>). The <c>X-CSRF-Token</c> header is <b>required</b> and must
    /// equal the <c>eams_csrf</c> cookie.
    /// </para>
    /// <para>
    /// <b>Rotation is single-use and replay burns the family.</b> Presenting a token that has already
    /// been rotated revokes every live token in its family — the legitimate client pays one sign-in,
    /// a thief's chain dies. Do not retry a refresh with the same cookie value after a failure; take
    /// the 401 and sign in.
    /// </para>
    /// <para>
    /// <b>The permission set is re-read on every refresh</b>, so a grant changed by an administrator
    /// takes effect within one access-token lifetime rather than at the user's next sign-in.
    /// </para>
    /// </remarks>
    /// <response code="200">Rotated. New cookies are set and a new access token is returned.</response>
    /// <response code="401">No session cookie, or one that is expired, unknown, or already used.</response>
    /// <response code="403">The <c>X-CSRF-Token</c> header is missing or does not match the cookie.</response>
    /// <response code="429">Too many requests from this address. Honour <c>Retry-After</c>.</response>
    [HttpPost("refresh")]
    [EnableRateLimiting(AuthRateLimiting.IpPolicyName)]
    [ProducesResponseType(typeof(AuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthTokenResponse>> Refresh(CancellationToken ct)
    {
        if (RefuseCsrf() is { } refused) return refused;

        var result = await _auth.RefreshAsync(
            AuthCookies.ReadRefreshToken(Request), UserAgent, ClientIp, ct);

        if (result.Outcome != AuthRefreshOutcome.Succeeded || result.Session is null)
        {
            // The refresh cookie is cleared on any failure. Leaving a dead token in the browser
            // guarantees the next refresh presents it again, which — if it was dead because it had
            // already been rotated — is indistinguishable from a replay and burns a family that may
            // still be live.
            //
            // The CSRF cookie is deliberately LEFT IN PLACE. Clearing it too is the tidier-looking
            // choice and it is wrong: two tabs refreshing at once is ordinary, and the second one
            // would then arrive with no CSRF cookie and be answered 403 — a status that tells a
            // client its request was malformed when what actually happened is that its session ended.
            // One condition must not surface as two different codes depending on timing. The value is
            // re-minted on the next successful login or refresh anyway.
            AuthCookies.ClearRefreshToken(HttpContext);

            return result.Outcome == AuthRefreshOutcome.Missing
                ? Failure(
                    StatusCodes.Status401Unauthorized, SessionMissingCode,
                    "No session was presented.",
                    "This request carried no session cookie. Sign in at POST /api/v1/auth/login. If " +
                    "you expected one, check that the cookie path matches the path this API is " +
                    "mounted at — see the 'Refresh-token cookie Path' line in the server log.")
                : Failure(
                    StatusCodes.Status401Unauthorized, SessionExpiredCode,
                    "That session can no longer be renewed.",
                    "Sign in again. The API deliberately does not say whether the token was expired, " +
                    "unknown, or already used — and if it was already used, every other session in " +
                    "its family has been revoked as a precaution.");
        }

        return Issue(result.Session);
    }

    // ------------------------------------------------------------------------------------- logout

    /// <summary>
    /// <c>POST /auth/logout</c> — end this session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This session only.</b> Signing out on a laptop does not sign the user out on their phone.
    /// The operation that ends every session is <c>POST /auth/change-password</c>, which is what a
    /// user reaches for when they believe a credential has leaked.
    /// </para>
    /// <para>
    /// Requires a Bearer token and the <c>X-CSRF-Token</c> header. It is idempotent: a logout that
    /// presents no session cookie revokes nothing and still answers 204 with the cookies cleared,
    /// because the postcondition — this browser is signed out — holds either way.
    /// </para>
    /// </remarks>
    /// <response code="204">Signed out. Both cookies are cleared.</response>
    /// <response code="401">No Bearer token, or one that is expired or malformed.</response>
    /// <response code="403">The <c>X-CSRF-Token</c> header is missing or does not match the cookie.</response>
    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableRateLimiting(AuthRateLimiting.IpPolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (RefuseCsrf() is { } refused) return refused;

        if (TokenUserId is not { } userId)
            return Failure(StatusCodes.Status401Unauthorized, InvalidCredentialsCode,
                "This token names no user.", UnreadableSubjectDetail);

        await _auth.LogoutAsync(userId, AuthCookies.ReadRefreshToken(Request), ClientIp, ct);

        AuthCookies.Clear(HttpContext);
        return NoContent();
    }

    // ----------------------------------------------------------------------------------------- me

    /// <summary>
    /// <c>GET /auth/me</c> — who this access token speaks for, and what it may do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The permissions come from the token, not from a fresh database read</b>, and that is the
    /// whole point of the endpoint. The server will enforce what this token carries for as long as it
    /// lives, so a UI that gates its navigation on anything else will eventually show a button the
    /// server refuses or hide one it would have allowed. A grant changed by an administrator appears
    /// here after the next <c>POST /auth/refresh</c> — at most one access-token lifetime away.
    /// </para>
    /// <para>
    /// The display fields are read from the database, because a name and an address are not access
    /// decisions and do not belong in a token that travels in a header on every request.
    /// </para>
    /// </remarks>
    /// <response code="200">The authenticated user.</response>
    /// <response code="401">No Bearer token, or one that is expired or malformed.</response>
    /// <response code="404">The token is valid and the account behind it no longer is.</response>
    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [ProducesResponseType(typeof(AuthUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthUserDto>> Me(CancellationToken ct)
    {
        if (TokenUserId is not { } userId)
            return Failure(StatusCodes.Status401Unauthorized, InvalidCredentialsCode,
                "This token names no user.", UnreadableSubjectDetail);

        var profile = await _auth.GetProfileAsync(userId, ct);
        if (profile is null)
        {
            return Failure(
                StatusCodes.Status404NotFound, PrincipalUnavailableCode,
                "This account is no longer available.",
                "The token is valid but the account it names has been deactivated or removed. Sign " +
                "out and sign in again; the token stops being accepted when it expires.");
        }

        return Ok(new AuthUserDto(
            profile.UserId, profile.SchoolId, profile.Email, profile.FullName, TokenPermissions));
    }

    // ------------------------------------------------------------------------------ change password

    /// <summary>
    /// <c>POST /auth/change-password</c> — change the caller's own password and end every session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Requires the current password even though the caller holds a valid token.</b> A token is
    /// what someone who walked past an unlocked laptop has; the current password is what only the
    /// account holder has, and this is the operation that would lock the real owner out.
    /// </para>
    /// <para>
    /// <b>Every refresh token for this user is revoked, including the caller's own.</b> The reason to
    /// change a password is usually that it may have leaked, and a rotation that leaves other sessions
    /// live changes nothing for whoever holds one. The caller's access token keeps working until it
    /// expires — up to fifteen minutes — and then cannot be renewed. Sign in again.
    /// </para>
    /// </remarks>
    /// <response code="204">Changed. Every session, including this one, has been revoked.</response>
    /// <response code="401">No Bearer token, or the current password did not verify.</response>
    /// <response code="422">The new password failed the rules a created account is held to.</response>
    /// <response code="429">Too many requests from this address. Honour <c>Retry-After</c>.</response>
    [HttpPost("change-password")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableRateLimiting(AuthRateLimiting.IpPolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        if (TokenUserId is not { } userId)
            return Failure(StatusCodes.Status401Unauthorized, InvalidCredentialsCode,
                "This token names no user.", UnreadableSubjectDetail);

        var result = await _auth.ChangePasswordAsync(
            userId, request.CurrentPassword ?? "", request.NewPassword ?? "", ClientIp, ct);

        switch (result.Outcome)
        {
            case ChangePasswordOutcome.Changed:
                // The caller's own session is among the revoked, so the cookie in their browser is
                // already dead. Clearing it here is what stops the next refresh from presenting a
                // revoked token, which the store would correctly read as a replay.
                AuthCookies.Clear(HttpContext);
                return NoContent();

            case ChangePasswordOutcome.ValidationFailed:
                // 422, not 400: the body parsed fine and failed a business rule. The message is safe to
                // return in full — unlike a login, this caller is already authenticated, so telling
                // them the password is too short discloses nothing they do not already know.
                return Failure(
                    StatusCodes.Status422UnprocessableEntity, PasswordInvalidCode,
                    "That new password cannot be used.", result.Message);

            case ChangePasswordOutcome.Rejected:
                return Failure(
                    StatusCodes.Status401Unauthorized, PasswordRejectedCode,
                    "The current password was not accepted.",
                    "Nothing was changed. Re-enter the current password for this account.");

            default:
                // Total over the enum, for the reason AttendanceController.StatusCodeFor records: a
                // discard arm that guessed "success" would report a password change that never happened.
                throw new ArgumentOutOfRangeException(
                    nameof(result), result.Outcome,
                    $"No response is mapped for this {nameof(ChangePasswordOutcome)}.");
        }
    }

    // ------------------------------------------------------------------------------------- shared

    private const string UnreadableSubjectDetail =
        "The presented token carries no readable 'sub' claim. If this host was recently reconfigured, " +
        "check that the Bearer scheme still sets MapInboundClaims = false — the default rewrites " +
        "'sub' to a WS-Federation URI and every claim-reading seam then resolves to null.";

    /// <summary>
    /// The 200 both <see cref="Login"/> and <see cref="Refresh"/> return.
    ///
    /// <para>
    /// <b>The access token is minted <em>after</em> the refresh row is written, and the ordering is
    /// the point (D-74).</b> On refresh the predecessor is already revoked by the time this runs, so
    /// a mint that failed here would have logged the user out by renewing them successfully. Minting
    /// last means the only thing that can fail after the session state has moved is composing a
    /// response — and if that throws, the client retries with a cookie that was already rotated, sees
    /// a 401, and signs in. Recoverable, and visible.
    /// </para>
    /// </summary>
    private ActionResult<AuthTokenResponse> Issue(AuthSession session)
    {
        AuthCookies.Issue(HttpContext, session.RefreshToken, session.RefreshTokenExpiresAt);

        var access = _tokens.Issue(session.Principal);

        return Ok(new AuthTokenResponse(
            access.Token,
            JwtBearerDefaults.AuthenticationScheme,
            access.ExpiresAt,
            new AuthUserDto(
                session.Principal.UserId,
                session.Principal.SchoolId,
                session.Principal.Email,
                session.Principal.FullName,
                session.Principal.Permissions)));
    }

    /// <summary>
    /// The double-submit gate on the two cookie-bearing routes, or <c>null</c> to continue.
    ///
    /// <para>
    /// <b>403 rather than 401.</b> The caller may well be perfectly authenticated — a Bearer token on
    /// logout, a valid session cookie on refresh — and what failed is a request-shape requirement,
    /// not a credential. A 401 would tell a client to go and re-authenticate, which would not fix it.
    /// </para>
    /// </summary>
    private ObjectResult? RefuseCsrf() =>
        AuthCookies.CsrfTokenMatches(Request)
            ? null
            : Failure(
                StatusCodes.Status403Forbidden, CsrfFailedCode,
                "This request is missing its CSRF token.",
                $"Send the value of the '{AuthCookies.CsrfCookieName}' cookie in an " +
                $"'{AuthCookies.CsrfHeaderName}' header. This is required on refresh and logout only — " +
                "they are the only routes a browser attaches a cookie to. Every other endpoint " +
                "authenticates with 'Authorization: Bearer', which a cross-site page cannot set.");

    /// <summary>
    /// Every refusal on this controller, through the <c>ProblemDetailsFactory</c> seam that
    /// stamps <c>traceId</c> once — the same one <c>DeviceKeyHandler</c>, the rate limiter and every
    /// other controller use.
    /// </summary>
    private ObjectResult Failure(int status, string code, string title, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: title, detail: detail);

        problem.Extensions[ErrorCodeProperty] = code;

        return StatusCode(status, problem);
    }

    /// <summary>
    /// The user id in the presented token's <c>sub</c>, or <c>null</c>.
    ///
    /// <para>
    /// Read through <see cref="EamsClaimTypes.ReadUserId"/>, which rejects a device subject and a bare
    /// GUID alike, so a <c>DeviceKey</c> principal can never satisfy a route on this controller even
    /// if one were ever reachable by that scheme.
    /// </para>
    /// </summary>
    private Guid? TokenUserId =>
        EamsClaimTypes.ReadUserId(User.FindFirst(EamsClaimTypes.Subject)?.Value);

    /// <summary>
    /// The <c>perm</c> claims on the presented token — see <see cref="AuthUserDto.Permissions"/> for
    /// why this is read from the token and never from the database.
    /// </summary>
    private IReadOnlyList<string> TokenPermissions =>
        [.. User.FindAll(EamsClaimTypes.Permission).Select(c => c.Value).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The client address, recorded against the session and the audit row.
    /// <b>Behind a reverse proxy this is the proxy</b> — see <see cref="AuthRateLimiting"/> for why
    /// forwarded headers are deliberately not honoured until a deployment phase can pin the hops.
    /// </summary>
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// The browser's <c>User-Agent</c>, stored against the session so an operator listing their
    /// devices sees "Chrome on Windows" rather than a row of GUIDs. Truncated by the store; display
    /// text, never trusted.
    /// </summary>
    private string? UserAgent => Request.Headers.UserAgent.ToString() is { Length: > 0 } value
        ? value
        : null;
}
