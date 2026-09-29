using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Api.Controllers;

/// <summary>
/// The role list — §11's roles, for the user-management screen to assign from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gated by <see cref="EamsPermissions.UsersRead"/>, not a <c>roles.read</c> code, and deliberately.</b>
/// Its only consumer today is the user-management screen's role picker — reading the roles is part of
/// administering users — so it reuses that surface's permission rather than minting a new one, the same
/// decision <see cref="ClassificationsController"/> records for reusing <c>students.*</c>. Role
/// <em>administration</em> (creating a role, editing its permissions) is a separate later increment and
/// will introduce its own write code where the grant matrix is the subject.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/roles")]
public class RolesController : ControllerBase
{
    private readonly IRoleAdminService _roles;

    public RolesController(IRoleAdminService roles) => _roles = roles;

    /// <summary>
    /// <c>GET /roles</c> — every role, ordered by name, with the count of users in this school that hold
    /// it and the permission codes it grants.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The roles.</response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.UsersRead)]
    [HasPermissionNotEnforced(EamsPermissions.UsersRead)]
    [ProducesResponseType(typeof(IReadOnlyList<RoleDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> List(CancellationToken ct) =>
        Ok(await _roles.ListAsync(ct));
}
