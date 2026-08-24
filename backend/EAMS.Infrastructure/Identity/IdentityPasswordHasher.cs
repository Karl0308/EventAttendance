using EAMS.Application.Abstractions;
using EAMS.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace EAMS.Infrastructure.Identity;

/// <summary>
/// <see cref="IPasswordHasher"/> over ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/>.
///
/// <para>
/// <b>Identity's hasher, and none of the rest of Identity.</b> The dependency is the
/// <c>Microsoft.Extensions.Identity.Core</c> package alone — <b>not</b> a
/// <c>FrameworkReference</c> to <c>Microsoft.AspNetCore.App</c>, which would drag the entire web
/// framework into the Infrastructure layer to obtain one class and make the §3 layering rule a matter
/// of discipline rather than of what is referenced. Identity's own schema (<c>AspNetUsers</c> and
/// friends) is deliberately unused; §4.11 declares this system's tables and <c>RbacEntities</c>
/// implements them.
/// </para>
///
/// <para>
/// <b>Why Identity's hasher rather than a hand-rolled PBKDF2 or a BCrypt package.</b> The format it
/// writes is self-describing — the stored string carries its own version byte, iteration count and
/// salt — which is the only reason <see cref="PasswordVerification.SuccessRehashNeeded"/> can exist at
/// all. A hand-rolled hash that stores parameters nowhere can never be upgraded without a flag day, and
/// the flag day never comes.
/// </para>
/// </summary>
internal sealed class IdentityPasswordHasher : IPasswordHasher
{
    /// <summary>
    /// <b>V3, stated rather than inherited.</b> It is the current default, and the alternative —
    /// <c>IdentityV2</c> — is a compatibility mode for hashes written by ASP.NET Identity 2, which this
    /// system has none of. Naming it means a future framework default change is a decision someone
    /// makes here rather than one that arrives with a package bump.
    /// </summary>
    private const PasswordHasherCompatibilityMode Compatibility =
        PasswordHasherCompatibilityMode.IdentityV3;

    /// <summary>
    /// PBKDF2-HMAC-SHA512 iterations. Stated explicitly for the same reason as the mode above, and
    /// because this is the number that decides how expensive an offline attack on a leaked
    /// <c>Users</c> table is.
    ///
    /// <para>
    /// <b>Raising it later is safe and is the whole point of the rehash path.</b> A hash written at
    /// today's count still verifies, reports
    /// <see cref="PasswordVerification.SuccessRehashNeeded"/>, and is rewritten at the new count the
    /// next time its owner logs in — so an increase costs nothing and reaches every active account on
    /// its own. That property only holds while callers act on the tri-state result rather than
    /// collapsing it to a boolean.
    /// </para>
    /// </summary>
    private const int IterationCount = 210_000;

    private readonly PasswordHasher<User> _hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = Compatibility,
        IterationCount = IterationCount,
    }));

    /// <summary>
    /// The hasher ignores the user object entirely — it is a type parameter Identity carries for
    /// callers who want to salt with user data, which this one does not (the salt is per-hash and
    /// random, which is stronger than anything derived from a row). One instance, reused, so nothing
    /// depends on having a <c>User</c> to hand.
    /// </summary>
    private static readonly User Unused = new();

    public string Hash(string password) => _hasher.HashPassword(Unused, password);

    public PasswordVerification Verify(string storedHash, string presentedPassword)
    {
        // A row with no usable hash must not be a row that authenticates. Identity's implementation
        // throws on a null or malformed stored hash rather than returning Failed, and an exception
        // escaping here would turn a corrupt row into a 500 on the login endpoint — which tells an
        // attacker that the address exists.
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(presentedPassword))
            return PasswordVerification.Failed;

        PasswordVerificationResult result;
        try
        {
            result = _hasher.VerifyHashedPassword(Unused, storedHash, presentedPassword);
        }
        catch (FormatException)
        {
            // Not a base64 payload at all. Loud enough to be a distinct branch, silent to the caller
            // by design: it is the same refusal as a wrong password, which is what the caller must
            // report either way.
            return PasswordVerification.Failed;
        }

        return result switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };
    }
}
