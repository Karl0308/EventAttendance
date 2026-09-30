using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Api.Controllers;

/// <summary>
/// The Event Audience master (EventAudience.docx) — reusable audience definitions, each filed under an
/// event classification, that resolve to eligible attendees from the live Academic Community data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own route rather than a branch of <c>/events</c> or <c>/event-classifications</c>:</b> a
/// definition is a row with its own lifecycle — named, edited, deactivated — that an organizer builds once
/// and reuses across many events. It reuses the <c>events.read</c> / <c>events.write</c> permission pair
/// for the reason <see cref="EventClassificationsController"/> records: this vocabulary exists only to
/// describe events, and minting an <c>event-audiences.*</c> pair would change the approved RBAC grant
/// matrix as a side effect of adding a reference list.
/// </para>
///
/// <para>
/// <b>There is no event-to-audience link here.</b> Wiring a chosen definition into event creation and
/// freezing its resolved roster is an additive follow-on, exactly as the event-to-classification link is —
/// this module delivers the reusable master and the live attendee resolution.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/event-audiences")]
public class AudienceDefinitionsController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on every other controller.</summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IAudienceDefinitionService _audiences;

    public AudienceDefinitionsController(IAudienceDefinitionService audiences) => _audiences = audiences;

    // ------------------------------------------------------------------------------------- reads

    /// <summary>
    /// <c>GET /event-audiences</c> — the definitions, active first then by name, optionally filtered to one
    /// event classification and/or to active only (the event-creation picker passes both).
    /// </summary>
    /// <param name="eventClassificationId">Only definitions under this classification, when supplied.</param>
    /// <param name="includeInactive">Defaults to false — what the picker wants; a report wants true.</param>
    /// <param name="page">1-based page number, default 1. Out-of-range values are clamped, not refused.</param>
    /// <param name="pageSize">Rows per page. Default 50, maximum 200; a larger value is clamped.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of audience definitions, possibly empty.</response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsRead)]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(PagedResult<AudienceDefinitionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AudienceDefinitionDto>>> List(
        [FromQuery] Guid? eventClassificationId, [FromQuery] bool includeInactive,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _audiences.ListAsync(
            eventClassificationId, includeInactive, PageRequest.From(page, pageSize), ct));

    /// <summary>
    /// <c>GET /event-audiences/options</c> — the distinct Academic Community values the criteria pickers are
    /// built from, so the form references existing data rather than inventing master data (spec §3/§7).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The option lists (may be empty on a fresh roster).</response>
    [HttpGet("options")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsRead)]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(AudienceOptionsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AudienceOptionsDto>> Options(CancellationToken ct)
        => Ok(await _audiences.OptionsAsync(ct));

    /// <summary>
    /// <c>GET /event-audiences/{id}</c> — one definition, active or not.
    /// </summary>
    /// <param name="id">The definition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The definition.</response>
    /// <response code="404">No such definition in this school.</response>
    [HttpGet("{id:guid}", Name = nameof(GetAudienceDefinition))]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsRead)]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(AudienceDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AudienceDefinitionDto>> GetAudienceDefinition(Guid id, CancellationToken ct)
    {
        var row = await _audiences.GetAsync(id, ct);
        return row is null ? Failure(NotFoundResponse()) : Ok(row);
    }

    /// <summary>
    /// <c>GET /event-audiences/{id}/attendees</c> — the students and personnel this definition currently
    /// resolves to, from the live roster. Counts are the true totals; the attendee list is capped.
    /// </summary>
    /// <param name="id">The definition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The resolved attendees and totals.</response>
    /// <response code="404">No such definition in this school.</response>
    [HttpGet("{id:guid}/attendees")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsRead)]
    [HasPermissionNotEnforced(EamsPermissions.EventsRead)]
    [ProducesResponseType(typeof(ResolvedAudienceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResolvedAudienceDto>> Attendees(Guid id, CancellationToken ct)
    {
        var resolved = await _audiences.ResolveAsync(id, ct);
        return resolved is null ? Failure(NotFoundResponse()) : Ok(resolved);
    }

    // ------------------------------------------------------------------------------------ writes

    /// <summary>
    /// <c>POST /event-audiences</c> — create a definition under an active event classification.
    /// </summary>
    /// <param name="request">Name, classification, type, and criteria.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The created definition, active. <c>Location</c> names it.</response>
    /// <response code="400">The name, type, or criteria break a rule.</response>
    /// <response code="409">The name's key is in use, the classification is unavailable, or no school resolved.</response>
    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(AudienceDefinitionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AudienceDefinitionDto>> Create(
        [FromBody] AudienceDefinitionWriteRequest request, CancellationToken ct)
    {
        var response = await _audiences.CreateAsync(request, ct);
        if (response.Outcome != AudienceWriteOutcome.Saved) return Failure(response);

        return CreatedAtRoute(
            nameof(GetAudienceDefinition), new { id = response.Definition!.Id }, response.Definition);
    }

    /// <summary>
    /// <c>PUT /event-audiences/{id}</c> — a full replacement of the definition's fields.
    /// </summary>
    /// <param name="id">The definition.</param>
    /// <param name="request">The new name, classification, type, and criteria.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The updated definition.</response>
    /// <response code="400">The name, type, or criteria break a rule.</response>
    /// <response code="404">No such definition.</response>
    /// <response code="409">Another definition holds that name's key, or the classification is unavailable.</response>
    [HttpPut("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(AudienceDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AudienceDefinitionDto>> Update(
        Guid id, [FromBody] AudienceDefinitionWriteRequest request, CancellationToken ct)
    {
        var response = await _audiences.UpdateAsync(id, request, ct);
        return response.Outcome == AudienceWriteOutcome.Saved ? Ok(response.Definition) : Failure(response);
    }

    /// <summary>
    /// <c>PATCH /event-audiences/{id}/active</c> — deactivate a definition, or bring it back.
    /// </summary>
    /// <param name="id">The definition.</param>
    /// <param name="request"><c>{"isActive": false}</c> to deactivate, <c>{"isActive": true}</c> to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The definition, with its new flag.</response>
    /// <response code="400"><c>isActive</c> was not supplied.</response>
    /// <response code="404">No such definition.</response>
    [HttpPatch("{id:guid}/active")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(AudienceDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AudienceDefinitionDto>> SetActive(
        Guid id, [FromBody] AudienceActiveRequest request, CancellationToken ct)
    {
        // Bound as bool? and refused here rather than in the service, for the reason
        // EventClassificationsController records: a missing member is indistinguishable from a deliberate
        // false by the time it reaches the service, and false withdraws the definition from the picker.
        if (request.IsActive is not { } isActive)
        {
            return Failure(new AudienceWriteResponse(
                AudienceWriteOutcome.ValidationFailed,
                "isActive is required. Send {\"isActive\": false} to deactivate this audience or " +
                "{\"isActive\": true} to bring it back — a missing member would bind to false and silently " +
                "withdraw it from the event-creation picker.",
                null));
        }

        var response = await _audiences.SetActiveAsync(id, isActive, ct);
        return response.Outcome == AudienceWriteOutcome.Saved ? Ok(response.Definition) : Failure(response);
    }

    /// <summary>
    /// <c>DELETE /event-audiences/{id}</c> — remove a definition. (No event references one yet.)
    /// </summary>
    /// <param name="id">The definition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The deleted definition, as it was.</response>
    /// <response code="404">No such definition.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.EventsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.EventsWrite)]
    [ProducesResponseType(typeof(AudienceDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AudienceDefinitionDto>> Delete(Guid id, CancellationToken ct)
    {
        var response = await _audiences.DeleteAsync(id, ct);
        return response.Outcome == AudienceWriteOutcome.Saved ? Ok(response.Definition) : Failure(response);
    }

    // -------------------------------------------------------------------------------- translation

    private static AudienceWriteResponse NotFoundResponse() =>
        new(AudienceWriteOutcome.NotFound, "Event audience not found.", null);

    /// <summary>
    /// The status-code contract for the whole surface, as one total function — every member listed rather
    /// than a fall-through, for the reason every other controller's <c>StatusCodeFor</c> records: a discard
    /// arm ships an unmapped outcome as a success.
    /// </summary>
    internal static int StatusCodeFor(AudienceWriteOutcome outcome) => outcome switch
    {
        AudienceWriteOutcome.Saved => StatusCodes.Status200OK,
        AudienceWriteOutcome.NotFound => StatusCodes.Status404NotFound,
        AudienceWriteOutcome.ValidationFailed => StatusCodes.Status400BadRequest,
        AudienceWriteOutcome.NameExists
            or AudienceWriteOutcome.NoSchoolResolved
            or AudienceWriteOutcome.ClassificationUnavailable => StatusCodes.Status409Conflict,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(AudienceWriteOutcome)}. Every outcome must be " +
            "mapped explicitly, or an unmapped one ships as a success."),
    };

    private ObjectResult Failure(AudienceWriteResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(response.Outcome), detail: response.Message);

        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(AudienceWriteOutcome outcome) => outcome switch
    {
        AudienceWriteOutcome.NotFound => "Event audience not found.",
        AudienceWriteOutcome.NameExists => "That event audience name is already in use.",
        AudienceWriteOutcome.NoSchoolResolved => "No school could be resolved.",
        AudienceWriteOutcome.ClassificationUnavailable => "That event classification is unavailable.",
        _ => "The request could not be processed.",
    };
}
