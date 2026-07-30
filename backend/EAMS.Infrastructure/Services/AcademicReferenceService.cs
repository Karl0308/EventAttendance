using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// ADR-001 D-1's academic layer, read-only — see <see cref="IAcademicReferenceService"/> for why it is
/// read-only and why these five families share one service.
///
/// <para>
/// <b>Every method here projects straight to its DTO and none of them loads an entity.</b> That is
/// not a micro-optimization: these are reference lists that a picker re-fetches on every screen open,
/// and materializing a <c>Course</c> to read two columns off it drags the whole row plus change
/// tracking through for nothing. <c>AsNoTracking</c> on top, because nothing here is ever written back
/// — the identity map would be pure cost.
/// </para>
///
/// <para>
/// <b>No <c>ISchoolContext</c> parameter, and its absence is the design.</b> Every write service in
/// this assembly takes one because a write has to <em>decide</em> which school a new row belongs to.
/// A read decides nothing: the global <c>SchoolId</c> query filter already scopes all five entities —
/// four on their own column, <c>CourseOfferings</c> through its required <c>Term</c> — so injecting a
/// tenant here would offer a second, hand-written scoping rule that could disagree with the ambient
/// one. Nothing below calls <c>IgnoreQueryFilters</c>; every row these methods return goes straight to
/// an HTTP caller, which makes widening the filter a disclosure bug rather than the scoping
/// convenience it is inside <see cref="StudentGroupProjection"/>.
/// </para>
/// </summary>
internal sealed class AcademicReferenceService : IAcademicReferenceService
{
    private readonly EamsDbContext _db;
    public AcademicReferenceService(EamsDbContext db) => _db = db;

    // ------------------------------------------------------------------------------------- terms

    public async Task<IReadOnlyList<TermDto>> ListTermsAsync(CancellationToken ct = default) =>
        await _db.Terms.AsNoTracking()
            // Current first, then newest code first. Term codes are operator-authored and sort
            // chronologically by construction ('2025-2026-1' < '2025-2026-2'), so this is the order a
            // picker wants without needing StartsOn to have been filled in — and it frequently is not,
            // since the source has no term-date columns at all.
            .OrderByDescending(t => t.IsCurrent)
            .ThenByDescending(t => t.Code)
            .Select(t => new TermDto(
                t.Id, t.Code, t.SchoolYear, t.Semester, t.IsCurrent, t.StartsOn, t.EndsOn))
            .ToListAsync(ct);

    /// <summary>
    /// <c>FirstOrDefault</c> rather than <c>Single</c>, and the difference is what happens when the
    /// filtered unique index is not doing its job: a <c>Single</c> would turn a data problem in one
    /// school into a 500 on a read every screen makes. The index is what guarantees there is at most
    /// one; this method does not need to re-assert it, and asserting it here would only change which
    /// error the caller sees.
    /// </summary>
    public Task<TermDto?> GetCurrentTermAsync(CancellationToken ct = default) =>
        _db.Terms.AsNoTracking()
            .Where(t => t.IsCurrent)
            .Select(t => new TermDto(
                t.Id, t.Code, t.SchoolYear, t.Semester, t.IsCurrent, t.StartsOn, t.EndsOn))
            .FirstOrDefaultAsync(ct);

    // ---------------------------------------------------------------------------------- colleges

    public async Task<IReadOnlyList<CollegeDto>> ListCollegesAsync(CancellationToken ct = default) =>
        await _db.Colleges.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CollegeDto(c.Id, c.Name, c.Code))
            .ToListAsync(ct);

    // ---------------------------------------------------------------------------------- programs

    public async Task<IReadOnlyList<AcademicProgramDto>> ListProgramsAsync(
        Guid? collegeId, CancellationToken ct = default)
    {
        var query = _db.Programs.AsNoTracking();

        if (collegeId is { } college) query = query.Where(p => p.CollegeId == college);

        return await query
            .OrderBy(p => p.Code)
            // p.College is a required navigation, so this is an inner join and the null-forgiving
            // operator is a modelling artifact rather than a real possibility — the same reading the
            // query filters take on every other required navigation in this model.
            .Select(p => new AcademicProgramDto(
                p.Id, p.Code, p.Name, p.CollegeId, p.College!.Name))
            .ToListAsync(ct);
    }

    // ----------------------------------------------------------------------------------- courses

    public async Task<IReadOnlyList<CourseDto>> ListCoursesAsync(
        Guid? collegeId, string? search, CancellationToken ct = default)
    {
        var query = _db.Courses.AsNoTracking();

        if (collegeId is { } college) query = query.Where(c => c.CollegeId == college);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Code OR title, for the reason the interface records: a user typing "criminology" is
            // naming the title and one typing "SSCI" is naming the code. Not normalized through
            // AcademicKey — that collapses separators, which would make a *partial* search match
            // across word boundaries a human did not intend ("ge2" matching "GE Elect 2"). Key
            // normalization is for equality on a whole value; this is a substring search.
            query = query.Where(c =>
                c.Code.Contains(search) || (c.Title != null && c.Title.Contains(search)));
        }

        return await query
            .OrderBy(c => c.Code)
            // Optional navigation here, unlike Programs above: a course's college is genuinely
            // nullable, so this is a left join and the conditional is real rather than defensive.
            .Select(c => new CourseDto(
                c.Id, c.Code, c.Title, c.CollegeId, c.College == null ? null : c.College.Name))
            .ToListAsync(ct);
    }

    // -------------------------------------------------------------------------- course offerings

    public async Task<IReadOnlyList<CourseOfferingDto>> ListCourseOfferingsAsync(
        Guid? termId, Guid? courseId, string? section, CancellationToken ct = default)
    {
        var query = _db.CourseOfferings.AsNoTracking();

        if (termId is { } term) query = query.Where(o => o.TermId == term);
        if (courseId is { } course) query = query.Where(o => o.CourseId == course);

        if (!string.IsNullOrWhiteSpace(section))
        {
            // Normalize first, compare second — the rule this whole layer is built on. The stored
            // SectionKey is what AcademicKey produced at import, so comparing a raw 'BSFS 2-A'
            // against it would match nothing while looking entirely correct.
            var sectionKey = AcademicKey.NormalizeOrUnspecified(section);
            query = query.Where(o => o.SectionKey == sectionKey);
        }

        return await query
            .OrderBy(o => o.Course!.Code)
            .ThenBy(o => o.SectionKey)
            .Select(o => new CourseOfferingDto(
                o.Id,
                o.TermId, o.Term!.Code,
                o.CourseId, o.Course!.Code, o.Course!.Title,
                o.SectionName, o.SectionKey,
                // A correlated subquery count inside the same round trip, never a loaded collection:
                // this list is one row per section across a whole term, so an Include here would be
                // the textbook N+1 — and it would pull every enrollment row in the term across the
                // wire to produce one integer per offering.
                //
                // Soft-deleted students are excluded because this number is an audience size, and
                // every other read in the system treats a soft-deleted student as gone. A count that
                // included them would overstate the denominator of an event nobody had held yet.
                o.Enrollments.Count(e => !e.Student!.IsDeleted)))
            .ToListAsync(ct);
    }
}
