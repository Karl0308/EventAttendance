using EAMS.Api.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using DomainDeviceKey = EAMS.Domain.DeviceKey;

namespace EAMS.Api.Authentication;

/// <summary>
/// <b>One route, two principals: <c>GET /events</c>.</b> An operator lists every event with
/// <c>events.read</c>; the capture app lists the events it can tap for with its device key, as it did
/// before the admin surface was gated. The app's event picker was built against this route, so it keeps
/// working without a release.
///
/// <para>
/// <b>A forwarding scheme rather than naming both schemes on the <c>[Authorize]</c>.</b> Naming both
/// makes the pipeline challenge <em>each</em> scheme on a refusal, and both handlers write an RFC 7807
/// body — the second write lands on a response that has already started. Forwarding by the header the
/// caller actually sent picks exactly one handler: <c>DeviceKey …</c> goes to the device scheme and
/// its 401/403 codes, anything else (including no header) goes to Bearer.
/// </para>
///
/// <para>
/// <b>What a device gets is narrower than what an operator gets, and the controller enforces that, not
/// this policy.</b> <c>EventsController.List</c> answers a device with its school's <c>Open</c> events
/// only, whatever <c>status</c> it asks for.
/// </para>
/// </summary>
public static class DeviceKeyOrBearer
{
    public const string AuthenticationScheme = "DeviceKeyOrBearer";

    /// <summary>
    /// <c>events.read</c> for a person, or <c>attendance.capture</c> for a device. Not itself a §4.11 code
    /// — it is the union of two — so it is named so that nobody mistakes it for one.
    /// </summary>
    public const string EventsListPolicy = "events.read-or-attendance.capture";

    /// <summary>The handler a request is authenticated by, chosen from the credential it presents.</summary>
    public static string SelectScheme(HttpContext context) =>
        context.Request.Headers.Authorization.Any(value =>
            value is not null
            && value.StartsWith(DomainDeviceKey.AuthenticationScheme + " ", StringComparison.OrdinalIgnoreCase))
            ? DomainDeviceKey.AuthenticationScheme
            : JwtBearerDefaults.AuthenticationScheme;

    /// <summary>
    /// A revoked key or an inactive device authenticates with no <c>perm</c> claim (see
    /// <c>DeviceKeyHandler</c>), so it fails this and is answered 403 — the same as on a capture route.
    /// </summary>
    public static void AddEventsListPolicy(AuthorizationOptions options) =>
        options.AddPolicy(
            EventsListPolicy,
            policy => policy
                .AddAuthenticationSchemes(AuthenticationScheme)
                .RequireAssertion(context =>
                    context.User.HasClaim(EamsClaimTypes.Permission, EamsPermissions.EventsRead)
                    || context.User.HasClaim(EamsClaimTypes.Permission, EamsPermissions.AttendanceCapture)));
}
