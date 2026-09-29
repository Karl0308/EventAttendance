namespace EAMS.Application.Dtos;

/// <summary>
/// One entry in the institution's event-classification vocabulary.
/// </summary>
/// <param name="Id">
/// The stable identity. <b>This is what an event row will hold</b> once the Event Audience module adds
/// the link, so it survives every rename — the reason the vocabulary is a table rather than a string
/// column.
/// </param>
/// <param name="Name">The display form, exactly as authored.</param>
/// <param name="NameKey">
/// The normalized key the uniqueness index is built on (letters and digits, upper-cased). Published so
/// an administrator told "that name is taken" by a row that does not look taken can see why.
/// </param>
/// <param name="Description">The optional description, or null.</param>
/// <param name="IsActive">
/// <c>false</c> is deactivated: not offered for new events, but still held by every event recorded under
/// it. Pickers filter on this; reports must not.
/// </param>
/// <param name="RetiredAt">When it was last deactivated, or null while active.</param>
public record EventClassificationDto(
    Guid Id,
    string Name,
    string NameKey,
    string? Description,
    bool IsActive,
    DateTime? RetiredAt);

/// <summary>
/// The body of <c>POST /event-classifications</c>.
/// </summary>
/// <param name="Name">
/// The display name. Required, 100 characters or fewer, no leading or trailing whitespace (refused
/// rather than trimmed), and it must contain at least one letter or digit so it has a normalized key.
/// </param>
/// <param name="Description">Optional, 1000 characters or fewer.</param>
public record EventClassificationCreateRequest(string Name, string? Description);

/// <summary>
/// The body of <c>PUT /event-classifications/{id}</c> — a full replacement of the two editable fields.
/// </summary>
/// <param name="Name">The new display name. Same rules as on creation.</param>
/// <param name="Description">
/// The new description, or null to clear it. A <c>PUT</c> replaces, so an omitted description clears the
/// stored one — the form always sends what it read back.
/// </param>
public record EventClassificationUpdateRequest(string Name, string? Description);

/// <summary>
/// The body of <c>PATCH /event-classifications/{id}/active</c>.
/// </summary>
/// <param name="IsActive">
/// <b>Nullable, and an omitted member is a 400 rather than a default.</b> A missing JSON member binds to
/// <c>false</c>, and on this route <c>false</c> deactivates a classification — the same reasoning
/// <c>ClassificationActiveRequest.IsActive</c> and <c>PATCH /academic/terms/{id}/current</c> record.
/// </param>
public record EventClassificationActiveRequest(bool? IsActive);
