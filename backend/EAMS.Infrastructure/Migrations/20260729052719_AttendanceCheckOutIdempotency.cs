using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 4c, D-34 — <c>AttendanceRecords.CheckOutDeviceTapId</c> and the filtered unique index
    /// behind it, so each half of a <c>TimeInOut</c> pair carries its own idempotency key.
    ///
    /// <para>
    /// <b>Scaffolded unedited, which is worth stating because the previous migration was not.</b> This
    /// one is purely additive: a new nullable column and a new index over it. Nothing is dropped,
    /// nothing is re-typed, no existing column changes nullability, and no backfill is needed — every
    /// row that exists predates the check-out key and truthfully has none, which is exactly what
    /// <c>NULL</c> says. It is therefore safe on any population, and unlike <c>RowLevelTenancy</c> its
    /// <c>Down</c> is safe too <em>as schema</em>: dropping the index deletes no data, and dropping the
    /// column deletes only values written after this migration ran. That last clause is the honest
    /// caveat — a reverted database loses the check-out tap ids captured in the meantime, and their
    /// only consequence is that those check-outs stop being individually replayable.
    /// </para>
    ///
    /// <para>
    /// <b>Why the index has to be filtered, in the specific way it is.</b> SQL Server compares
    /// <c>NULL</c> as <em>equal</em> inside a unique index, so an unfiltered
    /// <c>UNIQUE(SchoolId, DeviceId, CheckOutDeviceTapId)</c> would admit exactly one row per
    /// <c>(school, device)</c> with no check-out — which is every row in the table today and every
    /// <c>Single</c>-mode row forever. The filter is the same predicate the provider would have
    /// inferred, and it is stated anyway for the reason <c>EamsDbContext</c> records throughout: an
    /// index that happens to be correct because of a provider default is one refactor away from not
    /// being.
    /// </para>
    ///
    /// <para>
    /// Verified against <c>sys.indexes.filter_definition</c> by <c>SchemaConstraintTests</c> rather
    /// than against the EF model, per the standing rule that a unique index over a nullable column is
    /// checked in the database and not in the modelling layer.
    /// </para>
    /// </summary>
    public partial class AttendanceCheckOutIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckOutDeviceTapId",
                table: "AttendanceRecords",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Attendance_Device_CheckOutDeviceTapId",
                table: "AttendanceRecords",
                columns: new[] { "SchoolId", "DeviceId", "CheckOutDeviceTapId" },
                unique: true,
                filter: "[CheckOutDeviceTapId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Attendance_Device_CheckOutDeviceTapId",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "CheckOutDeviceTapId",
                table: "AttendanceRecords");
        }
    }
}
