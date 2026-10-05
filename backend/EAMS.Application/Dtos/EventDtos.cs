namespace EAMS.Application.Dtos;

// The Phase 3a write surface for Technical Plan §6.3, plus the §6.3/§12 roster. Read-side EventDto
// and EventSummaryDto stay in Dtos.cs with the rest of the core slice.

/// <summary>
/// The body of <c>POST /events</c> and <c>PUT /events/{id}</c>.
///
/// <para>
/// <b><c>Status</c> is deliberately absent.</b> <c>PATCH /events/{id}/status</c> is the only door into
/// the column, which is what makes the roster freeze impossible to bypass — an event cannot reach
/// <c>Closed</c> without passing through the one code path that materializes its absentees. A
/// <c>status</c> field here would be a second door, and the first caller to use it would create a
/// closed event with no frozen roster and no error.
/// </para>
///
/// <para>
/// <b><c>SchoolId</c> is deliberately absent too.</b> The tenant comes from <c>ISchoolContext</c>, not
/// from the request. A caller-supplied <c>SchoolId</c> on a write is exactly the hole
/// <c>KnownDefectTests.A_device_from_another_school_cannot_record_a_tap</c> is still open on; this
/// endpoint does not add a second one.
/// </para>
/// </summary>
/// <param name="StartAt">
/// UTC. Normalized through <c>UtcTime</c> on the way in — a bare or offset-bearing JSON date-time is
/// otherwise compared against a UTC <c>StartAt</c> and misjudges Present versus Late by the server's
/// offset, which is eight hours in Manila.
/// </param>
/// <param name="AttendanceMode">
/// <c>Single</c> or <c>TimeInOut</c>; null or blank takes §4.5's <c>Single</c> default.
/// </param>
/// <param name="IssuesCertificates">
/// Whether the event issues certificates of attendance. <b>Optional, and omitting it never changes
/// anything:</b> on <c>POST</c> an omitted or null value creates the event with <c>false</c>; on
/// <c>PUT</c> it <em>keeps the stored value</em>. Send <c>true</c> or <c>false</c> to set it.
///
/// <para>
/// <b>Why this one field is not a full replacement like the rest of the body.</b> It was added after
/// clients of this <c>PUT</c> already existed. As a plain boolean, every such client — which sends
/// the body it knows, without this field — would reset it to <c>false</c> on every edit, silently: a
/// successful save that turns certificates off. Null is therefore "not mentioned", not "false".
/// </para>
///
/// <para>
/// <b>Changeable in every status, <c>Closed</c> included.</b> It does not affect what any attendance row
/// means. On a <c>Closed</c> event a <c>PUT</c> is accepted only when it names this field and changes
/// nothing else — re-sending the current value included, so a retried toggle succeeds; everything else
/// on a closed event stays locked.
/// </para>
/// </param>
/// <param name="GraceMinutes">Grace period after start — the Present/Late cutoff (§4.5's original grace).</param>
/// <param name="GraceBeforeStartMinutes">
/// Grace period before start (EventGracePeriod.docx §1), or null. Null means "no per-event before-start
/// limit"; a value refuses taps earlier than <c>StartAt − this</c> as Too Early. Optional and defaulted
/// null so callers that predate the field are unchanged.
/// </param>
/// <param name="GraceAfterEndMinutes">
/// Grace period after end (EventGracePeriod.docx §3), or null. A value refuses taps later than
/// <c>EndAt + this</c> as Attendance Closed.
/// </param>
/// <param name="EventClassificationId">
/// The event's classification (Institutional, Departmental, Organizational, …), or null for
/// unclassified. Optional and defaulted null so callers that predate the field are unchanged — an
/// omitted value is "unclassified" on create and, unlike a full-replacement field, there is nothing to
/// reset on edit because null legitimately <em>is</em> the no-classification state.
///
/// <para>
/// When non-null it must name an <em>active</em> classification in this event's school, or the write is
/// refused as <see cref="EAMS.Application.Abstractions.EventWriteOutcome.UnknownReference"/> — unknown,
/// deactivated and another tenant's id are not distinguished, for the reason that outcome records. On a
/// <c>Closed</c> event it is a locked field like the rest of the body (only the certificates toggle is
/// editable there).
/// </para>
/// </param>
public record EventWriteRequest(
    string Name,
    string? Description,
    string? Location,
    DateTime StartAt,
    DateTime EndAt,
    string? AttendanceMode,
    int GraceMinutes,
    bool RequireRegistration,
    bool? IssuesCertificates = null,
    int? GraceBeforeStartMinutes = null,
    int? GraceAfterEndMinutes = null,
    Guid? EventClassificationId = null);

