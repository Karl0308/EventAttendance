using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// <see cref="EamsApiFactory"/>'s Development twin, and the only place in the suite that boots one.
///
/// <para>
/// Development is where the exception leak actually was: <c>WebApplication</c> installs the
/// developer exception page automatically in that environment, so an unhandled path returned a full
/// stack trace — on endpoints that are open to anyone who can reach the port (ADR-001 D-6). Whether
/// <c>UseExceptionHandler</c> intercepts before that page is a question about middleware
/// <em>ordering</em>, and ordering is not something a Production test can answer: in Production the
/// page is not installed at all, so the assertion passes for the wrong reason.
/// </para>
///
/// <para>
/// This host seeds (seeding is skipped only in Production), so tests using it must not assert on row
/// counts. <see cref="IntegrationTest.InitializeAsync"/> empties the database before each test, so
/// the seeded rows cannot escape into anything that does.
/// </para>
/// </summary>
internal sealed class DevelopmentApiFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringVariable = "ConnectionStrings__EamsDb";
    private const string EnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    private readonly string? _previousConnectionString;
    private readonly string? _previousEnvironment;

    /// <summary>
    /// The connection string has to arrive as an environment variable for the reason
    /// <see cref="EamsApiFactory"/> documents at length: <c>Program.cs</c> reads it before
    /// <c>builder.Build()</c>, so a test-registered configuration source is applied too late and the
    /// host silently falls back to the developer's real database.
    /// </summary>
    public DevelopmentApiFactory(string connectionString)
    {
        _previousConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        _previousEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);

        Environment.SetEnvironmentVariable(ConnectionStringVariable, connectionString);
        Environment.SetEnvironmentVariable(EnvironmentVariable, Environments.Development);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(DevelopmentApiFactory).Assembly);
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        Environment.SetEnvironmentVariable(ConnectionStringVariable, _previousConnectionString);
        Environment.SetEnvironmentVariable(EnvironmentVariable, _previousEnvironment);
    }
}
