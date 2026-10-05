using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Proves the second additive migration of Task 2 Phase 2 — <c>EventAudienceDefinitionLink</c>
/// (ADR-007 D-69) — is additive and reversible in the only sense that matters: applied to a database
/// that already holds Events and AudienceDefinitions, it adds the <c>EventAudienceDefinitions</c> link
/// table and its unique index, changes nothing that was there, and reverses cleanly — up, down, up.
///
/// <para>
/// <b>Why EF/TestData population is safe here, unlike the earlier migration tests' raw SQL.</b> Those
/// migrations add NOT NULL <em>columns</em> to existing tables, so seeding through the head model against
/// an earlier revision fails on the first missing column. This migration adds only a new <em>table</em>,
/// so Events and AudienceDefinitions carry their head columns at the prior revision already — the parents
/// can be written through the model, and only the new table is off-limits until Up has run.
/// </para>
///
/// <para>
/// Its own scratch database, because rolling the shared one back would break every other test in the
/// collection.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventAudienceDefinitionLinkMigrationTests : IntegrationTest
{
    public EventAudienceDefinitionLinkMigrationTests(SqlServerFixture sql) : base(sql) { }

    private const string PriorMigration = "EventClassificationLink";
    private const string LinkTable = "EventAudienceDefinitions";
    private const string UniqueIndex = "UX_EventAudienceDefinitions_Event_Definition";

    [Fact]
    public async Task The_link_table_applies_reverses_and_reapplies_on_a_populated_database()
    {
        var databaseName = $"EAMS_AudDefLink_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            // ---- stop at the migration immediately before this one; the link table does not exist yet.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.GetService<IMigrator>().MigrateAsync(PriorMigration);

            Assert.False(await TableExistsAsync(connectionString, LinkTable));

            // ---- a populated parent set: a classified event and a definition under its classification.
            Guid eventId, definitionId;
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
                eventId = ev.Id;
                definitionId = definition.Id;
            }

            var before = await CensusAsync(connectionString);
            Assert.Equal(new Census(1, 1, 1), before);

            // ---- UP: the table and its UNFILTERED unique index arrive; the parents are untouched.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            Assert.True(await TableExistsAsync(connectionString, LinkTable));
            Assert.True(await IndexExistsAsync(connectionString, UniqueIndex));
            // Standard, not filtered — both link columns are NOT NULL, so there is no NULL-equality
            // hazard and HasFilter(null) is the correct choice (unlike the EventGroups pair).
            Assert.Null(await ReadIndexFilterAsync(connectionString, UniqueIndex));
            Assert.Equal(before, await CensusAsync(connectionString));

            // ---- usable, and the unique index actually constrains: a link inserts, a duplicate is refused.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                db.EventAudienceDefinitions.Add(new EventAudienceDefinition
                {
                    EventId = eventId,
                    AudienceDefinitionId = definitionId,
                });
                await db.SaveChangesAsync();
                Assert.Equal(1, await db.EventAudienceDefinitions.CountAsync());
            }

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            {
                db.EventAudienceDefinitions.Add(new EventAudienceDefinition
                {
                    EventId = eventId,
                    AudienceDefinitionId = definitionId,
                });
                // UX_EventAudienceDefinitions_Event_Definition — idempotency by constraint (ADR-003 D-12
                // extended to the third source). A definition attaches at most once per event.
                await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            // ---- DOWN: the table goes, and nothing the rollback was never allowed to touch is touched.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.GetService<IMigrator>().MigrateAsync(PriorMigration);

            Assert.False(await TableExistsAsync(connectionString, LinkTable));
            Assert.Equal(before, await CensusAsync(connectionString));

            // ---- UP again: the table comes back, still additive, parents still intact.
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            Assert.True(await TableExistsAsync(connectionString, LinkTable));
            Assert.True(await IndexExistsAsync(connectionString, UniqueIndex));
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

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, string? name = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        if (name is not null) command.Parameters.AddWithValue("@name", name);
        return (T)(await command.ExecuteScalarAsync())!;
    }

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
