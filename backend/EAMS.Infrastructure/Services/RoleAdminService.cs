using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// The role surface — see <see cref="IRoleAdminService"/>. Read-only for now: it lists the roles a
/// user-management screen assigns from. Roles are global; the per-role user count is tenant-scoped
/// through the users that hold it (the <c>UserRoles</c> query filter reaches a school via the user).
/// </summary>
internal sealed class RoleAdminService : IRoleAdminService
{
    private readonly EamsDbContext _db;

    public RoleAdminService(EamsDbContext db) => _db = db;

    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default)
    {
        return await _db.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto(
                r.Id,
                r.Name,
                r.Description,
                r.IsSystem,
                // Tenant-scoped through the user: UserRoles is filtered by the user's SchoolId, so this
                // counts holders in the caller's school. RolePermissions is global and is not.
                _db.UserRoles.Count(ur => ur.RoleId == r.Id),
                r.RolePermissions
                    .Select(rp => rp.Permission!.Code)
                    .OrderBy(c => c)
                    .ToList()))
            .ToListAsync(ct);
    }
}
