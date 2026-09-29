using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// §11's User Management (UserWithRBAC.docx) — the admin surface over <c>Users</c> and the roles they
/// hold. Users are tenant-scoped, so every route here is bounded to the caller's school by the global
/// query filter.
/// </summary>
/// <remarks>
/// <para>
/// Gated by <see cref="EamsPermissions.UsersRead"/> / <see cref="EamsPermissions.UsersWrite"/>, held by
/// SuperAdmin and SchoolAdmin (see those codes). Role <em>administration</em> — creating a role, editing
/// its permissions — is a separate surface, because roles are global (§4.11 has no <c>SchoolId</c>);
/// this controller only assigns roles that already exist.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/users")]
public class UsersController : ControllerBase
{
    internal const string ErrorCodeProperty = "code";

    private readonly IUserAdminService _users;

    public UsersController(IUserAdminService users) => _users = users;

    // ------------------------------------------------------------------------------------- reads

    /// <summary>
    /// <c>GET /users</c> — the school's users, active first then by name.
    /// </summary>
    /// <param name="search">Optional fragment matched against e-mail and full name.</param>
    /// <param name="includeInactive">Include deactivated accounts. Defaults to false.</param>
    /// <param name="page">1-based page number, default 1.</param>
    /// <param name="pageSize">Rows per page, default 50, maximum 200.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of users, possibly empty.</response>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.UsersRead)]
    [HasPermissionNotEnforced(EamsPermissions.UsersRead)]
    [ProducesResponseType(typeof(PagedResult<UserDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<UserDto>>> List(
        [FromQuery] string? search, [FromQuery] bool includeInactive,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _users.ListAsync(search, includeInactive, PageRequest.From(page, pageSize), ct));

    /// <summary>
    /// <c>GET /users/{id}</c> — one user.
    /// </summary>
    /// <param name="id">The user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The user.</response>
    /// <response code="404">No such user in this school.</response>
    [HttpGet("{id:guid}", Name = nameof(GetUser))]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.UsersRead)]
    [HasPermissionNotEnforced(EamsPermissions.UsersRead)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> GetUser(Guid id, CancellationToken ct)
    {
        var user = await _users.GetAsync(id, ct);
        return user is null
            ? Failure(new UserAdminWriteResponse(UserAdminOutcome.NotFound, "User not found.", null))
            : Ok(user);
    }

    // ------------------------------------------------------------------------------------ writes

    /// <summary>
    /// <c>POST /users</c> — create a user with an initial role and password.
    /// </summary>
    /// <param name="request">The user's e-mail, name, initial role and password.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The created user. <c>Location</c> names it.</response>
    /// <response code="400">A field failed a column rule or the password policy.</response>
    /// <response code="409">The e-mail is already in use, the role is unknown, or no school could be resolved.</response>
    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.UsersWrite)]
    [HasPermissionNotEnforced(EamsPermissions.UsersWrite)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> Create(
        [FromBody] UserCreateRequest request, CancellationToken ct)
    {
        var response = await _users.CreateAsync(request, ct);
        if (response.Outcome != UserAdminOutcome.Saved) return Failure(response);

        return CreatedAtRoute(nameof(GetUser), new { id = response.User!.Id }, response.User);
    }

    /// <summary>
    /// <c>PUT /users/{id}</c> — edit the full name and phone. Not the e-mail; see <see cref="UserUpdateRequest"/>.
    /// </summary>
    /// <param name="id">The user.</param>
    /// <param name="request">The new full name and phone.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The updated user.</response>
    /// <response code="400">The full name is blank or a field is over-length.</response>
    /// <response code="404">No such user.</response>
    [HttpPut("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.UsersWrite)]
    [HasPermissionNotEnforced(EamsPermissions.UsersWrite)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> Update(
        Guid id, [FromBody] UserUpdateRequest request, CancellationToken ct)
    {
        var response = await _users.UpdateAsync(id, request, ct);
        return response.Outcome == UserAdminOutcome.Saved ? Ok(response.User) : Failure(response);
    }

    /// <summary>
    /// <c>PATCH /users/{id}/active</c> — deactivate a user, or bring them back.
    /// </summary>
    /// <param name="id">The user.</param>
    /// <param name="request"><c>{"isActive": false}</c> to deactivate, <c>{"isActive": true}</c> to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The user, with its new flag.</response>
    /// <response code="400"><c>isActive</c> was not supplied.</response>
    /// <response code="404">No such user.</response>
    /// <response code="409">You aimed it at your own account — that would lock you out.</response>
    [HttpPatch("{id:guid}/active")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.UsersWrite)]
    [HasPermissionNotEnforced(EamsPermissions.UsersWrite)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> SetActive(
        Guid id, [FromBody] UserActiveRequest request, CancellationToken ct)
    {
        if (request.IsActive is not { } isActive)
        {
            return Failure(new UserAdminWriteResponse(
                UserAdminOutcome.ValidationFailed,
                "isActive is required. Send {\"isActive\": false} to deactivate this user or " +
                "{\"isActive\": true} to bring them back — a missing member would bind to false and " +
                "silently lock them out.",
                null));
        }

        var response = await _users.SetActiveAsync(id, isActive, ct);
        return response.Outcome == UserAdminOutcome.Saved ? Ok(response.User) : Failure(response);
    }

    /// <summary>
    /// <c>PUT /users/{id}/roles</c> — replace the set of roles the user holds.
    /// </summary>
    /// <param name="id">The user.</param>
    /// <param name="request">The complete set of role ids the user should hold after the call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The user, with its new roles.</response>
    /// <response code="400"><c>roleIds</c> was not supplied.</response>
    /// <response code="404">No such user.</response>
    /// <response code="409">A role id is unknown, or you aimed it at your own account.</response>
    [HttpPut("{id:guid}/roles")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.UsersWrite)]
    [HasPermissionNotEnforced(EamsPermissions.UsersWrite)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> SetRoles(
        Guid id, [FromBody] UserRolesRequest request, CancellationToken ct)
    {
        // Nullable and refused here rather than in the service: "the member was absent" is a fact only the
        // deserializer has, and an omitted roleIds bound to an empty list would silently strip every role.
        if (request.RoleIds is null)
        {
            return Failure(new UserAdminWriteResponse(
                UserAdminOutcome.ValidationFailed,
                "roleIds is required. Send the complete set of role ids the user should hold — an empty " +
                "array removes every role, which is different from not sending the field at all.",
                null));
        }

        var response = await _users.SetRolesAsync(id, request.RoleIds, ct);
        return response.Outcome == UserAdminOutcome.Saved ? Ok(response.User) : Failure(response);
    }

    // -------------------------------------------------------------------------------- translation

    internal static int StatusCodeFor(UserAdminOutcome outcome) => outcome switch
    {
        UserAdminOutcome.Saved => StatusCodes.Status200OK,
        UserAdminOutcome.NotFound => StatusCodes.Status404NotFound,
        UserAdminOutcome.ValidationFailed => StatusCodes.Status400BadRequest,
        UserAdminOutcome.EmailInUse
            or UserAdminOutcome.UnknownRole
            or UserAdminOutcome.NoSchoolResolved
            or UserAdminOutcome.SelfLockout => StatusCodes.Status409Conflict,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(UserAdminOutcome)}. Every outcome must be mapped " +
            "explicitly, or an unmapped one ships as a success."),
    };

    private ObjectResult Failure(UserAdminWriteResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(response.Outcome), detail: response.Message);

        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(UserAdminOutcome outcome) => outcome switch
    {
        UserAdminOutcome.NotFound => "User not found.",
        UserAdminOutcome.EmailInUse => "That e-mail address is already in use.",
        UserAdminOutcome.UnknownRole => "That role does not exist.",
        UserAdminOutcome.NoSchoolResolved => "No school could be resolved.",
        UserAdminOutcome.SelfLockout => "You cannot lock yourself out.",
        _ => "The request could not be processed.",
    };
}
