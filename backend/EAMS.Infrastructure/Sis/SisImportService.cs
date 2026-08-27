using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Sis;

/// <summary>
/// Technical Plan §10 — the school-year roster import. See <see cref="ISisImportService"/> for the
/// contract; this file is about how the three passes work and why they are three.
///
/// <para>
/// <b>Pass 1 is distinct-driven, and that is a correctness property before it is a performance one.</b>
/// The source's 536 rows describe 1 college, 1 programme, 21 courses, 18 teachers and 38 offerings.
/// Resolving those per row means asking "does this course exist?" 536 times, and — worse — creating it
/// 536 times inside one unit of work unless every creation is remembered, which is the same dictionary
/// this pass builds, only built accidentally and in the middle of the fact loop. Doing it deliberately
/// and first means the fact loop never decides whether a dimension exists.
/// </para>
///
/// <para>
/// <b>Pass 2 writes facts and nothing else.</b> Every dimension it needs is already resolved, so a row
/// is a lookup and at most five upserts.
/// </para>
///
/// <para>
/// <b>Pass 3 refreshes what is derived.</b> The ADR-001 D-2 student display cache, then
/// <see cref="IStudentGroupProjection"/>. Both are idempotent; neither is a source of truth.
/// </para>
///
/// <para>
/// <b>There is no wrapping transaction, deliberately.</b> Every write here is an upsert and the whole
/// run is idempotent, so an interrupted run is repaired by running it again — which is a recovery an
/// operator already knows how to perform. The alternatives are worse: a transaction spanning ~5,000
/// row-touches holds locks on the roster for the duration of the import, and it cannot be combined with
/// the retrying execution strategy this application registers (<c>EnableRetryOnFailure</c>) without
/// wrapping every phase in <c>IExecutionStrategy.ExecuteAsync</c>, which would silently replay
/// already-committed phases on a transient fault. Partial progress that is safe to re-run beats
/// all-or-nothing that is not safe to retry.
/// </para>
/// </summary>
internal sealed class SisImportService : ISisImportService
{
    private readonly EamsDbContext _db;
    private readonly IStudentGroupProjection _projection;
    private readonly ICurrentUser _currentUser;

    public SisImportService(
        EamsDbContext db, IStudentGroupProjection projection, ICurrentUser currentUser)
    {
        _db = db;
        _projection = projection;
        _currentUser = currentUser;
    }

    /// <summary>How many staged rows an upload preview returns. Enough to eyeball, small enough to render.</summary>
    private const int PreviewSampleSize = 25;

    /// <summary>
    /// The per-command budget <see cref="RunAsync"/> raises the connection to, in seconds.
    ///
    /// <para>
    /// <b>Raised here rather than in <c>AddEamsInfrastructure</c>, because the global 30 seconds is
    /// right for everything else.</b> That default bounds a query a user is waiting on, and widening
    /// it everywhere would turn a stuck read into a request that hangs a screen for ten minutes. The
    /// import is the one operation whose per-command duration scales with the size of a file an
    /// operator chose: <c>ExecuteAsync</c> runs a handful of passes, each ending in one
    /// <c>SaveChanges</c> over every row in the batch, so at twenty thousand rows a single command
    /// legitimately outlives a budget written for a page of students.
    /// </para>
    ///
    /// <para>
    /// Ten minutes: far above the largest roster anyone has described, and still a ceiling rather than
    /// none at all - a command that has not returned in ten minutes is stuck, not slow, and the batch
    /// should say <c>Failed</c> so it can be retried rather than hold a connection indefinitely.
    /// </para>
    /// </summary>
    private const int RunCommandTimeoutSeconds = 600;

    /// <summary>
    /// How many <c>SisImportRowEntity</c> fan-out inserts one <c>SaveChanges</c> is allowed to carry.
    ///
    /// <para>
    /// <b>The number is chosen against SQL Server's lock-escalation threshold, not against a batch
    /// size.</b> EF wraps a multi-statement save in a transaction, and a transaction that takes roughly
    /// five thousand row or page locks on one object is escalated by SQL Server to a lock on the whole
    /// table. A full-roster fan-out is on the order of 190,000 inserts, so a single save crosses that
    /// threshold almost immediately and then holds a table lock on <c>SisImportRowEntities</c> until
    /// every last one has been written — with a transaction log that cannot truncate for the duration.
    /// At 500 a chunk stays an order of magnitude under the threshold, so each transaction is short,
    /// escalates nothing, and commits.
    /// </para>
    ///
    /// <para>
    /// <b>And it is large enough that the chunking costs little.</b> The SQL Server provider already
    /// packs about forty inserts into one command, so 500 is a dozen commands — one round trip's worth
    /// of work per save, and ~380 saves for a 190,000-row fan-out rather than 190,000 of them. Much
    /// smaller trades the lock for round trips; much larger walks back towards escalation.
    /// </para>
    ///
    /// <para>
    /// It is a ceiling per save rather than a quota: <see cref="RowLedger.ApplyInChunks"/> never splits
    /// one source row across two saves, so a save carries the largest whole number of rows that fits.
    /// </para>
    /// </summary>
    private const int FanOutSaveChunkSize = 500;

    // ==================================================================================== upload

    public async Task<SisImportPreviewDto> UploadAsync(
        SisImportUploadRequest request, CancellationToken ct = default)
    {
        var term = await LoadTermAsync(request.TermId, ct);
        var file = ExcelRosterReader.Read(request.Content);
        var profile = await EnsureBuiltInProfileAsync(term.SchoolId, ct);

        var batch = new SisImportBatch
        {
            SchoolId = term.SchoolId,
            TermId = term.Id,
            Source = SisImportSource.Excel,
            FileName = RosterText.Clean(request.FileName),
            SourceSheetName = file.SheetName,
            FileHash = file.FileHash,
            Status = SisImportStatus.Pending,
            TotalRows = file.Rows.Count,
            ImportProfileId = profile.Id,
            RunByUserId = _currentUser.UserId,
        };
        _db.SisImportBatches.Add(batch);

        foreach (var source in file.Rows)
        {
            // Keyed by the header as written, so a row dump is readable by whoever exported the file.
            // The pipeline re-keys through SisRosterColumns.HeaderKey when it reads this back, so the
            // legibility costs nothing at the point of use.
            var raw = new Dictionary<string, string>(file.Columns.Count, StringComparer.Ordinal);
            foreach (var column in file.Columns)
            {
                if (column.Length == 0) continue;
                raw[column] = source.Raw(column);
            }

            _db.SisImportRows.Add(new SisImportRow
            {
                Batch = batch,
                RowNumber = source.RowNumber,
                RawData = JsonSerializer.Serialize(raw),
                RowHash = RosterText.Fingerprint(file.Columns.Select(source.Raw)),
                Result = SisImportRowResult.Pending,
            });
        }

        await _db.SaveChangesAsync(ct);

        return await BuildPreviewAsync(batch, term, file, ct);
    }

    // ======================================================================================= run

    public async Task<SisImportBatchDto> RunAsync(
        Guid batchId, Guid termId, CancellationToken ct = default)
    {
        // AsNoTracking, and that is not a micro-optimization. This read answers the two questions below
        // and is stale the instant it returns; the claim is what decides. Tracking it would leave the
        // context holding the *pre-claim* row as the instance the run then writes through, so the
        // run's own saves would carry the previous attempt's progress and failure reason back over
        // what the claim just cleared. The instance ExecuteAsync writes through is loaded after the
        // claim instead, below.
        var snapshot = await _db.SisImportBatches.IgnoreQueryFilters().AsNoTracking()
                           .FirstOrDefaultAsync(b => b.Id == batchId, ct)
                       ?? throw new SisImportBatchNotFoundException(
                           $"No import batch {batchId}. Upload the file to create one; a batch id is " +
                           "returned by the upload and is not something a caller invents.");

        if (snapshot.TermId != termId)
            throw new SisImportException(
                $"Batch {batchId} was uploaded for term {snapshot.TermId} but the run declared term " +
                $"{termId}. The two must match — see SisImportRunRequest for why the term is confirmed " +
                "at run time as well as at upload.");

        // A completed batch is a historical record, not a re-runnable script: re-running it would
        // rewrite its counters and destroy the evidence of what the first run did. Re-importing is
        // uploading the file again, which produces a second batch and leaves the first intact — and
        // that second batch reporting every row Skipped is the idempotency proof. A Failed batch is
        // different: nothing about it is worth preserving, and retrying is the obvious repair.
        //
        // ADR-004 D-54.4: this is now belt-and-braces, NOT the defence. It reads a row and acts on the
        // answer several statements later, so two callers can both pass it — which is the whole race.
        // TryClaimAsync below is what actually decides, in one statement the database serializes. This
        // check stays because it is where the *reason* lives: the WHERE clause of the claim can say
        // which statuses are re-runnable but not why, and because refusing here saves an already-run
        // batch a term load and a full staged-row read before the same refusal.
        if (snapshot.Status is not (SisImportStatus.Pending or SisImportStatus.Failed))
            throw new SisImportException(
                $"Batch {batchId} has already been run (status {snapshot.Status}). Upload the file " +
                "again to import it a second time; a completed batch is kept as the record of what " +
                "that run did and is not rewritten.");

        var term = await LoadTermAsync(termId, ct);

        var staged = await _db.SisImportRows.IgnoreQueryFilters()
            .Where(r => r.BatchId == batchId)
            .OrderBy(r => r.RowNumber)
            .ToListAsync(ct);

        // Raised for the rest of this scoped context's life, which is this request. See
        // RunCommandTimeoutSeconds for why it is not raised globally.
        _db.Database.SetCommandTimeout(RunCommandTimeoutSeconds);

        // The claim, and it is the last thing that happens before the run starts. Everything above it
        // can throw — an unknown term, an unreadable staged row — and every one of those must leave the
        // batch in the status it arrived in. A claim taken before a step that can fail is a batch left
        // Running with nothing running, which is the zombie ADR-004 exists to eliminate.
        var claimedAt = DateTime.UtcNow;

        if (!await TryClaimAsync(batchId, claimedAt, ct))
            throw new SisImportException(
                $"Batch {batchId} was claimed by another run. A batch runs once at a time, and the " +
                "claim is a single statement, so exactly one caller wins and the rest are told this. " +
                "Wait for the run in progress to finish; if you meant to import the file a second " +
                "time, upload it again — a batch is kept as the record of what that run did and is " +
                "not rewritten.");

        // Read *after* the claim, and it is the only tracked copy of this row in the context. That
        // ordering is load-bearing rather than tidy.
        //
        // ExecuteUpdate writes SQL and deliberately touches no tracked graph, so a copy loaded before
        // the claim would still hold the pre-claim row — the previous attempt's FailureReason, its
        // phase, its unit counts — while being the instance ExecuteAsync mutates and saves through. EF
        // sends the properties that differ from the snapshot it loaded, so the run's own SaveChanges
        // would write all of that back over what the claim had just cleared. Loading it here makes the
        // snapshot the claimed row, so a pass's save carries its pass's changes and nothing else,
        // which is also what SisImportSaveBoundaryTests pins about the progress columns.
        //
        // Reconciling the stale copy in memory instead was tried and is a trap: assigning the claimed
        // values and then declaring the entry Unchanged does not leave EF agreeing with the database,
        // and the run wrote the previous attempt's progress back. The row is the authority; read it.
        var batch = await _db.SisImportBatches.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(b => b.Id == batchId, ct)
                    ?? throw new SisImportBatchNotFoundException(
                        $"Import batch {batchId} was claimed and then disappeared before the run could " +
                        "read it back. Nothing has been imported.");

        try
        {
            await ExecuteAsync(batch, term, staged, ct);
        }
        // Cancellation included, and the carve-out that used to be here is what produced two
        // permanently stuck batches on the deployment VM.
        //
        // The reasoning for excluding it was that a cancelled run has not "failed" - it was called off,
        // and marking it Failed overstates what happened. That is true about the word and wrong about
        // the consequence. `Running` is not in (Pending | Failed), so the re-run guard above refuses a
        // batch in that state forever: a cancelled run left a batch that could not be completed, could
        // not be retried, and was indistinguishable from one still in progress - the exact state the
        // block below says it exists to prevent. `Failed` is the honest label for a run that did not
        // finish, and it is the only one an operator can act on.
        //
        // The status write below already uses CancellationToken.None, so it still lands when the token
        // that got us here is the one that was cancelled.
        catch (Exception ex)
        {
            // Marked and rethrown, never swallowed. The batch says the run stopped and how far it got;
            // the exception still reaches the caller, is logged, and becomes a 500 with a trace id. A
            // batch left in Running forever would be indistinguishable from one still in progress.
            //
            // ExecuteUpdate rather than SaveChanges, and that is the whole point of this block working.
            // The most likely way ExecuteAsync fails IS a SaveChanges failure — a truncation, a unique
            // violation — and at that moment the context still tracks every entity the failed pass
            // added. A recovery SaveChanges would re-attempt all of them, throw the same error from
            // inside this catch, replace the original exception with it, and never persist the status —
            // leaving the batch stuck in Running, which is exactly what this block exists to prevent.
            // ExecuteUpdate issues one UPDATE against the batch row and touches no tracked graph.
            batch.Status = SisImportStatus.Failed;
            batch.FinishedAt = DateTime.UtcNow;

            try
            {
                _db.ChangeTracker.Clear();
                await _db.SisImportBatches.IgnoreQueryFilters()
                    .Where(b => b.Id == batch.Id)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(b => b.Status, batch.Status)
                              .SetProperty(b => b.FinishedAt, batch.FinishedAt),
                        CancellationToken.None);
            }
            catch (Exception statusWriteFailure)
            {
                // Not swallowed, and not rethrown either: the original exception is the one that
                // explains the run, and losing it to a second failure is the defect above. The second
                // is attached to the first so it reaches whoever inspects the exception rather than
                // disappearing.
                ex.Data["SisImportStatusWriteFailure"] = statusWriteFailure.ToString();
            }

            throw;
        }

