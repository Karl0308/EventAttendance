using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Task 2 Phase 2 (ADR-007 D-69/D-70): a reusable <c>AudienceDefinition</c> attaches to an event
/// alongside its sections and individuals, its STUDENT half joins the deduped denominator, it freezes
/// at the terminal transition exactly as a section does, and its PERSONNEL half is surfaced as an
/// advisory count that never enters the denominator.
///
/// <para>
/// Service-level like <see cref="EventCloseFreezeTests"/> — the behaviours under test (the live union,
/// the freeze, the dedupe) are the service's, and every one is a property of SQL Server (filtered/unique
/// indexes, UNION dedupe), so this runs on the real database, never EF InMemory.
/// </para>
///
/// <para>
/// Two of these carry a <b>negative control</b> performed out of band (standing repo rule
/// <c>feedback_negative_control_before_claiming_fixed</c>): the dedupe and the freeze were each reverted
/// in the production code and confirmed to turn the asserting test red for the right reason, then
/// restored. See the report for the recorded before/after.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventAudienceAttachDefinitionsApiTests : IntegrationTest
{
    public EventAudienceAttachDefinitionsApiTests(SqlServerFixture sql) : base(sql) { }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record World(Guid SchoolId, Guid ClassificationId, Guid EventId);

    /// <summary>A classified, Open event in its own school — the precondition for attaching any definition.</summary>
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

        await db.SaveChangesAsync();
        return new World(school.Id, classification.Id, ev.Id);
    }

    private static AudienceDefinition Definition(
        Guid schoolId, Guid classificationId, string name, string type,
        AudienceCriteriaDto criteria, bool isActive = true) => new()
    {
        SchoolId = schoolId,
        EventClassificationId = classificationId,
        Name = name,
        NameKey = AudienceText.KeyFor(name),
        AudienceType = type,
        CriteriaJson = JsonSerializer.Serialize(criteria, Web),
        IsActive = isActive,
    };

    private async Task AddSectionAStudentAsync(Guid schoolId, string studentNumber)
    {
        // TestData.NewStudent defaults Section = "A", which is what the Section ["A"] definitions below
        // resolve on — so a new student is a new definition-resolved attendee, with no group or
        // individual attachment involved. That isolation is the point: it proves the DEFINITION source
        // is live, not the section-group source.
        await using var db = NewDbContext();
        db.Students.Add(TestData.NewStudent(schoolId, studentNumber));
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------- attach + idempotency

    [Fact]
    public async Task Attaching_several_definitions_at_once_is_counted_once_and_re_posting_is_idempotent()
    {
        var world = await ArrangeAsync();

        Guid d1, d2;
        await using (var db = NewDbContext())
        {
            var a = Definition(world.SchoolId, world.ClassificationId, "Everyone",
                AudienceType.UniversityWide, new AudienceCriteriaDto(Scope: "Both"));
            var b = Definition(world.SchoolId, world.ClassificationId, "BSIT Department",
                AudienceType.Department, new AudienceCriteriaDto(Departments: ["BSIT"]));
            db.AudienceDefinitions.AddRange(a, b);
            await db.SaveChangesAsync();
            d1 = a.Id;
            d2 = b.Id;
        }

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest(null, null, [d1, d2]));

            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
            Assert.Equal(2, response.Result!.DefinitionsAttached);
            Assert.Equal(0, response.Result.DefinitionsAlreadyAttached);
        }

        // The same selection re-posted attaches nothing and reports both as already attached — the
        // observable half of idempotency, backed by the unique index not by this method remembering.
        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest(null, null, [d1, d2]));

            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
            Assert.Equal(0, response.Result!.DefinitionsAttached);
            Assert.Equal(2, response.Result.DefinitionsAlreadyAttached);
        }

        await using (var read = NewDbContext())
        {
            var audience = await EventsOn(read).GetAudienceAsync(world.EventId);
            Assert.Equal(2, audience!.Definitions.Count);
        }
    }

    // ------------------------------------------------------------------- reference validation (all 400)

    [Fact]
    public async Task A_definition_outside_the_events_classification_is_an_unknown_reference()
    {
        var world = await ArrangeAsync();

        Guid defId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewEventClassification(world.SchoolId, "Departmental Events");
            db.EventClassifications.Add(other);
            var def = Definition(world.SchoolId, other.Id, "Other",
                AudienceType.UniversityWide, new AudienceCriteriaDto(Scope: "Both"));
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            defId = def.Id;
        }

        await using var write = NewDbContext();
        var response = await EventsOn(write).AttachAudienceAsync(
            world.EventId, new EventAudienceRequest(null, null, [defId]));

        Assert.Equal(EventWriteOutcome.UnknownReference, response.Outcome);
    }

    [Fact]
    public async Task A_definition_from_another_school_is_an_unknown_reference()
    {
        var world = await ArrangeAsync();

        Guid defId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("USA2");
            db.Schools.Add(other);
            var otherClassification = TestData.NewEventClassification(other.Id);
            db.EventClassifications.Add(otherClassification);
            var def = Definition(other.Id, otherClassification.Id, "Cross Tenant",
                AudienceType.UniversityWide, new AudienceCriteriaDto(Scope: "Both"));
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            defId = def.Id;
        }

        await using var write = NewDbContext();
        var response = await EventsOn(write).AttachAudienceAsync(
            world.EventId, new EventAudienceRequest(null, null, [defId]));

        Assert.Equal(EventWriteOutcome.UnknownReference, response.Outcome);
    }

    [Fact]
    public async Task An_inactive_definition_is_an_unknown_reference()
    {
        var world = await ArrangeAsync();

        Guid defId;
        await using (var db = NewDbContext())
        {
            var def = Definition(world.SchoolId, world.ClassificationId, "Retired",
                AudienceType.UniversityWide, new AudienceCriteriaDto(Scope: "Both"), isActive: false);
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            defId = def.Id;
        }

        await using var write = NewDbContext();
        var response = await EventsOn(write).AttachAudienceAsync(
            world.EventId, new EventAudienceRequest(null, null, [defId]));

        Assert.Equal(EventWriteOutcome.UnknownReference, response.Outcome);
    }

    [Fact]
    public async Task A_definition_cannot_attach_to_an_unclassified_event()
    {
        // An event with no EventClassificationId can carry no definition: every definition is filed
        // under a classification, and none can be "under" a null one.
        Guid eventId, defId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            var classification = TestData.NewEventClassification(school.Id);
            db.EventClassifications.Add(classification);
            var ev = TestData.NewEvent(school.Id, EventStatus.Open); // EventClassificationId left null
            db.Events.Add(ev);
            var def = Definition(school.Id, classification.Id, "Any",
                AudienceType.UniversityWide, new AudienceCriteriaDto(Scope: "Both"));
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            eventId = ev.Id;
            defId = def.Id;
        }

        await using var write = NewDbContext();
        var response = await EventsOn(write).AttachAudienceAsync(
            eventId, new EventAudienceRequest(null, null, [defId]));

        Assert.Equal(EventWriteOutcome.UnknownReference, response.Outcome);
    }

    // ------------------------------------------------------------------- dedupe (live AND frozen)

    /// <summary>
    /// A student reached by BOTH an attached group and an attached definition counts ONCE — live and
    /// after the close. The live dedupe is the SQL <c>UNION</c> in <c>ExpectedStudentIdsAsync</c>; the
    /// frozen dedupe is <c>UX_EventGroups_Event_Student</c> plus the snapshot's <c>Except</c>. Negative
    /// control (out of band): reverting the live <c>UNION</c> to <c>Concat</c> makes the live assertion
    /// read 2 and go red — see the report.
    /// </summary>
    [Fact]
    public async Task A_student_in_both_a_group_and_a_definition_counts_once_live_and_frozen()
    {
        var world = await ArrangeAsync();

        Guid groupId, defId;
        await using (var db = NewDbContext())
        {
            var student = TestData.NewStudent(world.SchoolId, "2023-0001"); // Section "A"
            db.Students.Add(student);

            var group = TestData.NewGroup(world.SchoolId, "Manual Cohort");
            db.StudentGroups.Add(group);
            db.StudentGroupMembers.Add(new StudentGroupMember
            {
                StudentGroupId = group.Id,
                StudentId = student.Id,
            });

            var def = Definition(world.SchoolId, world.ClassificationId, "Section A",
                AudienceType.Section, new AudienceCriteriaDto(Sections: ["A"]));
            db.AudienceDefinitions.Add(def);

            await db.SaveChangesAsync();
            groupId = group.Id;
            defId = def.Id;
        }

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest([groupId], null, [defId]));
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        // Live: the one student is reached by both sources and counts once (UNION, not UNION ALL).
        await using (var read = NewDbContext())
            Assert.Equal(1, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        // Frozen: still one, read from the snapshot rows rather than re-resolved.
        await using (var read = NewDbContext())
        {
            Assert.Equal(1, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

            // And the snapshot wrote exactly one individual student row, not two.
            var studentRows = await read.EventGroups.AsNoTracking()
                .CountAsync(eg => eg.EventId == world.EventId && eg.StudentId != null);
            Assert.Equal(1, studentRows);
        }
    }

    // ------------------------------------------------------------------- liveness + freeze

    /// <summary>
    /// A definition resolves LIVE until the terminal transition and is FIXED after it (ADR-003 D-13
    /// applied to the definition source). Negative control (out of band): dropping the definition arm
    /// from the freeze snapshot leaves the frozen count at 0 and turns the post-close assertion red — see
    /// the report.
    /// </summary>
    [Fact]
    public async Task A_definitions_resolved_students_move_the_live_count_and_are_fixed_by_the_close()
    {
        var world = await ArrangeAsync();

        Guid defId;
        await using (var db = NewDbContext())
        {
            db.Students.AddRange(
                TestData.NewStudent(world.SchoolId, "2023-0001"),
                TestData.NewStudent(world.SchoolId, "2023-0002"));
            var def = Definition(world.SchoolId, world.ClassificationId, "Section A",
                AudienceType.Section, new AudienceCriteriaDto(Sections: ["A"]));
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            defId = def.Id;
        }

        await using (var db = NewDbContext())
            Assert.Equal(EventWriteOutcome.Saved, (await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest(null, null, [defId]))).Outcome);

        await using (var read = NewDbContext())
            Assert.Equal(2, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

        // A later enrolment the definition resolves moves the LIVE count.
        await AddSectionAStudentAsync(world.SchoolId, "2023-0003");
        await using (var read = NewDbContext())
            Assert.Equal(3, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

        // The close freezes it.
        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        // A further student the definition would resolve does NOT move the frozen count.
        await AddSectionAStudentAsync(world.SchoolId, "2023-0004");
        await using (var read = NewDbContext())
            Assert.Equal(3, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);
    }

    /// <summary>
    /// ADR-003 D-15 for the definition source: the LIVE denominator excludes a soft-deleted student, but
    /// the freeze resolves the definition with <c>includeDeleted: true</c> so the written snapshot rows
    /// and the frozen read are one set. Negative control: flipping the definition arm of
    /// <c>SnapshotAudienceAsync</c> to <c>includeDeleted: false</c> drops the deleted student from the
    /// snapshot rows and turns the frozen assertions red.
    /// </summary>
    [Fact]
    public async Task A_student_soft_deleted_after_attach_leaves_the_live_count_but_stays_in_the_frozen_set()
    {
        var world = await ArrangeAsync();

        Guid defId, deletedStudentId;
        await using (var db = NewDbContext())
        {
            var kept = TestData.NewStudent(world.SchoolId, "2023-0001");
            var doomed = TestData.NewStudent(world.SchoolId, "2023-0002");
            db.Students.AddRange(kept, doomed);
            var def = Definition(world.SchoolId, world.ClassificationId, "Section A",
                AudienceType.Section, new AudienceCriteriaDto(Sections: ["A"]));
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            defId = def.Id;
            deletedStudentId = doomed.Id;
        }

        await using (var db = NewDbContext())
            Assert.Equal(EventWriteOutcome.Saved, (await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest(null, null, [defId]))).Outcome);

        await using (var read = NewDbContext())
            Assert.Equal(2, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

        // Soft-delete a definition-resolved student AFTER the attach.
        await using (var db = NewDbContext())
        {
            var student = await db.Students.SingleAsync(s => s.Id == deletedStudentId);
            student.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        // Live: the deleted student is out.
        await using (var read = NewDbContext())
            Assert.Equal(1, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);

        // Frozen: the snapshot resolved the definition with includeDeleted:true, so both students have
        // a row and the frozen read counts both — written rows and frozen read are one set.
        await using (var read = NewDbContext())
        {
            Assert.Equal(2, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

            var rows = await read.EventGroups.AsNoTracking()
                .Where(eg => eg.EventId == world.EventId && eg.StudentId != null)
                .Select(eg => eg.StudentId!.Value)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Contains(deletedStudentId, rows);
        }
    }

    // ------------------------------------------------------------------- personnel advisory (D-70)

    [Fact]
    public async Task A_definitions_personnel_are_advisory_and_never_in_the_student_denominator()
    {
        var world = await ArrangeAsync();

        Guid defId;
        await using (var db = NewDbContext())
        {
            // A Department definition resolves students by Course and personnel by Department. One of each.
            db.Students.Add(TestData.NewStudent(world.SchoolId, "2023-0001", course: "CICT"));
            db.Personnel.Add(TestData.NewPersonnel(world.SchoolId, "EMP-0001", department: "CICT"));
            var def = Definition(world.SchoolId, world.ClassificationId, "CICT Department",
                AudienceType.Department, new AudienceCriteriaDto(Departments: ["CICT"]));
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            defId = def.Id;
        }

        await using (var db = NewDbContext())
            await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest(null, null, [defId]));

        await using (var read = NewDbContext())
        {
            var audience = await EventsOn(read).GetAudienceAsync(world.EventId);

            Assert.Equal(1, audience!.Expected);              // the student only
            Assert.Equal(1, audience.AdvisoryPersonnelCount); // the personnel, surfaced but NOT counted

            var definition = Assert.Single(audience.Definitions);
            Assert.Equal(defId, definition.AudienceDefinitionId);
            Assert.Equal(1, definition.StudentCount);
            Assert.Equal(1, definition.PersonnelCount);
            Assert.True(definition.IsActive);
        }

        // The summary's denominator agrees — personnel are nowhere in it.
        await using (var read = NewDbContext())
            Assert.Equal(1, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);
    }

    // ------------------------------------------------------------------- detach (removable surface)

    [Fact]
    public async Task Detaching_a_definition_removes_it_from_the_denominator_and_is_idempotent()
    {
        var world = await ArrangeAsync();

        Guid defId;
        await using (var db = NewDbContext())
        {
            db.Students.Add(TestData.NewStudent(world.SchoolId, "2023-0001")); // Section "A"
            var def = Definition(world.SchoolId, world.ClassificationId, "Section A",
                AudienceType.Section, new AudienceCriteriaDto(Sections: ["A"]));
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            defId = def.Id;
        }

        await using (var db = NewDbContext())
            await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest(null, null, [defId]));

        await using (var read = NewDbContext())
            Assert.Equal(1, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).DetachDefinitionAsync(world.EventId, defId);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        await using (var read = NewDbContext())
            Assert.Equal(0, (await EventsOn(read).GetSummaryAsync(world.EventId))!.Expected);

        // Idempotent: detaching one that is no longer attached still succeeds.
        await using (var db = NewDbContext())
            Assert.Equal(EventWriteOutcome.Saved,
                (await EventsOn(db).DetachDefinitionAsync(world.EventId, defId)).Outcome);
    }
}
