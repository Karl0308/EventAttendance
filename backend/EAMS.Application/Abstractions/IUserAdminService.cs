using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// The outcome of a user-administration write. An enum for the reason every other write surface uses one
/// (<see cref="ClassificationWriteOutcome"/>, <see cref="TermWriteOutcome"/>): the service owns the
/// decision, the controller owns only the HTTP translation.
/// </summary>
public enum UserAdminOutcome
{
    /// <summary>The write happened, or the request asked for a state the user was already in.</summary>
    Saved,

    /// <summary>No user with that id in this tenant. 404.</summary>
    NotFound,

    /// <summary>A field failed a column rule or the password policy. 400; the message names which.</summary>
    ValidationFailed,

    /// <summary><c>UX_Users_Email</c> already holds that address. 409. Refused, never reset.</summary>
    EmailInUse,

    /// <summary>A named role, or one of the requested role ids, does not exist. 409.</summary>
    UnknownRole,

    /// <summary>No school could be resolved to file a new user under. 409.</summary>
    NoSchoolResolved,

    /// <summary>
    /// <b>The request would have locked the acting administrator out of their own account.</b> 409.
    /// Deactivating yourself, or changing your own roles, is refused — the one action nobody can safely
    /// undo, because after it they can no longer reach the screen that would undo it.
    /// </summary>
    SelfLockout,
}

/// <summary>The result of a user-administration write. <see cref="User"/> is null unless it saved.</summary>
public record UserAdminWriteResponse(UserAdminOutcome Outcome, string Message, UserDto? User);

/// <summary>
/// <b>§11's User Management surface (UserWithRBAC.docx) — the admin CRUD over <c>Users</c> and their
/// role assignments.</b>
///
/// <para>
/// Users are tenant-scoped: every read here is bounded by the global <c>SchoolId</c> query filter to the
/// caller's school, and a create is filed under it. Roles are global (§4.11 <c>Roles</c> has no
/// <c>SchoolId</c>), so role administration itself is a separate surface — this one only <em>assigns</em>
/// existing roles.
/// </para>
///
/// <para>
/// <b>Creation delegates to <see cref="IUserProvisioningService"/></b> so the password policy, e-mail
/// normalization, duplicate refusal, school resolution and audit row are one implementation rather than
/// two that drift — the rule that interface's own remarks set out.
/// </para>
/// </summary>
public interface IUserAdminService
{
    /// <summary>
    /// <c>GET /users</c> — the school's users, active first then by name.
    /// </summary>
    /// <param name="search">
    /// Optional case-insensitive fragment matched against e-mail and full name. Blank matches all.
    /// </param>
    /// <param name="includeInactive">
    /// <c>false</c> (default) returns only active accounts; <c>true</c> includes deactivated ones, which
    /// an administration screen needs to reactivate them.
    /// </param>
    Task<PagedResult<UserDto>> ListAsync(
        string? search, bool includeInactive, PageRequest page, CancellationToken ct = default);

    /// <summary><c>GET /users/{id}</c> — one user, or null when this tenant has no such row.</summary>
    Task<UserDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /users</c> — create a user with an initial role, through the provisioning service.
    /// </summary>
    Task<UserAdminWriteResponse> CreateAsync(UserCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PUT /users/{id}</c> — edit the profile fields (full name, phone). Not the e-mail; see
    /// <see cref="UserUpdateRequest"/>.
    /// </summary>
    Task<UserAdminWriteResponse> UpdateAsync(
        Guid id, UserUpdateRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PATCH /users/{id}/active</c> — deactivate a user (they can no longer sign in) or reactivate one.
    /// Idempotent. Refused with <see cref="UserAdminOutcome.SelfLockout"/> if the caller aims it at their
    /// own account.
    /// </summary>
    Task<UserAdminWriteResponse> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// <c>PUT /users/{id}/roles</c> — replace the set of roles the user holds. Refused with
    /// <see cref="UserAdminOutcome.SelfLockout"/> if the caller aims it at their own account — changing
    /// your own roles is how you remove your own access with nothing left to restore it.
    /// </summary>
    Task<UserAdminWriteResponse> SetRolesAsync(
        Guid id, IReadOnlyList<Guid> roleIds, CancellationToken ct = default);
}
