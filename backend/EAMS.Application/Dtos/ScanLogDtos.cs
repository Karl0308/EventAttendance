namespace EAMS.Application.Dtos;

/// <summary>
/// One scan that reached the server and produced no attendance row, as <c>GET /events/{id}/scans</c>
/// reports it: a card that resolved to nobody, or a personnel card that was refused (ADR-015 D-100).
///
/// <para>
/// <b>Every row here is a scan that is in no other record.</b> A scan that is counted becomes an
/// <c>AttendanceRecord</c> and is reported by <c>GET /events/{id}/roster</c> and the attendance list;
/// it is deliberately absent from this report rather than duplicated into it. This is the remainder —
/// the population that until now existed only as an HTTP response nobody kept.
/// </para>
/// </summary>
/// <param name="CardUid">
/// The normalized serial the device sent. Unless <paramref name="PersonnelId"/> is set, it matched no
/// active card in this school at the moment the tap was decided — which is not the same as "no such card
/// exists now": a card issued afterwards does not retroactively resolve rows already written.
/// </param>
/// <param name="ScannedAt">
/// The device's <c>tappedAt</c> for this scan, not the moment the server saw it. For an offline queue
/// those differ by however long the device was away, and the first is when the person was standing
/// there.
/// </param>
/// <param name="RecordedAt">When the server wrote this row. Compare with ScannedAt to see flush lag.</param>
/// <param name="DeviceTapId">
/// The device's idempotency key. Present for anything queued offline; a device that omits it cannot
/// have its retries de-duplicated, which is why the batch path refuses rows without one.
/// </param>
/// <param name="DeviceId">The device that captured it, where known.</param>
/// <param name="ServerOutcome">
/// The code the device received: <c>CardNotFound</c>, <c>PersonnelInactive</c> or
/// <c>PersonnelNotInAudience</c>.
/// </param>
/// <param name="LocalOutcome">
/// What the <em>device</em> claimed, verbatim and possibly null. <b>Never authoritative.</b> Its
/// value is in disagreeing with <paramref name="ServerOutcome"/>: a device reporting that it found
/// this card while the server could not resolve it is evidence of a stale manifest, a sync that did
/// not run, or a cloned card.
/// </param>
/// <param name="PersonnelId">
/// ADR-015 D-100: the personnel member the card resolved to, when it did and the tap was refused anyway
/// (<c>PersonnelInactive</c>, <c>PersonnelNotInAudience</c>, or a client that cannot receive personnel).
/// Null for a card that resolved to nobody. An id only — this report never carries a name.
/// </param>
/// <param name="Reason">
/// Why a resolved card produced no row when <paramref name="ServerOutcome"/> alone does not say:
/// <c>PersonnelClientNotCapable</c> when the device was told <c>CardNotFound</c> because it did not send
/// <c>X-EAMS-Capabilities: personnel</c>. Null otherwise.
/// </param>
public record EventScanDto(
    string CardUid,
    DateTime ScannedAt,
    DateTime RecordedAt,
    string? DeviceTapId,
    Guid? DeviceId,
    string ServerOutcome,
    string? LocalOutcome,
    Guid? PersonnelId,
    string? Reason);

/// <summary>
/// The unresolved scans for one event, with the counts a reader needs before deciding whether the
/// list is worth reading.
/// </summary>
/// <param name="EventId">The event these scans were filed under.</param>
/// <param name="TotalScans">Every unresolved scan, including repeat presentations of one card.</param>
/// <param name="DistinctCards">
/// How many different cards those scans represent.
///
/// <para>
/// Both numbers are reported because they answer different questions and the gap between them is
/// itself informative. Forty scans over three cards is somebody presenting a card that is not working
/// and trying again; forty scans over forty cards is a roster that has not been given its RFID
/// column.
/// </para>
/// </param>
/// <param name="Scans">Most recent first.</param>
public record EventScanLogDto(
    Guid EventId,
    int TotalScans,
    int DistinctCards,
    IReadOnlyList<EventScanDto> Scans);
