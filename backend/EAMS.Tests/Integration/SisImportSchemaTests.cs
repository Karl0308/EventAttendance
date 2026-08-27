using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The migration-#3 import schema, asserted against the migrated database rather than against the EF
/// model — the same reading <see cref="AcademicSchemaTests"/> takes, and for the same reason.
///
/// <para>
/// <b>The SQL Server provider silently appends <c>IS NOT NULL</c> to any unique index covering a
/// nullable column.</b> That is not hypothetical in this repository: it shipped once on
/// <c>UX_Attendance_Event_Student_Occurrence</c> and left the entire attendance table unconstrained
/// while the index still appeared in every schema diff and every model snapshot. A model-level
/// assertion would have passed. So every index's <c>filter_definition</c> is read back out of
/// <c>sys.indexes</c>, and each metadata assertion is paired with the behaviour it buys, attempted for
/// real.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportSchemaTests : IntegrationTest
{
    public SisImportSchemaTests(SqlServerFixture sql) : base(sql) { }

    private static readonly int[] UniqueViolation = [2601, 2627];

    /// <summary>Cannot insert NULL into a NOT NULL column.</summary>
    private const int CannotInsertNull = 515;

    private sealed record IndexInfo(bool IsUnique, bool HasFilter, string? FilterDefinition);

    private async Task<IndexInfo?> ReadIndexAsync(string indexName)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT is_unique, has_filter, filter_definition FROM sys.indexes WHERE name = @name;",
            connection);
        command.Parameters.AddWithValue("@name", indexName);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new IndexInfo(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private async Task<IndexInfo> RequireIndexAsync(string indexName) =>
        await ReadIndexAsync(indexName)
        ?? throw new Xunit.Sdk.XunitException($"Index {indexName} does not exist in the migrated schema.");

    private async Task<string> ReadNullabilityAsync(string table, string column)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column;", connection);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (string)value!;
    }

    private static SqlException AssertSqlError(DbUpdateException ex, int[] expectedNumbers)
    {
        var sql = Assert.IsType<SqlException>(ex.InnerException);
        Assert.Contains(sql.Number, expectedNumbers);
        return sql;
    }

    private sealed record World(Guid SchoolId, Guid TermId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    private static SisImportBatch NewBatch(Guid schoolId, Guid termId) => new()
    {
        SchoolId = schoolId,
        TermId = termId,
        Source = SisImportSource.Excel,
        Status = SisImportStatus.Pending,
    };

    // ================================================================== the three new tables exist

    /// <summary>
    /// ADR-001 D-1 sized the academic addition at twelve tables and pinned nine by name, leaving "pin
    /// the remaining three during the schema phase" as an open follow-up. These are those three, and
    /// they are all import-side. Naming them here closes that follow-up.
    /// </summary>
    [Theory]
    [InlineData("SisImportRowEntities")]
    [InlineData("SisImportProfiles")]
    [InlineData("SisImportProfileColumns")]
    public async Task The_three_tables_ADR_001_counted_and_never_named_exist(string tableName)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE name = @name;", connection);
        command.Parameters.AddWithValue("@name", tableName);

        Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task The_migration_history_records_the_import_pipeline_on_top_of_the_academic_layer()
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%Section4Baseline' " +
            "OR MigrationId LIKE '%AcademicLayer' OR MigrationId LIKE '%SisImportPipeline';",
            connection);

        Assert.Equal(3, (int)(await command.ExecuteScalarAsync())!);
    }

    // ============================================================== every new unique index, verified

    /// <summary>
    /// The unfiltered ones. Every key component of each is NOT NULL by design, so the provider has no
    /// nullable column to attach an <c>IS NOT NULL</c> to — and this is what proves that stayed true.
    /// </summary>
    [Theory]
    [InlineData("UX_SisImportRows_Batch_RowNumber")]
    [InlineData("UX_SisImportRowEntities_Row_Type_Entity")]
    [InlineData("UX_SisImportProfiles_School_Name_Version")]
    [InlineData("UX_SisImportProfileColumns_Profile_Column_Target")]
    public async Task The_natural_key_index_is_unique_and_unfiltered(string indexName)
    {
        var index = await RequireIndexAsync(indexName);

        Assert.True(index.IsUnique, $"{indexName} is not unique.");
        Assert.False(
            index.HasFilter,
            $"{indexName} is filtered on {index.FilterDefinition}. Nothing in this index's key is " +
            "nullable, so a filter can only have come from the SQL Server provider appending " +
            "IS NOT NULL to a column that has since been made nullable — which would leave every row " +
            "with a NULL in that column entirely unconstrained.");
        Assert.Null(index.FilterDefinition);
    }

    /// <summary>
    /// Filtered on purpose, and for the same reason <c>UX_Terms_SchoolId_Current</c> is: at most one
    /// active mapping version per profile, with every superseded version left unconstrained so it
    /// survives to explain the batches that ran under it.
    /// </summary>
    [Fact]
    public async Task The_active_profile_index_is_unique_and_filtered_to_the_active_version()
    {
        var index = await RequireIndexAsync("UX_SisImportProfiles_School_Name_Active");

        Assert.True(index.IsUnique);
        Assert.True(index.HasFilter);
        Assert.Contains("IsActive", index.FilterDefinition!);
    }

    /// <summary>
    /// The two indexes migration #3 drops are genuinely redundant, and this states why in a form that
    /// breaks if it stops being true: each dropped index's column is the <em>leading</em> column of an
    /// unfiltered composite, which SQL Server can use for a prefix predicate.
    ///
    /// <para>
    /// The identical scaffolded drop was wrong once in this repository —
    /// <c>IX_StudentGroups_SchoolId</c> had to be kept because its replacement composite is filtered to
    /// <c>SourceType = 'Derived'</c>, and a filtered index cannot serve a query that does not imply its
    /// predicate. Neither replacement here is filtered.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_redundant_single_column_indexes_are_replaced_by_unfiltered_composites()
    {
        Assert.Null(await ReadIndexAsync("IX_SisImportRows_BatchId"));
        Assert.Null(await ReadIndexAsync("IX_SisImportBatches_SchoolId"));

        var rows = await RequireIndexAsync("IX_SisImportRows_BatchId_Result");
        Assert.False(rows.IsUnique);
        Assert.False(rows.HasFilter);

        var batches = await RequireIndexAsync("IX_SisImportBatches_SchoolId_TermId");
        Assert.False(batches.IsUnique);
        Assert.False(batches.HasFilter);

        Assert.Equal(["SchoolId", "TermId"], await ReadIndexColumnsAsync("IX_SisImportBatches_SchoolId_TermId"));
        Assert.Equal(["BatchId", "Result"], await ReadIndexColumnsAsync("IX_SisImportRows_BatchId_Result"));
    }

    /// <summary>The reverse lookup — "which import rows touched this course?" — is indexed.</summary>
    [Fact]
    public async Task The_fan_out_is_indexed_from_the_entitys_side_as_well_as_the_rows()
    {
        var index = await RequireIndexAsync("IX_SisImportRowEntities_Entity");

        Assert.False(index.IsUnique);
        Assert.False(index.HasFilter);
        Assert.Equal(["EntityType", "EntityId"], await ReadIndexColumnsAsync("IX_SisImportRowEntities_Entity"));
    }

    private async Task<string[]> ReadIndexColumnsAsync(string indexName)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT c.name
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = @name AND ic.is_included_column = 0
            ORDER BY ic.key_ordinal;
            """, connection);
        command.Parameters.AddWithValue("@name", indexName);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return [.. columns];
    }

    // ==================================================================== the behaviour they buy

    /// <summary>
    /// ADR-001 D-5's required <c>TermId</c>, asserted at the column rather than in the model: a batch
    /// that cannot say which term it is for cannot be placed in time, and every downstream query is
    /// term-scoped.
    /// </summary>
    [Fact]
    public async Task A_batch_cannot_exist_without_a_term()
    {
        Assert.Equal("NO", await ReadNullabilityAsync("SisImportBatches", "TermId"));

        var world = await ArrangeAsync();

        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "INSERT INTO SisImportBatches (Id, SchoolId, TermId, Source, Status, TotalRows, " +
            "InsertedRows, UpdatedRows, FailedRows, SkippedRows, WarningRows) " +
            "VALUES (NEWID(), @school, NULL, N'Excel', N'Pending', 0, 0, 0, 0, 0, 0);", connection);
        command.Parameters.AddWithValue("@school", world.SchoolId);

        var rejected = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(CannotInsertNull, rejected.Number);
    }

    /// <summary>
    /// One row number per batch. Without it "row 214" is ambiguous in the one table whose entire
    /// purpose is to answer questions about row 214.
    /// </summary>
    [Fact]
    public async Task A_batch_cannot_stage_the_same_worksheet_row_twice()
    {
        var world = await ArrangeAsync();

        Guid batchId;
        await using (var db = NewDbContext())
        {
            var batch = NewBatch(world.SchoolId, world.TermId);
            db.SisImportBatches.Add(batch);
            db.SisImportRows.Add(new SisImportRow
            {
                Batch = batch, RowNumber = 2, Result = SisImportRowResult.Pending,
            });
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }

        await using var second = NewDbContext();
        second.SisImportRows.Add(new SisImportRow
        {
            BatchId = batchId, RowNumber = 2, Result = SisImportRowResult.Pending,
        });

        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_SisImportRows_Batch_RowNumber", sql.Message);
    }

    /// <summary>
    /// The versioned-mapping guarantee: many versions of one profile, but only one of them active. Two
    /// active versions would mean a new batch picks one arbitrarily and two imports of the same file
    /// would be explained by different rules with nothing recording why (ADR-001 D-4).
    /// </summary>
    [Fact]
    public async Task A_profile_may_have_many_versions_but_only_one_active_one()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.SisImportProfiles.Add(NewProfile(world.SchoolId, version: 1, isActive: false));
            db.SisImportProfiles.Add(NewProfile(world.SchoolId, version: 2, isActive: false));
            db.SisImportProfiles.Add(NewProfile(world.SchoolId, version: 3, isActive: true));
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        Assert.Equal(3, await read.SisImportProfiles.CountAsync());
        Assert.Single(await read.SisImportProfiles.Where(p => p.IsActive).ToListAsync());

        await using var clash = NewDbContext();
        clash.SisImportProfiles.Add(NewProfile(world.SchoolId, version: 4, isActive: true));

        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => clash.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_SisImportProfiles_School_Name_Active", sql.Message);
    }

    [Fact]
    public async Task One_profile_cannot_hold_the_same_version_twice()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.SisImportProfiles.Add(NewProfile(world.SchoolId, version: 1, isActive: true));
            await db.SaveChangesAsync();
        }

        await using var clash = NewDbContext();
        clash.SisImportProfiles.Add(NewProfile(world.SchoolId, version: 1, isActive: false));

        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => clash.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_SisImportProfiles_School_Name_Version", sql.Message);
    }

    /// <summary>
    /// One source column may feed several targets — <c>COLLEGE_NAME</c> feeds both
    /// <c>College.Name</c> (verbatim) and <c>College.NameKey</c> (normalized) — but not the same target
    /// twice. That is why <c>TargetField</c> is in the key.
    ///
    /// <para>
    /// The example used to be REGNO feeding <c>Students.StudentNumber</c> and <c>RfidCards.CardUid</c>.
    /// The client corrected that on 2026-07-30 — the card serial is its own source column — so the
    /// index's justification is restated against a pair that is still live. The index itself is
    /// unchanged and so is what it proves.
    /// </para>
    /// </summary>
    [Fact]
    public async Task One_source_column_may_feed_two_targets_but_not_one_target_twice()
    {
        var world = await ArrangeAsync();

        Guid profileId;
        await using (var db = NewDbContext())
        {
            var profile = NewProfile(world.SchoolId, version: 1, isActive: true);
            db.SisImportProfiles.Add(profile);
            db.SisImportProfileColumns.Add(NewColumn(profile, "College.Name"));
            db.SisImportProfileColumns.Add(NewColumn(profile, "College.NameKey"));
            await db.SaveChangesAsync();
            profileId = profile.Id;
        }

        await using var read = NewDbContext();
        Assert.Equal(2, await read.SisImportProfileColumns.CountAsync());

        await using var clash = NewDbContext();
        var duplicate = NewColumn(null, "College.NameKey");
        duplicate.ProfileId = profileId;
        clash.SisImportProfileColumns.Add(duplicate);

        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => clash.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_SisImportProfileColumns_Profile_Column_Target", sql.Message);
    }

    /// <summary>
    /// One fan-out row per (source row, entity type, entity). A re-run rebuilds the trail from scratch;
    /// without this index a retry would append a second, contradictory answer to "what did this row
    /// do?".
    /// </summary>
    [Fact]
    public async Task A_source_row_records_each_entity_it_touched_exactly_once()
    {
        var world = await ArrangeAsync();
        var courseId = Guid.NewGuid();

        Guid rowId;
        await using (var db = NewDbContext())
        {
            var batch = NewBatch(world.SchoolId, world.TermId);
            db.SisImportBatches.Add(batch);
            var row = new SisImportRow
            {
                Batch = batch, RowNumber = 2, Result = SisImportRowResult.Inserted,
            };
            db.SisImportRows.Add(row);
            db.SisImportRowEntities.Add(new SisImportRowEntity
            {
                SisImportRow = row,
                EntityType = SisImportEntityType.Course,
                EntityId = courseId,
                Action = SisImportEntityAction.Inserted,
            });
            // Same row, same entity id, different type: legitimate and must be allowed.
            db.SisImportRowEntities.Add(new SisImportRowEntity
            {
                SisImportRow = row,
                EntityType = SisImportEntityType.Student,
                EntityId = courseId,
                Action = SisImportEntityAction.Inserted,
            });
            await db.SaveChangesAsync();
            rowId = row.Id;
        }

        await using var read = NewDbContext();
        Assert.Equal(2, await read.SisImportRowEntities.CountAsync());

        await using var clash = NewDbContext();
        clash.SisImportRowEntities.Add(new SisImportRowEntity
        {
            SisImportRowId = rowId,
            EntityType = SisImportEntityType.Course,
            EntityId = courseId,
            Action = SisImportEntityAction.Updated,
        });

        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => clash.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_SisImportRowEntities_Row_Type_Entity", sql.Message);
    }

    /// <summary>
    /// <c>SisImportRowEntities.EntityId</c> deliberately carries no foreign key: it points into one of
    /// ten tables depending on <c>EntityType</c>, and the trail has to survive its target being deleted.
    /// This states that as a behaviour so a later "tidy-up" that adds an FK fails here rather than in
    /// production.
    /// </summary>
    [Fact]
    public async Task The_fan_out_can_reference_an_entity_that_no_longer_exists()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var batch = NewBatch(world.SchoolId, world.TermId);
        db.SisImportBatches.Add(batch);
        var row = new SisImportRow { Batch = batch, RowNumber = 2, Result = SisImportRowResult.Failed };
        db.SisImportRows.Add(row);
        db.SisImportRowEntities.Add(new SisImportRowEntity
        {
            SisImportRow = row,
            EntityType = SisImportEntityType.Course,
            EntityId = Guid.NewGuid(),           // no such course, and never was
            Action = SisImportEntityAction.Unchanged,
        });

        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(1, await read.SisImportRowEntities.CountAsync());
    }

    /// <summary>
    /// ADR-001 D-5 widened the status set; the column has to have been widened with it, or
    /// <c>CompletedWithWarnings</c> (21 characters) arrives as SQL Server error 2628 mid-import.
    /// </summary>
    [Fact]
    public async Task The_longest_batch_status_fits_in_its_column()
    {
        var world = await ArrangeAsync();
        var longest = SisImportStatus.All.MaxBy(s => s.Length)!;

        await using var db = NewDbContext();
        var batch = NewBatch(world.SchoolId, world.TermId);
        batch.Status = longest;
        db.SisImportBatches.Add(batch);
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(longest, (await read.SisImportBatches.SingleAsync()).Status);
    }

    // ============================================================================ progress columns

    /// <summary>
    /// The seven progress columns, read out of <c>INFORMATION_SCHEMA</c> rather than off the EF model,
    /// with every expectation written by hand.
    ///
    /// <para>
    /// The lengths are the two that were chosen for a reason and are therefore the two worth pinning:
    /// <c>ProgressPhase</c> is 40 against a longest phase name of 22 characters
    /// (<c>RefreshingStudentCache</c>), and <c>FailureReason</c> is 400 — a sentence an operator can
    /// act on, not a stack trace.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ProgressPhase", "nvarchar", 40)]
    [InlineData("ProgressPhaseNumber", "int", null)]
    [InlineData("ProgressPhaseCount", "int", null)]
    [InlineData("ProgressUnitsDone", "int", null)]
    [InlineData("ProgressUnitsTotal", "int", null)]
    [InlineData("ProgressUpdatedAt", "datetime2", null)]
    [InlineData("FailureReason", "nvarchar", 400)]
    public async Task Every_progress_column_is_nullable_of_the_declared_type_and_has_no_default(
        string column, string dataType, int? maxLength)
    {
        var actual = await ReadColumnAsync("SisImportBatches", column);

        Assert.NotNull(actual);
        Assert.Equal(dataType, actual!.DataType);
        Assert.Equal(maxLength, actual.MaxLength);

        // Nullable is not a detail here: NULL is the value that says "this run predates progress
        // reporting", and it is the only value that can say it.
        Assert.Equal("YES", actual.IsNullable);

        // And no default, which is the half a schema diff will not show. A DEFAULT 0 would make every
        // batch that never reported progress indistinguishable from one sitting on a phase that has
        // done none of its units — a progress bar frozen at 0% on a run that finished months ago.
        Assert.Null(actual.Default);
    }

    /// <summary>
    /// The negative control for the assertion above: the six row counters on the same table <em>do</em>
    /// carry <c>DEFAULT 0</c>, and they are right to — "no rows failed" is a fact about a finished run,
    /// where a progress 0 is three different facts at once.
    ///
    /// <para>
    /// Without this, "no default" would pass just as happily against a
    /// <c>COLUMN_DEFAULT</c> read that never returns anything — a green test proving only that the
    /// query is broken.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("TotalRows")]
    [InlineData("InsertedRows")]
    [InlineData("UpdatedRows")]
    [InlineData("FailedRows")]
    [InlineData("SkippedRows")]
    [InlineData("WarningRows")]
    public async Task The_row_counters_on_the_same_table_do_carry_a_default(string column)
    {
        var actual = await ReadColumnAsync("SisImportBatches", column);

        Assert.NotNull(actual);
        Assert.Equal("NO", actual!.IsNullable);
        Assert.NotNull(actual.Default);
        Assert.Contains("0", actual.Default);
    }

    /// <summary>
    /// The behaviour the seven nullable columns buy: a batch can be written without mentioning any of
    /// them, and reads back with all seven NULL rather than zeroed.
    /// </summary>
    [Fact]
    public async Task A_batch_that_reports_no_progress_reads_back_as_seven_nulls()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.SisImportBatches.Add(NewBatch(world.SchoolId, world.TermId));
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var batch = await read.SisImportBatches.AsNoTracking().SingleAsync();

        Assert.Null(batch.ProgressPhase);
        Assert.Null(batch.ProgressPhaseNumber);
        Assert.Null(batch.ProgressPhaseCount);
        Assert.Null(batch.ProgressUnitsDone);
        Assert.Null(batch.ProgressUnitsTotal);
        Assert.Null(batch.ProgressUpdatedAt);
        Assert.Null(batch.FailureReason);

        // The counters on the same row are 0, not null. Same table, same insert, different answer —
        // which is the whole distinction this migration rests on.
        Assert.Equal(0, batch.TotalRows);
        Assert.Equal(0, batch.WarningRows);
    }

    /// <summary>
    /// The longest phase name fits, asserted the same way <c>Status</c>'s longest value is: by storing
    /// it and reading it back, so a column narrowed later fails here rather than as SQL Server error
    /// 2628 in the middle of somebody's import.
    /// </summary>
    [Fact]
    public async Task The_longest_phase_name_fits_in_its_column()
    {
        var world = await ArrangeAsync();
        var longest = SisImportPhase.All.MaxBy(p => p.Length)!;

        // Worked out by hand rather than by re-running MaxBy: 'RefreshingStudentCache' is 22
        // characters, and nvarchar(40) is the column.
        Assert.Equal("RefreshingStudentCache", longest);
        Assert.Equal(22, longest.Length);

        await using (var db = NewDbContext())
        {
            var batch = NewBatch(world.SchoolId, world.TermId);
            batch.ProgressPhase = longest;
            db.SisImportBatches.Add(batch);
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        Assert.Equal(longest, (await read.SisImportBatches.SingleAsync()).ProgressPhase);
    }

    private sealed record ColumnInfo(string DataType, int? MaxLength, string IsNullable, string? Default);

    private async Task<ColumnInfo?> ReadColumnAsync(string table, string column)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, COLUMN_DEFAULT " +
            "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table AND COLUMN_NAME = @column;",
            connection);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new ColumnInfo(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static SisImportProfile NewProfile(Guid schoolId, int version, bool isActive) => new()
    {
        SchoolId = schoolId,
        Name = "CICSS Faculty Evaluation Report",
        NameKey = AcademicKey.NormalizeOrUnspecified("CICSS Faculty Evaluation Report"),
        Version = version,
        Source = SisImportSource.Excel,
        IsActive = isActive,
    };

    private static SisImportProfileColumn NewColumn(SisImportProfile? profile, string targetField) => new()
    {
        Profile = profile,
        SourceColumn = SisRosterColumns.CollegeName,
        SourceColumnKey = SisRosterColumns.HeaderKey(SisRosterColumns.CollegeName),
        TargetField = targetField,
        NormalizationRule = "RosterText.Clean",
        IsRequired = true,
    };
}
