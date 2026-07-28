using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Proves the <c>AcademicLayer</c> migration is <b>additive</b> in the only sense that matters: applied
/// to a §4 database that already holds rows, it adds the academic layer and changes nothing that was
/// there.
///
/// <para>
/// <b>Why the rest of the suite does not already prove this.</b> Every other test runs against a
/// database migrated to head in one go, on an empty schema. That exercises the SQL but says nothing
/// about the case that actually happens in the field — the developer's <c>EAMS</c> database and, later,
/// the deployed one, both sitting at <c>Section4Baseline</c> with real rows in them. A migration that
/// quietly drops a column or rewrites a type passes a create-from-scratch run and destroys a populated
/// one.
/// </para>
///
/// <para>
/// <b>Why the fixture rows are raw SQL and not <c>SeedData</c>.</b> The EF model always describes the
/// <em>head</em> schema, so seeding through it against a database stopped at migration #1 fails on the
/// first column migration #2 adds — which is what happened when this test was first written. Raw
/// INSERTs are not a workaround for that; they are more faithful to what is being tested. These rows
/// are written exactly as the old schema would have written them, by something that has never heard of
/// the academic layer, which is precisely the population the migration has to survive.
/// </para>
///
/// <para>
/// It runs on its own scratch database because rolling the shared one back would break every other
/// test in the collection.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AcademicLayerMigrationTests : IntegrationTest
{
    public AcademicLayerMigrationTests(SqlServerFixture sql) : base(sql) { }

    private const string BaselineMigration = "Section4Baseline";
    private const string AcademicLayerMigration = "AcademicLayer";

    /// <summary>
    /// A §4-shaped population written the way the pre-academic-layer application wrote it. Deliberately
    /// includes a <c>StudentGroup</c> and a membership: those two tables gain NOT NULL columns in
    /// migration #2, so they are where a backfill can go wrong.
    /// </summary>
    private const string Section4Population = """
        DECLARE @school     UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111111';
        DECLARE @santos     UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222221';
        DECLARE @flores     UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222222';
        DECLARE @aquino     UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222223';
        DECLARE @event      UNIQUEIDENTIFIER = '33333333-3333-3333-3333-333333333331';
        DECLARE @pastEvent  UNIQUEIDENTIFIER = '33333333-3333-3333-3333-333333333332';
        DECLARE @group      UNIQUEIDENTIFIER = '44444444-4444-4444-4444-444444444441';
        DECLARE @now        DATETIME2 = SYSUTCDATETIME();

        INSERT INTO Schools (Id, Name, Code, TimeZone, IsActive, CreatedAt, UpdatedAt)
        VALUES (@school, N'University of San Agustin', N'USA', N'Asia/Manila', 1, @now, @now);

        INSERT INTO Students
            (Id, SchoolId, StudentNumber, FirstName, MiddleName, LastName, Email,
             Course, YearLevel, Section, Gender, Status, SisExternalId, IsDeleted, CreatedAt, UpdatedAt)
        VALUES
            (@santos, @school, N'2023-0001', N'Maria', N'Reyes', N'Santos', N'2023-0001@usa.edu.ph',
             N'BSIT', N'3rd Year', N'A', N'Female', N'Active', NULL, 0, @now, @now),
            (@flores, @school, N'2023-0006', N'Gabriel', NULL, N'Flores', N'2023-0006@usa.edu.ph',
             N'BSA', N'4th Year', N'A', N'Male', N'Active', NULL, 0, @now, @now),
            (@aquino, @school, N'2023-0007', N'Isabella', N'Marie', N'Aquino', N'2023-0007@usa.edu.ph',
             N'BSN', N'2nd Year', N'B', N'Female', N'Active', NULL, 0, @now, @now);

        INSERT INTO RfidCards (Id, SchoolId, StudentId, CardUid, Label, IsActive, IssuedAt, CreatedAt, UpdatedAt)
        VALUES
            (NEWID(), @school, @santos, N'04A1B2C3', N'Primary ID', 1, @now, @now, @now),
            (NEWID(), @school, @flores, N'04F7081A', N'Primary ID', 1, @now, @now, @now),
            (NEWID(), @school, @aquino, N'041B2C3D', N'Primary ID', 1, @now, @now, @now);

        INSERT INTO Events
            (Id, SchoolId, Name, Location, StartAt, EndAt, AttendanceMode, GraceMinutes,
             RequireRegistration, Status, IsDeleted, CreatedAt, UpdatedAt)
        VALUES
            (@event, @school, N'University Convocation 2026', N'USA Gymnasium',
             @now, DATEADD(HOUR, 2, @now), N'Single', 15, 0, N'Open', 0, @now, @now),
            (@pastEvent, @school, N'IT Week Seminar', N'AVR 2',
             DATEADD(DAY, -3, @now), DATEADD(DAY, -3, @now), N'Single', 10, 0, N'Closed', 0, @now, @now);

        INSERT INTO AttendanceRecords
            (Id, EventId, StudentId, CheckInAt, Status, CaptureMethod, DeviceTapId, CreatedAt, UpdatedAt)
        VALUES
            (NEWID(), @event, @santos, @now, N'Present', N'Rfid', N'seed-tap-1', @now, @now),
            (NEWID(), @event, @flores, @now, N'Late', N'Rfid', N'seed-tap-2', @now, @now);

        INSERT INTO StudentGroups (Id, SchoolId, Name, Type, CreatedAt, UpdatedAt)
        VALUES (@group, @school, N'SSC Officers', N'Org', @now, @now);

        INSERT INTO StudentGroupMembers (Id, StudentGroupId, StudentId)
        VALUES (NEWID(), @group, @flores);
        """;

    private static readonly Guid ManualGroupId = new("44444444-4444-4444-4444-444444444441");

    private sealed record Census(int Schools, int Students, int Cards, int Events, int Attendance, int Groups);

    private static async Task<Census> CountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM Schools),
                (SELECT COUNT(*) FROM Students),
                (SELECT COUNT(*) FROM RfidCards),
                (SELECT COUNT(*) FROM Events),
                (SELECT COUNT(*) FROM AttendanceRecords),
                (SELECT COUNT(*) FROM StudentGroups);
            """, connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Census(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
    }

    private static async Task<string> ArrangePopulatedBaselineAsync(SqlServerFixture sql, string databaseName)
    {
        var connectionString = await sql.CreateScratchDatabaseAsync(databaseName);

        await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            await db.GetService<IMigrator>().MigrateAsync(BaselineMigration);

        await ExecuteAsync(connectionString, Section4Population);
        return connectionString;
    }

    // ------------------------------------------------------------------ the additive proof

    [Fact]
    public async Task The_academic_layer_applies_on_top_of_a_populated_section_four_database()
    {
        var databaseName = $"EAMS_Migrate_{Guid.NewGuid():N}";
        var connectionString = await ArrangePopulatedBaselineAsync(Sql, databaseName);

        try
        {
            var before = await CountAsync(connectionString);
            Assert.Equal(new Census(1, 3, 3, 2, 2, 1), before);
            Assert.Equal(1, await AppliedMigrationCountAsync(connectionString));
            Assert.False(await TableExistsAsync(connectionString, "Enrollments"));

            // ---- the academic layer, applied to that populated database.
            //
            // Migrated to head rather than stopped at AcademicLayer, and that is forced rather than
            // chosen: the EF model always describes the *head* schema, so every read through it below
            // — Students, Terms, Enrollments — fails on the first column a later migration adds. This
            // file's own header records the same constraint as the reason its fixture is raw SQL.
            // Stopping here would mean rewriting every assertion below as raw SQL to buy nothing: what
            // is under test is that the populated §4 rows survive, and they survive everything applied
            // on top of them, not merely the next one.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            // Asserted by name, not by count. A hard-coded count is a running total of how many
            // migrations exist, so it fails the day an unrelated one lands and says nothing about
            // whether this one applied — which is what happened when SisImportPipeline arrived.
            Assert.True(await MigrationIsAppliedAsync(connectionString, AcademicLayerMigration));
            Assert.True(await AppliedMigrationCountAsync(connectionString) >= 2);
            Assert.Equal(before, await CountAsync(connectionString));

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                // The §4 rows are readable through the new model and their values are untouched. The
                // Section check is the pointed one: ADR-001 D-2 demotes that very column in this
                // migration, and "demoted to a cache" must not have meant "blanked".
                var santos = await db.Students.AsNoTracking().SingleAsync(s => s.StudentNumber == "2023-0001");
                Assert.Equal("A", santos.Section);
                Assert.Equal("BSIT", santos.Course);
                Assert.Equal("3rd Year", santos.YearLevel);
                Assert.Null(santos.AcademicCacheUpdatedAt);

                Assert.Equal(2, await db.AttendanceRecords.CountAsync());
                Assert.Equal(1, await db.AttendanceRecords.CountAsync(a => a.Status == "Late"));

                // And the new layer is usable against the rows that were already there — an enrollment
                // for a student the §4 schema created, which is the whole point of the migration.
                var school = await db.Schools.AsNoTracking().FirstAsync();
                var term = TestData.NewTerm(school.Id);
                db.Terms.Add(term);
                var course = TestData.NewCourse(school.Id);
                db.Courses.Add(course);
                var offering = TestData.NewOffering(term.Id, course.Id);
                db.CourseOfferings.Add(offering);
                db.Enrollments.Add(TestData.NewEnrollment(santos.Id, offering.Id));
                await db.SaveChangesAsync();

                Assert.Equal(1, await db.Enrollments.CountAsync());
            }

            // ---- the filtered guards arrived intact on the upgrade path too, not only on a schema
            //      built from scratch. A filter is the part of an index a migration is most able to get
            //      subtly wrong, and the wrong version still looks present in every schema diff.
            var derivedFilter = await ReadIndexFilterAsync(connectionString, "UX_StudentGroups_Derived_Source");
            Assert.NotNull(derivedFilter);
            Assert.Contains("SourceType", derivedFilter);
            Assert.Contains("Derived", derivedFilter);
            Assert.DoesNotContain("TermId", derivedFilter);

            Assert.Null(await ReadIndexFilterAsync(connectionString, "UX_CourseOfferings_Term_Course_Section"));

            // The FK index EF wanted to drop when the composite above appeared.
            Assert.True(await IndexExistsAsync(connectionString, "IX_StudentGroups_SchoolId"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// The existing <c>StudentGroups</c> / <c>StudentGroupMembers</c> rows gain three NOT NULL columns,
    /// and the values backfilled onto them are load-bearing rather than cosmetic: they are what tells
    /// the projection those rows belong to a person and must never be touched. A default of
    /// <c>Derived</c> — or a nullable column that a reader treats as derived — would hand every
    /// pre-existing hand-curated group to the set-diff, which would empty it on the first import.
    /// </summary>
    [Fact]
    public async Task Existing_groups_and_memberships_are_backfilled_as_manual()
    {
        var databaseName = $"EAMS_Backfill_{Guid.NewGuid():N}";
        var connectionString = await ArrangePopulatedBaselineAsync(Sql, databaseName);

        try
        {
            // Migrated to head for the reason given above: these assertions read through the EF model.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            await using var read = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
            var group = await read.StudentGroups.AsNoTracking().Include(g => g.Members)
                .SingleAsync(g => g.Id == ManualGroupId);

            Assert.Equal("SSC Officers", group.Name);
            Assert.Equal(GroupSourceType.Manual, group.SourceType);
            Assert.Equal(GroupSourceEntityType.None, group.SourceEntityType);
            Assert.Equal("", group.SourceKey);
            Assert.Null(group.SourceEntityId);
            Assert.Null(group.TermId);
            Assert.Null(group.LastSyncedAt);
            Assert.Equal(GroupSourceType.Manual, group.Members.Single().SourceType);
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, string? name = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        if (name is not null) command.Parameters.AddWithValue("@name", name);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Task<int> AppliedMigrationCountAsync(string connectionString) =>
        ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory;");

    /// <summary>
    /// Whether one named migration is recorded as applied. The <c>LIKE</c> is over the timestamp prefix
    /// EF puts on every MigrationId, so callers name the migration the way they wrote it.
    /// </summary>
    private static async Task<bool> MigrationIsAppliedAsync(string connectionString, string name) =>
        await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%' + @name;",
            name) > 0;

    private static async Task<bool> TableExistsAsync(string connectionString, string tableName) =>
        await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = @name;", tableName) > 0;

    private static async Task<bool> IndexExistsAsync(string connectionString, string indexName) =>
        await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.indexes WHERE name = @name;", indexName) > 0;

    private static async Task<string?> ReadIndexFilterAsync(string connectionString, string indexName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT filter_definition FROM sys.indexes WHERE name = @name;", connection);
        command.Parameters.AddWithValue("@name", indexName);

        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value); // null here means the index itself is missing, not that it is unfiltered
        return value is DBNull ? null : (string)value;
    }
}
