namespace EAMS.Application.Abstractions;

/// <summary>One §4.11 permission the product declares.</summary>
/// <param name="Code">The code an endpoint demands and a claim carries. Ordinal — case is significant.</param>
public record RbacPermissionSeed(string Code, string? Description);

/// <summary>
/// One §4.11 role and the permissions it is created holding.
/// </summary>
/// <param name="PermissionCodes">
/// The grant matrix for this role. <b>Applied at creation and never re-applied</b> — see
/// <see cref="RbacReferenceData"/> for why.
/// </param>
public record RbacRoleSeed(string Name, string? Description, IReadOnlyList<string> PermissionCodes);

/// <summary>
/// <b>The §4.11 rows that must exist in every environment, Production included.</b>
///
/// <para>
/// <b>This is reference data, not seed data, and the distinction is the one this phase existed to
/// draw.</b> The composition root used to express seeding as a single boolean — "are we in
/// Development?" — which put the <c>Permissions</c> rows and the four <c>Roles</c> on the same switch
/// as eight fictional students and a well-known kiosk key. That is a defect waiting for the day
/// enforcement goes live: permission checks over an empty <c>RolePermissions</c> table authorize
/// nobody, so the first production deployment with §11 turned on would lock every operator out of
/// their own installation, and the cause would look like a broken authorization layer rather than a
/// missing seed. Convenience data is Development-only; the rows the authorization model is *made of*
/// are not optional anywhere.
/// </para>
///
/// <para>
/// <b>Grants are written at role creation and never reconciled afterwards.</b> An administrator who
/// narrows <c>Organizer</c> has made a decision about their institution, and a startup that re-applied
/// the matrix would silently revert it on the next deploy — restoring permissions somebody
/// deliberately removed, with no log line and no audit entry. Adding a role that does not exist is
/// additive and safe; rewriting one that does is not. A genuinely new grant for an existing role is a
/// migration or an operator action, not a startup side effect.
/// </para>
///
/// <para>
/// <b>It is passed in from the composition root rather than defined here.</b> The registry of codes
/// lives beside the endpoints that declare them (<c>EAMS.Api.Authorization.EamsPermissions</c>, which
/// <c>PermissionRegistryTests</c> holds to the endpoints in both directions), and Infrastructure
/// cannot see <c>EAMS.Api</c> — the layering rule runs the other way. Handing the seeder its data
/// keeps the single source of truth where the compiler can enforce it and keeps the seeder ignorant of
/// what the codes mean.
/// </para>
/// </summary>
public record RbacReferenceData(
    IReadOnlyList<RbacPermissionSeed> Permissions,
    IReadOnlyList<RbacRoleSeed> Roles)
{
    /// <summary>
    /// Nothing to seed. Used by hosts that have no authorization registry to supply — a migration
    /// tool, a test that builds the container directly — so that the absence is stated rather than
    /// arrived at by passing null.
    /// </summary>
    public static RbacReferenceData Empty { get; } = new([], []);
}
