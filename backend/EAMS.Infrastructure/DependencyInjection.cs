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

        // The D-29 live endpoint's poll interval, resolved here because this is the seam that already
        // holds the IConfiguration — the Application layer defines the record but takes no dependency on
        // Microsoft.Extensions.Options (Technical Plan §3). Singleton: it is immutable and read once per
        // response, so a change is a restart, which is the documented behaviour.
        services.AddSingleton(
            AttendanceLiveOptions.Resolve(configuration[AttendanceLiveOptions.ConfigurationKey]));

        services.AddScoped<IStudentService, StudentService>();
        services.AddScoped<IEventService, EventService>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<IStudentGroupProjection, StudentGroupProjection>();

        // Phase 3b-2's two read services. Scoped like the rest, because they take the request's
        // EamsDbContext — and they take nothing else: a read decides no tenant, so neither has an
        // ISchoolContext to pin. The global SchoolId query filter is what scopes them.
        services.AddScoped<IAcademicReferenceService, AcademicReferenceService>();
        services.AddScoped<IStudentGroupService, StudentGroupService>();
        services.AddScoped<ISisImportService, SisImportService>();
        services.AddScoped<IDeviceService, DeviceService>();

        // D-53's term admin surface — the one write service in the academic layer, and registered
        // separately from the reference service above rather than folded into it precisely so that
        // "which academic tables are writable" is answerable from this list. It takes an ISchoolContext
        // because a create decides which school the term is filed under; the reads above decide
        // nothing.
        services.AddScoped<ITermAdminService, TermAdminService>();

        // Resolved per request by the DeviceKey authentication handler, from the request scope — so it
        // gets the same EamsDbContext the rest of the request will use.
        services.AddScoped<IDeviceAuthenticator, DeviceAuthenticator>();

        // ------------------------------------------------------------------------- §11 login (6a)
        //
        // The data and configuration foundation for user login. None of it is reachable from the wire
        // yet — there is no /auth route and no JWT scheme — but all of it is exercised by tests, which
        // is the point: the pieces with a schema, a race and a policy land and get proven before the
        // endpoint that composes them exists.
        //
        // The hasher is a singleton: it is stateless, and its one field is a PasswordHasher<T> whose
        // construction reads options once. Making it scoped would rebuild that per request for no
        // reason. Everything below it takes the request's EamsDbContext and is therefore scoped.
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddScoped<IUserCredentialVerifier, UserCredentialVerifier>();
        services.AddScoped<IUserProvisioningService, UserProvisioningService>();
        services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations, writes the §4.11 reference data, optionally seeds dev convenience
    /// data, and pins the development tenant. Every step is idempotent, so a second start is a no-op.
    ///
    /// <para>
    /// <b>Three concerns, and until §11 they were expressed as one boolean.</b> Migration is
    /// unconditional. <b>RBAC reference data is now unconditional too</b> — the <c>Permissions</c> rows
    /// and the four <c>Roles</c> with their grants are what the authorization model is made of, and
    /// enforcement over an empty <c>RolePermissions</c> table authorizes nobody, so a Production
    /// installation that never seeded them would lock every operator out of itself and look like a
    /// broken authorization layer while doing it. Only <paramref name="seedDevelopmentData"/> — eight
    /// fictional students, a kiosk whose key is printed in source, and an administrator account — is
    /// Development's alone. Riding all three on one switch is the defect this parameter list replaces.
    /// </para>
    /// </summary>
    /// <param name="rbac">
    /// The permission codes and grant matrix, supplied by the composition root. It is passed in rather
    /// than defined here because the registry lives beside the endpoints that declare the codes
    /// (<c>EAMS.Api.Authorization</c>) and Infrastructure cannot see that assembly — see
    /// <see cref="RbacReferenceData"/>. A host with no authorization registry passes
    /// <see cref="RbacReferenceData.Empty"/>.
    /// </param>
    /// <param name="environmentName">
    /// The host's environment. Carried through so <c>SeedData.SeedDevelopmentSuperAdminAsync</c> can
    /// refuse independently of its caller's gate — a seeded administrator on a Staging or Production
    /// host is not a mistake that should need one call site to stay correct.
    /// </param>
    /// <param name="seedDevelopmentData">
    /// Development convenience data only. The caller decides, and <c>Program.cs</c> gates it on
    /// <c>IsDevelopment()</c> rather than <c>!IsProduction()</c> — see
    /// <c>SeedData.DevelopmentKioskApiKey</c> for why that distinction is load-bearing. Also passed
    /// <c>false</c> by the <c>create-admin</c> console command, which needs the schema and the roles
    /// and none of the fixtures.
    /// </param>
    public static async Task InitializeEamsDatabaseAsync(
        this IServiceProvider services,
        RbacReferenceData rbac,
        string environmentName,
        bool seedDevelopmentData,
        CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EamsDbContext>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        await db.Database.MigrateAsync(ct);

        // Reference data, every environment. Before the dev seed, because the SuperAdmin below is
        // granted a role that has to exist first.
        await RbacSeed.ApplyAsync(db, rbac, loggerFactory.CreateLogger("EAMS.Rbac"), ct);

        if (seedDevelopmentData)
        {
            await SeedData.InitializeAsync(db, ct);

            await SeedData.SeedDevelopmentSuperAdminAsync(
                db,
                scope.ServiceProvider.GetRequiredService<IUserProvisioningService>(),
                environmentName,
                scope.ServiceProvider.GetRequiredService<IConfiguration>()[
                    SeedData.DevelopmentSuperAdminPasswordKey],
                loggerFactory.CreateLogger("EAMS.Seed"),
                ct);
        }

        ReportLivePollConfiguration(scope.ServiceProvider);

        await PinDevelopmentSchoolAsync(scope.ServiceProvider, db, ct);
    }

    /// <summary>
    /// Says out loud when a configured D-29 poll interval was thrown away.
    ///
    /// <para>
    /// <c>AttendanceLiveOptions.Resolve</c> falls back to the published default for anything
    /// unparseable or out of range, which is the right behaviour — a typo in a poll interval must not
    /// stop the dashboard — and a silent one. Without this line the operator who set
    /// <c>Attendance:LivePollAfterSeconds</c> to <c>5000</c> and is still seeing five-second polls has
    /// no way to find out why except by reading source.
    /// </para>
    ///
    /// <para>
    /// Deliberately <em>not</em> the same severity argument as the D-36 tap window, which logs every
    /// candidate it could not parse: that one changes which taps are accepted and is discovered from a
    /// report weeks later. This one changes a refresh rate and is visible in seconds. One line at
    /// startup is the proportionate answer.
    /// </para>
    /// </summary>
    private static void ReportLivePollConfiguration(IServiceProvider scoped)
    {
        var live = scoped.GetRequiredService<AttendanceLiveOptions>();
        if (live.RejectedConfiguredValue is not { } rejected) return;

        scoped.GetRequiredService<ILoggerFactory>()
            .CreateLogger("EAMS.Attendance")
            .LogInformation(
                "'{Setting}' is '{Value}', which is not a whole number of seconds between {Min} and " +
                "{Max}. Live attendance polling is using the default of {Default}s until the setting " +
                "is corrected.",
                AttendanceLiveOptions.ConfigurationKey, rejected,
                AttendanceLiveOptions.MinPollAfterSeconds, AttendanceLiveOptions.MaxPollAfterSeconds,
                live.PollAfterSeconds);
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
