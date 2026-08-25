namespace EAMS.Application.Abstractions;

/// <summary>
/// What a login attempt did. One <c>401</c> reaches the caller for
/// <see cref="InvalidCredentials"/> — the three ways it can be produced are not distinguished on the
/// wire, only in the audit row.
/// </summary>
public enum AuthLoginOutcome
{
    /// <summary>
    /// Unknown address, wrong password, or a deactivated account. <b>Deliberately one value.</b>
    ///
    /// <para>
    /// The three are collapsed here rather than at the controller so there is no second place to get
    /// it right. An API that answers "no such user" separately from "wrong password" is an account
    /// enumeration oracle — a form that tells an attacker which of ten thousand leaked addresses
    /// belong to this institution before they have guessed a single password. "This account is
    /// deactivated" is the same disclosure wearing a helpful face.
    /// </para>
    ///
    /// <para>
    /// The timing has to match as well as the body, which is what
    /// <c>UserCredentialVerifier</c>'s decoy hash buys: an unknown address still pays for one Argon-class
    /// verification, so the response time does not answer the question the status code refuses to.
    /// </para>
    /// </summary>
    InvalidCredentials,

    /// <summary>Verified, active, and holding a session.</summary>
    Succeeded,
}

/// <summary>What a refresh attempt did.</summary>
public enum AuthRefreshOutcome
{
    /// <summary>
    /// No refresh cookie was presented at all. Distinct from <see cref="Rejected"/> because the
    /// remedy is different and a client can act on it: "you were never given a session, sign in"
    /// rather than "your session ended".
    /// </summary>
    Missing,

    /// <summary>
    /// The token was malformed, unknown, expired, replayed, or belongs to a deactivated user.
    /// <b>One value, for the reason <see cref="AuthLoginOutcome.InvalidCredentials"/> records.</b>
    /// A client that could tell "expired" from "replayed" could probe for live token ids.
    /// </summary>
    Rejected,

    /// <summary>Rotated. The presented token is now revoked and a successor has been issued.</summary>
    Succeeded,
}

/// <summary>What a change-password attempt did.</summary>
public enum ChangePasswordOutcome
{
    /// <summary>The current password did not verify, or the account is no longer active.</summary>
    Rejected,

    /// <summary>
    /// The new password failed the same rules <see cref="IUserProvisioningService"/> applies. Carries
    /// a message, because unlike a login this caller is already authenticated: telling them the
    /// password is too short discloses nothing.
    /// </summary>
    ValidationFailed,

    /// <summary>Changed, and every refresh token this user held is revoked.</summary>
    Changed,
}

/// <summary>
/// Everything an access token is minted from, and everything <c>/auth/me</c> answers with.
///
/// <para>
/// <b><see cref="Permissions"/> is the effective set, resolved through
/// <c>UserRoles → RolePermissions → Permissions</c> and flattened here.</b> No role name travels with
/// it and none is minted into the token: policies in this system are permission-based
/// (<c>EamsPermissions</c>), so a role claim would be a second vocabulary that no policy reads and
/// that every reader would eventually be tempted to branch on.
/// </para>
/// </summary>
public record AuthPrincipal(
    Guid UserId,
    Guid SchoolId,
    string Email,
    string FullName,
    IReadOnlyList<string> Permissions);

/// <summary>
/// A verified principal plus the refresh token that renews it.
///
/// <para>
/// <b>No access token here, and that is the layering (D-74).</b> The signing key never enters
/// <c>EAMS.Infrastructure</c>: this layer verifies credentials and persists sessions, and the API
/// layer mints. See <c>TokenIssuer</c>.
/// </para>
/// </summary>
public record AuthSession(
    AuthPrincipal Principal,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);

/// <summary>The result of <see cref="IAuthService.LoginAsync"/>.</summary>
public record AuthLoginResult(AuthLoginOutcome Outcome, AuthSession? Session)
{
    public static AuthLoginResult Failed(AuthLoginOutcome outcome) => new(outcome, null);
}

/// <summary>The result of <see cref="IAuthService.RefreshAsync"/>.</summary>
public record AuthRefreshResult(AuthRefreshOutcome Outcome, AuthSession? Session)
{
    public static AuthRefreshResult Failed(AuthRefreshOutcome outcome) => new(outcome, null);
}

