using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PreRegistrationEventLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "PreRegistrationSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreRegistrationSessions_EventId",
                table: "PreRegistrationSessions",
                column: "EventId");

            migrationBuilder.AddForeignKey(
                name: "FK_PreRegistrationSessions_Events_EventId",
                table: "PreRegistrationSessions",
                column: "EventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PreRegistrationSessions_Events_EventId",
                table: "PreRegistrationSessions");

            migrationBuilder.DropIndex(
                name: "IX_PreRegistrationSessions_EventId",
                table: "PreRegistrationSessions");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "PreRegistrationSessions");
        }
    }
}
