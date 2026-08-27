using EAMS.Domain;
using EAMS.Infrastructure.Data;

namespace EAMS.Infrastructure.Sis;

/// <summary>
/// Collects what happened to each source row during a run, and writes it back at the end.
///
/// <para>
/// <b>Why the outcome is derived rather than decided at the point of writing.</b> A single source row
/// touches up to ten entities across three passes; the row's result is a fact about all of them, and
/// there is no moment during the passes when it is known. Recording every touch and folding them at the
/// end is what makes the rule "Inserted if this row created anything, else Updated if it changed
/// anything, else Skipped" a single statement in a single place, rather than a running guess that each
/// pass has to remember to revise.
/// </para>
///
/// <para>
/// <b>It is also what makes the headline claim testable.</b> A second import of an unchanged file
/// produces touches that are all <c>Unchanged</c>, so every row folds to <c>Skipped</c> and the batch
/// reports <c>0 / 0 / 0 / 536</c> — from the same code path that reports the first run's inserts, not
/// from a short-circuit that decided nothing had changed before looking.
/// </para>
/// </summary>
internal sealed class RowLedger
{
    /// <summary>
    /// Warning precedence, most actionable first. Only one code fits in
    /// <c>SisImportRows.WarningCode</c>; every message is kept in <c>WarningMessage</c>, so ordering
    /// this list decides what an operator filtering by code sees, not what they can find out.
    ///
    /// <para>
    /// The order is by how much a human has to do about it: a contradicted student identity is a source
    /// bug, a title alias is data loss from a shared column, and an unnamed teacher or section is simply
    /// the file being incomplete — true of 66 and 39 rows respectively, so putting either first would
    /// bury everything else.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> WarningPrecedence =
    [
        SisImportWarningCode.StudentIdentityConflict,
        SisImportWarningCode.CourseCollegeAdopted,
        SisImportWarningCode.CourseTitleAlias,
        SisImportWarningCode.SectionSpansPrograms,
        SisImportWarningCode.InstructorPlaceholder,
        SisImportWarningCode.SectionUnspecified,
    ];

    /// <summary>Matches <c>SisImportRows.WarningMessage nvarchar(1000)</c>.</summary>
    private const int WarningMessageMaxLength = 1000;

    /// <summary>Matches <c>SisImportRows.ErrorMessage nvarchar(1000)</c>.</summary>
    private const int ErrorMessageMaxLength = 1000;

