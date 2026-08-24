using EAMS.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EAMS.Api;

/// <summary>
/// <c>dotnet run -- create-admin</c> — <b>the only supported way a Production installation gets its
/// first user.</b>
///
/// <para>
/// <b>The gap it closes.</b> Everything else about §11 assumes somebody is already signed in: roles
/// are assigned through an authenticated surface, and the Development SuperAdmin seed is
/// Development-only by construction. A fresh production database therefore has four roles, twelve
/// permissions and nobody who can log in — and the documented alternative would be hand-written SQL
/// against <c>dbo.Users</c>, which cannot hash a password, cannot attach a role and cannot leave an
/// audit trail. That is the same failure mode the project already recorded for <c>dbo.Terms</c> before
/// D-53 gave terms a real write surface.
/// </para>
///
/// <para>
/// <b>It creates nothing itself.</b> The row goes through <see cref="IUserProvisioningService"/>, the
/// same service the Development seed calls. Two implementations of "create a user, hash a password,
/// grant a role, refuse a duplicate, write an audit row" would drift, and the copy that drifts is the
/// one nobody runs — this one, on the day an installation has no other way in.
/// </para>
///
/// <para>
/// <b>It does not start the web host and opens no port.</b> The host is <em>built</em>, because that
/// is what composes the container and the configuration chain, and then this returns an exit code
/// instead of reaching <c>app.Run()</c>. Nothing binds a socket before <c>Run</c>.
/// </para>
/// </summary>
internal static class CreateAdminCommand
{
    /// <summary>The verb, as the first argument: <c>dotnet run -- create-admin …</c>.</summary>
    public const string Verb = "create-admin";

    /// <summary>
    /// The one environment variable this command will take a password from, for an unattended
    /// install. <b>Warned about when used</b> — an environment variable is visible in the process
    /// table on some systems, is inherited by every child process, and is routinely captured whole
    /// into CI logs and crash dumps. It is the least-bad automation route, not a good one.
    /// </summary>
    public const string PasswordEnvironmentVariable = "EAMS_BOOTSTRAP_PASSWORD";

