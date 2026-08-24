namespace EAMS.Application.Abstractions;

/// <summary>What creating a user concluded.</summary>
public enum UserProvisioningOutcome
{
    /// <summary>The user exists, holds the requested role, and an audit row records who made it.</summary>
    Created,

    /// <summary>
    /// <c>UX_Users_Email</c> already holds that address. <b>Refused, never reset</b> — see
    /// <see cref="IUserProvisioningService"/> for why a create that silently becomes a password reset
    /// is the wrong failure to be forgiving about.
    /// </summary>
    EmailInUse,

    /// <summary>A field failed a <c>Users</c> column rule or the password policy. The message names which.</summary>
    ValidationFailed,

    /// <summary>No <c>Roles</c> row by that name. The message lists the four that exist.</summary>
    UnknownRole,

    /// <summary>No school could be resolved to file the user under. The message says how to name one.</summary>
    NoSchoolResolved,
}

/// <summary>The result of provisioning one user. Ids are <see cref="Guid.Empty"/> unless it was created.</summary>
public record UserProvisioningResult(
    UserProvisioningOutcome Outcome, string Message, Guid UserId, Guid SchoolId)
{
    public static UserProvisioningResult Failed(UserProvisioningOutcome outcome, string message) =>
        new(outcome, message, Guid.Empty, Guid.Empty);
}

/// <summary>
/// Who to create and as what.
///
/// <para>
/// <b>The password is deliberately not on this record.</b> A positional record generates a
/// <c>ToString()</c> that prints every member, so a password here would land in any log line, any
/// exception message and any debugger watch that ever rendered the request — the exact leak this
/// phase's console command goes to some length to avoid at the shell. It is a separate parameter on
/// <see cref="IUserProvisioningService.CreateAsync"/>, where nothing formats it.
/// </para>
/// </summary>
/// <param name="SchoolCode">
/// Which school to file the user under. Null means "resolve it" — the pinned tenant, or the only
/// school if there is exactly one. An installation with several schools and no pin has no honest
/// default, and gets <see cref="UserProvisioningOutcome.NoSchoolResolved"/> rather than a guess.
/// </param>
public record UserProvisioningRequest(
    string Email, string FullName, string RoleName, string? SchoolCode = null);

/// <summary>
/// <b>The one way a user is created in this system.</b>
///
/// <para>
/// <b>It exists because there are two callers and they must not be two implementations.</b> The
/// Development SuperAdmin seed and the <c>create-admin</c> console command both have to normalize an
/// e-mail, apply the password policy, hash with the current parameters, refuse a duplicate address,
/// attach a role and write an audit row. Written twice, those drift — and the copy that drifts is the
/// one nobody runs, which is the Production bootstrap path, discovered on the day an installation has
/// no other way in. The same rule <c>IntegrationTest.IssueDeviceKeyAsync</c> follows by minting device
/// keys through <c>IDeviceService</c>: no caller creates a credential by a rule the production path
/// does not use.
/// </para>
///
/// <para>
/// <b>An existing address is refused rather than updated.</b> The convenient behaviour — "create or
/// reset" — is how the wrong account gets its password reset by an operator who believed they were
/// creating a new one, and it is indistinguishable in a shell from success. Resetting a password is a
/// different operation and will be a different one when it exists.
/// </para>
///
/// <para>
/// <b>There is no "refuse once any user exists" guard, and that absence is a decision.</b> Such a
/// guard buys nothing against an attacker — anyone who can invoke this already holds the binary, the
/// connection string and the database, and can write the row directly — while removing the only
/// recovery path an installation has when its sole administrator leaves. The control is visibility
/// instead: every creation writes an <c>auth.admin.created</c> audit row naming the operating-system
/// account and machine it was run from, and the command prints a warning saying so.
/// </para>
/// </summary>
public interface IUserProvisioningService
{
    /// <summary>
    /// The password policy this service enforces, exposed so a caller can state it in a prompt rather
    /// than let the operator discover it by being refused after typing a password twice.
    /// </summary>
    int MinimumPasswordLength { get; }

    /// <summary>
    /// Creates the user, grants the role and writes the audit row, or reports why it did not.
    /// </summary>
    /// <param name="password">
    /// The plaintext, hashed immediately and never stored, logged or echoed. It is a
    /// <see cref="string"/> because <c>PasswordHasher&lt;T&gt;</c> takes one; a caller that reads it
    /// into a <c>char[]</c> and clears the array afterwards is doing hygiene, not erasure — the
    /// string this parameter binds to is immutable, interned nowhere but the managed heap, and lives
    /// until the GC collects it. Say that plainly rather than implying the secret is gone.
    /// </param>
    /// <param name="actor">
    /// Who is doing this, for the audit row — <c>"user@MACHINE"</c> from the console command, or the
    /// name of the seed that called it. There is no authenticated principal at the moment either
    /// caller runs, so this is the only attribution available and it is recorded rather than left
    /// null.
    /// </param>
    Task<UserProvisioningResult> CreateAsync(
        UserProvisioningRequest request,
        string password,
        string actor,
        CancellationToken ct = default);
}
