namespace EAMS.Application.Abstractions;

/// <summary>
/// What happened when a presented refresh token was redeemed.
///
/// <para>
/// <b>The split between the members is a contract, not a taxonomy.</b> A client has to be able to tell
/// "log in again" from "retry" — an offline mobile queue behaves differently for each — and lumping
/// every failure into one answer makes them indistinguishable. Every member below except
/// <see cref="Rotated"/> means "log in again"; what differs is what the <em>server</em> must do, and
/// <see cref="ReplayDetected"/> is the one that burns a family.
/// </para>
/// </summary>
public enum RefreshTokenOutcome
{
    /// <summary>The token is not shaped like a refresh token. No query was run.</summary>
    Malformed,

    /// <summary>No row holds that token id.</summary>
    Unknown,

    /// <summary>The id resolved, the secret did not match. The row is left alone — a wrong secret against a live id is a guess, not a replay.</summary>
    SecretMismatch,

    /// <summary>This individual token is past <c>ExpiresAt</c>.</summary>
    Expired,

    /// <summary>
    /// The family is past its 30-day ceiling. Distinct from <see cref="Expired"/> because the user's
    /// answer is the same but the operator's reading is not: this one means the session ran its full
    /// allowed length rather than being abandoned.
    /// </summary>
    FamilyExpired,

    /// <summary>
    /// <b>An already-revoked token was presented, and the whole family has been revoked as a
    /// result.</b> Indistinguishable from the server's side from a legitimate client retrying after
    /// losing the response that carried its successor — which is exactly why the family dies either
    /// way. See <c>RefreshToken</c> for why tolerating this case is what would make theft
    /// undetectable.
    /// </summary>
    ReplayDetected,

    /// <summary>The user behind the token is deactivated. The family is revoked with it.</summary>
    UserInactive,

    /// <summary>Redeemed. A successor was issued and the presented token is now revoked.</summary>
    Rotated,
}

/// <summary>
/// The result of redeeming a refresh token. <paramref name="Token"/> is the plaintext successor and is
/// non-null only for <see cref="RefreshTokenOutcome.Rotated"/> — it exists exactly once, in this
/// record, and is never stored.
/// </summary>
public record RefreshTokenRotation(
    RefreshTokenOutcome Outcome,
    string? Token,
    Guid UserId,
    Guid FamilyId,
    DateTime ExpiresAt)
{
    /// <summary>The failures that issue nothing. Ids are <see cref="Guid.Empty"/> where none resolved.</summary>
    public static RefreshTokenRotation Failed(
        RefreshTokenOutcome outcome, Guid userId = default, Guid familyId = default) =>
        new(outcome, null, userId, familyId, default);
}

/// <summary>
/// A live session, for the "where am I signed in?" list. Carries no hash and no token — there is
/// nothing here that could be redeemed if the response leaked.
/// </summary>
public record RefreshTokenSession(
    Guid FamilyId,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    DateTime FamilyExpiresAt,
    string? UserAgent,
    string? IpAddress);

/// <summary>
/// Issues, rotates and revokes §11's refresh tokens.
///
/// <para>
/// <b>It mints no access token and reads no claim.</b> This is the persistence half of login — the
/// half that has a table, a schema and a set of races — and it is deliberately separable from the
/// endpoint that will eventually compose it with a JWT. The same division <c>IDeviceAuthenticator</c>
/// and <c>DeviceKeyHandler</c> already run on: the store knows about rows, the handler knows about
/// credentials and claims, and neither knows the other's vocabulary.
/// </para>
///
/// <para>
/// <b>Nothing here reads or writes <c>Users.RefreshTokenHash</c>.</b> §4.11's single-column design
/// cannot express rotation, replay detection or more than one session, so the column is kept and left
/// permanently NULL — see <c>RefreshToken</c>, and <c>Device.ApiKey</c> for the identical decision one
/// phase earlier.
/// </para>
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>
    /// Starts a new family at sign-in and returns the plaintext token. The family's 30-day ceiling is
    /// fixed here and never moves again.
    /// </summary>
    Task<RefreshTokenRotation> IssueAsync(
        Guid userId, string? userAgent, string? ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Redeems a presented token for its successor, or reports why it could not be.
    ///
    /// <para>
    /// <b>The redemption is a compare-and-swap.</b> The presented row is revoked by an UPDATE that
    /// filters on <c>RevokedAt IS NULL</c>, so two concurrent redemptions of one token race on the
    /// database rather than on a read-then-write in this process, and exactly one of them affects a
    /// row. The loser is <see cref="RefreshTokenOutcome.ReplayDetected"/> and takes the family with
    /// it — which is the correct answer to a genuine double-submit as well as to a theft, for the
    /// reason <c>RefreshToken</c> records.
    /// </para>
    /// </summary>
    Task<RefreshTokenRotation> RotateAsync(
        string token, string? userAgent, string? ipAddress, CancellationToken ct = default);

    /// <summary>Revokes every live token in one family — an explicit sign-out of one session.</summary>
    Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken ct = default);

    /// <summary>Revokes every live token a user holds — "sign out everywhere", and what a password change must call.</summary>
    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The user's live sessions, newest first. One entry per family, not per token.</summary>
    Task<IReadOnlyList<RefreshTokenSession>> ListSessionsAsync(
        Guid userId, CancellationToken ct = default);
}
