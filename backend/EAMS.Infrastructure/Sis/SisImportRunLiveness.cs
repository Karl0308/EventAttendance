using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Sis;

/// <summary>
/// ADR-004 D-54.5: when a <c>Running</c> batch counts as alive, and the startup sweep that fails the
/// ones that are not.
///
/// <para>
/// <b>One threshold, two readers.</b> The sweep uses it to decide which <c>Running</c> batches died with
/// a previous process. The claim (D-54.4) uses it to decide whether another batch in the same school
/// still holds that school's one running slot. They have to agree. If the claim used a shorter window
/// than the sweep, a second import could start beside a run the sweep would still call alive. If it
/// used a longer one, a dead batch would keep blocking its school after the sweep had already given up
/// on it.
/// </para>
/// </summary>
/// <summary>
/// A run found that it no longer owns its batch. Its stamp went stale, and the batch was reclaimed by
/// a newer claim with a different <c>StartedAt</c>. The old run stops at its next stamp, and writes
/// nothing more to the batch row. A <see cref="SisImportException"/>, so a caller still waiting on
/// the old request gets a 409 explaining it, not a 500.
/// </summary>
internal sealed class SisImportRunSupersededException(Guid batchId, DateTime claimedAt, string phase)
    : SisImportException(
        $"This run of batch {batchId} (started {claimedAt:yyyy-MM-dd HH:mm:ss} UTC) stopped at {phase} " +
        "because it went quiet long enough to be presumed dead, and the batch was run again. The newer " +
        "run owns the batch now. Check the batch for its result.");

internal static class SisImportRunLiveness
{
    /// <summary>
    /// How long a <c>Running</c> batch may go without a progress stamp before it is presumed dead.
    ///
    /// <para>
    /// <b>Why 30 minutes.</b> The run stamps <c>ProgressUpdatedAt</c> at the claim and at every phase
    /// change, so the longest live gap is the longest single phase. The whole 100,000-row roster ran in
    /// 447 s end to end at compatibility level 110 (commit f230d74), and one command is capped at
    /// <c>RunCommandTimeoutSeconds</c> (600 s). Thirty minutes is about four times the whole measured
    /// run and three times the command ceiling, so a live run on slower VM hardware still stays well
    /// inside it. It also matches the web admin's <c>IMPORT_STALE_AFTER_MS</c>, so the browser and the
    /// server agree about when a run counts as abandoned.
    /// </para>
    ///
    /// <para>
    /// <b>Why erring long is the right side.</b> Too short, and a slow live run is declared dead: the
    /// claim then lets a second import into the same school beside it, which is the data race this
    /// guard exists to stop. Too long only costs time: a batch killed with a recent stamp blocks its
    /// school until the threshold passes, and the refusal tells the operator so.
    /// </para>
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// What an interrupted batch says. It is a sentence an operator can act on, kept under the 400
    /// characters <c>FailureReason</c> holds, because the remedy for an interrupted run is to run it again.
    /// </summary>
    public const string InterruptedReason =
        "This import was interrupted before it finished: the server stopped while it was running, so " +
        "it never reached a result. Rows it had already written are kept, and running it again is " +
        "safe because those rows will report Skipped. Run it again.";

    /// <summary>
    /// The oldest progress stamp that still counts as alive at <paramref name="now"/>. A stamp at or
    /// after this is live. A stamp before it, or no stamp at all, is not.
    /// </summary>
    public static DateTime LiveSince(DateTime now) => now - StaleAfter;

    /// <summary>Whether a progress stamp counts as alive at <paramref name="now"/>. No stamp is not alive.</summary>
    public static bool IsLive(DateTime? progressUpdatedAt, DateTime now) =>
        progressUpdatedAt is { } at && at >= LiveSince(now);

    /// <summary>
    /// The outcome statuses <c>Tally</c> saves. A batch can carry one of these while its run is still
    /// working, because the outcome is saved before the last two phases run.
    /// </summary>
    public static readonly string[] OutcomeStatuses =
    [
        SisImportStatus.Completed, SisImportStatus.CompletedWithWarnings, SisImportStatus.CompletedWithErrors,
    ];

