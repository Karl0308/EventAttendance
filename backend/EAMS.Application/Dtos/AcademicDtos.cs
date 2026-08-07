namespace EAMS.Application.Dtos;

// The read surface over ADR-001 D-1's academic layer, plus the one write surface D-53 carved out of
// it. Phase 3b-2 was reads only: the §10 import owns every one of these tables, and a hand-written
// course would be silently overwritten — or worse, duplicated under a second key — by the next roster
// run. That still holds for colleges, programmes, courses and offerings, and none of them has a write
// counterpart here.
//
// `Term` is the exception, and the carve-out is narrower than it looks: the importer only ever *reads*
// Terms — it takes a TermId as input (ADR-001 D-5) and writes no row of that table — so the conflict
// the read-only rule exists to prevent cannot arise. See TermWriteRequest.
//
// **Every DTO below publishes the display column and hides the *Key one, with one exception.** The
// normalized keys (`NameKey`, `CodeKey`) exist so `'SSCI 7'` and `'SSci7'` resolve to one row; they
// are an internal matching device and a client that branched on one would be coding against the
// normalizer's current rules. `CourseOfferingDto.SectionKey` is the exception and is published
// deliberately — see the remarks on that type.

/// <summary>
/// Technical Plan §4 / ADR-001 D-1 <c>Terms</c>, as a client sees it.
///
/// <para>
/// <b><see cref="StartsOn"/> and <see cref="EndsOn"/> are dates, not instants, and stay that way on
/// the wire.</b> A term boundary is a calendar date in the school's own timezone — it has no time of
/// day, and serializing it as a UTC instant would shift it by eight hours in Manila and make "does
/// this term contain today" answerable two different ways. They publish as <c>YYYY-MM-DD</c>.
/// </para>
/// </summary>
/// <param name="IsCurrent">
/// At most one term per school carries this, enforced by a filtered unique index rather than by
/// convention. <c>GET /academic/terms/current</c> is the direct route to it; this flag is here so a
/// list rendering does not need a second request to mark the row.
/// </param>
public record TermDto(
    Guid Id, string Code, string SchoolYear, string Semester, bool IsCurrent,
    DateOnly? StartsOn, DateOnly? EndsOn);

/// <summary>
/// The body of <c>POST /academic/terms</c> and <c>PUT /academic/terms/{id}</c> (D-53).
///
/// <para>
/// <b><see cref="Code"/> is stored exactly as typed.</b> It is the one natural key in the academic
/// layer that is authored by a person rather than scraped from a spreadsheet cell, so there is no dirt
/// to clean and <c>AcademicKey</c> never touches it. A value with leading or trailing whitespace is a
/// <c>400</c> rather than a silent trim — see <see cref="EAMS.Domain.TermText"/> for why refusing beats
/// normalizing here.
/// </para>
///
/// <para>
/// <b><c>SchoolId</c> is deliberately absent</b>, exactly as it is on <c>EventWriteRequest</c> and
/// <c>DeviceWriteRequest</c>: the tenant comes from
/// <see cref="EAMS.Application.Abstractions.ISchoolContext"/>, never from the request.
/// </para>
///
/// <para>
/// <b><c>IsCurrent</c> is deliberately absent too, and that is not symmetry for its own sake.</b>
/// Moving the flag is a two-row operation guarded by a filtered unique index
/// (<c>UX_Terms_SchoolId_Current</c>), so it is <c>PATCH /academic/terms/{id}/current</c> — the same
/// split <c>PATCH /events/{id}/status</c> draws for the same reason. It also keeps the create path's
/// unique-violation arm unambiguous: with the current flag out of the body, the only index a create can
/// possibly break is <c>UX_Terms_SchoolId_Code</c>, so answering <c>TermCodeExists</c> is a fact rather
/// than a guess between two indexes.
/// </para>
/// </summary>
/// <param name="StartsOn">
/// Optional, and normally absent — the SIS export has no term-date columns, so a real term carries
/// neither date. Supplied as <c>YYYY-MM-DD</c>: a term boundary is a calendar date in the school's own
/// timezone and has no time of day.
/// </param>
/// <param name="EndsOn">
/// Optional. If both dates are supplied, this one may not fall before <paramref name="StartsOn"/>.
/// </param>
public record TermWriteRequest(
    string Code, string SchoolYear, string Semester, DateOnly? StartsOn, DateOnly? EndsOn);

