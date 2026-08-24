using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>The §4.11 reference data, asserted against the database a real host actually leaves behind.</b>
///
/// <para>
/// <b>The regression this file exists to catch is a Production installation that authorizes nobody.</b>
/// Before this phase, seeding was one boolean — <c>seed: app.Environment.IsDevelopment()</c> — which
/// put the <c>Permissions</c> rows and the four <c>Roles</c> on the same switch as eight fictional
/// students and a kiosk key printed in source. Enforcement over an empty <c>RolePermissions</c> table
/// admits no one, so the first production deploy with §11 turned on would have locked every operator
/// out of their own installation, and it would have looked like a broken authorization layer rather
/// than a missing row. Nobody running the suite would have noticed, because the suite boots
/// Development hosts where the rows happened to appear.
/// </para>
///
/// <para>
/// Every test here boots a real host rather than calling <c>RbacSeed</c> directly. The seeder being
/// correct is not the property at issue — the property is that the composition root <em>invokes</em>
/// it, in the environment where it matters.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RbacSeedTests : IntegrationTest
{
    public RbacSeedTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Boots a host and returns once it has migrated and seeded. The client is what forces it —
    /// <c>WebApplicationFactory</c> builds lazily, so reading before <c>CreateClient</c> would look at
    /// the database <see cref="IntegrationTest.InitializeAsync"/> just emptied.
    /// </summary>
    private static void Boot(EnvironmentApiFactory factory) => factory.CreateClient().Dispose();

    private async Task<List<string>> PermissionCodesAsync()
    {
        await using var db = NewDbContext();
        return await db.Permissions.AsNoTracking()
            .Select(p => p.Code).OrderBy(c => c).ToListAsync();
    }

    private async Task<Dictionary<string, List<string>>> GrantsAsync()
    {
        await using var db = NewDbContext();

        var rows = await db.RolePermissions.AsNoTracking()
            .Join(db.Roles.AsNoTracking(), rp => rp.RoleId, r => r.Id, (rp, r) => new { r.Name, rp.PermissionId })
            .Join(db.Permissions.AsNoTracking(), x => x.PermissionId, p => p.Id, (x, p) => new { x.Name, p.Code })
            .ToListAsync();

        return rows
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Code).OrderBy(c => c).ToList(), StringComparer.Ordinal);
    }

    // ------------------------------------------------------------- permissions, in both directions

    /// <summary>
    /// <b>Forward: every code the registry declares has a row.</b> A code an endpoint demands and no
    /// <c>Permissions</c> row backs is a permission no role can be granted — the endpoint becomes
    /// reachable by nobody, and the first signal is a support ticket after enforcement goes live.
    /// </summary>
    [Fact]
    public async Task Every_registry_code_has_a_permission_row()
    {
        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production);
        Boot(factory);

        var seeded = (await PermissionCodesAsync()).ToHashSet(StringComparer.Ordinal);

        var missing = EamsPermissions.All.Where(c => !seeded.Contains(c)).Order().ToList();

        Assert.True(
            missing.Count == 0,
            "These EamsPermissions codes have no Permissions row after a real host started:\n  " +
            string.Join("\n  ", missing) +
            "\nA code with no row is a permission no role can hold, so the endpoints that demand it " +
            "become reachable by nobody the day enforcement is switched on.");
    }

    /// <summary>
    /// <b>Reverse: no row exists that the registry does not declare.</b> A stray code reads to the
    /// next person as a permission the product has, so it gets granted to a role and guards nothing —
    /// the same failure ADR-001 D-45 deleted <c>groups.read</c> for, one layer down.
    ///
    /// <para>
    /// The seeder deliberately does <em>not</em> delete unknown rows: that would take their grants
    /// with them, which is data loss decided by a startup path. A test can fail loudly where a seeder
    /// can only destroy quietly, so this is where the statement belongs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_permission_row_exists_that_the_registry_does_not_declare()
    {
        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production);
        Boot(factory);

        var known = EamsPermissions.All.ToHashSet(StringComparer.Ordinal);
        var strays = (await PermissionCodesAsync()).Where(c => !known.Contains(c)).Order().ToList();

        Assert.True(
            strays.Count == 0,
            "These Permissions rows name a code that is not on EamsPermissions:\n  " +
            string.Join("\n  ", strays) +
            "\nA code the table holds and the registry does not declare reads to the next person as " +
            "a permission the product has, which is how a role ends up granting something that " +
            "guards no endpoint.");
    }

    // ------------------------------------------------------------------- the grant matrix, exactly

    [Fact]
    public async Task The_four_roles_are_created_as_system_roles()
    {
        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production);
        Boot(factory);

        await using var db = NewDbContext();
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync();

        Assert.Equal(
            EamsRoleNames.All.Order(StringComparer.Ordinal),
            roles.Select(r => r.Name).Order(StringComparer.Ordinal));

        Assert.All(roles, r => Assert.True(
            r.IsSystem,
            $"Role '{r.Name}' is not marked IsSystem. The flag is what a future role editor reads to " +
            "refuse deletion, and a product whose four built-in roles can be deleted is one that can " +
            "be locked out of itself."));
    }

    /// <summary>
    /// The approved totals: 11 / 11 / 6 / 4. Literals, because they are the cheapest way to catch a
    /// matrix that has drifted by one cell — an assertion derived from the same code that produced the
    /// grants would agree with any drift.
    /// </summary>
    [Theory]
    [InlineData(EamsRoleNames.SuperAdmin, 11)]
    [InlineData(EamsRoleNames.SchoolAdmin, 11)]
    [InlineData(EamsRoleNames.Organizer, 6)]
    [InlineData(EamsRoleNames.Viewer, 4)]
    public async Task Each_seeded_role_holds_the_approved_number_of_grants(string role, int expected)
    {
        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production);
        Boot(factory);

        var grants = await GrantsAsync();

        Assert.True(grants.ContainsKey(role), $"Role '{role}' has no grants at all.");
        Assert.Equal(expected, grants[role].Count);
    }

    [Fact]
    public async Task The_seeded_grants_are_exactly_the_approved_matrix()
    {
        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production);
        Boot(factory);

        var grants = await GrantsAsync();

        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [EamsRoleNames.SuperAdmin] = [.. EamsRoles.HumanAssignable],
            [EamsRoleNames.SchoolAdmin] = [.. EamsRoles.HumanAssignable],
            [EamsRoleNames.Organizer] =
            [
                EamsPermissions.StudentsRead,
                EamsPermissions.EventsRead,
                EamsPermissions.EventsWrite,
                EamsPermissions.AttendanceRead,
                EamsPermissions.AttendanceWrite,
                EamsPermissions.AcademicRead,
            ],
            [EamsRoleNames.Viewer] =
            [
                EamsPermissions.StudentsRead,
                EamsPermissions.EventsRead,
                EamsPermissions.AttendanceRead,
                EamsPermissions.AcademicRead,
            ],
        };

        foreach (var (role, codes) in expected)
            Assert.Equal(codes.Order(StringComparer.Ordinal), grants[role].Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>No role grants <c>attendance.capture</c> — asserted as a positive statement about the
    /// database, not as an absence a later edit could quietly fill in.</b>
    ///
    /// <para>
    /// It is the <em>device's</em> permission: §11 scopes a kiosk API key to it and to nothing else,
    /// and it means "a card was presented at this reader". A human principal holding it could post
    /// taps that are indistinguishable, in the record and in every report, from a real card
    /// presentation — the exact line <see cref="EamsPermissions.AttendanceWrite"/>'s own remarks draw.
    /// An organizer records what they decided, through <c>attendance.write</c> and the audited
    /// <c>POST /attendance/manual</c>.
    /// </para>
    ///
    /// <para>
    /// An empty column in a grant matrix reads as an omission, and "SuperAdmin is missing one" is
    /// exactly the tidy-up that gets done without a second thought. This is what makes doing it a
    /// failing build.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_seeded_role_grants_attendance_capture()
    {
        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production);
        Boot(factory);

        var holders = (await GrantsAsync())
            .Where(g => g.Value.Contains(EamsPermissions.AttendanceCapture, StringComparer.Ordinal))
            .Select(g => g.Key)
            .ToList();

        Assert.True(
            holders.Count == 0,
            $"'{EamsPermissions.AttendanceCapture}' is granted to: {string.Join(", ", holders)}. It " +
            "is the device's permission and no role may hold it — a human carrying it could post " +
            "taps indistinguishable from a real card presentation at a reader. An organizer override " +
            $"goes through '{EamsPermissions.AttendanceWrite}'.");

        // And the code itself is still seeded: deleting it would unauthenticate every kiosk, because
        // it is the policy name of the gated capture endpoints and the claim a device key carries.
        Assert.Contains(EamsPermissions.AttendanceCapture, await PermissionCodesAsync());
    }

    // -------------------------------------------------------------- reference data vs dev convenience

    /// <summary>
    /// <b>Production seeds the reference data and no user.</b> The two halves of this assertion are
    /// the whole point of the phase: the rows enforcement is made of must exist everywhere, and the
    /// convenience account must exist nowhere but a developer's machine.
    /// </summary>
    [Fact]
    public async Task A_production_host_seeds_the_reference_data_and_no_user()
    {
        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production);
        Boot(factory);

        Assert.Equal(EamsPermissions.All.Count, (await PermissionCodesAsync()).Count);

        await using var db = NewDbContext();
        Assert.Equal(EamsRoleNames.All.Count, await db.Roles.CountAsync());

        var users = await db.Users.IgnoreQueryFilters().Select(u => u.Email).ToListAsync();

        Assert.True(
            users.Count == 0,
            $"A Production host created user(s): {string.Join(", ", users)}. The only user a host may " +
            "create is the Development SuperAdmin, and it must exist nowhere else — a seeded " +
            "administrator on a network-reachable host is a credential somebody else configured.");
    }

    /// <summary>
    /// Staging too, and it is a separate case rather than a duplicate. "Not Production" is not a
    /// synonym for "Development" — Staging is a real, network-reachable host — and that exact wrong
    /// predicate already shipped once in this codebase, gating the well-known kiosk key. See
    /// <c>SeedData.DevelopmentKioskApiKey</c>, which records it.
    /// </summary>
    [Fact]
    public async Task A_staging_host_seeds_the_reference_data_and_no_user()
    {
        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Staging);
        Boot(factory);

        Assert.Equal(EamsPermissions.All.Count, (await PermissionCodesAsync()).Count);

        await using var db = NewDbContext();
        Assert.Equal(0, await db.Users.IgnoreQueryFilters().CountAsync());
    }

    /// <summary>
    /// <b>A Development host seeds the SuperAdmin when the password is configured, and it holds the
    /// SuperAdmin role.</b> The password arrives as an environment variable for the reason
    /// <see cref="TestHostConfiguration"/> records: <c>Program.cs</c> reads configuration while the
    /// host is still being described, so a test-registered source would arrive too late.
    /// </summary>
    [Fact]
    public async Task A_development_host_seeds_the_super_admin_when_a_password_is_configured()
    {
        const string variable = "Seed__DevelopmentSuperAdminPassword";
        var previous = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, "a development password long enough");

            using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
            Boot(factory);

            await using var db = NewDbContext();

            var seeded = await db.Users.AsNoTracking().IgnoreQueryFilters()
                .SingleOrDefaultAsync(u => u.Email == SeedData.DevelopmentSuperAdminEmail);

            Assert.True(
                seeded is not null,
                $"No user '{SeedData.DevelopmentSuperAdminEmail}' after a Development host started " +
                $"with '{SeedData.DevelopmentSuperAdminPasswordKey}' configured.");

            Assert.True(seeded!.IsActive);

            // The password is hashed, never stored, and the plaintext appears nowhere in the row.
            Assert.NotEqual("a development password long enough", seeded.PasswordHash);
            Assert.Equal(
                PasswordVerification.Success,
                Passwords.Verify(seeded.PasswordHash, "a development password long enough"));

            // §4.11's single column stays null even on the account the seed creates.
            Assert.Null(seeded.RefreshTokenHash);

            var roles = await db.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == seeded.Id)
                .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
                .ToListAsync();

            Assert.Equal([EamsRoleNames.SuperAdmin], roles);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    /// <summary>
    /// <b>No configured password, no account — never a default and never a generated one.</b>
    ///
    /// <para>
    /// The tempting alternative is the precedent this repo already set for the kiosk:
    /// <c>SeedData.DevelopmentKioskApiKey</c> is a working credential printed in source, and it is
    /// acceptable there for exactly one reason — a capture-scoped device key on a Development-only row
    /// is worthless anywhere it could do harm. A SuperAdmin password is scoped to nothing: it mints
    /// device keys and redefines which semester the institution is in. Copying the precedent would put
    /// a working administrator password in a public repository with an environment check as the only
    /// thing between it and a real installation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_development_host_seeds_no_super_admin_when_no_password_is_configured()
    {
        const string variable = "Seed__DevelopmentSuperAdminPassword";
        var previous = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, null);

            using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
            Boot(factory);

            await using var db = NewDbContext();
            var users = await db.Users.AsNoTracking().IgnoreQueryFilters()
                .Select(u => u.Email).ToListAsync();

            Assert.True(
                users.Count == 0,
                $"A Development host with no '{SeedData.DevelopmentSuperAdminPasswordKey}' created " +
                $"user(s): {string.Join(", ", users)}. There must be no default password and no " +
                "generated one — a hard-coded administrator credential in source is a real " +
                "credential, and a generated one would change on every restart.");

            // The reference data still lands. The two are on different switches, which is the point.
            Assert.Equal(EamsPermissions.All.Count, (await PermissionCodesAsync()).Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    // ---------------------------------------------------------------- idempotency and its own guard

    /// <summary>
    /// <b>The seed lands on a database that already holds a <c>School</c> row.</b>
    ///
    /// <para>
    /// This is the regression <c>DevelopmentSeedTermTests</c> was written for, restated for the RBAC
    /// data because it is the same trap. <c>SeedData.InitializeAsync</c> returns early once a school
    /// exists — its bulk fixture must not be re-added to a database an operator has worked in — so
    /// anything that rides that guard runs only on a database nobody has: every existing dev machine
    /// carries a school. The term block was added after that was already true and short-circuited on
    /// every carried-over machine, leaving the import page dead. RBAC reference data on the same path
    /// would leave every existing database with zero permissions and no way to notice until
    /// enforcement went live.
    /// </para>
    ///
    /// <para>
    /// The school is written <em>before</em> the host boots, which is what makes the arrangement the
    /// real one rather than a simulation of it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_reference_data_is_seeded_onto_a_database_that_already_has_a_school()
    {
        await using (var arrange = NewDbContext())
        {
            arrange.Schools.Add(TestData.NewSchool("PRE"));
            await arrange.SaveChangesAsync();
        }

        using var factory = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production);
        Boot(factory);

        Assert.Equal(
            EamsPermissions.All.Order(StringComparer.Ordinal),
            (await PermissionCodesAsync()).Order(StringComparer.Ordinal));

        await using var db = NewDbContext();
        Assert.Equal(EamsRoleNames.All.Count, await db.Roles.CountAsync());
    }

    /// <summary>
    /// Two starts write one set of rows. Idempotency is not decoration here: this runs on every
    /// production start, so a seeder that appended would grow <c>RolePermissions</c> without bound and
    /// eventually collide with <c>UX_RolePermissions_Role_Permission</c> — a host that will not start,
    /// discovered on a deploy.
    /// </summary>
    [Fact]
    public async Task Seeding_twice_writes_one_set_of_rows()
    {
        using (var first = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production))
            Boot(first);

        var afterFirst = await GrantsAsync();

        using (var second = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production))
            Boot(second);

        var afterSecond = await GrantsAsync();

        Assert.Equal(afterFirst.Count, afterSecond.Count);
        foreach (var (role, codes) in afterFirst)
            Assert.Equal(codes, afterSecond[role]);

        await using var db = NewDbContext();
        Assert.Equal(EamsPermissions.All.Count, await db.Permissions.CountAsync());
        Assert.Equal(EamsRoleNames.All.Count, await db.Roles.CountAsync());
    }

    /// <summary>
    /// <b>A narrowed role stays narrowed.</b> An administrator who removes a permission from
    /// <c>Organizer</c> has made a decision about their institution; a startup that re-applied the
    /// matrix would silently restore it on the next deploy, with no log line and no audit row. Grants
    /// are written at role creation and never reconciled — this is what pins that.
    /// </summary>
    [Fact]
    public async Task An_administrators_narrowing_of_a_role_survives_the_next_start()
    {
        using (var first = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production))
            Boot(first);

        await using (var narrow = NewDbContext())
        {
            var organizer = await narrow.Roles.SingleAsync(r => r.Name == EamsRoleNames.Organizer);
            var write = await narrow.Permissions.SingleAsync(p => p.Code == EamsPermissions.EventsWrite);

            var grant = await narrow.RolePermissions
                .SingleAsync(rp => rp.RoleId == organizer.Id && rp.PermissionId == write.Id);

            narrow.RolePermissions.Remove(grant);
            await narrow.SaveChangesAsync();
        }

        using (var second = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production))
            Boot(second);

        var grants = await GrantsAsync();

        Assert.DoesNotContain(EamsPermissions.EventsWrite, grants[EamsRoleNames.Organizer]);
        Assert.Equal(5, grants[EamsRoleNames.Organizer].Count);
    }

    /// <summary>
    /// A role that is absent is created on a later start, even though the others already exist. The
    /// guard is per role name, not "have we seeded before?" — the same rule <c>SeedTermAsync</c>
    /// records, and the reason a fifth role added in a later phase reaches databases that already
    /// exist.
    /// </summary>
    [Fact]
    public async Task A_missing_role_is_created_even_though_the_others_already_exist()
    {
        using (var first = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production))
            Boot(first);

        await using (var remove = NewDbContext())
        {
            var viewer = await remove.Roles.SingleAsync(r => r.Name == EamsRoleNames.Viewer);
            remove.RolePermissions.RemoveRange(
                remove.RolePermissions.Where(rp => rp.RoleId == viewer.Id));
            remove.Roles.Remove(viewer);
            await remove.SaveChangesAsync();
        }

        using (var second = new EnvironmentApiFactory(Sql.ConnectionString, Environments.Production))
            Boot(second);

        var grants = await GrantsAsync();

        Assert.Equal(4, grants[EamsRoleNames.Viewer].Count);
    }

    /// <summary>
    /// The seeder called directly with nothing to seed writes nothing and does not throw — the state a
    /// host with no authorization registry is in (a migration tool, a console utility). Asserted
    /// because <c>RbacReferenceData.Empty</c> exists precisely so that absence is stated rather than
    /// arrived at by passing null.
    /// </summary>
    [Fact]
    public async Task Empty_reference_data_seeds_nothing_and_does_not_throw()
    {
        await using var db = NewDbContext();

        await RbacSeed.ApplyAsync(db, RbacReferenceData.Empty, NullLogger.Instance);

        Assert.Equal(0, await db.Permissions.CountAsync());
        Assert.Equal(0, await db.Roles.CountAsync());
    }
}
