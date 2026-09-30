namespace EAMS.Application.Dtos;

/// <summary>
/// Technical Plan §4.3, as a client sees it.
///
/// <para>
/// <b>The name parts and <c>Gender</c>/<c>PhotoUrl</c> arrived with the §6.2 write surface</b>, and
/// they are not decoration — they are what makes <c>PUT /students/{id}</c> usable at all. The PUT is a
/// full replacement of a student's own fields, so a field a client cannot <em>read</em> is a field it
/// cannot send back, and the round trip would blank it. <c>FullName</c> is computed and cannot be
/// split back into three columns, so an edit form had no way to populate itself. Additive on the wire;
/// existing consumers ignore them. Same reasoning <see cref="EventDto"/> records for
/// <c>Description</c>.
/// </para>
/// </summary>
/// <param name="Course">
/// <b>Read-only. ADR-001 D-2 derived cache.</b> Present because the students grid renders and filters
/// on it. Sending it back on a write is refused with <c>FieldIsDerived</c> rather than ignored — a
/// caller that echoes the object it read would otherwise believe it had saved a section.
/// </param>
/// <param name="YearLevel"><inheritdoc cref="Course"/></param>
/// <param name="Section"><inheritdoc cref="Course"/></param>
/// <param name="Classifications">
/// <b>What this person is classified as — a collection, because a person holds one classification per
/// axis and may hold several axes at once</b> (QA Q2). Ordered by axis then display name, so the same
/// person's categories never reshuffle between requests.
///
/// <para>
/// <b>Empty is an ordinary state, not a null to guard against.</b> Thirty-four people in the sampled
/// roster carry no category at all, most carry exactly one, and three carry two. A scalar field could
/// have described only the middle case — answering the two-axis people with one of their two values,
/// non-empty and plausible and wrong, which is the failure ADR-001 D-2 documents for
/// <paramref name="Course"/> and <paramref name="Section"/> right above.
/// </para>
///
/// <para>
/// <b>It is populated on every read that returns a student, and that is deliberate rather than
/// thorough.</b> A field that came back <c>[]</c> on the detail read while the grid showed two
/// categories would be the same silently-wrong answer in a new place — and worse here, because the edit
/// form opens from a grid row and would save the empty version back.
/// </para>
///
/// <para>
/// Read-only on this DTO. Assignment is
/// <c>PUT /students/{studentId}/classifications/{classificationId}</c>; sending this collection back on
/// a <c>PUT /students/{id}</c> is ignored exactly as <c>id</c> and <c>cards</c> are, because a
/// classification is not a column of the student row.
/// </para>
/// </param>
public record StudentDto(
    Guid Id, string StudentNumber, string FullName,
    string FirstName, string? MiddleName, string LastName,
    string? Email, string? Gender, string? PhotoUrl,
    string? Course, string? YearLevel, string? Section, string Status,
    IEnumerable<CardDto> Cards,
    IReadOnlyList<StudentClassificationDto> Classifications);

public record CardDto(Guid Id, string CardUid, string? Label, bool IsActive);

/// <summary>
/// Technical Plan §4.5, as a client sees it.
///
/// <para>
/// <c>Description</c> and <c>RequireRegistration</c> were added with the §6.3 write surface. They are
/// not decoration: <c>PUT /events/{id}</c> is a full replacement, so a client that cannot read a field
/// it is about to send back has no way to preserve it — the round trip would blank the description of
/// every event edited through the UI. Additive on the wire; existing consumers ignore them.
/// </para>
/// </summary>
/// <param name="IssuesCertificates">
/// Whether this event issues certificates of attendance. A setting only — no certificate is produced
/// anywhere yet. Always present, never null: an event created before the setting existed reads
/// <c>false</c>.
///
/// <para>
/// <b>Read it before a <c>PUT</c> if you intend to change it; you do not need it to preserve it.</b>
/// Omitting <c>issuesCertificates</c> from a <c>PUT /events/{id}</c> body keeps the stored value, so a
/// client that has never heard of this field cannot clear it. Also served to devices on
/// <c>GET /events</c>, where it is informational — it plays no part in capture.
/// </para>
/// </param>
/// <param name="GraceBeforeStartMinutes">
/// Per-event grace before start (EventGracePeriod.docx §1), or null for "no per-event limit". Read to
/// preserve on a <c>PUT</c> — the write surface is a full replacement.
/// </param>
/// <param name="GraceAfterEndMinutes">Per-event grace after end (§3), or null. As above.</param>
public record EventDto(
    Guid Id, string Name, string? Description, string? Location, DateTime StartAt, DateTime EndAt,
    string AttendanceMode, int GraceMinutes, bool RequireRegistration, string Status,
    bool IssuesCertificates, int? GraceBeforeStartMinutes, int? GraceAfterEndMinutes);

