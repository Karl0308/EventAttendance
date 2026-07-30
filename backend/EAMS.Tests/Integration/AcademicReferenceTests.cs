using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The Phase 3b-2 academic reference reads, at the service level.
///
/// <para>
/// <see cref="AcademicApiTests"/> owns what a client codes against — status codes, the RFC 7807 body,
/// the JSON member names. This file owns the behaviour underneath: which rows come back, in what
/// order, and — the half that is invisible from HTTP and matters most — <b>that every filter actually
/// filters and that no read crosses a tenant boundary</b>.
/// </para>
///
/// <para>
/// <b>Why "an unmatched filter returns empty" is asserted on every filter rather than once.</b> The
/// failure mode is not a wrong row, it is a silently ignored predicate — a filter that stops filtering
/// returns <em>everything</em>, and every one of these endpoints feeds an event-audience picker. The
/// visible symptom of that bug is an operator inviting an institution when they meant a section, which
/// is discovered after the event. A per-filter assertion is cheap; discovering this one from a
/// production roster is not.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AcademicReferenceTests : IntegrationTest
{
    public AcademicReferenceTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Two schools with a full academic structure each, so every tenancy assertion below has something
    /// real to leak. Returns the two school ids in creation order.
    ///
    /// <para>
    /// The <em>second</em> school is the one the tests pin, deliberately: a filter bug that returned
    /// the first school's rows would look like a pass if the test pinned the first.
    /// </para>
    /// </summary>
    private async Task<(Guid First, Guid Second)> ArrangeTwoSchoolsAsync()
    {
        await using var db = NewDbContext();

        var first = TestData.NewSchool("AAA");
        var second = TestData.NewSchool("ZZZ");
        db.Schools.AddRange(first, second);

        foreach (var schoolId in new[] { first.Id, second.Id })
        {
            var term = TestData.NewTerm(schoolId);
            db.Terms.Add(term);

            var college = TestData.NewCollege(schoolId);
            db.Colleges.Add(college);
            db.Programs.Add(TestData.NewProgram(schoolId, college.Id));

            var course = TestData.NewCourse(schoolId, collegeId: college.Id);
            db.Courses.Add(course);
            db.CourseOfferings.Add(TestData.NewOffering(term.Id, course.Id));
        }

        await db.SaveChangesAsync();
        return (first.Id, second.Id);
    }

    // ------------------------------------------------------------------------------------- terms

    [Fact]
    public async Task Terms_are_listed_current_first_then_newest_code_first()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        db.Terms.AddRange(
            TestData.NewTerm(school.Id, "2024-2025-1", isCurrent: false),
            TestData.NewTerm(school.Id, "2025-2026-2", isCurrent: false),
            TestData.NewTerm(school.Id, "2024-2025-2", isCurrent: true));
        await db.SaveChangesAsync();

        var terms = await AcademicOn(db).ListTermsAsync();

        // The current one leads even though its code sorts below '2025-2026-2'.
        Assert.Equal(
            ["2024-2025-2", "2025-2026-2", "2024-2025-1"],
            terms.Select(t => t.Code));
    }

    /// <summary>
    /// <c>Terms</c> owns a <c>SchoolId</c> column, so this is the simplest shape the filter takes —
    /// and the baseline the offering test below is contrasted against.
    /// </summary>
    [Fact]
    public async Task Terms_are_scoped_to_the_resolved_school()
    {
        var (_, second) = await ArrangeTwoSchoolsAsync();
        School.CurrentSchoolId = second;

        await using var db = NewDbContext();
        var terms = await AcademicOn(db).ListTermsAsync();

        Assert.Single(terms);

        // Negative control: unpinned, the same call sees both schools' terms — so the assertion above
        // is about the filter rather than about how much the fixture happened to write.
        await using var unscoped = NewDbContext(new TestSchoolContext());
        Assert.Equal(2, (await AcademicOn(unscoped).ListTermsAsync()).Count);
    }

    [Fact]
    public async Task The_current_term_is_the_flagged_one()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        db.Terms.AddRange(
            TestData.NewTerm(school.Id, "2024-2025-1", isCurrent: false),
            TestData.NewTerm(school.Id, "2025-2026-1", isCurrent: true));
        await db.SaveChangesAsync();

        var current = await AcademicOn(db).GetCurrentTermAsync();

        Assert.NotNull(current);
        Assert.Equal("2025-2026-1", current.Code);
        Assert.True(current.IsCurrent);
    }

    /// <summary>
    /// No term flagged is an ordinary state — it is what exists between semesters, before anyone has
    /// moved the flag — and the service says so with a null rather than throwing. The controller is
    /// what turns it into a 404; see <see cref="AcademicApiTests"/>.
    /// </summary>
    [Fact]
    public async Task There_is_no_current_term_when_none_is_flagged()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        db.Terms.Add(TestData.NewTerm(school.Id, isCurrent: false));
        await db.SaveChangesAsync();

        Assert.Null(await AcademicOn(db).GetCurrentTermAsync());
    }

    /// <summary>
    /// <b>Each school gets its own current term, and one must not be visible from the other.</b> The
    /// filtered unique index caps <c>IsCurrent</c> at one row <em>per school</em>, so two current terms
    /// legitimately exist across two tenants — which makes this the one read where a broken filter
    /// returns a plausible, wrong answer rather than an obviously wrong count.
    /// </summary>
    [Fact]
    public async Task The_current_term_does_not_cross_a_tenant_boundary()
    {
        var (_, second) = await ArrangeTwoSchoolsAsync();
        School.CurrentSchoolId = second;

        await using var db = NewDbContext();
        var current = await AcademicOn(db).GetCurrentTermAsync();

        Assert.NotNull(current);

        await using var unscoped = NewDbContext(new TestSchoolContext());
        var owner = (await unscoped.Terms.SingleAsync(t => t.Id == current.Id)).SchoolId;
        Assert.Equal(second, owner);
    }

    // ---------------------------------------------------------------------------------- colleges

    [Fact]
    public async Task Colleges_are_listed_and_scoped_to_the_resolved_school()
    {
        var (_, second) = await ArrangeTwoSchoolsAsync();
        School.CurrentSchoolId = second;

        await using var db = NewDbContext();
        var colleges = await AcademicOn(db).ListCollegesAsync();

        Assert.Single(colleges);
        Assert.Equal("College of Criminal Justice", colleges[0].Name);
    }

    // ---------------------------------------------------------------------------------- programs

    [Fact]
    public async Task Programs_are_filtered_by_college_and_an_unmatched_college_returns_none()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var criminology = TestData.NewCollege(school.Id, "College of Criminal Justice");
        var nursing = TestData.NewCollege(school.Id, "College of Nursing");
        db.Colleges.AddRange(criminology, nursing);
        db.Programs.AddRange(
            TestData.NewProgram(school.Id, criminology.Id, "BSCRIM"),
            TestData.NewProgram(school.Id, nursing.Id, "BSN"));
        await db.SaveChangesAsync();

        var service = AcademicOn(db);

        Assert.Equal(["BSCRIM", "BSN"], (await service.ListProgramsAsync(null)).Select(p => p.Code));

        var filtered = await service.ListProgramsAsync(nursing.Id);
        Assert.Equal(["BSN"], filtered.Select(p => p.Code));
        Assert.Equal("College of Nursing", filtered[0].CollegeName);

        // The assertion that matters: a filter naming nothing returns nothing, never everything.
        Assert.Empty(await service.ListProgramsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Programs_are_scoped_to_the_resolved_school()
    {
        var (_, second) = await ArrangeTwoSchoolsAsync();
        School.CurrentSchoolId = second;

        await using var db = NewDbContext();
        Assert.Single(await AcademicOn(db).ListProgramsAsync(null));
    }

    // ----------------------------------------------------------------------------------- courses

    /// <summary>
    /// Search hits the code and the title, because a user typing "criminology" is naming one and a user
    /// typing "SSCI" is naming the other. Both directions are asserted — a search implemented over the
    /// code alone passes a title-only test by returning nothing and looks like "no results".
    /// </summary>
    [Fact]
    public async Task Courses_are_searchable_by_code_and_by_title()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        db.Courses.AddRange(
            TestData.NewCourse(school.Id, "SSCI 7", "Introduction to Criminology"),
            TestData.NewCourse(school.Id, "NURS 1", "Fundamentals of Nursing"));
        await db.SaveChangesAsync();

        var service = AcademicOn(db);

        Assert.Equal(["SSCI 7"], (await service.ListCoursesAsync(null, "SSCI")).Select(c => c.Code));
        Assert.Equal(["SSCI 7"], (await service.ListCoursesAsync(null, "Criminology")).Select(c => c.Code));
        Assert.Equal(["NURS 1"], (await service.ListCoursesAsync(null, "Nursing")).Select(c => c.Code));

        Assert.Empty(await service.ListCoursesAsync(null, "Astrophysics"));
    }

    /// <summary>
    /// A course's college is nullable, so an unattributed course is matched by no <c>collegeId</c> —
    /// correct, because it is not known to be in one rather than known not to be. Pinned because the
    /// natural "fix" (treating null as a wildcard) would quietly put every unattributed course into
    /// every college's picker.
    /// </summary>
    [Fact]
    public async Task Courses_are_filtered_by_college_and_an_unattributed_course_matches_no_college()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var college = TestData.NewCollege(school.Id);
        db.Colleges.Add(college);
        db.Courses.AddRange(
            TestData.NewCourse(school.Id, "SSCI 7", collegeId: college.Id),
            TestData.NewCourse(school.Id, "GE Elect 2", collegeId: null));
        await db.SaveChangesAsync();

        var service = AcademicOn(db);

        Assert.Equal(2, (await service.ListCoursesAsync(null, null)).Count);

        var filtered = await service.ListCoursesAsync(college.Id, null);
        Assert.Equal(["SSCI 7"], filtered.Select(c => c.Code));
        Assert.Equal("College of Criminal Justice", filtered[0].CollegeName);

        Assert.Empty(await service.ListCoursesAsync(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task Courses_are_scoped_to_the_resolved_school()
    {
        var (_, second) = await ArrangeTwoSchoolsAsync();
        School.CurrentSchoolId = second;

        await using var db = NewDbContext();
        Assert.Single(await AcademicOn(db).ListCoursesAsync(null, null));
    }

    // -------------------------------------------------------------------------- course offerings

    /// <summary>
    /// <b>The grain assertion, and the reason this endpoint exists.</b> One course taught to two
    /// sections is two rows — that is what an invitation is issued against, and it is precisely what
    /// <c>Students.Section</c> (a single-valued ADR-001 D-2 cache column) cannot express.
    /// </summary>
    [Fact]
    public async Task A_course_taught_to_two_sections_is_two_offerings()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        var course = TestData.NewCourse(school.Id, "SSCI 7");
        db.Courses.Add(course);
        db.CourseOfferings.AddRange(
            TestData.NewOffering(term.Id, course.Id, "BSFS 2-A"),
            TestData.NewOffering(term.Id, course.Id, "BSFS 2-B"));
        await db.SaveChangesAsync();

        var offerings = await AcademicOn(db).ListCourseOfferingsAsync(term.Id, null, null);

        Assert.Equal(2, offerings.Count);
        Assert.Equal(["BSFS 2-A", "BSFS 2-B"], offerings.Select(o => o.SectionName).Order());
        Assert.All(offerings, o => Assert.Equal("SSCI 7", o.CourseCode));
        Assert.All(offerings, o => Assert.Equal("2025-2026-1", o.TermCode));
    }

    /// <summary>
    /// Every offering filter, including that each one narrows rather than merely reorders — and the
    /// section filter's normalization, which is the one a caller cannot see and would otherwise
    /// discover by getting no results for a section they can see on screen.
    /// </summary>
    [Fact]
    public async Task Offerings_filter_by_term_course_and_a_normalized_section()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var thisTerm = TestData.NewTerm(school.Id, "2025-2026-1");
        var lastTerm = TestData.NewTerm(school.Id, "2024-2025-1", isCurrent: false);
        db.Terms.AddRange(thisTerm, lastTerm);

        var criminology = TestData.NewCourse(school.Id, "SSCI 7");
        var nursing = TestData.NewCourse(school.Id, "NURS 1");
        db.Courses.AddRange(criminology, nursing);

        db.CourseOfferings.AddRange(
            TestData.NewOffering(thisTerm.Id, criminology.Id, "BSFS 2-A"),
            TestData.NewOffering(thisTerm.Id, nursing.Id, "BSN 1-A"),
            TestData.NewOffering(lastTerm.Id, criminology.Id, "BSFS 2-A"));
        await db.SaveChangesAsync();

        var service = AcademicOn(db);

        Assert.Equal(3, (await service.ListCourseOfferingsAsync(null, null, null)).Count);
        Assert.Equal(2, (await service.ListCourseOfferingsAsync(thisTerm.Id, null, null)).Count);
        Assert.Equal(2, (await service.ListCourseOfferingsAsync(null, criminology.Id, null)).Count);
        Assert.Single(await service.ListCourseOfferingsAsync(thisTerm.Id, criminology.Id, null));

        // 'BSFS 2-A', 'bsfs2a' and 'BSFS-2A' are one section. Matching the raw string would make the
        // filter depend on how the caller happened to type it — and would silently return nothing.
        foreach (var spelling in new[] { "BSFS 2-A", "bsfs2a", "BSFS-2A", " bsfs 2a " })
        {
            Assert.Equal(
                2,
                (await service.ListCourseOfferingsAsync(null, null, spelling)).Count);
        }

        Assert.Empty(await service.ListCourseOfferingsAsync(null, null, "NO SUCH SECTION"));
        Assert.Empty(await service.ListCourseOfferingsAsync(Guid.NewGuid(), null, null));
        Assert.Empty(await service.ListCourseOfferingsAsync(null, Guid.NewGuid(), null));
    }

    /// <summary>
    /// <b><c>CourseOfferings</c> is the one table in this layer with no <c>SchoolId</c> column</b> — it
    /// reaches a tenant only through its required <c>Term</c>, so its query filter is a join rather than
    /// a column comparison and is the one most likely to have been left off. Both schools hold an
    /// offering of an identically-named section here, so a missing filter returns two rows that look
    /// entirely plausible.
    /// </summary>
    [Fact]
    public async Task Offerings_are_scoped_through_their_term_to_the_resolved_school()
    {
        var (_, second) = await ArrangeTwoSchoolsAsync();
        School.CurrentSchoolId = second;

        await using var db = NewDbContext();
        var offerings = await AcademicOn(db).ListCourseOfferingsAsync(null, null, null);

        Assert.Single(offerings);

        await using var unscoped = NewDbContext(new TestSchoolContext());

        // The negative control, and without it this test could pass on a fixture that only ever wrote
        // one offering — which would assert nothing about the filter. Unpinned ("do not filter"), the
        // identical call sees both schools' rows, so the Single above is the filter's doing.
        Assert.Equal(2, (await AcademicOn(unscoped).ListCourseOfferingsAsync(null, null, null)).Count);

        var owner = (await unscoped.Terms.SingleAsync(t => t.Id == offerings[0].TermId)).SchoolId;
        Assert.Equal(second, owner);
    }

    /// <summary>
    /// <b>The enrolment count is the audience size, so a soft-deleted student is not in it</b> — the
    /// same rule every other read in the system applies, and the one that keeps this number reconcilable
    /// with the <c>expected</c> denominator on an event summary.
    /// </summary>
    [Fact]
    public async Task The_enrolled_count_comes_from_enrollments_and_excludes_the_soft_deleted()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        var course = TestData.NewCourse(school.Id);
        db.Courses.Add(course);
        var offering = TestData.NewOffering(term.Id, course.Id, "BSFS 2-A");
        db.CourseOfferings.Add(offering);

        var present = TestData.NewStudent(school.Id, "2023-0001");
        var deleted = TestData.NewStudent(school.Id, "2023-0002");
        deleted.IsDeleted = true;
        db.Students.AddRange(present, deleted);
        db.Enrollments.AddRange(
            TestData.NewEnrollment(present.Id, offering.Id),
            TestData.NewEnrollment(deleted.Id, offering.Id));
        await db.SaveChangesAsync();

        var offerings = await AcademicOn(db).ListCourseOfferingsAsync(term.Id, null, null);

        Assert.Equal(1, Assert.Single(offerings).EnrolledCount);
    }

    /// <summary>
    /// <b>The ADR-001 D-2 tripwire for this endpoint, posed as the case the cache is actually wrong
    /// about.</b>
    ///
    /// <para>
    /// One student is enrolled in two offerings in <em>different</em> sections — the shape twelve of the
    /// fifty-two real students have, and the shape a single-valued <c>Students.Section</c> cannot
    /// represent. Their cached triple says <c>BSIT / 3rd Year / A</c>, which matches neither. If this
    /// endpoint read the cache, the student would count towards at most one of the two offerings and
    /// neither of the section names would agree; reading <c>Enrollments</c>, they count in both.
    /// </para>
    ///
    /// <para>
    /// This is deliberately a <em>behavioural</em> assertion rather than a check that the DTO has no
    /// <c>section</c> member. A DTO shape can be satisfied while the query underneath still joins the
    /// wrong table — and it is the query that decides whether an event invites the right people.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_student_in_two_sections_counts_in_both_because_enrollments_not_the_cache_are_read()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        var course = TestData.NewCourse(school.Id);
        db.Courses.Add(course);

        var first = TestData.NewOffering(term.Id, course.Id, "BSFS 2-A");
        var second = TestData.NewOffering(term.Id, course.Id, "BSCRIM 2-B");
        db.CourseOfferings.AddRange(first, second);

        // The cache says one thing; the enrollments say another. TestData's defaults already disagree
        // with both sections, which is the realistic case rather than a contrived one.
        var student = TestData.NewStudent(school.Id, "2023-0001");
        Assert.Equal("A", student.Section);
        db.Students.Add(student);
        db.Enrollments.AddRange(
            TestData.NewEnrollment(student.Id, first.Id),
            TestData.NewEnrollment(student.Id, second.Id));
        await db.SaveChangesAsync();

        var offerings = await AcademicOn(db).ListCourseOfferingsAsync(term.Id, null, null);

        Assert.Equal(2, offerings.Count);
        Assert.All(offerings, o => Assert.Equal(1, o.EnrolledCount));

        // And the cache's own value names no offering at all — proof the filter is not reading it.
        Assert.Empty(await AcademicOn(db).ListCourseOfferingsAsync(term.Id, null, student.Section));
    }
}
