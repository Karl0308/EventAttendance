using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Identity;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>UserProvisioningService</c> — <b>the one way a user is created in this system</b>, and
/// therefore the shared implementation behind both the Development SuperAdmin seed and the
/// <c>create-admin</c> console command.
///
/// <para>
/// <b>Why it is one service and not two call sites.</b> Both callers have to normalize an address,
/// apply the password policy, hash at the current parameters, refuse a duplicate, attach a role and
/// write an audit row. Written twice, those drift — and the copy that drifts is the one nobody
/// exercises, which is the Production bootstrap path, discovered on the day an installation has no
/// other way in. The same rule <c>IntegrationTest.IssueDeviceKeyAsync</c> follows for device keys.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class UserProvisioningTests : IntegrationTest
{
    public UserProvisioningTests(SqlServerFixture sql) : base(sql) { }

    private const string Password = "a sufficiently long passphrase";

    /// <summary>A school and the RBAC reference data — a role has to exist before a user can hold one.</summary>
    private async Task<Guid> ArrangeAsync(string code = "USA")
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();

        await RbacSeed.ApplyAsync(db, EamsRoles.ReferenceData, NullLogger.Instance);

        return school.Id;
    }

    private IUserProvisioningService ServiceFor(EamsDbContext db, Guid schoolId) =>
        new UserProvisioningService(db, Passwords, new TestSchoolContext { CurrentSchoolId = schoolId });

    // ------------------------------------------------------------------------------- creation

    [Fact]
    public async Task Creating_a_user_hashes_the_password_grants_the_role_and_writes_an_audit_row()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await ServiceFor(db, schoolId).CreateAsync(
            new UserProvisioningRequest("Registrar@USA.edu.ph", "Ana Reyes", EamsRoleNames.SchoolAdmin),
            Password,
            actor: "acer@LAPTOP");

        Assert.Equal(UserProvisioningOutcome.Created, result.Outcome);
        Assert.Equal(schoolId, result.SchoolId);

        await using var read = NewDbContext();

        var user = await read.Users.AsNoTracking().IgnoreQueryFilters()
            .SingleAsync(u => u.Id == result.UserId);

        // Normalized on the way in, so one spelling is what ever reaches UX_Users_Email.
        Assert.Equal("registrar@usa.edu.ph", user.Email);
        Assert.Equal("Ana Reyes", user.FullName);
        Assert.True(user.IsActive);

        // Hashed, never stored. The plaintext appears nowhere in the row.
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.DoesNotContain(Password, user.PasswordHash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PasswordVerification.Success, Passwords.Verify(user.PasswordHash, Password));

        // §4.11's single refresh-token column stays null on every user this system creates.
        Assert.Null(user.RefreshTokenHash);

        var roles = await read.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == result.UserId)
            .Join(read.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
            .ToListAsync();

        Assert.Equal([EamsRoleNames.SchoolAdmin], roles);

        var audit = await read.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == UserProvisioningService.AuditAction);

        Assert.Equal(result.UserId, audit.EntityId);
        Assert.Equal(schoolId, audit.SchoolId);
        Assert.Null(audit.UserId); // nobody is authenticated when either caller runs
        Assert.Contains("acer@LAPTOP", audit.Changes);
        Assert.Contains(EamsRoleNames.SchoolAdmin, audit.Changes);
    }

    /// <summary>
    /// <b>The audit row never carries the password.</b> The visibility control that stands in for a
    /// "first user only" guard must not itself be where the credential leaks — and a
    /// serialize-the-request implementation would have put it there without anyone noticing, because
    /// nothing else reads that column.
    /// </summary>
    [Fact]
    public async Task The_audit_row_does_not_contain_the_password()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        await ServiceFor(db, schoolId).CreateAsync(
            new UserProvisioningRequest("a@usa.edu.ph", "A B", EamsRoleNames.Viewer),
            Password, "acer@LAPTOP");

        await using var read = NewDbContext();
        var audit = await read.AuditLogs.AsNoTracking().SingleAsync();

        Assert.DoesNotContain(Password, audit.Changes ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EamsRoleNames.SuperAdmin)]
    [InlineData(EamsRoleNames.SchoolAdmin)]
    [InlineData(EamsRoleNames.Organizer)]
    [InlineData(EamsRoleNames.Viewer)]
    public async Task Any_of_the_four_roles_can_be_granted(string role)
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await ServiceFor(db, schoolId).CreateAsync(
            new UserProvisioningRequest($"{role}@usa.edu.ph", "A B", role), Password, "test");

        Assert.Equal(UserProvisioningOutcome.Created, result.Outcome);
    }

    // -------------------------------------------------------------------------------- refusals

    /// <summary>
    /// <b>An existing address is refused, never reset.</b> The convenient behaviour — "create or
    /// reset" — is how the wrong account gets its password changed by an operator who believed they
    /// were creating a new one, and in a shell it is indistinguishable from success.
    /// </summary>
    [Fact]
    public async Task An_existing_address_is_refused_and_the_stored_password_is_untouched()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var service = ServiceFor(db, schoolId);

        var first = await service.CreateAsync(
            new UserProvisioningRequest("taken@usa.edu.ph", "First", EamsRoleNames.SchoolAdmin),
            Password, "test");
        Assert.Equal(UserProvisioningOutcome.Created, first.Outcome);

        var second = await service.CreateAsync(
            new UserProvisioningRequest("TAKEN@usa.edu.ph", "Impostor", EamsRoleNames.SuperAdmin),
            "an entirely different passphrase", "test");

        Assert.Equal(UserProvisioningOutcome.EmailInUse, second.Outcome);
        Assert.Equal(Guid.Empty, second.UserId);

        await using var read = NewDbContext();
        var user = await read.Users.AsNoTracking().IgnoreQueryFilters()
            .SingleAsync(u => u.Email == "taken@usa.edu.ph");

        Assert.Equal("First", user.FullName);
        Assert.Equal(PasswordVerification.Success, Passwords.Verify(user.PasswordHash, Password));
        Assert.Equal(PasswordVerification.Failed,
            Passwords.Verify(user.PasswordHash, "an entirely different passphrase"));

        // And no second role was granted to the existing account.
        Assert.Equal(1, await read.UserRoles.CountAsync(ur => ur.UserId == user.Id));
    }

    /// <summary>
    /// <b>A collision with the seeded Development account names it, and names the setting that
    /// created it.</b> A bare "that address is in use" is the confusing failure for an account the
    /// operator never knowingly created — the seed writes one from a configuration key, so a developer
    /// who then runs <c>create-admin</c> with the same address is told their address is taken by
    /// something they cannot see and did not type.
    /// </summary>
    [Fact]
    public async Task A_collision_with_the_seeded_development_account_names_the_configuration_key()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var service = ServiceFor(db, schoolId);

        await service.CreateAsync(
            new UserProvisioningRequest(
                SeedData.DevelopmentSuperAdminEmail, "Development SuperAdmin", EamsRoleNames.SuperAdmin),
            Password, "seed");

        var collision = await service.CreateAsync(
            new UserProvisioningRequest(
                SeedData.DevelopmentSuperAdminEmail, "Someone Else", EamsRoleNames.SchoolAdmin),
            Password, "acer@LAPTOP");

        Assert.Equal(UserProvisioningOutcome.EmailInUse, collision.Outcome);
        Assert.Contains(SeedData.DevelopmentSuperAdminPasswordKey, collision.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The password policy, and the refusal never echoes the password.</b> Twelve rather than
    /// eight: this service creates administrators, and the first one it creates is the account that
    /// can mint device keys and redefine which semester the institution is in.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public async Task A_password_below_the_policy_is_refused_without_being_echoed(string password)
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var service = ServiceFor(db, schoolId);

        var result = await service.CreateAsync(
            new UserProvisioningRequest("a@usa.edu.ph", "A B", EamsRoleNames.Viewer), password, "test");

        Assert.Equal(UserProvisioningOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(service.MinimumPasswordLength.ToString(), result.Message, StringComparison.Ordinal);

        if (password.Length > 0)
            Assert.DoesNotContain(password, result.Message, StringComparison.Ordinal);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.Users.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_password_equal_to_the_address_is_refused()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await ServiceFor(db, schoolId).CreateAsync(
            new UserProvisioningRequest("registrar@usa.edu.ph", "A B", EamsRoleNames.Viewer),
            "registrar@usa.edu.ph", "test");

        Assert.Equal(UserProvisioningOutcome.ValidationFailed, result.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("@usa.edu.ph")]
    [InlineData("registrar@")]
    [InlineData("two@at@signs.ph")]
    public async Task An_address_that_is_not_an_address_is_refused(string email)
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await ServiceFor(db, schoolId).CreateAsync(
            new UserProvisioningRequest(email, "A B", EamsRoleNames.Viewer), Password, "test");

        Assert.Equal(UserProvisioningOutcome.ValidationFailed, result.Outcome);
    }

    [Fact]
    public async Task A_blank_name_is_refused()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await ServiceFor(db, schoolId).CreateAsync(
            new UserProvisioningRequest("a@usa.edu.ph", "   ", EamsRoleNames.Viewer), Password, "test");

        Assert.Equal(UserProvisioningOutcome.ValidationFailed, result.Outcome);
    }

    /// <summary>
    /// An unknown role is refused and the known ones are listed — and, on a database with no roles at
    /// all, the message says the reference data has never been seeded rather than listing nothing.
    /// That is the state a create-admin against a database nobody has started the API on would be in,
    /// and "Known roles: " with an empty list is a dead end.
    /// </summary>
    [Fact]
    public async Task An_unknown_role_is_refused_and_the_known_ones_are_listed()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await ServiceFor(db, schoolId).CreateAsync(
            new UserProvisioningRequest("a@usa.edu.ph", "A B", "Administrator"), Password, "test");

        Assert.Equal(UserProvisioningOutcome.UnknownRole, result.Outcome);
        foreach (var known in EamsRoleNames.All)
            Assert.Contains(known, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unseeded_database_says_so_rather_than_listing_no_roles()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();

        var result = await ServiceFor(db, school.Id).CreateAsync(
            new UserProvisioningRequest("a@usa.edu.ph", "A B", EamsRoleNames.SchoolAdmin),
            Password, "test");

        Assert.Equal(UserProvisioningOutcome.UnknownRole, result.Outcome);
        Assert.Contains("never been seeded", result.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ school resolution

    [Fact]
    public async Task A_named_school_code_files_the_user_under_that_school()
    {
        await ArrangeAsync("AAA");
        var second = await ArrangeAsync("BBB");

        await using var db = NewDbContext();

        // No pinned tenant, two schools: the code is the only thing that can decide.
        var service = new UserProvisioningService(db, Passwords, new TestSchoolContext());

        var result = await service.CreateAsync(
            new UserProvisioningRequest("a@usa.edu.ph", "A B", EamsRoleNames.Viewer, SchoolCode: "BBB"),
            Password, "test");

        Assert.Equal(UserProvisioningOutcome.Created, result.Outcome);
        Assert.Equal(second, result.SchoolId);
    }

    [Fact]
    public async Task An_unknown_school_code_is_refused_by_name()
    {
        await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await new UserProvisioningService(db, Passwords, new TestSchoolContext())
            .CreateAsync(
                new UserProvisioningRequest("a@usa.edu.ph", "A B", EamsRoleNames.Viewer, SchoolCode: "NOPE"),
                Password, "test");

        Assert.Equal(UserProvisioningOutcome.NoSchoolResolved, result.Outcome);
        Assert.Contains("NOPE", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Several schools and no code and no pin: there is no honest choice, so it refuses rather than
    /// filing the administrator under whichever school sorts first. The same refusal
    /// <c>StudentService</c>, <c>EventService</c>, <c>DeviceService</c> and <c>TermAdminService</c>
    /// make, through the same helper.
    /// </summary>
    [Fact]
    public async Task Several_schools_and_no_code_is_refused_rather_than_guessed()
    {
        await ArrangeAsync("AAA");
        await ArrangeAsync("BBB");

        await using var db = NewDbContext();
        var result = await new UserProvisioningService(db, Passwords, new TestSchoolContext())
            .CreateAsync(
                new UserProvisioningRequest("a@usa.edu.ph", "A B", EamsRoleNames.Viewer), Password, "test");

        Assert.Equal(UserProvisioningOutcome.NoSchoolResolved, result.Outcome);
    }

    [Fact]
    public async Task One_school_and_no_code_resolves_to_the_only_school()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await new UserProvisioningService(db, Passwords, new TestSchoolContext())
            .CreateAsync(
                new UserProvisioningRequest("a@usa.edu.ph", "A B", EamsRoleNames.Viewer), Password, "test");

        Assert.Equal(UserProvisioningOutcome.Created, result.Outcome);
        Assert.Equal(schoolId, result.SchoolId);
    }

    // ----------------------------------------------------------------------------- the no-guard

    /// <summary>
    /// <b>There is deliberately no "refuse once a user exists" guard.</b>
    ///
    /// <para>
    /// Such a guard buys nothing against an attacker — anyone who can invoke this already holds the
    /// binary, the connection string and the database, and can write the row directly — while removing
    /// the only recovery path an installation has when its sole administrator leaves. The control is
    /// visibility: every creation writes an audit row naming the operating-system account and machine.
    /// This pins both halves, because "add a guard, it is obviously safer" is the change somebody will
    /// propose.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_second_administrator_can_be_created_and_both_creations_are_audited()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        var service = ServiceFor(db, schoolId);

        var first = await service.CreateAsync(
            new UserProvisioningRequest("first@usa.edu.ph", "First", EamsRoleNames.SchoolAdmin),
            Password, "acer@LAPTOP");
        var second = await service.CreateAsync(
            new UserProvisioningRequest("second@usa.edu.ph", "Second", EamsRoleNames.SchoolAdmin),
            Password, "someone@SERVER");

        Assert.Equal(UserProvisioningOutcome.Created, first.Outcome);
        Assert.Equal(UserProvisioningOutcome.Created, second.Outcome);

        await using var read = NewDbContext();
        var audits = await read.AuditLogs.AsNoTracking()
            .Where(a => a.Action == UserProvisioningService.AuditAction)
            .ToListAsync();

        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, a => a.Changes!.Contains("acer@LAPTOP", StringComparison.Ordinal));
        Assert.Contains(audits, a => a.Changes!.Contains("someone@SERVER", StringComparison.Ordinal));
    }

    /// <summary>
    /// A refused creation leaves no audit row. The trail has to mean "an administrator was created",
    /// not "somebody tried" — otherwise the one signal that stands in for a guard is diluted by every
    /// mistyped password.
    /// </summary>
    [Fact]
    public async Task A_refused_creation_writes_no_audit_row()
    {
        var schoolId = await ArrangeAsync();

        await using var db = NewDbContext();
        await ServiceFor(db, schoolId).CreateAsync(
            new UserProvisioningRequest("a@usa.edu.ph", "A B", EamsRoleNames.Viewer), "short", "test");

        await using var read = NewDbContext();
        Assert.Equal(0, await read.AuditLogs.CountAsync());
    }
}
