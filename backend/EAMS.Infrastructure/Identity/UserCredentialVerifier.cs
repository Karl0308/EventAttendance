using EAMS.Application.Abstractions;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Identity;

/// <summary>
/// Checks an e-mail/password pair against <c>Users</c>. See <see cref="IUserCredentialVerifier"/> for
/// why this is the persistence half of login and not login itself.
/// </summary>
internal sealed class UserCredentialVerifier : IUserCredentialVerifier
{
    private readonly EamsDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<UserCredentialVerifier> _logger;

    public UserCredentialVerifier(
        EamsDbContext db, IPasswordHasher hasher, ILogger<UserCredentialVerifier> logger)
    {
        _db = db;
        _hasher = hasher;
        _logger = logger;
    }

    /// <summary>
    /// A hash of a value nobody knows, verified against when no user matched.
    ///
    /// <para>
    /// <b>Without it, an unknown address returns in microseconds and a known one returns after a
    /// deliberately expensive PBKDF2.</b> That difference is measurable over the network and it answers
    /// exactly the question <see cref="UserCredentialOutcome.UnknownEmail"/> and
    /// <see cref="UserCredentialOutcome.PasswordMismatch"/> are collapsed into one response to avoid.
    /// Building the response carefully and then leaking the answer through the clock is the classic
    /// way that mitigation is undone.
    /// </para>
    ///
    /// <para>
    /// <c>Lazy</c> rather than a field initializer: hashing costs ~200k PBKDF2 iterations and this
    /// service is scoped, so paying it once per process — and only if an unknown address is ever
    /// presented — is the difference between a startup cost and a per-request one.
    /// </para>
    /// </summary>
    private static readonly Lazy<string> DecoyHash = new(
        () => new IdentityPasswordHasher().Hash(Guid.NewGuid().ToString("N")),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<UserCredentialCheck> VerifyAsync(
        string email, string password, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);

        // IgnoreQueryFilters, and this is one of the two places in the system that may: a credential
        // is presented before any tenant is resolved, so filtering the lookup by the current tenant
        // would refuse every login on a host that has not yet decided whose login it is. The same
        // exemption DeviceAuthenticator takes, for the same reason, and UX_Users_Email is what makes
        // it safe — the index is global, so this query has exactly one possible answer.
        var user = normalized.Length == 0
            ? null
            : await _db.Users
                .IgnoreQueryFilters()
                .Where(u => u.Email == normalized)
                .Select(u => new { u.Id, u.SchoolId, u.PasswordHash, u.IsActive })
                .FirstOrDefaultAsync(ct);

        if (user is null)
        {
            // Deliberately still pays for a verification. See DecoyHash.
            _hasher.Verify(DecoyHash.Value, password);
            return UserCredentialCheck.Failed(UserCredentialOutcome.UnknownEmail);
        }

        var verification = _hasher.Verify(user.PasswordHash, password);
        if (verification == PasswordVerification.Failed)
            return UserCredentialCheck.Failed(UserCredentialOutcome.PasswordMismatch);

        // The upgrade, taken at the only moment the plaintext exists. Persisted before the outcome is
        // returned rather than left to the caller: a caller that forgot would leave the account on the
        // old parameters forever, and nothing would ever say so.
        if (verification == PasswordVerification.SuccessRehashNeeded)
            await RehashAsync(user.Id, password, ct);

        // Checked AFTER the password, not before. An inactive account whose password is wrong must
        // still answer "wrong credential" — reporting Inactive on a bad password would confirm the
        // address exists to anyone who guessed it.
        return user.IsActive
            ? new UserCredentialCheck(UserCredentialOutcome.Verified, user.Id, user.SchoolId)
            : new UserCredentialCheck(UserCredentialOutcome.Inactive, user.Id, user.SchoolId);
    }

    /// <summary>
    /// Rewrites the stored hash at the current parameters.
    ///
    /// <para>
    /// <b>It must never fail the login it is riding on.</b> The user has presented a correct password;
    /// a failed upgrade means the row keeps working parameters that are merely older, which is not a
    /// reason to refuse them entry. Logged at warning rather than swallowed, because a rehash that
    /// fails every time is a rehash that never happens, and the only symptom otherwise is an
    /// iteration count that quietly never moves.
    /// </para>
    ///
    /// <para>
    /// <c>ExecuteUpdateAsync</c> rather than load-modify-save: it touches exactly one column, so it
    /// cannot trip the ADR-001 D-2 derived-field guard or stamp <c>UpdatedAt</c> on a row nobody
    /// edited, and it is one statement rather than a read followed by a write.
    /// </para>
    /// </summary>
    private async Task RehashAsync(Guid userId, string password, CancellationToken ct)
    {
        try
        {
            var rehashed = _hasher.Hash(password);

            await _db.Users
                .IgnoreQueryFilters()
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, rehashed), ct);

            _logger.LogInformation(
                "Upgraded the stored password hash for user {UserId} to the current parameters.",
                userId);
        }
        catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.SqlClient.SqlException)
        {
            _logger.LogWarning(
                ex,
                "Could not upgrade the stored password hash for user {UserId}. The login itself is " +
                "unaffected — the row keeps a valid hash at older parameters and will be retried on " +
                "the next successful sign-in.",
                userId);
        }
    }

    /// <summary>
    /// <b>Trim and lower-case, and nothing more.</b> E-mail addresses are compared here against a
    /// column that <c>UX_Users_Email</c> makes unique under SQL Server's default case-insensitive
    /// collation, so the fold is belt-and-braces for the comparison and load-bearing for the write
    /// path — <c>CreateAdminCommand</c> and the seed both store the normalized form, so one spelling
    /// is what ever reaches the index.
    ///
    /// <para>
    /// It deliberately does not strip dots or <c>+tags</c> from the local part. Those are
    /// provider-specific conventions, not standards; normalizing them would silently merge two
    /// addresses an institution considers distinct, and an attendance system's users are staff whose
    /// addresses their IT department assigned.
    /// </para>
    /// </summary>
    internal static string NormalizeEmail(string? email) =>
        email?.Trim().ToLowerInvariant() ?? "";
}
