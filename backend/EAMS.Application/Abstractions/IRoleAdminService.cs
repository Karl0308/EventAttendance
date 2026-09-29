using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// <b>The role surface.</b> Today it reads the role list — the set a user-management screen assigns from,
/// and where <c>UserCount</c> and the granted codes come from. Role <em>administration</em> (creating a
/// role, editing its permissions, deleting one) is a separate, later increment that will add write
/// methods here and its own <c>roles.write</c> code; this read exists now because assigning a role to a
/// user is impossible without listing the roles first.
///
/// <para>
/// Roles are global — §4.11's <c>Roles</c> table has no <c>SchoolId</c> — so the list is not
/// tenant-scoped. The per-role <c>UserCount</c> is, through the users that hold it.
/// </para>
/// </summary>
public interface IRoleAdminService
{
    /// <summary>
    /// <c>GET /roles</c> — every role, ordered by name, with the count of users in this school that hold
    /// it and the permission codes it grants.
    /// </summary>
    Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default);
}
