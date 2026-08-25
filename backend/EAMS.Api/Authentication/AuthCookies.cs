using System.Security.Cryptography;
using System.Text;

namespace EAMS.Api.Authentication;

/// <summary>
/// Where the refresh token lives, how the CSRF pair is checked, and why both answers are what they
/// are (Phase 6b).
///
/// <para>
/// <b>Drift from Technical Plan §6.1, recorded deliberately.</b> §6.1 sketches
/// <c>{ accessToken, refreshToken }</c> as a login response body and <c>{ refreshToken }</c> as a
/// refresh request body. The refresh token now travels only as an <c>httpOnly</c> cookie and has
/// disappeared from both bodies. The reason is that a refresh token in a JSON body has to be stored
/// by the SPA — <c>localStorage</c>, or a JavaScript variable that outlives a reload only if it is
/// written somewhere readable — and any script that runs on the page can read it, which makes one
/// XSS a permanent account takeover rather than a fifteen-minute one. <c>httpOnly</c> takes it out of
/// script's reach entirely. The access token stays in the body precisely so it is <em>not</em> a
/// cookie: held in memory and sent as <c>Authorization: Bearer</c>, it cannot be attached by the
/// browser to a cross-site request, which is what confines this API's CSRF exposure to the two routes
/// the cookie is scoped to.
/// </para>
///
/// <para>
/// <b><c>Secure</c> is unconditional, with no Development branch.</b> The tempting version reads
/// <c>Secure = !env.IsDevelopment()</c> so that <c>http://localhost:5173</c> works. What it actually
/// produces is a security-critical attribute whose value depends on an environment variable — and the
/// failure mode is a host started with <c>ASPNETCORE_ENVIRONMENT</c> unset or misspelled, silently
/// shipping session cookies over cleartext. Browsers treat <c>http://localhost</c> as a secure
/// context and send <c>Secure</c> cookies to it, so the branch buys nothing even in development. A
/// setting that is never off cannot be left off.
/// </para>
/// </summary>
internal static class AuthCookies
{
    /// <summary>The refresh token. <c>httpOnly</c>: no script ever reads this, in any browser.</summary>
    public const string RefreshCookieName = "eams_rt";

    /// <summary>
    /// The readable half of the double-submit pair. Deliberately <b>not</b> <c>httpOnly</c> — the SPA
    /// has to read it to echo it, which is the entire mechanism.
    /// </summary>
    public const string CsrfCookieName = "eams_csrf";

    /// <summary>The header the SPA echoes <see cref="CsrfCookieName"/> in.</summary>
    public const string CsrfHeaderName = "X-CSRF-Token";

    /// <summary>The path suffix the refresh cookie is scoped to, below the request's <c>PathBase</c>.</summary>
    public const string RefreshCookiePathSuffix = "/api/v1/auth";

    /// <summary>Bytes of CSRF token. 128 bits, rendered as 32 lower-case hex characters.</summary>
    private const int CsrfTokenBytes = 16;

    /// <summary>
    /// The refresh cookie's <c>Path</c>, derived from the request rather than configured.
    ///
    /// <para>
    /// <b>This is the single most likely thing in the phase to be silently wrong in production.</b>
    /// <c>docs/DEPLOY-IIS.md</c> mounts the API as an application named <c>eamsapi</c> under
    /// <c>Default Web Site</c>, so IIS hands ASP.NET Core a <c>PathBase</c> of <c>/eamsapi</c> and
    /// every route this process describes as <c>/api/v1/auth</c> is <c>/eamsapi/api/v1/auth</c> to the
    /// browser. A cookie written with the un-prefixed path is simply never sent back: the refresh call
    /// arrives with no cookie, answers <c>401</c>, and produces no error anywhere — not in the
    /// application log, not in the IIS log, not in the browser console. The symptom is "everyone is
    /// signed out every fifteen minutes" and it points at nothing.
    /// </para>
    ///
    /// <para>
    /// Hence <see cref="LogCookiePath"/>, which says the resolved value out loud the first time a
    /// cookie is issued, and the startup line beside the CORS one that says what the rule is.
    /// </para>
    /// </summary>
    public static string RefreshCookiePath(HttpRequest request) =>
        request.PathBase.HasValue
            ? request.PathBase.Value + RefreshCookiePathSuffix
            : RefreshCookiePathSuffix;

