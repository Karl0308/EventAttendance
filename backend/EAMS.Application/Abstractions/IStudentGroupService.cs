using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// The read surface over Technical Plan §4.7 <c>StudentGroups</c> — <b>the list an event audience is
/// picked from</b>. <c>POST /events/{id}/attendees</c> takes ids from here, and until this existed
/// there was no way to obtain one over HTTP at all: the admin SPA could attach an audience only if
/// somebody read a group id out of the database by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="IAcademicReferenceService"/> on purpose, and the split is not
/// arbitrary.</b> That interface reads the academic tables the import owns; this one reads the
/// grouping layer those tables are <em>projected into</em>. They are a different table family with a
/// different owner — <see cref="IStudentGroupProjection"/> writes this one — and a different consumer:
/// the academic reads answer "what structure exists", this one answers "who can I invite". Folding
/// them into a single seven-method service would put two write-owners behind one read contract, which
/// is the cohesion smell that makes the next person unsure which half to extend.
/// </para>
///
/// <para>
/// <b>Reads only.</b> Manual groups have no write surface yet and derived ones must never get one —
/// the projection reconciles them from the academic tables on every import, so a hand-edited derived
/// membership is erased on the next run. Creating manual groups is a real gap and a deliberate one;
/// it is a decision about who owns membership, not an oversight.
/// </para>
/// </remarks>
public interface IStudentGroupService
{
    /// <summary>
    /// Groups in the resolved school, by name.
    /// </summary>
    /// <param name="sourceType">
    /// <c>Manual</c> or <c>Derived</c> (§4.7 / ADR-001 D-1 provenance). Null lists both.
    ///
    /// <para>
    /// Matched case-insensitively against the documented set and canonicalized before it reaches the
    /// query, so <c>derived</c> and <c>Derived</c> are one filter. <b>A value outside the set returns
    /// an empty list, never every row</b> — a mistyped filter that silently stops filtering is how an
    /// audience picker offers manual groups to a flow that asked only for cohorts.
    /// </para>
    /// </param>
    /// <param name="termId">
    /// Narrows to the derived groups of one term. Null lists every term's, plus the manual groups,
    /// which carry no term.
    ///
    /// <para>
    /// <b>This is the filter that matters most and the one a caller is most likely to omit.</b> A
    /// section name is reused every semester against an entirely different set of students, so an
    /// unscoped list shows several distinct cohorts under names that differ only by the term suffix
    /// the projection composes into them.
    /// </para>
    /// </param>
    Task<IReadOnlyList<StudentGroupDto>> ListAsync(
        string? sourceType, Guid? termId, CancellationToken ct = default);
}
