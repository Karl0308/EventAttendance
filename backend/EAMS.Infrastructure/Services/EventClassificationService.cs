using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// The administrator-owned event-classification vocabulary — see <see cref="IEventClassificationService"/>
/// for why it is a table rather than a string column, and why the event link is deliberately not here.
///
/// <para>
/// Modelled on <see cref="ClassificationService"/> and simpler: no axis, no merge. What it keeps is that
/// class's two load-bearing habits — <c>UX_EventClassifications_SchoolId_NameKey</c> decides the
/// duplicate question and is caught behind the pre-check so it never surfaces as a 500, and every
/// statement whose result set could span schools carries an explicit <c>SchoolId</c> predicate rather
/// than leaning on the global query filter, which is inert whenever no tenant is pinned.
/// </para>
/// </summary>
internal sealed class EventClassificationService : IEventClassificationService
{
    private readonly EamsDbContext _db;

    /// <summary>
    /// Which tenant a newly created classification is filed under, through
    /// <see cref="SchoolResolution.ResolveSchoolIdAsync"/> so this agrees with the tenant the startup log
    /// announced rather than inventing a second answer.
    /// </summary>
    private readonly ISchoolContext _school;

    /// <summary>Where the one thing this service recovers from is recorded: a write that lost the
    /// duplicate-name race to the index.</summary>
    private readonly ILogger<EventClassificationService> _logger;

    public EventClassificationService(
        EamsDbContext db, ISchoolContext school, ILogger<EventClassificationService> logger)
    {
        _db = db;
        _school = school;
        _logger = logger;
    }

    private static EventClassificationDto ToDto(EventClassification c) => new(
        c.Id, c.Name, c.NameKey, c.Description, c.IsActive, c.RetiredAt);

    // ---------------------------------------------------------------------------------------- reads

    public async Task<PagedResult<EventClassificationDto>> ListAsync(
        bool includeInactive, PageRequest page, CancellationToken ct = default)
    {
        var filtered = includeInactive
            ? _db.EventClassifications.AsNoTracking()
            : _db.EventClassifications.AsNoTracking().Where(c => c.IsActive);

        // Active first, then by name, then by Id so the order is total — an OFFSET/FETCH over a
        // non-total order silently repeats one row and skips another between pages, which is what
        // PaginationTests.Every_paged_list_query_orders_by_a_unique_column enforces.
        return await filtered.ToPageAsync(
            q => q
                .OrderByDescending(c => c.IsActive)
                .ThenBy(c => c.Name)
                .ThenBy(c => c.Id)
                .Select(c => new EventClassificationDto(
                    c.Id, c.Name, c.NameKey, c.Description, c.IsActive, c.RetiredAt)),
            page,
            ct);
    }

