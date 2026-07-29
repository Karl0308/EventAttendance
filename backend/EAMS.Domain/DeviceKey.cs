using System.Security.Cryptography;
using System.Text;

namespace EAMS.Domain;

/// <summary>
/// The device API key format published to the mobile developer and frozen in
/// <c>docs/api/attendance-contract-handoff.md</c> (ADR-001 D-6 / Phase 4a D-23, D-24):
///
/// <code>
/// Authorization: DeviceKey eams_dk_&lt;keyId&gt;_&lt;secret&gt;
/// </code>
///
/// <para>
/// <b>The token is split on purpose, and the split is the whole design.</b> <c>keyId</c> is public,
/// twelve characters, and stored in an indexed column; <c>secret</c> is 256 bits of CSPRNG output and
/// is <em>never</em> stored — only the SHA-256 of it is. Verification is therefore one index seek on
/// <c>Devices.ApiKeyId</c> followed by one fixed-time hash comparison, rather than a scan that hashes
/// every row's candidate. Without the id half, "which device is this?" has no answer that does not
/// involve trying every device.
/// </para>
///
/// <para>
/// <b>SHA-256 rather than bcrypt/Argon2 is deliberate, and it is only correct because of one
/// precondition: the secret is server-generated with 256 bits of entropy.</b> It is not a password.
/// There is no dictionary, no rainbow table, and no meaningful offline search — brute-forcing 2^256 is
/// not a threat model, it is arithmetic. A slow KDF exists to make <em>low-entropy human input</em>
/// expensive to guess, and buying that here would add tens of milliseconds to the hot path of every
/// tap for nothing.
/// </para>
///
/// <para>
/// <b>That reasoning becomes wrong the moment anyone lets an operator choose or supply a key.</b> An
/// operator-chosen key is a password, the precondition evaporates, and this class must switch to a
/// real KDF in the same change. <see cref="Issue"/> is the only way to mint a key and takes no input,
/// so that change is visible rather than incidental.
/// </para>
///
/// <para>
/// Hexadecimal rather than base64url or base32: it is unambiguous, contains no <c>_</c> to collide
/// with the separator, and survives every transport a header goes through. The cost is length (85
/// characters), which nothing here cares about.
/// </para>
///
/// <para>
/// <b>Case is significant, and lower-case is the only accepted form.</b> An earlier version of this
/// note offered "case-normalizable" as part of the rationale, which was wrong twice over: nothing here
/// normalizes case, and adding it would be the wrong call anyway. The token is frozen contract handed
/// to one client that stores what we gave it verbatim, so accepting a second spelling widens what
/// counts as a valid credential to buy nothing — and it would put a case fold in front of the only
/// comparison in the system that has to be exact. An upper-cased token is malformed and 401s, which is
/// the honest answer.
/// </para>
/// </summary>
public static class DeviceKey
{
    /// <summary>The <c>Authorization</c> scheme name. Frozen contract — do not rename.</summary>
    public const string AuthenticationScheme = "DeviceKey";

    /// <summary>The token prefix. Frozen contract — do not rename.</summary>
    public const string TokenPrefix = "eams_dk_";

    /// <summary>Separator between prefix, key id and secret.</summary>
    private const char Separator = '_';

    /// <summary>
    /// Characters in the public key id: 48 bits of randomness rendered as hex. Enough that a
    /// collision is not a practical concern on a table that will hold hundreds of rows, and short
    /// enough to read off a screen when someone is comparing a key id in a log to one in the
    /// database.
    /// </summary>
    public const int KeyIdLength = 12;

    /// <summary>Bytes of secret. 256 bits — see the type remarks for why that number is load-bearing.</summary>
    public const int SecretByteLength = 32;

    /// <summary>Characters in the secret half: <see cref="SecretByteLength"/> rendered as hex.</summary>
    public const int SecretLength = SecretByteLength * 2;

    /// <summary>Characters in a stored hash: SHA-256 rendered as lower-case hex.</summary>
    public const int HashLength = 64;

    /// <summary>
    /// Total characters in a well-formed token. Used to size <c>Devices.ApiKeyId</c>'s neighbours and
    /// to reject an absurd header before any hashing happens.
    /// </summary>
    public const int TokenLength = 8 /* eams_dk_ */ + KeyIdLength + 1 /* _ */ + SecretLength;

