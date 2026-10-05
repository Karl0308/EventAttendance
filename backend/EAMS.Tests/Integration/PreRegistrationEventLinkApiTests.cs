using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Task 2 Phase 3 (ADR-008 D-71..D-75): a pre-registration session links to an event and REPLACES the
/// section + definition union as the expected audience; the link is reversible; linking/unlinking is
/// refused once the event is terminal; the student registrants drive the denominator and freeze with it,
/// while personnel are an advisory count; and the audience read publishes the source and the counts.
///
/// <para>
/// The STANDARD set over real HTTP (sign in with <see cref="IntegrationTest.SignedInClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{Program}, string)"/>,
/// tenant from the token). The three negative-controlled CONTROL tests ADR-008 adds — the liveness/replace
/// control, the freeze soft-delete symmetry control, and the capture guardrail control — are owned by a
/// separate dispatch and are deliberately NOT written here; their names are left free.
/// </para>
///
/// <para>
/// SQL Server via Testcontainers, never EF InMemory: the replace/freeze dedupe is a SQL <c>UNION</c>, the
/// session link is a real FK, and the read counts are correlated SQL aggregates — all provider behaviour.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PreRegistrationEventLinkApiTests : IntegrationTest
{
    public PreRegistrationEventLinkApiTests(SqlServerFixture sql) : base(sql) { }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record World(Guid SchoolId, Guid ClassificationId, Guid SessionDefinitionId, Guid EventId);

    /// <summary>A classified, Open event in its own (single) school, plus a generic definition a session can
    /// be opened against. One school, so <c>SignedInClientAsync(factory)</c> signs in to exactly it.</summary>
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

        var def = Definition(school.Id, classification.Id, "Everyone",
            AudienceType.UniversityWide, new AudienceCriteriaDto(Scope: "Both"));
        db.AudienceDefinitions.Add(def);

        await db.SaveChangesAsync();
        return new World(school.Id, classification.Id, def.Id, ev.Id);
    }

    private static AudienceDefinition Definition(
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

    private async Task<Guid> AddStudentAsync(Guid schoolId, string number, string course = "BSIT")
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, number, course: course);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private async Task<Guid> AddPersonnelAsync(Guid schoolId, string number)
    {
        await using var db = NewDbContext();
        var personnel = TestData.NewPersonnel(schoolId, number);
        db.Personnel.Add(personnel);
        await db.SaveChangesAsync();
        return personnel.Id;
    }

    private async Task AttachDefinitionAsync(Guid eventId, Guid definitionId)
    {
        await using var db = NewDbContext();
        db.EventAudienceDefinitions.Add(new EventAudienceDefinition
        {
            EventId = eventId,
            AudienceDefinitionId = definitionId,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Creates a pre-registration session (optionally already linked to an event) with the given
    /// student and personnel registrants, written directly so the counts under test are deterministic.</summary>
    private async Task<Guid> AddSessionAsync(
        Guid schoolId, Guid definitionId, Guid? eventId, string name,
        IEnumerable<Guid> studentIds, IEnumerable<Guid>? personnelIds = null)
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

        foreach (var pid in personnelIds ?? [])
            db.PreRegistrations.Add(new PreRegistration
            {
                SchoolId = schoolId,
                Session = session,
                PersonnelId = pid,
                AttendeeType = PreRegistrantType.Personnel,
                Method = PreRegistrationMethod.Manual,
            });

        await db.SaveChangesAsync();
        return session.Id;
    }

    private static string Base(Guid eventId) => $"/api/v1/events/{eventId}";

    private static async Task<JsonElement> ReadAudienceAsync(HttpClient client, Guid eventId)
    {
        var response = await client.GetAsync($"{Base(eventId)}/attendees");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    // ------------------------------------------------------------------- link + unlink + idempotency

    [Fact]
    public async Task A_session_links_and_unlinks_and_unlinking_is_idempotent()
    {
        var world = await ArrangeAsync();
        var studentId = await AddStudentAsync(world.SchoolId, "2023-0001");
        var sessionId = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: null, "Freshman", [studentId]);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        // Link.
        var link = await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions",
            new { preRegistrationSessionId = sessionId });
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);

        var linked = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.PreRegistration, linked.GetProperty("expectedSource").GetString());
        Assert.Equal(JsonValueKind.Object, linked.GetProperty("preRegistration").ValueKind);

        await using (var db = NewDbContext())
            Assert.Equal(world.EventId, await db.PreRegistrationSessions
                .Where(s => s.Id == sessionId).Select(s => s.EventId).SingleAsync());

        // Unlink.
        var unlink = await client.DeleteAsync($"{Base(world.EventId)}/pre-registration/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, unlink.StatusCode);

        var unlinked = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.Audience, unlinked.GetProperty("expectedSource").GetString());
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("preRegistration").ValueKind);

        // Unlinking again (the same session) still succeeds — the postcondition holds either way.
        var again = await client.DeleteAsync($"{Base(world.EventId)}/pre-registration/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // And unlinking a session that was never linked here is also a success (event exists).
        var never = await client.DeleteAsync(
            $"{Base(world.EventId)}/pre-registration/sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, never.StatusCode);
    }

    // ------------------------------------------------------------------- expectedSource flips + replace + freeze

    [Fact]
    public async Task Linking_replaces_the_union_flips_expectedSource_and_unlinking_restores_it()
    {
        var world = await ArrangeAsync();

        // The AUDIENCE: a Department(CICT) definition resolving exactly two CICT students, attached.
        var a1 = await AddStudentAsync(world.SchoolId, "2023-0001", course: "CICT");
        var a2 = await AddStudentAsync(world.SchoolId, "2023-0002", course: "CICT");
        Guid audienceDefId;
        await using (var db = NewDbContext())
        {
            var def = Definition(world.SchoolId, world.ClassificationId, "CICT Department",
                AudienceType.Department, new AudienceCriteriaDto(Departments: ["CICT"]));
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            audienceDefId = def.Id;
        }
        await AttachDefinitionAsync(world.EventId, audienceDefId);

        // The SESSION: three BSIT students the CICT definition does NOT resolve — so a replace is visible.
        var s1 = await AddStudentAsync(world.SchoolId, "2023-1001", course: "BSIT");
        var s2 = await AddStudentAsync(world.SchoolId, "2023-1002", course: "BSIT");
        var s3 = await AddStudentAsync(world.SchoolId, "2023-1003", course: "BSIT");
        var sessionId = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: null, "Orientation", [s1, s2, s3]);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        // Before linking: the union drives it — two CICT students, Audience source.
        var before = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.Audience, before.GetProperty("expectedSource").GetString());
        Assert.Equal(2, before.GetProperty("expected").GetInt32());

        // Link: the session REPLACES the union — three, not two and not five, and the attached definition
        // is KEPT (still listed), just not resolved.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions",
            new { preRegistrationSessionId = sessionId })).StatusCode);

        var linked = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.PreRegistration, linked.GetProperty("expectedSource").GetString());
        Assert.Equal(3, linked.GetProperty("expected").GetInt32());
        Assert.Equal(3, linked.GetProperty("preRegistration")
            .GetProperty("totalPreRegisteredStudentCount").GetInt32());
        Assert.Equal(1, linked.GetProperty("definitions").GetArrayLength()); // kept, not deleted

        // Unlink: the union is restored with no data loss — back to two.
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(
            $"{Base(world.EventId)}/pre-registration/sessions/{sessionId}")).StatusCode);

        var after = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.Audience, after.GetProperty("expectedSource").GetString());
        Assert.Equal(2, after.GetProperty("expected").GetInt32());
    }

    [Fact]
    public async Task Closing_a_linked_event_freezes_the_pre_registered_students_not_the_union()
    {
        var world = await ArrangeAsync();

        // An attached definition resolving one student, plus a linked session of two DIFFERENT students.
        var unionStudent = await AddStudentAsync(world.SchoolId, "2023-0001", course: "CICT");
        Guid audienceDefId;
        await using (var db = NewDbContext())
        {
            var def = Definition(world.SchoolId, world.ClassificationId, "CICT Department",
                AudienceType.Department, new AudienceCriteriaDto(Departments: ["CICT"]));
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            audienceDefId = def.Id;
        }
        await AttachDefinitionAsync(world.EventId, audienceDefId);

        var p1 = await AddStudentAsync(world.SchoolId, "2023-1001", course: "BSIT");
        var p2 = await AddStudentAsync(world.SchoolId, "2023-1002", course: "BSIT");
        var sessionId = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: null, "Orientation", [p1, p2]);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions",
            new { preRegistrationSessionId = sessionId })).StatusCode);

        // Close (Open -> Closed) — the freeze snapshots the PRE-REG students via the shared selector.
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(
            $"{Base(world.EventId)}/status", new { status = EventStatus.Closed })).StatusCode);

        var frozen = await ReadAudienceAsync(client, world.EventId);
        Assert.True(frozen.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(2, frozen.GetProperty("expected").GetInt32()); // the two pre-reg students, not the one union

        // The snapshot wrote exactly the two pre-registered students as individual rows — and NOT the
        // union student, proving the freeze used the pre-reg selector rather than the section+definition set.
        await using var read = NewDbContext();
        var snapshot = await read.EventGroups.AsNoTracking()
            .Where(eg => eg.EventId == world.EventId && eg.StudentId != null)
            .Select(eg => eg.StudentId!.Value)
            .ToListAsync();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(p1, snapshot);
        Assert.Contains(p2, snapshot);
        Assert.DoesNotContain(unionStudent, snapshot);
    }

    // ------------------------------------------------------------------- count accuracy + N:1 dedupe

    [Fact]
    public async Task Counts_dedupe_across_many_linked_sessions_and_personnel_stay_advisory()
    {
        var world = await ArrangeAsync();

        var a = await AddStudentAsync(world.SchoolId, "2023-0001");
        var b = await AddStudentAsync(world.SchoolId, "2023-0002");
        var c = await AddStudentAsync(world.SchoolId, "2023-0003");
        var person = await AddPersonnelAsync(world.SchoolId, "EMP-0001");

        // N:1 — two sessions on one event. Students {A,B} and {B,C}; the same personnel in both.
        var session1 = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: null, "Morning", [a, b], [person]);
        var session2 = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: null, "Afternoon", [b, c], [person]);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        foreach (var sessionId in new[] { session1, session2 })
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
                $"{Base(world.EventId)}/pre-registration/sessions",
                new { preRegistrationSessionId = sessionId })).StatusCode);

        var audience = await ReadAudienceAsync(client, world.EventId);
        var preReg = audience.GetProperty("preRegistration");

        // Per-session counts: each session has two students and one personnel.
        Assert.Equal(2, preReg.GetProperty("linkedSessions").GetArrayLength());
        foreach (var session in preReg.GetProperty("linkedSessions").EnumerateArray())
        {
            Assert.Equal(2, session.GetProperty("preRegisteredStudentCount").GetInt32());
            Assert.Equal(1, session.GetProperty("advisoryPersonnelCount").GetInt32());
        }

        // Totals dedupe: {A,B} ∪ {B,C} = three students; the one personnel counted once.
        Assert.Equal(3, preReg.GetProperty("totalPreRegisteredStudentCount").GetInt32());
        Assert.Equal(1, preReg.GetProperty("totalAdvisoryPersonnelCount").GetInt32());

        // The denominator is the three students; personnel are surfaced but excluded.
        Assert.Equal(3, audience.GetProperty("expected").GetInt32());
        Assert.Equal(1, audience.GetProperty("advisoryPersonnelCount").GetInt32());

        // The summary agrees — personnel are nowhere in the denominator.
        var summary = await client.GetAsync($"{Base(world.EventId)}/summary");
        using var summaryBody = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        Assert.Equal(3, summaryBody.RootElement.GetProperty("expected").GetInt32());
    }

    [Fact]
    public async Task Pre_registered_personnel_are_surfaced_but_never_in_the_expected_denominator()
    {
        var world = await ArrangeAsync();
        var studentId = await AddStudentAsync(world.SchoolId, "2023-0001");
        var person1 = await AddPersonnelAsync(world.SchoolId, "EMP-0001");
        var person2 = await AddPersonnelAsync(world.SchoolId, "EMP-0002");

        var sessionId = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: null, "Mixed",
            [studentId], [person1, person2]);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions",
            new { preRegistrationSessionId = sessionId })).StatusCode);

        var audience = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(1, audience.GetProperty("expected").GetInt32());              // the one student
        Assert.Equal(2, audience.GetProperty("advisoryPersonnelCount").GetInt32()); // the two personnel
        Assert.Equal(2, audience.GetProperty("preRegistration")
            .GetProperty("totalAdvisoryPersonnelCount").GetInt32());
    }

    // ------------------------------------------------------------------- terminal refusal (409)

    [Fact]
    public async Task Linking_unlinking_and_create_from_event_are_refused_on_a_terminal_event()
    {
        var world = await ArrangeAsync(status: EventStatus.Closed);
        var studentId = await AddStudentAsync(world.SchoolId, "2023-0001");
        var sessionId = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: null, "Freshman", [studentId]);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var link = await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions",
            new { preRegistrationSessionId = sessionId });
        Assert.Equal(HttpStatusCode.Conflict, link.StatusCode);

        var unlink = await client.DeleteAsync(
            $"{Base(world.EventId)}/pre-registration/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.Conflict, unlink.StatusCode);

        var fromEvent = await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions/from-event", new { name = "X" });
        Assert.Equal(HttpStatusCode.Conflict, fromEvent.StatusCode);
    }

    // ------------------------------------------------------------------- reference validation (400 / 404)

    [Fact]
    public async Task An_unknown_a_cross_tenant_and_an_already_linked_session_are_all_rejected()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        // Unknown session → 400.
        var unknown = await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions",
            new { preRegistrationSessionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // A session in another tenant → 400 (indistinguishable from unknown, by design).
        Guid crossTenantSessionId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("USA2");
            db.Schools.Add(other);
            var otherClassification = TestData.NewEventClassification(other.Id);
            db.EventClassifications.Add(otherClassification);
            var otherDef = Definition(other.Id, otherClassification.Id, "Everyone",
                AudienceType.UniversityWide, new AudienceCriteriaDto(Scope: "Both"));
            db.AudienceDefinitions.Add(otherDef);
            await db.SaveChangesAsync();
            crossTenantSessionId = await AddSessionAsync(
                other.Id, otherDef.Id, eventId: null, "Other", []);
        }

        var crossTenant = await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions",
            new { preRegistrationSessionId = crossTenantSessionId });
        Assert.Equal(HttpStatusCode.BadRequest, crossTenant.StatusCode);

        // A session already linked to a DIFFERENT event in this tenant → 400 (a session links to one event).
        Guid otherEventId;
        await using (var db = NewDbContext())
        {
            var ev2 = TestData.NewEvent(world.SchoolId, EventStatus.Open);
            ev2.EventClassificationId = world.ClassificationId;
            db.Events.Add(ev2);
            await db.SaveChangesAsync();
            otherEventId = ev2.Id;
        }
        var linkedElsewhere = await AddSessionAsync(
            world.SchoolId, world.SessionDefinitionId, eventId: otherEventId, "Taken", []);

        var conflict = await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions",
            new { preRegistrationSessionId = linkedElsewhere });
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
    }

    [Fact]
    public async Task Linking_to_a_missing_event_is_a_404()
    {
        await ArrangeAsync(); // one school must exist for the signed-in client

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Base(Guid.NewGuid())}/pre-registration/sessions",
            new { preRegistrationSessionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------- create-from-event

    [Fact]
    public async Task Create_from_event_opens_and_links_a_session_against_the_single_attached_definition()
    {
        var world = await ArrangeAsync();
        await AttachDefinitionAsync(world.EventId, world.SessionDefinitionId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions/from-event",
            new { name = "Walk-up Desk" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var audience = await ReadAudienceAsync(client, world.EventId);
        Assert.Equal(ExpectedAudienceSource.PreRegistration, audience.GetProperty("expectedSource").GetString());
        // A freshly created session has no registrants yet: the linked-but-empty session makes Expected 0.
        Assert.Equal(0, audience.GetProperty("expected").GetInt32());

        var sessions = audience.GetProperty("preRegistration").GetProperty("linkedSessions");
        Assert.Equal(1, sessions.GetArrayLength());
        Assert.Equal("Walk-up Desk", sessions[0].GetProperty("name").GetString());

        // The session exists in the database, linked to this event, with a valid (>=1) capacity.
        await using var read = NewDbContext();
        var row = await read.PreRegistrationSessions.AsNoTracking()
            .SingleAsync(s => s.EventId == world.EventId);
        Assert.True(row.Capacity >= 1);
        Assert.Equal(world.SessionDefinitionId, row.AudienceDefinitionId);
    }

    [Fact]
    public async Task Create_from_event_is_refused_when_the_event_has_no_single_attached_definition()
    {
        var world = await ArrangeAsync(); // no definition attached

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Base(world.EventId)}/pre-registration/sessions/from-event", new { name = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
