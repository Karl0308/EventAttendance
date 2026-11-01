using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>ADR-015 D-96's <c>PersonnelAttendance</c> migration: its guarded <c>Down</c>.</b> The <c>Up</c>
/// shape is pinned by <see cref="SchemaConstraintTests"/> against the shared database; these tests own
/// the rollback, on scratch databases, because rolling the shared one back would break every other test.
///
/// <para>
/// <b>Why the guard is the test that matters.</b> <c>Down</c>'s D3 drops <c>AttendanceRecords.PersonnelId</c>
/// and the <c>EventPersonnel</c> table — JJ's approved exception to the no-DROP rule, legal only because
/// D1 first proves both are empty. Without D1 a rollback would delete personnel attendance silently, so a
/// Down that does not throw on one personnel row is a data-loss migration.
/// </para>
///
/// <para>
/// Rows are written at head through the head model (both columns exist there) and every assertion after a
/// rollback reads with raw SQL, never through the EF model, which no longer matches the schema.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelAttendanceMigrationTests : IntegrationTest
{
    public PersonnelAttendanceMigrationTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>The migration immediately before <c>PersonnelAttendance</c>.</summary>
    private const string PriorMigration = "StudentOrganization";

    private const string MigrationId = "20261027150707_PersonnelAttendance";

    /// <summary>D1's refusal. ADR-015 D-96 fixes the number.</summary>
    private const int DownRefused = 50015;

    private static async Task MigrateToAsync(string connectionString, string? target)
    {
        await using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<int> CountAsync(string connectionString, string sql) =>
        (int)(await ScalarAsync(connectionString, sql))!;

    private static Task<int> HistoryRowsAsync(string connectionString) =>
        CountAsync(connectionString,
            $"SELECT COUNT(*) FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'{MigrationId}';");

    private static Task<int> PersonnelColumnsAsync(string connectionString) =>
        CountAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns " +
            "WHERE object_id = OBJECT_ID(N'dbo.AttendanceRecords') AND name = N'PersonnelId';");

    private static Task<int> EventPersonnelTablesAsync(string connectionString) =>
        CountAsync(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = N'EventPersonnel';");

    private sealed record World(Guid SchoolId, Guid StudentId, Guid PersonnelId, Guid EventId);

    /// <summary>A migrated-to-head scratch database holding a school, a student, a person and an event.</summary>
    private async Task<(string ConnectionString, World World)> ArrangeAsync(string databaseName)
    {
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);
        await MigrateToAsync(connectionString, null);

        await using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        var school = TestData.NewSchool();
        var student = TestData.NewStudent(school.Id);
        var person = TestData.NewPersonnel(school.Id);
        var ev = TestData.NewEvent(school.Id);
        db.Schools.Add(school);
        db.Students.Add(student);
        db.Personnel.Add(person);
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        return (connectionString, new World(school.Id, student.Id, person.Id, ev.Id));
    }

    private static async Task AddAttendanceAsync(string connectionString, World w, bool personnel)
    {
        await using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = w.SchoolId,
            EventId = w.EventId,
            StudentId = personnel ? null : w.StudentId,
            PersonnelId = personnel ? w.PersonnelId : null,
            CheckInAt = TestData.Now,
            Status = AttendanceStatus.Present,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Migration_down_throws_while_one_personnel_attendance_row_exists()
    {
        var databaseName = $"EAMS_PersAtt_{Guid.NewGuid():N}";
        var (connectionString, world) = await ArrangeAsync(databaseName);

        try
        {
            await AddAttendanceAsync(connectionString, world, personnel: true);

            var refusal = await Assert.ThrowsAsync<SqlException>(() => MigrateToAsync(connectionString, PriorMigration));
            Assert.Equal(DownRefused, refusal.Number);

            // Nothing moved: the migration's transaction rolled the whole Down back, history row included.
            Assert.Equal(1, await HistoryRowsAsync(connectionString));
            Assert.Equal(1, await PersonnelColumnsAsync(connectionString));
            Assert.Equal(1, await EventPersonnelTablesAsync(connectionString));
            Assert.Equal(1, await CountAsync(connectionString,
                $"SELECT COUNT(*) FROM dbo.AttendanceRecords WHERE PersonnelId = '{world.PersonnelId}';"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    /// <summary>D1's second half: a frozen <c>EventPersonnel</c> row refuses the rollback as well.</summary>
    [Fact]
    public async Task Migration_down_throws_while_one_frozen_event_personnel_row_exists()
    {
        var databaseName = $"EAMS_PersAtt_{Guid.NewGuid():N}";
        var (connectionString, world) = await ArrangeAsync(databaseName);

        try
        {
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                db.EventPersonnel.Add(new EventPersonnel { EventId = world.EventId, PersonnelId = world.PersonnelId });
                await db.SaveChangesAsync();
            }

            var refusal = await Assert.ThrowsAsync<SqlException>(() => MigrateToAsync(connectionString, PriorMigration));
            Assert.Equal(DownRefused, refusal.Number);

            Assert.Equal(1, await HistoryRowsAsync(connectionString));
            Assert.Equal(1, await CountAsync(connectionString, "SELECT COUNT(*) FROM dbo.EventPersonnel;"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// With only student attendance present, <c>Down</c> restores the pre-ADR-015 shape exactly — the
    /// unfiltered unique index, a NOT NULL <c>StudentId</c>, no <c>PersonnelId</c>, no snapshot table —
    /// keeps the student row, and <c>Up</c> re-applies cleanly over it.
    /// </summary>
    [Fact]
    public async Task Migration_down_with_only_student_rows_restores_the_prior_shape_and_up_reapplies()
    {
        var databaseName = $"EAMS_PersAtt_{Guid.NewGuid():N}";
        var (connectionString, world) = await ArrangeAsync(databaseName);

        try
        {
            await AddAttendanceAsync(connectionString, world, personnel: false);

            await MigrateToAsync(connectionString, PriorMigration);

            Assert.Equal(0, await HistoryRowsAsync(connectionString));
            Assert.Equal(0, await PersonnelColumnsAsync(connectionString));
            Assert.Equal(0, await EventPersonnelTablesAsync(connectionString));
            Assert.Equal(0, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'CK_AttendanceRecords_StudentOrPersonnel';"));
            Assert.Equal(false, await ScalarAsync(connectionString,
                "SELECT is_nullable FROM sys.columns " +
                "WHERE object_id = OBJECT_ID(N'dbo.AttendanceRecords') AND name = N'StudentId';"));
            Assert.Equal(1, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_Attendance_Event_Student_Occurrence' " +
                "AND is_unique = 1 AND has_filter = 0;"));
            Assert.Equal(1, await CountAsync(connectionString,
                $"SELECT COUNT(*) FROM dbo.AttendanceRecords WHERE StudentId = '{world.StudentId}';"));

            await MigrateToAsync(connectionString, null);

            Assert.Equal(1, await HistoryRowsAsync(connectionString));
            Assert.Equal("([StudentId] IS NOT NULL)", await ScalarAsync(connectionString,
                "SELECT filter_definition FROM sys.indexes WHERE name = N'UX_Attendance_Event_Student_Occurrence';"));
            Assert.Equal(1, await CountAsync(connectionString,
                $"SELECT COUNT(*) FROM dbo.AttendanceRecords WHERE StudentId = '{world.StudentId}';"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }
}
