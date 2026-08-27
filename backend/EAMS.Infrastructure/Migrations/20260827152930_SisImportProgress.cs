using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// Seven nullable columns on <c>SisImportBatches</c> so a run can say where it has got to, and so a
    /// failed run can say why it stopped.
    ///
    /// <para>
    /// <b>Scaffolded unedited, and purely additive.</b> <c>Up</c> is seven <c>AddColumn</c> calls and
    /// nothing else — <b>no <c>DROP</c>, no <c>ALTER COLUMN</c>, no re-type, no rename, no new
    /// index</b>. Nothing that exists changes shape or nullability. Compare the migration two before
    /// this one, which had to be rewritten by hand because EF proposed stamping every existing batch
    /// with <c>Guid.Empty</c>; there is nothing of that kind here, which is why this one is shipped as
    /// generated.
    /// </para>
    ///
    /// <para>
    /// <b>All seven are nullable, and there is deliberately no default and no backfill.</b> A batch
    /// that ran before progress reporting existed has no progress to report, and <c>NULL</c> is the
    /// only value that says so. The six row counters on this same table <em>are</em>
    /// <c>DEFAULT 0</c> and are right to be — "no rows failed" is a fact about a finished run. A
    /// progress <c>0</c> would be three different facts at once: the phase has not started, the phase
    /// has no countable units, and the phase has done none of its units yet. A poller cannot tell
    /// those apart, so a backfilled zero would show every historical import as a progress bar stuck at
    /// 0% — a fabricated claim about a run that in fact completed months ago. Backfilling nothing is
    /// the honest option and it is also the cheap one: no table scan, no lock, and safe on any
    /// population.
    /// </para>
    ///
    /// <para>
    /// <b>No index, on purpose.</b> Every reader of these columns fetches the batch by primary key —
    /// a progress poller asks about the one batch whose id it already holds. An index here would cost
    /// writes on the hot path of a run (these columns are the ones a run updates repeatedly) to serve
    /// a query nobody makes.
    /// </para>
    ///
    /// <para>
    /// <b><c>ProgressPhase</c> is <c>nvarchar(40)</c> and the longest phase name is 22 characters</b>
    /// (<c>RefreshingStudentCache</c>) — headroom for a phase name that has not been thought of,
    /// without the column becoming a place to store a sentence. <c>FailureReason</c> is
    /// <c>nvarchar(400)</c>: a sentence an operator can act on, deliberately not a stack trace, since
    /// a truncated exception dump is a worse answer than a short written one and the log keeps the
    /// full detail.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing writes these columns yet.</b> The schema, the phase constants
    /// (<c>EAMS.Domain.SisImportPhase</c>) and the DTO fields land together so this is one migration
    /// rather than three; the writer is a later phase. Until it lands, every row reads <c>NULL</c> —
    /// which is precisely what the paragraphs above say <c>NULL</c> means.
    /// </para>
    ///
    /// <para>
    /// <b><c>Down</c> is scaffolded and unguarded, and that is a considered difference from the
    /// import-pipeline migration's hand-written guards.</b> Those refuse to drop
    /// <c>Students.AlternateEmail</c> because it holds a personal e-mail address harvested from a
    /// registrar's roster: unique, not re-derivable, and gone for good. Everything dropped here is
    /// telemetry about a run — a phase name, two counters, a timestamp, and a message explaining a
    /// failure — all of which the pipeline regenerates by re-running the import, which is the
    /// documented remedy for a failed batch anyway (imports are idempotent, §10.4). Rolling back
    /// discards the diagnostics of past runs and nothing of record. That is stated here so nobody has
    /// to infer it, and so that a future column on this table carrying something irreplaceable is not
    /// added under the same rollback by analogy.
    /// </para>
    /// </summary>
    public partial class SisImportProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "SisImportBatches",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProgressPhase",
                table: "SisImportBatches",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressPhaseCount",
                table: "SisImportBatches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressPhaseNumber",
                table: "SisImportBatches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressUnitsDone",
                table: "SisImportBatches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressUnitsTotal",
                table: "SisImportBatches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProgressUpdatedAt",
                table: "SisImportBatches",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "ProgressPhase",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "ProgressPhaseCount",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "ProgressPhaseNumber",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "ProgressUnitsDone",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "ProgressUnitsTotal",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "ProgressUpdatedAt",
                table: "SisImportBatches");
        }
    }
}
