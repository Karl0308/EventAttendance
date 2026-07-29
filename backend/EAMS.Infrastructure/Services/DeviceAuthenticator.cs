using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// Verifies a presented <c>DeviceKey</c> token (Phase 4a design, D-23 / D-24).
/// </summary>
internal sealed class DeviceAuthenticator : IDeviceAuthenticator
{
    /// <summary>
    /// How stale <c>ApiKeyLastUsedAt</c> is allowed to get before an authenticated request refreshes
    /// it. One minute: fine enough that "when did this kiosk last talk to us?" is answerable, coarse
    /// enough that a queue drain of two hundred taps writes the row once instead of two hundred times.
    /// </summary>
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(1);

    private readonly EamsDbContext _db;
    private readonly ILogger<DeviceAuthenticator> _logger;

    public DeviceAuthenticator(EamsDbContext db, ILogger<DeviceAuthenticator> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// <b>This is the one query in the system that deliberately ignores the <c>SchoolId</c> global
    /// query filter, and the reason is not an optimization.</b> Authentication is what <em>establishes</em>
    /// the tenant. At the moment it runs there are no claims, so <c>ClaimsSchoolContext</c> is falling
    /// back to whatever school the host pinned at startup — and looking a device up through that filter
    /// would mean a second school's kiosk could never authenticate at all, on a host that had happened
    /// to pin the first school. A device key is globally unique by construction (48 bits of key id plus
    /// 256 bits of secret), so the lookup does not need a tenant to be unambiguous; it produces one.
    ///
    /// <para>
    /// <b>The seek on <c>ApiKeyId</c> is not constant-time, and that question is moot rather than
    /// merely tolerable.</b> A timing oracle is only worth anything for a value an attacker cannot
    /// otherwise obtain — and <c>ApiKeyId</c> is returned in <c>DeviceDto</c> from an open
    /// <c>GET /devices</c> and is documented as safe to show and to log. There is nothing to extract
    /// by measurement that is not already published. The <em>secret</em> is the value that must not
    /// leak by timing, and that comparison is <see cref="DeviceKey.SecretMatches"/>, which is
    /// fixed-time.
    /// </para>
    ///
    /// <para>
    /// The disclosure objection that applies to <c>IgnoreQueryFilters</c> elsewhere — see
    /// <c>AttendanceService.FindByDeviceTapAsync</c>, where reaching across the filter would have put
    /// another school's student in a response body — does not apply here: this projection returns two
    /// ids and a pair of flags, and the caller had to present that device's own secret to see them.
    /// </para>
    /// </summary>
    public async Task<DeviceAuthentication> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        // Shape first, so a junk header costs a string comparison rather than an index seek.
        if (!DeviceKey.TryParse(token, out var keyId, out var secret))
            return DeviceAuthentication.Failed(DeviceAuthenticationOutcome.Malformed);

        var candidate = await _db.Devices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.ApiKeyId == keyId)
            .Select(d => new
            {
                d.Id,
                d.SchoolId,
                d.ApiKeyHash,
                d.ApiKeyRevokedAt,
                d.IsActive,
            })
            .FirstOrDefaultAsync(ct);

        if (candidate is null)
            return DeviceAuthentication.Failed(DeviceAuthenticationOutcome.UnknownKey);

        // Constant-time. See DeviceKey.SecretMatches.
        if (!DeviceKey.SecretMatches(secret, candidate.ApiKeyHash))
            return DeviceAuthentication.Failed(DeviceAuthenticationOutcome.SecretMismatch);

        // Both of these identify the device and then refuse it: 403, not 401. The order matters only
        // for the message — a revoked key on a retired device is reported as revoked, because that is
        // the one the operator acted on deliberately.
        if (candidate.ApiKeyRevokedAt is not null)
            return new DeviceAuthentication(
                DeviceAuthenticationOutcome.Revoked, candidate.Id, candidate.SchoolId);

        if (!candidate.IsActive)
            return new DeviceAuthentication(
                DeviceAuthenticationOutcome.DeviceInactive, candidate.Id, candidate.SchoolId);

        return new DeviceAuthentication(
            DeviceAuthenticationOutcome.Authenticated, candidate.Id, candidate.SchoolId);
    }

    /// <summary>
    /// Stamps <c>ApiKeyLastUsedAt</c> and <c>LastSeenAt</c>, at most once per
    /// <see cref="LastUsedThrottle"/> per device.
    ///
    /// <para>
    /// <c>ExecuteUpdateAsync</c> rather than load-modify-save: it is one statement, it takes no change
    /// tracker entry (so it cannot interfere with whatever the request goes on to write on the same
    /// context), and the <c>WHERE</c> clause carries the throttle — so two concurrent taps race into
    /// the same UPDATE rather than into a read-then-write, and the loser simply updates zero rows.
    /// </para>
    ///
    /// <para>
    /// <b>It swallows its own failure, and that is not a silent catch.</b> The exception is logged at
    /// warning with the device id; what is suppressed is the <em>propagation</em>, because this runs
    /// after a tap has already been accepted and failing the request over a liveness timestamp would
    /// turn a recorded attendance into a 500 that §8.2's client retries — re-recording nothing and
    /// failing again. The rule being applied is "fail loud at boundaries"; this is not a boundary, it
    /// is bookkeeping riding on one.
    /// </para>
    /// </summary>
    public async Task TouchAsync(Guid deviceId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var stale = now - LastUsedThrottle;

        try
        {
            await _db.Devices
                .IgnoreQueryFilters()
                .Where(d => d.Id == deviceId
                         && (d.ApiKeyLastUsedAt == null || d.ApiKeyLastUsedAt < stale))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.ApiKeyLastUsedAt, now)
                          .SetProperty(d => d.LastSeenAt, now),
                    ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not stamp liveness for device {DeviceId}. The request itself is unaffected; " +
                "LastSeenAt will be stale until the next successful call or heartbeat.",
                deviceId);
        }
    }
}
