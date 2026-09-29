using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// <b>Grants the new <c>users.read</c> and <c>users.write</c> codes to the SuperAdmin and SchoolAdmin
    /// roles that already exist.</b> Data only — no schema change, no <c>DROP</c>, no <c>ALTER</c>. The
    /// exact shape and reasoning of <c>GrantReportsReadToAdminRoles</c>, for the two codes the RBAC admin
    /// phase adds.
    ///
    /// <para>
    /// <b>Why a migration and not the startup seed.</b> <c>RbacSeed.SeedRolesAsync</c> grants only when it
    /// <em>creates</em> a role and never reconciles one that exists, so on every database seeded before
    /// this phase the seed would add the two <c>Permissions</c> rows and grant them to nobody — and the
    /// user-admin routes would answer the administrators 403.
    /// </para>
    ///
    /// <para>
    /// Idempotent with the seed in either order (guarded by <c>NOT EXISTS</c> on the natural keys),
    /// literals rather than constants (a migration records what was true when written, and
    /// <c>EAMS.Infrastructure</c> cannot see <c>EamsPermissions</c>), and valid on SQL Server 2012.
    /// </para>
    /// </summary>
    public partial class GrantUserAdminToAdminRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM [Permissions] WHERE [Code] = N'users.read')
                    INSERT INTO [Permissions] ([Id], [Code], [Description])
                    VALUES (NEWID(), N'users.read', NULL);

                IF NOT EXISTS (SELECT 1 FROM [Permissions] WHERE [Code] = N'users.write')
                    INSERT INTO [Permissions] ([Id], [Code], [Description])
                    VALUES (NEWID(), N'users.write', NULL);

                INSERT INTO [RolePermissions] ([Id], [RoleId], [PermissionId])
                SELECT NEWID(), r.[Id], p.[Id]
                FROM [Roles] AS r
                CROSS JOIN [Permissions] AS p
                WHERE r.[Name] IN (N'SuperAdmin', N'SchoolAdmin')
                  AND p.[Code] IN (N'users.read', N'users.write')
                  AND NOT EXISTS (
                      SELECT 1 FROM [RolePermissions] AS rp
                      WHERE rp.[RoleId] = r.[Id] AND rp.[PermissionId] = p.[Id]);
                """);
        }

        /// <summary>
        /// Removes the two codes and their admin grants by name, whoever inserted them — this migration or
        /// the startup seed. Refuses (and changes nothing) if an operator has granted either to any other
        /// role, exactly as <c>GrantReportsReadToAdminRoles.Down</c> does.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1
                    FROM [RolePermissions] AS rp
                    JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                    JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                    WHERE p.[Code] IN (N'users.read', N'users.write')
                      AND r.[Name] NOT IN (N'SuperAdmin', N'SchoolAdmin'))
                    THROW 51003,
                        N'users.read or users.write is granted to a role other than SuperAdmin or SchoolAdmin. That grant was made by an operator, not by this migration, and rolling back would have to delete it. Revoke it deliberately, then roll back.',
                        1;

                DELETE rp
                FROM [RolePermissions] AS rp
                JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                WHERE p.[Code] IN (N'users.read', N'users.write')
                  AND r.[Name] IN (N'SuperAdmin', N'SchoolAdmin');

                DELETE FROM [Permissions] WHERE [Code] IN (N'users.read', N'users.write');
                """);
        }
    }
}
