using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;

namespace EAMS.Api.Identity;

/// <summary>
/// Technical Plan §11's identity, resolved from the request's claims (Phase 6b) — the twin of
/// <see cref="ClaimsDeviceContext"/> and of <c>ClaimsSchoolContext</c>, and the last of the three
/// seams ADR-001 D-6 created to be filled here.
///
/// <para>
/// <b>It answers <c>null</c> for every request that carries no Bearer token, which today is almost
/// all of them, and that is correct rather than a gap.</b> A NULL
/// <c>AttendanceRecords.RecordedByUserId</c> is an accurate statement — "written by no authenticated
/// person" — and it is the statement <c>UnauthenticatedCurrentUser</c> has been making since Phase 1.
/// Replacing that registration does not change any existing endpoint's behaviour: no endpoint outside
/// <c>/auth</c> accepts a Bearer token yet, so there is no principal for this to find on any of them.
/// What changes is that when enforcement does arrive, attribution is already correct rather than
/// needing a backfill that cannot be done.
/// </para>
///
/// <para>
/// <b>This class is the reason <c>MapInboundClaims = false</c> is not optional.</b> The default
/// JwtBearer behaviour rewrites the <c>sub</c> claim to the WS-Federation
/// <c>…/nameidentifier</c> URI while leaving <c>school_id</c> and <c>perm</c> untouched. Every policy
/// would still pass, every tenant filter would still resolve, <c>/auth/me</c> would still answer —
/// and this property would silently return <c>null</c>, so every audited write would be attributed to
/// nobody, with a fully green test suite. See <c>JwtBearerClaimMappingTests</c>.
/// </para>
///
/// <para>
/// <b>Scoped</b>, unlike the singleton it replaces: the answer is a property of the request.
/// </para>
/// </summary>
internal sealed class ClaimsCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;

    public ClaimsCurrentUser(IHttpContextAccessor http) => _http = http;

    public Guid? UserId =>
        EamsClaimTypes.ReadUserId(
            _http.HttpContext?.User.FindFirst(EamsClaimTypes.Subject)?.Value);
}
