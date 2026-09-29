using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// §11's Role Management (UserWithRBAC.docx) — the role list, and administration of custom roles.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>GET /roles</c> is gated by <see cref="EamsPermissions.UsersRead"/></b>, because its first
/// consumer is the user-management screen's role picker — reading the roles is part of assigning them.
/// The by-id read and every write are Role Management proper, gated by
/// <see cref="EamsPermissions.RolesRead"/> / <see cref="EamsPermissions.RolesWrite"/>. Roles are global
/// (§4.11 has no <c>SchoolId</c>), so a write affects every tenant — which is why the write code is
/// administrators' alone, and why the four built-in roles are read-only.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/roles")]
public class RolesController : ControllerBase
{
    internal const string ErrorCodeProperty = "code";

    private readonly IRoleAdminService _roles;

    public RolesController(IRoleAdminService roles) => _roles = roles;

    /// <summary>
    /// <c>GET /roles</c> — every role, with the count of users in this school that hold it and the codes
    /// it grants. The set user management assigns from.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The roles.</response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.UsersRead)]
    [HasPermissionNotEnforced(EamsPermissions.UsersRead)]
    [ProducesResponseType(typeof(IReadOnlyList<RoleDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> List(CancellationToken ct) =>
        Ok(await _roles.ListAsync(ct));

    /// <summary>
    /// <c>GET /roles/{id}</c> — one role.
    /// </summary>
    /// <param name="id">The role.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The role.</response>
    /// <response code="404">No such role.</response>
    [HttpGet("{id:guid}", Name = nameof(GetRole))]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.RolesRead)]
    [HasPermissionNotEnforced(EamsPermissions.RolesRead)]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleDto>> GetRole(Guid id, CancellationToken ct)
    {
        var role = await _roles.GetAsync(id, ct);
        return role is null
            ? Failure(new RoleWriteResponse(RoleWriteOutcome.NotFound, "Role not found.", null))
            : Ok(role);
    }

    /// <summary>
    /// <c>POST /roles</c> — create a custom role. It starts with no permissions; configure them next.
    /// </summary>
    /// <param name="request">The role's name and optional description.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The created role. <c>Location</c> names it.</response>
    /// <response code="400">The name is blank or a field is over-length.</response>
    /// <response code="409">A role with that name already exists.</response>
    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.RolesWrite)]
    [HasPermissionNotEnforced(EamsPermissions.RolesWrite)]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleDto>> Create([FromBody] RoleCreateRequest request, CancellationToken ct)
    {
        var response = await _roles.CreateAsync(request, ct);
        if (response.Outcome != RoleWriteOutcome.Saved) return Failure(response);
        return CreatedAtRoute(nameof(GetRole), new { id = response.Role!.Id }, response.Role);
    }

    /// <summary>
    /// <c>PUT /roles/{id}</c> — rename and re-describe a custom role.
    /// </summary>
    /// <param name="id">The role.</param>
    /// <param name="request">The new name and description.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The updated role.</response>
    /// <response code="400">The name is blank or a field is over-length.</response>
    /// <response code="404">No such role.</response>
    /// <response code="409">Another role holds that name, or this is a built-in role.</response>
    [HttpPut("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.RolesWrite)]
    [HasPermissionNotEnforced(EamsPermissions.RolesWrite)]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleDto>> Update(
        Guid id, [FromBody] RoleUpdateRequest request, CancellationToken ct)
    {
        var response = await _roles.UpdateAsync(id, request, ct);
        return response.Outcome == RoleWriteOutcome.Saved ? Ok(response.Role) : Failure(response);
    }

    /// <summary>
    /// <c>PUT /roles/{id}/permissions</c> — replace the set of permission codes a custom role grants.
    /// </summary>
    /// <param name="id">The role.</param>
    /// <param name="request">The complete set of permission codes the role should grant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The role, with its new permissions.</response>
    /// <response code="400"><c>permissionCodes</c> was not supplied, or names a code a role may not hold.</response>
    /// <response code="404">No such role.</response>
    /// <response code="409">This is a built-in role, or a code cannot be granted.</response>
    [HttpPut("{id:guid}/permissions")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.RolesWrite)]
    [HasPermissionNotEnforced(EamsPermissions.RolesWrite)]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleDto>> SetPermissions(
        Guid id, [FromBody] RolePermissionsRequest request, CancellationToken ct)
    {
        if (request.PermissionCodes is null)
        {
            return Failure(new RoleWriteResponse(
                RoleWriteOutcome.ValidationFailed,
                "permissionCodes is required. Send the complete set of codes the role should grant — an " +
                "empty array grants nothing, which is different from not sending the field.",
                null));
        }

        // The human-assignable gate lives here, in the assembly that owns the registry and the matrix: a
        // role may hold any code except attendance.capture, which is the device's alone. A code outside
        // that set is refused before the service is asked to persist it.
        var offered = EamsRoles.HumanAssignable.ToHashSet(StringComparer.Ordinal);
        var rejected = request.PermissionCodes
            .Where(c => !offered.Contains(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (rejected.Count > 0)
        {
            return Failure(new RoleWriteResponse(
                RoleWriteOutcome.ValidationFailed,
                $"These codes cannot be granted to a role: {string.Join(", ", rejected)}. A role may hold " +
                "any permission except attendance.capture, which belongs to a capture device alone.",
                null));
        }

        var response = await _roles.SetPermissionsAsync(id, request.PermissionCodes, ct);
        return response.Outcome == RoleWriteOutcome.Saved ? Ok(response.Role) : Failure(response);
    }

    /// <summary>
    /// <c>DELETE /roles/{id}</c> — delete a custom role that no user holds.
    /// </summary>
    /// <param name="id">The role.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The deleted role.</response>
    /// <response code="404">No such role.</response>
    /// <response code="409">This is a built-in role, or users still hold it.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.RolesWrite)]
    [HasPermissionNotEnforced(EamsPermissions.RolesWrite)]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleDto>> Delete(Guid id, CancellationToken ct)
    {
        var response = await _roles.DeleteAsync(id, ct);
        return response.Outcome == RoleWriteOutcome.Saved ? Ok(response.Role) : Failure(response);
    }

    // -------------------------------------------------------------------------------- translation

    internal static int StatusCodeFor(RoleWriteOutcome outcome) => outcome switch
    {
        RoleWriteOutcome.Saved => StatusCodes.Status200OK,
        RoleWriteOutcome.NotFound => StatusCodes.Status404NotFound,
        RoleWriteOutcome.ValidationFailed => StatusCodes.Status400BadRequest,
        RoleWriteOutcome.NameExists
            or RoleWriteOutcome.SystemRoleProtected
            or RoleWriteOutcome.InUse
            or RoleWriteOutcome.UnknownPermission => StatusCodes.Status409Conflict,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(RoleWriteOutcome)}. Every outcome must be mapped " +
            "explicitly, or an unmapped one ships as a success."),
    };

    private ObjectResult Failure(RoleWriteResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(response.Outcome), detail: response.Message);

        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(RoleWriteOutcome outcome) => outcome switch
    {
        RoleWriteOutcome.NotFound => "Role not found.",
        RoleWriteOutcome.NameExists => "That role name is already in use.",
        RoleWriteOutcome.SystemRoleProtected => "That is a built-in role.",
        RoleWriteOutcome.InUse => "That role is still assigned to users.",
        RoleWriteOutcome.UnknownPermission => "A permission code cannot be granted.",
        _ => "The request could not be processed.",
    };
}