    /// <summary>A freshly minted key. <see cref="Secret"/> and <see cref="Token"/> exist exactly once.</summary>
    /// <param name="KeyId">The public half. Stored, indexed, safe to log.</param>
    /// <param name="Secret">The private half. Never stored, never logged, never retrievable.</param>
    /// <param name="Hash">SHA-256 of <paramref name="Secret"/>, lower-case hex. This is what is stored.</param>
    /// <param name="Token">
    /// The complete <c>eams_dk_…</c> string handed to the operator. Returned by
    /// <c>POST /devices</c> and <c>POST /devices/{id}/regenerate-key</c> and by nothing else, ever.
    /// </param>
    public readonly record struct IssuedKey(string KeyId, string Secret, string Hash, string Token);

    /// <summary>
    /// Mints a key. Takes no parameters deliberately: there is no way to supply a secret, so the
    /// entropy precondition in the type remarks cannot be weakened by a caller.
    /// </summary>
    public static IssuedKey Issue()
    {
        // KeyIdLength is even, so this is exact.
        var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(KeyIdLength / 2)).ToLowerInvariant();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretByteLength)).ToLowerInvariant();

        return new IssuedKey(keyId, secret, HashSecret(secret), TokenPrefix + keyId + Separator + secret);
    }

    /// <summary>
    /// Splits a presented token into its two halves, or reports it as malformed.
    ///
    /// <para>
    /// Every shape check happens here, before any database work: a header that is not a well-formed
    /// token must cost one string comparison rather than an index seek. Malformed is
    /// indistinguishable from unknown to the caller — both are 401 — but they are different things and
    /// only one of them is worth a query.
    /// </para>
    /// </summary>
    public static bool TryParse(string? token, out string keyId, out string secret)
    {
        keyId = "";
        secret = "";

        if (string.IsNullOrEmpty(token) || token.Length != TokenLength) return false;
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return false;

        var body = token.AsSpan(TokenPrefix.Length);
        if (body[KeyIdLength] != Separator) return false;

        var idPart = body[..KeyIdLength];
        var secretPart = body[(KeyIdLength + 1)..];

        if (!IsLowerHex(idPart) || !IsLowerHex(secretPart)) return false;

        keyId = idPart.ToString();
        secret = secretPart.ToString();
        return true;
    }

    /// <summary>SHA-256 of the secret, lower-case hex — exactly what <c>Devices.ApiKeyHash</c> holds.</summary>
    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>
    /// Whether a presented secret matches a stored hash, compared in <b>fixed time</b>.
    ///
    /// <para>
    /// The comparison is over the raw 32 hash bytes rather than the 64 hex characters, and it uses
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> rather than <c>string ==</c>. Ordinary
    /// string equality returns at the first differing character, which leaks how much of a guess was
    /// right — the classic way a "compare the hashes" check becomes an oracle. The leak is small here
    /// (the attacker would be guessing a hash, not the secret) but the correct comparison costs
    /// nothing and removes the question.
    /// </para>
    ///
    /// <para>
    /// A stored hash of the wrong length is <c>false</c> rather than an exception: it means the row was
    /// written by something other than <see cref="Issue"/>, and a device that cannot authenticate is
    /// the right outcome for that.
    /// </para>
    /// </summary>
    public static bool SecretMatches(string secret, string? storedHash)
    {
        if (storedHash is null || storedHash.Length != HashLength) return false;

        Span<byte> presented = stackalloc byte[SecretByteLength];
        SHA256.HashData(Encoding.UTF8.GetBytes(secret), presented);

        Span<byte> stored = stackalloc byte[SecretByteLength];
        if (!TryFromLowerHex(storedHash, stored)) return false;

        return CryptographicOperations.FixedTimeEquals(presented, stored);
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;

        return true;
    }

    private static bool TryFromLowerHex(string hex, Span<byte> destination)
    {
        if (!IsLowerHex(hex)) return false;

        for (var i = 0; i < destination.Length; i++)
            destination[i] = (byte)((HexValue(hex[i * 2]) << 4) | HexValue(hex[i * 2 + 1]));

        return true;

        static int HexValue(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
    }
}
