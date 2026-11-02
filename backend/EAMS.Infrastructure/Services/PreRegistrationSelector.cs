using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// <b>ADR-015 D-103 — the one selector: does pre-registration replace this population of this event?</b>
/// True iff the event's linked sessions hold at least one <em>countable</em> registrant from that
/// population. Otherwise that population still comes from the event's audience, so a linked session with
/// no registrants replaces nothing. This amends ADR-008 D-71, where any linked session replaced everyone.
///
/// <para>
/// <b>The live read and the freeze both ask this, and neither passes anything that could change the
/// answer.</b> That is D-15's symmetry: the selector always decides on the countable predicate, while
/// <c>includeDeleted</c> changes only which rows the governing set yields. A session whose only registrant
/// was soft-deleted does not govern at the close either. No raw registration count decides it, including
/// the display totals on <c>EventPreRegistrationDto</c>.
/// </para>
///
/// <para>
/// Static over the caller's context so the returned queries compose into one statement, like
/// <see cref="AudienceResolution"/>. The <c>SchoolId</c> predicates are explicit because the §11 global
/// filter is inert when no tenant is pinned.
/// </para>
/// </summary>
internal static class PreRegistrationSelector
{
    /// <summary>D-103's student half.</summary>
    public static Task<bool> GovernsStudentsAsync(
        EamsDbContext db, Guid eventId, Guid schoolId, CancellationToken ct) =>
        StudentRegistrants(db, eventId, schoolId, includeDeleted: false).AnyAsync(ct);

    /// <summary>D-103's personnel half.</summary>
    public static Task<bool> GovernsPersonnelAsync(
        EamsDbContext db, Guid eventId, Guid schoolId, CancellationToken ct) =>
        PersonnelRegistrants(db, eventId, schoolId, includeDeleted: false).AnyAsync(ct);

    /// <summary>
    /// The linked sessions' student registrants, de-duplicated across sessions. A countable student is not
    /// deleted; the freeze passes <paramref name="includeDeleted"/> so a registrant soft-deleted before the
    /// close stays in the written-down set, as the section and definition sources do (ADR-003 D-15).
    /// </summary>
    public static IQueryable<Guid> StudentRegistrants(
        EamsDbContext db, Guid eventId, Guid schoolId, bool includeDeleted) =>
        db.PreRegistrations
            .Where(r => r.Session!.EventId == eventId
                        && r.SchoolId == schoolId
                        && r.StudentId != null
                        && (includeDeleted || !r.Student!.IsDeleted))
            .Select(r => r.StudentId!.Value)
            .Distinct();

    /// <summary>
    /// The linked sessions' personnel registrants, de-duplicated across sessions, through
    /// <see cref="ExpectedPersonnel.Countable"/>. <paramref name="includeDeleted"/> means what it means on
    /// <see cref="StudentRegistrants"/>; it never admits an <c>Inactive</c> registrant.
    /// </summary>
    public static IQueryable<Guid> PersonnelRegistrants(
        EamsDbContext db, Guid eventId, Guid schoolId, bool includeDeleted)
    {
        var countable = ExpectedPersonnel.Countable(db.Personnel, schoolId, includeDeleted).Select(p => p.Id);

        return db.PreRegistrations
            .Where(r => r.Session!.EventId == eventId
                        && r.SchoolId == schoolId
                        && r.PersonnelId != null
                        && countable.Contains(r.PersonnelId!.Value))
            .Select(r => r.PersonnelId!.Value)
            .Distinct();
    }
}
