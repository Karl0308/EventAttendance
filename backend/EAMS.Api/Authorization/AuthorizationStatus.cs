using System.Reflection;

namespace EAMS.Api.Authorization;

/// <summary>
/// The runtime half of making ADR-001 D-6's deferred authorization impossible to miss. The attribute
/// is only visible to someone reading the source; this is visible to anyone who starts the process.
/// </summary>
public static class AuthorizationStatus
{
    /// <summary>
    /// <c>false</c> until Technical Plan §11 is implemented.
    ///
    /// <para>
    /// Derived from the assembly's <see cref="AuthorizationNotEnforcedAttribute"/> rather than
    /// declared separately, so there is exactly one thing to change and no way for a flag and a
    /// marker to disagree. Anything that needs to gate on real authorization — a deployment check, a
    /// QA assertion, a future "refuse to start outside Development while open" guard — reads this.
    /// </para>
    /// </summary>
    public static bool IsEnforced =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AuthorizationNotEnforcedAttribute>() is null;

    /// <summary>
    /// Writes the state of authorization to the log at startup. Deliberately at
    /// <see cref="LogLevel.Warning"/> and deliberately not gated on environment: an open API is a
    /// warning wherever it runs, and a line that only appears in Development teaches nobody that the
    /// build they just deployed is open.
    /// </summary>
    public static void LogEnforcementState(ILogger logger)
    {
        if (IsEnforced) return;

        logger.LogWarning(
            "AUTHORIZATION IS NOT ENFORCED. Every endpoint in this API is open and unauthenticated. " +
            "[HasPermissionNotEnforced] records intended permissions only and guards nothing, and " +
            "this assembly carries [assembly: AuthorizationNotEnforced]. Deferred by ADR-001 D-6 " +
            "until the data layer stabilizes; Technical Plan §11 (JWT + permission-based RBAC) is " +
            "the fix. Do not expose this build outside local/development use.");
    }
}
