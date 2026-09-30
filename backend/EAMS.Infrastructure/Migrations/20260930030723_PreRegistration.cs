using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PreRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PreRegistrationSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AudienceDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Capacity = table.Column<int>(type: "int", nullable: false),
                    IsClosed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreRegistrationSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreRegistrationSessions_AudienceDefinitions_AudienceDefinitionId",
                        column: x => x.AudienceDefinitionId,
                        principalTable: "AudienceDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreRegistrationSessions_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PreRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersonnelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttendeeType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreRegistrations", x => x.Id);
                    table.CheckConstraint("CK_PreRegistrations_StudentOrPersonnel", "([StudentId] IS NOT NULL AND [PersonnelId] IS NULL) OR ([StudentId] IS NULL AND [PersonnelId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PreRegistrations_Personnel_PersonnelId",
                        column: x => x.PersonnelId,
                        principalTable: "Personnel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreRegistrations_PreRegistrationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "PreRegistrationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreRegistrations_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreRegistrations_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreRegistrations_PersonnelId",
                table: "PreRegistrations",
                column: "PersonnelId");

            migrationBuilder.CreateIndex(
                name: "IX_PreRegistrations_SchoolId",
                table: "PreRegistrations",
                column: "SchoolId");

            migrationBuilder.CreateIndex(
                name: "IX_PreRegistrations_SessionId",
                table: "PreRegistrations",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_PreRegistrations_StudentId",
                table: "PreRegistrations",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "UX_PreRegistrations_Session_Personnel",
                table: "PreRegistrations",
                columns: new[] { "SessionId", "PersonnelId" },
                unique: true,
                filter: "[PersonnelId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_PreRegistrations_Session_Student",
                table: "PreRegistrations",
                columns: new[] { "SessionId", "StudentId" },
                unique: true,
                filter: "[StudentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PreRegistrationSessions_AudienceDefinitionId",
                table: "PreRegistrationSessions",
                column: "AudienceDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_PreRegistrationSessions_SchoolId",
                table: "PreRegistrationSessions",
                column: "SchoolId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreRegistrations");

            migrationBuilder.DropTable(
                name: "PreRegistrationSessions");
        }
    }
}
