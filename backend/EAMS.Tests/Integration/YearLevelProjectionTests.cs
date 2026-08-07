using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// D-47/D-48/D-49 end to end: the projection derives each student's year level from the sections they
/// are actually enrolled in, writes it to <c>StudentTermRecords</c> and nowhere else, and projects it
/// into an invitable <c>StudentGroup</c> shaped exactly like a section's.
///
/// <para>
/// <see cref="Unit.YearLevelsTests"/> owns whether the <em>rule</em> is right; this file owns whether
/// the projection feeds it the right data, stores the answer in the right column, and turns it into
/// the right audience. The two halves fail differently and only one of them can be seen from a unit
/// test: a rule that is perfect on the arguments it is handed is still wrong if the caller hands it
/// <c>StudentTermRecord.HomeSectionKey</c> — the importer's <em>mode</em>, which for a student whose
/// file rows are mostly <c>NSTP 2</c> holds a subject block.
/// </para>
///
/// <para>
/// <b>The programme code and section names below are arranged so the rule's anchor fires.</b> Whether
/// the registrar's real <c>PROGRAM</c> column produces a code that prefixes its own section names is an
/// open question put to them separately — <c>"BSci - Crim"</c> normalizes to <c>BSCICRIM</c> while
/// <c>"BSCRIM 2-A"</c> normalizes to <c>BSCRIM2A</c>, which do not match, so on today's real file every
/// student derives <c>null</c>. Nothing in this file asserts anything about that column: every case
/// states what the projection does with the placements and enrolments it is given, which stays true
/// however that question is answered.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class YearLevelProjectionTests : IntegrationTest
{
    public YearLevelProjectionTests(SqlServerFixture sql) : base(sql) { }

    private const string TermCode = "2025-2026-1";

    /// <summary>
    /// Six students of one programme, arranged so that every outcome D-47 can produce is present at
    /// once — a year, a shared year, a different year, and three separate roads to <c>null</c>.
    ///
    /// <list type="table">
    ///   <item><term>Reyes</term><description><c>BSCRIM 2-A</c> + <c>GE 8</c> — year 2, with a block
    ///   beside it whose digit disagrees. A digit-scanner reads this student as ambiguous.</description></item>
    ///   <item><term>Cruz</term><description><c>BSCRIM 2-A</c> — year 2, so the cohort has two
    ///   members and a group of one cannot be mistaken for a correct one.</description></item>
    ///   <item><term>Lim</term><description><c>BSCRIM 3-A</c> — year 3, so there are two year groups
    ///   to keep apart.</description></item>
    ///   <item><term>Tan</term><description><c>NSTP 2</c> + <c>GE 2</c> — <b>the tripwire.</b> Two
    ///   subject blocks that <em>agree</em> on a digit, so a digit-scanner reports a confident
    ///   "2nd year".</description></item>
    ///   <item><term>Bautista</term><description><c>ROTC</c> — no digit anywhere.</description></item>
    ///   <item><term>Uy</term><description><c>BSCRIM 2-A</c> <em>and</em> <c>BSCRIM 3-A</c> — a roster
    ///   error, not a tie to break.</description></item>
    /// </list>
    ///
    /// <para>
    /// Every placement starts with <c>YearLevel = null</c> and no <c>HomeSectionKey</c>, so nothing a
    /// test asserts can have arrived from the fixture rather than from the derivation.
    /// </para>
    /// </summary>
    private sealed record World(
        Guid SchoolId, Guid TermId, Guid ProgramId,
        Guid ReyesId, Guid CruzId, Guid LimId, Guid TanId, Guid BautistaId, Guid UyId,
        Guid Section2AOfferingId, Guid Section3AOfferingId, Guid NstpOfferingId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var term = TestData.NewTerm(school.Id, TermCode);
        db.Terms.Add(term);

        var college = TestData.NewCollege(school.Id);
        db.Colleges.Add(college);
        var program = TestData.NewProgram(school.Id, college.Id, "BSCRIM");
        db.Programs.Add(program);

        var ssci7 = TestData.NewCourse(school.Id, "SSCI 7", "Introduction to Criminology");
        var ssci8 = TestData.NewCourse(school.Id, "SSCI 8", "Criminology 2");
        var nstp1 = TestData.NewCourse(school.Id, "NSTP 1", "Civic Welfare");
        var ge2 = TestData.NewCourse(school.Id, "GE 2", "Readings in Philippine History");
        var ge8 = TestData.NewCourse(school.Id, "GE 8", "Ethics");
        var rotc1 = TestData.NewCourse(school.Id, "ROTC 1", "Reserve Officers' Training");
        db.Courses.AddRange(ssci7, ssci8, nstp1, ge2, ge8, rotc1);

        // Programme cohorts: the key begins with the programme's own CodeKey, which is the anchor.
        var section2A = TestData.NewOffering(term.Id, ssci7.Id, "BSCRIM 2-A");
        var section3A = TestData.NewOffering(term.Id, ssci8.Id, "BSCRIM 3-A");
        // Subject blocks: they do not, and they mix students from every year.
        var nstpBlock = TestData.NewOffering(term.Id, nstp1.Id, "NSTP 2");
        var geTwoBlock = TestData.NewOffering(term.Id, ge2.Id, "GE 2");
        var geEightBlock = TestData.NewOffering(term.Id, ge8.Id, "GE 8");
        var rotcBlock = TestData.NewOffering(term.Id, rotc1.Id, "ROTC");
        db.CourseOfferings.AddRange(
            section2A, section3A, nstpBlock, geTwoBlock, geEightBlock, rotcBlock);

        var reyes = TestData.NewStudent(school.Id, "2023-0001", lastName: "Reyes");
        var cruz = TestData.NewStudent(school.Id, "2023-0002", lastName: "Cruz");
        var lim = TestData.NewStudent(school.Id, "2023-0003", lastName: "Lim");
        var tan = TestData.NewStudent(school.Id, "2023-0004", lastName: "Tan");
        var bautista = TestData.NewStudent(school.Id, "2023-0005", lastName: "Bautista");
        var uy = TestData.NewStudent(school.Id, "2023-0006", lastName: "Uy");
        db.Students.AddRange(reyes, cruz, lim, tan, bautista, uy);

        foreach (var student in new[] { reyes, cruz, lim, tan, bautista, uy })
        {
            db.StudentTermRecords.Add(TestData.NewTermRecord(
                student.Id, term.Id, program.Id, college.Id, yearLevel: null, homeSection: null));
        }

        db.Enrollments.AddRange(
            TestData.NewEnrollment(reyes.Id, section2A.Id),
            TestData.NewEnrollment(reyes.Id, geEightBlock.Id),
            TestData.NewEnrollment(cruz.Id, section2A.Id),
            TestData.NewEnrollment(lim.Id, section3A.Id),
            TestData.NewEnrollment(tan.Id, nstpBlock.Id),
            TestData.NewEnrollment(tan.Id, geTwoBlock.Id),
            TestData.NewEnrollment(bautista.Id, rotcBlock.Id),
            TestData.NewEnrollment(uy.Id, section2A.Id),
            TestData.NewEnrollment(uy.Id, section3A.Id));

        await db.SaveChangesAsync();

        return new World(
            school.Id, term.Id, program.Id,
            reyes.Id, cruz.Id, lim.Id, tan.Id, bautista.Id, uy.Id,
            section2A.Id, section3A.Id, nstpBlock.Id);
    }

    private async Task<GroupProjectionResult> SyncAsync(Guid termId)
    {
        await using var db = NewDbContext();
        return await ProjectionOn(db).SyncTermAsync(termId);
    }

    private async Task<string?> ReadYearAsync(Guid studentId)
    {
        await using var db = NewDbContext();
        return (await db.StudentTermRecords.AsNoTracking()
            .SingleAsync(r => r.StudentId == studentId)).YearLevel;
    }

    /// <summary>Every year-level group in the database, members included.</summary>
    private async Task<IReadOnlyList<StudentGroup>> ReadYearGroupsAsync()
    {
        await using var db = NewDbContext();
        return await db.StudentGroups.AsNoTracking().Include(g => g.Members)
            .Where(g => g.SourceEntityType == GroupSourceEntityType.YearLevel)
            .ToListAsync();
    }

    private async Task<StudentGroup> ReadGroupAsync(string sourceEntityType, string sourceKey)
    {
        await using var db = NewDbContext();
        return await db.StudentGroups.AsNoTracking().Include(g => g.Members)
            .SingleAsync(g => g.SourceEntityType == sourceEntityType && g.SourceKey == sourceKey);
    }

    // ------------------------------------------------------------------ the tripwire (D-47)

    /// <summary>
    /// <b>The reason this phase exists.</b> Tan's only sections are <c>NSTP 2</c> and <c>GE 2</c> —
    /// two subject blocks that agree on a digit, so the naive reading does not merely guess, it
    /// returns a unanimous "2nd year". A block mixes students from every year by construction, so that
    /// answer is wrong for most of the block and there is nothing on screen to say a reading was made.
    /// </summary>
    [Fact]
    public async Task A_subject_block_containing_a_digit_never_becomes_a_year()
    {
        var world = await ArrangeAsync();

        await SyncAsync(world.TermId);

        Assert.Null(await ReadYearAsync(world.TanId));

        var years = await ReadYearGroupsAsync();
        Assert.DoesNotContain(years, g => g.Members.Any(m => m.StudentId == world.TanId));
    }

    /// <summary>
    /// <c>ROTC</c> has no digit at all, and the rule has no default year to fall back on — "nothing to
    /// read" resolves to nothing rather than to a first year.
    /// </summary>
    [Fact]
    public async Task A_section_with_no_digit_at_all_yields_no_year()
    {
        var world = await ArrangeAsync();

        await SyncAsync(world.TermId);

        Assert.Null(await ReadYearAsync(world.BautistaId));

        var years = await ReadYearGroupsAsync();
        Assert.DoesNotContain(years, g => g.Members.Any(m => m.StudentId == world.BautistaId));
    }

    /// <summary>
    /// <b><c>null</c> is a first-class outcome, and the students it applies to are not stranded.</b>
    /// Tan joins no year group — in particular no "(no year)" cohort is fabricated, which would offer
    /// the audience picker a selectable group whose members share nothing but a missing value — and
    /// stays invitable by every other axis the projection publishes.
    /// </summary>
    [Fact]
    public async Task A_student_with_no_derivable_year_joins_no_year_group_and_stays_invitable()
    {
        var world = await ArrangeAsync();

        await SyncAsync(world.TermId);

        var years = await ReadYearGroupsAsync();
        Assert.NotEmpty(years); // the run did produce year groups — Tan is excluded, not everyone
        Assert.DoesNotContain(years, g => g.Members.Any(m => m.StudentId == world.TanId));

        var programme = await ReadGroupAsync(
            GroupSourceEntityType.Program, world.ProgramId.ToString("D"));
        var section = await ReadGroupAsync(GroupSourceEntityType.Section, "NSTP2");
        var offering = await ReadGroupAsync(
            GroupSourceEntityType.CourseOffering, world.NstpOfferingId.ToString("D"));

        Assert.Contains(programme.Members, m => m.StudentId == world.TanId);
        Assert.Contains(section.Members, m => m.StudentId == world.TanId);
        Assert.Contains(offering.Members, m => m.StudentId == world.TanId);
    }

    /// <summary>
    /// Uy is in <c>BSCRIM 2-A</c> and <c>BSCRIM 3-A</c>. That is a roster error rather than a tie, and
    /// a deterministic pick would be wrong half the time with nothing to say a choice was made — so
    /// the gap is the honest answer. Uy still belongs to both <em>section</em> groups, which is the
    /// part that stays true and invitable.
    /// </summary>
    [Fact]
    public async Task Conflicting_home_sections_yield_no_year_rather_than_an_arbitrary_pick()
    {
        var world = await ArrangeAsync();

        await SyncAsync(world.TermId);

        Assert.Null(await ReadYearAsync(world.UyId));

        var years = await ReadYearGroupsAsync();
        Assert.DoesNotContain(years, g => g.Members.Any(m => m.StudentId == world.UyId));

        var second = await ReadGroupAsync(GroupSourceEntityType.Section, "BSCRIM2A");
        var third = await ReadGroupAsync(GroupSourceEntityType.Section, "BSCRIM3A");
        Assert.Contains(second.Members, m => m.StudentId == world.UyId);
        Assert.Contains(third.Members, m => m.StudentId == world.UyId);
    }

    // ------------------------------------------------------------------ D-48: where it is written

    /// <summary>
    /// <b>A derived year is written to <c>StudentTermRecords</c> and to nothing else.</b>
    /// <c>Students.YearLevel</c> is the ADR-001 D-2 display cache — single-valued, wrong for 23% of
    /// students on the section column, and kept read-only precisely so a second disagreeing answer
    /// cannot appear. Deriving a value does not promote it to a join key.
    ///
    /// <para>
    /// The guard is asserted alive afterwards on the same student, so this cannot pass by the guard
    /// having been quietly relaxed to let the projection through.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Derivation_writes_the_term_record_and_never_the_student_display_cache()
    {
        var world = await ArrangeAsync();

        await SyncAsync(world.TermId);

        Assert.Equal("2", await ReadYearAsync(world.ReyesId));

        await using var read = NewDbContext();
        var reyes = await read.Students.AsNoTracking().SingleAsync(s => s.Id == world.ReyesId);

        // Still the fixture's display value, untouched — and no refresh was recorded, because the
        // projection never opened the refresh scope that would have permitted a write.
        Assert.Equal("3rd Year", reyes.YearLevel);
        Assert.Null(reyes.AcademicCacheUpdatedAt);

        await using var write = NewDbContext();
        var tracked = await write.Students.SingleAsync(s => s.Id == world.ReyesId);
        tracked.YearLevel = "2nd Year";

        var refused = await Assert.ThrowsAsync<AcademicCacheWriteException>(
            () => write.SaveChangesAsync());
        Assert.Equal(nameof(Student.YearLevel), refused.PropertyName);
    }

    // ------------------------------------------------------------------ D-49: the year group

    /// <summary>
    /// A derived year becomes an audience shaped exactly like a section's: no source row to point at,
    /// identified by its <c>SourceKey</c> alone, and named with the term riding in it.
    ///
    /// <para>
    /// <b>The <c>SourceKey</c> is the bare digit and the ordinal suffix lives only in the name.</b>
    /// <c>SourceKey</c> is what the next run matches this group on, so a key carrying "2nd Year" would
    /// bake a rendering decision into an identity — and the day the label changes, the reconcile would
    /// create a second group and silently strand the first.
    /// </para>
    ///
    /// <para>
    /// The term is in the name for a sharper reason here than anywhere else: "2nd Year" names a
    /// completely different set of students every September, so a stale <c>EventGroups</c> row would
    /// otherwise read as this year's cohort.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_derived_year_becomes_an_invitable_group_with_no_source_row_to_point_at()
    {
        var world = await ArrangeAsync();

        await SyncAsync(world.TermId);

        var second = await ReadGroupAsync(GroupSourceEntityType.YearLevel, "2");

        Assert.Equal(StudentGroupType.YearLevel, second.Type);
        Assert.Equal(GroupSourceType.Derived, second.SourceType);
        Assert.Null(second.SourceEntityId);
        Assert.Equal($"2nd Year ({TermCode})", second.Name);
        Assert.Equal(world.TermId, second.TermId);
        Assert.Equal(world.SchoolId, second.SchoolId);
        Assert.NotNull(second.LastSyncedAt);

        var third = await ReadGroupAsync(GroupSourceEntityType.YearLevel, "3");
        Assert.Equal($"3rd Year ({TermCode})", third.Name);
        Assert.Null(third.SourceEntityId);
    }

    /// <summary>
    /// Membership is the students the rule actually resolved, and only them. Reyes and Cruz are 2nd
    /// years; Lim is a 3rd year; Tan, Bautista and Uy are in neither, which is the same statement as
    /// "three of the six have no year" made from the audience side rather than the column side.
    /// </summary>
    [Fact]
    public async Task A_year_group_holds_exactly_the_students_that_derived_that_year()
    {
        var world = await ArrangeAsync();

        await SyncAsync(world.TermId);

        var years = await ReadYearGroupsAsync();
        Assert.Equal(2, years.Count);

        var second = years.Single(g => g.SourceKey == "2");
        var third = years.Single(g => g.SourceKey == "3");

        Assert.Equal(
            new[] { world.CruzId, world.ReyesId }.Order(),
            second.Members.Select(m => m.StudentId).Order());
        Assert.Equal(world.LimId, third.Members.Single().StudentId);
    }

    // ------------------------------------------------------------------ re-running the projection

    /// <summary>
    /// An import batch is re-runnable by design (ADR-001 D-5) and the projection runs at the end of
    /// every one, so this runs repeatedly over unchanged data. Deriving is part of that: a year
    /// recomputed to the same value must not churn the group, or every re-import would report work it
    /// did not do and <c>IsNoOp</c> would stop meaning anything.
    /// </summary>
    [Fact]
    public async Task Re_deriving_over_unchanged_data_is_a_no_op()
    {
        var world = await ArrangeAsync();

        Assert.False((await SyncAsync(world.TermId)).IsNoOp);

        var before = await ReadYearGroupsAsync();

        Assert.True((await SyncAsync(world.TermId)).IsNoOp);

        var after = await ReadYearGroupsAsync();
        Assert.Equal(
            before.Select(g => g.Id).Order(),
            after.Select(g => g.Id).Order());
        Assert.Equal(
            before.Sum(g => g.Members.Count),
            after.Sum(g => g.Members.Count));
        Assert.Equal("2", await ReadYearAsync(world.ReyesId));
    }

    /// <summary>
    /// <b>Why derivation lives in the projection rather than in the importer.</b> A corrected roster
    /// moves a student between year groups on the next run with no separate step — and, in the half
    /// that is easy to leave out, <em>clears</em> a year that no longer follows from the file.
    ///
    /// <para>
    /// Cruz moves from <c>BSCRIM 2-A</c> to <c>BSCRIM 3-A</c>. Reyes loses their home section
    /// altogether and is left with <c>GE 8</c> alone. A derivation that only ever wrote non-null would
    /// pass the first half and leave Reyes stranded in last week's cohort, which is precisely the
    /// stale-answer failure the whole rule is built to avoid.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_corrected_roster_moves_a_student_between_year_groups_and_clears_a_stale_one()
    {
        var world = await ArrangeAsync();
        await SyncAsync(world.TermId);

        Assert.Equal("2", await ReadYearAsync(world.CruzId));
        Assert.Equal("2", await ReadYearAsync(world.ReyesId));

        await using (var db = NewDbContext())
        {
            var cruz = await db.Enrollments.SingleAsync(
                e => e.StudentId == world.CruzId && e.CourseOfferingId == world.Section2AOfferingId);
            db.Enrollments.Remove(cruz);
            db.Enrollments.Add(TestData.NewEnrollment(world.CruzId, world.Section3AOfferingId));

            var reyes = await db.Enrollments.SingleAsync(
                e => e.StudentId == world.ReyesId && e.CourseOfferingId == world.Section2AOfferingId);
            db.Enrollments.Remove(reyes);

            await db.SaveChangesAsync();
        }

        await SyncAsync(world.TermId);

        Assert.Equal("3", await ReadYearAsync(world.CruzId));
        Assert.Null(await ReadYearAsync(world.ReyesId));

        var second = await ReadGroupAsync(GroupSourceEntityType.YearLevel, "2");
        var third = await ReadGroupAsync(GroupSourceEntityType.YearLevel, "3");

        // The 2nd-year cohort is now empty and its row survives — the same treatment a section group
        // whose source disappeared gets, and for the same reason: a historical EventGroups link.
        Assert.Empty(second.Members);
        Assert.Equal(
            new[] { world.CruzId, world.LimId }.Order(),
            third.Members.Select(m => m.StudentId).Order());
    }
}
