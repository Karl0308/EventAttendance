using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Identity;

/// <summary>
/// Technical Plan §11's <c>/auth</c> behaviour: verify, resolve, persist, audit (Phase 6b).
///
/// <para>
/// <b>It composes the Phase 6a pieces rather than reimplementing any of them.</b>
/// <see cref="IUserCredentialVerifier"/> owns the password comparison and its decoy-hash timing
/// equalisation; <see cref="IRefreshTokenStore"/> owns rotation, family burning and the
/// compare-and-swap that decides a race. What is new here is the join between them, the effective
/// permission set, and the audit trail.
/// </para>
///
/// <para>
/// <b>It does not mint an access token, and cannot (D-74).</b> The signing key is resolved in
/// <c>EAMS.Api</c> and stays there. This class returns an <see cref="AuthPrincipal"/> and a refresh
/// token; <c>TokenIssuer</c> turns the first into a JWT. Keeping the key out of this assembly means
/// the layer that talks to the database has no way to forge a credential for it, and it means
/// changing the token format never touches a file that also writes rows.
/// </para>
/// </summary>
internal sealed class AuthService : IAuthService
{
    // The §4.13 Action values this service writes. Named constants rather than literals for the same
    // reason EamsPermissions exists: a typo'd audit action compiles, writes, and is discovered by a
    // query that returns nothing on the day someone needs the trail.
    internal const string LoginSucceededAction = "auth.login.succeeded";
    internal const string LoginFailedAction = "auth.login.failed";
    internal const string RefreshReplayedAction = "auth.refresh.replayed";
    internal const string PasswordChangedAction = "auth.password.changed";
    internal const string LogoutAction = "auth.logout";

