namespace EAMS.Api.Authorization;

/// <summary>
/// <b>This attribute enforces nothing by itself.</b> The <c>[Authorize(AuthenticationSchemes = …,
/// Policy = …)]</c> beside it on every action is what refuses a request; this one records the same
/// permission code in a form the registry tests can read.
///
/// <para>
/// It began as the placeholder for Technical Plan §11's permission-based RBAC, deferred by ADR-001
/// D-6, so that enforcement could be wired onto a list of already-declared codes instead of an audit
/// of every controller. That is what happened: every action that carries it now also carries a real
/// <c>[Authorize]</c> naming the same code as its policy.
/// </para>
///
/// <para>
/// <b>The two must agree, and a test holds them to it.</b> An <c>[Authorize(Policy = "x")]</c> over a
/// <c>[HasPermissionNotEnforced("y")]</c> would enforce one permission while documenting another.
/// <c>AuthorizationCoverageTests</c> fails the build on that, and on any action outside the anonymous
/// <c>/auth</c> pair that declares a permission without enforcing it.
/// </para>
///
/// <para>
/// <b>It is structurally incapable of enforcing anything.</b> It derives from <see cref="Attribute"/>
/// and implements no ASP.NET Core interface, so the MVC filter pipeline never sees it. Collapsing the
/// pair into one enforcing <c>[HasPermission]</c> — the name §11 uses — is a rename worth doing, and
/// the coverage test is what makes it safe to do mechanically.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method,
    AllowMultiple = true,
    Inherited = true)]
public sealed class HasPermissionNotEnforcedAttribute : Attribute
{
    /// <param name="permission">
    /// The §4.11 permission code the adjacent <c>[Authorize]</c> enforces — e.g. <c>students.read</c>,
    /// <c>events.write</c>, <c>attendance.capture</c>.
    /// </param>
    public HasPermissionNotEnforcedAttribute(string permission) => Permission = permission;

    /// <summary>Declared only. The policy on the adjacent <c>[Authorize]</c> is what decides.</summary>
    public string Permission { get; }
}
