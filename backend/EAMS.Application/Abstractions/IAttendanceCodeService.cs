using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>The outcome of an attendance-code operation.</summary>
public enum AttendanceCodeOutcome
{
    Ok,

    /// <summary>No such event in this school. 404.</summary>
    EventNotFound,

    /// <summary><c>Selected</c> with no ids, or an unknown email mode. 400.</summary>
    ValidationFailed,
}

/// <summary>The result of a list or generate read — the codes, plus a tally for generate.</summary>
public record AttendanceCodeListResult(
    AttendanceCodeOutcome Outcome,
    IReadOnlyList<AttendanceCodeDto> Codes);

/// <summary>The result of a generate — the full code list, plus what the run did to reach it.</summary>
/// <param name="Created">Attendees that had no code and now do.</param>
/// <param name="Regenerated">Existing codes replaced (only when <c>regenerate</c> was asked).</param>
/// <param name="AlreadyHad">Attendees skipped because they already held a code (non-regenerate runs).</param>
public record AttendanceCodeGenerateResult(
    AttendanceCodeOutcome Outcome,
    int Created,
    int Regenerated,
    int AlreadyHad,
    IReadOnlyList<AttendanceCodeDto> Codes);

/// <summary>The result of an email send.</summary>
public record AttendanceCodeEmailOutcome(
    AttendanceCodeOutcome Outcome,
    string Message,
    AttendanceCodeEmailResultDto? Result);

/// <summary>
/// <b>The Live Attendance attendance-code surface (LiveAttendance.docx §3–§5).</b> Generates a unique code
/// per attendee per event, lists them for the Live Attendance screen, and emails them to attendees who have
/// an address on file.
///
/// <para>
/// An <b>attendee</b> is a student with an attendance record for the event — the people Live Attendance
/// lists. Codes are keyed on the student, so a <c>TimeInOut</c> event's two taps share one code.
/// </para>
/// </summary>
public interface IAttendanceCodeService
{
    /// <summary><c>GET /events/{eventId}/attendance-codes</c> — the codes issued for this event.</summary>
    Task<AttendanceCodeListResult> ListAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /events/{eventId}/attendance-codes/generate</c> — issue a code to every attendee that lacks
    /// one. Idempotent unless <paramref name="regenerate"/> is set, which rewrites every attendee's code
    /// (§5's "deliberate regeneration process").
    /// </summary>
    Task<AttendanceCodeGenerateResult> GenerateAsync(
        Guid eventId, bool regenerate, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /events/{eventId}/attendance-codes/email</c> — email the codes to <c>All</c> eligible
    /// attendees or to a <c>Selected</c> subset. Attendees with no address or no code are skipped and
    /// tallied rather than failing the whole send.
    /// </summary>
    Task<AttendanceCodeEmailOutcome> EmailAsync(
        Guid eventId, AttendanceCodeEmailRequest request, CancellationToken ct = default);
}
