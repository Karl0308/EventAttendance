using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// §11's Role Management surface — see <see cref="IRoleAdminService"/>. Roles are global; the per-role
/// <c>UserCount</c> is tenant-scoped through the users that hold it. The four built-in roles are
/// read-only — their names are load-bearing (login, the seed, <c>EamsRoleNames</c>) and their grants are
/// the approved matrix.
///
/// <para>
/// Name uniqueness is decided by <c>UX_Roles_Name</c>: this checks first and catches the violation too,
/// the same "check first, catch anyway" every other write surface uses.
/// </para>
/// </summary>
internal sealed class RoleAdminService : IRoleAdminService
{
    private const int NameMaxLength = 50;
    private const int DescriptionMaxLength = 300;

    private readonly EamsDbContext _db;

    public RoleAdminService(EamsDbContext db) => _db = db;

    // ---------------------------------------------------------------------------------------- reads

    // The DTO projection is inlined at each call site rather than a shared method, because EF Core cannot
    // translate a method call inside .Select() to SQL — it must see the `new RoleDto(...)` expression. The
    // UserCount subquery is tenant-scoped through the UserRoles query filter (which reaches a school via
    // the user); RolePermissions is global. Same correlated-subquery shape ClassificationService uses.

    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default)
    {
        return await _db.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto(
                r.Id, r.Name, r.Description, r.IsSystem,
                _db.UserRoles.Count(ur => ur.RoleId == r.Id),
                r.RolePermissions.Select(rp => rp.Permission!.Code).OrderBy(c => c).ToList()))
            .ToListAsync(ct);
    }

    public async Task<RoleDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Roles.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new RoleDto(
                r.Id, r.Name, r.Description, r.IsSystem,
                _db.UserRoles.Count(ur => ur.RoleId == r.Id),
                r.RolePermissions.Select(rp => rp.Permission!.Code).OrderBy(c => c).ToList()))
            .FirstOrDefaultAsync(ct);
    }

    // --------------------------------------------------------------------------------- create/edit

    public async Task<RoleWriteResponse> CreateAsync(
        RoleCreateRequest request, CancellationToken ct = default)
    {
        if (Validate(request.Name, request.Description) is { } refused) return refused;

        var name = request.Name.Trim();

        if (await _db.Roles.AsNoTracking().AnyAsync(r => r.Name == name, ct))
            return NameExists(name);

        var role = new Role
        {
            Name = name,
            Description = Normalize(request.Description),
            IsSystem = false,
        };
        _db.Roles.Add(role);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            _db.Entry(role).State = EntityState.Detached;
            return NameExists(name);
        }

        return await SavedAsync(role.Id, "Role created. Configure its permissions to give it access.", ct);
    }

    public async Task<RoleWriteResponse> UpdateAsync(
        Guid id, RoleUpdateRequest request, CancellationToken ct = default)
    {
        if (Validate(request.Name, request.Description) is { } refused) return refused;

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return NotFound();
        if (role.IsSystem) return SystemProtected(role, "renamed or re-described");

        var name = request.Name.Trim();

        if (!string.Equals(name, role.Name, StringComparison.Ordinal)
            && await _db.Roles.AsNoTracking().AnyAsync(r => r.Name == name && r.Id != id, ct))
        {
            return NameExists(name);
        }

        role.Name = name;
        role.Description = Normalize(request.Description);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            return NameExists(name);
        }

        return await SavedAsync(id, "Role updated.", ct);
    }

    // ------------------------------------------------------------------------------ permission config

    public async Task<RoleWriteResponse> SetPermissionsAsync(
        Guid id, IReadOnlyList<string> permissionCodes, CancellationToken ct = default)
    {
        var role = await _db.Roles
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return NotFound();
        if (role.IsSystem) return SystemProtected(role, "re-permissioned");

        var wanted = permissionCodes.Distinct(StringComparer.Ordinal).ToList();

        // Every requested code must have a Permissions row. The controller has already refused
        // non-human-assignable codes (attendance.capture, unknown strings); this is the second, database
        // check — a code with no row cannot be granted, and a composite that slipped past the controller
        // is caught here rather than as a foreign-key 500.
        var ids = await _db.Permissions.AsNoTracking()
            .Where(p => wanted.Contains(p.Code))
            .Select(p => new { p.Id, p.Code })
            .ToListAsync(ct);

        var missing = wanted.Except(ids.Select(x => x.Code), StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            return new RoleWriteResponse(
                RoleWriteOutcome.UnknownPermission,
                $"{missing.Count} of the requested permission code(s) cannot be granted to a role. " +
                "Nothing was changed.",
                null);
        }

        var targetIds = ids.Select(x => x.Id).ToHashSet();
        var currentIds = role.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();

        foreach (var toRemove in role.RolePermissions.Where(rp => !targetIds.Contains(rp.PermissionId)).ToList())
            _db.RolePermissions.Remove(toRemove);

        foreach (var permissionId in targetIds.Where(pid => !currentIds.Contains(pid)))
            _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissionId });

        await _db.SaveChangesAsync(ct);

        return await SavedAsync(
            id,
            "Permissions updated. Users holding this role get the change on their next sign-in or token " +
            "refresh.",
            ct);
    }

    // -------------------------------------------------------------------------------- guarded delete

    public async Task<RoleWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var role = await _db.Roles
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return NotFound();
        if (role.IsSystem) return SystemProtected(role, "deleted");

        // Held-by-anyone is refused. IgnoreQueryFilters: a role is global, so a holder in another school
        // still holds it, and deleting the role would strip that person's access. The user-count in the
        // DTO is tenant-scoped for display; this guard is not.
        var holders = await _db.UserRoles.IgnoreQueryFilters().CountAsync(ur => ur.RoleId == id, ct);
        if (holders > 0)
        {
            return new RoleWriteResponse(
                RoleWriteOutcome.InUse,
                $"'{role.Name}' cannot be deleted: {holders} user(s) hold it. Reassign them to another " +
                "role first — deleting a held role would strip access from people who never asked to " +
                "lose it.",
                await GetAsync(id, ct));
        }

        // Its own grants go with it — they describe nothing once the role is gone, and no other row
        // references a RolePermission.
        _db.RolePermissions.RemoveRange(role.RolePermissions);
        _db.Roles.Remove(role);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsConstraintConflict(ex))
        {
            // Lost the race to a concurrent assignment: someone gave this role to a user between the count
            // and the delete, and the UserRoles foreign key (Restrict) refused it. Answered as the
            // conflict it is rather than a 500.
            _db.Entry(role).State = EntityState.Detached;
            return new RoleWriteResponse(
                RoleWriteOutcome.InUse,
                $"'{role.Name}' was assigned to a user while it was being deleted, so it was not removed. " +
                "Reassign that user and try again.",
                null);
        }

        return new RoleWriteResponse(
            RoleWriteOutcome.Saved,
            $"Role '{role.Name}' was deleted. No user held it.",
            new RoleDto(role.Id, role.Name, role.Description, role.IsSystem, 0, []));
    }

    // --------------------------------------------------------------------------------------- plumbing

    private static string? Normalize(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static RoleWriteResponse? Validate(string name, string? description)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            return new RoleWriteResponse(RoleWriteOutcome.ValidationFailed, "A role name is required.", null);

        if (trimmed.Length > NameMaxLength)
            return new RoleWriteResponse(
                RoleWriteOutcome.ValidationFailed,
                $"The role name is longer than the {NameMaxLength} characters allowed.", null);

        if (description is { } d && d.Trim().Length > DescriptionMaxLength)
            return new RoleWriteResponse(
                RoleWriteOutcome.ValidationFailed,
                $"The description is longer than the {DescriptionMaxLength} characters allowed.", null);

        return null;
    }

    private async Task<RoleWriteResponse> SavedAsync(Guid id, string message, CancellationToken ct) =>
        new(RoleWriteOutcome.Saved, message, await GetAsync(id, ct));

    private static RoleWriteResponse NotFound() =>
        new(RoleWriteOutcome.NotFound, "Role not found.", null);

    private static RoleWriteResponse NameExists(string name) =>
        new(RoleWriteOutcome.NameExists,
            $"A role named '{name}' already exists. Role names are unique.", null);

    private static RoleWriteResponse SystemProtected(Role role, string verb) =>
        new(RoleWriteOutcome.SystemRoleProtected,
            $"'{role.Name}' is a built-in role and cannot be {verb}. Its name is used throughout the " +
            "system and its permissions are the approved defaults. Create a custom role if you need " +
            "different access.",
            null);
}
