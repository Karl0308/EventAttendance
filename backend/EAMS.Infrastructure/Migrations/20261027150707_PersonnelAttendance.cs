using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// <b>ADR-015 D-96 / D-97: an attendance row is a student's XOR a personnel member's, and an event can
    /// freeze its expected personnel.</b> Hand-written SQL in place of the scaffolded bodies; the model
    /// snapshot is EF's.
    ///
    /// <para>
    /// <b>Why hand-written.</b> The scaffold altered <c>StudentId</c> in place, which SQL Server refuses
    /// while <c>IX_AttendanceRecords_StudentId</c> and the FK depend on the column, and its <c>Down</c>
    /// re-tightened the column with a <c>Guid.Empty</c> default — a value that would silently replace a
    /// NULL. Each block below is its own <c>migrationBuilder.Sql</c> call, so it runs as its own batch;
    /// all of them run inside the migration's one transaction. Valid on SQL Server 2012: filtered indexes
    /// date from 2008 and <c>THROW</c> from 2012.
    /// </para>
    ///
    /// <para>
    /// <b>No row is touched.</b> Every existing row keeps its <c>StudentId</c> and passes the CHECK, which
    /// <c>WITH CHECK</c> validates.
    /// </para>
    /// </summary>
    public partial class PersonnelAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // U1
            migrationBuilder.Sql("""
                ALTER TABLE dbo.AttendanceRecords ADD PersonnelId uniqueidentifier NULL;
                """);

            // U2
            migrationBuilder.Sql("""
                ALTER TABLE dbo.AttendanceRecords WITH CHECK ADD CONSTRAINT FK_AttendanceRecords_Personnel_PersonnelId
                    FOREIGN KEY (PersonnelId) REFERENCES dbo.Personnel (Id);
                CREATE INDEX IX_AttendanceRecords_PersonnelId ON dbo.AttendanceRecords (PersonnelId);
                """);

            // U3: relax StudentId. Indexes and FK are dropped and re-created around ALTER COLUMN: the
            // 2012-safe form. Both unique indexes are filtered on the attendee id, never on OccurrenceId.
            migrationBuilder.Sql("""
                DROP INDEX UX_Attendance_Event_Student_Occurrence ON dbo.AttendanceRecords;
                DROP INDEX IX_AttendanceRecords_StudentId ON dbo.AttendanceRecords;
                ALTER TABLE dbo.AttendanceRecords DROP CONSTRAINT FK_AttendanceRecords_Students_StudentId;
                ALTER TABLE dbo.AttendanceRecords ALTER COLUMN StudentId uniqueidentifier NULL;
                ALTER TABLE dbo.AttendanceRecords WITH CHECK ADD CONSTRAINT FK_AttendanceRecords_Students_StudentId
                    FOREIGN KEY (StudentId) REFERENCES dbo.Students (Id);
                CREATE INDEX IX_AttendanceRecords_StudentId ON dbo.AttendanceRecords (StudentId);
                CREATE UNIQUE INDEX UX_Attendance_Event_Student_Occurrence
                    ON dbo.AttendanceRecords (EventId, StudentId, OccurrenceId) WHERE [StudentId] IS NOT NULL;
                CREATE UNIQUE INDEX UX_Attendance_Event_Personnel_Occurrence
                    ON dbo.AttendanceRecords (EventId, PersonnelId, OccurrenceId) WHERE [PersonnelId] IS NOT NULL;
                ALTER TABLE dbo.AttendanceRecords WITH CHECK ADD CONSTRAINT CK_AttendanceRecords_StudentOrPersonnel
                    CHECK (([StudentId] IS NOT NULL AND [PersonnelId] IS NULL) OR ([StudentId] IS NULL AND [PersonnelId] IS NOT NULL));
                """);

            // U4: the D-97 snapshot table
            migrationBuilder.Sql("""
                CREATE TABLE dbo.EventPersonnel (
                    Id          uniqueidentifier NOT NULL CONSTRAINT PK_EventPersonnel PRIMARY KEY,
                    EventId     uniqueidentifier NOT NULL CONSTRAINT FK_EventPersonnel_Events_EventId REFERENCES dbo.Events (Id),
                    PersonnelId uniqueidentifier NOT NULL CONSTRAINT FK_EventPersonnel_Personnel_PersonnelId REFERENCES dbo.Personnel (Id));
                CREATE UNIQUE INDEX UX_EventPersonnel_Event_Personnel ON dbo.EventPersonnel (EventId, PersonnelId);
                CREATE INDEX IX_EventPersonnel_PersonnelId ON dbo.EventPersonnel (PersonnelId);
                """);
        }

        /// <summary>
        /// <b>Refuses while any personnel data exists</b> (<c>THROW 50015</c>), and changes nothing when it
        /// does — the migration's transaction leaves the database as it was. Once D1 has proven
        /// <c>AttendanceRecords.PersonnelId</c> is all NULL and <c>EventPersonnel</c> is empty, it restores
        /// the pre-ADR-015 constraints exactly and drops what <c>Up</c> added.
        ///
        /// <para>
        /// <b>D3's DROPs are JJ's approved exception to the no-DROP rule</b> (2026-10-27, ADR-015 decision 2;
        /// ADR-011 D-79 precedent). They are legal only behind D1's guard: without it, D3 would be a
        /// data-loss migration. Do not move, weaken or split the guard from them.
        /// </para>
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // D1 (lock, then guard)
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM dbo.AttendanceRecords WITH (TABLOCKX, HOLDLOCK) WHERE PersonnelId IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM dbo.EventPersonnel WITH (TABLOCKX, HOLDLOCK))
                    THROW 50015, N'ADR-015 Down refused: personnel attendance or frozen personnel rows exist. Roll forward.', 1;
                """);

            // D2: restore the pre-ADR-015 constraints exactly
            migrationBuilder.Sql("""
                ALTER TABLE dbo.AttendanceRecords DROP CONSTRAINT CK_AttendanceRecords_StudentOrPersonnel;
                DROP INDEX UX_Attendance_Event_Personnel_Occurrence ON dbo.AttendanceRecords;
                DROP INDEX UX_Attendance_Event_Student_Occurrence ON dbo.AttendanceRecords;
                DROP INDEX IX_AttendanceRecords_StudentId ON dbo.AttendanceRecords;
                ALTER TABLE dbo.AttendanceRecords DROP CONSTRAINT FK_AttendanceRecords_Students_StudentId;
                ALTER TABLE dbo.AttendanceRecords ALTER COLUMN StudentId uniqueidentifier NOT NULL;
                ALTER TABLE dbo.AttendanceRecords WITH CHECK ADD CONSTRAINT FK_AttendanceRecords_Students_StudentId
                    FOREIGN KEY (StudentId) REFERENCES dbo.Students (Id);
                CREATE INDEX IX_AttendanceRecords_StudentId ON dbo.AttendanceRecords (StudentId);
                CREATE UNIQUE INDEX UX_Attendance_Event_Student_Occurrence
                    ON dbo.AttendanceRecords (EventId, StudentId, OccurrenceId);   -- unfiltered, as before
                """);

            // D3: drop the now provably empty additions (JJ's approved exception, above)
            migrationBuilder.Sql("""
                ALTER TABLE dbo.AttendanceRecords DROP CONSTRAINT FK_AttendanceRecords_Personnel_PersonnelId;
                DROP INDEX IX_AttendanceRecords_PersonnelId ON dbo.AttendanceRecords;
                ALTER TABLE dbo.AttendanceRecords DROP COLUMN PersonnelId;
                DROP TABLE dbo.EventPersonnel;
                """);
        }
    }
}
