using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// The real host in Production, with a working connection string and a <b>caller-chosen</b>
/// <c>Jwt:SigningKey</c> — including no key at all.
///
/// <para>
/// <b>It is the twin of <see cref="UnconfiguredApiFactory"/> and it exists for the same reason.</b>
/// Proving that a host refuses to start on a bad signing key cannot be done by reading
/// <c>appsettings.json</c> or by calling <c>JwtOptions.Resolve</c> directly: the first proves only
/// what is on disk next to the test, and the second proves the validator works without proving the
/// composition root ever calls it. Booting the host and requiring it to fail proves the thing that
/// matters, which is that the guard is <em>armed</em>.
/// </para>
///
/// <para>
/// The connection string is supplied deliberately. Without it the host would throw on
/// <c>ConnectionStrings:EamsDb</c> first — <c>AddEamsInfrastructure</c> runs before the JWT
/// resolution, on purpose — and the test would pass for entirely the wrong reason.
/// </para>
/// </summary>
internal sealed class JwtKeyApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _previousConnectionString;
    private readonly string? _previousEnvironment;
    private readonly string? _previousSigningKey;

    /// <param name="signingKey">
    /// The value to place in <c>Jwt__SigningKey</c>. <c>null</c> clears it, which is the
    /// "not configured at all" case.
    /// </param>
    public JwtKeyApiFactory(string connectionString, string? signingKey)
    {
        _previousConnectionString =
            Environment.GetEnvironmentVariable(TestHostConfiguration.ConnectionStringVariable);
        _previousEnvironment =
            Environment.GetEnvironmentVariable(TestHostConfiguration.EnvironmentVariable);
        _previousSigningKey =
            Environment.GetEnvironmentVariable(TestHostConfiguration.SigningKeyVariable);

        Environment.SetEnvironmentVariable(
            TestHostConfiguration.ConnectionStringVariable, connectionString);
        Environment.SetEnvironmentVariable(
            TestHostConfiguration.EnvironmentVariable, Environments.Production);
        Environment.SetEnvironmentVariable(
            TestHostConfiguration.SigningKeyVariable, signingKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        Environment.SetEnvironmentVariable(
            TestHostConfiguration.ConnectionStringVariable, _previousConnectionString);
        Environment.SetEnvironmentVariable(
            TestHostConfiguration.EnvironmentVariable, _previousEnvironment);
        Environment.SetEnvironmentVariable(
            TestHostConfiguration.SigningKeyVariable, _previousSigningKey);
    }
}
