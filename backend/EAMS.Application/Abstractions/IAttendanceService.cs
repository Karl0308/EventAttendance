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

    Task<ManualResponse> ManualAsync(
        Guid eventId, Guid studentId, string status, string? notes, CancellationToken ct = default);
}
