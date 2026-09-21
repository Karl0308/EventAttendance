using System.Reflection;
using EAMS.Api.Authentication;
using EAMS.Api.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;
using DeviceKey = EAMS.Domain.DeviceKey;

namespace EAMS.Tests.Unit;

/// <summary>
/// <b>Every action in EAMS.Api is gated, except the two that cannot be.</b> Technical Plan §11's
/// enforcement, asserted over the controllers themselves rather than over a hand-kept list of routes —
/// so an action added tomorrow without an <c>[Authorize]</c> is a red build, not an open endpoint.
///
/// <para>
/// <b>What each test holds.</b> Coverage: nothing outside <c>/auth/login</c> and
/// <c>/auth/refresh</c> is reachable without a credential. Scheme: the five capture routes take a
/// device key and nothing else does. Policy: every gate names a policy that exists, and it is the
/// permission the action declares through <see cref="HasPermissionNotEnforcedAttribute"/>. The live
/// proof that a request is actually refused is <c>AuthEnforcementTests</c>, where a real host exists.
/// </para>
/// </summary>
public class AuthorizationCoverageTests
{
    private static readonly Assembly ApiAssembly = typeof(EamsPermissions).Assembly;

    /// <summary>
    /// The only two actions a caller holding nothing may reach. Sign-in is how a credential is
    /// obtained, and refresh authenticates with its cookie rather than with a scheme.
    /// </summary>
    private static readonly string[] Anonymous =
    [
        "AuthController.Login",
        "AuthController.Refresh",
    ];

    /// <summary>
    /// The capture surface: the routes a kiosk or handset calls with its device key (D-28, D-46).
    /// Everything else that is gated takes a person's Bearer token, except <see cref="SharedGates"/>.
    /// </summary>
    private static readonly string[] DeviceKeyGated =
    [
        "AttendanceController.Tap",
        "AttendanceController.TapBatch",
        "DevicesController.Heartbeat",
        "EventManifestController.Manifest",
        "StudentsController.ByCard",
    ];

    /// <summary>
    /// Routes a person and a device both reach, through the forwarding scheme and a policy that accepts
    /// either one's permission. Exactly one: <c>GET /events</c>, the capture app's event picker. A second
    /// entry is a deliberate edit here, because every entry widens what a device key can read.
    /// </summary>
    private static readonly Dictionary<string, (string Scheme, string Policy)> SharedGates = new(StringComparer.Ordinal)
    {
        ["EventsController.List"] = (DeviceKeyOrBearer.AuthenticationScheme, DeviceKeyOrBearer.EventsListPolicy),
    };

    private sealed record ApiAction(string Name, MethodInfo Method, IReadOnlyList<AuthorizeAttribute> Authorize)
    {
        public bool IsAuthSurface => Method.DeclaringType?.Name == "AuthController";

        public IReadOnlyList<string> Declared { get; } = Method
            .GetCustomAttributes(typeof(HasPermissionNotEnforcedAttribute), inherit: true)
            .Cast<HasPermissionNotEnforcedAttribute>()
            .Select(a => a.Permission)
            .ToList();
    }

