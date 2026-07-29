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
public record StudentDto(
    Guid Id, string StudentNumber, string FullName,
    string FirstName, string? MiddleName, string LastName,
    string? Email, string? Gender, string? PhotoUrl,
    string? Course, string? YearLevel, string? Section, string Status,
    IEnumerable<CardDto> Cards);

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
public record EventDto(
    Guid Id, string Name, string? Description, string? Location, DateTime StartAt, DateTime EndAt,
    string AttendanceMode, int GraceMinutes, bool RequireRegistration, string Status);

public record AttendanceDto(
    Guid Id, Guid EventId, Guid StudentId, string StudentName, string StudentNumber,
    DateTime? CheckInAt, DateTime? CheckOutAt, string Status, string CaptureMethod);

// POST /attendance/tap — the core capture payload from the Technical Plan §6.4.
public record TapRequest(
    Guid EventId, string CardUid, Guid? DeviceId, string? DeviceTapId, DateTime? TappedAt);

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
