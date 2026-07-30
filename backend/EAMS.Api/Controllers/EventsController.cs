using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

// Permission codes per Technical Plan §6.3 (ADR-001 D-6). The attribute enforces nothing.
[ApiController]
[Route("api/v1/events")]
public class EventsController : ControllerBase
{
    private readonly IEventService _events;
    public EventsController(IEventService events) => _events = events;

    // ---------------------------------------------------------------------------------- reads

    /// <summary><c>GET /events</c> — the event list, optionally filtered by status.</summary>
    /// <param name="status">
    /// <c>Draft</c>, <c>Open</c>, <c>Closed</c> or <c>Cancelled</c>. A capture client wants
    /// <c>?status=Open</c> — a tap against any other status is <c>400 EventNotOpen</c>.
    /// </param>
    /// <param name="page">
    /// 1-based page number, default 1. Out-of-range values are clamped, never refused; the response
    /// echoes the page actually served.
    /// </param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200 — a larger value is clamped to the maximum and the
    /// response says so in its own <c>pageSize</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of matching events, possibly empty.</response>
    [HttpGet]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(PagedResult<EventDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<EventDto>>> List(
        [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => Ok(await _events.ListAsync(status, PageRequest.From(page, pageSize), ct));

    /// <summary><c>GET /events/{id}</c> — one event.</summary>
    /// <remarks>
    /// The event's own record only. Its counts are <c>GET /events/{id}/summary</c> and its audience is
    /// <c>GET /events/{id}/roster</c> — kept apart so rendering an event detail page does not pay for
    /// aggregates the caller may not be showing.
    /// </remarks>
    /// <param name="id">The event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The event.</response>
    /// <response code="404">No such event, or it is soft-deleted.</response>
    [HttpGet("{id:guid}")]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventDto>> Get(Guid id, CancellationToken ct)
    {
        var e = await _events.GetAsync(id, ct);
        return e is null ? NotFound() : Ok(e);
    }

    /// <summary>
    /// <c>GET /events/{id}/summary</c> — the §6.7/§12 Event Attendance Summary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>expected</c> is the invited population, not the number who tapped</b> (ADR-003 D-19):
    /// students in the event's attached groups plus its individually attached students, de-duplicated.
    /// Zero means no audience is attached, and nothing falls back to a row count in that case — a
    /// denominator that silently changes definition depending on whether a table is empty is worse than
    /// one that is honestly absent.
    /// </para>
    ///
    /// <para>
    /// <b><c>attendanceRate</c> cannot exceed 100</b>, structurally rather than by clamping: numerator
    /// and denominator are both computed over the expected set. Walk-ins are therefore not in it — they
    /// are counted as <c>unexpected</c>, and <c>GET /events/{id}/roster</c> names them.
    /// </para>
    ///
    /// <para>
    /// <b>This read is unbounded, and that is the difference from the identical object inside
    /// <c>GET /attendance/live/{eventId}</c>.</b> There is no cursor here and therefore no ceiling: it
    /// counts every committed row at the moment it runs. The live endpoint bounds the same counters by
    /// its own cursor ceiling so the headline number cannot exceed the list of names under it; the two
    /// converge as soon as any in-flight write commits.
    /// </para>
    /// </remarks>
    /// <param name="id">The event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The summary.</response>
    /// <response code="404">No such event, or it is soft-deleted.</response>
    //
    // JUDGEMENT CALL, flagged rather than buried: §6.7 assigns `reports.read` to the equivalent
    // report (`GET /reports/event/{eventId}/summary`), but this endpoint lives on the events resource
    // and returns an event's own counts, so `events.read` is what a caller who can already open the
    // event would expect to need. Decided this way because the alternative gates a number the event
    // detail page renders inline behind a second, unrelated permission — a UI that shows the event
    // and an empty summary box. If §12's reporting module later moves this under /reports, it should
    // move to `reports.read` with it; that is one attribute, and Phase 6 will be looking at all of
    // them anyway.
    [HttpGet("{id:guid}/summary")]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(EventSummaryDto), StatusCodes.Status200OK)]
    // typeof: [ApiController] turns NotFound() into a ProblemDetails, so declaring the status alone
    // publishes a 404 the document says carries no body — while 4e's <response> paragraph above
    // describes one.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventSummaryDto>> Summary(Guid id, CancellationToken ct)
    {
        var summary = await _events.GetSummaryAsync(id, ct);
        return summary is null ? NotFound() : Ok(summary);
    }

    /// <summary>
    /// §6.3 <c>GET /events/{id}/roster</c> — expected versus actual, and the source of §12's Absentee
    /// Report. <c>events.read</c> for the same reason the summary carries it.
    /// </summary>
    [HttpGet("{id:guid}/roster")]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(EventRosterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventRosterDto>> Roster(Guid id, CancellationToken ct)
    {
        var roster = await _events.GetRosterAsync(id, ct);
        return roster is null ? NotFound() : Ok(roster);
    }

    // --------------------------------------------------------------------------------- writes

    /// <summary>
    /// §6.3 <c>POST /events</c>. Always creates a <c>Draft</c>; <c>PATCH /events/{id}/status</c> is the
    /// only way to move it, which is what makes the roster freeze unskippable.
    /// </summary>
    [HttpPost]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventDto>> Create(
        [FromBody] EventWriteRequest request, CancellationToken ct)
    {
        var response = await _events.CreateAsync(request, ct);
        if (response.Outcome != EventWriteOutcome.Saved) return Failure(response);

        return CreatedAtAction(nameof(Get), new { id = response.Event!.Id }, response.Event);
    }

    /// <summary><c>PUT /events/{id}</c> — update an event.</summary>
    /// <remarks>
    /// A full replace of the event's own fields; the audience is not part of this body and moves through
    /// the <c>attendees</c> routes instead. A status transition the event cannot make is <c>409</c>
    /// rather than <c>400</c> — the request is well-formed, the event is simply not in a state that
    /// allows it.
    /// </remarks>
    /// <param name="id">The event.</param>
    /// <param name="request">The new field values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The updated event.</response>
    /// <response code="400">The body is invalid.</response>
    /// <response code="404">No such event, or it is soft-deleted.</response>
    /// <response code="409">The event's current status does not allow this change.</response>
    [HttpPut("{id:guid}")]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventDto>> Update(
        Guid id, [FromBody] EventWriteRequest request, CancellationToken ct)
    {
        var response = await _events.UpdateAsync(id, request, ct);
        return response.Outcome == EventWriteOutcome.Saved ? Ok(response.Event) : Failure(response);
    }

    /// <summary>
    /// §6.3 <c>PATCH /events/{id}/status</c> — Open / Close / Cancel.
    ///
    /// <para>
    /// Closing materializes the expected roster as <c>Absent</c> records; the response message says how
    /// many. An illegal transition is a 400 naming what <em>is</em> reachable, never a silent write.
    /// </para>
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventDto>> ChangeStatus(
        Guid id, [FromBody] EventStatusRequest request, CancellationToken ct)
    {
        var response = await _events.ChangeStatusAsync(id, request.Status, ct);
        return response.Outcome == EventWriteOutcome.Saved ? Ok(response.Event) : Failure(response);
    }

    /// <summary>§6.3 <c>DELETE /events/{id}</c> — soft (§4.5 <c>IsDeleted</c>).</summary>
    [HttpDelete("{id:guid}")]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var response = await _events.DeleteAsync(id, ct);
        return response.Outcome == EventWriteOutcome.Saved ? NoContent() : Failure(response);
    }

    // ------------------------------------------------------------------------------- audience

    /// <summary>
    /// §6.3 <c>POST /events/{id}/attendees</c> — associate <c>{studentGroupIds[], studentIds[]}</c>.
    /// Idempotent; the response reports what was attached versus what already was.
    /// </summary>
    [HttpPost("{id:guid}/attendees")]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(EventAudienceResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventAudienceResultDto>> AttachAudience(
        Guid id, [FromBody] EventAudienceRequest request, CancellationToken ct)
    {
        var response = await _events.AttachAudienceAsync(id, request, ct);
        return response.Outcome == EventWriteOutcome.Saved
            ? Ok(response.Result)
            : AudienceFailure(response);
    }

    /// <summary>
    /// Detaches one group. Sub-resource <c>DELETE</c>s rather than a body on <c>DELETE
    /// /attendees</c>: a request body on <c>DELETE</c> is legal but is dropped by enough proxies and
    /// client libraries to be a poor contract to publish, and the plan does not define removal at all.
    ///
    /// <para>
    /// 204 whether or not the group was attached — the postcondition holds either way, so a retry is
    /// safe. A missing <em>event</em> is still 404: that one is named by the URL.
    /// </para>
    /// </summary>
    [HttpDelete("{id:guid}/attendees/groups/{studentGroupId:guid}")]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DetachGroup(
        Guid id, Guid studentGroupId, CancellationToken ct)
    {
        var response = await _events.DetachGroupAsync(id, studentGroupId, ct);
        return response.Outcome == EventWriteOutcome.Saved ? NoContent() : AudienceFailure(response);
    }

    /// <summary>
    /// <c>DELETE /events/{id}/attendees/students/{studentId}</c> — detach one individually-attached
    /// student from the event's audience.
    /// </summary>
    /// <remarks>
    /// Idempotent, on the same reasoning as <see cref="DetachGroup"/>: detaching a student who is not
    /// attached is <c>204</c>, not <c>404</c>, so a retry is safe. A missing <em>event</em> is still
    /// <c>404</c> — that one is named by the URL.
    ///
    /// <para>
    /// Spelled out rather than left as <c>&lt;inheritdoc&gt;</c>: Swashbuckle does not resolve that tag,
    /// so this operation shipped with an empty description while looking documented in the source.
    /// </para>
    /// </remarks>
    /// <param name="id">The event.</param>
    /// <param name="studentId">The student to detach.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Detached, or was not attached.</response>
    /// <response code="404">No such event.</response>
    /// <response code="409">The event's status does not allow an audience change.</response>
    [HttpDelete("{id:guid}/attendees/students/{studentId:guid}")]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DetachStudent(Guid id, Guid studentId, CancellationToken ct)
    {
        var response = await _events.DetachStudentAsync(id, studentId, ct);
        return response.Outcome == EventWriteOutcome.Saved ? NoContent() : AudienceFailure(response);
    }

    // --------------------------------------------------------------------------------- mapping

    /// <summary>
    /// The §6 status-code contract for the events write surface, as one total function over
    /// <see cref="EventWriteOutcome"/>.
    ///
    /// <para>
    /// <b>Every member is listed instead of a <c>_ =&gt; Ok(...)</c> fall-through</b>, for the reason
    /// <c>AttendanceController.StatusCodeFor</c> records at length: a discard arm made "an outcome
    /// nobody mapped" indistinguishable from "an outcome that means success", and shipped a rejection
    /// as a 200. The final arm has to exist — C# does not treat a fully enumerated enum switch as
    /// exhaustive — but it throws rather than guessing, and
    /// <c>EventsControllerMappingTests</c> turns that into a test failure instead of a 500.
    /// </para>
    ///
    /// <para>
    /// <b>Why <see cref="EventWriteOutcome.EventLocked"/> is 409 while
    /// <see cref="EventWriteOutcome.IllegalTransition"/> is 400.</b> The split is the one
    /// <c>SisImportController</c> already draws. A bad transition names a target that is not reachable
    /// from this event's status under any circumstances — the request is wrong, and retrying it
    /// unchanged will always be wrong. A locked event rejects a payload that is entirely well formed
    /// and would be accepted against the same event in another state; that is a conflict with the state
    /// of the resource, which is what 409 is for.
    /// </para>
    /// </summary>
    internal static int StatusCodeFor(EventWriteOutcome outcome) => outcome switch
    {
        EventWriteOutcome.Saved => StatusCodes.Status200OK,

        EventWriteOutcome.NotFound => StatusCodes.Status404NotFound,

        // All four are the caller's payload: a field outside §4.5's rules, a transition the §4.5 status
        // graph does not have, or an id that resolves to nothing in this event's school.
        EventWriteOutcome.ValidationFailed
            or EventWriteOutcome.IllegalTransition
            or EventWriteOutcome.UnknownReference
            or EventWriteOutcome.NotACohort => StatusCodes.Status400BadRequest,

        // The request is fine; the resource's state forbids it. See the method remarks.
        EventWriteOutcome.EventLocked
            or EventWriteOutcome.NoSchoolResolved => StatusCodes.Status409Conflict,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(EventWriteOutcome)}. Every outcome must be " +
            "mapped explicitly — see EventsControllerMappingTests."),
    };