    /// <summary>Whether this invocation is the command rather than the web host.</summary>
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase);

    /// <summary>Exit codes, so a deployment script can branch on the reason rather than on stderr text.</summary>
    public static class ExitCodes
    {
        public const int Success = 0;
        public const int UsageError = 1;
        public const int Refused = 2;
        public const int NoPassword = 3;
    }

    /// <summary>
    /// Parses the arguments, obtains the password, and provisions the user.
    /// </summary>
    /// <param name="input">
    /// Where a piped password is read from. Null means "read the console", which is the interactive
    /// path.
    /// </param>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string[] args,
        TextWriter output,
        TextReader? input = null,
        CancellationToken ct = default)
    {
        if (!TryParse(args, out var request, out var parseError))
        {
            output.WriteLine(parseError);
            output.WriteLine();
            output.WriteLine(Usage);
            return ExitCodes.UsageError;
        }

        using var scope = services.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioningService>();

        var password = ReadPassword(output, input, provisioning.MinimumPasswordLength);
        if (password is null) return ExitCodes.NoPassword;

        var result = await provisioning.CreateAsync(
            request,
            password,
            actor: $"{Environment.UserName}@{Environment.MachineName}",
            ct);

        if (result.Outcome != UserProvisioningOutcome.Created)
        {
            output.WriteLine($"Refused ({result.Outcome}): {result.Message}");
            return ExitCodes.Refused;
        }

        output.WriteLine(result.Message);

        // The visibility that stands in for a "first user only" guard. There deliberately is no such
        // guard — it would buy nothing against anyone who can already run this binary against this
        // database, while removing the only recovery path an installation has when its sole
        // administrator leaves. See IUserProvisioningService.
        output.WriteLine(
            $"An audit row was written (Action '{AuditActionName}') recording " +
            $"{Environment.UserName}@{Environment.MachineName}. This command can be run again at any " +
            "time and is not restricted to the first user — the audit trail, not a guard, is what " +
            "makes an unexpected administrator visible.");

        return ExitCodes.Success;
    }

    /// <summary>
    /// The audit action, restated here for the message above. It is
    /// <c>UserProvisioningService.AuditAction</c>, which lives in Infrastructure and is not visible
    /// from this assembly by design — the layering rule is worth more than sharing one string, and
    /// <c>CreateAdminCommandTests</c> asserts the two agree so the copy cannot rot.
    /// </summary>
    internal const string AuditActionName = "auth.admin.created";

    internal const string Usage = """
        Usage:
          dotnet run -- create-admin --email <address> --name "<full name>" [--role <role>] [--school <code>]

        Options:
          --email    Required. The login identifier. Must not already exist.
          --name     Required. The person's full name.
          --role     Optional. SuperAdmin | SchoolAdmin | Organizer | Viewer. Default: SchoolAdmin.
          --school   Optional. A school Code. Required only when the database holds several schools.

        The password is NEVER passed as an argument. It is prompted for (hidden, entered twice), or
        read from standard input when that is redirected, or taken from EAMS_BOOTSTRAP_PASSWORD.
        """;

    /// <summary>
    /// <b>There is no <c>--password</c> flag and there will not be one.</b> A password on the command
    /// line lands in the shell's history file, in the process table where any local user can read it
    /// with <c>ps</c> or Task Manager, and verbatim in the log of every CI system that echoes the
    /// command it ran. Those are three separate durable copies of a credential that was supposed to
    /// exist only in the operator's head, and none of them is under this program's control.
    /// </summary>
    internal static bool TryParse(string[] args, out UserProvisioningRequest request, out string error)
    {
        request = new UserProvisioningRequest("", "", EamsRoleNames.SchoolAdmin);
        error = "";

        string? email = null, name = null, school = null;
        // SchoolAdmin, not SuperAdmin. The two hold identical permissions — §4.11's Roles table has no
        // SchoolId, so a cross-school SuperAdmin is not expressible — and defaulting to the
        // wider-sounding name would advertise a capability the schema cannot deliver.
        var role = EamsRoleNames.SchoolAdmin;

        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i];

            if (string.Equals(flag, "--password", StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "--password is not accepted. A password given on the command line is written to " +
                    "shell history, is visible in the process table, and is captured by CI logs. It " +
                    "will be prompted for instead.";
                return false;
            }

            if (i + 1 >= args.Length)
            {
                error = $"'{flag}' expects a value.";
                return false;
            }

            var value = args[++i];

            switch (flag.ToLowerInvariant())
            {
                case "--email": email = value; break;
                case "--name": name = value; break;
                case "--role": role = value; break;
                case "--school": school = value; break;
                default:
                    error = $"Unknown option '{flag}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            error = "--email is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "--name is required.";
            return false;
        }

        // Matched here as well as in the service, so a typo'd role is refused before the operator is
        // asked to type a password twice. The service checks it against the database, which is the
        // check that counts; this one is courtesy.
        var matched = EamsRoleNames.All.FirstOrDefault(
            r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

        if (matched is null)
        {
            error = $"Unknown role '{role}'. Known roles: {string.Join(", ", EamsRoleNames.All)}.";
            return false;
        }

        request = new UserProvisioningRequest(email, name, matched, school);
        return true;
    }

    /// <summary>
    /// Obtains the password, in the order least-bad first, and returns null if there is none.
    ///
    /// <para>
    /// <b>On clearing the characters afterwards: this does not do it, and saying so is more honest
    /// than a gesture.</b> <c>PasswordHasher&lt;T&gt;.HashPassword</c> takes a <see cref="string"/>,
    /// so the plaintext exists as an immutable managed string no matter what this method reads it
    /// into. Reading into a <c>char[]</c> and zeroing it would clear one copy while the string handed
    /// to the hasher — and every intermediate the runtime made — stays on the heap until the GC
    /// collects it, and possibly in a page file after that. It would be hygiene, not erasure, and
    /// writing it here would imply a guarantee this process cannot make. The real mitigations are the
    /// ones above: the secret is never an argument, never echoed, and never logged.
    /// </para>
    /// </summary>
    private static string? ReadPassword(TextWriter output, TextReader? input, int minimumLength)
    {
        if (Environment.GetEnvironmentVariable(PasswordEnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            output.WriteLine(
                $"Using the password from {PasswordEnvironmentVariable}. Note that an environment " +
                "variable is inherited by child processes and is commonly captured whole into CI " +
                "logs and crash dumps — clear it after this run.");
            return fromEnv;
        }

        // A redirected stream: a deployment feeding the secret from a vault or a protected file. Read
        // one line and nothing more, so a file with trailing content cannot change the password.
        if (input is not null || Console.IsInputRedirected)
        {
            var reader = input ?? Console.In;
            var piped = reader.ReadLine();

            if (string.IsNullOrEmpty(piped))
            {
                output.WriteLine(
                    "No password was supplied on standard input. Pipe one from a secret store, or " +
                    "run this interactively to be prompted.");
                return null;
            }

            return piped;
        }

        output.WriteLine($"Password (at least {minimumLength} characters, not echoed):");
        var first = ReadHidden();

        output.WriteLine("Confirm:");
        var second = ReadHidden();

        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            output.WriteLine("The two entries did not match. Nothing was created.");
            return null;
        }

        if (first.Length == 0)
        {
            output.WriteLine("No password was entered. Nothing was created.");
            return null;
        }

        return first;
    }

    /// <summary>
    /// Reads a line from the console without echoing it.
    ///
    /// <para>
    /// Backspace is handled because a prompt that cannot be corrected is a prompt an operator gets
    /// wrong twice and then works around — commonly by reaching for the environment variable, which is
    /// the worse route. Nothing is written to the screen for any keystroke, so the length does not
    /// leak to a shoulder either.
    /// </para>
    /// </summary>
    private static string ReadHidden()
    {
        var builder = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter) break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0) builder.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar)) builder.Append(key.KeyChar);
        }

        return builder.ToString();
    }
}
