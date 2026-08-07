using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// ADR-001 D-1's academic layer over HTTP — <b>reads, plus D-53's three term-administration writes and
/// nothing else</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this controller exists at all.</b> The tables and the §10 import that populates them have
/// been in place since Phase 2, and nothing exposed them. The admin SPA therefore could not build an
/// event-audience picker — "invite BSCRIM 2-A" was unanswerable over the API, because listing the
/// sections required reading the database by hand. That was the single hard blocker on the whole
/// back-office, and closing it is the whole of this controller's original job.
/// </para>
///
/// <para>
/// <b>Colleges, programmes, courses and offerings are read-only, and their having no POST, PUT or
/// DELETE is a decision.</b> The roster import owns those four tables. A hand-created course is matched
/// by the importer on its normalized key and is either silently overwritten or — if the operator typed
/// the code differently — duplicated into a second row that splits the enrollments between them. Who
/// wins that conflict is an open question, and until it is answered the honest surface is the one that
/// cannot create it.
/// </para>
///
/// <para>
/// <b><c>Terms</c> is the exception, and the carve-out is narrower than reversing that (D-53).</b> The
/// importer only ever <em>reads</em> <c>Terms</c> — it takes a <c>TermId</c> as input (ADR-001 D-5) and
/// writes no row of that table — so a hand-authored term has no conflict to lose. A term is also the
/// one academic row with no source in the roster file, and every import needs one to exist first: until
/// these three routes landed, the documented way to create one was hand-written SQL against
/// <c>dbo.Terms</c>, which is not a procedure an operator can be given. The writes go through
/// <see cref="ITermAdminService"/> rather than <see cref="IAcademicReferenceService"/>, so that
/// interface's reads-only reasoning stays true of every method on it.
/// </para>
///
/// <para>
/// <b>Still no DELETE, anywhere on this controller.</b> A term with a batch imported against it cannot
/// be removed without taking that batch's enrolments and term records with it, which the no-data-loss
/// rule forbids. Retiring a term is <c>PATCH /academic/terms/{id}/current</c> with
/// <c>isCurrent: false</c>.
/// </para>
///
/// <para>
/// <b><c>academic.read</c> is a permission code Phase 3b-2 minted, and D-44 keeps it.</b> The plan's
/// §6 tables predate the academic layer entirely and assign it no routes, so there was nothing to
/// inherit and this is an <em>addition</em> to the plan's map rather than a contradiction of it —
/// which is exactly what separates it from the <c>groups.read</c> that D-45 deleted, where §7.1 had
/// already assigned <c>students.read</c> to the same page. The nearest signal here is that same
/// §7.1 line: reused, it would have meant "anyone who can browse the roster can browse its
/// structure", which is defensible but collapses two questions Phase 6 should get to answer
/// separately. The attribute enforces nothing (ADR-001 D-6), so this is a declaration of intent on
/// the audit list Phase 6's rename walks, and changing it then is one edit per action. The code
/// itself now lives once, on <see cref="EamsPermissions"/>, rather than at six call sites.
/// </para>
/// </remarks>
// Permission codes are this phase's, not §6.2's — see the remarks. ADR-001 D-6's bargain was that
// endpoints get decorated as they are written so Phase 6 wires enforcement rather than re-deriving
// what each endpoint should have demanded. The attribute enforces nothing.
[ApiController]
[Route("api/v1/academic")]
public class AcademicController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on <c>StudentsController</c>.</summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IAcademicReferenceService _academic;

    /// <summary>
    /// D-53's term writes. A second dependency rather than three more methods on the reference service
    /// — see <see cref="ITermAdminService"/> for why the read-only argument that protects the other
    /// four entity families does not reach terms.
    /// </summary>
    private readonly ITermAdminService _terms;

    public AcademicController(IAcademicReferenceService academic, ITermAdminService terms)
    {
        _academic = academic;
        _terms = terms;
    }

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
    /// <param name="page">1-based page number, default 1. Out-of-range values are clamped, not refused.</param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200; a larger value is clamped and the response says so.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of terms, possibly empty.</response>
    [HttpGet("terms")]
    [HasPermissionNotEnforced(EamsPermissions.AcademicRead)]
    [ProducesResponseType(typeof(PagedResult<TermDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<TermDto>>> Terms(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _academic.ListTermsAsync(PageRequest.From(page, pageSize), ct));

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
    [HasPermissionNotEnforced(EamsPermissions.AcademicRead)]
    [ProducesResponseType(typeof(TermDto), StatusCodes.Status200OK)]
    // typeof: [ApiController] turns NotFound() into a ProblemDetails, so declaring the status alone
    // would publish a 404 the document says carries no body while the pipeline returns one.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TermDto>> CurrentTerm(CancellationToken ct)
    {
        var term = await _academic.GetCurrentTermAsync(ct);
        return term is null ? NotFound() : Ok(term);
    }

    // ------------------------------------------------------------------------ terms: D-53 writes

    /// <summary>
    /// <c>POST /academic/terms</c> — create a school year + semester (D-53).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one academic row a human authors rather than imports.</b> It has no source in the roster
    /// file and every import requires one to exist first, so without this route a fresh installation's
    /// only path to a term was hand-written SQL. The other four entity families behind this controller
    /// stay read-only — see the controller remarks.
    /// </para>
    ///
    /// <para>
    /// <b>The new term is never current.</b> Moving that flag is <c>PATCH /academic/terms/{id}/current</c>,
    /// which is a two-row operation under a filtered unique index rather than a field write; the create
    /// body carries no <c>isCurrent</c> at all.
    /// </para>
    ///
    /// <para>
    /// <b><c>code</c> is unique per school and a duplicate is a 409, never a 500.</b> The body carries
    /// <c>code: "TermCodeExists"</c>. The check is made before the insert and the unique index is caught
    /// as well, because those are two statements and a second operator creating the same code in the
    /// same second is decided by the index.
    /// </para>
    /// </remarks>
    /// <param name="request">The term's code, school year, semester and optional calendar dates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The created term. <c>Location</c> names the term list — see below.</response>
    /// <response code="400">A field is blank, over-length, whitespace-padded, or the dates run backwards.</response>
    /// <response code="409">
    /// A term with this code already exists in this school, or no school could be resolved to file it
    /// under.
    /// </response>
    // The Location header names the collection rather than the new row, for the reason
    // StudentsController.AddCard's does: there is no GET /academic/terms/{id}, and a 201 whose Location
    // 404s is worse than one naming the resource the term is actually visible on. Adding a by-id read
    // was considered and left out — D-53 defines three routes, and this controller's surface is the
    // thing the decision is about.
    [HttpPost("terms")]
    [HasPermissionNotEnforced(EamsPermissions.AcademicWrite)]
    [ProducesResponseType(typeof(TermDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TermDto>> CreateTerm(
        [FromBody] TermWriteRequest request, CancellationToken ct)
    {
        var response = await _terms.CreateAsync(request, ct);
        if (response.Outcome != TermWriteOutcome.Saved) return Failure(response);

        return CreatedAtAction(nameof(Terms), null, response.Term);
    }

    /// <summary>
    /// <c>PUT /academic/terms/{id}</c> — edit a term's authored fields (D-53).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full replacement of the term's own fields: <c>code</c>, <c>schoolYear</c>, <c>semester</c> and
    /// the two optional dates. <b>It does not move the current-term flag</b> — that is
    /// <c>PATCH /academic/terms/{id}/current</c>, and a display edit silently redefining which semester
    /// the institution is in is exactly the side effect the split exists to prevent.
    /// </para>
    ///
    /// <para>
    /// <b><c>code</c> is editable, and renaming onto another term's code is a 409.</b> Fixing a typo in
    /// a code is the most likely edit anyone makes here. What a rename does not rewrite: derived
    /// <c>StudentGroup</c> display names embed the code at projection time
    /// (<c>"BSFS 2-A (2025-2026-1)"</c>) and keep the old text until the next import re-runs the
    /// projection — stale wording, not a wrong audience, since groups resolve on ids.
    /// </para>
    /// </remarks>
    /// <param name="id">The term.</param>
    /// <param name="request">The new field values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The updated term.</response>
    /// <response code="400">A field is blank, over-length, whitespace-padded, or the dates run backwards.</response>
    /// <response code="404">No such term.</response>
    /// <response code="409">Another term in this school already holds that code.</response>
    [HttpPut("terms/{id:guid}")]
    [HasPermissionNotEnforced(EamsPermissions.AcademicWrite)]
    [ProducesResponseType(typeof(TermDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TermDto>> UpdateTerm(
        Guid id, [FromBody] TermWriteRequest request, CancellationToken ct)
    {
        var response = await _terms.UpdateAsync(id, request, ct);
        return response.Outcome == TermWriteOutcome.Saved ? Ok(response.Term) : Failure(response);
    }

    /// <summary>
    /// <c>PATCH /academic/terms/{id}/current</c> — make this the current term, or retire it (D-53).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own route rather than an <c>isCurrent</c> field on the PUT, for the same reason
    /// <c>PATCH /events/{id}/status</c> is its own route.</b> <c>IsCurrent</c> is not a property of the
    /// row, it is a claim about the school: a filtered unique index
    /// (<c>UNIQUE(SchoolId) WHERE IsCurrent = 1</c>) caps it at one term per school, so setting it
    /// clears whichever term holds it — a two-row transaction, not a field write. A PUT carrying the
    /// flag would be one resource's payload silently rewriting a different resource, and it would fail
    /// or no-op depending on which term happened to be current at the time.
    /// </para>
    ///
    /// <para>
    /// <b><c>isCurrent</c> is required, and omitting it is a 400 rather than a default.</b> A missing
    /// JSON member would bind to <c>false</c> and quietly retire the school's term. Send
    /// <c>{"isCurrent": true}</c> to make this term current, or <c>{"isCurrent": false}</c> to retire it
    /// — which is the only retirement there is, since D-53 offers no deletion. Retiring leaves the
    /// school with no current term, an ordinary state the reads already answer for.
    /// </para>
    ///
    /// <para>
    /// Idempotent: setting a term that is already current, or clearing one that is not, changes nothing
    /// and answers 200.
    /// </para>
    /// </remarks>
    /// <param name="id">The term.</param>
    /// <param name="request"><c>{"isCurrent": true}</c> or <c>{"isCurrent": false}</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The term, with its new flag. Exactly one term per school can carry it.</response>
    /// <response code="400"><c>isCurrent</c> was not supplied.</response>
    /// <response code="404">No such term.</response>
    [HttpPatch("terms/{id:guid}/current")]
    [HasPermissionNotEnforced(EamsPermissions.AcademicWrite)]
    [ProducesResponseType(typeof(TermDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TermDto>> SetCurrentTerm(
        Guid id, [FromBody] TermCurrentRequest request, CancellationToken ct)
    {
        // Bound as bool? and refused here rather than in the service, because "the member was absent"
        // is a fact only the deserializer has — by the time a bool reaches the service it is
        // indistinguishable from a deliberate false, and on this route false is a destructive
        // instruction. The service's own guards still cover every value it can be handed.
        if (request.IsCurrent is not { } isCurrent)
        {
            return Failure(new TermWriteResponse(
                TermWriteOutcome.ValidationFailed,
                "isCurrent is required. Send {\"isCurrent\": true} to make this the current term or " +
                "{\"isCurrent\": false} to retire it — a missing member would bind to false and " +
                "silently leave the school with no current term.",
                null));
        }

        var response = await _terms.SetCurrentAsync(id, isCurrent, ct);
        return response.Outcome == TermWriteOutcome.Saved ? Ok(response.Term) : Failure(response);
    }

    // ---------------------------------------------------------------------------------- colleges

    /// <summary>
    /// <c>GET /academic/colleges</c> — every college, by name.
    /// </summary>
    /// <remarks>
    /// Unfiltered, and paged like every other admin list — a college list really is a handful of rows
    /// per institution, but "this table is small today" is a property of the data rather than of the
    /// endpoint, and the two lists on this controller that were argued unbounded on exactly that
    /// reasoning (offerings, groups) are the two that grew per term. <c>code</c> is nullable: the
    /// roster source has no college code column, so the natural key is the normalized name and the
    /// code exists only to be filled in later.
    /// </remarks>
    /// <param name="page">1-based page number, default 1. Out-of-range values are clamped, not refused.</param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200; a larger value is clamped and the response says so.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of colleges, possibly empty.</response>
    [HttpGet("colleges")]
    [HasPermissionNotEnforced(EamsPermissions.AcademicRead)]
    [ProducesResponseType(typeof(PagedResult<CollegeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CollegeDto>>> Colleges(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _academic.ListCollegesAsync(PageRequest.From(page, pageSize), ct));

    // ---------------------------------------------------------------------------------- programs

    /// <summary>
    /// <c>GET /academic/programs</c> — degree programmes, optionally narrowed to one college.
    /// </summary>
    /// <param name="collegeId">
    /// Omit to list them all. An id matching no college returns an empty list rather than everything —
    /// a filter that silently stops filtering is how an "invite this college" flow ends up inviting
    /// the institution.
    /// </param>
    /// <param name="page">1-based page number, default 1. Out-of-range values are clamped, not refused.</param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200; a larger value is clamped and the response says so.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of matching programmes, possibly empty.</response>
    [HttpGet("programs")]
    [HasPermissionNotEnforced(EamsPermissions.AcademicRead)]
    [ProducesResponseType(typeof(PagedResult<AcademicProgramDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AcademicProgramDto>>> Programs(
        [FromQuery] Guid? collegeId, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => Ok(await _academic.ListProgramsAsync(collegeId, PageRequest.From(page, pageSize), ct));

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
    /// <param name="page">1-based page number, default 1. Out-of-range values are clamped, not refused.</param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200; a larger value is clamped and the response says so.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of matching courses, possibly empty.</response>
    [HttpGet("courses")]
    [HasPermissionNotEnforced(EamsPermissions.AcademicRead)]
    [ProducesResponseType(typeof(PagedResult<CourseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CourseDto>>> Courses(
        [FromQuery] Guid? collegeId, [FromQuery] string? search,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _academic.ListCoursesAsync(
            collegeId, search, PageRequest.From(page, pageSize), ct));

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
    /// <b>Omit to get the current term, not every term.</b> Section names repeat each semester against
    /// a different cohort, so an unscoped list stacked several distinct audiences under identical
    /// names and grew with every import the institution had ever run — this endpoint returned every
    /// offering of every term, which is the defect the default closes.
    ///
    /// <para>
    /// <b>When no term is flagged current, the response is an empty page.</b> Zero current terms is a
    /// real state — the flag is capped at one per school, not pinned at one, and between semesters
    /// nobody has moved it yet. Widening back to every term in that case would restore the unbounded
    /// read on the one day nobody is watching for it. Ask <c>GET /academic/terms/current</c> to tell
    /// "no offerings this term" from "no term is current"; it 404s on the second.
    /// </para>
    /// </param>
    /// <param name="courseId">Omit to list offerings of every course in the scoped term.</param>
    /// <param name="section">
    /// The section's display name. <b>Normalized before it is compared</b> — <c>BSFS 2-A</c>,
    /// <c>bsfs2a</c> and <c>BSFS-2A</c> are one section — so the filter does not depend on how the
    /// caller happened to type it.
    /// </param>
    /// <param name="page">1-based page number, default 1. Out-of-range values are clamped, not refused.</param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200; a larger value is clamped and the response says so.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">
    /// One page of matching offerings, possibly empty — including when no term is flagged current and
    /// none was named.
    /// </response>
    [HttpGet("course-offerings")]
    [HasPermissionNotEnforced(EamsPermissions.AcademicRead)]
    [ProducesResponseType(typeof(PagedResult<CourseOfferingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CourseOfferingDto>>> CourseOfferings(
        [FromQuery] Guid? termId, [FromQuery] Guid? courseId, [FromQuery] string? section,
        [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => Ok(await _academic.ListCourseOfferingsAsync(
            termId, courseId, section, PageRequest.From(page, pageSize), ct));

    // --------------------------------------------------------------------------------- mapping

    /// <summary>
    /// The status-code contract for D-53's term write surface, as one total function over
    /// <see cref="TermWriteOutcome"/>.
    ///
    /// <para>
    /// <b>Every member is listed instead of a <c>_ =&gt; Ok(...)</c> fall-through</b>, for the reason
    /// <c>StudentsController.StatusCodeFor</c> and <c>AttendanceController.StatusCodeFor</c> both
    /// record at length: a discard arm made "an outcome nobody mapped" indistinguishable from "an
    /// outcome that means success", and shipped a rejection as a 200. The final arm has to exist — C#
    /// does not treat a fully enumerated enum switch as exhaustive — but it throws rather than guessing.
    /// </para>
    ///
    /// <para>
    /// <b>Why <c>TermCodeExists</c> is 409 and not 400.</b> Same split the students and events surfaces
    /// draw. A blank or whitespace-padded code is wrong under every circumstance and the fix is to
    /// change the request; a taken code rejects a payload that is entirely well formed and would be
    /// accepted the moment the term holding that code is renamed. That is a conflict with the state of
    /// the resource, which is what 409 is for — and it is the specific thing D-53 promises will not be
    /// a 500.
    /// </para>
    /// </summary>
    internal static int StatusCodeFor(TermWriteOutcome outcome) => outcome switch
    {
        TermWriteOutcome.Saved => StatusCodes.Status200OK,

        // The URL claims the term exists.
        TermWriteOutcome.NotFound => StatusCodes.Status404NotFound,

        // The caller's payload: a field outside the Terms column rules, or a PATCH that did not say
        // which way.
        TermWriteOutcome.ValidationFailed => StatusCodes.Status400BadRequest,

        // The request is fine; the state of the school forbids it. See the method remarks.
        TermWriteOutcome.TermCodeExists
            or TermWriteOutcome.NoSchoolResolved => StatusCodes.Status409Conflict,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(TermWriteOutcome)}. Every outcome must be " +
            "mapped explicitly, or an unmapped one ships as a success."),
    };

    /// <summary>
    /// §6's declared error shape (RFC 7807), built through <see cref="ProblemDetailsFactory"/> so the
    /// <c>traceId</c> is stamped once, in <c>TracedProblemDetailsFactory</c>, rather than by each
    /// action — the same seam every other controller's failure path uses.
    /// </summary>
    private ObjectResult Failure(TermWriteResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(response.Outcome),
            detail: response.Message);

        // The token a client branches on, derived from the outcome in one place so it cannot drift
        // from the status it arrives with. `title` and `detail` are prose written for a person.
        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(TermWriteOutcome outcome) => outcome switch
    {
        TermWriteOutcome.NotFound => "Term not found.",
        TermWriteOutcome.TermCodeExists => "That term code is already in use.",
        TermWriteOutcome.NoSchoolResolved => "No school could be resolved.",
        _ => "The request could not be processed.",
    };
}
