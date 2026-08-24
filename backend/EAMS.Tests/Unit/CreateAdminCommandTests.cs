using EAMS.Api;
using EAMS.Application.Abstractions;
using EAMS.Infrastructure.Identity;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <see cref="CreateAdminCommand"/>'s argument surface. The provisioning behaviour itself lives in
/// <c>UserProvisioningTests</c> against a real database; what is pinned here is the part that decides
/// what the operator is allowed to type.
/// </summary>
public class CreateAdminCommandTests
{
    private static string[] Args(params string[] rest) => ["create-admin", .. rest];

    [Fact]
    public void The_verb_is_recognised_only_as_the_first_argument()
    {
        Assert.True(CreateAdminCommand.IsRequested(["create-admin"]));
        Assert.True(CreateAdminCommand.IsRequested(["create-admin", "--email", "a@b.c"]));

        Assert.False(CreateAdminCommand.IsRequested([]));
        Assert.False(CreateAdminCommand.IsRequested(["--urls", "http://localhost:5080"]));

        // Not in a value position: an event named "create-admin" passed to some other flag must not
        // silently turn a web host start into a user creation.
        Assert.False(CreateAdminCommand.IsRequested(["--name", "create-admin"]));
    }

    [Fact]
    public void A_complete_invocation_parses()
    {
        Assert.True(CreateAdminCommand.TryParse(
            Args("--email", "Registrar@USA.edu.ph", "--name", "Ana Reyes",
                 "--role", "Organizer", "--school", "USA"),
            out var request,
            out var error));

        Assert.Equal("", error);
        Assert.Equal("Registrar@USA.edu.ph", request.Email); // normalized by the service, not here
        Assert.Equal("Ana Reyes", request.FullName);
        Assert.Equal("Organizer", request.RoleName);
        Assert.Equal("USA", request.SchoolCode);
    }

    /// <summary>
    /// <b>SchoolAdmin, not SuperAdmin.</b> The two hold identical permissions — §4.11's <c>Roles</c>
    /// table has no <c>SchoolId</c>, so a cross-school SuperAdmin is not expressible — and defaulting
    /// to the wider-sounding name would advertise a capability the schema cannot deliver. Pinned,
    /// because "the bootstrap command should obviously make a SuperAdmin" is the intuitive change.
    /// </summary>
    [Fact]
    public void The_default_role_is_SchoolAdmin()
    {
        Assert.True(CreateAdminCommand.TryParse(
            Args("--email", "a@b.c", "--name", "A B"), out var request, out _));

        Assert.Equal(EamsRoleNames.SchoolAdmin, request.RoleName);
        Assert.Null(request.SchoolCode);
    }

    /// <summary>
    /// <b>There is no <c>--password</c> flag, and the refusal names why.</b> A password on the command
    /// line lands in shell history, in the process table where any local user can read it, and
    /// verbatim in the log of every CI system that echoes what it ran — three durable copies of a
    /// secret that was meant to exist only in the operator's head, none of them under this program's
    /// control.
    ///
    /// <para>
    /// It is refused explicitly rather than falling out of "unknown option", so that an operator who
    /// reaches for it is told the reason instead of assuming they mistyped the flag name.
    /// </para>
    /// </summary>
    [Fact]
    public void A_password_argument_is_refused_with_the_reason()
    {
        Assert.False(CreateAdminCommand.TryParse(
            Args("--email", "a@b.c", "--name", "A B", "--password", "hunter2hunter2"),
            out _,
            out var error));

        Assert.Contains("shell history", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("process table", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And the value never appears in the refusal. A guard that echoed the password it was rejecting
    /// would put it in the terminal scrollback — which is the same class of leak, arrived at through
    /// the code that exists to prevent it.
    /// </summary>
    [Fact]
    public void The_password_refusal_does_not_echo_the_password()
    {
        const string secret = "SuperSecretValue123";

        CreateAdminCommand.TryParse(
            Args("--email", "a@b.c", "--name", "A B", "--password", secret), out _, out var error);

        Assert.DoesNotContain(secret, error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_email_is_required()
    {
        Assert.False(CreateAdminCommand.TryParse(Args("--name", "A B"), out _, out var error));
        Assert.Contains("--email", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_is_required()
    {
        Assert.False(CreateAdminCommand.TryParse(Args("--email", "a@b.c"), out _, out var error));
        Assert.Contains("--name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_option_is_refused_by_name()
    {
        Assert.False(CreateAdminCommand.TryParse(
            Args("--email", "a@b.c", "--name", "A B", "--admin", "yes"), out _, out var error));

        Assert.Contains("--admin", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flag_with_no_value_is_refused_rather_than_swallowing_the_next_flag()
    {
        Assert.False(CreateAdminCommand.TryParse(
            Args("--email", "a@b.c", "--name"), out _, out var error));

        Assert.Contains("--name", error, StringComparison.Ordinal);
        Assert.Contains("expects a value", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown role is caught here, before the operator is asked to type a password twice. The
    /// service checks it against the database as well — that is the check that counts — but being
    /// refused after entering a password is a worse experience than being refused before.
    /// </summary>
    [Fact]
    public void An_unknown_role_is_refused_and_the_known_ones_are_listed()
    {
        Assert.False(CreateAdminCommand.TryParse(
            Args("--email", "a@b.c", "--name", "A B", "--role", "Administrator"),
            out _,
            out var error));

        Assert.Contains("Administrator", error, StringComparison.Ordinal);
        foreach (var known in EamsRoleNames.All)
            Assert.Contains(known, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Role names are matched case-insensitively at the command line and stored in their canonical
    /// casing. <c>Roles.Name</c> is compared ordinally by everything downstream, so accepting
    /// <c>organizer</c> and then writing <c>organizer</c> would create a user holding a role nothing
    /// else recognises.
    /// </summary>
    [Fact]
    public void A_role_typed_in_the_wrong_case_is_normalized_to_the_canonical_name()
    {
        Assert.True(CreateAdminCommand.TryParse(
            Args("--email", "a@b.c", "--name", "A B", "--role", "vIeWeR"), out var request, out _));

        Assert.Equal(EamsRoleNames.Viewer, request.RoleName);
    }

    /// <summary>
    /// The command's copy of the audit action must equal the one the service actually writes. The
    /// duplication is deliberate — <c>UserProvisioningService</c> is internal to
    /// <c>EAMS.Infrastructure</c> and the layering rule is worth more than sharing one string — so
    /// this is what stops the copy rotting into a message that names an action nobody can search for.
    /// </summary>
    [Fact]
    public void The_printed_audit_action_matches_the_one_the_service_writes()
    {
        Assert.Equal(UserProvisioningService.AuditAction, CreateAdminCommand.AuditActionName);
    }

    /// <summary>The usage text has to name the things an operator will otherwise guess at.</summary>
    [Fact]
    public void The_usage_text_states_that_the_password_is_never_an_argument()
    {
        Assert.Contains("NEVER passed as an argument", CreateAdminCommand.Usage, StringComparison.Ordinal);
        Assert.Contains(CreateAdminCommand.PasswordEnvironmentVariable, CreateAdminCommand.Usage, StringComparison.Ordinal);

        foreach (var role in EamsRoleNames.All)
            Assert.Contains(role, CreateAdminCommand.Usage, StringComparison.Ordinal);
    }
}
