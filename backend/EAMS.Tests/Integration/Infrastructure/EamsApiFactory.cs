using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// Boots the real <c>EAMS.Api</c> host — the whole pipeline, real controllers, real
/// <c>AddEamsInfrastructure</c> — against the test database.
///
/// <para>
/// <b>Nothing is substituted except the connection string.</b> The UTC bug this suite guards
/// produced two <em>different</em> serializations of one field depending on whether the value came
/// from a tracked entity or a fresh read, so the only assertion worth making is on the bytes the
/// HTTP layer actually emits. A faked serializer, a stubbed service, or an in-memory provider would
/// each have hidden it.
/// </para>
/// </summary>
internal sealed class EamsApiFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringVariable = "ConnectionStrings__EamsDb";
    private const string EnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    private readonly string? _previousConnectionString;
    private readonly string? _previousEnvironment;

    /// <summary>
    /// <b>The override has to be an environment variable, and that is not a shortcut.</b> Under
    /// minimal hosting <c>Program.cs</c> calls <c>AddEamsInfrastructure(builder.Configuration)</c>
    /// <em>before</em> <c>builder.Build()</c>, so the connection string is read while the host is
    /// still being described — and every <c>IWebHostBuilder.ConfigureAppConfiguration</c> callback a
    /// test factory registers is replayed later, during <c>Build()</c>. An in-memory source added
    /// there arrives after the value has already been read and is simply ignored.
    ///
    /// <para>
    /// That failure is silent and dangerous rather than merely inconvenient: the host falls back to
    /// <c>appsettings.json</c> and connects to the developer's real <c>.\SQLEXPRESS</c> <c>EAMS</c>
    /// database. It was caught here only because a card lookup returned a seeded student instead of
    /// the one the test had just written — an HTTP suite that happened to assert less would have
    /// been quietly reading, and writing, live data. Environment variables are part of the default
    /// configuration chain and are added <em>after</em> the JSON files, so they win, and they are in
    /// place before any of <c>Program.cs</c> runs.
    /// </para>
    ///
    /// <para>
    /// Process-wide mutation is safe here and only here: the integration tests share one collection
    /// and therefore run serially, every factory in a run is given the same connection string, and
    /// both variables are restored on dispose.
    /// </para>
    /// </summary>
    public EamsApiFactory(string connectionString)
    {
        _previousConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        _previousEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);

        Environment.SetEnvironmentVariable(ConnectionStringVariable, connectionString);

        // Production, so Program.cs skips dev seeding: these tests own every row they assert on and
        // eight seeded students appearing behind them would make counts meaningless. Migration and
        // tenant pinning still run — they are not environment-gated — so the host still exercises
        // its real startup path.
        Environment.SetEnvironmentVariable(EnvironmentVariable, Environments.Production);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);

        builder.ConfigureTestServices(services =>
        {
            // Registers this assembly's PermissionProbeController. Test-only, and reachable only
            // through this factory — EAMS.Api's own routes are untouched.
            services.AddControllers().AddApplicationPart(typeof(EamsApiFactory).Assembly);

            // The host logs an authorization warning and a query-filter warning on every start by
            // design. Useful in a terminal, noise in a test run.
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
