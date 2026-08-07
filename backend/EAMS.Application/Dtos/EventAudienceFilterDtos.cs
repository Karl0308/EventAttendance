namespace EAMS.Application.Dtos;

// D-50/D-51's audience filter builder, as it crosses the wire. The registry of what may be filtered on
// is EAMS.Domain.AudienceField; these are only the shapes that carry a selection to the resolver and a
// resolution back.
//
// Nothing here attaches anything. `POST /events/audience/resolve` is a read that happens to be a POST
// — see IEventService.ResolveAudienceAsync — and D-52's attach is a separate, later decision.

/// <summary>
/// The body of <c>POST /events/audience/resolve</c> — a term and a set of filter rows.
/// </summary>
/// <param name="TermId">
/// <b>Optional, and omitting it means the term flagged <c>IsCurrent</c> — not "every term".</b>
///
/// <para>
/// The default follows <c>IAcademicReferenceService.ListCourseOfferingsAsync</c>, which is the endpoint
/// this builder's section and course values come from, and it follows it for the same reason: section
/// names repeat every semester against an entirely different cohort, so an unscoped resolution would
/// stack several distinct audiences under identical names and grow with every import the institution has
/// ever run. It also keeps the create-event screen honest — the §4 mock-up has a filter builder and no
/// term picker, so a required <c>termId</c> would be a field the UI has nowhere to get.
/// </para>
///
/// <para>
/// <b>The term actually resolved against comes back on <see cref="AudienceResolutionDto.TermId"/>.</b>
/// That is the price of the default and it is what makes it safe: a caller can see which semester its
/// count describes rather than trusting that the server picked the one it had in mind.
/// </para>
///
/// <para>
/// <b>When no term is flagged current and none was named, the resolution is empty and its
/// <c>termId</c> is <c>null</c>.</b> Zero current terms is a real state — the flag is capped at one per
/// school by a filtered unique index, not pinned at one, and between semesters nobody has moved it.
/// Widening to every term in that case would restore the unbounded read the default exists to close, on
/// the one day nobody is watching for it. A caller that needs to tell "this filter matches nobody" from
/// "no term is current" reads <c>termId</c>, or asks <c>GET /academic/terms/current</c>.
/// </para>
/// </param>
/// <param name="Filters">
/// The rows of the builder. Null or empty resolves the whole term — which is the operator's "or all",
/// not a malformed request.
/// </param>
public record AudienceResolveRequest(Guid? TermId, IReadOnlyList<AudienceFilterDto>? Filters);

/// <summary>
/// One row of the filter builder: a field, and the values selected on it.
/// </summary>
/// <param name="Field">
/// One of <b>five</b> values — <c>College</c>, <c>Program</c>, <c>YearLevel</c>, <c>Section</c>,
/// <c>Course</c> — matched case-insensitively. See <see cref="EAMS.Domain.AudienceField"/> for the whole
/// registry and for why the list is closed rather than open over the student columns.
///
/// <para>
/// <b>Anything else is <c>400 UnknownAudienceField</c>, never a row that is quietly skipped.</b> A
/// filter that vanishes produces a count that is larger than the operator asked for, looks entirely
/// normal, and is invisible until the wrong people are standing in a hall.
/// </para>
/// </param>
/// <param name="Values">
/// The values selected on this field — ids for <c>College</c>, <c>Program</c> and <c>Course</c>, the
/// bare year digit for <c>YearLevel</c>, the section key for <c>Section</c>.
///
/// <para>
/// <b>Within one row the values union; across rows they intersect (D-51).</b>
/// <c>Year is any of 2, 3</c> matches 2nd <em>or</em> 3rd years; adding <c>Program is BSIT</c> narrows
/// that to 2nd- and 3rd-year BSIT students. Two rows naming the same field intersect like any other
/// pair, because a row is a row — <c>Year in (2,3)</c> and <c>Year in (3,4)</c> resolves to year 3.
/// </para>
///
/// <para>
/// <b>An empty or absent list makes the row a no-op, not "match nobody".</b> An empty-but-present row is
/// a half-finished edit, and resolving it to zero would collapse the count to nothing while the operator
/// is still choosing values. The field is still validated: <c>{"field": "Nickname", "values": []}</c> is
/// a 400.
/// </para>
///
/// <para>
/// <b>A value the field cannot express is <c>400 InvalidAudienceFilterValue</c>; a well-formed value
/// that matches nobody is simply an empty answer.</b> The line is between the two kinds of wrong: a
/// college id that is not a GUID is a type error the caller can only have made by mistake, whereas a
/// perfectly good id naming a college that no longer exists is the same "filter matched nothing" every
/// other read in this system already answers with an empty list. The blank string is refused on every
/// field, and on <c>Section</c> so is the <c>(unspecified)</c> sentinel — both when it is sent verbatim,
/// as <c>GET /academic/course-offerings</c> publishes it for a blank-section offering, and when a blank
/// value normalizes onto it. That key means "the source recorded no section", it is the one audience
/// <c>POST /events/{id}/attendees</c> refuses as <c>NotACohort</c>, and the builder must not be a way
/// around that.
/// </para>
/// </param>
public record AudienceFilterDto(string Field, IReadOnlyList<string>? Values);

