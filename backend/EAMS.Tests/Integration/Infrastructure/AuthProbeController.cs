using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// A Bearer-gated endpoint that reports what the identity and tenant seams resolved to — the only way
/// to test the thing that actually breaks when <c>MapInboundClaims</c> is left at its default.
///
/// <para>
/// <b>Why a probe and not a real endpoint.</b> The failure this exists to catch is silent NULL
/// attribution: <c>ICurrentUser.UserId</c> returning null on an authenticated request, so an audited
/// write records nobody. Phase 6b deliberately puts <c>[Authorize]</c> on no endpoint outside
/// <c>/auth</c> — the staged cutover is a later phase's decision — so there is no production route
/// that both requires a Bearer token and consults <c>ICurrentUser</c>. Without this controller the
/// only available assertion would be on the configured flag, which proves the setting and not its
/// effect.
/// </para>
///
/// <para>
/// Test-only and reachable only through the test factories, exactly like
/// <see cref="PermissionProbeController"/>. It is mounted under <c>/test-only/</c> because
/// <c>OpenApiDocumentTests</c> strips that prefix before comparing the served document to the
/// committed contract — a probe that leaked into <c>docs/api/openapi.json</c> would publish a route
/// the real application does not serve.
/// </para>
/// </summary>
[ApiController]
[Route(Route)]
public sealed class AuthProbeController : ControllerBase
{
    public const string Route = "/test-only/auth-probe";

    private readonly ICurrentUser _currentUser;
    private readonly ISchoolContext _school;

    public AuthProbeController(ICurrentUser currentUser, ISchoolContext school)
    {
        _currentUser = currentUser;
        _school = school;
    }

    /// <summary>What the three claim-reading seams resolved to for this request.</summary>
    public sealed record Seams(Guid? UserId, Guid? SchoolId, string[] Permissions);

    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public ActionResult<Seams> Get() => Ok(new Seams(
        _currentUser.UserId,
        _school.CurrentSchoolId,
        [.. User.FindAll(EAMS.Api.Authorization.EamsClaimTypes.Permission).Select(c => c.Value)]));
}
