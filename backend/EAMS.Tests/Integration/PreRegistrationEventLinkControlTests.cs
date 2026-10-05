using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The three negative-controlled CONTROL tests ADR-008 adds (Follow-Up Actions, named-tests item), kept
/// in their own file so <see cref="PreRegistrationEventLinkApiTests"/> stays the STANDARD set and these
/// stay the ones whose assertions have each been shown capable of going red (the green-but-useless trap
/// this repo has been bitten by — see <c>memory/feedback_negative_control_before_claiming_fixed.md</c>):
///
/// <list type="number">
/// <item><b>Liveness / REPLACE control.</b> A linked session's registrants ARE the live denominator
/// (not the section+definition union); a registrant added while the event is live MOVES Expected; unlink
/// RESTORES the union with no data loss. Negative control: neutralise the REPLACE branch in
/// <c>ExpectedStudentIdsAsync</c> and the linked assertions go red.</item>
/// <item><b>Freeze write/read SYMMETRY control (ADR-003 D-15 over the pre-reg source).</b> A student
/// registered then soft-deleted BEFORE close is still in the FROZEN denominator (the snapshot resolves
/// with <c>includeDeleted: true</c>), and a registration change AFTER close does not move the frozen
/// number (D-13). Negative control: flip the snapshot selector to <c>includeDeleted: false</c> and the
/// frozen-count assertion goes red.</item>
/// <item><b>Capture guardrail control (ADR-008 D-75 / D-20, plan §8.2).</b> On a LINKED event a
/// non-pre-registered tap is ACCEPTED and flagged <c>isExpected = false</c> — the REPLACE is a
/// denominator change, never a capture block — and the denominator does not move; a re-tap with the same
/// <c>(DeviceId, deviceTapId)</c> is idempotent.</item>
/// </list>
///
/// <para>
/// Real HTTP for the link / unlink / close contract (sign in with <see cref="IntegrationTest.SignedInClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{Program})"/>,
/// tenant from the token), and the <see cref="IntegrationTest"/> service seams (<c>AttendanceOn</c>,
/// <c>EventsOn</c>) for the capture path — the same seams <see cref="TapFlowTests"/> drives. SQL Server via
/// Testcontainers, never EF InMemory: the replace/freeze dedupe is a SQL <c>UNION</c>, the soft-delete
/// exclusion is a provider predicate, and the tap idempotency rides <c>UX_Attendance_Device_DeviceTapId</c>
/// over a nullable <c>DeviceId</c> — all provider behaviour an in-memory store does not reproduce.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PreRegistrationEventLinkControlTests : IntegrationTest
{
    public PreRegistrationEventLinkControlTests(SqlServerFixture sql) : base(sql) { }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record World(Guid SchoolId, Guid ClassificationId, Guid SessionDefinitionId, Guid EventId);

    // ----------------------------------------------------------------------------------- arrange
    // Minimal arrange over the shared TestData builders and IntegrationTest seams. Deliberately NOT a copy
    // of PreRegistrationEventLinkApiTests' private helpers (they belong to that class's standard set); these
    // build the specific worlds the controls need and nothing more.

    private async Task<World> ArrangeAsync(string status = EventStatus.Open)
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var classification = TestData.NewEventClassification(school.Id);
        db.EventClassifications.Add(classification);

        var ev = TestData.NewEvent(school.Id, status);
        ev.EventClassificationId = classification.Id;
        db.Events.Add(ev);

        var def = NewDefinition(school.Id, classification.Id, "Everyone",
            AudienceType.UniversityWide, new AudienceCriteriaDto(Scope: "Both"));
        db.AudienceDefinitions.Add(def);

        await db.SaveChangesAsync();
        return new World(school.Id, classification.Id, def.Id, ev.Id);
    }

    private static AudienceDefinition NewDefinition(
        Guid schoolId, Guid classificationId, string name, string type, AudienceCriteriaDto criteria) => new()
    {
        SchoolId = schoolId,
        EventClassificationId = classificationId,
        Name = name,
        NameKey = AudienceText.KeyFor(name),
        AudienceType = type,
        CriteriaJson = JsonSerializer.Serialize(criteria, Web),
        IsActive = true,
    };

    /// <summary>A Department(CICT) definition attached to the event — the UNION the replace must supersede.</summary>
    private async Task<Guid> AttachCictDepartmentAsync(World world)
    {
        await using var db = NewDbContext();
        var def = NewDefinition(world.SchoolId, world.ClassificationId, "CICT Department",
            AudienceType.Department, new AudienceCriteriaDto(Departments: ["CICT"]));
        db.AudienceDefinitions.Add(def);
        db.EventAudienceDefinitions.Add(new EventAudienceDefinition
        {
            EventId = world.EventId,
            AudienceDefinition = def,
        });
        await db.SaveChangesAsync();
        return def.Id;
    }

    private async Task<Guid> AddStudentAsync(Guid schoolId, string number, string course = "BSIT")
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, number, course: course);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private async Task<Guid> AddStudentWithCardAsync(Guid schoolId, string number, string cardUid)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, number, lastName: "Flores");
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(schoolId, student.Id, cardUid));
        await db.SaveChangesAsync();
        return student.Id;
    }

    private async Task<Guid> AddDeviceAsync(Guid schoolId, string name = "TEST-KIOSK")
    {
        await using var db = NewDbContext();
        var device = TestData.NewDevice(schoolId, name);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    /// <summary>Writes a session (optionally already linked) with the given student registrants directly, so the
    /// numbers under test are deterministic. Same entity shape the production write path produces.</summary>
    private async Task<Guid> AddSessionAsync(
        Guid schoolId, Guid definitionId, Guid? eventId, string name, IEnumerable<Guid> studentIds)
    {
        await using var db = NewDbContext();
        var session = new PreRegistrationSession
        {
            SchoolId = schoolId,
            AudienceDefinitionId = definitionId,
            Name = name,
            Capacity = 1000,
            EventId = eventId,
        };
        db.PreRegistrationSessions.Add(session);

        foreach (var sid in studentIds)
            db.PreRegistrations.Add(new PreRegistration
            {
                SchoolId = schoolId,
                Session = session,
                StudentId = sid,
                AttendeeType = PreRegistrantType.Student,
                Method = PreRegistrationMethod.Manual,
            });

        await db.SaveChangesAsync();
        return session.Id;
    }

    /// <summary>Registers one more student into an EXISTING session — the "re-register while live / after close" move.</summary>
    private async Task RegisterStudentAsync(Guid schoolId, Guid sessionId, Guid studentId)
    {
        await using var db = NewDbContext();
        db.PreRegistrations.Add(new PreRegistration
        {
            SchoolId = schoolId,
            SessionId = sessionId,
            StudentId = studentId,
            AttendeeType = PreRegistrantType.Student,
            Method = PreRegistrationMethod.Manual,
        });
        await db.SaveChangesAsync();
    }

    private async Task SoftDeleteStudentAsync(Guid studentId)
    {
        await using var db = NewDbContext();
        var student = await db.Students.SingleAsync(s => s.Id == studentId);
        student.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static string Base(Guid eventId) => $"/api/v1/events/{eventId}";

    private static async Task<JsonElement> ReadAudienceAsync(HttpClient client, Guid eventId)
    {
        var response = await client.GetAsync($"{Base(eventId)}/attendees");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task<HttpStatusCode> LinkAsync(HttpClient client, Guid eventId, Guid sessionId) =>
        (await client.PostAsJsonAsync(
            $"{Base(eventId)}/pre-registration/sessions",
            new { preRegistrationSessionId = sessionId })).StatusCode;

    private async Task<HttpStatusCode> UnlinkAsync(HttpClient client, Guid eventId, Guid sessionId) =>
        (await client.DeleteAsync($"{Base(eventId)}/pre-registration/sessions/{sessionId}")).StatusCode;

    // ===================================================================== 1. liveness / REPLACE control

    /// <summary>
    /// The denominator FOLLOWS the linked session while the event is live: a registrant added after linking
    /// moves Expected, and unlinking restores the superseded union with no data loss. This is the
    /// "replace, not union" property proven DYNAMICALLY — ray's standard test proves the static flip; this
    /// proves the live set tracks the session and reverts.
    ///
    /// <para>Negative-controlled: neutralising the REPLACE branch in <c>ExpectedStudentIdsAsync</c> makes a
    /// linked event resolve the 2-student union instead, so the linked assertions below go red.</para>
    /// </summary>
    [Fact]
    public async Task Control_a_registrant_added_to_a_linked_session_moves_live_expected_and_unlink_restores_the_union()
    {
        var world = await ArrangeAsync();

        // UNION: a CICT department definition resolving exactly two CICT students (N = 2).
        await AttachCictDepartmentAsync(world);
        await AddStudentAsync(world.SchoolId, "2023-0001", course: "CICT");
        await AddStudentAsync(world.SchoolId, "2023-0002", course: "CICT");

        // SESSION: three BSIT students the CICT definition does NOT resolve, so a replace is visible (M = 3).
        var s1 = await AddStudentAsync(world.SchoolId, "2023-1001");
        var s2 = await AddStudentAsync(world.SchoolId, "2023-1002");
        var s3 = await AddStudentAsync(world.SchoolId, "2023-1003");
        var sessionId = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: null, "Orientation", [s1, s2, s3]);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        // Before linking: the union drives it — two CICT students.
        var before = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.Audience, before.GetProperty("expectedSource").GetString());
        Assert.Equal(2, before.GetProperty("expected").GetInt32());

        // Link: REPLACE — three pre-registered students, not two (union) and not five (union ∪ session).
        Assert.Equal(HttpStatusCode.OK, await LinkAsync(client, world.EventId, sessionId));
        var linked = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.PreRegistration, linked.GetProperty("expectedSource").GetString());
        Assert.Equal(3, linked.GetProperty("expected").GetInt32());

        // Re-register a FOURTH student into the already-linked session: Expected MOVES while live (3 -> 4).
        var s4 = await AddStudentAsync(world.SchoolId, "2023-1004");
        await RegisterStudentAsync(world.SchoolId, sessionId, s4);
        var moved = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.PreRegistration, moved.GetProperty("expectedSource").GetString());
        Assert.Equal(4, moved.GetProperty("expected").GetInt32());

        // Unlink: the kept-but-unresolved union is restored with no data loss — back to two.
        Assert.Equal(HttpStatusCode.OK, await UnlinkAsync(client, world.EventId, sessionId));
        var after = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.Audience, after.GetProperty("expectedSource").GetString());
        Assert.Equal(2, after.GetProperty("expected").GetInt32());
    }

    // ===================================================================== 2. freeze write/read symmetry (D-15)

    /// <summary>
    /// ADR-003 D-15 over the pre-registration source: a student registered then SOFT-DELETED before the
    /// close is excluded from the LIVE denominator but is still written into the FROZEN one (the snapshot
    /// resolves pre-registered students with <c>includeDeleted: true</c>), so the frozen Expected equals the
    /// snapshot rows exactly; and a registration change AFTER close does not move the frozen number (D-13).
    ///
    /// <para>Negative-controlled: flipping the snapshot selector to <c>includeDeleted: false</c> drops the
    /// soft-deleted registrant from the snapshot, so the frozen-count assertion goes red (1, not 2).</para>
    /// </summary>
    [Fact]
    public async Task Control_a_registrant_soft_deleted_before_close_remains_in_the_frozen_denominator()
    {
        var world = await ArrangeAsync();

        var p1 = await AddStudentAsync(world.SchoolId, "2023-1001");
        var p2 = await AddStudentAsync(world.SchoolId, "2023-1002");
        var sessionId = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: null, "Orientation", [p1, p2]);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(HttpStatusCode.OK, await LinkAsync(client, world.EventId, sessionId));

        // Soft-delete p2 BEFORE close: the LIVE denominator excludes them (D-15 live side) — one, not two.
        await SoftDeleteStudentAsync(p2);
        var live = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(1, live.GetProperty("expected").GetInt32());

        // Close (Open -> Closed): the freeze snapshots the pre-registered students with includeDeleted:true.
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(
            $"{Base(world.EventId)}/status", new { status = EventStatus.Closed })).StatusCode);

        // FROZEN denominator INCLUDES the soft-deleted registrant: two, and it steps up from the live one
        // (D-15's accepted bounded discontinuity). The frozen Expected equals the snapshot rows exactly.
        var frozen = await ReadAudienceAsync(client, world.EventId);
        Assert.True(frozen.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(2, frozen.GetProperty("expected").GetInt32());

        await using (var read = NewDbContext())
        {
            var snapshot = await read.EventGroups.AsNoTracking()
                .Where(eg => eg.EventId == world.EventId && eg.StudentId != null)
                .Select(eg => eg.StudentId!.Value)
                .ToListAsync();
            Assert.Equal(2, snapshot.Count);                 // write == read: snapshot rows == frozen Expected
            Assert.Contains(p1, snapshot);
            Assert.Contains(p2, snapshot);                   // the soft-deleted registrant is frozen in
        }

        // D-13: a registration change AFTER close does not move the frozen number — the frozen read is the
        // written-down rows, not a live resolve.
        var p3 = await AddStudentAsync(world.SchoolId, "2023-1003");
        await RegisterStudentAsync(world.SchoolId, sessionId, p3);
        var stillFrozen = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(2, stillFrozen.GetProperty("expected").GetInt32());
    }

    // ===================================================================== 3. capture guardrail (D-75 / D-20)

    /// <summary>
    /// ADR-008 D-75: the replace is a denominator change, never a capture-time gate. On a LINKED event a
    /// student who is NOT pre-registered taps successfully (recorded, not rejected), is flagged
    /// <c>isExpected = false</c> on the roster, and does not enter the denominator; and a re-tap carrying the
    /// same <c>(DeviceId, deviceTapId)</c> is idempotent — one attendance row, the same record returned
    /// (<c>UX_Attendance_Device_DeviceTapId</c>, plan §8.2).
    ///
    /// <para>
    /// The accept-and-flag half is asserted on observable behaviour rather than by reverting production:
    /// no single line toggles it — the tap path (<c>AttendanceService.TapAsync</c>) never consults the
    /// expected set to decide whether to record, so "accept" is structural, and "flag" is the roster's
    /// <c>recorded EXCEPT expected</c>. The idempotency guard IS negative-controllable and is already
    /// negative-controlled in <see cref="TapFlowTests"/> (the two-device / replay pair over the same index);
    /// here it is shown by the replay collapsing to one row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Control_a_non_pre_registered_tap_on_a_linked_event_is_accepted_flagged_unexpected_and_idempotent()
    {
        var world = await ArrangeAsync();

        // Linked session of one pre-registered student (the whole denominator), written already-linked.
        var preRegistered = await AddStudentAsync(world.SchoolId, "2023-0001");
        await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: world.EventId, "Orientation", [preRegistered]);

        // A DIFFERENT student, NOT pre-registered, holding a card — the walk-in.
        const string walkInUid = "04A7B8C9";
        var walkIn = await AddStudentWithCardAsync(world.SchoolId, "2023-9001", walkInUid);
        var deviceId = await AddDeviceAsync(world.SchoolId);
        const string tapId = "walkin-tap-0001";
        var request = new TapRequest(world.EventId, walkInUid, deviceId, tapId, TestData.Now);

        // The tap is ACCEPTED, not rejected — the linked event does not block an uninvited card.
        await using (var db = NewDbContext())
        {
            var tap = await AttendanceOn(db).TapAsync(request);
            Assert.Equal(TapOutcome.Recorded, tap.Outcome);
            Assert.True(tap.Result.Success);
            Assert.NotNull(tap.Result.Record);
            Assert.Equal(walkIn, tap.Result.Record!.StudentId);
        }

        // The denominator does NOT move (still the one pre-registered student), and the walk-in is flagged
        // unexpected on the roster and counted as Unexpected on the summary.
        await using (var db = NewDbContext())
        {
            var roster = await EventsOn(db).GetRosterAsync(world.EventId);
            Assert.NotNull(roster);
            Assert.Equal(1, roster!.Expected);
            Assert.Equal(1, roster.Unexpected);
            Assert.True(roster.Entries.Single(e => e.StudentId == preRegistered).IsExpected);
            Assert.False(roster.Entries.Single(e => e.StudentId == walkIn).IsExpected);

            var summary = await EventsOn(db).GetSummaryAsync(world.EventId);
            Assert.NotNull(summary);
            Assert.Equal(1, summary!.Expected);
            Assert.Equal(1, summary.Unexpected);
        }

        // Replay the SAME (DeviceId, deviceTapId): idempotent — one row, the same record returned.
        await using (var db = NewDbContext())
        {
            var replay = await AttendanceOn(db).TapAsync(request);
            Assert.Equal(TapOutcome.DuplicateIgnored, replay.Outcome);
            Assert.True(replay.Result.Success);
            Assert.Equal(walkIn, replay.Result.Record!.StudentId);
        }

        await using (var read = NewDbContext())
        {
            var rows = await read.AttendanceRecords.AsNoTracking()
                .Where(a => a.EventId == world.EventId)
                .ToListAsync();
            Assert.Single(rows);
            Assert.Equal(walkIn, rows[0].StudentId);
        }
    }
}
