using System.Text;

namespace EAMS.Api.Authentication;

/// <summary>
/// The §11 access-token settings, resolved once at startup — and the reason the host <b>refuses to
/// start</b> without a usable signing key.
///
/// <para>
/// <b>Why a missing key is a refusal rather than a generated default.</b> The convenient behaviour is
/// to mint a random key when none is configured, and it is wrong in two directions at once. In
/// development it rotates on every restart, so every issued token becomes invalid the moment the
/// developer saves a file — the symptom is "I keep getting logged out", which reads as a bug in the
/// login code and not as a configuration default. In production, behind more than one instance or an
/// IIS app pool that recycles, tokens minted by one process are rejected by the next, intermittently,
/// under load, and only for some users. Both failures are silent, and both point away from their cause.
/// A host that will not start names the setting and is fixed in one line.
/// </para>
///
/// <para>
/// <b>Where the key comes from.</b> In development, user secrets — <c>dotnet user-secrets set
/// "Jwt:SigningKey" "&lt;64+ random characters&gt;" --project backend/EAMS.Api</c>, which keeps it off
/// disk inside the repository and out of every diff. On IIS, an environment variable on the
/// application pool identity (<c>Jwt__SigningKey</c>), which is what
/// <c>docs/DEPLOY-IIS.md</c> already does for <c>ConnectionStrings__EamsDb</c>. It is deliberately
/// absent from <c>appsettings.json</c> for the reason that file records about connection strings: the
/// base layer is the <em>production</em> default, and a value there disarms the guard permanently.
/// </para>
///
/// <para>
/// <b>The placeholder check is not paranoia.</b> The realistic failure is not an empty setting — that
/// one is caught by its own absence — it is a key copied out of a README, a sample file or a Stack
/// Overflow answer and shipped. Such a key is public, so tokens signed with it can be forged by
/// anyone, and nothing about the running system looks wrong. Refusing the known shapes turns the most
/// likely real mistake into a startup message.
/// </para>
/// </summary>
internal sealed class JwtOptions
{
    /// <summary>The configuration section. Environment form: <c>Jwt__SigningKey</c>.</summary>
    public const string SectionName = "Jwt";

    /// <summary>The full key path, named so the refusal message and the tests quote the same string.</summary>
    public const string SigningKeyPath = SectionName + ":SigningKey";

    /// <summary>
    /// Minimum bytes of signing key. <b>256 bits, because HMAC-SHA256 is defined over a key at least
    /// as long as its output</b> — RFC 7518 §3.2 says so, and .NET's own token handler throws at
    /// runtime on a shorter one. Enforcing it here turns "the first login attempt after a deploy
    /// throws" into "the deploy did not start", which is the same defect discovered an hour earlier
    /// and by the person who caused it.
    /// </summary>
    public const int MinimumSigningKeyBytes = 32;

    /// <summary>
    /// Keys this host will not sign with, matched case-insensitively after trimming.
    ///
    /// <para>
    /// <b>The list is short on purpose.</b> It catches copy-paste, not weakness — judging entropy is
    /// not something a substring check can do, and a longer list would give false confidence that it
    /// had. Anything that survives this is assumed to be a real secret, which is why
    /// <see cref="MinimumSigningKeyBytes"/> is the other half of the answer.
    /// </para>
    /// </summary>
    private static readonly string[] KnownPlaceholders =
    [
        "changeme",
        "change-me",
        "your-secret-key",
        "your-256-bit-secret",
        "supersecretkey",
        "secret",
        "replace-this",
        "todo",
        "example",
    ];

    private JwtOptions(string signingKey, string issuer, string audience, TimeSpan accessTokenLifetime)
    {
        SigningKey = signingKey;
        Issuer = issuer;
        Audience = audience;
        AccessTokenLifetime = accessTokenLifetime;
    }

    public string SigningKey { get; }

    public string Issuer { get; }

    public string Audience { get; }

    /// <summary>
    /// How long an access token is good for.
    ///
    /// <para>
    /// Short by design: an access token cannot be revoked without a deny list on the hot path of every
    /// request, so its lifetime <em>is</em> its revocation window. Fifteen minutes is what makes
    /// "deactivate this user" mean something without adding a lookup to every call. The refresh token
    /// carries the long-lived half, in a table, where it can be revoked properly — see
    /// <c>RefreshToken</c>.
    /// </para>
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; }

    /// <summary>
    /// Reads and validates the settings, or throws with a message naming what to configure.
    ///
    /// <para>
    /// Called from <c>Program.cs</c> while the host is still being described — before
    /// <c>builder.Build()</c> — so the failure happens at startup rather than at the first login, and
    /// it is deliberately placed <em>after</em> <c>AddEamsInfrastructure</c> so that a host missing
    /// both a connection string and a key still reports the connection string first. That ordering is
    /// what keeps <c>HostPipelineTests</c>' connection-string refusal failing for its own reason.
    /// </para>
    /// </summary>
    public static JwtOptions Resolve(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var key = section["SigningKey"]?.Trim() ?? "";

        if (key.Length == 0)
        {
            throw new InvalidOperationException(
                $"'{SigningKeyPath}' is not configured, and this host will not start without it. " +
                "It is deliberately not defaulted and never generated: a generated key changes on " +
                "every restart, which invalidates every live session silently and looks like a bug " +
                "in login rather than a missing setting. In development set it with " +
                $"`dotnet user-secrets set \"{SigningKeyPath}\" \"<64+ random characters>\"`; on IIS " +
                "set the environment variable Jwt__SigningKey on the application pool.");
        }

        // Bytes, not characters. A key of 32 non-ASCII characters is longer than 32 bytes and a key
        // of 32 ASCII characters is exactly 32 — the signing algorithm counts bytes, so this must too.
        var keyBytes = Encoding.UTF8.GetByteCount(key);
        if (keyBytes < MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"'{SigningKeyPath}' is {keyBytes} bytes; HMAC-SHA256 requires at least " +
                $"{MinimumSigningKeyBytes} (RFC 7518 §3.2), and a shorter key throws at the first " +
                "token issued rather than here. Use at least 64 random characters.");
        }

        if (IsPlaceholder(key))
        {
            throw new InvalidOperationException(
                $"'{SigningKeyPath}' looks like a placeholder that was copied from a sample rather " +
                "than a secret. A key that appears in documentation is public, so anyone can forge a " +
                "token this host will accept — and nothing about the running system would look " +
                "wrong. Generate a real one, e.g. " +
                "`[Convert]::ToBase64String((1..48 | %{Get-Random -Max 256}))`.");
        }

        var lifetime = TimeSpan.FromMinutes(15);

        return new JwtOptions(
            key,
            section["Issuer"]?.Trim() is { Length: > 0 } issuer ? issuer : "eams",
            section["Audience"]?.Trim() is { Length: > 0 } audience ? audience : "eams",
            lifetime);
    }

    /// <summary>
    /// Whether the key is one of the known copy-paste values. Compared as a whole and as a prefix:
    /// <c>changeme123</c> is the same mistake as <c>changeme</c>, padded to get past a length check.
    /// </summary>
    internal static bool IsPlaceholder(string key) =>
        KnownPlaceholders.Any(p =>
            key.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            || key.Contains(p, StringComparison.OrdinalIgnoreCase));
}
