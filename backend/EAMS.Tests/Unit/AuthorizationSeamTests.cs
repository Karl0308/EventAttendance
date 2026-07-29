using System.Reflection;
using EAMS.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// ADR-001 D-6's outstanding follow-up: <em>"Make the no-op [HasPermission] unmistakably inert
/// (naming, XML doc, and a test asserting it denies nothing) so it cannot be mistaken for
/// enforcement at review time."</em>
///
/// <para>
/// The risk this guards is not that the attribute stops working — it does nothing, so it cannot.
/// It is that someone <em>makes</em> it look like enforcement: implements <c>IAuthorizationFilter</c>
/// "to finish the job", or flips <see cref="AuthorizationStatus.IsEnforced"/> to <c>true</c> ahead
/// of §11 actually landing. Either would make every decorated endpoint read as protected while the
/// API stayed open. These tests fail loudly on both, and the live "an undecorated, uncredentialed
/// request still gets 200" proof is in the integration suite where a real host exists.
/// </para>
///
/// <para>
/// <b>These tests are meant to be deleted by Phase 6</b>, not made to pass. When §11 lands, real
/// enforcement makes every assertion here false — that failure is the signal to remove this file
/// and replace it with tests that assert access is actually denied.
/// </para>
/// </summary>
public class AuthorizationSeamTests
{
    private static readonly Assembly ApiAssembly = typeof(AuthorizationStatus).Assembly;

    [Fact]
    public void Authorization_is_reported_as_not_enforced()
    {
        Assert.False(
            AuthorizationStatus.IsEnforced,
            "AuthorizationStatus.IsEnforced went true. Either Technical Plan §11 landed — in which " +
            "case delete AuthorizationSeamTests and assert real denial instead — or the " +
            "[assembly: AuthorizationNotEnforced] marker was removed while the API is still open, " +
            "which is the failure ADR-001 D-6 exists to prevent.");
    }

    [Fact]
    public void The_api_assembly_still_carries_the_not_enforced_marker() =>
        Assert.NotNull(ApiAssembly.GetCustomAttribute<AuthorizationNotEnforcedAttribute>());

    /// <summary>
    /// The structural argument, and the strongest one available: the attribute implements no
    /// interface at all. ASP.NET Core can only act on a type through <see cref="IFilterMetadata"/>,
    /// <see cref="IAuthorizeData"/> or an <see cref="IAuthorizationRequirement"/>; a type with an
    /// empty interface set cannot be reached by any of those paths, whatever a reviewer assumes
    /// from the name. Asserting on the whole set rather than on three named interfaces means adding
    /// a fourth one still fails this test.
    /// </summary>
    [Fact]
    public void The_attribute_implements_no_interfaces_at_all()
    {
        var implemented = typeof(HasPermissionNotEnforcedAttribute).GetInterfaces();

        Assert.True(
            implemented.Length == 0,
            "[HasPermissionNotEnforced] now implements " +
            string.Join(", ", implemented.Select(i => i.Name)) +
            ". It is documented and reviewed as structurally incapable of enforcing anything. If " +
            "enforcement is being built, rename the attribute in the same change (ADR-001 D-6) so " +
            "no call site keeps reading 'NotEnforced' while it denies requests.");
    }

    [Fact]
    public void The_attribute_is_a_plain_attribute_with_no_mvc_base_type() =>
        Assert.Equal(typeof(Attribute), typeof(HasPermissionNotEnforcedAttribute).BaseType);

    /// <summary>
    /// The permission string is recorded, and only recorded. Constructing the attribute is the
    /// whole of its behaviour; there is nothing to invoke.
    /// </summary>
    [Fact]
    public void The_attribute_records_its_permission_code_and_does_nothing_with_it()
    {
        var attribute = new HasPermissionNotEnforcedAttribute("attendance.capture");

        Assert.Equal("attendance.capture", attribute.Permission);

        // Property accessors excluded — the point is that there is no behaviour to call, and
        // `get_Permission` is the recording, not behaviour.
        Assert.DoesNotContain(
            typeof(HasPermissionNotEnforcedAttribute)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            m => !m.IsSpecialName);
    }

