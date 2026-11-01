namespace EAMS.Application.Dtos;

/// <summary>
/// <c>AttendanceDtoV2.personType</c>'s two values (ADR-015 D-99). Always present on a v2 record.
/// </summary>
public static class AttendeePersonType
{
    public const string Student = "Student";
    public const string Personnel = "Personnel";
}

/// <summary>
/// The v2 attendance record (ADR-015 D-99): every <see cref="AttendanceDto"/> member, in the same order,
/// plus who the attendee is.
///
/// <para>
/// <b>The v1 records are never extended.</b> <see cref="AttendanceDto"/>, <see cref="TapResult"/>,
/// <see cref="TapBatchRowResult"/> and <see cref="TapBatchResult"/> stay byte-identical; a client that did
/// not send <c>X-EAMS-Capabilities: personnel</c> keeps receiving them, and a personnel record can never
/// be expressed in them — the v1 mapper answers <c>CardNotFound</c> instead.
/// </para>
/// </summary>
/// <param name="StudentId">Null on a personnel record.</param>
/// <param name="StudentName">Null on a personnel record.</param>
/// <param name="StudentNumber">Null on a personnel record.</param>
/// <param name="PersonType"><c>Student</c> or <c>Personnel</c>, always present.</param>
/// <param name="PersonnelId">Always present; null on a student record.</param>
/// <param name="PersonnelNumber">Always present; null on a student record.</param>
/// <param name="PersonnelName">Always present; null on a student record.</param>
public record AttendanceDtoV2(
    Guid Id, Guid EventId, Guid? StudentId, string? StudentName, string? StudentNumber,
    DateTime? CheckInAt, DateTime? CheckOutAt, string Status, string CaptureMethod,
    string PersonType, Guid? PersonnelId, string? PersonnelNumber, string? PersonnelName);

/// <summary>The v2 <see cref="TapResult"/>: identical but for the record's type.</summary>
public record TapResultV2(
    bool Success, string Message, AttendanceDtoV2? Record, string Code, DateTime ServerTime);

/// <summary>The v2 <see cref="TapBatchRowResult"/>: identical but for the record's type.</summary>
public record TapBatchRowResultV2(
    int Index, string? DeviceTapId, string Code, int Status, AttendanceDtoV2? Record, string Message);

/// <summary>The v2 <see cref="TapBatchResult"/>: identical but for the rows' type.</summary>
public record TapBatchResultV2(
    int Accepted, int Rejected, DateTime ServerTime, IReadOnlyList<TapBatchRowResultV2> Results);
