using EAMS.Api.MultiTenancy;
using EAMS.Application.Abstractions;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-004 <b>D-54.3</b> through the <em>real host's</em> container — the shape P3c's
/// <c>BackgroundService</c> will use: a scope opened from the root provider, with no
/// <c>HttpContext</c> anywhere in sight.
///
/// <para>
/// <c>AmbientTenantTests</c> pins the four branches by constructing <c>ClaimsSchoolContext</c>
/// directly. That leaves one thing unproven and it is the thing a background runner depends on: that
/// the wiring exists in the host at all. A missing <c>AddScoped&lt;AmbientTenant&gt;()</c> is not a
/// compile error and not a failing unit test — it is a <c>GetRequiredService</c> throwing inside a
/// background job at 3am, which is where this system is least able to tell anyone.
/// </para>
///
/// <para>
/// <b>Note what the untenanted case here means.</b> The host has pinned a development school by the
/// time these run, and an undeclared background scope still resolves to <c>null</c> rather than to
/// that pin — because the pin answers a <em>request</em> that carried no credentials, which is a
/// different question. That is the branch startup migration and seeding take, and it is why the host
/// can still boot.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class BackgroundScopeTenancyTests : IntegrationTest
{
    public BackgroundScopeTenancyTests(SqlServerFixture sql) : base(sql) { }

    private static readonly Guid SomeSchool = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void A_host_scope_that_declares_a_tenant_filters_to_it()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var scope = factory.Services.CreateScope();

        var ambient = scope.ServiceProvider.GetRequiredService<AmbientTenant>();
        ambient.DeclareBackgroundScope();
        ambient.PinSchool(SomeSchool);

        Assert.Equal(SomeSchool, scope.ServiceProvider.GetRequiredService<ISchoolContext>().CurrentSchoolId);
    }

    [Fact]
    public void A_host_scope_that_declares_itself_background_and_names_no_tenant_throws()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<AmbientTenant>().DeclareBackgroundScope();

        var school = scope.ServiceProvider.GetRequiredService<ISchoolContext>();

        Assert.Throws<InvalidOperationException>(() => school.CurrentSchoolId);
    }

    /// <summary>
    /// Startup's branch, taken from a scope of the very host that has already completed startup: the
    /// answer is <c>null</c>, meaning "do not filter", exactly as before this change.
    /// </summary>
    [Fact]
    public void A_host_scope_that_declares_nothing_is_unfiltered()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var scope = factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetRequiredService<ISchoolContext>().CurrentSchoolId);
    }

    /// <summary>
    /// Scoped, not singleton. Two background runs must not be able to see each other's tenant — and
    /// a single-instance ambient tenant would also make the "already pinned" refusal fire on the
    /// second run rather than on a genuine mistake.
    /// </summary>
    [Fact]
    public void Each_scope_gets_its_own_ambient_tenant()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<AmbientTenant>().PinSchool(SomeSchool);

        Assert.Null(second.ServiceProvider.GetRequiredService<ISchoolContext>().CurrentSchoolId);
    }
}
