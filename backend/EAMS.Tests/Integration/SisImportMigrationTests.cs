using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Proves migration #3 is additive in the only sense that matters: applied to a database that already
/// sits at #2 <b>with rows in it</b>, it adds the import schema and changes nothing that was there.
///
/// <para>
/// <b>Why the rest of the suite does not already prove this.</b> Every other test runs against a
/// database migrated to head in one go, on an empty schema. That exercises the SQL and says nothing
/// about the case that actually happens — the developer's <c>EAMS</c> database and, later, the deployed
/// one, both sitting at <c>AcademicLayer</c> with a real roster in them.
/// </para>
///
/// <para>
/// It runs on its own scratch database, because rolling the shared one back would break every other
/// test in the collection.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportMigrationTests : IntegrationTest
{
    public SisImportMigrationTests(SqlServerFixture sql) : base(sql) { }

    private const string AcademicLayerMigration = "AcademicLayer";

    /// <summary>
    /// A §4-plus-academic-layer population, written as raw SQL the way the pre-import application wrote
    /// it — by something that has never heard of a term id on a batch.
    ///
    /// <para>
    /// Raw INSERTs rather than <c>SeedData</c> or the EF model, because the model always describes the
    /// <em>head</em> schema: seeding through it against a database stopped at #2 fails on the first
    /// column #3 adds. These rows are more faithful anyway — they are exactly the population the
    /// migration has to survive.
    /// </para>
    /// </summary>
    private const string PreImportPopulation = """
        DECLARE @school  UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111111';
        DECLARE @term    UNIQUEIDENTIFIER = '55555555-5555-5555-5555-555555555551';
        DECLARE @college UNIQUEIDENTIFIER = '66666666-6666-6666-6666-666666666661';
        DECLARE @course  UNIQUEIDENTIFIER = '77777777-7777-7777-7777-777777777771';
        DECLARE @offer   UNIQUEIDENTIFIER = '88888888-8888-8888-8888-888888888881';
        DECLARE @santos  UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222221';
        DECLARE @flores  UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222222';
        DECLARE @now     DATETIME2 = SYSUTCDATETIME();

        INSERT INTO Schools (Id, Name, Code, TimeZone, IsActive, CreatedAt, UpdatedAt)
        VALUES (@school, N'University of San Agustin', N'USA', N'Asia/Manila', 1, @now, @now);

        INSERT INTO Students
            (Id, SchoolId, StudentNumber, FirstName, MiddleName, LastName, Email,
             Course, YearLevel, Section, Gender, Status, SisExternalId, IsDeleted, CreatedAt, UpdatedAt)
        VALUES
            (@santos, @school, N'2023-0001', N'Maria', N'Reyes', N'Santos', N'2023-0001@usa.edu.ph',
             N'BSIT', N'3rd Year', N'A', N'Female', N'Active', NULL, 0, @now, @now),
            (@flores, @school, N'2021005781', N'Gabriel', NULL, N'Flores', N'2021005781@usa.edu.ph',
             N'BSA', N'4th Year', N'A', N'Male', N'Active', NULL, 0, @now, @now);

        INSERT INTO RfidCards (Id, SchoolId, StudentId, CardUid, Label, IsActive, IssuedAt, CreatedAt, UpdatedAt)
        VALUES
            (NEWID(), @school, @santos, N'04A1B2C3', N'Primary ID', 1, @now, @now, @now),
            (NEWID(), @school, @flores, N'0012503326', N'Primary ID', 1, @now, @now, @now);

        INSERT INTO Terms (Id, SchoolId, Code, SchoolYear, Semester, IsCurrent, CreatedAt, UpdatedAt)
        VALUES (@term, @school, N'2025-2026-1', N'2025-2026', N'1st Semester', 1, @now, @now);

        INSERT INTO Colleges (Id, SchoolId, Name, NameKey, Code, CreatedAt, UpdatedAt)
        VALUES (@college, @school, N'College of Criminal Justice', N'COLLEGEOFCRIMINALJUSTICE', N'CCJ', @now, @now);

        INSERT INTO Courses (Id, SchoolId, CollegeId, Code, CodeKey, Title, CreatedAt, UpdatedAt)
        VALUES (@course, @school, @college, N'SSCI 7', N'SSCI7', N'Life and Works of Rizal', @now, @now);

        INSERT INTO CourseOfferings (Id, TermId, CourseId, SectionKey, SectionName, CreatedAt, UpdatedAt)
        VALUES (@offer, @term, @course, N'BSCRIM2A', N'BSCRIM 2-A', @now, @now);

        INSERT INTO Enrollments (Id, StudentId, CourseOfferingId, CreatedAt, UpdatedAt)
        VALUES (NEWID(), @santos, @offer, @now, @now),
               (NEWID(), @flores, @offer, @now, @now);
        """;

    private static readonly Guid SantosId = new("22222222-2222-2222-2222-222222222221");

    private sealed record Census(int Students, int Cards, int Terms, int Courses, int Offerings, int Enrollments);

    private static async Task<Census> CountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM Students),
                (SELECT COUNT(*) FROM RfidCards),
                (SELECT COUNT(*) FROM Terms),
                (SELECT COUNT(*) FROM Courses),
                (SELECT COUNT(*) FROM CourseOfferings),
                (SELECT COUNT(*) FROM Enrollments);
            """, connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Census(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
    }

    private static async Task<string> ArrangePopulatedAcademicLayerAsync(
        SqlServerFixture sql, string databaseName)
    {
        var connectionString = await sql.CreateScratchDatabaseAsync(databaseName);

        await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            await db.GetService<IMigrator>().MigrateAsync(AcademicLayerMigration);

        await ExecuteAsync(connectionString, PreImportPopulation);
        return connectionString;
    }

    // ------------------------------------------------------------------------- the additive proof

    [Fact]
    public async Task The_import_schema_applies_on_top_of_a_populated_academic_layer_database()
    {
        var databaseName = $"EAMS_Sis_{Guid.NewGuid():N}";
        var connectionString = await ArrangePopulatedAcademicLayerAsync(Sql, databaseName);

        try
        {
            var before = await CountAsync(connectionString);
            Assert.Equal(new Census(2, 2, 1, 1, 1, 2), before);
            Assert.Equal(2, await AppliedMigrationCountAsync(connectionString));
            Assert.False(await TableExistsAsync(connectionString, "SisImportRowEntities"));

            // ---- migration #3, applied to that populated database.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            Assert.Equal(HeadMigrationCount(connectionString), await AppliedMigrationCountAsync(connectionString));
            Assert.Equal(before, await CountAsync(connectionString));

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                // The §4 and academic rows are readable through the new model with their values intact.
                // Section is the pointed one: ADR-001 D-2 demoted it to a cache, and the importer is now
                // its writer — "the importer may write it" must not have meant "the migration blanks it".
                var santos = await db.Students.AsNoTracking().SingleAsync(s => s.Id == SantosId);
                Assert.Equal("A", santos.Section);
                Assert.Equal("BSIT", santos.Course);
                Assert.Equal("3rd Year", santos.YearLevel);

                // The new column arrives NULL on every existing row rather than defaulted to something.
                Assert.Null(santos.AlternateEmail);

                // A ten-digit card serial with significant leading zeros survives the migration
                // unchanged — this migration must not reformat it, and neither must anything else. The
                // fixture used to assert the legacy REGNO here, on the premise that REGNO was the card
                // UID; the client corrected that on 2026-07-30, and a leading-zero serial is the
                // stronger version of the same "do not reformat an identifier" property anyway.
                Assert.Equal(1, await db.RfidCards.CountAsync(c => c.CardUid == "0012503326"));
                Assert.Equal(0, await db.RfidCards.CountAsync(c => c.CardUid == "12503326"));

                // The legacy ten-digit REGNO still survives as a student number, unchanged.
                Assert.Equal(1, await db.Students.CountAsync(s => s.StudentNumber == "2021005781"));

                // And the new schema is usable against the rows that were already there.
                var term = await db.Terms.AsNoTracking().FirstAsync();
                var batch = new SisImportBatch
                {
                    SchoolId = term.SchoolId,
                    TermId = term.Id,
                    Source = SisImportSource.Excel,
                    Status = SisImportStatus.Pending,
                };
                db.SisImportBatches.Add(batch);
                var row = new SisImportRow
                {
                    Batch = batch, RowNumber = 2, Result = SisImportRowResult.Pending,
                };
                db.SisImportRows.Add(row);
                db.SisImportRowEntities.Add(new SisImportRowEntity
                {
                    SisImportRow = row,
                    EntityType = SisImportEntityType.Student,
                    EntityId = SantosId,
                    Action = SisImportEntityAction.Unchanged,
                });
                await db.SaveChangesAsync();

                Assert.Equal(1, await db.SisImportRowEntities.CountAsync());
            }

            // ---- the filtered guards arrived intact on the upgrade path too, not only on a schema
            //      built from scratch. A filter is the part of an index a migration is most able to get
            //      subtly wrong, and the wrong version still looks present in every schema diff.
            var activeFilter = await ReadIndexFilterAsync(
                connectionString, "UX_SisImportProfiles_School_Name_Active");
            Assert.NotNull(activeFilter);
            Assert.Contains("IsActive", activeFilter);

            Assert.Null(await ReadIndexFilterAsync(connectionString, "UX_SisImportRows_Batch_RowNumber"));
            Assert.Null(await ReadIndexFilterAsync(
                connectionString, "UX_SisImportRowEntities_Row_Type_Entity"));

            // The academic layer's own guards are untouched by this migration.
            Assert.True(await IndexExistsAsync(connectionString, "UX_CourseOfferings_Term_Course_Section"));
            Assert.True(await IndexExistsAsync(connectionString, "IX_StudentGroups_SchoolId"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// <b>The data-preservation guard, and the reason the scaffolded migration was rewritten by hand.</b>
    ///
    /// <para>
    /// EF proposed adding the required <c>TermId</c> with <c>defaultValue: Guid.Empty</c>. On a
    /// populated table that stamps every existing batch with the all-zero GUID and then fails seconds
    /// later when the foreign key is added — a half-applied migration whose error names a constraint
    /// rather than the cause. And there is no honest backfill available: a batch imported before terms
    /// were required has no term recorded anywhere, so picking the current one, or inventing an
    /// "unknown" term, would fabricate the single most load-bearing fact about a historical import.
    /// </para>
    ///
    /// <para>
    /// So it refuses, loudly, and <b>nothing is lost</b> — the migration rolls back inside its own
    /// transaction and the database is exactly as it was. This test is what proves both halves.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_migration_refuses_rather_than_inventing_a_term_for_batches_that_predate_it()
    {
        var databaseName = $"EAMS_SisGuard_{Guid.NewGuid():N}";
        var connectionString = await ArrangePopulatedAcademicLayerAsync(Sql, databaseName);

        try
        {
            // A batch written by the pre-import schema: no TermId column existed, so none was recorded.
            await ExecuteAsync(connectionString, """
                INSERT INTO SisImportBatches
                    (Id, SchoolId, Source, FileName, Status, TotalRows, InsertedRows, UpdatedRows,
                     FailedRows, StartedAt, FinishedAt)
                VALUES
                    (NEWID(), '11111111-1111-1111-1111-111111111111', N'Csv', N'legacy.csv',
                     N'Completed', 10, 10, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);

            var before = await CountAsync(connectionString);

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                var refused = await Assert.ThrowsAnyAsync<SqlException>(() => db.Database.MigrateAsync());
                Assert.Contains("TermId", refused.Message, StringComparison.Ordinal);
                Assert.Contains("fabricate", refused.Message, StringComparison.OrdinalIgnoreCase);
            }

            // Rolled back whole: still at migration #2, none of the new tables exist, the legacy batch
            // is still there, and no row anywhere was touched.
            Assert.Equal(2, await AppliedMigrationCountAsync(connectionString));
            Assert.False(await TableExistsAsync(connectionString, "SisImportRowEntities"));
            Assert.False(await TableExistsAsync(connectionString, "SisImportProfiles"));
            Assert.False(await ColumnExistsAsync(connectionString, "SisImportBatches", "TermId"));
            Assert.False(await ColumnExistsAsync(connectionString, "Students", "AlternateEmail"));
            Assert.Equal(before, await CountAsync(connectionString));
            Assert.Equal(1, await ScalarAsync<int>(
                connectionString, "SELECT COUNT(*) FROM SisImportBatches;"));

            // And the documented recovery works: export and clear the legacy batches, then migrate.
            await ExecuteAsync(connectionString, "DELETE FROM SisImportBatches;");

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            Assert.Equal(HeadMigrationCount(connectionString), await AppliedMigrationCountAsync(connectionString));
            Assert.Equal(before, await CountAsync(connectionString));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    // ------------------------------------------------------- the progress columns, and no backfill

    /// <summary>The migration immediately before <c>SisImportProgress</c>.</summary>
    private const string PreProgressMigration = "UserLoginFoundation";

    private static readonly Guid LegacyBatchId = new("99999999-9999-9999-9999-999999999991");

    /// <summary>
    /// One finished batch, written by a schema that has never heard of a progress column — which is
    /// what every batch in the developer's database and on the deployment VM is.
    /// </summary>
    private const string FinishedBatchPopulation = """
        INSERT INTO SisImportBatches
            (Id, SchoolId, TermId, Source, FileName, SourceSheetName, Status, TotalRows,
             InsertedRows, UpdatedRows, FailedRows, SkippedRows, WarningRows, StartedAt, FinishedAt)
        VALUES
            ('99999999-9999-9999-9999-999999999991',
             '11111111-1111-1111-1111-111111111111',
             '55555555-5555-5555-5555-555555555551',
             N'Excel', N'CCJ-roster.xlsx', N'Faculty Evaluation Report',
             N'CompletedWithWarnings', 536, 470, 0, 0, 66, 66,
             '2026-08-01T02:15:00', '2026-08-01T02:17:30');
        """;

    /// <summary>
    /// <b>The no-backfill proof.</b> Applied on top of a database that already holds a finished import,
    /// the progress migration adds its seven columns and leaves that batch reading seven NULLs — not
    /// seven zeros, and not a fabricated "Done".
    ///
    /// <para>
    /// <b>Why this is worth its own test rather than being obvious from the migration.</b> The
    /// scaffolder's habitual answer to a new non-nullable column is a default, and a default here
    /// would be silently wrong in a way no schema diff shows: every historical batch would come back
    /// as a progress bar frozen at 0% out of 0 phases, which is a claim about runs that in fact
    /// completed. A NULL says "this run predates progress reporting", which is the truth. So the
    /// assertion is not merely that the migration applied — it is that the row that was already there
    /// gained nothing but nulls, and lost nothing at all.
    /// </para>
    ///
    /// <para>
    /// It also pins the direction: the seven columns did not exist one migration earlier. Without that
    /// half, a test that finds nulls afterwards proves nothing about when they arrived.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_progress_columns_arrive_null_on_a_batch_that_ran_before_they_existed()
    {
        var databaseName = $"EAMS_SisProgress_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        // Every progress column, named by hand rather than read off the entity — the point of the test
        // is that the schema matches what was asked for, and deriving the list from the model under
        // test would make it agree with itself.
        string[] progressColumns =
        [
            "ProgressPhase", "ProgressPhaseNumber", "ProgressPhaseCount",
            "ProgressUnitsDone", "ProgressUnitsTotal", "ProgressUpdatedAt", "FailureReason",
        ];

        try
        {
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.GetService<IMigrator>().MigrateAsync(PreProgressMigration);

            await ExecuteAsync(connectionString, PreImportPopulation);
            await ExecuteAsync(connectionString, FinishedBatchPopulation);

            // ---- one migration earlier, none of the seven exists.
            foreach (var column in progressColumns)
            {
                Assert.False(
                    await ColumnExistsAsync(connectionString, "SisImportBatches", column),
                    $"{column} already exists at {PreProgressMigration}; this test is asserting nothing.");
            }

            var before = await CountAsync(connectionString);
            Assert.Equal(1, await ScalarAsync<int>(
                connectionString, "SELECT COUNT(*) FROM SisImportBatches;"));

            // ---- the progress migration, applied to that populated database.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            Assert.Equal(HeadMigrationCount(connectionString), await AppliedMigrationCountAsync(connectionString));
            Assert.Equal(before, await CountAsync(connectionString));

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                var batch = await db.SisImportBatches.AsNoTracking()
                    .SingleAsync(x => x.Id == LegacyBatchId);

                // Seven nulls. Nothing was backfilled, and nothing was defaulted.
                Assert.Null(batch.ProgressPhase);
                Assert.Null(batch.ProgressPhaseNumber);
                Assert.Null(batch.ProgressPhaseCount);
                Assert.Null(batch.ProgressUnitsDone);
                Assert.Null(batch.ProgressUnitsTotal);
                Assert.Null(batch.ProgressUpdatedAt);
                Assert.Null(batch.FailureReason);

                // And the batch itself survived intact — the counters, the status the column was
                // widened for, and the file it came from. Written out by hand from the INSERT above
                // rather than compared against a re-read, so a migration that rewrote them fails here.
                Assert.Equal("CompletedWithWarnings", batch.Status);
                Assert.Equal(536, batch.TotalRows);
                Assert.Equal(470, batch.InsertedRows);
                Assert.Equal(0, batch.UpdatedRows);
                Assert.Equal(0, batch.FailedRows);
                Assert.Equal(66, batch.SkippedRows);
                Assert.Equal(66, batch.WarningRows);
                Assert.Equal("CCJ-roster.xlsx", batch.FileName);
                Assert.Equal("Faculty Evaluation Report", batch.SourceSheetName);
                Assert.NotNull(batch.FinishedAt);

                // 470 + 0 + 0 + 66 = 536. Hand arithmetic, and the reconciliation ADR-001 D-5 exists
                // for: a migration that touched a counter would break it here rather than in a report
                // an operator reads six months later.
                Assert.Equal(
                    batch.TotalRows,
                    batch.InsertedRows + batch.UpdatedRows + batch.FailedRows + batch.SkippedRows);
            }

            // ---- and the new columns are usable on the row that was already there.
            await ExecuteAsync(connectionString, $"""
                UPDATE SisImportBatches
                SET ProgressPhase = N'RefreshingStudentCache', ProgressPhaseNumber = 7,
                    ProgressPhaseCount = 8, ProgressUnitsDone = 300, ProgressUnitsTotal = 536,
                    ProgressUpdatedAt = SYSUTCDATETIME(), FailureReason = N'Connection reset.'
                WHERE Id = '{LegacyBatchId}';
                """);

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                var batch = await db.SisImportBatches.AsNoTracking()
                    .SingleAsync(x => x.Id == LegacyBatchId);

                Assert.Equal(SisImportPhase.RefreshingStudentCache, batch.ProgressPhase);
                Assert.Equal(7, batch.ProgressPhaseNumber);
                Assert.Equal(8, batch.ProgressPhaseCount);
                Assert.Equal(300, batch.ProgressUnitsDone);
                Assert.Equal(536, batch.ProgressUnitsTotal);
                Assert.NotNull(batch.ProgressUpdatedAt);
                Assert.Equal("Connection reset.", batch.FailureReason);
            }

            // The academic layer's own guards are untouched by this migration.
            Assert.True(await IndexExistsAsync(connectionString, "UX_CourseOfferings_Term_Course_Section"));
            Assert.True(await IndexExistsAsync(connectionString, "UX_SisImportRows_Batch_RowNumber"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    // -------------------------------------------------------------------- the reverse-path guards

    /// <summary>
    /// <b><c>Down</c> refuses rather than truncating a status or destroying a personal e-mail address.</b>
    ///
    /// <para>
    /// <c>Up</c>'s <c>TermId</c> guard was written by hand and is fully tested; <c>Down</c> got none of
    /// that, and scaffolded as generated it was not reversible at all on any database that had run an
    /// import. Two separate faults: narrowing <c>Status</c> back to <c>nvarchar(20)</c> fails on any row
    /// carrying <c>CompletedWithWarnings</c> (21 characters), with an error naming a column rather than
    /// the cause; and <c>DropColumn AlternateEmail</c> destroys a value per student with no export step
    /// and no way back.
    /// </para>
    ///
    /// <para>
    /// Both now refuse first, before anything is dropped, so the rollback is whole — and both clear once
    /// the documented recovery is performed, which is the half that proves the guard is a guard and not
    /// an obstacle.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rolling_the_import_schema_back_refuses_to_truncate_a_status_or_drop_a_populated_column()
    {
        var databaseName = $"EAMS_SisDown_{Guid.NewGuid():N}";
        var connectionString = await ArrangePopulatedAcademicLayerAsync(Sql, databaseName);

        try
        {
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            Assert.Equal(HeadMigrationCount(connectionString), await AppliedMigrationCountAsync(connectionString));

            // The state a database is in after one real import: a warned batch, and a student whose
            // alternate e-mail came off the roster.
            await ExecuteAsync(connectionString, $"""
                INSERT INTO SisImportBatches
                    (Id, SchoolId, TermId, Source, FileName, Status, TotalRows, InsertedRows,
                     UpdatedRows, FailedRows, SkippedRows, WarningRows)
                VALUES
                    (NEWID(), '11111111-1111-1111-1111-111111111111',
                     '55555555-5555-5555-5555-555555555551', N'Excel', N'Copy-of-CCJ.xlsx',
                     N'{SisImportStatus.CompletedWithWarnings}', 536, 536, 0, 0, 0, 66);

                UPDATE Students SET AlternateEmail = N'maria.santos@gmail.com' WHERE Id = '{SantosId}';
                """);

            var before = await CountAsync(connectionString);

            // ---- guard 1: the status would be truncated.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                var refused = await Assert.ThrowsAnyAsync<SqlException>(
                    () => db.GetService<IMigrator>().MigrateAsync(AcademicLayerMigration));

                Assert.Contains("Status", refused.Message, StringComparison.Ordinal);
                Assert.Contains("Export", refused.Message, StringComparison.OrdinalIgnoreCase);
            }

            // Rolled back whole: still at #3, every new table still there, nothing touched.
            Assert.Equal(HeadMigrationCount(connectionString), await AppliedMigrationCountAsync(connectionString));
            Assert.True(await TableExistsAsync(connectionString, "SisImportRowEntities"));
            Assert.True(await ColumnExistsAsync(connectionString, "Students", "AlternateEmail"));
            Assert.Equal(before, await CountAsync(connectionString));
            Assert.Equal(1, await ScalarAsync<int>(
                connectionString, "SELECT COUNT(*) FROM SisImportBatches;"));

            // ---- guard 2: with the batches exported and cleared, the personal data guard is next, and
            //      it fires on its own rather than being masked by the first.
            await ExecuteAsync(connectionString, "DELETE FROM SisImportBatches;");

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                var refused = await Assert.ThrowsAnyAsync<SqlException>(
                    () => db.GetService<IMigrator>().MigrateAsync(AcademicLayerMigration));

                Assert.Contains("AlternateEmail", refused.Message, StringComparison.Ordinal);
                Assert.Contains("Export", refused.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(HeadMigrationCount(connectionString), await AppliedMigrationCountAsync(connectionString));
            Assert.Equal(1, await ScalarAsync<int>(
                connectionString, "SELECT COUNT(*) FROM Students WHERE AlternateEmail IS NOT NULL;"));

            // ---- and the documented recovery works: export the column, clear it, then roll back.
            await ExecuteAsync(connectionString, "UPDATE Students SET AlternateEmail = NULL;");

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.GetService<IMigrator>().MigrateAsync(AcademicLayerMigration);

            Assert.Equal(2, await AppliedMigrationCountAsync(connectionString));
            Assert.False(await TableExistsAsync(connectionString, "SisImportRowEntities"));
            Assert.False(await ColumnExistsAsync(connectionString, "Students", "AlternateEmail"));

            // The §4 and academic rows the rollback was never allowed to touch are all still there.
            Assert.Equal(before, await CountAsync(connectionString));
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
    /// How many migrations the assembly defines — the number a database migrated to head must show.
    ///
    /// <para>
    /// <b>This used to be the literal <c>3</c>, in five places.</b> Three was what "at the head" meant
    /// on the day these were written, and every one of those assertions is about the schema being whole
    /// — that the import migration applied, or that a refused rollback left everything where it was.
    /// None of them is about the number three. Phase 3a added a fourth migration on an unrelated table
    /// and broke all five at once, which read in the diff as a regression in the import pipeline.
    /// </para>
    ///
    /// <para>
    /// Reads the migrations declared in the assembly; it does not touch the database. The deliberately
    /// controlled counts — the <c>2</c> that says "this scratch database is stopped at
    /// <c>AcademicLayer</c>" — stay literal, because there the number is the point.
    /// </para>
    /// </summary>
    private static int HeadMigrationCount(string connectionString)
    {
        using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        return db.Database.GetMigrations().Count();
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string tableName) =>
        await ScalarAsync<int>(
            connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = @name;", tableName) > 0;

    private static async Task<bool> IndexExistsAsync(string connectionString, string indexName) =>
        await ScalarAsync<int>(
            connectionString, "SELECT COUNT(*) FROM sys.indexes WHERE name = @name;", indexName) > 0;

    private static async Task<bool> ColumnExistsAsync(
        string connectionString, string table, string column)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column;", connection);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        return (int)(await command.ExecuteScalarAsync())! > 0;
    }

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
