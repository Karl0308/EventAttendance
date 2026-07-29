namespace EAMS.Application.Dtos;

/// <summary>
/// Technical Plan §4.10 / §6.6, as a client sees it.
///
/// <para>
/// <b>There is deliberately no key-shaped field on this record.</b> Not the secret, not the hash, not
/// the token. The plaintext key exists in exactly two responses — the 201 from <c>POST /devices</c> and
/// the 200 from <c>POST /devices/{id}/regenerate-key</c> — and in <see cref="DeviceKeyIssuedDto"/>
/// alone. Adding a <c>apiKey</c> here to "make the list more useful" would turn every
/// <c>GET /devices</c> into a credential dump, which is precisely why
/// <c>DeviceLifecycleTests.No_read_response_ever_carries_the_key</c> asserts on the serialized bytes
/// rather than on the record's shape.
/// </para>
/// </summary>
/// <param name="ApiKeyId">
/// The <em>public</em> half of the key — the twelve characters between <c>eams_dk_</c> and the secret.
/// Safe to show and to log: it identifies the credential without being one, which is what makes
/// "device 3's key id is <c>a91f…</c>, and the log line that failed says <c>a91f…</c>" a possible
/// sentence. Null when the device has never been issued a key.
/// </param>
/// <param name="HasActiveKey">
/// Whether this device could authenticate right now: it has a key, the key is not revoked, and the
/// device is active. A single boolean rather than three, because "why can this kiosk not tap?" is one
/// question and the three columns answer it together.
/// </param>
public record DeviceDto(
    Guid Id,
    string Name,
    string DeviceType,
    string? ReaderModel,
    bool IsActive,
    string? ApiKeyId,
    bool HasActiveKey,
    DateTime? ApiKeyIssuedAt,
    DateTime? ApiKeyLastUsedAt,
    DateTime? ApiKeyRevokedAt,
    DateTime? LastSeenAt);

/// <summary>
/// The body of <c>POST /devices</c> and <c>PUT /devices/{id}</c>.
///
/// <para>
/// <b><c>SchoolId</c> is deliberately absent</b>, exactly as it is on <c>EventWriteRequest</c>: the
/// tenant comes from <see cref="EAMS.Application.Abstractions.ISchoolContext"/>, never from the
/// request. A caller-supplied tenant on a write is the hole Phase 4b closed on the tap path
/// (D-27); this surface does not open a second one.
/// </para>
///
/// <para>
/// <b>No key field either.</b> A key is minted by the server or it is not minted — see
/// <c>DeviceKey.Issue</c> for why an operator-chosen key would invalidate the hashing decision.
/// </para>
/// </summary>
public record DeviceWriteRequest(string Name, string? DeviceType, string? ReaderModel, bool IsActive);

/// <summary>
/// The one and only carrier of a plaintext device key.
///
/// <para>
/// Returned at issue and at rotation, and never afterwards — the server keeps only
/// <c>SHA-256(secret)</c>, so "show me the key again" has no implementation rather than a refused
/// one. An operator who loses it rotates.
/// </para>
/// </summary>
/// <param name="ApiKey">
/// The complete <c>eams_dk_&lt;keyId&gt;_&lt;secret&gt;</c> token. Frozen format — see
/// <c>docs/api/attendance-contract-handoff.md</c>.
/// </param>
public record DeviceKeyIssuedDto(DeviceDto Device, string ApiKey);
