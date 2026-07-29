using EAMS.Application.Abstractions;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Services;
using EAMS.Infrastructure.Sis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// Shared plumbing for the integration tests: a clean database before every test, and short ways to
/// get a context or a service pointed at it.
///
/// <para>
/// <b>Isolation is per test, not per class.</b> <see cref="InitializeAsync"/> runs before each test
/// method (xUnit constructs a new instance per test) and empties every table, so no test can see a
/// row it did not write — including the seed data and the leftover manual-verification rows in the
/// developer's own <c>EAMS</c> database, which this suite never touches at all.
/// </para>
///
/// <para>
/// Services are returned as their <c>EAMS.Application.Abstractions</c> interfaces. The concrete
/// classes are reachable here (EAMS.Infrastructure grants this assembly internal access) but using
/// them would test something the rest of the system cannot call — the interface is the contract
/// Phase 4 publishes to the mobile developer, so the interface is what gets exercised.
/// </para>
/// </summary>
public abstract class IntegrationTest : IAsyncLifetime
{
    protected IntegrationTest(SqlServerFixture sql) => Sql = sql;

    protected SqlServerFixture Sql { get; }

    /// <summary>
    /// The tenant these tests run under. Left unpinned (<c>null</c> — "do not filter") by default so
    /// arrange steps can write rows for several schools; the multi-tenant tests set it explicitly.
    /// </summary>
    protected TestSchoolContext School { get; } = new();

    /// <summary>
    /// The identity writes are attributed to. Null by default, matching the production
    /// <c>ICurrentUser</c> for the whole of the pre-auth build, so a test that does not care about
    /// attribution exercises exactly what ships. The attribution tests set it.
    /// </summary>
    protected TestCurrentUser CurrentUser { get; } = new();

    public virtual Task InitializeAsync() => Sql.ResetAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A fresh context on the test database. Deliberately not cached: a stale first-level cache is
    /// the classic way an integration test passes against memory rather than against the database,
    /// so every read-back in this suite uses a new context.
    /// </summary>
    internal EamsDbContext NewDbContext() => Sql.NewDbContext(School);

    internal EamsDbContext NewDbContext(ISchoolContext school) => Sql.NewDbContext(school);

    internal IAttendanceService AttendanceOn(EamsDbContext db) => new AttendanceService(db, CurrentUser);

    /// <summary>
    /// The §6.2 students service, wired to the same tenant as <see cref="NewDbContext()"/>.
    ///
    /// <para>
    /// The tenant seam matters here for the same reason it does on events: <c>POST /students</c> has to
    /// decide which school a new student belongs to, and a card's denormalized <c>SchoolId</c>
    /// (ADR-001 D-3) has to agree with its owner's. Passing the test's own <see cref="School"/> is what
    /// lets a multi-tenant test prove a student cannot be filed against another school.
    /// </para>
    /// </summary>
    internal IStudentService StudentsOn(EamsDbContext db) =>
        new StudentService(db, School, NullLogger<StudentService>.Instance);

    /// <summary>
    /// The same service with a logger a test can read back. See <see cref="CapturingLogger{T}"/> for
    /// why the recovered-from failures need one.
    /// </summary>
    internal IStudentService StudentsOn(EamsDbContext db, CapturingLogger<StudentService> logger) =>
        new StudentService(db, School, logger);

    /// <summary>
    /// The §6.3 events service, wired to the same tenant and identity as <see cref="NewDbContext()"/>.
    ///
    /// <para>
    /// Both seams matter here and neither did before Phase 3a: <c>POST /events</c> has to decide which
    /// school a new event belongs to, and closing an event stamps <c>RecordedByUserId</c> on every
    /// absentee it materializes. Passing the test's own <see cref="School"/> rather than a fresh
    /// unpinned one is what lets a multi-tenant test prove an event cannot be filed against, or invite,
    /// another school's rows.
    /// </para>
    /// </summary>
    internal IEventService EventsOn(EamsDbContext db) => new EventService(db, School, CurrentUser);

    internal IStudentGroupProjection ProjectionOn(EamsDbContext db) => new StudentGroupProjection(db);

    /// <summary>
    /// The §10 import pipeline, wired to the same context the test asserts against.
    ///
    /// <para>
    /// The projection is a real <see cref="StudentGroupProjection"/> rather than a stand-in: the
    /// importer's third pass calls it, and a fake would leave the one interaction between the two
    /// untested — the case where a term's groups are materialized from enrollments the import just
    /// wrote, which is what makes an imported roster usable as an event audience.
    /// </para>
    /// </summary>
    internal ISisImportService SisImportOn(EamsDbContext db) =>
        new SisImportService(db, new StudentGroupProjection(db), CurrentUser);
}
