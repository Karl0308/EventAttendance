namespace EAMS.Application.Dtos;

/// <summary>
/// The student side of a clearance report (Clearance-Checker-Module.docx) — the info card at the top of
/// the result. No clearance <em>status</em> is computed anywhere: the administrator reads the events and
/// interprets them.
/// </summary>
/// <param name="Department">The student's course/department, from the roster cache (e.g. BSIT).</param>
/// <param name="Program">The programme/course — the same cached value; the spec lists both columns.</param>
/// <param name="College">The college the student's current-term programme belongs to (e.g. CICT), or null
/// when there is no current-term record to resolve it from.</param>
public record ClearanceStudentDto(
    Guid StudentId,
    string StudentNumber,
    string FullName,
    string? Department,
    string? Program,
    string? College,
    string? YearLevel,
    string? Section);

/// <summary>
/// One event on a clearance report, with the student's attendance for it.
/// </summary>
/// <param name="Attendance">
/// <c>Attended</c>, <c>Late</c>, <c>Excused</c> or <c>Missed</c> — mapped from the §4.9 attendance status
/// (Present → Attended, Absent or no record → Missed), which is the vocabulary the spec's report uses.
/// </param>
public record ClearanceEventDto(
    Guid EventId,
    string EventName,
    DateTime EventDate,
    string Attendance,
    DateTime? CheckInAt,
    DateTime? CheckOutAt);

/// <summary>
/// A student's clearance report: the info card and every event they were eligible for, with attendance.
/// </summary>
public record ClearanceReportDto(
    ClearanceStudentDto Student,
    IReadOnlyList<ClearanceEventDto> Events);
