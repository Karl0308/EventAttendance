using System.Reflection;
using EAMS.Api.Authorization;
using EAMS.Api.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// §14's "rate limiting on <c>/auth</c> and <c>/attendance/tap</c>", applied to the capture surface
/// (Phase 4a design, D-28).
///
/// <para>
/// <b>This asserts attachment, not behaviour, and the reason is worth stating so nobody reads it as
/// more than it is.</b> Proving the limiter actually rejects would mean firing
/// <see cref="CaptureRateLimiting.PermitsPerWindow"/> + 1 requests through a real host, and the limit
/// is deliberately generous — a test that slow, for a mechanism whose stated purpose is friction and
/// detection rather than a hard quota, is not worth a minute on every build. What is cheap and worth
/// having is the regression this catches: an <c>[EnableRateLimiting]</c> quietly dropped from an
/// endpoint during an unrelated edit, which is invisible in review and silent at runtime.
/// </para>
///
/// <para>
/// <b>Every gated endpoint is rate limited, and the only endpoint limited without being gated is the
/// live poll.</b> That exception is named rather than left to a looser assertion: Phase 4d added
/// <c>GET /attendance/live/{eventId}</c>, which is deliberately unauthenticated (a dashboard read, and
/// the only credential that exists is scoped to <c>attendance.capture</c>) and is therefore the one
/// place where an IP partition is not a choice — there is no device claim to partition by. It gets its
/// own policy so a browser tab left polling can never spend a kiosk's capture budget.
/// </para>
///
/// <para>
/// The general rule the exception is measured against still holds: limiting an <em>ordinary</em>
/// ungated endpoint would partition a whole campus behind one NAT into a single bucket, which is why
/// this is an allow-list of two names rather than "anything may be limited".
/// </para>
/// </summary>
public class CaptureRateLimitingTests
{
    private static readonly Assembly ApiAssembly = typeof(AuthorizationStatus).Assembly;

    /// <summary>
    /// The endpoints that are rate limited <em>without</em> a device key gating them. Exactly one, and
    /// adding a second is a deliberate one-line edit here — the same allow-list shape
    /// <c>AuthorizationSeamTests</c> uses, for the same reason: "all gated endpoints are limited" is a
    /// property that stops being true the first time anyone limits an open one, and the natural repair
    /// is to delete the test.
    /// </summary>
    private static readonly string[] LimitedButNotGated = ["AttendanceController.Live"];

    [Fact]
    public void The_rate_limited_actions_are_the_gated_ones_plus_the_live_poll()
    {
        var actions = ApiAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .ToList();

        var gated = Named(actions.Where(m =>
            m.GetCustomAttributes(inherit: true).OfType<AuthorizeAttribute>().Any()));

        var limited = Named(actions.Where(m =>
            m.GetCustomAttributes(inherit: true).OfType<EnableRateLimitingAttribute>().Any()));

        Assert.NotEmpty(gated);
        Assert.Equal(
            gated.Concat(LimitedButNotGated).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            limited);
    }

    /// <summary>
    /// A gated endpoint takes the device-partitioned policy; the live poll takes the IP-partitioned
    /// one. Crossing them is the mistake worth catching — putting the live endpoint on
    /// <c>device-capture</c> would drop every unauthenticated poll into the anonymous IP bucket
    /// <em>shared with the capture endpoints</em>, so a dashboard could exhaust the budget a kiosk
    /// needs before it has presented its key.
    /// </summary>
    [Fact]
    public void Each_rate_limited_action_uses_the_policy_matching_how_it_is_partitioned()
    {
        var limited = ApiAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => new
            {
                Name = $"{m.DeclaringType?.Name}.{m.Name}",
                Policies = m.GetCustomAttributes(inherit: true)
                    .OfType<EnableRateLimitingAttribute>().Select(a => a.PolicyName).ToList(),
            })
            .Where(x => x.Policies.Count > 0)
            .ToList();

        Assert.NotEmpty(limited);

        foreach (var action in limited)
        {
            // Three policies, three partitions. The manifest pull is gated like the capture endpoints
            // and partitioned by device like them, but it must not share their bucket: a device
            // refreshing its offline cache would otherwise spend the tap budget its kiosk needs.
            var expected = action.Name switch
            {
                _ when LimitedButNotGated.Contains(action.Name) => CaptureRateLimiting.LivePolicyName,
                "EventManifestController.Manifest" => CaptureRateLimiting.ManifestPolicyName,
                _ => CaptureRateLimiting.PolicyName,
            };

            Assert.All(action.Policies, policy => Assert.Equal(expected, policy));
        }
    }

    /// <summary>
    /// The two policies must not share a partition key space, or a dashboard's polling and a kiosk's
    /// taps compete for one bucket. Asserted on the names because the partition functions are private
    /// and the names are what make the buckets distinct.
    /// </summary>
    [Fact]
    public void The_policies_are_distinct()
    {
        string[] policies =
        [
            CaptureRateLimiting.PolicyName,
            CaptureRateLimiting.LivePolicyName,
            CaptureRateLimiting.ManifestPolicyName,
        ];

        Assert.Equal(policies.Length, policies.Distinct(StringComparer.Ordinal).Count());
    }

    private static List<string> Named(IEnumerable<MethodInfo> methods) =>
        methods.Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
}
