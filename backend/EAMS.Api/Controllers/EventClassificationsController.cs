using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// The institution's event-classification vocabulary — Institutional, Departmental, Organizational, and
/// whatever the client adds — as a list an administrator edits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own route rather than a branch of <c>/events</c>, for the reason
/// <see cref="ClassificationsController"/> is its own route and not a branch of <c>/students</c>:</b> a
/// classification is a row with its own lifecycle, renamed and deactivated independently of any event,
/// that outlives every event filed under it.
/// </para>
///
/// <para>
/// <b>Permission codes are <c>events.read</c> / <c>events.write</c> rather than a newly minted
/// <c>event-classifications.*</c> pair, deliberately.</b> This is the same decision, for the same reason,
/// that <see cref="ClassificationsController"/> records for reusing <c>students.*</c>: the vocabulary
/// exists only to describe events, the administrator who curates one curates the other, and minting a
/// pair here would change the approved RBAC grant matrix (<c>RbacSeedTests</c> pins the counts) as a side
/// effect of adding a reference list — an authorization decision that the RBAC module / Phase 6 should
/// make where the matrix is the subject, not a vocabulary phase. The attribute enforces nothing today
/// (ADR-001 D-6); <c>[Authorize]</c> does the enforcing.
/// </para>
///
/// <para>
/// <b>There is no event-to-classification link on this controller</b>, and its absence is the current
/// state of a dependent module rather than an oversight: the <c>Events.EventClassificationId</c> foreign
/// key and the "classification then audience" event-creation flow land with the Event Audience module
/// that depends on this vocabulary. The vocabulary is correct under that later shape, which is why it
/// ships first — the same order <see cref="ClassificationsController"/> took.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/event-classifications")]
public class EventClassificationsController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on every other controller.</summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IEventClassificationService _classifications;

    public EventClassificationsController(IEventClassificationService classifications) =>
        _classifications = classifications;

    // ------------------------------------------------------------------------------------- reads

    /// <summary>
    /// <c>GET /event-classifications</c> — the vocabulary, active entries first, then by name.
    /// </summary>
    /// <param name="includeInactive">
    /// Defaults to false — what the event-creation picker wants. A report wants <c>true</c>: a deactivated
    /// classification still describes the events recorded under it.
    /// </param>
    /// <param name="page">1-based page number, default 1. Out-of-range values are clamped, not refused.</param>
    /// <param name="pageSize">Rows per page. Default 50, maximum 200; a larger value is clamped.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of event classifications, possibly empty.</response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsRead)]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(PagedResult<EventClassificationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<EventClassificationDto>>> List(
        [FromQuery] bool includeInactive, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => Ok(await _classifications.ListAsync(
            includeInactive, PageRequest.From(page, pageSize), ct));

    /// <summary>
    /// <c>GET /event-classifications/{id}</c> — one entry, active or not.
    /// </summary>
    /// <param name="id">The classification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The classification.</response>
    /// <response code="404">No such classification in this school.</response>
    [HttpGet("{id:guid}", Name = nameof(GetEventClassification))]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsRead)]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(EventClassificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventClassificationDto>> GetEventClassification(
        Guid id, CancellationToken ct)
    {
        var row = await _classifications.GetAsync(id, ct);

        return row is null
            ? Failure(new EventClassificationWriteResponse(
                EventClassificationWriteOutcome.NotFound, "Event classification not found.", null))
            : Ok(row);
    }

    // ------------------------------------------------------------------------------------ writes

    /// <summary>
    /// <c>POST /event-classifications</c> — add a classification to this school's vocabulary.
    /// </summary>
    /// <param name="request">The display name and optional description.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The created classification, active. <c>Location</c> names it.</response>
    /// <response code="400">The name is blank, over-length, whitespace-padded, has no letter or digit, or the description is over-length.</response>
    /// <response code="409">That name's key is already in use here, or no school could be resolved.</response>
    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(EventClassificationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventClassificationDto>> Create(
        [FromBody] EventClassificationCreateRequest request, CancellationToken ct)
    {
        var response = await _classifications.CreateAsync(request, ct);
        if (response.Outcome != EventClassificationWriteOutcome.Saved) return Failure(response);

        return CreatedAtRoute(
            nameof(GetEventClassification),
            new { id = response.Classification!.Id },
            response.Classification);
    }

    /// <summary>
    /// <c>PUT /event-classifications/{id}</c> — change the display name and/or description.
    /// </summary>
    /// <param name="id">The classification.</param>
    /// <param name="request">The new name and description.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The updated classification.</response>
    /// <response code="400">The name or description breaks a column rule.</response>
    /// <response code="404">No such classification.</response>
    /// <response code="409">Another classification in this school already holds that name's key.</response>
    [HttpPut("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(EventClassificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventClassificationDto>> Update(
        Guid id, [FromBody] EventClassificationUpdateRequest request, CancellationToken ct)
    {
        var response = await _classifications.UpdateAsync(id, request, ct);

        return response.Outcome == EventClassificationWriteOutcome.Saved
            ? Ok(response.Classification)
            : Failure(response);
    }

    /// <summary>
    /// <c>PATCH /event-classifications/{id}/active</c> — deactivate a classification, or bring it back.
    /// </summary>
    /// <param name="id">The classification.</param>
    /// <param name="request"><c>{"isActive": false}</c> to deactivate, <c>{"isActive": true}</c> to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The classification, with its new flag.</response>
    /// <response code="400"><c>isActive</c> was not supplied.</response>
    /// <response code="404">No such classification.</response>
    [HttpPatch("{id:guid}/active")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(EventClassificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventClassificationDto>> SetActive(
        Guid id, [FromBody] EventClassificationActiveRequest request, CancellationToken ct)
    {
        // Bound as bool? and refused here rather than in the service, because "the member was absent" is a
        // fact only the deserializer has — by the time a bool reaches the service it is indistinguishable
        // from a deliberate false, and on this route false withdraws a classification from the picker.
        if (request.IsActive is not { } isActive)
        {
            return Failure(new EventClassificationWriteResponse(
                EventClassificationWriteOutcome.ValidationFailed,
                "isActive is required. Send {\"isActive\": false} to deactivate this classification or " +
                "{\"isActive\": true} to bring it back — a missing member would bind to false and " +
                "silently withdraw it from the event-creation picker.",
                null));
        }

        var response = await _classifications.SetActiveAsync(id, isActive, ct);

        return response.Outcome == EventClassificationWriteOutcome.Saved
            ? Ok(response.Classification)
            : Failure(response);
    }

    /// <summary>
    /// <c>DELETE /event-classifications/{id}</c> — remove a classification, but only when no event
    /// references it and it is not one of the seeded three.
    /// </summary>
    /// <param name="id">The classification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The deleted classification, as it was.</response>
    /// <response code="404">No such classification.</response>
    /// <response code="409">An event is classified as it, or it is one of the seeded values the startup seed would re-create — deactivate those instead. It was not deleted.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(EventClassificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventClassificationDto>> Delete(Guid id, CancellationToken ct)
    {
        var response = await _classifications.DeleteAsync(id, ct);

        return response.Outcome == EventClassificationWriteOutcome.Saved
            ? Ok(response.Classification)
            : Failure(response);
    }

    // -------------------------------------------------------------------------------- translation

    /// <summary>
    /// The status-code contract for the whole surface, as one total function — every member listed rather
    /// than a fall-through, for the reason every other controller's <c>StatusCodeFor</c> records: a
    /// discard arm ships an unmapped outcome as a success.
    /// </summary>
    internal static int StatusCodeFor(EventClassificationWriteOutcome outcome) => outcome switch
    {
        EventClassificationWriteOutcome.Saved => StatusCodes.Status200OK,

        EventClassificationWriteOutcome.NotFound => StatusCodes.Status404NotFound,

        EventClassificationWriteOutcome.ValidationFailed => StatusCodes.Status400BadRequest,

        EventClassificationWriteOutcome.NameExists
            or EventClassificationWriteOutcome.NoSchoolResolved
            or EventClassificationWriteOutcome.InUse
            or EventClassificationWriteOutcome.SeedProtected => StatusCodes.Status409Conflict,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(EventClassificationWriteOutcome)}. Every outcome " +
            "must be mapped explicitly, or an unmapped one ships as a success."),
    };

    private ObjectResult Failure(EventClassificationWriteResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(response.Outcome), detail: response.Message);

        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(EventClassificationWriteOutcome outcome) => outcome switch
    {
        EventClassificationWriteOutcome.NotFound => "Event classification not found.",
        EventClassificationWriteOutcome.NameExists => "That event classification name is already in use.",
        EventClassificationWriteOutcome.NoSchoolResolved => "No school could be resolved.",
        EventClassificationWriteOutcome.InUse => "That event classification is still in use.",
        EventClassificationWriteOutcome.SeedProtected => "That event classification is part of the seeded vocabulary.",
        _ => "The request could not be processed.",
    };
}
