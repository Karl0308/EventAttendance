using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// Migration #3 — the §10 import pipeline's staging schema: ADR-001 D-4's versioned mapping,
    /// D-5's required <c>TermId</c> and warning/skip columns, and the row-level fan-out table.
    /// Additive throughout; nothing existing is dropped, renamed or retyped except one widening.
    ///
    /// <para>
    /// <b>The three new tables are the ones ADR-001 D-1 counted and never named.</b> D-1 sized the
    /// academic addition at twelve tables, pinned nine, and left "pin the remaining three during the
    /// schema phase" as a follow-up. They are <c>SisImportRowEntities</c>, <c>SisImportProfiles</c> and
    /// <c>SisImportProfileColumns</c> — all import-side. See <c>SisImportEntities.cs</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The one non-additive-looking change is a widening.</b> <c>SisImportBatches.Status</c> goes
    /// from <c>nvarchar(20)</c> to <c>nvarchar(30)</c> to fit D-5's <c>CompletedWithWarnings</c> (21
    /// characters) and <c>CompletedWithErrors</c> (19). Widening an <c>nvarchar</c> is an online
    /// metadata-only change on SQL Server and cannot truncate anything, so no existing value is at
    /// risk. The alternative — abbreviating the status — would have put a code in the one column an
    /// operator reads directly.
    /// </para>
    /// </summary>
    public partial class SisImportPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both of these are genuinely redundant, and that is worth stating because the identical
            // scaffolded drop was WRONG once in this repo. When UX_StudentGroups_Derived_Source
            // appeared, EF proposed dropping IX_StudentGroups_SchoolId and it had to be kept: that
            // composite is FILTERED to SourceType = 'Derived', and SQL Server cannot use a filtered
            // index for a query that does not imply its predicate, so every tenant-scoped read would
            // have gone to a scan.
            //
            // Neither replacement here is filtered, and in both the dropped index's column is the
            // composite's leading column — (BatchId) under (BatchId, Result), (SchoolId) under
            // (SchoolId, TermId). An unfiltered composite serves a prefix predicate, so both of these
            // really are duplicates and keeping them would cost two index writes per row on the
            // heaviest insert path in the system: 536 rows per import, plus the batch.
            migrationBuilder.DropIndex(
                name: "IX_SisImportRows_BatchId",
                table: "SisImportRows");

            migrationBuilder.DropIndex(
                name: "IX_SisImportBatches_SchoolId",
                table: "SisImportBatches");

            migrationBuilder.AddColumn<string>(
                name: "AlternateEmail",
                table: "Students",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowHash",
                table: "SisImportRows",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SkipReason",
                table: "SisImportRows",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WarningCode",
                table: "SisImportRows",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WarningMessage",
                table: "SisImportRows",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "SisImportBatches",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<string>(
                name: "FileHash",
                table: "SisImportBatches",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportProfileId",
                table: "SisImportBatches",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SkippedRows",
                table: "SisImportBatches",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceSheetName",
                table: "SisImportBatches",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            // ADR-001 D-5's required TermId, hand-written because the scaffolded version is unsafe.
            //
            // EF proposed `AddColumn<Guid>(nullable: false, defaultValue: Guid.Empty)`. On a populated
            // table that stamps every existing batch with the all-zero GUID and then fails seconds
            // later when FK_SisImportBatches_Terms_TermId is added — a half-applied migration whose
            // error message names a foreign key rather than the cause. It also leaves a DEFAULT
            // constraint behind, so any future INSERT that forgets TermId silently gets the zero GUID
            // instead of an error.
            //
            // There is no honest backfill available. A batch that already exists was imported before
            // terms were a required input, so nothing anywhere records which term it was for; picking
            // the current term, or inventing an "unknown" one, would fabricate the single most
            // load-bearing fact about a historical import and make it indistinguishable from a
            // recorded one.
            //
            // So: refuse, loudly, with instructions. No data is lost — the migration rolls back inside
            // its own transaction and the database is exactly as it was. In every database that exists
            // today the table is empty (no import pipeline has ever run), so this is a guard rather
            // than an obstacle, and it is asserted by the migration test.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [SisImportBatches])
                    THROW 51000,
                        N'SisImportBatches already holds rows, and ADR-001 D-5 makes TermId required. There is no term recorded for those batches and inventing one would fabricate history. Export them, delete them, re-run this migration, then re-import under an explicit term.',
                        1;

                ALTER TABLE [SisImportBatches] ADD [TermId] uniqueidentifier NOT NULL;
                """);

            migrationBuilder.AddColumn<int>(
                name: "WarningRows",
                table: "SisImportBatches",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SisImportProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SisImportProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SisImportProfiles_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SisImportRowEntities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SisImportRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SisImportRowEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SisImportRowEntities_SisImportRows_SisImportRowId",
                        column: x => x.SisImportRowId,
                        principalTable: "SisImportRows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SisImportProfileColumns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceColumn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SourceColumnKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetField = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizationRule = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SisImportProfileColumns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SisImportProfileColumns_SisImportProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "SisImportProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SisImportRows_BatchId_Result",
                table: "SisImportRows",
                columns: new[] { "BatchId", "Result" });

            migrationBuilder.CreateIndex(
                name: "UX_SisImportRows_Batch_RowNumber",
                table: "SisImportRows",
                columns: new[] { "BatchId", "RowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SisImportBatches_ImportProfileId",
                table: "SisImportBatches",
                column: "ImportProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_SisImportBatches_SchoolId_TermId",
                table: "SisImportBatches",
                columns: new[] { "SchoolId", "TermId" });

            migrationBuilder.CreateIndex(
                name: "IX_SisImportBatches_TermId",
                table: "SisImportBatches",
                column: "TermId");

            migrationBuilder.CreateIndex(
                name: "UX_SisImportProfileColumns_Profile_Column_Target",
                table: "SisImportProfileColumns",
                columns: new[] { "ProfileId", "SourceColumnKey", "TargetField" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SisImportProfiles_School_Name_Active",
                table: "SisImportProfiles",
                columns: new[] { "SchoolId", "NameKey" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_SisImportProfiles_School_Name_Version",
                table: "SisImportProfiles",
                columns: new[] { "SchoolId", "NameKey", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SisImportRowEntities_Entity",
                table: "SisImportRowEntities",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "UX_SisImportRowEntities_Row_Type_Entity",
                table: "SisImportRowEntities",
                columns: new[] { "SisImportRowId", "EntityType", "EntityId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SisImportBatches_SisImportProfiles_ImportProfileId",
                table: "SisImportBatches",
                column: "ImportProfileId",
                principalTable: "SisImportProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SisImportBatches_Terms_TermId",
                table: "SisImportBatches",
                column: "TermId",
                principalTable: "Terms",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <b>Down is guarded on the same principle as Up, and for the same two reasons.</b> Scaffolded
        /// as generated it was not reversible at all on any database that had run an import: the
        /// <c>Status</c> narrowing back to <c>nvarchar(20)</c> fails on SQL Server error 8152 the moment
        /// a batch carries <c>CompletedWithWarnings</c> (21 characters) or <c>CompletedWithErrors</c>
        /// (19 fits, 21 does not), and the error names a column rather than the cause; and
        /// <c>DropColumn AlternateEmail</c> destroys a personal e-mail address per student with no
        /// export step and no way back.
        ///
        /// <para>
        /// Both refuse, first, before anything is dropped — so the migration rolls back inside its own
        /// transaction and the database is exactly as it was, exactly like Up's <c>TermId</c> guard.
        /// Neither is an obstacle in the case a rollback is actually wanted: a database that has never
        /// run an import has no long statuses and no alternate e-mails, and passes both silently.
        /// </para>
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Ordered first on purpose: a guard that fires after half the schema is gone still rolls
            // back, but the operator reads the failure with a half-described database in front of them.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [SisImportBatches] WHERE LEN([Status]) > 20)
                    THROW 51001,
                        N'Rolling this migration back narrows SisImportBatches.Status to nvarchar(20), and at least one batch carries a longer value — CompletedWithWarnings is 21 characters. SQL Server would truncate it, so the value that says whether an import needs looking at would be silently corrupted. Export those batches, delete them, then roll back.',
                        1;

                IF EXISTS (SELECT 1 FROM [Students] WHERE [AlternateEmail] IS NOT NULL)
                    THROW 51002,
                        N'Rolling this migration back drops Students.AlternateEmail, and it holds values. That column is a student''s personal e-mail address; dropping it destroys data no other column carries and no import can reproduce, because the roster is a snapshot and an older export may not have it. Export Students(StudentNumber, AlternateEmail), clear the column, then roll back.',
                        1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_SisImportBatches_SisImportProfiles_ImportProfileId",
                table: "SisImportBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_SisImportBatches_Terms_TermId",
                table: "SisImportBatches");

            migrationBuilder.DropTable(
                name: "SisImportProfileColumns");

            migrationBuilder.DropTable(
                name: "SisImportRowEntities");

            migrationBuilder.DropTable(
                name: "SisImportProfiles");

            migrationBuilder.DropIndex(
                name: "IX_SisImportRows_BatchId_Result",
                table: "SisImportRows");

            migrationBuilder.DropIndex(
                name: "UX_SisImportRows_Batch_RowNumber",
                table: "SisImportRows");

            migrationBuilder.DropIndex(
                name: "IX_SisImportBatches_ImportProfileId",
                table: "SisImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_SisImportBatches_SchoolId_TermId",
                table: "SisImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_SisImportBatches_TermId",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "AlternateEmail",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "RowHash",
                table: "SisImportRows");

            migrationBuilder.DropColumn(
                name: "SkipReason",
                table: "SisImportRows");

            migrationBuilder.DropColumn(
                name: "WarningCode",
                table: "SisImportRows");

            migrationBuilder.DropColumn(
                name: "WarningMessage",
                table: "SisImportRows");

            migrationBuilder.DropColumn(
                name: "FileHash",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "ImportProfileId",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "SkippedRows",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "SourceSheetName",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "TermId",
                table: "SisImportBatches");

            migrationBuilder.DropColumn(
                name: "WarningRows",
                table: "SisImportBatches");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "SisImportBatches",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30);

            migrationBuilder.CreateIndex(
                name: "IX_SisImportRows_BatchId",
                table: "SisImportRows",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SisImportBatches_SchoolId",
                table: "SisImportBatches",
                column: "SchoolId");
        }
    }
}
