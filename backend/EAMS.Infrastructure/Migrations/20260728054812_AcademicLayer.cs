using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// ADR-001 D-1/D-2: the academic-structure layer, on top of <c>Section4Baseline</c>.
    ///
    /// <para>
    /// <b>Purely additive, and that is verified rather than intended.</b> <c>Up</c> contains nine
    /// <c>CreateTable</c>s, eight <c>AddColumn</c>s and twenty-three <c>CreateIndex</c>es, and no
    /// <c>DropColumn</c>, <c>DropTable</c>, <c>DropIndex</c>, <c>AlterColumn</c> or raw <c>Sql</c> —
    /// so applying it to a populated §4 database cannot lose a row or change the type of a column that
    /// already holds one. <c>AcademicLayerMigrationTests</c> proves it end to end: it migrates a
    /// database to <c>Section4Baseline</c> alone, seeds it, then applies this migration and asserts
    /// every seeded row survives unchanged.
    /// </para>
    ///
    /// <para>
    /// <b>The scaffolder wanted to drop an index and was overruled.</b> The first generated version of
    /// this migration opened with <c>DropIndex("IX_StudentGroups_SchoolId")</c>: EF saw the new
    /// composite <c>UX_StudentGroups_Derived_Source</c> leading with <c>SchoolId</c> and treated the
    /// foreign-key index as redundant. It is not — the composite is filtered to
    /// <c>SourceType = 'Derived'</c> and SQL Server cannot use a filtered index for a query that does
    /// not imply its predicate, so every §11 tenant-filtered read of <c>StudentGroups</c> would have
    /// dropped to a scan. The index is now declared explicitly in <c>EamsDbContext</c>, which is what
    /// keeps it here.
    /// </para>
    ///
    /// <para>
    /// The three new <c>NOT NULL</c> columns on the existing <c>StudentGroups</c> /
    /// <c>StudentGroupMembers</c> tables all carry defaults, so the backfill onto existing rows is
    /// part of the <c>ADD COLUMN</c> itself. The values chosen (<c>Manual</c> / <c>None</c> / <c>''</c>)
    /// are the true statement about those rows: every one of them was written before a projection
    /// existed, by a person.
    /// </para>
    /// </summary>
    public partial class AcademicLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcademicCacheUpdatedAt",
                table: "Students",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncedAt",
                table: "StudentGroups",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceEntityId",
                table: "StudentGroups",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceEntityType",
                table: "StudentGroups",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "StudentGroups",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "StudentGroups",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<Guid>(
                name: "TermId",
                table: "StudentGroups",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "StudentGroupMembers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateTable(
                name: "Colleges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Colleges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Colleges_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Instructors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MergedIntoInstructorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instructors", x => x.Id);
                    table.CheckConstraint("CK_Instructors_NoSelfMerge", "[MergedIntoInstructorId] IS NULL OR [MergedIntoInstructorId] <> [Id]");
                    table.ForeignKey(
                        name: "FK_Instructors_Instructors_MergedIntoInstructorId",
                        column: x => x.MergedIntoInstructorId,
                        principalTable: "Instructors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Instructors_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Terms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SchoolYear = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Semester = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Terms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Terms_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Courses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CollegeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CodeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Courses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Courses_Colleges_CollegeId",
                        column: x => x.CollegeId,
                        principalTable: "Colleges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Courses_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Programs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CollegeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CodeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Programs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Programs_Colleges_CollegeId",
                        column: x => x.CollegeId,
                        principalTable: "Colleges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Programs_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CourseOfferings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TermId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CourseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SectionKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, defaultValue: "(unspecified)"),
                    SectionName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseOfferings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseOfferings_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CourseOfferings_Terms_TermId",
                        column: x => x.TermId,
                        principalTable: "Terms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StudentTermRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TermId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CollegeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    YearLevel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    HomeSectionKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HomeSectionName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentTermRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentTermRecords_Colleges_CollegeId",
                        column: x => x.CollegeId,
                        principalTable: "Colleges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentTermRecords_Programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "Programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentTermRecords_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentTermRecords_Terms_TermId",
                        column: x => x.TermId,
                        principalTable: "Terms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CourseOfferingInstructors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CourseOfferingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstructorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseOfferingInstructors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseOfferingInstructors_CourseOfferings_CourseOfferingId",
                        column: x => x.CourseOfferingId,
                        principalTable: "CourseOfferings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CourseOfferingInstructors_Instructors_InstructorId",
                        column: x => x.InstructorId,
                        principalTable: "Instructors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Enrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CourseOfferingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Enrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Enrollments_CourseOfferings_CourseOfferingId",
                        column: x => x.CourseOfferingId,
                        principalTable: "CourseOfferings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Enrollments_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudentGroups_TermId",
                table: "StudentGroups",
                column: "TermId");

            migrationBuilder.CreateIndex(
                name: "UX_StudentGroups_Derived_Source",
                table: "StudentGroups",
                columns: new[] { "SchoolId", "TermId", "SourceEntityType", "SourceKey" },
                unique: true,
                filter: "[SourceType] = 'Derived'");

            migrationBuilder.CreateIndex(
                name: "UX_Colleges_SchoolId_NameKey",
                table: "Colleges",
                columns: new[] { "SchoolId", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourseOfferingInstructors_InstructorId",
                table: "CourseOfferingInstructors",
                column: "InstructorId");

            migrationBuilder.CreateIndex(
                name: "UX_CourseOfferingInstructors_Offering_Instructor",
                table: "CourseOfferingInstructors",
                columns: new[] { "CourseOfferingId", "InstructorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourseOfferings_CourseId",
                table: "CourseOfferings",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_CourseOfferings_TermId_SectionKey",
                table: "CourseOfferings",
                columns: new[] { "TermId", "SectionKey" });

            migrationBuilder.CreateIndex(
                name: "UX_CourseOfferings_Term_Course_Section",
                table: "CourseOfferings",
                columns: new[] { "TermId", "CourseId", "SectionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Courses_CollegeId",
                table: "Courses",
                column: "CollegeId");

            migrationBuilder.CreateIndex(
                name: "UX_Courses_SchoolId_CodeKey",
                table: "Courses",
                columns: new[] { "SchoolId", "CodeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Enrollments_CourseOfferingId",
                table: "Enrollments",
                column: "CourseOfferingId");

            migrationBuilder.CreateIndex(
                name: "UX_Enrollments_Student_CourseOffering",
                table: "Enrollments",
                columns: new[] { "StudentId", "CourseOfferingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instructors_MergedIntoInstructorId",
                table: "Instructors",
                column: "MergedIntoInstructorId");

            migrationBuilder.CreateIndex(
                name: "UX_Instructors_SchoolId_ExternalId",
                table: "Instructors",
                columns: new[] { "SchoolId", "ExternalId" },
                unique: true,
                filter: "[ExternalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Instructors_SchoolId_NameKey",
                table: "Instructors",
                columns: new[] { "SchoolId", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Programs_CollegeId",
                table: "Programs",
                column: "CollegeId");

            migrationBuilder.CreateIndex(
                name: "UX_Programs_SchoolId_CodeKey",
                table: "Programs",
                columns: new[] { "SchoolId", "CodeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentTermRecords_CollegeId",
                table: "StudentTermRecords",
                column: "CollegeId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentTermRecords_ProgramId",
                table: "StudentTermRecords",
                column: "ProgramId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentTermRecords_TermId",
                table: "StudentTermRecords",
                column: "TermId");

            migrationBuilder.CreateIndex(
                name: "UX_StudentTermRecords_Student_Term",
                table: "StudentTermRecords",
                columns: new[] { "StudentId", "TermId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Terms_SchoolId_Code",
                table: "Terms",
                columns: new[] { "SchoolId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Terms_SchoolId_Current",
                table: "Terms",
                column: "SchoolId",
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_StudentGroups_Terms_TermId",
                table: "StudentGroups",
                column: "TermId",
                principalTable: "Terms",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StudentGroups_Terms_TermId",
                table: "StudentGroups");

            migrationBuilder.DropTable(
                name: "CourseOfferingInstructors");

            migrationBuilder.DropTable(
                name: "Enrollments");

            migrationBuilder.DropTable(
                name: "StudentTermRecords");

            migrationBuilder.DropTable(
                name: "Instructors");

            migrationBuilder.DropTable(
                name: "CourseOfferings");

            migrationBuilder.DropTable(
                name: "Programs");

            migrationBuilder.DropTable(
                name: "Courses");

            migrationBuilder.DropTable(
                name: "Terms");

            migrationBuilder.DropTable(
                name: "Colleges");

            migrationBuilder.DropIndex(
                name: "IX_StudentGroups_TermId",
                table: "StudentGroups");

            migrationBuilder.DropIndex(
                name: "UX_StudentGroups_Derived_Source",
                table: "StudentGroups");

            migrationBuilder.DropColumn(
                name: "AcademicCacheUpdatedAt",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "LastSyncedAt",
                table: "StudentGroups");

            migrationBuilder.DropColumn(
                name: "SourceEntityId",
                table: "StudentGroups");

            migrationBuilder.DropColumn(
                name: "SourceEntityType",
                table: "StudentGroups");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "StudentGroups");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "StudentGroups");

            migrationBuilder.DropColumn(
                name: "TermId",
                table: "StudentGroups");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "StudentGroupMembers");
        }
    }
}
