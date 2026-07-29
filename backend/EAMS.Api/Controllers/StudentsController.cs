using EAMS.Api.Authorization;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace EAMS.Api.Controllers;

// Permission codes are Technical Plan §6.2's, action for action. ADR-001 D-6's bargain was that
// endpoints get decorated as they are written so Phase 6 wires enforcement rather than re-deriving
// what each endpoint should have demanded — and the attribute enforces nothing, so this is a
// declaration of intent and not a change in behaviour. See HasPermissionNotEnforcedAttribute.
[ApiController]
[Route("api/v1/students")]
public class StudentsController : ControllerBase
{
    /// <summary>
    /// The machine-readable half of §6's RFC 7807 body.
    ///
    /// <para>
    /// <c>title</c> and <c>detail</c> are written for a person; a client branching on either is
    /// branching on prose that will be reworded. The <c>FieldIsDerived</c> refusal in particular is
    /// something a caller has to <em>handle</em> — strip the derived fields and re-send — so it needs a
    /// stable token, and every other outcome carries one for the same reason rather than making that
    /// one a special case.
    /// </para>
    /// </summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IStudentService _students;
    public StudentsController(IStudentService students) => _students = students;

    // ---------------------------------------------------------------------------------- reads

    /// <summary><c>GET /students</c> — the roster, every filter optional.</summary>
    /// <param name="search">Matches student number or name.</param>
    /// <param name="course">
    /// <b>ADR-001 D-2 derived display cache.</b> A student can sit in several sections at once — twelve
    /// of fifty-two in the real roster do — so this filter cannot represent the truth. It exists for the
    /// admin grid; do not build reporting on it.
    /// </param>
    /// <param name="status"><c>Active</c>, <c>Inactive</c> or <c>Graduated</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The matching students, possibly empty.</response>
    [HttpGet]
    [HasPermissionNotEnforced("students.read")]
    [ProducesResponseType(typeof(IEnumerable<StudentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<StudentDto>>> List(
        [FromQuery] string? search, [FromQuery] string? course, [FromQuery] string? status,
        CancellationToken ct)
        => Ok(await _students.ListAsync(search, course, status, ct));

    [HttpGet("{id:guid}")]
    [HasPermissionNotEnforced("students.read")]
    [ProducesResponseType(typeof(StudentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StudentDto>> Get(Guid id, CancellationToken ct)
    {
        var s = await _students.GetAsync(id, ct);
        return s is null ? NotFound() : Ok(s);
    }

    /// <summary>
    /// <c>GET /students/by-card/{cardUid}</c> — UID→student resolution for the mobile scan screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A card UID <em>is</em> the student number (REGNO).</b> There is no tap-to-bind screen to
    /// build — students arrive card-ready from the roster import.
    /// </para>
    ///
    /// <para>
    /// <b>Normalisation is uppercase with every non-alphanumeric stripped.</b> <c>04:A7:B8:C9</c>,
    /// <c>04-a7-b8-c9</c> and <c>04a7b8c9</c> are one card, stored as <c>04A7B8C9</c>. The server
    /// normalises what you send, so any reader format is accepted — but normalise before any
    /// <em>local</em> cache or comparison, or your dedupe will disagree with ours.
    /// </para>
    /// </remarks>
    /// <param name="cardUid">The raw or normalised UID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The student, with every card they hold.</response>
    /// <response code="404">No <em>active</em> card matches that UID.</response>
    // The service normalizes the UID; callers may pass any reader format.
    //
    // `attendance.capture`, not `students.read` — §6.2 assigns this one endpoint the capture
    // permission because its caller is the reader/mobile scan screen, which must resolve a UID
    // without being trusted to browse the roster. A device API key is scoped to `attendance.capture`
    // alone (§11), so reading it as a student permission would lock the kiosks out of the one lookup
    // they exist to perform.
    //
    // One of the four endpoints a device key gates (Phase 4a design, D-28) — and the only *read* among
    // them, which is why it is worth stating why it is gated at all while the rest of the roster is
    // open. This endpoint resolves a card UID to a named student, a card UID is a student number, and
    // student numbers are sequential and printed on the ID. Left open it is an enumeration oracle over
    // the whole roster; behind a device key it is what a scan screen needs and nothing else.
    [HttpGet("by-card/{cardUid}")]
    [Authorize(AuthenticationSchemes = DeviceKey.AuthenticationScheme, Policy = EamsPermissions.AttendanceCapture)]
    [EnableRateLimiting(CaptureRateLimiting.PolicyName)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceCapture)]
    [ProducesResponseType(typeof(StudentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<StudentDto>> ByCard(string cardUid, CancellationToken ct)
    {
        var s = await _students.GetByCardUidAsync(cardUid, ct);
        return s is null ? NotFound("No active card matches that UID.") : Ok(s);
    }

    // --------------------------------------------------------------------------------- writes

    /// <summary>
    /// §6.2 <c>POST /students</c> — manual roster entry, alongside the §10 bulk import.
    /// </summary>
    [HttpPost]
    [HasPermissionNotEnforced("students.write")]
    [ProducesResponseType(typeof(StudentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StudentDto>> Create(
        [FromBody] StudentWriteRequest request, CancellationToken ct)
    {
        var response = await _students.CreateAsync(request, ct);
        if (response.Outcome != StudentWriteOutcome.Saved) return Failure(response);

        return CreatedAtAction(nameof(Get), new { id = response.Student!.Id }, response.Student);
    }

    /// <summary>
    /// §6.2 <c>PUT /students/{id}</c>. A full replacement of the student's own editable fields.
    ///
    /// <para>
    /// Sending back <c>course</c>, <c>yearLevel</c> or <c>section</c> — which
    /// <c>GET /students/{id}</c> returns — is a 400 with <c>code: "FieldIsDerived"</c>, not a silent
    /// drop. See <see cref="StudentWriteOutcome.FieldIsDerived"/>.
    /// </para>
    /// </summary>
    [HttpPut("{id:guid}")]
    [HasPermissionNotEnforced("students.write")]
    [ProducesResponseType(typeof(StudentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StudentDto>> Update(
        Guid id, [FromBody] StudentWriteRequest request, CancellationToken ct)
    {
        var response = await _students.UpdateAsync(id, request, ct);
        return response.Outcome == StudentWriteOutcome.Saved ? Ok(response.Student) : Failure(response);
    }

    /// <summary>§6.2 <c>DELETE /students/{id}</c> — soft (§4.3 <c>IsDeleted</c>).</summary>
    [HttpDelete("{id:guid}")]
    [HasPermissionNotEnforced("students.write")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var response = await _students.DeleteAsync(id, ct);
        return response.Outcome == StudentWriteOutcome.Saved ? NoContent() : Failure(response);
    }

    // ---------------------------------------------------------------------------------- cards

    /// <summary>
    /// §6.2 <c>POST /students/{id}/cards</c> — assign an RFID card <c>{cardUid, label}</c>.
    ///
    /// <para>
    /// <b>The <c>Location</c> header points at the student, not at the card.</b> §6.2 defines no
    /// <c>GET /cards/{id}</c>, and a 201 whose Location 404s is worse than one that names the resource
    /// the new card is actually visible on — <c>GET /students/{id}</c> returns it in <c>cards[]</c>.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/cards")]
    [HasPermissionNotEnforced("students.write")]
    [ProducesResponseType(typeof(CardDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CardDto>> AddCard(
        Guid id, [FromBody] StudentCardRequest request, CancellationToken ct)
    {
        var response = await _students.AddCardAsync(id, request, ct);
        if (response.Outcome != StudentWriteOutcome.Saved) return CardFailure(response);

        return CreatedAtAction(nameof(Get), new { id }, response.Card);
    }

    /// <summary>
    /// §6.2 <c>DELETE /students/{id}/cards/{cardId}</c> — <b>deactivate</b>. The row survives, because
    /// ADR-001 D-3's whole point is that a reissued card carries the same REGNO and a past tap must
    /// keep resolving to the physical card that produced it.
    ///
    /// <para>
    /// 204 whether or not the card was still active — the postcondition holds either way, so a retry is
    /// safe. A card that belongs to another student is still a 404: this URL claims it belongs to this
    /// one.
    /// </para>
    /// </summary>
    [HttpDelete("{id:guid}/cards/{cardId:guid}")]
    [HasPermissionNotEnforced("students.write")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateCard(Guid id, Guid cardId, CancellationToken ct)
    {
        var response = await _students.DeactivateCardAsync(id, cardId, ct);
        return response.Outcome == StudentWriteOutcome.Saved ? NoContent() : CardFailure(response);
    }

    // --------------------------------------------------------------------------------- mapping

    /// <summary>
    /// The §6 status-code contract for the students write surface, as one total function over
    /// <see cref="StudentWriteOutcome"/>.
    ///
    /// <para>
    /// <b>Every member is listed instead of a <c>_ =&gt; Ok(...)</c> fall-through</b>, for the reason
    /// <c>AttendanceController.StatusCodeFor</c> records at length: a discard arm made "an outcome
    /// nobody mapped" indistinguishable from "an outcome that means success", and shipped a rejection
    /// as a 200. The final arm has to exist — C# does not treat a fully enumerated enum switch as
    /// exhaustive — but it throws rather than guessing, and <c>StudentsControllerMappingTests</c> turns
    /// that into a test failure instead of a 500.
    /// </para>
    ///
    /// <para>
    /// <b>Why the two uniqueness failures are 409 while <c>FieldIsDerived</c> is 400.</b> Same split
    /// <c>EventsController</c> draws. A derived field in the payload is wrong under every
    /// circumstance — no state of the resource would ever accept it, and the fix is to change the
    /// request. A taken student number or an assigned card UID rejects a payload that is entirely well
    /// formed and would be accepted a moment after the conflicting row is deleted or deactivated; that
    /// is a conflict with the state of the resource, which is what 409 is for.
    /// </para>
    /// </summary>
    internal static int StatusCodeFor(StudentWriteOutcome outcome) => outcome switch
    {
        StudentWriteOutcome.Saved => StatusCodes.Status200OK,

        // Both name something the URL claims exists: the student, or that student's card.
        StudentWriteOutcome.NotFound
            or StudentWriteOutcome.CardNotFound => StatusCodes.Status404NotFound,

        // The caller's payload: a field outside §4.3's rules, or a column no request may ever carry.
        StudentWriteOutcome.ValidationFailed
            or StudentWriteOutcome.FieldIsDerived => StatusCodes.Status400BadRequest,

        // The request is fine; the state of the roster forbids it. See the method remarks.
        StudentWriteOutcome.DuplicateStudentNumber
            or StudentWriteOutcome.CardUidInUse
            or StudentWriteOutcome.NoSchoolResolved => StatusCodes.Status409Conflict,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(StudentWriteOutcome)}. Every outcome must be " +
            "mapped explicitly — see StudentsControllerMappingTests."),
    };

    /// <summary>
    /// §6's declared error shape (RFC 7807), built through <see cref="ProblemDetailsFactory"/> so the
    /// <c>traceId</c> is stamped once, in <c>TracedProblemDetailsFactory</c>, rather than by each
    /// action — the same seam <c>EventsController.Problem</c> uses and for the same reason.
    /// </summary>
    private ObjectResult Failure(StudentWriteResponse response) =>
        Problem(response.Outcome, response.Message);

    private ObjectResult CardFailure(StudentCardResponse response) =>
        Problem(response.Outcome, response.Message);

    private ObjectResult Problem(StudentWriteOutcome outcome, string message)
    {
        var status = StatusCodeFor(outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(outcome), detail: message);

        problem.Extensions[ErrorCodeProperty] = outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(StudentWriteOutcome outcome) => outcome switch
    {
        StudentWriteOutcome.NotFound => "Student not found.",
        StudentWriteOutcome.CardNotFound => "Card not found on this student.",
        StudentWriteOutcome.FieldIsDerived => "That field is a derived read-only cache.",
        StudentWriteOutcome.DuplicateStudentNumber => "That student number is already in use.",
        StudentWriteOutcome.CardUidInUse => "That card UID is already active on another student.",
        StudentWriteOutcome.NoSchoolResolved => "No school could be resolved.",
        _ => "The request could not be processed.",
    };
}
