using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Proves the single additive migration of ADR-011 D-79 — <c>StudentOrganization</c> — adds the nullable
/// <c>Students.Organization nvarchar(150)</c> column to a database that already holds students, changes
/// nothing that was there, and reverses and re-applies cleanly: up, down, up.
///
/// <para>
/// <b>Students are seeded with raw SQL, not the head model.</b> This migration adds a <em>column</em> to
/// <c>Students</c>, so writing a student through the head <c>DbContext</c> at the prior revision would name
/// the missing <c>Organization</c> column and fail — the trap
/// <see cref="PreRegistrationEventLinkMigrationTests"/> records for column migrations. The insert names only
/// columns that exist at the prior revision.
/// </para>
///
/// <para>
/// <c>Down</c> drops the column and so loses any organization written since <c>Up</c>. That is JJ's
/// explicit, recorded exception to the no-DROP rule (ADR-011 D-79): the SIS file is the source and a
/// re-import restores the values. These tests therefore assert the column's shape and that no OTHER data
/// moves; they deliberately do not assert that an organization survives a rollback.
/// </para>
///
/// <para>Its own scratch database, because rolling the shared one back would break every other test.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentOrganizationMigrationTests : IntegrationTest
{
    public StudentOrganizationMigrationTests(SqlServerFixture sql) : base(sql) { }

    private const string PriorMigration = "PreRegistrationEventLink";
    private const string Table = "Students";
    private const string Column = "Organization";

    /// <summary>The Students columns that exist at the prior revision, in a fixed order.</summary>
    private const string PriorColumns =
        "Id, SchoolId, StudentNumber, FirstName, MiddleName, LastName, Email, AlternateEmail, Course, " +
        "YearLevel, Section, Status, IsDeleted, SisExternalId, Gender, PhotoUrl, CreatedAt, UpdatedAt, " +
        "LastSyncedAt, AcademicCacheUpdatedAt";

    [Fact]
    public async Task The_organization_column_applies_reverses_and_reapplies_on_a_populated_database()
    {
        var databaseName = $"EAMS_StudentOrg_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await MigrateToAsync(connectionString, PriorMigration);
            Assert.True(await TableExistsAsync(connectionString, Table)); // the table is older
            Assert.False(await ColumnExistsAsync(connectionString, Table, Column));

            await SeedStudentsAsync(connectionString);
            Assert.Equal(3, await CountStudentsAsync(connectionString));

            // ---- UP: a nullable nvarchar(150), and the students are still there.
            await MigrateToHeadAsync(connectionString);
            await AssertOrganizationColumnShapeAsync(connectionString);
            Assert.Equal(3, await CountStudentsAsync(connectionString));

            // ---- usable: a value fits to the limit; one past it is refused by the column, not truncated.
            await SetOrganizationAsync(connectionString, "MIG-0001", new string('A', 150));
            await Assert.ThrowsAsync<SqlException>(() =>
                SetOrganizationAsync(connectionString, "MIG-0001", new string('A', 151)));

            // ---- DOWN: the column goes (D-79's accepted data-losing rollback); every student stays.
            await MigrateToAsync(connectionString, PriorMigration);
            Assert.False(await ColumnExistsAsync(connectionString, Table, Column));
            Assert.True(await TableExistsAsync(connectionString, Table));
            Assert.Equal(3, await CountStudentsAsync(connectionString));

            // ---- UP again: the same shape comes back.
            await MigrateToHeadAsync(connectionString);
            await AssertOrganizationColumnShapeAsync(connectionString);
            Assert.Equal(3, await CountStudentsAsync(connectionString));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Up_leaves_every_existing_student_row_unchanged()
    {
        var databaseName = $"EAMS_StudentOrgRows_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await MigrateToAsync(connectionString, PriorMigration);
            await SeedStudentsAsync(connectionString);

            var before = await ReadPriorColumnsAsync(connectionString);
            Assert.Equal(3, before.Count);                          // the seed really wrote three rows
            Assert.Contains(before, r => r.Contains("MIG-0003"));   // including the soft-deleted one

            await MigrateToHeadAsync(connectionString);

            // Every pre-existing column of every row (soft-deleted included) is byte-for-byte what it was,
            // and the new column is NULL on all of them: no backfill, no default.
            Assert.Equal(before, await ReadPriorColumnsAsync(connectionString));
            Assert.Equal(3, await ScalarAsync<int>(connectionString,
                "SELECT COUNT(*) FROM Students WHERE Organization IS NULL;"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task MigrateToAsync(string connectionString, string migration)
    {
        await using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    private static async Task MigrateToHeadAsync(string connectionString)
    {
        await using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// A school through the head model (its columns predate this migration) and three students through raw
    /// SQL at the prior revision: one fully populated, one sparse, one soft-deleted.
    /// </summary>
    private static async Task SeedStudentsAsync(string connectionString)
    {
        Guid schoolId;
        await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO Students (Id, SchoolId, StudentNumber, FirstName, MiddleName, LastName, Email,
                AlternateEmail, Course, YearLevel, Section, Status, IsDeleted, SisExternalId, Gender,
                PhotoUrl, CreatedAt, UpdatedAt, LastSyncedAt, AcademicCacheUpdatedAt)
            VALUES
              (NEWID(), @school, 'MIG-0001', 'Maria', 'Reyes', 'Santos', 'maria@usa.edu.ph',
               'maria.personal@example.com', 'BSIT', '3rd Year', 'A', 'Active', 0, 'EXT-1', 'F',
               'https://example.test/p.png', '2026-01-02T03:04:05', '2026-02-03T04:05:06',
               '2026-03-04T05:06:07', '2026-04-05T06:07:08'),
              (NEWID(), @school, 'MIG-0002', 'Juan', NULL, 'Cruz', NULL,
               NULL, NULL, NULL, NULL, 'Inactive', 0, NULL, NULL,
               NULL, '2026-01-02T03:04:05', '2026-01-02T03:04:05', NULL, NULL),
              (NEWID(), @school, 'MIG-0003', 'Ana', 'E.', 'Reyes', 'ana@usa.edu.ph',
               NULL, 'BSCS', '1st Year', 'B', 'Active', 1, NULL, NULL,
               NULL, '2026-01-02T03:04:05', '2026-05-06T07:08:09', NULL, NULL);
            """, connection);
        command.Parameters.AddWithValue("@school", schoolId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadPriorColumnsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT {PriorColumns} FROM Students ORDER BY StudentNumber;", connection);

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = reader.IsDBNull(i)
                    ? "<null>"
                    : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            rows.Add(string.Join("|", cells));
        }

        return rows;
    }

    private static async Task SetOrganizationAsync(string connectionString, string studentNumber, string value)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE Students SET Organization = @org WHERE StudentNumber = @no;", connection);
        command.Parameters.AddWithValue("@org", value);
        command.Parameters.AddWithValue("@no", studentNumber);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertOrganizationColumnShapeAsync(string connectionString)
    {
        Assert.True(await ColumnExistsAsync(connectionString, Table, Column));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = 'Students' AND COLUMN_NAME = 'Organization';
            """, connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("nvarchar", reader.GetString(0));
        Assert.Equal(150, reader.GetInt32(1));
        Assert.Equal("YES", reader.GetString(2));
    }

    private static Task<int> CountStudentsAsync(string connectionString) =>
        ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM Students;");

    private static async Task<T> ScalarAsync<T>(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string tableName) =>
        await ScalarAsync<int>(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = @name;", ("@name", tableName)) > 0;

    private static async Task<bool> ColumnExistsAsync(
        string connectionString, string tableName, string columnName) =>
        await ScalarAsync<int>(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE name = @col AND object_id = OBJECT_ID(@tbl);",
            ("@col", columnName), ("@tbl", tableName)) > 0;
}
