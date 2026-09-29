using EAMS.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Api.Controllers;

/// <summary>
/// The permission catalogue the role-permission editor is built from — every code a role may hold.
/// </summary>
/// <remarks>
/// <para>
/// It returns <see cref="EamsRoles.HumanAssignable"/>: the whole registry except
/// <see cref="EamsPermissions.AttendanceCapture"/>, which belongs to a capture device alone and no role
/// may hold. Served from the registry itself rather than the <c>Permissions</c> table, because "what a
/// role may be granted" is an authorization statement this assembly owns, and the table can lag behind a
/// deploy by one start on an installation whose grant migration has not run yet. The SPA groups these by
/// prefix (the "module", e.g. <c>events</c>) and suffix (the action, e.g. <c>read</c>).
/// </para>
/// <para>
/// Gated by <see cref="EamsPermissions.RolesRead"/>: reading the catalogue is part of configuring roles.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/permissions")]
public class PermissionsController : ControllerBase
{
    /// <summary>
    /// <c>GET /permissions</c> — every permission code a role may be granted, ordered.
    /// </summary>
    /// <response code="200">The grantable permission codes.</response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.RolesRead)]
    [HasPermissionNotEnforced(EamsPermissions.RolesRead)]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> List() =>
        Ok(EamsRoles.HumanAssignable.OrderBy(c => c, StringComparer.Ordinal).ToList());
}
