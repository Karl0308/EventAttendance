using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 phase B1c-1: the personnel half of <c>POST /attendance/tap</c> and <c>/tap/batch</c>, through
/// the real host, the real device key and real SQL Server.
///
/// <para>
/// <b>What this file pins.</b> A client that never sent <c>X-EAMS-Capabilities: personnel</c> keeps
/// today's contract even when the card is a staff card (#705's old-client tests 2 to 6), a capable client
/// gets D-95's refusals in order, and the two share one access decision. <c>CaptureContractTests</c> holds
/// the frozen shapes; this holds the behaviour.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelCaptureTests : IntegrationTest
{
    public PersonnelCaptureTests(SqlServerFixture sql) : base(sql) { }

    private const string StudentUid = "04A7B8C9";
    private const string PersonnelUid = "05D4E5F6";
    private const string TapRoute = "/api/v1/attendance/tap";
    private const string BatchRoute = "/api/v1/attendance/tap/batch";
    private const string Capable = CaptureCapabilities.PersonnelToken;

    private const string Number = "EMP-0001";
    private const string FirstName = "Jose";
    private const string LastName = "Rizal";

    private static readonly CaptureCapabilities PersonnelClient = new(Personnel: true);

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId, Guid PersonnelId, string ApiKey);

    /// <param name="mode">The event's attendance mode.</param>
    /// <param name="startedMinutesAgo">
    /// Pass 90 for a <c>TimeInOut</c> test that needs two taps at explicit past times.
    /// </param>
    /// <param name="personnelStatus">The holder's status.</param>
    /// <param name="inAudience">
    /// False attaches a definition that resolves other people, so the event has an audience and the
    /// holder is outside it.
    /// </param>
    private async Task<World> ArrangeAsync(
        string mode = AttendanceMode.Single, int startedMinutesAgo = 5,
        string personnelStatus = PersonnelStatus.Active, bool inAudience = true)
    {
        Guid schoolId, eventId, studentId, personnelId;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
            db.Schools.Add(school);

            var student = TestData.NewStudent(school.Id);
            db.Students.Add(student);
            db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StudentUid));

            var person = TestData.NewPersonnel(
                school.Id, Number, FirstName, "P", LastName, rfidUid: PersonnelUid, status: personnelStatus);
            db.Personnel.Add(person);

            var ev = TestData.NewLiveEvent(
                school.Id, EventStatus.Open, mode, startedMinutesAgo: startedMinutesAgo);
            db.Events.Add(ev);

            var classification = TestData.NewEventClassification(school.Id);
            db.EventClassifications.Add(classification);

            var definition = new AudienceDefinition
            {
                SchoolId = school.Id,
                EventClassificationId = classification.Id,
                Name = "The audience",
                NameKey = AudienceText.KeyFor("The audience"),
                AudienceType = inAudience ? AudienceType.UniversityWide : AudienceType.Department,
                CriteriaJson = inAudience
                    ? """{"scope":"Employees"}"""
                    : """{"departments":["NOBODY-HERE"],"scope":"Employees"}""",
            };
            db.AudienceDefinitions.Add(definition);
            db.EventAudienceDefinitions.Add(new EventAudienceDefinition
            {
                EventId = ev.Id,
                AudienceDefinitionId = definition.Id,
            });

            await db.SaveChangesAsync();
            (schoolId, eventId, studentId, personnelId) = (school.Id, ev.Id, student.Id, person.Id);
        }

        return new World(schoolId, eventId, studentId, personnelId, await IssueDeviceKeyAsync(schoolId));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string route, object body, params string[] capabilities)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body),
        };

        if (capabilities.Length > 0)
            request.Headers.TryAddWithoutValidation(CaptureCapabilities.HeaderName, capabilities);

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string CodeOf(JsonElement body) => body.GetProperty("code").GetString()!;

    private static object Tap(Guid eventId, string cardUid, string deviceTapId, DateTime? tappedAt = null) =>
        new { eventId, cardUid, deviceTapId, tappedAt };

    private static object Batch(params object[] taps) => new { clientClockAt = DateTime.UtcNow, taps };

    private async Task<List<AttendanceRecord>> RowsAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        return await db.AttendanceRecords.AsNoTracking().Where(a => a.EventId == eventId).ToListAsync();
    }

    private async Task<IReadOnlyList<EventScanDto>> ScansAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        return (await EventsOn(db).GetScanLogAsync(eventId))!.Scans;
    }

    private async Task<List<string>> RawScanRowsAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == eventId && a.Action == ScanLog.UnresolvedAction)
            .Select(a => a.Changes!)
            .ToListAsync();
    }

    private static void AssertFlagged(EventScanDto scan, Guid personnelId, string uid)
    {
        Assert.Equal(uid, scan.CardUid);
        Assert.Equal(personnelId, scan.PersonnelId);
        Assert.Equal(ScanLog.PersonnelClientNotCapableReason, scan.Reason);
        Assert.Equal(nameof(TapOutcome.CardNotFound), scan.ServerOutcome);
    }

    /// <summary>Nothing about personnel, in any casing, as a key or as prose.</summary>
    private static void AssertNoTraceOfPersonnel(string text)
    {
        Assert.DoesNotContain("personnel", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("personType", text, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------- #705 old-client tests 2, 3, 4

    /// <summary>#705 old-client test 2.</summary>
    [Fact]
    public async Task OldClient_personnel_tap_is_CardNotFound_with_a_flagged_scan_and_no_attendance_row()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var personnel = await client.PostAsJsonAsync(TapRoute, Tap(world.EventId, PersonnelUid, "t-1"));
        var unknown = await client.PostAsJsonAsync(TapRoute, Tap(world.EventId, "NOSUCHCARD", "t-2"));

        Assert.Equal(HttpStatusCode.NotFound, personnel.StatusCode);
        var body = await BodyOf(personnel);
        Assert.Equal(nameof(TapOutcome.CardNotFound), CodeOf(body));
        AssertNoTraceOfPersonnel(body.GetRawText());

        // Not an oracle: the same words as an unknown card, but for the UID the client itself sent.
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(unknownBody.GetProperty("title").GetString(), body.GetProperty("title").GetString());
        Assert.Equal(
            unknownBody.GetProperty("detail").GetString()!.Replace("NOSUCHCARD", PersonnelUid),
            body.GetProperty("detail").GetString());

        Assert.Empty(await RowsAsync(world.EventId));

        var scans = await ScansAsync(world.EventId);
        Assert.Equal(2, scans.Count);
        AssertFlagged(Assert.Single(scans, s => s.CardUid == PersonnelUid), world.PersonnelId, PersonnelUid);

        var plain = Assert.Single(scans, s => s.CardUid == "NOSUCHCARD");
        Assert.Null(plain.PersonnelId);
        Assert.Null(plain.Reason);
    }

    /// <summary>#705 old-client test 3, in both attendance modes.</summary>
    [Theory]
    [InlineData(AttendanceMode.Single)]
    [InlineData(AttendanceMode.TimeInOut)]
    public async Task Capable_personnel_tap_is_recorded_and_its_replay_is_DuplicateIgnored(string mode)
    {
        var world = await ArrangeAsync(mode);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var first = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);
        var replay = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(nameof(TapOutcome.Recorded), CodeOf(await BodyOf(first)));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await BodyOf(replay);
        Assert.Equal(nameof(TapOutcome.DuplicateIgnored), CodeOf(replayed));
        Assert.Equal(
            world.PersonnelId, replayed.GetProperty("record").GetProperty("personnelId").GetGuid());

        var row = Assert.Single(await RowsAsync(world.EventId));
        Assert.Equal(world.PersonnelId, row.PersonnelId);
        Assert.Null(row.StudentId);
        Assert.Null(row.RfidCardId);
        Assert.Equal("t-1", row.DeviceTapId);
        Assert.Empty(await ScansAsync(world.EventId));
    }

    /// <summary>#705 old-client test 4.</summary>
    [Fact]
    public async Task OldClient_mixed_batch_records_students_and_refuses_personnel_per_row()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(
            BatchRoute,
            Batch(Tap(world.EventId, PersonnelUid, "b-1"), Tap(world.EventId, StudentUid, "b-2")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("rejected").GetInt32());

        var results = body.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(nameof(TapOutcome.CardNotFound), CodeOf(results[0]));
        Assert.Equal(404, results[0].GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, results[0].GetProperty("record").ValueKind);
        Assert.Equal(nameof(TapOutcome.Recorded), CodeOf(results[1]));
        AssertNoTraceOfPersonnel(body.GetRawText());

        var row = Assert.Single(await RowsAsync(world.EventId));
        Assert.Equal(world.StudentId, row.StudentId);
        AssertFlagged(Assert.Single(await ScansAsync(world.EventId)), world.PersonnelId, PersonnelUid);
    }

    /// <summary>D-100: the declaration applies to every row of a flush, and v2 can express both kinds.</summary>
    [Fact]
    public async Task Capable_batch_answers_every_row_in_v2()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await SendAsync(
            client, BatchRoute,
            Batch(Tap(world.EventId, PersonnelUid, "b-1"), Tap(world.EventId, StudentUid, "b-2")),
            Capable);

        var results = (await BodyOf(response)).GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(
            AttendeePersonType.Personnel, results[0].GetProperty("record").GetProperty("personType").GetString());
        Assert.Equal(
            AttendeePersonType.Student, results[1].GetProperty("record").GetProperty("personType").GetString());
        Assert.Equal(2, (await RowsAsync(world.EventId)).Count);
    }

    // ----------------------------------------------------------------- the v2 record, guardrail 2

    /// <summary>D-99 and #705 guardrail 2: the exact member set, with null members present rather than omitted.</summary>
    [Fact]
    public async Task Capable_personnel_record_carries_exactly_the_v2_members_and_no_personal_extras()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);

        var record = (await BodyOf(response)).GetProperty("record");
        Assert.Equal(
            [
                "captureMethod", "checkInAt", "checkOutAt", "eventId", "id", "personType", "personnelId",
                "personnelName", "personnelNumber", "status", "studentId", "studentName", "studentNumber",
            ],
            record.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        Assert.Equal(AttendeePersonType.Personnel, record.GetProperty("personType").GetString());
        Assert.Equal(world.PersonnelId, record.GetProperty("personnelId").GetGuid());
        Assert.Equal(Number, record.GetProperty("personnelNumber").GetString());
        Assert.Equal($"{FirstName} P {LastName}", record.GetProperty("personnelName").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("studentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("studentName").ValueKind);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("studentNumber").ValueKind);
    }

    [Fact]
    public async Task Capable_student_tap_keeps_its_student_members_and_carries_null_personnel_members()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await SendAsync(client, TapRoute, Tap(world.EventId, StudentUid, "t-1"), Capable);

        var record = (await BodyOf(response)).GetProperty("record");
        Assert.Equal(AttendeePersonType.Student, record.GetProperty("personType").GetString());
        Assert.Equal(world.StudentId, record.GetProperty("studentId").GetGuid());
        Assert.Equal("Maria Reyes Santos", record.GetProperty("studentName").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("personnelId").ValueKind);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("personnelNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("personnelName").ValueKind);
    }

    /// <summary>One code path for both attendee kinds: a personnel member checks out like a student does.</summary>
    [Fact]
    public async Task Capable_second_personnel_tap_on_a_TimeInOut_event_checks_out_and_is_replayable()
    {
        var world = await ArrangeAsync(AttendanceMode.TimeInOut, startedMinutesAgo: 90);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);
        var checkIn = DateTime.UtcNow.AddMinutes(-60);
        var checkOut = checkIn.AddMinutes(20);

        await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "in", checkIn), Capable);
        var second = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "out", checkOut), Capable);
        var replay = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "out", checkOut), Capable);

        Assert.Equal(nameof(TapOutcome.CheckedOut), CodeOf(await BodyOf(second)));
        Assert.Equal(nameof(TapOutcome.DuplicateIgnored), CodeOf(await BodyOf(replay)));

        var row = Assert.Single(await RowsAsync(world.EventId));
        Assert.Equal("in", row.DeviceTapId);
        Assert.Equal("out", row.CheckOutDeviceTapId);
    }

    // ------------------------------------------------------------------ D-95's refusals, in order

    [Fact]
    public async Task Refusal_holder_outside_the_audience_is_PersonnelNotInAudience()
    {
        var world = await ArrangeAsync(inAudience: false);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(nameof(TapOutcome.PersonnelNotInAudience), CodeOf(await BodyOf(response)));
        Assert.Empty(await RowsAsync(world.EventId));

        var scan = Assert.Single(await ScansAsync(world.EventId));
        Assert.Equal(world.PersonnelId, scan.PersonnelId);
        Assert.Equal(nameof(TapOutcome.PersonnelNotInAudience), scan.ServerOutcome);
        Assert.Null(scan.Reason);
    }

    /// <summary>
    /// D-95's order: an Inactive holder who is also outside the audience is told <c>PersonnelInactive</c>.
    /// An Inactive holder can never be in the live set, so only this test can see the order at all.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refusal_order_inactive_is_reported_before_audience_membership(bool inAudience)
    {
        var world = await ArrangeAsync(personnelStatus: PersonnelStatus.Inactive, inAudience: inAudience);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(nameof(TapOutcome.PersonnelInactive), CodeOf(await BodyOf(response)));
        Assert.Empty(await RowsAsync(world.EventId));

        var scan = Assert.Single(await ScansAsync(world.EventId));
        Assert.Equal(world.PersonnelId, scan.PersonnelId);
        Assert.Equal(nameof(TapOutcome.PersonnelInactive), scan.ServerOutcome);
        Assert.Null(scan.Reason);
    }

    /// <summary>D-95: the tap calls the one resolver, so pre-registration narrows who may tap.</summary>
    [Fact]
    public async Task Refusal_preregistration_governs_who_the_tap_accepts()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var registrant = TestData.NewPersonnel(world.SchoolId, "EMP-0002", rfidUid: "0A0B0C0D");
            db.Personnel.Add(registrant);
            var session = new PreRegistrationSession
            {
                SchoolId = world.SchoolId,
                AudienceDefinitionId = await db.AudienceDefinitions.Select(d => d.Id).SingleAsync(),
                Name = "Staff session",
                Capacity = 10,
                EventId = world.EventId,
            };
            db.PreRegistrationSessions.Add(session);
            db.PreRegistrations.Add(new PreRegistration
            {
                SchoolId = world.SchoolId,
                Session = session,
                PersonnelId = registrant.Id,
                AttendeeType = PreRegistrantType.Personnel,
                Method = PreRegistrationMethod.Manual,
            });
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var outsider = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);
        var registered = await SendAsync(client, TapRoute, Tap(world.EventId, "0A0B0C0D", "t-2"), Capable);

        Assert.Equal(nameof(TapOutcome.PersonnelNotInAudience), CodeOf(await BodyOf(outsider)));
        Assert.Equal(nameof(TapOutcome.Recorded), CodeOf(await BodyOf(registered)));
    }

    /// <summary>D-99: a replay of a stored tap is a replay, whatever has happened to the holder since.</summary>
    [Fact]
    public async Task Replay_precedes_refusal_when_the_holder_has_since_gone_inactive()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);

        await using (var db = NewDbContext())
        {
            await db.Personnel.ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PersonnelStatus.Inactive));
        }

        var replay = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);
        var fresh = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-2"), Capable);

        Assert.Equal(nameof(TapOutcome.DuplicateIgnored), CodeOf(await BodyOf(replay)));
        Assert.Equal(nameof(TapOutcome.PersonnelInactive), CodeOf(await BodyOf(fresh)));
    }

    /// <inheritdoc cref="Replay_precedes_refusal_when_the_holder_has_since_gone_inactive"/>
    [Fact]
    public async Task Replay_precedes_refusal_when_the_holder_has_since_left_the_audience()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);

        await using (var db = NewDbContext())
        {
            await db.EventAudienceDefinitions.ExecuteDeleteAsync();
        }

        var replay = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);
        var fresh = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-2"), Capable);

        Assert.Equal(nameof(TapOutcome.DuplicateIgnored), CodeOf(await BodyOf(replay)));
        Assert.Equal(nameof(TapOutcome.PersonnelNotInAudience), CodeOf(await BodyOf(fresh)));
    }

    // ------------------------------------------------------------------ guardrails 4 and 8

    /// <summary>#705 guardrail 4, on the tap's problem body and on a batch row.</summary>
    [Theory]
    [InlineData(PersonnelStatus.Active, false, nameof(TapOutcome.PersonnelNotInAudience))]
    [InlineData(PersonnelStatus.Inactive, true, nameof(TapOutcome.PersonnelInactive))]
    public async Task Guardrail4_a_refusal_reveals_no_name_no_number_and_no_id(
        string status, bool inAudience, string code)
    {
        var world = await ArrangeAsync(personnelStatus: status, inAudience: inAudience);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var tap = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);
        var batch = await SendAsync(client, BatchRoute, Batch(Tap(world.EventId, PersonnelUid, "b-1")), Capable);

        var problem = await BodyOf(tap);
        Assert.Equal("application/problem+json", tap.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, CodeOf(problem));
        Assert.Equal(
            ["code", "detail", "instance", "serverTime", "status", "title", "traceId"],
            problem.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var row = Assert.Single((await BodyOf(batch)).GetProperty("results").EnumerateArray());
        Assert.Equal(code, CodeOf(row));
        Assert.Equal(404, row.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("record").ValueKind);

        foreach (var text in new[] { problem.GetRawText(), row.GetRawText() })
        {
            Assert.DoesNotContain(FirstName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LastName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Number, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(world.PersonnelId.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(world.PersonnelId.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>#705 guardrail 8, read from the stored row rather than from the DTO that projects it.</summary>
    [Fact]
    public async Task Guardrail8_the_scan_log_carries_ids_and_the_normalized_uid_never_names()
    {
        var world = await ArrangeAsync(inAudience: false);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        await SendAsync(client, TapRoute, Tap(world.EventId, "05:d4:e5:f6", "t-1"), Capable); // refused
        await client.PostAsJsonAsync(TapRoute, Tap(world.EventId, PersonnelUid, "t-2"));      // no header

        var rows = await RawScanRowsAsync(world.EventId);
        Assert.Equal(2, rows.Count);

        foreach (var row in rows)
        {
            using var json = JsonDocument.Parse(row);
            Assert.Equal(PersonnelUid, json.RootElement.GetProperty("cardUid").GetString());
            Assert.Equal(world.PersonnelId, json.RootElement.GetProperty("personnelId").GetGuid());

            Assert.DoesNotContain(FirstName, row, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LastName, row, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Number, row, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@usa.edu.ph", row, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(rows, r => r.Contains($"\"reason\":\"{ScanLog.PersonnelClientNotCapableReason}\"", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("\"reason\":null", StringComparison.Ordinal));
    }

    // ---------------------------------------------------- guardrail 5 / #705 old-client test 5

    /// <summary>
    /// #705 guardrail 5 and old-client test 5: school A's device cannot reach school B's personnel on a
    /// tap, with or without the header, and the two answers are the same answer.
    /// </summary>
    [Fact]
    public async Task Guardrail5_school_a_device_cannot_reach_school_b_personnel_with_or_without_the_header()
    {
        const string ForeignUid = "0B0B0B0B";
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("SCHOOL-B");
            db.Schools.Add(other);
            db.Personnel.Add(TestData.NewPersonnel(other.Id, "EMP-B", rfidUid: ForeignUid));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var withHeader = await SendAsync(client, TapRoute, Tap(world.EventId, ForeignUid, "t-1"), Capable);
        var without = await client.PostAsJsonAsync(TapRoute, Tap(world.EventId, ForeignUid, "t-2"));

        Assert.Equal(HttpStatusCode.NotFound, withHeader.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, without.StatusCode);

        var (a, b) = (await BodyOf(withHeader), await BodyOf(without));
        Assert.Equal(nameof(TapOutcome.CardNotFound), CodeOf(a));
        Assert.Equal(nameof(TapOutcome.CardNotFound), CodeOf(b));
        Assert.Equal(a.GetProperty("detail").GetString(), b.GetProperty("detail").GetString());

        Assert.Empty(await RowsAsync(world.EventId));
        Assert.All(await ScansAsync(world.EventId), scan =>
        {
            Assert.Null(scan.PersonnelId);
            Assert.Null(scan.Reason);
        });
    }

    /// <summary>
    /// D-99 tenancy: both card reads state the school themselves, because the §11 filter is inert with no
    /// tenant pinned, and that is how this service is reached by a direct call. The student half is the
    /// predicate this phase added to the tap's existing read.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tenancy_a_card_in_another_school_is_not_found_even_with_no_tenant_pinned(bool studentCard)
    {
        const string ForeignUid = "0B0B0B0B";
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("SCHOOL-B");
            db.Schools.Add(other);

            if (studentCard)
            {
                var student = TestData.NewStudent(other.Id, "2023-0099");
                db.Students.Add(student);
                db.RfidCards.Add(TestData.NewCard(other.Id, student.Id, ForeignUid));
            }
            else
            {
                db.Personnel.Add(TestData.NewPersonnel(other.Id, "EMP-B", rfidUid: ForeignUid));
            }

            await db.SaveChangesAsync();
        }

        await using var tap = NewDbContext();
        var response = await AttendanceOn(tap).TapAsync(
            new TapRequest(world.EventId, ForeignUid, null, "t-1", null), PersonnelClient);

        Assert.Equal(TapOutcome.CardNotFound, response.Outcome);
        Assert.Empty(await RowsAsync(world.EventId));
    }

    // --------------------------------------------------------------- #705 old-client test 5, access

    /// <summary>#705 old-client test 5: scheme and policy never look at the header.</summary>
    [Fact]
    public async Task HeaderChangesNoAccess_no_key_and_a_revoked_key_answer_the_same_either_way()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var anonymous = factory.CreateClient();
        using var keyed = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var tap = Tap(world.EventId, PersonnelUid, "t-1");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(TapRoute, tap)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, TapRoute, tap, Capable)).StatusCode);

        var pinned = new TestSchoolContext { CurrentSchoolId = world.SchoolId };
        await using (var db = NewDbContext(pinned))
        {
            var deviceId = await db.Devices.Select(d => d.Id).SingleAsync();
            await new DeviceService(db, pinned).RevokeKeyAsync(deviceId);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await keyed.PostAsJsonAsync(TapRoute, tap)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(keyed, TapRoute, tap, Capable)).StatusCode);
        Assert.Empty(await RowsAsync(world.EventId));
    }

    /// <summary>
    /// #705 old-client test 5: one device, one budget. If the header split the partition, alternating
    /// would buy double the permits and the request after the limit would still be a 200.
    /// </summary>
    [Fact]
    public async Task HeaderChangesNoAccess_alternating_the_header_shares_one_rate_limit_partition()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var empty = Batch();

        for (var i = 0; i < CaptureRateLimiting.PermitsPerWindow; i++)
        {
            using var allowed = i % 2 == 0
                ? await SendAsync(client, BatchRoute, empty, Capable)
                : await SendAsync(client, BatchRoute, empty);

            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var overBudget = await SendAsync(client, BatchRoute, empty, Capable);
        Assert.Equal(HttpStatusCode.TooManyRequests, overBudget.StatusCode);
    }

    /// <summary>
    /// #705 old-client test 5, the audience half. Both answers are the same status, neither writes a row,
    /// and only the capable one names the refusal.
    /// </summary>
    [Theory]
    [InlineData(PersonnelStatus.Active, false, nameof(TapOutcome.PersonnelNotInAudience))]
    [InlineData(PersonnelStatus.Inactive, true, nameof(TapOutcome.PersonnelInactive))]
    public async Task HeaderChangesNoAccess_a_refused_holder_is_refused_either_way(
        string status, bool inAudience, string capableCode)
    {
        var world = await ArrangeAsync(personnelStatus: status, inAudience: inAudience);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var capable = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), Capable);
        var plain = await client.PostAsJsonAsync(TapRoute, Tap(world.EventId, PersonnelUid, "t-2"));

        Assert.Equal(capable.StatusCode, plain.StatusCode);
        Assert.Equal(capableCode, CodeOf(await BodyOf(capable)));
        Assert.Equal(nameof(TapOutcome.CardNotFound), CodeOf(await BodyOf(plain)));
        Assert.Empty(await RowsAsync(world.EventId));
    }

    // ----------------------------------------------------------------- #705 old-client test 6

    /// <summary>#705 old-client test 6. Never a 400, and never a v2 shape.</summary>
    [Theory]
    [InlineData("personel")]
    [InlineData("PERSONNEL")]
    [InlineData("PERSONNEL ")]
    [InlineData("Personnel")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    [InlineData("future")]
    public async Task OldClient_an_unknown_or_misspelled_capability_behaves_as_no_header(string value)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var personnel = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), value);
        var student = await SendAsync(client, TapRoute, Tap(world.EventId, StudentUid, "t-2"), value);

        Assert.Equal(HttpStatusCode.NotFound, personnel.StatusCode);
        Assert.Equal(nameof(TapOutcome.CardNotFound), CodeOf(await BodyOf(personnel)));

        Assert.Equal(HttpStatusCode.OK, student.StatusCode);
        Assert.False((await BodyOf(student)).GetProperty("record").TryGetProperty("personType", out _));

        AssertFlagged(Assert.Single(await ScansAsync(world.EventId)), world.PersonnelId, PersonnelUid);
    }

    /// <summary>The list syntax end to end: the value ASP.NET hands the boundary, and the repeated-line form.</summary>
    [Theory]
    [InlineData("personnel ")]
    [InlineData("future, personnel")]
    public async Task Capable_list_syntax_is_honoured_through_the_host(string value)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), value);

        Assert.Equal(nameof(TapOutcome.Recorded), CodeOf(await BodyOf(response)));
    }

    [Fact]
    public async Task Capable_repeated_field_lines_merge()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await SendAsync(
            client, TapRoute, Tap(world.EventId, PersonnelUid, "t-1"), "future", Capable);

        Assert.Equal(nameof(TapOutcome.Recorded), CodeOf(await BodyOf(response)));
    }

    // --------------------------------------------------------------- D-99's replay-leak test

    /// <summary>
    /// D-99's named test, on the single tap. A replay matches on the device key before identity, so a
    /// no-header request can land on a row it knows nothing about. The answer is <c>CardNotFound</c>, with
    /// no personnel member anywhere, and the flagged scan row is what proves the service's rule fired
    /// rather than only the mapper behind it.
    /// </summary>
    [Fact]
    public async Task ReplayLeak_on_tap_a_student_card_under_a_personnel_tap_id_is_CardNotFound()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "shared"), Capable);

        var response = await client.PostAsJsonAsync(TapRoute, Tap(world.EventId, StudentUid, "shared"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(nameof(TapOutcome.CardNotFound), CodeOf(body));
        AssertNoTraceOfPersonnel(body.GetRawText());

        var row = Assert.Single(await RowsAsync(world.EventId));
        Assert.Equal(world.PersonnelId, row.PersonnelId);
        AssertFlagged(Assert.Single(await ScansAsync(world.EventId)), world.PersonnelId, StudentUid);
    }

    /// <inheritdoc cref="ReplayLeak_on_tap_a_student_card_under_a_personnel_tap_id_is_CardNotFound"/>
    [Fact]
    public async Task ReplayLeak_on_batch_a_student_card_under_a_personnel_tap_id_is_CardNotFound()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "shared"), Capable);

        var response = await client.PostAsJsonAsync(BatchRoute, Batch(Tap(world.EventId, StudentUid, "shared")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        var result = Assert.Single(body.GetProperty("results").EnumerateArray());
        Assert.Equal(nameof(TapOutcome.CardNotFound), CodeOf(result));
        Assert.Equal(404, result.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("record").ValueKind);
        AssertNoTraceOfPersonnel(body.GetRawText());

        Assert.Single(await RowsAsync(world.EventId));
        AssertFlagged(Assert.Single(await ScansAsync(world.EventId)), world.PersonnelId, StudentUid);
    }

    /// <summary>
    /// The TimeInOut form: the student has a check-in, so their next tap is a later tap, and the tap id is
    /// a personnel row's <em>check-out</em> id, the other arm of the replay check.
    /// </summary>
    [Fact]
    public async Task ReplayLeak_on_the_TimeInOut_later_tap_a_personnel_check_out_id_is_CardNotFound()
    {
        var world = await ArrangeAsync(AttendanceMode.TimeInOut, startedMinutesAgo: 90);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);
        var start = DateTime.UtcNow.AddMinutes(-60);

        await client.PostAsJsonAsync(TapRoute, Tap(world.EventId, StudentUid, "s-in", start));
        await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "p-in", start), Capable);
        await SendAsync(client, TapRoute, Tap(world.EventId, PersonnelUid, "shared", start.AddMinutes(20)), Capable);

        var response = await client.PostAsJsonAsync(
            TapRoute, Tap(world.EventId, StudentUid, "shared", start.AddMinutes(30)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(nameof(TapOutcome.CardNotFound), CodeOf(body));
        AssertNoTraceOfPersonnel(body.GetRawText());

        var rows = await RowsAsync(world.EventId);
        Assert.Null(Assert.Single(rows, r => r.StudentId == world.StudentId).CheckOutAt);
        AssertFlagged(Assert.Single(await ScansAsync(world.EventId)), world.PersonnelId, StudentUid);
    }

    /// <summary>
    /// The deepest of the stored-row paths: the replay check missed, the personnel row committed a moment
    /// later, and the later-tap path found it on its re-check. This is why the rule sits over the result
    /// and not at each replay site.
    /// </summary>
    [Fact]
    public async Task ReplayLeak_on_the_later_taps_own_replay_recheck_is_CardNotFound()
    {
        Guid schoolId, eventId, personnelId;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            var student = TestData.NewStudent(school.Id);
            db.Students.Add(student);
            db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StudentUid));
            var person = TestData.NewPersonnel(school.Id, Number, rfidUid: PersonnelUid);
            db.Personnel.Add(person);
            var ev = TestData.NewEvent(school.Id, EventStatus.Open, AttendanceMode.TimeInOut);
            db.Events.Add(ev);
            var classification = TestData.NewEventClassification(school.Id);
            db.EventClassifications.Add(classification);
            var definition = new AudienceDefinition
            {
                SchoolId = school.Id,
                EventClassificationId = classification.Id,
                Name = "Everyone",
                NameKey = AudienceText.KeyFor("Everyone"),
                AudienceType = AudienceType.UniversityWide,
                CriteriaJson = """{"scope":"Employees"}""",
            };
            db.AudienceDefinitions.Add(definition);
            db.EventAudienceDefinitions.Add(
                new EventAudienceDefinition { EventId = ev.Id, AudienceDefinitionId = definition.Id });
            await db.SaveChangesAsync();
            (schoolId, eventId, personnelId) = (school.Id, ev.Id, person.Id);
        }

        await using (var db = NewDbContext())
            await AttendanceOn(db).TapAsync(new TapRequest(eventId, StudentUid, null, "s-in", TestData.Now));

        var original = new CommitAfterReplayCheck(async () =>
        {
            await using var other = NewDbContext();
            var stored = await AttendanceOn(other).TapAsync(
                new TapRequest(eventId, PersonnelUid, null, "shared", TestData.Now.AddMinutes(5)), PersonnelClient);
            Assert.Equal(TapOutcome.Recorded, stored.Outcome);
        });

        await using var retrying = new EamsDbContext(
            new DbContextOptionsBuilder<EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString).AddInterceptors(original).Options,
            School);

        var response = await AttendanceOn(retrying).TapAsync(
            new TapRequest(eventId, StudentUid, null, "shared", TestData.Now.AddSeconds(1)), CaptureCapabilities.None);

        Assert.True(original.Fired, "The personnel row never committed mid-tap, so this proved nothing.");
        Assert.Equal(TapOutcome.CardNotFound, response.Outcome);
        Assert.Null(response.Result.Record);
        AssertFlagged(Assert.Single(await ScansAsync(eventId)), personnelId, StudentUid);

        // The suppressed-tap audit row belongs to a student's own double tap, which this was not.
        await using var read = NewDbContext();
        Assert.False(await read.AuditLogs.AnyAsync(
            a => a.SchoolId == schoolId && a.Action == ScanLog.SuppressedAction));
    }

    /// <summary>
    /// Runs <paramref name="commit"/> once, just before the first command after the replay check's
    /// <c>UNION</c>: the same seam <c>TapIntervalFlowTests</c> uses.
    /// </summary>
    private sealed class CommitAfterReplayCheck(Func<Task> commit) : DbCommandInterceptor
    {
        private bool _sawReplayCheck;

        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UNION", StringComparison.Ordinal))
            {
                _sawReplayCheck = true;
            }
            else if (_sawReplayCheck && !Fired)
            {
                Fired = true;
                await commit();
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
