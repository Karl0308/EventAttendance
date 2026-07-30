using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Api.Controllers;

/// <summary>
/// ADR-001 D-1's academic layer over HTTP — <b>reads only</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this controller exists at all.</b> The tables and the §10 import that populates them have
/// been in place since Phase 2, and nothing exposed them. The admin SPA therefore could not build an
/// event-audience picker — "invite BSCRIM 2-A" was unanswerable over the API, because listing the
/// sections required reading the database by hand. That was the single hard blocker on the whole
/// back-office, and closing it is the whole of this controller's job.
/// </para>
///
/// <para>
/// <b>No POST, PUT or DELETE, and their absence is a decision.</b> The roster import owns every table
/// behind these routes. A hand-created course is matched by the importer on its normalized key and is
/// either silently overwritten or — if the operator typed the code differently — duplicated into a
/// second row that splits the enrollments between them. Who wins that conflict is an open question,
/// and until it is answered the honest surface is the one that cannot create it.
/// </para>
///
/// <para>
/// <b><c>academic.read</c> is a permission code this phase minted; §6 does not define one.</b> The
/// plan's §6 tables predate the academic layer entirely and assign it no routes, so there was nothing
/// to inherit. The nearest signal is §7.1, which guards the frontend's <c>/groups</c> page with
/// <c>students.read</c> — reused here it would have meant "anyone who can browse the roster can browse
/// its structure", which is defensible but collapses two questions Phase 6 should get to answer
/// separately. The attribute enforces nothing (ADR-001 D-6), so this is a declaration of intent on
/// the audit list Phase 6's rename walks, and changing it then is one edit per action.
/// </para>
/// </remarks>
// Permission codes are this phase's, not §6.2's — see the remarks. ADR-001 D-6's bargain was that
// endpoints get decorated as they are written so Phase 6 wires enforcement rather than re-deriving
// what each endpoint should have demanded. The attribute enforces nothing.
[ApiController]
[Route("api/v1/academic")]
public class AcademicController : ControllerBase
{
    private readonly IAcademicReferenceService _academic;
    public AcademicController(IAcademicReferenceService academic) => _academic = academic;

    // ------------------------------------------------------------------------------------- terms

