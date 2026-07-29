using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;

namespace EAMS.Api.MultiTenancy;

/// <summary>
/// Technical Plan §11's tenant, resolved from the request's claims (Phase 4a design, D-23).
///
/// <para>
/// <b>This is Phase 6's implementation, shipped early, with one branch to delete.</b> That is the
/// single largest "build toward the seam" win in Phase 4b: the §11 multi-tenant guard is the piece
/// ADR-001 D-6 called genuinely expensive to retrofit, and until now the interface it reads had a
/// development stand-in behind it that pinned one school for the process. Every global query filter in
/// <c>EamsDbContext</c> now goes through a real <c>school_id</c> claim whenever one exists, which means
/// the filter is exercised against a *moving* tenant rather than a constant — the condition under which
/// its failure modes actually appear.
/// </para>
///
/// <para>
/// Three answers, in order:
/// <list type="number">
///   <item>
///     <b>A <c>school_id</c> claim</b> — the authenticated answer. Today only a device key produces
///     one; Phase 6's JWT produces the identical claim type, so this code does not change.
///   </item>
///   <item>
///     <b>No claim, but a request</b> — the pinned development school
///     (<see cref="IPinnedSchoolContext"/>). Still the ordinary case: every endpoint outside the four
///     device-gated ones is open under ADR-001 D-6, so most requests carry no credentials at all.
///     <b>This branch is what Phase 6 deletes</b>, at which point an unauthenticated request is
///     rejected before it can reach a query.
///   </item>
///   <item>
///     <b>No <c>HttpContext</c> at all</b> — <c>null</c>, meaning "do not filter". Migration, seeding
///     and design-time model building run here, and the interface's nullable design already documents
///     null as the unfiltered state. Not the pin: startup work runs *before* a tenant is pinned, so
///     consulting it would be reading a value that is null anyway and implying it might not be.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// <b>Scoped, not singleton</b>, unlike the development stand-in it replaces — the answer is now a
/// property of the request. <c>EamsDbContext</c> is scoped too and re-reads this on every query
/// execution, so nothing is captured for longer than one request.
/// </para>
/// </summary>
internal sealed class ClaimsSchoolContext : ISchoolContext
{
    private readonly IHttpContextAccessor _http;
    private readonly IPinnedSchoolContext _pinned;

    public ClaimsSchoolContext(IHttpContextAccessor http, IPinnedSchoolContext pinned)
    {
        _http = http;
        _pinned = pinned;
    }

    public Guid? CurrentSchoolId
    {
        get
        {
            var context = _http.HttpContext;
            if (context is null) return null;

            var claim = context.User.FindFirst(EamsClaimTypes.SchoolId)?.Value;

            // A claim that is present and unparseable is treated as "no claim", which falls to the pin.
            // It cannot happen — the handler writes a Guid.ToString() — and if it ever does, the honest
            // reading is that this principal named no tenant, not that it named an impossible one.
            return Guid.TryParse(claim, out var schoolId) ? schoolId : _pinned.CurrentSchoolId;
        }
    }
}
