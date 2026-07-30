using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// The read surface over ADR-001 D-1's academic layer — terms, colleges, programmes, courses and
/// course offerings. <b>Reads only, and that is a decision rather than an unfinished half.</b>
///
/// <para>
/// The §10 roster import owns every one of these tables. A course created by hand would be matched by
/// the importer on its normalized key and either silently overwritten or, if the operator typed the
/// code differently, duplicated as a second row that splits the enrollments between them. Until there
/// is a decision about which side wins, the honest surface is the one that cannot create the conflict.
/// </para>
///
/// <para>
/// <b>Five entity families behind one interface, not five interfaces.</b> They are one read model:
/// fed by a single import, versioned by a single term, and consumed together by a single screen — an
/// audience picker needs colleges to narrow programmes to narrow offerings in one interaction. Five
/// one-method interfaces would be five registrations and five constructor parameters describing one
/// cohesive thing. The §4.7 grouping layer is deliberately <em>not</em> in here: see
/// <see cref="IStudentGroupService"/>.
/// </para>
///
/// <para>
/// <b>Tenancy is the global <c>SchoolId</c> query filter, not a predicate written here.</b> All five
/// entities are covered — the four that own a <c>SchoolId</c> column directly, and
/// <c>CourseOfferings</c>, which reaches one through its required <c>Term</c>. Nothing in this
/// interface's implementation calls <c>IgnoreQueryFilters</c>, and nothing should: every row these
/// methods return goes straight to an HTTP caller, so widening the filter here is a disclosure bug
/// rather than the scoping convenience it is inside the group projection.
/// </para>
/// </summary>
public interface IAcademicReferenceService
{
    /// <summary>
    /// Every term in the resolved school, current first and then newest code first — the order a term
    /// picker wants, since the answer is almost always "this one" and otherwise "the one before".
    /// </summary>
    Task<IReadOnlyList<TermDto>> ListTermsAsync(CancellationToken ct = default);

    /// <summary>
    /// The term flagged <c>IsCurrent</c>, or <c>null</c> when none is.
    ///
    /// <para>
    /// <b>At most one can come back, and that is the database's guarantee rather than this method's.</b>
    /// A filtered unique index caps <c>IsCurrent</c> at one row per school; without it two "current"
    /// terms would make every term-defaulting query pick one at random, which is the class of bug that
    /// surfaces as a report being quietly about the wrong semester.
    /// </para>
    ///
    /// <para>
    /// <c>null</c> is an ordinary answer, not a fault: between semesters nobody may have flagged one
    /// yet. The caller turns it into a 404 so a client can tell "no current term" from "a term with no
    /// fields", which an empty 200 body cannot express.
    /// </para>
    /// </summary>
    Task<TermDto?> GetCurrentTermAsync(CancellationToken ct = default);

    /// <summary>Every college in the resolved school, by name.</summary>
    Task<IReadOnlyList<CollegeDto>> ListCollegesAsync(CancellationToken ct = default);

    /// <summary>
    /// Degree programmes, optionally narrowed to one college.
    /// </summary>
    /// <param name="collegeId">
    /// Null lists them all. An id that matches no college returns an empty list rather than
    /// everything — a filter that silently stops filtering is how an "invite this college" flow ends
    /// up inviting the institution.
    /// </param>
    Task<IReadOnlyList<AcademicProgramDto>> ListProgramsAsync(
        Guid? collegeId, CancellationToken ct = default);

    /// <summary>
    /// Courses, optionally narrowed to one college and/or matched against a search term.
    /// </summary>
    /// <param name="collegeId">
    /// Null lists them all. Note that a course's college is itself nullable — an unattributed course
    /// is matched by no <paramref name="collegeId"/>, which is correct: it is not known to be in one.
    /// </param>
    /// <param name="search">
    /// Matched against <em>both</em> the course code and the title, because a user searching "criminology"
    /// is naming the title while one searching "SSCI" is naming the code, and asking which they meant
    /// is a worse experience than searching both. Substring, case-insensitive by the database's
    /// collation.
    /// </param>
    Task<IReadOnlyList<CourseDto>> ListCoursesAsync(
        Guid? collegeId, string? search, CancellationToken ct = default);

    /// <summary>
    /// Course offerings — <b>the section grain</b>, and the row an event-audience picker is really
    /// looking for. A course taught to two sections is two entries.
    /// </summary>
    /// <param name="termId">
    /// Null lists every term's offerings. Callers should almost always pass one: section names repeat
    /// every semester against a different cohort, so an unscoped list contains several distinct
    /// audiences under identical names.
    /// </param>
    /// <param name="courseId">Null lists offerings of every course.</param>
    /// <param name="section">
    /// <b>Normalized before it is compared, never after.</b> <c>'BSFS 2-A'</c>, <c>'bsfs2a'</c> and
    /// <c>'BSFS-2A'</c> are one section, and matching the raw string would make the filter depend on
    /// how the caller happened to type it. Pass the display form; the service normalizes.
    /// </param>
    Task<IReadOnlyList<CourseOfferingDto>> ListCourseOfferingsAsync(
        Guid? termId, Guid? courseId, string? section, CancellationToken ct = default);
}
