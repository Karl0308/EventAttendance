using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>The outcome of recording a manual ID entry.</summary>
public enum ManualIdEntryOutcome
{
    /// <summary>The entry was saved (resolved or not).</summary>
    Saved,

    /// <summary>The card UID or ID number was blank, or the person type was not Student/Employee. 400.</summary>
    ValidationFailed,

    /// <summary>No school could be resolved to file the entry under. 409.</summary>
    NoSchoolResolved,
}

/// <summary>The result of recording a manual ID entry. <see cref="Entry"/> is null unless it saved.</summary>
public record ManualIdEntryResponse(ManualIdEntryOutcome Outcome, string Message, ManualIdEntryDto? Entry);

/// <summary>
/// <b>Manual ID entry for unrecognized RFID scans (UnrecognizedRFIDScans.docx).</b> When a reader scans a
/// card the system cannot place, the operator types the corresponding Student or Employee ID; this stores
/// that as a record — identifiable as an unrecognized-scan entry — so an administrator can review and
/// reconcile it later. The recognized-RFID flow is untouched: this is only reached for a scan already
/// found unrecognized.
///
/// <para>
/// Records are kept as <c>AuditLogs</c> rows (<see cref="ScanLog.ManualIdEntryAction"/>), the same store
/// the unresolved-scan log uses, and scoped to the tenant on read.
/// </para>
/// </summary>
public interface IUnrecognizedScanService
{
    /// <summary>
    /// <c>POST /scans/manual-id</c> — record the ID an operator entered for an unrecognized scan. The ID
    /// is resolved against the roster (Student number or Personnel number) best-effort; an unresolved
    /// entry is still saved.
    /// </summary>
    Task<ManualIdEntryResponse> RecordAsync(ManualIdEntryRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /scans/manual-id</c> — the recorded manual ID entries for this school, newest first, for
    /// review and reconciliation.
    /// </summary>
    Task<PagedResult<ManualIdEntryDto>> ListAsync(PageRequest page, CancellationToken ct = default);
}
