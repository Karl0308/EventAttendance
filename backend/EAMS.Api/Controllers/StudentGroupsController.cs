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
/// <b>It declares <c>students.read</c>, and the <c>groups.read</c> it used to declare no longer
/// exists (D-45).</b> Phase 3b-2 minted that code on the reasoning that §6's tables assign no route
/// to groups at all — true, but it read past the one place the plan does answer this: §7.1 guards
/// the frontend's <c>/groups</c> page with <c>students.read</c>. The plan is source of truth for the
/// permission map, so the minted code was a contradiction rather than the addition
/// <c>academic.read</c> is (D-44). It is deleted rather than kept as a synonym: two codes for one
/// page is precisely the drift <see cref="EamsPermissions"/> was made a registry to stop, and a
/// synonym would have to be granted twice by every role Phase 6 writes.
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
    /// <b>Paged, and the argument that it need not be was wrong.</b> This used to say a school's group
    /// count is bounded by its academic structure so a picker could hold the whole set — true of one
    /// term, and the projection writes a fresh row per section and per offering on <em>every</em> term
    /// it runs for, so the bound is "per term" multiplied by every term ever imported. <c>termId</c>
    /// narrows it and is still the filter most worth passing; the page is what makes the answer
    /// bounded when nobody passes one.
    /// </para>
    /// </remarks>
    /// <param name="sourceType">
    /// <c>Manual</c> (a person made it) or <c>Derived</c> (the projection owns it). Omit for both.
    /// Matched case-insensitively; <b>a value outside the set returns an empty list, not every row</b>
    /// — a mistyped filter that silently stops filtering is how a cohort-only flow ends up offering
    /// manual groups.
    /// </param>
    /// <param name="type">
    /// What kind of audience: <c>Course</c>, <c>Section</c>, <c>Org</c>, <c>Custom</c>,
    /// <c>College</c>, <c>Program</c> or <c>YearLevel</c>. Omit for every kind. Matched
    /// case-insensitively; <b>a value outside the set returns an empty list, not every row</b>, for the
    /// reason <c>sourceType</c> does — this is the filter an audience builder narrows by, so one that
    /// silently stopped filtering would offer a year picker every college and offering in the term.
    /// </param>
    /// <param name="termId">
    /// Narrows to one term's derived groups. Omit for every term's, plus the manual groups, which
    /// carry no term. <b>The filter most worth passing</b>: a section name is reused every semester
    /// against an entirely different set of students, so an unscoped list holds several distinct
    /// cohorts under names differing only by the term suffix the projection composes in.
    /// </param>
    /// <param name="search">
    /// A substring of the group's display name — <c>BSFS</c>, <c>2nd</c>, <c>Officers</c>. Omit to
    /// match every name. Case-insensitive, and matched against the name alone: that is the whole of
    /// what this list publishes as text, and it already carries the term suffix the projection composes
    /// in, so <c>2025-2026-1</c> is searchable too.
    /// </param>
    /// <param name="page">
    /// 1-based page number, default 1. Out-of-range values are clamped, never refused; the response
    /// echoes the page actually served.
    /// </param>
    /// <param name="pageSize">
    /// Rows per page. Default 50, maximum 200 — a larger value is clamped to the maximum and the
    /// response says so in its own <c>pageSize</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of matching groups, possibly empty.</response>
    [HttpGet]
    [HasPermissionNotEnforced(EamsPermissions.StudentsRead)]
    [ProducesResponseType(typeof(PagedResult<StudentGroupDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<StudentGroupDto>>> List(
        [FromQuery] string? sourceType, [FromQuery] string? type, [FromQuery] Guid? termId,
        [FromQuery] string? search,
        [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => Ok(await _groups.ListAsync(
            sourceType, type, termId, search, PageRequest.From(page, pageSize), ct));
}
