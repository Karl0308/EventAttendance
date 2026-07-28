using EAMS.Api.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// A decorated endpoint that exists only inside <see cref="EamsApiFactory"/>'s host.
///
/// <para>
/// ADR-001 D-6's follow-up asks for a test proving <c>[HasPermissionNotEnforced]</c> denies nothing.
/// Nothing in EAMS.Api carries the attribute yet, so the proof needs a subject — and inventing one
/// here is better than decorating a production endpoint to make a test possible. The structural
/// argument (the attribute implements no interface the MVC pipeline can reach) lives in the unit
/// suite; this is the empirical half: a real request, through a real pipeline, with no credentials,
/// against an action the attribute is applied to.
/// </para>
/// </summary>
[ApiController]
[Route("test-only/permission-probe")]
public sealed class PermissionProbeController : ControllerBase
{
    public const string ReachedBody = "reached";

    [HttpGet]
    [HasPermissionNotEnforced("attendance.capture")]
    public IActionResult Get() => Ok(ReachedBody);

    /// <summary>Class-level application of the attribute is legal too (<c>AttributeTargets.Class</c>).</summary>
    [HttpGet("stacked")]
    [HasPermissionNotEnforced("attendance.capture")]
    [HasPermissionNotEnforced("attendance.override")]
    public IActionResult Stacked() => Ok(ReachedBody);
}
