namespace EAMS.Api.Dtos;

public record StudentDto(
    Guid Id, string StudentNumber, string FullName, string? Email,
    string? Course, string? YearLevel, string? Section, string Status,
    IEnumerable<CardDto> Cards);

public record CardDto(Guid Id, string CardUid, string? Label, bool IsActive);

public record EventDto(
    Guid Id, string Name, string? Location, DateTime StartAt, DateTime EndAt,
    string AttendanceMode, int GraceMinutes, string Status);

public record AttendanceDto(
    Guid Id, Guid EventId, Guid StudentId, string StudentName, string StudentNumber,
    DateTime? CheckInAt, DateTime? CheckOutAt, string Status, string CaptureMethod);

// POST /attendance/tap — the core capture payload from the Technical Plan §6.4.
public record TapRequest(
    Guid EventId, string CardUid, Guid? DeviceId, string? DeviceTapId, DateTime? TappedAt);

public record TapResult(
    bool Success, string Message, AttendanceDto? Record);

public record EventSummaryDto(
    Guid EventId, string EventName, int Expected, int Present, int Late,
    int Absent, int Excused, double AttendanceRate);
