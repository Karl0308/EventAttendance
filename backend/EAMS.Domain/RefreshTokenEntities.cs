namespace EAMS.Domain;

/// <summary>
/// The lifetimes that govern a refresh-token family. Named here rather than spelled at the two places
/// that read them, because "how long can a stolen token stay useful?" is the single most consequential
/// number in the login design and it must be answerable from one place.
/// </summary>
public static class RefreshTokenPolicy
{
    /// <summary>
    /// How long one issued refresh token may be redeemed for. A client that has not come back within
    /// this window logs in again.
    /// </summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(14);

    /// <summary>
    /// <b>The absolute ceiling on a family, and it never moves.</b> Rotation issues a successor that
    /// inherits the family's original <see cref="RefreshToken.FamilyExpiresAt"/> rather than computing
    /// a fresh one, so a client that refreshes every day is still forced back through a password at
    /// day 30. Without the ceiling, rotation is an unbounded renewal and a session that is never
    /// closed is a session that is never re-authenticated.
    /// </summary>
    public static readonly TimeSpan FamilyLifetime = TimeSpan.FromDays(30);
}

/// <summary>
/// One issued refresh token. <b>Not in Technical Plan §4.11</b> — an additive table, recorded as
/// drift, and the reason it is a table rather than a column is the whole design.
///
/// <para>
/// <b>§4.11 gives <c>Users</c> a single <c>RefreshTokenHash</c> column, and one column cannot express
/// any of what a refresh token has to do.</b> It caps a user at one live session, so signing in on a
/// phone silently signs the same person out of the desk browser; it has nowhere to record when a token
/// was issued or when it expires, so a token is either eternal or governed by a rule stored nowhere;
/// and — the part that matters — it cannot distinguish "this token was rotated normally" from "this
/// token was rotated and is now being presented a second time", which is the only observable signature
/// a stolen refresh token has. The column is <b>kept and stays permanently NULL</b>, exactly as
/// <see cref="Device.ApiKey"/> is: dropping a column the plan declares is data-loss SQL and would make
/// the schema disagree with §4.11 with nothing recording why.
/// </para>
///
/// <para>
/// <b>Rotation is a compare-and-swap and replay revokes the whole family.</b> Redeeming a token
/// revokes it in the same statement that claims it (<c>WHERE RevokedAt IS NULL</c>) and issues a
/// successor carrying the same <see cref="FamilyId"/>. Presenting an already-revoked token therefore
/// means one of two things — a client retried after its successor was already issued, or somebody else
/// is holding a copy — and there is no way to tell them apart from the server. Revoking the entire
/// family is the answer to both: the legitimate client is inconvenienced by one re-login, and the
/// thief's stolen chain dies at the same moment. Tolerating replay to spare the first case is what
/// makes the second case undetectable.
/// </para>
///
/// <para>
/// <b>There is no grace window, deliberately.</b> A few seconds during which a rotated token still
/// works is the usual concession to flaky networks, and it is precisely the window a thief racing the
/// legitimate client needs. The cost of refusing it is a re-login after a dropped response; the cost
/// of granting it is that replay detection has a hole whose size is stated in the configuration.
/// </para>
/// </summary>
public class RefreshToken : Entity
{
    /// <summary>
    /// The user this token authenticates.
    ///
    /// <para>
    /// <b>No <c>SchoolId</c>, and no tenant query filter on this table</b> — see
    /// <c>EamsDbContext.ConfigureRefreshTokens</c>. A refresh is presented <em>before</em> any tenant
    /// is resolved, because resolving the tenant is one of the things the refresh is for; a filter
    /// here would hide every row from the only query that ever reads them.
    /// </para>
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The chain this token belongs to. Constant across every rotation of one login: the token issued
    /// at sign-in starts a family and each successor inherits it, so revoking a family closes exactly
    /// one session and leaves the user's other devices alone.
    /// </summary>
    public Guid FamilyId { get; set; }

    /// <summary>
    /// SHA-256 of the secret half, lower-case hex — the secret itself is never stored. See
    /// <see cref="RefreshTokenValue"/> for why a fast hash is the correct choice for this credential
    /// and the wrong one for a password.
    /// </summary>
    public string TokenHash { get; set; } = "";

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this individual token stops being redeemable (<see cref="RefreshTokenPolicy.TokenLifetime"/>).</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the <em>family</em> stops being renewable. Copied forward unchanged by every rotation, so
    /// it is a ceiling rather than a sliding window — see <see cref="RefreshTokenPolicy.FamilyLifetime"/>.
    /// </summary>
    public DateTime FamilyExpiresAt { get; set; }

    /// <summary>
    /// When this token was revoked — by being rotated, by an explicit sign-out, or by its family being
    /// burned after a replay. Null means live.
    ///
    /// <para>
    /// <b>Set in the same UPDATE that claims the rotation</b>, which is what makes the redemption
    /// atomic: two concurrent redemptions of one token both filter on <c>RevokedAt IS NULL</c> and
    /// exactly one of them affects a row.
    /// </para>
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// The successor issued when this token was rotated, or null if it was revoked without one (an
    /// explicit sign-out, or a family burn). The chain it forms is what makes a replay investigable
    /// after the fact rather than merely blocked.
    /// </summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>
    /// The <c>User-Agent</c> the token was issued to, verbatim and truncated, for the "your active
    /// sessions" list a user is shown before revoking one.
    ///
    /// <para>
    /// <b>It is display text and nothing else.</b> Nothing compares it on redemption: a user agent is
    /// attacker-controlled, so pinning a session to it would deny a legitimate client after a browser
    /// update while costing a thief one header. It exists so a person can recognise which row is the
    /// laptop they left at home.
    /// </para>
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// The address the token was issued from, sized like <c>AuditLogs.IpAddress</c> (IPv6 textual
    /// maximum). Display and audit only, for the reason <see cref="UserAgent"/> gives.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>Live means: not revoked, not past its own expiry, and its family not past its ceiling.</summary>
    public bool IsLiveAt(DateTime utcNow) =>
        RevokedAt is null && ExpiresAt > utcNow && FamilyExpiresAt > utcNow;
}
