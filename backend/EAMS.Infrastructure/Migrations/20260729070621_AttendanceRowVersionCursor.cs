using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 4d, D-30 — <c>AttendanceRecords.RowVersion</c> and the index the live-attendance delta
    /// read seeks on. Additive: one database-generated column and one non-unique index.
    ///
    /// <para>
    /// <b>Safe on a populated table, and it needs no backfill.</b>
    /// <c>ALTER TABLE … ADD [RowVersion] rowversion NOT NULL</c> is a SQL Server special case — the
    /// engine stamps every existing row with a fresh value as part of the ALTER, so there is no window
    /// in which the column is NOT NULL and empty, and nothing to write a data migration for. Nothing is
    /// dropped, nothing is re-typed, no existing column changes nullability.
    /// </para>
    ///
    /// <para>
    /// <b>The scaffold was edited, and this is what changed.</b> <c>dotnet ef</c> emitted
    /// <c>defaultValue: new byte[0]</c> on the <c>AddColumn</c>, as it does for any non-nullable column
    /// added to an existing table. It is removed here. The SQL Server generator special-cases
    /// <c>rowversion</c> and omits the <c>DEFAULT</c> clause anyway — the generated script is
    /// <c>ADD [RowVersion] rowversion NOT NULL;</c> either way, verified with
    /// <c>dotnet ef migrations script</c> before and after — so this is a readability fix and not a
    /// behaviour one. It is worth making: SQL Server rejects a <c>DEFAULT</c> on a <c>timestamp</c>
    /// column outright, so a reader who trusts the C# is reading a statement that could not run, and
    /// the next person to copy this <c>AddColumn</c> onto an ordinary column would carry a default they
    /// did not choose.
    /// </para>
    ///
    /// <para>
    /// <b>The column is NOT a concurrency token, and the mapping is where that is decided.</b> See
    /// <c>EamsDbContext.ConfigureAttendanceRecords</c> and <c>AttendanceRecord.RowVersion</c>: EF's
    /// <c>.IsRowVersion()</c> would have made it one, which would append
    /// <c>AND [RowVersion] = @original</c> to the <c>TimeInOut</c> check-out <c>UPDATE</c> and raise
    /// <c>DbUpdateConcurrencyException</c> on a race the service already handles through its unique
    /// indexes. Nothing in this migration hints at that difference — the column definition is identical
    /// either way — which is exactly why it is written down here as well as there. <b>This phase does
    /// not change write semantics.</b>
    /// </para>
    ///
    /// <para>
    /// <b>The index costs something on writes, and the trade is deliberate — measured, not asserted.</b>
    /// A <c>rowversion</c> changes on every UPDATE, so every check-out and every manual override moves
    /// this index entry: a delete plus an insert in <c>IX_Attendance_EventId_RowVersion</c> on top of
    /// the row write. Inserts pay the ordinary cost. What it buys, on SQL Server 2022 with 40,000
    /// attendance rows across two tenants and statistics fully updated:
    /// <list type="bullet">
    ///   <item><b>With the index</b> — <c>Index Seek</c> on <c>IX_Attendance_EventId_RowVersion</c> plus
    ///   a clustered-index lookup: <b>3 logical reads</b>.</item>
    ///   <item><b>With it dropped</b> — a clustered-index scan: <b>2057 logical reads</b>, once per poll,
    ///   per open dashboard, growing with the event's whole history.</item>
    /// </list>
    /// D-29 chose polling over a hub on the argument that a poll is cheap; one index maintenance per
    /// write is what keeps that true.
    /// </para>
    ///
    /// <para>
    /// <b>Read that number for what it is: the delta query, not the endpoint.</b> A whole poll is this
    /// seek <em>plus</em> the <c>MIN_ACTIVE_ROWVERSION()</c> scalar, the event read, and the four
    /// aggregate queries behind <c>counters</c> — two of which walk <c>EventGroups</c> into current
    /// section membership. This index makes the part that scales with the event's history cheap; it
    /// does not make the endpoint cheap, which is why the route also carries a rate limiter.
    /// </para>
    ///
    /// <para>
    /// <b>Note for whoever reads the sibling finding on <c>FindByDeviceTapAsync</c> and expects the same
    /// result here.</b> That lookup is two index <em>scans</em> because the §11 query filter's
    /// disjunction sits on <c>SchoolId</c>, the <em>leading</em> column of both indexes it would
    /// otherwise seek. This index is keyed <c>(EventId, RowVersion)</c> and does not contain
    /// <c>SchoolId</c> at all, so the same disjunction is evaluated as a residual predicate after the
    /// seek and costs nothing. Same filter, opposite outcome, and the difference is entirely which
    /// column leads.
    /// </para>
    ///
    /// <para>
    /// <c>Down</c> is safe as schema — dropping this index and column destroys no attendance data, only
    /// the cursor values themselves, which are database-assigned and regenerate on the next <c>Up</c>.
    /// </para>
    ///
    /// <para>
    /// <b>What it does to a client's cursor is not "one harmless snapshot", and the first version of
    /// this note said it was</b> (corrected at the Phase 4d review). <c>@@DBTS</c> is a property of the
    /// database, not of the column, and it does <em>not</em> reset when the column is dropped and
    /// re-added — so a cursor a dashboard is still holding decodes perfectly well afterwards and is used
    /// as a floor against re-issued row versions that mean something completely different. The
    /// dashboard silently shows a wrong window rather than falling back to a snapshot, which is the
    /// silent-loss class this endpoint is otherwise careful about.
    /// </para>
    ///
    /// <para>
    /// The clamp in <c>EventService.GetLiveAttendanceAsync</c> catches the direction that is
    /// recoverable — a cursor <em>above</em> the current ceiling falls back to a snapshot, which covers
    /// a restore-from-backup and a <c>Down</c> followed by a re-populated <c>Up</c>. It cannot catch a
    /// cursor that still happens to be below the ceiling. <b>If this migration is ever reverted against
    /// live dashboards, treat every outstanding cursor as invalid</b> — a restart of the clients, or a
    /// deployment window with nothing polling, is the whole of the mitigation needed.
    /// </para>
    /// </summary>
    public partial class AttendanceRowVersionCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "AttendanceRecords",
                type: "rowversion",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_Attendance_EventId_RowVersion",
                table: "AttendanceRecords",
                columns: new[] { "EventId", "RowVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Attendance_EventId_RowVersion",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "AttendanceRecords");
        }
    }
}
