using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EAMS.Infrastructure.Migrations
{
    /// <summary>
    /// P5 / client QA Q20: <c>Events.IssuesCertificates</c>, the per-event "issues certificates" flag.
    /// One additive column; nothing existing is altered, and every event already in the table reads
    /// <c>0</c> (false) because SQL Server fills a NOT NULL column from its default when it is added.
    ///
    /// <para>
    /// <b>Raw SQL rather than the scaffolded <c>AddColumn</c>, for the constraint name alone.</b> EF Core 9
    /// cannot name a default constraint, and the scaffolded form leaves SQL Server to invent
    /// <c>DF__Events__Issue__&lt;hash&gt;</c> — different on every database, so no script and no test can
    /// address it. The SQL is otherwise exactly what <c>AddColumn&lt;bool&gt;(nullable: false,
    /// defaultValue: false)</c> generates, and it runs on SQL Server 2012 (compatibility level 110).
    /// </para>
    ///
    /// <para>
    /// <b><c>Down</c> is guarded: it refuses (<c>THROW 51003</c>) while any event has the flag set</b>,
    /// because dropping the column then would erase an administrator's setting (hard rule: no data-loss
    /// migrations). The check takes <c>TABLOCKX, HOLDLOCK</c> on <c>Events</c> so a running application
    /// cannot set a flag between the check and the drop; the lock is held to the end of the migration's
    /// transaction. The message says how many events are affected and what to do: record them, clear the
    /// flag deliberately, then roll back. Mirrors <c>GrantReportsReadToAdminRoles</c>'s 51002.
    /// </para>
    ///
    /// <para>
    /// Once nothing is lost, <c>Down</c> uses EF's <c>DropColumn</c>, which on SQL Server first drops
    /// whatever default constraint the column carries, <b>by lookup</b> — so it still works if the
    /// constraint was ever renamed. It is deliberately not a hand-written <c>DROP CONSTRAINT</c>.
    /// </para>
    /// </summary>
    public partial class IssuesCertificatesFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE [Events] ADD [IssuesCertificates] bit NOT NULL " +
                "CONSTRAINT [DF_Events_IssuesCertificates] DEFAULT CAST(0 AS bit);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [Events] WITH (TABLOCKX, HOLDLOCK) WHERE [IssuesCertificates] = 1)
                BEGIN
                    DECLARE @affected int = (SELECT COUNT(*) FROM [Events] WHERE [IssuesCertificates] = 1);
                    DECLARE @msg nvarchar(2048) = CONCAT(
                        N'Refusing to roll back IssuesCertificatesFlag: ', @affected,
                        N' event(s) have IssuesCertificates = 1, and dropping the column would erase that setting. ',
                        N'Record them first (SELECT Id, Name FROM Events WHERE IssuesCertificates = 1), ',
                        N'set them to 0 deliberately (UPDATE Events SET IssuesCertificates = 0 WHERE IssuesCertificates = 1), ',
                        N'then roll back.');
                    THROW 51003, @msg, 1;
                END
                """);

            migrationBuilder.DropColumn(
                name: "IssuesCertificates",
                table: "Events");
        }
    }
}