    /// <summary>
    /// The phases that run <em>after</em> the outcome is saved: the student cache and the group
    /// projection. A batch with an outcome status and one of these phases, stamped recently, is a run
    /// still writing to its school, so it holds the school's running slot like a <c>Running</c> batch.
    ///
    /// <para>
    /// Named, rather than written as "phase is not <c>Done</c>". A batch that completed before the
    /// heartbeat existed keeps the claim's first phase forever and never reaches <c>Done</c>. Read as
    /// "not Done", it would block its school for up to <see cref="StaleAfter"/> after this deploy.
    /// </para>
    /// </summary>
    public static readonly string[] PostOutcomePhases =
    [
        SisImportPhase.RefreshingStudentCache, SisImportPhase.SyncingStudentGroups,
    ];

    /// <summary>
    /// Fails every <c>Running</c> batch whose progress stamp is stale or <c>NULL</c>, in every school,
    /// and returns how many it failed. It is idempotent: a second call finds nothing to fail.
    ///
    /// <para>
    /// <b>Called once, from host start-up</b> (<c>InitializeEamsDatabaseAsync</c>), after migrations.
    /// No run of <em>this</em> process can be live at that point. The threshold is still applied,
    /// because a run in <em>another</em> process may be. During an IIS overlapped recycle the old
    /// worker keeps its in-flight requests for up to its shutdown time limit, and two hosts can share
    /// one database. A fresh <c>Running</c> batch is left alone, and the claim's guard keeps treating
    /// it as alive until its stamp goes stale.
    /// </para>
    ///
    /// <para>
    /// <b><c>NULL</c> counts as stale.</b> Every claim since D-54.4 stamps <c>ProgressUpdatedAt</c>, so
    /// a <c>Running</c> batch without a stamp was claimed before progress columns existed. Those are
    /// exactly the zombies this ADR began with.
    /// </para>
    ///
    /// <para>
    /// <b>What it leaves.</b> <c>ProgressPhase</c> and <c>ProgressUpdatedAt</c> stay as they were. They
    /// record where the run died and when it was last seen alive. <c>FinishedAt</c> is stamped so the
    /// batch reads as a finished attempt. The status update repeats the stale predicate, so a batch
    /// that sends a fresh stamp between the read and the write is not failed.
    /// </para>
    /// </summary>
    public static async Task<int> SweepInterruptedRunsAsync(
        EamsDbContext db, ILogger logger, DateTime now, CancellationToken ct)
    {
        var liveSince = LiveSince(now);

        var candidates = await db.SisImportBatches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.Status == SisImportStatus.Running
                        && (b.ProgressUpdatedAt == null || b.ProgressUpdatedAt < liveSince))
            .Select(b => new { b.Id, b.SchoolId, b.ProgressPhase, b.ProgressUpdatedAt })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            logger.LogInformation(
                "Roster import sweep: no interrupted runs (no Running batch without progress in the last " +
                "{StaleAfterMinutes} minutes).",
                StaleAfter.TotalMinutes);
            return 0;
        }

        // Few rows by construction (one running slot per school), so the literal IN list that
        // compatibility level 110 produces for Contains stays small.
        var ids = candidates.Select(c => c.Id).ToList();

        var failed = await db.SisImportBatches.IgnoreQueryFilters()
            .Where(b => ids.Contains(b.Id)
                        && b.Status == SisImportStatus.Running
                        && (b.ProgressUpdatedAt == null || b.ProgressUpdatedAt < liveSince))
            .ExecuteUpdateAsync(
                s => s.SetProperty(b => b.Status, SisImportStatus.Failed)
                      .SetProperty(b => b.FinishedAt, (DateTime?)now)
                      .SetProperty(b => b.FailureReason, (string?)InterruptedReason),
                ct);

        foreach (var c in candidates)
            logger.LogWarning(
                "Roster import sweep: batch {BatchId} (school {SchoolId}) was Running with its last " +
                "progress {LastProgress} in phase {Phase}. Its run did not survive the previous process, " +
                "so it is now Failed and can be run again.",
                c.Id, c.SchoolId, c.ProgressUpdatedAt?.ToString("O") ?? "never recorded",
                c.ProgressPhase ?? "(none)");

        logger.LogWarning(
            "Roster import sweep: marked {Failed} of {Candidates} interrupted batch(es) Failed.",
            failed, candidates.Count);

        return failed;
    }
}
