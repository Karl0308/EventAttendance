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
    /// No controller or action in EAMS.Api carries a real ASP.NET Core authorization attribute
    /// either. Without this, "IsEnforced is false" could coexist with endpoints that are in fact
    /// gated — the flag and reality would disagree, which is the exact condition
    /// <see cref="AuthorizationStatus"/> was written to make impossible.
    /// </summary>
    [Fact]
    public void No_endpoint_in_the_api_is_gated_by_a_real_authorization_attribute()
    {
        var gated = ApiAssembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MemberInfo>()
                .Append(t))
            .Where(m => m.GetCustomAttributes().Any(a => a is IAuthorizeData or IAuthorizationFilter or IAsyncAuthorizationFilter))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToList();

        Assert.True(
            gated.Count == 0,
            "Real authorization attributes appeared on: " + string.Join(", ", gated) +
            ". AuthorizationStatus.IsEnforced is still false, so the API now reports itself open " +
            "while parts of it are gated. Make the two agree.");
    }
}
