using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.RateLimiting;

/// <summary>
/// Technical Plan §14's other half — "rate limiting on <c>/auth</c>" — as <b>two</b> limiters rather
/// than one (Phase 6b).
///
/// <para>
/// <b>Why two, when one is the obvious answer.</b> Each single-key choice has a failure that the
/// other does not, and both failures are worse than the attack they prevent:
/// </para>
///
/// <list type="bullet">
///   <item>
///     <b>IP only</b> puts an entire campus behind one NAT in a single bucket. The University of San
///     Agustin's staff and students reach this API through a handful of public addresses; a limit
///     tight enough to stop password guessing would lock out everyone on the network the moment one
///     person fat-fingered a login at the start of a semester.
///   </item>
///   <item>
///     <b>Account only</b> is a denial-of-service vector aimed at a person. Anyone who knows an
///     administrator's e-mail address can spend that account's budget from anywhere and keep the real
///     owner permanently locked out — the classic reason
///     <b>this design has no lockout columns on <c>User</c> at all</b>. A lockout flag is a durable,
///     attacker-writable state; a rate-limit bucket is in-memory, self-healing within a minute, and
///     cannot be used to disable an account.
///   </item>
/// </list>
///
/// <para>
/// So: a <b>loose IP anti-flood</b> (<see cref="IpPolicyName"/>) that only ever fires on something
/// automated, applied by the rate-limiter middleware exactly as
/// <see cref="CaptureRateLimiting"/> applies its policies; and a <b>tight anti-guess</b>
/// (<see cref="AuthAccountLimiter"/>) partitioned by <c>(email, ip)</c> <em>and</em> by <c>email</c>,
/// which is what actually stops password guessing without handing anyone a way to lock a colleague
/// out.
/// </para>
///
/// <para>
/// <b>The two are applied by different mechanisms, and not by preference.</b> ASP.NET Core's rate
/// limiter admits exactly one partition per policy per endpoint, and the account limiter's key is the
/// e-mail address in the request body — which does not exist until model binding has run. Both facts
/// point the same way: the IP limiter runs in the middleware, before the body is read; the account
/// limiter runs at the top of the action, after it. See <see cref="AuthAccountLimiter"/>.
/// </para>
///
/// <para>
/// <b>Behind a reverse proxy every IP partition in this file collapses into one.</b> IIS ARR, nginx,
/// or any load balancer terminates the connection, so <c>Connection.RemoteIpAddress</c> becomes the
/// proxy's address and every caller in the world shares a bucket — the campus-NAT failure above,
/// applied to the whole internet. The fix is <c>UseForwardedHeaders</c> with an explicit
/// <c>KnownProxies</c> list. <b>It is deliberately not implemented here</b>: honouring
/// <c>X-Forwarded-For</c> without pinning which hops may set it lets any caller choose their own
/// partition key, which is strictly worse than one shared bucket. It belongs in the deployment phase
/// that knows what the proxy is.
/// </para>
///
/// <para>
/// <b>Per-instance, like every limiter in this API.</b> Behind several §13 Option A instances the real
/// ceiling is <c>Limit × instances</c>. Accepted for the reason <see cref="CaptureRateLimiting"/>
/// records at length: the purpose is friction and detection, not a contractual quota, and a hard
/// quota needs a shared store that does not exist yet.
/// </para>
/// </summary>
public static class AuthRateLimiting
{
    /// <summary>The middleware policy name on the <c>/auth</c> write routes.</summary>
    public const string IpPolicyName = "auth-ip";

    /// <summary>
    /// Requests per <see cref="Window"/> per client address, across every <c>/auth</c> write.
    ///
    /// <para>
    /// <b>Deliberately far above anything a campus does honestly.</b> A shared address serving a few
    /// hundred staff produces login and refresh traffic in the low tens per minute — refreshes are
    /// once per fifteen minutes per session. A hundred and twenty is roughly an order of magnitude
    /// above that, which is the margin that stops this from being the limiter that gets raised by
    /// whoever is on call and then is not a limiter. It exists to stop a flood, not a guess; the
    /// guessing is <see cref="AuthAccountLimiter"/>'s job.
    /// </para>
    /// </summary>
    public const int IpPermitsPerWindow = 120;

