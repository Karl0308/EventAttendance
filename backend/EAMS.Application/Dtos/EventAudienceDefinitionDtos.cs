namespace EAMS.Application.Dtos;

/// <summary>
/// The selection that picks an audience's eligible attendees, interpreted per the definition's
/// <c>AudienceType</c>. All lists are optional; each type reads the ones that apply to it (and
/// <c>Custom</c> combines them). Stored as JSON on the definition — references, never duplicated data.
/// </summary>
/// <param name="Scope">University-wide only: <c>Students</c>, <c>Employees</c> or <c>Both</c>.</param>
public record AudienceCriteriaDto(
    string? Scope = null,
    IReadOnlyList<string>? Departments = null,
    IReadOnlyList<string>? Programs = null,
    IReadOnlyList<string>? YearLevels = null,
    IReadOnlyList<string>? Sections = null,
    IReadOnlyList<string>? Classifications = null,
    IReadOnlyList<string>? Organizations = null,
    IReadOnlyList<Guid>? StudentIds = null,
    IReadOnlyList<Guid>? PersonnelIds = null);

/// <summary>One reusable Event Audience definition, as the list and by-id reads publish it.</summary>
public record AudienceDefinitionDto(
    Guid Id,
    string Name,
    Guid EventClassificationId,
    string EventClassificationName,
    string AudienceType,
    AudienceCriteriaDto Criteria,
    bool IsActive);

/// <summary>The body of <c>POST /event-audiences</c> and <c>PUT /event-audiences/{id}</c>.</summary>
/// <param name="EventClassificationId">Must reference an active event classification in this school.</param>
public record AudienceDefinitionWriteRequest(
    string Name,
    Guid EventClassificationId,
    string AudienceType,
    AudienceCriteriaDto Criteria);

/// <summary>The body of <c>PATCH /event-audiences/{id}/active</c>.</summary>
public record AudienceActiveRequest(bool? IsActive);

/// <summary>
/// The distinct Academic Community values the audience-criteria pickers offer, so the form references
/// existing data rather than duplicating master data (spec §3/§7).
/// </summary>
public record AudienceOptionsDto(
    IReadOnlyList<string> Departments,
    IReadOnlyList<string> Programs,
    IReadOnlyList<string> YearLevels,
    IReadOnlyList<string> Sections,
    IReadOnlyList<string> Classifications,
    IReadOnlyList<string> Organizations);

/// <summary>One resolved attendee of an audience.</summary>
/// <param name="Type"><c>Student</c> or <c>Personnel</c>.</param>
public record AudienceAttendeeDto(
    string Type,
    Guid Id,
    string Number,
    string FullName,
    string? DepartmentOrProgram);

/// <summary>
/// The people an audience currently resolves to (EventAudience.docx: "determines the eligible attendees").
/// </summary>
/// <param name="Attendees">Capped for the wire; <see cref="StudentCount"/>/<see cref="PersonnelCount"/> are the true totals.</param>
public record ResolvedAudienceDto(
    Guid AudienceDefinitionId,
    int StudentCount,
    int PersonnelCount,
    IReadOnlyList<AudienceAttendeeDto> Attendees);
