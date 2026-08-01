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
    /// The policy on <c>GET /attendance/live/{eventId}</c> (Phase 4d review).
    ///
    /// <para>
    /// <b>Separate from <see cref="PolicyName"/> because it limits a different thing for a different
    /// reason.</b> The capture policy protects writes and partitions by device, which requires an
    /// authenticated principal. The live endpoint is deliberately unauthenticated — it is a dashboard
    /// read, and the only credential that exists is scoped to <c>attendance.capture</c> — so there is
    /// no device to partition by and the remote address is the only key available.
    /// </para>
    ///
    /// <para>
    /// <b>It needs one at all because the endpoint is the most expensive read in the API and is
    /// designed to be called in a loop.</b> A single poll is not one indexed seek: it is the
    /// <c>MIN_ACTIVE_ROWVERSION()</c> scalar, the event read, the delta read, and four aggregate
    /// queries for the counters — two of which (<c>INTERSECT</c> and <c>EXCEPT</c> against the expected
    /// set) walk <c>EventGroups</c> into current section membership. Open, unlimited, and loop-shaped is
    /// a combination worth refusing even before anyone is trying.
    /// </para>
    /// </summary>
    public const string LivePolicyName = "attendance-live";

    /// <summary>
    /// Requests per <see cref="Window"/> per client address for the live endpoint.
    ///
    /// <para>
    /// <b>Deliberately permissive, and the number is derived rather than picked.</b> At the default
    /// five-second poll a dashboard makes twelve requests a minute, so this is room for roughly twenty
    /// dashboards behind one address — a plausible staff room, and far more than a campus NAT would
    /// legitimately need for a *dashboard*. It is a ceiling on a runaway client, not a quota: the
    /// failure it exists to prevent is one page stuck in a tight retry loop quietly costing the
    /// database six queries per iteration.
    /// </para>
    ///
    /// <para>
    /// The IP partition is the reason it cannot be tighter. Every dashboard behind one campus NAT
    /// shares this bucket, and a limiter that fires on legitimate traffic gets raised by whoever is on
    /// call — which is the same reasoning <see cref="PermitsPerWindow"/> records, applied to a key that
    /// is much coarser.
    /// </para>
    /// </summary>
    public const int LivePermitsPerWindow = 240;

    /// <summary>
    /// The policy on <c>GET /events/{id}/manifest</c> (D-46).
    ///
    /// <para>
    /// <b>A third policy rather than a reuse of either existing one, and both alternatives are wrong
    /// in a way worth naming.</b> Reusing <see cref="PolicyName"/> would let a manifest pull spend a
    /// kiosk's <em>tap</em> budget — a device that refreshes its cache aggressively would throttle the
    /// capture path, which is the one thing on this API that must not be throttled by anything a client
    /// does for its own convenience. Reusing <see cref="LivePolicyName"/> would partition by IP, and a
    /// campus NAT puts every handset in one bucket: the failure mode is a gym full of devices where the
    /// first two can pull and the rest cannot.
    /// </para>
    ///
    /// <para>
    /// Partitioned by <c>device_id</c>, like the capture policy, and for the same reason — which is
    /// available here precisely because this endpoint is device-gated.
    /// </para>
    /// </summary>
    public const string ManifestPolicyName = "device-manifest";

    /// <summary>
    /// Manifest pulls per <see cref="Window"/> per device.
    ///
    /// <para>
    /// <b>Two orders of magnitude below <see cref="PermitsPerWindow"/>, because this is a different
    /// shape of request.</b> A device pulls once before a session and then revalidates — the whole
    /// point of the ETag is that it should almost never need a body. Thirty a minute is a pull every
    /// two seconds, which is far past any honest client and still leaves room for one that retries
    /// after a network wobble. The refusal costs the device nothing: it keeps the manifest it has, and
    /// no refusal from this endpoint ever stops tap capture.
    /// </para>
    ///
    /// <para>
    /// Note what a 304 does <em>not</em> save. It saves the device's radio, battery and parse; the
    /// server does the same work either way, because the version cannot be known without composing the
    /// content it hashes. So the budget is over pulls, not over bodies.
    /// </para>
    /// </summary>
    public const int ManifestPermitsPerWindow = 30;

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
            options.AddPolicy(LivePolicyName, LivePartitionFor);
            options.AddPolicy(ManifestPolicyName, ManifestPartitionFor);

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

        // Which policy rejected decides the prose. Every body carries the same `code`, so a client
        // branching on RateLimited is unaffected either way — but telling a dashboard that "this device
        // is limited to 600 capture requests" would send its reader looking for a kiosk.
        var policy = http.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

        var factory = http.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateProblemDetails(
            http,
            statusCode: StatusCodes.Status429TooManyRequests,
            title: policy switch
            {
                LivePolicyName => "Too many live-attendance polls.",
                ManifestPolicyName => "Too many manifest pulls.",
                _ => "Too many capture requests.",
            },
            detail: policy switch
            {
                LivePolicyName =>
                    $"This client address is limited to {LivePermitsPerWindow} live-attendance polls " +
                    $"per {Window.TotalMinutes:0} minute(s). Wait for the period named by Retry-After " +
                    "and poll again; honour the pollAfterSeconds in each response rather than polling " +
                    "as fast as the endpoint answers.",
                ManifestPolicyName =>
                    $"This device is limited to {ManifestPermitsPerWindow} manifest pulls per " +
                    $"{Window.TotalMinutes:0} minute(s). Wait for the period named by Retry-After and " +
                    "pull again. Keep the manifest you already have, and keep capturing — no refusal " +
                    "from that endpoint ever stops tap capture.",
                _ =>
                    $"This device is limited to {PermitsPerWindow} capture requests per " +
                    $"{Window.TotalMinutes:0} minute(s). Wait for the period named by Retry-After and " +
                    "resend; nothing was recorded.",
            },
            instance: http.Request.GetEncodedPathAndQuery());

        problem.Extensions["code"] = RateLimitedCode;

        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });

        _ = ct;
    }

    /// <summary>
    /// <inheritdoc cref="LivePolicyName" path="/summary/para[1]"/>
    ///
    /// <para>
    /// Always the remote address, never the device claim, and the partition key is prefixed
    /// <c>live:</c> so a dashboard's polling can never spend a kiosk's capture budget — or the reverse.
    /// Two policies sharing one partition table is exactly how a limiter starts refusing taps because
    /// somebody left a browser tab open.
    /// </para>
    /// </summary>
    private static RateLimitPartition<string> LivePartitionFor(HttpContext context)
    {
        var key = LivePartitionPrefix + AnonymousPartitionPrefix +
                  (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = LivePermitsPerWindow,
            Window = Window,
            // Reject rather than queue, matching the capture policy: a queued poll is a poll whose
            // answer is stale by the time it is served, and the client is going to ask again anyway.
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <inheritdoc cref="LivePartitionFor"/>
    private const string LivePartitionPrefix = "live:";

    /// <summary>
    /// <inheritdoc cref="ManifestPolicyName" path="/summary/para[1]"/>
    ///
    /// <para>
    /// The <c>manifest:</c> prefix is what actually keeps the budgets separate — three policies sharing
    /// one partition table would mean a device's cache refreshes and its taps drawing on the same
    /// bucket, which is the thing this policy exists so as not to do. The unauthenticated fallback
    /// mirrors the capture policy's: this endpoint is gated, so an anonymous request is refused a
    /// moment later anyway, and the fallback exists so it is not unlimited on the way there.
    /// </para>
    /// </summary>
    private static RateLimitPartition<string> ManifestPartitionFor(HttpContext context)
    {
        var deviceId = context.User.FindFirst(EamsClaimTypes.DeviceId)?.Value;

        var key = ManifestPartitionPrefix + (deviceId is not null
            ? DevicePartitionPrefix + deviceId
            : AnonymousPartitionPrefix + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"));

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ManifestPermitsPerWindow,
            Window = Window,
            // Reject rather than queue, matching both other policies: a queued pull is a pull whose
            // answer is stale by the time it is served, and the client already holds a usable manifest.
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <inheritdoc cref="ManifestPartitionFor"/>
    private const string ManifestPartitionPrefix = "manifest:";

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
