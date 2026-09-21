using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// <b>Grants the new <c>reports.read</c> code to the SuperAdmin and SchoolAdmin roles that already
    /// exist.</b> Data only — no schema change, no <c>DROP</c>, no <c>ALTER</c>.
    ///
    /// <para>
    /// <b>Why a migration and not the startup seed.</b> <c>RbacSeed.SeedRolesAsync</c> grants only
    /// when it <em>creates</em> a role and deliberately never reconciles one that exists — an
    /// administrator's narrowing must survive the next deploy. So on every database seeded before this
    /// code existed (dev machines and the deployed VM) the seed would add the <c>Permissions</c> row
    /// and grant it to nobody, and the reports routes would answer the administrators 403.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent with the seed, in either order.</b> Every insert is guarded by <c>NOT EXISTS</c>
    /// on the natural keys (<c>Permissions.Code</c>, <c>Roles.Name</c>, the role/permission pair). On a
    /// fresh database this runs first, while no role exists: it inserts the <c>Permissions</c> row and
    /// grants nothing, and the seed then finds the code, creates the roles and grants once. The unique
    /// indexes <c>UX_Permissions_Code</c> and <c>UX_RolePermissions_Role_Permission</c> back both.
    /// </para>
    ///
    /// <para>
    /// <b>Literals, not <c>EamsPermissions.ReportsRead</c>.</b> A migration records what was true when it
    /// was written; one that read a constant would change meaning if the constant were ever renamed —
    /// and <c>EAMS.Infrastructure</c> cannot see that assembly anyway. Valid on SQL Server 2012
    /// (compatibility level 110): no <c>CREATE OR ALTER</c>, no <c>STRING_AGG</c>, no
    /// <c>DROP … IF EXISTS</c>.
    /// </para>
    /// </summary>
    public partial class GrantReportsReadToAdminRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM [Permissions] WHERE [Code] = N'reports.read')
                    INSERT INTO [Permissions] ([Id], [Code], [Description])
                    VALUES (NEWID(), N'reports.read', NULL);

                INSERT INTO [RolePermissions] ([Id], [RoleId], [PermissionId])
                SELECT NEWID(), r.[Id], p.[Id]
                FROM [Roles] AS r
                CROSS JOIN [Permissions] AS p
                WHERE r.[Name] IN (N'SuperAdmin', N'SchoolAdmin')
                  AND p.[Code] = N'reports.read'
                  AND NOT EXISTS (
                      SELECT 1 FROM [RolePermissions] AS rp
                      WHERE rp.[RoleId] = r.[Id] AND rp.[PermissionId] = p.[Id]);
                """);
        }

        /// <summary>
        /// Removes <c>reports.read</c> and its two admin grants <b>by name, whoever inserted them</b> —
        /// this migration or the startup seed (JJ, P3).
        ///
        /// <para>
        /// The only purpose of rolling back is returning to a build that has never heard of the code,
        /// and that build's <c>RbacSeedTests</c> refuse a <c>Permissions</c> row its registry does not
        /// declare. It removes access grants, never user data.
        /// </para>
        ///
        /// <para>
        /// <b>It refuses rather than widen what it deletes.</b> If an operator has granted
        /// <c>reports.read</c> to any other role, the <c>Permissions</c> row cannot go (its foreign
        /// keys are <c>Restrict</c>), and deleting that grant too would be undoing an operator's decision
        /// from a rollback. So it throws before touching anything, in the style of
        /// <c>SisImportPipeline</c>'s guarded rollback, and the migration's transaction leaves the
        /// database as it was.
        /// </para>
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1
                    FROM [RolePermissions] AS rp
                    JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                    JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                    WHERE p.[Code] = N'reports.read'
                      AND r.[Name] NOT IN (N'SuperAdmin', N'SchoolAdmin'))
                    THROW 51002,
                        N'reports.read is granted to a role other than SuperAdmin or SchoolAdmin. That grant was made by an operator, not by this migration, and rolling back would have to delete it. Revoke it deliberately, then roll back.',
                        1;

                DELETE rp
                FROM [RolePermissions] AS rp
                JOIN [Permissions] AS p ON p.[Id] = rp.[PermissionId]
                JOIN [Roles] AS r ON r.[Id] = rp.[RoleId]
                WHERE p.[Code] = N'reports.read'
                  AND r.[Name] IN (N'SuperAdmin', N'SchoolAdmin');

                DELETE FROM [Permissions] WHERE [Code] = N'reports.read';
                """);
        }
    }
}
