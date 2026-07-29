using System.Net;
using EAMS.Api.Cors;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// How closely an origin has to match, and what CORS does <em>not</em> do.
///
/// <para>
/// <see cref="CorsTests"/> proves the whitelist is a whitelist by rejecting
/// <c>https://not-the-admin-spa.example</c> — an origin that shares nothing with the allowed one. That
/// catches <c>AllowAnyOrigin</c> and a reflected origin, which are the two ways the policy could be
/// decorative. It does not describe the boundary, and the boundary is where the operational failures
/// actually are: an origin that differs from the allowed one by a trailing slash, a port, a scheme or
/// <c>127.0.0.1</c>-versus-<c>localhost</c> is refused, and the browser reports all four the same
/// indistinguishable way.
/// </para>
///
/// <para>
/// This matters now rather than in the abstract because Phase 3b-1 is the release that gives the SPA
/// something to <em>write</em>. A GET that is refused cross-origin shows an empty grid; a preflighted
/// <c>PUT</c> that is refused loses the operator's edit, and the console message names CORS rather than
/// the form.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CorsOriginMatchingTests : IntegrationTest
{
    public CorsOriginMatchingTests(SqlServerFixture sql) : base(sql) { }

    private const string AllowOriginHeader = "Access-Control-Allow-Origin";
    private const string AllowMethodsHeader = "Access-Control-Allow-Methods";
    private const string AllowHeadersHeader = "Access-Control-Allow-Headers";

    private const string Route = "/api/v1/students";

    private static HttpRequestMessage Preflight(string origin, string method = "POST")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, Route);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return request;
    }

    // ------------------------------------------------------------------------ near misses

    /// <summary>
    /// Four origins that a person would call "the same as" the allowed one, and that the policy does not.
    ///
    /// <para>
    /// Each is a real way to arrive at the wrong string:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Trailing slash</b> — never sent by a browser, but the shape a human writes into
    ///   <c>Cors:AllowedOrigins</c>. It is here as the mirror of the configuration case below.</item>
    ///   <item><b>Different port</b> — Vite picks the next free port when 5173 is taken, silently, and
    ///   prints the new one where nobody reads it.</item>
    ///   <item><b>Different scheme</b> — <c>vite --https</c>, or a proxy in front of the dev server.</item>
    ///   <item><b><c>127.0.0.1</c></b> — the same machine, the same server, a different origin. This is
    ///   the one that costs an afternoon, because the SPA loads and only the API calls fail.</item>
    /// </list>
    ///
    /// <para>
    /// None of these is a bug: origin comparison is exact by specification, and it has to be. They are
    /// pinned so that the failure is documented somewhere other than a browser console, and so that a
    /// future attempt to be helpful — a prefix match, a host-only match, a "localhost is localhost"
    /// special case — is a failing test rather than a quiet widening of the whitelist.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://localhost:5174")]
    [InlineData("https://localhost:5173")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("HTTP://LOCALHOST:5173")]
    [InlineData("http://localhost")]
    public async Task An_origin_that_differs_from_the_allowed_one_in_any_way_is_refused(string origin)
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(origin));

        Assert.False(response.Headers.Contains(AllowOriginHeader),
            $"'{origin}' is not '{LocalDevelopmentCors.ViteDevServerOrigin}'. Origin matching is exact " +
            "by specification; anything looser is a whitelist that admits strings nobody listed.");
    }

    // -------------------------------------------------------------------- the write surface

    /// <summary>
    /// The three methods Phase 3b-1 added, each preflighted from the allowed origin.
    ///
    /// <para>
    /// <c>AllowAnyMethod</c> makes this true, and that is exactly why it is worth asserting: the policy
    /// says "any" once, in one line, and every write endpoint depends on it without naming it. A future
    /// narrowing to <c>WithMethods("GET", "POST")</c> — the kind of tightening that reads as a hardening
    /// improvement — would leave <c>PUT</c> and <c>DELETE</c> unreachable from the browser while every
    /// server-side test kept passing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task A_preflight_for_each_write_method_is_answered_from_the_allowed_origin(string method)
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            Preflight(LocalDevelopmentCors.ViteDevServerOrigin, method));

        Assert.True(response.Headers.TryGetValues(AllowOriginHeader, out var allowed));
        Assert.Equal(LocalDevelopmentCors.ViteDevServerOrigin, Assert.Single(allowed!));

        Assert.True(response.Headers.TryGetValues(AllowMethodsHeader, out var methods),
            $"A preflight naming {method} must be answered with {AllowMethodsHeader}, or the browser " +
            "never sends the real request.");
        Assert.Contains(method, string.Join(',', methods!), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>content-type</c> must come back allowed, or every JSON body on the new write surface is
    /// blocked before it is sent.
    ///
    /// <para>
    /// This is the one preflight header whose absence is invisible in a GET-only application: a simple
    /// <c>GET</c> is not preflighted at all, so a policy missing <c>AllowAnyHeader</c> would have looked
    /// completely healthy right up until the first <c>POST /students</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_json_content_type_is_allowed_on_the_preflight()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(LocalDevelopmentCors.ViteDevServerOrigin));

        Assert.True(response.Headers.TryGetValues(AllowHeadersHeader, out var headers));
        Assert.Contains("content-type", string.Join(',', headers!), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ what CORS is not

    /// <summary>
    /// <b>A refused origin still gets the data.</b>
    ///
    /// <para>
    /// The most important thing in this file, and the reason it is asserted rather than assumed. CORS is
    /// enforced by the <em>browser</em>: the server runs the request, produces the response, and merely
    /// omits a header that tells a browser it may be read. <c>curl</c>, a script, a mobile client, and
    /// any non-browser caller are entirely unaffected.
    /// </para>
    ///
    /// <para>
    /// Every endpoint in this build is open (ADR-001 D-6), and a reader who takes
    /// <c>An_origin_that_differs_from_the_allowed_one_in_any_way_is_refused</c> as evidence of access
    /// control would conclude the API is protected from anything but the admin SPA. It is not, and this
    /// is what says so. When §11's auth lands, this test is unaffected — which is itself the point:
    /// authorization and CORS are answering different questions and neither substitutes for the other.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_refused_origin_is_a_missing_header_and_not_a_refused_request()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.Add("Origin", "https://not-the-admin-spa.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(AllowOriginHeader));

        // The body really was produced and sent. Only a browser would decline to hand it to script.
        Assert.False(string.IsNullOrEmpty(await response.Content.ReadAsStringAsync()));
    }

    // ------------------------------------------------------------------- configuration shape

    /// <summary>
    /// A configured origin is used verbatim, so a trailing slash written into
    /// <c>Cors:AllowedOrigins</c> produces an entry that matches nothing.
    ///
    /// <para>
    /// The resolution step trims whitespace and drops blanks — it deliberately does not canonicalize —
    /// so this is the paired half of the trailing-slash case above: the string a browser sends never has
    /// the slash, and the string an operator types often does. Pinned because the symptom is the worst
    /// kind: the startup log says the policy admits <c>https://admin.example/</c>, which is exactly what
    /// was configured, and every call is still refused.
    /// </para>
    /// </summary>
    [Fact]
    public void A_configured_origin_is_not_canonicalized_so_a_trailing_slash_survives()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{LocalDevelopmentCors.ConfigurationSection}:0"] = "https://admin.example/",
            })
            .Build();

        var origins = LocalDevelopmentCors.ResolveOrigins(configuration, new DevelopmentEnvironment());

        Assert.Equal(["https://admin.example/"], origins);
    }

    /// <summary>
    /// Configuring any origin at all removes the Vite fallback, including on a Development host.
    ///
    /// <para>
    /// <c>ResolveOrigins</c> returns the configured list <em>or</em> the fallback, never both, and the
    /// existing coverage only exercises the unconfigured branch against the environment gate. The
    /// consequence is the one a developer meets first: adding a staging origin to
    /// <c>appsettings.Development.json</c> silently stops their own <c>npm run dev</c> from reaching the
    /// API, and nothing in the log calls that out because the policy is not empty.
    /// </para>
    /// </summary>
    [Fact]
    public void Configuring_any_origin_replaces_the_development_fallback_rather_than_adding_to_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{LocalDevelopmentCors.ConfigurationSection}:0"] = "https://staging.example",
            })
            .Build();

        var origins = LocalDevelopmentCors.ResolveOrigins(configuration, new DevelopmentEnvironment());

        Assert.Equal(["https://staging.example"], origins);
        Assert.DoesNotContain(LocalDevelopmentCors.ViteDevServerOrigin, origins);
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "EAMS.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
