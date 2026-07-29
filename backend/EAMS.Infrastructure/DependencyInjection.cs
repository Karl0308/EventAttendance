using EAMS.Application.Abstractions;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Identity;
using EAMS.Infrastructure.MultiTenancy;
using EAMS.Infrastructure.Services;
using EAMS.Infrastructure.Sis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure;

/// <summary>
/// The single composition seam into Infrastructure. Everything else in this assembly is internal,
/// so no other project can reach <c>EamsDbContext</c> or a service implementation — the layering
/// rule (Technical Plan §3) is a compile error, not a convention.
/// </summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "EamsDb";

    public static IServiceCollection AddEamsInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. " +
                "Set ConnectionStrings:" + ConnectionStringName + " in appsettings.json or the environment.");

        services.AddDbContext<EamsDbContext>(o => o.UseSqlServer(connectionString, sql =>
        {
            // Transient SQL Server faults (failover, throttling) retry; everything else fails loud.
            sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
            sql.CommandTimeout((int)TimeSpan.FromSeconds(30).TotalSeconds);
        }));

        // ADR-001 D-6 tenant seam. Registered as the concrete type as well so database
        // initialization can pin it without the interface having to expose a setter — and so that
        // Phase 6 replacing the ISchoolContext registration makes the pinning step visibly dead
        // code (GetService<DevelopmentSchoolContext>() returns null) rather than quietly wrong.
        services.AddSingleton<DevelopmentSchoolContext>();
        services.AddSingleton<IPinnedSchoolContext>(sp => sp.GetRequiredService<DevelopmentSchoolContext>());

        // The default ISchoolContext for any host that is not the API — a migration, a console tool,
        // a test that builds the container directly. EAMS.Api *replaces* this registration with the
        // claims-reading ClaimsSchoolContext (Phase 4a design, D-23), which falls back to
        // IPinnedSchoolContext above whenever a request carries no credentials. Registering the pin
        // here rather than only in the web host is what keeps `new ServiceCollection()
        // .AddEamsInfrastructure(...)` resolvable, which is the gap CompositionRootTests exists for.
        services.AddSingleton<ISchoolContext>(sp => sp.GetRequiredService<DevelopmentSchoolContext>());

        // The identity seam, twinned with the tenant seam above (ADR-001 D-6 deferred auth; this is
        // the half of it that cannot be retrofitted, because a row written today with a null
        // RecordedByUserId can never be attributed later). Singleton because the implementation is
        // stateless and constant; Phase 6's claims-reading replacement becomes scoped, and that is a
        // one-line change here rather than anywhere on a write path.
        services.AddSingleton<ICurrentUser, UnauthenticatedCurrentUser>();

        // The device seam, the third of the family (Phase 4a design, D-26). Same shape and the same
        // replacement path: EAMS.Api registers a scoped, claims-reading implementation over this one.
        services.AddSingleton<IDeviceContext, UnauthenticatedDeviceContext>();

        // The services in this assembly take an ILogger<T>, so the seam has to guarantee one exists
        // rather than assume its caller happened to add logging. A web host always has — which is
        // exactly why this is easy to get wrong: production would have worked and only a bare
        // `new ServiceCollection().AddEamsInfrastructure(...)` would fail, which is a test, a
        // background worker, or a console tool. AddLogging is built entirely from TryAdd, so where
        // the host has already configured providers this changes nothing at all.
        services.AddLogging();

        services.AddScoped<IStudentService, StudentService>();
        services.AddScoped<IEventService, EventService>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<IStudentGroupProjection, StudentGroupProjection>();
        services.AddScoped<ISisImportService, SisImportService>();
        services.AddScoped<IDeviceService, DeviceService>();

        // Resolved per request by the DeviceKey authentication handler, from the request scope — so it
        // gets the same EamsDbContext the rest of the request will use.
        services.AddScoped<IDeviceAuthenticator, DeviceAuthenticator>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations, seeds dev convenience data, and pins the development tenant.
    /// Called from the API composition root; every step is idempotent, so a second start is a no-op.
    /// </summary>
    public static async Task InitializeEamsDatabaseAsync(
        this IServiceProvider services, bool seed, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EamsDbContext>();
        await db.Database.MigrateAsync(ct);
        if (seed) await SeedData.InitializeAsync(db, ct);

        await PinDevelopmentSchoolAsync(scope.ServiceProvider, db, ct);
    }

    /// <summary>
    /// Resolves the tenant for the pre-auth build (ADR-001 D-6) once, here, rather than on demand
    /// from inside the query filter — the filter is evaluated synchronously and a lazy resolver
    /// would mean either sync-over-async or a second connection on the hot path.
    ///
    /// <para>
    /// The logging is the point as much as the pinning. A global query filter that silently drops
    /// rows is a miserable thing to debug, so each of the three possible states says out loud what
    /// it is doing to every subsequent query.
    /// </para>
    /// </summary>
    private static async Task PinDevelopmentSchoolAsync(
        IServiceProvider scoped, EamsDbContext db, CancellationToken ct)
    {
        // Absent once Phase 6 registers a claims-based ISchoolContext — nothing to pin then.
        var development = scoped.GetService<DevelopmentSchoolContext>();
        if (development is null) return;

        var logger = scoped.GetRequiredService<ILoggerFactory>().CreateLogger("EAMS.MultiTenancy");

        // Schools carries no SchoolId and is not filtered, so this read is tenant-agnostic.
        var schools = await db.Schools
            .OrderBy(s => s.Code)
            .Select(s => new { s.Id, s.Code })
            .ToListAsync(ct);

        if (schools.Count == 0)
        {
            development.Pin(null);
            logger.LogWarning(
                "SchoolId query filter is INACTIVE: no school rows exist, so no tenant could be " +
                "resolved and every tenant-scoped query runs unfiltered (ADR-001 D-6).");
            return;
        }

        var pinned = schools[0];
        development.Pin(pinned.Id);

        if (schools.Count == 1)
        {
            logger.LogWarning(
                "SchoolId query filter is ACTIVE and pinned to the only school '{Code}' ({SchoolId}) " +
                "— development stand-in for §11's claims-based tenant (ADR-001 D-6). Rows belonging " +
                "to any other school would be hidden from every query.",
                pinned.Code, pinned.Id);
            return;
        }

        logger.LogWarning(
            "SchoolId query filter is ACTIVE and pinned to '{Code}' ({SchoolId}), but {Count} schools " +
            "exist. Data for the other {HiddenCount} IS HIDDEN from every tenant-scoped query in this " +
            "process. There are no claims to resolve a tenant from yet (ADR-001 D-6); the pin is the " +
            "lowest school Code.",
            pinned.Code, pinned.Id, schools.Count, schools.Count - 1);
    }
}
