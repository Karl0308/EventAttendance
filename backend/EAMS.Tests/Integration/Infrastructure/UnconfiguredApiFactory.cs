using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// <see cref="EamsApiFactory"/>'s opposite: the real host, in Production, with <em>nothing</em>
/// supplied for the connection string.
///
/// <para>
/// It exists to prove a negative that is otherwise untestable — that the shipped
/// <c>appsettings.json</c> carries no connection string of its own. Reading the file and asserting
/// on its contents would prove only what is on disk next to the test, not what the host actually
/// resolves through the configuration chain; booting the host and requiring it to fail proves the
/// thing that matters. The caller is responsible for clearing
/// <c>ConnectionStrings__EamsDb</c> from the environment first — this type deliberately sets no
/// variables, because setting them is exactly what it is testing the absence of.
/// </para>
/// </summary>
internal sealed class UnconfiguredApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
    }
}
