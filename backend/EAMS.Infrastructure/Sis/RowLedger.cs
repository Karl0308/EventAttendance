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
    ///
    /// <para>
    /// <b>The four Task 5 classification codes split across that ordering rather than sitting together,
    /// and the split is the point.</b> The first two describe a category the file named and the system
    /// would not apply — somebody has to go and change a vocabulary row or settle a disagreement — so
    /// they rank with the source bugs. The last two describe a category the file did not name, which is
    /// 34 of 21,497 rows and is an absence to work through rather than a contradiction to resolve; they
    /// rank below the shared-column losses and above the file simply being incomplete.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="SisImportWarningCode.ClassificationAxisUnknown"/> is inserted above all of them,
    /// and it is the only entry here that is not about the data.</b> It says the batch's own mapping
    /// names an axis that does not exist, so every finding below it is provisional: the operator has to
    /// author a corrected profile version and run the batch again, and that re-run recomputes the lot.
    /// By this list's own rule — how much a human has to do about it — nothing else on it is larger.
    /// Ranking it first does mean it takes the single code slot on every row of such a batch; it also
    /// puts its message first in <c>WarningMessage</c>, which is what guarantees that message arrives
    /// whole rather than under the clamp (see <see cref="WarningMessageMaxLength"/> — the rest of this
    /// row's findings may not). The alternative is worse in the way that matters, because a batch-wide
    /// fault ranked last is displaced on precisely the rows that have something else wrong with them
    /// and so disappears from a filter by code. It cannot displace anything on a correctly mapped
    /// batch, where it cannot fire at all.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> WarningPrecedence =
    [
        SisImportWarningCode.ClassificationAxisUnknown,
        SisImportWarningCode.StudentIdentityConflict,
        SisImportWarningCode.ClassificationUnavailable,
        SisImportWarningCode.ClassificationConflict,
        SisImportWarningCode.CourseCollegeAdopted,
        SisImportWarningCode.CourseTitleAlias,
        SisImportWarningCode.ClassificationRegNoSuggestsPersonnel,
        SisImportWarningCode.ClassificationMissing,
        SisImportWarningCode.SectionSpansPrograms,
        SisImportWarningCode.InstructorPlaceholder,
        SisImportWarningCode.SectionUnspecified,
    ];

    /// <summary>
    /// Matches <c>SisImportRows.WarningMessage nvarchar(1000)</c>.
    ///
    /// <para>
    /// <b>Two classification findings on one row overflow this, so the clamp decides which of them an
    /// operator ever reads — and that decision is made by <see cref="WarningPrecedence"/> rather than
    /// by the order the passes happened to record them.</b> The Task 5 messages are long because they
    /// each name a remedy: <c>ClassificationMissing</c>'s personnel-prefix text alone is 857
    /// characters, and a REGNO with blank category cells on a batch whose mapping names an unknown axis
    /// carries 1,614 characters of findings. Joined in insertion order, the 614 that fell off the end
    /// were whichever the passes ran last, and the loss was invisible: a message cut mid-sentence still
    /// looks like a message. Ranked, the highest-precedence finding is whole, which is the one the row
    /// is filed under in <c>WarningCode</c>.
    /// </para>
    ///
    /// <para>
    /// <b>This does not make the clamp lossless and nothing here should be read as claiming it does.</b>
    /// A lower-ranked message on a row carrying several long findings can still be truncated or lost
    /// entirely. What changed is that what survives is a consequence of the ranking a reader can see
    /// above, rather than of pass order — and that the message matching <c>WarningCode</c> is never the
    /// one that goes missing, which is the only part any of this is allowed to depend on.
    /// </para>
    /// </summary>
    private const int WarningMessageMaxLength = 1000;

    /// <summary>Matches <c>SisImportRows.ErrorMessage nvarchar(1000)</c>.</summary>
    private const int ErrorMessageMaxLength = 1000;

    private sealed class Entry
    {
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }
        public List<(string Code, string Message)> Warnings { get; } = [];
        public List<(string Type, Guid Id, string Action)> Touches { get; } = [];
        public List<Action> Deferred { get; } = [];
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
    /// Holds a write against an already-tracked entity until this row is staged, so that the chunk's
    /// <c>SaveChangesAsync</c> flushes it in <b>the same transaction</b> as the row's own
    /// <c>Result</c>, <c>WarningCode</c> and <c>WarningMessage</c>.
    ///
    /// <para>
    /// <b>This exists for one caller and one reason: a memory of an announcement must not outlive the
    /// announcement.</b> <c>SisImportService.ResolveClassificationsAsync</c> decides during the fact
    /// pass that a file-vs-record disagreement is news, and records that it has been reported by
    /// setting <c>StudentClassification.ReportedRosterValue</c>. Written there and then, that landed in
    /// the <em>fact pass</em> save, several transactions before the warning it gates — so a process
    /// death in between left the memory on disk with nothing anywhere to show for it, and because the
    /// memory lives on <c>StudentClassifications</c> rather than on <c>SisImportRows</c>, the retry's
    /// delete-and-rebuild never cleared it. The next run read it back, saw the file still saying the
    /// same thing, and stayed quiet about a disagreement nobody had ever been shown.
    /// </para>
    ///
    /// <para>
    /// <b>The write is an <see cref="Action"/> rather than a typed classification method</b> because
    /// this class knows about source rows and their outcomes, and deliberately nothing about the
    /// domain those rows describe: <see cref="Touch"/> already reduces every entity it fans out to a
    /// type name and an id for the same reason. The callback's only contract is that it mutates an
    /// entity the caller has already tracked — nothing here adds, removes or queries, and no deferred
    /// write contributes to the chunk accounting below, so a row's ceiling is still counted purely in
    /// fan-out rows.
    /// </para>
    /// </summary>
    public void DeferUntilStaged(SisImportRow row, Action write) =>
        _entries[row.Id].Deferred.Add(write);

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
    /// <b>The boundary always falls between source rows.</b> A row's outcome, its own fan-out and
    /// anything <see cref="DeferUntilStaged"/> held for it are therefore never written by different
    /// saves, which is what makes a part-written fan-out readable rather than merely inconsistent. It
    /// also makes <paramref name="fanOutChunkSize"/> a ceiling that
    /// only a single row touching more entities than the whole chunk could exceed — fourteen is the
    /// most any row touches (ten, plus one <c>StudentClassification</c> per axis; see
    /// <c>SisImportService.ResolveFactsAsync</c>), so it cannot.
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
        // First, and outside every early return below. A deferred write is held here precisely because
        // it must share a transaction with this row's warning, and the failure branch still writes one
        // — so skipping it for a failed row would re-open, for that row alone, the exact window
        // DeferUntilStaged exists to close.
        foreach (var write in entry.Deferred) write();

        row.WarningCode = null;
        row.WarningMessage = null;
        row.ErrorMessage = null;
        row.SkipReason = null;

        if (entry.Warnings.Count > 0)
        {
            // Ranked once and used for BOTH halves. The code was already chosen by precedence; the
            // message was joined in insertion order and then clamped, so on a row with more than one
            // finding the 1000 characters that survived were an accident of which pass ran first —
            // including, measurably, the message belonging to the code the row is filed under. Sorting
            // the join by the same ranking makes the clamp cut from the least actionable end.
            // OrderBy is stable, so equally ranked findings keep the order they were recorded in.
            var ranked = entry.Warnings
                .OrderBy(w => IndexOfPrecedence(w.Code))
                .ToList();

            row.WarningCode = ranked[0].Code;
            row.WarningMessage = Clamp(
                string.Join(" ", ranked.Select(w => $"[{w.Code}] {w.Message}")),
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
