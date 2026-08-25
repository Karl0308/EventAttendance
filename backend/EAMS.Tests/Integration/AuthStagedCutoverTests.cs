using System.Net;
using EAMS.Api.Authorization;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>The invariant Phase 6b promised and that nothing else would notice being broken: adding
/// <c>/auth</c> changed nothing about any other endpoint.</b>
///
/// <para>
/// ADR-001 D-6 defers §11's authorization until after the data layer settles, and Phase 6b is
/// deliberately only the <em>authentication</em> surface — the login exists, and enforcing it on the
/// roster, the events and the import pipeline is a later phase's decision. That makes this a staged
/// cutover, and the hazard of a staged cutover is that the stage silently completes itself: one
/// <c>[Authorize]</c> added to a base class, one <c>FallbackPolicy</c> set while wiring the scheme,
/// and every existing client breaks at once with no test saying so.
/// </para>
///
/// <para>
/// These assertions are the opposite of aspirational. They will <em>fail</em> on the day enforcement
/// is deliberately rolled out, and that is correct: the phase that turns authorization on is the
/// phase that gets to delete them, having looked at each one.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AuthStagedCutoverTests : IntegrationTest
{
    public AuthStagedCutoverTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// One representative read from each controller that is still open. Deliberately <em>not</em>
    /// derived from the route table: a derived list would follow a mistake that added
    /// <c>[Authorize]</c> everywhere, and this test exists precisely to notice that.
    /// </summary>
    private static readonly string[] OpenRoutes =
    [
        "/api/v1/students",
        "/api/v1/events",
        "/api/v1/attendance",
        "/api/v1/devices",
        "/api/v1/student-groups",
        "/api/v1/academic/terms",
        "/api/v1/academic/colleges",
    ];

    public static TheoryData<string> OpenEndpoints => [.. OpenRoutes];

    [Theory]
    [MemberData(nameof(OpenEndpoints))]
    public async Task An_uncredentialed_request_to_a_non_auth_endpoint_still_succeeds(string route)
    {
        await using (var db = NewDbContext())
        {
            db.Schools.Add(TestData.NewSchool());
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(route);

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {route} answered {(int)response.StatusCode} with no credential. Every endpoint " +
            $"outside /auth is still open under ADR-001 D-6, and Phase 6b was additive by " +
            $"construction — a 401 or 403 here means the Bearer scheme was wired as a fallback " +
            $"policy or an [Authorize] leaked onto a controller that must not have one yet.");
    }

    [Fact]
    public async Task No_non_auth_endpoint_answers_with_a_www_authenticate_challenge()
    {
        await using (var db = NewDbContext())
        {
            db.Schools.Add(TestData.NewSchool());
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        foreach (var route in OpenRoutes)
        {
            var response = await client.GetAsync(route);

            Assert.True(
                response.Headers.WwwAuthenticate.Count == 0,
                $"GET {route} sent a WWW-Authenticate challenge. The endpoint is open; a challenge " +
                $"means something started asking for a credential.");
        }
    }

    [Fact]
    public void Authorization_is_still_not_enforced()
    {
        // The assembly-level marker ADR-001 D-6 created, read through the accessor rather than by
        // looking for the attribute here — one source of truth, and removing the attribute is part of
        // a later phase's definition of done rather than a side effect of this one.
        Assert.False(
            AuthorizationStatus.IsEnforced,
            "[assembly: AuthorizationNotEnforced] is gone from EAMS.Api. Phase 6b ships the /auth " +
            "surface only; removing that marker is the later phase's statement that §11 is enforced " +
            "across the API, and nothing in this phase is entitled to make it.");
    }

    [Fact]
    public void The_inert_permission_attribute_is_still_inert_and_still_named_so()
    {
        var attribute = typeof(HasPermissionNotEnforcedAttribute);

        // Structural, not textual. The attribute must remain incapable of enforcing anything — it
        // implements no MVC filter interface, so the pipeline never sees it — which is the property
        // that lets thirty endpoints carry it while being open.
        Assert.Empty(attribute.GetInterfaces());
        Assert.Equal(typeof(Attribute), attribute.BaseType);

        // And the name still says so at every call site. Renaming it to [HasPermission] is the
        // rename D-6 designed as the audit trigger, and it belongs to the phase that turns
        // enforcement on.
        Assert.Equal("HasPermissionNotEnforcedAttribute", attribute.Name);
    }

    // The exact set of [Authorize]-carrying actions, and the scheme each one names, is asserted by
    // AuthorizationSeamTests.Only_the_device_capture_endpoints_are_gated_by_a_real_authorization_attribute
    // — the allow-list that has held that invariant since Phase 4b. Phase 6b extended it with the
    // three Bearer actions rather than adding a second list here: two guards over one invariant is how
    // one of them ends up out of date and trusted anyway.

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

        // DeviceKey, not Bearer. Adding a second scheme must not change which one challenges on a
        // device-gated route — the mobile client's published contract says DeviceKey, and a Bearer
        // challenge would send an offline queue looking for a login endpoint it has no credential for.
        Assert.Equal(
            "DeviceKey",
            Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }
}
