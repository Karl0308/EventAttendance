using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 3a. Makes "this group is attached to this event" unique, which §4.8 never declared because
    /// nothing wrote <c>EventGroups</c> until <c>POST /events/{id}/attendees</c> existed.
    ///
    /// <para>
    /// <b>Purely additive — two CREATE INDEX and nothing else.</b> The first scaffold of this migration
    /// opened with a <c>DROP INDEX IX_EventGroups_EventId</c>: EF sees two composites leading with
    /// <c>EventId</c> and calls the single-column one redundant. It is not — both composites are
    /// filtered, and SQL Server cannot use a filtered index for a query that does not imply its
    /// predicate, so the audience read would have quietly gone to a table scan. The index is now
    /// declared explicitly in <c>EamsDbContext</c> so the scaffolder stops proposing it. Same trap,
    /// same fix, as <c>IX_StudentGroups_SchoolId</c> in the AcademicLayer migration.
    /// </para>
    ///
    /// <para>
    /// Safe to apply to a populated database: nothing has ever written this table outside test
    /// fixtures, so there is no pre-existing duplicate for the unique index to reject.
    /// </para>
    /// </summary>
    public partial class EventAudienceUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_EventGroups_Event_Group",
                table: "EventGroups",
                columns: new[] { "EventId", "StudentGroupId" },
                unique: true,
                filter: "[StudentGroupId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_EventGroups_Event_Student",
                table: "EventGroups",
                columns: new[] { "EventId", "StudentId" },
                unique: true,
                filter: "[StudentId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_EventGroups_Event_Group",
                table: "EventGroups");

            migrationBuilder.DropIndex(
                name: "UX_EventGroups_Event_Student",
                table: "EventGroups");
        }
    }
}