    private sealed class Entry
    {
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }
        public List<(string Code, string Message)> Warnings { get; } = [];
        public List<(string Type, Guid Id, string Action)> Touches { get; } = [];
    }

    private readonly Dictionary<Guid, Entry> _entries;
    private readonly IReadOnlyList<SisImportRow> _rows;

    public RowLedger(IReadOnlyList<SisImportRow> rows)
    {
        _rows = rows;
        _entries = rows.ToDictionary(r => r.Id, _ => new Entry());
    }

    /// <summary>
    /// Records that this row cannot be imported. The first failure wins: once a row is out, later passes
    /// have nothing to add and a second message would only make the first harder to read.
    /// </summary>
    public void Fail(SisImportRow row, string code, string message)
    {
        var entry = _entries[row.Id];
        if (entry.FailureCode is not null) return;

        entry.FailureCode = code;
        entry.FailureMessage = message;
    }

    /// <summary>Records a non-fatal anomaly. The row still imports (ADR-001 D-5).</summary>
    public void Warn(SisImportRow row, string code, string message)
    {
        var entry = _entries[row.Id];

        // One code once per row. WarnOnSectionsSpanningPrograms and WarnOnTitleAliases both iterate
        // over groups a row can belong to more than once, and a row carrying the same warning twice
        // would double its message for no information.
        if (entry.Warnings.Any(w => w.Code == code)) return;

        entry.Warnings.Add((code, message));
    }

    /// <summary>Records that this row touched one entity, and what it did to it.</summary>
    public void Touch(SisImportRow row, string entityType, Guid entityId, string action) =>
        _entries[row.Id].Touches.Add((entityType, entityId, action));

    /// <summary>
    /// Folds everything collected into the staged rows: one terminal result each, plus the fan-out
    /// rows — <b>pausing at chunk boundaries so the caller can save what has accumulated</b>.
    ///
    /// <para>
    /// Every row gets a terminal result, including one nothing was recorded against, so
    /// <c>Inserted + Updated + Failed + Skipped == TotalRows</c> holds by construction rather than by
    /// each pass remembering to keep it true (ADR-001 D-5).
    /// </para>
    ///
    /// <para>
    /// <b>This is a lazy sequence, and enumerating it is what performs the fold.</b> Each element means
    /// "a chunk is staged, save now"; its value is the number of fan-out rows that chunk added. The
    /// final element is yielded even when it carries no fan-out at all, because a row that touched
    /// nothing still has a <c>Result</c> to write — so a full enumeration always ends with everything
    /// staged. A caller that abandons the sequence early leaves rows unfolded, which the batch's own
    /// counter reconciliation reports rather than hides.
    /// </para>
    ///
    /// <para>
    /// <b>The boundary always falls between source rows.</b> A row's outcome and its own fan-out are
    /// therefore never written by different saves, which is what makes a part-written fan-out readable
    /// rather than merely inconsistent. It also makes <paramref name="fanOutChunkSize"/> a ceiling that
    /// only a single row touching more entities than the whole chunk could exceed — ten is the most any
    /// row touches (see <c>SisImportService.ResolveFactsAsync</c>), so it cannot.
    /// </para>
    /// </summary>
    public IEnumerable<int> ApplyInChunks(EamsDbContext db, int fanOutChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fanOutChunkSize, 1);

        var pending = 0;

        foreach (var row in _rows)
        {
            var entry = _entries[row.Id];

            // A failed row stages no fan-out at all — Stage returns before adding any — so counting its
            // touches here would cut a chunk short against rows that are never going to be written.
            var fanOut = entry.FailureCode is null ? entry.Touches.Count : 0;

            // Decided *before* the row is staged rather than after. A row is never split across two
            // saves, so deciding afterwards would mean discovering the ceiling had already been passed.
            if (pending > 0 && pending + fanOut > fanOutChunkSize)
            {
                yield return pending;
                pending = 0;
            }

            Stage(db, row, entry);
            pending += fanOut;
        }

        // Unconditional: the tail chunk, and — when the last rows touched nothing — the only save that
        // carries their results.
        yield return pending;
    }

    /// <summary>Folds one row. Extracted from <see cref="ApplyInChunks"/> so its early returns stay
    /// early returns rather than becoming conditions the chunk accounting has to repeat.</summary>
    private static void Stage(EamsDbContext db, SisImportRow row, Entry entry)
    {
        row.WarningCode = null;
        row.WarningMessage = null;
        row.ErrorMessage = null;
        row.SkipReason = null;

        if (entry.Warnings.Count > 0)
        {
            row.WarningCode = entry.Warnings
                .OrderBy(w => IndexOfPrecedence(w.Code))
                .First().Code;
            row.WarningMessage = Clamp(
                string.Join(" ", entry.Warnings.Select(w => $"[{w.Code}] {w.Message}")),
                WarningMessageMaxLength);
        }

        if (entry.FailureCode is not null)
        {
            row.Result = SisImportRowResult.Failed;
            row.ErrorMessage = Clamp(
                $"{entry.FailureCode}: {entry.FailureMessage}", ErrorMessageMaxLength);
            return;
        }

        foreach (var (type, id, action) in entry.Touches)
        {
            db.SisImportRowEntities.Add(new SisImportRowEntity
            {
                // Set as a navigation as well as by id: the row may itself be a pending insert on a
                // retry, and the navigation is what lets EF order the two. Added through the DbSet
                // rather than through row.Entities because Entity assigns its own Id in the
                // initializer, and a graph-added entity carrying an Id can be classified as an
                // existing row and turned into an UPDATE that matches nothing.
                SisImportRow = row,
                EntityType = type,
                EntityId = id,
                Action = action,
            });
        }

        row.Result = Fold(entry.Touches);

        if (row.Result != SisImportRowResult.Skipped) return;

        // A skip is never a shrug. When the row's only distinguishing content was a placeholder
        // teacher, say so — that is the 27 (REGNO, COURSE_CODE) pairs, which are one enrollment
        // described twice rather than a duplicate that was thrown away.
        row.SkipReason = entry.Warnings.Any(w => w.Code == SisImportWarningCode.InstructorPlaceholder)
            ? SisImportSkipReason.InstructorPlaceholder
            : SisImportSkipReason.NoChange;
    }

    /// <summary>
    /// <c>Inserted</c> beats <c>Updated</c> beats <c>Unchanged</c>. A row that created an enrollment and
    /// merely referenced an existing course is an insert — the strongest thing it did is what it did.
    /// </summary>
    private static string Fold(IReadOnlyList<(string Type, Guid Id, string Action)> touches)
    {
        if (touches.Any(t => t.Action == SisImportEntityAction.Inserted))
            return SisImportRowResult.Inserted;

        if (touches.Any(t => t.Action == SisImportEntityAction.Updated))
            return SisImportRowResult.Updated;

        return SisImportRowResult.Skipped;
    }

    private static int IndexOfPrecedence(string code)
    {
        for (var i = 0; i < WarningPrecedence.Count; i++)
            if (WarningPrecedence[i] == code) return i;

        // An unlisted code sorts last rather than first: a code added without being ranked should not
        // silently outrank every deliberate ranking above it.
        return int.MaxValue;
    }

    private static string Clamp(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "…");
}
