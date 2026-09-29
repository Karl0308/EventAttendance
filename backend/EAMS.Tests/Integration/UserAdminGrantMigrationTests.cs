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
/// <b><c>GrantUserAdminToAdminRoles</c> — the migration that gives existing installations the new
/// <c>users.read</c> and <c>users.write</c> codes.</b> The sibling of
/// <c>ReportsReadGrantMigrationTests</c>, proving the same property for the RBAC admin phase: the seed
/// never reconciles an existing role, so this migration is the only thing standing between an
/// already-seeded installation's administrators and a 403 on the user-management routes. Every assertion
/// reads the tables with raw SQL, never through the EF model.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class UserAdminGrantMigrationTests : IntegrationTest
{
    public UserAdminGrantMigrationTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>The migration immediately before this one.</summary>
    private const string PreviousMigration = "EventClassifications";

    private static readonly string[] Codes = ["users.read", "users.write"];

    /// <summary>
    /// The reference data as a build from before this phase declared it: the same roles and grants, minus
    /// the two user-admin codes everywhere. Derived from the current matrix so the only difference is the
    /// one this migration is about.
    /// </summary>
    private static RbacReferenceData PreUserAdminReferenceData { get; } = new(
        Permissions: [.. EamsRoles.ReferenceData.Permissions
            .Where(p => !Codes.Contains(p.Code, StringComparer.Ordinal))],
        Roles: [.. EamsRoles.ReferenceData.Roles.Select(r => r with
        {
            PermissionCodes = [.. r.PermissionCodes.Where(c => !Codes.Contains(c, StringComparer.Ordinal))],
        })]);

    private static async Task<int> CountAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static Task<int> PermissionRowsAsync(string connectionString) =>
        CountAsync(connectionString,
            "SELECT COUNT(*) FROM [Permissions] WHERE [Code] IN (N'users.read', N'users.write');");

    private static Task<int> GrantsAsync(string connectionString, string roleName) =>
        CountAsync(connectionString, $"""
            SELECT COUNT(*)
            FROM [RolePermissions] AS rp
            JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
            JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
            WHERE p.[Code] IN (N'users.read', N'users.write') AND r.[Name] = N'{roleName}';
            """);

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

    private async Task<string> ArrangePreUserAdminDatabaseAsync(string databaseName)
    {
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);
        await MigrateToAsync(connectionString, PreviousMigration);

        await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            await RbacSeed.ApplyAsync(db, PreUserAdminReferenceData, NullLogger.Instance);

        return connectionString;
    }

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

    [Fact]
    public async Task Migration_grants_the_user_admin_codes_to_admin_roles_created_before_it()
    {
        var databaseName = $"EAMS_Usr_{Guid.NewGuid():N}";
        var connectionString = await ArrangePreUserAdminDatabaseAsync(databaseName);

        try
        {
            Assert.Equal(0, await PermissionRowsAsync(connectionString));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));

            var before = new Dictionary<string, int>();
            foreach (var role in EamsRoleNames.All)
                before[role] = await AllGrantsAsync(connectionString, role);

            await MigrateToAsync(connectionString, null);

            Assert.Equal(2, await PermissionRowsAsync(connectionString));

            foreach (var admin in new[] { EamsRoleNames.SuperAdmin, EamsRoleNames.SchoolAdmin })
            {
                Assert.Equal(2, await GrantsAsync(connectionString, admin));
                Assert.Equal(before[admin] + 2, await AllGrantsAsync(connectionString, admin));
            }

            foreach (var other in new[] { EamsRoleNames.Organizer, EamsRoleNames.Viewer })
            {
                Assert.Equal(0, await GrantsAsync(connectionString, other));
                Assert.Equal(before[other], await AllGrantsAsync(connectionString, other));
            }

            // A subsequent start of the new build adds nothing further.
            await StartUpAsync(connectionString);
            Assert.Equal(2, await PermissionRowsAsync(connectionString));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Migration_and_seed_leave_exactly_one_grant_per_code_per_admin_on_a_fresh_database()
    {
        var databaseName = $"EAMS_Usr_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await StartUpAsync(connectionString);
            await StartUpAsync(connectionString);

            Assert.Equal(2, await PermissionRowsAsync(connectionString));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.Organizer));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.Viewer));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Migration_down_removes_the_codes_and_their_admin_grants()
    {
        var databaseName = $"EAMS_Usr_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await StartUpAsync(connectionString);
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));

            var before = new Dictionary<string, int>();
            foreach (var role in EamsRoleNames.All)
                before[role] = await AllGrantsAsync(connectionString, role);

            await MigrateToAsync(connectionString, PreviousMigration);

            Assert.Equal(0, await PermissionRowsAsync(connectionString));
            Assert.Equal(before[EamsRoleNames.SuperAdmin] - 2, await AllGrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(before[EamsRoleNames.SchoolAdmin] - 2, await AllGrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
            Assert.Equal(before[EamsRoleNames.Organizer], await AllGrantsAsync(connectionString, EamsRoleNames.Organizer));
            Assert.Equal(EamsRoleNames.All.Count, await CountAsync(connectionString, "SELECT COUNT(*) FROM [Roles];"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }
}
