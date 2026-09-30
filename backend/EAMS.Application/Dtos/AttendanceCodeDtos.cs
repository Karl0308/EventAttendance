namespace EAMS.Application.Dtos;

/// <summary>One attendee's attendance code, as the list and generate reads publish it.</summary>
public record AttendanceCodeDto(
    Guid Id,
    Guid StudentId,
    string StudentNumber,
    string StudentName,
    string? Email,
    string Code,
    DateTime? LastEmailedAt,
    int EmailCount);

/// <summary>
/// The body of <c>POST /events/{eventId}/attendance-codes/email</c>.
/// </summary>
/// <param name="Mode"><c>All</c> emails every attendee with a code and an address; <c>Selected</c> emails
/// only <paramref name="StudentIds"/>.</param>
/// <param name="StudentIds">Required and non-empty when <paramref name="Mode"/> is <c>Selected</c>; ignored
/// otherwise.</param>
public record AttendanceCodeEmailRequest(string Mode, IReadOnlyList<Guid>? StudentIds);

/// <summary>
/// The outcome of an email send — a per-recipient tally rather than a single boolean, because §4/§5 ask the
/// screen to distinguish who was sent to from who was skipped and why.
/// </summary>
/// <param name="Requested">How many attendees the request targeted.</param>
/// <param name="Sent">How many emails were actually sent.</param>
/// <param name="SkippedNoEmail">Targeted attendees with no email address on file.</param>
/// <param name="SkippedNoCode">Targeted attendees that have no code generated yet.</param>
/// <param name="Failed">Targeted attendees whose send threw.</param>
public record AttendanceCodeEmailResultDto(
    int Requested,
    int Sent,
    int SkippedNoEmail,
    int SkippedNoCode,
    int Failed);