/// <summary>The body of <c>PATCH /events/{id}/status</c>. See <c>EventStatusTransition</c>.</summary>
public record EventStatusRequest(string Status);

/// <summary>
/// The body of <c>POST /events/{id}/attendees</c> — §6.3's <c>{studentIds[], groupIds[]}</c>.
///
/// <para>
/// Both lists are optional and both may be sent at once. Sending neither is a no-op rather than an
/// error: it is what "the organizer cleared the form and saved" looks like, and 400-ing it would say
/// the request was malformed when it was merely empty.
/// </para>
/// </summary>
/// <param name="AudienceDefinitionIds">
/// Reusable <c>AudienceDefinition</c>s to attach alongside the sections and individuals (ADR-007 D-69).
/// Optional, like the other two. Each must be an active definition in this event's school <em>and</em>
/// filed under the event's <c>EventClassificationId</c>; an id that is unknown, another tenant's,
/// inactive, or outside the event's classification is refused identically as
/// <see cref="EAMS.Application.Abstractions.EventWriteOutcome.UnknownReference"/> (400), for the same
/// no-existence-disclosure reason the section and student refs are. The definition's <em>student</em>
/// half joins the expected denominator; its <em>personnel</em> half is excluded and surfaced as an
/// advisory count on the audience read (D-70).
/// </param>
public record EventAudienceRequest(
    IReadOnlyList<Guid>? StudentGroupIds,
    IReadOnlyList<Guid>? StudentIds,
    IReadOnlyList<Guid>? AudienceDefinitionIds = null);

/// <summary>
/// What one call to <c>POST /events/{id}/attendees</c> did.
///
/// <para>
/// The <c>Already</c> counters exist so idempotency is <em>observable</em> rather than merely true. A
/// re-post returning <c>{attached: 0, alreadyAttached: 3}</c> tells a caller its earlier request landed;
/// a bare 200 would leave "did that save?" unanswerable without a second round trip.
/// </para>
/// </summary>
/// <param name="Expected">
/// The event's expected-attendee count after this call — the same number
/// <c>GET /events/{id}/summary</c> reports. Returned here so the UI can show the denominator move as
/// sections are attached, without a follow-up request.
/// </param>
/// <param name="Warnings">
/// Non-fatal observations about what was attached. Empty on the ordinary case. See
/// <c>IEventService.AttachAudienceAsync</c> for why a term mismatch warns rather than refuses.
/// </param>
/// <param name="DefinitionsAttached">Attached audience definitions this call newly linked (ADR-007 D-69).</param>
/// <param name="DefinitionsAlreadyAttached">
/// Audience definitions the request named that were already linked — the observable half of idempotency
/// for the third source, exactly as <c>GroupsAlreadyAttached</c> is for sections.
/// </param>
public record EventAudienceResultDto(
    Guid EventId,
    int GroupsAttached,
    int StudentsAttached,
    int DefinitionsAttached,
    int GroupsAlreadyAttached,
    int StudentsAlreadyAttached,
    int DefinitionsAlreadyAttached,
    int Expected,
    IReadOnlyList<string> Warnings);

