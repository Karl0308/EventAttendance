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

public record TapResponse(TapOutcome Outcome, TapResult Result);

public record ManualResponse(ManualOutcome Outcome, TapResult Result);

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
