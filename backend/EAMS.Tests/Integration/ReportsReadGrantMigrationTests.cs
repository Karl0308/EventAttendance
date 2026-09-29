using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Infrastructure;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b><c>GrantReportsReadToAdminRoles</c> — the migration that gives existing installations the new
/// code.</b>
///
/// <para>
/// <b>Why it has to be proven on a database built before it, and not on the suite's shared one.</b>
/// The shared database is migrated to head on an empty schema and seeded afterwards, and on that path
/// the startup seed creates the roles <em>with</em> <c>reports.read</c> — so the grant is present
/// whether or not this migration does anything at all. The case the migration exists for is the other
/// one: the dev databases and the deployed VM, whose SuperAdmin and SchoolAdmin rows were created by
/// an older build and which <c>RbacSeed</c> deliberately never reconciles. Every assertion reads the
/// tables with raw SQL, never through the EF model (project memory: verify schema against the
/// database, not the model).
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ReportsReadGrantMigrationTests : IntegrationTest
{
    public ReportsReadGrantMigrationTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>The migration immediately before this one.</summary>
    private const string PreviousMigration = "ClassificationReportedRosterValue";

    /// <summary>
    /// The §4.11 reference data exactly as a build from before P3 declared it: the same roles and
    /// grants, minus <c>reports.read</c> everywhere. Derived from the current matrix rather than
    /// re-listed, so the only difference between "then" and "now" is the one this migration is about.
    /// </summary>
    private static RbacReferenceData PreReportsReferenceData { get; } = new(
        Permissions: [.. EamsRoles.ReferenceData.Permissions
            .Where(p => p.Code != EamsPermissions.ReportsRead)],
        Roles: [.. EamsRoles.ReferenceData.Roles.Select(r => r with
        {
            PermissionCodes = [.. r.PermissionCodes.Where(c => c != EamsPermissions.ReportsRead)],
        })]);

    // ------------------------------------------------------------------------------------ plumbing

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

    /// <summary>How many <c>reports.read</c> <c>Permissions</c> rows exist — read from the table.</summary>
    private static Task<int> ReportsReadRowsAsync(string connectionString) =>
        CountAsync(connectionString,
            $"SELECT COUNT(*) FROM [Permissions] WHERE [Code] = N'{EamsPermissions.ReportsRead}';");

    /// <summary>How many <c>reports.read</c> grants one role holds — read from the table.</summary>
    private static Task<int> ReportsReadGrantsAsync(string connectionString, string roleName) =>
        CountAsync(connectionString, $"""
            SELECT COUNT(*)
            FROM [RolePermissions] AS rp
            JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
            JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
            WHERE p.[Code] = N'{EamsPermissions.ReportsRead}' AND r.[Name] = N'{roleName}';
            """);

    /// <summary>Every grant one role holds, of any code — so a migration that touched others shows.</summary>
    private static Task<int> AllGrantsAsync(string connectionString, string roleName) =>
        CountAsync(connectionString, $"""
            SELECT COUNT(*)
            FROM [RolePermissions] AS rp
            JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
            WHERE r.[Name] = N'{roleName}';
            """);

    private static async Task MigrateToAsync(string connectionString, string? target)
    {
        await using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    /// <summary>
    /// A database exactly as a pre-P3 host left it: schema at the previous migration, and the roles
    /// created by that build's startup seed — which granted everything it knew, and did not know
    /// <c>reports.read</c>.
    /// </summary>
    private async Task<string> ArrangePreReportsDatabaseAsync(string databaseName)
    {
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);
        await MigrateToAsync(connectionString, PreviousMigration);

        // The RBAC tables are unchanged since the baseline, so the current model can write them on a
        // database stopped one migration back.
        await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            await RbacSeed.ApplyAsync(db, PreReportsReferenceData, NullLogger.Instance);

        return connectionString;
    }

    /// <summary>
    /// The real startup path — <c>AddEamsInfrastructure</c> and <c>InitializeEamsDatabaseAsync</c>, as
    /// <c>Program.cs</c> calls them — rather than the two halves invoked by hand, so the order the
    /// migration and the seed actually run in is the order under test.
    /// </summary>
    private static async Task StartUpAsync(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{DependencyInjection.ConnectionStringName}"] = connectionString,
            })
            .Build();

        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddEamsInfrastructure(configuration)
            .BuildServiceProvider();

        await provider.InitializeEamsDatabaseAsync(
            EamsRoles.ReferenceData, Environments.Production, seedDevelopmentData: false);
    }

    // ------------------------------------------------------------------------------------ the tests

    /// <summary>
    /// <b>The case the migration exists for.</b> Admin roles created by an older build, without the
    /// grant; the migration applied; the grant is in the table — once per admin role, for no other
    /// role, and nothing else about any role's grants changed.
    /// </summary>
    [Fact]
    public async Task Migration_grants_reports_read_to_admin_roles_created_before_it()
    {
        var databaseName = $"EAMS_Rpt_{Guid.NewGuid():N}";
        var connectionString = await ArrangePreReportsDatabaseAsync(databaseName);

        try
        {
            // The arrangement really is the pre-P3 state — otherwise the assertions below prove
            // nothing about the migration.
            Assert.Equal(0, await ReportsReadRowsAsync(connectionString));
            Assert.Equal(0, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.SuperAdmin));

            var before = new Dictionary<string, int>();
            foreach (var role in EamsRoleNames.All)
                before[role] = await AllGrantsAsync(connectionString, role);

            await MigrateToAsync(connectionString, null);

            Assert.Equal(1, await ReportsReadRowsAsync(connectionString));

            foreach (var admin in new[] { EamsRoleNames.SuperAdmin, EamsRoleNames.SchoolAdmin })
            {
                Assert.True(
                    await ReportsReadGrantsAsync(connectionString, admin) == 1,
                    $"{admin} does not hold reports.read after the migration. On every database seeded " +
                    "before P3 the startup seed will never add it — it does not reconcile existing roles " +
                    "— so this migration is the only thing standing between the administrators and a 403.");
                Assert.Equal(before[admin] + 1, await AllGrantsAsync(connectionString, admin));
            }

            foreach (var other in new[] { EamsRoleNames.Organizer, EamsRoleNames.Viewer })
            {
                Assert.Equal(0, await ReportsReadGrantsAsync(connectionString, other));
                Assert.Equal(before[other], await AllGrantsAsync(connectionString, other));
            }

            // And the next start of the new build adds nothing further.
            await StartUpAsync(connectionString);
            Assert.Equal(1, await ReportsReadRowsAsync(connectionString));
            Assert.Equal(1, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(1, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// On an empty database the migration runs first, while no role exists, and the seed runs after it.
    /// The two must not both grant: exactly one row per grant, and exactly one <c>Permissions</c> row —
    /// also after a second start.
    /// </summary>
    [Fact]
    public async Task Migration_and_startup_seed_leave_exactly_one_reports_read_grant_per_admin_role_on_a_fresh_database()
    {
        var databaseName = $"EAMS_Rpt_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await StartUpAsync(connectionString);
            await StartUpAsync(connectionString);

            Assert.Equal(1, await ReportsReadRowsAsync(connectionString));
            Assert.Equal(1, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(1, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
            Assert.Equal(0, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.Organizer));
            Assert.Equal(0, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.Viewer));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// <c>Down</c> removes the code and its two admin grants by name, <b>whoever inserted them</b>
    /// (JJ, P3) — exercised on both paths: grants inserted by the migration on a pre-P3 database, and
    /// grants inserted by the startup seed on a fresh one. Every other grant survives untouched.
    /// </summary>
    [Fact]
    public async Task Migration_down_removes_reports_read_and_its_grants_whoever_inserted_them()
    {
        var insertedByMigration = $"EAMS_Rpt_{Guid.NewGuid():N}";
        var insertedBySeed = $"EAMS_Rpt_{Guid.NewGuid():N}";

        try
        {
            var migrated = await ArrangePreReportsDatabaseAsync(insertedByMigration);
            await MigrateToAsync(migrated, null);

            var seeded = await Sql.CreateScratchDatabaseAsync(insertedBySeed);
            await StartUpAsync(seeded);

            foreach (var connectionString in new[] { migrated, seeded })
            {
                Assert.Equal(1, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.SuperAdmin));

                var before = new Dictionary<string, int>();
                foreach (var role in EamsRoleNames.All)
                    before[role] = await AllGrantsAsync(connectionString, role);

                await MigrateToAsync(connectionString, PreviousMigration);

                Assert.Equal(0, await ReportsReadRowsAsync(connectionString));

                // Migrating this far back also unwinds the later GrantUserAdminToAdminRoles migration,
                // whose Down removes the two user-admin grants (users.read, users.write) from each admin
                // role. So an admin loses three grants in total, not one — reports.read plus those two.
                // (Any future grant migration added after this one widens this delta again; it is the
                // cost of a down-test that must cross every later migration to reach its target.)
                const int adminGrantsRemovedByLaterMigrations = 3;
                Assert.Equal(before[EamsRoleNames.SuperAdmin] - adminGrantsRemovedByLaterMigrations, await AllGrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
                Assert.Equal(before[EamsRoleNames.SchoolAdmin] - adminGrantsRemovedByLaterMigrations, await AllGrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
                Assert.Equal(before[EamsRoleNames.Organizer], await AllGrantsAsync(connectionString, EamsRoleNames.Organizer));
                Assert.Equal(before[EamsRoleNames.Viewer], await AllGrantsAsync(connectionString, EamsRoleNames.Viewer));
                Assert.Equal(EamsRoleNames.All.Count, await CountAsync(connectionString, "SELECT COUNT(*) FROM [Roles];"));
            }
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(insertedByMigration);
            await Sql.DropScratchDatabaseAsync(insertedBySeed);
        }
    }

    /// <summary>
    /// <c>Down</c> refuses — and changes nothing — when an operator has granted <c>reports.read</c> to
    /// a role other than the two admins. Deleting that grant would undo an operator's decision from a
    /// rollback, and leaving it would make the <c>Permissions</c> delete fail on its foreign key.
    /// </summary>
    [Fact]
    public async Task Migration_down_refuses_while_an_operator_grant_of_reports_read_exists()
    {
        var databaseName = $"EAMS_Rpt_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await StartUpAsync(connectionString);
            await ExecuteAsync(connectionString, $"""
                INSERT INTO [RolePermissions] ([Id], [RoleId], [PermissionId])
                SELECT NEWID(), r.[Id], p.[Id]
                FROM [Roles] AS r CROSS JOIN [Permissions] AS p
                WHERE r.[Name] = N'{EamsRoleNames.Organizer}' AND p.[Code] = N'{EamsPermissions.ReportsRead}';
                """);

            var refusal = await Assert.ThrowsAsync<SqlException>(() => MigrateToAsync(connectionString, PreviousMigration));
            Assert.Equal(51002, refusal.Number);

            // Nothing moved: the migration's transaction rolled the whole Down back — including the
            // history row EF deletes as the last step of a Down. Were it gone, the database would
            // claim to sit at the previous migration while still holding this one's rows.
            Assert.Equal(1, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM [__EFMigrationsHistory] " +
                "WHERE [MigrationId] = N'20260921051300_GrantReportsReadToAdminRoles';"));
            Assert.Equal(1, await ReportsReadRowsAsync(connectionString));
            Assert.Equal(1, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(1, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
            Assert.Equal(1, await ReportsReadGrantsAsync(connectionString, EamsRoleNames.Organizer));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }
}