/// <summary>
/// <c>GET /events/{id}/attendees</c> — what is currently attached to an event's audience, as the
/// §4.8 <c>EventGroups</c> rows themselves rather than as the population they resolve to.
///
/// <para>
/// <b><see cref="IsFrozen"/> is the discriminator, and reading this object without it is a mistake.</b>
/// It is the same predicate <see cref="EventRosterDto.IsFrozen"/> publishes — terminal, so both
/// <c>Closed</c> and <c>Cancelled</c>, not <c>Closed</c> alone — and it changes what
/// <see cref="Students"/> means, as that field's own documentation sets out.
/// </para>
///
/// <para>
/// This is the read half of <c>POST /events/{id}/attendees</c> and the two <c>DELETE</c>
/// sub-resources: every id it returns is one those routes accept. It is deliberately <em>not</em> a
/// roster — it says who was invited, not who came. <c>GET /events/{id}/roster</c> is the other
/// question and lists people; this lists the invitation.
/// </para>
/// </summary>
/// <param name="Status">The event's §4.5 status, so a caller need not fetch the event to know it.</param>
/// <param name="IsFrozen">
/// True once this event's audience has been snapshotted — both terminal statuses (ADR-003 D-16). The
/// same flag, computed the same way, as <see cref="EventRosterDto.IsFrozen"/>: the two cannot disagree
/// about one event.
/// </param>
/// <param name="Expected">
/// The invited population — <b>the same number <c>GET /events/{id}/summary</c> and
/// <c>GET /events/{id}/roster</c> report for this event</b>, from the same query. It is deliberately
/// not derivable from the lists below: on a live event the group members are not enumerated here, and
/// on a terminal event <see cref="Students"/> is empty by contract. A caller wanting the denominator
/// reads this field; a caller wanting the people reads the roster.
/// </param>
/// <param name="Groups">
/// Every attached §4.7 group, whatever the event's status. <b>On a terminal event these are the
/// historical record of which cohort was invited</b> (ADR-003 D-13) — no query resolves them any more,
/// and they are returned precisely because that record is the thing the freeze keeps.
/// </param>
/// <param name="Students">
/// <b>Its meaning depends on <see cref="IsFrozen"/>, which is why that flag is published beside it.</b>
///
/// <para>
/// While the event is live (<c>Draft</c>/<c>Open</c>) these are the individually-attached students —
/// the handful an organizer named by hand on top of the sections, and the exact set the
/// <c>DELETE .../attendees/students/{studentId}</c> sub-resource addresses.
/// </para>
///
/// <para>
/// Once <see cref="IsFrozen"/>, <b>this list is empty by contract</b>, and the emptiness does not mean
/// "no students were attached" — it means "ask elsewhere". The freeze writes the whole resolved
/// audience down as individual rows (ADR-003 D-13), so the same query that returns three students on a
/// live event returns the entire population, unbounded, on a terminal one. That is
/// <c>GET /events/{id}/roster</c>'s job, and duplicating it here would add a second unbounded response
/// where ADR-003 already carries one as an open follow-up. <b>The per-student frozen set is
/// <c>GET /events/{id}/roster</c>.</b> <see cref="Expected"/> and <see cref="Groups"/> carry the answer
/// for anyone who only needs the size and the cohort.
/// </para>
///
/// <para>
/// Soft-deleted students are listed on a live event rather than hidden. Their §4.8 row exists and is
/// detachable, so hiding it would leave a row no client could see and no organizer could remove, while
/// a re-post naming that student would report it as already attached against a panel showing nothing.
/// They are excluded from <see cref="Expected"/> while the event is live regardless (ADR-003 D-15), so
/// the two figures legitimately differ — this list is the attachment, not the denominator.
/// </para>
/// </param>
/// <param name="Definitions">
/// Every attached reusable <c>AudienceDefinition</c> (ADR-007 D-69), whatever the event's status. <b>On a
/// terminal event these are the historical record of which definition was invited</b> — like
/// <see cref="Groups"/>, no query resolves them any more; the per-definition counts are current (live)
/// figures for display, the same way <see cref="EventAudienceGroupDto.MemberCount"/> is.
/// </param>
/// <param name="AdvisoryPersonnelCount">
/// The total personnel the attached definitions currently resolve to, de-duplicated across definitions
/// (ADR-007 D-70). <b>These are NOT in <see cref="Expected"/></b> — the denominator, the freeze snapshot
/// and attendance are all student-keyed, so personnel have nowhere to be counted. The figure exists so
/// the exclusion is visible rather than silent: an organizer who attaches a personnel-heavy definition
/// can see what it resolved to even though none of it enters the student denominator.
/// </param>
/// <param name="ExpectedSource">
/// Where <see cref="Expected"/> is resolved from (ADR-008 D-71) — one of
/// <see cref="EAMS.Domain.ExpectedAudienceSource"/>. <c>"PreRegistration"</c> when one or more
/// pre-registration sessions are linked to this event, in which case <see cref="Expected"/> is the
/// linked sessions' deduped student registrants and the attached <see cref="Groups"/>/<see cref="Definitions"/>
/// are kept but not resolved; otherwise <c>"Audience"</c> and both keep their Phase-2 meaning. The signal
/// exists so the FE can explain why attached sections may not be counted.
/// </param>
/// <param name="PreRegistration">
/// The linked pre-registration sessions and their counts (ADR-008 D-71/D-72), or <c>null</c> when no
/// session is linked. When present, <see cref="AdvisoryPersonnelCount"/> reflects the pre-registered
/// personnel (D-72) rather than the Phase-2 definition advisory; when null, the definition advisory stands.
/// </param>
public record EventAudienceDto(
    Guid EventId,
    string Status,
    bool IsFrozen,
    int Expected,
    IReadOnlyList<EventAudienceGroupDto> Groups,
    IReadOnlyList<EventAudienceStudentDto> Students,
    IReadOnlyList<EventAudienceDefinitionRefDto> Definitions,
    int AdvisoryPersonnelCount,
    string ExpectedSource,
    EventPreRegistrationDto? PreRegistration);

