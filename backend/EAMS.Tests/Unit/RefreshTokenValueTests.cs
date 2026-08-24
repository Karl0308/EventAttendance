using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <see cref="RefreshTokenValue"/> — the token format, its parser, and the property the whole
/// rotation design rests on: the secret is never stored.
/// </summary>
public class RefreshTokenValueTests
{
    [Fact]
    public void An_issued_token_round_trips_through_the_parser()
    {
        var issued = RefreshTokenValue.Issue();

        Assert.True(RefreshTokenValue.TryParse(issued.Token, out var tokenId, out var secret));
        Assert.Equal(issued.TokenId, tokenId);
        Assert.Equal(issued.Secret, secret);
    }

    /// <summary>
    /// <b>The stored value is a hash and the token is not derivable from it.</b> Asserted on the
    /// strings rather than on the types, because the failure this guards is somebody storing
    /// <c>issued.Secret</c> in the column that is named for a hash — which would compile, verify
    /// correctly, and make a leaked <c>RefreshTokens</c> table a list of live credentials.
    /// </summary>
    [Fact]
    public void The_hash_is_not_the_secret_and_does_not_contain_it()
    {
        var issued = RefreshTokenValue.Issue();

        Assert.NotEqual(issued.Secret, issued.Hash);
        Assert.DoesNotContain(issued.Secret, issued.Hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RefreshTokenValue.HashLength, issued.Hash.Length);
        Assert.Equal(RefreshTokenValue.HashSecret(issued.Secret), issued.Hash);
    }

