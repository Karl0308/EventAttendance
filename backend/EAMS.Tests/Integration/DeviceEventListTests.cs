using System.Net;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>GET /events</c> with a device key — the capture app's event picker, which keeps working unchanged
/// now that the rest of the admin surface demands a signed-in person.
///
/// <para>
/// What a device gets is deliberately narrower than what an operator gets, and each test pins one edge:
/// only <c>Open</c> events, only its own school's, whatever <c>status</c> it asks for; an inactive
/// device is refused; and the operator's view of the same route is unchanged.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DeviceEventListTests : IntegrationTest
{
    public DeviceEventListTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/events";

    private sealed record World(Guid MineId, Guid TheirsId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var mine = TestData.NewSchool("AAA");
        var theirs = TestData.NewSchool("ZZZ");
        db.Schools.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        Event Named(Guid schoolId, string status, string name)
        {
            var e = TestData.NewEvent(schoolId, status);
            e.Name = name;
            return e;
        }

        db.Events.AddRange(
            Named(mine.Id, "Open", "Mine Open"),
            Named(mine.Id, "Draft", "Mine Draft"),
            Named(mine.Id, "Closed", "Mine Closed"),
            Named(theirs.Id, "Open", "Theirs Open"));
        await db.SaveChangesAsync();

        return new World(mine.Id, theirs.Id);
    }

    private static async Task<List<string?>> NamesAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return [.. body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_device_sees_only_its_own_schools_open_events_whatever_status_it_asks_for()
    {
        var world = await ArrangeAsync();

        // Keyed to the school the pin does NOT choose as well, so a result that matched could not be
        // the development pin answering in place of the device's claim.
        var theirKey = await IssueDeviceKeyAsync(world.TheirsId);
        var myKey = await IssueDeviceKeyAsync(world.MineId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var mine = factory.CreateClient().WithDeviceKey(myKey);
        using var theirs = factory.CreateClient().WithDeviceKey(theirKey);

        Assert.Equal(["Mine Open"], await NamesAsync(await mine.GetAsync(Route)));
        Assert.Equal(["Mine Open"], await NamesAsync(await mine.GetAsync($"{Route}?status=Open")));
        Assert.Equal(["Mine Open"], await NamesAsync(await mine.GetAsync($"{Route}?status=Draft")));
        Assert.Equal(["Theirs Open"], await NamesAsync(await theirs.GetAsync(Route)));
    }

    [Fact]
    public async Task An_operator_still_lists_every_status()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var operatorClient = await SignedInClientAsync(factory, world.MineId);

        Assert.Equal(
            ["Mine Closed", "Mine Draft", "Mine Open"],
            await NamesAsync(await operatorClient.GetAsync(Route)));
        Assert.Equal(["Mine Draft"], await NamesAsync(await operatorClient.GetAsync($"{Route}?status=Draft")));
    }

    /// <summary>
    /// A deactivated device authenticates without the capture permission, so the shared policy refuses it
    /// with 403 — the same answer it gets on every capture route.
    /// </summary>
    [Fact]
    public async Task An_inactive_device_is_forbidden()
    {
        var world = await ArrangeAsync();
        var key = await IssueDeviceKeyAsync(world.MineId, name: "Retired Kiosk");

        await using (var db = NewDbContext())
        {
            await db.Devices.IgnoreQueryFilters()
                .Where(d => d.Name == "Retired Kiosk")
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsActive, false));
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var device = factory.CreateClient().WithDeviceKey(key);

        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task No_credential_is_challenged_for_a_bearer_token()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }
}
