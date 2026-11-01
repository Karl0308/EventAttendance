using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// <b>Grants the new <c>personnel.read</c> and <c>personnel.write</c> codes to the SuperAdmin and
    /// SchoolAdmin roles that already exist</b> (ADR-015 D-104). Data only — no schema change, no
    /// <c>DROP</c>, no <c>ALTER</c>. The sibling of <c>GrantUserAdminToAdminRoles</c> and
    /// <c>GrantRoleAdminToAdminRoles</c>; the reasoning and the SQL 2012 constraints are the same and are
    /// recorded there and on <c>GrantReportsReadToAdminRoles</c>.
    ///
    /// <para>
    /// <b>Without it the administrators would lose the Personnel tab.</b> The personnel routes move off
    /// <c>students.*</c> onto these codes in the same change, and <c>RbacSeed.SeedRolesAsync</c> never
    /// reconciles a role that exists — so on every database seeded before this migration the seed would
    /// add the two <c>Permissions</c> rows, grant them to nobody, and the routes would answer 403.
    /// </para>
    /// </summary>
    public partial class GrantPersonnelPermissionsToAdminRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM [Permissions] WHERE [Code] = N'personnel.read')
                    INSERT INTO [Permissions] ([Id], [Code], [Description])
                    VALUES (NEWID(), N'personnel.read', NULL);

                IF NOT EXISTS (SELECT 1 FROM [Permissions] WHERE [Code] = N'personnel.write')
                    INSERT INTO [Permissions] ([Id], [Code], [Description])
                    VALUES (NEWID(), N'personnel.write', NULL);

                INSERT INTO [RolePermissions] ([Id], [RoleId], [PermissionId])
                SELECT NEWID(), r.[Id], p.[Id]
                FROM [Roles] AS r
                CROSS JOIN [Permissions] AS p
                WHERE r.[Name] IN (N'SuperAdmin', N'SchoolAdmin')
                  AND p.[Code] IN (N'personnel.read', N'personnel.write')
                  AND NOT EXISTS (
                      SELECT 1 FROM [RolePermissions] AS rp
                      WHERE rp.[RoleId] = r.[Id] AND rp.[PermissionId] = p.[Id]);
                """);
        }

        /// <summary>
        /// Removes the two codes and their admin grants by name, whoever inserted them — this migration or
        /// the startup seed. Refuses (<c>THROW 51005</c>, and changes nothing) if an operator has granted
        /// either to any other role, exactly as <c>GrantUserAdminToAdminRoles.Down</c> does with 51003.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1
                    FROM [RolePermissions] AS rp
                    JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                    JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                    WHERE p.[Code] IN (N'personnel.read', N'personnel.write')
                      AND r.[Name] NOT IN (N'SuperAdmin', N'SchoolAdmin'))
                    THROW 51005,
                        N'personnel.read or personnel.write is granted to a role other than SuperAdmin or SchoolAdmin. That grant was made by an operator, not by this migration, and rolling back would have to delete it. Revoke it deliberately, then roll back.',
                        1;

                DELETE rp
                FROM [RolePermissions] AS rp
                JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                WHERE p.[Code] IN (N'personnel.read', N'personnel.write')
                  AND r.[Name] IN (N'SuperAdmin', N'SchoolAdmin');

                DELETE FROM [Permissions] WHERE [Code] IN (N'personnel.read', N'personnel.write');
                """);
        }
    }
}
