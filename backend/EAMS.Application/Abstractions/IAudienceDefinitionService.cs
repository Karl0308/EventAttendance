using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>The outcome of an audience-definition write.</summary>
public enum AudienceWriteOutcome
{
    Saved,
    NotFound,

    /// <summary>A blank/over-length name, an unknown audience type, or criteria that do not fit the type. 400.</summary>
    ValidationFailed,

    /// <summary><c>UX_AudienceDefinitions_SchoolId_NameKey</c> already holds this name. 409.</summary>
    NameExists,

    /// <summary>No school could be resolved. 409.</summary>
    NoSchoolResolved,

    /// <summary>The named event classification does not exist, is retired, or belongs to another school. 409.</summary>
    ClassificationUnavailable,
}

/// <summary>The result of an audience-definition write. Null unless it saved.</summary>
public record AudienceWriteResponse(AudienceWriteOutcome Outcome, string Message, AudienceDefinitionDto? Definition);

/// <summary>
/// <b>The Event Audience master (EventAudience.docx).</b> Reusable audience definitions, each under an
/// event classification, that resolve to eligible attendees from the live Academic Community data.
///
/// <para>
/// Tenant-scoped by the global query filter. Only <b>active</b> event classifications may be assigned to a
/// new or edited definition; existing definitions keep their recorded classification even if it is later
/// retired (spec §8). Wiring a definition into event creation is a follow-on (see <c>AudienceDefinition</c>).
/// </para>
/// </summary>
public interface IAudienceDefinitionService
{
    /// <summary>
    /// <c>GET /event-audiences</c> — the definitions, optionally filtered to one classification and/or to
    /// active only (the event-creation picker passes both).
    /// </summary>
    Task<PagedResult<AudienceDefinitionDto>> ListAsync(
        Guid? eventClassificationId, bool includeInactive, PageRequest page, CancellationToken ct = default);

    /// <summary><c>GET /event-audiences/{id}</c> — one definition, or null.</summary>
    Task<AudienceDefinitionDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary><c>POST /event-audiences</c> — create a definition under an active classification.</summary>
    Task<AudienceWriteResponse> CreateAsync(AudienceDefinitionWriteRequest request, CancellationToken ct = default);

    /// <summary><c>PUT /event-audiences/{id}</c> — a full replacement of the definition's fields.</summary>
    Task<AudienceWriteResponse> UpdateAsync(Guid id, AudienceDefinitionWriteRequest request, CancellationToken ct = default);

    /// <summary><c>PATCH /event-audiences/{id}/active</c> — activate/deactivate. Idempotent.</summary>
    Task<AudienceWriteResponse> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default);

    /// <summary><c>DELETE /event-audiences/{id}</c> — delete a definition. (No event references one yet.)</summary>
    Task<AudienceWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /event-audiences/{id}/attendees</c> — the students and personnel the definition currently
    /// resolves to, from the live roster, or null when the definition does not exist.
    /// </summary>
    Task<ResolvedAudienceDto?> ResolveAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /event-audiences/options</c> — the distinct Academic Community values the criteria pickers
    /// are built from (departments, programmes, year levels, sections, classifications, organizations), so
    /// the form references existing data rather than inventing master data (spec §3/§7).
    /// </summary>
    Task<AudienceOptionsDto> OptionsAsync(CancellationToken ct = default);
}
