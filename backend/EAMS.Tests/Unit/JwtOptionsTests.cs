using System.Text;
using EAMS.Api.Authentication;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <see cref="JwtOptions.Resolve"/> — what the host will and will not accept as a signing key.
///
/// <para>
/// The refusal <em>at startup</em> is pinned separately, by <c>HostPipelineTests</c>, and the two are
/// not redundant: this file proves the validator is right, that one proves the composition root
/// actually calls it. A validator nothing invokes passes every test here and protects nothing.
/// </para>
/// </summary>
public class JwtOptionsTests
{
    /// <summary>72 characters of hex — comfortably over the 32-byte floor and on no placeholder list.</summary>
    private const string GoodKey =
        "9f2c7a41d80b6e35c14fa9037be25d8c6a1e4f70b93d2685cf07a4e1d9b3520867ac41fe";

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static IConfiguration WithKey(string? key) =>
        Configuration((JwtOptions.SigningKeyPath, key));

    [Fact]
    public void A_configured_key_resolves()
    {
        var options = JwtOptions.Resolve(WithKey(GoodKey));

        Assert.Equal(GoodKey, options.SigningKey);
        Assert.Equal(TimeSpan.FromMinutes(15), options.AccessTokenLifetime);
        Assert.False(string.IsNullOrWhiteSpace(options.Issuer));
        Assert.False(string.IsNullOrWhiteSpace(options.Audience));
    }

    /// <summary>
    /// The absent case, and the message is as much the subject as the throw. An operator who deploys
    /// without the setting has to be told which setting, or the refusal is only a different kind of
    /// dead end.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_key_is_refused_by_name(string? key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.Resolve(WithKey(key)));

        Assert.Contains(JwtOptions.SigningKeyPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("user-secrets", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Short keys are refused here rather than at the first token issued.</b> HMAC-SHA256 requires
    /// a key at least as long as its 256-bit output (RFC 7518 §3.2), and .NET's token handler throws
    /// when it is not — at run time, on the login endpoint, in production, on the first user to try.
    /// Enforcing it at startup is the same defect discovered an hour earlier and by the person who
    /// caused it.
    /// </summary>
    [Theory]
    [InlineData("a")]
    [InlineData("0123456789abcdef")]                  // 16 bytes
    [InlineData("0123456789abcdef0123456789abcde")]   // 31 bytes — one short
    public void A_key_shorter_than_the_algorithm_requires_is_refused(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.Resolve(WithKey(key)));

        Assert.Contains(JwtOptions.SigningKeyPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains(JwtOptions.MinimumSigningKeyBytes.ToString(), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exactly at the floor is accepted, and the boundary is asserted so a later refactor cannot make
    /// the check <c>&lt;=</c> and reject a legitimate 32-byte key.
    /// </summary>
    [Fact]
    public void A_key_of_exactly_the_minimum_length_is_accepted()
    {
        var key = new string('7', JwtOptions.MinimumSigningKeyBytes);

        Assert.Equal(JwtOptions.MinimumSigningKeyBytes, Encoding.UTF8.GetByteCount(key));
        Assert.Equal(key, JwtOptions.Resolve(WithKey(key)).SigningKey);
    }

    /// <summary>
    /// <b>Bytes, not characters.</b> A key of 32 multi-byte characters is well over the floor, and a
    /// check that counted <c>string.Length</c> would agree with this one — so the interesting case is
    /// the reverse, which cannot be constructed. What this pins is that a multi-byte key is measured
    /// the way the signing algorithm measures it, so the two never disagree about a borderline value.
    /// </summary>
    [Fact]
    public void The_length_check_counts_utf8_bytes()
    {
        // 16 characters, 48 bytes in UTF-8 — under the floor by character count, over it by byte count.
        var key = new string('中', 16);

        Assert.True(key.Length < JwtOptions.MinimumSigningKeyBytes);
        Assert.True(Encoding.UTF8.GetByteCount(key) >= JwtOptions.MinimumSigningKeyBytes);

        Assert.Equal(key, JwtOptions.Resolve(WithKey(key)).SigningKey);
    }

    /// <summary>
    /// <b>The realistic mistake.</b> Not an empty setting — that one announces itself — but a key
    /// lifted from a sample, a README or an answer online. Such a key is public, so anyone can forge a
    /// token this host would accept, and nothing about the running system looks wrong.
    ///
    /// <para>
    /// Every case here is long enough to clear the byte floor, deliberately: the point is that length
    /// alone is not the test, and a placeholder padded out to 64 characters is the same mistake with
    /// better disguise.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ChangeMe-ChangeMe-ChangeMe-ChangeMe-ChangeMe-ChangeMe-ChangeMe")]
    [InlineData("your-secret-key-your-secret-key-your-secret-key-your-secret-key")]
    [InlineData("this-is-a-longer-value-that-still-says-CHANGEME-somewhere-inside")]
    [InlineData("your-256-bit-secret-your-256-bit-secret-your-256-bit-secret-xxxx")]
    [InlineData("SuperSecretKey1234567890SuperSecretKey1234567890SuperSecretKey12")]
    [InlineData("example-signing-key-example-signing-key-example-signing-key-1234")]
    public void A_placeholder_key_is_refused_however_long_it_is(string key)
    {
        Assert.True(
            Encoding.UTF8.GetByteCount(key) >= JwtOptions.MinimumSigningKeyBytes,
            "This case is meant to clear the length floor so the placeholder check is what refuses it.");

        var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.Resolve(WithKey(key)));

        Assert.Contains("placeholder", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The negative half of the check above: a real random key must not be caught by it. Without this
    /// a placeholder list that matched everything would pass every test in this file.
    /// </summary>
    [Theory]
    [InlineData(GoodKey)]
    [InlineData("Zx8Kq2Lm9Rt4Nv6Bw1Yc3Ph5Jd7Fg0Sa2De4Wq6Er8Ty0Ui1Op3As5Df7Gh9Jk")]
    public void A_real_key_is_not_mistaken_for_a_placeholder(string key)
    {
        Assert.False(JwtOptions.IsPlaceholder(key));
        Assert.Equal(key, JwtOptions.Resolve(WithKey(key)).SigningKey);
    }

    /// <summary>
    /// Surrounding whitespace is trimmed, not refused. A key pasted into an IIS environment-variable
    /// box arrives with a trailing newline more often than not, and refusing it would be a support
    /// call; refusing the trimmed value's <em>length</em> would be a different bug.
    /// </summary>
    [Fact]
    public void Whitespace_around_a_key_is_trimmed()
    {
        Assert.Equal(GoodKey, JwtOptions.Resolve(WithKey($"  {GoodKey}\r\n")).SigningKey);
    }

    /// <summary>
    /// Whitespace that is the <em>whole</em> value is the absent case, not a 4-byte key. Asserted
    /// because trimming and length-checking in the wrong order would let "    " through the emptiness
    /// test and fail with a length message that sends the operator looking for the wrong thing.
    /// </summary>
    [Fact]
    public void A_key_that_is_only_whitespace_is_reported_as_missing_not_as_short()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.Resolve(WithKey("      ")));

        Assert.Contains("is not configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_issuer_and_audience_are_read_from_configuration_when_present()
    {
        var options = JwtOptions.Resolve(Configuration(
            (JwtOptions.SigningKeyPath, GoodKey),
            ($"{JwtOptions.SectionName}:Issuer", "eams-usa"),
            ($"{JwtOptions.SectionName}:Audience", "eams-admin")));

        Assert.Equal("eams-usa", options.Issuer);
        Assert.Equal("eams-admin", options.Audience);
    }
}
