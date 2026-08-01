namespace EAMS.Application.Dtos;

/// <summary>
/// The body of <c>GET /api/v1/events/{id}/manifest</c> (D-46) — the offline capture cache a device
/// pulls before it starts scanning, published as the <c>EventManifest*</c> schemas in the generated
/// OpenAPI document and frozen with the external mobile developer.
///
/// <para>
/// <b>This is the invitation, not the roster.</b> It says who is expected and which card resolves to
/// whom; it says nothing about who has already tapped. That is
/// <c>GET /attendance/live/{eventId}</c>. A manifest carrying attendance state would read as
/// authoritative on the device, and the one thing this object must never be is authoritative — the
/// server rules on every tap, always.
/// </para>
///
/// <para>
/// <b>Offline validation against it is display-only and never gating.</b> A tap whose UID is absent
/// from this manifest MUST still be queued and flushed. The server decides; it lands as a walk-in
/// (<c>isExpected: false</c>, already modelled by <see cref="EventRosterDto"/>, D-20). A client that
/// refuses to capture an unknown card converts "the cache is stale" into "the attendance never
/// existed", and the two are indistinguishable afterwards.
/// </para>
///
/// <para>
/// <b>Never truncated and never paged.</b> Over
/// <c>EventManifestLimits.MaxAttendees</c> this endpoint is a loud <c>413</c>, never a partial body.
/// </para>
/// </summary>
/// <param name="Event">The event's own capture-relevant fields. See <see cref="EventManifestEventDto"/>.</param>
/// <param name="Groups">
/// Every §4.7 group attached to this event's audience, ordered by <c>studentGroupId</c>.
///
/// <para>
/// <b>Called <c>groups</c> rather than <c>sections</c>, matching <see cref="EventAudienceDto"/>.</b>
/// <c>StudentGroups.Type</c> is wider than "Section" — a college, a programme and a hand-made "SSC
/// Officers" list are all groups — so a field named <c>sections</c> would be a lie on three of the
/// values it can carry, and a client that filtered on the name would drop them.
/// </para>
/// </param>
/// <param name="Attendees">
/// Who is expected, <b>de-duplicated to exactly one row per student</b>, ordered by <c>studentId</c>.
///
/// <para>
/// <b>The de-duplication is the contract, not an implementation detail.</b> Twelve of the fifty-two
/// students in the real roster sit in more than one section (ADR-001 D-1/D-2), so an event that
/// invites two sections of one programme reaches a quarter of them twice. A client keying its offline
/// index by <c>studentId</c> against a doubled list either overwrites a row's <c>groupIds</c> — losing
/// the second section — or double-counts the denominator it shows on screen. Both look plausible.
/// </para>
///
/// <para>
/// <b><c>attendees.length</c> equals the <c>expected</c> that <c>GET /events/{id}/summary</c>,
/// <c>GET /events/{id}/roster</c> and <c>GET /events/{id}/attendees</c> report for the same event</b>
/// — the same population from the same query, with the soft-deleted excluded the same way (ADR-003
/// D-15). It is not "approximately the same": a device showing a different denominator from the
/// dashboard beside it is what an operator files as "the system is wrong", and neither number looks
/// wrong on its own. ADR-003 D-19 records what a second implementation of that count costs.
/// </para>
/// </param>
/// <param name="ServerTime">
/// Our clock when this body was composed. <b>Check it against the device clock before enabling scan
/// mode</b>: if the two differ by more than five minutes, do not scan. Every tap captured past that
/// threshold arrives as <c>TappedAtOutOfRange</c> — poison, dropped by the client's own queue,
/// attendance gone. It is the one clock rule that prevents loss rather than reporting it.
///
/// <para>
/// <b>A 304 carries no body and therefore no <c>serverTime</c>; take the offset from the HTTP
/// <c>Date</c> header instead.</b> Assuming the previous offset is still valid because the manifest
/// did not change is wrong — the manifest not changing says nothing about the clock.
/// </para>
///
/// <para>
/// <b>There is deliberately no separate <c>generatedAt</c>.</b> The two would be identical today, and
/// a client that treated them as interchangeable would compute its clock offset from a cache-fill
/// time the day a server-side cache lands.
/// </para>
/// </param>
/// <param name="Version">
/// The <c>ETag</c> with the <c>W/</c> prefix and the quotes stripped. <b>Opaque: never parse it,
/// never order it, compare it for equality only.</b>
///
/// <para>
/// It is duplicated into the body because an offline client persists a parsed object and very often
/// not the headers that came with it — and the value it has to send back in <c>If-None-Match</c> on
/// the next pull is exactly this one.
/// </para>
/// </param>
public record EventManifestDto(
    EventManifestEventDto Event,
    IReadOnlyList<EventManifestGroupDto> Groups,
    IReadOnlyList<EventManifestAttendeeDto> Attendees,
    DateTime ServerTime,
    string Version);

