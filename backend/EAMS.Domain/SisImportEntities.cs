namespace EAMS.Domain;

// Technical Plan §4.12 — SIS import tables, completed by ADR-001 D-4 and D-5.
//
// ---------------------------------------------------------------------------------------------------
// THE THREE TABLES D-1 COUNTED AND PHASE 1 DID NOT LAND
//
// ADR-001 D-1 sized the academic addition at *twelve* tables and pinned only nine of them by name
// ("the remaining table names are pinned during the schema phase, not by this ADR"), leaving a
// follow-up action to do exactly that. The academic layer migration landed those nine. These are the
// other three, and they are all import-side:
//
//   1. SisImportRowEntities   — the row-level fan-out. §4.12 gives SisImportRows a single nullable
//                               StudentId, which was adequate when a source row was a student. This
//                               source row is a student x course x section x teacher, and importing one
//                               touches up to ten rows across ten tables. One nullable FK cannot say
//                               which of them this row created, so "what did row 214 actually do?" —
//                               the first question asked of any import — had no answer in the schema.
//
//   2. SisImportProfiles      — ADR-001 D-4's versioned mapping, header. §10.2 put the column map in
//                               SystemSettings key/value; D-4 replaced that with a versioned table so a
//                               batch can be explained months later against the rules it actually ran
//                               under, rather than against whatever the mapping was last edited to.
//
//   3. SisImportProfileColumns — D-4's mapping, per column. The map is seventeen columns each with its
//                               own normalization rule; that is a child table, not a string.
//
// Naming them here closes ADR-001's first follow-up action. The count is now twelve as D-1 predicted.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// One import run: a file, a term, and the counters that reconcile against its rows.
/// </summary>
public class SisImportBatch : Entity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>
    /// ADR-001 D-5. <b>Required, and declared by the operator — never inferred.</b>
    ///
    /// <para>
    /// The file has no term column, and every plausible way to guess one is wrong in a way nobody
    /// notices: a filename ("CCJ-2025.xlsx" is which semester?), an upload date (imports for next term
    /// happen in this one), or the current term (which makes a backfill silently overwrite the live
    /// roster). A wrong term misfiles an entire batch, and every downstream query is term-scoped, so the
    /// damage is invisible until someone asks a term-specific question. Requiring the operator to say it
    /// converts a silent corruption into a form field.
    /// </para>
    /// </summary>
    public Guid TermId { get; set; }
    public Term? Term { get; set; }

    /// <summary>See <see cref="SisImportSource"/>.</summary>
    public string Source { get; set; } = "";

    public string? FileName { get; set; }

    /// <summary>
    /// Which worksheet the rows came from. The sample workbook has two sheets and only one of them is
    /// the roster; recording the choice means a batch that read the wrong one is diagnosable from the
    /// batch row instead of by re-opening the file.
    /// </summary>
    public string? SourceSheetName { get; set; }

    /// <summary>
    /// SHA-256 of the uploaded bytes, hex. <b>Recorded for future use; nothing reads it yet.</b>
    ///
    /// <para>
    /// It is here so that "is this byte-for-byte the file we imported last time?" is answerable later
    /// from the batch row alone. It is deliberately <em>not</em> wired into a fast path that skips a
    /// matching re-import: idempotency comes from the upserts, which is the stronger design — it holds
    /// for a file that was edited and re-saved as well as for one that was not, and it is provable by
    /// running the pipeline rather than by trusting a hash comparison to have been right. Claiming a
    /// short-circuit here that does not exist is how the two would drift apart.
    /// </para>
    ///
    /// <para>
    /// Deliberately not unique: re-importing the same file into a different term is a supported,
    /// meaningful operation (D-5), and re-importing it into the same term is the idempotency contract
    /// being exercised. A unique index here would forbid both.
    /// </para>
    /// </summary>
    public string? FileHash { get; set; }

    /// <summary>See <see cref="SisImportStatus"/>.</summary>
    public string Status { get; set; } = "";

    public int TotalRows { get; set; }
    public int InsertedRows { get; set; }
    public int UpdatedRows { get; set; }
    public int FailedRows { get; set; }

    /// <summary>
    /// ADR-001 D-5. §4.12 defined <c>Skipped</c> as a row result and gave the batch nowhere to count it,
    /// so <c>Inserted + Updated + Failed</c> did not reconcile to <see cref="TotalRows"/> whenever any
    /// row was skipped — which, on a re-import, is every row.
    /// </summary>
    public int SkippedRows { get; set; }

    /// <summary>
    /// Rows carrying a <see cref="SisImportWarningCode"/>. <b>Orthogonal to the four outcome counters,
    /// not a fifth one</b> — a warned row still imported, so it is already counted as inserted, updated
    /// or skipped. Adding this into the reconciliation would break it.
    /// </summary>
    public int WarningRows { get; set; }

    /// <summary>
    /// ADR-001 D-4: the mapping version this batch executed under. Nullable because a batch may predate
    /// any profile, and because a null here is honest ("we do not know which rules ran") where a
    /// pointer at the current profile would be a lie of exactly the kind D-4 exists to prevent.
    /// </summary>
    public Guid? ImportProfileId { get; set; }
    public SisImportProfile? ImportProfile { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    // -------------------------------------------------------------------------------------------
    // PROGRESS — where a running import has got to.
    //
    // All seven are nullable with NO database default and NO backfill, and that is the whole design
    // rather than an omission. A batch that ran before progress reporting existed genuinely has no
    // progress to report, and NULL is the only value that says so. The four existing counters are
    // `HasDefaultValue(0)` and are right to be — zero inserted rows is a fact about a finished run.
    // Here a 0 would collapse three different truths into one: "this phase has not started", "this
    // phase has no countable units", and "this phase has done none of its units yet". A poller cannot
    // tell those apart, so it would render a progress bar sitting at 0% for a run that is either
    // finished, healthy, or dead.
    //
    // The atomic claim (ADR-004 D-54.4) stamps ProgressPhase and ProgressUpdatedAt when a run starts
    // and clears the other five, so a Running batch always reports the phase it began on. The writer
    // that moves them on between passes is a later phase; until it lands, everything but those two
    // reads NULL — which is exactly what the paragraph above says it should mean.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The step currently executing — see <see cref="SisImportPhase"/>, whose remarks carry the
    /// mapping to <c>SisImportService.ExecuteAsync</c>. Only meaningful while <see cref="Status"/> is
    /// <c>Running</c>; a terminal batch's last value is the step it finished on or died on.
    /// </summary>
    public string? ProgressPhase { get; set; }

    /// <summary>
    /// 1-based position of <see cref="ProgressPhase"/> in <see cref="SisImportPhase.Working"/>,
    /// <b>stored rather than derived</b>. Deriving it at read time would mean a future re-ordering or
    /// insertion in that list silently re-labelled every historical batch's progress; storing it means
    /// an old row keeps reporting the numbering it actually ran under.
    /// </summary>
    public int? ProgressPhaseNumber { get; set; }

    /// <summary>
    /// How many phases the run expects in total, stored for the same reason as
    /// <see cref="ProgressPhaseNumber"/>: "4 of 8" has to stay "4 of 8" after a ninth phase is added.
    /// </summary>
    public int? ProgressPhaseCount { get; set; }

    /// <summary>
    /// Units completed <em>within</em> the current phase, and <see cref="ProgressUnitsTotal"/> is what
    /// they are out of. What a unit is belongs to the phase — rows for a parsing phase, chunks for a
    /// fan-out — so the pair is only ever compared against itself, never across phases.
    ///
    /// <para>
    /// Both are nullable independently because a phase with no meaningful unit count (one long
    /// <c>SaveChanges</c>) must be able to report a phase without inventing a denominator. A UI reads
    /// "no bar for this step", not "0%".
    /// </para>
    /// </summary>
    public int? ProgressUnitsDone { get; set; }

    /// <summary>The denominator for <see cref="ProgressUnitsDone"/>. See its remarks.</summary>
    public int? ProgressUnitsTotal { get; set; }

    /// <summary>
    /// When the progress fields above were last written, UTC.
    ///
    /// <para>
    /// <b>This is the field that makes the rest diagnosable.</b> A detached run that dies — process
    /// recycled, connection lost — leaves a batch reading <c>Running</c> on some phase forever, and
    /// nothing else on the row distinguishes that from a phase that is merely slow. A stale timestamp
    /// does.
    /// </para>
    /// </summary>
    public DateTime? ProgressUpdatedAt { get; set; }

    /// <summary>
    /// Why a run stopped, for an operator, when <see cref="Status"/> is <c>Failed</c>.
    ///
    /// <para>
    /// Distinct from <c>SisImportRow.ErrorMessage</c>, which is per row and says a row could not be
    /// imported. This is per <em>run</em> and says the run itself did not finish — the case where
    /// there may be no failed row at all to explain it, because the failure was the file, the term
    /// check, or the process. Before this column that story lived only in the log, which an operator
    /// looking at a batch does not have.
    /// </para>
    ///
    /// <para>
    /// 400 characters: a sentence an operator can act on, deliberately not a stack trace. A truncated
    /// exception dump would be a worse answer than a short written one, and the log keeps the full
    /// detail for whoever needs it.
    /// </para>
    /// </summary>
    public string? FailureReason { get; set; }

    public Guid? RunByUserId { get; set; }
    public User? RunByUser { get; set; }

    public ICollection<SisImportRow> Rows { get; set; } = new List<SisImportRow>();
}