public record AttendanceDto(
    Guid Id, Guid EventId, Guid StudentId, string StudentName, string StudentNumber,
    DateTime? CheckInAt, DateTime? CheckOutAt, string Status, string CaptureMethod);

// POST /attendance/tap — the core capture payload from the Technical Plan §6.4.
/// <param name="LocalOutcome">
/// What the device concluded about this card from its own cached manifest, before it knew what the
/// server would say. Optional; omit it and nothing changes.
///
/// <para>
/// <b>It never influences the outcome, and a test pins that.</b> The server resolves the card itself
/// and rules on the tap exactly as it would have without this field. A device that lied — or that is
/// simply running a stale manifest — cannot mark anyone present, absent, or unknown by saying so.
/// </para>
///
/// <para>
/// <b>Its purpose is the disagreement.</b> A device that says it found a card the server cannot
/// resolve is evidence of something real: a stale cache, a sync that did not run, a cloned card, a
/// reader misreading. That case leaves no trace otherwise, because <c>CardNotFound</c> is a rejection
/// rather than a row, and it is the case most worth seeing.
/// </para>
///
/// <para>
/// <b>Free text, stored verbatim, never parsed into a closed set.</b> The vocabulary is the mobile
/// client's and is not fixed yet. Validating it would mean a value we have not seen could 400 an
/// entire flush — a field that exists only for reporting must never be able to reject attendance.
/// Anything longer than <c>TapRequestLimits.MaxLocalOutcomeLength</c> is truncated rather than
/// refused, for the same reason.
/// </para>
/// </param>
public record TapRequest(
    Guid EventId, string CardUid, Guid? DeviceId, string? DeviceTapId, DateTime? TappedAt,
    string? LocalOutcome = null);

/// <summary>Bounds on the free-text fields of <see cref="TapRequest"/>.</summary>
public static class TapRequestLimits
{
    /// <summary>
    /// How much of <see cref="TapRequest.LocalOutcome"/> is kept.
    ///
    /// <para>
    /// Long enough for any word a client would sensibly send, short enough that a device cannot write
    /// a megabyte into the audit trail one tap at a time. Truncated rather than rejected: this field
    /// is reporting, and reporting must not be able to fail a capture.
    /// </para>
    /// </summary>
    public const int MaxLocalOutcomeLength = 64;
}

/// <summary>
/// The body of <c>POST /attendance/tap</c> and <c>POST /attendance/manual</c> on every path that
/// produced a record. Failures carry the same two machine-readable fields in an RFC 7807 body instead
/// — see <c>AttendanceController</c>.
/// </summary>
/// <param name="Message">
/// <b>Prose. Never parse it.</b> It is written for a person reading a log or a kiosk screen and is
/// reworded whenever the wording is wrong; <paramref name="Code"/> is what a client branches on.
/// </param>
/// <param name="Code">
/// The stable outcome token — <c>nameof</c> the <see cref="EAMS.Application.Abstractions.TapOutcome"/>
/// or <see cref="EAMS.Application.Abstractions.ManualOutcome"/> member this response carries (Phase 4c,
/// D-37).
///
/// <para>
/// <b>Why the field is <c>code</c> and not <c>outcome</c>.</b> A failure is an RFC 7807 problem body,
/// which already carries <c>code</c> everywhere else in this API (<c>StudentsController</c>,
/// <c>DevicesController</c>, the device-key handler, the rate limiter). Naming the success field the
/// same thing means one accessor — <c>body.code</c> — works across success and failure alike, which is
/// the whole of what the mobile client asked for: today all four tap successes are an
/// indistinguishable 200 whose only differentiator is the prose above, so reconciling a flushed queue
/// against what actually landed is impossible.
/// </para>
///
/// <para>
/// <b>These token names are published contract.</b> Renaming <c>DuplicateIgnored</c> is a breaking
/// change for a consumer we cannot recompile, so <c>TapOutcomeContractTests</c> enumerates the enum
/// against a frozen list and fails the build rather than letting a rename ship quietly.
/// </para>
/// </param>
/// <param name="ServerTime">
/// The server's UTC clock at the moment this response was produced. The published contract promises it
/// on every response and the client computes a clock offset from it before enqueueing — which is the
/// half of D-36 that lets a drifted device correct itself instead of having its taps refused. It is on
/// rejections too, and most of all on <c>TappedAtOutOfRange</c>: that is precisely the response whose
/// reader needs to know what time we think it is.
/// </param>
public record TapResult(
    bool Success, string Message, AttendanceDto? Record, string Code, DateTime ServerTime);

