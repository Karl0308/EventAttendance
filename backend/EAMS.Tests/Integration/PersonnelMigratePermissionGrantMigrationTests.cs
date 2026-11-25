using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Infrastructure;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b><c>GrantPersonnelMigrate</c> — the data-only migration that gives existing installations'
/// administrators <c>personnel.migrate</c></b> (JJ, 2026-11-25). The sibling of
/// <see cref="PersonnelPermissionGrantMigrationTests"/>: the seed never reconciles an existing role, so
/// without this migration the admins of an already-seeded database would get 403 on
/// <c>POST /personnel/migrate-from-students</c>. Raw SQL reads, never the EF model.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelMigratePermissionGrantMigrationTests : IntegrationTest
{
    public PersonnelMigratePermissionGrantMigrationTests(SqlServerFixture sql) : base(sql) { }

    private const string PreviousMigration = "GrantPersonnelPermissionsToAdminRoles";

    private static RbacReferenceData PreMigrateReferenceData { get; } = new(
        Permissions: [.. EamsRoles.ReferenceData.Permissions
            .Where(p => p.Code != EamsPermissions.PersonnelMigrate)],
        Roles: [.. EamsRoles.ReferenceData.Roles.Select(r => r with
        {
            PermissionCodes = [.. r.PermissionCodes.Where(c => c != EamsPermissions.PersonnelMigrate)],
        })]);

    private static async Task<int> CountAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static Task<int> PermissionRowsAsync(string connectionString) =>
        CountAsync(connectionString, "SELECT COUNT(*) FROM [Permissions] WHERE [Code] = N'personnel.migrate';");

    private static Task<int> GrantsAsync(string connectionString, string roleName) =>
        CountAsync(connectionString, $"""
            SELECT COUNT(*)
            FROM [RolePermissions] AS rp
            JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
            JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
            WHERE p.[Code] = N'personnel.migrate' AND r.[Name] = N'{roleName}';
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
    public async Task Migration_grants_personnel_migrate_to_the_two_admin_roles_only_and_down_removes_it()
    {
        var databaseName = $"EAMS_PerMig_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await MigrateToAsync(connectionString, PreviousMigration);
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await RbacSeed.ApplyAsync(db, PreMigrateReferenceData, NullLogger.Instance);
            Assert.Equal(0, await PermissionRowsAsync(connectionString));

            await MigrateToAsync(connectionString, null);

            Assert.Equal(1, await PermissionRowsAsync(connectionString));
            Assert.Equal(1, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));
            Assert.Equal(1, await GrantsAsync(connectionString, EamsRoleNames.SchoolAdmin));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.Organizer));
            Assert.Equal(0, await GrantsAsync(connectionString, EamsRoleNames.Viewer));

            // A later start of the new build adds nothing further.
            await StartUpAsync(connectionString);
            Assert.Equal(1, await PermissionRowsAsync(connectionString));
            Assert.Equal(1, await GrantsAsync(connectionString, EamsRoleNames.SuperAdmin));

            await MigrateToAsync(connectionString, PreviousMigration);

            Assert.Equal(0, await PermissionRowsAsync(connectionString));
            // The earlier personnel codes belong to the migration this did not cross.
            Assert.Equal(2, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM [Permissions] WHERE [Code] IN (N'personnel.read', N'personnel.write');"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }
}
