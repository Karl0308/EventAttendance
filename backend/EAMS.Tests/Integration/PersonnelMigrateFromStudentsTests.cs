using System.Net;
using System.Net.Http.Json;
using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>POST /api/v1/personnel/migrate-from-students</c> (JJ, 2026-11-25): staff imported as students with
/// 7200… numbers become personnel — card moved, student soft-deleted, past attendance untouched — under the
/// D-98 card-UID lock. Real SQL Server, because the move is decided by two filtered unique indexes and an
/// app lock, none of which exist in an in-memory provider.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelMigrateFromStudentsTests : IntegrationTest
{
    public PersonnelMigrateFromStudentsTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/personnel/migrate-from-students";
    private const string StaffNumber = "7200012345";
    private const string StaffUid = "0012503326";

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>A live student with one active card.</summary>
    private async Task<(Guid StudentId, Guid CardId)> ArrangeStudentAsync(
        Guid schoolId, string number = StaffNumber, string uid = StaffUid)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, number, firstName: "Juan", middleName: "M", lastName: "Dela Cruz");
        var card = TestData.NewCard(schoolId, student.Id, uid);
        db.Students.Add(student);
        db.RfidCards.Add(card);
        await db.SaveChangesAsync();
        return (student.Id, card.Id);
    }

    private async Task<Guid> ArrangeAttendanceAsync(Guid schoolId, Guid studentId, Guid cardId, string eventStatus)
    {
        await using var db = NewDbContext();
        var ev = TestData.NewEvent(schoolId, status: eventStatus);
        var record = new AttendanceRecord
        {
            SchoolId = schoolId, EventId = ev.Id, StudentId = studentId, RfidCardId = cardId,
            CheckInAt = TestData.Now, Status = AttendanceStatus.Present, CaptureMethod = CaptureMethod.Rfid,
        };
        db.Events.Add(ev);
        db.AttendanceRecords.Add(record);
        await db.SaveChangesAsync();
        return record.Id;
    }

    private async Task<MigrateFromStudentsResultDto> PostAsync(Guid schoolId, object body)
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(Route, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");

        var result = await response.Content.ReadFromJsonAsync<MigrateFromStudentsResultDto>();
        Assert.NotNull(result);
        return result;
    }

    private Task<MigrateFromStudentsResultDto> RunAsync(Guid schoolId) =>
        PostAsync(schoolId, new { prefix = "7200", dryRun = false });

    private async Task AssertStudentUntouchedAsync(Guid studentId, Guid cardId)
    {
        await using var db = NewDbContext();
        Assert.False(await db.Students.Where(s => s.Id == studentId).Select(s => s.IsDeleted).SingleAsync());
        Assert.True(await db.RfidCards.Where(c => c.Id == cardId).Select(c => c.IsActive).SingleAsync());
    }

    [Fact]
    public async Task MigrateFromStudents_dry_run_is_the_default_and_writes_nothing()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, cardId) = await ArrangeStudentAsync(schoolId);

        // No dryRun in the body: the default must be the preview.
        var result = await PostAsync(schoolId, new { prefix = "7200" });

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Created);
        var row = Assert.Single(result.Rows);
        Assert.Equal(MigrateFromStudentsOutcome.Create, row.Outcome);
        Assert.Equal(StaffUid, row.RfidUid);
        Assert.Null(row.PersonnelId);

        await AssertStudentUntouchedAsync(studentId, cardId);
        await using var db = NewDbContext();
        Assert.Equal(0, await db.Personnel.CountAsync());
    }

    [Fact]
    public async Task MigrateFromStudents_run_moves_the_card_creates_personnel_and_soft_deletes_the_student()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, cardId) = await ArrangeStudentAsync(schoolId);

        var result = await RunAsync(schoolId);

        Assert.False(result.DryRun);
        var row = Assert.Single(result.Rows);
        Assert.Equal(MigrateFromStudentsOutcome.Create, row.Outcome);
        Assert.NotNull(row.PersonnelId);

        await using var db = NewDbContext();
        var personnel = await db.Personnel.SingleAsync();
        Assert.Equal(row.PersonnelId, personnel.Id);
        Assert.Equal(StaffNumber, personnel.PersonnelNumber);
        Assert.Equal(("Juan", "M", "Dela Cruz"), (personnel.FirstName, personnel.MiddleName, personnel.LastName));
        Assert.Equal($"{StaffNumber}@test.local", personnel.Email);
        Assert.Equal(StaffUid, personnel.RfidUid);
        Assert.Equal(PersonnelStatus.Active, personnel.Status);

        var card = await db.RfidCards.SingleAsync(c => c.Id == cardId);
        Assert.False(card.IsActive);
        Assert.NotNull(card.DeactivatedAt);

        Assert.True(await db.Students.IgnoreQueryFilters()
            .Where(s => s.Id == studentId).Select(s => s.IsDeleted).SingleAsync());
    }

    [Fact]
    public async Task MigrateFromStudents_existing_personnel_keeps_its_values_and_gets_the_rfid_filled()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, _) = await ArrangeStudentAsync(schoolId);
        Guid personnelId;
        await using (var db = NewDbContext())
        {
            var existing = TestData.NewPersonnel(schoolId, StaffNumber, firstName: "Juanito", lastName: "Cruz");
            db.Personnel.Add(existing);
            await db.SaveChangesAsync();
            personnelId = existing.Id;
        }

        var result = await RunAsync(schoolId);

        var row = Assert.Single(result.Rows);
        Assert.Equal(MigrateFromStudentsOutcome.Update, row.Outcome);
        Assert.Equal(personnelId, row.PersonnelId);

        await using var check = NewDbContext();
        var personnel = await check.Personnel.SingleAsync();
        Assert.Equal(("Juanito", "Cruz", "CICT"), (personnel.FirstName, personnel.LastName, personnel.Department));
        Assert.Equal(StaffUid, personnel.RfidUid);
        Assert.True(await check.Students.IgnoreQueryFilters()
            .Where(s => s.Id == studentId).Select(s => s.IsDeleted).SingleAsync());
    }

    [Fact]
    public async Task MigrateFromStudents_rerun_is_a_no_op()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeStudentAsync(schoolId);

        await RunAsync(schoolId);
        var second = await RunAsync(schoolId);

        Assert.Equal(0, second.Matched);
        Assert.Empty(second.Rows);

        await using var db = NewDbContext();
        Assert.Equal(1, await db.Personnel.CountAsync());
        Assert.Equal(0, await db.RfidCards.CountAsync(c => c.IsActive));
    }

    [Fact]
    public async Task MigrateFromStudents_student_in_an_open_event_is_refused()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, cardId) = await ArrangeStudentAsync(schoolId);
        await ArrangeAttendanceAsync(schoolId, studentId, cardId, EventStatus.Open);

        var result = await RunAsync(schoolId);

        var row = Assert.Single(result.Rows);
        Assert.Equal(MigrateFromStudentsOutcome.Conflict, row.Outcome);
        Assert.Equal(MigrateFromStudentsConflict.ActiveEvent, row.ConflictCode);
        Assert.Equal(1, result.Conflicts);

        await AssertStudentUntouchedAsync(studentId, cardId);
        await using var db = NewDbContext();
        Assert.Equal(0, await db.Personnel.CountAsync());
    }

    [Fact]
    public async Task MigrateFromStudents_uid_held_by_another_personnel_is_reported_as_a_conflict()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, cardId) = await ArrangeStudentAsync(schoolId);

        // A pre-D-98 collision: the two tables' own indexes cannot see each other, so this row is legal.
        await using (var db = NewDbContext())
        {
            db.Personnel.Add(TestData.NewPersonnel(schoolId, "EMP-0009", rfidUid: StaffUid));
            await db.SaveChangesAsync();
        }

        var result = await RunAsync(schoolId);

        var row = Assert.Single(result.Rows);
        Assert.Equal(MigrateFromStudentsOutcome.Conflict, row.Outcome);
        Assert.Equal(MigrateFromStudentsConflict.UidHeldByOtherPersonnel, row.ConflictCode);
        Assert.Contains("EMP-0009", row.Message);

        await AssertStudentUntouchedAsync(studentId, cardId);
        await using var check = NewDbContext();
        Assert.Equal(1, await check.Personnel.CountAsync());
    }

    [Fact]
    public async Task MigrateFromStudents_leaves_past_attendance_rows_unchanged()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, cardId) = await ArrangeStudentAsync(schoolId);
        var recordId = await ArrangeAttendanceAsync(schoolId, studentId, cardId, EventStatus.Closed);

        AttendanceRecord before;
        await using (var db = NewDbContext())
            before = await db.AttendanceRecords.AsNoTracking().SingleAsync(a => a.Id == recordId);

        var result = await RunAsync(schoolId);
        Assert.Equal(MigrateFromStudentsOutcome.Create, Assert.Single(result.Rows).Outcome);

        await using var check = NewDbContext();
        var after = await check.AttendanceRecords.AsNoTracking().IgnoreQueryFilters().SingleAsync(a => a.Id == recordId);
        Assert.Equal(1, await check.AttendanceRecords.IgnoreQueryFilters().CountAsync());
        Assert.Equal(
            (before.StudentId, before.PersonnelId, before.RfidCardId, before.EventId, before.Status, before.CheckInAt, before.UpdatedAt),
            (after.StudentId, after.PersonnelId, after.RfidCardId, after.EventId, after.Status, after.CheckInAt, after.UpdatedAt));
    }

    [Fact]
    public async Task MigrateFromStudents_leaves_students_without_the_prefix_untouched()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeStudentAsync(schoolId);
        var (otherId, otherCardId) = await ArrangeStudentAsync(schoolId, "2023-0001", "04A7B8C9");

        var result = await RunAsync(schoolId);

        Assert.Equal(1, result.Matched);
        Assert.DoesNotContain(result.Rows, r => r.StudentId == otherId);
        await AssertStudentUntouchedAsync(otherId, otherCardId);
    }

    [Fact]
    public async Task MigrateFromStudents_without_personnel_migrate_is_403_and_writes_nothing()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, cardId) = await ArrangeStudentAsync(schoolId);
        Assert.DoesNotContain(EamsPermissions.PersonnelMigrate,
            EamsRoles.ReferenceData.Roles.Single(r => r.Name == EamsRoleNames.Organizer).PermissionCodes);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId, EamsRoleNames.Organizer);

        var response = await client.PostAsJsonAsync(Route, new { prefix = "7200", dryRun = false });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertStudentUntouchedAsync(studentId, cardId);
    }

    // ======================================================== service-level (JJReviewer W1/W2/W3)

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static IPersonnelMigrationService MigrateOn(EamsDbContext db, Guid schoolId, ICardUidWriteBarrier barrier) =>
        new PersonnelMigrationService(db, new TestSchoolContext { CurrentSchoolId = schoolId }, barrier,
            NullLogger<PersonnelMigrationService>.Instance);

    private async Task<MigrateFromStudentsResultDto> RunOnServiceAsync(
        Guid schoolId, ICardUidWriteBarrier? barrier = null)
    {
        await using var db = NewDbContext();
        var response = await MigrateOn(db, schoolId, barrier ?? NoCardUidWriteBarrier.Instance)
            .MigrateFromStudentsAsync(new MigrateFromStudentsRequest("7200", DryRun: false));
        Assert.Equal(PersonnelWriteOutcome.Saved, response.Outcome);
        return response.Result!;
    }

    private async Task<bool> IsDeletedAsync(Guid studentId)
    {
        await using var db = NewDbContext();
        return await db.Students.IgnoreQueryFilters().Where(s => s.Id == studentId).Select(s => s.IsDeleted).SingleAsync();
    }

    /// <summary>
    /// W1: two students that trim to the number of ONE existing personnel record with no card. The first in
    /// order fills the card; the second is held back whole (card active, student live) rather than having
    /// its card deactivated with nowhere to put the UID.
    /// </summary>
    [Fact]
    public async Task MigrateFromStudents_two_students_onto_one_existing_personnel_never_lose_a_card()
    {
        var schoolId = await ArrangeSchoolAsync();
        // The leading-space number sorts first, so it is the one that lands.
        var (firstId, _) = await ArrangeStudentAsync(schoolId, " " + StaffNumber, "AAAA0001");
        var (secondId, secondCardId) = await ArrangeStudentAsync(schoolId, StaffNumber, "BBBB0002");
        await using (var db = NewDbContext())
        {
            db.Personnel.Add(TestData.NewPersonnel(schoolId, StaffNumber));
            await db.SaveChangesAsync();
        }

        var result = await RunOnServiceAsync(schoolId);

        Assert.Equal(MigrateFromStudentsOutcome.Update, result.Rows.Single(r => r.StudentId == firstId).Outcome);
        Assert.Equal(MigrateFromStudentsConflict.ConcurrentChange,
            result.Rows.Single(r => r.StudentId == secondId).ConflictCode);

        await using var check = NewDbContext();
        Assert.Equal("AAAA0001", await check.Personnel.Select(p => p.RfidUid).SingleAsync());
        Assert.True(await check.RfidCards.Where(c => c.Id == secondCardId).Select(c => c.IsActive).SingleAsync());
        Assert.False(await IsDeletedAsync(secondId));
    }

    [Fact]
    public async Task MigrateFromStudents_matches_on_the_trimmed_number_and_files_it_trimmed()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, _) = await ArrangeStudentAsync(schoolId, "  " + StaffNumber + " ");

        var result = await RunOnServiceAsync(schoolId);

        Assert.Equal(MigrateFromStudentsOutcome.Create, Assert.Single(result.Rows).Outcome);
        await using var db = NewDbContext();
        Assert.Equal(StaffNumber, await db.Personnel.Select(p => p.PersonnelNumber).SingleAsync());
        Assert.True(await IsDeletedAsync(studentId));
    }

    [Fact]
    public async Task MigrateFromStudents_student_with_multiple_active_cards_is_a_conflict()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, cardId) = await ArrangeStudentAsync(schoolId);
        await using (var db = NewDbContext())
        {
            db.RfidCards.Add(TestData.NewCard(schoolId, studentId, "CCCC0003"));
            await db.SaveChangesAsync();
        }

        var result = await RunOnServiceAsync(schoolId);

        Assert.Equal(MigrateFromStudentsConflict.MultipleActiveCards, Assert.Single(result.Rows).ConflictCode);
        await AssertStudentUntouchedAsync(studentId, cardId);
        await using var check = NewDbContext();
        Assert.Equal(2, await check.RfidCards.CountAsync(c => c.IsActive));
        Assert.Equal(0, await check.Personnel.CountAsync());
    }

    public enum GuardLeg { DirectAudience, GroupAudience, DefinitionAudience, PreRegistration }

    /// <summary>
    /// W2: every way a non-terminal event reaches a student holds that student back. A second staff member
    /// whose Course the definition does not select still moves, so the definition leg is a resolution and
    /// not a blanket refusal.
    /// </summary>
    [Theory]
    [InlineData(GuardLeg.DirectAudience, EventStatus.Open)]
    [InlineData(GuardLeg.GroupAudience, EventStatus.Draft)]
    [InlineData(GuardLeg.DefinitionAudience, EventStatus.Open)]
    [InlineData(GuardLeg.PreRegistration, EventStatus.Open)]
    public async Task MigrateFromStudents_event_guard_covers_every_audience_leg(GuardLeg leg, string eventStatus)
    {
        var schoolId = await ArrangeSchoolAsync();
        var (studentId, cardId) = await ArrangeStudentAsync(schoolId);
        Guid bystanderId;
        await using (var db = NewDbContext())
        {
            var bystander = TestData.NewStudent(schoolId, "7200099999", course: "STAFF");
            db.Students.Add(bystander);
            await db.SaveChangesAsync();
            bystanderId = bystander.Id;
        }

        await ArrangeLegAsync(schoolId, studentId, leg, eventStatus);

        var result = await RunOnServiceAsync(schoolId);

        Assert.Equal(MigrateFromStudentsConflict.ActiveEvent,
            result.Rows.Single(r => r.StudentId == studentId).ConflictCode);
        Assert.Equal(MigrateFromStudentsOutcome.Create, result.Rows.Single(r => r.StudentId == bystanderId).Outcome);
        await AssertStudentUntouchedAsync(studentId, cardId);
    }

    private async Task ArrangeLegAsync(Guid schoolId, Guid studentId, GuardLeg leg, string eventStatus)
    {
        await using var db = NewDbContext();
        var ev = TestData.NewEvent(schoolId, status: eventStatus);
        db.Events.Add(ev);

        AudienceDefinition NewDefinition(string programme)
        {
            var classification = TestData.NewEventClassification(schoolId, $"Class {Guid.NewGuid():N}");
            db.EventClassifications.Add(classification);
            var name = $"Audience {Guid.NewGuid():N}";
            var definition = new AudienceDefinition
            {
                SchoolId = schoolId, EventClassificationId = classification.Id,
                Name = name, NameKey = AudienceText.KeyFor(name),
                AudienceType = AudienceType.Program, CriteriaJson = "{\"programs\":[\"" + programme + "\"]}",
                IsActive = true,
            };
            db.AudienceDefinitions.Add(definition);
            return definition;
        }

        switch (leg)
        {
            case GuardLeg.DirectAudience:
                db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = studentId });
                break;
            case GuardLeg.GroupAudience:
                var group = TestData.NewGroup(schoolId);
                db.StudentGroups.Add(group);
                db.StudentGroupMembers.Add(new StudentGroupMember { StudentGroupId = group.Id, StudentId = studentId });
                db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentGroupId = group.Id });
                break;
            case GuardLeg.DefinitionAudience:
                // TestData.NewStudent's Course is BSIT; the bystander's is STAFF.
                var definition = NewDefinition("BSIT");
                db.EventAudienceDefinitions.Add(new EventAudienceDefinition { EventId = ev.Id, AudienceDefinitionId = definition.Id });
                break;
            case GuardLeg.PreRegistration:
                var session = new PreRegistrationSession
                {
                    SchoolId = schoolId, AudienceDefinitionId = NewDefinition("NONE").Id,
                    Name = "Staff session", Capacity = 10, EventId = ev.Id,
                };
                db.PreRegistrationSessions.Add(session);
                db.PreRegistrations.Add(new PreRegistration
                {
                    SchoolId = schoolId, Session = session, StudentId = studentId,
                    AttendeeType = PreRegistrantType.Student, Method = PreRegistrationMethod.Manual,
                });
                break;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Throws at the seam on the given call: inside that chunk's transaction, before its save.</summary>
    private sealed class FaultOnCallBarrier(int faultingCall) : ICardUidWriteBarrier
    {
        private int _calls;

        public Task BetweenCheckAndWriteAsync(CancellationToken ct) =>
            ++_calls == faultingCall
                ? throw new InvalidOperationException("Injected fault in a later chunk.")
                : Task.CompletedTask;
    }

    /// <summary>
    /// W3: more students than one transaction holds. A fault in the second chunk leaves the first chunk
    /// committed and the second untouched; the same request again moves exactly the rest.
    /// </summary>
    [Fact]
    public async Task MigrateFromStudents_crosses_the_chunk_boundary_and_resumes_after_a_fault_in_a_later_chunk()
    {
        const int extra = 5;
        const int total = PersonnelMigrationService.StudentsPerTransaction + extra;
        var schoolId = await ArrangeSchoolAsync();
        await using (var db = NewDbContext())
        {
            for (var i = 0; i < total; i++)
            {
                var student = TestData.NewStudent(schoolId, $"72000{i:D5}");
                db.Students.Add(student);
                db.RfidCards.Add(TestData.NewCard(schoolId, student.Id, $"C{i:D9}"));
            }
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunOnServiceAsync(schoolId, new FaultOnCallBarrier(faultingCall: 2)));

        await using (var check = NewDbContext())
        {
            Assert.Equal(PersonnelMigrationService.StudentsPerTransaction, await check.Personnel.CountAsync());
            Assert.Equal(extra, await check.Students.CountAsync(s => !s.IsDeleted));
            Assert.Equal(extra, await check.RfidCards.CountAsync(c => c.IsActive));
        }

        var resumed = await RunOnServiceAsync(schoolId);

        Assert.Equal(extra, resumed.Matched);
        Assert.Equal(extra, resumed.Created);
        await using var final = NewDbContext();
        Assert.Equal(total, await final.Personnel.CountAsync(p => p.RfidUid != null));
        Assert.Equal(0, await final.Students.CountAsync(s => !s.IsDeleted));
        Assert.Equal(0, await final.RfidCards.CountAsync(c => c.IsActive));
    }

    /// <summary>Parks the migration at the seam (after its UID lock and re-check) until released.</summary>
    private sealed class HoldingBarrier : ICardUidWriteBarrier
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task BetweenCheckAndWriteAsync(CancellationToken ct)
        {
            Reached.TrySetResult();
            await _release.Task.WaitAsync(Patience, ct);
        }
    }

    /// <summary>Polls for an APPLICATION lock request in WAIT; false if <paramref name="until"/> finishes first.</summary>
    private async Task<bool> WaitUntilSomeoneWaitsOnAnAppLockAsync(Task until)
    {
        await using var probe = new SqlConnection(Sql.ConnectionString);
        await probe.OpenAsync();

        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = N'APPLICATION' " +
                "AND request_status = N'WAIT' AND resource_database_id = DB_ID();", probe);
            if ((int)(await command.ExecuteScalarAsync())! > 0) return true;
            if (until.IsCompleted) return false;
            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>
    /// W3 / ADR-015 D-98: a personnel write claiming the same UID while the migration sits between its
    /// re-check and its write must WAIT on the card-UID lock, then see the migrated personnel record as the
    /// holder. Without the migration's lock it does not wait, sees the student's still-active card and names
    /// a student, which is how this goes red.
    /// </summary>
    [Fact]
    public async Task MigrateFromStudents_holds_the_card_uid_lock_so_a_racing_claim_waits_and_sees_the_moved_card()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeStudentAsync(schoolId);
        var barrier = new HoldingBarrier();

        await using var db = NewDbContext();
        var migrate = Task.Run(() => MigrateOn(db, schoolId, barrier)
            .MigrateFromStudentsAsync(new MigrateFromStudentsRequest("7200", DryRun: false)));

        var first = await Task.WhenAny(barrier.Reached.Task, migrate).WaitAsync(Patience);
        Assert.True(first != migrate, "The migration finished without ever reaching its barrier.");

        await using var other = NewDbContext();
        var personnel = new PersonnelService(other, new TestSchoolContext { CurrentSchoolId = schoolId },
            NoCardUidWriteBarrier.Instance, NullLogger<PersonnelService>.Instance);
        var create = Task.Run(() => personnel.CreateAsync(new PersonnelWriteRequest(
            "EMP-0100", "Ana", null, "Cruz", null, null, null, null, null, StaffUid, null)));

        var waited = await WaitUntilSomeoneWaitsOnAnAppLockAsync(create);
        barrier.Release();

        var migrated = await migrate.WaitAsync(Patience);
        var created = await create.WaitAsync(Patience);

        Assert.True(waited, "The racing personnel write never waited on the card-UID lock.");
        Assert.Equal(MigrateFromStudentsOutcome.Create, Assert.Single(migrated.Result!.Rows).Outcome);
        Assert.Equal(PersonnelWriteOutcome.DuplicateRfid, created.Outcome);
        Assert.Equal(AttendeePersonType.Personnel, created.Holder!.PersonType);
        Assert.Equal(StaffNumber, created.Holder.Number);
    }
}
