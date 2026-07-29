namespace EAMS.Api.Authorization;

/// <summary>
/// The claim types every principal in this system carries, whichever scheme produced it.
///
/// <para>
/// <b>They are short JWT-style names, not the WS-Federation URIs <c>ClaimTypes.*</c> hands out, and
/// that is the point.</b> Phase 6 adds <c>.AddJwtBearer("Bearer")</c> beside the device scheme, and a
/// JWT's payload keys arrive verbatim as claim types — so a policy written against
/// <c>http://schemas.xmlsoap.org/ws/2005/05/identity/claims/…</c> would match the device scheme and
/// silently not match the JWT one. Naming them here, once, is what makes "the two schemes emit the
/// same claim <em>types</em>" a checkable statement rather than an intention.
/// </para>
/// </summary>
public static class EamsClaimTypes
{
    /// <summary>
    /// The principal's identity. <c>device:{id}</c> for a device key; a user id for Phase 6's JWT.
    /// Prefixed rather than bare so a device id and a user id can never be confused for each other by
    /// anything that reads only this claim.
    /// </summary>
    public const string Subject = "sub";

    /// <summary>
    /// The tenant. Read by <c>ClaimsSchoolContext</c> and therefore by every §11 global query filter —
    /// this single claim is what makes the multi-tenant guard real rather than pinned.
    /// </summary>
    public const string SchoolId = "school_id";

    /// <summary>
    /// One §4.11 permission code. Repeatable: a JWT principal will carry many, a device key carries
    /// exactly one. Policies are expressed as <c>RequireClaim(Permission, code)</c>, so a device and a
    /// user reaching the same endpoint are indistinguishable to the authorization layer — which is the
    /// property that lets Phase 6 <em>extend</em> this rather than replace it.
    /// </summary>
    public const string Permission = "perm";

    /// <summary>
    /// The authenticated device's <c>Devices.Id</c>, unprefixed. Device-specific and deliberately not
    /// read by any policy — the authorization layer must stay ignorant of what kind of principal it is
    /// looking at. Read only by <c>ClaimsDeviceContext</c>, for the D-26 cross-check.
    /// </summary>
    public const string DeviceId = "device_id";

    /// <summary>Builds the <see cref="Subject"/> value for a device.</summary>
    public static string DeviceSubject(Guid deviceId) => $"device:{deviceId}";
}

/// <summary>
/// The §4.11 permission codes that are <em>enforced</em> today. The full set of codes an endpoint may
/// declare lives on the <see cref="HasPermissionNotEnforcedAttribute"/> call sites; this class holds
/// only the ones that are also policy names.
/// </summary>
public static class EamsPermissions
{
    /// <summary>
    /// §11: "kiosks authenticate with a long-lived device API key scoped to <c>attendance.capture</c>
    /// only". The one permission a device key carries, and the policy name of the four gated
    /// endpoints — policy name and claim value are deliberately the same string, so a policy that
    /// exists but demands nothing is not expressible.
    /// </summary>
    public const string AttendanceCapture = "attendance.capture";
}
