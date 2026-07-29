using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// Why the outcome is an enum and not an exception or a bare bool: the tap flow has several
/// non-exceptional failures that map to different HTTP statuses (404 vs 400). The service owns the
/// decision; the controller owns only the translation. Keeps HTTP concerns out of the service.
/// </summary>
public enum TapOutcome
{
    Recorded,
    DuplicateIgnored,
    CheckedOut,
    AlreadyRecorded,
    EventNotFound,
    EventNotOpen,
    CardNotFound,

    /// <summary>
    /// The request named a <c>DeviceId</c> that is not in <c>Devices</c>. A rejection rather than the
    /// foreign-key violation this used to be: §15 makes device registration a Phase 3 concern, so a
    /// wiped or re-provisioned handset holds a stale id, and §8.2's offline queue retries on 5xx.
    /// An unhandled FK violation therefore made every queued tap retry forever and the queue never
    /// drained. A 4xx tells the client to stop and re-register instead.
    ///
    /// <para>
    /// <b>It is also what a cross-tenant device gets</b>, since Phase 4b (D-27). A device whose
    /// <c>SchoolId</c> does not match the event's is "not registered" <em>to this event</em>, and
    /// saying so with the existing outcome rather than a new 403 is deliberate: a distinct status
    /// would confirm to the caller that the device exists somewhere else, which is a cross-tenant
    /// existence disclosure bought for no client benefit. The published contract already tells the
    /// mobile developer this token means "unknown device, or device belongs to another school".
    /// </para>
    /// </summary>
    DeviceNotRegistered,

    /// <summary>
    /// The body's <c>deviceId</c> disagrees with the authenticated device (Phase 4a design, D-26).
    ///
    /// <para>
    /// <b>A rejection rather than a silent override, and that is the whole decision.</b> The principal
    /// wins on the write either way — a body field cannot choose which device recorded a tap — so
    /// ignoring the mismatch would be *safe* and would still be wrong: the client believes it recorded
    /// a tap against device A, the row says device B, and its local queue and our table disagree about
    /// a key it will later retry on. Same reasoning the students surface applies to a derived field
    /// echoed back on a <c>PUT</c>: a value that is quietly discarded is worse than one that is
    /// refused, because only the refusal is discoverable.
    /// </para>
    /// </summary>
    DeviceMismatch,

    /// <summary>
    /// <c>tappedAt</c> is more than <see cref="EAMS.Domain.TapTimeWindow.FutureToleranceMinutes"/>
    /// minutes ahead of the server's clock (Phase 4c, D-36).
    ///
    /// <para>
    /// A device cannot observe a card that has not been presented yet, so this is always a drifted
    /// clock — and it is the drift direction that benefits the student, since a check-in dated forward
    /// past the grace boundary is exactly what a Late arrival would want. The response carries
    /// <c>serverTime</c>, so the client has what it needs to correct itself and resend.
    /// </para>
    ///
    /// <para>
    /// There is deliberately no matching outcome for the distant <em>past</em>: an old queued tap is
    /// the point of §8.2's offline sync. What bounds the past is
    /// <see cref="TappedAtOutsideEventWindow"/>, which bounds it by the event rather than by age.
    /// </para>
    /// </summary>
    TappedAtOutOfRange,

    /// <summary>
    /// <c>tappedAt</c> falls outside the event's window — <c>[StartAt − 60min, EndAt + 60min]</c> by
    /// default, per school via §4.13 (Phase 4c, D-36).
    ///
    /// <para>
    /// <b>Rejected rather than clamped, and that was the decision.</b> Clamping a skewed timestamp into
    /// the window would invent an observation nobody made, on the one field that decides Present vs
    /// Late — the same fabrication <c>AttendanceService.RecordsAnArrival</c> refuses when it declines to
    /// stamp a <c>CheckInAt</c> on an <c>Absent</c> row.
    /// </para>
    ///
    /// <para>
    /// It is checked on <em>submission-time-stamped</em> taps too (<c>tappedAt: null</c> means "now, on
    /// the server"). Exempting them would make omitting the field a way to bypass the window, which is
    /// the one bypass an attacker holding a guessed card UID would reach for first.
    /// </para>
    /// </summary>
    TappedAtOutsideEventWindow,

    /// <summary>
    /// A row of <c>POST /attendance/tap/batch</c> carried no <c>deviceTapId</c> (Phase 4d, D-33).
    /// Per-row, never batch-level: one unkeyed row does not spoil the other 199.
    ///
    /// <para>
    /// <b>Required on batch, optional on the single tap, and the asymmetry is the decision.</b> A tap
    /// arriving one at a time is being made <em>now</em> — if the response is lost the client can
    /// resend and, at worst, <c>AlreadyRecorded</c> absorbs it, because the
    /// <c>(EventId, StudentId, OccurrenceId)</c> index still holds. A tap arriving in a batch has by
    /// definition been sitting in a queue, and the queue's entire retry contract is
    /// "resend the batch, every row is idempotent". A row with no idempotency key cannot honour that:
    /// its only guard is the event/student index, which silently converts a retry into
    /// <c>AlreadyRecorded</c> and so loses the second genuine tap of a <c>TimeInOut</c> pair. Refusing
    /// it names the problem on the client's side where it can be fixed; accepting it would produce a
    /// row that looks recorded and is not replayable.
    /// </para>
    ///
    /// <para>
    /// Published as a 400 in the frozen token table, which is what makes it a "stop, bug on your side"
    /// rather than something the queue retries.
    /// </para>
    /// </summary>
    DeviceTapIdRequired,

