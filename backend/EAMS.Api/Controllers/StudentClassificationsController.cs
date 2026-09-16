using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// <b>Who is classified as what.</b> The Add/Edit form's classification field, and the write surface the
/// vocabulary phase deliberately shipped without.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nested under the student because an assignment is a fact about a person.</b> The vocabulary is
/// not — it lives at <c>/classifications</c>, has its own lifecycle, and outlives everybody filed under
/// it. The two surfaces are the two halves of the same feature and they are deliberately not the same
/// controller: renaming a category must not read as an operation on a student, and classifying a student
/// must not read as an operation on the category.
/// </para>
///
/// <para>
/// <b>The classification is the path segment and there is no request body on any route here.</b> That
/// falls out of what the operation is: the resource is "this person's assignment of this
/// classification", <c>PUT</c> makes it exist and <c>DELETE</c> makes it not, both idempotently. It also
/// removes a hazard the sibling surfaces had to write around — a body member that binds to
/// <c>Guid.Empty</c> when it is omitted is indistinguishable from a caller who meant it, which is why
/// <c>POST /classifications/{id}/merge</c> takes a nullable and refuses the default.
/// </para>
///
/// <para>
/// <b>The axis never appears in a URL or a payload, and that is the invariant rather than a
/// simplification.</b> <c>StudentClassifications.Axis</c> is denormalized from
/// <c>Classifications.Axis</c> so <c>UX_StudentClassifications_Student_Axis</c> can exist at all, and
/// the composite foreign key <c>(ClassificationId, Axis)</c> → <c>Classifications(Id, Axis)</c> makes a
/// row that disagrees with its parent unwritable. A caller-supplied axis could therefore only ever be
/// right or be a 547; taking it from the classification means the question never arises.
/// </para>
///
/// <para>
/// <b>Assigning replaces within an axis rather than adding, and the response says so.</b> QA answered Q2
/// that classification is a multiple selection — one value per axis, across several axes — so giving
/// somebody <c>ACAD</c> when they are <c>NAP</c> removes <c>NAP</c>. That is correct and requested and
/// invisible in a payload that shows only the new state, so <c>replacedClassificationId</c> names what
/// went.
/// </para>
///
/// <para>
/// <b>Permission codes are <c>students.read</c> / <c>students.write</c> rather than a newly minted
/// <c>classifications.*</c> pair</b>, for the reason <c>ClassificationsController</c> records at length:
/// the nearest existing fit is the roster, and minting a code here would change the approved RBAC grant
/// matrix (<c>RbacSeedTests</c> pins 11 / 11 / 6 / 4) as a side effect of adding a form field. The
/// attribute enforces nothing today (ADR-001 D-6).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/students/{studentId:guid}/classifications")]
public class StudentClassificationsController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on every other controller.</summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IStudentClassificationService _assignments;

    public StudentClassificationsController(IStudentClassificationService assignments) =>
        _assignments = assignments;

    /// <summary>
    /// <c>GET /students/{studentId}/classifications</c> — everything this person is classified as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Retired classifications are included and must be rendered.</b> Retiring a category withdraws
    /// it from pickers and leaves every existing assignment standing, so a person can hold a withdrawn
    /// one indefinitely — and a form that filtered it out of its own display would blank it on the next
    /// save. Each entry carries <c>isActive</c> so a picker can show it as held-but-not-offered.
    /// </para>
    /// </remarks>
    /// <param name="studentId">The student.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The person's classifications, ordered by axis then name. Possibly empty.</response>
    /// <response code="404">No such student.</response>
    [HttpGet]
    [HasPermissionNotEnforced(EamsPermissions.StudentsRead)]
    [ProducesResponseType(typeof(StudentClassificationsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StudentClassificationsDto>> List(
        Guid studentId, CancellationToken ct)
    {
        var held = await _assignments.GetAsync(studentId, ct);

        return held is null
            ? Failure(new StudentClassificationWriteResponse(
                StudentClassificationWriteOutcome.StudentNotFound, "Student not found.", null, null))
            : Ok(held);
    }

    /// <summary>
    /// <c>PUT /students/{studentId}/classifications/{classificationId}</c> — give this person this
    /// classification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent, and a no-op is a 200.</b> Assigning a classification somebody already holds
    /// changes nothing and does not bump an audit column, so a double-submitted form is safe — and so is
    /// re-sending a set that a retirement has touched since it was read, which is why the "already
    /// holds it" case is decided before the retired one.
    /// </para>
    ///
    /// <para>
    /// <b>It replaces within the classification's axis.</b> A person holds at most one classification
    /// per axis, so this displaces whatever was in that slot and leaves every other axis alone. The
    /// response returns their whole set, so a client can see that the other axes really were untouched,
    /// and <c>replacedClassificationId</c> names the displaced one.
    /// </para>
    ///
    /// <para>
    /// <b>A retired classification is a 409 and a merged-away one is a different 409.</b> The two have
    /// different remedies — reactivate it or pick another, versus assign the survivor it was merged into
    /// — so they carry different <c>code</c> tokens and the merged one names the survivor's id in its
    /// message.
    /// </para>
    /// </remarks>
    /// <param name="studentId">The student.</param>
    /// <param name="classificationId">The classification to give them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The person's classifications after the write, and what was replaced.</response>
    /// <response code="404">
    /// No such student, no such classification, or the two belong to different schools.
    /// </response>
    /// <response code="409">
    /// The classification is retired or was merged away, or somebody else set this person's
    /// classification on the same axis at the same moment. Nothing was changed.
    /// </response>
    [HttpPut("{classificationId:guid}")]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(StudentClassificationWriteResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StudentClassificationWriteResult>> Assign(
        Guid studentId, Guid classificationId, CancellationToken ct) =>
        Result(await _assignments.AssignAsync(studentId, classificationId, ct));

    /// <summary>
    /// <c>DELETE /students/{studentId}/classifications/{classificationId}</c> — take this classification
    /// off this person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The classification itself is untouched.</b> It is shared by everybody filed under it, so a
    /// clear that reached it would uncategorise every one of them — the data-loss shape
    /// <c>DELETE /classifications/{id}</c> is guarded against, and it would be no better arriving one
    /// student at a time. Only the assignment row goes.
    /// </para>
    ///
    /// <para>
    /// <b>200 with the remaining classifications rather than 204</b>, matching what <c>PUT</c> returns:
    /// the axes are the point of this surface, and the one thing a client needs to see after clearing
    /// one is that the others survived. A 204 would have forced a second round trip to find that out.
    /// </para>
    ///
    /// <para>
    /// The classification's own state is irrelevant here — a retired or merged-away category can always
    /// be cleared, or somebody could be permanently stuck holding one.
    /// </para>
    ///
    /// <para>
    /// <b>It can answer 409, which a <c>DELETE</c> usually cannot.</b> Clearing takes locks on the same
    /// row an assignment does, by a different index — the clear filters
    /// <c>(StudentId, ClassificationId)</c> while an assign uses
    /// <c>UX_StudentClassifications_Student_Axis</c> — so the two can deadlock, and the victim is
    /// answered as the conflict it is rather than as a server fault. Nothing was removed when that
    /// happens; the request can simply be repeated.
    /// </para>
    /// </remarks>
    /// <param name="studentId">The student.</param>
    /// <param name="classificationId">The classification to take off them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The person's remaining classifications.</response>
    /// <response code="404">
    /// No such student, or this person does not hold that classification — including on a second delete.
    /// </response>
    /// <response code="409">
    /// Another request was changing this person's classifications at the same moment and this one was
    /// rolled back to break the tie. Nothing was removed; re-read and try again.
    /// </response>
    [HttpDelete("{classificationId:guid}")]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(StudentClassificationWriteResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StudentClassificationWriteResult>> Clear(
        Guid studentId, Guid classificationId, CancellationToken ct) =>
        Result(await _assignments.ClearAsync(studentId, classificationId, ct));

    // -------------------------------------------------------------------------------- translation

    private ActionResult<StudentClassificationWriteResult> Result(
        StudentClassificationWriteResponse response)
    {
        if (response.Outcome != StudentClassificationWriteOutcome.Saved) return Failure(response);

        var held = response.Classifications!;

        return Ok(new StudentClassificationWriteResult(
            held.StudentId, held.Classifications, response.ReplacedClassificationId, response.Message));
    }

    /// <summary>
    /// The status-code contract for this surface, as one total function over
    /// <see cref="StudentClassificationWriteOutcome"/>.
    ///
    /// <para>
    /// <b>Every member is listed instead of a <c>_ =&gt; Ok(...)</c> fall-through</b>, for the reason
    /// every other <c>StatusCodeFor</c> on this API records: a discard arm made "an outcome nobody
    /// mapped" indistinguishable from "an outcome that means success", and shipped a rejection as a 200.
    /// </para>
    ///
    /// <para>
    /// <b>The 404/409 split is the same one every other surface draws.</b> The four 404s all name
    /// something a URL claims exists — the student, the classification, a pair that is in one tenant,
    /// an assignment this person holds. The three 409s each reject a request that is entirely well
    /// formed and would be accepted once the state of the vocabulary or the roster changes.
    /// </para>
    /// </summary>
    internal static int StatusCodeFor(StudentClassificationWriteOutcome outcome) => outcome switch
    {
        StudentClassificationWriteOutcome.Saved => StatusCodes.Status200OK,

        // Each names something the URL claims: the student, the classification, the pair, or the
        // assignment between them. CrossSchool is a 404 rather than a 409 because there is no state of
        // the database in which that pair is assignable, and saying "it exists, elsewhere" is a
        // disclosure — the same answer ClassificationService.MergeAsync gives a cross-school merge.
        StudentClassificationWriteOutcome.StudentNotFound
            or StudentClassificationWriteOutcome.ClassificationNotFound
            or StudentClassificationWriteOutcome.CrossSchool
            or StudentClassificationWriteOutcome.NotAssigned => StatusCodes.Status404NotFound,

        // The request is fine; the state of the vocabulary or of this person's axis forbids it. Each
        // would be accepted after a reactivation, a different choice, or a re-read.
        StudentClassificationWriteOutcome.ClassificationRetired
            or StudentClassificationWriteOutcome.ClassificationMerged
            or StudentClassificationWriteOutcome.ConcurrentAssignment => StatusCodes.Status409Conflict,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(StudentClassificationWriteOutcome)}. Every " +
            "outcome must be mapped explicitly, or an unmapped one ships as a success."),
    };

    /// <summary>
    /// §6's declared error shape (RFC 7807), built through <see cref="ProblemDetailsFactory"/> so the
    /// <c>traceId</c> is stamped once, in <c>TracedProblemDetailsFactory</c>, rather than by each action.
    /// </summary>
    private ObjectResult Failure(StudentClassificationWriteResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(response.Outcome),
            detail: response.Message);

        // The token a client branches on, derived from the outcome in one place so it cannot drift from
        // the status it arrives with. `title` and `detail` are prose written for a person.
        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(StudentClassificationWriteOutcome outcome) => outcome switch
    {
        StudentClassificationWriteOutcome.StudentNotFound => "Student not found.",
        StudentClassificationWriteOutcome.ClassificationNotFound => "Classification not found.",
        StudentClassificationWriteOutcome.CrossSchool => "That pair belongs to two different schools.",
        StudentClassificationWriteOutcome.NotAssigned => "This person does not hold that classification.",
        StudentClassificationWriteOutcome.ClassificationRetired => "That classification is retired.",
        StudentClassificationWriteOutcome.ClassificationMerged => "That classification was merged away.",
        StudentClassificationWriteOutcome.ConcurrentAssignment =>
            "Somebody else changed this person's classification on that axis.",
        _ => "The request could not be processed.",
    };
}

/// <summary>
/// The body of a successful assign or clear: what the person is classified as now, and what the write
/// displaced.
/// </summary>
/// <remarks>
/// <b>An API-layer shape rather than a <c>StudentClassificationWriteResponse</c> on the wire</b>, for the
/// reason <c>ClassificationMergeResult</c> records: that record carries the outcome enum the controller
/// has already translated into a status code, and publishing it would put two answers to "did this
/// succeed" in one response — a client that read the wrong one would be right most of the time.
/// </remarks>
/// <param name="StudentId">The person.</param>
/// <param name="Classifications">
/// Their full set after the write, ordered by axis then name. <b>All of it, not just the row that
/// changed</b> — the point of the axes is that a write to one leaves the others alone, and this is the
/// only way a client can see that it did.
/// </param>
/// <param name="ReplacedClassificationId">
/// What this assignment displaced on its axis, or null if the slot was empty or nothing was written.
/// <b>Present because the replacement is otherwise silent:</b> an operator who thought they were adding
/// a second personnel category needs to see which one went.
/// </param>
/// <param name="Message">What happened, in a sentence an administrator can read.</param>
public record StudentClassificationWriteResult(
    Guid StudentId,
    IReadOnlyList<StudentClassificationDto> Classifications,
    Guid? ReplacedClassificationId,
    string Message);
