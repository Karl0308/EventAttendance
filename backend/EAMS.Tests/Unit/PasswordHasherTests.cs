using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <see cref="IdentityPasswordHasher"/> — the one credential in this system hashed slowly, and the
/// three things a caller may rely on: a round trip, a refusal, and the upgrade signal.
/// </summary>
public class PasswordHasherTests
{
    private readonly IPasswordHasher _hasher = new IdentityPasswordHasher();

    private const string Password = "correct horse battery staple";

    [Fact]
    public void A_hashed_password_verifies()
    {
        var hash = _hasher.Hash(Password);

        Assert.Equal(PasswordVerification.Success, _hasher.Verify(hash, Password));
    }

    /// <summary>
    /// <b>The hash is not the password, and not a deterministic function of it either.</b> Two hashes
    /// of one password differ because each carries its own random salt — which is what makes a
    /// precomputed table useless against a leaked <c>Users</c> column. A hasher that returned the same
    /// string twice would pass the round-trip test above and be worthless.
    /// </summary>
    [Fact]
    public void Two_hashes_of_the_same_password_differ()
    {
        var first = _hasher.Hash(Password);
        var second = _hasher.Hash(Password);

        Assert.NotEqual(first, second);
        Assert.DoesNotContain(Password, first, StringComparison.OrdinalIgnoreCase);

        // Both still verify — the salt is inside the stored value, not beside it.
        Assert.Equal(PasswordVerification.Success, _hasher.Verify(first, Password));
        Assert.Equal(PasswordVerification.Success, _hasher.Verify(second, Password));
    }

    [Theory]
    [InlineData("wrong password entirely")]
    [InlineData("correct horse battery stapl")]   // one character short
    [InlineData("Correct horse battery staple")]  // case differs
    [InlineData("")]
    public void A_wrong_password_is_refused(string presented)
    {
        var hash = _hasher.Hash(Password);

        Assert.Equal(PasswordVerification.Failed, _hasher.Verify(hash, presented));
    }

    /// <summary>
    /// <b>A stored value that is not a hash must refuse, not throw.</b> Identity's own implementation
    /// throws on a malformed stored hash, and an exception escaping the verifier would turn a corrupt
    /// row into a 500 on the login endpoint — which tells an attacker that the address exists, on
    /// exactly the response that was written not to.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-base64-at-all!!")]
    [InlineData("AQAAAAIAAYag")] // valid base64, far too short to be a V3 payload
    public void A_stored_value_that_is_not_a_hash_refuses_rather_than_throwing(string storedHash)
    {
        var result = Record.Exception(() => _hasher.Verify(storedHash, Password));

        Assert.Null(result);
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify(storedHash, Password));
    }

    /// <summary>
    /// <b>The whole reason <see cref="PasswordVerification"/> is an enum.</b> A hash written at a
    /// lower iteration count still verifies — correctly, it is the same password — and must be
    /// reported as needing a rewrite. Collapsed into a boolean this is unobservable, which is how an
    /// application ends up having "upgraded" its hashing parameters for new accounts only while every
    /// long-standing one keeps the old ones indefinitely.
    ///
    /// <para>
    /// The stale hash is produced by a second <c>PasswordHasher</c> configured at a deliberately lower
    /// count, which is exactly what a hash written before an upgrade looks like — rather than by a
    /// hand-assembled byte array that might be malformed in some other way and fail for the wrong
    /// reason.
    /// </para>
    /// </summary>
    [Fact]
    public void A_hash_at_older_parameters_verifies_and_asks_to_be_rewritten()
    {
        var stale = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = 1_000,
        })).HashPassword(new User(), Password);

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, _hasher.Verify(stale, Password));

        // And the wrong password against a stale hash is still just wrong — the upgrade signal must
        // not leak past the credential check.
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify(stale, "something else"));
    }

    /// <summary>
    /// The V2 compatibility case, which is the other shape a legacy hash arrives in. It is not a
    /// format this system has ever written, and that is the point: if a database is ever migrated in
    /// from one that did, the hash still verifies and still asks to be rewritten rather than locking
    /// the account out.
    /// </summary>
    [Fact]
    public void An_identity_v2_hash_verifies_and_asks_to_be_rewritten()
    {
        var v2 = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
        })).HashPassword(new User(), Password);

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, _hasher.Verify(v2, Password));
    }
}
