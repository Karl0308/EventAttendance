using EAMS.Application.Abstractions;
using EAMS.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <c>AddEamsInfrastructure</c> is the only public type in EAMS.Infrastructure, which makes it the
/// only thing that can go wrong in a way the compiler cannot see. The integration suite constructs
/// the services directly (so it can control the tenant and keep the concurrency test free of a
/// retrying execution strategy), and that choice would leave the registrations themselves unproven
/// — a service dropped from the container fails at the first HTTP request in Development, not at
/// build time. This covers that gap without needing a database: resolving a
/// <c>DbContext</c> does not open a connection.
/// </summary>
public class CompositionRootTests
{
    private const string DummyConnectionString =
        @"Server=(local)\NOWHERE;Database=EAMS_Unresolvable;Trusted_Connection=True;TrustServerCertificate=True";

    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return new ServiceCollection().AddEamsInfrastructure(configuration).BuildServiceProvider();
    }

    private static ServiceProvider BuildWithConnectionString() =>
        Build(($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", DummyConnectionString));

    [Fact]
    public void Every_application_service_resolves()
    {
        using var provider = BuildWithConnectionString();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IStudentService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEventService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAttendanceService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISchoolContext>());
    }

    /// <summary>
    /// The tenant seam is a singleton and the services are scoped, so every request in a process
    /// sees the same pinned school. If <c>ISchoolContext</c> were registered scoped, the pin applied
    /// once at startup would be invisible to every later request and the query filter would silently
    /// go inert — the failure ADR-001 D-6 installed it early to avoid.
    /// </summary>
    [Fact]
    public void The_tenant_context_is_a_singleton_shared_across_scopes()
    {
        using var provider = BuildWithConnectionString();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ISchoolContext>(),
            second.ServiceProvider.GetRequiredService<ISchoolContext>());
    }

    [Fact]
    public void The_services_are_scoped_so_each_request_gets_its_own_context()
    {
        using var provider = BuildWithConnectionString();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IAttendanceService>(),
            second.ServiceProvider.GetRequiredService<IAttendanceService>());
    }

    /// <summary>
    /// A missing connection string must fail at composition with a message naming the setting — not
    /// later, at the first query, as a connection error that says nothing about what to configure.
    /// </summary>
    [Fact]
    public void A_missing_connection_string_fails_loudly_at_registration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build());

        Assert.Contains(DependencyInjection.ConnectionStringName, ex.Message);
    }
}