/// <summary>
/// What a filter set resolves to: how many students match, who they are, and enough of them to preview.
///
/// <para>
/// <b>A student in two matching sections is counted once.</b> That is the ADR-001 D-2 tripwire and the
/// reason this endpoint exists at all rather than a query over <c>Students.Section</c>. It holds
/// structurally rather than by a <c>DISTINCT</c> bolted on the end: the resolution is rooted in
/// <c>StudentTermRecords</c>, which carries exactly one row per student per term, and the section and
/// course filters are existence tests over <c>Enrollments</c> that cannot multiply it.
/// </para>
/// </summary>
/// <param name="TermId">
/// The term this resolution was actually computed against — the one requested, or the current one when
/// none was. <c>null</c> means no term could be resolved, in which case every other field is empty; see
/// <see cref="AudienceResolveRequest.TermId"/>.
/// </param>
/// <param name="Count">
/// <b>How many students the filter matches. It is never bounded by anything below it.</b>
///
/// <para>
/// This is the number the builder shows and the number an operator acts on, so it is a <c>COUNT(*)</c>
/// over the composed filter and not the length of <see cref="StudentIds"/>. The distinction is D-42's:
/// the live endpoint once published counters bounded by its read ceiling rather than by what it had
/// actually returned, and the result was a headline number describing rows nobody received — internally
/// consistent, entirely plausible, wrong. If <see cref="StudentIds"/> is short, this field is why, and
/// <see cref="StudentIdsTruncated"/> says so.
/// </para>
/// </param>
/// <param name="StudentIds">
/// The matched students, ordered by student number then id — a total order, so two identical requests
/// return the same list in the same sequence.
///
/// <para>
/// <b>Bounded by <see cref="StudentIdLimit"/>, and the bound is announced rather than inferred.</b> When
/// <see cref="StudentIdsTruncated"/> is <c>true</c> this is the first <see cref="StudentIdLimit"/> of
/// that ordering and <see cref="Count"/> is the real size. <b>Do not attach an audience by posting a
/// truncated list back</b> — an attach re-runs the filter server-side (D-52), which is the only way the
/// invited set and the previewed count can be the same set.
/// </para>
/// </param>
/// <param name="StudentIdsTruncated">
/// Whether <see cref="StudentIds"/> is shorter than <see cref="Count"/>. Published as its own flag
/// rather than left for a client to derive by comparing lengths: the comparison is easy to forget, and
/// forgetting it is silent.
/// </param>
/// <param name="StudentIdLimit">
/// The ceiling <see cref="StudentIds"/> was cut at, whether or not it was reached. Echoed so a caller
/// can size its own behaviour against the server's actual limit instead of a number compiled into it.
/// </param>
/// <param name="Sample">
/// The first few matched students, with names — the "[ Preview ]" list. It is a <em>prefix</em> of
/// <see cref="StudentIds"/>, from the same ordering in the same query shape, so the first name in the
/// preview is the first id in the list.
/// </param>
public record AudienceResolutionDto(
    Guid? TermId,
    int Count,
    IReadOnlyList<Guid> StudentIds,
    bool StudentIdsTruncated,
    int StudentIdLimit,
    IReadOnlyList<AudienceStudentDto> Sample);

/// <summary>
/// One student in a resolution preview. Identity and a display name, and deliberately nothing else —
/// this is a filter preview, not a roster read, and <c>section</c> in particular is absent because the
/// only single-valued answer available for it is the ADR-001 D-2 cache column this whole feature is
/// built to avoid reading.
/// </summary>
public record AudienceStudentDto(Guid StudentId, string StudentNumber, string FullName);
