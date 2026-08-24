using EAMS.Application.Abstractions;

namespace EAMS.Api.Authorization;

/// <summary>
/// <b>The grant matrix: which of §11's four roles holds which permission code.</b> This closes
/// ADR-001 D-44 and D-53, both of which minted a code and deliberately left "which role holds it" to
/// the phase that would have to live with the answer.
///
/// <para>
/// <b>Why it is here and not in the seeder.</b> A grant is a statement about endpoints — "an
/// organizer may write attendance but may not mint a device key" — and the endpoints and their codes
/// live in this assembly, where <c>PermissionRegistryTests</c> already holds the registry and the call
/// sites to each other in both directions. <c>EAMS.Infrastructure</c> cannot see this assembly (the
/// layering rule runs the other way) and should not want to: a seeder that knew what
/// <c>sis.import</c> meant would be a seeder with an opinion about authorization. The names
/// themselves are shared vocabulary and live in <see cref="EamsRoleNames"/>, at the layer all three
/// projects have in common.
/// </para>
///
/// <para>
/// <b>All four are system roles.</b> §4.11 gives <c>Roles</c> an <c>IsSystem</c> flag and the seed
/// sets it on every row it creates. It is what a future role-administration surface will read to
/// refuse deletion — a product whose four built-in roles can be deleted is a product that can be
/// locked out of itself — and it is set at creation, because retrofitting it means deciding after the
/// fact which of an operator's rows were ours.
/// </para>
/// </summary>
public static class EamsRoles
{
    /// <summary>
    /// <b>Every registry code except <see cref="EamsPermissions.AttendanceCapture"/>, which no role
    /// in this system holds — not SuperAdmin, not anyone.</b>
    ///
    /// <para>
    /// <b>An empty column in a grant matrix reads as an omission, so it is stated here as a
    /// decision.</b> §11 scopes a kiosk's device API key to <c>attendance.capture</c> and to nothing
    /// else: it is the permission that means "a card was presented at this reader". A human principal
    /// holding it could post taps that are indistinguishable — in the record and in every report —
    /// from a real card presentation, which is exactly the line
    /// <see cref="EamsPermissions.AttendanceWrite"/>'s own remarks draw. A kiosk records what a card
    /// presented; an organizer records what they decided, through
    /// <see cref="EamsPermissions.AttendanceWrite"/> and <c>POST /attendance/manual</c>, which is
    /// audited as an override precisely so the two are never confused. Granting the capture code to an
    /// administrator "for completeness" would erase that distinction in the one direction the data
    /// cannot be recovered from afterwards.
    /// </para>
    ///
    /// <para>
    /// <b>The code still exists, is still seeded into <c>Permissions</c>, and is still enforced</b> —
    /// it is the policy name of the gated capture endpoints and the claim a device key carries. What
    /// has no row is any <c>RolePermissions</c> grant of it. <c>RbacSeedTests</c> asserts that as a
    /// positive statement rather than as an absence, so filling the gap in is a failing test rather
    /// than a quiet widening.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> HumanAssignable { get; } =
    [
        .. EamsPermissions.All.Where(
            code => !string.Equals(code, EamsPermissions.AttendanceCapture, StringComparison.Ordinal)),
    ];

    /// <summary>
    /// The §4.11 rows every environment must have, handed to <c>InitializeEamsDatabaseAsync</c> by
    /// the composition root.
    ///
    /// <para>
    /// <b>Every code is a reference to a constant on <see cref="EamsPermissions"/>, never a
    /// literal.</b> A literal here compiles, seeds a grant nothing demands, and stays invisible until
    /// an endpoint is unreachable — the same failure <c>PermissionRegistryTests</c> exists to stop one
    /// layer up. <c>RbacSeedTests</c> asserts that every granted code is a registry code, so a literal
    /// that crept in is a failing test rather than a dead grant.
    /// </para>
    /// </summary>
    public static RbacReferenceData ReferenceData { get; } = new(
        Permissions: [.. EamsPermissions.All.Select(code => new RbacPermissionSeed(code, null))],
        Roles:
        [
            new RbacRoleSeed(
                EamsRoleNames.SuperAdmin,
                "Full access to every permission a human principal may hold.",
                HumanAssignable),

            new RbacRoleSeed(
                EamsRoleNames.SchoolAdmin,
                "Administers one school: roster imports, terms, devices, events and attendance.",
                HumanAssignable),

            new RbacRoleSeed(
                EamsRoleNames.Organizer,
                "Runs events: authors them, records attendance, reads the roster and academic structure.",
                [
                    EamsPermissions.StudentsRead,
                    EamsPermissions.EventsRead,
                    EamsPermissions.EventsWrite,
                    EamsPermissions.AttendanceRead,
                    EamsPermissions.AttendanceWrite,
                    EamsPermissions.AcademicRead,
                ]),

            new RbacRoleSeed(
                EamsRoleNames.Viewer,
                "Read-only access to the roster, events, attendance and academic structure.",
                [
                    EamsPermissions.StudentsRead,
                    EamsPermissions.EventsRead,
                    EamsPermissions.AttendanceRead,
                    EamsPermissions.AcademicRead,
                ]),
        ]);
}
