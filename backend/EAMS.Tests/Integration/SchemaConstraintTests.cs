using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The guards that live in the schema rather than in C#, asserted the only way that means anything:
/// by attempting the violation against real SQL Server and requiring it to be rejected.
///
/// <para>
/// Each one is paired with an assertion on the index metadata itself. Behaviour alone is not enough
/// here — <c>UX_Attendance_Event_Student_Occurrence</c> once shipped with the SQL Server provider's
/// automatic <c>WHERE [OccurrenceId] IS NOT NULL</c> attached, which left every non-recurring
/// attendance row (that is, all of them) unconstrained. The behavioural test would have caught that
/// today; the metadata test says <em>why</em> it broke, and catches the same mistake on the day
/// occurrences start being populated and the behavioural test stops covering the NULL case.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SchemaConstraintTests : IntegrationTest
{
    public SchemaConstraintTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// 2601 is a unique <em>index</em>, 2627 a unique <em>constraint</em>. Every guard below is
    /// expressed as an index, so 2601 is what fires today — but whether a guard is an index or a
    /// constraint is a schema detail, and a test that pinned one number would fail on a
    /// behaviourally identical schema. The production code makes the same allowance.
    /// </summary>
    private static readonly int[] UniqueViolation = [2601, 2627];

    private static readonly int[] ConstraintViolation = [547]; // CHECK and FOREIGN KEY share this.

    private static SqlException AssertSqlError(DbUpdateException ex, int[] expectedNumbers)
    {
        var sql = Assert.IsType<SqlException>(ex.InnerException);
        Assert.Contains(sql.Number, expectedNumbers);
        return sql;
    }

    private static async Task<DbUpdateException> AssertRejectedAsync(Func<Task> act) =>
        await Assert.ThrowsAsync<DbUpdateException>(act);

    private sealed record IndexInfo(bool IsUnique, bool HasFilter, string? FilterDefinition);

    private async Task<IndexInfo> ReadIndexAsync(string indexName)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT is_unique, has_filter, filter_definition FROM sys.indexes WHERE name = @name;",
            connection);
        command.Parameters.AddWithValue("@name", indexName);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Index {indexName} does not exist in the migrated schema.");

        return new IndexInfo(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private async Task<(Guid SchoolId, Guid StudentId, Guid EventId)> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        var ev = TestData.NewEvent(school.Id);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return (school.Id, student.Id, ev.Id);
    }

    // ------------------------------- UX_Attendance_Event_Student_Occurrence

    /// <summary>
    /// The regression guard for the shipped-filtered bug. A filter of <c>[OccurrenceId] IS NOT NULL</c>
    /// here would constrain nothing at all today, because every attendance row in the core slice has
    /// a NULL <c>OccurrenceId</c> — the index would look present in every schema diff and enforce
    /// nothing.
    /// </summary>
    [Fact]
    public async Task The_event_student_occurrence_index_is_unique_and_unfiltered()
    {
        var index = await ReadIndexAsync("UX_Attendance_Event_Student_Occurrence");

        Assert.True(index.IsUnique);
        Assert.False(
            index.HasFilter,
            "UX_Attendance_Event_Student_Occurrence is filtered on " + index.FilterDefinition +
            ". Every core-slice attendance row has a NULL OccurrenceId, so a filter over that " +
            "column leaves the whole table unconstrained. HasFilter(null) in EamsDbContext is what " +
            "suppresses the SQL Server provider's automatic IS NOT NULL — it is load-bearing.");
        Assert.Null(index.FilterDefinition);
    }

    /// <summary>
    /// The behaviour the index exists for, exercised at the value that would escape a filtered
    /// version: <c>OccurrenceId</c> NULL on both rows. SQL Server compares NULL as equal inside a
    /// unique index, so the two rows collide — that is the property §4.9's "one attendance per
    /// student per event" depends on for every non-recurring event.
    /// </summary>
    [Fact]
    public async Task A_duplicate_attendance_row_with_a_null_occurrence_is_rejected()
    {
        var (schoolId, studentId, eventId) = await ArrangeAsync();

        await using var db = NewDbContext();
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = schoolId,
            EventId = eventId, StudentId = studentId, OccurrenceId = null,
            CheckInAt = TestData.Now, Status = "Present",
        });
        await db.SaveChangesAsync();

        await using var second = NewDbContext();
        second.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = schoolId,
            EventId = eventId, StudentId = studentId, OccurrenceId = null,
            CheckInAt = TestData.Now.AddMinutes(1), Status = "Late",
        });

        var rejected = await AssertRejectedAsync(() => second.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_Attendance_Event_Student_Occurrence", sql.Message);
    }

    [Fact]
    public async Task Two_students_may_both_attend_the_same_event()
    {
        var (schoolId, studentId, eventId) = await ArrangeAsync();

        await using var db = NewDbContext();
        var other = TestData.NewStudent(schoolId, "2023-0006", lastName: "Flores");
        db.Students.Add(other);
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = schoolId,
            EventId = eventId, StudentId = studentId, CheckInAt = TestData.Now, Status = "Present",
        });
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = schoolId,
            EventId = eventId, StudentId = other.Id, CheckInAt = TestData.Now, Status = "Present",
        });

        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(2, await read.AttendanceRecords.CountAsync());
    }

    // ------------------------------- UX_Attendance_Device_DeviceTapId

    /// <summary>
    /// This one <em>is</em> filtered, and correctly so: most rows carry no tap id, and without the
    /// filter SQL Server's NULL equality would let only a single tap-id-less row exist in the whole
    /// table. Asserted so the two indexes' opposite filter decisions stay deliberate rather than
    /// becoming a copy-paste of each other.
    /// </summary>
    [Fact]
    public async Task The_device_tap_index_is_unique_and_filtered_to_rows_that_carry_a_tap_id()
    {
        var index = await ReadIndexAsync("UX_Attendance_Device_DeviceTapId");

        Assert.True(index.IsUnique);
        Assert.True(index.HasFilter);
        Assert.Contains("DeviceTapId", index.FilterDefinition!);
        Assert.Contains("IS NOT NULL", index.FilterDefinition!);
    }

    [Fact]
    public async Task Many_attendance_rows_may_have_no_tap_id_at_all()
    {
        var (schoolId, studentId, eventId) = await ArrangeAsync();

        await using var db = NewDbContext();
        var second = TestData.NewStudent(schoolId, "2023-0006", lastName: "Flores");
        var third = TestData.NewStudent(schoolId, "2023-0007", lastName: "Aquino");
        db.Students.AddRange(second, third);
        foreach (var id in new[] { studentId, second.Id, third.Id })
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = schoolId,
                EventId = eventId, StudentId = id, DeviceTapId = null,
                CheckInAt = TestData.Now, Status = "Present",
            });

        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(3, await read.AttendanceRecords.CountAsync());
    }

    // ------------------------------- UX_RfidCards_SchoolId_CardUid_Active

    [Fact]
    public async Task The_card_index_is_unique_and_filtered_to_active_cards()
    {
        var index = await ReadIndexAsync("UX_RfidCards_SchoolId_CardUid_Active");

        Assert.True(index.IsUnique);
        Assert.True(index.HasFilter);
        Assert.Contains("IsActive", index.FilterDefinition!);
    }

    /// <summary>
    /// ADR-001 D-3's whole reason for existing: the registrar reissues a lost ID carrying the
    /// <em>same</em> REGNO. Under §4.4's literal global <c>UNIQUE(CardUid)</c> this sequence is
    /// impossible without destroying the original row — and with it the ability to say which
    /// physical card produced a past tap.
    /// </summary>
    [Fact]
    public async Task A_card_can_be_deactivated_and_the_same_uid_reissued()
    {
        var (schoolId, studentId, _) = await ArrangeAsync();
        const string regno = "USA00962";

        await using (var db = NewDbContext())
        {
            db.RfidCards.Add(TestData.NewCard(schoolId, studentId, regno));
            await db.SaveChangesAsync();
        }

        await using (var db = NewDbContext())
        {
            var original = await db.RfidCards.SingleAsync();
            original.IsActive = false;
            original.DeactivatedAt = TestData.Now;
            db.RfidCards.Add(TestData.NewCard(schoolId, studentId, regno));
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var cards = await read.RfidCards.AsNoTracking().ToListAsync();
        Assert.Equal(2, cards.Count);
        Assert.Single(cards, c => c.IsActive);
        Assert.All(cards, c => Assert.Equal(regno, c.CardUid));
    }

    [Fact]
    public async Task A_second_active_card_with_the_same_uid_is_rejected()
    {
        var (schoolId, studentId, _) = await ArrangeAsync();
        const string regno = "USA00962";

        await using (var db = NewDbContext())
        {
            db.RfidCards.Add(TestData.NewCard(schoolId, studentId, regno));
            await db.SaveChangesAsync();
        }

        await using var second = NewDbContext();
        second.RfidCards.Add(TestData.NewCard(schoolId, studentId, regno));

        var rejected = await AssertRejectedAsync(() => second.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_RfidCards_SchoolId_CardUid_Active", sql.Message);
    }

    /// <summary>
    /// The negative case that keeps the filter honest: two <em>inactive</em> rows with one UID are a
    /// normal issuance history (lost twice), so the index must not reject them.
    /// </summary>
    [Fact]
    public async Task Several_inactive_cards_may_share_a_uid()
    {
        var (schoolId, studentId, _) = await ArrangeAsync();
        const string regno = "USA00962";

        await using var db = NewDbContext();
        db.RfidCards.Add(TestData.NewCard(schoolId, studentId, regno, isActive: false));
        db.RfidCards.Add(TestData.NewCard(schoolId, studentId, regno, isActive: false));
        db.RfidCards.Add(TestData.NewCard(schoolId, studentId, regno));
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(3, await read.RfidCards.CountAsync());
    }

    /// <summary>
    /// The index is tenant-scoped (ADR-001 D-3), matching <c>UNIQUE(SchoolId, StudentNumber)</c> on
    /// Students. Two schools issuing the same REGNO must not collide — otherwise the first tenant to
    /// use a number would lock every other tenant out of it.
    /// </summary>
    [Fact]
    public async Task The_same_active_uid_is_allowed_in_a_different_school()
    {
        var (schoolId, studentId, _) = await ArrangeAsync();
        const string regno = "USA00962";

        await using var db = NewDbContext();
        var otherSchool = TestData.NewSchool("CICSS");
        db.Schools.Add(otherSchool);
        var otherStudent = TestData.NewStudent(otherSchool.Id, "2023-0006", lastName: "Flores");
        db.Students.Add(otherStudent);

        db.RfidCards.Add(TestData.NewCard(schoolId, studentId, regno));
        db.RfidCards.Add(TestData.NewCard(otherSchool.Id, otherStudent.Id, regno));
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(2, await read.RfidCards.CountAsync(c => c.CardUid == regno && c.IsActive));
    }

    // ------------------------------- CK_EventGroups_GroupOrStudent

    /// <summary>
    /// §4.8's XOR: an <c>EventGroup</c> row targets a group or an individual student, never both and
    /// never neither. "Neither" is the case a nullable-columns-only schema would silently accept —
    /// a row that invites nobody, which reads as a configured audience right up until nobody can tap.
    /// </summary>
    [Fact]
    public async Task An_event_group_row_naming_both_a_group_and_a_student_is_rejected()
    {
        var (schoolId, studentId, eventId) = await ArrangeAsync();

        await using var db = NewDbContext();
        var group = TestData.NewGroup(schoolId);
        db.StudentGroups.Add(group);
        await db.SaveChangesAsync();

        db.EventGroups.Add(new EventGroup
        {
            EventId = eventId, StudentGroupId = group.Id, StudentId = studentId,
        });

        var rejected = await AssertRejectedAsync(() => db.SaveChangesAsync());
        var sql = AssertSqlError(rejected, ConstraintViolation);
        Assert.Contains("CK_EventGroups_GroupOrStudent", sql.Message);
    }

    [Fact]
    public async Task An_event_group_row_naming_neither_a_group_nor_a_student_is_rejected()
    {
        var (_, _, eventId) = await ArrangeAsync();

        await using var db = NewDbContext();
        db.EventGroups.Add(new EventGroup
        {
            EventId = eventId, StudentGroupId = null, StudentId = null,
        });

        var rejected = await AssertRejectedAsync(() => db.SaveChangesAsync());
        var sql = AssertSqlError(rejected, ConstraintViolation);
        Assert.Contains("CK_EventGroups_GroupOrStudent", sql.Message);
    }

    [Fact]
    public async Task An_event_group_row_naming_exactly_one_target_is_accepted()
    {
        var (schoolId, studentId, eventId) = await ArrangeAsync();

        await using var db = NewDbContext();
        var group = TestData.NewGroup(schoolId);
        db.StudentGroups.Add(group);
        await db.SaveChangesAsync();

        db.EventGroups.Add(new EventGroup { EventId = eventId, StudentGroupId = group.Id });
        db.EventGroups.Add(new EventGroup { EventId = eventId, StudentId = studentId });
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(2, await read.EventGroups.CountAsync());
    }

    // ------------------------------- other §4 guards worth pinning

    /// <summary>
    /// §4.13: a NULL <c>SchoolId</c> on <c>SystemSettings</c> is the <em>global</em> scope, and SQL
    /// Server's NULL equality is what keeps it to one row per key. The same <c>HasFilter(null)</c>
    /// call as the attendance index is what makes this hold, so it regresses the same way.
    /// </summary>
    [Fact]
    public async Task A_duplicate_global_system_setting_key_is_rejected()
    {
        await using (var db = NewDbContext())
        {
            db.SystemSettings.Add(new SystemSetting { SchoolId = null, Key = "grace.minutes", Value = "15" });
            await db.SaveChangesAsync();
        }

        await using var second = NewDbContext();
        second.SystemSettings.Add(new SystemSetting { SchoolId = null, Key = "grace.minutes", Value = "20" });

        var rejected = await AssertRejectedAsync(() => second.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_SystemSettings_SchoolId_Key", sql.Message);
    }

    [Fact]
    public async Task Two_students_in_one_school_may_not_share_a_student_number()
    {
        var (schoolId, _, _) = await ArrangeAsync();

        await using var db = NewDbContext();
        db.Students.Add(TestData.NewStudent(schoolId, "2023-0001", lastName: "Flores"));

        var rejected = await AssertRejectedAsync(() => db.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("IX_Students_SchoolId_StudentNumber", sql.Message);
    }

    /// <summary>
    /// Hard deletes are restricted everywhere (no cascade), so an attendance trail cannot vanish
    /// because a parent row was removed. §4 uses <c>IsDeleted</c>; this is the guard that makes the
    /// soft-delete convention non-optional.
    /// </summary>
    [Fact]
    public async Task Deleting_an_event_that_has_attendance_is_refused_rather_than_cascaded()
    {
        var (schoolId, studentId, eventId) = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = schoolId,
                EventId = eventId, StudentId = studentId, CheckInAt = TestData.Now, Status = "Present",
            });
            await db.SaveChangesAsync();
        }

        await using var remove = NewDbContext();
        remove.Events.Remove(await remove.Events.SingleAsync());

        var rejected = await AssertRejectedAsync(() => remove.SaveChangesAsync());
        AssertSqlError(rejected, ConstraintViolation);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.AttendanceRecords.CountAsync());
    }

    [Fact]
    public async Task The_migration_history_records_the_section_four_baseline()
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%Section4Baseline';",
            connection);

        Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
    }
}
