using EAMS.Api.Authorization;

// Assembly-level marker. Machine-readable proof that nothing in EAMS.Api enforces authorization —
// a test, a startup check, or a deployment gate can assert its absence before allowing a
// non-development environment, without relying on anyone having read a comment.
[assembly: AuthorizationNotEnforced]

namespace EAMS.Api.Authorization;

/// <summary>
/// <b>THIS ATTRIBUTE ENFORCES NOTHING. A METHOD MARKED WITH IT IS PUBLIC AND UNAUTHENTICATED.</b>
///
/// <para>
/// It is a placeholder for Technical Plan §11's permission-based RBAC, deferred by ADR-001 D-6 until
/// the data layer settles. Its only job is to record which permission code an endpoint <em>will</em>
/// require, so Phase 6 wires enforcement instead of auditing every controller to work out what each
/// endpoint should have demanded.
/// </para>
///
/// <para>
/// <b>Why the name is so ugly.</b> ADR-001 D-6 calls out the hazard directly: a no-op
/// <c>[HasPermission]</c> is indistinguishable at a glance from a real one, so a reviewer reads a
/// decorated endpoint as protected and it ships open. The state therefore lives in the name — at the
/// call site this reads <c>[HasPermissionNotEnforced("students.read")]</c>, and there is no way to
/// see that in a diff and believe it guards anything. Renaming it to <c>[HasPermission]</c> is
/// Phase 6's job, and the rename is a feature: the compiler will point at every decorated endpoint
/// so each one is looked at on the day enforcement becomes real.
/// </para>
///
/// <para>
/// <b>It is also structurally incapable of enforcing anything.</b> It derives from
/// <see cref="Attribute"/> and implements no ASP.NET Core interface — not <c>IAuthorizationFilter</c>,
/// not <c>IAsyncAuthorizationFilter</c>, not <c>IFilterMetadata</c>, not <c>IAuthorizeData</c>. The
/// MVC filter pipeline never sees it, so it cannot short-circuit a request even by accident. The
/// inertness is a property of the type, not a promise in a comment.
/// </para>
///
/// <para>
/// Nothing is decorated with it yet, and the API is open. Per ADR-001 D-6 this system must not be
/// exposed beyond local/development use until §11 lands in full.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method,
    AllowMultiple = true,
    Inherited = true)]
public sealed class HasPermissionNotEnforcedAttribute : Attribute
{
    /// <param name="permission">
    /// The §4.11 permission code this endpoint will require once §11 is implemented — e.g.
    /// <c>students.read</c>, <c>events.write</c>, <c>attendance.capture</c>.
    /// </param>
    public HasPermissionNotEnforcedAttribute(string permission) => Permission = permission;

    /// <summary>Declared intent only. Nothing reads this to make a decision.</summary>
    public string Permission { get; }
}

/// <summary>
/// Applied to an assembly whose authorization is a placeholder (ADR-001 D-6). Present on EAMS.Api
/// today; removing it is part of Phase 6's definition of done.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class AuthorizationNotEnforcedAttribute : Attribute
{
}
