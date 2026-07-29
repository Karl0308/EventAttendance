namespace EAMS.Application.Abstractions;

/// <summary>
/// What happened when a presented <c>DeviceKey</c> token was checked.
///
/// <para>
/// <b>The split between the 401 members and the 403 members is the published contract, not a
/// preference.</b> <c>docs/api/attendance-contract-handoff.md</c> tells the mobile developer:
/// "<c>401</c> missing or bad key, <c>403</c> revoked key". A client has to tell "your credential is
/// wrong — check what you stored" from "your credential was turned off — re-enrol", because those need
/// different behaviour from an offline queue, and a single 401 for both makes them indistinguishable.
/// </para>
///
/// <para>
/// So <see cref="Malformed"/>, <see cref="UnknownKey"/> and <see cref="SecretMismatch"/> fail
/// authentication outright (401), while <see cref="Revoked"/> and <see cref="DeviceInactive"/>
/// <em>succeed</em> at authentication — the device is identified — and are denied by authorization for
/// carrying no <c>attendance.capture</c> permission (403).
/// </para>
/// </summary>
public enum DeviceAuthenticationOutcome
{
    /// <summary>The token is not shaped like a key. No query was run.</summary>
    Malformed,

    /// <summary>No device holds that key id.</summary>
    UnknownKey,

    /// <summary>The key id resolved, the secret did not match.</summary>
    SecretMismatch,

    /// <summary>The credential was burned (<c>Devices.ApiKeyRevokedAt</c>). Identified, not permitted.</summary>
    Revoked,

    /// <summary>The device is retired (<c>Devices.IsActive = 0</c>). Identified, not permitted.</summary>
    DeviceInactive,

    /// <summary>Authenticated and permitted.</summary>
    Authenticated,
}

/// <summary>
/// The device behind an accepted or identified key. Ids only — deliberately no name, no school name,
/// nothing a wrong answer could disclose. See <c>DeviceAuthenticator</c> for why this lookup is the one
/// query in the system that legitimately ignores the tenant filter.
/// </summary>
public record DeviceAuthentication(DeviceAuthenticationOutcome Outcome, Guid DeviceId, Guid SchoolId)
{
    /// <summary>The failures that never resolved a device. Ids are <see cref="Guid.Empty"/>.</summary>
    public static DeviceAuthentication Failed(DeviceAuthenticationOutcome outcome) =>
        new(outcome, Guid.Empty, Guid.Empty);
}

/// <summary>
/// Verifies a presented device key and reports the outcome (Phase 4a design, D-23).
///
/// <para>
/// It lives in the Application layer for the same reason every other service interface does: the
/// ASP.NET Core authentication handler that calls it is in <c>EAMS.Api</c>, and nothing outside
/// <c>EAMS.Infrastructure</c> may see <c>EamsDbContext</c>. The handler therefore knows about tokens
/// and claims and nothing about tables.
/// </para>
/// </summary>
public interface IDeviceAuthenticator
{
    Task<DeviceAuthentication> AuthenticateAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Records that this device was seen, <b>throttled to at most once a minute per device</b>.
    ///
    /// <para>
    /// §4.10 keeps <c>LastSeenAt</c> for liveness and §6.6 gives it a dedicated heartbeat endpoint, but
    /// a kiosk that is tapping is self-evidently alive and should not need a second call to say so. The
    /// throttle is what makes that free: without it, every tap in a queue drain becomes an extra UPDATE
    /// on the row every one of those taps is already reading — a write on the hot path for a column
    /// nobody reads to the second.
    /// </para>
    ///
    /// <para>
    /// Never throws. A failed liveness stamp must not fail the request it was riding on.
    /// </para>
    /// </summary>
    Task TouchAsync(Guid deviceId, CancellationToken ct = default);
}
