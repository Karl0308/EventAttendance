using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace EAMS.Api.Controllers;

/// <summary>
/// Technical Plan §4.7 <c>StudentGroups</c> over HTTP — <b>reads only</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the list <c>POST /events/{id}/attendees</c> takes its ids from.</b> Until this route
/// existed there was no way to obtain one over the API at all: the events surface accepted
/// <c>studentGroupIds</c> and nothing published them, so attaching an audience meant reading a group
/// id out of the database by hand. That is the gap this closes.
/// </para>
///
/// <para>
/// <b>Top-level rather than nested under <c>/academic</c>, because it is not an academic table.</b>
/// A group is the §4.7 grouping layer — a manual "SSC Officers" sits in it beside a projected
/// "BSFS 2-A (2025-2026-1)", and only the second has anything to do with the academic tables. Filing
/// the route under <c>/academic</c> would say the opposite of what ADR-001 D-1 arranged, which is that
/// <c>EventGroups</c> and the §12 denominator never learn the academic layer exists.
/// </para>
///
/// <para>
/// <b>No write surface, deliberately.</b> Derived groups are reconciled from the academic tables on
/// every import, so a hand-edited derived membership is erased on the next run; manual groups have no
/// creation route yet because who owns membership is an open decision, not an oversight.
/// </para>
///
/// <para>
/// <b><c>groups.read</c> is a permission code this phase minted; §6 defines none.</b> §6's tables
/// assign no route to groups at all. §7.1 guards the frontend's <c>/groups</c> page with
/// <c>students.read</c>, which is the one contrary signal — reused here it would say "anyone who can
/// browse the roster can browse its audiences". That is defensible and it is Phase 6's call; the
/// attribute enforces nothing (ADR-001 D-6), so this is a line on the audit list rather than a
/// decision that binds.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/student-groups")]
public class StudentGroupsController : ControllerBase
{
    private readonly IStudentGroupService _groups;
    public StudentGroupsController(IStudentGroupService groups) => _groups = groups;

    /// <summary>
    /// <c>GET /student-groups</c> — the audiences an event can be attached to, every filter optional.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>memberCount</c> is the number an organizer is really choosing on — "invite BSCRIM 2-A" is a
    /// different decision at 8 students than at 80 — and a derived group showing zero is the visible
    /// signal that the projection has not run for its term. Soft-deleted students are excluded, so it
    /// agrees with the <c>expected</c> denominator <c>GET /events/{id}/summary</c> computes.
    /// </para>
    ///
    /// <para>
    /// Unpaged, like the other reference lists: a school's group count is bounded by its academic
    /// structure — one row per college, programme, section and offering per term, plus the handful of
    /// manual ones — and a picker wants the whole set to filter client-side.
    /// </para>
    /// </remarks>
    /// <param name="sourceType">
    /// <c>Manual</c> (a person made it) or <c>Derived</c> (the projection owns it). Omit for both.
    /// Matched case-insensitively; <b>a value outside the set returns an empty list, not every row</b>
    /// — a mistyped filter that silently stops filtering is how a cohort-only flow ends up offering
    /// manual groups.
    /// </param>
    /// <param name="termId">
    /// Narrows to one term's derived groups. Omit for every term's, plus the manual groups, which
    /// carry no term. <b>The filter most worth passing</b>: a section name is reused every semester
    /// against an entirely different set of students, so an unscoped list holds several distinct
    /// cohorts under names differing only by the term suffix the projection composes in.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The matching groups, possibly empty.</response>
    [HttpGet]
    [HasPermissionNotEnforced("groups.read")]
    [ProducesResponseType(typeof(IEnumerable<StudentGroupDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<StudentGroupDto>>> List(
        [FromQuery] string? sourceType, [FromQuery] Guid? termId, CancellationToken ct)
        => Ok(await _groups.ListAsync(sourceType, termId, ct));
}
