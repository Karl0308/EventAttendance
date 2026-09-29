using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// Why the outcome is an enum rather than an exception, and why every member maps to exactly one HTTP
/// status in the controller: the same reasoning <see cref="ClassificationWriteOutcome"/> and
/// <see cref="TermWriteOutcome"/> record. The service owns the decision; the controller owns only the
/// translation.
/// </summary>
public enum EventClassificationWriteOutcome
{
    /// <summary>The write happened, or the request asked for a state the row was already in.</summary>
    Saved,

    /// <summary>No event classification with that id in this tenant. 404.</summary>
    NotFound,

    /// <summary>
    /// The payload is wrong under every circumstance — a blank, over-length or whitespace-padded name,
    /// a name with no letter or digit, an over-length description, or an omitted <c>isActive</c>. 400,
    /// and the message names the field.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// <c>UX_EventClassifications_SchoolId_NameKey</c> already holds this name's normalized key in this
    /// school. 409 — including when the collision is only discovered by the index, because two concurrent
    /// creates of one name is a conflict and not a server fault.
    /// </summary>
    NameExists,

    /// <summary>
    /// A new classification cannot be filed against a school. 409. The same refusal
    /// <c>TermAdminService</c>, <c>StudentService</c> and <c>ClassificationService</c> make.
    /// </summary>
    NoSchoolResolved,

    /// <summary>
    /// <b>The delete was refused because an event still references the classification.</b> 409, and this
    /// member is the spec's "a classification already used by an existing event should not be deleted
    /// without appropriate validation" in one word.
    ///
    /// <para>
    /// A delete that cascaded or nulled the referring column would be a data-loss migration wearing a
    /// CRUD costume. The row survives and the caller is told to deactivate it instead
    /// (<c>PATCH /event-classifications/{id}/active</c>), which keeps every event's recorded classification.
    /// </para>
    /// </summary>
    InUse,

    /// <summary>
    /// <b>The delete was refused because the startup seed would put the row straight back.</b> 409, and
    /// the caller is told to deactivate it instead. Deliberately not <see cref="InUse"/>: nothing
    /// references a seeded row an administrator wants gone, so "still in use" would send them looking for
    /// a referrer that does not exist.
    /// </summary>
    SeedProtected,
}

/// <summary>The result of a write against one event classification. Null unless it saved.</summary>
public record EventClassificationWriteResponse(
    EventClassificationWriteOutcome Outcome, string Message, EventClassificationDto? Classification);

/// <summary>
/// <b>The administrator-owned vocabulary of event classifications — Institutional, Departmental,
/// Organizational, and whatever the client adds.</b>
///
/// <para>
/// <b>This interface is the vocabulary and nothing else.</b> How an <em>event</em> selects a
/// classification, and the "classification then audience" event-creation flow the spec describes, land
/// with the Event Audience module that depends on this one — the same shipping order
/// <see cref="IClassificationService"/> follows for person assignment. The vocabulary is correct under
/// that later shape, which is why it ships first.
/// </para>
///
/// <para>
/// <b>Three operations keep data and one can destroy it, so only one is guarded.</b> Renaming/editing
/// (<see cref="UpdateAsync"/>) touches one row and no event, because identity is the GUID. Deactivating
/// (<see cref="SetActiveAsync"/>) takes the row out of the picker and leaves every event standing.
/// <see cref="DeleteAsync"/> is the only one that removes a row, and it is refused while an event
/// references it or while it is one of the seeded three.
/// </para>
/// </summary>
public interface IEventClassificationService
{
    /// <summary>
    /// <c>GET /event-classifications</c> — the vocabulary, active entries first and then by name.
    /// </summary>
    /// <param name="includeInactive">
    /// <c>false</c> — the default a picker wants — returns only classifications still offered for new
    /// events. <c>true</c> returns deactivated rows as well, which is what an administration screen and
    /// any historical report need.
    /// </param>
    /// <param name="page">Paged like every other admin list read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<EventClassificationDto>> ListAsync(
        bool includeInactive, PageRequest page, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /event-classifications/{id}</c> — one entry, or null when this tenant has no such row.
    /// </summary>
    Task<EventClassificationDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /event-classifications</c> — add a classification to the resolved tenant's vocabulary,
    /// always created active.
    /// </summary>
    Task<EventClassificationWriteResponse> CreateAsync(
        EventClassificationCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PUT /event-classifications/{id}</c> — change the display name and/or description.
    ///
    /// <para>
    /// A rename onto a name whose normalized key another classification in the school already holds is
    /// <see cref="EventClassificationWriteOutcome.NameExists"/>, decided by
    /// <c>UX_EventClassifications_SchoolId_NameKey</c> rather than by this method remembering to check.
    /// A deactivated classification can still be renamed — the events recorded under it must be described
    /// correctly in a report — and renaming does not bring it back into the picker.
    /// </para>
    /// </summary>
    Task<EventClassificationWriteResponse> UpdateAsync(
        Guid id, EventClassificationUpdateRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PATCH /event-classifications/{id}/active</c> — deactivate a classification, or bring it back.
    /// Idempotent. Deactivating stops it being offered for new events and leaves every recorded event
    /// untouched.
    /// </summary>
    Task<EventClassificationWriteResponse> SetActiveAsync(
        Guid id, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// <c>DELETE /event-classifications/{id}</c> — <b>a guarded hard delete: it removes the row only when
    /// no event references it and it is not one of the seeded three, and refuses otherwise.</b>
    ///
    /// <para>
    /// <b>Why a hard delete exists at all.</b> A classification typo'd into the vocabulary thirty seconds
    /// ago has no event behind it, and deactivating it would leave permanent clutter in the picker. The
    /// unreferenced, non-seeded case is the only case where deleting destroys nothing, so it is the only
    /// case allowed. Every foreign key in this model is <c>DeleteBehavior.Restrict</c>, so a referrer this
    /// service has not been taught about produces a loud constraint violation rather than a silent orphan.
    /// </para>
    /// </summary>
    Task<EventClassificationWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default);
}