/// <summary>
/// The pre-registration sessions linked to an event (ADR-008 D-71/D-73), as the audience read publishes
/// them. Null on <see cref="EventAudienceDto.PreRegistration"/> when nothing is linked.
/// </summary>
/// <param name="TotalPreRegisteredStudentCount">
/// Distinct student registrants across all linked sessions, soft-deleted excluded — a person registered in
/// two linked sessions counts once (a <c>UNION</c>, matching how the denominator dedupes). On a live linked
/// event this equals <see cref="EventAudienceDto.Expected"/>.
/// </param>
/// <param name="TotalAdvisoryPersonnelCount">
/// Distinct personnel registrants across all linked sessions, soft-deleted excluded (D-72). Advisory only —
/// personnel are never in the denominator, the freeze snapshot or attendance, exactly as a definition's
/// personnel are (D-70).
/// </param>
public record EventPreRegistrationDto(
    IReadOnlyList<LinkedPreRegistrationSessionDto> LinkedSessions,
    int TotalPreRegisteredStudentCount,
    int TotalAdvisoryPersonnelCount);

/// <summary>One pre-registration session linked to an event (ADR-008 D-71).</summary>
/// <param name="PreRegisteredStudentCount">
/// This session's distinct student registrants, soft-deleted excluded. The per-session figures can sum
/// higher than <see cref="EventPreRegistrationDto.TotalPreRegisteredStudentCount"/> when a person is in
/// more than one linked session — the total dedupes, these do not.
/// </param>
/// <param name="AdvisoryPersonnelCount">This session's distinct personnel registrants, soft-deleted excluded (advisory only, D-72).</param>
public record LinkedPreRegistrationSessionDto(
    Guid PreRegistrationSessionId,
    string Name,
    int PreRegisteredStudentCount,
    int AdvisoryPersonnelCount);

/// <summary>The body of <c>POST /events/{eventId}/pre-registration/sessions</c> — link an existing session.</summary>
public record LinkPreRegistrationSessionRequest(Guid PreRegistrationSessionId);

/// <summary>
/// The body of <c>POST /events/{eventId}/pre-registration/sessions/from-event</c>. <paramref name="Name"/>
/// is optional; omitted or blank, the session takes a name derived from the event.
/// </summary>
public record CreatePreRegistrationFromEventRequest(string? Name);

/// <summary>
/// One attached reusable <c>AudienceDefinition</c> (ADR-007 D-69), as the audience read publishes it.
/// </summary>
/// <param name="StudentCount">
/// Students the definition currently resolves to in this event's school, soft-deleted excluded — the
/// portion that enters <see cref="EventAudienceDto.Expected"/> (de-duplicated there against the sections
/// and individuals).
/// </param>
/// <param name="PersonnelCount">
/// Personnel the definition currently resolves to — advisory only, never in the denominator (D-70). Its
/// contribution to <see cref="EventAudienceDto.AdvisoryPersonnelCount"/> before cross-definition dedupe.
/// </param>
/// <param name="IsActive">
/// The definition's current active flag. A definition can only be <em>attached</em> while active, but one
/// deactivated afterwards keeps resolving (the link is what counts, like a group row); this surfaces that
/// state for display.
/// </param>
public record EventAudienceDefinitionRefDto(
    Guid AudienceDefinitionId,
    string Name,
    string AudienceType,
    int StudentCount,
    int PersonnelCount,
    bool IsActive);

