using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// Why the outcome is an enum rather than an exception or a bare bool — the same reasoning
/// <see cref="TapOutcome"/> records: the write surface has several non-exceptional failures that map to
/// different HTTP statuses, and the service owns the decision while the controller owns only the
/// translation.
/// </summary>
public enum EventWriteOutcome
{
    /// <summary>The write happened, or the request asked for a state the event was already in.</summary>
    Saved,

    /// <summary>No such event, or it is soft-deleted. 404.</summary>
    NotFound,

    /// <summary>
    /// A field failed a §4.5 rule — a blank or over-length name, an end before its start, an
    /// undocumented <c>AttendanceMode</c> or <c>Status</c> value, a negative grace period. 400, and the
    /// message names the field.
    ///
    /// <para>
    /// One member rather than one per field on purpose. <see cref="ManualOutcome.InvalidStatus"/> and
    /// <see cref="ManualOutcome.InvalidNotes"/> are split because each records a distinct shipped defect
    /// worth naming; these are a validation layer written all at once, they all map to 400, and eight
    /// enum members that a controller treats identically would be eight things to keep in sync for no
    /// reader's benefit.
    /// </para>
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// The requested status is not reachable from the current one — see <c>EventStatusTransition</c>.
    /// 400 rather than a silent write, and the message lists what <em>is</em> reachable.
    /// </summary>
    IllegalTransition,

    /// <summary>
    /// The event's state forbids this change: its audience cannot move once it is <c>Closed</c> or
    /// <c>Cancelled</c>, and its own fields cannot be edited once it is <c>Closed</c>. 409, not 400 —
    /// the request is well formed and would be legal against the same event in another state, which is
    /// the distinction <c>SisImportController</c> already draws between "cannot be run" and "malformed".
    /// </summary>
    EventLocked,

    /// <summary>
    /// A <c>studentGroupId</c> or <c>studentId</c> in the payload does not resolve to a row belonging to
    /// this event's school. 400, listing the ids. Covers the unknown id and the other tenant's id
    /// identically and on purpose: telling a caller which of the two it was confirms the existence of a
    /// row in a school they cannot see.
    /// </summary>
    UnknownReference,

    /// <summary>
    /// The payload named a derived <c>Section</c> group whose key is <c>AcademicKey.Unspecified</c>.
    /// 400. The projection deliberately never creates one — a union of every offering whose section
    /// cell was blank is not a cohort anybody could have meant to invite — so this can only be a
    /// hand-written row, and inviting it would put an arbitrary slice of the institution in a
    /// denominator.
    /// </summary>
    NotACohort,

    /// <summary>
    /// A new event cannot be filed against a school. 409. Only reachable in the pre-auth build with no
    /// tenant pinned and zero or several <c>Schools</c> rows; Phase 6 makes it unreachable by resolving
    /// the tenant from claims before the request gets this far.
    /// </summary>
    NoSchoolResolved,
}

/// <summary>The result of a write against one event. <paramref name="Event"/> is null unless it saved.</summary>
public record EventWriteResponse(EventWriteOutcome Outcome, string Message, EventDto? Event);

/// <summary>The result of an audience change. <paramref name="Result"/> is null unless it saved.</summary>
public record EventAudienceResponse(
    EventWriteOutcome Outcome, string Message, EventAudienceResultDto? Result);

/// <summary>
/// What <c>GET /attendance/live/{eventId}</c> decided (Phase 4d, D-29). Its own enum rather than a
/// member of <see cref="TapOutcome"/> on purpose: that enum is frozen published contract, and a
/// dashboard read has no business appearing in the mobile capture client's branch table.
/// </summary>
public enum LiveOutcome
{
    /// <summary>A snapshot or a delta was produced.</summary>
    Ok,

    /// <summary>No such event, or it is soft-deleted. 404.</summary>
    EventNotFound,

    /// <summary>
    /// <c>?since=</c> was present but is not a cursor this API issued. 400.
    ///
    /// <para>
    /// <b>Refused rather than quietly treated as "no cursor".</b> Falling back to a full snapshot is
    /// the friendlier-looking behaviour and is the wrong one: the client asked for a delta, so it would
    /// merge a complete row set into state that already holds those rows. See
    /// <c>EAMS.Domain.AttendanceCursor.TryDecode</c>.
    /// </para>
    /// </summary>
    InvalidCursor,
}

