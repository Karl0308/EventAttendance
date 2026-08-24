using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>UserCredentialVerifier</c> — the persistence half of login. It answers "is this password
/// good?"; it mints no token and reads no claim, exactly as <c>DeviceAuthenticator</c> answers "is
/// this device key good?" for the handler that turns the answer into claims.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class UserCredentialTests : IntegrationTest
{
    public UserCredentialTests(SqlServerFixture sql) : base(sql) { }

    private const string Email = "registrar@usa.edu.ph";
    private const string Password = "a sufficiently long passphrase";

    private async Task<(Guid SchoolId, Guid UserId)> ArrangeAsync(
        string email = Email, string password = Password)
    {
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        return (schoolId, await CreateUserAsync(schoolId, email, password));
    }

    [Fact]
    public async Task The_right_password_verifies_and_reports_the_user_and_tenant()
    {
        var (schoolId, userId) = await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await CredentialsOn(db).VerifyAsync(Email, Password);

        Assert.Equal(UserCredentialOutcome.Verified, result.Outcome);
        Assert.Equal(userId, result.UserId);
        Assert.Equal(schoolId, result.SchoolId);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_identifies_nobody()
    {
        await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await CredentialsOn(db).VerifyAsync(Email, "not the password at all");

        Assert.Equal(UserCredentialOutcome.PasswordMismatch, result.Outcome);
        Assert.Equal(Guid.Empty, result.UserId);
        Assert.Equal(Guid.Empty, result.SchoolId);
    }

    [Theory]
    [InlineData("nobody@usa.edu.ph")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unknown_address_is_refused(string email)
    {
        await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await CredentialsOn(db).VerifyAsync(email, Password);

        Assert.Equal(UserCredentialOutcome.UnknownEmail, result.Outcome);
        Assert.Equal(Guid.Empty, result.UserId);
    }

    /// <summary>
    /// Addresses are compared in their normalized form, so the same person typing
    /// <c>Registrar@USA.edu.ph</c> gets in. The normalization happens on both the write and the read
    /// path — one spelling is what ever reaches <c>UX_Users_Email</c>.
    /// </summary>
    [Theory]
    [InlineData("REGISTRAR@USA.EDU.PH")]
    [InlineData("  registrar@usa.edu.ph  ")]
    [InlineData("Registrar@Usa.Edu.Ph")]
    public async Task An_address_is_matched_in_its_normalized_form(string presented)
    {
        var (_, userId) = await ArrangeAsync();

        await using var db = NewDbContext();
        var result = await CredentialsOn(db).VerifyAsync(presented, Password);

        Assert.Equal(UserCredentialOutcome.Verified, result.Outcome);
        Assert.Equal(userId, result.UserId);
    }

    /// <summary>
    /// <b>Inactive is reported only when the password is right.</b> An inactive account with a wrong
    /// password answers <see cref="UserCredentialOutcome.PasswordMismatch"/> — reporting
    /// <c>Inactive</c> on a bad guess would confirm the address exists to anybody who tried it, which
    /// is the enumeration oracle the outcome split exists to keep out of the response.
    /// </summary>
    [Fact]
    public async Task A_deactivated_account_reports_inactive_only_when_the_password_is_right()
    {
        var (schoolId, userId) = await ArrangeAsync();

        await using (var deactivate = NewDbContext())
        {
            await deactivate.Users.IgnoreQueryFilters()
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        await using var db = NewDbContext();
        var verifier = CredentialsOn(db);

        var right = await verifier.VerifyAsync(Email, Password);
        Assert.Equal(UserCredentialOutcome.Inactive, right.Outcome);
        Assert.Equal(userId, right.UserId);
        Assert.Equal(schoolId, right.SchoolId);

        var wrong = await verifier.VerifyAsync(Email, "guessing");
        Assert.Equal(UserCredentialOutcome.PasswordMismatch, wrong.Outcome);
    }

    /// <summary>
    /// <b>The lookup ignores the tenant filter, and it has to.</b> A credential is presented before
    /// any tenant is resolved — resolving it is one of the things authenticating is for. A verifier
    /// that respected the filter would refuse every login on a host whose tenant is not yet decided,
    /// and would do it as "unknown address" rather than as an error.
    /// </summary>
    [Fact]
    public async Task A_user_is_found_even_when_the_context_is_pinned_to_another_school()
    {
        var (_, userId) = await ArrangeAsync();

        // A tenant that owns nothing — the state a request carrying no credentials is in.
        var elsewhere = new TestSchoolContext { CurrentSchoolId = Guid.NewGuid() };

        await using var db = NewDbContext(elsewhere);

        // The filter is genuinely active on this context: the user is invisible to an ordinary read.
        Assert.Equal(0, await db.Users.CountAsync());

        var result = await CredentialsOn(db).VerifyAsync(Email, Password);

        Assert.Equal(UserCredentialOutcome.Verified, result.Outcome);
        Assert.Equal(userId, result.UserId);
    }

    // ----------------------------------------------------------------- the rehash-on-login upgrade

    /// <summary>
    /// <b>A hash written at older parameters is rewritten at the current ones, in place, during the
    /// login that presented it.</b>
    ///
    /// <para>
    /// This is the only moment the plaintext exists, so it is the only moment the upgrade can happen.
    /// Skipping it — which is what treating verification as a boolean amounts to — means an increase
    /// in the iteration count reaches new accounts only, while every long-standing one, which is every
    /// account worth attacking, keeps the old parameters indefinitely and nothing ever says so.
    /// </para>
    ///
    /// <para>
    /// The stale hash is produced by a real <c>PasswordHasher</c> at a lower count rather than
    /// hand-assembled, so it is exactly what a pre-upgrade row looks like.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_password_hashed_at_older_parameters_is_upgraded_during_login()
    {
        var (_, userId) = await ArrangeAsync();

        var stale = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = 1_000,
        })).HashPassword(new User(), Password);

        await using (var downgrade = NewDbContext())
        {
            await downgrade.Users.IgnoreQueryFilters()
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, stale));
        }

        // Pre-condition: the row really is on the old parameters.
        Assert.Equal(PasswordVerification.SuccessRehashNeeded, Passwords.Verify(stale, Password));

        await using (var db = NewDbContext())
        {
            var result = await CredentialsOn(db).VerifyAsync(Email, Password);
            Assert.Equal(UserCredentialOutcome.Verified, result.Outcome);
        }

        await using var read = NewDbContext();
        var rewritten = await read.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleAsync();

        Assert.NotEqual(stale, rewritten);
        Assert.Equal(
            PasswordVerification.Success,
            Passwords.Verify(rewritten, Password));

        // And the password still works next time, which is the half a broken rehash would destroy
        // silently — an upgrade that wrote a hash of the wrong thing locks the account out on the
        // *following* login, not this one.
        await using var again = NewDbContext();
        Assert.Equal(
            UserCredentialOutcome.Verified,
            (await CredentialsOn(again).VerifyAsync(Email, Password)).Outcome);
    }

    /// <summary>
    /// A wrong password against a stale hash upgrades nothing. The rehash rides a successful
    /// verification, and a version that rewrote on any presentation would let an attacker with no
    /// credential churn the column.
    /// </summary>
    [Fact]
    public async Task A_failed_login_against_a_stale_hash_rewrites_nothing()
    {
        var (_, userId) = await ArrangeAsync();

        var stale = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            IterationCount = 1_000,
        })).HashPassword(new User(), Password);

        await using (var downgrade = NewDbContext())
        {
            await downgrade.Users.IgnoreQueryFilters()
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, stale));
        }

        await using (var db = NewDbContext())
            Assert.Equal(
                UserCredentialOutcome.PasswordMismatch,
                (await CredentialsOn(db).VerifyAsync(Email, "wrong")).Outcome);

        await using var read = NewDbContext();
        Assert.Equal(
            stale,
            await read.Users.AsNoTracking().IgnoreQueryFilters()
                .Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleAsync());
    }

    /// <summary>
    /// A hash already at the current parameters is left exactly as it was. Rewriting on every login
    /// would be a write on the login path for no reason, and would make the column change under an
    /// operator watching it for a reason that does not exist.
    /// </summary>
    [Fact]
    public async Task A_current_hash_is_not_rewritten_on_login()
    {
        var (_, userId) = await ArrangeAsync();

        string before;
        await using (var read = NewDbContext())
            before = await read.Users.AsNoTracking().IgnoreQueryFilters()
                .Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleAsync();

        await using (var db = NewDbContext())
            Assert.Equal(
                UserCredentialOutcome.Verified,
                (await CredentialsOn(db).VerifyAsync(Email, Password)).Outcome);

        await using var after = NewDbContext();
        Assert.Equal(
            before,
            await after.Users.AsNoTracking().IgnoreQueryFilters()
                .Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleAsync());
    }

    /// <summary>
    /// <b>A corrupt stored hash refuses rather than throwing.</b> An exception escaping the verifier
    /// would become a 500 on the login endpoint, which tells an attacker the address exists — on
    /// exactly the response written to say nothing.
    /// </summary>
    [Fact]
    public async Task A_row_whose_stored_hash_is_not_a_hash_refuses_rather_than_throwing()
    {
        var (_, userId) = await ArrangeAsync();

        await using (var corrupt = NewDbContext())
        {
            await corrupt.Users.IgnoreQueryFilters()
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "not a hash at all"));
        }

        await using var db = NewDbContext();
        var verifier = CredentialsOn(db);

        var thrown = await Record.ExceptionAsync(() => verifier.VerifyAsync(Email, Password));
        Assert.Null(thrown);

        Assert.Equal(
            UserCredentialOutcome.PasswordMismatch,
            (await verifier.VerifyAsync(Email, Password)).Outcome);
    }
}
