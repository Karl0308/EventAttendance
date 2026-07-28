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
public record EventWriteRequest(
    string Name,
    string? Description,
    string? Location,
    DateTime StartAt,
    DateTime EndAt,
    string? AttendanceMode,
    int GraceMinutes,
    bool RequireRegistration);

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
public record EventAudienceRequest(
    IReadOnlyList<Guid>? StudentGroupIds,
    IReadOnlyList<Guid>? StudentIds);

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
public record EventAudienceResultDto(
    Guid EventId,
    int GroupsAttached,
    int StudentsAttached,
    int GroupsAlreadyAttached,
    int StudentsAlreadyAttached,
    int Expected,
    IReadOnlyList<string> Warnings);

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
/// student, de-duplicated, excluding the soft-deleted. This is the denominator §4.5 and §12 mean, and
/// it is <em>not</em> the length of <see cref="Entries"/> — a student who tapped without being invited
/// is listed with <c>IsExpected = false</c> and is not counted here.
/// </param>
/// <param name="NotRecorded">
/// Expected students with no attendance row at all. While the event is live this is the running
/// absentee list; after the close it is zero, because closing materializes every one of them as an
/// <c>Absent</c> record.
/// </param>
/// <param name="IsFrozen">
/// True once the event is <c>Closed</c>. The published statement that these numbers can no longer move
/// on their own — a later roster import that adds students to an attached section does not change them.
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