    /// <summary>
    /// §6's declared error shape (RFC 7807), built through <see cref="ProblemDetailsFactory"/> so the
    /// <c>traceId</c> is stamped once, in <c>TracedProblemDetailsFactory</c>, rather than by each
    /// action — the same seam <c>SisImportController.ImportProblem</c> uses and for the same reason.
    /// </summary>
    private ObjectResult Failure(EventWriteResponse response) => Problem(response.Outcome, response.Message);

    private ObjectResult AudienceFailure(EventAudienceResponse response) =>
        Problem(response.Outcome, response.Message);

    private ObjectResult Problem(EventWriteOutcome outcome, string message)
    {
        var status = StatusCodeFor(outcome);
        return StatusCode(status, ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(outcome), detail: message));
    }

    private static string TitleFor(EventWriteOutcome outcome) => outcome switch
    {
        EventWriteOutcome.NotFound => "Event not found.",
        EventWriteOutcome.IllegalTransition => "That status change is not possible.",
        EventWriteOutcome.EventLocked => "The event's state forbids this change.",
        EventWriteOutcome.UnknownReference => "The audience names something that does not exist.",
        EventWriteOutcome.NotACohort => "That group is not a cohort.",
        EventWriteOutcome.NoSchoolResolved => "No school could be resolved.",
        _ => "The request could not be processed.",
    };
}
