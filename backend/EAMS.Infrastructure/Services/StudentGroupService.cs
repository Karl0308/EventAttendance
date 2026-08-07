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
        string? sourceType, string? type, Guid? termId, string? search, PageRequest page,
        CancellationToken ct = default)
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

        if (!string.IsNullOrWhiteSpace(type))
        {
            // Canonicalized and empty-on-unknown for the same two reasons `sourceType` is, and the
            // stakes are higher on this one. `type` is the filter an audience builder narrows by — "the
            // year-level groups", "the section groups" — so a value that silently stopped filtering
            // would hand a year picker every college, programme, section and offering in the term,
            // under names that look entirely plausible next to each other. Empty is a wrong answer
            // somebody notices in the first five seconds.
            if (!StudentGroupType.TryNormalize(type, out var canonicalType))
            {
                return new PagedResult<StudentGroupDto>([], page.Page, page.PageSize, Total: 0);
            }

            query = query.Where(g => g.Type == canonicalType);
        }

        if (termId is { } term) query = query.Where(g => g.TermId == term);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Name only, which is the whole of what this list publishes as text and the whole of what a
            // picker renders. Deliberately not extended to SourceKey: that column holds a Guid string
            // for three of the five group kinds, so searching it would match on fragments of an id no
            // user has ever seen and produce hits nobody can explain.
            //
            // Not normalized through AcademicKey either, matching AcademicReferenceService.
            // Normalization collapses separators, which is right for equality on a whole key and wrong
            // for a substring search — "ge2" would match "GE Elect 2" across a word boundary a human
            // did not intend. Case-insensitivity comes from the database collation, as it does there.
            query = query.Where(g => g.Name.Contains(search));
        }

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
