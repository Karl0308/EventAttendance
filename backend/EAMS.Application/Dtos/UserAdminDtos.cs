namespace EAMS.Application.Dtos;

/// <summary>One role a user holds, as an id/name pair so a picker can round-trip the selection.</summary>
public record UserRoleRef(Guid Id, string Name);

/// <summary>
/// One user, as <c>GET /users</c> and <c>GET /users/{id}</c> publish them.
/// </summary>
/// <param name="Email">The login identifier, normalized.</param>
/// <param name="IsActive"><c>false</c> is deactivated — the account cannot sign in, and its historical
/// rows (attendance it recorded, audit entries) are untouched.</param>
/// <param name="LastLoginAt">When the user last signed in, or null if never.</param>
/// <param name="Roles">
/// Every role the user holds. Empty is an ordinary state — a user with no role has no access, which is
/// §11's "users without roles" case, not an error.
/// </param>
public record UserDto(
    Guid Id,
    string Email,
    string FullName,
    string? Phone,
    bool IsActive,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    IReadOnlyList<UserRoleRef> Roles);

/// <summary>
/// The body of <c>POST /users</c>.
/// </summary>
/// <remarks>
/// <b>The create delegates to <see cref="Abstractions.IUserProvisioningService"/></b>, so the password
/// policy, e-mail normalization, duplicate refusal and audit row are the one vetted implementation both
/// the console <c>create-admin</c> and this endpoint use. A user is created with one role; further roles
/// are assigned afterwards through <c>PUT /users/{id}/roles</c>.
/// </remarks>
/// <param name="Password">
/// The initial password. Held to the same length policy the provisioning service enforces.
/// </param>
public record UserCreateRequest(
    string Email, string FullName, string? Phone, string RoleName, string Password)
{
    /// <summary>
    /// Redacted, deliberately. A positional record's generated <c>ToString()</c> prints every member,
    /// so a log line or debugger watch that rendered this request would leak the password — the exact
    /// reason <c>UserProvisioningRequest</c> keeps the password off its own record. This request has to
    /// carry it (it is one JSON body on the wire), so it hides it instead.
    /// </summary>
    public override string ToString() =>
        $"{nameof(UserCreateRequest)} {{ Email = {Email}, FullName = {FullName}, RoleName = {RoleName}, Password = *** }}";
}

/// <summary>
/// The body of <c>PUT /users/{id}</c> — the editable profile fields.
/// </summary>
/// <remarks>
/// <b>E-mail is deliberately not editable here.</b> It is the login identifier and the subject of the
/// global unique index <c>UX_Users_Email</c>; changing it is an identity change with its own
/// consequences (existing sessions, audit trails that name the address) and is out of scope for a
/// profile edit. Deactivating and re-creating is the supported path if an address must change.
/// </remarks>
public record UserUpdateRequest(string FullName, string? Phone);

/// <summary>
/// The body of <c>PATCH /users/{id}/active</c>.
/// </summary>
/// <param name="IsActive">
/// Nullable, and an omitted member is a 400 rather than a default — the same reasoning
/// <c>ClassificationActiveRequest.IsActive</c> records: a missing member binds to <c>false</c>, and on
/// this route <c>false</c> locks a user out.
/// </param>
public record UserActiveRequest(bool? IsActive);

/// <summary>
/// The body of <c>PUT /users/{id}/roles</c> — the complete set of roles the user should hold after the
/// call (a replacement, not a delta).
/// </summary>
/// <param name="RoleIds">
/// The roles to hold. An empty list is allowed and leaves the user with no access — §11's "users without
/// roles" state — but null is a 400: it is the difference between "remove every role" and "the field was
/// not sent".
/// </param>
public record UserRolesRequest(IReadOnlyList<Guid>? RoleIds);