        return ToDto(batch, term);
    }

    /// <summary>
    /// The phase a claimed batch reports until the progress writer moves it on — the first of
    /// <see cref="SisImportPhase.Working"/>, read from that list rather than named, so a re-ordering
    /// there cannot leave the claim stamping a phase the run no longer starts with.
    /// </summary>
    private static string FirstWorkingPhase => SisImportPhase.Working[0];

    /// <summary>
    /// ADR-004 D-54.4 — moves a batch to <c>Running</c> in <b>one</b> statement, and reports whether
    /// this caller is the one that moved it.
    ///
    /// <para>
    /// <b>What it replaces is a read, a check and a write with no transaction around them.</b> Two
    /// callers could both load a <c>Pending</c> batch, both find it re-runnable, and both start
    /// importing the same roster. The window was small while the run was bounded by the HTTP request —
    /// a second operator had to click inside it — and detaching the run (D-54) makes it ordinary: a
    /// double-submit, a retried request, or two tabs. Here the deciding and the writing are the same
    /// statement, so the row lock picks the winner and there is no window at all. Zero rows affected
    /// means somebody else got there first, which the controller answers <c>409</c>.
    /// </para>
    ///
    /// <para>
    /// <b><c>IgnoreQueryFilters</c> is load-bearing, not tidiness.</b> <c>EamsDbContext</c> puts a
    /// tenancy filter on <c>SisImportBatch</c>; without this the predicate carries a <c>SchoolId</c>
    /// term as well, and a scoped context whose tenant does not resolve updates zero rows — which is
    /// indistinguishable here from losing the race. Every claim would look refused and no import would
    /// ever start, with nothing in the failure saying why. It matches the reads either side of it.
    /// </para>
    ///
    /// <para>
    /// <b>The progress columns are reset, not merely written</b> (D-54.4). A retry of a <c>Failed</c>
    /// batch that carried the previous attempt's <c>FailureReason</c>, <c>FinishedAt</c>, phase number
    /// and unit counts into the new run would show an operator the last run's death notice and a
    /// fabricated position — "phase 5 of 8" against a run that has just started phase 1. Cleared to
    /// <c>NULL</c> rather than to zero, for the reason <c>SisImportBatch</c>'s progress block gives:
    /// <c>NULL</c> is "nothing to report", and 0 is a claim about work done.
    /// <see cref="SisImportBatch.ProgressPhaseNumber"/> and its count stay <c>NULL</c> here because
    /// they are the progress writer's to fill; what this statement owes them is only that they do not
    /// describe a previous run.
    /// </para>
    /// </summary>
    private async Task<bool> TryClaimAsync(Guid batchId, DateTime claimedAt, CancellationToken ct)
    {
        var claimed = await _db.SisImportBatches.IgnoreQueryFilters()
            .Where(b => b.Id == batchId
                        && (b.Status == SisImportStatus.Pending || b.Status == SisImportStatus.Failed))
            .ExecuteUpdateAsync(
                s => s.SetProperty(b => b.Status, SisImportStatus.Running)
                      .SetProperty(b => b.StartedAt, (DateTime?)claimedAt)
                      .SetProperty(b => b.ProgressPhase, (string?)FirstWorkingPhase)
                      .SetProperty(b => b.ProgressPhaseNumber, (int?)null)
                      .SetProperty(b => b.ProgressPhaseCount, (int?)null)
                      .SetProperty(b => b.ProgressUnitsDone, (int?)null)
                      .SetProperty(b => b.ProgressUnitsTotal, (int?)null)
                      .SetProperty(b => b.ProgressUpdatedAt, (DateTime?)claimedAt)
                      .SetProperty(b => b.FailureReason, (string?)null)
                      .SetProperty(b => b.FinishedAt, (DateTime?)null),
                ct);

        // Exactly one, not "at least one": Id is the primary key, so a claim that reported two rows
        // would mean the predicate no longer identifies a single batch, and treating that as success
        // would start a run over a set nobody has looked at.
        return claimed == 1;
    }

    private async Task ExecuteAsync(
        SisImportBatch batch, Term term, IReadOnlyList<SisImportRow> staged, CancellationToken ct)
    {
        // Only ever non-empty when this is a retry of a Failed batch. The fan-out is rebuilt from
        // scratch each run, so leaving the previous attempt's rows would double every entry and make
        // the trail — whose entire job is to say what happened — say it twice with different answers.
        // ExecuteDelete rather than loading and removing: these rows carry no guarded columns and there
        // can be several thousand of them.
        var rowIds = staged.Select(r => r.Id).ToList();
        await _db.SisImportRowEntities.IgnoreQueryFilters()
            .Where(e => rowIds.Contains(e.SisImportRowId))
            .ExecuteDeleteAsync(ct);

        var ledger = new RowLedger(staged);
        var parsed = ParseRows(staged, await ResolveRfidColumnKeyAsync(batch, ct), ledger);

        var dimensions = await ResolveDimensionsAsync(term, parsed, ledger, ct);
        await _db.SaveChangesAsync(ct);

        // The fact pass can fail rows of its own — one whose RFID serial belongs to another student —
        // and those rows are excluded from the derived cache for the same reason they were excluded
        // from the facts: a failed row must not have contributed anything, including a Section string.
        var mismatchedRows = await ResolveFactsAsync(term, parsed, dimensions, ledger, ct);
        await _db.SaveChangesAsync(ct);

        // ---- the fan-out, in saves of its own -----------------------------------------------------
        //
        // Split off the batch-row update below, and chunked, and both halves of that are about how long
        // a lock is held rather than about throughput. EF wraps a multi-statement SaveChanges in a
        // transaction, so one save carrying the whole fan-out *and* the batch row holds an exclusive
        // lock on the SisImportBatches row for the length of the longest phase in the run — which is
        // precisely the interval a progress poller exists to report on. This deployment does not enable
        // RCSI (the only snapshot work in the repo is inside AttendanceLiveTests, and EventService
        // declines to treat it as a feature's decision), so under READ COMMITTED that poll blocks
        // rather than reading a slightly stale row: a progress endpoint that hangs during the part you
        // most need to watch is the original complaint arriving by a different door.
        //
        // Accepted consequence, decided rather than overlooked. A failure part-way through now leaves
        // some SisImportRows carrying terminal results and the rest still Pending, where one save left
        // them all Pending. Both states repair identically — the batch goes Failed, and a retry's
        // ExecuteAsync deletes the prior fan-out and RowLedger re-assigns Result, WarningCode,
        // WarningMessage, ErrorMessage and SkipReason on every row unconditionally — so the
        // inconsistency is cosmetic and is only ever visible on a batch that already says Failed. Tally
        // is unaffected either way: it counts the in-memory `staged` list, which the fold populates in
        // full however the saves were grouped.
        foreach (var _ in ledger.ApplyInChunks(_db, FanOutSaveChunkSize))
        {
            await _db.SaveChangesAsync(ct);
        }

        // ---- the batch row, alone -----------------------------------------------------------------
        //
        // Never in the same save as a SisImportRowEntity insert. That is the whole of the change above,
        // and no read-back can see it — the rows on disk are identical either way — which is why
        // SisImportSaveBoundaryTests asserts about save boundaries instead.
        Tally(batch, staged);
        batch.FinishedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await RefreshStudentCacheAsync(term, parsed, dimensions, mismatchedRows, ct);
        await _projection.SyncTermAsync(term.Id, ct);
    }

    /// <summary>
    /// Which source column this batch takes the RFID card serial from, as a
    /// <see cref="SisRosterColumns.HeaderKey"/> — or <c>null</c> when the mapping does not carry one, in
    /// which case no row in the batch resolves a card.
    ///
    /// <para>
    /// <b>Read from the batch's own ADR-001 D-4 profile rows, not from
    /// <see cref="SisRosterColumns.RfidCardSerial"/>, and that is the whole point of the seam.</b> We do
    /// not have the client's export and do not know what they will call the column. The profile is the
    /// designed venue for exactly that unknown: when the file arrives naming it <c>CARD_SERIAL</c> or
    /// <c>RFID NO.</c>, an operator authors profile version 3 with that <c>SourceColumn</c> and the
    /// pipeline reads it with no code change and no redeploy. The constant supplies only the built-in
    /// version's default. This is genuinely wired — the resolution below is the only thing that decides
    /// which cell is read — rather than a table written for show.
    /// </para>
    ///
    /// <para>
    /// It reads the profile the <em>batch</em> points at, not the currently active one, for the same
    /// reason D-4 exists: a batch re-run after a mapping change must execute the rules it was uploaded
    /// under, not whatever is live today.
    /// </para>
    /// </summary>
    private async Task<string?> ResolveRfidColumnKeyAsync(SisImportBatch batch, CancellationToken ct)
    {
        if (batch.ImportProfileId is not { } profileId) return null;

        var target = SisImportProfileTemplate.RfidCardUidTarget;

        // Ordinal, so a mapping that lists the target twice — which the profile's own unique index
        // permits across two different source columns — resolves the same way on every run rather than
        // by whatever order the server returns rows in.
        var column = await _db.SisImportProfileColumns.IgnoreQueryFilters()
            .Where(c => c.ProfileId == profileId && c.TargetField == target)
            .OrderBy(c => c.Ordinal)
            .FirstOrDefaultAsync(ct);

        if (column is null) return null;

        var key = column.SourceColumnKey.Length > 0
            ? column.SourceColumnKey
            : SisRosterColumns.HeaderKey(column.SourceColumn);

        return key.Length == 0 ? null : key;
    }

    // =========================================================================== parse (per row)

    /// <summary>
    /// One staged row, interpreted. Every value here has already been through <see cref="RosterText"/>
    /// and, where it is a key, <see cref="AcademicKey"/> — nothing downstream re-normalizes, so there is
    /// exactly one place a normalization rule is applied.
    /// </summary>
    private sealed record ParsedRow(
        SisImportRow Staged,
        string RegNo,
        // The row's RFID card serial, normalized — or null when the row names no card, which is every
        // row of every file until the client's RFID-bearing export arrives. Null is a first-class value
        // here and not a defect: it means "this student has no card", the row still imports in full, and
        // no RfidCards row is created. See ParseRows for why it is not even warned about.
        string? CardUid,
        string FirstName,
        string? MiddleName,
        string LastName,
        string? Email,
        string? AlternateEmail,
        string CollegeName,
        string CollegeKey,
        string ProgramCode,
        string ProgramKey,
        string? SectionName,
        string SectionKey,
        string CourseCode,
        string CourseKey,
        string? CourseTitle,
        TeacherName Teacher,
        string? TeacherKey);

    /// <summary>
    /// Interprets the staged rows. <paramref name="rfidColumnKey"/> is the
    /// <see cref="SisRosterColumns.HeaderKey"/> the batch's profile maps to
    /// <c>RfidCard.CardUid</c>, or <c>null</c> when the mapping names no such column.
    ///
    /// <para>
    /// <b>A row with no RFID serial produces no warning and no fan-out row, deliberately.</b> This is
    /// the normal case, not an anomaly: the roster in hand has no RFID column at all, so a warning would
    /// fire on 100% of the rows of every batch and make <c>CompletedWithWarnings</c> the permanent
    /// status of every import — destroying the one-field "do I need to go and look at this batch?"
    /// signal that <see cref="SisImportStatus"/> exists to carry, and burying the genuine warnings
    /// underneath it. Nor is the fact unrecorded: the absence of a
    /// <see cref="SisImportEntityType.RfidCard"/> entry in the row's fan-out already says "this row
    /// touched no card", which is the same positive trail a revoked card leaves and is queryable in
    /// exactly the same way.
    /// </para>
    ///
    /// <para>
    /// It follows that the serial can never raise
    /// <see cref="SisImportFailureCode.MissingRequiredValue"/> — there is nothing required about it.
    /// </para>
    /// </summary>
    private List<ParsedRow> ParseRows(
        IReadOnlyList<SisImportRow> staged, string? rfidColumnKey, RowLedger ledger)
    {
        var parsed = new List<ParsedRow>(staged.Count);

        foreach (var row in staged)
        {
            var cells = ReadCells(row);
            string Raw(string column) =>
                cells.TryGetValue(SisRosterColumns.HeaderKey(column), out var v) ? v : "";

            // REGNO is the one column without which the row names nobody: it is the student number and
            // the key every fact in the row hangs off. It is NOT the card UID — the RFID serial is its
            // own column, read below, and its absence is not an error.
            var regNo = RosterText.Clean(Raw(SisRosterColumns.RegNo));
            if (regNo is null)
            {
                ledger.Fail(row, SisImportFailureCode.MissingRequiredValue,
                    $"{SisRosterColumns.RegNo} is blank. It is the student number, and nothing in the " +
                    "row can be placed without it.");
                continue;
            }

            var firstName = RosterText.CleanName(Raw(SisRosterColumns.StudentFirstName));
            var lastName = RosterText.CleanName(Raw(SisRosterColumns.StudentLastName));
            if (firstName is null || lastName is null)
            {
                ledger.Fail(row, SisImportFailureCode.MissingRequiredValue,
                    $"{SisRosterColumns.StudentFirstName} and {SisRosterColumns.StudentLastName} are " +
                    $"both required (Students has them NOT NULL); REGNO {regNo} supplied " +
                    $"'{Raw(SisRosterColumns.StudentFirstName)}' / '{Raw(SisRosterColumns.StudentLastName)}'.");
                continue;
            }

            var collegeName = RosterText.Clean(Raw(SisRosterColumns.CollegeName));
            var programCode = RosterText.Clean(Raw(SisRosterColumns.Program));
            var courseCode = RosterText.Clean(Raw(SisRosterColumns.CourseCode));
            if (collegeName is null || programCode is null || courseCode is null)
            {
                ledger.Fail(row, SisImportFailureCode.MissingRequiredValue,
                    $"{SisRosterColumns.CollegeName}, {SisRosterColumns.Program} and " +
                    $"{SisRosterColumns.CourseCode} are required to place an enrollment; REGNO {regNo} " +
                    "left at least one blank.");
                continue;
            }

            var sectionName = RosterText.Clean(Raw(SisRosterColumns.SectionName));
            var keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SisRosterColumns.CollegeName] = AcademicKey.NormalizeOrUnspecified(collegeName),
                [SisRosterColumns.Program] = AcademicKey.NormalizeOrUnspecified(programCode),
                [SisRosterColumns.CourseCode] = AcademicKey.NormalizeOrUnspecified(courseCode),
                [SisRosterColumns.SectionName] = AcademicKey.NormalizeOrUnspecified(sectionName),
            };

            // Checked here rather than left to SQL Server, whose truncation error (2628) names neither
            // the row nor the column — see AcademicKey.IsWithinLength.
            var overlong = keys.FirstOrDefault(k => !AcademicKey.IsWithinLength(k.Value));
            if (overlong.Key is not null)
            {
                ledger.Fail(row, SisImportFailureCode.KeyTooLong,
                    $"{overlong.Key} normalizes to {overlong.Value.Length} characters, over the " +
                    $"{AcademicKey.MaxLength}-character key limit.");
                continue;
            }

            var teacher = TeacherNames.Parse(
                Raw(SisRosterColumns.TeacherFullName),
                Raw(SisRosterColumns.TeacherFirstName),
                Raw(SisRosterColumns.TeacherLastName),
                Raw(SisRosterColumns.TeacherSuffix));

            var cardUid = ReadCardUid(rfidColumnKey, cells);

            // A pre-2026-07-30 profile maps the card UID out of REGNO, and D-4 requires that mapping to
            // go on running for the batches pinned to it. Allowed, but no longer silent: without this the
            // only trace that a re-run minted student-number cards is the serials themselves.
            if (cardUid is not null && IsLegacyCardUidColumn(rfidColumnKey))
            {
                ledger.Warn(row, SisImportWarningCode.RfidCardFromLegacyMapping,
                    $"This batch's import profile maps the card serial from a column other than " +
                    $"'{SisRosterColumns.RfidCardSerial}', so the card for this row carries " +
                    $"'{cardUid}' — which is the student number, not an RFID serial. That is what the " +
                    "profile version this batch is pinned to says to do, and re-running a batch under " +
                    "its own rules is deliberate (ADR-001 D-4). Upload the file again to import it " +
                    "under the current mapping instead.");
            }

            parsed.Add(new ParsedRow(
                Staged: row,
                RegNo: regNo,
                CardUid: cardUid,
                FirstName: firstName,
                MiddleName: RosterText.CleanName(Raw(SisRosterColumns.StudentMiddleName)),
                LastName: lastName,
                Email: RosterText.CleanEmail(Raw(SisRosterColumns.UsaEmail)),
                AlternateEmail: RosterText.CleanEmail(Raw(SisRosterColumns.EmailId)),
                CollegeName: collegeName,
                CollegeKey: keys[SisRosterColumns.CollegeName],
                ProgramCode: programCode,
                ProgramKey: keys[SisRosterColumns.Program],
                SectionName: sectionName,
                SectionKey: keys[SisRosterColumns.SectionName],
                CourseCode: courseCode,
                CourseKey: keys[SisRosterColumns.CourseCode],
                CourseTitle: RosterText.Clean(Raw(SisRosterColumns.CourseName)),
                Teacher: teacher,
                TeacherKey: teacher.DisplayName is null
                    ? null
                    : AcademicKey.NormalizeOrUnspecified(teacher.DisplayName)));

            if (sectionName is null)
                ledger.Warn(row, SisImportWarningCode.SectionUnspecified,
                    $"{SisRosterColumns.SectionName} is blank; the offering is filed under " +
                    $"'{AcademicKey.Unspecified}' and its students get no section group.");

            if (teacher.IsPlaceholder)
                ledger.Warn(row, SisImportWarningCode.InstructorPlaceholder,
                    $"Teacher is the '{Raw(SisRosterColumns.TeacherFullName)}' placeholder; the " +
                    "offering is left unstaffed and no instructor row was created.");
        }

        return parsed;
    }

    /// <summary>
    /// One row's RFID card serial, or <c>null</c> when the mapping names no RFID column, the file has no
    /// such column, or the cell is blank — three different absences that mean the same thing here and
    /// are deliberately not distinguished, because the answer is identical in all three: this student
    /// has no card and imports anyway.
    ///
    /// <para>
    /// <see cref="RosterText.Clean"/> then <see cref="CardUid.Normalize"/>, in that order and for the
    /// same reason every other column runs the two: invisible characters and stray whitespace first,
    /// then the canonical UID form every reader lookup compares against. <b>Leading zeros survive both</b>
    /// — neither strips a digit — so the <c>0012503326</c> the export carries is the
    /// <c>0012503326</c> a tap has to match. What can still lose them is the cell arriving as an Excel
    /// <em>number</em>; see <see cref="RosterText.FormatNumericCell"/>.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether the profile draws <c>RfidCard.CardUid</c> from something other than the RFID column.
    ///
    /// <para>
    /// <c>null</c> is <b>not</b> legacy: it means the mapping names no card column at all, which is the
    /// ordinary state of every import today and is deliberately silent (see <see cref="ParseRows"/>).
    /// Only a mapping that names a <em>different</em> column is making a claim this system no longer
    /// believes.
    /// </para>
    /// </summary>
    private static bool IsLegacyCardUidColumn(string? rfidColumnKey) =>
        rfidColumnKey is not null
        && !string.Equals(
            rfidColumnKey,
            SisRosterColumns.HeaderKey(SisRosterColumns.RfidCardSerial),
            StringComparison.Ordinal);

    private static string? ReadCardUid(string? rfidColumnKey, IReadOnlyDictionary<string, string> cells)
    {
        if (rfidColumnKey is null) return null;
        if (!cells.TryGetValue(rfidColumnKey, out var raw)) return null;

        var cleaned = RosterText.Clean(raw);
        if (cleaned is null) return null;

        var normalized = CardUid.Normalize(cleaned);
        return normalized.Length == 0 ? null : normalized;
    }

    private static Dictionary<string, string> ReadCells(SisImportRow row)
    {
        var raw = row.RawData is null
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(row.RawData) ?? [];

        var cells = new Dictionary<string, string>(raw.Count, StringComparer.Ordinal);
        foreach (var (header, value) in raw)
        {
            var key = SisRosterColumns.HeaderKey(header);
            if (key.Length > 0) cells.TryAdd(key, value);
        }

        return cells;
    }

    // ============================================================================ pass 1: dimensions

    /// <summary>
    /// A dimension row plus the credit for creating it: which staged row first named it, and what
    /// happened to it then. Every <em>other</em> row that names it reports <c>Unchanged</c>, which is
    /// what stops 536 rows all claiming to have created the same college.
    /// </summary>
    private sealed record Touched<T>(T Entity, string Action, int RowNumber)
    {
        public string ActionFor(int rowNumber) =>
            rowNumber == RowNumber ? Action : SisImportEntityAction.Unchanged;
    }

    private sealed record Dimensions(
        IReadOnlyDictionary<string, Touched<College>> Colleges,
        IReadOnlyDictionary<string, Touched<AcademicProgram>> Programs,
        IReadOnlyDictionary<string, Touched<Course>> Courses,
        IReadOnlyDictionary<string, Touched<Instructor>> Instructors,
        IReadOnlyDictionary<(Guid CourseId, string SectionKey), Touched<CourseOffering>> Offerings,
        IReadOnlySet<int> CollidedRows);

    private async Task<Dimensions> ResolveDimensionsAsync(
        Term term, IReadOnlyList<ParsedRow> parsed, RowLedger ledger, CancellationToken ct)
    {
        var schoolId = term.SchoolId;

        // Every read below carries an explicit SchoolId (or TermId) predicate and ignores the ambient
        // query filter, for the reason StudentGroupProjection spells out: the filter is inert whenever
        // no tenant is pinned, and an import that silently resolved another school's course would write
        // a cross-tenant row into the roster. The scope is not dropped, it is stated.
        var colleges = await _db.Colleges.IgnoreQueryFilters()
            .Where(c => c.SchoolId == schoolId).ToDictionaryAsync(c => c.NameKey, ct);
        var programs = await _db.Programs.IgnoreQueryFilters()
            .Where(p => p.SchoolId == schoolId).ToDictionaryAsync(p => p.CodeKey, ct);
        var courses = await _db.Courses.IgnoreQueryFilters()
            .Where(c => c.SchoolId == schoolId).ToDictionaryAsync(c => c.CodeKey, ct);
        var instructors = await _db.Instructors.IgnoreQueryFilters()
            .Where(i => i.SchoolId == schoolId).ToDictionaryAsync(i => i.NameKey, ct);

        var resolvedColleges = ResolveColleges(schoolId, parsed, colleges);
        var resolvedPrograms = ResolvePrograms(schoolId, parsed, programs, resolvedColleges);
        var (resolvedCourses, collidedRows) =
            ResolveCourses(schoolId, parsed, courses, resolvedColleges, ledger);
        var resolvedInstructors = ResolveInstructors(schoolId, parsed, instructors);

        WarnOnSectionsSpanningPrograms(parsed, ledger);

        // Offerings depend on the courses above, including ones that do not exist in the database yet.
        // That works because Entity assigns its Id in the initializer, so a pending course already has
        // the value the offering's FK needs and EF orders the two inserts by the graph.
        await _db.SaveChangesAsync(ct);

        var offerings = await ResolveOfferingsAsync(term, parsed, resolvedCourses, collidedRows, ct);

        return new Dimensions(
            resolvedColleges, resolvedPrograms, resolvedCourses, resolvedInstructors,
            offerings, collidedRows);
    }

    private Dictionary<string, Touched<College>> ResolveColleges(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed, Dictionary<string, College> existing)
    {
        var resolved = new Dictionary<string, Touched<College>>(StringComparer.Ordinal);

        foreach (var group in GroupRows(parsed, r => r.CollegeKey))
        {
            var first = group.First;

            if (existing.TryGetValue(first.CollegeKey, out var college))
            {
                var changed = Assign(college.Name, first.CollegeName, v => college.Name = v);
                if (changed) college.UpdatedAt = DateTime.UtcNow;
                resolved[first.CollegeKey] = new Touched<College>(
                    college, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            college = new College
            {
                SchoolId = schoolId,
                Name = first.CollegeName,
                NameKey = first.CollegeKey,
            };
            _db.Colleges.Add(college);
            existing[first.CollegeKey] = college;
            resolved[first.CollegeKey] =
                new Touched<College>(college, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    private Dictionary<string, Touched<AcademicProgram>> ResolvePrograms(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed,
        Dictionary<string, AcademicProgram> existing,
        IReadOnlyDictionary<string, Touched<College>> colleges)
    {
        var resolved = new Dictionary<string, Touched<AcademicProgram>>(StringComparer.Ordinal);

        foreach (var group in GroupRows(parsed, r => r.ProgramKey))
        {
            var first = group.First;
            var collegeId = colleges[first.CollegeKey].Entity.Id;

            if (existing.TryGetValue(first.ProgramKey, out var program))
            {
                var changed = Assign(program.Code, first.ProgramCode, v => program.Code = v);
                // A programme's college is a required FK, so an existing row always has one. Moving it
                // is a real change and is reported as an update rather than being applied quietly.
                changed |= Assign(program.CollegeId, collegeId, v => program.CollegeId = v);
                if (changed) program.UpdatedAt = DateTime.UtcNow;
                resolved[first.ProgramKey] = new Touched<AcademicProgram>(
                    program, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            program = new AcademicProgram
            {
                SchoolId = schoolId,
                CollegeId = collegeId,
                Code = first.ProgramCode,
                CodeKey = first.ProgramKey,
                Name = first.ProgramCode,
            };
            _db.Programs.Add(program);
            existing[first.ProgramKey] = program;
            resolved[first.ProgramKey] = new Touched<AcademicProgram>(
                program, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    /// <summary>
    /// Resolves courses, and is where the two hardest source defects are decided.
    ///
    /// <para>
    /// <b>One code, two titles.</b> <c>'GE Elect 2'</c> resolves to <em>The Entrepreneurial Mind</em> on
    /// 25 rows and <em>Gender and Society / Entrepreneurial Mind</em> on 6. <c>Courses.Title</c> is
    /// single-valued and is not identity (see <see cref="Course"/>), so one of them is necessarily lost
    /// from that column. First-seen wins — deterministic, because the rows are processed in worksheet
    /// order — and every row carrying the losing title imports normally with a
    /// <see cref="SisImportWarningCode.CourseTitleAlias"/> naming both. The row's own <c>RawData</c>
    /// keeps the original verbatim, so nothing is destroyed; what is refused is doing it silently.
    /// </para>
    ///
    /// <para>
    /// <b>One code, two colleges.</b> This one fails the row. See
    /// <see cref="SisImportFailureCode.CourseCollegeCollision"/> for why a silent resolve to the
    /// existing course is unrecoverable where a failed row is merely annoying.
    /// </para>
    /// </summary>
    private (Dictionary<string, Touched<Course>> Courses, HashSet<int> CollidedRows) ResolveCourses(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed, Dictionary<string, Course> existing,
        IReadOnlyDictionary<string, Touched<College>> colleges, RowLedger ledger)
    {
        var resolved = new Dictionary<string, Touched<Course>>(StringComparer.Ordinal);
        var collided = new HashSet<int>();

        foreach (var group in GroupRows(parsed, r => r.CourseKey))
        {
            var first = group.First;
            var collegeId = colleges[first.CollegeKey].Entity.Id;

            if (existing.TryGetValue(first.CourseKey, out var course))
            {
                if (course.CollegeId is { } owner && owner != collegeId)
                {
                    // Every row naming this course code fails, not just the first. Importing some of
                    // them would leave a course half-populated under a college it may not belong to,
                    // which is the ambiguous middle state this check exists to prevent.
                    foreach (var row in group.All)
                    {
                        collided.Add(row.Staged.RowNumber);
                        ledger.Fail(row.Staged, SisImportFailureCode.CourseCollegeCollision,
                            $"Course code '{first.CourseCode}' (key {first.CourseKey}) already exists " +
                            $"under a different college. Existing CollegeId {owner}; this row says " +
                            $"'{row.CollegeName}' ({collegeId}). This FAILS rather than warns because " +
                            "Courses are keyed UNIQUE(SchoolId, CodeKey) — institution-wide — and " +
                            "Courses.CollegeId is single-valued: resolving to the existing row merges " +
                            "two colleges' courses irreversibly, because the column cannot then hold " +
                            "both values. ADR-002 D-11 records this key as a deliberate hedge whose " +
                            "widening path (backfill CollegeId, make it NOT NULL, re-index to " +
                            "UNIQUE(SchoolId, CollegeId, CodeKey)) is data-preserving ONLY while no " +
                            "merged data exists — after a silent merge it becomes a split that has to " +
                            "re-key live CourseOfferings. Do not downgrade this to a warning to clear " +
                            "a blocked batch; fix the source, or widen the key first, then re-import.");
                    }

                    continue;
                }

                var changed = Assign(course.Code, first.CourseCode, v => course.Code = v);

                if (course.CollegeId is null)
                {
                    // Filling a blank rather than overwriting a value: an enrichment, not a conflict.
                    // Still warned, because it changes what a shared row means and someone should be
                    // able to see when it happened.
                    course.CollegeId = collegeId;
                    changed = true;
                    foreach (var row in group.All)
                        ledger.Warn(row.Staged, SisImportWarningCode.CourseCollegeAdopted,
                            $"Course '{first.CourseCode}' had no college recorded; adopted " +
                            $"'{first.CollegeName}' from this import.");
                }

                if (first.CourseTitle is not null && course.Title is null)
                {
                    course.Title = first.CourseTitle;
                    changed = true;
                }

                if (changed) course.UpdatedAt = DateTime.UtcNow;
                resolved[first.CourseKey] = new Touched<Course>(
                    course, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
            }
            else
            {
                course = new Course
                {
                    SchoolId = schoolId,
                    CollegeId = collegeId,
                    Code = first.CourseCode,
                    CodeKey = first.CourseKey,
                    Title = first.CourseTitle,
                };
                _db.Courses.Add(course);
                existing[first.CourseKey] = course;
                resolved[first.CourseKey] = new Touched<Course>(
                    course, SisImportEntityAction.Inserted, first.Staged.RowNumber);
            }

            WarnOnTitleAliases(group, course, ledger);
        }

        return (resolved, collided);
    }

    private static void WarnOnTitleAliases(RowGroup group, Course course, RowLedger ledger)
    {
        foreach (var row in group.All)
        {
            if (row.CourseTitle is null) continue;
            if (string.Equals(row.CourseTitle, course.Title, StringComparison.Ordinal)) continue;

            ledger.Warn(row.Staged, SisImportWarningCode.CourseTitleAlias,
                $"Course code '{row.CourseCode}' is recorded as '{course.Title}' but this row calls it " +
                $"'{row.CourseTitle}'. The first-seen title is kept on Courses.Title and this one is " +
                "recorded here as an alias; the row imported normally. Courses.Title is not identity " +
                "(see the Course type remarks) — the code is.");
        }
    }

    private Dictionary<string, Touched<Instructor>> ResolveInstructors(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed, Dictionary<string, Instructor> existing)
    {
        var resolved = new Dictionary<string, Touched<Instructor>>(StringComparer.Ordinal);

        // The placeholder is filtered out here, once, which is the whole implementation of "no
        // synthetic TBA instructor". Nothing downstream has to remember: there is simply no instructor
        // to link, and CourseOfferingInstructors is many-to-many so zero teachers is representable.
        var named = parsed.Where(r => !r.Teacher.IsPlaceholder && r.TeacherKey is not null).ToList();

        foreach (var group in GroupRows(named, r => r.TeacherKey!))
        {
            var first = group.First;
            var key = first.TeacherKey!;

            if (existing.TryGetValue(key, out var instructor))
            {
                var changed = Assign(
                    instructor.DisplayName, first.Teacher.DisplayName!, v => instructor.DisplayName = v);
                if (changed) instructor.UpdatedAt = DateTime.UtcNow;
                resolved[key] = new Touched<Instructor>(
                    instructor,
                    changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            instructor = new Instructor
            {
                SchoolId = schoolId,
                DisplayName = first.Teacher.DisplayName!,
                NameKey = key,
            };
            _db.Instructors.Add(instructor);
            existing[key] = instructor;
            resolved[key] = new Touched<Instructor>(
                instructor, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    /// <summary>
    /// The section half of the Phase 1 review's collision requirement.
    ///
    /// <para>
    /// <b>It warns where the course collision fails, and the asymmetry is deliberate.</b>
    /// <c>CourseOfferings</c> is keyed <c>(TermId, CourseId, SectionKey)</c> — the course is <em>in</em>
    /// the key — so two programmes using the section name <c>2-A</c> only collide when they are also
    /// the same course, which is usually genuinely the same class. <c>Courses</c> is keyed
    /// <c>(SchoolId, CodeKey)</c> with no such qualifier, which is why that one is unrecoverable and
    /// this one is not. What is not acceptable is silence: a section name shared across programmes is
    /// the leading indicator that the roster has outgrown institution-wide keys, and this is where it
    /// becomes visible before it becomes a merge.
    /// </para>
    /// </summary>
    private static void WarnOnSectionsSpanningPrograms(IReadOnlyList<ParsedRow> parsed, RowLedger ledger)
    {
        var programsBySection = parsed
            .Where(r => r.SectionKey != AcademicKey.Unspecified)
            .GroupBy(r => r.SectionKey, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.ProgramCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        foreach (var row in parsed)
        {
            if (!programsBySection.TryGetValue(row.SectionKey, out var programCodes)) continue;
            if (programCodes.Count < 2) continue;

            ledger.Warn(row.Staged, SisImportWarningCode.SectionSpansPrograms,
                $"Section '{row.SectionName ?? row.SectionKey}' appears under {programCodes.Count} " +
                $"programmes in this file ({string.Join(", ", programCodes)}). This WARNS rather than " +
                "fails — and the asymmetry with COURSE_COLLEGE_COLLISION is deliberate, not an " +
                "inconsistency to tidy up. CourseOfferings is keyed (TermId, CourseId, SectionKey) " +
                "with the course IN the key (ADR-002 D-11), so two programmes sharing a section name " +
                "collide only when they are also the same course, which is usually genuinely the same " +
                "class; nothing merges and no column has to hold two values. Courses has no such " +
                "qualifier, which is why that one is unrecoverable and this one is not. It is still " +
                "reported because a section name shared across programmes is how institution-wide " +
                "keys start to collide.");
        }
    }

    private async Task<Dictionary<(Guid, string), Touched<CourseOffering>>> ResolveOfferingsAsync(
        Term term, IReadOnlyList<ParsedRow> parsed,
        IReadOnlyDictionary<string, Touched<Course>> courses,
        IReadOnlySet<int> collidedRows, CancellationToken ct)
    {
        var existing = await _db.CourseOfferings.IgnoreQueryFilters()
            .Where(o => o.TermId == term.Id)
            .ToDictionaryAsync(o => (o.CourseId, o.SectionKey), ct);

        var resolved = new Dictionary<(Guid, string), Touched<CourseOffering>>();

        var eligible = parsed
            .Where(r => !collidedRows.Contains(r.Staged.RowNumber) && courses.ContainsKey(r.CourseKey))
            .ToList();

        foreach (var group in GroupRows(eligible, r => (courses[r.CourseKey].Entity.Id, r.SectionKey)))
        {
            var first = group.First;
            var key = (courses[first.CourseKey].Entity.Id, first.SectionKey);

            if (existing.TryGetValue(key, out var offering))
            {
                // AssignIfPresent: the offering is already keyed by SectionKey, so a row reaching this
                // branch with a blank SECTION_NAME is one whose section normalized to the same key from
                // a cell that says nothing — it is not an instruction to forget the display name that
                // is on the row. See AssignIfPresent for the general rule.
                var changed = AssignIfPresent(
                    offering.SectionName, first.SectionName, v => offering.SectionName = v);
                if (changed) offering.UpdatedAt = DateTime.UtcNow;
                resolved[key] = new Touched<CourseOffering>(
                    offering, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            offering = new CourseOffering
            {
                TermId = term.Id,
                CourseId = key.Item1,
                SectionKey = first.SectionKey,
                SectionName = first.SectionName,
            };
            _db.CourseOfferings.Add(offering);
            existing[key] = offering;
            resolved[key] = new Touched<CourseOffering>(
                offering, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    // ================================================================================ pass 2: facts

    /// <summary>
    /// Writes the facts, and returns the rows it <em>refused</em> to write — the ones whose RFID serial
    /// is owned by another student. They are the fact pass's own equivalent of
    /// <see cref="Dimensions.CollidedRows"/>, and the caller excludes them from the derived cache too.
    /// </summary>
    private async Task<IReadOnlySet<int>> ResolveFactsAsync(
        Term term, IReadOnlyList<ParsedRow> parsed, Dimensions dimensions,
        RowLedger ledger, CancellationToken ct)
    {
        var schoolId = term.SchoolId;
        var live = parsed.Where(r => !dimensions.CollidedRows.Contains(r.Staged.RowNumber)).ToList();
        if (live.Count == 0) return new HashSet<int>();

        var regNos = live.Select(r => r.RegNo).Distinct(StringComparer.Ordinal).ToList();

        // OfType rather than a Where plus a null-forgiving cast: rows carrying no serial are simply not
        // members of card space, and this is the one place that has to be said. Today that is every row
        // of every file, so the query below is skipped outright rather than issued with an empty IN
        // list — an import that touches no cards should not ask the database about them.
        var cardUids = live.Select(r => r.CardUid).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();

        var students = await _db.Students.IgnoreQueryFilters()
            .Where(s => s.SchoolId == schoolId && regNos.Contains(s.StudentNumber))
            .ToDictionaryAsync(s => s.StudentNumber, StringComparer.Ordinal, ct);

        // Every card carrying one of these UIDs, active or not — see ResolveCards for why the inactive
        // ones have to be visible. Include(Student) so a mismatch can name the other student rather
        // than quoting a GUID at an operator: the owner's StudentNumber is very often absent from this
        // file (that is how the two rows drifted apart), so it cannot be recovered from `students`.
        var cards = cardUids.Count == 0
            ? new Dictionary<string, List<RfidCard>>(StringComparer.Ordinal)
            : (await _db.RfidCards.IgnoreQueryFilters()
                    .Include(c => c.Student)
                    .Where(c => c.SchoolId == schoolId && cardUids.Contains(c.CardUid))
                    .ToListAsync(ct))
                .GroupBy(c => c.CardUid, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // The one refusal that has to happen before anything is written. Detected from the rows and the
        // cards alone, deliberately ahead of ResolveStudents: failing a row after its student has been
        // created leaves the half-imported state that CourseCollegeCollision's own pre-filter exists to
        // avoid — a student row with no card and no enrollment, from a row reported as Failed.
        var mismatched = FailRowsWhoseCardBelongsToAnotherStudent(live, students, cards, ledger);
        if (mismatched.Count > 0)
            live = live.Where(r => !mismatched.Contains(r.Staged.RowNumber)).ToList();
        if (live.Count == 0) return mismatched;

        var termRecords = await _db.StudentTermRecords.IgnoreQueryFilters()
            .Where(r => r.TermId == term.Id && r.Student!.SchoolId == schoolId)
            .ToDictionaryAsync(r => r.StudentId, ct);

        var offeringIds = dimensions.Offerings.Values.Select(o => o.Entity.Id).ToList();

        // Keyed by the natural key, valued by the row's *own* Id — not a HashSet of keys.
        //
        // A set answers "does this enrollment exist?", which is all the upsert needs, and that is how
        // the fan-out came to record `offering.Id` under EntityType = Enrollment on the Unchanged
        // branch: there was no enrollment id in scope to record. On a re-import — the steady state —
        // that is every enrollment touch pointing into the wrong table, so
        // IX_SisImportRowEntities_Entity answers "which rows touched enrollment X?" with nothing.
        var enrollments = (await _db.Enrollments.IgnoreQueryFilters()
                .Where(e => offeringIds.Contains(e.CourseOfferingId))
                .Select(e => new { e.Id, e.StudentId, e.CourseOfferingId })
                .ToListAsync(ct))
            .ToDictionary(e => (e.StudentId, e.CourseOfferingId), e => e.Id);

        var assignments = (await _db.CourseOfferingInstructors.IgnoreQueryFilters()
                .Where(a => offeringIds.Contains(a.CourseOfferingId))
                .Select(a => new { a.Id, a.CourseOfferingId, a.InstructorId })
                .ToListAsync(ct))
            .ToDictionary(a => (a.CourseOfferingId, a.InstructorId), a => a.Id);

        var resolvedStudents = ResolveStudents(schoolId, live, students, ledger);
        var resolvedCards = ResolveCards(schoolId, live, resolvedStudents, cards, ledger);
        var resolvedRecords = ResolveTermRecords(term, live, dimensions, resolvedStudents, termRecords);

        foreach (var row in live)
        {
            var rowNumber = row.Staged.RowNumber;
            var student = resolvedStudents[row.RegNo];
            var offering = dimensions.Offerings[(dimensions.Courses[row.CourseKey].Entity.Id, row.SectionKey)];

            ledger.Touch(row.Staged, SisImportEntityType.College,
                dimensions.Colleges[row.CollegeKey].Entity.Id,
                dimensions.Colleges[row.CollegeKey].ActionFor(rowNumber));
            ledger.Touch(row.Staged, SisImportEntityType.Program,
                dimensions.Programs[row.ProgramKey].Entity.Id,
                dimensions.Programs[row.ProgramKey].ActionFor(rowNumber));
            ledger.Touch(row.Staged, SisImportEntityType.Course,
                dimensions.Courses[row.CourseKey].Entity.Id,
                dimensions.Courses[row.CourseKey].ActionFor(rowNumber));
            ledger.Touch(row.Staged, SisImportEntityType.CourseOffering,
                offering.Entity.Id, offering.ActionFor(rowNumber));
            ledger.Touch(row.Staged, SisImportEntityType.Student,
                student.Entity.Id, student.ActionFor(rowNumber));

            // Two absences, one trail entry withheld, and that is deliberate. A row can name no serial
            // at all (no RFID column in the file — today, every row), or name one whose only card is
            // deactivated (SisImportWarningCode.RfidCardRevoked). Either way no card touch is recorded,
            // and recording nothing is the honest answer: this row touched no card, which is a different
            // statement from touching one and changing nothing, and is exactly the distinction the
            // fan-out exists to keep. It is also the positive record that a student has no card, which
            // is why the no-serial case needs no warning of its own.
            if (row.CardUid is { } uid && resolvedCards.TryGetValue(uid, out var card))
                ledger.Touch(row.Staged, SisImportEntityType.RfidCard,
                    card.Entity.Id, card.ActionFor(rowNumber));

            ledger.Touch(row.Staged, SisImportEntityType.StudentTermRecord,
                resolvedRecords[row.RegNo].Entity.Id, resolvedRecords[row.RegNo].ActionFor(rowNumber));

            // §4.12's own StudentId link, kept because it is the one entity a human looks for.
            row.Staged.StudentId = student.Entity.Id;

            // The teacher belongs to the *offering*, not to the enrollment — which is the whole reason
            // the 27 "duplicate" (REGNO, COURSE_CODE) pairs are not duplicates. Both rows of a pair
            // describe one enrollment; only the one naming a real teacher adds an assignment, and the
            // placeholder row correctly adds nothing.
            if (!row.Teacher.IsPlaceholder && row.TeacherKey is not null
                && dimensions.Instructors.TryGetValue(row.TeacherKey, out var instructor))
            {
                ledger.Touch(row.Staged, SisImportEntityType.Instructor,
                    instructor.Entity.Id, instructor.ActionFor(rowNumber));

                var assignmentKey = (offering.Entity.Id, instructor.Entity.Id);
                if (!assignments.TryGetValue(assignmentKey, out var assignmentId))
                {
                    var assignment = new CourseOfferingInstructor
                    {
                        CourseOfferingId = offering.Entity.Id,
                        InstructorId = instructor.Entity.Id,
                    };
                    _db.CourseOfferingInstructors.Add(assignment);

                    // Remembered so the *next* row naming this pair reports Unchanged against this same
                    // id rather than re-inserting it. Entity assigns its Id in the initializer, so the
                    // id is real before SaveChanges.
                    assignments[assignmentKey] = assignment.Id;
                    ledger.Touch(row.Staged, SisImportEntityType.CourseOfferingInstructor,
                        assignment.Id, SisImportEntityAction.Inserted);
                }
                else
                {
                    ledger.Touch(row.Staged, SisImportEntityType.CourseOfferingInstructor,
                        assignmentId, SisImportEntityAction.Unchanged);
                }
            }

            var enrollmentKey = (student.Entity.Id, offering.Entity.Id);
            if (!enrollments.TryGetValue(enrollmentKey, out var enrollmentId))
            {
                var enrollment = new Enrollment
                {
                    StudentId = student.Entity.Id,
                    CourseOfferingId = offering.Entity.Id,
                };
                _db.Enrollments.Add(enrollment);
                enrollments[enrollmentKey] = enrollment.Id;
                ledger.Touch(row.Staged, SisImportEntityType.Enrollment,
                    enrollment.Id, SisImportEntityAction.Inserted);
            }
            else
            {
                ledger.Touch(row.Staged, SisImportEntityType.Enrollment,
                    enrollmentId, SisImportEntityAction.Unchanged);
            }
        }

        return mismatched;
    }

    /// <summary>
    /// Fails every row whose RFID serial already identifies someone else, and returns their row numbers
    /// so the caller can drop them before a single fact is written.
    ///
    /// <para>
    /// <b>Two shapes, one rule.</b> The serial's owner is the student on its active card if there is
    /// one, and otherwise the first row in the file to claim it. Any row naming a different student
    /// fails. That covers a serial whose active card in the database belongs to another student, and its
    /// twin with no card in the database at all: <em>two rows inside one file carrying the same serial
    /// for two different REGNOs</em> — a mis-keyed export, a cloned card, or a serial recycled onto a new
    /// card while the previous holder is still listed. Without this the second student would silently be
    /// handed the first one's card in the fan-out and receive none of their own.
    /// </para>
    ///
    /// <para>
    /// <b>Rows carrying no serial are not considered at all.</b> They are excluded from the grouping
    /// rather than gathered under a blank key — see <see cref="GroupByCardUid"/>. Twelve students with
    /// no card are not twelve students fighting over one, and grouping them together would fail eleven
    /// of them for sharing a card that does not exist.
    /// </para>
    ///
    /// <para>
    /// Students are compared by identity where one exists and by REGNO otherwise, which is exact:
    /// <c>StudentNumber</c> is unique per school, so two distinct REGNOs are two distinct students
    /// whether or not either has been created yet.
    /// </para>
    /// </summary>
    private static HashSet<int> FailRowsWhoseCardBelongsToAnotherStudent(
        IReadOnlyList<ParsedRow> live,
        IReadOnlyDictionary<string, Student> students,
        IReadOnlyDictionary<string, List<RfidCard>> cards,
        RowLedger ledger)
    {
        var mismatched = new HashSet<int>();

        foreach (var (uid, group) in GroupByCardUid(live))
        {
            var active = cards.TryGetValue(uid, out var forUid)
                ? forUid.FirstOrDefault(c => c.IsActive)
                : null;

            // Named for the message. The active card's own owner is the authority when there is one;
            // otherwise the file's first claimant is, and every later REGNO is the intruder.
            var ownerRegNo = active?.Student?.StudentNumber ?? group.First.RegNo;
            var ownerId = active?.StudentId;

            foreach (var row in group.All)
            {
                var claimant = students.TryGetValue(row.RegNo, out var existing) ? existing.Id : (Guid?)null;

                var isOwner = active is not null
                    ? claimant == ownerId
                    : string.Equals(row.RegNo, ownerRegNo, StringComparison.Ordinal);

                if (isOwner) continue;

                mismatched.Add(row.Staged.RowNumber);
                ledger.Fail(row.Staged, SisImportFailureCode.RfidCardStudentMismatch,
                    $"REGNO '{row.RegNo}' claims RFID card serial '{uid}', which already identifies " +
                    $"student '{ownerRegNo}'" +
                    (ownerId is null ? " (first claimant in this file)" : $" (StudentId {ownerId})") +
                    ". A serial names one physical card and " +
                    "UX_RfidCards_SchoolId_CardUid_Active is keyed on it, so one card cannot be active " +
                    "for two students: the file says it is and the database will not have it. Moving " +
                    "the card would cost the first student their active card and re-attribute every " +
                    "past and future tap on that physical card to the wrong person; issuing a second " +
                    "active card would violate that index and fail the whole batch. The card was left " +
                    "exactly as it was. The usual causes are a mis-keyed serial in the export, a cloned " +
                    "card, or a serial re-issued to a new student while the previous holder is still " +
                    "listed — fix the source, or deactivate the old card in the back office, and " +
                    "re-import.");
            }
        }

        return mismatched;
    }

    private Dictionary<string, Touched<Student>> ResolveStudents(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed,
        Dictionary<string, Student> existing, RowLedger ledger)
    {
        var resolved = new Dictionary<string, Touched<Student>>(StringComparer.Ordinal);

        foreach (var group in GroupRows(parsed, r => r.RegNo))
        {
            var first = group.First;
            WarnOnIdentityConflicts(group, ledger);

            if (existing.TryGetValue(first.RegNo, out var student))
            {
                // Two rules, and which column gets which is a statement about what the file is
                // authoritative for. See Assign and AssignIfPresent.
                //
                // Assign — the file is the source of truth and a differing value replaces the stored
                // one. FirstName and LastName qualify because the parse refuses a row without them, so
                // a value here is always a real name and never an absent cell.
                var changed = Assign(student.FirstName, first.FirstName, v => student.FirstName = v);
                changed |= Assign(student.LastName, first.LastName, v => student.LastName = v);

                // AssignIfPresent — optional contact and display columns, where the roster is one
                // source among several. A blank MIDDLE NAME / USA_EMAIL / EMAIL_ID cell says "this
                // file does not carry that", not "clear what you have". Overwriting with null here
                // would report Updated for erasing a value a person typed in the back office, and the
                // whole stated point of a re-import is that it *updates*.
                changed |= AssignIfPresent(student.MiddleName, first.MiddleName, v => student.MiddleName = v);
                changed |= AssignIfPresent(student.Email, first.Email, v => student.Email = v);
                changed |= AssignIfPresent(
                    student.AlternateEmail, first.AlternateEmail, v => student.AlternateEmail = v);

                // Deliberately NOT written here: Course, YearLevel and Section. They are the ADR-001
                // D-2 derived cache and the SaveChanges guard refuses them outside the refresh scope —
                // this importer is precisely the writer that guard was aimed at. They are set in
                // RefreshStudentCacheAsync, from the enrollments this run just wrote.

                // Stamped on every run, including one that changes nothing, and deliberately outside
                // the `changed` flag so it neither bumps UpdatedAt nor counts as a row outcome. Same
                // reasoning as AcademicCacheUpdatedAt in RefreshStudentCacheAsync: "the SIS confirmed
                // this student on this date" has to be distinguishable from "never synced", and if the
                // stamp counted as work then a no-op import would report 52 updates and the counters
                // would stop meaning anything. The accepted cost is that a no-op run still issues one
                // UPDATE per student here and one in the cache refresh — real, bounded by the roster,
                // and the price of the column meaning what it says.
                student.LastSyncedAt = DateTime.UtcNow;

                if (changed) student.UpdatedAt = DateTime.UtcNow;
                resolved[first.RegNo] = new Touched<Student>(
                    student, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            student = new Student
            {
                SchoolId = schoolId,
                // Verbatim, both shapes. 51 REGNOs are USA##### and one is the legacy 2021005781;
                // neither is reformatted into the other's shape, here or anywhere downstream.
                StudentNumber = first.RegNo,
                FirstName = first.FirstName,
                MiddleName = first.MiddleName,
                LastName = first.LastName,
                Email = first.Email,
                AlternateEmail = first.AlternateEmail,
                Status = "Active",
                LastSyncedAt = DateTime.UtcNow,
            };
            _db.Students.Add(student);
            existing[first.RegNo] = student;
            resolved[first.RegNo] = new Touched<Student>(
                student, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    /// <summary>
    /// REGNO functionally determines the student's name and both e-mail addresses across all 536 sample
    /// rows, with zero violations — so the first row for a REGNO can be taken as that student's
    /// identity. This is what says so out loud if it ever stops being true, rather than the later rows
    /// being dropped without a word.
    /// </summary>
    private static void WarnOnIdentityConflicts(RowGroup group, RowLedger ledger)
    {
        var first = group.First;

        foreach (var row in group.All)
        {
            if (row.Staged.RowNumber == first.Staged.RowNumber) continue;

            var differences = new List<string>();
            if (!string.Equals(row.FirstName, first.FirstName, StringComparison.Ordinal))
                differences.Add($"first name '{row.FirstName}' vs '{first.FirstName}'");
            if (!string.Equals(row.LastName, first.LastName, StringComparison.Ordinal))
                differences.Add($"last name '{row.LastName}' vs '{first.LastName}'");
            if (!string.Equals(row.Email, first.Email, StringComparison.Ordinal))
                differences.Add($"institutional e-mail '{row.Email}' vs '{first.Email}'");

            if (differences.Count == 0) continue;

            ledger.Warn(row.Staged, SisImportWarningCode.StudentIdentityConflict,
                $"REGNO {row.RegNo} is described differently here than on row " +
                $"{first.Staged.RowNumber}: {string.Join("; ", differences)}. The first row's values " +
                "were kept.");
        }
    }

    /// <summary>
    /// Resolves the card behind each RFID serial named in the batch, and <b>never moves or resurrects
    /// one</b>. Rows naming no serial reach here and resolve to nothing at all: no card row, no warning,
    /// no fan-out entry. That is the normal outcome today and is the shape of a student with no card.
    ///
    /// <para>
    /// <b>An active card whose student differs is refused, not reassigned.</b> That refusal happens
    /// upstream in <see cref="FailRowsWhoseCardBelongsToAnotherStudent"/>, before any student is
    /// created, so by the time a group reaches here its student is the card's owner. The check below is
    /// an assertion rather than a branch: reaching it means the pre-filter has a hole, and a loud stop
    /// beats reassigning a card and rewriting whose taps those were.
    /// </para>
    ///
    /// <para>
    /// <b>A card that exists but is deactivated is left deactivated</b>, and the row is warned rather
    /// than handed a new one — see <see cref="SisImportWarningCode.RfidCardRevoked"/>. This is why the
    /// prefetch loads inactive cards: filtered to <c>IsActive</c>, a card revoked without a replacement
    /// is invisible here and the next import creates a fresh active card carrying the same serial —
    /// which, since a serial names one physical card, makes the revoked card work again.
    /// </para>
    /// </summary>
    private Dictionary<string, Touched<RfidCard>> ResolveCards(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed,
        IReadOnlyDictionary<string, Touched<Student>> students,
        Dictionary<string, List<RfidCard>> existing, RowLedger ledger)
    {
        var resolved = new Dictionary<string, Touched<RfidCard>>(StringComparer.Ordinal);

        foreach (var (uid, group) in GroupByCardUid(parsed))
        {
            var first = group.First;
            var studentId = students[first.RegNo].Entity.Id;
            existing.TryGetValue(uid, out var forUid);

            if (forUid?.FirstOrDefault(c => c.IsActive) is { } card)
            {
                if (card.StudentId != studentId)
                    throw new SisImportException(
                        $"Card UID '{uid}' belongs to student {card.StudentId} but row " +
                        $"{first.Staged.RowNumber} resolved it to {studentId}. This is refused before " +
                        "the fact pass runs (SisImportFailureCode.RfidCardStudentMismatch), so " +
                        "reaching this point means that pre-filter and this resolver disagree about " +
                        "who owns a UID. Stopping the run: the alternative is silently moving an " +
                        "active card between students, which re-attributes every tap on it.");

                resolved[uid] = new Touched<RfidCard>(
                    card, SisImportEntityAction.Unchanged, first.Staged.RowNumber);
                continue;
            }

            if (forUid is { Count: > 0 })
            {
                // Deactivated, and it stays that way. Reported against every row naming the UID, and
                // resolved to nothing at all — the fact loop records no RfidCard touch for these rows,
                // which is the truthful trail: this row touched no card.
                var revoked = forUid.OrderByDescending(c => c.DeactivatedAt ?? c.IssuedAt).First();
                foreach (var row in group.All)
                    ledger.Warn(row.Staged, SisImportWarningCode.RfidCardRevoked,
                        $"RFID card serial '{uid}' exists but is deactivated" +
                        (revoked.DeactivatedAt is { } at ? $" (since {at:yyyy-MM-dd})" : "") +
                        (revoked.StudentId == studentId
                            ? ", for this same student."
                            : $", for student {revoked.StudentId}.") +
                        " No card was created and the revoked one was left revoked. The serial names " +
                        "one physical card, so issuing a replacement carrying it would make that same " +
                        "card work again and silently undo whoever revoked it — lost, stolen or " +
                        "suspended is not something the roster has a column for, so the roster does " +
                        "not get to reverse the decision. Re-issue it in the back office if that is " +
                        "what is wanted; the student imported normally and is enrolled either way.");

                continue;
            }

            var issued = new RfidCard
            {
                // ADR-001 D-3: denormalized from the student so the filtered unique index
                // (SchoolId, CardUid) WHERE IsActive = 1 can be a single index. Must equal the
                // student's own SchoolId, which it does by construction here.
                SchoolId = schoolId,
                StudentId = studentId,
                CardUid = uid,
                Label = SisRosterColumns.RfidCardSerial,
                IsActive = true,
                IssuedAt = DateTime.UtcNow,
            };
            _db.RfidCards.Add(issued);
            existing[uid] = [issued];
            resolved[uid] = new Touched<RfidCard>(
                issued, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    private Dictionary<string, Touched<StudentTermRecord>> ResolveTermRecords(
        Term term, IReadOnlyList<ParsedRow> parsed, Dimensions dimensions,
        IReadOnlyDictionary<string, Touched<Student>> students,
        Dictionary<Guid, StudentTermRecord> existing)
    {
        var resolved = new Dictionary<string, Touched<StudentTermRecord>>(StringComparer.Ordinal);
        var homeSections = HomeSections(parsed);

        foreach (var group in GroupRows(parsed, r => r.RegNo))
        {
            var first = group.First;
            var studentId = students[first.RegNo].Entity.Id;
            var (sectionKey, sectionName) = homeSections[first.RegNo];
            var collegeId = dimensions.Colleges[first.CollegeKey].Entity.Id;
            var programId = dimensions.Programs[first.ProgramKey].Entity.Id;

            if (existing.TryGetValue(studentId, out var record))
            {
                var changed = Assign(record.CollegeId, collegeId, v => record.CollegeId = v);
                changed |= Assign(record.ProgramId, programId, v => record.ProgramId = v);
                changed |= Assign(record.HomeSectionKey, sectionKey, v => record.HomeSectionKey = v);
                changed |= Assign(record.HomeSectionName, sectionName, v => record.HomeSectionName = v);
                // YearLevel is deliberately left alone *here*, and it is no longer left alone
                // everywhere: the export still has no year-level column, and reading a section name
                // row by row ('NSTP 2' looks exactly as much like year 2 as 'BSCRIM 2-A' does) would be
                // a guess stored as a fact. D-47 derives it instead, in StudentGroupProjection, from
                // the student's *whole* enrollment set measured against their own programme code —
                // which this pass cannot do, because it is looking at one student's rows before the
                // offerings they resolve to are all known. The projection runs at the end of every
                // import, so a re-import still recomputes the year; it just does not happen on this
                // line. See YearLevels.
                if (changed) record.UpdatedAt = DateTime.UtcNow;
                resolved[first.RegNo] = new Touched<StudentTermRecord>(
                    record, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            record = new StudentTermRecord
            {
                StudentId = studentId,
                TermId = term.Id,
                CollegeId = collegeId,
                ProgramId = programId,
                HomeSectionKey = sectionKey,
                HomeSectionName = sectionName,
            };
            _db.StudentTermRecords.Add(record);
            existing[studentId] = record;
            resolved[first.RegNo] = new Touched<StudentTermRecord>(
                record, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    /// <summary>
    /// Each student's home section: the one they appear under most often in this file, ties broken by
    /// the lowest key ordinally.
    ///
    /// <para>
    /// <b>This answers ADR-001's open follow-up "define the which-enrollment-shows rule".</b> 12 of the
    /// 52 sample students sit in more than one section, so any single-valued home section is lossy by
    /// construction (D-2 says so). What matters is that the loss is <em>deterministic</em>: the mode is
    /// the student's actual cohort in every real case — a BSCRIM student taking one ROTC class is in
    /// BSCRIM — and the ordinal tie-break means two runs over the same file never disagree, which is
    /// what the idempotency contract needs. Blank sections are excluded from the vote; a student whose
    /// every row is blank gets <c>null</c>, meaning "not recorded", which is a different statement from
    /// <c>CourseOfferings.SectionKey</c>'s <c>(unspecified)</c>.
    /// </para>
    /// </summary>
    private static Dictionary<string, (string? Key, string? Name)> HomeSections(
        IReadOnlyList<ParsedRow> parsed) =>
        parsed
            .GroupBy(r => r.RegNo, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var named = g.Where(r => r.SectionKey != AcademicKey.Unspecified).ToList();
                    if (named.Count == 0) return ((string?)null, (string?)null);

                    var winner = named
                        .GroupBy(r => r.SectionKey, StringComparer.Ordinal)
                        .OrderByDescending(x => x.Count())
                        .ThenBy(x => x.Key, StringComparer.Ordinal)
                        .First();

                    return ((string?)winner.Key, winner.First().SectionName);
                },
                StringComparer.Ordinal);

    // =========================================================================== pass 3: derived

    /// <summary>
    /// Refreshes the ADR-001 D-2 display cache on <c>Students</c> — the one place in this codebase that
    /// writes those three columns, and the reason
    /// <see cref="EamsDbContext.BeginAcademicCacheRefresh"/> exists.
    ///
    /// <para>
    /// <b><c>YearLevel</c> is not written.</b> The export has no year-level column, so there is nothing
    /// to refresh it from; writing null would erase whatever a previous source put there, and deriving
    /// a value from the section name would be a guess stored where a fact is expected.
    /// </para>
    ///
    /// <para>
    /// <b><c>AcademicCacheUpdatedAt</c> is stamped on every run, including one that changes nothing,</b>
    /// and it deliberately does not bump <c>UpdatedAt</c> or count as a row outcome. Same reasoning as
    /// the projection's <c>LastSyncedAt</c>: "refreshed, unchanged" has to be distinguishable from
    /// "never refreshed", and if the stamp counted as work then a no-op import would report 52 updates
    /// and the counters would stop meaning anything.
    /// </para>
    /// </summary>
    private async Task RefreshStudentCacheAsync(
        Term term, IReadOnlyList<ParsedRow> parsed, Dimensions dimensions,
        IReadOnlySet<int> mismatchedRows, CancellationToken ct)
    {
        // Both exclusions, for one reason: a row reported Failed must not have left a trace anywhere,
        // and a cached Section derived from a row that did not import is exactly such a trace.
        var live = parsed
            .Where(r => !dimensions.CollidedRows.Contains(r.Staged.RowNumber)
                        && !mismatchedRows.Contains(r.Staged.RowNumber))
            .ToList();
        if (live.Count == 0) return;

        var homeSections = HomeSections(live);
        var regNos = live.Select(r => r.RegNo).Distinct(StringComparer.Ordinal).ToList();
        var programByRegNo = live
            .GroupBy(r => r.RegNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ProgramCode, StringComparer.Ordinal);

        var students = await _db.Students.IgnoreQueryFilters()
            .Where(s => s.SchoolId == term.SchoolId && regNos.Contains(s.StudentNumber))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        using (_db.BeginAcademicCacheRefresh())
        {
            foreach (var student in students)
            {
                if (programByRegNo.TryGetValue(student.StudentNumber, out var programCode))
                    student.Course = programCode;

                if (homeSections.TryGetValue(student.StudentNumber, out var section))
                    student.Section = section.Name ?? section.Key;

                student.AcademicCacheUpdatedAt = now;
            }

            // Inside the scope: the guard runs in SaveChanges, so a save after the scope closes is
            // exactly the write it is built to refuse.
            await _db.SaveChangesAsync(ct);
        }
    }

    // ==================================================================================== counters

    private static void Tally(SisImportBatch batch, IReadOnlyList<SisImportRow> staged)
    {
        batch.TotalRows = staged.Count;
        batch.InsertedRows = staged.Count(r => r.Result == SisImportRowResult.Inserted);
        batch.UpdatedRows = staged.Count(r => r.Result == SisImportRowResult.Updated);
        batch.FailedRows = staged.Count(r => r.Result == SisImportRowResult.Failed);
        batch.SkippedRows = staged.Count(r => r.Result == SisImportRowResult.Skipped);
        batch.WarningRows = staged.Count(r => r.WarningCode is not null);

        batch.Status = batch.FailedRows > 0
            ? SisImportStatus.CompletedWithErrors
            : batch.WarningRows > 0
                ? SisImportStatus.CompletedWithWarnings
                : SisImportStatus.Completed;
    }

    // ======================================================================================= reads

    public async Task<SisImportBatchDto?> GetAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await _db.SisImportBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return null;

        var term = await _db.Terms.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == batch.TermId, ct);

        return ToDto(batch, term);
    }

    public async Task<IReadOnlyList<SisImportRowDto>> GetRowsAsync(
        Guid batchId, string? result, CancellationToken ct = default)
    {
        var query = _db.SisImportRows.AsNoTracking()
            .Include(r => r.Entities)
            .Where(r => r.BatchId == batchId);

        if (!string.IsNullOrWhiteSpace(result))
        {
            // An unrecognised filter matches nothing rather than everything. "?result=failure" silently
            // returning all 536 rows is how an operator concludes a clean import failed completely.
            if (!SisImportRowResult.TryNormalize(result, out var canonical)) return [];
            query = query.Where(r => r.Result == canonical);
        }

        var rows = await query.OrderBy(r => r.RowNumber).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    // ======================================================================================= plumbing

    private async Task<Term> LoadTermAsync(Guid termId, CancellationToken ct) =>
        await _db.Terms.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == termId, ct)
        ?? throw new SisImportException(
            $"No term {termId}. The term is required and operator-declared (ADR-001 D-5): it is not " +
            "in the file and must not be inferred from the filename or the upload date.");

    private async Task<SisImportPreviewDto> BuildPreviewAsync(
        SisImportBatch batch, Term term, ExcelRosterReader.RosterFile file, CancellationToken ct)
    {
        var sample = await _db.SisImportRows.AsNoTracking()
            .Include(r => r.Entities)
            .Where(r => r.BatchId == batch.Id)
            .OrderBy(r => r.RowNumber)
            .Take(PreviewSampleSize)
            .ToListAsync(ct);

        int Distinct(string column) => file.Rows
            .Select(r => AcademicKey.Normalize(r.Raw(column)))
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new SisImportPreviewDto(
            Batch: ToDto(batch, term),
            Columns: file.Columns.Where(c => c.Length > 0).ToList(),
            DistinctStudents: Distinct(SisRosterColumns.RegNo),
            DistinctColleges: Distinct(SisRosterColumns.CollegeName),
            DistinctPrograms: Distinct(SisRosterColumns.Program),
            DistinctCourses: Distinct(SisRosterColumns.CourseCode),
            DistinctSections: Distinct(SisRosterColumns.SectionName),
            // The placeholder is excluded: reporting 19 teachers when one of them is 'TO BE ANNOUNCE'
            // is the same lie the pipeline refuses to write into the Instructors table.
            DistinctInstructors: file.Rows
                .Select(r => AcademicKey.Normalize(r.Raw(SisRosterColumns.TeacherFullName)))
                .Where(v => v.Length > 0 && v != TeacherNames.PlaceholderKey)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            BlankSectionRows: file.Rows.Count(r =>
                AcademicKey.Normalize(r.Raw(SisRosterColumns.SectionName)).Length == 0),
            PlaceholderInstructorRows: file.Rows.Count(r =>
                TeacherNames.IsPlaceholder(r.Raw(SisRosterColumns.TeacherFullName))),
            SampleRows: sample.Select(ToDto).ToList());
    }

    /// <summary>
    /// Ensures the built-in ADR-001 D-4 profile exists for this school at
    /// <see cref="SisImportProfileTemplate.BuiltInVersion"/>, and returns it. Idempotent: the version is
    /// part of the natural key, so a second call finds the row rather than creating a version that
    /// differs from its predecessor in nothing.
    ///
    /// <para>
    /// <b>The lookup matches the version, not merely <c>IsActive</c>, and that is what makes a bump
    /// work.</b> Matching on <c>(SchoolId, NameKey, IsActive)</c> alone returns whatever version is
    /// active — so raising <see cref="SisImportProfileTemplate.BuiltInVersion"/> would keep handing back
    /// the stale one for ever, and every batch would execute the new mapping while claiming the old
    /// one's rules. On the 2026-07-30 RFID correction that is not merely a wrong label: the pipeline
    /// reads the RFID source column <em>out of</em> these rows, so a stale version 1 would take the card
    /// serial from the REGNO column and reinstate the defect on exactly the databases that already have
    /// data.
    /// </para>
    ///
    /// <para>
    /// <b>The previous version is deactivated, not deleted, and this is the schema's own mechanism.</b>
    /// <c>UX_SisImportProfiles_School_Name_Active</c> is <c>UNIQUE(SchoolId, NameKey) WHERE IsActive=1</c>
    /// — at most one active version per school — so two live versions is not a shape this database
    /// permits, and creating one would fail the upload with a raw constraint error. Superseding by
    /// clearing the flag leaves the version 1 row and every one of its column rows byte-for-byte intact,
    /// which is what <see cref="SisImportProfile.IsActive"/> documents and what every batch's
    /// <c>ImportProfileId</c> still resolves to. Nothing historical is rewritten: an old batch continues
    /// to be explained by the rules it ran under.
    /// </para>
    ///
    /// <para>
    /// <c>ExecuteUpdate</c> rather than tracked writes for the supersession, because both statements hit
    /// one table under one filtered unique index and EF orders same-table commands by its own graph
    /// rules, not by index safety — an INSERT of the new active version issued before the UPDATE that
    /// clears the old one is a unique violation. Issuing the UPDATE immediately removes the ordering
    /// question. A crash between the two leaves no active version, which the next upload repairs by
    /// running this method again.
    /// </para>
    /// </summary>
    private async Task<SisImportProfile> EnsureBuiltInProfileAsync(Guid schoolId, CancellationToken ct)
    {
        var nameKey = AcademicKey.NormalizeOrUnspecified(SisImportProfileTemplate.ProfileName);
        var version = SisImportProfileTemplate.BuiltInVersion;

        var existing = await _db.SisImportProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.SchoolId == schoolId && p.NameKey == nameKey && p.Version == version, ct);

        if (existing is not null)
        {
            // Present but superseded by something newer an operator authored: that is their decision and
            // this method does not fight it. Reactivating would silently overrule a live mapping.
            if (existing.IsActive) return existing;

            // `p.Version > version` is load-bearing, not a tidy-up. Without it this accepts ANY active
            // version — including an OLDER one — which is the exact defect the summary above says the
            // version-matched lookup exists to prevent, arriving through the fallback instead of through
            // the lookup. A school whose operator activated version 1 would have every NEW upload read
            // the card serial out of the REGNO column and reinstate the 2026-07-30 correction's defect,
            // silently and on live data. Deferring to a newer operator-authored version is deliberate;
            // deferring to an older one is the bug.
            var newer = await _db.SisImportProfiles.IgnoreQueryFilters()
                .Where(p => p.SchoolId == schoolId && p.NameKey == nameKey && p.IsActive
                         && p.Version > version)
                .OrderByDescending(p => p.Version)
                .FirstOrDefaultAsync(ct);

            // Falls back to the built-in row itself when the only active version is older — this method
            // promises the built-in profile, and an older active version is a state the paragraph above
            // does not contemplate and must not be allowed to satisfy.
            return newer ?? existing;
        }

        await _db.SisImportProfiles.IgnoreQueryFilters()
            .Where(p => p.SchoolId == schoolId && p.NameKey == nameKey && p.IsActive)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.IsActive, false)
                      .SetProperty(p => p.UpdatedAt, DateTime.UtcNow),
                ct);

        var profile = new SisImportProfile
        {
            SchoolId = schoolId,
            Name = SisImportProfileTemplate.ProfileName,
            NameKey = nameKey,
            Version = version,
            Source = SisImportSource.Excel,
            IsActive = true,
            Description = SisImportProfileTemplate.Description,
        };
        _db.SisImportProfiles.Add(profile);

        for (var i = 0; i < SisImportProfileTemplate.Entries.Count; i++)
        {
            var entry = SisImportProfileTemplate.Entries[i];
            _db.SisImportProfileColumns.Add(new SisImportProfileColumn
            {
                Profile = profile,
                SourceColumn = entry.SourceColumn,
                SourceColumnKey = SisRosterColumns.HeaderKey(entry.SourceColumn),
                TargetField = entry.TargetField,
                NormalizationRule = entry.NormalizationRule,
                IsRequired = entry.IsRequired,
                Ordinal = i,
            });
        }

        return profile;
    }

    private static SisImportBatchDto ToDto(SisImportBatch batch, Term term) => new(
        batch.Id, batch.TermId, term.Code, batch.Source, batch.FileName, batch.SourceSheetName,
        batch.FileHash, batch.Status, batch.TotalRows, batch.InsertedRows, batch.UpdatedRows,
        batch.FailedRows, batch.SkippedRows, batch.WarningRows, batch.StartedAt, batch.FinishedAt,
        // Passed straight through, nulls included. The claim stamps ProgressPhase and
        // ProgressUpdatedAt (D-54.4); the rest stay null until the progress writer lands. Null here is
        // the honest "nothing to report" rather than a gap to be filled in with zeros on the way out.
        batch.ProgressPhase, batch.ProgressPhaseNumber, batch.ProgressPhaseCount,
        batch.ProgressUnitsDone, batch.ProgressUnitsTotal, batch.ProgressUpdatedAt,
        batch.FailureReason);

    private static SisImportRowDto ToDto(SisImportRow row) => new(
        row.Id, row.RowNumber, row.Result, row.SkipReason, row.WarningCode, row.WarningMessage,
        row.ErrorMessage, row.StudentId, row.RawData,
        row.Entities
            .OrderBy(e => e.EntityType, StringComparer.Ordinal)
            .Select(e => new SisImportRowEntityDto(e.EntityType, e.EntityId, e.Action))
            .ToList());

    // ---------------------------------------------------------------------------- small helpers

    /// <summary>
    /// Assigns only when the value actually differs, and reports whether it did.
    ///
    /// <para>
    /// This is what makes <c>Updated</c> mean something. Assigning unconditionally would mark every
    /// entity modified on every run — EF's change tracker compares values, so it would not issue the
    /// UPDATE, but this pipeline's own counters would report one, and the headline "a second import
    /// changes nothing" assertion would be false while the database was in fact untouched.
    /// </para>
    /// </summary>
    private static bool Assign<T>(T current, T value, Action<T> set)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        set(value);
        return true;
    }

    /// <summary>
    /// <see cref="Assign"/> for a column the file is <em>not</em> authoritative for: a null source
    /// value means "this file does not say", never "clear what is stored".
    ///
    /// <para>
    /// <b>Why the two rules are not one.</b> <see cref="RosterText.Clean"/> returns <c>null</c> for a
    /// blank cell, so plain <see cref="Assign"/> turns an absent column into a <c>NULL</c> written over
    /// a populated one — and reports it as <c>Updated</c>, which is a replace-with-nothing driven by
    /// the absence of data. On the optional contact and display columns that collides head-on with the
    /// stated contract that a re-import <em>updates</em>: a student whose e-mail was typed in by hand,
    /// or a section display name a later export stopped carrying, would be erased by the next run of a
    /// file that simply never had that column filled in.
    /// </para>
    ///
    /// <para>
    /// It is deliberately <em>not</em> the default. Columns the roster owns outright — a corrected
    /// surname, a course's college, a programme's code — must still be able to change, and a required
    /// column's value is never null anyway because the parse fails the row first. The rule is therefore
    /// per column and stated at each call site, not inferred from nullability.
    /// </para>
    /// </summary>
    private static bool AssignIfPresent<T>(T? current, T? value, Action<T?> set) where T : class
    {
        if (value is null) return false;
        return Assign(current, value, set);
    }

    /// <summary>One distinct key's rows, and the first of them in worksheet order.</summary>
    private sealed record RowGroup(ParsedRow First, IReadOnlyList<ParsedRow> All);

    /// <summary>
    /// Groups rows by a key, preserving worksheet order so "first seen" is deterministic. The whole of
    /// pass 1 is built on this: 536 rows in, one group per college, programme, course, teacher and
    /// offering out, and the fact loop never has to decide whether a dimension exists.
    /// </summary>
    private static IEnumerable<RowGroup> GroupRows<TKey>(
        IReadOnlyList<ParsedRow> rows, Func<ParsedRow, TKey> key) where TKey : notnull =>
        rows.GroupBy(key)
            .Select(g => new RowGroup(g.First(), g.ToList()));

    /// <summary>
    /// <see cref="GroupRows{TKey}"/> for the RFID serial, which is the one grouping key that can be
    /// absent.
    ///
    /// <para>
    /// <b>Rows with no serial are excluded from the result entirely, not grouped under a blank key.</b>
    /// That distinction is the whole reason this exists rather than
    /// <c>GroupRows(rows, r =&gt; r.CardUid ?? "")</c>: a batch of a file with no RFID column is 536 rows
    /// with no serial, and gathering them into one group makes them look like 536 students fighting over
    /// a single card — <see cref="FailRowsWhoseCardBelongsToAnotherStudent"/> would fail 535 of them for
    /// sharing a card that does not exist, and <see cref="ResolveCards"/> would issue one card with an
    /// empty UID. Both are silent, both are catastrophic, and both are prevented here once instead of at
    /// each call site.
    /// </para>
    ///
    /// <para>
    /// Written as a loop rather than a LINQ chain because the pattern match is what unwraps the nullable
    /// — <c>Where(r =&gt; r.CardUid is not null)</c> leaves the compiler with <c>string?</c> and forces a
    /// null-forgiving <c>!</c> at the grouping key, which is exactly the assertion this method exists to
    /// avoid having to make. Worksheet order is preserved, so "first claimant" stays deterministic.
    /// </para>
    /// </summary>
    private static List<(string CardUid, RowGroup Group)> GroupByCardUid(IReadOnlyList<ParsedRow> rows)
    {
        var byUid = new Dictionary<string, List<ParsedRow>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var row in rows)
        {
            if (row.CardUid is not { } uid) continue;

            if (!byUid.TryGetValue(uid, out var members))
            {
                members = [];
                byUid[uid] = members;
                order.Add(uid);
            }

            members.Add(row);
        }

        return order
            .Select(uid => (uid, new RowGroup(byUid[uid][0], byUid[uid])))
            .ToList();
    }
}