    /// <summary>The fixed window, matching <see cref="CaptureRateLimiting.Window"/>.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string AuthPartitionPrefix = "auth:ip:";

    /// <summary>
    /// Registers <see cref="IpPolicyName"/> on the existing limiter.
    ///
    /// <para>
    /// Called from the same <c>AddRateLimiter</c> configuration <see cref="CaptureRateLimiting"/>
    /// builds, so there is one <c>OnRejected</c> writer and one 429 body shape across the whole API —
    /// a client that already branches on <c>code: RateLimited</c> needs no new case.
    /// </para>
    /// </summary>
    internal static void AddAuthPolicies(Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options) =>
        options.AddPolicy(IpPolicyName, PartitionFor);

    /// <summary>
    /// Always the remote address, and prefixed <c>auth:ip:</c> so an <c>/auth</c> flood can never
    /// spend a dashboard's or a kiosk's budget — the same partition-collision hazard
    /// <see cref="CaptureRateLimiting"/> records for its three policies.
    ///
    /// <para>
    /// <b>Never the e-mail address, even though the body has one by the time a partition key would be
    /// convenient.</b> Reading the request body inside a partitioner means buffering it before the
    /// pipeline has decided the request is worth reading, which is an amplification target on the one
    /// endpoint an attacker is already pointing traffic at.
    /// </para>
    /// </summary>
    private static RateLimitPartition<string> PartitionFor(HttpContext context)
    {
        var key = AuthPartitionPrefix +
                  (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = IpPermitsPerWindow,
            Window = Window,
            // Reject rather than queue, matching every other policy in this API: a queued login is a
            // login the user believes is in flight while they type their password again.
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}

/// <summary>
/// The tight anti-guess limiter: <c>(email, ip)</c> chained with <c>email</c> (Phase 6b).
///
/// <para>
/// <b>Chained, so both must admit the attempt.</b> <c>(email, ip)</c> is the tight one — one address
/// guessing at one account — and <c>email</c> alone is the wider net that catches the same guessing
/// spread across a botnet, at a limit loose enough that it is not itself a way to lock someone out of
/// their account for more than a minute.
/// </para>
///
/// <para>
/// <b>Why it is acquired inside the action rather than by the middleware.</b> Two reasons, either
/// sufficient: the partition key is the e-mail address in the request body, which does not exist
/// before model binding; and ASP.NET Core's rate limiter permits exactly one partition per policy per
/// endpoint, so a chained limiter cannot be expressed as an <c>[EnableRateLimiting]</c> policy at all
/// without becoming a global limiter in front of every route in the API — which this phase must not
/// do, because every endpoint outside <c>/auth</c> has to behave exactly as it does today.
/// </para>
///
/// <para>
/// <b>A permit is spent on every attempt, including one for an address that does not exist.</b> That
/// is not an oversight: refusing to create a bucket for an unknown address would make the 429 itself
/// an account-existence oracle, undoing what
/// <c>AuthLoginOutcome.InvalidCredentials</c> and the decoy hash are for.
/// </para>
///
/// <para>
/// <b>Singleton, holding process-lifetime state.</b> The partition tables are the limiter's memory;
/// a scoped instance would mint a fresh, empty limiter per request and refuse nothing. It is disposed
/// with the container.
/// </para>
/// </summary>
public sealed class AuthAccountLimiter : IAsyncDisposable
{
    /// <summary>
    /// Attempts per <see cref="AuthRateLimiting.Window"/> for one address from one client IP.
    ///
    /// <para>
    /// <b>Ten, which is generous for a human and ruinous for a guesser.</b> A person who has forgotten
    /// which of their two passwords this system holds tries three or four times and then uses the
    /// reset flow; ten leaves room for a typo-prone morning without ever being reached. An attacker
    /// gets six hundred attempts an hour against one account from one address, against a
    /// twelve-character minimum — which is not an attack, it is a log entry.
    /// </para>
    /// </summary>
    public const int PerEmailPerIpPermits = 10;

    /// <summary>
    /// Attempts per <see cref="AuthRateLimiting.Window"/> for one address from <em>anywhere</em>.
    ///
    /// <para>
    /// <b>Five times the per-IP limit, and that ratio is the whole design.</b> Tighter, and one person
    /// with a spare address can lock a colleague out for the rest of the minute — the victim-DoS this
    /// class exists to avoid. Looser, and distributing the guessing across fifty hosts costs the
    /// attacker nothing. Fifty per minute across all sources is still three thousand an hour against a
    /// single account, which the password rules make worthless, while leaving an ordinary user's
    /// worst morning far below the ceiling even if they are signing in from a phone, a laptop and a
    /// lab machine at once.
    /// </para>
    /// </summary>
    public const int PerEmailPermits = 50;

    private readonly PartitionedRateLimiter<string> _chained;

    public AuthAccountLimiter()
    {
        var perEmailPerIp = PartitionedRateLimiter.Create<string, string>(
            key => Partition("auth:email-ip:" + key, PerEmailPerIpPermits));

        var perEmail = PartitionedRateLimiter.Create<string, string>(
            // Everything before the first '|' is the e-mail; the IP is dropped, which is what makes
            // this the wider net.
            key => Partition("auth:email:" + key[..key.IndexOf('|', StringComparison.Ordinal)],
                PerEmailPermits));

        _chained = PartitionedRateLimiter.CreateChained(perEmailPerIp, perEmail);
    }

    /// <summary>
    /// Tries to spend one attempt. <b>Dispose the returned lease</b> — a fixed-window limiter does not
    /// return the permit on dispose (that is what makes it a window rather than a concurrency limit),
    /// but the lease holds the <c>Retry-After</c> metadata the caller needs.
    /// </summary>
    /// <param name="email">
    /// The normalized address. Normalized by the caller, because <c>Admin@x</c> and <c>admin@x</c>
    /// resolving to different buckets would be a limit an attacker steps around by changing case.
    /// </param>
    /// <param name="ipAddress">The client address, or <c>null</c> when there is none.</param>
    public ValueTask<RateLimitLease> AcquireAsync(string email, string? ipAddress) =>
        _chained.AcquireAsync($"{email}|{ipAddress ?? "unknown"}");

    private static RateLimitPartition<string> Partition(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = AuthRateLimiting.Window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    public ValueTask DisposeAsync() => _chained.DisposeAsync();
}

/// <summary>
/// The 429 an <see cref="AuthAccountLimiter"/> refusal produces.
///
/// <para>
/// Written here rather than by the middleware's <c>OnRejected</c>, because the middleware never saw
/// this refusal — but produced in the same shape, through the same
/// <see cref="ProblemDetailsFactory"/> seam, carrying the same
/// <see cref="CaptureRateLimiting.RateLimitedCode"/>. A client that branches on <c>code</c> cannot
/// tell which limiter refused it, and should not need to.
/// </para>
/// </summary>
internal static class AuthRateLimitRefusal
{
    public static async Task WriteAsync(HttpContext http, RateLimitLease lease)
    {
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
        }

        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        var factory = http.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateProblemDetails(
            http,
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many sign-in attempts.",
            // Deliberately says nothing about whether the account exists, and nothing about which of
            // the two chained buckets was exhausted. Both would answer a question the 401 refuses to.
            detail:
                "Too many sign-in attempts have been made for this account recently. Wait for the " +
                "period named by Retry-After and try again. If this was not you, your password was " +
                "not disclosed by these attempts — nothing here confirms whether the account exists.",
            instance: http.Request.GetEncodedPathAndQuery());

        problem.Extensions["code"] = CaptureRateLimiting.RateLimitedCode;

        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }
}
