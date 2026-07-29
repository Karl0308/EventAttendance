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
/// The pairing is the assertion, not the presence: <b>every gated endpoint is rate limited, and
/// nothing else is.</b> Limiting an ungated endpoint would partition by remote IP, which for a campus
/// behind one NAT is a single shared bucket for the whole institution.
/// </para>
/// </summary>
public class CaptureRateLimitingTests
{
    private static readonly Assembly ApiAssembly = typeof(AuthorizationStatus).Assembly;

    [Fact]
    public void The_rate_limited_actions_are_exactly_the_gated_ones()
    {
        var actions = ApiAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .ToList();

        var gated = Named(actions.Where(m =>
            m.GetCustomAttributes(inherit: true).OfType<AuthorizeAttribute>().Any()));

        var limited = Named(actions.Where(m =>
            m.GetCustomAttributes(inherit: true).OfType<EnableRateLimitingAttribute>().Any()));

        Assert.NotEmpty(gated);
        Assert.Equal(gated, limited);
    }

    [Fact]
    public void Every_rate_limited_action_uses_the_device_partitioned_policy()
    {
        var policies = ApiAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes(inherit: true).OfType<EnableRateLimitingAttribute>())
            .Select(a => a.PolicyName)
            .ToList();

        Assert.NotEmpty(policies);
        Assert.All(policies, policy => Assert.Equal(CaptureRateLimiting.PolicyName, policy));
    }

    private static List<string> Named(IEnumerable<MethodInfo> methods) =>
        methods.Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
}
