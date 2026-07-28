using EAMS.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// Owns the SQL Server the integration suite runs against, and the one database it creates on it.
///
/// <para>
/// <b>Why a real SQL Server and not EF InMemory.</b> Every behaviour this suite exists to protect is
/// a property of SQL Server rather than of EF: filtered unique indexes, NULL-equals-NULL inside a
/// unique index, <c>datetime2</c> losing <see cref="DateTimeKind"/>, and the unique-violation races
/// the tap flow recovers from. InMemory enforces no index and raises no 2601, so those tests would
/// all pass without exercising anything — a suite that reports confidence it has not earned is
/// worse than no suite, because it also stops anyone writing the real one.
/// </para>
///
/// <para>
/// <b>Where the server comes from,</b> in order — the choice is announced on the console, never
/// silent, because "which server did that run against?" is the first question when an integration
/// failure looks impossible:
/// <list type="number">
///   <item><c>EAMS_TEST_SQL</c> — an explicit server connection string. Always wins.</item>
///   <item>Testcontainers. The default, and the only path CI takes.</item>
///   <item>A local <c>.\SQLEXPRESS</c>, so the suite still runs on a developer box with Docker
///   stopped. <b>Never used when <c>CI</c> is set</b> — a build agent that cannot reach Docker must
///   fail, not quietly test something else.</item>
/// </list>
/// </para>
///
/// <para>
/// Whichever server is chosen, the suite creates its <em>own</em> run-scoped database
/// (<c>EAMS_Test_&lt;guid&gt;</c>) and drops it afterwards. It never opens the developer's <c>EAMS</c>
/// database, so the leftover manual-verification rows there cannot influence a result.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>
    /// Named explicitly rather than left to the Testcontainers module default. Testcontainers 4.10
    /// has already obsoleted the parameterless <c>MsSqlBuilder</c> and will remove it, precisely
    /// because an implicit default image moves between package versions — and "the schema tests
    /// started failing" is a terrible way to discover the server changed underneath them.
    /// </summary>
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-latest";

    private const string ServerOverrideVariable = "EAMS_TEST_SQL";

    private const string LocalExpressServer =
        @"Server=.\SQLEXPRESS;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"EAMS_Test_{Guid.NewGuid():N}";

    private MsSqlContainer? _container;
    private string _serverConnectionString = "";

    /// <summary>Connection string for the run-scoped database. Every test connects through this.</summary>
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _serverConnectionString = await ResolveServerAsync();

        ConnectionString = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = _databaseName,
            TrustServerCertificate = true,
        }.ConnectionString;

        await CreateDatabaseAsync();
        await MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        // A container takes the database with it. A borrowed server does not, so drop what we made
        // — a developer's instance must not accumulate a database per test run.
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        try
        {
            await ExecuteOnServerAsync(
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{_databaseName}];");
        }
        catch (SqlException ex)
        {
            // Reported, not swallowed: a leaked test database is harmless but must be visible, and
            // throwing from teardown would mask whatever the tests actually found.
            Console.WriteLine(
                $"[EAMS.Tests] Could not drop test database {_databaseName}: {ex.Message}. " +
                "Drop it manually if it accumulates.");
        }
    }

    /// <summary>
    /// Returns the database to empty between tests. Chosen over the two alternatives deliberately:
    /// a database per test costs a full 19-table migration each time, and a transaction rolled back
    /// per test cannot be used here at all — the tap flow's concurrency test needs several
    /// connections racing each other, which an ambient transaction would serialize into a deadlock
    /// and, worse, would hide the very race the test exists to prove is handled.
    ///
    /// <para>
    /// Constraints are disabled for the delete rather than the tables being ordered by dependency:
    /// every foreign key in this model is <c>DeleteBehavior.Restrict</c>, so a fixed order would be
    /// correct only until the next table is added. This stays correct by construction. Unique
    /// indexes are unaffected by <c>NOCHECK</c>, so nothing here can mask an index the tests assert
    /// on, and <c>WITH CHECK CHECK</c> re-arms the constraints — including
    /// <c>CK_EventGroups_GroupOrStudent</c>, which one test then deliberately violates.
    /// </para>
    /// </summary>
    public async Task ResetAsync()
    {
        const string sql = """
            DECLARE @disable NVARCHAR(MAX) = N'';
            DECLARE @delete  NVARCHAR(MAX) = N'';
            DECLARE @enable  NVARCHAR(MAX) = N'';

            SELECT
                @disable += N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N' NOCHECK CONSTRAINT ALL;',
                @delete  += N'DELETE FROM '  + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N';',
                @enable  += N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N' WITH CHECK CHECK CONSTRAINT ALL;'
            FROM sys.tables AS t
            WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory';

            EXEC sp_executesql @disable;
            EXEC sp_executesql @delete;
            EXEC sp_executesql @enable;
            """;

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A context on the run-scoped database. Constructed directly rather than resolved from
    /// <c>AddEamsInfrastructure</c> so tests control the tenant and so no execution strategy retries
    /// the concurrency test's deliberate unique violations. <see cref="CompositionRootTests"/>
    /// covers the DI path separately, so nothing is left unproven by that choice.
    /// </summary>
    internal EamsDbContext NewDbContext(EAMS.Application.Abstractions.ISchoolContext school) =>
        new(new DbContextOptionsBuilder<EamsDbContext>().UseSqlServer(ConnectionString).Options, school);

    /// <summary>
    /// An extra, empty database on the same server, for the one test that must control which
    /// migrations have been applied.
    ///
    /// <para>
    /// It cannot use <see cref="ConnectionString"/>: that database is migrated to the head revision in
    /// <see cref="InitializeAsync"/>, and a test that proves "migration #2 applies cleanly on top of
    /// #1 with data already in it" has to start from #1 and no further. Rolling the shared database
    /// back would leave every other test in the collection racing an incompatible schema.
    /// </para>
    ///
    /// <para>
    /// The caller drops it through <see cref="DropScratchDatabaseAsync"/>. On the container path the
    /// container takes it anyway; on a borrowed developer instance it must not accumulate.
    /// </para>
    /// </summary>
    internal async Task<string> CreateScratchDatabaseAsync(string name)
    {
        await ExecuteOnServerAsync($"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}];");

        return new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = name,
            TrustServerCertificate = true,
        }.ConnectionString;
    }

    internal async Task DropScratchDatabaseAsync(string name)
    {
        try
        {
            await ExecuteOnServerAsync(
                $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
        catch (SqlException ex)
        {
            // Reported, never swallowed, and never rethrown from a cleanup path — a leaked scratch
            // database is harmless, but masking the assertion that ran before it would not be.
            Console.WriteLine($"[EAMS.Tests] Could not drop scratch database {name}: {ex.Message}.");
        }
    }

    /// <summary>A context on an arbitrary database on this server. Used only by the migration test.</summary>
    internal static EamsDbContext NewDbContextOn(
        string connectionString, EAMS.Application.Abstractions.ISchoolContext school) =>
        new(new DbContextOptionsBuilder<EamsDbContext>().UseSqlServer(connectionString).Options, school);

    private async Task<string> ResolveServerAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ServerOverrideVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Console.WriteLine($"[EAMS.Tests] SQL Server from {ServerOverrideVariable}.");
            return configured;
        }

        var isContinuousIntegration =
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

        try
        {
            _container = new MsSqlBuilder(SqlServerImage).Build();
            await _container.StartAsync();
            Console.WriteLine($"[EAMS.Tests] SQL Server from Testcontainers ({SqlServerImage}).");
            return _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            _container = null;

            if (isContinuousIntegration)
                throw new InvalidOperationException(
                    "Docker is required to run the EAMS integration suite on CI and was not " +
                    "reachable. The local SQL Server fallback is disabled here on purpose: a build " +
                    "that silently tests a different server proves nothing. Ensure the runner has " +
                    "Docker, or set " + ServerOverrideVariable + " to a reachable SQL Server.", ex);

            Console.WriteLine(
                $"[EAMS.Tests] Testcontainers unavailable ({ex.GetType().Name}: {ex.Message}). " +
                @"Falling back to local .\SQLEXPRESS.");
        }

        try
        {
            await using var probe = new SqlConnection(LocalExpressServer);
            await probe.OpenAsync();
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                "No SQL Server is available for the integration suite. Start Docker Desktop, or " +
                "start a local SQL Server, or point " + ServerOverrideVariable + " at one. These " +
                "tests are not skipped when the server is missing — the behaviours they cover " +
                "(filtered unique indexes, NULL equality in a unique index, unique-violation " +
                "races) exist only on real SQL Server, so a skipped run has no signal at all.", ex);
        }

        Console.WriteLine(@"[EAMS.Tests] SQL Server from local .\SQLEXPRESS.");
        return LocalExpressServer;
    }

    private async Task CreateDatabaseAsync() =>
        await ExecuteOnServerAsync(
            $"IF DB_ID(N'{_databaseName}') IS NULL CREATE DATABASE [{_databaseName}];");

    private async Task MigrateAsync()
    {
        // The migration is applied, not EnsureCreated. The schema tests assert on index filters and
        // check constraints, and the thing that has to be right is the migration that ships — not a
        // schema regenerated from the model, which would pass even if the two had drifted apart.
        await using var db = NewDbContext(new TestSchoolContext());
        await db.Database.MigrateAsync();
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        var master = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = "master",
            TrustServerCertificate = true,
        }.ConnectionString;

        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
