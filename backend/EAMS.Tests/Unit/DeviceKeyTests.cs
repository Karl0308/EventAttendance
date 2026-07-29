using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The device key format (Phase 4a design, D-24) — the half of authentication that is pure arithmetic
/// and needs no database.
///
/// <para>
/// The token format is <b>frozen contract</b>: it is published to the mobile developer in
/// <c>docs/api/attendance-contract-handoff.md</c> and an app is being written against it right now.
/// These tests are what make a change to it a build failure rather than a field report.
/// </para>
/// </summary>
public class DeviceKeyTests
{
    [Fact]
    public void An_issued_token_matches_the_published_format()
    {
        var issued = DeviceKey.Issue();

        Assert.StartsWith("eams_dk_", issued.Token, StringComparison.Ordinal);
        Assert.Equal($"eams_dk_{issued.KeyId}_{issued.Secret}", issued.Token);
        Assert.Equal(12, issued.KeyId.Length);
        Assert.Equal(64, issued.Secret.Length);
        Assert.Equal(85, issued.Token.Length);
    }

    /// <summary>
    /// 256 bits, and the entropy is the precondition that makes SHA-256 the correct choice rather
    /// than a shortcut — see <see cref="DeviceKey"/>. Asserted through the constant rather than the
    /// literal so the two cannot drift.
    /// </summary>
    [Fact]
    public void The_secret_carries_256_bits()
    {
        Assert.Equal(32, DeviceKey.SecretByteLength);
        Assert.Equal(DeviceKey.SecretByteLength * 2, DeviceKey.Issue().Secret.Length);
    }

    [Fact]
    public void Two_issued_keys_share_neither_half()
    {
        var first = DeviceKey.Issue();
        var second = DeviceKey.Issue();

        Assert.NotEqual(first.KeyId, second.KeyId);
        Assert.NotEqual(first.Secret, second.Secret);
    }

    /// <summary>
    /// <b>The stored value must not contain the secret.</b> This is the whole point of the column
    /// split, and it is the assertion that would fail first if someone "simplified" the hash away.
    /// </summary>
    [Fact]
    public void The_stored_hash_is_not_the_secret()
    {
        var issued = DeviceKey.Issue();

        Assert.NotEqual(issued.Secret, issued.Hash);
        Assert.DoesNotContain(issued.Secret, issued.Hash, StringComparison.Ordinal);
        Assert.Equal(DeviceKey.HashLength, issued.Hash.Length);
        Assert.Equal(DeviceKey.HashSecret(issued.Secret), issued.Hash);
    }

    [Fact]
    public void A_token_round_trips_through_parse()
    {
        var issued = DeviceKey.Issue();

        Assert.True(DeviceKey.TryParse(issued.Token, out var keyId, out var secret));
        Assert.Equal(issued.KeyId, keyId);
        Assert.Equal(issued.Secret, secret);
    }

    /// <summary>
    /// Every shape a caller might present that is not a key. All of these must be rejected before any
    /// database work happens — a junk header should cost a string comparison, not an index seek.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer eyJhbGciOi")]                                   // another scheme's token
    [InlineData("eams_dk_")]                                            // prefix alone
    [InlineData("eams_dk_0123456789ab")]                                // no secret
    [InlineData("eams_dk_0123456789ab_")]                              // empty secret
    [InlineData("eams_dk_0123456789ab_tooshort")]
    [InlineData("eams_dk_0123456789AB_0000000000000000000000000000000000000000000000000000000000000000")] // upper-case id
    [InlineData("eams_dk_0123456789ag_0000000000000000000000000000000000000000000000000000000000000000")] // non-hex id
    [InlineData("eams_dk_0123456789ab-0000000000000000000000000000000000000000000000000000000000000000")] // wrong separator
    [InlineData("EAMS_DK_0123456789ab_0000000000000000000000000000000000000000000000000000000000000000")] // upper-case prefix
    public void A_malformed_token_is_refused(string? token) =>
        Assert.False(DeviceKey.TryParse(token, out _, out _));

    [Fact]
    public void A_matching_secret_verifies_and_a_wrong_one_does_not()
    {
        var issued = DeviceKey.Issue();

        Assert.True(DeviceKey.SecretMatches(issued.Secret, issued.Hash));
        Assert.False(DeviceKey.SecretMatches(DeviceKey.Issue().Secret, issued.Hash));
    }

    /// <summary>
    /// A stored hash that is null, truncated, or not hex is <c>false</c> rather than an exception: it
    /// means the row was written by something other than <c>Issue</c>, and a device that cannot
    /// authenticate is the right outcome for that — not a 500 on the capture hot path.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deadbeef")]
    [InlineData("zzzz000000000000000000000000000000000000000000000000000000000000")]
    public void An_unusable_stored_hash_verifies_nothing(string? storedHash) =>
        Assert.False(DeviceKey.SecretMatches(DeviceKey.Issue().Secret, storedHash));

    /// <summary>
    /// Hashing is deterministic — otherwise a key would authenticate once and then stop, which is the
    /// kind of defect that reads as "flaky network" in the field for a week.
    /// </summary>
    [Fact]
    public void Hashing_the_same_secret_twice_gives_the_same_value()
    {
        var secret = DeviceKey.Issue().Secret;

        Assert.Equal(DeviceKey.HashSecret(secret), DeviceKey.HashSecret(secret));
    }

    /// <summary>
    /// The seeded development kiosk's well-known key is a <em>real</em> key, parseable by the same
    /// code path a production key takes.
    ///
    /// <para>
    /// <b>This test exists because the first draft of that constant was not.</b> Its key id read
    /// <c>0dev00000001</c> — the <c>v</c> is not hexadecimal, so <see cref="DeviceKey.TryParse"/>
    /// rejected it, the seed wrote a device with an empty key id, and the only symptom was a 401 on a
    /// development host with nothing in any log to explain it. A hard-coded constant that has to
    /// satisfy a format is exactly the thing to assert about, because nothing else will.
    /// </para>
    /// </summary>
    [Fact]
    public void The_seeded_development_key_is_a_well_formed_key()
    {
        Assert.True(
            DeviceKey.TryParse(SeedData.DevelopmentKioskApiKey, out var keyId, out var secret),
            "SeedData.DevelopmentKioskApiKey is not a well-formed device key. Both halves must be " +
            "lower-case hexadecimal.");

        Assert.Equal(DeviceKey.KeyIdLength, keyId.Length);
        Assert.Equal(DeviceKey.SecretLength, secret.Length);
    }
}
