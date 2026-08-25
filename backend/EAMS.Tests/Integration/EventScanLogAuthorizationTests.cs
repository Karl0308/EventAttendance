using System.Net;
using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>GET /events/{id}/scans</c> is the first read in this API that authorization actually enforces.
///
/// <para>
/// <b>Why these tests carry more weight than the endpoint's size suggests.</b> Every other read is
/// still open under ADR-001 D-6, so this is the only route where the §11 machinery - the Bearer
/// scheme, the permission claim, the policy - is load-bearing rather than recorded. If the gate
/// silently stops refusing, no other test in the suite notices, because no other route is gated for
/// it to be compared against.
/// </para>
///
/// <para>
/// The three cases below are the three answers the endpoint can give a caller, and they are
/// deliberately distinct: <c>401</c> says the request carried no identity, <c>403</c> says it carried
/// one that is not allowed. Collapsing them tells a signed-in operator to sign in again, which is a
/// dead end - they already did.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventScanLogAuthorizationTests : IntegrationTest
{
    public EventScanLogAuthorizationTests(SqlServerFixture sql) : base(sql) { }

    private const string Email = "scan.reader@usa.edu.ph";
    private const string Password = "correct-horse-battery";

    private async Task<(Guid SchoolId, Guid EventId)> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var ev = TestData.NewEvent(school.Id, "Open", "Single", 15, null);
        db.Events.Add(ev);

        await db.SaveChangesAsync();
        return (school.Id, ev.Id);
    }

    private string Route(Guid eventId) => $"/api/v1/events/{eventId}/scans";

    [Fact]
    public async Task An_uncredentialed_request_is_refused()
    {
        var (_, eventId) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route(eventId));

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized,
            $"GET the scan log with no credential answered {(int)response.StatusCode}. This endpoint " +
            "is deliberately gated while the rest of the API is open, so a 200 here means the " +
            "[Authorize] attribute or its policy was dropped - and the report carries card serials " +
            "and scan times for a whole event.");
    }

    [Fact]
    public async Task A_signed_in_operator_with_events_read_may_read_it()
    {
        var (schoolId, eventId) = await ArrangeAsync();
        await CreateUserAsync(schoolId, Email, Password, EamsRoleNames.Viewer);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var login = await client.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var response = await client.Http.WithBearer(client.AccessToken!).GetAsync(Route(eventId));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"A signed-in Viewer - a role that carries events.read - was answered " +
            $"{(int)response.StatusCode}. If this is 403 the policy is demanding a claim the token " +
            "does not carry, and every operator is locked out of a report they are entitled to.");
    }

    /// <summary>
    /// Authenticated, but without the permission: <c>403</c>, and specifically not <c>401</c>.
    ///
    /// <para>
    /// Every seeded role happens to carry <c>events.read</c>, so the only way to reach this state is
    /// to take the grant away - which is also the realistic one, since an administrator narrowing a
    /// role is exactly how a person ends up authenticated and not entitled.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_signed_in_operator_without_events_read_is_forbidden_not_challenged()
    {
        var (schoolId, eventId) = await ArrangeAsync();
        var userId = await CreateUserAsync(schoolId, Email, Password, EamsRoleNames.Viewer);

        await using (var db = NewDbContext())
        {
            await db.UserRoles.IgnoreQueryFilters()
                .Where(ur => ur.UserId == userId)
                .ExecuteDeleteAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var login = await client.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var response = await client.Http.WithBearer(client.AccessToken!).GetAsync(Route(eventId));

        Assert.True(
            response.StatusCode == HttpStatusCode.Forbidden,
            $"An authenticated operator carrying no permissions was answered " +
            $"{(int)response.StatusCode}. It must be 403: a 401 tells someone who is already signed " +
            "in to sign in again, which is a loop they cannot escape, and a 200 means the policy is " +
            "admitting any authenticated principal regardless of what they were granted.");
    }

    /// <summary>
    /// A device key must not open it.
    ///
    /// <para>
    /// A capture credential is scoped to <c>attendance.capture</c> and belongs to a handset in a
    /// corridor, not to a person. The policy names the Bearer scheme rather than only the claim
    /// precisely so that a second scheme cannot satisfy it - and a device key that could read a whole
    /// event's scan log would be a credential on a shared device reading a report about the people it
    /// scanned.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_device_key_cannot_read_the_scan_log()
    {
        var (schoolId, eventId) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        var deviceKey = await IssueDeviceKeyAsync(schoolId);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"DeviceKey {deviceKey}");

        var response = await client.GetAsync(Route(eventId));

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A device key was answered {(int)response.StatusCode} on the scan log. A capture " +
            "credential must not read reports: it lives on a shared handset, it is scoped to " +
            "attendance.capture, and this report names every card presented at an event.");
    }
}
