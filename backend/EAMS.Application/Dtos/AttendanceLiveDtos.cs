using System.Text.Json.Serialization;

namespace EAMS.Application.Dtos;

/// <summary>
/// One attendance row as the live dashboard sees it (Phase 4d, D-29).
///
/// <para>
/// <b>This type is deliberately a superset of the SignalR payload Technical Plan §6.4 declares</b>
/// (<c>{eventId, studentId, status, checkInAt, presentCount, expectedCount}</c>) — those six names,
/// spelled those ways, are the first six parameters below. That is the whole mechanism by which
/// deferring the hub stays reversible: if a hub is ever added it emits <em>this object</em>, unchanged,
/// and the client's reducer does not move. Collapsing the extra fields into a leaner "delta" type, or
/// renaming <c>presentCount</c> to match <c>EventSummaryDto.Present</c>, would quietly cost that
/// property and nothing would fail.
/// </para>
/// </summary>
/// <param name="PresentCount">
/// §6.4's field, and it is an <em>event-level</em> number repeated on every delta.
///
/// <para>
/// That looks like denormalization because it is. §6.4 declares it on the hub payload, so a hub client
/// receiving a single delta over a socket has the headline counts without a second request — which is
/// the entire reason a hub payload would carry them. The value is <c>EventSummaryDto.Present</c>:
/// students with a <c>Present</c> record, <em>not</em> Present + Late. Taking the summary's own field
/// rather than inventing a third definition is the point — the dashboard header and
/// <c>GET /events/{id}/summary</c> cannot disagree, because they are the same number from the same
/// query.
/// </para>
/// </param>
/// <param name="ExpectedCount">
/// <inheritdoc cref="PresentCount" path="/summary"/> The invited population — <c>EventSummaryDto.Expected</c>,
/// which is ADR-003 D-19's denominator and not "how many rows exist".
/// </param>
/// <param name="AttendanceId">
/// The <c>AttendanceRecords.Id</c> this delta describes. Not in §6.4's declared payload; added because
/// a reducer keyed on <c>studentId</c> alone cannot tell an update from a second row, and §4.6
/// occurrences will eventually make two rows per student per event legal.
/// </param>
public record AttendanceDeltaDto(
    Guid EventId,
    Guid StudentId,
    string Status,
    DateTime? CheckInAt,
    int PresentCount,
    int ExpectedCount,
    Guid AttendanceId,
    string StudentNumber,
    string StudentName,
    DateTime? CheckOutAt,
    string CaptureMethod);

/// <summary>
/// The body of <c>GET /attendance/live/{eventId}</c> — cursor-delta polling in place of the §5/§6.4
/// SignalR hub (Phase 4d, D-29).
///
/// <para>
/// <b>Why polling, recorded here because the endpoint is the argument.</b> A hub client has to fetch a
/// snapshot on every reconnect to close the gap it missed, so an endpoint of exactly this shape is
/// required under <em>both</em> designs — the hub would have been the second mechanism, not the first.
/// Polling also needs no backplane and no sticky sessions, survives a campus firewall, and keeps a
/// device key out of a WebSocket query string where it lands in access logs.
/// </para>
///
/// <para>
/// <b>One type serves both calls, and exactly one of the two collections is present.</b>
/// <see cref="Entries"/> when the caller sent no cursor, <see cref="Changes"/> when it sent one; the
/// absent one is omitted from the JSON entirely rather than serialized as <c>null</c>, so the two
/// published shapes are byte-for-byte what the document describes. The distinction is not cosmetic —
/// a client that received <c>{entries: [...], changes: null}</c> would have to guess which mode it is
/// in, and the whole point of the split is that a snapshot <em>replaces</em> state while a delta
/// <em>merges</em> into it.
/// </para>
/// </summary>
/// <param name="Cursor">
/// Opaque. Store it, send it back as <c>?since=</c>, do not parse it. See
/// <c>EAMS.Domain.AttendanceCursor</c> for what is inside and why.
///
/// <para>
/// <b>It advances even when nothing changed</b>, because it is a ceiling on what has been read rather
/// than the high-water mark of what was returned. Echoing the caller's own cursor back on an empty
/// delta would leave every poll re-scanning the same range forever after a quiet event.
/// </para>
/// </param>
/// <param name="Counters">
/// The event's headline numbers, and they are <c>GET /events/{id}/summary</c>'s own object, produced by
/// the same code. Reusing it rather than shaping a leaner "live counters" type is deliberate: the
/// denominator is ADR-003 D-12/D-13's arithmetic, which that ADR records as failing <em>silently</em>
/// when it is duplicated, and a dashboard header that disagreed with the summary page beside it would
/// be the exact plausible-but-wrong number Phase 3a existed to remove.
/// </param>
/// <param name="PollAfterSeconds">
/// How long the client should wait before polling again. <b>In the body on purpose</b>: it lets the
/// server back every dashboard off under load without anyone shipping a client release, which is the
/// one operational lever a polling design has and a hub does not.
/// </param>
/// <param name="HasMore">
/// True when this response was truncated at
/// <see cref="EAMS.Application.Abstractions.AttendanceLiveOptions.MaxPageRows"/> and more
/// rows are already available below <see cref="Cursor"/>. <b>Poll again immediately rather than waiting
/// <see cref="PollAfterSeconds"/>.</b>
///
/// <para>
/// Without this the page cap would be its own defect: a five-thousand-row first snapshot at five
/// hundred rows a page and five seconds between pages takes the better part of a minute to fill a
/// dashboard that used to fill in one request. The flag costs one boolean and turns the cap into
/// paging.
/// </para>
/// </param>
public record AttendanceLiveDto(
    Guid EventId,
    string Cursor,
    EventSummaryDto Counters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AttendanceDeltaDto>? Entries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AttendanceDeltaDto>? Changes,
    DateTime ServerTime,
    int PollAfterSeconds,
    bool HasMore);
