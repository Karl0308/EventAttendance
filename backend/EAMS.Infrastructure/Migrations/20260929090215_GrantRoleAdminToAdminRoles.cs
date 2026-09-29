using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// <b>Grants the new <c>roles.read</c> and <c>roles.write</c> codes to the SuperAdmin and SchoolAdmin
    /// roles that already exist.</b> Data only. The sibling of <c>GrantUserAdminToAdminRoles</c>, for the
    /// two codes the Role Management increment adds; the reasoning and the SQL 2012 constraints are the
    /// same and are recorded there and on <c>GrantReportsReadToAdminRoles</c>.
    /// </summary>
    public partial class GrantRoleAdminToAdminRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM [Permissions] WHERE [Code] = N'roles.read')
                    INSERT INTO [Permissions] ([Id], [Code], [Description])
                    VALUES (NEWID(), N'roles.read', NULL);

                IF NOT EXISTS (SELECT 1 FROM [Permissions] WHERE [Code] = N'roles.write')
                    INSERT INTO [Permissions] ([Id], [Code], [Description])
                    VALUES (NEWID(), N'roles.write', NULL);

                INSERT INTO [RolePermissions] ([Id], [RoleId], [PermissionId])
                SELECT NEWID(), r.[Id], p.[Id]
                FROM [Roles] AS r
                CROSS JOIN [Permissions] AS p
                WHERE r.[Name] IN (N'SuperAdmin', N'SchoolAdmin')
                  AND p.[Code] IN (N'roles.read', N'roles.write')
                  AND NOT EXISTS (
                      SELECT 1 FROM [RolePermissions] AS rp
                      WHERE rp.[RoleId] = r.[Id] AND rp.[PermissionId] = p.[Id]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1
                    FROM [RolePermissions] AS rp
                    JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                    JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                    WHERE p.[Code] IN (N'roles.read', N'roles.write')
                      AND r.[Name] NOT IN (N'SuperAdmin', N'SchoolAdmin'))
                    THROW 51004,
                        N'roles.read or roles.write is granted to a role other than SuperAdmin or SchoolAdmin. That grant was made by an operator, not by this migration, and rolling back would have to delete it. Revoke it deliberately, then roll back.',
                        1;

                DELETE rp
                FROM [RolePermissions] AS rp
                JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                WHERE p.[Code] IN (N'roles.read', N'roles.write')
                  AND r.[Name] IN (N'SuperAdmin', N'SchoolAdmin');

                DELETE FROM [Permissions] WHERE [Code] IN (N'roles.read', N'roles.write');
                """);
        }
    }
}