/// <summary><paramref name="Live"/> is null unless <paramref name="Outcome"/> is <see cref="LiveOutcome.Ok"/>.</summary>
public record LiveAttendanceResponse(LiveOutcome Outcome, string Message, AttendanceLiveDto? Live);

/// <summary>
/// What <c>GET /events/{id}/manifest</c> decided (D-46). Its own enum rather than a member of
/// <see cref="TapOutcome"/> for the reason <see cref="LiveOutcome"/> records: that table is frozen
/// published contract for the capture path, and a manifest pull is not a tap.
///
/// <para>
/// Only <see cref="EventFrozen"/> and <see cref="ManifestTooLarge"/> are new tokens on the wire;
/// <see cref="EventNotFound"/> and <see cref="EventNotOpen"/> are the ones a capture client already
/// branches on, deliberately reused so the device has one meaning per token across both endpoints.
/// </para>
/// </summary>
public enum ManifestOutcome
{
    /// <summary>A manifest was composed. 200 — or 304, which the controller decides from the ETag.</summary>
    Ok,

    /// <summary>
    /// No such event, it is soft-deleted, <b>or it belongs to another school</b>. 404, and the three
    /// are deliberately indistinguishable — D-27, no cross-tenant existence disclosure. The device's
    /// own <c>school_id</c> claim scopes the read, so the last case never reaches a branch here at all.
    /// </summary>
    EventNotFound,

    /// <summary>
    /// The event is <c>Draft</c> — it has not been opened yet. 409.
    ///
    /// <para>
    /// <b>Refused rather than served, and that is a decision.</b> Serving a draft manifest would let a
    /// device scan against an event whose audience is still being assembled and whose taps
    /// <c>POST /attendance/tap</c> would refuse anyway. The guarantee that a device only ever holds a
    /// manifest it can capture against belongs on our side, not in an external client.
    /// </para>
    /// </summary>
    EventNotOpen,

    /// <summary>
    /// The event is <c>Closed</c> or <c>Cancelled</c>. 409.
    ///
    /// <para>
    /// Split from <see cref="EventNotOpen"/> because the client instruction is the opposite way round:
    /// on this one it must <b>flush its queue first</b> and only then stop and tell the operator the
    /// event is over — the cached manifest is what its queued taps still need, so discarding it before
    /// the queue drains loses the names. The predicate is <c>EventStatusTransition.IsTerminal</c>, the
    /// same one <see cref="EventRosterDto.IsFrozen"/> publishes: both terminal statuses, not
    /// <c>Closed</c> alone.
    /// </para>
    /// </summary>
    EventFrozen,

    /// <summary>
    /// The expected population exceeds <c>EventManifestLimits.MaxAttendees</c>. 413.
    ///
    /// <para>
    /// Deliberately loud rather than truncated, and unlike <see cref="TapOutcome.BatchTooLarge"/> there
    /// is nothing for the client to halve — see <c>EventManifestLimits.MaxAttendees</c>.
    /// </para>
    /// </summary>
    ManifestTooLarge,
}

/// <summary>
/// <paramref name="Manifest"/> is null unless <paramref name="Outcome"/> is
/// <see cref="ManifestOutcome.Ok"/>.
/// </summary>
/// <param name="Attendees">
/// How many attendees the manifest carries, or would have carried. Populated on
/// <see cref="ManifestOutcome.ManifestTooLarge"/> as well as on success, so the refusal can say how far
/// over the ceiling the event is — "too large" with no number gives an operator nothing to report.
/// </param>
public record EventManifestResponse(
    ManifestOutcome Outcome, string Message, EventManifestDto? Manifest, int Attendees);

/// <summary>
/// What <c>POST /events/audience/resolve</c> decided (D-50/D-51). Its own enum rather than a member of
/// <see cref="EventWriteOutcome"/>, for the reason <see cref="LiveOutcome"/> and
/// <see cref="ManifestOutcome"/> each record: that table is the events <em>write</em> surface's branch
/// list, and resolving an audience writes nothing at all.
///
/// <para>
/// Both failures are 400 and both exist for one reason — <b>a filter must never quietly stop
/// filtering.</b> A dropped row or a dropped value produces a count larger than the operator asked
/// for, which looks entirely normal and is not discovered until the wrong people are invited.
/// </para>
/// </summary>
public enum AudienceResolveOutcome
{
    /// <summary>A resolution was produced. 200 — possibly matching nobody, which is an answer.</summary>
    Ok,