/// <summary>
/// One row of the source file, staged verbatim at upload and given an outcome at run.
/// </summary>
public class SisImportRow : Entity
{
    public Guid BatchId { get; set; }
    public SisImportBatch? Batch { get; set; }

    /// <summary>
    /// The worksheet row number, 1-based and including the header — so row 2 is the first data row and
    /// the number can be typed straight into Excel's Go To box. An index into the parsed collection
    /// would be the number that is easy to compute and useless to act on.
    /// </summary>
    public int RowNumber { get; set; }

    /// <summary>The source row as JSON, column name to raw cell text. §4.12's <c>nvarchar(max)</c>.</summary>
    public string? RawData { get; set; }

    /// <summary>
    /// SHA-256 of the raw cell values in column order, hex. <b>Recorded for future use; nothing reads
    /// it yet.</b>
    ///
    /// <para>
    /// The intended use is a batch-to-batch row diff — "which rows of this file actually changed since
    /// last term?" as a join rather than a comparison of JSON blobs. No such join exists today, and the
    /// per-row outcome the pipeline already writes answers the question an operator actually asks
    /// after an import, so the column is a cheap option kept open rather than a dependency of anything.
    /// </para>
    /// </summary>
    public string? RowHash { get; set; }

    /// <summary>See <see cref="SisImportRowResult"/>.</summary>
    public string Result { get; set; } = "";

