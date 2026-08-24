namespace EAMS.Application.Abstractions;

/// <summary>
/// What checking a presented password against a stored hash concluded.
///
/// <para>
/// <b>Three members rather than a <c>bool</c>, and the third one is the entire reason this is an enum.</b>
/// A password hash embeds the algorithm and work factor it was produced with, so a hash written under
/// last year's parameters still verifies correctly today — and stays weak forever unless somebody
/// notices. <see cref="SuccessRehashNeeded"/> is that notice. Collapsed into <c>true</c> it is
/// unobservable, which is how an application ends up having "upgraded" its hashing parameters for new
/// users only while every long-standing account — the valuable ones — keeps the old ones indefinitely.
/// </para>
/// </summary>
public enum PasswordVerification
{
    /// <summary>The password does not match. Nothing else may be inferred from this.</summary>
    Failed,

    /// <summary>Matches, and the stored hash already uses the current parameters.</summary>
    Success,

    /// <summary>
    /// Matches, but the stored hash was produced with older parameters. <b>The caller must rehash the
    /// presented password and persist it</b> — this is the only moment the plaintext is available to
    /// do so, and skipping it means the account never gets the upgrade.
    /// </summary>
    SuccessRehashNeeded,
}

/// <summary>
/// Hashes and verifies <em>user passwords</em> — low-entropy human input, and therefore the one
/// credential in this system that needs a deliberately slow hash.
///
/// <para>
/// <b>The contrast with <c>DeviceKey</c> and <c>RefreshTokenValue</c> is the point, not an
/// inconsistency.</b> Both of those hash a server-generated 256-bit secret with SHA-256, because a
/// fast hash over exhaustive entropy is not guessable and a slow one would only tax the hot path. A
/// password has neither property: it is chosen by a person, it appears in every breach corpus, and an
/// attacker holding the table can try billions of candidates a second against a fast hash. The
/// algorithm follows the entropy, in both directions.
/// </para>
///
/// <para>
/// The implementation lives in <c>EAMS.Infrastructure</c> for the reason every service does — nothing
/// outside that assembly may see <c>EamsDbContext</c>, and the credential store that persists a rehash
/// needs both halves.
/// </para>
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a password for storage in <c>Users.PasswordHash</c>.</summary>
    string Hash(string password);

    /// <summary>
    /// Checks a presented password against a stored hash. Never throws on a malformed or empty stored
    /// hash — that is <see cref="PasswordVerification.Failed"/>, because a row that cannot be verified
    /// must not be a row that authenticates.
    /// </summary>
    PasswordVerification Verify(string storedHash, string presentedPassword);
}
