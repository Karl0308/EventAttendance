namespace EAMS.Application.Dtos;

/// <summary>
/// One scan that reached the server and resolved to nobody, as <c>GET /events/{id}/scans</c> reports
/// it.
///
/// <para>
/// <b>Every row here is a scan that is in no other record.</b> A scan whose card resolves becomes an
/// <c>AttendanceRecord</c> and is reported by <c>GET /events/{id}/roster</c> and the attendance list;
/// it is deliberately absent from this report rather than duplicated into it. This is the remainder —
/// the population that until now existed only as an HTTP response nobody kept.
/// </para>
/// </summary>
/// <param name="CardUid">
/// The normalized serial the device sent. It matched no active card in this school at the moment the
/// tap was decided — which is not the same as "no such card exists now": a card issued afterwards
/// does not retroactively resolve rows already written.
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
/// What the server decided. <c>CardNotFound</c> today, and the field exists so that a second
/// unresolved outcome can be added later without changing the shape of this report.
/// </param>
/// <param name="LocalOutcome">
/// What the <em>device</em> claimed, verbatim and possibly null. <b>Never authoritative.</b> Its
/// value is in disagreeing with <paramref name="ServerOutcome"/>: a device reporting that it found
/// this card while the server could not resolve it is evidence of a stale manifest, a sync that did
/// not run, or a cloned card.
/// </param>
public record EventScanDto(
    string CardUid,
    DateTime ScannedAt,
    DateTime RecordedAt,
    string? DeviceTapId,
    Guid? DeviceId,
    string ServerOutcome,
    string? LocalOutcome);

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