    /// <summary>
    /// A filter row named a field outside <see cref="EAMS.Domain.AudienceField.All"/>. 400, and the
    /// message names both the value sent and the five that are accepted.
    ///
    /// <para>
    /// <b>Refused rather than skipped, which is the whole of D-50's enforcement at this boundary.</b>
    /// The registry is closed so that <c>Students.Course</c>, <c>Students.YearLevel</c> and
    /// <c>Students.Section</c> are unreachable; silently ignoring an unregistered field would make an
    /// attempt to reach them indistinguishable from not having sent it, and the caller would read the
    /// resulting (larger) count as the answer to the filter it thought it had applied.
    /// </para>
    /// </summary>
    UnknownAudienceField,

    /// <summary>
    /// A filter row carried a value the field cannot express — a non-GUID where an id belongs, a blank
    /// string, or a <c>Section</c> value that normalizes onto <c>AcademicKey.Unspecified</c>. 400,
    /// naming the field and the value.
    ///
    /// <para>
    /// <b>The same rule as <see cref="UnknownAudienceField"/>, one level down.</b> Dropping the bad
    /// value from the list would silently widen the row — <c>Program is any of (BSIT, "oops")</c> would
    /// resolve as <c>Program is BSIT</c> and report a count for a filter nobody built.
    /// </para>
    ///
    /// <para>
    /// <b>It is not the answer for a well-formed value that matches nobody.</b> An id naming a college
    /// that does not exist is a filter that matched nothing, and this layer already answers that with an
    /// empty result rather than an error — see <c>IAcademicReferenceService.ListProgramsAsync</c>. The
    /// line is between a value the field cannot hold and a value it can hold but nobody has.
    /// </para>
    /// </summary>
    InvalidAudienceFilterValue,
}

/// <summary>
/// <paramref name="Resolution"/> is null unless <paramref name="Outcome"/> is
/// <see cref="AudienceResolveOutcome.Ok"/>.
/// </summary>
public record AudienceResolveResponse(
    AudienceResolveOutcome Outcome, string Message, AudienceResolutionDto? Resolution);

/// <summary>Technical Plan §6.3 and the §6.7/§12 event summary and roster.</summary>
public interface IEventService
{
    /// <summary>
    /// §6.3 <c>GET /events</c> — "Paged", per the plan. Newest start first, then by <c>Id</c> so two
    /// events starting at the same instant cannot trade places between pages.
    /// </summary>
    Task<PagedResult<EventDto>> ListAsync(
        string? status, PageRequest page, CancellationToken ct = default);

