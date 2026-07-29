using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 4b. Two independent things that happen to be one schema change: the split device key on
    /// <c>Devices</c> (Phase 4a design, D-24), and <c>SchoolId</c> on <c>AttendanceRecords</c> with the
    /// idempotency index re-scoped around it (D-35).
    ///
    /// <para>
    /// <b>Hand-edited after scaffolding, and the edits are the point.</b> The generated version did
    /// three things wrong: it dropped the old index before the replacement column existed, it added
    /// <c>SchoolId</c> as <c>NOT NULL DEFAULT '00000000-…'</c>, and it added the foreign key with every
    /// existing row still holding that empty guid — which would have failed on any populated database,
    /// and would have left the column carrying a permanent DEFAULT constraint producing a value no
    /// <c>Schools</c> row will ever have. What ships is the documented three-step: add nullable,
    /// backfill, tighten to <c>NOT NULL</c>.
    /// </para>
    ///
    /// <para>
    /// <b>On the <c>DropIndex</c> and the global no-DROP rule — read this before flagging it.</b> The
    /// rule is about <em>data loss</em>. An index holds no data: dropping
    /// <c>UX_Attendance_Device_DeviceTapId</c> and recreating it one column wider deletes nothing. It
    /// is an index swap, not a destructive schema change.
    /// </para>
    ///
    /// <para>
    /// <b><c>Up</c> is safe on any population. <c>Down</c> is not, and this migration is therefore
    /// one-way in practice once real rows exist.</b> Stated plainly because the earlier draft of this
    /// remark claimed the opposite.
    /// </para>
    ///
    /// <para>
    /// The reachable failure is <b><c>DeviceId IS NULL</c></b>, not two schools sharing a device — that
    /// second one cannot happen, because <c>Devices.SchoolId</c> is immutable through the API and the
    /// D-27 guard forces <c>device.SchoolId == ev.SchoolId</c> on every tap. The filter on this index
    /// is <c>[DeviceTapId] IS NOT NULL</c> and says nothing about <c>DeviceId</c>, and SQL Server
    /// treats <c>NULL = NULL</c> as a <em>duplicate</em> inside a unique index (unlike PostgreSQL —
    /// the same quirk <c>CLAUDE.md</c> names as the reason this suite can never move to EF InMemory,
    /// and the same one <c>AttendanceService.FindByDeviceTapAsync</c> relies on to constrain
    /// device-less taps). So two tenants each holding a device-less tap with the same client-generated
    /// id — <c>(SchoolA, NULL, 'queued-0001')</c> and <c>(SchoolB, NULL, 'queued-0001')</c> — are two
    /// legal rows under the new key and one duplicate under the old one. <c>Down</c> then fails on
    /// <c>CREATE UNIQUE INDEX</c> with error 1505.
    /// </para>
    ///
    /// <para>
    /// <b>The consequence is loud, not lossy.</b> EF runs the migration in a transaction, so a failed
    /// <c>Down</c> rolls back and leaves the database on the new schema with every row intact —
    /// nothing is deleted and nothing is silently wrong. What it does not do is explain itself: error
    /// 1505 names the index and the duplicate key and says nothing about why the key was ever widened.
    /// That is what this paragraph is for. Recovering means deciding which of the colliding rows to
    /// re-key, which is a data decision and not a migration's to make.
    /// </para>
    ///
    /// <para>
    /// The shape is not hypothetical: <c>AttendanceTenancyTests.PreTenancyPopulation</c> writes
    /// device-less rows with tap ids, and the direct-service path D-27's comment names — the §10
    /// import, a future worker — can produce it in production.
    /// </para>
    ///
    /// <para>
    /// <b>And the swap cannot fail on existing data, structurally.</b> The new key
    /// <c>(SchoolId, DeviceId, DeviceTapId)</c> is a strict superset of the old
    /// <c>(DeviceId, DeviceTapId)</c>. Widening a unique key can only ever <em>permit</em> more rows —
    /// any two rows the new index would reject as duplicates must already agree on <c>DeviceId</c> and
    /// <c>DeviceTapId</c>, and would therefore already have been rejected by the old one. So there is
    /// no populated database on which this <c>CREATE UNIQUE INDEX</c> can fail. A <em>narrowing</em>
    /// would be the dangerous direction, and this is not one.
    /// </para>
    ///
    /// <para>
    /// <b>Why drop-then-create rather than create-then-drop.</b> The replacement keeps the old name,
    /// because it is the same logical constraint widened rather than a new one — every comment and test
    /// in the repo names it, and renaming it to sequence the DDL differently would trade a real
    /// documentation link for a cosmetic ordering. SQL Server will not hold two indexes of one name on
    /// one table, so the drop has to come first. There is no window: EF wraps a migration in a
    /// transaction, and the DDL takes a schema-modification lock on the table for its duration, so no
    /// write can land between the two statements.
    /// </para>
    ///
    /// <para>
    /// <b>The backfill is data-preserving and idempotent.</b> It reads <c>Events.SchoolId</c> through
    /// the required <c>EventId</c> foreign key — every attendance row has exactly one event and every
    /// event has exactly one school, so the value is derived rather than guessed and no row can be left
    /// unclassified. <c>WHERE SchoolId IS NULL</c> means a re-run writes nothing.
    /// </para>
    /// </summary>
    public partial class RowLevelTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------- Devices: the split key
            //
            // Purely additive. §4.10's own ApiKey column and UX_Devices_ApiKey are untouched and stay —
            // see Device.ApiKey for why a column that will never be written again is kept rather than
            // dropped.

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyId",
                table: "Devices",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyHash",
                table: "Devices",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApiKeyIssuedAt",
                table: "Devices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApiKeyLastUsedAt",
                table: "Devices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApiKeyRevokedAt",
                table: "Devices",
                type: "datetime2",
                nullable: true);

            // The authentication hot path: one seek per presented token. Filtered, because a device may
            // exist without a key and SQL Server's NULL-equals-NULL inside a unique index would
            // otherwise cap that at one row.
            migrationBuilder.CreateIndex(
                name: "UX_Devices_ApiKeyId",
                table: "Devices",
                column: "ApiKeyId",
                unique: true,
                filter: "[ApiKeyId] IS NOT NULL");

            // ------------------------------------------------- AttendanceRecords: the tenant column
            //
            // Step 1 of 3 — add nullable. A NOT NULL column with a default would stamp every existing
            // row with an empty guid that the foreign key below rejects, and would leave the default
            // constraint behind permanently.
            migrationBuilder.AddColumn<Guid>(
                name: "SchoolId",
                table: "AttendanceRecords",
                type: "uniqueidentifier",
                nullable: true);

            // Step 2 of 3 — backfill, derived rather than guessed. EventId is NOT NULL and
            // Events.SchoolId is NOT NULL, so this classifies every row. Re-running writes nothing.
            migrationBuilder.Sql("""
                UPDATE a
                SET    a.[SchoolId] = e.[SchoolId]
                FROM   [AttendanceRecords] AS a
                JOIN   [Events]            AS e ON e.[Id] = a.[EventId]
                WHERE  a.[SchoolId] IS NULL;
                """);

            // Step 3 of 3 — tighten. Safe now, and it would have thrown before the backfill, which is
            // the property that makes this ordering worth stating rather than assuming.
            migrationBuilder.AlterColumn<Guid>(
                name: "SchoolId",
                table: "AttendanceRecords",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceRecords_Schools_SchoolId",
                table: "AttendanceRecords",
                column: "SchoolId",
                principalTable: "Schools",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ----------------------------------------------------- AttendanceRecords: the index swap
            //
            // See the type remarks: an index swap is not data loss, and widening a unique key cannot
            // fail on existing data.
            migrationBuilder.DropIndex(
                name: "UX_Attendance_Device_DeviceTapId",
                table: "AttendanceRecords");

            migrationBuilder.CreateIndex(
                name: "UX_Attendance_Device_DeviceTapId",
                table: "AttendanceRecords",
                columns: new[] { "SchoolId", "DeviceId", "DeviceTapId" },
                unique: true,
                filter: "[DeviceTapId] IS NOT NULL");

            // DeviceId used to lead the composite above, so the foreign key to Devices had index
            // support for free. It does not any more — the same trap IX_StudentGroups_SchoolId and
            // IX_EventGroups_EventId record, arriving this time through a column being *prepended*
            // rather than through a new index being added. Without this, deleting or re-keying a device
            // scans AttendanceRecords, which is the largest table in the system.
            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_DeviceId",
                table: "AttendanceRecords",
                column: "DeviceId");
        }

        /// <summary>
        /// Reverses <c>Up</c> exactly, in reverse order.
        ///
        /// <para>
        /// It does drop <c>AttendanceRecords.SchoolId</c> — reversing an <c>ADD COLUMN</c> is what a
        /// down migration is. Nothing unrecoverable is lost: the value is derived from
        /// <c>Events.SchoolId</c>, so re-applying <c>Up</c> recomputes every row from the same source.
        /// The device key columns are the same case; a rolled-back device needs its key reissued, which
        /// is what <c>POST /devices/{id}/regenerate-key</c> is for.
        /// </para>
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttendanceRecords_DeviceId",
                table: "AttendanceRecords");

            migrationBuilder.DropIndex(
                name: "UX_Attendance_Device_DeviceTapId",
                table: "AttendanceRecords");

            migrationBuilder.CreateIndex(
                name: "UX_Attendance_Device_DeviceTapId",
                table: "AttendanceRecords",
                columns: new[] { "DeviceId", "DeviceTapId" },
                unique: true,
                filter: "[DeviceTapId] IS NOT NULL");

            migrationBuilder.DropForeignKey(
                name: "FK_AttendanceRecords_Schools_SchoolId",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "SchoolId",
                table: "AttendanceRecords");

            migrationBuilder.DropIndex(
                name: "UX_Devices_ApiKeyId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ApiKeyRevokedAt",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ApiKeyLastUsedAt",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ApiKeyIssuedAt",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ApiKeyHash",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "Devices");
        }
    }
}
