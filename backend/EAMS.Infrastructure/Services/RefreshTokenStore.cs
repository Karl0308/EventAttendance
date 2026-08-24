using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// §11 login's refresh-token rotation. See <see cref="IRefreshTokenStore"/> for the contract and
/// <see cref="RefreshToken"/> for why rotation, replay detection and a family ceiling are one design
/// rather than three features.
/// </summary>
internal sealed class RefreshTokenStore : IRefreshTokenStore
{
    private readonly EamsDbContext _db;
    private readonly ILogger<RefreshTokenStore> _logger;

    public RefreshTokenStore(EamsDbContext db, ILogger<RefreshTokenStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RefreshTokenRotation> IssueAsync(
        Guid userId, string? userAgent, string? ipAddress, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var familyExpiresAt = now + RefreshTokenPolicy.FamilyLifetime;

        var issued = RefreshTokenValue.Issue();
        var familyId = Guid.NewGuid();

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = issued.TokenId,
            UserId = userId,
            FamilyId = familyId,
            TokenHash = issued.Hash,
            IssuedAt = now,
            ExpiresAt = TokenExpiry(now, familyExpiresAt),
            FamilyExpiresAt = familyExpiresAt,
            UserAgent = Truncate(userAgent, UserAgentMaxLength),
            IpAddress = Truncate(ipAddress, IpAddressMaxLength),
        });

        await _db.SaveChangesAsync(ct);

