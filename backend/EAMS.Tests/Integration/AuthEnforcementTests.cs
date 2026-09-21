using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using EAMS.Api.Authentication;
using EAMS.Application.Abstractions;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>Technical Plan §11 enforced over the wire: a request that carries no credential reaches nothing
/// but sign-in.</b> The live half of <c>AuthorizationCoverageTests</c>, which asserts the attributes;
/// this asserts what the real pipeline does with them.
///
/// <para>
/// <b>Derived from the host's own route table, on purpose.</b> The test this replaced kept a hand list
/// of open routes, because its job was to notice enforcement arriving by accident. This one's job is
/// the opposite — to notice a route that arrived without enforcement — and a hand list would miss
/// exactly the route nobody remembered to add to it.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public partial class AuthEnforcementTests : IntegrationTest
{
    public AuthEnforcementTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>The routes a caller holding nothing may reach, as "METHOD template".</summary>
    private static readonly string[] Anonymous =
    [
        "POST api/v1/auth/login",
        "POST api/v1/auth/refresh",
    ];

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();

    /// <summary>
    /// Every route, every method, no credential: <c>401</c>, challenged with the scheme the route's own
    /// <c>[Authorize]</c> names.
    ///
    /// <para>
    /// A <c>500</c> here usually means a policy the route names was never registered — which is why this
    /// test sends a request to every route rather than a sample: a missing policy is not a refusal, it is
    /// an exception on every request, and only the route that names it shows it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_route_outside_sign_in_refuses_a_request_with_no_credential()
    {
        await using (var db = NewDbContext())
        {
            db.Schools.Add(TestData.NewSchool());
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("api/v1/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (Method: method, Endpoint: e)))
            .Where(r => !Anonymous.Contains($"{r.Method} {r.Endpoint.RoutePattern.RawText}", StringComparer.Ordinal))
            .ToList();

        Assert.True(routes.Count > 40, $"Only {routes.Count} gated routes were found; the route table was not read.");

        foreach (var (method, endpoint) in routes)
        {
            var template = endpoint.RoutePattern.RawText!;
            var path = "/" + RouteParameter().Replace(template, _ => Guid.NewGuid().ToString());

            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT" or "PATCH")
            {
                // The body type the route accepts, or routing answers 415 before authorization is
                // asked anything: the roster upload takes multipart, everything else JSON.
                var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes ?? [];
                request.Content = accepts.Any(t => t.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                    ? new MultipartFormDataContent()
                    : new StringContent("{}", Encoding.UTF8, "application/json");
            }

            var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{method} /{template} answered {(int)response.StatusCode} with no credential. Every route " +
                "outside sign-in must refuse an anonymous caller. A 2xx or 4xx other than 401 means the " +
                "route has no [Authorize]; a 500 usually means its policy was never registered.");

            var expectedScheme = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.AuthenticationSchemes)
                .Single(s => s is not null);

            // The shared route forwards a request with no device key to Bearer, so Bearer challenges.
            if (expectedScheme == DeviceKeyOrBearer.AuthenticationScheme)
                expectedScheme = "Bearer";

            Assert.Equal(expectedScheme, response.Headers.WwwAuthenticate.Single().Scheme);
        }
    }

    /// <summary>
    /// <b>401 and 403 are different answers, and an operator must get the right one.</b> A signed-in
    /// Viewer holds <c>students.read</c> and not <c>students.write</c>: the read succeeds, the write is
    /// refused as forbidden. A 401 on the write would tell someone who is already signed in to sign in
    /// again, which is a loop with no exit; a 2xx would mean the policy admits any signed-in person
    /// whatever their role grants.
    /// </summary>
    [Fact]
    public async Task A_signed_in_operator_is_forbidden_what_their_role_does_not_grant()
    {
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var viewer = await SignedInClientAsync(factory, schoolId, EamsRoleNames.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/students")).StatusCode);

        var write = await viewer.PostAsJsonAsync("/api/v1/students", new { studentNumber = "2023-0001" });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var mint = await viewer.PostAsJsonAsync("/api/v1/devices", new { name = "Rogue", deviceType = "Kiosk" });
        Assert.Equal(HttpStatusCode.Forbidden, mint.StatusCode);
    }

    [Fact]
    public async Task The_device_scheme_still_answers_its_own_challenge_shape()
    {
        await using (var db = NewDbContext())
        {
            db.Schools.Add(TestData.NewSchool());
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/students/by-card/04A7B8C9");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // DeviceKey, not Bearer. The mobile client's published contract says DeviceKey, and a Bearer
        // challenge would send an offline queue looking for a login endpoint it has no credential for.
        Assert.Equal(
            "DeviceKey",
            Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }
}
