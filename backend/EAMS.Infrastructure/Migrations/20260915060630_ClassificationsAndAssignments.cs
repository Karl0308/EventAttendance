using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClassificationsAndAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Classifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Axis = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    RetiredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MergedIntoClassificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Classifications", x => x.Id);
                    table.UniqueConstraint("AK_Classifications_Id_Axis", x => new { x.Id, x.Axis });
                    table.CheckConstraint("CK_Classifications_Axis", "[Axis] IN ('Student', 'Personnel', 'Friars', 'Special')");
                    table.CheckConstraint("CK_Classifications_MergedIsRetired", "[MergedIntoClassificationId] IS NULL OR [IsActive] = 0");
                    table.CheckConstraint("CK_Classifications_NoSelfMerge", "[MergedIntoClassificationId] IS NULL OR [MergedIntoClassificationId] <> [Id]");
                    table.ForeignKey(
                        name: "FK_Classifications_Classifications_MergedIntoClassificationId",
                        column: x => x.MergedIntoClassificationId,
                        principalTable: "Classifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Classifications_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StudentClassifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Axis = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentClassifications", x => x.Id);
                    table.CheckConstraint("CK_StudentClassifications_Axis", "[Axis] IN ('Student', 'Personnel', 'Friars', 'Special')");
                    table.ForeignKey(
                        name: "FK_StudentClassifications_Classifications_ClassificationId_Axis",
                        columns: x => new { x.ClassificationId, x.Axis },
                        principalTable: "Classifications",
                        principalColumns: new[] { "Id", "Axis" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentClassifications_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Classifications_MergedIntoClassificationId",
                table: "Classifications",
                column: "MergedIntoClassificationId");

            migrationBuilder.CreateIndex(
                name: "UX_Classifications_SchoolId_NameKey",
                table: "Classifications",
                columns: new[] { "SchoolId", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentClassifications_ClassificationId",
                table: "StudentClassifications",
                column: "ClassificationId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentClassifications_ClassificationId_Axis",
                table: "StudentClassifications",
                columns: new[] { "ClassificationId", "Axis" });

            migrationBuilder.CreateIndex(
                name: "UX_StudentClassifications_Student_Axis",
                table: "StudentClassifications",
                columns: new[] { "StudentId", "Axis" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudentClassifications");

            migrationBuilder.DropTable(
                name: "Classifications");
        }
    }
}
