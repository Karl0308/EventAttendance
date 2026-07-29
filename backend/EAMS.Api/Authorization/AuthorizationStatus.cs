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
            "AUTHORIZATION IS NOT ENFORCED. Every endpoint in this API is open and unauthenticated " +
            "EXCEPT the {GatedCount} capture endpoints a device key gates ({Gated}). " +
            "[HasPermissionNotEnforced] records intended permissions only and guards nothing, and " +
            "this assembly carries [assembly: AuthorizationNotEnforced]. Deferred by ADR-001 D-6 " +
            "until the data layer stabilizes; Technical Plan §11 (JWT + permission-based RBAC) is " +
            "the fix. Do not expose this build outside local/development use.",
            GatedEndpoints.Length,
            string.Join(", ", GatedEndpoints));
    }

    /// <summary>
    /// The endpoints Phase 4b narrowed the open surface to (Phase 4a design, D-28), named so the
    /// startup warning states what is <em>and is not</em> covered rather than making a claim a reader
    /// has to go and verify.
    ///
    /// <para>
    /// <c>POST /attendance/tap/batch</c> is deliberately absent: it is published as frozen contract to
    /// the mobile developer but is Phase 4d work and does not exist yet. Listing an endpoint that
    /// returns 404 would make this line the wrong kind of documentation.
    /// </para>
    /// </summary>
    public static readonly string[] GatedEndpoints =
    [
        "POST /api/v1/attendance/tap",
        "GET /api/v1/students/by-card/{cardUid}",
        "POST /api/v1/devices/{id}/heartbeat",
    ];
}
