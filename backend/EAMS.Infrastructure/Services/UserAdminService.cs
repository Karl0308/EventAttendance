using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// §11's User Management surface — see <see cref="IUserAdminService"/>. Reads are bounded to the caller's
/// school by the global <c>SchoolId</c> query filter; creation goes through
/// <see cref="IUserProvisioningService"/> so no user is ever created by a rule the production bootstrap
/// path does not use.
/// </summary>
internal sealed class UserAdminService : IUserAdminService
{
    private readonly EamsDbContext _db;
    private readonly IUserProvisioningService _provisioning;

    /// <summary>
    /// Who is making the request — read for the two self-lockout guards, and to attribute the create's
    /// audit row. In a gated request this is the claims-reading implementation, so it is the signed-in
    /// administrator; it is null only in the pre-auth build, where these routes are unreachable anyway.
    /// </summary>
    private readonly ICurrentUser _currentUser;

    public UserAdminService(
        EamsDbContext db, IUserProvisioningService provisioning, ICurrentUser currentUser)
    {
        _db = db;
        _provisioning = provisioning;
        _currentUser = currentUser;
    }

    // ---------------------------------------------------------------------------------------- reads

    public async Task<PagedResult<UserDto>> ListAsync(
        string? search, bool includeInactive, PageRequest page, CancellationToken ct = default)
    {
        var query = _db.Users.AsNoTracking().AsQueryable();

        if (!includeInactive) query = query.Where(u => u.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var fragment = search.Trim();
            // SQL Server's default collation is case-insensitive, so Contains matches regardless of case.
            query = query.Where(u => u.Email.Contains(fragment) || u.FullName.Contains(fragment));
        }

        // Active first, then by name, then by Id so the order is total — PaginationTests enforces a
        // unique final sort column, without which OFFSET/FETCH repeats and skips rows between pages.
        return await query.ToPageAsync(
            q => q
                .OrderByDescending(u => u.IsActive)
                .ThenBy(u => u.FullName)
                .ThenBy(u => u.Id)
                .Select(u => new UserDto(
                    u.Id, u.Email, u.FullName, u.Phone, u.IsActive, u.LastLoginAt, u.CreatedAt,
                    u.UserRoles
                        .Select(ur => new UserRoleRef(ur.Role!.Id, ur.Role.Name))
                        .ToList())),
            page,
            ct);
    }

