using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// Technical Plan §4.7 <c>StudentGroups</c>, read-only — see <see cref="IStudentGroupService"/> for
/// why it is read-only and why it is separate from the academic reference reads.
///
/// <para>
/// <b>The write-side twin is <see cref="StudentGroupProjection"/>, and the two scope their tenancy
/// differently on purpose.</b> The projection reads with <c>IgnoreQueryFilters</c> and substitutes an
/// explicit <c>SchoolId</c> predicate taken from the term it was handed, because it is addressed by
/// term id and must behave identically whether or not a tenant happens to be pinned. This service is
/// the opposite case: it is addressed by an HTTP request and hands every row it reads to the caller,
/// so the ambient filter is exactly the right mechanism and reaching past it would be a disclosure
/// bug. <c>StudentGroups</c> owns a <c>SchoolId</c> column, so the filter is a column comparison
/// rather than a join.
/// </para>
/// </summary>
internal sealed class StudentGroupService : IStudentGroupService
{
    private readonly EamsDbContext _db;
    public StudentGroupService(EamsDbContext db) => _db = db;

    public async Task<PagedResult<StudentGroupDto>> ListAsync(
        string? sourceType, Guid? termId, PageRequest page, CancellationToken ct = default)
    {
        var query = _db.StudentGroups.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            // Canonicalized through the domain value set rather than compared raw, for the two
            // reasons DomainValues records: `derived` and `Derived` are one filter to a user and
            // rejecting over casing alone teaches nothing, while the stored value is canonical and a
            // raw comparison is ordinal in the C# that reads it back.
            //
            // An undocumented value yields no rows rather than every row. That asymmetry is the
            // point: the failure mode of a filter that silently stops filtering is an audience picker
            // offering manual groups to a flow that asked for cohorts, and nothing in the response
            // says the filter was ignored. Empty is a visible wrong answer; unfiltered is an
            // invisible one.
            if (!GroupSourceType.TryNormalize(sourceType, out var canonical))
            {
                return new PagedResult<StudentGroupDto>([], page.Page, page.PageSize, Total: 0);
            }

            query = query.Where(g => g.SourceType == canonical);
        }

        if (termId is { } term) query = query.Where(g => g.TermId == term);

        // Counted and paged through PagedQuery.ToPageAsync — the seam all nine admin lists share, so
        // the total cannot end up describing a different filter than the rows do.
        return await query.ToPageAsync(
            // ThenBy(Id) is the total order. Group names are unique per (school, source, term) by the
            // projection's index but not globally, and the unfiltered query an untenanted caller sees
            // spans schools — so "BSFS 2-A (2025-2026-1)" can appear twice, and equal sort keys are
            // what a page boundary silently reorders across.
            ordered => ordered
                .OrderBy(g => g.Name).ThenBy(g => g.Id)
                .Select(g => new StudentGroupDto(
                    g.Id, g.Name, g.Type,
                    g.SourceType, g.SourceEntityType,
                    g.TermId,
                    // Optional navigation — null on a manual group, which belongs to no term.
                    g.Term == null ? null : g.Term.Code,
                    // A correlated subquery count, never a loaded Members collection: this endpoint
                    // backs a picker that lists every group in a school, so including the memberships
                    // would pull one row per student per group to produce one integer each.
                    //
                    // Soft-deleted students are excluded, matching CourseOfferingDto.EnrolledCount and
                    // every other read in the system — this is an audience size, and a deleted student
                    // is not in the audience.
                    g.Members.Count(m => !m.Student!.IsDeleted),
                    g.LastSyncedAt)),
            page, ct);
    }
}
