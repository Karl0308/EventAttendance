using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// The Academic Community's Personnel tab (StudentsEmployees.docx) — faculty and employees master data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gated by <see cref="EamsPermissions.StudentsRead"/> / <see cref="EamsPermissions.StudentsWrite"/></b>,
/// deliberately reusing the roster codes rather than minting <c>personnel.*</c>: Students and Personnel
/// are the two tabs of one module (Academic Community), curated by the same administrator — the same
/// decision <see cref="ClassificationsController"/> records for reusing <c>students.*</c>. Import and
/// export are a later increment.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/personnel")]
public class PersonnelController : ControllerBase
{
    internal const string ErrorCodeProperty = "code";

    private readonly IPersonnelService _personnel;

    public PersonnelController(IPersonnelService personnel) => _personnel = personnel;

    /// <summary>
    /// <c>GET /personnel</c> — one page of the school's personnel, active first then by name, filtered.
    /// </summary>
    /// <response code="200">One page of personnel, possibly empty.</response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsRead)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsRead)]
    [ProducesResponseType(typeof(PagedResult<PersonnelDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<PersonnelDto>>> List(
        [FromQuery] string? personnelNumber, [FromQuery] string? rfidUid, [FromQuery] string? name,
        [FromQuery] string? department, [FromQuery] string? position, [FromQuery] string? classification,
        [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _personnel.ListAsync(
            new PersonnelListFilter(personnelNumber, rfidUid, name, department, position, classification, status),
            PageRequest.From(page, pageSize), ct));

    /// <summary>
    /// <c>GET /personnel/{id}</c> — one record.
    /// </summary>
    /// <response code="200">The record.</response>
    /// <response code="404">No such personnel record in this school.</response>
    [HttpGet("{id:guid}", Name = nameof(GetPersonnel))]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsRead)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsRead)]
    [ProducesResponseType(typeof(PersonnelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PersonnelDto>> GetPersonnel(Guid id, CancellationToken ct)
    {
        var row = await _personnel.GetAsync(id, ct);
        return row is null
            ? Failure(new PersonnelWriteResponse(PersonnelWriteOutcome.NotFound, "Personnel record not found.", null))
            : Ok(row);
    }

    /// <summary>
    /// <c>POST /personnel</c> — create a personnel record.
    /// </summary>
    /// <response code="201">The created record. <c>Location</c> names it.</response>
    /// <response code="400">A field breaks a column rule or the status is undocumented.</response>
    /// <response code="409">The ID or RFID UID is already in use, or no school could be resolved.</response>
    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(PersonnelDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PersonnelDto>> Create(
        [FromBody] PersonnelWriteRequest request, CancellationToken ct)
    {
        var response = await _personnel.CreateAsync(request, ct);
        if (response.Outcome != PersonnelWriteOutcome.Saved) return Failure(response);
        return CreatedAtRoute(nameof(GetPersonnel), new { id = response.Personnel!.Id }, response.Personnel);
    }

    /// <summary>
    /// <c>PUT /personnel/{id}</c> — a full replacement of the record's fields.
    /// </summary>
    /// <response code="200">The updated record.</response>
    /// <response code="400">A field breaks a column rule or the status is undocumented.</response>
    /// <response code="404">No such record.</response>
    /// <response code="409">The ID or RFID UID is already in use by another record.</response>
    [HttpPut("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(PersonnelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PersonnelDto>> Update(
        Guid id, [FromBody] PersonnelWriteRequest request, CancellationToken ct)
    {
        var response = await _personnel.UpdateAsync(id, request, ct);
        return response.Outcome == PersonnelWriteOutcome.Saved ? Ok(response.Personnel) : Failure(response);
    }

    /// <summary>
    /// <c>DELETE /personnel/{id}</c> — soft-delete the record.
    /// </summary>
    /// <response code="200">The removed record, as it was.</response>
    /// <response code="404">No such record.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.StudentsWrite)]
    [HasPermissionNotEnforced(EamsPermissions.StudentsWrite)]
    [ProducesResponseType(typeof(PersonnelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PersonnelDto>> Delete(Guid id, CancellationToken ct)
    {
        var response = await _personnel.DeleteAsync(id, ct);
        return response.Outcome == PersonnelWriteOutcome.Saved ? Ok(response.Personnel) : Failure(response);
    }

    // -------------------------------------------------------------------------------- translation

    internal static int StatusCodeFor(PersonnelWriteOutcome outcome) => outcome switch
    {
        PersonnelWriteOutcome.Saved => StatusCodes.Status200OK,
        PersonnelWriteOutcome.NotFound => StatusCodes.Status404NotFound,
        PersonnelWriteOutcome.ValidationFailed => StatusCodes.Status400BadRequest,
        PersonnelWriteOutcome.DuplicateNumber
            or PersonnelWriteOutcome.DuplicateRfid
            or PersonnelWriteOutcome.NoSchoolResolved => StatusCodes.Status409Conflict,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(PersonnelWriteOutcome)}. Every outcome must be " +
            "mapped explicitly, or an unmapped one ships as a success."),
    };

    private ObjectResult Failure(PersonnelWriteResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(response.Outcome), detail: response.Message);

        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(PersonnelWriteOutcome outcome) => outcome switch
    {
        PersonnelWriteOutcome.NotFound => "Personnel record not found.",
        PersonnelWriteOutcome.DuplicateNumber => "That personnel ID is already in use.",
        PersonnelWriteOutcome.DuplicateRfid => "That RFID UID is already in use.",
        PersonnelWriteOutcome.NoSchoolResolved => "No school could be resolved.",
        _ => "The request could not be processed.",
    };
}