    /// <summary>
    /// <c>GET /academic/terms</c> — every term, current first and then newest first.
    /// </summary>
    /// <remarks>
    /// The order is deliberate and does not depend on <c>startsOn</c>: the roster source has no term
    /// date columns at all, so those two fields are frequently null and sorting on them would produce
    /// an arbitrary list on real data. Term codes are operator-authored and sort chronologically by
    /// construction, which is what this falls back to.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The terms, possibly empty.</response>
    [HttpGet("terms")]
    [HasPermissionNotEnforced("academic.read")]
    [ProducesResponseType(typeof(IEnumerable<TermDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TermDto>>> Terms(CancellationToken ct)
        => Ok(await _academic.ListTermsAsync(ct));

    /// <summary>
    /// <c>GET /academic/terms/current</c> — the term flagged current, or 404 when none is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>404 rather than a 200 carrying null, because the two are different facts and a client has to
    /// act on them differently.</b> "No term is flagged current" is a configuration state an
    /// administrator fixes — it happens between semesters, before anyone has moved the flag — whereas
    /// an empty 200 body reads as "here is the current term, it just has no fields". A caller
    /// defaulting a term picker needs to tell those apart to know whether to prompt.
    /// </para>
    ///
    /// <para>
    /// <b>At most one term can be current, and the database is what guarantees it</b> — a filtered
    /// unique index, not a convention. Two "current" terms would make every term-defaulting query pick
    /// one at random, which surfaces months later as a report quietly about the wrong semester.
    /// </para>
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The current term.</response>
    /// <response code="404">No term in this school is flagged current.</response>
    [HttpGet("terms/current")]
    [HasPermissionNotEnforced("academic.read")]
    [ProducesResponseType(typeof(TermDto), StatusCodes.Status200OK)]
    // typeof: [ApiController] turns NotFound() into a ProblemDetails, so declaring the status alone
    // would publish a 404 the document says carries no body while the pipeline returns one.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TermDto>> CurrentTerm(CancellationToken ct)
    {
        var term = await _academic.GetCurrentTermAsync(ct);
        return term is null ? NotFound() : Ok(term);
    }

    // ---------------------------------------------------------------------------------- colleges

    /// <summary>
    /// <c>GET /academic/colleges</c> — every college, by name.
    /// </summary>
    /// <remarks>
    /// Unfiltered and unpaged on purpose: a college list is a handful of rows per institution and is
    /// the top of every drill-down in the audience picker, so a cursor would be ceremony over a
    /// dropdown. <c>code</c> is nullable — the roster source has no college code column, so the
    /// natural key is the normalized name and the code exists only to be filled in later.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The colleges, possibly empty.</response>
    [HttpGet("colleges")]
    [HasPermissionNotEnforced("academic.read")]
    [ProducesResponseType(typeof(IEnumerable<CollegeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CollegeDto>>> Colleges(CancellationToken ct)
        => Ok(await _academic.ListCollegesAsync(ct));

    // ---------------------------------------------------------------------------------- programs

    /// <summary>
    /// <c>GET /academic/programs</c> — degree programmes, optionally narrowed to one college.
    /// </summary>
    /// <param name="collegeId">
    /// Omit to list them all. An id matching no college returns an empty list rather than everything —
    /// a filter that silently stops filtering is how an "invite this college" flow ends up inviting
    /// the institution.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The matching programmes, possibly empty.</response>
    [HttpGet("programs")]
    [HasPermissionNotEnforced("academic.read")]
    [ProducesResponseType(typeof(IEnumerable<AcademicProgramDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<AcademicProgramDto>>> Programs(
        [FromQuery] Guid? collegeId, CancellationToken ct)
        => Ok(await _academic.ListProgramsAsync(collegeId, ct));

    // ----------------------------------------------------------------------------------- courses

    /// <summary>
    /// <c>GET /academic/courses</c> — courses, optionally narrowed by college and search term.
    /// </summary>
    /// <param name="collegeId">
    /// Omit to list them all. A course's college is itself nullable, so an unattributed course matches
    /// no <c>collegeId</c> — which is correct: it is not known to be in one, rather than known not to
    /// be.
    /// </param>
    /// <param name="search">
    /// Matched against <b>both</b> the code and the title. A user typing "criminology" is naming the
    /// title while one typing "SSCI" is naming the code, and asking which they meant is worse than
    /// searching both. Substring, case-insensitive by the database's collation.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The matching courses, possibly empty.</response>
    [HttpGet("courses")]
    [HasPermissionNotEnforced("academic.read")]
    [ProducesResponseType(typeof(IEnumerable<CourseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CourseDto>>> Courses(
        [FromQuery] Guid? collegeId, [FromQuery] string? search, CancellationToken ct)
        => Ok(await _academic.ListCoursesAsync(collegeId, search, ct));

    // -------------------------------------------------------------------------- course offerings

    /// <summary>
    /// <c>GET /academic/course-offerings</c> — <b>the section grain, and the row an audience picker is
    /// really looking for.</b> A course taught to two sections is two entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the endpoint that answers "who is in BSCRIM 2-A", and <c>students.section</c> is
    /// not.</b> That column is the ADR-001 D-2 derived display cache: it is single-valued, and twelve
    /// of the fifty-two students in the real roster sit in more than one section, so it is wrong for
    /// roughly a quarter of them. An offering is the grain an invitation is actually issued at.
    /// </para>
    ///
    /// <para>
    /// <c>sectionKey</c> is published alongside <c>sectionName</c> because a <em>cohort</em> has no
    /// row of its own — it is a key shared by every offering that cohort takes within a term, which is
    /// why the group projection keys section groups on it. <c>sectionName</c> is null where the source
    /// was blank (39 sample rows are); the key is a sentinel rather than null in that case, because a
    /// nullable key component would have capped the table at one blank section under SQL Server's
    /// unique index.
    /// </para>
    /// </remarks>
    /// <param name="termId">
    /// Omit to list every term's. Callers should almost always pass one: section names repeat each
    /// semester against a different cohort, so an unscoped list holds several distinct audiences under
    /// identical names.
    /// </param>
    /// <param name="courseId">Omit to list offerings of every course.</param>
    /// <param name="section">
    /// The section's display name. <b>Normalized before it is compared</b> — <c>BSFS 2-A</c>,
    /// <c>bsfs2a</c> and <c>BSFS-2A</c> are one section — so the filter does not depend on how the
    /// caller happened to type it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The matching offerings, possibly empty.</response>
    [HttpGet("course-offerings")]
    [HasPermissionNotEnforced("academic.read")]
    [ProducesResponseType(typeof(IEnumerable<CourseOfferingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CourseOfferingDto>>> CourseOfferings(
        [FromQuery] Guid? termId, [FromQuery] Guid? courseId, [FromQuery] string? section,
        CancellationToken ct)
        => Ok(await _academic.ListCourseOfferingsAsync(termId, courseId, section, ct));
}
