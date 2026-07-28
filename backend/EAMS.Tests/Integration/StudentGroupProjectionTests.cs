using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The ADR-001 D-1 projection: colleges, programmes, sections and course offerings materialized into
/// §4.7 <c>StudentGroups</c> so §4.8 <c>EventGroups</c> and §12's expected-attendee denominator keep
/// working without learning that the academic layer exists.
///
/// <para>
/// The world these tests arrange is a miniature of the real roster and is deliberately shaped around
/// its two awkward facts: one student sits in two sections (12 of 52 do), and one offering has no
/// section name at all (39 rows do not).
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentGroupProjectionTests : IntegrationTest
{
    public StudentGroupProjectionTests(SqlServerFixture sql) : base(sql) { }

    private const string TermCode = "2025-2026-1";

    /// <summary>
    /// <para><b>Two sections.</b> <c>BSFS 2-A</c> takes SSCI 7 and CA 2; <c>BS Chem 2-A</c> takes CHEM 1.</para>
    /// <para><b>Three students.</b> Santos and Flores are in BSFS 2-A only. Aquino is in both — the
    /// multi-section case that <c>Students.Section</c> cannot represent.</para>
    /// </summary>
    private sealed record World(
        Guid SchoolId, Guid TermId, Guid CollegeId, Guid ProgramId,
        Guid SantosId, Guid FloresId, Guid AquinoId,
        Guid CriminologyOfferingId, Guid ChemistryOfferingId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var term = TestData.NewTerm(school.Id, TermCode);
        db.Terms.Add(term);

        var college = TestData.NewCollege(school.Id, "College of Criminal Justice");
        db.Colleges.Add(college);
        var program = TestData.NewProgram(school.Id, college.Id, "BSCRIM");
        db.Programs.Add(program);

        var ssci = TestData.NewCourse(school.Id, "SSCI 7", "Introduction to Criminology");
        var ca2 = TestData.NewCourse(school.Id, "CA  2", "Criminalistics 2");
        var chem = TestData.NewCourse(school.Id, "CHEM 1", "General Chemistry");
        db.Courses.AddRange(ssci, ca2, chem);

        var criminology = TestData.NewOffering(term.Id, ssci.Id, "BSFS 2-A");
        var criminalistics = TestData.NewOffering(term.Id, ca2.Id, "BSFS 2-A");
        var chemistry = TestData.NewOffering(term.Id, chem.Id, "BS Chem 2-A");
        db.CourseOfferings.AddRange(criminology, criminalistics, chemistry);

        var santos = TestData.NewStudent(school.Id, "2023-0001", lastName: "Santos");
        var flores = TestData.NewStudent(school.Id, "2023-0006", lastName: "Flores");
        var aquino = TestData.NewStudent(school.Id, "2023-0007", lastName: "Aquino");
        db.Students.AddRange(santos, flores, aquino);

        foreach (var student in new[] { santos, flores, aquino })
        {
            db.StudentTermRecords.Add(TestData.NewTermRecord(
                student.Id, term.Id, program.Id, college.Id));
            db.Enrollments.Add(TestData.NewEnrollment(student.Id, criminology.Id));
            db.Enrollments.Add(TestData.NewEnrollment(student.Id, criminalistics.Id));
        }

        // The multi-section student. This single row is the whole reason the academic layer exists.
        db.Enrollments.Add(TestData.NewEnrollment(aquino.Id, chemistry.Id));

        await db.SaveChangesAsync();

        return new World(
            school.Id, term.Id, college.Id, program.Id,
            santos.Id, flores.Id, aquino.Id,
            criminology.Id, chemistry.Id);
    }

    private async Task<GroupProjectionResult> SyncAsync(Guid termId)
    {
        await using var db = NewDbContext();
        return await ProjectionOn(db).SyncTermAsync(termId);
    }

    private async Task<StudentGroup> ReadGroupAsync(string sourceEntityType, string sourceKey)
    {
        await using var db = NewDbContext();
        return await db.StudentGroups.AsNoTracking().Include(g => g.Members)
            .SingleAsync(g => g.SourceEntityType == sourceEntityType && g.SourceKey == sourceKey);
    }

    // ------------------------------------------------------------------ what it materializes

    /// <summary>
    /// One group per college, per programme, per section key, and per offering. Four kinds, and
    /// section is not the same thing as offering: "everyone in BSFS 2-A" is a cohort, "SSCI 7 – BSFS
    /// 2-A" is a class, and an organizer needs to be able to invite either.
    /// </summary>
    [Fact]
    public async Task It_materializes_a_group_per_college_programme_section_and_offering()
    {
        var world = await ArrangeAsync();

        var result = await SyncAsync(world.TermId);

        await using var read = NewDbContext();
        var groups = await read.StudentGroups.AsNoTracking()
            .Where(g => g.SourceType == GroupSourceType.Derived).ToListAsync();

        Assert.Equal(1, groups.Count(g => g.SourceEntityType == GroupSourceEntityType.College));
        Assert.Equal(1, groups.Count(g => g.SourceEntityType == GroupSourceEntityType.Program));
        Assert.Equal(2, groups.Count(g => g.SourceEntityType == GroupSourceEntityType.Section));
        Assert.Equal(3, groups.Count(g => g.SourceEntityType == GroupSourceEntityType.CourseOffering));
        Assert.Equal(7, result.GroupsCreated);
        Assert.All(groups, g => Assert.Equal(world.TermId, g.TermId));
        Assert.All(groups, g => Assert.NotNull(g.LastSyncedAt));
    }

    /// <summary>
    /// §4.7's <c>Type</c> is what the admin UI groups and filters by, so a projected group has to land
    /// in a sensible bucket rather than all of them being "Custom".
    /// </summary>
    [Fact]
    public async Task Each_group_carries_the_section_four_type_that_matches_what_it_projects()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        await using var read = NewDbContext();
        var groups = await read.StudentGroups.AsNoTracking()
            .Where(g => g.SourceType == GroupSourceType.Derived)
            .Select(g => new { g.SourceEntityType, g.Type })
            .ToListAsync();

        // Grouped in memory rather than in SQL: this is an assertion about a handful of rows, and a
        // GroupBy projection that EF cannot translate fails at runtime with a message about the
        // expression tree instead of about the schema.
        var typesBySource = groups
            .GroupBy(g => g.SourceEntityType)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Type).Distinct().ToList());

        Assert.Equal(new[] { StudentGroupType.College }, typesBySource[GroupSourceEntityType.College]);
        Assert.Equal(new[] { StudentGroupType.Program }, typesBySource[GroupSourceEntityType.Program]);
        Assert.Equal(new[] { StudentGroupType.Section }, typesBySource[GroupSourceEntityType.Section]);
        Assert.Equal(new[] { StudentGroupType.Course }, typesBySource[GroupSourceEntityType.CourseOffering]);
    }

    /// <summary>
    /// <b>The multi-section student lands in both section groups.</b> This is the assertion the whole
    /// phase is for: <c>Students.Section</c> can hold one of BSFS 2-A and BS Chem 2-A, so a
    /// section-filtered audience read from it silently omits Aquino from one of the two events she
    /// belongs to. Read from <c>Enrollments</c>, she is in both.
    /// </summary>
    [Fact]
    public async Task A_student_enrolled_across_two_sections_is_a_member_of_both_section_groups()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        var bsfs = await ReadGroupAsync(GroupSourceEntityType.Section, "BSFS2A");
        var chem = await ReadGroupAsync(GroupSourceEntityType.Section, "BSCHEM2A");

        Assert.Equal(3, bsfs.Members.Count);
        Assert.Contains(bsfs.Members, m => m.StudentId == world.AquinoId);

        Assert.Single(chem.Members);
        Assert.Equal(world.AquinoId, chem.Members.Single().StudentId);
    }

    /// <summary>
    /// Section names repeat every year against different students, and an <c>EventGroups</c> row from a
    /// past term still points at the group it was written against. Without the term in the name, an
    /// organizer reading a past event's audience sees a name that now describes a different cohort and
    /// nothing on screen says so.
    /// </summary>
    [Fact]
    public async Task Every_derived_group_name_carries_its_term()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        await using var read = NewDbContext();
        var names = await read.StudentGroups.AsNoTracking()
            .Where(g => g.SourceType == GroupSourceType.Derived)
            .Select(g => g.Name).ToListAsync();

        Assert.All(names, n => Assert.EndsWith($"({TermCode})", n));
        Assert.Contains($"BSFS 2-A ({TermCode})", names);
        Assert.Contains($"College of Criminal Justice ({TermCode})", names);
        Assert.Contains($"BSCRIM ({TermCode})", names);
        Assert.Contains($"SSCI 7 — BSFS 2-A ({TermCode})", names);
    }

    /// <summary>
    /// A section group projects a <em>key</em> shared by many offerings, so there is no row for it to
    /// point at — which is why <c>SourceKey</c> exists at all. The other three do point at a row, and
    /// that is what lets a reader get from a group back to the offering it came from.
    /// </summary>
    [Fact]
    public async Task Only_a_section_group_has_no_source_row_to_point_at()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        await using var read = NewDbContext();
        var groups = await read.StudentGroups.AsNoTracking()
            .Where(g => g.SourceType == GroupSourceType.Derived).ToListAsync();

        Assert.All(
            groups.Where(g => g.SourceEntityType == GroupSourceEntityType.Section),
            g => Assert.Null(g.SourceEntityId));
        Assert.All(
            groups.Where(g => g.SourceEntityType != GroupSourceEntityType.Section),
            g => Assert.NotNull(g.SourceEntityId));

        Assert.Equal(world.CollegeId, groups
            .Single(g => g.SourceEntityType == GroupSourceEntityType.College).SourceEntityId);
        Assert.Equal(world.ProgramId, groups
            .Single(g => g.SourceEntityType == GroupSourceEntityType.Program).SourceEntityId);
        Assert.Contains(groups, g => g.SourceEntityId == world.ChemistryOfferingId);
    }

    /// <summary>
    /// The blank-section case — 39 rows of the real file — produces an <b>offering</b> group but
    /// deliberately <b>no section</b> group.
    ///
    /// <para>
    /// The students still have to be invitable, and they are: the offering group is the precise
    /// audience for "this class". What must not exist is a <em>section</em> group keyed on the
    /// sentinel, because that unions every blank-section offering across every course and programme
    /// into one cohort whose members share nothing but an empty field — then offers it in the admin
    /// UI as a selectable audience beside "BSFS 2-A".
    /// </para>
    ///
    /// <para>
    /// The distinction is the one the model already draws and the projection must not erase:
    /// <c>CourseOfferings.SectionKey</c>'s sentinel means "this class, section not recorded", which
    /// is not the same statement as a cohort.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_offering_with_no_section_name_is_invitable_but_forms_no_section_cohort()
    {
        var world = await ArrangeAsync();
        Guid offeringId;

        await using (var db = NewDbContext())
        {
            var course = TestData.NewCourse(world.SchoolId, "NSTP 1", "Civic Welfare");
            db.Courses.Add(course);
            var offering = TestData.NewOffering(world.TermId, course.Id, sectionName: null);
            db.CourseOfferings.Add(offering);
            offeringId = offering.Id;
            db.Enrollments.Add(TestData.NewEnrollment(world.SantosId, offering.Id));
            await db.SaveChangesAsync();
        }

        await SyncAsync(world.TermId);

        await using var read = NewDbContext();
        var derived = await read.StudentGroups.IgnoreQueryFilters().AsNoTracking()
            .Include(g => g.Members)
            .Where(g => g.SourceType == GroupSourceType.Derived)
            .ToListAsync();

        // Invitable: the offering group exists and holds the student.
        var offeringGroup = Assert.Single(derived, g => g.SourceEntityId == offeringId);
        Assert.Contains(offeringGroup.Members, m => m.StudentId == world.SantosId);

        // But no cohort was fabricated from the sentinel.
        Assert.DoesNotContain(derived, g =>
            g.SourceEntityType == GroupSourceEntityType.Section
            && g.SourceKey == AcademicKey.Unspecified);
    }

    /// <summary>
    /// The reason the exclusion above is not cosmetic. Two blank-section offerings of <em>different</em>
    /// courses would have unioned into a single "(unspecified)" cohort containing both their students —
    /// a group no one could have meant to invite. Each keeps its own offering group instead.
    /// </summary>
    [Fact]
    public async Task Blank_section_offerings_of_different_courses_do_not_merge_into_one_cohort()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            foreach (var (code, title) in new[] { ("NSTP 1", "Civic Welfare"), ("GE 8", "Ethics") })
            {
                var course = TestData.NewCourse(world.SchoolId, code, title);
                db.Courses.Add(course);
                var offering = TestData.NewOffering(world.TermId, course.Id, sectionName: null);
                db.CourseOfferings.Add(offering);
                db.Enrollments.Add(TestData.NewEnrollment(world.SantosId, offering.Id));
            }
            await db.SaveChangesAsync();
        }

        await SyncAsync(world.TermId);

        await using var read = NewDbContext();
        var sectionGroups = await read.StudentGroups.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.SourceType == GroupSourceType.Derived
                        && g.SourceEntityType == GroupSourceEntityType.Section)
            .ToListAsync();

        Assert.DoesNotContain(sectionGroups, g => g.SourceKey == AcademicKey.Unspecified);
    }

    // ------------------------------------------------------------------ idempotency

    /// <summary>
    /// <b>The contract that makes a re-import safe.</b> An import batch is re-runnable by design
    /// (ADR-001 D-5), so this runs on the same data repeatedly. If it accumulated duplicate members,
    /// every §12 denominator built on these groups would inflate a little on each run — a number that
    /// is wrong but never obviously wrong.
    /// </summary>
    [Fact]
    public async Task A_second_run_over_unchanged_data_changes_nothing()
    {
        var world = await ArrangeAsync();

        var first = await SyncAsync(world.TermId);
        Assert.False(first.IsNoOp);

        int groupsAfterFirst, membersAfterFirst;
        await using (var read = NewDbContext())
        {
            groupsAfterFirst = await read.StudentGroups.CountAsync();
            membersAfterFirst = await read.StudentGroupMembers.CountAsync();
        }

        var second = await SyncAsync(world.TermId);

        Assert.True(second.IsNoOp, $"Second run was not a no-op: {second}.");
        await using var after = NewDbContext();
        Assert.Equal(groupsAfterFirst, await after.StudentGroups.CountAsync());
        Assert.Equal(membersAfterFirst, await after.StudentGroupMembers.CountAsync());
    }

    /// <summary>
    /// <c>LastSyncedAt</c> is stamped even by a run that changed nothing, and deliberately does not
    /// count as an update. "Reconciled just now, nothing to do" and "never reconciled" are different
    /// operational states and only one of them warrants investigation.
    /// </summary>
    [Fact]
    public async Task A_no_op_run_still_records_that_it_ran()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        DateTime firstSync;
        await using (var read = NewDbContext())
            firstSync = (await read.StudentGroups.AsNoTracking()
                .FirstAsync(g => g.SourceType == GroupSourceType.Derived)).LastSyncedAt!.Value;

        // Long enough to clear the Windows system-clock granularity DateTime.UtcNow inherits (~15ms) —
        // a shorter wait makes this a coin flip rather than a test.
        await Task.Delay(60);
        var second = await SyncAsync(world.TermId);

        await using var after = NewDbContext();
        var resynced = await after.StudentGroups.AsNoTracking()
            .Where(g => g.SourceType == GroupSourceType.Derived).ToListAsync();

        Assert.True(second.IsNoOp);
        Assert.All(resynced, g => Assert.True(g.LastSyncedAt > firstSync));
    }

    /// <summary>
    /// Membership follows the academic tables in both directions. A dropped enrollment removes the
    /// derived membership; a new one adds it. This is the diff, not a rebuild — the group row and its
    /// identity survive.
    /// </summary>
    [Fact]
    public async Task Dropping_an_enrollment_removes_the_derived_membership_on_the_next_run()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        await using (var db = NewDbContext())
        {
            var enrollment = await db.Enrollments.SingleAsync(
                e => e.StudentId == world.AquinoId && e.CourseOfferingId == world.ChemistryOfferingId);
            db.Enrollments.Remove(enrollment);
            await db.SaveChangesAsync();
        }

        var result = await SyncAsync(world.TermId);

        Assert.Equal(2, result.MembersRemoved); // the section group and the offering group
        var chem = await ReadGroupAsync(GroupSourceEntityType.Section, "BSCHEM2A");
        Assert.Empty(chem.Members); // the group row itself survives — see the next test for why
    }

    /// <summary>
    /// A group whose academic source has gone away keeps its row. Deleting it would break any
    /// historical <c>EventGroups</c> link pointing at it — and with every FK in this model set to
    /// <c>Restrict</c>, the delete would not even succeed; it would abort the whole projection.
    /// </summary>
    [Fact]
    public async Task A_group_whose_source_disappeared_keeps_its_row_and_loses_only_its_members()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        await using (var db = NewDbContext())
        {
            var offering = await db.CourseOfferings.Include(o => o.Enrollments)
                .SingleAsync(o => o.Id == world.ChemistryOfferingId);
            db.Enrollments.RemoveRange(offering.Enrollments);
            db.CourseOfferings.Remove(offering);
            await db.SaveChangesAsync();
        }

        await SyncAsync(world.TermId);

        await using var read = NewDbContext();
        var orphan = await read.StudentGroups.AsNoTracking().Include(g => g.Members)
            .SingleAsync(g => g.SourceEntityId == world.ChemistryOfferingId);

        Assert.Empty(orphan.Members);
        Assert.NotNull(orphan.LastSyncedAt);
        Assert.Equal(2, await read.StudentGroups.CountAsync(
            g => g.SourceEntityType == GroupSourceEntityType.Section));
    }

    // ------------------------------------------------------------------ manual members are sacred

    /// <summary>
    /// <b>The rule the projection must never break.</b> An adviser hand-added to a section's group is
    /// not in <c>Enrollments</c> and never will be, so a naive set-diff deletes them on the very next
    /// import — and the person who added them has no way to know.
    /// </summary>
    [Fact]
    public async Task A_manually_added_member_of_a_derived_group_is_never_removed()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        Guid adviserId;
        await using (var db = NewDbContext())
        {
            var adviser = TestData.NewStudent(world.SchoolId, "2023-9999", lastName: "Adviser");
            db.Students.Add(adviser);
            var group = await db.StudentGroups.SingleAsync(
                g => g.SourceEntityType == GroupSourceEntityType.Section && g.SourceKey == "BSFS2A");
            db.StudentGroupMembers.Add(new StudentGroupMember
            {
                StudentGroup = group, StudentId = adviser.Id, SourceType = GroupSourceType.Manual,
            });
            await db.SaveChangesAsync();
            adviserId = adviser.Id;
        }

        var result = await SyncAsync(world.TermId);

        Assert.Equal(0, result.MembersRemoved);
        var bsfs = await ReadGroupAsync(GroupSourceEntityType.Section, "BSFS2A");
        Assert.Contains(bsfs.Members, m => m.StudentId == adviserId);
        Assert.Equal(GroupSourceType.Manual,
            bsfs.Members.Single(m => m.StudentId == adviserId).SourceType);
    }

    /// <summary>
    /// The same student added by hand and also genuinely enrolled. Adding a second, derived membership
    /// row would not merely duplicate them — <c>UX_StudentGroupMembers_Group_Student</c> is unique on
    /// <c>(StudentGroupId, StudentId)</c>, so it would throw a duplicate-key violation and abort the
    /// whole projection. The rule and the constraint have to agree, and this is where that is proved.
    /// </summary>
    [Fact]
    public async Task A_student_who_is_both_a_manual_member_and_genuinely_enrolled_is_not_duplicated()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var group = TestData.NewGroup(world.SchoolId, $"BSFS 2-A ({TermCode})");
            group.SourceType = GroupSourceType.Derived;
            group.SourceEntityType = GroupSourceEntityType.Section;
            group.SourceKey = "BSFS2A";
            group.TermId = world.TermId;
            db.StudentGroups.Add(group);
            db.StudentGroupMembers.Add(new StudentGroupMember
            {
                StudentGroup = group, StudentId = world.SantosId, SourceType = GroupSourceType.Manual,
            });
            await db.SaveChangesAsync();
        }

        await SyncAsync(world.TermId);

        var bsfs = await ReadGroupAsync(GroupSourceEntityType.Section, "BSFS2A");

        // Three members, not four: Santos was already there and stays there, as a manual row. A
        // derived row for her would have hit UX_StudentGroupMembers_Group_Student and aborted the run.
        Assert.Equal(3, bsfs.Members.Count);
        Assert.Single(bsfs.Members, m => m.StudentId == world.SantosId);
        Assert.Equal(GroupSourceType.Manual,
            bsfs.Members.Single(m => m.StudentId == world.SantosId).SourceType);

        // And the pre-existing group was adopted rather than duplicated — the projection matched it
        // on (SourceEntityType, SourceKey) and reused it.
        await using var read = NewDbContext();
        Assert.Single(await read.StudentGroups
            .Where(g => g.SourceEntityType == GroupSourceEntityType.Section && g.SourceKey == "BSFS2A")
            .ToListAsync());

        Assert.True((await SyncAsync(world.TermId)).IsNoOp);
    }

    /// <summary>
    /// A wholly manual group — "SSC Officers", which has no academic counterpart — is not touched at
    /// all: not renamed, not re-typed, not stamped, and its members are left alone. It is excluded from
    /// the projection's index by the <c>SourceType = 'Derived'</c> filter, which is what makes it
    /// invisible to the diff rather than merely skipped by it.
    /// </summary>
    [Fact]
    public async Task A_manual_group_is_left_entirely_alone()
    {
        var world = await ArrangeAsync();

        Guid manualGroupId;
        await using (var db = NewDbContext())
        {
            var group = TestData.NewGroup(world.SchoolId, "SSC Officers");
            group.Type = StudentGroupType.Org;
            db.StudentGroups.Add(group);
            db.StudentGroupMembers.Add(new StudentGroupMember
            {
                StudentGroup = group, StudentId = world.FloresId,
            });
            await db.SaveChangesAsync();
            manualGroupId = group.Id;
        }

        await SyncAsync(world.TermId);
        await SyncAsync(world.TermId);

        await using var read = NewDbContext();
        var manual = await read.StudentGroups.AsNoTracking().Include(g => g.Members)
            .SingleAsync(g => g.Id == manualGroupId);

        Assert.Equal("SSC Officers", manual.Name);
        Assert.Equal(StudentGroupType.Org, manual.Type);
        Assert.Equal(GroupSourceType.Manual, manual.SourceType);
        Assert.Null(manual.TermId);
        Assert.Null(manual.LastSyncedAt);
        Assert.Equal(world.FloresId, manual.Members.Single().StudentId);
    }

    /// <summary>
    /// Two manual groups collide on nothing. They share <c>SourceEntityType = 'None'</c> and
    /// <c>SourceKey = ''</c>, so an <em>unfiltered</em> version of the derived-group index would have
    /// rejected the second one — which is why the index is filtered to derived rows rather than merely
    /// including the source columns.
    /// </summary>
    [Fact]
    public async Task Several_manual_groups_can_coexist_under_the_derived_group_index()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        db.StudentGroups.Add(TestData.NewGroup(world.SchoolId, "SSC Officers"));
        db.StudentGroups.Add(TestData.NewGroup(world.SchoolId, "Dean's Listers"));
        db.StudentGroups.Add(TestData.NewGroup(world.SchoolId, "Varsity"));
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(3, await read.StudentGroups.CountAsync(g => g.SourceType == GroupSourceType.Manual));
    }

    // ------------------------------------------------------------------ boundaries

    /// <summary>
    /// Terms partition everything. Projecting one term must not disturb another's groups, or importing
    /// next semester would silently rewrite last semester's event audiences.
    /// </summary>
    [Fact]
    public async Task Projecting_one_term_does_not_disturb_another()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        Guid nextTermId;
        await using (var db = NewDbContext())
        {
            var next = TestData.NewTerm(world.SchoolId, "2025-2026-2", isCurrent: false);
            db.Terms.Add(next);
            var course = TestData.NewCourse(world.SchoolId, "SSCI 8", "Criminology 2");
            db.Courses.Add(course);
            var offering = TestData.NewOffering(next.Id, course.Id, "BSFS 2-A");
            db.CourseOfferings.Add(offering);
            db.Enrollments.Add(TestData.NewEnrollment(world.SantosId, offering.Id));
            await db.SaveChangesAsync();
            nextTermId = next.Id;
        }

        await SyncAsync(nextTermId);

        await using var read = NewDbContext();

        // The same section name now exists in both terms, as two distinct groups with distinct names.
        var sections = await read.StudentGroups.AsNoTracking()
            .Where(g => g.SourceEntityType == GroupSourceEntityType.Section && g.SourceKey == "BSFS2A")
            .ToListAsync();

        Assert.Equal(2, sections.Count);
        Assert.Contains(sections, g => g.TermId == world.TermId && g.Name == $"BSFS 2-A ({TermCode})");
        Assert.Contains(sections, g => g.TermId == nextTermId && g.Name == "BSFS 2-A (2025-2026-2)");

        var thisTerm = sections.Single(g => g.TermId == world.TermId);
        Assert.Equal(3, await read.StudentGroupMembers.CountAsync(m => m.StudentGroupId == thisTerm.Id));
    }

    /// <summary>
    /// An unknown term is a caller error, not a request that should quietly do nothing. Returning an
    /// empty result would let a misconfigured import report success having projected no one.
    /// </summary>
    [Fact]
    public async Task Projecting_an_unknown_term_fails_loudly()
    {
        var missing = Guid.NewGuid();

        await using var db = NewDbContext();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectionOn(db).SyncTermAsync(missing));

        Assert.Contains(missing.ToString(), thrown.Message);
    }

    /// <summary>
    /// A term with nothing under it yet — the state immediately after an operator creates it and before
    /// the import runs. It must be a clean no-op rather than an error or an empty group per nothing.
    /// </summary>
    [Fact]
    public async Task Projecting_an_empty_term_creates_nothing()
    {
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            db.Terms.Add(TestData.NewTerm(school.Id));
            await db.SaveChangesAsync();
        }

        Guid termId;
        await using (var read = NewDbContext())
            termId = (await read.Terms.AsNoTracking().SingleAsync()).Id;

        var result = await SyncAsync(termId);

        Assert.True(result.IsNoOp);
        await using var after = NewDbContext();
        Assert.Empty(await after.StudentGroups.ToListAsync());
    }

    /// <summary>
    /// The projection is addressed by term, and a term belongs to one school. Another tenant's students
    /// must not appear in these groups even when no tenant is pinned — which is the state the whole
    /// test suite and every design-time context run in, so the global query filter cannot be what
    /// protects this.
    /// </summary>
    [Fact]
    public async Task Another_schools_students_never_appear_in_a_terms_groups()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("CICSS");
            db.Schools.Add(other);
            var otherTerm = TestData.NewTerm(other.Id, TermCode);
            db.Terms.Add(otherTerm);
            var otherCollege = TestData.NewCollege(other.Id, "College of Nursing");
            db.Colleges.Add(otherCollege);
            var otherStudent = TestData.NewStudent(other.Id, "CICSS-0001", lastName: "Outsider");
            db.Students.Add(otherStudent);
            db.StudentTermRecords.Add(TestData.NewTermRecord(
                otherStudent.Id, otherTerm.Id, collegeId: otherCollege.Id));
            await db.SaveChangesAsync();
        }

        await SyncAsync(world.TermId);

        await using var read = NewDbContext();
        var derived = await read.StudentGroups.AsNoTracking().ToListAsync();

        Assert.All(derived, g => Assert.Equal(world.SchoolId, g.SchoolId));
        Assert.DoesNotContain(derived, g => g.Name.Contains("Nursing"));
    }
}
