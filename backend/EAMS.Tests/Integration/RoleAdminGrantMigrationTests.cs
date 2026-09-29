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
/// <b><c>GrantRoleAdminToAdminRoles</c> — the migration that gives existing installations the new
/// <c>roles.read</c>/<c>roles.write</c> codes.</b> The sibling of <c>UserAdminGrantMigrationTests</c>; its
/// PreviousMigration is <c>GrantUserAdminToAdminRoles</c>, so migrating down to that point unwinds only
/// this migration — a clean −2, no cascade.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RoleAdminGrantMigrationTests : IntegrationTest
{
    public RoleAdminGrantMigrationTests(SqlServerFixture sql) : base(sql) { }

    private const string PreviousMigration = "GrantUserAdminToAdminRoles";
    private static readonly string[] Codes = ["roles.read", "roles.write"];

    private static RbacReferenceData PreRoleAdminReferenceData { get; } = new(
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
            "SELECT COUNT(*) FROM [Permissions] WHERE [Code] IN (N'roles.read', N'roles.write');");

    private static Task<int> GrantsAsync(string connectionString, string roleName) =>
        CountAsync(connectionString, $"""
            SELECT COUNT(*)
            FROM [RolePermissions] AS rp
            JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
            JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
            WHERE p.[Code] IN (N'roles.read', N'roles.write') AND r.[Name] = N'{roleName}';
            """);

    private static async Task MigrateToAsync(string connectionString, string? target)
    {
        await using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<string> ArrangePreRoleAdminDatabaseAsync(string databaseName)
    {
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);
        await MigrateToAsync(connectionString, PreviousMigration);

        await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
            await RbacSeed.ApplyAsync(db, PreRoleAdminReferenceData, NullLogger.Instance);

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
    public async Task Migration_grants_the_role_admin_codes_to_admin_roles_created_before_it()
    {
        var databaseName = $"EAMS_Rol_{Guid.NewGuid():N}";
        var connectionString = await ArrangePreRoleAdminDatabaseAsync(databaseName);

        try
        {
            Assert.Equal(0, await PermissionRowsAsync(connectionString));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));

            await MigrateToAsync(connectionString, null);

            Assert.Equal(2, await PermissionRowsAsync(connectionString));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.Organizer));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.Viewer));

            await StartUpAsync(connectionString);
            Assert.Equal(2, await PermissionRowsAsync(connectionString));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Migration_and_seed_leave_exactly_one_grant_per_code_per_admin_on_a_fresh_database()
    {
        var databaseName = $"EAMS_Rol_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await StartUpAsync(connectionString);
            await StartUpAsync(connectionString);

            Assert.Equal(2, await PermissionRowsAsync(connectionString));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.Organizer));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Migration_down_removes_the_codes_and_their_admin_grants()
    {
        var databaseName = $"EAMS_Rol_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await StartUpAsync(connectionString);
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));

            // PreviousMigration is GrantUserAdminToAdminRoles, so this unwinds only this migration.
            await MigrateToAsync(connectionString, PreviousMigration);

            Assert.Equal(0, await PermissionRowsAsync(connectionString));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
            // users.* survive — they belong to the earlier migration this did not cross.
            Assert.Equal(2, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM [Permissions] WHERE [Code] IN (N'users.read', N'users.write');"));
            Assert.Equal(EamsRoleNames.All.Count, await CountAsync(connectionString, "SELECT COUNT(*) FROM [Roles];"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }
}