    public async Task<UserDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new UserDto(
                u.Id, u.Email, u.FullName, u.Phone, u.IsActive, u.LastLoginAt, u.CreatedAt,
                u.UserRoles.Select(ur => new UserRoleRef(ur.Role!.Id, ur.Role.Name)).ToList()))
            .FirstOrDefaultAsync(ct);
    }

    // --------------------------------------------------------------------------------- create/edit

    public async Task<UserAdminWriteResponse> CreateAsync(
        UserCreateRequest request, CancellationToken ct = default)
    {
        // Delegated: the password policy, e-mail normalization, duplicate refusal, school resolution and
        // audit row are the one vetted implementation. SchoolCode null means "the resolved tenant",
        // which on a gated request is the administrator's own school.
        var result = await _provisioning.CreateAsync(
            new UserProvisioningRequest(request.Email, request.FullName, request.RoleName),
            request.Password,
            Actor(),
            ct);

        if (result.Outcome != UserProvisioningOutcome.Created)
            return new UserAdminWriteResponse(MapProvisioning(result.Outcome), result.Message, null);

        // Phone is not part of the provisioning contract (the console command has no use for one), so it
        // is set here in a follow-up write when supplied. A blank phone is left null.
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        if (phone is not null)
        {
            if (phone.Length > PhoneMaxLength)
            {
                // The user was created; only the optional phone was out of range. Report it as validation
                // but with the user, so the caller sees the account exists and can fix the phone.
                return new UserAdminWriteResponse(
                    UserAdminOutcome.ValidationFailed,
                    $"The user was created, but the phone number is longer than {PhoneMaxLength} " +
                    "characters and was not saved. Edit the user to set it.",
                    await GetAsync(result.UserId, ct));
            }

            var created = await _db.Users.FirstAsync(u => u.Id == result.UserId, ct);
            created.Phone = phone;
            created.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return new UserAdminWriteResponse(
            UserAdminOutcome.Saved, result.Message, await GetAsync(result.UserId, ct));
    }

    public async Task<UserAdminWriteResponse> UpdateAsync(
        Guid id, UserUpdateRequest request, CancellationToken ct = default)
    {
        if (Validate(request, out var message) is false)
            return new UserAdminWriteResponse(UserAdminOutcome.ValidationFailed, message, null);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        user.FullName = request.FullName.Trim();
        user.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return await SavedAsync(id, "User updated.", ct);
    }

    // ------------------------------------------------------------------------------- deactivate

    public async Task<UserAdminWriteResponse> SetActiveAsync(
        Guid id, bool isActive, CancellationToken ct = default)
    {
        // The self-lockout guard, before the row is even read: deactivating your own account leaves you
        // unable to reach the screen that would reactivate it, and no other guard here can undo that.
        if (!isActive && _currentUser.UserId == id)
        {
            return new UserAdminWriteResponse(
                UserAdminOutcome.SelfLockout,
                "You cannot deactivate your own account — you would be signed out with no way back in. " +
                "Ask another administrator to do it.",
                null);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        if (user.IsActive == isActive)
        {
            return await SavedAsync(
                id,
                isActive ? "That user was already active." : "That user was already deactivated.",
                ct);
        }

        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return await SavedAsync(
            id,
            isActive
                ? "User reactivated. They can sign in again."
                : "User deactivated. They can no longer sign in, and every record they created is kept.",
            ct);
    }

    // ------------------------------------------------------------------------------------ roles

    public async Task<UserAdminWriteResponse> SetRolesAsync(
        Guid id, IReadOnlyList<Guid> roleIds, CancellationToken ct = default)
    {
        // Changing your own roles is how you remove your own access with nothing left to restore it, so
        // it is refused for the same reason self-deactivation is.
        if (_currentUser.UserId == id)
        {
            return new UserAdminWriteResponse(
                UserAdminOutcome.SelfLockout,
                "You cannot change your own roles — you could remove the access that lets you manage " +
                "users at all. Ask another administrator to change them.",
                null);
        }

        var user = await _db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var wanted = roleIds.Distinct().ToList();

        // Roles are global (§4.11 has no SchoolId), so this is not tenant-filtered — but every id must
        // name a real role, or the set would silently drop the unknown ones.
        var known = await _db.Roles.AsNoTracking()
            .Where(r => wanted.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync(ct);

        var missing = wanted.Except(known).ToList();
        if (missing.Count > 0)
        {
            return new UserAdminWriteResponse(
                UserAdminOutcome.UnknownRole,
                $"{missing.Count} of the requested role id(s) do not exist. Nothing was changed.",
                null);
        }

        var current = user.UserRoles.Select(ur => ur.RoleId).ToHashSet();
        var target = wanted.ToHashSet();

        foreach (var toRemove in user.UserRoles.Where(ur => !target.Contains(ur.RoleId)).ToList())
            _db.UserRoles.Remove(toRemove);

        foreach (var roleId in target.Where(rid => !current.Contains(rid)))
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return await SavedAsync(
            id,
            target.Count == 0
                ? "All roles removed. This user now has no access until a role is assigned."
                : "Roles updated. The change takes effect the next time the user signs in or their token " +
                  "refreshes.",
            ct);
    }

    // --------------------------------------------------------------------------------------- plumbing

    internal const int PhoneMaxLength = 30;
    internal const int FullNameMaxLength = 200;

    private string Actor() =>
        _currentUser.UserId is { } id ? $"user:{id}" : "unauthenticated";

    private static bool Validate(UserUpdateRequest request, out string message)
    {
        message = "";

        var fullName = request.FullName?.Trim() ?? "";
        if (fullName.Length == 0)
        {
            message = "A full name is required.";
            return false;
        }

        if (fullName.Length > FullNameMaxLength)
        {
            message = $"The full name is longer than the {FullNameMaxLength} characters Users.FullName holds.";
            return false;
        }

        if (request.Phone is { } phone && phone.Trim().Length > PhoneMaxLength)
        {
            message = $"The phone number is longer than the {PhoneMaxLength} characters Users.Phone holds.";
            return false;
        }

        return true;
    }

    private static UserAdminOutcome MapProvisioning(UserProvisioningOutcome outcome) => outcome switch
    {
        UserProvisioningOutcome.Created => UserAdminOutcome.Saved,
        UserProvisioningOutcome.EmailInUse => UserAdminOutcome.EmailInUse,
        UserProvisioningOutcome.ValidationFailed => UserAdminOutcome.ValidationFailed,
        UserProvisioningOutcome.UnknownRole => UserAdminOutcome.UnknownRole,
        UserProvisioningOutcome.NoSchoolResolved => UserAdminOutcome.NoSchoolResolved,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome, "No UserAdminOutcome is mapped for this provisioning outcome."),
    };

    private async Task<UserAdminWriteResponse> SavedAsync(Guid id, string message, CancellationToken ct) =>
        new(UserAdminOutcome.Saved, message, await GetAsync(id, ct));

    private static UserAdminWriteResponse NotFound() =>
        new(UserAdminOutcome.NotFound, "User not found.", null);
}
