namespace EAMS.Domain;

/// <summary>
/// D-50's <b>closed</b> list of the five fields an event audience can be filtered on, and the reason it
/// is closed.
///
/// <para>
/// <b>The point of this type is what is <em>not</em> in it.</b> A generic "filter on any column" builder
/// would offer <c>Students.Course</c>, <c>Students.YearLevel</c> and <c>Students.Section</c> — three
/// columns sitting right on the student entity, which look ideal for exactly this feature and are wrong
/// for roughly a quarter of the roster. ADR-001 D-2 quantifies it: <b>12 of 52 students sit in more than
/// one section</b>, and a single-valued column can only name one of them, so a section filter reading
/// that column returns a plausible, non-empty, incomplete answer with nothing to notice. Every field
/// below resolves through <c>StudentTermRecords</c> or <c>Enrollments</c> instead, which are the tables
/// that carry the real grain.
/// </para>
///
/// <para>
/// <b>The <c>Students</c> cache columns are unreachable by construction rather than by convention.</b>
/// Nothing anywhere accepts a column name: a caller names one of the five values below, the resolver
/// switches on it, and each arm writes its own predicate by hand against the academic tables. There is
/// no string that reaches EF as a column, so there is no input — malformed, hostile or merely
/// enthusiastic — that can select on the derived triple.
/// </para>
///
/// <para>
/// <b>Adding a sixth field is two deliberate edits and cannot be half-done.</b> A constant here plus an
/// arm in the resolver's switch. Add the constant alone and the resolver's final arm throws rather than
/// silently ignoring the filter — the same "no discard fall-through" discipline every
/// <c>StatusCodeFor</c> in the API follows, and for the same reason: an unmapped case that behaves like
/// a mapped one is invisible. Status, gender and has-card were considered for this list and deferred
/// (Phase 5 §7); none was asked for.
/// </para>
///
/// <para>
/// <b>Where each field's selectable values come from</b>, so a picker has one answer per row:
/// </para>
/// <list type="table">
///   <item><term><see cref="College"/></term><description><c>GET /academic/colleges</c> — values are college ids.</description></item>
///   <item><term><see cref="Program"/></term><description><c>GET /academic/programs</c> — values are programme ids.</description></item>
///   <item><term><see cref="YearLevel"/></term><description>the distinct derived years (D-47) — values are bare digits, <c>"2"</c>.</description></item>
///   <item><term><see cref="Section"/></term><description><c>GET /academic/course-offerings</c> — values are <c>sectionKey</c>.</description></item>
///   <item><term><see cref="Course"/></term><description><c>GET /academic/courses</c> — values are course ids.</description></item>
/// </list>
///
/// <para>
/// <b><see cref="YearLevel"/> legitimately offers no values at all on today's real roster, and that is
/// not a broken field.</b> D-47 anchors derivation on the student's own programme code, and the live
/// export spells the programme <c>"BSci - Crim"</c> while its sections read <c>"BSCRIM 2-A"</c> — so the
/// anchor does not fire and every student derives <c>null</c>. A registrar question is open. The correct
/// reading of an empty year list is "no year levels have been derived", never "widen the anchor": a
/// student in the wrong year group is an invisible wrong denominator, which is the whole of D-47.
/// </para>
/// </summary>
public static class AudienceField
{
    /// <summary>Resolves through <c>StudentTermRecords.CollegeId</c>. Values are college ids.</summary>
    public const string College = "College";

    /// <summary>Resolves through <c>StudentTermRecords.ProgramId</c>. Values are programme ids.</summary>
    public const string Program = "Program";

    /// <summary>
    /// Resolves through <c>StudentTermRecords.YearLevel</c> — the value <see cref="YearLevels.Derive"/>
    /// wrote (D-47), which is a bare digit and never a display label. A student whose year derived
    /// <c>null</c> matches no year filter and stays reachable by every other field, which is the
    /// first-class outcome D-47 designed for rather than a gap to paper over.
    /// </summary>
    public const string YearLevel = "YearLevel";

    /// <summary>
    /// Resolves through <c>Enrollments → CourseOfferings.SectionKey</c> — <b>never through
    /// <c>Students.Section</c></b>, which is the ADR-001 D-2 cache this whole registry exists to keep
    /// out of reach. Values are normalized before they are compared, so <c>BSFS 2-A</c>, <c>bsfs2a</c>
    /// and <c>BSFS-2A</c> are one section.
    /// </summary>
    public const string Section = "Section";

    /// <summary>Resolves through <c>Enrollments → CourseOfferings.CourseId</c>. Values are course ids.</summary>
    public const string Course = "Course";

    /// <summary>
    /// The whole list, and the whole of what <see cref="TryNormalize"/> accepts. A field outside it is a
    /// refusal, never a filter row that quietly stops applying — see
    /// <c>IEventService.ResolveAudienceAsync</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [College, Program, YearLevel, Section, Course];

