using System.Net;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 phase B1c-2: <c>GET /events/{id}/manifest</c> under <c>X-EAMS-Capabilities</c> (D-99, D-100),
/// through the real host, the real device key and real SQL Server.
///
/// <para>
/// <b>What this file pins.</b> A client that did not opt in gets today's body, version and ETag, whatever
/// personnel the event has. A capable client gets exactly the countable set in exactly the four permitted
/// members. <c>Vary</c> covers every response an action produces. <c>EventManifestTests</c> holds the v1
/// behaviour; <c>CaptureContractTests</c> the frozen v1 shape.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventManifestPersonnelTests : IntegrationTest
{
    public EventManifestPersonnelTests(SqlServerFixture sql) : base(sql) { }

    private const string Capable = CaptureCapabilities.PersonnelToken;

    /// <summary>#705 guardrail 2, transcribed: what a device may never hold.</summary>
    private static readonly string[] ForbiddenPersonnelKeys =
        ["email", "position", "classification", "department", "organization", "status"];

    private static readonly string[] PermittedPersonnelKeys =
        ["cardUids", "fullName", "personnelId", "personnelNumber"];

    private static readonly string[] V1ManifestKeys =
        ["attendees", "event", "groups", "serverTime", "version"];

    private async Task<PersonnelEventWorld> ArrangeAsync(
        string eventStatus = EventStatus.Open, string personnelStatus = PersonnelStatus.Active, bool inAudience = true)
    {
        await using var db = NewDbContext();
        return await PersonnelEventWorld.ArrangeAsync(db, eventStatus, personnelStatus, inAudience);
    }

    private static async Task<HttpResponseMessage> PullAsync(
        HttpClient client, Guid eventId, string? capability = null, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/events/{eventId}/manifest");

        if (capability is not null)
            request.Headers.TryAddWithoutValidation(CaptureCapabilities.HeaderName, capability);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string[] KeysOf(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    /// <summary>The body with its one field that moves on every pull taken out.</summary>
    private static string WithoutServerTime(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(p => p.Name != "serverTime")
            .ToDictionary(p => p.Name, p => p.Value));

    // --------------------------------------------------------------------------- no header: v1, untouched

    /// <summary>
    /// D-100: without the header the manifest is v1, and its <c>version</c> and ETag are computed exactly
    /// as today. A personnel member joining the audience must not move a single byte: this is the old
    /// client's whole guarantee. <c>EventManifestVersionTests</c> pins the hash itself to its golden value.
    /// </summary>
    [Fact]
    public async Task No_header_manifest_is_v1_and_personnel_joining_the_audience_moves_neither_bytes_nor_etag()
    {
        var world = await ArrangeAsync(inAudience: false);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var before = await PullAsync(client, world.EventId);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var beforeBody = await BodyOf(before);

        Assert.Equal(V1ManifestKeys, KeysOf(beforeBody));
        Assert.StartsWith(EventManifestVersion.ContractShapePrefix, beforeBody.GetProperty("version").GetString());

        await using (var db = NewDbContext())
        {
            var definition = await db.AudienceDefinitions.SingleAsync(d => d.Id == world.DefinitionId);
            definition.AudienceType = AudienceType.UniversityWide;
            definition.CriteriaJson = """{"scope":"Employees"}""";
            await db.SaveChangesAsync();
        }

        // The person is in the audience now: a capable client would see them.
        var capable = await BodyOf(await PullAsync(client, world.EventId, Capable));
        Assert.Single(capable.GetProperty("personnel").EnumerateArray());

        var after = await PullAsync(client, world.EventId);
        var afterBody = await BodyOf(after);

        Assert.Equal(before.Headers.ETag, after.Headers.ETag);
        Assert.Equal(WithoutServerTime(beforeBody), WithoutServerTime(afterBody));
        Assert.DoesNotContain("personnel", afterBody.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#705 old-client test 6, on this route: anything but the exact token is no header at all.</summary>
    [Theory]
    [InlineData("PERSONNEL")]
    [InlineData("Personnel")]
    [InlineData("personel")]
    [InlineData("")]
    [InlineData("gps, offline")]
    public async Task A_value_that_is_not_the_exact_token_reads_as_no_header(string capability)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var response = await PullAsync(client, world.EventId, capability);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(V1ManifestKeys, KeysOf(await BodyOf(response)));
    }

    // ------------------------------------------------------------------------------------ v2 contents

    /// <summary>
    /// D-99: every v1 member plus <c>personnel</c>, whose item is exactly the permitted four. The students
    /// are the same students as v1 serves. This is also #705 guardrail 2.
    /// </summary>
    [Fact]
    public async Task Capable_manifest_carries_the_v1_members_plus_personnel_in_exactly_the_four_permitted_keys()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var v1 = await BodyOf(await PullAsync(client, world.EventId));
        var v2Text = await (await PullAsync(client, world.EventId, Capable)).Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(v2Text);
        var v2 = document.RootElement;

        Assert.Equal(
            [.. V1ManifestKeys.Append("personnel").Order(StringComparer.Ordinal)], KeysOf(v2));

        foreach (var member in new[] { "event", "groups", "attendees" })
            Assert.Equal(v1.GetProperty(member).GetRawText(), v2.GetProperty(member).GetRawText());

        var item = Assert.Single(v2.GetProperty("personnel").EnumerateArray());
        Assert.Equal(PermittedPersonnelKeys, KeysOf(item));
        Assert.Equal(world.PersonnelId, item.GetProperty("personnelId").GetGuid());
        Assert.Equal(PersonnelEventWorld.PersonnelNumber, item.GetProperty("personnelNumber").GetString());
        Assert.Equal(PersonnelEventWorld.PersonnelName, item.GetProperty("fullName").GetString());
        Assert.Equal(
            PersonnelEventWorld.PersonnelUid, Assert.Single(item.GetProperty("cardUids").EnumerateArray()).GetString());

        foreach (var key in ForbiddenPersonnelKeys)
            Assert.False(item.TryGetProperty(key, out _), $"A device manifest must not carry '{key}'.");

        // The value, not only the key: a differently named member would pass the loop above.
        Assert.DoesNotContain(PersonnelEventWorld.PersonnelEmail, v2Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CICT", v2Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Personnel_is_always_present_and_is_an_empty_array_when_the_event_expects_none()
    {
        var world = await ArrangeAsync(inAudience: false);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var body = await BodyOf(await PullAsync(client, world.EventId, Capable));

        Assert.Equal(JsonValueKind.Array, body.GetProperty("personnel").ValueKind);
        Assert.Empty(body.GetProperty("personnel").EnumerateArray());
    }

    [Fact]
    public async Task A_personnel_member_with_no_card_is_listed_with_empty_card_uids()
    {
        var world = await ArrangeAsync();
        await using (var db = NewDbContext())
        {
            var person = await db.Personnel.SingleAsync(p => p.Id == world.PersonnelId);
            person.RfidUid = null;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var body = await BodyOf(await PullAsync(client, world.EventId, Capable));

        var item = Assert.Single(body.GetProperty("personnel").EnumerateArray());
        Assert.Empty(item.GetProperty("cardUids").EnumerateArray());
    }

    /// <summary>D-99: <c>Active</c>, not-deleted, in-audience only. Each case is one person short of the set.</summary>
    [Theory]
    [InlineData(PersonnelStatus.Inactive, true, false)]
    [InlineData(PersonnelStatus.Active, false, false)]
    [InlineData(PersonnelStatus.Active, true, true)]
    public async Task Only_countable_personnel_reach_the_manifest(string status, bool inAudience, bool softDeleted)
    {
        var world = await ArrangeAsync(personnelStatus: status, inAudience: inAudience);
        if (softDeleted)
        {
            await using var db = NewDbContext();
            var person = await db.Personnel.SingleAsync(p => p.Id == world.PersonnelId);
            person.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var body = await BodyOf(await PullAsync(client, world.EventId, Capable));

        Assert.Empty(body.GetProperty("personnel").EnumerateArray());
    }

    /// <summary>
    /// #705 guardrail 5: school B's people never reach school A's manifest, though A's audience is
    /// university-wide and B has a countable member. A's own member is asserted too, so this cannot pass on
    /// an empty list. Through the host the device pins the tenant; the direct call has none pinned, so there
    /// the explicit <c>SchoolId</c> terms are the only guard (D-99 tenancy).
    /// </summary>
    [Fact]
    public async Task A_school_b_personnel_never_reaches_a_school_a_manifest()
    {
        var a = await ArrangeAsync();
        var b = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(a.SchoolId));

        var body = await BodyOf(await PullAsync(client, a.EventId, Capable));
        var item = Assert.Single(body.GetProperty("personnel").EnumerateArray());
        Assert.Equal(a.PersonnelId, item.GetProperty("personnelId").GetGuid());

        await using (var unpinned = NewDbContext())
        {
            var direct = await EventsOn(unpinned).GetManifestAsync(a.EventId, new CaptureCapabilities(Personnel: true));
            Assert.Equal(a.PersonnelId, Assert.Single(direct.ManifestV2!.Personnel).PersonnelId);
        }

        // And A's key cannot ask about B's event at all: indistinguishable from no event (D-27).
        Assert.Equal(HttpStatusCode.NotFound, (await PullAsync(client, b.EventId, Capable)).StatusCode);
    }

    // ------------------------------------------------------------------------------- version and ETag

    /// <summary>
    /// D-99: a <c>version</c> over the v2 body, never equal to a v1 version, so a validator carried across
    /// a capability change can only answer 200.
    /// </summary>
    [Fact]
    public async Task Capable_version_is_its_own_and_a_v1_validator_never_answers_304_for_it()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var v1Response = await PullAsync(client, world.EventId);
        var v2Response = await PullAsync(client, world.EventId, Capable);
        var v1Version = (await BodyOf(v1Response)).GetProperty("version").GetString()!;
        var v2Version = (await BodyOf(v2Response)).GetProperty("version").GetString()!;

        Assert.NotEqual(v1Version, v2Version);
        Assert.StartsWith(EventManifestVersion.ContractShapePrefixV2, v2Version);
        Assert.Equal(EventManifestVersion.ETagFor(v2Version), v2Response.Headers.ETag!.ToString());

        var crossV1 = await PullAsync(client, world.EventId, Capable, EventManifestVersion.ETagFor(v1Version));
        Assert.Equal(HttpStatusCode.OK, crossV1.StatusCode);

        var crossV2 = await PullAsync(client, world.EventId, ifNoneMatch: EventManifestVersion.ETagFor(v2Version));
        Assert.Equal(HttpStatusCode.OK, crossV2.StatusCode);

        var unchanged = await PullAsync(client, world.EventId, Capable, EventManifestVersion.ETagFor(v2Version));
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
    }

    [Fact]
    public async Task A_change_to_a_listed_personnel_member_moves_the_capable_version_and_not_the_v1_one()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var v1Before = (await PullAsync(client, world.EventId)).Headers.ETag;
        var v2Before = (await PullAsync(client, world.EventId, Capable)).Headers.ETag;

        await using (var db = NewDbContext())
        {
            var person = await db.Personnel.SingleAsync(p => p.Id == world.PersonnelId);
            person.RfidUid = "0A0B0C0D";
            await db.SaveChangesAsync();
        }

        Assert.Equal(v1Before, (await PullAsync(client, world.EventId)).Headers.ETag);
        Assert.NotEqual(v2Before, (await PullAsync(client, world.EventId, Capable)).Headers.ETag);
    }

    // ------------------------------------------------------------------------------------------- Vary

    private static void AssertVariesOnBoth(HttpResponseMessage response)
    {
        Assert.Contains("Authorization", response.Headers.Vary, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(CaptureCapabilities.HeaderName, response.Headers.Vary, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>D-100: on the 200, the 304 and the refusals, whichever family was asked for.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(Capable)]
    public async Task Vary_names_authorization_and_the_capability_header_on_200_304_and_refusals(string? capability)
    {
        var world = await ArrangeAsync();
        var draft = await ArrangeDraftEventAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var ok = await PullAsync(client, world.EventId, capability);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        AssertVariesOnBoth(ok);

        var notModified = await PullAsync(client, world.EventId, capability, ok.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        AssertVariesOnBoth(notModified);

        var notFound = await PullAsync(client, Guid.NewGuid(), capability);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        AssertVariesOnBoth(notFound);

        var notOpen = await PullAsync(client, draft, capability);
        Assert.Equal(HttpStatusCode.Conflict, notOpen.StatusCode);
        AssertVariesOnBoth(notOpen);
    }

    private async Task<Guid> ArrangeDraftEventAsync(Guid schoolId)
    {
        await using var db = NewDbContext();
        var draft = TestData.NewLiveEvent(schoolId, EventStatus.Draft);
        db.Events.Add(draft);
        await db.SaveChangesAsync();
        return draft.Id;
    }

    // ---------------------------------------------------------------------------------- MaxAttendees

    /// <summary>
    /// D-99/D-100: <c>MaxAttendees</c> counts students plus personnel, and this is the one place the
    /// header may change access. An event of exactly the ceiling fits for a client that asked for students
    /// and does not fit once one personnel member is added to what is sent. Real volume, because the count
    /// is taken over the composed list by design and a smaller limit does not exist to test against.
    /// </summary>
    [Fact]
    public async Task MaxAttendees_counts_students_plus_personnel_so_413_may_differ_with_the_header()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var definition = await db.AudienceDefinitions.SingleAsync(d => d.Id == world.DefinitionId);
            definition.CriteriaJson = """{"scope":"Both"}""";
            await db.SaveChangesAsync();

            // The arranged student is one of the ceiling's worth.
            await InsertStudentsAsync(db, world, EventManifestLimits.MaxAttendees - 1);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var fits = await PullAsync(client, world.EventId);
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        Assert.Equal(EventManifestLimits.MaxAttendees, (await BodyOf(fits)).GetProperty("attendees").GetArrayLength());

        var refused = await PullAsync(client, world.EventId, Capable);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);

        // D-100: a refusal must not be cacheable either, and the 413 is the one that differs by header.
        AssertVariesOnBoth(refused);
        Assert.True(refused.Headers.CacheControl is { Private: true, NoCache: true });

        var body = await BodyOf(refused);
        Assert.Equal(nameof(ManifestOutcome.ManifestTooLarge), body.GetProperty("code").GetString());
        Assert.Equal(EventManifestLimits.MaxAttendees + 1, body.GetProperty("attendeeCount").GetInt32());
        Assert.Equal(EventManifestLimits.MaxAttendees, body.GetProperty("maxAttendees").GetInt32());
    }

    /// <summary>
    /// Copies the arranged student set-based. <c>count</c> rows through the change tracker would take this
    /// one test past everything else in the file; the column list comes from the model so a new column does
    /// not break it.
    /// </summary>
    private static async Task InsertStudentsAsync(
        EAMS.Infrastructure.Data.EamsDbContext db, PersonnelEventWorld world, int count)
    {
        var entity = db.Model.FindEntityType(typeof(Student))!;
        var replaced = new[] { nameof(Student.Id), nameof(Student.StudentNumber), nameof(Student.Email) };

        var copied = entity.GetProperties()
            .Where(p => !replaced.Contains(p.Name, StringComparer.Ordinal) && !p.IsConcurrencyToken)
            .Select(p => $"[{p.Name}]")
            .ToList();

        var columns = string.Join(", ", copied);

        var sql =
            $"""
            WITH n AS (
                SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO Students ([Id], [StudentNumber], [Email], {columns})
            SELECT NEWID(), CONCAT('BULK-', n.i), CONCAT('bulk', n.i, '@test.local'), {columns}
            FROM Students CROSS JOIN n
            WHERE Students.[Id] = '{world.StudentId}';
            """;

        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
        await db.Database.ExecuteSqlRawAsync(sql);
    }
}
