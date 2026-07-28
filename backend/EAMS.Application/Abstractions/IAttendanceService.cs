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
    /// </summary>
    DeviceNotRegistered,
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
