using System.Net;
using System.Net.Http.Json;
using EAMS.Api.Cors;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The browser-origin policy: who the API admits, who it does not, and the one ordering property that
/// decides whether a cross-origin caller can read an error at all.
///
/// <para>
/// None of this is reachable from a service-level test — it is decided in <c>Program.cs</c>, and CORS
/// is the class of thing that is invisible to every non-browser client and total to a browser one. A
/// policy that admits nothing looks identical to a working one from <c>curl</c>.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CorsTests : IntegrationTest
{
    public CorsTests(SqlServerFixture sql) : base(sql) { }

    private const string AllowOriginHeader = "Access-Control-Allow-Origin";
    private const string DisallowedOrigin = "https://not-the-admin-spa.example";

    private static HttpRequestMessage Preflight(string route, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, route);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return request;
    }

    private static HttpRequestMessage From(HttpMethod method, string route, string origin)
    {
        var request = new HttpRequestMessage(method, route);
        request.Headers.Add("Origin", origin);
        return request;
    }

    // --------------------------------------------------------------------------- development

    /// <summary>
    /// The whole reason this exists: the admin SPA runs on Vite at <c>:5173</c> and this API on
    /// <c>:5080</c>, so every call it makes is cross-origin and the browser drops the response without
    /// a matching preflight answer.
    /// </summary>
    [Fact]
    public async Task A_preflight_from_the_dev_server_is_allowed()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            Preflight("/api/v1/students", LocalDevelopmentCors.ViteDevServerOrigin));

        Assert.True(response.Headers.TryGetValues(AllowOriginHeader, out var allowed),
            "A preflight from the configured dev-server origin must be answered with " +
            $"{AllowOriginHeader}, or the SPA cannot call this API at all.");
        Assert.Equal(LocalDevelopmentCors.ViteDevServerOrigin, Assert.Single(allowed!));
    }

    /// <summary>
    /// The other half, and the one that makes the first half mean something: the policy is a
    /// whitelist, not <c>AllowAnyOrigin</c>. An echoed arbitrary origin would pass the test above
    /// exactly as well.
    /// </summary>
    [Fact]
    public async Task A_preflight_from_an_unlisted_origin_is_not_allowed()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/api/v1/students", DisallowedOrigin));

        Assert.False(response.Headers.Contains(AllowOriginHeader),
            $"{DisallowedOrigin} is not a configured origin and must not be echoed back. A wildcard " +
            "or reflected origin would make the whitelist decorative.");
    }

    [Fact]
    public async Task A_simple_request_from_the_dev_server_carries_the_allow_header()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            From(HttpMethod.Get, "/api/v1/students", LocalDevelopmentCors.ViteDevServerOrigin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(AllowOriginHeader));
    }

    /// <summary>
    /// A 500 stays readable cross-origin, which is what makes the API's RFC 7807 body worth anything
    /// to the SPA: a browser that cannot read an error response reports a CORS failure and never
    /// surfaces the <c>traceId</c> an operator would search logs by.
    ///
    /// <para>
    /// <b>What this does not assert, stated so nobody reads more into it.</b> The worry it started
    /// from was ordering — <c>ExceptionHandlerMiddleware</c> clears the response before writing its
    /// ProblemDetails, so headers written on the way down would be lost. Negative-controlled by moving
    /// <c>UseCors</c> ahead of <c>UseExceptionHandler</c>: this test still passed, because
    /// <c>CorsMiddleware</c> applies its headers through <c>Response.OnStarting</c>, after the
    /// clearing. So the ordering in <c>Program.cs</c> is convention, not the thing holding this up,
    /// and this test would <em>not</em> catch a reordering. It catches the behaviour going away —
    /// which is what a client actually depends on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_server_error_is_still_readable_from_an_allowed_origin()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            From(HttpMethod.Get, "/test-only/fault-probe", LocalDevelopmentCors.ViteDevServerOrigin));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.Contains(AllowOriginHeader),
            "A 500 without the CORS header is unreadable in a browser: the SPA sees a CORS error and " +
            "never gets the traceId it would otherwise quote back.");
    }

    /// <summary>
    /// <c>Location</c> has to be readable cross-origin, or the two 201s that set it are lying to the
    /// only client that will consume them.
    ///
    /// <para>
    /// CORS exposes exactly seven response headers by default and <c>Location</c> is not among them:
    /// without <c>WithExposedHeaders</c> the browser receives the header, sees it is not on the list,
    /// and hides it from JavaScript. The SPA would get a 201 it cannot follow — no error, no clue,
    /// just an undefined value where the new resource's URL should be. Asserted through
    /// <c>Access-Control-Expose-Headers</c> because that is the wire mechanism; the header itself is
    /// present either way, which is precisely why this fails silently in a browser and passes in every
    /// non-browser test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_location_header_of_a_201_is_exposed_to_the_browser()
    {
        await using (var db = NewDbContext())
        {
            db.Schools.Add(TestData.NewSchool());
            await db.SaveChangesAsync();
        }

        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/students")
        {
            Content = JsonContent.Create(new
            {
                studentNumber = "2023-0777",
                firstName = "Juan",
                lastName = "Dela Cruz",
            }),
        };
        request.Headers.Add("Origin", LocalDevelopmentCors.ViteDevServerOrigin);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        Assert.True(
            response.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposed),
            "A 201 whose Location the browser hides is a 201 the SPA cannot follow.");
        Assert.Contains(exposed!, value =>
            value.Split(',').Any(h => h.Trim().Equals("Location", StringComparison.OrdinalIgnoreCase)));
    }

    // ---------------------------------------------------------------------------- production

    /// <summary>
    /// The dev default must not survive into a deployed environment. There is no production origin in
    /// this build by decision — the SPA is not wired to this API yet — so an unconfigured non-Development
    /// host admits nobody rather than permanently trusting a page served from the viewer's own machine.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_production_host_admits_no_origin()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            Preflight("/api/v1/students", LocalDevelopmentCors.ViteDevServerOrigin));

        Assert.False(response.Headers.Contains(AllowOriginHeader),
            "The Vite dev-server default is Development-only. A production host that has not been " +
            "given Cors:AllowedOrigins must not silently trust localhost.");
    }

    // --------------------------------------------------------------------------- configuration

    /// <summary>
    /// The resolution rule itself, without a host: configuration wins, the dev default applies only in
    /// Development, and a blank entry is dropped rather than passed to <c>WithOrigins</c> — where it
    /// would look configured and match nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unconfigured_origin_list_falls_back_only_in_development(bool development)
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new StubEnvironment(development);

        var origins = LocalDevelopmentCors.ResolveOrigins(configuration, environment);

        if (development)
            Assert.Equal([LocalDevelopmentCors.ViteDevServerOrigin], origins);
        else
            Assert.Empty(origins);
    }

    [Fact]
    public void A_configured_origin_list_wins_and_blank_entries_are_dropped()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{LocalDevelopmentCors.ConfigurationSection}:0"] = "  https://admin.example  ",
                [$"{LocalDevelopmentCors.ConfigurationSection}:1"] = "   ",
            })
            .Build();

        var origins = LocalDevelopmentCors.ResolveOrigins(configuration, new StubEnvironment(true));

        Assert.Equal(["https://admin.example"], origins);
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public StubEnvironment(bool development) =>
            EnvironmentName = development ? Environments.Development : Environments.Production;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "EAMS.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
