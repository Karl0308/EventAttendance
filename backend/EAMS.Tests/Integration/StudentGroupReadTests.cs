using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The Phase 3b-2 §4.7 group reads — <b>the list an event audience is picked from</b>.
///
/// <para>
/// <see cref="StudentGroupProjectionTests"/> owns whether the derived groups are <em>correct</em>;
/// this file owns whether they can be found. The two filters are the whole of the endpoint's
/// behaviour and both fail the same dangerous way: a filter that silently stops filtering hands an
/// organizer every group in the school, including last semester's cohorts under names that differ only
/// by a term suffix.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentGroupReadTests : IntegrationTest
{
    public StudentGroupReadTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// A school with one manual group and one term's worth of projected ones, built by running the real
    /// <see cref="IStudentGroupProjection"/> rather than by writing <c>Derived</c> rows by hand — a
    /// fixture that minted its own provenance could pass against a shape the projection never produces.
    /// </summary>
    private async Task<(Guid SchoolId, Guid TermId)> ArrangeProjectedSchoolAsync(
        string code = "USA", string section = "BSFS 2-A")
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool(code);
        db.Schools.Add(school);

        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);

        var college = TestData.NewCollege(school.Id);
        db.Colleges.Add(college);
        var program = TestData.NewProgram(school.Id, college.Id);
        db.Programs.Add(program);

        var course = TestData.NewCourse(school.Id);
        db.Courses.Add(course);
        var offering = TestData.NewOffering(term.Id, course.Id, section);
        db.CourseOfferings.Add(offering);

        var student = TestData.NewStudent(school.Id, $"{code}-0001");
        db.Students.Add(student);
        db.Enrollments.Add(TestData.NewEnrollment(student.Id, offering.Id));
        db.StudentTermRecords.Add(
            TestData.NewTermRecord(student.Id, term.Id, program.Id, college.Id, homeSection: section));

        db.StudentGroups.Add(TestData.NewGroup(school.Id, $"SSC Officers ({code})"));

        await db.SaveChangesAsync();
        await ProjectionOn(db).SyncTermAsync(term.Id);

        return (school.Id, term.Id);
    }

    // ------------------------------------------------------------------------------------ basics

    /// <summary>
    /// The manual group and the projected ones come back from one call, which is the property the
    /// picker depends on: an organizer chooses "BSFS 2-A (2025-2026-1)" from the same list as
    /// "SSC Officers" and nothing downstream learns one of them was materialized.
    /// </summary>
    [Fact]
    public async Task Manual_and_derived_groups_are_listed_together_with_their_provenance()
    {
        await ArrangeProjectedSchoolAsync();

        await using var db = NewDbContext();
        var groups = (await StudentGroupsOn(db).ListAsync(null, null, null, null, PageRequest.Default)).Items;

        Assert.Contains(groups, g => g.SourceType == GroupSourceType.Manual);
        Assert.Contains(groups, g => g.SourceType == GroupSourceType.Derived);

        var section = Assert.Single(
            groups, g => g.SourceEntityType == GroupSourceEntityType.Section);

        Assert.Equal(StudentGroupType.Section, section.Type);
        Assert.Equal(1, section.MemberCount);
        Assert.NotNull(section.TermId);
        Assert.Equal("2025-2026-1", section.TermCode);
        Assert.NotNull(section.LastSyncedAt);
    }

    /// <summary>
    /// A manual group carries no term and has never been synced, and both are published as nulls rather
    /// than as zero values — "never synced" and "synced, changed nothing" are different facts, and the
    /// projection stamps <c>LastSyncedAt</c> on every run precisely so they stay distinguishable.
    /// </summary>
    [Fact]
    public async Task A_manual_group_carries_no_term_and_no_sync_stamp()
    {
        await ArrangeProjectedSchoolAsync();

        await using var db = NewDbContext();
        var groups = (await StudentGroupsOn(db).ListAsync(GroupSourceType.Manual, null, null, null, PageRequest.Default)).Items;

        var manual = Assert.Single(groups);
        Assert.Null(manual.TermId);
        Assert.Null(manual.TermCode);
        Assert.Null(manual.LastSyncedAt);
        Assert.Equal(GroupSourceEntityType.None, manual.SourceEntityType);
    }

    // ----------------------------------------------------------------------------------- tenancy

    /// <summary>
    /// <c>StudentGroups</c> owns a <c>SchoolId</c> column, so the filter is a column comparison — but
    /// both schools here hold a group whose <em>name</em> is built from the same section, so a missing
    /// filter returns a duplicate-looking pair rather than an obviously foreign row.
    /// </summary>
    [Fact]
    public async Task Groups_are_scoped_to_the_resolved_school()
    {
        await ArrangeProjectedSchoolAsync("AAA");
        var (second, _) = await ArrangeProjectedSchoolAsync("ZZZ");

        School.CurrentSchoolId = second;

        await using var db = NewDbContext();
        var groups = (await StudentGroupsOn(db).ListAsync(null, null, null, null, PageRequest.Default)).Items;

        Assert.NotEmpty(groups);
        Assert.Contains(groups, g => g.Name == "SSC Officers (ZZZ)");
        Assert.DoesNotContain(groups, g => g.Name == "SSC Officers (AAA)");

        // Negative control: unpinned, both schools' groups are visible from the identical call — so the
        // exclusion above is the query filter's doing and not an artifact of the fixture.
        await using var unscoped = NewDbContext(new TestSchoolContext());
        var all = (await StudentGroupsOn(unscoped).ListAsync(null, null, null, null, PageRequest.Default)).Items;
        Assert.Contains(all, g => g.Name == "SSC Officers (AAA)");
        Assert.Contains(all, g => g.Name == "SSC Officers (ZZZ)");
    }

    // ----------------------------------------------------------------------------- sourceType

    [Fact]
    public async Task The_source_type_filter_narrows_to_one_provenance()
    {
        await ArrangeProjectedSchoolAsync();

        await using var db = NewDbContext();
        var service = StudentGroupsOn(db);

        var derived = (await service.ListAsync(GroupSourceType.Derived, null, null, null, PageRequest.Default)).Items;
        Assert.NotEmpty(derived);
        Assert.All(derived, g => Assert.Equal(GroupSourceType.Derived, g.SourceType));

        var manual = (await service.ListAsync(GroupSourceType.Manual, null, null, null, PageRequest.Default)).Items;
        Assert.All(manual, g => Assert.Equal(GroupSourceType.Manual, g.SourceType));

        Assert.Equal(
            ((await service.ListAsync(null, null, null, null, PageRequest.Default)).Items).Count,
            derived.Count + manual.Count);
    }

    /// <summary>
    /// Casing is canonicalized rather than rejected — liberal in what is accepted, canonical in what is
    /// compared, the rule the whole <c>DomainValues</c> family follows.
    /// </summary>
    [Theory]
    [InlineData("Derived")]
    [InlineData("derived")]
    [InlineData("DERIVED")]
    [InlineData("  Derived  ")]
    public async Task The_source_type_filter_is_case_insensitive(string spelling)
    {
        await ArrangeProjectedSchoolAsync();

        await using var db = NewDbContext();
        var groups = (await StudentGroupsOn(db).ListAsync(spelling, null, null, null, PageRequest.Default)).Items;

        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.Equal(GroupSourceType.Derived, g.SourceType));
    }

    /// <summary>
    /// <b>An undocumented value returns nothing, not everything.</b> The asymmetry is the decision: a
    /// filter that silently stops filtering offers manual groups to a flow that asked for cohorts, and
    /// nothing in the response says the filter was dropped. Empty is a visible wrong answer; unfiltered
    /// is an invisible one.
    /// </summary>
    [Fact]
    public async Task An_undocumented_source_type_returns_no_groups_rather_than_all_of_them()
    {
        await ArrangeProjectedSchoolAsync();

        await using var db = NewDbContext();
        var service = StudentGroupsOn(db);

        Assert.NotEmpty((await service.ListAsync(null, null, null, null, PageRequest.Default)).Items);
        Assert.Empty((await service.ListAsync("Banana", null, null, null, PageRequest.Default)).Items);
    }

    // ------------------------------------------------------------------------------------ type

    /// <summary>
    /// A section arranged to be <em>programme-shaped</em> — its key begins with the fixture
    /// programme's own <c>CodeKey</c> — so D-47's anchor fires and the term gains a year-level group.
    /// The default <c>BSFS 2-A</c> deliberately does not, which is what keeps every test above
    /// unchanged.
    ///
    /// <para>
    /// The value is arranged rather than taken from the real roster: whether the registrar's
    /// <c>PROGRAM</c> column produces a code that prefixes its own section names is an open question,
    /// and nothing in this file depends on the answer. What is being tested here is the filter, not
    /// the derivation.
    /// </para>
    /// </summary>
    private const string ProgrammeShapedSection = "BSCRIM 2-A";

    /// <summary>
    /// <b>The axis an audience builder picks along</b> — "show me the year levels", "show me the
    /// sections" — and the reason D-49 made year a <c>Type</c> rather than a new event-side concept.
    /// </summary>
    [Fact]
    public async Task The_type_filter_narrows_to_one_kind_of_audience()
    {
        await ArrangeProjectedSchoolAsync(section: ProgrammeShapedSection);

        await using var db = NewDbContext();
        var service = StudentGroupsOn(db);

        var years = (await service.ListAsync(
            null, StudentGroupType.YearLevel, null, null, PageRequest.Default)).Items;

        var year = Assert.Single(years);
        Assert.Equal(StudentGroupType.YearLevel, year.Type);
        Assert.Equal(GroupSourceEntityType.YearLevel, year.SourceEntityType);
        Assert.Equal("2nd Year (2025-2026-1)", year.Name);

        // The other kinds are still there and still findable — the filter narrowed, it did not empty.
        var sections = (await service.ListAsync(
            null, StudentGroupType.Section, null, null, PageRequest.Default)).Items;
        Assert.All(sections, g => Assert.Equal(StudentGroupType.Section, g.Type));
        Assert.Contains(sections, g => g.Name == $"{ProgrammeShapedSection} (2025-2026-1)");

        Assert.True(
            (await service.ListAsync(null, null, null, null, PageRequest.Default)).Items.Count
                > years.Count + sections.Count,
            "The unfiltered list must be larger than the two filtered ones, or the filter proved nothing.");
    }

    /// <summary>
    /// Casing is canonicalized rather than rejected, exactly as <c>sourceType</c> is — liberal in what
    /// is accepted, canonical in what is compared. The comparison has to be decided in C# rather than
    /// left to the database's case-insensitive collation, or the rule would hold only for as long as
    /// the deployment's collation does.
    /// </summary>
    [Theory]
    [InlineData("YearLevel")]
    [InlineData("yearlevel")]
    [InlineData("YEARLEVEL")]
    [InlineData("  YearLevel  ")]
    public async Task The_type_filter_is_case_insensitive(string spelling)
    {
        await ArrangeProjectedSchoolAsync(section: ProgrammeShapedSection);

        await using var db = NewDbContext();
        var groups = (await StudentGroupsOn(db).ListAsync(
            null, spelling, null, null, PageRequest.Default)).Items;

        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.Equal(StudentGroupType.YearLevel, g.Type));
    }

    /// <summary>
    /// <b>An undocumented value returns nothing, not everything</b> — the same asymmetry
    /// <c>sourceType</c> draws, and the stakes are higher on this one. This is the filter an audience
    /// builder narrows by, so a value that silently stopped filtering would hand a year picker every
    /// college, programme, section and offering in the term, under names that look entirely plausible
    /// beside each other. Empty is a wrong answer somebody notices immediately; unfiltered is one
    /// nobody notices at all.
    /// </summary>
    [Fact]
    public async Task An_undocumented_type_returns_no_groups_rather_than_all_of_them()
    {
        await ArrangeProjectedSchoolAsync(section: ProgrammeShapedSection);

        await using var db = NewDbContext();
        var service = StudentGroupsOn(db);

        Assert.NotEmpty((await service.ListAsync(null, null, null, null, PageRequest.Default)).Items);
        Assert.Empty((await service.ListAsync(null, "Banana", null, null, PageRequest.Default)).Items);

        // "Year" is the near miss worth pinning: it is what somebody types from memory, and it is not
        // a member of the set. It has to be refused like any other unknown rather than prefix-matched.
        Assert.Empty((await service.ListAsync(null, "Year", null, null, PageRequest.Default)).Items);
    }

    // ---------------------------------------------------------------------------------- search

    /// <summary>
    /// The name is the whole of what a picker renders, so it is the whole of what <c>search</c> looks
    /// at. It carries the term suffix the projection composes in, which is what makes a semester
    /// searchable without a second parameter.
    /// </summary>
    [Fact]
    public async Task The_search_filter_matches_the_display_name()
    {
        await ArrangeProjectedSchoolAsync(section: ProgrammeShapedSection);

        await using var db = NewDbContext();
        var service = StudentGroupsOn(db);

        var year = Assert.Single(
            (await service.ListAsync(null, null, null, "2nd Year", PageRequest.Default)).Items);
        Assert.Equal(StudentGroupType.YearLevel, year.Type);

        // A manual group is reachable by the same parameter — the picker does not know or care which
        // half of the list a name came from.
        var manual = Assert.Single(
            (await service.ListAsync(null, null, null, "Officers", PageRequest.Default)).Items);
        Assert.Equal(GroupSourceType.Manual, manual.SourceType);

        // Case-insensitive, and a substring rather than a prefix.
        var byProgramme =
            (await service.ListAsync(null, null, null, "bscrim", PageRequest.Default)).Items;
        Assert.NotEmpty(byProgramme);
        Assert.All(byProgramme, g => Assert.Contains(
            "BSCRIM", g.Name, StringComparison.OrdinalIgnoreCase));

        Assert.Empty(
            (await service.ListAsync(null, null, null, "no group is named this", PageRequest.Default)).Items);

        // Blank is "do not filter" rather than "match the empty string", which would be the same thing
        // here but is not the same statement — a picker with an empty search box must show everything.
        Assert.NotEmpty(
            (await service.ListAsync(null, null, null, "   ", PageRequest.Default)).Items);
    }

    // --------------------------------------------------------------------------------- termId

    /// <summary>
    /// <b>The filter that matters most.</b> A section name is reused every semester against an entirely
    /// different set of students, so two terms produce two groups whose names differ only by the term
    /// suffix the projection composes in — and inviting the wrong one invites last year's cohort.
    /// </summary>
    [Fact]
    public async Task The_term_filter_separates_two_terms_that_reuse_one_section_name()
    {
        Guid thisTermId;
        Guid lastTermId;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);

            var thisTerm = TestData.NewTerm(school.Id, "2025-2026-1");
            var lastTerm = TestData.NewTerm(school.Id, "2024-2025-1", isCurrent: false);
            db.Terms.AddRange(thisTerm, lastTerm);
            thisTermId = thisTerm.Id;
            lastTermId = lastTerm.Id;

            var course = TestData.NewCourse(school.Id);
            db.Courses.Add(course);

            // The same section name in both terms, against different students.
            var thisOffering = TestData.NewOffering(thisTerm.Id, course.Id, "BSFS 2-A");
            var lastOffering = TestData.NewOffering(lastTerm.Id, course.Id, "BSFS 2-A");
            db.CourseOfferings.AddRange(thisOffering, lastOffering);

            var current = TestData.NewStudent(school.Id, "2025-0001");
            var former = TestData.NewStudent(school.Id, "2024-0001");
            db.Students.AddRange(current, former);
            db.Enrollments.AddRange(
                TestData.NewEnrollment(current.Id, thisOffering.Id),
                TestData.NewEnrollment(former.Id, lastOffering.Id));

            await db.SaveChangesAsync();

            await ProjectionOn(db).SyncTermAsync(thisTerm.Id);
            await ProjectionOn(db).SyncTermAsync(lastTerm.Id);
        }

        await using var read = NewDbContext();
        var service = StudentGroupsOn(read);

        var thisTermGroups = (await service.ListAsync(null, null, thisTermId, null, PageRequest.Default)).Items;
        var lastTermGroups = (await service.ListAsync(null, null, lastTermId, null, PageRequest.Default)).Items;

        Assert.NotEmpty(thisTermGroups);
        Assert.NotEmpty(lastTermGroups);
        Assert.All(thisTermGroups, g => Assert.Equal(thisTermId, g.TermId));
        Assert.All(lastTermGroups, g => Assert.Equal(lastTermId, g.TermId));

        // Same section, two terms, disjoint groups — which is exactly why the name carries the term.
        Assert.Empty(thisTermGroups.Select(g => g.Id).Intersect(lastTermGroups.Select(g => g.Id)));

        Assert.Empty((await service.ListAsync(null, null, Guid.NewGuid(), null, PageRequest.Default)).Items);
    }

    /// <summary>
    /// The member count is an audience size, so it excludes the soft-deleted — matching
    /// <c>CourseOfferingDto.EnrolledCount</c> and the <c>expected</c> denominator on an event summary.
    /// A count that disagreed with the denominator would make an organizer's pre-event estimate
    /// unreconcilable with the post-event report.
    /// </summary>
    [Fact]
    public async Task The_member_count_excludes_soft_deleted_students()
    {
        Guid groupId;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);

            var group = TestData.NewGroup(school.Id);
            db.StudentGroups.Add(group);
            groupId = group.Id;

            var present = TestData.NewStudent(school.Id, "2023-0001");
            var deleted = TestData.NewStudent(school.Id, "2023-0002");
            deleted.IsDeleted = true;
            db.Students.AddRange(present, deleted);

            db.StudentGroupMembers.AddRange(
                new StudentGroupMember { StudentGroupId = group.Id, StudentId = present.Id },
                new StudentGroupMember { StudentGroupId = group.Id, StudentId = deleted.Id });

            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var groups = (await StudentGroupsOn(read).ListAsync(null, null, null, null, PageRequest.Default)).Items;

        Assert.Equal(1, Assert.Single(groups, g => g.Id == groupId).MemberCount);
    }
}
