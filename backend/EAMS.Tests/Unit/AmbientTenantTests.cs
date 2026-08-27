using System.Security.Claims;
using EAMS.Api.Authorization;
using EAMS.Api.MultiTenancy;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// ADR-004 <b>D-54.3</b>: the tenant of a DI scope that has no <c>HttpContext</c>.
///
/// <para>
/// <b>What is actually being pinned here, and why it needs a test at all.</b> Before this,
/// <c>ClaimsSchoolContext</c> answered <c>null</c> whenever there was no <c>HttpContext</c>, and
/// <c>ISchoolContext</c> documents <c>null</c> as "do not filter". A <c>BackgroundService</c>'s scope
/// has no <c>HttpContext</c>, so the roster import about to move into one would have run with every
/// global query filter switched off — matching and upserting rows across every school. <b>The whole
/// suite would have stayed green</b>: it builds one school, and with one school an unfiltered query
/// and a correctly filtered one return the same rows for every assertion in it.
/// </para>
///
/// <para>
/// So these tests assert the <em>seam</em> rather than the consequence. The consequence — an import
/// that touches another school's rows — needs two schools and the runner that P3c builds, and it is
/// named in ADR-004's follow-ups as the highest-value missing test in that document. What is provable
/// without either is that the four branches of <c>ClaimsSchoolContext</c> answer what they claim to,
/// including the one that answers by throwing.
/// </para>
///
/// <para>
/// Unit, not integration, deliberately: no branch here reaches a database, and the two claims cases
/// are the ones that must be shown <em>unchanged</em> — which is easier to read against a
/// hand-built <c>DefaultHttpContext</c> than against a booted host.
/// </para>
/// </summary>
public class AmbientTenantTests
{
    private static readonly Guid BackgroundSchool = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClaimSchool = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PinnedSchool = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class StubPinnedSchoolContext : IPinnedSchoolContext
    {
        public Guid? CurrentSchoolId { get; init; }
    }

