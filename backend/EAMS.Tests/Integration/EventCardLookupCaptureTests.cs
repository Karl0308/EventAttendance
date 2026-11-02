using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 phase B1c-2: <c>GET /events/{id}/by-card/{cardUid}</c> (D-99), the scan screen's event-scoped
/// lookup, through the real host, the real device key and real SQL Server.
///
/// <para>
/// <b>The rule worth a file: it is a read, and a read is not a state oracle</b> (JJ, 2026-10-27, decision
/// 7). A personnel card a tap would refuse answers exactly what a card nobody holds answers, and writes
/// nothing. Only a tap may say why, and only a tap writes a scan-log row.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventCardLookupCaptureTests : IntegrationTest
{
    public EventCardLookupCaptureTests(SqlServerFixture sql) : base(sql) { }

    private static readonly string[] AllMembers =
        ["cardUids", "fullName", "personType", "personnelId", "personnelNumber", "studentId", "studentNumber"];

    private async Task<PersonnelEventWorld> ArrangeAsync(
        string eventStatus = EventStatus.Open, string personnelStatus = PersonnelStatus.Active, bool inAudience = true)
    {
        await using var db = NewDbContext();
        return await PersonnelEventWorld.ArrangeAsync(db, eventStatus, personnelStatus, inAudience);
    }

    private static Task<HttpResponseMessage> LookupAsync(HttpClient client, Guid eventId, string cardUid) =>
        client.GetAsync($"/api/v1/events/{eventId}/by-card/{Uri.EscapeDataString(cardUid)}");

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string[] KeysOf(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The whole body but the two members that say nothing about the card's holder: <c>traceId</c> differs
    /// on every request, and <c>instance</c> echoes the URL the client itself sent.
    /// </summary>
    private static string WithoutRequestEcho(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(p => p.Name is not ("traceId" or "instance"))
            .ToDictionary(p => p.Name, p => p.Value));

    private async Task<int> ScanRowsAsync(PersonnelEventWorld world)
    {
        await using var db = NewDbContext();
        return await world.ScanRowCountAsync(db);
    }

    // ------------------------------------------------------------------------------------------- 200s

    /// <summary>
    /// D-99: a student is returned regardless of audience, because a tap would record them as a walk-in
    /// (D-19). Every member present, <c>null</c> where it does not apply.
    /// </summary>
    [Fact]
    public async Task A_student_is_returned_whatever_the_audience_says()
    {
        var world = await ArrangeAsync(inAudience: false);
        await using (var db = NewDbContext())
        {
            // Not attached in any way: a walk-in.
            db.EventGroups.RemoveRange(db.EventGroups.Where(g => g.EventId == world.EventId));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var response = await LookupAsync(client, world.EventId, PersonnelEventWorld.StudentUid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(AllMembers, KeysOf(body));
        Assert.Equal(AttendeePersonType.Student, body.GetProperty("personType").GetString());
        Assert.Equal(world.StudentId, body.GetProperty("studentId").GetGuid());
        Assert.Equal("2023-0001", body.GetProperty("studentNumber").GetString());
        Assert.Equal("Maria Reyes Santos", body.GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("personnelId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("personnelNumber").ValueKind);
        Assert.Equal(
            PersonnelEventWorld.StudentUid, Assert.Single(body.GetProperty("cardUids").EnumerateArray()).GetString());
    }

    /// <summary>D-99: a countable personnel member, in the members a device may hold (#705 guardrail 2).</summary>
    [Fact]
    public async Task A_countable_personnel_member_is_returned_with_the_student_members_null()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        // Any reader format: normalize first, compare second.
        var response = await LookupAsync(client, world.EventId, "05:d4:e5:f6");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = await BodyOf(response);
        Assert.Equal(AllMembers, KeysOf(body));
        Assert.Equal(AttendeePersonType.Personnel, body.GetProperty("personType").GetString());
        Assert.Equal(world.PersonnelId, body.GetProperty("personnelId").GetGuid());
        Assert.Equal(PersonnelEventWorld.PersonnelNumber, body.GetProperty("personnelNumber").GetString());
        Assert.Equal(PersonnelEventWorld.PersonnelName, body.GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("studentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("studentNumber").ValueKind);
        Assert.Equal(
            PersonnelEventWorld.PersonnelUid, Assert.Single(body.GetProperty("cardUids").EnumerateArray()).GetString());

        Assert.DoesNotContain(PersonnelEventWorld.PersonnelEmail, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CICT", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------- not a state oracle

    /// <summary>
    /// D-99, decision 7. Inactive, out-of-audience and soft-deleted holders answer one body, the body of a
    /// card nobody holds, down to the byte but for the request echo, and none of them writes a scan-log row.
    /// </summary>
    [Fact]
    public async Task A_personnel_card_a_tap_would_refuse_answers_CardNotFound_identical_to_an_unknown_card_with_no_scan_row()
    {
        var inactive = await ArrangeAsync(personnelStatus: PersonnelStatus.Inactive);
        var outside = await ArrangeAsync(inAudience: false);
        var deleted = await ArrangeAsync();
        await using (var db = NewDbContext())
        {
            var person = await db.Personnel.SingleAsync(p => p.Id == deleted.PersonnelId);
            person.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        async Task<string> RefusedAsync(PersonnelEventWorld world, string uid)
        {
            using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));
            var response = await LookupAsync(client, world.EventId, uid);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await BodyOf(response);
            Assert.Equal(nameof(TapOutcome.CardNotFound), body.GetProperty("code").GetString());
            return WithoutRequestEcho(body);
        }

        var unknownCard = await RefusedAsync(inactive, "NOSUCHCARD");

        Assert.Equal(unknownCard, await RefusedAsync(inactive, PersonnelEventWorld.PersonnelUid));
        Assert.Equal(unknownCard, await RefusedAsync(outside, PersonnelEventWorld.PersonnelUid));
        Assert.Equal(unknownCard, await RefusedAsync(deleted, PersonnelEventWorld.PersonnelUid));

        Assert.Equal(0, await ScanRowsAsync(inactive));
        Assert.Equal(0, await ScanRowsAsync(outside));
        Assert.Equal(0, await ScanRowsAsync(deleted));
    }

    // ---------------------------------------------------------------------------- the event's refusals

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Closed)]
    [InlineData(EventStatus.Cancelled)]
    public async Task An_event_that_is_not_open_is_refused_EventNotOpen_even_for_a_countable_holder(string status)
    {
        var world = await ArrangeAsync(eventStatus: status);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        foreach (var uid in new[] { PersonnelEventWorld.StudentUid, PersonnelEventWorld.PersonnelUid })
        {
            var response = await LookupAsync(client, world.EventId, uid);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(nameof(TapOutcome.EventNotOpen), (await BodyOf(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task An_unknown_event_is_EventNotFound()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var response = await LookupAsync(client, Guid.NewGuid(), PersonnelEventWorld.PersonnelUid);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(nameof(TapOutcome.EventNotFound), (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_malformed_event_id_is_a_400_with_no_code_not_a_bare_404()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var response = await client.GetAsync($"/api/v1/events/banana/by-card/{PersonnelEventWorld.PersonnelUid}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await BodyOf(response)).TryGetProperty("code", out _));
    }

    // ------------------------------------------------------------------------------------- guardrails

    /// <summary>
    /// #705 guardrail 3: audience-scoped and Open-only. The school-wide route stays student-only, so
    /// there is no way to ask about a staff card outside one Open event.
    /// </summary>
    [Fact]
    public async Task The_school_wide_lookup_stays_student_only()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var student = await client.GetAsync($"/api/v1/students/by-card/{PersonnelEventWorld.StudentUid}");
        var personnel = await client.GetAsync($"/api/v1/students/by-card/{PersonnelEventWorld.PersonnelUid}");

        Assert.Equal(HttpStatusCode.OK, student.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, personnel.StatusCode);
        Assert.DoesNotContain(
            PersonnelEventWorld.PersonnelNumber, await personnel.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// #705 guardrail 5, both halves. School A's device cannot ask about school B's event, and school B's
    /// card on school A's event is nobody, though the same UID is a countable member over there.
    /// </summary>
    [Fact]
    public async Task School_b_stays_invisible_to_a_school_a_device()
    {
        var a = await ArrangeAsync();
        var b = await ArrangeAsync();
        await using (var db = NewDbContext())
        {
            var mine = await db.Personnel.SingleAsync(p => p.Id == a.PersonnelId);
            mine.RfidUid = "0A0B0C0D";
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(a.SchoolId));

        var theirEvent = await LookupAsync(client, b.EventId, PersonnelEventWorld.PersonnelUid);
        Assert.Equal(HttpStatusCode.NotFound, theirEvent.StatusCode);
        Assert.Equal(nameof(TapOutcome.EventNotFound), (await BodyOf(theirEvent)).GetProperty("code").GetString());

        var theirCard = await LookupAsync(client, a.EventId, PersonnelEventWorld.PersonnelUid);
        Assert.Equal(HttpStatusCode.NotFound, theirCard.StatusCode);
        Assert.Equal(nameof(TapOutcome.CardNotFound), (await BodyOf(theirCard)).GetProperty("code").GetString());
    }

    /// <summary>
    /// #705 guardrail 6. The device-capture budget, not the manifest's: this is called per scan.
    /// <c>CaptureRateLimitingTests</c> also fails if a gated route loses its limiter altogether.
    /// </summary>
    [Fact]
    public void The_lookup_sits_under_the_device_capture_rate_limit()
    {
        var limiter = typeof(EventManifestController)
            .GetMethod(nameof(EventManifestController.ByCard))!
            .GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(limiter);
        Assert.Equal(CaptureRateLimiting.PolicyName, limiter.PolicyName);
    }

    /// <summary>#705 guardrail 9, on the answer and on the refusals alike.</summary>
    [Fact]
    public async Task Every_response_is_private_no_cache()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(await IssueDeviceKeyAsync(world.SchoolId));

        var routes = new (Guid EventId, string Uid, HttpStatusCode Status)[]
        {
            (world.EventId, PersonnelEventWorld.PersonnelUid, HttpStatusCode.OK),
            (world.EventId, "NOSUCHCARD", HttpStatusCode.NotFound),
            (Guid.NewGuid(), PersonnelEventWorld.PersonnelUid, HttpStatusCode.NotFound),
        };

        foreach (var (eventId, uid, status) in routes)
        {
            var response = await LookupAsync(client, eventId, uid);

            Assert.Equal(status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.Private, "Not private.");
            Assert.True(response.Headers.CacheControl?.NoCache, "Not no-cache.");
        }
    }

    /// <summary>#705 guardrail 1: existing auth only. A person's token is not a device key.</summary>
    [Fact]
    public async Task The_lookup_takes_a_device_key_and_nothing_else()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using var anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await LookupAsync(anonymous, world.EventId, PersonnelEventWorld.PersonnelUid)).StatusCode);

        using var person = await SignedInClientAsync(factory, world.SchoolId);
        Assert.Equal(
            AuthenticationSchemes.Bearer, person.DefaultRequestHeaders.Authorization?.Scheme, StringComparer.Ordinal);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await LookupAsync(person, world.EventId, PersonnelEventWorld.PersonnelUid)).StatusCode);
    }

    private static class AuthenticationSchemes
    {
        public const string Bearer = "Bearer";
    }

    // ----------------------------------------------------------------------------------------- mapping

    [Theory]
    [InlineData(EventCardLookupOutcome.Ok, StatusCodes.Status200OK)]
    [InlineData(EventCardLookupOutcome.EventNotFound, StatusCodes.Status404NotFound)]
    [InlineData(EventCardLookupOutcome.EventNotOpen, StatusCodes.Status400BadRequest)]
    [InlineData(EventCardLookupOutcome.CardNotFound, StatusCodes.Status404NotFound)]
    public void Every_lookup_outcome_maps_to_the_status_a_tap_gives_the_same_token(
        EventCardLookupOutcome outcome, int expected) =>
        Assert.Equal(expected, EventManifestController.StatusCodeFor(outcome));

    [Fact]
    public void An_unmapped_lookup_outcome_throws_rather_than_reading_as_success() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EventManifestController.StatusCodeFor((EventCardLookupOutcome)999));
}
