using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// Why the outcome is an enum rather than an exception, and every member maps to exactly one HTTP
/// status in the controller: the same reasoning <see cref="TapOutcome"/> records. The service owns the
/// decision, the controller owns only the translation.
/// </summary>
public enum DeviceWriteOutcome
{
    Saved,

    /// <summary>No device with that id in this tenant.</summary>
    NotFound,

    /// <summary>A field outside §4.10's rules — a blank name, an unknown device type, an over-length model.</summary>
    ValidationFailed,

    /// <summary>
    /// No tenant could be resolved for a new device. Same refusal <c>StudentService</c> and
    /// <c>EventService</c> make: with zero or several schools and nothing pinned there is no honest
    /// answer, and guessing files the row under a school at random.
    /// </summary>
    NoSchoolResolved,

    /// <summary>
    /// The minted key id collided with one already in the table. 48 bits makes this effectively
    /// impossible, which is exactly why it is reported rather than retried in a loop: if it ever
    /// happens, the interesting fact is that it happened, not that a second attempt worked.
    /// </summary>
    KeyCollision,
}

/// <summary>
/// The result of a device write. <see cref="IssuedKey"/> is non-null on exactly two operations —
/// registration and rotation — and null on every other, including the successful ones.
/// </summary>
public record DeviceWriteResponse(
    DeviceWriteOutcome Outcome, string Message, DeviceDto? Device, DeviceKeyIssuedDto? IssuedKey);

/// <summary>
/// Technical Plan §6.6 — device registration and key lifecycle (Phase 4a design, D-25).
///
/// <para>
/// <b>Rotation is a hard cut with no overlap window.</b> The new key replaces the old one in the same
/// row; there is no second key column and no grace period during which both work. That is a real
/// operational cost — a kiosk keeps failing until someone re-enrols it — and it is chosen anyway,
/// because an overlap window means a compromised key stays valid for as long as the window lasts, and
/// the whole point of rotating is that it should not. The device is one device; it holds one
/// credential.
/// </para>
/// </summary>
public interface IDeviceService
{
    Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct = default);

    Task<DeviceDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// §6.6 <c>POST /devices</c>. Registers the device <em>and</em> issues its first key — the two are
    /// one operation because a registered device with no key cannot do the one thing it exists for,
    /// and splitting them would make "registered but useless" a reachable state nobody would notice.
    /// </summary>
    Task<DeviceWriteResponse> RegisterAsync(DeviceWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// §6.6 <c>PUT /devices/{id}</c> — the device's own fields. <b>Never touches the key.</b>
    /// Deactivating a device through <c>IsActive</c> stops it authenticating but leaves its credential
    /// intact and reissuable; burning the credential is <see cref="RevokeKeyAsync"/>. See
    /// <c>Device.ApiKeyRevokedAt</c> for why those are two different statements.
    /// </summary>
    Task<DeviceWriteResponse> UpdateAsync(Guid id, DeviceWriteRequest request, CancellationToken ct = default);

    /// <summary>§6.6 <c>POST /devices/{id}/regenerate-key</c> — revoke and reissue in one step.</summary>
    Task<DeviceWriteResponse> RegenerateKeyAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /devices/{id}/revoke-key</c> — burn the credential without issuing a replacement.
    ///
    /// <para>
    /// <b>Not in §6.6, and added deliberately.</b> §6.6 defines only regeneration, which conflates
    /// "this key is compromised" with "give me a new one" — and the published mobile contract already
    /// distinguishes them on the wire (<c>401</c> bad key versus <c>403</c> revoked key). Without this
    /// operation the 403 state is unreachable through the API and <c>Devices.ApiKeyRevokedAt</c> is a
    /// column nothing can write, which is a worse answer than one extra route.
    /// </para>
    ///
    /// <para>
    /// Idempotent: revoking an already-revoked key succeeds and changes nothing, so a retry after a
    /// timeout is safe. Revoking a device that has never held a key is <c>Saved</c> too — the
    /// postcondition ("this device cannot authenticate") holds either way.
    /// </para>
    /// </summary>
    Task<DeviceWriteResponse> RevokeKeyAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// §6.6 <c>POST /devices/{id}/heartbeat</c> — liveness. The one endpoint on this surface a device
    /// calls itself, which is why it is one of the four gated by the device key rather than by an
    /// admin permission.
    /// </summary>
    Task<DeviceWriteResponse> HeartbeatAsync(Guid id, CancellationToken ct = default);
}
