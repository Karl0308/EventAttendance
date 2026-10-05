using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EventAudienceDefinitionLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventAudienceDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AudienceDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventAudienceDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventAudienceDefinitions_AudienceDefinitions_AudienceDefinitionId",
                        column: x => x.AudienceDefinitionId,
                        principalTable: "AudienceDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventAudienceDefinitions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventAudienceDefinitions_AudienceDefinitionId",
                table: "EventAudienceDefinitions",
                column: "AudienceDefinitionId");

            migrationBuilder.CreateIndex(
                name: "UX_EventAudienceDefinitions_Event_Definition",
                table: "EventAudienceDefinitions",
                columns: new[] { "EventId", "AudienceDefinitionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventAudienceDefinitions");
        }
    }
}
