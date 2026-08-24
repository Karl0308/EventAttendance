using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <b><see cref="EamsRoles.ReferenceData"/> — the approved §11 grant matrix, pinned cell by cell.</b>
/// This closes ADR-001 D-44 and D-53, and the numbers below are the approved ones rather than
/// whatever the code currently produces.
///
/// <para>
/// <b>It is not redundant with <c>RbacSeedTests</c>.</b> That one asserts the database ends up
/// holding this matrix — it would pass if the matrix itself were wrong, as long as the seeder wrote
/// it faithfully. This one asserts the matrix is the approved one, and would pass if the seeder were
/// broken. Both are needed because the two ways of getting it wrong are unrelated.
/// </para>
/// </summary>
public class RbacGrantMatrixTests
{
    private static IReadOnlyList<string> GrantsFor(string roleName) =>
        EamsRoles.ReferenceData.Roles.Single(r => r.Name == roleName).PermissionCodes;

    /// <summary>
    /// <b>The registry has twelve codes.</b> A literal, so that adding or removing one is a decision
    /// someone makes here as well as there — every count below is relative to this number and a silent
    /// thirteenth code would quietly widen SuperAdmin.
    /// </summary>
    [Fact]
    public void The_registry_declares_twelve_permission_codes()
    {
        Assert.Equal(12, EamsPermissions.All.Count);
        Assert.Equal(12, EamsPermissions.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_four_roles_are_seeded_and_no_others()
    {
        Assert.Equal(
            new[]
            {
                EamsRoleNames.SuperAdmin,
                EamsRoleNames.SchoolAdmin,
                EamsRoleNames.Organizer,
                EamsRoleNames.Viewer,
            }.Order(StringComparer.Ordinal),
            EamsRoles.ReferenceData.Roles.Select(r => r.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every permission the registry declares gets a <c>Permissions</c> row — all twelve, including
    /// the one no role holds. The code has to exist for a device key to carry it; what it must not
    /// have is a grant.
    /// </summary>
    [Fact]
    public void Every_registry_code_is_seeded_as_a_permission_row()
    {
        Assert.Equal(
            EamsPermissions.All.Order(StringComparer.Ordinal),
            EamsRoles.ReferenceData.Permissions.Select(p => p.Code).Order(StringComparer.Ordinal));
    }

    // -------------------------------------------------------------------- the totals, per the matrix

    [Theory]
    [InlineData(EamsRoleNames.SuperAdmin, 11)]
    [InlineData(EamsRoleNames.SchoolAdmin, 11)]
    [InlineData(EamsRoleNames.Organizer, 6)]
    [InlineData(EamsRoleNames.Viewer, 4)]
    public void Each_role_holds_exactly_the_approved_number_of_permissions(string role, int expected)
    {
        var granted = GrantsFor(role);

        Assert.Equal(expected, granted.Count);
        Assert.Equal(expected, granted.Distinct(StringComparer.Ordinal).Count());
    }

    // -------------------------------------------------------------- attendance.capture: no grants

    /// <summary>
    /// <b>No role grants <c>attendance.capture</c>. Asserted as a positive statement, deliberately.</b>
    ///
    /// <para>
    /// An empty column in a grant matrix reads as an omission to whoever maintains the seeder next,
    /// and "SuperAdmin is missing one" is exactly the kind of tidy-up that gets done without a second
    /// thought. Stating the absence as a test with this explanation attached is what turns filling it
    /// in from a cleanup into a failing build.
    /// </para>
    ///
    /// <para>
    /// The reason: <c>attendance.capture</c> is the <em>device's</em> permission. §11 scopes a kiosk
    /// API key to it and to nothing else, and it means "a card was presented at this reader". A human
    /// principal holding it could post taps indistinguishable — in the record and in every report —
    /// from a real card presentation. An organizer records what they <em>decided</em>, through
    /// <c>attendance.write</c> and the audited <c>POST /attendance/manual</c> override, which is
    /// precisely the distinction <see cref="EamsPermissions.AttendanceWrite"/>'s own remarks draw.
    /// </para>
    /// </summary>
    [Fact]
    public void No_role_grants_attendance_capture()
    {
        var holders = EamsRoles.ReferenceData.Roles
            .Where(r => r.PermissionCodes.Contains(EamsPermissions.AttendanceCapture, StringComparer.Ordinal))
            .Select(r => r.Name)
            .ToList();

        Assert.True(
            holders.Count == 0,
            $"'{EamsPermissions.AttendanceCapture}' is granted to: {string.Join(", ", holders)}. It " +
            "is the device's permission and no role may hold it — a human carrying it could post " +
            "taps indistinguishable from a real card presentation at a reader. An organizer override " +
            $"goes through '{EamsPermissions.AttendanceWrite}'. If this is a deliberate reversal, " +
            "register it as a new ADR decision rather than adding the grant.");
    }

    /// <summary>
    /// The other half: the code is still declared and still seeded. Deleting it would be the opposite
    /// mistake — it is the policy name of the gated capture endpoints and the claim a device key
    /// carries, so removing it would unauthenticate every kiosk.
    /// </summary>
    [Fact]
    public void Attendance_capture_is_still_a_declared_and_seeded_permission()
    {
        Assert.Contains(EamsPermissions.AttendanceCapture, EamsPermissions.All);
        Assert.Contains(
            EamsPermissions.AttendanceCapture,
            EamsRoles.ReferenceData.Permissions.Select(p => p.Code));

        Assert.DoesNotContain(EamsPermissions.AttendanceCapture, EamsRoles.HumanAssignable);
    }

    // ------------------------------------------------------------------------- the matrix, cell by cell

    [Fact]
    public void SuperAdmin_and_SchoolAdmin_hold_identically_the_human_assignable_codes()
    {
        var superAdmin = GrantsFor(EamsRoleNames.SuperAdmin).Order(StringComparer.Ordinal).ToList();
        var schoolAdmin = GrantsFor(EamsRoleNames.SchoolAdmin).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(superAdmin, schoolAdmin);

        // §4.11's Roles table has no SchoolId, so a cross-school SuperAdmin is not expressible in this
        // schema. Subtracting something from SchoolAdmin to imply the difference would mint a
        // capability that does not exist — visible in a seed and in nothing that enforces anything.
        Assert.Equal(
            EamsPermissions.All
                .Where(c => c != EamsPermissions.AttendanceCapture)
                .Order(StringComparer.Ordinal),
            superAdmin);
    }

    [Fact]
    public void Organizer_holds_exactly_the_six_event_running_codes()
    {
        Assert.Equal(
            new[]
            {
                EamsPermissions.StudentsRead,
                EamsPermissions.EventsRead,
                EamsPermissions.EventsWrite,
                EamsPermissions.AttendanceRead,
                EamsPermissions.AttendanceWrite,
                EamsPermissions.AcademicRead,
            }.Order(StringComparer.Ordinal),
            GrantsFor(EamsRoleNames.Organizer).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Viewer_holds_exactly_the_four_reads()
    {
        Assert.Equal(
            new[]
            {
                EamsPermissions.StudentsRead,
                EamsPermissions.EventsRead,
                EamsPermissions.AttendanceRead,
                EamsPermissions.AcademicRead,
            }.Order(StringComparer.Ordinal),
            GrantsFor(EamsRoleNames.Viewer).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The named exclusions, stated as their own assertion because each one is a decision somebody
    /// might reverse casually. <c>students.write</c> because the roster is import-only;
    /// <c>sis.import</c> and <c>academic.write</c> because both redefine the institution's structure
    /// and a term made current by the wrong person misdirects every later import (D-53);
    /// <c>devices.*</c> because <c>devices.write</c> mints capture credentials and reading the device
    /// list is the reconnaissance half of that.
    /// </summary>
    [Theory]
    [InlineData(EamsPermissions.StudentsWrite)]
    [InlineData(EamsPermissions.DevicesRead)]
    [InlineData(EamsPermissions.DevicesWrite)]
    [InlineData(EamsPermissions.SisImport)]
    [InlineData(EamsPermissions.AcademicWrite)]
    public void Neither_Organizer_nor_Viewer_holds_an_administrative_code(string code)
    {
        Assert.DoesNotContain(code, GrantsFor(EamsRoleNames.Organizer));
        Assert.DoesNotContain(code, GrantsFor(EamsRoleNames.Viewer));
    }

    /// <summary>
    /// Viewer writes nothing at all — asserted by the shape of the code rather than by re-listing it,
    /// so a future write permission with a new name is caught without anyone remembering to add it
    /// here.
    /// </summary>
    [Fact]
    public void Viewer_holds_no_code_that_ends_in_write()
    {
        Assert.DoesNotContain(
            GrantsFor(EamsRoleNames.Viewer),
            code => code.EndsWith(".write", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every granted code is a registry code. This is what makes "derived from the constants, never
    /// from literals" checkable — a bare string in the matrix compiles, seeds a grant nothing demands,
    /// and stays invisible until an endpoint is unreachable.
    /// </summary>
    [Fact]
    public void Every_granted_code_is_a_registry_code()
    {
        var known = EamsPermissions.All.ToHashSet(StringComparer.Ordinal);

        var unknown = EamsRoles.ReferenceData.Roles
            .SelectMany(r => r.PermissionCodes.Select(c => (r.Name, Code: c)))
            .Where(g => !known.Contains(g.Code))
            .Select(g => $"{g.Name} → '{g.Code}'")
            .ToList();

        Assert.True(
            unknown.Count == 0,
            "These grants name a code that is not on EamsPermissions:\n  " +
            string.Join("\n  ", unknown));
    }
}
