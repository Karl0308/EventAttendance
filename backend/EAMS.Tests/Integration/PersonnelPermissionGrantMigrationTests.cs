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
/// <b><c>GrantPersonnelPermissionsToAdminRoles</c> — the migration that gives existing installations the
/// new <c>personnel.read</c> and <c>personnel.write</c> codes</b> (ADR-015 D-104). The sibling of
/// <see cref="UserAdminGrantMigrationTests"/>: the seed never reconciles an existing role, and the
/// personnel routes move off <c>students.*</c> in the same change, so this migration is the only thing
/// standing between an already-seeded installation's administrators and a 403 on the Personnel tab. Every
/// assertion reads the tables with raw SQL, never through the EF model.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelPermissionGrantMigrationTests : IntegrationTest
{
    public PersonnelPermissionGrantMigrationTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>The migration immediately before this one.</summary>
    private const string PreviousMigration = "PersonnelAttendance";

    private const string MigrationId = "20261027150949_GrantPersonnelPermissionsToAdminRoles";

    /// <summary>This migration's own Down refusal (51003 is <c>GrantUserAdminToAdminRoles</c>'s).</summary>
    private const int DownRefused = 51005;

    private static readonly string[] Codes = [EamsPermissions.PersonnelRead, EamsPermissions.PersonnelWrite];

    /// <summary>
    /// The reference data as a build from before D-104 declared it: the same roles and grants, minus the
    /// two personnel codes everywhere.
    /// </summary>
    private static RbacReferenceData PrePersonnelReferenceData { get; } = new(
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

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<int> PermissionRowsAsync(string connectionString) =>
        CountAsync(connectionString,
            "SELECT COUNT(*) FROM [Permissions] WHERE [Code] IN (N'personnel.read', N'personnel.write');");

    private static Task<int> GrantsAsync(string connectionString, string roleName) =>
        CountAsync(connectionString, $"""
            SELECT COUNT(*)
            FROM [RolePermissions] AS rp
            JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
            JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
            WHERE p.[Code] IN (N'personnel.read', N'personnel.write') AND r.[Name] = N'{roleName}';
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
    public async Task Migration_grants_the_personnel_codes_to_SuperAdmin_and_SchoolAdmin_only()
    {
        var databaseName = $"EAMS_PerGr_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await MigrateToAsync(connectionString, PreviousMigration);
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await RbacSeed.ApplyAsync(db, PrePersonnelReferenceData, NullLogger.Instance);

            Assert.Equal(0, await PermissionRowsAsync(connectionString));

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

            // D-104's consequence: a role that holds students.read does not gain personnel access.
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
    public async Task Migration_down_removes_the_codes_and_their_admin_grants()
    {
        var databaseName = $"EAMS_PerGr_{Guid.NewGuid():N}";
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
            Assert.Equal(before[EamsRoleNames.Viewer], await AllGrantsAsync(connectionString, EamsRoleNames.Viewer));
            Assert.Equal(EamsRoleNames.All.Count, await CountAsync(connectionString, "SELECT COUNT(*) FROM [Roles];"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// <c>Down</c> refuses — and changes nothing — when an operator has granted a personnel code to a role
    /// other than the two admins: deleting that grant would undo an operator's decision from a rollback.
    /// </summary>
    [Fact]
    public async Task Migration_down_refuses_while_a_third_role_holds_a_personnel_code()
    {
        var databaseName = $"EAMS_PerGr_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await StartUpAsync(connectionString);
            await ExecuteAsync(connectionString, $"""
                INSERT INTO [RolePermissions] ([Id], [RoleId], [PermissionId])
                SELECT NEWID(), r.[Id], p.[Id]
                FROM [Roles] AS r CROSS JOIN [Permissions] AS p
                WHERE r.[Name] = N'{EamsRoleNames.Organizer}' AND p.[Code] = N'{EamsPermissions.PersonnelRead}';
                """);

            var refusal = await Assert.ThrowsAsync<SqlException>(() => MigrateToAsync(connectionString, PreviousMigration));
            Assert.Equal(DownRefused, refusal.Number);

            // Nothing moved: the migration's transaction rolled the whole Down back, history row included.
            Assert.Equal(1, await CountAsync(connectionString,
                $"SELECT COUNT(*) FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'{MigrationId}';"));
            Assert.Equal(2, await PermissionRowsAsync(connectionString));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(2, await GrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
            Assert.Equal(1, await GrantsAsync(connectionString, EamsRoleNames.Organizer));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }
}