    /// <summary>Set when <see cref="Result"/> is <c>Failed</c>. Prefixed with a <see cref="SisImportFailureCode"/>.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// ADR-001 D-5. Set when <see cref="Result"/> is <c>Skipped</c> — see
    /// <see cref="SisImportSkipReason"/>. A skip with no reason is indistinguishable from a bug.
    /// </summary>
    public string? SkipReason { get; set; }

    /// <summary>See <see cref="SisImportWarningCode"/>. Independent of <see cref="Result"/>: a warned row imported.</summary>
    public string? WarningCode { get; set; }

    /// <summary>The specifics behind <see cref="WarningCode"/> — both course titles, both colleges, the teacher that was dropped.</summary>
    public string? WarningMessage { get; set; }

    /// <summary>
    /// §4.12's original single link. Kept — it is the one entity a human looks for, and every existing
    /// reader expects it — but it is no longer the whole story; see <see cref="Entities"/>.
    /// </summary>
    public Guid? StudentId { get; set; }
    public Student? Student { get; set; }

    /// <summary>Everything this row touched. See <see cref="SisImportRowEntity"/>.</summary>
    public ICollection<SisImportRowEntity> Entities { get; set; } = new List<SisImportRowEntity>();
}

/// <summary>
/// One entity one source row touched, and what it did to it.
///
/// <para>
/// <b>Why <see cref="SisImportRow.StudentId"/> could not do this job.</b> A single row of the CICSS
/// export names a college, a programme, a course, a section, a teacher and a student, and importing it
/// can create or update up to ten rows across ten tables. §4.12's lone nullable <c>StudentId</c> can
/// express one of those ten. So the questions an operator actually asks after an import — "which rows
/// created courses?", "row 214 says Skipped, what did it match against?", "who created this offering?"
/// — had no answer that did not involve re-running the import and watching.
/// </para>
///
/// <para>
/// <b>It records <c>Unchanged</c> as well as <c>Inserted</c> and <c>Updated</c>,</b> and that is the
/// point rather than noise: a re-import's proof is that every row resolved to entities that already
/// existed, which is a positive statement no absence of rows can make.
/// </para>
///
/// <para>
/// A junction-shaped audit row, so no <c>CreatedAt</c>/<c>UpdatedAt</c> — the batch's timestamps are
/// the only ones that mean anything here, and duplicating them per row would be 5,000 redundant
/// datetimes per import.
/// </para>
///
/// <para>
/// <b><see cref="EntityId"/> carries no foreign key,</b> deliberately. It points into one of ten tables
/// depending on <see cref="EntityType"/>, and the alternative — ten nullable FK columns with ten
/// indexes and a ten-way check constraint — would cost real write throughput on the import's hottest
/// table to enforce referential integrity on a diagnostic trail. The trail must also survive its target
/// being deleted, which a real FK (<c>Restrict</c> everywhere here) would prevent outright.
/// </para>
/// </summary>
public class SisImportRowEntity : Entity
{
    public Guid SisImportRowId { get; set; }
    public SisImportRow? SisImportRow { get; set; }