    public async Task<EventClassificationDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.EventClassifications.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row is null ? null : ToDto(row);
    }

    // --------------------------------------------------------------------------------- create/edit

    public async Task<EventClassificationWriteResponse> CreateAsync(
        EventClassificationCreateRequest request, CancellationToken ct = default)
    {
        if (Validate(request.Name, request.Description) is { } refused) return refused;

        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null) return NoSchool();

        var key = EventClassificationText.KeyFor(request.Name);

        if (await TakenKeyAsync(schoolId.Value, key, excluding: null, ct) is { } taken) return taken;

        var row = new EventClassification
        {
            SchoolId = schoolId.Value,
            Name = request.Name,
            NameKey = key,
            Description = request.Description,
            IsActive = true,
        };

        _db.EventClassifications.Add(row);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // Detached first because EF leaves a failed insert Added, and a retained Added row would
            // shadow every later read of that key through identity resolution — the same detach
            // ClassificationService and TermAdminService both perform.
            _db.Entry(row).State = EntityState.Detached;

            _logger.LogInformation(
                "A create of event classification '{Name}' (key {NameKey}) lost the race to " +
                "UX_EventClassifications_SchoolId_NameKey and was answered 409.", request.Name, key);

            return Duplicate(request.Name, key);
        }

        return new EventClassificationWriteResponse(
            EventClassificationWriteOutcome.Saved,
            "Event classification created, and active. Deactivate it with " +
            "PATCH /event-classifications/{id}/active if it should not be offered for new events.",
            ToDto(row));
    }

    public async Task<EventClassificationWriteResponse> UpdateAsync(
        Guid id, EventClassificationUpdateRequest request, CancellationToken ct = default)
    {
        if (Validate(request.Name, request.Description) is { } refused) return refused;

        var row = await _db.EventClassifications.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound();

        var key = EventClassificationText.KeyFor(request.Name);

        // Only when the key actually moved. A PUT that re-sends the row's own name is the ordinary case
        // (the form round-trips every field), and checking it against the index would report the row as
        // its own duplicate.
        if (!string.Equals(key, row.NameKey, StringComparison.Ordinal)
            && await TakenKeyAsync(row.SchoolId, key, excluding: row.Id, ct) is { } taken)
        {
            return taken;
        }

        row.Name = request.Name;
        row.NameKey = key;
        row.Description = request.Description;
        row.UpdatedAt = DateTime.UtcNow;

        // IsActive and RetiredAt are deliberately untouched: editing a name must not bring a deactivated
        // classification back into the picker as a side effect — that is SetActiveAsync.

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            _logger.LogInformation(
                "An update of event classification {Id} onto '{Name}' (key {NameKey}) lost the race to " +
                "UX_EventClassifications_SchoolId_NameKey and was answered 409.", row.Id, request.Name, key);

            return Duplicate(request.Name, key);
        }

        return new EventClassificationWriteResponse(
            EventClassificationWriteOutcome.Saved,
            "Event classification updated. No event was reclassified: an event holds this by id.",
            ToDto(row));
    }

    // ------------------------------------------------------------------------------------ deactivate

    public async Task<EventClassificationWriteResponse> SetActiveAsync(
        Guid id, bool isActive, CancellationToken ct = default)
    {
        var row = await _db.EventClassifications.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound();

        if (row.IsActive == isActive)
        {
            // Idempotent, and short-circuited rather than re-written: bumping UpdatedAt on a no-op would
            // make an audit column record clicks rather than edits.
            return new EventClassificationWriteResponse(
                EventClassificationWriteOutcome.Saved,
                isActive
                    ? "That event classification was already active."
                    : "That event classification was already deactivated.",
                ToDto(row));
        }

        row.IsActive = isActive;
        row.RetiredAt = isActive ? null : DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return new EventClassificationWriteResponse(
            EventClassificationWriteOutcome.Saved,
            isActive
                ? "Event classification reactivated. It is offered for new events again."
                : "Event classification deactivated. It is no longer offered for new events, and every " +
                  "event already recorded under it keeps it.",
            ToDto(row));
    }

    // -------------------------------------------------------------------------------- guarded delete

    public async Task<EventClassificationWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.EventClassifications.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound();

        // A SEEDED NAME IS DEACTIVATED, NOT DELETED — the startup seed re-adds a missing one on the next
        // start (SeedData.SeedEventClassificationsAsync guards per NameKey), so a delete would not stick
        // and the row would reappear in the picker with nothing to explain it. Same reasoning, and the
        // same shared IsSeededKey list, as ClassificationService.DeleteAsync.
        if (EventClassificationSeedValues.IsSeededKey(row.NameKey))
        {
            return new EventClassificationWriteResponse(
                EventClassificationWriteOutcome.SeedProtected,
                $"'{row.Name}' is one of the {EventClassificationSeedValues.All.Count} classifications " +
                "this installation seeds, so deleting it would not stick: the next start finds the value " +
                "missing and adds it back. Deactivate it instead " +
                $"(PATCH /event-classifications/{row.Id}/active with isActive false) — a deactivated " +
                "classification is withdrawn from the picker and accepts no new events, and the seed " +
                "leaves it alone, so it stays gone.",
                ToDto(row));
        }

        _db.EventClassifications.Remove(row);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsConstraintConflict(ex))
        {
            // An event references this classification. The FK is Restrict, so the database refuses the
            // delete rather than orphaning or cascading — the belt-and-braces half of the spec's
            // "a classification used by an event must not be deleted without validation". (The Events
            // → EventClassification link lands with the Event Audience module; this catch is what keeps
            // the guard correct the moment it does, whether or not this service was taught to pre-check.)
            _db.Entry(row).State = EntityState.Detached;

            _logger.LogInformation(
                "A delete of event classification {Id} ('{Name}') was refused by a foreign key: an " +
                "event references it. Answered 409.", row.Id, row.Name);

            return InUse(row);
        }

        return new EventClassificationWriteResponse(
            EventClassificationWriteOutcome.Saved,
            $"Event classification '{row.Name}' was deleted. No event referenced it, which is the only " +
            "circumstance in which deleting one destroys nothing.",
            ToDto(row));
    }

    // --------------------------------------------------------------------------------------- plumbing

    private static EventClassificationWriteResponse? Validate(string name, string? description)
    {
        if (!EventClassificationText.IsValidName(name))
        {
            return new EventClassificationWriteResponse(
                EventClassificationWriteOutcome.ValidationFailed,
                $"Name is required, must be {EventClassificationText.NameMaxLength} characters or fewer, " +
                "may not start or end with whitespace, and must contain at least one letter or digit. " +
                "Two names that differ only in punctuation, spacing or case are the same classification " +
                "and the second is refused.",
                null);
        }

        if (!EventClassificationText.IsValidDescription(description))
        {
            return new EventClassificationWriteResponse(
                EventClassificationWriteOutcome.ValidationFailed,
                $"Description must be {EventClassificationText.DescriptionMaxLength} characters or fewer.",
                null);
        }

        return null;
    }

    /// <summary>
    /// The duplicate-name pre-check. A courtesy, not the guard: the guard is
    /// <c>UX_EventClassifications_SchoolId_NameKey</c>, and both write paths catch its violation as well.
    /// </summary>
    private async Task<EventClassificationWriteResponse?> TakenKeyAsync(
        Guid schoolId, string nameKey, Guid? excluding, CancellationToken ct)
    {
        var clash = await _db.EventClassifications.AsNoTracking()
            .Where(c => c.SchoolId == schoolId && c.NameKey == nameKey)
            .Where(c => excluding == null || c.Id != excluding)
            .Select(c => new { Id = (Guid?)c.Id, c.Name, c.IsActive })
            .FirstOrDefaultAsync(ct);

        if (clash is null) return null;

        var held = clash.IsActive
            ? $"'{clash.Name}'"
            : $"'{clash.Name}', which is deactivated — reactivate it with " +
              $"PATCH /event-classifications/{clash.Id}/active rather than creating a second row";

        return new EventClassificationWriteResponse(
            EventClassificationWriteOutcome.NameExists,
            $"This school already has an event classification with the key '{nameKey}': {held}. Names " +
            "are compared with punctuation, spacing and case removed.",
            null);
    }

    private static EventClassificationWriteResponse Duplicate(string name, string nameKey) =>
        new(EventClassificationWriteOutcome.NameExists,
            $"An event classification with the key '{nameKey}' already exists in this school, so " +
            $"'{name}' cannot be added. Names are unique per school once punctuation, spacing and case " +
            "are removed (UX_EventClassifications_SchoolId_NameKey).",
            null);

    private static EventClassificationWriteResponse InUse(EventClassification row) =>
        new(EventClassificationWriteOutcome.InUse,
            $"'{row.Name}' cannot be deleted: at least one event is classified as it. Deleting it would " +
            "either destroy that reference or silently blank it, and both lose data nobody asked to " +
            $"lose. Deactivate it instead (PATCH /event-classifications/{row.Id}/active with isActive " +
            "false), which keeps every event's recorded classification.",
            ToDto(row));

    private static EventClassificationWriteResponse NotFound() =>
        new(EventClassificationWriteOutcome.NotFound, "Event classification not found.", null);

    private static EventClassificationWriteResponse NoSchool() =>
        new(EventClassificationWriteOutcome.NoSchoolResolved,
            "No school could be resolved for this event classification. With no tenant pinned there is " +
            "nothing to file it under.",
            null);
}
