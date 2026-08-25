using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Identity;

/// <summary>
/// The single implementation of <see cref="IUserProvisioningService"/> — see that interface for why
/// there is exactly one and what both of its callers would otherwise have duplicated.
/// </summary>
internal sealed class UserProvisioningService : IUserProvisioningService
{
    /// <summary>
    /// <b>Twelve, not eight.</b> Eight is the floor for an ordinary account behind a rate limiter and
    /// a lockout; this service creates administrators, and the first one it creates is the account
    /// that can mint device keys and redefine which semester the institution is in. There is no
    /// composition rule (no "one digit, one symbol") because length is what actually resists an offline
    /// attack on a leaked hash and composition rules mostly produce <c>Password1!</c>.
    /// </summary>
    /// <summary>
    /// The minimum password length this system will accept, at creation and at change.
    ///
    /// <para>
    /// <c>internal</c> rather than private since Phase 6b: <c>AuthService.ChangePasswordAsync</c>
    /// holds a new password to the same bar, and two numbers that must agree is one number written
    /// twice.
    /// </para>
    /// </summary>
    internal const int MinimumPassword = 12;

    /// <summary>
    /// The audit <c>Action</c> every provisioned user leaves behind. A constant rather than a literal
    /// because it is what an operator searches for, and a typo would make the row unfindable by the
    /// one query anyone would run.
    /// </summary>
    internal const string AuditAction = "auth.admin.created";

    private readonly EamsDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ISchoolContext _school;

    public UserProvisioningService(EamsDbContext db, IPasswordHasher hasher, ISchoolContext school)
    {
        _db = db;
        _hasher = hasher;
        _school = school;
    }

    public int MinimumPasswordLength => MinimumPassword;