    /// <summary>
    /// <c>POST /attendance/tap/batch</c> was sent more than <see cref="EAMS.Domain.TapBatchLimits.MaxRows"/>
    /// rows (Phase 4d, D-31). <b>The only outcome in this enum that is batch-level rather than
    /// per-row</b> — nothing was processed, so there are no row results to carry it.
    ///
    /// <para>
    /// The refusal echoes the limit in its problem body, so a client discovers the number it has to
    /// chunk to rather than being told a number it already sent was wrong. That is the one case in the
    /// frozen table whose guidance is "chunk, retry" instead of "stop retrying".
    /// </para>
    /// </summary>
    BatchTooLarge,
}

public enum ManualOutcome
{
    Saved,
    EventNotFound,
    StudentNotFound,

    /// <summary>
    /// The status was outside §4.9's <see cref="EAMS.Domain.AttendanceStatus"/> set. Rejected rather
    /// than stored: a row whose status is in no bucket is counted by no summary, so the event totals
    /// stop reconciling — and an over-length one used to reach SQL Server and come back as a
    /// truncation 500.
    /// </summary>
    InvalidStatus,

    /// <summary>
    /// <c>notes</c> exceeded <see cref="EAMS.Domain.AttendanceNotes.MaxLength"/>. The other half of
    /// the same defect as <see cref="InvalidStatus"/>: both fields were written to a bounded
    /// <c>nvarchar</c> column unchecked, so an over-length value reached SQL Server and returned as
    /// error 2628 (truncation) — a 500 on input the caller got wrong, which is a 400.
    /// </summary>
    InvalidNotes,
}

/// <summary>
/// The tap workflow's decision, plus the body a client sees.
///
/// <para>
/// <b>Build one through <see cref="For"/>, never through the constructor.</b> <c>TapResult.Code</c> is
/// a projection of <see cref="Outcome"/> and the two must never disagree — a response saying
/// <c>CheckedOut</c> in one field and <c>AlreadyRecorded</c> in the other is worse than the prose-only
/// body D-37 replaced, because a client would trust it. The factory is the only place the projection
/// happens; <c>TapOutcomeContractTests</c> asserts it holds for every declared member, and
/// <c>TapFlowTests</c> re-asserts it on every response the real service returns.
/// </para>
/// </summary>
public record TapResponse(TapOutcome Outcome, TapResult Result)
{
    public static TapResponse For(
        TapOutcome outcome, bool success, string message, AttendanceDto? record, DateTime serverTime) =>
        new(outcome, new TapResult(success, message, record, outcome.ToString(), serverTime));
}

/// <summary><inheritdoc cref="TapResponse" path="/summary"/></summary>
public record ManualResponse(ManualOutcome Outcome, TapResult Result)
{
    public static ManualResponse For(
        ManualOutcome outcome, bool success, string message, AttendanceDto? record, DateTime serverTime) =>
        new(outcome, new TapResult(success, message, record, outcome.ToString(), serverTime));
}

/// <summary>
/// One row of a batch, paired with where it came from in the request (Phase 4d, D-31).
///
/// <para>
/// It carries a whole <see cref="TapResponse"/> rather than a token, so the row a batch produces and
/// the row the single endpoint produces are literally the same object — the controller applies the
/// same outcome→status map to both, and there is no second projection to drift.
/// </para>
/// </summary>
public record TapBatchRow(int Index, string? DeviceTapId, TapResponse Response);

/// <summary>
/// What one call to <c>POST /attendance/tap/batch</c> did.
/// </summary>
/// <param name="Refusal">
/// Non-null only when the batch was refused as a whole — today that is
/// <see cref="TapOutcome.BatchTooLarge"/> and nothing else. <see cref="Rows"/> is then empty and
/// <b>nothing was written</b>. Null on every well-formed batch, including an empty one.
/// </param>
/// <param name="Rows">
/// One entry per submitted tap, in the request's array order. The <em>processing</em> order was
/// ascending <c>tappedAt</c>; that is not observable here and does not need to be.
/// </param>
public record TapBatchResponse(
    TapResponse? Refusal, IReadOnlyList<TapBatchRow> Rows, DateTime ServerTime);

/// <summary>Technical Plan §6.4 — RFID capture and organizer override.</summary>
public interface IAttendanceService
{
    Task<IReadOnlyList<AttendanceDto>> ListAsync(
        Guid? eventId, Guid? studentId, string? status, CancellationToken ct = default);

    /// <summary>
    /// The capture workflow, kept whole: resolve UID → validate the event window → idempotency
    /// check on <c>DeviceTapId</c> → upsert → compute Present/Late. Deliberately not decomposed
    /// behind a repository — the steps are one transaction's worth of decision-making.
    /// </summary>
    Task<TapResponse> TapAsync(TapRequest request, CancellationToken ct = default);

    /// <summary>
    /// §8.2's offline queue flush: many taps, one request, each one decided by
    /// <see cref="TapAsync"/> itself.
    ///
    /// <para>
    /// <b>The implementation may not contain a second copy of the tap rules, and that is a hard
    /// constraint rather than a preference.</b> This codebase has been bitten twice by a duplicated
    /// write path — <c>ManualAsync</c> shipped as an undefended copy of the read-then-write, and
    /// <c>FindByEventStudentAsync</c>/<c>SaveNewRecordAsync</c> exist so it could not happen a third
    /// time. A batch endpoint that re-derived "is this a check-out?" or "is this a duplicate?" would be
    /// the third, on the busiest path in the system, and the divergence would show up as two clients
    /// getting different answers for the same tap.
    /// </para>
    /// </summary>
    Task<TapBatchResponse> TapBatchAsync(TapBatchRequest request, CancellationToken ct = default);

    Task<ManualResponse> ManualAsync(
        Guid eventId, Guid studentId, string status, string? notes, CancellationToken ct = default);
}
