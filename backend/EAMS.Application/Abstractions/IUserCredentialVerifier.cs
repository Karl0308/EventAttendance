namespace EAMS.Application.Abstractions;

/// <summary>
/// What checking an e-mail and password against <c>Users</c> concluded.
///
/// <para>
/// <b>The distinctions here are for the server's logs, never for the client's response body.</b>
/// <see cref="UnknownEmail"/> and <see cref="PasswordMismatch"/> must reach a caller as one
/// indistinguishable refusal — telling them apart is a user-enumeration oracle, and an attendance
/// system's user list is staff names. They are separate members because an operator reading an audit
/// trail needs to tell "somebody is guessing addresses" from "one person has forgotten their
/// password", and those are different incidents.
/// </para>
/// </summary>
public enum UserCredentialOutcome
{
    /// <summary>No user holds that e-mail. Reported to a client as an ordinary refusal.</summary>
    UnknownEmail,

    /// <summary>The user resolved, the password did not match. Reported identically to the above.</summary>
    PasswordMismatch,

    /// <summary>
    /// The credential is correct and the account is deactivated (<c>Users.IsActive = 0</c>).
    /// Identified, not permitted — the same split <c>DeviceAuthenticationOutcome.DeviceInactive</c>
    /// makes, and for the same reason: a person whose account was turned off needs to be told that
    /// rather than left retyping a password that is in fact right.
    /// </summary>
    Inactive,

    /// <summary>Correct, active, and the caller may proceed to mint a session.</summary>
    Verified,
}

/// <summary>
/// The user behind a verified credential. Ids and the tenant only — deliberately no name and no
/// e-mail, so a wrong answer discloses nothing.
/// </summary>
public record UserCredentialCheck(UserCredentialOutcome Outcome, Guid UserId, Guid SchoolId)
{
    /// <summary>The failures that identify nobody.</summary>
    public static UserCredentialCheck Failed(UserCredentialOutcome outcome) =>
        new(outcome, Guid.Empty, Guid.Empty);
}

/// <summary>
/// Verifies an e-mail/password pair against <c>Users</c>, and upgrades a stale password hash in place
/// when it does.
///
/// <para>
/// <b>The deliberate twin of <c>IDeviceAuthenticator</c>.</b> That interface answers "is this device
/// key good?" for the authentication handler that turns the answer into claims; this one answers "is
/// this password good?" for the login endpoint that will do the same. Neither mints a token, issues a
/// claim or knows what a JWT is — the store knows about rows, the handler knows about credentials, and
/// keeping them apart is what let device authentication be added without touching a table and will let
/// user authentication be added without touching one either.
/// </para>
///
/// <para>
/// <b>It is the one query in the user path that legitimately ignores the tenant filter</b>, for the
/// reason <c>DeviceAuthenticator</c> records about its own: a credential is presented <em>before</em>
/// any tenant is known, because resolving the tenant is one of the things authenticating it is for.
/// <c>UX_Users_Email</c> is globally unique with no <c>SchoolId</c> in it precisely so this lookup has
/// exactly one answer — see <c>EamsDbContext.ConfigureRbac</c>, and the test that pins it.
/// </para>
/// </summary>
public interface IUserCredentialVerifier
{
    /// <summary>
    /// Checks the pair, and on <see cref="PasswordVerification.SuccessRehashNeeded"/> rehashes the
    /// presented password and persists it before returning.
    ///
    /// <para>
    /// <b>The timing of a refusal does not depend on whether the e-mail exists.</b> An unknown address
    /// still costs one password verification against a throwaway hash, because the obvious "return
    /// early if there is no row" is a timing oracle that answers the enumeration question the response
    /// body was carefully written not to.
    /// </para>
    /// </summary>
    Task<UserCredentialCheck> VerifyAsync(
        string email, string password, CancellationToken ct = default);
}