/// <summary>
/// Technical Plan §6.7/§12 — the Event Attendance Summary.
/// </summary>
/// <param name="Expected">
/// The invited population: students in the event's attached §4.8 groups plus its individually attached
/// students, de-duplicated, excluding the soft-deleted.
///
/// <para>
/// <b>This used to be the recorded-row count</b>, which made the rate structurally incapable of falling
/// below the share of rows somebody had marked <c>Absent</c> by hand, and made an absentee
/// unrepresentable: a student who never taps has no row, so they were not counted as expected either.
/// A number that was always about 100% and meant nothing.
/// </para>
///
/// <para>
/// <b>Zero means no audience is attached</b>, not "nobody was invited to a well-defined event". Nothing
/// falls back to the old count in that case — a denominator that silently changes definition depending
/// on whether a table is empty is worse than one that is honestly absent. <c>GET /events/{id}/roster</c>
/// is where to look: it lists whoever <em>did</em> tap, each flagged <c>isExpected: false</c>.
/// </para>
/// </param>
/// <param name="Present">
/// Counted over every attendance row on the event, including any belonging to a student the audience
/// does not name. That is deliberate: these four must reconcile with
/// <c>GET /attendance?eventId=</c> and with the roster, or the summary becomes a fifth opinion.
/// </param>
/// <param name="Unexpected">
/// Attendees who were recorded but never invited — walk-ins. Counted as <em>people</em>, so it is
/// exactly the number of <c>GET /events/{id}/roster</c> entries flagged <c>isExpected: false</c>.
///
/// <para>
/// <b>This is where the over-100% signal went, and it is a better one than the rate was.</b>
/// <paramref name="AttendanceRate"/> used to divide every Present and Late row — walk-ins included —
/// by a denominator that counted only the invited, so twenty-nine expected, twenty-nine present and
/// two tapping alumni read 106.9%. Every underlying row was truthful and the headline number was
/// impossible. Folding the walk-ins into the rate was the wrong place to surface them: it corrupted
/// the one number an operator reads first, and it said "something is off" without saying how many or
/// who. Counting them here says both, and the roster names them.
/// </para>
/// </param>
/// <param name="AttendanceRate">
/// The share of the <em>invited</em> who turned up: invited students with a <c>Present</c> or
/// <c>Late</c> record, over <paramref name="Expected"/>, as a percentage to one decimal place. Zero
/// when <paramref name="Expected"/> is zero.
///
/// <para>
/// <b>It cannot exceed 100, structurally rather than by clamping.</b> Numerator and denominator are
/// both computed over the expected set — the numerator as a SQL <c>INTERSECT</c> against it, which is
/// distinct on both sides — so the numerator is a subset of the denominator by construction. No cap is
/// applied and none is needed; a clamp would have hidden the arithmetic rather than fixed it.
/// </para>
///
/// <para>
/// Walk-ins are therefore <em>not</em> in this number. They are not discarded: see
/// <paramref name="Unexpected"/>, and note that <paramref name="Present"/> and
/// <paramref name="Late"/> still count every row on the event, so the buckets keep reconciling with
/// <c>GET /attendance?eventId=</c> while the rate answers the question it is named for.
/// </para>
/// </param>
public record EventSummaryDto(
    Guid EventId, string EventName, int Expected, int Present, int Late,
    int Absent, int Excused, int Unexpected, double AttendanceRate);
