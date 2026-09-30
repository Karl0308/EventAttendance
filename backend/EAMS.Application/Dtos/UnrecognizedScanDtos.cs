namespace EAMS.Application.Dtos;

/// <summary>
/// The body of <c>POST /scans/manual-id</c> — the ID an operator typed for an unrecognized RFID scan
/// (UnrecognizedRFIDScans.docx).
/// </summary>
/// <param name="CardUid">The scanned RFID UID. Normalized server-side before it is stored.</param>
/// <param name="IdNumber">
/// The Student or Employee ID the operator entered. Required — the record cannot be saved without it.
/// </param>
/// <param name="PersonType"><c>Student</c> or <c>Employee</c>, so the right roster is checked and the row is identifiable.</param>
/// <param name="EventId">The event the scan happened at, if known.</param>
/// <param name="Note">An optional free-text note.</param>
public record ManualIdEntryRequest(
    string CardUid, string IdNumber, string PersonType, Guid? EventId, string? Note);

/// <summary>
/// One recorded manual ID entry, as the review list returns it.
/// </summary>
/// <param name="IsResolved">
/// Whether <see cref="IdNumber"/> matched a real person in the named roster at record time. An unresolved
/// entry is still kept — the point is to capture the operator's input for later reconciliation — but the
/// review can highlight the ones that need attention.
/// </param>
/// <param name="ResolvedName">The matched person's name, when resolved.</param>
public record ManualIdEntryDto(
    Guid Id,
    string CardUid,
    string IdNumber,
    string PersonType,
    bool IsResolved,
    string? ResolvedName,
    Guid? EventId,
    string? Note,
    DateTime RecordedAt);
