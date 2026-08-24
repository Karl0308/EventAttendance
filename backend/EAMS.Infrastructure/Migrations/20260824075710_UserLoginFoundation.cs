using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 6a — the data foundation for §11 user login: the <c>RefreshTokens</c> table and a
    /// nullable <c>AuditLogs.SchoolId</c>.
    ///
    /// <para>
    /// <b>Scaffolded unedited, and purely additive.</b> <c>Up</c> is one nullable column, one new
    /// table, four new indexes and two foreign keys. <b>There is no <c>DROP</c>, no
    /// <c>ALTER COLUMN</c>, no re-type and no rename anywhere in it</b> — nothing that exists changes
    /// shape, nothing changes nullability, and no backfill is needed: every <c>AuditLogs</c> row that
    /// predates this truthfully has no school, which is exactly what <c>NULL</c> says. It is therefore
    /// safe on any population.
    /// </para>
    ///
    /// <para>
    /// <b>What it deliberately does NOT do: it does not touch <c>Users.RefreshTokenHash</c>.</b>
    /// §4.11's single-column design cannot express rotation, replay detection or more than one
    /// session, so this table supersedes it — but the column is <em>kept</em> and left permanently
    /// NULL rather than dropped. Dropping it would be data-loss SQL under the global rule, and it is
    /// the plan's own column, so removing it would make §4.11 and the schema disagree with nothing
    /// recording why. The identical decision one phase earlier is <c>Device.ApiKey</c>, and
    /// <c>RbacSchemaTests</c> pins both halves here: the column still exists, and nothing ever writes
    /// it.
    /// </para>
    ///
    /// <para>
    /// <b>Also deliberately absent: any change to §4.11's RBAC tables.</b> <c>Users</c>, <c>Roles</c>,
    /// <c>Permissions</c>, <c>UserRoles</c> and <c>RolePermissions</c> were created by
    /// <c>Section4Baseline</c> and are exactly right as they stand — this phase fills them with
    /// reference data (see <c>RbacSeed</c>) rather than reshaping them.
    /// </para>
    ///
    /// <para>
    /// <b><c>UX_RefreshTokens_TokenHash</c> is unique and unfiltered, and both halves matter.</b>
    /// <c>TokenHash</c> is <c>NOT NULL</c>, so the SQL Server provider attaches no automatic
    /// <c>IS NOT NULL</c> predicate — but a later change making the column nullable would silently
    /// acquire one, and a unique index whose filter excludes every row constrains nothing while
    /// appearing present in every schema diff. That is not a hypothetical here:
    /// <c>UX_Attendance_Event_Student_Occurrence</c> shipped in exactly that state and left the whole
    /// attendance table unconstrained. Verified against <c>sys.indexes.filter_definition</c> by
    /// <c>RbacSchemaTests</c> rather than against the EF model, per the standing rule that a unique
    /// index is checked in the database and not in the modelling layer.
    /// </para>
    ///
    /// <para>
    /// <b><c>Down</c> is honest rather than lossless, and is written down as such.</b> Dropping
    /// <c>RefreshTokens</c> discards every live session — the practical consequence is that everyone
    /// signs in again, not that anything of record is lost, since a refresh token is a credential and
    /// not data. Dropping <c>AuditLogs.SchoolId</c> discards the tenant attribution on audit rows
    /// written after this migration ran, and those rows survive with everything else intact. Neither
    /// is a reason to revert casually, and both are stated so nobody has to infer them.
    /// </para>
    /// </summary>
    public partial class UserLoginFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SchoolId",
                table: "AuditLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FamilyExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReplacedByTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_SchoolId",
                table: "AuditLogs",
                column: "SchoolId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_FamilyId",
                table: "RefreshTokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId_IssuedAt",
                table: "RefreshTokens",
                columns: new[] { "UserId", "IssuedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_Schools_SchoolId",
                table: "AuditLogs",
                column: "SchoolId",
                principalTable: "Schools",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_Schools_SchoolId",
                table: "AuditLogs");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_SchoolId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "SchoolId",
                table: "AuditLogs");
        }
    }
}
