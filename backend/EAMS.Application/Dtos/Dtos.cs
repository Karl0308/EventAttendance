namespace EAMS.Application.Dtos;

public record StudentDto(
    Guid Id, string StudentNumber, string FullName, string? Email,
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

public record TapResult(
    bool Success, string Message, AttendanceDto? Record);

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
/// <param name="AttendanceRate">
/// <c>(Present + Late) / Expected</c>, as a percentage to one decimal place; zero when
/// <paramref name="Expected"/> is zero.
///
/// <para>
/// <b>It can exceed 100</b>, when more people tapped than were invited, and it is left uncapped on
/// purpose. A rate over 100 is a self-describing signal that the audience is wrong or incomplete, and
/// the roster names the walk-ins responsible; capping it, or narrowing the numerator to the invited
/// students, would hide exactly that.
/// </para>
/// </param>
public record EventSummaryDto(
    Guid EventId, string EventName, int Expected, int Present, int Late,
    int Absent, int Excused, double AttendanceRate);
