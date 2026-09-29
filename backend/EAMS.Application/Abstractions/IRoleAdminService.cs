using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>The outcome of a role-administration write. An enum, for the reason every write surface uses one.</summary>
public enum RoleWriteOutcome
{
    /// <summary>The write happened.</summary>
    Saved,

    /// <summary>No role with that id. 404.</summary>
    NotFound,

    /// <summary>A field failed a column rule. 400; the message names which.</summary>
    ValidationFailed,

    /// <summary><c>UX_Roles_Name</c> already holds that name. 409.</summary>
    NameExists,

    /// <summary>
    /// <b>The target is one of the four built-in roles.</b> 409. Their names are used throughout the
    /// system (login, the seed, <c>EamsRoleNames</c>), and their seeded grants are the approved matrix, so
    /// they are read-only: renaming, re-permissioning or deleting one is refused. Custom roles are fully
    /// editable.
    /// </summary>
    SystemRoleProtected,

    /// <summary>
    /// <b>The delete was refused because users still hold the role.</b> 409. Reassign them first — deleting
    /// a held role would strip access from people who never asked to lose it.
    /// </summary>
    InUse,

    /// <summary>
    /// A requested permission code is not one a role may hold — it does not exist, or it is
    /// <c>attendance.capture</c>, which is the device's alone. 409.
    /// </summary>
    UnknownPermission,
}

/// <summary>The result of a role-administration write. <see cref="Role"/> is null unless it saved.</summary>
public record RoleWriteResponse(RoleWriteOutcome Outcome, string Message, RoleDto? Role);

/// <summary>
/// <b>§11's Role Management surface (UserWithRBAC.docx).</b> Reads the role list (the set user management
/// assigns from) and administers custom roles: creating them, editing name/description, configuring their
/// permissions, and deleting an unassigned one.
///
/// <para>
/// Roles are global — §4.11's <c>Roles</c> table has no <c>SchoolId</c> — so writes here affect every
/// tenant, which is why the write code (<c>roles.write</c>) is administrators' alone. The four built-in
/// roles are protected: their names are load-bearing and their grants are the approved seed matrix, so
/// they are read-only.
/// </para>
/// </summary>
public interface IRoleAdminService
{
    /// <summary>
    /// <c>GET /roles</c> — every role, ordered by name, with the count of users in this school that hold
    /// it and the permission codes it grants.
    /// </summary>
    Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default);

    /// <summary><c>GET /roles/{id}</c> — one role, or null if there is none.</summary>
    Task<RoleDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary><c>POST /roles</c> — create a custom role (never a system role) with no permissions yet.</summary>
    Task<RoleWriteResponse> CreateAsync(RoleCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PUT /roles/{id}</c> — rename a custom role and edit its description. Refused for a system role
    /// (<see cref="RoleWriteOutcome.SystemRoleProtected"/>).
    /// </summary>
    Task<RoleWriteResponse> UpdateAsync(Guid id, RoleUpdateRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PUT /roles/{id}/permissions</c> — replace the set of permission codes a custom role grants.
    /// Refused for a system role. Each code must be one a role may hold; the controller has already
    /// rejected non-human-assignable codes, and this rejects any that has no <c>Permissions</c> row.
    /// </summary>
    Task<RoleWriteResponse> SetPermissionsAsync(
        Guid id, IReadOnlyList<string> permissionCodes, CancellationToken ct = default);

    /// <summary>
    /// <c>DELETE /roles/{id}</c> — delete a custom role that no user holds. Refused for a system role, and
    /// refused with <see cref="RoleWriteOutcome.InUse"/> while any user (in any school) holds it.
    /// </summary>
    Task<RoleWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default);
}
