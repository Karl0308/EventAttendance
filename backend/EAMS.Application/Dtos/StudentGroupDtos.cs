namespace EAMS.Application.Dtos;

/// <summary>
/// Technical Plan §4.7 <c>StudentGroups</c>, as a client sees it — <b>the thing an event audience is
/// actually attached to</b>. <c>POST /events/{id}/attendees</c> takes ids from this list.
///
/// <para>
/// <b>The provenance fields are the reason this list is usable as one picker.</b> ADR-001 D-1
/// projects colleges, programmes, sections and course offerings <em>into</em> this table as ordinary
/// rows marked <see cref="SourceType"/> <c>Derived</c>, so an organizer picks "BSFS 2-A (2025-2026-1)"
/// from the same list as "SSC Officers" and nothing downstream learns that one of them was
/// materialized. Publishing the provenance lets a UI group and label the two kinds without needing a
/// second endpoint or a naming convention.
/// </para>
/// </summary>
/// <param name="Type">
/// §4.7's set plus <c>College</c>, <c>Program</c> and <c>YearLevel</c> — <c>Course</c>,
/// <c>Section</c>, <c>Org</c>, <c>Custom</c>, <c>College</c>, <c>Program</c>, <c>YearLevel</c>. What
/// kind of audience this is, and the axis <c>GET /student-groups?type=…</c> narrows by.
/// </param>
/// <param name="SourceType">
/// <c>Manual</c> (a person made it) or <c>Derived</c> (the projection owns it). The distinction
/// matters to a caller: a derived group's membership is rewritten from the academic tables on every
/// import, so hand-editing it is not durable, while a manual group is nobody's to reconcile.
/// </param>
/// <param name="SourceEntityType">
/// Which academic concept a derived group projects — <c>College</c>, <c>Program</c>, <c>Section</c>,
/// <c>CourseOffering</c>, <c>YearLevel</c> — or <c>None</c> on a manual group. <c>None</c> is a
/// sentinel rather than a null so the column stays NOT NULL and the projection's unique index has no
/// nullable component; a year group carries its own value rather than borrowing that sentinel, because
/// <c>None</c> is what a reader tests to mean "the projection did not build this".
/// </param>
/// <param name="TermId">
/// The term a derived group belongs to; null on a manual group, which spans terms by nature.
/// <b>Filtering by it is the main reason to filter at all</b> — section names are reused every
/// semester against an entirely different set of students, so "BSFS 2-A" without a term names several
/// distinct cohorts. <see cref="Name"/> carries the term for the same reason.
/// </param>
/// <param name="MemberCount">
/// Members excluding the soft-deleted, counted in the database as part of the same query. This is the
/// number an organizer is really choosing on — "invite BSCRIM 2-A" is a different decision at 8
/// students than at 80 — and a group showing zero is the visible signal that a projection has not run
/// for its term.
/// </param>
/// <param name="LastSyncedAt">
/// When the projection last reconciled this group, stamped on every run <em>including</em> runs that
/// changed nothing — so "synced, no change" is distinguishable from "never synced". Null on a manual
/// group, which the projection never touches.
/// </param>
public record StudentGroupDto(
    Guid Id, string Name, string Type,
    string SourceType, string SourceEntityType,
    Guid? TermId, string? TermCode,
    int MemberCount, DateTime? LastSyncedAt);
