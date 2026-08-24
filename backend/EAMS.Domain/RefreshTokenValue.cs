using System.Security.Cryptography;
using System.Text;

namespace EAMS.Domain;

/// <summary>
/// The refresh-token format for §11 user login:
///
/// <code>
/// eams_rt_&lt;tokenId&gt;_&lt;secret&gt;
/// </code>
///
/// <para>
/// <b>It is deliberately the same shape as <see cref="DeviceKey"/>, and for the same reason.</b> The
/// id half is public, is the row's primary key, and is what the presented token is looked up by; the
/// secret half is 256 bits of CSPRNG output and is <em>never</em> stored — only its SHA-256 is.
/// Verification is one primary-key seek followed by one fixed-time hash comparison, rather than a scan
/// that hashes every live token's candidate. Without the id half, "which token is this?" has no answer
/// that does not involve trying every row in the table, which on the busiest table an auth system has
/// is not an implementation detail.
/// </para>
///
/// <para>
/// <b>SHA-256 rather than a slow KDF is correct here for exactly one reason, and it is the reason
/// <see cref="DeviceKey"/> records (D-24): the secret is server-generated with 256 bits of
/// entropy.</b> It is not a password. There is no dictionary, no rainbow table and no meaningful
/// offline search — brute-forcing 2^256 is arithmetic, not a threat model. A slow KDF exists to make
/// <em>low-entropy human input</em> expensive to guess, and buying that here would put tens of
/// milliseconds on the hot path of every token refresh for nothing. Passwords are the other half of
/// this phase and they go through <c>IPasswordHasher</c> (Identity V3 / PBKDF2), because they
/// <em>are</em> low-entropy human input. Two credential kinds, two hashes, and the difference between
/// them is the entropy precondition rather than a preference.
/// </para>
///
/// <para>
/// <b>That reasoning becomes wrong the moment anyone lets a caller supply a refresh token's
/// secret.</b> <see cref="Issue"/> is the only way to mint one and it takes no secret, so such a
/// change would be visible rather than incidental — the same guard <see cref="DeviceKey.Issue"/>
/// has.
/// </para>
///
/// <para>
/// The id half is the <c>RefreshTokens.Id</c> GUID in <c>"N"</c> form: 32 lower-case hex characters,
/// no separators. Using the primary key rather than a second indexed column means the lookup is the
/// clustered seek the table already has, and it means <c>ReplacedByTokenId</c> points at something a
/// reader can match against a token they are holding.
/// </para>
///
/// <para>
/// Lower-case hexadecimal throughout, case-significant, for the reasons <see cref="DeviceKey"/> sets
/// out: hex contains no <c>_</c> to collide with the separator, survives every transport a token
/// travels through, and a case fold in front of the one comparison that has to be exact buys nothing.
/// </para>
/// </summary>
public static class RefreshTokenValue
{
    /// <summary>The token prefix. Frozen contract once §11's login endpoint publishes it.</summary>
    public const string TokenPrefix = "eams_rt_";

    /// <summary>Separator between prefix, token id and secret.</summary>
    private const char Separator = '_';

    /// <summary>Characters in the id half: a GUID in <c>"N"</c> form.</summary>
    public const int TokenIdLength = 32;

    /// <summary>Bytes of secret. 256 bits — see the type remarks for why that number is load-bearing.</summary>
    public const int SecretByteLength = 32;

    /// <summary>Characters in the secret half: <see cref="SecretByteLength"/> rendered as hex.</summary>
    public const int SecretLength = SecretByteLength * 2;

    /// <summary>Characters in a stored hash: SHA-256 rendered as lower-case hex.</summary>
    public const int HashLength = 64;

    /// <summary>Total characters in a well-formed token.</summary>
    public const int TokenLength =
        8 /* eams_rt_ */ + TokenIdLength + 1 /* _ */ + SecretLength;

    /// <summary>
    /// A freshly minted refresh token. <see cref="Secret"/> and <see cref="Token"/> exist exactly
    /// once — the row keeps only <see cref="Hash"/>.
    /// </summary>
    /// <param name="TokenId">The row's primary key, and the public half of the token.</param>
    /// <param name="Secret">The private half. Never stored, never logged, never retrievable.</param>
    /// <param name="Hash">SHA-256 of <paramref name="Secret"/>, lower-case hex. This is what is stored.</param>
    /// <param name="Token">The complete <c>eams_rt_…</c> string handed to the client.</param>
    public readonly record struct IssuedToken(Guid TokenId, string Secret, string Hash, string Token);

    /// <summary>
    /// Mints a token for a row that does not exist yet — the caller writes the row using
    /// <see cref="IssuedToken.TokenId"/> as its <c>Id</c>.
    ///
    /// <para>
    /// It takes no parameters deliberately: there is no way to supply a secret, so the entropy
    /// precondition in the type remarks cannot be weakened by a caller.
    /// </para>
    /// </summary>
    public static IssuedToken Issue()
    {
        var tokenId = Guid.NewGuid();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretByteLength))
            .ToLowerInvariant();

        return new IssuedToken(
            tokenId,
            secret,
            HashSecret(secret),
            TokenPrefix + tokenId.ToString("N") + Separator + secret);
    }

    /// <summary>
    /// Splits a presented token into its two halves, or reports it as malformed.
    ///
    /// <para>
    /// Every shape check happens here, before any database work: a body that is not a well-formed
    /// token must cost one string comparison rather than a key seek.
    /// </para>
    /// </summary>
    public static bool TryParse(string? token, out Guid tokenId, out string secret)
    {
        tokenId = Guid.Empty;
        secret = "";

        if (string.IsNullOrEmpty(token) || token.Length != TokenLength) return false;
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return false;

        var body = token.AsSpan(TokenPrefix.Length);
        if (body[TokenIdLength] != Separator) return false;

        var idPart = body[..TokenIdLength];
        var secretPart = body[(TokenIdLength + 1)..];

        // Checked before Guid.TryParseExact rather than relying on it: "N" accepts upper-case hex,
        // and this token has exactly one accepted spelling.
        if (!IsLowerHex(idPart) || !IsLowerHex(secretPart)) return false;
        if (!Guid.TryParseExact(idPart, "N", out tokenId)) return false;

        secret = secretPart.ToString();
        return true;
    }

    /// <summary>
    /// SHA-256 of the secret, lower-case hex — exactly what <c>RefreshTokens.TokenHash</c> holds.
    /// </summary>
    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>
    /// Whether a presented secret matches a stored hash, compared in <b>fixed time</b>.
    ///
    /// <para>
    /// Over the raw 32 hash bytes rather than the 64 hex characters, and through
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> rather than <c>string ==</c>, for the
    /// reason <see cref="DeviceKey.SecretMatches"/> records: ordinary string equality returns at the
    /// first differing character, which leaks how much of a guess was right.
    /// </para>
    ///
    /// <para>
    /// A stored hash of the wrong length is <c>false</c> rather than an exception: it means the row
    /// was written by something other than <see cref="Issue"/>, and a token that cannot be redeemed is
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
