namespace EAMS.Domain;

// Technical Plan §4.10 — RFID readers / kiosks.
public class Device : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public string Name { get; set; } = "";
    public string DeviceType { get; set; } = ""; // Mobile/Kiosk/Handheld — see DeviceTypes
    public string? ReaderModel { get; set; }

    /// <summary>
    /// §4.10's <c>ApiKey nvarchar(256) NULL UNIQUE</c>. <b>Permanently NULL, and it stays.</b>
    ///
    /// <para>
    /// Phase 4b (D-24) replaced a single stored key with the split
    /// <see cref="ApiKeyId"/> / <see cref="ApiKeyHash"/> pair, because a column that holds the key
    /// itself can only be verified by storing the secret in plaintext. This column is not dropped —
    /// the global no-data-loss rule forbids it, and it is the plan's own column, so removing it would
    /// make §4.10 and the schema disagree with nothing recording why.
    /// </para>
    ///
    /// <para>
    /// Nothing writes it.
    /// <c>DeviceLifecycleTests.Registering_and_rotating_never_write_the_plan_s_original_ApiKey_column</c>
    /// pins that: registering or rotating a device must leave it null, so a future "just put the key here, it is right there" edit fails the build
    /// rather than reintroducing a plaintext credential column.
    /// </para>
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The public half of the device key (D-24) — the twelve characters between <c>eams_dk_</c> and
    /// the secret. Indexed and unique; this is what a presented token is looked up by.
    /// </summary>
    public string? ApiKeyId { get; set; }

    /// <summary>
    /// SHA-256 of the secret half, lower-case hex. The secret itself is never stored. See
    /// <see cref="DeviceKey"/> for why a fast hash is the correct choice <em>here specifically</em>.
    /// </summary>
    public string? ApiKeyHash { get; set; }

    /// <summary>When the current key was issued. Null while the device has never had one.</summary>
    public DateTime? ApiKeyIssuedAt { get; set; }

    /// <summary>
    /// When this key was last accepted. Written opportunistically and <b>throttled</b> — see
    /// <c>DeviceAuthenticator</c>. A write on every tap would put an UPDATE on the hot path for a
    /// column nobody reads to the minute.
    /// </summary>
    public DateTime? ApiKeyLastUsedAt { get; set; }

    /// <summary>
    /// When the current key was burned.
    ///
    /// <para>
    /// <b>Deliberately distinct from <see cref="IsActive"/>, and the two must not be collapsed.</b>
    /// Revoking says "this credential is compromised or retired"; deactivating says "this physical
    /// device is out of service". Revoke-and-reissue on a device that stays in the gym is the ordinary
    /// case — a handset was lost, the kiosk it belonged to did not move.
    /// </para>
    ///
    /// <para>
    /// A revoked key still <em>authenticates</em> (the device is identified) but carries no
    /// <c>perm</c> claim, so it is a 403 rather than a 401. That is the published contract: the mobile
    /// handoff document says "<c>401</c> missing or bad key, <c>403</c> revoked key", and a client
    /// needs to tell "your key is wrong" from "your key was turned off" to decide whether to re-enrol.
    /// </para>
    /// </summary>
    public DateTime? ApiKeyRevokedAt { get; set; }

    public DateTime? LastSeenAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this device's stored key could authenticate a request at all. Both halves must be
    /// present — a row with an id and no hash cannot be verified, and treating it as usable would mean
    /// a device that authenticates on nothing.
    /// </summary>
    public bool HasKey => ApiKeyId is not null && ApiKeyHash is not null;
}

/// <summary>Technical Plan §4.10 — <c>Devices.DeviceType</c>.</summary>
public static class DeviceTypes
{
    public const string Mobile = "Mobile";
    public const string Kiosk = "Kiosk";
    public const string Handheld = "Handheld";

    public static readonly IReadOnlyList<string> All = [Mobile, Kiosk, Handheld];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// Technical Plan §4.10 — the bounded <c>nvarchar</c> columns on <c>Devices</c>. Lengths rather than
/// value sets, for the reason <see cref="AttendanceNotes"/> records: an over-length value reaching SQL
/// Server comes back as error 2628 (<c>String or binary data would be truncated</c>) — a 500 on input
/// the caller got wrong.
/// </summary>
public static class DeviceText
{
    /// <summary>Matches <c>Devices.Name nvarchar(100)</c>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Matches <c>Devices.ReaderModel nvarchar(100)</c>.</summary>
    public const int ReaderModelMaxLength = 100;

    public static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= NameMaxLength;

    public static bool IsValidReaderModel(string? value) =>
        value is null || value.Trim().Length <= ReaderModelMaxLength;
}
