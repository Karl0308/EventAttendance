using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// <b>ADR-015 D-95 — the one <c>ExpectedPersonnelIds(event)</c> resolver.</b> A personnel member is
/// countable for an event iff they are in this set. The tap uses it now; the manifest, the event card
/// lookup, manual entry, the summary and the freeze (B1c-2 / B2) call the same method, never a copy
/// (D-15 symmetry, ADR-003 D-19).
///
/// <para>
/// <b>Three branches.</b>
/// <list type="number">
///   <item><b>Terminal event</b> (<see cref="EventStatusTransition.HasFrozenAudience"/>): the frozen
///   <see cref="EventPersonnel"/> rows (D-97), whatever the people have become since. Empty until B2's
///   freeze writes them, which is expected.</item>
///   <item><b>Live, pre-registration governs personnel</b> (D-103): the linked sessions hold at least one
///   <em>countable</em> personnel registrant. The set is those registrants.</item>
///   <item><b>Live, otherwise</b>: the personnel side of the attached audience definitions, after
///   ADR-013's scope mask (<see cref="AudienceResolution.BuildQueries"/> applies it).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Live sets keep only countable rows</b> — not deleted AND <c>Active</c> — and the D-103 selector
/// decides on that same predicate (<see cref="CountablePreRegisteredIds"/>), never on a raw registration
/// count. So a session whose only personnel registrant is Inactive does not govern, and the personnel side
/// falls back to the audience.
/// </para>
///
/// <para>
/// <b>Students are untouched.</b> <c>EventService.ExpectedStudentIdsAsync</c> keeps ADR-008 D-71's
/// whole-set REPLACE until B2 amends it; this class decides the personnel population only.
/// </para>
///
/// <para>
/// Static over the caller's <see cref="EamsDbContext"/>, like <see cref="AudienceResolution"/>, so the
/// returned query composes on the caller's context (a membership test is one <c>EXISTS</c>). Every
/// predicate names <c>SchoolId</c> explicitly: the §11 global filter is inert when no tenant is pinned
/// (D-27's precedent), and a definition's criteria resolve a fresh person query.
/// </para>
/// </summary>
internal static class ExpectedPersonnel
{
    /// <summary>The one countable-personnel predicate (D-103), as SQL-translatable inline terms.</summary>
    private static IQueryable<Personnel> Countable(IQueryable<Personnel> personnel, Guid schoolId) =>
        personnel.Where(p => p.SchoolId == schoolId && !p.IsDeleted && p.Status == PersonnelStatus.Active);

    /// <summary>
    /// The countable personnel expected at <paramref name="eventId"/>. Lazy: nothing is materialized; the
    /// only round trips made here are the selector and the definition read that decide its shape.
    /// </summary>
    public static async Task<IQueryable<Guid>> IdsAsync(
        EamsDbContext db, Guid eventId, Guid schoolId, string status, CancellationToken ct)
    {
        if (EventStatusTransition.HasFrozenAudience(status))
        {
            return db.EventPersonnel
                .Where(ep => ep.EventId == eventId && ep.Event!.SchoolId == schoolId)
                .Select(ep => ep.PersonnelId);
        }

        if (await PreRegistrationGovernsAsync(db, eventId, schoolId, ct))
            return CountablePreRegisteredIds(db, eventId, schoolId);

        return await AudienceIdsAsync(db, eventId, schoolId, ct);
    }

    /// <summary>
    /// D-103's selector, personnel half: does pre-registration replace this event's personnel population?
    /// True iff the linked sessions hold at least one countable personnel registrant. The live read and
    /// (B2) the freeze both ask this, so they cannot disagree.
    /// </summary>
    public static Task<bool> PreRegistrationGovernsAsync(
        EamsDbContext db, Guid eventId, Guid schoolId, CancellationToken ct) =>
        CountablePreRegisteredIds(db, eventId, schoolId).AnyAsync(ct);

    /// <summary>The linked sessions' countable personnel registrants, de-duplicated across sessions.</summary>
    private static IQueryable<Guid> CountablePreRegisteredIds(EamsDbContext db, Guid eventId, Guid schoolId)
    {
        var countable = Countable(db.Personnel, schoolId).Select(p => p.Id);

        return db.PreRegistrations
            .Where(r => r.Session!.EventId == eventId
                        && r.SchoolId == schoolId
                        && r.PersonnelId != null
                        && countable.Contains(r.PersonnelId!.Value))
            .Select(r => r.PersonnelId!.Value)
            .Distinct();
    }

    /// <summary>
    /// The personnel side of every definition attached to the event, scope-masked, countable only. An
    /// event with no definition (or only student-side ones) expects no personnel — an empty query, not
    /// null, because personnel have no other source (sections and individual attachments are students).
    /// </summary>
    private static async Task<IQueryable<Guid>> AudienceIdsAsync(
        EamsDbContext db, Guid eventId, Guid schoolId, CancellationToken ct)
    {
        var definitions = await db.EventAudienceDefinitions.AsNoTracking()
            .Where(link => link.EventId == eventId
                        && link.AudienceDefinition!.SchoolId == schoolId)
            .Select(link => new { link.AudienceDefinition!.AudienceType, link.AudienceDefinition.CriteriaJson })
            .ToListAsync(ct);

        IQueryable<Guid>? union = null;
        foreach (var definition in definitions)
        {
            var criteria = AudienceResolution.ParseCriteria(definition.CriteriaJson);
            var (_, personnel) = AudienceResolution.BuildQueries(
                db, definition.AudienceType, criteria, includeDeleted: false);
            if (personnel is null) continue;

            var ids = Countable(personnel, schoolId).Select(p => p.Id);
            union = union is null ? ids : union.Union(ids);
        }

        return union ?? db.Personnel.Where(_ => false).Select(p => p.Id);
    }
}