/// <summary>The result of <see cref="IAuthService.ChangePasswordAsync"/>.</summary>
public record ChangePasswordResult(ChangePasswordOutcome Outcome, string Message, int SessionsRevoked)
{
    public static ChangePasswordResult Failed(ChangePasswordOutcome outcome, string message = "") =>
        new(outcome, message, 0);
}

/// <summary>
/// Technical Plan §11's <c>/auth</c> surface, minus the minting (D-74).
///
/// <para>
/// <b>Every method here writes an <c>AuditLogs</c> row in the same unit of work as the state it
/// changes.</b> §6.4 already calls attendance audited and §4.13 gives the table a nullable
/// <c>UserId</c> and (since Phase 6a) a nullable <c>SchoolId</c>, which is exactly what a failed login
/// against an unknown address needs: the attempt is real and the principal is not.
/// </para>
///
/// <para>
/// <b>Rate-limit refusals are deliberately not audited by this interface.</b> They are refused before
/// a credential is read, so there is nothing to attribute and no database round trip to spend; they
/// are visible as <c>429</c>s in the request log. See <c>AuthRateLimiting</c>.
/// </para>
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Verifies a credential and opens a session. Never distinguishes an unknown address from a wrong
    /// password from a deactivated account — see <see cref="AuthLoginOutcome.InvalidCredentials"/>.
    /// </summary>
    /// <param name="email">The login identifier, normalized by the implementation.</param>
    /// <param name="password">The presented password. Never logged, never echoed.</param>
    /// <param name="userAgent">Recorded against the session so an operator can recognise a device.</param>
    /// <param name="ipAddress">Recorded against the session and the audit row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuthLoginResult> LoginAsync(
        string email, string password, string? userAgent, string? ipAddress,
        CancellationToken ct = default);

    /// <summary>
    /// Rotates a refresh token and re-reads the principal, so a permission granted or revoked since
    /// the last login takes effect on the next refresh rather than on the next sign-in.
    /// </summary>
    Task<AuthRefreshResult> RefreshAsync(
        string? refreshToken, string? userAgent, string? ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Ends the session the presented refresh token belongs to, and only that one.
    ///
    /// <para>
    /// <b>One session, not all of them.</b> "Sign out" on this laptop must not sign the user out of
    /// the phone in their pocket; the operation that ends every session is
    /// <see cref="ChangePasswordAsync"/>, which is what a user reaches for when they believe a
    /// credential has leaked. A logout that carries no refresh cookie names no session and therefore
    /// revokes nothing — the access token it was called with expires within its 15-minute window.
    /// </para>
    /// </summary>
    /// <returns>How many live refresh tokens were revoked. Zero is a normal answer.</returns>
    Task<int> LogoutAsync(
        Guid userId, string? refreshToken, string? ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Changes a password after re-verifying the current one, and revokes <b>every</b> refresh token
    /// the user holds — including the caller's own.
    ///
    /// <para>
    /// <b>Including the caller's own, deliberately.</b> The reason to change a password is usually
    /// that it may have leaked, and a rotation that leaves the other sessions live changes nothing
    /// for the attacker holding one. The cost is one re-login on every device, which is the
    /// behaviour a user expects from this operation anyway.
    /// </para>
    /// </summary>
    Task<ChangePasswordResult> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, string? ipAddress,
        CancellationToken ct = default);

    /// <summary>
    /// The user's display fields for <c>/auth/me</c>. <b>Permissions are deliberately not returned
    /// here</b> — the endpoint answers with the ones baked into the presented token, so that what the
    /// UI gates on is what the server will actually enforce for that token's remaining life. See
    /// <c>AuthController.Me</c>.
    /// </summary>
    /// <returns><c>null</c> when the user no longer exists or is no longer active.</returns>
    Task<AuthProfile?> GetProfileAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>The display half of a principal — no permissions, by design. See <see cref="IAuthService.GetProfileAsync"/>.</summary>
public record AuthProfile(Guid UserId, Guid SchoolId, string Email, string FullName);
