using Microsoft.Extensions.Hosting;

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
/// This host seeds — as of Phase 4b it is the <em>only</em> environment that does — so tests using it
/// must not assert on row counts. <see cref="IntegrationTest.InitializeAsync"/> empties the database
/// before each test, so the seeded rows cannot escape into anything that does.
/// </para>
///
/// <para>
/// A one-line specialization of <see cref="EnvironmentApiFactory"/>, kept under its own name because
/// "the Development host" is the thing most of these tests are actually about.
/// </para>
/// </summary>
internal sealed class DevelopmentApiFactory : EnvironmentApiFactory
{
    public DevelopmentApiFactory(string connectionString)
        : base(connectionString, Environments.Development)
    {
    }
}