    /// <summary>No request in flight — the shape of every scope a <c>BackgroundService</c> opens.</summary>
    private sealed class NoRequest : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => null; set => throw new NotSupportedException(); }
    }

    private static ClaimsSchoolContext Subject(AmbientTenant ambient, HttpContext? http = null) =>
        new(
            http is null ? new NoRequest() : new HttpContextAccessor { HttpContext = http },
            new StubPinnedSchoolContext { CurrentSchoolId = PinnedSchool },
            ambient);

    private static HttpContext RequestWith(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        return context;
    }

    // ------------------------------------------------------------------- the three new behaviours

    /// <summary>
    /// The case the runner will be in once P3c pins from <c>SisImportBatch.SchoolId</c>: no request,
    /// a declared tenant, and every query filter scoped to it rather than switched off.
    /// </summary>
    [Fact]
    public void A_background_scope_that_pinned_a_school_resolves_to_that_school()
    {
        var ambient = new AmbientTenant();
        ambient.DeclareBackgroundScope();
        ambient.PinSchool(BackgroundSchool);

        Assert.Equal(BackgroundSchool, Subject(ambient).CurrentSchoolId);
    }

    /// <summary>
    /// The silent-cross-tenant case, made impossible. There is no answer that is both honest and safe
    /// here: <c>null</c> is "do not filter" (the original defect) and the process pin belongs to a
    /// different question. The message has to say what the caller failed to do, because the caller is
    /// a background job with nobody watching it — the exception text is the entire diagnosis.
    /// </summary>
    [Fact]
    public void A_background_scope_that_named_no_school_throws_rather_than_running_unfiltered()
    {
        var ambient = new AmbientTenant();
        ambient.DeclareBackgroundScope();

        var subject = Subject(ambient);
        var ex = Assert.Throws<InvalidOperationException>(() => subject.CurrentSchoolId);

        Assert.Contains(nameof(AmbientTenant.PinSchool), ex.Message, StringComparison.Ordinal);
        Assert.Contains("SisImportBatch.SchoolId", ex.Message, StringComparison.Ordinal);
        Assert.Contains("do not filter", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Startup: migration, seeding, <c>create-admin</c> and design-time model building. They declare
    /// nothing, they are legitimately untenanted, and <c>null</c> is the answer the interface was
    /// written to give them. A blanket "throw when there is no <c>HttpContext</c>" would have made the
    /// host unable to boot — a dead site traded for a silent data defect.
    /// </summary>
    [Fact]
    public void A_scope_that_declared_nothing_is_unfiltered_exactly_as_before()
    {
        Assert.Null(Subject(new AmbientTenant()).CurrentSchoolId);
    }

    // ------------------------------------------------------------------ the claims path, unchanged

    [Fact]
    public void A_request_carrying_a_school_claim_still_resolves_to_the_claim()
    {
        var http = RequestWith(new Claim(EamsClaimTypes.SchoolId, ClaimSchool.ToString()));

        Assert.Equal(ClaimSchool, Subject(new AmbientTenant(), http).CurrentSchoolId);
    }

    [Fact]
    public void A_request_carrying_no_claim_still_falls_back_to_the_pinned_school()
    {
        Assert.Equal(PinnedSchool, Subject(new AmbientTenant(), RequestWith()).CurrentSchoolId);
    }

    /// <summary>
    /// A request wins over the ambient tenant, and this is the branch order stated in
    /// <c>ClaimsSchoolContext</c>'s remarks. Pinning the ambient tenant from inside a request is
    /// therefore inert rather than a way to move a live request's tenant.
    /// </summary>
    [Fact]
    public void An_ambient_tenant_does_not_override_a_request_that_has_claims()
    {
        var ambient = new AmbientTenant();
        ambient.PinSchool(BackgroundSchool);

        var http = RequestWith(new Claim(EamsClaimTypes.SchoolId, ClaimSchool.ToString()));

        Assert.Equal(ClaimSchool, Subject(ambient, http).CurrentSchoolId);
    }

    // ----------------------------------------------------------------------- the declaration rules

    /// <summary>
    /// A caller that can name its tenant up front needs one call. Pinning implies the declaration, so
    /// there is no way to end up pinned-but-undeclared.
    /// </summary>
    [Fact]
    public void Pinning_implies_the_declaration()
    {
        var ambient = new AmbientTenant();
        ambient.PinSchool(BackgroundSchool);

        Assert.Equal(BackgroundSchool, ambient.Resolve());
    }

    /// <summary>Idempotent, and it must never walk a pinned scope back to the throwing state.</summary>
    [Fact]
    public void Declaring_again_after_a_pin_does_not_clear_the_school()
    {
        var ambient = new AmbientTenant();
        ambient.PinSchool(BackgroundSchool);
        ambient.DeclareBackgroundScope();

        Assert.Equal(BackgroundSchool, ambient.Resolve());
    }

    /// <summary>
    /// <c>Guid.Empty</c> is the default, not a tenant. Accepting it would filter every query in the
    /// scope to a school that cannot exist, and the run would report zero rows touched and call it
    /// success — which is the failure <c>ISchoolContext</c>'s own documentation cites as the reason
    /// <c>null</c> means "unfiltered" instead of "empty".
    /// </summary>
    [Fact]
    public void An_empty_guid_is_refused_as_a_tenant()
    {
        var ambient = new AmbientTenant();

        var ex = Assert.Throws<ArgumentException>(() => ambient.PinSchool(Guid.Empty));

        Assert.Contains("Guid.Empty is not a tenant", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scope has exactly one tenant. Re-pinning would file the rows written before and after the
    /// second call under different schools, with nothing in the log to say so.
    /// </summary>
    [Fact]
    public void A_scope_cannot_be_re_pinned_to_a_second_school()
    {
        var ambient = new AmbientTenant();
        ambient.PinSchool(BackgroundSchool);

        var ex = Assert.Throws<InvalidOperationException>(() => ambient.PinSchool(ClaimSchool));

        Assert.Contains(BackgroundSchool.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains(ClaimSchool.ToString(), ex.Message, StringComparison.Ordinal);
    }
}
