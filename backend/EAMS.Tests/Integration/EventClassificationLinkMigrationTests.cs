using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The <c>EventClassificationLink</c> migration — the additive <c>Events.EventClassificationId</c> FK.
///
/// <para>
/// <b>Hard rule: no data-loss migrations.</b> <c>Up</c> only adds a nullable column, its index and a
/// <c>Restrict</c> foreign key; <c>Down</c> removes exactly those three. Dropping a <em>newly-added</em>
/// nullable column loses no pre-existing data, so the whole migration is reversible — this proves it
/// against a real SQL Server by applying, reverting and re-applying it with rows in the table the whole
/// time. The schema is read from <c>sys.*</c> rather than the EF model (project memory: verify schema
/// against the database, not the model).
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventClassificationLinkMigrationTests : IntegrationTest
{
    public EventClassificationLinkMigrationTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>The migration immediately before <c>EventClassificationLink</c>.</summary>
    private const string PreviousMigration = "PreRegistration";

    private const string ForeignKeyName = "FK_Events_EventClassifications_EventClassificationId";
    private const string IndexName = "IX_Events_EventClassificationId";

    private static readonly Guid SchoolId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PreColumnEventId = new("33333333-3333-3333-3333-333333333331");
    private static readonly Guid ClassifiedEventId = new("33333333-3333-3333-3333-333333333332");
    private static readonly Guid ClassificationId = new("44444444-4444-4444-4444-444444444441");

    [Fact]
    public async Task Applies_reverts_and_re_applies_cleanly()
    {
        var databaseName = $"EAMS_EcLink_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            // --- Start one migration back: the column, index and FK do not exist yet. ---
            await MigrateToAsync(connectionString, PreviousMigration);
            Assert.Equal(0, await ColumnCountAsync(connectionString));
            Assert.Equal(0, await IndexCountAsync(connectionString));
            Assert.Equal(0, await ForeignKeyCountAsync(connectionString));

            // A school and an event written the way the previous build wrote them — no knowledge of the
            // column this database does not have yet.
            await ExecuteAsync(connectionString, $"""
                DECLARE @now DATETIME2 = SYSUTCDATETIME();

                INSERT INTO Schools (Id, Name, Code, TimeZone, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{SchoolId}', N'University of San Agustin', N'USA', N'Asia/Manila', 1, @now, @now);

                INSERT INTO Events
                    (Id, SchoolId, Name, StartAt, EndAt, AttendanceMode, GraceMinutes,
                     RequireRegistration, Status, IsDeleted, IssuesCertificates, CreatedAt, UpdatedAt)
                VALUES
                    ('{PreColumnEventId}', '{SchoolId}', N'Convocation', @now, DATEADD(HOUR, 3, @now),
                     N'Single', 15, 0, N'Open', 0, 0, @now, @now);
                """);

            // --- Up: the column, index and FK appear; the pre-existing event is untouched and unlinked. ---
            await MigrateToAsync(connectionString, target: null);
            Assert.Equal(1, await ColumnCountAsync(connectionString));
            Assert.Equal(1, await IndexCountAsync(connectionString));
            Assert.Equal(1, await ForeignKeyCountAsync(connectionString));
            Assert.Equal(1, await CountAsync(connectionString,
                $"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{PreColumnEventId}' " +
                "AND [EventClassificationId] IS NULL;"));

            // The FK is live: a classification, and an event that references it, both insert cleanly.
            await ExecuteAsync(connectionString, $"""
                DECLARE @now DATETIME2 = SYSUTCDATETIME();

                INSERT INTO EventClassifications (Id, SchoolId, Name, NameKey, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{ClassificationId}', '{SchoolId}', N'Institutional Events',
                        N'INSTITUTIONALEVENTS', 1, @now, @now);

                INSERT INTO Events
                    (Id, SchoolId, Name, StartAt, EndAt, AttendanceMode, GraceMinutes,
                     RequireRegistration, Status, IsDeleted, IssuesCertificates,
                     EventClassificationId, CreatedAt, UpdatedAt)
                VALUES
                    ('{ClassifiedEventId}', '{SchoolId}', N'Foundation Day', @now, DATEADD(HOUR, 3, @now),
                     N'Single', 0, 0, N'Draft', 0, 0, '{ClassificationId}', @now, @now);
                """);
            Assert.Equal(1, await CountAsync(connectionString,
                $"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{ClassifiedEventId}' " +
                $"AND [EventClassificationId] = '{ClassificationId}';"));

            // --- Down: the column, index and FK are gone; every row survives. The link value goes with
            // the column it lived in, which is inherent to removing a newly-added column and is not a loss
            // of pre-existing data — the events and the classification are all still here. ---
            await MigrateToAsync(connectionString, PreviousMigration);
            Assert.Equal(0, await ColumnCountAsync(connectionString));
            Assert.Equal(0, await IndexCountAsync(connectionString));
            Assert.Equal(0, await ForeignKeyCountAsync(connectionString));
            Assert.Equal(2, await CountAsync(connectionString, "SELECT COUNT(*) FROM [Events];"));
            Assert.Equal(1, await CountAsync(connectionString, "SELECT COUNT(*) FROM [EventClassifications];"));
            Assert.Equal(1, await CountAsync(connectionString,
                $"SELECT COUNT(*) FROM [Events] WHERE [Name] = N'Foundation Day' AND [Status] = N'Draft';"));

            // --- Up again: re-applies cleanly onto the same data. ---
            await MigrateToAsync(connectionString, target: null);
            Assert.Equal(1, await ColumnCountAsync(connectionString));
            Assert.Equal(1, await IndexCountAsync(connectionString));
            Assert.Equal(1, await ForeignKeyCountAsync(connectionString));
            Assert.Equal(2, await CountAsync(connectionString, "SELECT COUNT(*) FROM [Events];"));
            // Re-added as a nullable column, so both events read unlinked — nothing claims a stale link.
            Assert.Equal(2, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM [Events] WHERE [EventClassificationId] IS NULL;"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    // ------------------------------------------------------------------------------------ plumbing

    private static async Task MigrateToAsync(string connectionString, string? target)
    {
        await using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static Task<int> ColumnCountAsync(string connectionString) =>
        CountAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns " +
            "WHERE object_id = OBJECT_ID(N'dbo.Events') AND name = N'EventClassificationId';");

    private static Task<int> IndexCountAsync(string connectionString) =>
        CountAsync(connectionString,
            $"SELECT COUNT(*) FROM sys.indexes " +
            $"WHERE object_id = OBJECT_ID(N'dbo.Events') AND name = N'{IndexName}';");

    private static Task<int> ForeignKeyCountAsync(string connectionString) =>
        CountAsync(connectionString,
            $"SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'{ForeignKeyName}';");

    private static async Task<int> CountAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
