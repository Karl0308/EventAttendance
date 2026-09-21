using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// The institution's classification vocabulary — <c>STUDENT</c>, <c>NAP</c>, <c>ACAD</c> and the five
/// other values the client's access-control export carries — as a list an administrator edits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this controller exists.</b> QA answered Q2 (MDVault #404): the classification list is
/// <em>not</em> fixed, and "Admin can edit them". Nothing in this codebase held the concept at all
/// before this phase — no entity, no column, no DTO — so this is the whole of it.
/// </para>
///
/// <para>
/// <b>Its own route rather than a branch of <c>/students</c>, and that is a decision.</b> A
/// classification is not a property of a student any more than a term is a property of an enrollment:
/// it is a row with its own lifecycle, which outlives every person filed under it and is renamed,
/// retired and merged independently of them. Nesting it under the roster would also have made the
/// merge — which is a statement about two categories — read as an operation on a student.
/// </para>
///
/// <para>
/// <b>There is no person-to-classification assignment on this controller, and its absence is the
/// current state of an open architecture question rather than an oversight.</b> The source carries
/// four category axes and real people hold two classifications at once, so a single scalar assignment
/// would answer with a plausible, non-empty, wrong value — the identical failure ADR-001 D-2 documents
/// for the <c>Course</c>/<c>Section</c> cache. Assignment lands additively once that is ruled on; the
/// vocabulary is correct under every candidate shape, which is why it ships first.
/// </para>
///
/// <para>
/// <b>Permission codes are <c>students.read</c> / <c>students.write</c> rather than a newly minted
/// <c>classifications.*</c> pair, deliberately.</b> The nearest existing fit is the roster: this
/// vocabulary exists only to describe the people in it, and the administrator who curates one curates
/// the other. Minting a pair here would also have changed the approved RBAC grant matrix
/// (<c>RbacSeedTests</c> pins 11 / 11 / 6 / 4) as a side effect of adding a reference list — an
/// authorization decision made by a vocabulary phase, which is exactly the drift D-44 and D-45 were
/// argued over. If classification administration deserves its own code, Phase 6 should mint it where
/// the matrix is the subject. The attribute enforces nothing today (ADR-001 D-6).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/classifications")]
public class ClassificationsController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on every other controller.</summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IClassificationService _classifications;

    public ClassificationsController(IClassificationService classifications) =>
        _classifications = classifications;

    // ------------------------------------------------------------------------------------- reads

    /// <summary>
    /// <c>GET /classifications</c> — the vocabulary, active entries first, then grouped by axis, then
    /// by name.
    /// </summary>
    /// <param name="includeRetired">
    /// <b>Defaults to false, which is what a picker wants and is the less dangerous default of the
    /// two.</b> A picker that offered retired categories would let an administrator file new people
    /// under one that was deliberately withdrawn. A <em>report</em> wants <c>true</c>: a retired
    /// classification still describes everyone who carries it, so a historical count that filtered it
    /// out would under-report them silently — which is the same class of error as reading
    /// <c>students.section</c> instead of the offerings.
    /// </param>
    /// <param name="page">1-based page number, default 1. Out-of-range values are clamped, not refused.</param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200; a larger value is clamped and the response says so.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of classifications, possibly empty.</response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsRead)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsRead)]
    [ProducesResponseType(typeof(PagedResult<ClassificationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ClassificationDto>>> List(
        [FromQuery] bool includeRetired, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => Ok(await _classifications.ListAsync(
            includeRetired, PageRequest.From(page, pageSize), ct));

    /// <summary>
    /// <c>GET /classifications/{id}</c> — one entry, retired or not.
    /// </summary>
    /// <remarks>
    /// Unlike <c>GET /academic/terms</c>, this family has a by-id read, and it earns its place: the
    /// 201 from a create needs a <c>Location</c> that resolves, and a client that holds an id it read
    /// off a person needs to be able to render the name even when the classification has since been
    /// retired.
    /// </remarks>
    /// <param name="id">The classification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The classification.</response>
    /// <response code="404">No such classification in this school.</response>
    [HttpGet("{id:guid}", Name = nameof(GetClassification))]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsRead)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsRead)]
    [ProducesResponseType(typeof(ClassificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClassificationDto>> GetClassification(Guid id, CancellationToken ct)
    {
        var row = await _classifications.GetAsync(id, ct);

        return row is null
            ? Failure(new ClassificationWriteResponse(
                ClassificationWriteOutcome.NotFound, "Classification not found.", null))
            : Ok(row);
    }

    // ------------------------------------------------------------------------------------ writes

    /// <summary>
    /// <c>POST /classifications</c> — add a classification to this school's vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A duplicate name is a 409, not a 500 and not a 400.</b> The payload is well formed and would
    /// be accepted the moment the classification holding that name is renamed or merged, which is what
    /// 409 is for. The check is made before the insert <em>and</em>
    /// <c>UX_Classifications_SchoolId_NameKey</c> is caught behind it, because those are two statements
    /// and a second administrator creating the same name in the same second is decided by the index.
    /// </para>
    ///
    /// <para>
    /// <b>Names collide on their normalized key, not on their text.</b> <c>USA FRIARS</c>,
    /// <c>USA-Friars</c> and <c>usafriars</c> are one classification; the refusal names the row that
    /// holds the key, and says so when that row is retired — a name reported as taken by something
    /// invisible in every picker is a dead end otherwise.
    /// </para>
    /// </remarks>
    /// <param name="request">The display name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The created classification, active. <c>Location</c> names it.</response>
    /// <response code="400">The name is blank, over-length, whitespace-padded, or has no letter or digit.</response>
    /// <response code="409">That name's key is already in use here, or no school could be resolved.</response>
    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(ClassificationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ClassificationDto>> Create(
        [FromBody] ClassificationCreateRequest request, CancellationToken ct)
    {
        var response = await _classifications.CreateAsync(request, ct);
        if (response.Outcome != ClassificationWriteOutcome.Saved) return Failure(response);

        // Unlike POST /academic/terms, the Location header names the row rather than the collection:
        // there is a by-id read here, so it resolves. A 201 whose Location 404s is worse than one
        // naming the list.
        return CreatedAtRoute(
            nameof(GetClassification),
            new { id = response.Classification!.Id },
            response.Classification);
    }

    /// <summary>
    /// <c>PUT /classifications/{id}</c> — rename a classification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the operation the table exists for, and it reassigns nobody.</b> A person's
    /// classification is held by id, so correcting <c>SUPERVISORY/MANAGERIAL</c> to
    /// <c>Supervisory / Managerial</c> changes one row. Had the vocabulary been a string column on the
    /// roster, this would have been a mass <c>UPDATE</c> over every person carrying it, with nothing
    /// to roll back to.
    /// </para>
    ///
    /// <para>
    /// <b>Renaming onto a name another classification already holds is a 409, not a merge.</b> The two
    /// are different intentions and only one of them moves people: combining two categories is
    /// <c>POST /classifications/{id}/merge</c>, which is explicit about which one survives and leaves
    /// a tombstone saying so. A rename that silently absorbed another row would be a merge nobody
    /// asked for, and no record that it happened.
    /// </para>
    ///
    /// <para>
    /// A retired classification can be renamed — the people filed under it still have to be described
    /// correctly in a report — and renaming does not bring it back into pickers. That is
    /// <c>PATCH /classifications/{id}/active</c>.
    /// </para>
    /// </remarks>
    /// <param name="id">The classification.</param>
    /// <param name="request">The new display name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The renamed classification.</response>
    /// <response code="400">The name is blank, over-length, whitespace-padded, or has no letter or digit.</response>
    /// <response code="404">No such classification.</response>
    /// <response code="409">Another classification in this school already holds that name's key.</response>
    [HttpPut("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(ClassificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ClassificationDto>> Rename(
        Guid id, [FromBody] ClassificationRenameRequest request, CancellationToken ct)
    {
        var response = await _classifications.RenameAsync(id, request, ct);

        return response.Outcome == ClassificationWriteOutcome.Saved
            ? Ok(response.Classification)
            : Failure(response);
    }

    /// <summary>
    /// <c>PATCH /classifications/{id}/active</c> — retire a classification, or bring it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Retiring is the safe half of "delete" and is always available.</b> The classification stops
    /// being offered for new assignments; <b>everyone already filed under it keeps it</b>. That is the
    /// acceptance criterion in one sentence, and it is why this route exists separately from
    /// <c>DELETE</c>: the two differ in exactly one respect — whether the row survives — and only one
    /// of them is safe when anything references it.
    /// </para>
    ///
    /// <para>
    /// <b><c>isActive</c> is required, and omitting it is a 400 rather than a default.</b> A missing
    /// JSON member binds to <c>false</c> and would quietly withdraw a category from every picker. The
    /// same refusal, for the same reason, as <c>PATCH /academic/terms/{id}/current</c>.
    /// </para>
    ///
    /// <para>
    /// Idempotent. A classification that was merged away cannot be reactivated — its population moved
    /// to the survivor, so bringing it back would offer an empty category under a familiar name — and
    /// answers 409.
    /// </para>
    /// </remarks>
    /// <param name="id">The classification.</param>
    /// <param name="request"><c>{"isActive": false}</c> to retire, <c>{"isActive": true}</c> to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The classification, with its new flag.</response>
    /// <response code="400"><c>isActive</c> was not supplied.</response>
    /// <response code="404">No such classification.</response>
    /// <response code="409">It was merged into another classification and cannot be reactivated.</response>
    [HttpPatch("{id:guid}/active")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(ClassificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ClassificationDto>> SetActive(
        Guid id, [FromBody] ClassificationActiveRequest request, CancellationToken ct)
    {
        // Bound as bool? and refused here rather than in the service, because "the member was absent"
        // is a fact only the deserializer has — by the time a bool reaches the service it is
        // indistinguishable from a deliberate false, and on this route false withdraws a category.
        if (request.IsActive is not { } isActive)
        {
            return Failure(new ClassificationWriteResponse(
                ClassificationWriteOutcome.ValidationFailed,
                "isActive is required. Send {\"isActive\": false} to retire this classification or " +
                "{\"isActive\": true} to bring it back — a missing member would bind to false and " +
                "silently withdraw it from every picker.",
                null));
        }

        var response = await _classifications.SetActiveAsync(id, isActive, ct);

        return response.Outcome == ClassificationWriteOutcome.Saved
            ? Ok(response.Classification)
            : Failure(response);
    }

    /// <summary>
    /// <c>DELETE /classifications/{id}</c> — remove a classification, but only when nobody holds it,
    /// nothing points at it, and it is not itself the record of a merge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A referenced classification is a 409 and stays exactly where it is.</b> The two ways to make
    /// this verb "succeed" anyway are a cascade and a null-out, and both destroy assignments the
    /// operator never asked to destroy — a data-loss migration issued one HTTP request at a time. The
    /// refusal names the two operations that do what they actually wanted: retire it, which keeps every
    /// assignment, or merge it, which moves them.
    /// </para>
    ///
    /// <para>
    /// <b>A merged-away classification is refused too, and it is the case that looks safest.</b> After
    /// a merge the losing row holds nobody and nothing points at it, so a guard written only as "does
    /// anything reference this" would delete it happily — and with it the only record that those
    /// people were ever somewhere else. Deleting it makes them indistinguishable from people who were
    /// always on the survivor, and makes the merge unexplainable.
    /// </para>
    ///
    /// <para>
    /// <b>Why a delete exists here when <c>/academic/terms</c> deliberately has none.</b> A term always
    /// has an import behind it. A classification typo'd into the vocabulary thirty seconds ago has
    /// nothing behind it, and retiring it would leave permanent clutter in a list of eight real
    /// categories that every picker then has to filter. The unreferenced case is the only case where
    /// deleting destroys nothing, so it is the only case allowed.
    /// </para>
    ///
    /// <para>
    /// <b>200 with the deleted row rather than 204.</b> The body is the last description of something
    /// that no longer exists — an administrator who deleted the wrong row can re-create it from the
    /// response without going to a backup, and a 204 would have thrown that away to save a few bytes.
    /// </para>
    /// </remarks>
    /// <param name="id">The classification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The deleted classification, as it was.</response>
    /// <response code="404">No such classification.</response>
    /// <response code="409">
    /// Somebody holds it, something points at it, it is a merged-away classification whose row is the
    /// only record that the merge happened, or it is one of the eight seeded values, which the startup
    /// seed would re-create — retire those instead. It was not deleted.
    /// </response>
    [HttpDelete("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(ClassificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ClassificationDto>> Delete(Guid id, CancellationToken ct)
    {
        var response = await _classifications.DeleteAsync(id, ct);

        return response.Outcome == ClassificationWriteOutcome.Saved
            ? Ok(response.Classification)
            : Failure(response);
    }

    /// <summary>
    /// <c>POST /classifications/{id}/merge</c> — collapse two classifications into one, moving every
    /// assignment onto the survivor and deleting nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what an administrator reaches for when the delete is refused, and the seeded
    /// vocabulary guarantees they will.</b> The eight starting values came out of an access-control
    /// export that spells things inconsistently, so the first duplicate is a matter of time — and
    /// without a merge the only recoveries are "retire one and leave the population split across two
    /// rows" or "delete it", which the guard correctly refuses.
    /// </para>
    ///
    /// <para>
    /// <c>{id}</c> is the classification being collapsed and <c>intoClassificationId</c> is the one
    /// that survives. Both halves come back in the response, because the operator needs to see both:
    /// the survivor is what the population is filed under now, and the retired row is the tombstone
    /// proving nothing was thrown away.
    /// </para>
    ///
    /// <para>
    /// <b>Refused when it would build a chain or strand a population:</b> merging into a retired
    /// classification, into one that has itself been merged away, or merging a row that is already a
    /// tombstone. Merging a classification into itself is a 400 — no state of the database makes that
    /// request meaningful.
    /// </para>
    /// </remarks>
    /// <param name="id">The classification to collapse. It ends up retired and tombstoned.</param>
    /// <param name="request">The surviving classification's id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The survivor and the retired row.</response>
    /// <response code="400"><c>intoClassificationId</c> was not supplied, or named the same row.</response>
    /// <response code="404">One of the two ids names no classification in this school.</response>
    /// <response code="409">The merge would strand a population or build a chain of tombstones.</response>
    [HttpPost("{id:guid}/merge")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(ClassificationMergeResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ClassificationMergeResult>> Merge(
        Guid id, [FromBody] ClassificationMergeRequest request, CancellationToken ct)
    {
        // Nullable and refused here for the reason PATCH /active's isActive is: an omitted member
        // binds to Guid.Empty, which is indistinguishable from a caller who meant it, and "no such
        // classification" is the wrong thing to tell someone who simply did not say.
        if (request.IntoClassificationId is not { } into)
        {
            return FailureFrom(
                ClassificationWriteOutcome.ValidationFailed,
                "intoClassificationId is required. Send the id of the classification that should " +
                "survive the merge — this route moves people between categories, so there is no " +
                "sensible default.");
        }

        var response = await _classifications.MergeAsync(id, into, ct);

        if (response.Outcome != ClassificationWriteOutcome.Saved)
            return FailureFrom(response.Outcome, response.Message);

        return Ok(new ClassificationMergeResult(
            response.Survivor!, response.Merged!, response.StudentsRepointed, response.Message));
    }

    // -------------------------------------------------------------------------------- translation

    /// <summary>
    /// The status-code contract for the whole surface, as one total function over
    /// <see cref="ClassificationWriteOutcome"/>.
    ///
    /// <para>
    /// <b>Every member is listed instead of a <c>_ =&gt; Ok(...)</c> fall-through</b>, for the reason
    /// <c>AcademicController.StatusCodeFor</c>, <c>StudentsController.StatusCodeFor</c> and
    /// <c>AttendanceController.StatusCodeFor</c> all record: a discard arm made "an outcome nobody
    /// mapped" indistinguishable from "an outcome that means success", and shipped a rejection as a
    /// 200. The final arm has to exist — C# does not treat a fully enumerated enum switch as
    /// exhaustive — but it throws rather than guessing.
    /// </para>
    ///
    /// <para>
    /// <b>The 400/409 split is the same one every other surface draws.</b> A blank name is wrong under
    /// every circumstance and the fix is to change the request. A taken name, a referenced row, an
    /// unmergeable pair — each rejects a payload that is entirely well formed and would be accepted
    /// once the state of the vocabulary changes. <see cref="ClassificationWriteOutcome.InUse"/> in
    /// particular is the specific thing this phase promises will never be a 500 and never a silent
    /// cascade.
    /// </para>
    /// </summary>
    internal static int StatusCodeFor(ClassificationWriteOutcome outcome) => outcome switch
    {
        ClassificationWriteOutcome.Saved => StatusCodes.Status200OK,

        // The URL claims the classification exists.
        ClassificationWriteOutcome.NotFound => StatusCodes.Status404NotFound,

        // The caller's payload: a name outside the column rules, an omitted required member, or a
        // classification merged into itself.
        ClassificationWriteOutcome.ValidationFailed => StatusCodes.Status400BadRequest,

        // The request is fine; the state of the vocabulary forbids it.
        ClassificationWriteOutcome.NameExists
            or ClassificationWriteOutcome.NoSchoolResolved
            or ClassificationWriteOutcome.InUse
            or ClassificationWriteOutcome.NotMergeable
            or ClassificationWriteOutcome.CrossAxis
            or ClassificationWriteOutcome.SeedProtected => StatusCodes.Status409Conflict,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(ClassificationWriteOutcome)}. Every outcome " +
            "must be mapped explicitly, or an unmapped one ships as a success."),
    };

    /// <summary>
    /// §6's declared error shape (RFC 7807), built through <see cref="ProblemDetailsFactory"/> so the
    /// <c>traceId</c> is stamped once, in <c>TracedProblemDetailsFactory</c>, rather than by each
    /// action — the same seam every other controller's failure path uses.
    /// </summary>
    private ObjectResult Failure(ClassificationWriteResponse response) =>
        FailureFrom(response.Outcome, response.Message);

    private ObjectResult FailureFrom(ClassificationWriteOutcome outcome, string message)
    {
        var status = StatusCodeFor(outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(outcome), detail: message);

        // The token a client branches on, derived from the outcome in one place so it cannot drift
        // from the status it arrives with. `title` and `detail` are prose written for a person.
        problem.Extensions[ErrorCodeProperty] = outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(ClassificationWriteOutcome outcome) => outcome switch
    {
        ClassificationWriteOutcome.NotFound => "Classification not found.",
        ClassificationWriteOutcome.NameExists => "That classification name is already in use.",
        ClassificationWriteOutcome.NoSchoolResolved => "No school could be resolved.",
        ClassificationWriteOutcome.InUse => "That classification is still in use.",
        ClassificationWriteOutcome.NotMergeable => "Those classifications cannot be merged.",
        ClassificationWriteOutcome.CrossAxis => "Those classifications are on different axes.",
        ClassificationWriteOutcome.SeedProtected => "That classification is part of the seeded vocabulary.",
        _ => "The request could not be processed.",
    };
}

/// <summary>
/// The body of a successful merge: both rows, and the sentence describing what happened.
/// </summary>
/// <remarks>
/// <b>An API-layer shape rather than a <c>ClassificationMergeResponse</c> on the wire</b>, because that
/// record carries the outcome enum the controller has already translated into a status code. Publishing
/// it would put two answers to "did this succeed" in one response, and a client that read the wrong one
/// would be right most of the time.
/// </remarks>
/// <param name="Survivor">The classification the population is filed under now.</param>
/// <param name="Merged">
/// The retired, tombstoned row. <b>Present precisely because it still exists</b> — a merge that
/// returned only the survivor would look exactly like a merge that had deleted the loser.
/// </param>
/// <param name="StudentsRepointed">
/// How many assignments moved onto the survivor. <b>The number that lets an operator catch a merge
/// they cannot undo</b> — "moved 271 people" against an expectation of three names the wrong pair
/// while they still remember what they clicked.
/// </param>
/// <param name="Message">What happened, in a sentence an administrator can read.</param>
public record ClassificationMergeResult(
    ClassificationDto Survivor, ClassificationDto Merged, int StudentsRepointed, string Message);
