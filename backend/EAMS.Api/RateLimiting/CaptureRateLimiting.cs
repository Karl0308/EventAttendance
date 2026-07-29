using System.Globalization;
using System.Threading.RateLimiting;
using EAMS.Api.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace EAMS.Api.RateLimiting;

/// <summary>
/// Technical Plan §14: "rate limiting on <c>/auth</c> and <c>/attendance/tap</c>" (Phase 4a design,
/// D-28).
///
/// <para>
/// <b>Partitioned by <c>device_id</c>, which is why the limiter runs after authentication rather than
/// before it.</b> The usual ordering advice — limiter first, so a brute-force attempt never reaches
/// credential validation — is right when the partition key is an IP. It is not available here: the
/// thing worth limiting is a device, and "which device is this?" is exactly what authentication
/// answers. The cost being conceded is one index seek and one SHA-256 per rejected request, which is
/// not a meaningful amplification target; the cost being avoided is every kiosk behind one campus NAT
/// sharing a single bucket, which would take the whole gym down when one handset misbehaves.
/// Unauthenticated requests still fall back to the remote address, so the endpoint is not unlimited
/// before a key is presented.
/// </para>
///
/// <para>
/// <b>The built-in limiter is per-instance, so behind several SaaS instances (§13 Option A) the real
/// ceiling is <c>Limit × instances</c> and a device's requests are not guaranteed to land on the same
/// one.</b> That is accepted rather than overlooked: the purpose here is friction and detection — a
/// runaway client shows up as 429s in the logs instead of as a silent write storm — not a contractual
/// quota. A hard quota needs a shared store (Redis, §14's cache), and buying that before there is a
/// second instance would be inventing infrastructure for a deployment that does not exist.
/// </para>
/// </summary>
public static class CaptureRateLimiting
{
    /// <summary>The policy name referenced by <c>[EnableRateLimiting]</c> on the capture endpoints.</summary>
    public const string PolicyName = "device-capture";

    /// <summary>
    /// The machine-readable token on a 429 body. Deliberately outside the frozen tap-outcome list in
    /// <c>docs/api/attendance-contract-handoff.md</c> — that list describes what happened to a tap, and
    /// a rate-limited request never became one.
    /// </summary>
    public const string RateLimitedCode = "RateLimited";

    /// <summary>
    /// Requests per <see cref="Window"/> per device. A kiosk with a queue of students taps perhaps once
    /// a second, so this is roughly ten times a busy device's real rate — high enough that no honest
    /// client ever sees a 429, low enough that a client stuck in a retry loop is visible within a
    /// minute. Deliberately generous: a limiter that fires on legitimate traffic gets raised by
    /// whoever is on call, and then it is not a limiter.
    /// </summary>
    public const int PermitsPerWindow = 600;

    /// <summary>The fixed window. Matches the unit an operator thinks in ("taps per minute").</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The partition key prefix for an unauthenticated caller. Prefixed rather than bare so an IP
    /// address can never collide with a device id in the partition table.
    /// </summary>
    private const string AnonymousPartitionPrefix = "ip:";

    private const string DevicePartitionPrefix = "device:";

    public static void AddCaptureRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, PartitionFor);

            options.OnRejected = WriteRejectionAsync;
        });

    /// <summary>
    /// What a rejected caller actually receives: <c>Retry-After</c>, and an RFC 7807 body carrying a
    /// <c>traceId</c> and a stable <c>code</c>.
    ///
    /// <para>
    /// <b>Retry-After is the only thing that turns a 429 from "stop" into "stop until".</b> Without it
    /// an offline client's only sane strategy is to back off by a number it invented, and §8.2's queue
    /// is precisely the caller that will be holding a backlog when this fires.
    /// </para>
    ///
    /// <para>
    /// <b>The problem body is not decoration.</b> §6's header declares RFC 7807 for errors and every
    /// other surface on this API honours it — <c>TracedProblemDetailsFactory</c> for validation 400s,
    /// the controllers for their outcome mappings, <c>DeviceKeyHandler</c> for 401 and 403.
    /// <c>AttendanceController</c> additionally declares <c>[ProducesResponseType(429)]</c>, which
    /// promised a shape the framework's bare 429 never produced. A caller with one accessor —
    /// <c>body.code</c> — and one handle to quote back — <c>traceId</c> — must not lose both on the one
    /// status a runaway client sees most.
    /// </para>
    ///
    /// <para>
    /// Built through <see cref="ProblemDetailsFactory"/> so the <c>traceId</c> is stamped in exactly
    /// one place, the same seam the controllers and the authentication handler use.
    /// </para>
    /// </summary>
    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken ct)
    {
        var http = context.HttpContext;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
        }

        // Set explicitly rather than relying on the middleware having already applied
        // RejectionStatusCode: writing a body against whatever status happens to be there is how a 429
        // ships as a 200 with an error payload.
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        var factory = http.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateProblemDetails(
            http,
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many capture requests.",
            detail:
                $"This device is limited to {PermitsPerWindow} capture requests per " +
                $"{Window.TotalMinutes:0} minute(s). Wait for the period named by Retry-After and " +
                "resend; nothing was recorded.",
            instance: http.Request.GetEncodedPathAndQuery());

        problem.Extensions["code"] = RateLimitedCode;

        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });

        _ = ct;
    }

    private static RateLimitPartition<string> PartitionFor(HttpContext context)
    {
        var deviceId = context.User.FindFirst(EamsClaimTypes.DeviceId)?.Value;

        var key = deviceId is not null
            ? DevicePartitionPrefix + deviceId
            : AnonymousPartitionPrefix + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitsPerWindow,
            Window = Window,
            QueueLimit = 0, // Reject rather than queue: a queued tap is a tap the client thinks is in flight.
            AutoReplenishment = true,
        });
    }
}
