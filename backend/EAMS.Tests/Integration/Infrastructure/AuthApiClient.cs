using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// A browser, as far as the <c>/auth</c> surface is concerned — one that this suite drives by hand.
///
/// <para>
/// <b>Cookies are handled manually (<c>HandleCookies = false</c>) rather than by
/// <c>CookieContainerHandler</c>, and that is not a convenience.</b> Two reasons, both load-bearing
/// for what these tests exist to prove:
/// </para>
///
/// <list type="number">
///   <item>
///     <c>CookieContainer</c> silently withholds a <c>Secure</c> cookie from an <c>http://</c> URI.
///     Since <see cref="AuthCookies"/> sets <c>Secure</c> unconditionally, an automatic container over
///     the default <c>http://localhost</c> test address would never echo the refresh cookie and every
///     refresh test would fail for a reason that has nothing to do with the code under test.
///   </item>
///   <item>
///     Half of these tests are about a cookie or a header being <em>absent</em>, <em>wrong</em>, or
///     <em>reused after rotation</em>. A container that manages that for you cannot be asked to get it
///     wrong on purpose.
///   </item>
/// </list>
///
/// <para>
/// The base address is <c>https://</c> so <c>Request.IsHttps</c> is true and nothing in the pipeline
/// takes a different branch than it would in production.
/// </para>
/// </summary>
internal sealed class AuthApiClient : IDisposable
{
    private static readonly WebApplicationFactoryClientOptions Options = new()
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = false,
        AllowAutoRedirect = false,
    };

    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public AuthApiClient(WebApplicationFactory<Program> factory) => Http = factory.CreateClient(Options);

    public HttpClient Http { get; }

    /// <summary>The access token from the last successful login or refresh, if any.</summary>
    public string? AccessToken { get; private set; }

    /// <summary>The <c>Set-Cookie</c> header values from the last response, verbatim and unparsed.</summary>
    public IReadOnlyList<string> LastSetCookies { get; private set; } = [];

    public string? Cookie(string name) => _cookies.TryGetValue(name, out var v) ? v : null;

    /// <summary>Forgets a cookie without telling the server — a browser with its jar cleared.</summary>
    public void DropCookie(string name) => _cookies.Remove(name);

    public void SetCookie(string name, string value) => _cookies[name] = value;

    /// <summary>Forgets the access token, so the next call is unauthenticated.</summary>
    public void DropAccessToken() => AccessToken = null;

    // --------------------------------------------------------------------------------- the calls

    public Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        SendAsync(HttpMethod.Post, "/api/v1/auth/login",
            JsonContent.Create(new { email, password }), csrf: false);

    /// <param name="csrf">
    /// Whether to send the <c>X-CSRF-Token</c> header. <c>false</c> is what a cross-site form can do;
    /// the tests use it to prove the double-submit check is real rather than decorative.
    /// </param>
    public Task<HttpResponseMessage> RefreshAsync(bool csrf = true, string? csrfOverride = null) =>
        SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", content: null, csrf, csrfOverride);

    public Task<HttpResponseMessage> LogoutAsync(bool csrf = true) =>
        SendAsync(HttpMethod.Post, "/api/v1/auth/logout", content: null, csrf);

    public Task<HttpResponseMessage> MeAsync() =>
        SendAsync(HttpMethod.Get, "/api/v1/auth/me", content: null, csrf: false);

    public Task<HttpResponseMessage> ChangePasswordAsync(string current, string next) =>
        SendAsync(HttpMethod.Post, "/api/v1/auth/change-password",
            JsonContent.Create(new { currentPassword = current, newPassword = next }), csrf: false);

    public Task<HttpResponseMessage> ProbeAsync() =>
        SendAsync(HttpMethod.Get, AuthProbeController.Route, content: null, csrf: false);

    // -------------------------------------------------------------------------------- the plumbing

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content, bool csrf, string? csrfOverride = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };

        if (AccessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        if (_cookies.Count > 0)
        {
            request.Headers.Add(
                "Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        if (csrf)
        {
            var token = csrfOverride ?? Cookie(AuthCookies.CsrfCookieName);
            if (token is not null) request.Headers.Add(AuthCookies.CsrfHeaderName, token);
        }

        var response = await Http.SendAsync(request);

        Absorb(response);
        await CaptureAccessTokenAsync(response);

        return response;
    }

    /// <summary>
    /// Applies <c>Set-Cookie</c> the way a browser would, including a deletion: the server clears a
    /// cookie by re-sending it empty with an expiry in the past, and a jar that ignored that would let
    /// a logged-out client keep presenting a dead session.
    /// </summary>
    private void Absorb(HttpResponseMessage response)
    {
        LastSetCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values]
            : [];

        foreach (var header in LastSetCookies)
        {
            var pair = header.Split(';', 2)[0];
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0) continue;

            var name = pair[..split].Trim();
            var value = pair[(split + 1)..].Trim();

            if (value.Length == 0) _cookies.Remove(name);
            else _cookies[name] = value;
        }
    }

    private async Task CaptureAccessTokenAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK) return;
        if (response.Content.Headers.ContentType?.MediaType != "application/json") return;

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        if (json.RootElement.TryGetProperty("accessToken", out var token))
            AccessToken = token.GetString();
    }

    /// <summary>The <c>Set-Cookie</c> header for one cookie from the last response, or null.</summary>
    public string? SetCookieHeader(string name) =>
        LastSetCookies.FirstOrDefault(h => h.StartsWith(name + "=", StringComparison.Ordinal));

    public void Dispose() => Http.Dispose();
}