    /// <summary>
    /// <b>Exactly four actions in EAMS.Api carry a real authorization attribute, and this test names
    /// them.</b> It used to assert <em>zero</em>; Phase 4b (Phase 4a design, D-28) gated the capture
    /// surface with a device key, so the assertion moved from "none" to "these, and nothing else", and
    /// Phase 4d added the fourth and last of D-28's list when the batch endpoint arrived.
    ///
    /// <para>
    /// <b>The live endpoint added in the same phase is deliberately <em>not</em> here.</b>
    /// <c>GET /attendance/live/{eventId}</c> is a dashboard read, and the only scheme that exists is the
    /// device key, which §11 scopes to <c>attendance.capture</c> — so gating it would have handed the
    /// admin SPA a capture-scoped credential in order to watch attendance. It declares
    /// <c>attendance.read</c> through the inert attribute and stays open under ADR-001 D-6 with the rest
    /// of the admin surface. This list is exactly the endpoints a <em>device</em> authenticates.
    /// </para>
    ///
    /// <para>
    /// The change of shape is the important part. "None are gated" is a property that stops being true
    /// the first time anyone gates anything, and then the natural repair is to delete the test —
    /// which removes the only thing standing between "we gated one more endpoint deliberately" and
    /// "an <c>[Authorize]</c> arrived by copy-paste while the API still reports itself open". An
    /// allow-list keeps failing usefully: adding a fourth gated endpoint is a one-line, deliberate
    /// edit here, and adding one by accident is a red build.
    /// </para>
    ///
    /// <para>
    /// <c>AuthorizationStatus.IsEnforced</c> stays <c>false</c> throughout, and that is not a
    /// contradiction: §11 is permission-based RBAC over human users, and none of it exists. Four
    /// endpoints authenticate one principal type. The flag means "§11 has landed", not "nothing is
    /// gated" — which is why the startup warning now names the gated endpoints rather than claiming
    /// every endpoint is open.
    /// </para>
    ///
    /// <para>
    /// <b>Still meant to be deleted by Phase 6</b>, along with the rest of this file.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_the_device_capture_endpoints_are_gated_by_a_real_authorization_attribute()
    {
        string[] expected =
        [
            "AttendanceController.Tap",
            "AttendanceController.TapBatch",
            "DevicesController.Heartbeat",
            "StudentsController.ByCard",
        ];

        var gated = ApiAssembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MemberInfo>()
                .Append(t))
            .Where(m => m.GetCustomAttributes().Any(a => a is IAuthorizeData or IAuthorizationFilter or IAsyncAuthorizationFilter))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, gated);
    }

    /// <summary>
    /// Every gated action also declares the permission it demands through the inert attribute, and the
    /// two agree about which permission that is.
    ///
    /// <para>
    /// <b>Why keep the inert attribute on an endpoint that is genuinely enforced.</b> It is the audit
    /// trail ADR-001 D-6 created: Phase 6 renames <c>[HasPermissionNotEnforced]</c> to
    /// <c>[HasPermission]</c> and the compiler then points at every decorated endpoint so each is
    /// looked at on the day enforcement becomes real. Dropping it from the three endpoints that were
    /// enforced first would put a hole in exactly that list — and it would be the least visible kind of
    /// hole, because those three are the ones a reader is least likely to check.
    /// </para>
    ///
    /// <para>
    /// The equality assertion is what stops the two drifting: an <c>[Authorize(Policy = "x")]</c> over
    /// a <c>[HasPermissionNotEnforced("y")]</c> would enforce one permission while documenting another,
    /// and Phase 6's rename would then silently swap which one applies.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_gated_action_declares_the_same_permission_through_the_inert_attribute()
    {
        var gated = ApiAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => new
            {
                Method = m,
                Authorize = m.GetCustomAttributes(inherit: true).OfType<AuthorizeAttribute>().ToList(),
            })
            .Where(x => x.Authorize.Count > 0)
            .ToList();

        Assert.NotEmpty(gated);

        foreach (var action in gated)
        {
            var name = $"{action.Method.DeclaringType?.Name}.{action.Method.Name}";

            var declared = action.Method
                .GetCustomAttributes(typeof(HasPermissionNotEnforcedAttribute), inherit: true)
                .Cast<HasPermissionNotEnforcedAttribute>()
                .Select(a => a.Permission)
                .ToList();

            Assert.True(
                declared.Count > 0,
                $"{name} carries [Authorize] but no [HasPermissionNotEnforced]. The inert attribute is " +
                "the list Phase 6's rename walks; an enforced endpoint missing from it is the one " +
                "least likely to be noticed.");

            // A policy-less [Authorize] is the hole this loop used to have: OfType<string>() silently
            // dropped nulls, so an attribute with no Policy iterated zero times and passed.
            //
            // That is not a theoretical gap on this API, and the reason is DeviceKeyHandler's own
            // design. It returns AuthenticateResult.Success for Revoked and DeviceInactive — on
            // purpose, so those become 403 rather than 401 — and ASP.NET Core's default policy is only
            // RequireAuthenticatedUser(). So a bare [Authorize] on a gated endpoint would admit a
            // revoked key and a deactivated device, which is precisely the pair the 403 exists to
            // refuse. All four endpoints name a policy; the risk was always the *next* gated one, and
            // Phase 4d's POST /attendance/tap/batch is the one this assertion was written ahead of.
            Assert.All(action.Authorize, attribute => Assert.True(
                attribute.Policy is not null,
                $"{name} carries an [Authorize] with no Policy. The default policy is only " +
                "RequireAuthenticatedUser(), and a revoked or deactivated device key authenticates " +
                "successfully by design — so a policy-less gate admits exactly the credentials the " +
                $"403 exists to refuse. Name a policy ('{EamsPermissions.AttendanceCapture}')."));

            foreach (var policy in action.Authorize.Select(a => a.Policy).OfType<string>())
            {
                Assert.True(
                    declared.Contains(policy),
                    $"{name} enforces policy '{policy}' but declares [{string.Join(", ", declared)}]. " +
                    "The enforced permission and the documented one must be the same string.");
            }
        }
    }
}