    /// <summary>
    /// Writes the refresh cookie and a fresh CSRF cookie.
    ///
    /// <para>
    /// <b>The CSRF cookie is rotated on every issue, not reused.</b> A long-lived token is one that
    /// leaks once and works forever; rotating it costs 16 bytes of entropy per login or refresh and
    /// bounds the value of a leaked one to a single fifteen-minute window.
    /// </para>
    ///
    /// <para>
    /// <b>Both cookies carry the same <c>Expires</c>, and the pair only works if they do.</b> The
    /// refresh cookie must survive a browser restart or "remember me" is a fiction — but the CSRF
    /// cookie is the other half of the double-submit pair that <c>/auth/refresh</c> requires. Giving
    /// only one of them a lifetime is what this originally did, and the result was that after every
    /// browser restart the refresh arrived carrying its credential and missing its pair, was answered
    /// <c>403 CsrfTokenInvalid</c>, and the user signed in again — making the refresh cookie's own
    /// persistence decorative.
    /// </para>
    ///
    /// <para>
    /// Neither half of that was wrong on its own, which is why it survived review twice: a persistent
    /// refresh cookie is correct, and a session-scoped CSRF token is a perfectly ordinary choice. The
    /// defect was the <em>interaction</em>. Two cookies that must be presented together need one
    /// lifetime, and this is the only place it is decided.
    /// </para>
    /// </summary>
    public static void Issue(HttpContext context, string refreshToken, DateTime refreshExpiresAtUtc)
    {
        var path = RefreshCookiePath(context.Request);
        LogCookiePath(context, path);

        // Computed once and applied to both cookies. Two expressions could drift; one cannot, and the
        // whole defect this replaced was the two halves disagreeing about how long they live.
        var expires = new DateTimeOffset(DateTime.SpecifyKind(refreshExpiresAtUtc, DateTimeKind.Utc));

        context.Response.Cookies.Append(RefreshCookieName, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = path,
            Expires = expires,
            IsEssential = true,
        });