/// <summary>One attached §4.7 group.</summary>
/// <param name="MemberCount">
/// Members excluding the soft-deleted — <b>the same definition <c>GET /student-groups</c> uses</b>, so
/// the number in a picker and the number in the attached panel agree about one group. It is current
/// membership even on a terminal event: the group row is a historical record of what was invited, but
/// this count describes the group as it is now, and the frozen population is
/// <see cref="EventAudienceDto.Expected"/>.
/// </param>
public record EventAudienceGroupDto(
    Guid StudentGroupId,
    string Name,
    string Type,
    string SourceType,
    Guid? TermId,
    string? TermCode,
    int MemberCount);

/// <summary>One individually-attached student. See <see cref="EventAudienceDto.Students"/>.</summary>
public record EventAudienceStudentDto(
    Guid StudentId,
    string StudentNumber,
    string FullName,
    string? Section);

/// <summary>
/// <c>GET /events/{id}/roster</c> — §6.3's "expected vs present roster" and the source of §12's
/// Absentee Report.
///
/// <para>
/// <b>The header counts and <see cref="Entries"/> always reconcile.</b> The buckets are counted over
/// exactly the rows listed, so an operator adding up the roster gets the header. They also match
/// <c>GET /events/{id}/summary</c>, which counts the same attendance rows.
/// </para>
/// </summary>
/// <param name="Expected">
/// The invited population: every student in an attached group, plus every individually attached
/// student, de-duplicated. This is the denominator §4.5 and §12 mean, and it is <em>not</em> the length
/// of <see cref="Entries"/> — a student who tapped without being invited is listed with
/// <c>IsExpected = false</c> and is not counted here.
///
/// <para>
/// While the event is live the soft-deleted are excluded: a student the roster says does not exist
/// cannot be expected to attend. Once <see cref="IsFrozen"/> they are included, because by then this
/// is a written-down record of who was invited and a student deleted next year must not retroactively
/// shrink a past event's denominator.
/// </para>
/// </param>
/// <param name="NotRecorded">
/// Expected students with no attendance row at all. While the event is live this is the running
/// absentee list; after a <em>close</em> it is zero, because closing materializes every one of them as
/// an <c>Absent</c> record. After a <em>cancellation</em> it is the whole audience, and that is the
/// honest answer: the invitation list was frozen, and nobody attended because the event did not happen.
/// </param>
/// <param name="Unexpected">
/// Entries flagged <c>IsExpected = false</c> — recorded but never invited. The same number
/// <c>GET /events/{id}/summary</c> reports under the same name, so the two views of one event agree
/// about how many walk-ins it had.
/// </param>
/// <param name="IsFrozen">
/// True once this event's audience has been snapshotted — both terminal statuses. The published
/// statement that these numbers can no longer move on their own: a later roster import that adds
/// students to an attached section does not change them.
///
/// <para>
/// <b>It covers <c>Cancelled</c> as well as <c>Closed</c>, and did not always.</b> Cancelling now
/// writes the resolved audience down exactly as closing does — it simply materializes no absentees —
/// so a cancelled event's denominator is as fixed as a closed one's. Reporting <c>false</c> for it
/// would be the same class of plausible-but-wrong published number this phase exists to remove.
/// </para>
/// </param>
public record EventRosterDto(
    Guid EventId,
    string EventName,
    string Status,
    bool IsFrozen,
    int Expected,
    int Present,
    int Late,
    int Absent,
    int Excused,
    int NotRecorded,
    int Unexpected,
    IReadOnlyList<EventRosterEntryDto> Entries);

/// <summary>
/// One line of the roster.
/// </summary>
/// <param name="IsExpected">
/// Whether the event's audience includes this student. False means a walk-in: they have an attendance
/// record but no invitation. Listed rather than hidden, because the alternative is a roster whose rows
/// do not add up to the summary an organizer is looking at beside it.
/// </param>
/// <param name="Status">
/// The §4.9 status, or <c>null</c> for an expected student with no record yet. Null is the absentee
/// signal before a close; there are none after one.
/// </param>
public record EventRosterEntryDto(
    Guid StudentId,
    string StudentNumber,
    string FullName,
    string? Section,
    bool IsExpected,
    string? Status,
    DateTime? CheckInAt,
    DateTime? CheckOutAt,
    string? CaptureMethod);
