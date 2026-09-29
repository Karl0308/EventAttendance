namespace EAMS.Application.Dtos;

/// <summary>
/// One role, as <c>GET /roles</c> lists it — enough for the user-management screen's role picker to
/// offer it and show what it grants.
/// </summary>
/// <param name="IsSystem">
/// <c>true</c> for the four built-in roles (§4.11). A future role-administration surface reads this to
/// refuse deleting one — a product whose built-in roles can be deleted can be locked out of itself.
/// </param>
/// <param name="UserCount">
/// How many users in this school hold the role — scoped by the tenant query filter through the user, so a
/// SchoolAdmin sees their own school's count. Roles themselves are global.
/// </param>
/// <param name="PermissionCodes">The permission codes the role grants, so the picker can show what it does.</param>
public record RoleDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystem,
    int UserCount,
    IReadOnlyList<string> PermissionCodes);

/// <summary>The body of <c>POST /roles</c>.</summary>
/// <param name="Name">Required, 50 characters or fewer, unique among roles, not a whitespace-only value.</param>
/// <param name="Description">Optional, 300 characters or fewer.</param>
public record RoleCreateRequest(string Name, string? Description);

/// <summary>The body of <c>PUT /roles/{id}</c> — rename and re-describe a custom role.</summary>
public record RoleUpdateRequest(string Name, string? Description);

/// <summary>
/// The body of <c>PUT /roles/{id}/permissions</c> — the complete set of permission codes the role should
/// grant after the call (a replacement, not a delta).
/// </summary>
/// <param name="PermissionCodes">
/// The codes to grant. An empty list is allowed and leaves the role granting nothing. Null is a 400 —
/// "grant nothing" and "the field was not sent" are different.
/// </param>
public record RolePermissionsRequest(IReadOnlyList<string>? PermissionCodes);