/// <summary>The event this manifest is for, in the fields a capture client actually branches on.</summary>
/// <param name="Id">The event id every queued tap must carry.</param>
/// <param name="Name">Prose. Display only — never parsed, and reworded freely.</param>
/// <param name="StartAt">
/// UTC, and the same field <see cref="EventDto"/> publishes — <c>startAt</c>, not <c>startsAt</c>.
/// </param>
/// <param name="EndAt">
/// UTC.
///
/// <para>
/// <b>This is not the capture window, and treating it as one loses taps.</b> The range D-36 actually
/// accepts is <see cref="StartAt"/>/<see cref="EndAt"/> widened by a per-school margin, and that
/// margin is deliberately not published: publishing it invites the client to enforce the window
/// locally and refuse taps the server would have accepted. Queue everything; let the server rule.
/// </para>
/// </param>
/// <param name="GraceMinutes">
/// §4.5's Present-versus-Late boundary, published so a device can label its own screen. The server
/// decides the stored status regardless.
/// </param>
/// <param name="AttendanceMode">
/// <c>Single</c> or <c>TimeInOut</c>. <b>An unrecognised value must be treated as <c>Single</c></b>
/// (§4.5's default) — a device that refuses to scan because a future mode was added is worse than one
/// that captures single taps.
/// </param>
/// <param name="Status">
/// Always <c>Open</c> on a 200: this endpoint serves no other status (see the 409s). Published anyway
/// so a persisted manifest carries what it was captured against.
/// </param>
public record EventManifestEventDto(
    Guid Id,
    string Name,
    DateTime StartAt,
    DateTime EndAt,
    int GraceMinutes,
    string AttendanceMode,
    string Status);

/// <summary>One §4.7 group attached to the event's audience.</summary>
/// <param name="StudentGroupId">
/// The id <see cref="EventManifestAttendeeDto.GroupIds"/> refers to. Stable across pulls.
/// </param>
/// <param name="Name">
/// Prose. Display only. A derived group's name carries its term — <c>"BSFS 2-A (2025-2026-1)"</c> —
/// which is presentation, not structure; do not parse it back out.
/// </param>
/// <param name="Type">
/// §4.7's set plus <c>College</c>/<c>Program</c> (ADR-001 D-1). <b>An unrecognised value must render
/// as a plain group and must never be dropped</b> — otherwise a future group kind silently removes
/// students from the device's filter, and they are fully expected on the server the whole time.
/// </param>
public record EventManifestGroupDto(Guid StudentGroupId, string Name, string Type);

/// <summary>
/// One expected student. Exactly one row per student, however many attached groups reach them.
/// </summary>
/// <param name="StudentId">The identity to key the offline index by. Stable.</param>
/// <param name="StudentNumber">
/// The institutional student number.
///
/// <para>
/// <b>It is not a tap identity and must never be matched against a scanned serial</b> (D-43). The card
/// UID and the student number come from different source columns and have no relationship; a device
/// that falls back to matching the number when a UID misses will attribute a tap to a stranger.
/// </para>
/// </param>
/// <param name="FullName">Prose. Display only.</param>
/// <param name="GroupIds">
/// Which of <see cref="EventManifestDto.Groups"/> reach this student, ordinal-ordered.
///
/// <para>
/// <b>Empty is common and is not an anomaly</b> — it is the individually-attached student, the handful
/// an organizer named by hand on top of the sections. A client that filters by group must keep an
/// "all" or "ungrouped" view, or those students are invisible on the device while being fully expected
/// on the server.
/// </para>
/// </param>
/// <param name="CardUids">
/// The student's <em>active</em> card serials, normalized (<c>CardUid.Normalize</c> — uppercased,
/// non-alphanumerics stripped) and ordinal-ordered. Normalize a scanned serial the same way before
/// comparing: normalize first, compare second.
///
/// <para>
/// <b>Strings, never numbers, at every layer.</b> Real serials from the CICSS export are ten decimal
/// digits with significant leading zeros: <c>0012503301</c> through a numeric type is
/// <c>12503301</c> — a different card that will never match anything — and it breaks identically on
/// both sides of the wire, so nothing anywhere reports an error.
/// </para>
///
/// <para>
/// <b>Plural, because a reissue leaves the old row active</b> (ADR-001 D-3). A student can legitimately
/// present either card.
/// </para>
///
/// <para>
/// <b>Empty is today's ordinary case, not an error.</b> The supplied roster carries no RFID column
/// (D-43), so every student currently imports with no card at all. A client whose manifest is entirely
/// cardless must tell the operator the offline cache is unusable rather than silently offering an
/// index that can never hit.
/// </para>
/// </param>
public record EventManifestAttendeeDto(
    Guid StudentId,
    string StudentNumber,
    string FullName,
    IReadOnlyList<Guid> GroupIds,
    IReadOnlyList<string> CardUids);
