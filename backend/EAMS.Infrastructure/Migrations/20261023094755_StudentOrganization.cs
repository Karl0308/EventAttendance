using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StudentOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Organization",
                table: "Students",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-losing by decision (ADR-011 D-79, JJ 2026-10-23): the values come from the SIS file,
            // so a re-import restores them after a rollback. Read D-79 before flagging this.
            migrationBuilder.DropColumn(
                name: "Organization",
                table: "Students");
        }
    }
}