    public async Task<UserProvisioningResult> CreateAsync(
        UserProvisioningRequest request,
        string password,
        string actor,
        CancellationToken ct = default)
    {
        var email = UserCredentialVerifier.NormalizeEmail(request.Email);
        var fullName = request.FullName?.Trim() ?? "";

        if (!Validate(email, fullName, password, out var message))
            return UserProvisioningResult.Failed(UserProvisioningOutcome.ValidationFailed, message);

        // IgnoreQueryFilters: UX_Users_Email is global, so an address taken by another school's user
        // is still taken. A tenant-filtered pre-check would report the address free and then lose to
        // the index — a 500 where a clear refusal belongs.
        var existing = await _db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Email == email)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != Guid.Empty)
            return UserProvisioningResult.Failed(UserProvisioningOutcome.EmailInUse, EmailInUseMessage(email));

        var role = await _db.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Name == request.RoleName, ct);

        if (role is null)
        {
            var known = await _db.Roles.AsNoTracking().OrderBy(r => r.Name)
                .Select(r => r.Name).ToListAsync(ct);

            return UserProvisioningResult.Failed(
                UserProvisioningOutcome.UnknownRole,
                $"No role named '{request.RoleName}'. " + (known.Count == 0
                    ? "This database holds no roles at all, which means the RBAC reference data has " +
                      "never been seeded — start the API once against it, or re-run this command, " +
                      "since both seed it before doing anything else."
                    : $"Known roles: {string.Join(", ", known)}."));
        }

        var schoolId = await ResolveSchoolAsync(request.SchoolCode, ct);
        if (schoolId is null)
        {
            return UserProvisioningResult.Failed(
                UserProvisioningOutcome.NoSchoolResolved,
                request.SchoolCode is { Length: > 0 } code
                    ? $"No school has the code '{code}'."
                    : "No school could be resolved for this user. Exactly one school must exist, or " +
                      "the school must be named explicitly by its code.");
        }

        var user = new User
        {
            SchoolId = schoolId.Value,
            Email = email,
            FullName = fullName,
            PasswordHash = _hasher.Hash(password),
            IsActive = true,
            // RefreshTokenHash is left null and nothing here or anywhere else writes it — §4.11's
            // single-column design cannot express rotation, so RefreshTokens carries the sessions.
            // See RefreshToken, and Device.ApiKey for the identical decision one phase earlier.
        };

        _db.Users.Add(user);
        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });

        // The visibility that stands in for a "first user only" guard. Written in the same
        // SaveChanges as the user, so an audit row without its user, or a user without its audit row,
        // is not a state this can produce.
        _db.AuditLogs.Add(new AuditLog
        {
            SchoolId = schoolId.Value,
            // Null: there is no authenticated principal at the moment either caller runs. The honest
            // answer, exactly as ICurrentUser's null is — the actor is in Changes below instead.
            UserId = null,
            Action = AuditAction,
            EntityType = nameof(User),
            EntityId = user.Id,
            Changes = JsonSerializer.Serialize(new
            {
                email,
                role = role.Name,
                actor,
                machine = Environment.MachineName,
            }),
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // The pre-check above and this insert are two statements. Two operators bootstrapping the
            // same installation in the same second race on UX_Users_Email, and the loser gets the same
            // clear refusal as the one who checked first rather than a 500 — the pattern
            // TermAdminService records at length for UX_Terms_SchoolId_Code.
            return UserProvisioningResult.Failed(UserProvisioningOutcome.EmailInUse, EmailInUseMessage(email));
        }

        return new UserProvisioningResult(
            UserProvisioningOutcome.Created,
            $"Created {email} as {role.Name}.",
            user.Id,
            schoolId.Value);
    }

    /// <summary>
    /// <b>Names the seeded development account when that is what the address collided with.</b>
    ///
    /// <para>
    /// A bare "that e-mail is in use" is the confusing failure for an account the operator never
    /// knowingly created: the Development seed writes one from a configuration key, so a developer who
    /// then runs <c>create-admin</c> with the same address is told their address is taken by something
    /// they cannot see and did not type. Saying which key produced it turns a dead end into an
    /// instruction.
    /// </para>
    /// </summary>
    private static string EmailInUseMessage(string email) =>
        string.Equals(email, SeedData.DevelopmentSuperAdminEmail, StringComparison.Ordinal)
            ? $"'{email}' already exists — it is the Development seed's SuperAdmin, created because " +
              $"'{SeedData.DevelopmentSuperAdminPasswordKey}' is configured on this host. Use a " +
              "different address, or clear that setting and drop the seeded user, but note that this " +
              "command refuses to reset an existing account's password by design."
            : $"'{email}' already exists. This command creates a user and never resets one — an " +
              "existing account's password is changed deliberately, not as a side effect of a create " +
              "that was expected to fail.";

    private async Task<Guid?> ResolveSchoolAsync(string? schoolCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schoolCode))
            return await _db.ResolveSchoolIdAsync(_school, ct);

        var code = schoolCode.Trim();

        // Schools carries no SchoolId and is not filtered, so this read is tenant-agnostic.
        return await _db.Schools.AsNoTracking()
            .Where(s => s.Code == code)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The column rules and the password policy, in one place so both callers get the same refusals.
    ///
    /// <para>
    /// E-mail shape is checked for an <c>@</c> with something either side and nothing more. A full
    /// RFC 5322 validator rejects addresses that real mail systems deliver to, and this value is not a
    /// contact route — it is a login identifier the institution's IT department assigned.
    /// </para>
    /// </summary>
    private bool Validate(string email, string fullName, string password, out string message)
    {
        message = "";

        if (email.Length == 0)
        {
            message = "An e-mail address is required — it is the login identifier.";
            return false;
        }

        if (email.Length > 256)
        {
            message = "The e-mail address is longer than the 256 characters Users.Email holds.";
            return false;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == email.Length - 1 || email.IndexOf('@', at + 1) >= 0)
        {
            message = $"'{email}' is not an e-mail address.";
            return false;
        }

        if (fullName.Length == 0)
        {
            message = "A full name is required.";
            return false;
        }

        if (fullName.Length > 200)
        {
            message = "The full name is longer than the 200 characters Users.FullName holds.";
            return false;
        }

        if (password.Length < MinimumPassword)
        {
            // The length is named, the password is not. Nothing in this method ever formats it.
            message = $"The password must be at least {MinimumPassword} characters.";
            return false;
        }

        if (string.Equals(password.Trim(), email, StringComparison.OrdinalIgnoreCase))
        {
            message = "The password must not be the e-mail address.";
            return false;
        }

        return true;
    }
}
