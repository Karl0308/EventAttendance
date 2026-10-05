using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Proves the single additive migration of Task 2 Phase 3 — <c>PreRegistrationEventLink</c> (ADR-008
/// D-71/D-73/D-74) — is additive and reversible in the only sense that matters: applied to a database
/// that already holds Schools, Events and AudienceDefinitions, it adds the nullable
/// <c>PreRegistrationSessions.EventId</c> column, its EF-convention index and the Restrict FK, changes
/// nothing that was there, and reverses cleanly — up, down, up.
///
/// <para>
/// <b>Why only the parents are seeded before Up.</b> This migration adds a <em>column</em> to
/// <c>PreRegistrationSessions</c>, so seeding a session through the head model at the prior revision would
/// fail on the missing <c>EventId</c> column (the trap <c>EventAudienceDefinitionLinkMigrationTests</c>
/// records for column migrations). Schools, Events and AudienceDefinitions are untouched by this migration,
/// so their head columns exist at the prior revision and the parents can be written; the session is written
/// only after Up, which is also where the new FK column is proved usable and constraining.
/// </para>
///
/// <para>
/// Its own scratch database, because rolling the shared one back would break every other test in the
/// collection.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PreRegistrationEventLinkMigrationTests : IntegrationTest
{
    public PreRegistrationEventLinkMigrationTests(SqlServerFixture sql) : base(sql) { }

    private const string PriorMigration = "EventAudienceDefinitionLink";
    private const string Table = "PreRegistrationSessions";
    private const string Column = "EventId";
    private const string Index = "IX_PreRegistrationSessions_EventId";
    private const string ForeignKey = "FK_PreRegistrationSessions_Events_EventId";

    [Fact]
    public async Task The_event_link_column_applies_reverses_and_reapplies_on_a_populated_database()
    {
        var databaseName = $"EAMS_PreRegEventLink_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            // ---- stop at the migration immediately before this one; the EventId column does not exist yet.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.GetService<IMigrator>().MigrateAsync(PriorMigration);

            Assert.True(await TableExistsAsync(connectionString, Table)); // the table itself is older
            Assert.False(await ColumnExistsAsync(connectionString, Table, Column));

            // ---- a populated parent set: a school, an event, and an audience definition.
            Guid schoolId, eventId, definitionId;
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                var school = TestData.NewSchool();
                db.Schools.Add(school);

                var classification = TestData.NewEventClassification(school.Id);
                db.EventClassifications.Add(classification);

                var ev = TestData.NewEvent(school.Id, EventStatus.Open);
                ev.EventClassificationId = classification.Id;
                db.Events.Add(ev);

                var definition = new AudienceDefinition
                {
                    SchoolId = school.Id,
                    EventClassificationId = classification.Id,
                    Name = "Everyone",
                    NameKey = AudienceText.KeyFor("Everyone"),
                    AudienceType = AudienceType.UniversityWide,
                    CriteriaJson = "{\"scope\":\"Both\"}",
                    IsActive = true,
                };
                db.AudienceDefinitions.Add(definition);

                await db.SaveChangesAsync();
                schoolId = school.Id;
                eventId = ev.Id;
                definitionId = definition.Id;
            }

            var before = await CensusAsync(connectionString);
            Assert.Equal(new Census(1, 1, 1), before);

            // ---- UP: the nullable column, its index and the FK arrive; the parents are untouched.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            Assert.True(await ColumnExistsAsync(connectionString, Table, Column));
            Assert.True(await ColumnIsNullableAsync(connectionString, Table, Column));
            Assert.True(await IndexExistsAsync(connectionString, Index));
            Assert.True(await ForeignKeyExistsAsync(connectionString, ForeignKey));
            Assert.Equal(before, await CensusAsync(connectionString));

            // ---- usable, and the FK actually constrains: a session links to a real event; a bogus one is refused.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                db.PreRegistrationSessions.Add(new PreRegistrationSession
                {
                    SchoolId = schoolId,
                    AudienceDefinitionId = definitionId,
                    Name = "Linked",
                    Capacity = 10,
                    EventId = eventId,
                });
                await db.SaveChangesAsync();
                Assert.Equal(1, await db.PreRegistrationSessions.CountAsync(s => s.EventId == eventId));
            }

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                db.PreRegistrationSessions.Add(new PreRegistrationSession
                {
                    SchoolId = schoolId,
                    AudienceDefinitionId = definitionId,
                    Name = "Dangling",
                    Capacity = 10,
                    EventId = Guid.NewGuid(), // no such event — FK_PreRegistrationSessions_Events_EventId refuses it
                });
                await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            // ---- DOWN: the FK, the index and the column go; nothing the rollback was never allowed to touch is.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.GetService<IMigrator>().MigrateAsync(PriorMigration);

            Assert.False(await ColumnExistsAsync(connectionString, Table, Column));
            Assert.False(await IndexExistsAsync(connectionString, Index));
            Assert.False(await ForeignKeyExistsAsync(connectionString, ForeignKey));
            Assert.True(await TableExistsAsync(connectionString, Table)); // the table is older and stays
            Assert.Equal(before, await CensusAsync(connectionString));

            // ---- UP again: the column, index and FK come back, still additive, parents still intact.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            Assert.True(await ColumnExistsAsync(connectionString, Table, Column));
            Assert.True(await IndexExistsAsync(connectionString, Index));
            Assert.True(await ForeignKeyExistsAsync(connectionString, ForeignKey));
            Assert.Equal(before, await CensusAsync(connectionString));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Census(int Schools, int Events, int Definitions);

    private static async Task<Census> CensusAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM Schools),
                (SELECT COUNT(*) FROM Events),
                (SELECT COUNT(*) FROM AudienceDefinitions);
            """, connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Census(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

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

    private static async Task<bool> IndexExistsAsync(string connectionString, string indexName) =>
        await ScalarAsync<int>(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE name = @name;", ("@name", indexName)) > 0;

    private static async Task<bool> ForeignKeyExistsAsync(string connectionString, string fkName) =>
        await ScalarAsync<int>(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = @name;", ("@name", fkName)) > 0;

    private static async Task<bool> ColumnExistsAsync(
        string connectionString, string tableName, string columnName) =>
        await ScalarAsync<int>(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE name = @col " +
            "AND object_id = OBJECT_ID(@tbl);",
            ("@col", columnName), ("@tbl", tableName)) > 0;

    private static async Task<bool> ColumnIsNullableAsync(
        string connectionString, string tableName, string columnName) =>
        // sys.columns.is_nullable is a bit, which SqlClient hands back as a Boolean.
        await ScalarAsync<bool>(connectionString,
            "SELECT is_nullable FROM sys.columns WHERE name = @col " +
            "AND object_id = OBJECT_ID(@tbl);",
            ("@col", columnName), ("@tbl", tableName));
}