    private readonly EamsDbContext _db;
    private readonly IUserCredentialVerifier _credentials;
    private readonly IRefreshTokenStore _refreshTokens;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        EamsDbContext db,
        IUserCredentialVerifier credentials,
        IRefreshTokenStore refreshTokens,
        IPasswordHasher hasher,
        ILogger<AuthService> logger)
    {
        _db = db;
        _credentials = credentials;
        _refreshTokens = refreshTokens;
        _hasher = hasher;
        _logger = logger;
    }

    // ------------------------------------------------------------------------------------- login

    public async Task<AuthLoginResult> LoginAsync(
        string email, string password, string? userAgent, string? ipAddress,
        CancellationToken ct = default)
    {
        var check = await _credentials.VerifyAsync(email, password, ct);

        if (check.Outcome != UserCredentialOutcome.Verified)
        {
            // One audit row for all three failure shapes, and the outcome recorded *inside* it. The
            // wire cannot distinguish them (see AuthLoginOutcome.InvalidCredentials) but an operator
            // reading the trail must: "forty attempts against addresses that do not exist" and "forty
            // attempts against one address that does" are different incidents.
            await AuditAsync(
                LoginFailedAction,
                schoolId: check.SchoolId == Guid.Empty ? null : check.SchoolId,
                userId: check.UserId == Guid.Empty ? null : check.UserId,
                entityId: check.UserId == Guid.Empty ? null : check.UserId,
                ipAddress: ipAddress,
                changes: new
                {
                    email = UserCredentialVerifier.NormalizeEmail(email),
                    reason = check.Outcome.ToString(),
                    userAgent,
                },
                ct);

            return AuthLoginResult.Failed(AuthLoginOutcome.InvalidCredentials);
        }

        var profile = await LoadProfileAsync(check.UserId, ct);
        if (profile is null)
        {
            // The row verified a moment ago and is gone now. Deletes are Restrict everywhere, so this
            // is unreachable; "unreachable" is exactly when a session must not be opened.
            _logger.LogWarning(
                "User {UserId} verified a credential and then could not be read back. No session was " +
                "opened.", check.UserId);

            return AuthLoginResult.Failed(AuthLoginOutcome.InvalidCredentials);
        }

        var permissions = await LoadPermissionsAsync(check.UserId, ct);

        var issued = await _refreshTokens.IssueAsync(check.UserId, userAgent, ipAddress, ct);
        if (issued.Outcome != RefreshTokenOutcome.Rotated || issued.Token is null)
        {
            _logger.LogError(
                "A verified login for user {UserId} could not be issued a refresh token ({Outcome}).",
                check.UserId, issued.Outcome);

            return AuthLoginResult.Failed(AuthLoginOutcome.InvalidCredentials);
        }

        await AuditAsync(
            LoginSucceededAction,
            schoolId: profile.SchoolId,
            userId: profile.UserId,
            entityId: profile.UserId,
            ipAddress: ipAddress,
            // The permission *count*, not the codes. The codes are derivable from the roles at any
            // time and would make every audit row grow with the permission registry; the count is what
            // makes "this account suddenly signs in holding eleven permissions instead of four"
            // visible in a scan of the table.
            changes: new { email = profile.Email, permissionCount = permissions.Count, userAgent },
            ct);

        return new AuthLoginResult(
            AuthLoginOutcome.Succeeded,
            new AuthSession(
                new AuthPrincipal(
                    profile.UserId, profile.SchoolId, profile.Email, profile.FullName, permissions),
                issued.Token,
                issued.ExpiresAt));
    }

    // ----------------------------------------------------------------------------------- refresh

    public async Task<AuthRefreshResult> RefreshAsync(
        string? refreshToken, string? userAgent, string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(refreshToken))
            return AuthRefreshResult.Failed(AuthRefreshOutcome.Missing);

        var rotation = await _refreshTokens.RotateAsync(refreshToken, userAgent, ipAddress, ct);

        if (rotation.Outcome == RefreshTokenOutcome.ReplayDetected)
        {
            // The store has already burned the family by the time this runs — see
            // RefreshTokenStore.BurnFamilyAsync. This row is what makes the burn findable afterwards:
            // "why was this user signed out of everything at 02:14?" has an answer in the table rather
            // than only in a log that may have rolled.
            await AuditAsync(
                RefreshReplayedAction,
                schoolId: null,
                userId: rotation.UserId == Guid.Empty ? null : rotation.UserId,
                entityId: rotation.FamilyId == Guid.Empty ? null : rotation.FamilyId,
                ipAddress: ipAddress,
                changes: new { familyId = rotation.FamilyId, userAgent },
                ct);
        }

        if (rotation.Outcome != RefreshTokenOutcome.Rotated || rotation.Token is null)
            return AuthRefreshResult.Failed(AuthRefreshOutcome.Rejected);

        var profile = await LoadProfileAsync(rotation.UserId, ct);
        if (profile is null)
            return AuthRefreshResult.Failed(AuthRefreshOutcome.Rejected);

        // Re-read on every refresh rather than carried forward from the old token. This is the whole
        // reason a 15-minute access token is worth the round trip: a permission granted or revoked
        // takes effect within one refresh interval instead of at the next sign-in, without putting a
        // permission lookup on the hot path of every request.
        var permissions = await LoadPermissionsAsync(rotation.UserId, ct);

        return new AuthRefreshResult(
            AuthRefreshOutcome.Succeeded,
            new AuthSession(
                new AuthPrincipal(
                    profile.UserId, profile.SchoolId, profile.Email, profile.FullName, permissions),
                rotation.Token,
                rotation.ExpiresAt));
    }

    // ------------------------------------------------------------------------------------ logout

    public async Task<int> LogoutAsync(
        Guid userId, string? refreshToken, string? ipAddress, CancellationToken ct = default)
    {
        var revoked = 0;
        Guid? familyId = null;

        if (!string.IsNullOrEmpty(refreshToken)
            && RefreshTokenValue.TryParse(refreshToken, out var tokenId, out var secret))
        {
            var row = await _db.RefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tokenId, ct);

            // The secret is verified even here, and the user id is checked against the caller's. A
            // logout that revoked whatever family a presented token id named would be a way for
            // anyone holding a valid access token to end a *stranger's* session by guessing ids.
            if (row is not null
                && row.UserId == userId
                && RefreshTokenValue.SecretMatches(secret, row.TokenHash))
            {
                familyId = row.FamilyId;
                revoked = await _refreshTokens.RevokeFamilyAsync(row.FamilyId, ct);
            }
        }

        await AuditAsync(
            LogoutAction,
            schoolId: null,
            userId: userId,
            entityId: familyId,
            ipAddress: ipAddress,
            changes: new { familyId, revoked },
            ct);

        return revoked;
    }

    // --------------------------------------------------------------------------- change password

    public async Task<ChangePasswordResult> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, string? ipAddress,
        CancellationToken ct = default)
    {
        // GetProfileAsync, not LoadProfileAsync: this caller is authenticated, so the tenant is
        // resolved and the filter is active — and it should be. A token minted for one school must not
        // be able to reach a user row in another, however it came to name one.
        var profile = await GetProfileAsync(userId, ct);
        if (profile is null)
            return ChangePasswordResult.Failed(ChangePasswordOutcome.Rejected);

        // Re-verified through the same seam a login uses, so the decoy-hash timing behaviour and any
        // future hash-parameter upgrade apply here too rather than being a second implementation that
        // drifts.
        var check = await _credentials.VerifyAsync(profile.Email, currentPassword, ct);
        if (check.Outcome != UserCredentialOutcome.Verified || check.UserId != userId)
            return ChangePasswordResult.Failed(ChangePasswordOutcome.Rejected);

        if (!ValidateNewPassword(profile.Email, currentPassword, newPassword, out var message))
            return ChangePasswordResult.Failed(ChangePasswordOutcome.ValidationFailed, message);

        var hash = _hasher.Hash(newPassword);

        // The tenant filter is active and correct on this path — the caller's token carried a
        // school_id, so ClaimsSchoolContext resolved a real tenant. No IgnoreQueryFilters, and none
        // is wanted: a user may only change their own password, inside their own school.
        var updated = await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, hash), ct);

        if (updated == 0)
            return ChangePasswordResult.Failed(ChangePasswordOutcome.Rejected);

        // Every session, including this one. See IAuthService.ChangePasswordAsync for why the
        // convenience of staying signed in is the wrong trade on precisely this operation. Ordered
        // after the hash write: a crash between them leaves a changed password and live sessions
        // whose tokens still refresh, which is recoverable; the reverse leaves the user signed out of
        // everything with the old password still valid, which reads as the change having failed.
        var revoked = await _refreshTokens.RevokeAllForUserAsync(userId, ct);

        await AuditAsync(
            PasswordChangedAction,
            schoolId: profile.SchoolId,
            userId: userId,
            entityId: userId,
            ipAddress: ipAddress,
            changes: new { sessionsRevoked = revoked },
            ct);

        return new ChangePasswordResult(
            ChangePasswordOutcome.Changed,
            revoked == 1
                ? "Password changed. Your other devices were already signed out."
                : $"Password changed. {revoked} session(s) were signed out, including this one.",
            revoked);
    }

    // --------------------------------------------------------------------------------- /auth/me

    public async Task<AuthProfile?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        // No IgnoreQueryFilters, and that is the point of it being a third method rather than a reuse
        // of LoadProfileAsync. By the time /auth/me runs, the request carries a school_id claim, so
        // the tenant filter is active and resolved — and it *should* apply: a token minted for one
        // school must not be able to read a user row from another, however it came to name one.
        var profile = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => new AuthProfile(u.Id, u.SchoolId, u.Email, u.FullName))
            .FirstOrDefaultAsync(ct);

        return profile;
    }

    // ------------------------------------------------------- the two tenant-filter exemptions
    //
    // These are the ONLY two methods in this class that call IgnoreQueryFilters, and
    // AuthTenantContainmentTests asserts that in the source. The hazard they contain is subtle enough
    // to state twice:
    //
    // `Users` and `UserRoles` both carry a SchoolId query filter, and the login that reads them is
    // what DETERMINES the tenant — there is no claim yet. Behind ClaimsSchoolContext the unauthenticated
    // fallback is the pinned development school, so during the staged cutover a filtered login query
    // would see only that school's users and every other school's operators would get a perfectly
    // correct-looking 401. After the pin is deleted the tenant is null and the filter goes inactive,
    // so the same code would start working. In a one-school development database the two behave
    // identically and correctly, which is why this cannot be left to be noticed later.
    //
    // Both project immediately. Nothing that is still an entity — and therefore nothing that could be
    // handed to a caller, tracked, or navigated from — escapes either method.

    /// <summary>
    /// The user's identity fields, read without the tenant filter because the tenant is what the
    /// caller is trying to establish. Projects to <see cref="AuthProfile"/> in the query.
    /// </summary>
    private async Task<AuthProfile?> LoadProfileAsync(Guid userId, CancellationToken ct) =>
        await _db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => new AuthProfile(u.Id, u.SchoolId, u.Email, u.FullName))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The effective permission set: <c>UserRoles → Role → RolePermissions → Permission.Code</c>,
    /// flattened, de-duplicated and ordered. Projects to <c>string</c> in the query.
    ///
    /// <para>
    /// <c>UserRoles</c> is the filtered root — <c>Roles</c>, <c>RolePermissions</c> and
    /// <c>Permissions</c> are global reference data and carry no filter — so one
    /// <c>IgnoreQueryFilters</c> at the top covers the join.
    /// </para>
    ///
    /// <para>
    /// Ordered so a token's <c>perm</c> array is stable between two mints of the same grant set. That
    /// costs nothing and makes two tokens comparable by eye when someone is asking why one works.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadPermissionsAsync(Guid userId, CancellationToken ct) =>
        await _db.UserRoles.AsNoTracking().IgnoreQueryFilters()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role!.RolePermissions)
            .Select(rp => rp.Permission!.Code)
            .Distinct()
            .OrderBy(code => code)
            .ToListAsync(ct);

    // ------------------------------------------------------------------------------------ shared

    /// <summary>
    /// The same rules <see cref="UserProvisioningService"/> applies at creation, so an account cannot
    /// be walked below the bar it was created at by changing its password. The length is read off
    /// that class rather than re-declared here — two numbers that must agree is one number written
    /// twice.
    /// </summary>
    private static bool ValidateNewPassword(
        string email, string currentPassword, string newPassword, out string message)
    {
        message = "";

        if (newPassword.Length < UserProvisioningService.MinimumPassword)
        {
            // The length is named; the password never is. Nothing in this method formats either one.
            message = $"The new password must be at least {UserProvisioningService.MinimumPassword} characters.";
            return false;
        }

        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
        {
            message = "The new password must be different from the current one.";
            return false;
        }

        if (string.Equals(newPassword.Trim(), email, StringComparison.OrdinalIgnoreCase))
        {
            message = "The password must not be the e-mail address.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// One §4.13 row, saved immediately.
    ///
    /// <para>
    /// <b>Saved on its own rather than folded into the caller's unit of work, and only here.</b> The
    /// state each caller changes is written by <c>ExecuteUpdate</c> or by the refresh-token store,
    /// neither of which participates in this context's change tracker — so there is no shared
    /// SaveChanges to join. What that costs is the possibility of a state change without its audit
    /// row if the process dies between them; what it buys is that a failure to audit can never roll
    /// back a completed login. Given the alternative is an explicit transaction spanning a
    /// compare-and-swap that is deliberately outside one (see <c>RefreshTokenStore</c>), this is the
    /// honest trade rather than the convenient one.
    /// </para>
    ///
    /// <para>
    /// <c>SchoolId</c> is nullable and is genuinely null for a failed login against an address that
    /// belongs to no school. That column landed in Phase 6a for exactly this row.
    /// </para>
    /// </summary>
    private async Task AuditAsync(
        string action,
        Guid? schoolId,
        Guid? userId,
        Guid? entityId,
        string? ipAddress,
        object changes,
        CancellationToken ct)
    {
        try
        {
            _db.AuditLogs.Add(new AuditLog
            {
                SchoolId = schoolId,
                UserId = userId,
                Action = action,
                EntityType = nameof(User),
                EntityId = entityId,
                Changes = JsonSerializer.Serialize(changes),
                IpAddress = Truncate(ipAddress, IpAddressMaxLength),
            });

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.SqlClient.SqlException)
        {
            // Loud, and not fatal. An unwritable audit row must not turn a successful sign-in into a
            // 500 — but it must not vanish either, so the row's whole content goes to the log where a
            // structured sink can still capture it.
            _logger.LogError(
                ex,
                "Could not write the '{Action}' audit row for user {UserId} (school {SchoolId}, " +
                "entity {EntityId}). The operation itself was unaffected.",
                action, userId, schoolId, entityId);

            // The failed Add is still sitting in the change tracker and would be retried by the next
            // SaveChanges on this context — on a different operation, which would then fail for a
            // reason that has nothing to do with it.
            _db.ChangeTracker.Clear();
        }
    }

    // Matches AuditLogs.IpAddress in ConfigureSystem. Truncated rather than refused, as
    // RefreshTokenStore does: a proxy chain in a header must not be able to fail a login.
    private const int IpAddressMaxLength = 45;

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