        return new RefreshTokenRotation(
            RefreshTokenOutcome.Rotated, issued.Token, userId, familyId,
            TokenExpiry(now, familyExpiresAt));
    }

    public async Task<RefreshTokenRotation> RotateAsync(
        string token, string? userAgent, string? ipAddress, CancellationToken ct = default)
    {
        // Every shape check before any query: a body that is not a token must cost one string
        // comparison rather than a key seek.
        if (!RefreshTokenValue.TryParse(token, out var tokenId, out var secret))
            return RefreshTokenRotation.Failed(RefreshTokenOutcome.Malformed);

        // AsNoTracking, because the row is about to be revoked by ExecuteUpdate rather than by the
        // change tracker — a tracked copy would be a second, stale opinion of the same row.
        // RefreshTokens carries no query filter (see ConfigureRefreshTokens), so this seek needs no
        // IgnoreQueryFilters and would silently return nothing if one were ever added.
        var row = await _db.RefreshTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tokenId, ct);

        if (row is null)
            return RefreshTokenRotation.Failed(RefreshTokenOutcome.Unknown);

        // Fixed-time, over the raw hash bytes. A wrong secret against a live id is a guess, not a
        // replay: the row is left alone deliberately, because revoking a family on a failed guess
        // would hand anyone who knows a token id a way to log a user out.
        if (!RefreshTokenValue.SecretMatches(secret, row.TokenHash))
            return RefreshTokenRotation.Failed(RefreshTokenOutcome.SecretMismatch, row.UserId, row.FamilyId);

        var now = DateTime.UtcNow;

        // Replay. Checked before expiry on purpose: a revoked-and-expired token presented late is
        // still evidence of a copy, and reporting it as merely Expired would drop the signal.
        if (row.RevokedAt is not null)
            return await BurnFamilyAsync(row, RefreshTokenOutcome.ReplayDetected, ct);

        if (row.ExpiresAt <= now)
            return RefreshTokenRotation.Failed(RefreshTokenOutcome.Expired, row.UserId, row.FamilyId);

        if (row.FamilyExpiresAt <= now)
            return RefreshTokenRotation.Failed(RefreshTokenOutcome.FamilyExpired, row.UserId, row.FamilyId);

        // The account may have been deactivated since the token was issued. Ignoring the tenant
        // filter for the same reason UserCredentialVerifier does: no tenant is resolved yet, and
        // resolving one is part of what this refresh is for.
        var isActive = await _db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Id == row.UserId)
            .Select(u => (bool?)u.IsActive)
            .FirstOrDefaultAsync(ct);

        // Null means the user row is gone — deletes are Restrict everywhere so this should be
        // unreachable, and "unreachable" is exactly when a live session must not be renewed.
        if (isActive != true)
            return await BurnFamilyAsync(row, RefreshTokenOutcome.UserInactive, ct);

        var successor = RefreshTokenValue.Issue();

        // ------------------------------------------------------------------ the compare-and-swap
        //
        // This UPDATE is the serialization point of the whole design. Two concurrent redemptions of
        // one token both filter on RevokedAt IS NULL, SQL Server admits exactly one of them, and the
        // other affects zero rows — so "who won?" is decided by the database rather than by a
        // read-then-write in this process, where both callers would have read a live row and both
        // would have issued a successor.
        var claimed = await _db.RefreshTokens
            .Where(t => t.Id == tokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.ReplacedByTokenId, successor.TokenId),
                ct);

        if (claimed == 0)
        {
            // Lost the race. Indistinguishable from a stolen token being replayed — which is why it
            // is treated as one: the legitimate client pays a re-login, the thief's chain dies.
            return await BurnFamilyAsync(row, RefreshTokenOutcome.ReplayDetected, ct);
        }

        // Written after the swap, not inside a transaction with it, and the asymmetry is deliberate.
        // The predecessor is already revoked, so a crash here leaves a family with no live token —
        // the client logs in again. The opposite order would leave a window in which two live tokens
        // share a family, which is the state replay detection exists to make impossible.
        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = successor.TokenId,
            UserId = row.UserId,
            FamilyId = row.FamilyId,
            TokenHash = successor.Hash,
            IssuedAt = now,
            ExpiresAt = TokenExpiry(now, row.FamilyExpiresAt),
            // Inherited unchanged. This is the 30-day ceiling; recomputing it here is what would turn
            // rotation into unbounded renewal.
            FamilyExpiresAt = row.FamilyExpiresAt,
            UserAgent = Truncate(userAgent, UserAgentMaxLength),
            IpAddress = Truncate(ipAddress, IpAddressMaxLength),
        });

        await _db.SaveChangesAsync(ct);

        return new RefreshTokenRotation(
            RefreshTokenOutcome.Rotated, successor.Token, row.UserId, row.FamilyId,
            TokenExpiry(now, row.FamilyExpiresAt));
    }

    public async Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task<IReadOnlyList<RefreshTokenSession>> ListSessionsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // One entry per family, not per token: a session that has refreshed forty times is one
        // session, and listing forty rows would make "revoke that laptop" a question about which row.
        // The newest live token in each family is the one whose timestamps describe it.
        var live = await _db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId
                     && t.RevokedAt == null
                     && t.ExpiresAt > now
                     && t.FamilyExpiresAt > now)
            .OrderByDescending(t => t.IssuedAt)
            .ToListAsync(ct);

        return live
            .GroupBy(t => t.FamilyId)
            .Select(g => g.First())
            .OrderByDescending(t => t.IssuedAt)
            .Select(t => new RefreshTokenSession(
                t.FamilyId, t.IssuedAt, t.ExpiresAt, t.FamilyExpiresAt, t.UserAgent, t.IpAddress))
            .ToList();
    }

    /// <summary>
    /// Revokes every live token in the presented token's family and reports the outcome that caused
    /// it. Logged at warning, because a replay is either a client bug or a theft and both are things
    /// an operator wants to find in a log rather than infer from a support ticket.
    /// </summary>
    private async Task<RefreshTokenRotation> BurnFamilyAsync(
        RefreshToken row, RefreshTokenOutcome outcome, CancellationToken ct)
    {
        var revoked = await RevokeFamilyAsync(row.FamilyId, ct);

        _logger.LogWarning(
            "Refresh token family {FamilyId} for user {UserId} was revoked ({Revoked} live token(s)) " +
            "after {Outcome}. A replayed token is indistinguishable from a client retrying a lost " +
            "response, so the family is burned either way — the user signs in again.",
            row.FamilyId, row.UserId, revoked, outcome);

        return RefreshTokenRotation.Failed(outcome, row.UserId, row.FamilyId);
    }

    /// <summary>
    /// A token never outlives its family. Without the clamp the last rotation before the ceiling would
    /// hand out a token whose own <c>ExpiresAt</c> is fourteen days past the ceiling, and the ceiling
    /// would then be enforced only by a second check that a future refactor could drop.
    /// </summary>
    private static DateTime TokenExpiry(DateTime now, DateTime familyExpiresAt)
    {
        var ownExpiry = now + RefreshTokenPolicy.TokenLifetime;
        return ownExpiry < familyExpiresAt ? ownExpiry : familyExpiresAt;
    }

    // Sized to the columns in ConfigureRefreshTokens. Truncated rather than refused: a browser sending
    // an absurd User-Agent must not be able to fail a login, and the value is display text.
    private const int UserAgentMaxLength = 400;
    private const int IpAddressMaxLength = 45;

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