    private static List<ApiAction> Actions() =>
    [
        .. ApiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().Any())
            .Select(m => new ApiAction(
                $"{m.DeclaringType?.Name}.{m.Name}",
                m,
                [
                    .. m.GetCustomAttributes(inherit: true).OfType<AuthorizeAttribute>(),
                    .. m.DeclaringType!.GetCustomAttributes(inherit: true).OfType<AuthorizeAttribute>(),
                ]))
            .OrderBy(a => a.Name, StringComparer.Ordinal),
    ];

    [Fact]
    public void Only_sign_in_and_refresh_are_reachable_without_a_credential()
    {
        var actions = Actions();
        Assert.NotEmpty(actions);

        var open = actions.Where(a => a.Authorize.Count == 0).Select(a => a.Name).ToList();

        Assert.True(
            open.SequenceEqual(Anonymous, StringComparer.Ordinal),
            $"Ungated actions: [{string.Join(", ", open)}]; expected exactly [{string.Join(", ", Anonymous)}]. " +
            "An action with no [Authorize] answers anyone who can reach the host. Add " +
            "[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = " +
            "EamsPermissions.X)] beside its [HasPermissionNotEnforced(EamsPermissions.X)].");
    }

    [Fact]
    public void The_capture_routes_take_a_device_key_and_every_other_gate_takes_a_bearer_token()
    {
        foreach (var action in Actions().Where(a => a.Authorize.Count > 0))
        {
            var expected = SharedGates.TryGetValue(action.Name, out var shared)
                ? shared.Scheme
                : DeviceKeyGated.Contains(action.Name, StringComparer.Ordinal)
                    ? DeviceKey.AuthenticationScheme
                    : JwtBearerDefaults.AuthenticationScheme;

            // Named per attribute rather than inferred from the default scheme. The default is the
            // device scheme, so an [Authorize] that named none would quietly accept a kiosk's key on
            // an operator's route — the cross-scheme escalation this assertion exists to refuse.
            Assert.All(action.Authorize, attribute => Assert.True(
                attribute.AuthenticationSchemes == expected,
                $"{action.Name} names scheme '{attribute.AuthenticationSchemes}', expected '{expected}'. " +
                "A Bearer gate on a capture route tells a kiosk to go and sign in; a DeviceKey gate on " +
                "an admin route lets a capture credential act as a person."));
        }
    }

    /// <summary>
    /// A gate names the permission it enforces, and that permission is the one the action declares.
    ///
    /// <para>
    /// <b>Why a policy is required, not just the scheme.</b> The default policy is only
    /// <c>RequireAuthenticatedUser()</c>, and <c>DeviceKeyHandler</c> returns success for a revoked key
    /// and a deactivated device on purpose so those become 403 rather than 401 — so a bare gate on a
    /// capture route would admit exactly the credentials the 403 exists to refuse, and a bare gate on
    /// an admin route would admit any signed-in operator whatever their role grants.
    /// </para>
    ///
    /// <para>
    /// The <c>/auth</c> actions are the one exemption: <c>/me</c>, <c>/logout</c> and
    /// <c>/change-password</c> demand "any signed-in person", which is a scheme requirement rather than
    /// a §4.11 grant, and they pay for that by naming Bearer (asserted above).
    /// </para>
    /// </summary>
    [Fact]
    public void Every_gate_names_the_permission_its_action_declares()
    {
        foreach (var action in Actions().Where(a => a.Authorize.Count > 0 && !a.IsAuthSurface))
        {
            if (SharedGates.TryGetValue(action.Name, out var shared))
            {
                // The union policy, declared as both codes it accepts.
                Assert.Equal(shared.Policy, Assert.Single(action.Authorize).Policy);
                Assert.Equal(
                    [EamsPermissions.AttendanceCapture, EamsPermissions.EventsRead],
                    action.Declared.Order(StringComparer.Ordinal));
                continue;
            }

            Assert.True(
                action.Declared.Count > 0,
                $"{action.Name} carries [Authorize] but no [HasPermissionNotEnforced]. PermissionRegistryTests " +
                "reads the declared code, so an undeclared one is invisible to the registry.");

            Assert.All(action.Authorize, attribute =>
            {
                Assert.True(
                    attribute.Policy is not null,
                    $"{action.Name} carries an [Authorize] with no Policy. The default policy is only " +
                    "RequireAuthenticatedUser(): any signed-in operator, or any revoked device key, passes it.");

                Assert.True(
                    action.Declared.Contains(attribute.Policy!, StringComparer.Ordinal),
                    $"{action.Name} enforces policy '{attribute.Policy}' but declares " +
                    $"[{string.Join(", ", action.Declared)}]. The enforced permission and the documented " +
                    "one must be the same string.");
            });
        }
    }

    /// <summary>
    /// Every named policy is one <c>Program.cs</c> registers: <see cref="EamsPermissions.AttendanceCapture"/>
    /// against the device scheme, and each of <see cref="EamsRoles.HumanAssignable"/> against Bearer. An
    /// unregistered policy is not a 403 — it is an <c>InvalidOperationException</c> and a 500 on every
    /// request to that route, which would read as the endpoint being broken rather than guarded.
    /// </summary>
    [Fact]
    public void Every_named_policy_is_one_the_host_registers()
    {
        foreach (var action in Actions())
        {
            foreach (var policy in action.Authorize.Select(a => a.Policy).OfType<string>())
            {
                var registered = SharedGates.TryGetValue(action.Name, out var shared)
                    ? policy == shared.Policy
                    : DeviceKeyGated.Contains(action.Name, StringComparer.Ordinal)
                        ? policy == EamsPermissions.AttendanceCapture
                        : EamsRoles.HumanAssignable.Contains(policy, StringComparer.Ordinal);

                Assert.True(
                    registered,
                    $"{action.Name} names policy '{policy}', which is not registered for its scheme. " +
                    "A device route may only name attendance.capture; a Bearer route may only name a " +
                    "code a role can hold.");
            }
        }
    }

    /// <summary>
    /// The declaring attribute still cannot enforce anything by itself. Enforcement lives in
    /// <c>[Authorize]</c>; if the attribute starts implementing a filter interface, a route could end up
    /// guarded twice by rules that disagree, or guarded by it alone in a way the tests above do not see.
    /// Collapse the pair into one enforcing attribute deliberately instead.
    /// </summary>
    [Fact]
    public void The_declaring_attribute_implements_no_interfaces_at_all()
    {
        Assert.Empty(typeof(HasPermissionNotEnforcedAttribute).GetInterfaces());
        Assert.Equal(typeof(Attribute), typeof(HasPermissionNotEnforcedAttribute).BaseType);
    }
}
