using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EventClassificationLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventClassificationId",
                table: "Events",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_EventClassificationId",
                table: "Events",
                column: "EventClassificationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_EventClassifications_EventClassificationId",
                table: "Events",
                column: "EventClassificationId",
                principalTable: "EventClassifications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_EventClassifications_EventClassificationId",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_EventClassificationId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "EventClassificationId",
                table: "Events");
        }
    }
}
