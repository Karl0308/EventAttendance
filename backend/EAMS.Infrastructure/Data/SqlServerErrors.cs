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
        IsUniqueViolation((Exception)ex);

    /// <summary>
    /// <inheritdoc cref="IsUniqueViolation(DbUpdateException)" path="/summary"/>
    ///
    /// <para>
    /// <b>The overload for statements that do not go through <c>SaveChanges</c> — made symmetric with
    /// <see cref="IsConstraintConflict(Exception)"/> deliberately, rather than left as the untreated
    /// twin of a bug this file has already shipped once.</b> <c>ExecuteUpdateAsync</c> and
    /// <c>ExecuteDeleteAsync</c> issue their command directly, so the provider's
    /// <see cref="SqlException"/> propagates unwrapped; a filter written only for the
    /// <see cref="DbUpdateException"/> shape then matches nothing at all, silently, and the violation it
    /// was written to answer ships as a 500. That is exactly what happened to 547 on
    /// <c>StudentClassificationService</c>'s replacement path, and every unique index in this schema is
    /// one <c>ExecuteUpdate</c> away from the same thing.
    /// </para>
    ///
    /// <para>
    /// <b>Behaviour-preserving for the twelve existing callers, which all save.</b> A
    /// <c>DbUpdateException</c> raised by <c>SaveChanges</c> carries the <see cref="SqlException"/> as
    /// its direct inner, which is the first thing <see cref="Unwrap"/> finds — so the old expression and
    /// the new one agree on every input any current caller can produce. The only inputs they differ on
    /// are ones the old form answered <c>false</c> to while a unique violation genuinely had occurred.
    /// </para>
    /// </summary>
    public static bool IsUniqueViolation(Exception ex) =>
        Unwrap(ex) is { } sql && UniqueViolationErrorNumbers.Contains(sql.Number);

    /// <summary>
    /// SQL Server error 547 — "The DELETE/INSERT/UPDATE statement conflicted with the ... constraint".
    ///
    /// <para>
    /// <b>547 covers FOREIGN KEY and CHECK alike, and the caller has to know which it asked for.</b>
    /// The number does not distinguish them, so this predicate is deliberately named after the error
    /// rather than after one of its two meanings. On a <c>DELETE</c> a check constraint cannot fire —
    /// a row being removed satisfies nothing and violates nothing — so 547 from a delete is a
    /// reference conflict and nothing else, which is the one place
    /// <c>ClassificationService.DeleteAsync</c> relies on it.
    /// </para>
    ///
    /// <para>
    /// <b>It IS used to recover from an insert and from an update — and only where the caller
    /// establishes which constraint fired.</b> <c>StudentClassificationService</c> does so on both of
    /// its write branches: it catches 547, re-reads the classification, and <b>rethrows</b> if the row
    /// is still there, because the only 547 it is equipped to answer is the composite
    /// <c>(ClassificationId, Axis)</c> reference failing against a concurrently deleted parent. Its
    /// <c>CHECK</c> cannot fire — the axis is copied off a parent that already satisfies
    /// <c>CK_Classifications_Axis</c> — so a 547 raised with the parent present is a bug in the writing
    /// service, and rethrowing is what keeps it visible.
    /// </para>
    ///
    /// <para>
    /// That discipline is the condition, not the exception: <b>do not recover from this on a write
    /// without saying which constraint you expect and rethrowing the other</b>. Swallowing a
    /// check-constraint failure as "conflict, try again" hides a defect in the code that wrote the row.
    /// <em>(This paragraph previously asserted the predicate was used on no insert or update anywhere.
    /// That became false when those call sites landed, in the file the repo treats as the single source
    /// of truth on 547 — so it is corrected rather than left to be read as permission.)</em>
    /// </para>
    /// </summary>
    public static bool IsConstraintConflict(DbUpdateException ex) =>
        IsConstraintConflict((Exception)ex);

    /// <summary>
    /// <inheritdoc cref="IsConstraintConflict(DbUpdateException)" path="/summary"/>
    ///
    /// <para>
    /// <b>The overload for statements that do not go through <c>SaveChanges</c>.</b>
    /// <c>ExecuteUpdateAsync</c> and <c>ExecuteDeleteAsync</c> issue their command directly, so the
    /// provider's <see cref="SqlException"/> propagates rather than arriving wrapped in a
    /// <see cref="DbUpdateException"/> — and a catch filter written for the wrapped shape then matches
    /// nothing at all, silently, turning the conflict it was written to answer into a 500. Unwrapped
    /// the same way <see cref="IsDeadlockVictim"/> is, and for the same reason.
    /// </para>
    ///
    /// <para>
    /// The caller still owes the same discipline the wrapped overload documents: 547 covers FOREIGN KEY
    /// and CHECK alike, so recovering from one on a write means establishing which fired rather than
    /// assuming, and rethrowing when it was the other.
    /// </para>
    /// </summary>
    public static bool IsConstraintConflict(Exception ex) =>
        Unwrap(ex) is { Number: ConstraintConflictErrorNumber };

    /// <inheritdoc cref="IsConstraintConflict(DbUpdateException)"/>
    private const int ConstraintConflictErrorNumber = 547;

    /// <summary>
    /// SQL Server error 1205 — this transaction was chosen as a deadlock victim and rolled back.
    ///
    /// <para>
    /// <b>It means nothing was written, which is what makes it answerable rather than fatal.</b> The
    /// victim's transaction is fully rolled back by the server, so the caller's request had no effect
    /// at all and retrying it is safe. That is a conflict — two operations wanted the same rows at the
    /// same time — and not a server fault.
    /// </para>
    ///
    /// <para>
    /// <b>Why this is matched here rather than left to <c>EnableRetryOnFailure</c>.</b> The production
    /// registration does enable a retrying execution strategy, and 1205 is on its transient list, so a
    /// deadlocked merge would usually be retried and succeed. But that makes correctness depend on a
    /// registration flag: the test host does not enable it, a console tool composing
    /// <c>AddEamsInfrastructure</c> need not, and in both the deadlock arrives as an unhandled
    /// exception — a 500 for something the caller can simply repeat. A service should not be
    /// answerable only when its host is configured a particular way.
    /// </para>
    /// </summary>
    public static bool IsDeadlockVictim(Exception ex) =>
        Unwrap(ex) is { Number: DeadlockVictimErrorNumber };

    /// <inheritdoc cref="IsDeadlockVictim"/>
    private const int DeadlockVictimErrorNumber = 1205;

    /// <summary>
    /// Finds the <see cref="SqlException"/> anywhere in the chain.
    ///
    /// <para>
    /// A deadlock raised inside <c>CreateExecutionStrategy().ExecuteAsync</c> does not arrive as a
    /// <c>DbUpdateException</c> at all — the strategy wraps it in an
    /// <c>InvalidOperationException</c> whose message recommends enabling retries — so matching on the
    /// outer type, as the two predicates above do, would miss it entirely.
    /// </para>
    /// </summary>
    private static SqlException? Unwrap(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
            if (current is SqlException sql) return sql;

        return null;
    }
}
