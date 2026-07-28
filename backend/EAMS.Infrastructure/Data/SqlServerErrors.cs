using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Data;

/// <summary>
/// The one place that decides what a SQL Server error <em>means</em> to this application.
///
/// <para>
/// Extracted from <c>AttendanceService</c> when the event-close path needed the same predicate.
/// Duplicating it would have been three lines and a slow divergence: the two write paths race against
/// the <em>same</em> index — <c>UX_Attendance_Event_Student_Occurrence</c> — one inserting a tap and
/// the other inserting the frozen absentee it implies, so a copy that learned about a new error number
/// while the other did not would surface as one path recovering from a race the other turned into a
/// 500.
/// </para>
/// </summary>
internal static class SqlServerErrors
{
    /// <summary>
    /// SQL Server's two unique-violation errors: 2627 is a UNIQUE <em>constraint</em>, 2601 a unique
    /// <em>index</em>. Every guard in this schema is an index, so 2601 is the one that fires today; 2627
    /// is matched too because whether a guard is expressed as a constraint or an index is a schema
    /// detail this code should not be coupled to.
    /// </summary>
    private static readonly int[] UniqueViolationErrorNumbers = [2601, 2627];

    /// <summary>
    /// Narrowly matches a unique-key violation. Everything else — a foreign-key failure, a
    /// check-constraint failure, a truncation, a dropped connection — returns false and is left to
    /// propagate: a <c>when</c> clause that swallowed those would turn real corruption into a cheerful
    /// 200.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && UniqueViolationErrorNumbers.Contains(sql.Number);
}