        context.Response.Cookies.Append(CsrfCookieName, NewCsrfToken(), new CookieOptions
        {
            // Readable by design — see CsrfCookieName.
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,

            // Root path, NOT the refresh cookie's path, and the asymmetry is load-bearing. Under IIS
            // the SPA is a sibling application (`/eams`) of the API (`/eamsapi`), and a cookie scoped
            // to `/eamsapi/api/v1/auth` is not visible to `document.cookie` on a page served from
            // `/eams`. A CSRF cookie the client cannot read is a CSRF check that always fails.
            Path = "/",

            // The same lifetime as the refresh cookie, deliberately. A session cookie here expires on
            // browser close while `eams_rt` persists, and the next refresh is then refused for a
            // missing pair rather than an ended session. Rotation still bounds the value of a leaked
            // token: a fresh CSRF token is minted on every login and every refresh regardless.
            Expires = expires,
            IsEssential = true,
        });
    }

    /// <summary>
    /// Clears both cookies. <b>The options must match what was written</b> — a browser deletes a
    /// cookie by name <em>and</em> path, so a delete issued at the wrong path leaves the original in
    /// place and the user is still holding a live session after a logout that reported success.
    /// </summary>
    public static void Clear(HttpContext context)
    {
        ClearRefreshToken(context);

        context.Response.Cookies.Delete(CsrfCookieName, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }

    /// <summary>
    /// Clears the refresh cookie and leaves the CSRF cookie alone — what a <em>failed</em> refresh
    /// does, as opposed to a logout.
    ///
    /// <para>
    /// The distinction is not fussiness. Two browser tabs refreshing at once is ordinary; if the first
    /// failure took the CSRF cookie with it, the second tab would arrive without one and be answered
    /// <c>403 CsrfTokenInvalid</c> — telling a client its request was malformed when what actually
    /// happened is that its session ended. One condition must not surface as two codes depending on
    /// timing.
    /// </para>
    /// </summary>
    public static void ClearRefreshToken(HttpContext context) =>
        context.Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath(context.Request),
        });

    /// <summary>The refresh token the browser sent, or <c>null</c>.</summary>
    public static string? ReadRefreshToken(HttpRequest request) =>
        request.Cookies.TryGetValue(RefreshCookieName, out var value) && value.Length > 0
            ? value
            : null;

    /// <summary>
    /// The double-submit check, and the honest account of what it does and does not stop.
    ///
    /// <para>
    /// <b><c>SameSite=Strict</c> alone is not sufficient here, which is why this exists.</b> The
    /// usual argument — "a strict cookie is never sent on a cross-site request, so CSRF is
    /// impossible" — depends on the attacker's page being cross-<em>site</em>.
    /// <c>docs/DEPLOY-IIS.md</c> puts EAMS under IIS's <c>Default Web Site</c> alongside whatever else
    /// that site hosts, so a compromised sibling application is same-site (indeed same-origin), the
    /// cookie <em>is</em> sent, and <c>Strict</c> stops nothing.
    /// </para>
    ///
    /// <para>
    /// <b>What double-submit adds:</b> a request must carry a header, and neither an HTML form, an
    /// <c>&lt;img&gt;</c>, a navigation, nor any other browser-initiated request that is not scripted
    /// can set one. That closes form-based CSRF from a sibling path — the realistic shape of "someone
    /// uploaded a page to the other app" — as well as every cross-site variant.
    /// </para>
    ///
    /// <para>
    /// <b>What it does not close, stated rather than implied:</b> script executing on the same origin.
    /// Such a script can read <see cref="CsrfCookieName"/> from <c>document.cookie</c> and set the
    /// header itself. Nothing a server can do defends against that, and pretending otherwise is worse
    /// than saying so — the mitigation is that the sibling application must not be attacker-writable,
    /// which is a deployment property, not a code one.
    /// </para>
    ///
    /// <para>
    /// <b>Enforced on <c>/auth/refresh</c> and <c>/auth/logout</c> only.</b> Those are the only two
    /// routes in the entire API that a browser will attach an ambient credential to. Every other
    /// endpoint is reached with <c>Authorization: Bearer</c>, which a cross-site page cannot set, so
    /// applying this check to them would be ceremony that protects nothing and one more thing for a
    /// client to get wrong.
    /// </para>
    /// </summary>
    public static bool CsrfTokenMatches(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CsrfCookieName, out var cookie) || cookie.Length == 0)
            return false;

        var header = request.Headers[CsrfHeaderName].ToString();
        if (header.Length == 0) return false;

        // Length is compared first and in variable time on purpose: the token's length is fixed and
        // public (32 hex characters), so it is not a secret, and FixedTimeEquals requires equal spans.
        var presented = Encoding.UTF8.GetBytes(header);
        var expected = Encoding.UTF8.GetBytes(cookie);

        return presented.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static string NewCsrfToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(CsrfTokenBytes)).ToLowerInvariant();

    /// <summary>
    /// Says the resolved cookie path out loud, once per process, the first time a cookie is issued.
    ///
    /// <para>
    /// The startup line states the <em>rule</em>; this states the <em>answer</em>, and only the answer
    /// is checkable against what the browser did. Once, because it is a property of the deployment
    /// rather than of the request, and a line per login is a line nobody reads.
    /// </para>
    /// </summary>
    private static void LogCookiePath(HttpContext context, string path)
    {
        if (Interlocked.Exchange(ref _pathLogged, 1) != 0) return;

        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("EAMS.Auth")
            .LogInformation(
                "Refresh-token cookie Path resolved to '{Path}' (PathBase '{PathBase}'). If that is " +
                "not the path the browser sees for POST {Suffix}, the cookie is never sent back and " +
                "refresh answers 401 with nothing in any log.",
                path, context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "(none)",
                RefreshCookiePathSuffix);
    }

    private static int _pathLogged;
}