    Task<EventDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<EventSummaryDto?> GetSummaryAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// §6.3 <c>POST /events</c>. The event is created <c>Draft</c> — always, and regardless of what the
    /// caller wants — because <see cref="ChangeStatusAsync"/> is the only path that can freeze a roster.
    /// See <see cref="EventWriteRequest"/>.
    /// </summary>
    Task<EventWriteResponse> CreateAsync(EventWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// §6.3 <c>PUT /events/{id}</c>. A full replacement of the event's own fields; it does not touch
    /// <c>Status</c>, <c>SchoolId</c>, <c>IsDeleted</c>, or the audience. Refused on a <c>Closed</c>
    /// event — see <c>EventStatusTransition.AcceptsEdits</c>.
    /// </summary>
    Task<EventWriteResponse> UpdateAsync(
        Guid id, EventWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// §6.3 <c>PATCH /events/{id}/status</c> — Open / Close / Cancel.
    ///
    /// <para>
    /// <b>The transition to <c>Closed</c> materializes the expected roster</b>: every expected student
    /// with no attendance record gets one, <c>Absent</c> / <c>Import</c>. That is what fixes the
    /// denominator and the absentee list in place, so no later roster import can move a past event's
    /// numbers. Idempotent by construction — <c>UNIQUE(EventId, StudentId, OccurrenceId)</c> — and
    /// skipped entirely when the event is already <c>Closed</c>.
    /// </para>
    /// </summary>
    Task<EventWriteResponse> ChangeStatusAsync(
        Guid id, string status, CancellationToken ct = default);

    /// <summary>
    /// §6.3 <c>DELETE /events/{id}</c> — soft, per §4.5's <c>IsDeleted</c>. Attendance rows are left
    /// exactly where they are: every FK in this model is <c>Restrict</c>, and an attendance trail must
    /// not vanish because someone tidied a calendar.
    /// </summary>
    Task<EventWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// §6.3 <c>POST /events/{id}/attendees</c> — writes §4.8 <c>EventGroups</c> rows.
    ///
    /// <para>
    /// <b>Idempotent.</b> Re-posting the same selection attaches nothing and reports the ids as already
    /// attached. Backed by two filtered unique indexes rather than by this method remembering to check,
    /// so two concurrent posts cannot both win.
    /// </para>
    ///
    /// <para>
    /// <b>Cross-school references are refused</b> (<see cref="EventWriteOutcome.UnknownReference"/>) and
    /// so is the <c>(unspecified)</c> section group (<see cref="EventWriteOutcome.NotACohort"/>).
    /// </para>
    ///
    /// <para>
    /// <b>A group from another term warns rather than refuses,</b> and the asymmetry with the two
    /// refusals above is the decision worth recording. An <c>Event</c> carries no <c>TermId</c> — §4.5
    /// has no such column — so "another term" can only mean "not the term currently flagged
    /// <c>IsCurrent</c>", which is a property of the school right now and not of the event. Refusing on
    /// it would make the identical request succeed today and fail after the registrar advances the
    /// term, for an event nobody touched; it would also foreclose two legitimate cases, an event
    /// prepared for next term and an event that deliberately invites a past cohort. The mistake is
    /// visible without help — every derived group's name carries its term, so the attached list reads
    /// "BSCRIM 2-A (2024-2025-2)" — and it is fully reversible with one <c>DELETE</c>. Contrast the
    /// <c>(unspecified)</c> group, which has no legitimate reading at all, and a cross-school group,
    /// which is a tenancy breach rather than a judgement call. This follows ADR-001 D-5's line: the
    /// import pipeline warns on anomalies and hard-fails only where the alternative is unrecoverable.
    /// </para>
    /// </summary>
    Task<EventAudienceResponse> AttachAudienceAsync(
        Guid id, EventAudienceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Removes an attached group. Succeeds whether or not the group was attached — the postcondition
    /// ("this group is not in this event's audience") holds either way, so a retry is safe. A missing
    /// <em>event</em> is still a 404: that one is named by the URL.
    /// </summary>
    Task<EventAudienceResponse> DetachGroupAsync(
        Guid id, Guid studentGroupId, CancellationToken ct = default);

    /// <inheritdoc cref="DetachGroupAsync"/>
    Task<EventAudienceResponse> DetachStudentAsync(
        Guid id, Guid studentId, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /events/{id}/attendees</c> — the read half of the audience write surface above. Null when
    /// there is no such event.
    ///
    /// <para>
    /// <b>It returns the §4.8 rows, not the population they resolve to.</b> Attached groups come back as
    /// groups — including on a terminal event, where they are ADR-003 D-13's record of which cohort was
    /// invited and nothing resolves them any more. Individually-attached students come back as students
    /// while the event is live; once it is terminal that same set is the whole frozen audience, and
    /// <see cref="EventAudienceDto.Students"/> is empty by contract with the roster named as the place
    /// to enumerate it. See that DTO for the full reasoning.
    /// </para>
    ///
    /// <para>
    /// <c>Expected</c> comes from the one denominator query <see cref="GetSummaryAsync"/> and
    /// <see cref="GetRosterAsync"/> already use — deliberately not recomputed here. ADR-003 D-19 records
    /// what a second implementation of that number costs: every copy stays plausible while they drift.
    /// </para>
    /// </summary>
    Task<EventAudienceDto?> GetAudienceAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// §6.3 <c>GET /events/{id}/roster</c> — expected versus actual, de-duplicated across attached
    /// groups. Null when there is no such event.
    /// </summary>
    Task<EventRosterDto?> GetRosterAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /attendance/live/{eventId}?since=</c> — the D-29 polling endpoint that stands in for the
    /// §5/§6.4 SignalR hub.
    ///
    /// <para>
    /// <b>It lives on the event service rather than the attendance service even though its route is
    /// attendance's</b>, and the reason is the <c>counters</c> block. Those are
    /// <see cref="GetSummaryAsync"/>'s numbers, and the denominator behind them is ADR-003 D-12/D-13
    /// arithmetic that the ADR explicitly records as failing <em>silently</em> when it is duplicated —
    /// a second copy would drift and every number involved would stay plausible. Producing them from
    /// the one method that already owns them costs a controller its second constructor argument and
    /// buys the guarantee that the dashboard header and the summary page cannot disagree.
    /// </para>
    /// </summary>
    /// <param name="since">
    /// Null or absent for a snapshot; otherwise a cursor from a previous response. Whitespace is
    /// treated as absent — a query string that lost its value is not a corrupted cursor.
    /// </param>
    Task<LiveAttendanceResponse> GetLiveAttendanceAsync(
        Guid id, string? since, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /events/{id}/manifest</c> — the offline capture cache a device pulls before it scans
    /// (D-46). Only an <c>Open</c> event is served; every other status is a refusal.
    ///
    /// <para>
    /// <b>It lives on the event service, and on this one in particular, because of
    /// <see cref="EventManifestDto.Attendees"/>.</b> That list must be exactly the population
    /// <see cref="GetSummaryAsync"/>, <see cref="GetRosterAsync"/> and <see cref="GetAudienceAsync"/>
    /// count as <c>expected</c> — same query, same exclusion of the soft-deleted (ADR-003 D-15) — and
    /// ADR-003 D-19 records what a second implementation of that population costs: every copy stays
    /// plausible while they drift, and the symptom is a device showing a different denominator from the
    /// dashboard beside it. A separate manifest service would have had to re-derive it.
    /// </para>
    ///
    /// <para>
    /// <b>The whole published body is composed inside one read snapshot</b> — see the implementation.
    /// Across several unsynchronized reads the version could name a state that never existed, and two
    /// successive pulls could flip between them: a device oscillating between two versions, each a
    /// perfectly valid 200.
    /// </para>
    ///
    /// <para>
    /// <b>The optional <c>?clientClockAt=</c> is deliberately not a parameter here.</b> It is measured
    /// against our clock <em>on arrival</em>, logged, and never validated — exactly as
    /// <see cref="TapBatchRequest.ClientClockAt"/> is, and never a refusal. "On arrival" is a property
    /// of the request rather than of the manifest, so the measurement belongs at the HTTP boundary; and
    /// threading a value through this method that no part of the answer depends on would suggest it
    /// could one day change the answer, which is the one thing the frozen contract says it must never
    /// do.
    /// </para>
    /// </summary>
    /// <param name="id">The event.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<EventManifestResponse> GetManifestAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /events/audience/resolve</c> — the D-50/D-51 audience filter builder's one query: filter
    /// rows in, a count plus the matched student ids and a preview out.
    ///
    /// <para>
    /// <b>It is read-only and idempotent despite the verb.</b> Resolving writes nothing, touches no
    /// event, and can be repeated freely — it backs a live count that runs while an operator is still
    /// choosing values. <c>POST</c> rather than <c>GET</c> because the filter set is a structured body
    /// of arbitrarily many rows each carrying arbitrarily many values, which outgrows a query string as
    /// fields are added and would have to be encoded into one twice — once by the client and once by
    /// this API's parser — with the encoding as the contract.
    /// </para>
    ///
    /// <para>
    /// <b>It resolves to students; it does not attach them.</b> D-52 decides how a built filter becomes
    /// an audience — materialised to individual rows when several rows are present, attached as its
    /// group row for a single-field selection — and none of that is here. Nothing in this method's path
    /// writes.
    /// </para>
    ///
    /// <para>
    /// <b>How the rows combine (D-51): values within a row union, rows intersect.</b> Composed as one
    /// <c>IQueryable</c> — one <c>Where</c> per row, one <c>Contains</c> per value list — so there is no
    /// query language to parse and no caller-authored SQL anywhere near the audience. An empty value
    /// list makes its row a no-op; an unregistered field or an unusable value is a refusal. See
    /// <see cref="AudienceFilterDto"/>.
    /// </para>
    ///
    /// <para>
    /// <b>A student in two matching sections is counted once</b>, and the de-duplication is structural —
    /// see <see cref="AudienceResolutionDto"/>. That is the ADR-001 D-2 tripwire, and it is the reason
    /// the builder resolves through <c>Enrollments</c> instead of reading <c>Students.Section</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Tenancy is the ambient <c>SchoolId</c> query filter and nothing else.</b> Every row this
    /// method reads goes to an HTTP caller, so widening it would be a disclosure bug rather than the
    /// scoping convenience it is inside <c>StudentGroupProjection</c>; nothing on this path calls
    /// <c>IgnoreQueryFilters</c>.
    /// </para>
    /// </summary>
    /// <param name="request">The term (optional) and the filter rows.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AudienceResolveResponse> ResolveAudienceAsync(
        AudienceResolveRequest request, CancellationToken ct = default);
}
