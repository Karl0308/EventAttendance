using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// Boots the real <c>EAMS.Api</c> host in a named environment.
///
/// <para>
/// <b>It exists because "not Production" is not a synonym for "Development", and Phase 4b made that
/// distinction load-bearing.</b> The seed writes a device row carrying a working well-known API key,
/// so which environments seed is a security property rather than a convenience one — and with only a
/// Production factory and a Development factory in the suite, <c>Staging</c> was a hole neither could
/// see. That is exactly where the wrong predicate hid.
/// </para>
///
/// <para>
/// The connection string has to arrive as an environment variable for the reason
/// <see cref="EamsApiFactory"/> documents at length: <c>Program.cs</c> reads it before
/// <c>builder.Build()</c>, so a test-registered configuration source is applied too late and the host
/// silently falls back to the developer's real database.
/// </para>
/// </summary>
internal class EnvironmentApiFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringVariable = TestHostConfiguration.ConnectionStringVariable;
    private const string EnvironmentVariable = TestHostConfiguration.EnvironmentVariable;
    private const string SigningKeyVariable = TestHostConfiguration.SigningKeyVariable;

    private readonly string _environment;
    private readonly string? _previousConnectionString;
    private readonly string? _previousEnvironment;
    private readonly string? _previousSigningKey;

    public EnvironmentApiFactory(string connectionString, string environment)
    {
        _environment = environment;

        _previousConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        _previousEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        _previousSigningKey = Environment.GetEnvironmentVariable(SigningKeyVariable);

        Environment.SetEnvironmentVariable(ConnectionStringVariable, connectionString);
        Environment.SetEnvironmentVariable(EnvironmentVariable, environment);

        // Phase 6a: the host refuses to start without a usable Jwt:SigningKey in every environment.
        // See TestHostConfiguration.
        Environment.SetEnvironmentVariable(SigningKeyVariable, TestHostConfiguration.SigningKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(EnvironmentApiFactory).Assembly);
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        Environment.SetEnvironmentVariable(ConnectionStringVariable, _previousConnectionString);
        Environment.SetEnvironmentVariable(EnvironmentVariable, _previousEnvironment);
        Environment.SetEnvironmentVariable(SigningKeyVariable, _previousSigningKey);
    }
}
