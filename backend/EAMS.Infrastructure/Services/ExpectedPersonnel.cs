using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// <b>ADR-015 D-95 — the one <c>ExpectedPersonnelIds(event)</c> resolver.</b> A personnel member is
/// countable for an event iff they are in this set. The tap, the manifest, the event card lookup, manual
/// entry and the summary call the same method, never a copy, and the freeze writes down what
/// <see cref="ToFreezeAsync"/> resolves through the same branches (D-15 symmetry, ADR-003 D-19).
///
/// <para>
/// <b>Three branches.</b>
/// <list type="number">
///   <item><b>Terminal event</b> (<see cref="EventStatusTransition.HasFrozenAudience"/>): the frozen
///   <see cref="EventPersonnel"/> rows (D-97), whatever the people have become since. An event closed
///   before ADR-015 has none, so its numbers do not move.</item>
///   <item><b>Live, pre-registration governs personnel</b> (D-103,
///   <see cref="PreRegistrationSelector.GovernsPersonnelAsync"/>): the set is the linked sessions' countable
///   personnel registrants.</item>
///   <item><b>Live, otherwise</b>: the personnel side of the attached audience definitions, after
///   ADR-013's scope mask (<see cref="AudienceResolution.BuildQueries"/> applies it).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Live sets keep only countable rows</b> — not deleted AND <c>Active</c> — and the D-103 selector
/// decides on that same predicate, never on a raw registration count. So a session whose only personnel
/// registrant is Inactive does not govern, and the personnel side falls back to the audience.
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
    /// <summary>
    /// The one countable-personnel predicate (D-103), as SQL-translatable inline terms.
    /// <paramref name="includeDeleted"/> is the freeze's argument, the same one the student freeze passes
    /// (D-97); it never admits an <c>Inactive</c> member.
    /// </summary>
    public static IQueryable<Personnel> Countable(
        IQueryable<Personnel> personnel, Guid schoolId, bool includeDeleted) =>
        personnel.Where(p => p.SchoolId == schoolId
                             && (includeDeleted || !p.IsDeleted)
                             && p.Status == PersonnelStatus.Active);

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

        return await ResolvedIdsAsync(db, eventId, schoolId, includeDeleted: false, ct);
    }

    /// <summary>
    /// What the freeze writes down (D-97): the set as it stands at the transition, resolved through the
    /// same selector with the student freeze's <c>includeDeleted: true</c>, <b>plus every personnel member
    /// with an attendance row on the event</b>, whether or not they are still <c>Active</c> or in the
    /// audience (JJ, 2026-11-02). Without the second half a tapper who went <c>Inactive</c> would fall out
    /// of the frozen set, and their row could never be corrected on the closed event.
    /// </summary>
    public static async Task<IQueryable<Guid>> ToFreezeAsync(
        EamsDbContext db, Guid eventId, Guid schoolId, CancellationToken ct)
    {
        var resolved = await ResolvedIdsAsync(db, eventId, schoolId, includeDeleted: true, ct);

        return resolved.Union(db.AttendanceRecords
            .Where(a => a.EventId == eventId && a.SchoolId == schoolId && a.PersonnelId != null)
            .Select(a => a.PersonnelId!.Value));
    }

    private static async Task<IQueryable<Guid>> ResolvedIdsAsync(
        EamsDbContext db, Guid eventId, Guid schoolId, bool includeDeleted, CancellationToken ct) =>
        await PreRegistrationSelector.GovernsPersonnelAsync(db, eventId, schoolId, ct)
            ? PreRegistrationSelector.PersonnelRegistrants(db, eventId, schoolId, includeDeleted)
            : await AudienceIdsAsync(db, eventId, schoolId, includeDeleted, ct);

    /// <summary>
    /// The personnel side of every definition attached to the event, scope-masked, countable only. An
    /// event with no definition (or only student-side ones) expects no personnel — an empty query, not
    /// null, because personnel have no other source (sections and individual attachments are students).
    /// </summary>
    private static async Task<IQueryable<Guid>> AudienceIdsAsync(
        EamsDbContext db, Guid eventId, Guid schoolId, bool includeDeleted, CancellationToken ct)
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
                db, definition.AudienceType, criteria, includeDeleted);
            if (personnel is null) continue;

            var ids = Countable(personnel, schoolId, includeDeleted).Select(p => p.Id);
            union = union is null ? ids : union.Union(ids);
        }

        return union ?? db.Personnel.Where(_ => false).Select(p => p.Id);
    }
}