    /// <summary>
    /// Maps any casing of a registered field onto its canonical spelling, exactly as the §4 value sets
    /// in <c>DomainValues</c> do and for the same two reasons: the resolver switches on these with
    /// ordinal <c>==</c>, so a stored-but-differently-cased value would land in no arm; and rejecting a
    /// client over casing alone is a 400 that teaches nothing.
    /// </summary>
    /// <returns><c>false</c> for null, blank, and anything outside <see cref="All"/>.</returns>
    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// The three ceilings <c>POST /events/audience/resolve</c> answers under, named once so the resolver, the
/// published response and the tests that probe the boundary cannot disagree about them.
///
/// <para>
/// <b>Two bound the answer and one bounds the question.</b> <see cref="MaxStudentIds"/> and
/// <see cref="SampleSize"/> cut a response that is too big to send; <see cref="MaxFilterRows"/> refuses a
/// request that is too big to plan. They are separated because the failures are different — an oversized
/// answer is still an answer, an oversized question never becomes one.
/// </para>
///
/// <para>
/// <b>Neither of the response bounds touches <c>count</c>, and that is the D-42 lesson written down.</b> The live
/// endpoint once shipped counters bounded by the read ceiling rather than by the cursor it returned, so
/// a truncated page carried a headline number describing rows it had not delivered — plausible,
/// self-consistent, and wrong. Here <c>count</c> is a <c>COUNT(*)</c> over the composed filter and
/// nothing else; the list below it is what gets bounded, and the response says so out loud when it is.
/// </para>
/// </summary>
public static class AudienceResolutionLimits
{
    /// <summary>
    /// How many <c>studentIds</c> one resolution carries. Beyond this the list is the first
    /// <see cref="MaxStudentIds"/> of a deterministic ordering and the response's
    /// <c>studentIdsTruncated</c> flag is <c>true</c>; <c>count</c> is unaffected.
    ///
    /// <para>
    /// <b>Truncated rather than refused, unlike <see cref="EventManifestLimits.MaxAttendees"/>, because
    /// the two lists are for different things.</b> A manifest is the device's only copy of who may tap,
    /// so a short one silently turns invited students into unknown cards and the honest answer is a loud
    /// 413. This list is a convenience for a preview whose actual answer is <c>count</c> — an operator
    /// building a filter reads the number, not five thousand GUIDs — so bounding it costs the caller
    /// nothing as long as the bound is visible, which the flag makes it.
    /// </para>
    ///
    /// <para>
    /// <b>A caller must not attach an audience by posting back a truncated list.</b> D-52's attach
    /// re-runs the filter server-side for exactly this reason; ids are published for preview and for
    /// client-side cross-referencing, not as the wire format of an invitation.
    /// </para>
    ///
    /// <para>
    /// Five thousand: comfortably above any single-event audience anyone has described, and small
    /// enough that a body driven by a filter builder's keystrokes stays around a couple of hundred
    /// kilobytes rather than a megabyte.
    /// </para>
    /// </summary>
    public const int MaxStudentIds = 5_000;

    /// <summary>
    /// How many students the <c>sample</c> carries — the "[ Preview ]" list beside the count. It is the
    /// first <see cref="SampleSize"/> of the <em>same</em> ordering <c>studentIds</c> uses, so the two
    /// agree by construction rather than by two queries happening to sort alike.
    /// </summary>
    public const int SampleSize = 25;

    /// <summary>
    /// How many filter rows one request may carry. Beyond this the request is refused with
    /// <c>AudienceResolveOutcome.TooManyFilterRows</c> — a <b>400</b>, before the term is looked up and
    /// before any database read.
    ///
    /// <para>
    /// <b>Refused rather than truncated, unlike <see cref="MaxStudentIds"/>, and for the reason every
    /// other refusal on this route exists.</b> Dropping the rows past the ceiling would silently widen
    /// the filter: rows intersect (D-51), so each one discarded can only <em>grow</em> the answer, and
    /// the caller would read a count for a filter it did not build. That is the same failure
    /// <c>UnknownAudienceField</c> and <c>InvalidAudienceFilterValue</c> refuse, one level up from a row.
    /// </para>
    ///
    /// <para>
    /// <b>Twenty, and the number is bounded from both directions.</b> From below: there are five
    /// registered fields (<see cref="AudienceField.All"/>), so a filter set naming each one once is five
    /// rows, and twenty leaves four times that for the repeated-field shape <c>AudienceFilterDto</c>
    /// documents — <c>Year in (2,3)</c> and <c>Year in (3,4)</c> resolving to year 3. Nothing a filter
    /// builder can construct out of five fields needs more. From above: this is composed as one
    /// <c>Where</c> per row, and SQL Server's optimizer measurably suffers well before any plausible
    /// caller does — a 2KB body of 50 rows cost about 3.2 seconds of planning time, and 60 rows exceeded
    /// the server's internal resources outright (Msg 8623) and surfaced as an unhandled exception, i.e.
    /// a 500 for a perfectly well-formed request. Twenty sits comfortably below the region where
    /// planning cost becomes visible at all, so the ceiling is felt only by callers who are not
    /// filtering.
    /// </para>
    ///
    /// <para>
    /// <b>Counted over the rows as sent, not over the conditions composed from them.</b> Whether a row
    /// with no values is dropped is an open decision; counting composed conditions would quietly couple
    /// this ceiling to it, so that changing that decision would change what this refuses. The request's
    /// size is a property of the request.
    /// </para>
    /// </summary>
    public const int MaxFilterRows = 20;
}
