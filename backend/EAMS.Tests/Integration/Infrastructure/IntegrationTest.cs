using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
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

    /// <summary>
    /// The device a capture is attributed to. Null by default, matching the production context on
    /// every request that is not a device's — an organizer override, an import, a direct service call.
    /// The D-26 mismatch tests set it.
    /// </summary>
    protected TestDeviceContext Device { get; } = new();

    public virtual Task InitializeAsync() => Sql.ResetAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A fresh context on the test database. Deliberately not cached: a stale first-level cache is
    /// the classic way an integration test passes against memory rather than against the database,
    /// so every read-back in this suite uses a new context.
    /// </summary>
    internal EamsDbContext NewDbContext() => Sql.NewDbContext(School);

    internal EamsDbContext NewDbContext(ISchoolContext school) => Sql.NewDbContext(school);

    internal IAttendanceService AttendanceOn(EamsDbContext db) =>
        new AttendanceService(db, CurrentUser, Device, NullLogger<AttendanceService>.Instance);

    /// <summary>
    /// The same service with a logger a test can read back — the shape <see cref="StudentsOn"/> already
    /// takes, and needed for the same reason: the D-36 tap window falls back to its published default
    /// when a §4.13 settings row is unreadable, and a fallback nobody can observe is indistinguishable
    /// from a setting that worked.
    /// </summary>
    internal IAttendanceService AttendanceOn(EamsDbContext db, CapturingLogger<AttendanceService> logger) =>
        new AttendanceService(db, CurrentUser, Device, logger);

    /// <summary>
    /// The §6.6 device surface, wired to the same tenant as <see cref="NewDbContext()"/> — which
    /// matters for the same reason it does on students and events: registering a device has to decide
    /// which school it belongs to, and the tenant comes from the context rather than the request.
    /// </summary>
    internal IDeviceService DevicesOn(EamsDbContext db) => new DeviceService(db, School);

    /// <summary>
    /// The device-key verifier, on the same context the test asserts against. Real, not a stand-in:
    /// the whole of what it does is a hash comparison against a row, and a fake would leave the one
    /// thing worth testing — that the stored value is a hash and not the secret — untested.
    /// </summary>
    internal IDeviceAuthenticator DeviceAuthOn(EamsDbContext db) =>
        new DeviceAuthenticator(db, NullLogger<DeviceAuthenticator>.Instance);

    /// <summary>
    /// Registers a device and returns the plaintext key, for the HTTP tests that now have to present
    /// one. It goes through <see cref="IDeviceService"/> rather than writing the row directly, so a
    /// fixture cannot mint a key by a rule the production path does not use — which is exactly the
    /// class of bug that would make an authentication suite pass against itself.
    /// </summary>
    internal async Task<string> IssueDeviceKeyAsync(
        Guid schoolId, string name = "Test Kiosk", CancellationToken ct = default)
    {
        var pinned = new TestSchoolContext { CurrentSchoolId = schoolId };
        await using var db = NewDbContext(pinned);

        var response = await new DeviceService(db, pinned).RegisterAsync(
            new DeviceWriteRequest(name, "Kiosk", null, IsActive: true), ct);

        Assert.Equal(DeviceWriteOutcome.Saved, response.Outcome);
        return response.IssuedKey!.ApiKey;
    }

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
    internal IEventService EventsOn(EamsDbContext db) => EventsOn(db, AttendanceLiveOptions.Default);

    /// <summary>
    /// The same service with an explicit D-29 poll interval, for the one test that asserts the value in
    /// the live body is the configured one rather than a constant. Everything else takes
    /// <see cref="AttendanceLiveOptions.Default"/>, which is what an unconfigured host resolves to — so
    /// the ordinary tests exercise exactly what ships.
    /// </summary>
    internal IEventService EventsOn(EamsDbContext db, AttendanceLiveOptions live) =>
        new EventService(db, School, CurrentUser, live);

    internal IStudentGroupProjection ProjectionOn(EamsDbContext db) => new StudentGroupProjection(db);

    /// <summary>
    /// The Phase 3b-2 academic reference reads.
    ///
    /// <para>
    /// <b>No <see cref="School"/> argument, unlike every builder above, and that is the thing to
    /// notice.</b> The write services take the tenant because a write has to decide which school a new
    /// row belongs to; a read decides nothing and is scoped by the global <c>SchoolId</c> query filter
    /// instead. The tenant still reaches it — through <see cref="NewDbContext()"/>, which builds the
    /// context on this test's <see cref="School"/> — so a tenancy test pins <see cref="School"/> and
    /// passes the resulting context, rather than pinning the service.
    /// </para>
    /// </summary>
    internal IAcademicReferenceService AcademicOn(EamsDbContext db) => new AcademicReferenceService(db);

    /// <summary>The §4.7 group reads. Scoped exactly as <see cref="AcademicOn"/> is.</summary>
    internal IStudentGroupService StudentGroupsOn(EamsDbContext db) => new StudentGroupService(db);

    /// <summary>
    /// D-53's term write surface, wired to the same tenant as <see cref="NewDbContext()"/>.
    ///
    /// <para>
    /// It takes <see cref="School"/> where <see cref="AcademicOn"/> does not, and the asymmetry is the
    /// same one <see cref="StudentsOn"/> and <see cref="EventsOn(EamsDbContext)"/> record: creating a
    /// term has to decide which school it is filed under, and that answer comes from the context rather
    /// than the request. A read decides nothing and is scoped by the query filter alone.
    /// </para>
    /// </summary>
    internal ITermAdminService TermsOn(EamsDbContext db) =>
        new TermAdminService(db, School, NullLogger<TermAdminService>.Instance);

    /// <summary>
    /// The same service with a logger a test can read back, for the reason
    /// <see cref="CapturingLogger{T}"/> exists: losing the duplicate-code race to
    /// <c>UX_Terms_SchoolId_Code</c> is a failure the service <em>recovers from</em>, so the caller
    /// gets a tidy 409 whether the conflict was decided by the pre-check or by the index. The log entry
    /// is the only observable difference between those two paths, and therefore the only way a test can
    /// prove it exercised the one that matters.
    /// </summary>
    internal ITermAdminService TermsOn(EamsDbContext db, CapturingLogger<TermAdminService> logger) =>
        new TermAdminService(db, School, logger);

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
