namespace EAMS.Application.Abstractions;

/// <summary>
/// <b>Technical Plan §11's four role names.</b> The values stored in <c>Roles.Name</c>, granted by
/// the seed, named by the <c>create-admin</c> console command, and matched by every later
/// role-assignment surface.
///
/// <para>
/// <b>They live in the Application layer rather than beside the grant matrix, because three layers
/// need them and only one of those may see the others.</b> <c>EAMS.Api</c> owns the matrix (a grant is
/// a statement about endpoints, and the endpoints are there); <c>EAMS.Infrastructure</c> has to name
/// one role when it seeds the Development administrator; and neither may reference the other — the
/// layering rule runs Api → Application → Domain, with Infrastructure implementing Application. The
/// shared vocabulary belongs at the point they already have in common. The alternative was a bare
/// <c>"SuperAdmin"</c> literal in the seeder, which is exactly the drift
/// <c>EamsPermissions</c> was created to end one layer up: a literal compiles, seeds a user with no
/// role, and is invisible until somebody cannot log in.
/// </para>
/// </summary>
public static class EamsRoleNames
{
    /// <summary>
    /// The installation's owner. Holds every code a human principal may hold — see
    /// <c>EamsRoles.HumanAssignable</c>, which is every registry code except
    /// <c>attendance.capture</c>.
    /// </summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>
    /// The school's administrator. <b>Holds exactly the same codes as <see cref="SuperAdmin"/>,
    /// deliberately and not as an unfinished draft.</b>
    ///
    /// <para>
    /// §4.11's <c>Roles</c> table carries no <c>SchoolId</c>. A cross-school SuperAdmin is therefore
    /// not expressible in this schema at all, and a matrix that subtracted something from
    /// <c>SchoolAdmin</c> to imply the difference would be minting a capability that does not exist —
    /// a distinction visible in a seed and in nothing that enforces anything. The two differ today in
    /// name and intent only. When multi-tenancy becomes operational the divergence arrives as a
    /// cross-school permission that has to be <em>added</em>, not as one removed from here.
    /// </para>
    ///
    /// <para>
    /// It is also why <c>create-admin</c> defaults to this role rather than to
    /// <see cref="SuperAdmin"/>: the two are equivalent in what they permit, so defaulting to the
    /// wider-sounding name would advertise a capability the schema cannot express.
    /// </para>
    /// </summary>
    public const string SchoolAdmin = "SchoolAdmin";

    /// <summary>
    /// Runs events. Reads the roster and the academic structure, authors events, and records
    /// attendance — including §6.4's manual override, which is the whole reason the role exists.
    ///
    /// <para>
    /// <b>What it deliberately does not hold.</b> <c>students.write</c>, because the roster is
    /// import-only and an organizer editing a student would be editing something the next import
    /// overwrites. <c>sis.import</c> and <c>academic.write</c>, because both redefine the institution's
    /// own structure: a term made current by the wrong person points every subsequent import and every
    /// default at the wrong semester (ADR-001 D-53). <c>devices.read</c> and <c>devices.write</c>,
    /// because <c>devices.write</c> mints capture credentials — §11's most consequential admin code —
    /// and reading the device list is the reconnaissance half of that.
    /// </para>
    /// </summary>
    public const string Organizer = "Organizer";

    /// <summary>
    /// Reads, and writes nothing at all: exactly <see cref="Organizer"/>'s four reads. Deliberately
    /// stated as its own list rather than derived as "Organizer minus the writes", so that granting
    /// Organizer something new does not silently widen Viewer.
    /// </summary>
    public const string Viewer = "Viewer";

    /// <summary>The four, for a command or an admin surface that has to validate one.</summary>
    public static IReadOnlyList<string> All { get; } = [SuperAdmin, SchoolAdmin, Organizer, Viewer];
}