    [Fact]
    public void Every_issued_token_is_distinct()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => RefreshTokenValue.Issue()).ToList();

        Assert.Equal(200, tokens.Select(t => t.Token).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(200, tokens.Select(t => t.TokenId).Distinct().Count());
        Assert.Equal(200, tokens.Select(t => t.Hash).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_issued_token_has_the_published_shape()
    {
        var issued = RefreshTokenValue.Issue();

        Assert.StartsWith(RefreshTokenValue.TokenPrefix, issued.Token, StringComparison.Ordinal);
        Assert.Equal(RefreshTokenValue.TokenLength, issued.Token.Length);
        Assert.Equal(issued.Token, issued.Token.ToLowerInvariant());
    }

    /// <summary>
    /// Malformed input costs one string comparison and never a query — which is why every shape check
    /// lives in the parser. Each case below is a real way a token arrives wrong: absent, truncated,
    /// re-cased, from the wrong credential family, or with the separator moved.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eams_rt_")]
    [InlineData("not-a-token-at-all")]
    public void Obviously_malformed_input_is_refused(string? token)
    {
        Assert.False(RefreshTokenValue.TryParse(token, out _, out _));
    }

    /// <summary>
    /// <b>A device key is not a refresh token, and the prefixes are what say so.</b> The two formats
    /// are deliberately similar — same split, same hash, same hex — so the prefix is the only thing
    /// stopping one from being presented where the other is expected. Worth a test of its own.
    /// </summary>
    [Fact]
    public void A_device_key_is_not_accepted_as_a_refresh_token()
    {
        var deviceKey = DeviceKey.Issue().Token;

        Assert.False(RefreshTokenValue.TryParse(deviceKey, out _, out _));
    }

    [Fact]
    public void A_refresh_token_is_not_accepted_as_a_device_key()
    {
        var refreshToken = RefreshTokenValue.Issue().Token;

        Assert.False(DeviceKey.TryParse(refreshToken, out _, out _));
    }

    /// <summary>
    /// <b>Case is significant, and upper-case hex is malformed rather than normalized.</b> The same
    /// decision <see cref="DeviceKey"/> records: the token is issued by this system and stored
    /// verbatim by the client, so accepting a second spelling widens what counts as a valid credential
    /// to buy nothing — and puts a case fold in front of the one comparison that must be exact.
    /// </summary>
    [Fact]
    public void An_upper_cased_token_is_malformed()
    {
        var issued = RefreshTokenValue.Issue();

        Assert.False(RefreshTokenValue.TryParse(issued.Token.ToUpperInvariant(), out _, out _));
    }

    [Fact]
    public void A_token_of_the_wrong_length_is_refused()
    {
        var issued = RefreshTokenValue.Issue();

        Assert.False(RefreshTokenValue.TryParse(issued.Token[..^1], out _, out _));
        Assert.False(RefreshTokenValue.TryParse(issued.Token + "0", out _, out _));
    }

    /// <summary>
    /// A token whose id half is the right length but is not a GUID. Reachable only if the format
    /// changes, and pinned because the parser would otherwise return a default <see cref="Guid"/> and
    /// the store would look up a row that cannot exist — a silent unknown-token answer for a
    /// structural problem.
    /// </summary>
    [Fact]
    public void A_token_whose_id_half_is_not_hexadecimal_is_refused()
    {
        var issued = RefreshTokenValue.Issue();
        var corrupted = RefreshTokenValue.TokenPrefix
            + new string('z', RefreshTokenValue.TokenIdLength)
            + issued.Token[(RefreshTokenValue.TokenPrefix.Length + RefreshTokenValue.TokenIdLength)..];

        Assert.Equal(issued.Token.Length, corrupted.Length);
        Assert.False(RefreshTokenValue.TryParse(corrupted, out _, out _));
    }

    // ------------------------------------------------------------------------------ SecretMatches

    [Fact]
    public void The_right_secret_matches_its_stored_hash()
    {
        var issued = RefreshTokenValue.Issue();

        Assert.True(RefreshTokenValue.SecretMatches(issued.Secret, issued.Hash));
    }

    [Fact]
    public void Another_tokens_secret_does_not_match()
    {
        var mine = RefreshTokenValue.Issue();
        var theirs = RefreshTokenValue.Issue();

        Assert.False(RefreshTokenValue.SecretMatches(theirs.Secret, mine.Hash));
    }

    /// <summary>
    /// A stored hash of the wrong length means the row was written by something other than
    /// <see cref="RefreshTokenValue.Issue"/>. It is <c>false</c> rather than an exception: a token
    /// that cannot be redeemed is the right outcome, and throwing would turn one corrupt row into a
    /// 500 on the refresh endpoint.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deadbeef")]
    public void A_stored_hash_that_is_not_a_sha256_never_matches(string? storedHash)
    {
        var issued = RefreshTokenValue.Issue();

        Assert.False(RefreshTokenValue.SecretMatches(issued.Secret, storedHash));
    }

    /// <summary>
    /// <b>The plaintext secret in the hash column must not authenticate.</b> The failure this catches
    /// is the one that makes a leaked table catastrophic rather than merely embarrassing, and it would
    /// otherwise be invisible: a store that wrote the secret where the hash belongs still lets every
    /// legitimate client log in.
    /// </summary>
    [Fact]
    public void A_secret_stored_where_the_hash_belongs_does_not_authenticate()
    {
        var issued = RefreshTokenValue.Issue();

        Assert.False(RefreshTokenValue.SecretMatches(issued.Secret, issued.Secret));
    }

    /// <summary>
    /// The policy numbers, pinned. They are the answer to "how long can a stolen token stay useful?",
    /// and a silent change to either is a security change that should require editing a test.
    /// </summary>
    [Fact]
    public void The_token_and_family_lifetimes_are_the_published_numbers()
    {
        Assert.Equal(TimeSpan.FromDays(14), RefreshTokenPolicy.TokenLifetime);
        Assert.Equal(TimeSpan.FromDays(30), RefreshTokenPolicy.FamilyLifetime);

        Assert.True(
            RefreshTokenPolicy.TokenLifetime < RefreshTokenPolicy.FamilyLifetime,
            "A family ceiling that is not longer than one token's life would make every session end " +
            "at the ceiling on its first refresh, which is not what either number is for.");
    }
}