    /// <summary>See <see cref="SisImportEntityType"/>.</summary>
    public string EntityType { get; set; } = "";

    /// <summary>The touched row's <c>Id</c>. Not a foreign key — see the type remarks.</summary>
    public Guid EntityId { get; set; }

    /// <summary>See <see cref="SisImportEntityAction"/>.</summary>
    public string Action { get; set; } = "";
}

/// <summary>
/// ADR-001 D-4: a versioned source-to-target column mapping. One row is one immutable version.
///
/// <para>
/// <b>Why versioned rather than editable.</b> §10.4 claims imports are "idempotent and re-runnable".
/// That claim is only meaningful if the rules a batch ran under can be recovered, and an editable
/// mapping cannot provide that — six months after an import, <c>SystemSettings</c> holds whatever it
/// was last changed to, and there is no way to tell whether a surprising old batch was a bug or the
/// rules of the day. Each edit therefore creates a new version and
/// <see cref="SisImportBatch.ImportProfileId"/> pins the one that ran.
/// </para>
///
/// <para>
/// <b>Rows are never edited or deleted once a batch references them.</b> That is not enforced by a
/// constraint — a database cannot easily express "immutable after first reference" — but it is why
/// <see cref="Version"/> is part of the natural key rather than a column that gets bumped in place.
/// </para>
/// </summary>
public class SisImportProfile : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>Operator-facing name of the mapping — e.g. <c>CICSS Faculty Evaluation Report</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary><see cref="AcademicKey.NormalizeOrUnspecified"/> of <see cref="Name"/>; the key column.</summary>
    public string NameKey { get; set; } = "";

    /// <summary>1-based, monotonic within <see cref="NameKey"/>.</summary>
    public int Version { get; set; }

    /// <summary>See <see cref="SisImportSource"/> — which kind of file this mapping reads.</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// The version a new batch picks up. At most one per <c>(SchoolId, NameKey)</c>, enforced by a
    /// filtered unique index for the same reason <c>Terms.IsCurrent</c> is: two active versions means
    /// every import picks one arbitrarily, and two batches of the same file would be explained by
    /// different rules with nothing to say why.
    /// </summary>
    public bool IsActive { get; set; }

    public string? Description { get; set; }

    public ICollection<SisImportProfileColumn> Columns { get; set; } = new List<SisImportProfileColumn>();
}

/// <summary>
/// One source column's mapping within one profile version: where it comes from, what it feeds, and
/// which normalization rule is applied on the way.
///
/// <para>
/// <b>This is the half §10.2's key/value store could not hold.</b> A <c>SystemSettings</c> row is
/// <c>Key</c>/<c>Value</c>/<c>DataType</c>; the mapping is seventeen tuples of (source column, target
/// field, rule, required?) fanning out across six entities. Encoded into a string it is unqueryable and
/// unconstrainable — "which profile reads COURSE_CODE?" becomes a LIKE over a blob.
/// </para>
///
/// <para>
/// A child of an immutable version, so it carries no timestamps of its own.
/// </para>
/// </summary>
public class SisImportProfileColumn : Entity
{
    public Guid ProfileId { get; set; }
    public SisImportProfile? Profile { get; set; }

    /// <summary>The header as it appears in the file — see <see cref="SisRosterColumns"/>.</summary>
    public string SourceColumn { get; set; } = "";

    /// <summary>
    /// <see cref="SisRosterColumns.HeaderKey"/> of <see cref="SourceColumn"/>. Headers are matched on
    /// this, so <c>COURSE_CODE</c> and <c>Course Code</c> are one column.
    /// </summary>
    public string SourceColumnKey { get; set; } = "";

    /// <summary>Dotted target — <c>Student.StudentNumber</c>, <c>Course.Code</c>. Documentation with a constraint on it.</summary>
    public string TargetField { get; set; } = "";

    /// <summary>
    /// The domain rule applied to this column, named after the method that implements it —
    /// <c>RosterText.CleanName</c>, <c>AcademicKey.NormalizeOrUnspecified</c>, <c>CardUid.Normalize</c>.
    /// Naming the implementation rather than describing it is what keeps the recorded rule and the
    /// executed rule from drifting apart.
    /// </summary>
    public string NormalizationRule { get; set; } = "";

    /// <summary>Whether a row missing this column's value can still be imported.</summary>
    public bool IsRequired { get; set; }

    /// <summary>Display order, so a mapping screen and a diff both read in file order.</summary>
    public int Ordinal { get; set; }
}
