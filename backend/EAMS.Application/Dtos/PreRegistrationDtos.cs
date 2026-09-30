namespace EAMS.Application.Dtos;

/// <summary>One pre-registration session, as the list and by-id reads publish it.</summary>
/// <param name="RegisteredCount">How many attendees are registered — kept in step with the rows.</param>
/// <param name="IsFull"><c>RegisteredCount &gt;= Capacity</c>, precomputed for the counter display.</param>
public record PreRegistrationSessionDto(
    Guid Id,
    string Name,
    Guid AudienceDefinitionId,
    string AudienceName,
    int Capacity,
    int RegisteredCount,
    bool IsClosed,
    bool IsFull);

/// <summary>The body of <c>POST /pre-registration/sessions</c>.</summary>
public record PreRegistrationSessionCreateRequest(string Name, Guid AudienceDefinitionId, int Capacity);

/// <summary>The body of <c>PATCH /pre-registration/sessions/{id}/close</c>.</summary>
public record PreRegistrationCloseRequest(bool? IsClosed);

/// <summary>One pre-registered attendee (PreRegistration.docx: the Pre-Registered Attendees list).</summary>
public record PreRegistrantDto(
    Guid Id,
    string Type,
    Guid AttendeeId,
    string Number,
    string FullName,
    string? RfidUid,
    string? DepartmentOrProgram,
    string Method,
    DateTime RegisteredAt);

/// <summary>
/// The body of <c>POST /pre-registration/sessions/{id}/register</c>.
/// </summary>
/// <param name="Method"><c>Tapped</c> (supply <paramref name="CardUid"/>) or <c>Manual</c> (supply
/// <paramref name="Type"/> and <paramref name="AttendeeId"/>).</param>
/// <param name="CardUid">The scanned card, for a <c>Tapped</c> registration.</param>
/// <param name="Type"><c>Student</c> or <c>Personnel</c>, for a <c>Manual</c> registration.</param>
/// <param name="AttendeeId">The chosen student or personnel id, for a <c>Manual</c> registration.</param>
public record PreRegisterRequest(
    string Method,
    string? CardUid,
    string? Type,
    Guid? AttendeeId);
