using EAMS.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EAMS.Infrastructure.Data;

/// <summary>
/// Design-time only — used by <c>dotnet ef</c>, never by the running application (which resolves
/// its connection string from configuration in <see cref="DependencyInjection"/>).
///
/// Having this here means migrations can be scaffolded and applied from EAMS.Infrastructure alone,
/// without booting the API host:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project backend/EAMS.Infrastructure
/// dotnet ef database update      --project backend/EAMS.Infrastructure
/// </code>
/// Override the target with the <c>EAMS_DESIGNTIME_CONNECTION</c> environment variable. The default
/// is the local developer instance; it carries no secret (Windows integrated auth).
/// </summary>
internal sealed class EamsDbContextFactory : IDesignTimeDbContextFactory<EamsDbContext>
{
    private const string EnvVar = "EAMS_DESIGNTIME_CONNECTION";

    private const string LocalDefault =
        @"Server=.\SQLEXPRESS;Database=EAMS;Trusted_Connection=True;TrustServerCertificate=True";

    public EamsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvVar) ?? LocalDefault;

        var options = new DbContextOptionsBuilder<EamsDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        // An unpinned tenant context. Design time has no request and no claims, and a migration must
        // see the whole model — the SchoolId query filters are inert while CurrentSchoolId is null.
        return new EamsDbContext(options, new DevelopmentSchoolContext());
    }
}