/// <summary>
/// The body of <c>PATCH /academic/terms/{id}/current</c> (D-53).
/// </summary>
/// <param name="IsCurrent">
/// <b>Nullable so that omitting it is a refusal rather than a decision.</b> A non-nullable
/// <c>bool</c> deserializes a missing member to <c>false</c>, which on this route means "clear the
/// current term" — a client that forgot the field would silently retire the school's term and get a
/// <c>200</c> for it. Send <c>true</c> to make this term current, or <c>false</c> to retire it; there is
/// no default.
///
/// <para>
/// <c>false</c> is the whole of "retiring a term", and it is why this route is not named
/// <c>make-current</c>: D-53 offers no deletion, because a term with a batch imported against it cannot
/// be removed without data loss. Clearing the flag leaves zero current terms, which is an ordinary state
/// the read surface already answers for — <c>GET /academic/terms/current</c> 404s and
/// <c>GET /academic/course-offerings</c> returns an empty page.
/// </para>
/// </param>
public record TermCurrentRequest(bool? IsCurrent);

/// <summary>
/// A college. <paramref name="Code"/> is nullable because the roster source has no college code
/// column at all — the natural key is the normalized name, and the code exists so a real one can be
/// recorded later without a migration.
/// </summary>
public record CollegeDto(Guid Id, string Name, string? Code);

/// <summary>
/// A degree programme — BSCRIM, BSFS, BSN. The CLR entity is <c>AcademicProgram</c> rather than
/// <c>Program</c> (the API's top-level-statements entry point owns that name); the table, the route
/// and this DTO all keep the plan's word.
/// </summary>
/// <param name="CollegeId">
/// Never null. The roster carries <c>COLLEGE_NAME</c> on every row, so a programme's college is
/// always derivable at import — which is why this is a required FK and
/// <see cref="CourseDto.CollegeId"/> is not.
/// </param>
public record AcademicProgramDto(
    Guid Id, string Code, string? Name, Guid CollegeId, string CollegeName);

/// <summary>
/// A course, keyed on the normalized course code.
/// </summary>
/// <param name="Title">
/// <b>Nullable, and not identity.</b> <c>COURSE_CODE → COURSE_NAME</c> is not 1:1 in the source —
/// <c>'GE Elect 2'</c> resolves to two different names inside a single college's export — so two
/// source titles collapse onto one row and one of them is lost. A missing or surprising title here is
/// the import reporting that anomaly, not a bug in this endpoint.
/// </param>
/// <param name="CollegeId">
/// <b>Nullable today, and the nullability is load-bearing.</b> The course key is currently unique
/// across the whole institution; if generic codes (<c>GE Elect n</c>, <c>PE n</c>, <c>NSTP n</c>) turn
/// out to collide between colleges, the key widens to include this column. Clients should treat an
/// absent college as "not yet attributed", never as "no college".
/// </param>
public record CourseDto(
    Guid Id, string Code, string? Title, Guid? CollegeId, string? CollegeName);

/// <summary>
/// One course, in one term, taught to one named section — <b>the row an event-audience picker
/// actually wants</b>.
///
/// <para>
/// <b>This is the real "section", and it is why <c>Students.Section</c> cannot answer the question.</b>
/// That column is the ADR-001 D-2 derived display cache and is single-valued; twelve of the fifty-two
/// students in the real roster sit in more than one section, so no single-valued column can describe
/// them. A course taught to two sections is two rows here, which is the grain an invitation is issued
/// at.
/// </para>
///
/// <para>
/// <b><see cref="SectionKey"/> is published even though the other normalized keys are not.</b> It is
/// the only stable identifier a <em>cohort</em> has: a section is a key shared by every offering that
/// cohort takes within a term (<c>BSFS2A</c> spans all of them) and has no row of its own to point at,
/// which is exactly why the group projection keys section groups on it. A caller grouping offerings
/// into cohorts needs it; <see cref="SectionName"/> is the display form and is null where the source
/// was blank — 39 sample rows are — in which case the key is the <c>Unspecified</c> sentinel rather
/// than null, because a nullable key component would have capped the table at one blank section.
/// </para>
/// </summary>
/// <param name="EnrolledCount">
/// Students enrolled in this offering, excluding the soft-deleted — the size of the audience
/// inviting this offering would produce. Counted in the database as part of the same query, never by
/// loading the enrollment rows.
/// </param>
public record CourseOfferingDto(
    Guid Id,
    Guid TermId, string TermCode,
    Guid CourseId, string CourseCode, string? CourseTitle,
    string? SectionName, string SectionKey,
    int EnrolledCount);
