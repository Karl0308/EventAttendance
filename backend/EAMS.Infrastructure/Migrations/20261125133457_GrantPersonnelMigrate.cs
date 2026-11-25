using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// <b>Grants the new <c>personnel.migrate</c> code to the SuperAdmin and SchoolAdmin roles that already
    /// exist</b> (JJ, 2026-11-25 — <c>POST /personnel/migrate-from-students</c>). Data only — no schema
    /// change, no <c>DROP</c>, no <c>ALTER</c>. The sibling of <c>GrantPersonnelPermissionsToAdminRoles</c>;
    /// the reasoning and the SQL 2012 constraints are the same and are recorded there.
    ///
    /// <para>
    /// <b>Needed because <c>RbacSeed.SeedRolesAsync</c> never reconciles a role that exists:</b> on every
    /// database seeded before this migration the seed would add the <c>Permissions</c> row, grant it to
    /// nobody, and the route would answer 403 to the administrators it is for.
    /// </para>
    /// </summary>
    public partial class GrantPersonnelMigrate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM [Permissions] WHERE [Code] = N'personnel.migrate')
                    INSERT INTO [Permissions] ([Id], [Code], [Description])
                    VALUES (NEWID(), N'personnel.migrate', NULL);

                INSERT INTO [RolePermissions] ([Id], [RoleId], [PermissionId])
                SELECT NEWID(), r.[Id], p.[Id]
                FROM [Roles] AS r
                CROSS JOIN [Permissions] AS p
                WHERE r.[Name] IN (N'SuperAdmin', N'SchoolAdmin')
                  AND p.[Code] = N'personnel.migrate'
                  AND NOT EXISTS (
                      SELECT 1 FROM [RolePermissions] AS rp
                      WHERE rp.[RoleId] = r.[Id] AND rp.[PermissionId] = p.[Id]);
                """);
        }

        /// <summary>
        /// Removes the code and its admin grants by name, whoever inserted them — this migration or the
        /// startup seed. Refuses (<c>THROW 51006</c>, and changes nothing) if an operator has granted it to
        /// any other role, exactly as <c>GrantPersonnelPermissionsToAdminRoles.Down</c> does with 51005.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1
                    FROM [RolePermissions] AS rp
                    JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                    JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                    WHERE p.[Code] = N'personnel.migrate'
                      AND r.[Name] NOT IN (N'SuperAdmin', N'SchoolAdmin'))
                    THROW 51006,
                        N'personnel.migrate is granted to a role other than SuperAdmin or SchoolAdmin. That grant was made by an operator, not by this migration, and rolling back would have to delete it. Revoke it deliberately, then roll back.',
                        1;

                DELETE rp
                FROM [RolePermissions] AS rp
                JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                WHERE p.[Code] = N'personnel.migrate'
                  AND r.[Name] IN (N'SuperAdmin', N'SchoolAdmin');

                DELETE FROM [Permissions] WHERE [Code] = N'personnel.migrate';
                """);
        }
    }
}
