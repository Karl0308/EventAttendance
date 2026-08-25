using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EAMS.Api.Authentication;
using EAMS.Api.Authorization;
using Microsoft.Extensions.Configuration;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// A JWT forger. It assembles the three segments by hand rather than through
/// <c>JsonWebTokenHandler</c>, and that is the entire point of it existing.
///
/// <para>
/// <b>Why not the real handler.</b> Every attack in <c>AuthTokenForgeryTests</c> is a token the
/// issuing library will not produce: a header that says <c>alg: none</c>, a token with no
/// <c>exp</c> at all, a payload edited after signing. <c>SecurityTokenDescriptor</c> defends against
/// all three — it defaults an expiry when you omit one, and it will not emit an unsigned token — so a
/// forgery built through it would silently become a well-formed token and the test would prove
/// nothing. The signature is applied here with a raw <c>HMACSHA*</c> over the signing input, which is
/// exactly what a forger has.
/// </para>
///
/// <para>
/// <b>The issuer, audience and key are read from the same code the host reads them from.</b>
/// <see cref="HostOptions"/> resolves <see cref="JwtOptions"/> from the one environment variable
/// <see cref="EamsApiFactory"/> sets, so a forgery cannot drift from what the booted host expects —
/// if it did, every rejection in the file would pass for the wrong reason. The positive control
/// (a correctly-signed forgery that MUST be accepted) is what makes that guarantee visible.
/// </para>
/// </summary>
internal static class ForgedJwt
{
    public const string None = "none";
    public const string Hs256 = "HS256";
    public const string Hs384 = "HS384";
    public const string Hs512 = "HS512";

    /// <summary>
    /// What a host booted by <see cref="EamsApiFactory"/> resolves — the same key, and the same
    /// defaulted issuer and audience, because it goes through <see cref="JwtOptions.Resolve"/>.
    /// </summary>
    public static JwtOptions HostOptions { get; } = JwtOptions.Resolve(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [JwtOptions.SigningKeyPath] = TestHostConfiguration.SigningKey,
            })
            .Build());

    public static byte[] RealKey { get; } = Encoding.UTF8.GetBytes(TestHostConfiguration.SigningKey);

    /// <summary>A different key of the same length. Not a truncation — see <see cref="TruncatedKey"/>.</summary>
    public static byte[] WrongKey { get; } = Encoding.UTF8.GetBytes(
        "0000111122223333444455556666777788889999aaaabbbbccccddddeeeeffff01234567");

    /// <summary>
    /// The first 40 bytes of the real key. Still long enough for HMAC-SHA256 (RFC 7518 §3.2 asks for
    /// 32), so it signs successfully — which is what makes it a real attack rather than a crash: a
    /// key that leaked through a truncating log or a fixed-width column is a prefix of the secret.
    /// </summary>
    public static byte[] TruncatedKey { get; } = RealKey[..40];

    /// <summary>The claim set a genuine access token carries, ready to be spoiled one field at a time.</summary>
    public static JsonObject ClaimsFor(Guid userId, Guid schoolId, params string[] permissions)
    {
        var now = DateTimeOffset.UtcNow;

        return new JsonObject
        {
            [EamsClaimTypes.Subject] = EamsClaimTypes.UserSubject(userId),
            [EamsClaimTypes.SchoolId] = schoolId.ToString(),
            [EamsClaimTypes.Permission] = new JsonArray([.. permissions.Select(p => JsonValue.Create(p))]),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iss"] = HostOptions.Issuer,
            ["aud"] = HostOptions.Audience,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(15).ToUnixTimeSeconds(),
        };
    }

    /// <summary>
    /// Assembles and signs. <paramref name="key"/> is ignored when <paramref name="alg"/> is
    /// <see cref="None"/> — the signature segment is then present and empty, which is the shape the
    /// <c>alg: none</c> attack actually sends.
    /// </summary>
    public static string Mint(JsonObject claims, string alg = Hs256, byte[]? key = null)
    {
        var header = new JsonObject { ["alg"] = alg, ["typ"] = "JWT" };

        var signingInput =
            Encode(Encoding.UTF8.GetBytes(header.ToJsonString())) +
            "." +
            Encode(Encoding.UTF8.GetBytes(claims.ToJsonString()));

        return alg == None
            ? signingInput + "."
            : signingInput + "." + Encode(Sign(alg, key ?? RealKey, Encoding.ASCII.GetBytes(signingInput)));
    }

    /// <summary>
    /// Edits the payload of a real token and puts the <em>original</em> signature back on it. The
    /// naive tamper — and the one that works against anything that decodes before it verifies.
    /// </summary>
    public static string TamperUnsigned(string jwt, Action<JsonObject> edit)
    {
        var parts = jwt.Split('.');
        var payload = PayloadOf(jwt);
        edit(payload);

        return parts[0] + "." + Encode(Encoding.UTF8.GetBytes(payload.ToJsonString())) + "." + parts[2];
    }

    /// <summary>Edits the payload of a real token and re-signs it with a key of the caller's choosing.</summary>
    public static string TamperAndResign(string jwt, Action<JsonObject> edit, byte[] key, string alg = Hs256)
    {
        var payload = PayloadOf(jwt);
        edit(payload);
        return Mint(payload, alg, key);
    }

    public static JsonObject PayloadOf(string jwt) =>
        JsonNode.Parse(Decode(jwt.Split('.')[1]))!.AsObject();

    public static JsonDocument HeaderOf(string jwt) =>
        JsonDocument.Parse(Decode(jwt.Split('.')[0]));

    // ------------------------------------------------------------------------------- the plumbing

    private static byte[] Sign(string alg, byte[] key, byte[] data) => alg switch
    {
        Hs256 => HMACSHA256.HashData(key, data),
        Hs384 => HMACSHA384.HashData(key, data),
        Hs512 => HMACSHA512.HashData(key, data),

        // Total over what this forger supports. A silent fallback to HS256 would make an
        // "algorithm confusion" test sign with the algorithm it claims to be confusing away from.
        _ => throw new ArgumentOutOfRangeException(nameof(alg), alg, "This forger cannot sign that."),
    };

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
