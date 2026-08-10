using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>POST /api/v1/events/audience/resolve</c> — D-50/D-51's filter builder, asserted on what it
/// actually answers.
///
/// <para>
/// <b>Over HTTP, and that is not ceremony.</b> The subject of this file is a query composed from a
/// request body: which arm each field name selects, what the composition does to the grain, and what
/// two ceilings do to the response. Every one of those is a property of the SQL that reaches SQL
/// Server — the dedup is <em>structural</em> (a root with one row per student, and <c>EXISTS</c>
/// predicates that cannot multiply it) rather than a <c>Distinct</c>, and a provider that resolves
/// <c>Any()</c> in memory would satisfy every assertion below while shipping a query that does not.
/// Moving this suite to EF InMemory is a project hard rule against for exactly that reason.
/// </para>
///
/// <para>
/// <b>The fixture poisons <c>Students.Section</c> on purpose.</b> The nursing students carry
/// <c>"BSCRIM 2-A"</c> in that column and the real second-years carry <c>"BSN 1-B"</c>, so an
/// implementation that read the ADR-001 D-2 display cache instead of
/// <c>Enrollments → CourseOfferings.SectionKey</c> would return a plausible, non-empty, <em>disjoint</em>
/// set for a section filter. A fixture whose cache column happened to agree with its enrolments would
/// pass against both implementations, which is the failure D-2 is about.
/// </para>
///
/// <para>
/// <b>Two behaviours are deliberately not asserted here, either way.</b> A filter row with a recognised
/// field and no values is currently skipped, and two rows naming the same field currently intersect.
/// Both are open decisions. No test in this file sends a row with empty values. The row-ceiling tests
/// do send repeated rows naming one field — twenty distinct rows cannot be built out of five fields —
/// but the rows they repeat are <em>identical</em>, which resolves to the same students whether such
/// rows intersect or union, so nothing here pins either outcome. See <c>RepeatedRows</c>.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventAudienceResolveTests : IntegrationTest
{
    public EventAudienceResolveTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/events/audience/resolve";

    // The section display names, and the keys AcademicKey derives from them. Both spellings are sent
    // by the tests below, because "values are normalized before they are compared, never after" is a
    // rule with a silent failure mode: comparing a raw 'BSCRIM 2-A' against a stored 'BSCRIM2A'
    // matches nothing while looking entirely correct.
    private const string FirstYearSection = "BSCRIM 1-A";
    private const string SecondYearSection = "BSCRIM 2-A";
    private const string RotcSection = "ROTC";
    private const string NursingSection = "BSN 1-B";

    /// <summary>
    /// The current term's roster, and every student in it is here to break one specific wrong
    /// implementation.
    ///
    /// <list type="table">
    ///   <item><term>Abad, Bautista, Cruz</term><description>1st-year criminology — <c>BSCRIM 1-A</c>.</description></item>
    ///   <item><term>Dizon, Espino, Flores</term><description>2nd-year criminology — <c>BSCRIM 2-A</c>.</description></item>
    ///   <item><term>Garcia</term><description>2nd year <b>and</b> <c>ROTC</c>. The ADR-001 D-2 double-count: two matching sections, one student.</description></item>
    ///   <item><term>Hilario, Ignacio</term><description>Nursing, and their <c>Students.Section</c> cache lies that they are in <c>BSCRIM 2-A</c>.</description></item>
    ///   <item><term>Jimenez</term><description>2nd year and soft-deleted. Matches nothing, anywhere.</description></item>
    ///   <item><term>Karganilla</term><description>Placed in <em>this</em> term, but enrolled only in <em>last</em> term's <c>BSCRIM 2-A</c> / <c>SSCI 7</c> offering. Both <c>EXISTS</c> arms must exclude them.</description></item>
    ///   <item><term>Lazaro</term><description>Last term's cohort entirely. The root's <c>TermId</c> must exclude them.</description></item>
    /// </list>
    /// </summary>
    private sealed record World(
        Guid SchoolId,
        Guid CurrentTermId, Guid PriorTermId,
        Guid CriminologyCollegeId, Guid NursingCollegeId,
        Guid CriminologyProgramId, Guid NursingProgramId,
        Guid FirstYearCourseId, Guid SecondYearCourseId, Guid RotcCourseId, Guid NursingCourseId,
        Guid Abad, Guid Bautista, Guid Cruz,
        Guid Dizon, Guid Espino, Guid Flores, Guid Garcia,
        Guid Hilario, Guid Ignacio,
        Guid Jimenez, Guid Karganilla, Guid Lazaro)
    {
        public Guid[] FirstYears => [Abad, Bautista, Cruz];
        public Guid[] SecondYears => [Dizon, Espino, Flores, Garcia];
        public Guid[] Nurses => [Hilario, Ignacio];

        /// <summary>Every student the current term can legitimately resolve to.</summary>
        public Guid[] WholeCurrentTerm =>
            [Abad, Bautista, Cruz, Dizon, Espino, Flores, Garcia, Hilario, Ignacio, Karganilla];
    }

    /// <summary>
    /// <paramref name="staleSection"/> is written straight onto <c>Students.Section</c>, which the
    /// <c>SaveChanges</c> guard permits on insert (it blocks <em>updates</em> — the divergence, not the
    /// initial value). Every student below is given a value that is wrong on purpose; see the class
    /// summary.
    /// </summary>
    private static Student NewStudent(
        Guid schoolId, string studentNumber, string lastName, string staleSection)
    {
        var student = TestData.NewStudent(schoolId, studentNumber, lastName: lastName);
        student.Section = staleSection;
        return student;
    }

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var current = TestData.NewTerm(school.Id, "2025-2026-1", isCurrent: true);
        var prior = TestData.NewTerm(school.Id, "2024-2025-2", isCurrent: false);
        db.Terms.AddRange(current, prior);

        var ccj = TestData.NewCollege(school.Id, "College of Criminal Justice", "CCJ");
        var con = TestData.NewCollege(school.Id, "College of Nursing", "CON");
        db.Colleges.AddRange(ccj, con);

        var crim = TestData.NewProgram(school.Id, ccj.Id, "BSCRIM");
        var nurs = TestData.NewProgram(school.Id, con.Id, "BSN");
        db.Programs.AddRange(crim, nurs);

        var crim1 = TestData.NewCourse(school.Id, "CRIM 1", "Introduction to Criminology");
        var ssci7 = TestData.NewCourse(school.Id, "SSCI 7", "Life and Works of Rizal");
        var ms32 = TestData.NewCourse(school.Id, "MS 32", "Military Science 32");
        var nstp2 = TestData.NewCourse(school.Id, "NSTP 2", "National Service Training Program 2");
        db.Courses.AddRange(crim1, ssci7, ms32, nstp2);

        var firstYear = TestData.NewOffering(current.Id, crim1.Id, FirstYearSection);
        var secondYear = TestData.NewOffering(current.Id, ssci7.Id, SecondYearSection);
        var rotc = TestData.NewOffering(current.Id, ms32.Id, RotcSection);
        var nursing = TestData.NewOffering(current.Id, nstp2.Id, NursingSection);

        // Last term's second-year block: the SAME section key and the SAME course as this term's, in a
        // different term. Without `CourseOffering.TermId == termId` inside both EXISTS predicates, an
        // enrolment here would satisfy a filter about this term.
        var priorSecondYear = TestData.NewOffering(prior.Id, ssci7.Id, SecondYearSection);

        db.CourseOfferings.AddRange(firstYear, secondYear, rotc, nursing, priorSecondYear);

        // Student numbers are assigned in surname order, so the response's documented ordering
        // (StudentNumber, then Id) is one a test can write down.
        var abad = NewStudent(school.Id, "2025-0001", "Abad", NursingSection);
        var bautista = NewStudent(school.Id, "2025-0002", "Bautista", NursingSection);
        var cruz = NewStudent(school.Id, "2025-0003", "Cruz", NursingSection);
        var dizon = NewStudent(school.Id, "2025-0004", "Dizon", NursingSection);
        var espino = NewStudent(school.Id, "2025-0005", "Espino", NursingSection);
        var flores = NewStudent(school.Id, "2025-0006", "Flores", NursingSection);
        var garcia = NewStudent(school.Id, "2025-0007", "Garcia", NursingSection);

        // The cache column names the section these two are provably not in.
        var hilario = NewStudent(school.Id, "2025-0008", "Hilario", SecondYearSection);
        var ignacio = NewStudent(school.Id, "2025-0009", "Ignacio", SecondYearSection);

        var jimenez = NewStudent(school.Id, "2025-0010", "Jimenez", NursingSection);
        jimenez.IsDeleted = true;

        var karganilla = NewStudent(school.Id, "2025-0011", "Karganilla", NursingSection);
        var lazaro = NewStudent(school.Id, "2024-0001", "Lazaro", NursingSection);

        db.Students.AddRange(
            abad, bautista, cruz, dizon, espino, flores, garcia,
            hilario, ignacio, jimenez, karganilla, lazaro);

        db.Enrollments.AddRange(
            TestData.NewEnrollment(abad.Id, firstYear.Id),
            TestData.NewEnrollment(bautista.Id, firstYear.Id),
            TestData.NewEnrollment(cruz.Id, firstYear.Id),
            TestData.NewEnrollment(dizon.Id, secondYear.Id),
            TestData.NewEnrollment(espino.Id, secondYear.Id),
            TestData.NewEnrollment(flores.Id, secondYear.Id),
            // The double-count: Garcia is in two of the three sections a "1st years, 2nd years and
            // ROTC" filter names.
            TestData.NewEnrollment(garcia.Id, secondYear.Id),
            TestData.NewEnrollment(garcia.Id, rotc.Id),
            TestData.NewEnrollment(hilario.Id, nursing.Id),
            TestData.NewEnrollment(ignacio.Id, nursing.Id),
            TestData.NewEnrollment(jimenez.Id, secondYear.Id),
            // Last term's offering, for a student placed in this one.
            TestData.NewEnrollment(karganilla.Id, priorSecondYear.Id),
            TestData.NewEnrollment(lazaro.Id, priorSecondYear.Id));

        // Year levels are the bare digits YearLevels.Derive produces (D-47) — never a display label.
        db.StudentTermRecords.AddRange(
            TestData.NewTermRecord(abad.Id, current.Id, crim.Id, ccj.Id, "1", FirstYearSection),
            TestData.NewTermRecord(bautista.Id, current.Id, crim.Id, ccj.Id, "1", FirstYearSection),
            TestData.NewTermRecord(cruz.Id, current.Id, crim.Id, ccj.Id, "1", FirstYearSection),
            TestData.NewTermRecord(dizon.Id, current.Id, crim.Id, ccj.Id, "2", SecondYearSection),
            TestData.NewTermRecord(espino.Id, current.Id, crim.Id, ccj.Id, "2", SecondYearSection),
            TestData.NewTermRecord(flores.Id, current.Id, crim.Id, ccj.Id, "2", SecondYearSection),
            TestData.NewTermRecord(garcia.Id, current.Id, crim.Id, ccj.Id, "2", SecondYearSection),
            TestData.NewTermRecord(hilario.Id, current.Id, nurs.Id, con.Id, "1", NursingSection),
            TestData.NewTermRecord(ignacio.Id, current.Id, nurs.Id, con.Id, "1", NursingSection),
            TestData.NewTermRecord(jimenez.Id, current.Id, crim.Id, ccj.Id, "2", SecondYearSection),
            // Placed this term, and with no year derived — the outcome D-47 designs for, and on today's
            // real export the outcome for every student.
            TestData.NewTermRecord(karganilla.Id, current.Id, crim.Id, ccj.Id, null, null),
            TestData.NewTermRecord(lazaro.Id, prior.Id, crim.Id, ccj.Id, "2", SecondYearSection));

        await db.SaveChangesAsync();

        return new World(
            school.Id, current.Id, prior.Id,
            ccj.Id, con.Id, crim.Id, nurs.Id,
            crim1.Id, ssci7.Id, ms32.Id, nstp2.Id,
            abad.Id, bautista.Id, cruz.Id,
            dizon.Id, espino.Id, flores.Id, garcia.Id,
            hilario.Id, ignacio.Id,
            jimenez.Id, karganilla.Id, lazaro.Id);
    }

    // ------------------------------------------------------------------------------ request helpers

    private static object Filter(string field, params string[] values) => new { field, values };

    private static object Body(Guid? termId, params object[] filters) => new { termId, filters };

    /// <summary>
    /// Posts and asserts a 200, returning the parsed resolution. Deserialized into the published DTO
    /// rather than read out of a <c>JsonDocument</c>, because the property names are already pinned by
    /// <see cref="The_resolution_is_served_as_camel_cased_json"/> and repeating them in twenty
    /// behavioural tests would make every one of them fail for two unrelated reasons.
    /// </summary>
    private static async Task<AudienceResolutionDto> ResolveAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var resolution = await response.Content.ReadFromJsonAsync<AudienceResolutionDto>();
        Assert.NotNull(resolution);
        return resolution;
    }

    /// <summary>
    /// Posts and asserts a 400 carrying the RFC 7807 <c>code</c> token for <paramref name="expected"/>.
    ///
    /// <para>
    /// The <c>code</c> is compared against <c>outcome.ToString()</c> rather than a string literal, and
    /// that is the assertion rather than a shortcut: the token is what a filter builder branches on, so
    /// it is derived from the enum member name and renaming the member is a wire-breaking change. A
    /// literal here would let a rename ship with the tests rewritten to match it.
    /// </para>
    /// </summary>
    private static async Task<JsonElement> RefusedAsync(
        HttpClient client, object body, AudienceResolveOutcome expected)
    {
        var response = await client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(expected.ToString(), root.GetProperty("code").GetString());
        Assert.Equal(400, root.GetProperty("status").GetInt32());

        // Prose for a person alongside the token for a machine — both, or the 400 is unactionable.
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()));

        return root.Clone();
    }

    private static void AssertResolvesTo(
        AudienceResolutionDto resolution, params Guid[] expected)
    {
        Assert.Equal(expected.Length, resolution.Count);
        Assert.Equal(
            expected.OrderBy(id => id).ToArray(),
            resolution.StudentIds.OrderBy(id => id).ToArray());
    }

    // ============================================================= 1. the D-2 structural de-duplication

    /// <summary>
    /// <b>The whole reason this endpoint exists rather than a query over <c>Students.Section</c>.</b>
    /// Three sections are named; their memberships sum to eight; the answer is seven, because Garcia is
    /// in two of them.
    ///
    /// <para>
    /// This is ADR-001 D-2 at the scale the real roster has it — <c>BSCRIM1A</c> (23) plus
    /// <c>BSCRIM2A</c> (30) plus <c>ROTC</c> (1) is 53 students and not 54, and twelve of fifty-two real
    /// students sit in more than one section. The sum is asserted alongside the count, so the failure
    /// message says which of the two the implementation produced instead of just "expected 7".
    /// </para>
    ///
    /// <para>
    /// It holds <em>structurally</em>: the root is <c>StudentTermRecords</c>, which is
    /// <c>UNIQUE(StudentId, TermId)</c>, and the section arm is an <c>EXISTS</c> that cannot multiply a
    /// row. Rewriting either as a join and papering over it with <c>Distinct()</c> would pass this test
    /// and break on the first projection somebody adds, so the per-section counts are asserted too —
    /// they are what makes 8 the number a duplicating implementation would report.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Three_sections_that_overlap_resolve_to_the_distinct_students_and_not_their_sum()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var firstYears = await ResolveAsync(client, Body(null, Filter("Section", FirstYearSection)));
        var secondYears = await ResolveAsync(client, Body(null, Filter("Section", SecondYearSection)));
        var rotc = await ResolveAsync(client, Body(null, Filter("Section", RotcSection)));

        Assert.Equal(3, firstYears.Count);
        Assert.Equal(4, secondYears.Count);
        Assert.Equal(1, rotc.Count);

        // The sum a per-section tally produces, spelled out so the assertion below has something to be
        // distinct *from*.
        Assert.Equal(8, firstYears.Count + secondYears.Count + rotc.Count);

        var combined = await ResolveAsync(client, Body(
            null, Filter("Section", FirstYearSection, SecondYearSection, RotcSection)));

        Assert.Equal(7, combined.Count);
        AssertResolvesTo(combined, [.. world.FirstYears, .. world.SecondYears]);

        // Garcia is present exactly once — the id list is where a duplicating join would show itself
        // even if a Distinct() had been bolted onto the count.
        Assert.Single(combined.StudentIds, id => id == world.Garcia);
        Assert.Equal(combined.StudentIds.Count, combined.StudentIds.Distinct().Count());
    }

    // ================================================================== 2. the five field arms

    [Fact]
    public async Task The_college_arm_resolves_through_the_term_record()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var criminology = await ResolveAsync(
            client, Body(null, Filter("College", world.CriminologyCollegeId.ToString())));
        AssertResolvesTo(criminology,
            [.. world.FirstYears, .. world.SecondYears, world.Karganilla]);

        var nursing = await ResolveAsync(
            client, Body(null, Filter("College", world.NursingCollegeId.ToString())));
        AssertResolvesTo(nursing, world.Nurses);

        // Within a row, values union (D-51).
        var both = await ResolveAsync(client, Body(null, Filter(
            "College", world.CriminologyCollegeId.ToString(), world.NursingCollegeId.ToString())));
        AssertResolvesTo(both, world.WholeCurrentTerm);
    }

    [Fact]
    public async Task The_program_arm_resolves_through_the_term_record()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var criminology = await ResolveAsync(
            client, Body(null, Filter("Program", world.CriminologyProgramId.ToString())));
        AssertResolvesTo(criminology,
            [.. world.FirstYears, .. world.SecondYears, world.Karganilla]);

        var nursing = await ResolveAsync(
            client, Body(null, Filter("Program", world.NursingProgramId.ToString())));
        AssertResolvesTo(nursing, world.Nurses);
    }

    /// <summary>
    /// The year arm reads the <em>derived</em> <c>StudentTermRecords.YearLevel</c> (D-47), whose values
    /// are bare digits.
    ///
    /// <para>
    /// <b>Karganilla is the assertion that carries this test.</b> Their derived year is <c>null</c> —
    /// which on today's real export is every student's, because the programme reads <c>"BSci - Crim"</c>
    /// while the sections read <c>"BSCRIM 2-A"</c> and the home-section anchor never fires. A null year
    /// must match <em>no</em> year filter and must stay reachable by every other field, which the
    /// college and programme tests above already show it is.
    /// </para>
    ///
    /// <para>
    /// <b>Two wrong implementations fail here, and they are named because each was checked to actually
    /// fail.</b> One that treated a null year as "matches anything" puts Karganilla into whichever year
    /// group is asked for: the year-1 set would be six rather than five, <c>either</c> ten rather than
    /// nine, and the year-4 filter would match one student instead of nobody — three assertions, all
    /// red. One that read <c>Students.YearLevel</c>, the ADR-001 D-2 display cache, returns
    /// <em>nothing</em> for both <c>"1"</c> and <c>"2"</c>: the fixture's students all carry
    /// <c>"3rd Year"</c> in that column (it is <c>TestData.NewStudent</c>'s default and no test overrides
    /// it), and a display label is not a value <c>YearLevels.Derive</c> can write. That is the same trap
    /// the section arm is built around, one column over.
    /// </para>
    ///
    /// <para>
    /// <b>What is <em>not</em> pinned here: the <c>r.YearLevel != null</c> conjunct in the predicate.</b>
    /// It is required by C# — <c>keys</c> is a <c>List&lt;string&gt;</c> and the column is
    /// <c>string?</c> — and it is a no-op in SQL. EF 9.0.1 emits
    /// <c>[s].[YearLevel] IS NOT NULL AND [s].[YearLevel] IN (…)</c> with it and the second conjunct
    /// alone without it, and <c>NULL IN (…)</c> is <c>UNKNOWN</c>, so the row is filtered out either way:
    /// deleting the guard leaves every assertion below green. Naming it as the thing this test protects
    /// would advertise a revert-and-watch-it-fail target that does not fail, which is precisely the
    /// false-green this project has a standing rule against. The null behaviour is guaranteed by
    /// three-valued logic; what the assertions guard is the two implementations above.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_year_level_arm_reads_the_derived_digit_and_a_null_year_matches_no_filter()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var first = await ResolveAsync(client, Body(null, Filter("YearLevel", "1")));
        AssertResolvesTo(first, [.. world.FirstYears, .. world.Nurses]);

        var second = await ResolveAsync(client, Body(null, Filter("YearLevel", "2")));
        AssertResolvesTo(second, world.SecondYears);

        var either = await ResolveAsync(client, Body(null, Filter("YearLevel", "1", "2")));
        Assert.Equal(9, either.Count);
        Assert.DoesNotContain(world.Karganilla, either.StudentIds);

        // A well-formed year nobody has been derived into is an empty answer, not an error — the line
        // between "a value the field cannot hold" and "a value it can hold but nobody has".
        var fourth = await ResolveAsync(client, Body(null, Filter("YearLevel", "4")));
        Assert.Equal(0, fourth.Count);
        Assert.Empty(fourth.StudentIds);

        // The display spelling is well-formed and simply matches nobody — YearLevels.Derive cannot
        // produce it, so no student carries it. Asserted so that "2nd Year" is proven inert rather than
        // assumed to be.
        var displayLabel = await ResolveAsync(client, Body(null, Filter("YearLevel", "2nd Year")));
        Assert.Equal(0, displayLabel.Count);
    }

    /// <summary>
    /// <b>The section arm resolves through <c>Enrollments → CourseOfferings.SectionKey</c>, and this
    /// fixture makes the wrong answer visible.</b>
    ///
    /// <para>
    /// Hilario and Ignacio carry <c>"BSCRIM 2-A"</c> in <c>Students.Section</c> and are not enrolled in
    /// it; Dizon, Espino, Flores and Garcia are enrolled in it and carry <c>"BSN 1-B"</c> in that
    /// column. The two candidate answers are therefore <em>disjoint</em>, so an implementation reading
    /// the ADR-001 D-2 display cache fails on membership rather than on a count that might coincide.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_section_arm_resolves_through_enrollments_and_not_the_student_cache_column()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var secondYears = await ResolveAsync(
            client, Body(null, Filter("Section", SecondYearSection)));

        AssertResolvesTo(secondYears, world.SecondYears);

        // The cache column's answer, named so the failure is legible: these two would be the result of
        // reading Students.Section, and neither may appear.
        Assert.DoesNotContain(world.Hilario, secondYears.StudentIds);
        Assert.DoesNotContain(world.Ignacio, secondYears.StudentIds);

        var nursing = await ResolveAsync(client, Body(null, Filter("Section", NursingSection)));
        AssertResolvesTo(nursing, world.Nurses);
    }

    /// <summary>
    /// Section values are normalized before they are compared, never after. The three spellings below
    /// are one section: the stored <c>SectionKey</c> is what <c>AcademicKey</c> produced at import, so
    /// an implementation comparing the raw display string against it would match nothing while looking
    /// entirely correct.
    /// </summary>
    [Theory]
    [InlineData("BSCRIM 2-A")]
    [InlineData("BSCRIM2A")]
    [InlineData("bscrim-2a")]
    public async Task Any_spelling_of_a_section_resolves_to_the_same_students(string spelling)
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        AssertResolvesTo(
            await ResolveAsync(client, Body(null, Filter("Section", spelling))),
            world.SecondYears);
    }

    [Fact]
    public async Task The_course_arm_resolves_through_enrollments()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var rizal = await ResolveAsync(
            client, Body(null, Filter("Course", world.SecondYearCourseId.ToString())));
        AssertResolvesTo(rizal, world.SecondYears);

        var militaryScience = await ResolveAsync(
            client, Body(null, Filter("Course", world.RotcCourseId.ToString())));
        AssertResolvesTo(militaryScience, world.Garcia);

        // A well-formed id naming nothing is an empty answer, not a refusal.
        var unknown = await ResolveAsync(
            client, Body(null, Filter("Course", Guid.NewGuid().ToString())));
        Assert.Equal(0, unknown.Count);
        Assert.Empty(unknown.StudentIds);
        Assert.Empty(unknown.Sample);
        Assert.False(unknown.StudentIdsTruncated);
    }

    /// <summary>
    /// Rows intersect across <em>different</em> fields (D-51) — <c>Program is BSCRIM</c> plus
    /// <c>Year is 1</c> is the first-year criminology cohort, not their union.
    ///
    /// <para>
    /// Deliberately two rows naming two <em>different</em> fields. Whether two rows naming the
    /// <em>same</em> field intersect is an open decision and is not asserted anywhere in this file.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rows_naming_different_fields_intersect()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var firstYearCriminology = await ResolveAsync(client, Body(
            null,
            Filter("Program", world.CriminologyProgramId.ToString()),
            Filter("YearLevel", "1")));

        AssertResolvesTo(firstYearCriminology, world.FirstYears);

        // Neither row alone gives this answer, which is what makes the intersection the thing proven.
        Assert.Equal(8, (await ResolveAsync(
            client, Body(null, Filter("Program", world.CriminologyProgramId.ToString())))).Count);
        Assert.Equal(5, (await ResolveAsync(
            client, Body(null, Filter("YearLevel", "1")))).Count);

        // An intersection that matches nobody is an empty 200: nursing students are all 1st year.
        var impossible = await ResolveAsync(client, Body(
            null,
            Filter("Program", world.NursingProgramId.ToString()),
            Filter("YearLevel", "2")));
        Assert.Equal(0, impossible.Count);
    }

    /// <summary>No filter rows at all resolves the whole term — the operator's "or all".</summary>
    [Fact]
    public async Task An_empty_filter_set_resolves_the_whole_term()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        AssertResolvesTo(
            await ResolveAsync(client, new { termId = (Guid?)null, filters = Array.Empty<object>() }),
            world.WholeCurrentTerm);

        // And an absent `filters` member is the same request, not a malformed one.
        AssertResolvesTo(
            await ResolveAsync(client, new { termId = (Guid?)null }),
            world.WholeCurrentTerm);
    }

    // ============================================== 5 & 6. which term, and what "no term" answers

    /// <summary>
    /// The term default and its echo, which is what makes the default safe. A caller that omits
    /// <c>termId</c> gets the current term and is told <em>which</em> one it got, rather than trusting
    /// the server picked the semester it had in mind — section names repeat every semester against an
    /// entirely different cohort.
    /// </summary>
    [Fact]
    public async Task The_term_defaults_to_the_current_one_and_the_response_says_which()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var defaulted = await ResolveAsync(client, Body(null));
        Assert.Equal(world.CurrentTermId, defaulted.TermId);
        AssertResolvesTo(defaulted, world.WholeCurrentTerm);

        // Naming the current term explicitly is the same request.
        var explicitCurrent = await ResolveAsync(client, Body(world.CurrentTermId));
        Assert.Equal(world.CurrentTermId, explicitCurrent.TermId);
        AssertResolvesTo(explicitCurrent, world.WholeCurrentTerm);

        // And a different term is a different cohort under the same section names — the whole reason
        // the default is the current term rather than "every term".
        var priorTerm = await ResolveAsync(client, Body(world.PriorTermId));
        Assert.Equal(world.PriorTermId, priorTerm.TermId);
        AssertResolvesTo(priorTerm, world.Lazaro);

        var priorSection = await ResolveAsync(
            client, Body(world.PriorTermId, Filter("Section", SecondYearSection)));
        AssertResolvesTo(priorSection, world.Lazaro);
    }

    /// <summary>
    /// <b>Zero current terms is a real state and answers 200, not an error and not "every term".</b>
    ///
    /// <para>
    /// The <c>IsCurrent</c> flag is capped at one per school by a filtered unique index, not pinned at
    /// one, so between semesters nobody has moved it. Widening to every term there would restore the
    /// unbounded read the default exists to close, on the one day nobody is watching for it — so the
    /// answer is empty, and <c>termId: null</c> is what lets a caller tell "this filter matches nobody"
    /// from "no term is current".
    /// </para>
    ///
    /// <para>
    /// The rows are all still present — asserted through the explicit-term request at the end, which
    /// resolves ten students against the very term whose flag was cleared. Without that, an empty
    /// answer here would be equally consistent with an arrange that wrote nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_current_term_and_no_named_term_is_an_empty_two_hundred_rather_than_an_error()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var term = await db.Terms.FindAsync(world.CurrentTermId);
            term!.IsCurrent = false;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var resolution = await ResolveAsync(client, Body(null, Filter("YearLevel", "1")));

        Assert.Null(resolution.TermId);
        Assert.Equal(0, resolution.Count);
        Assert.Empty(resolution.StudentIds);
        Assert.Empty(resolution.Sample);
        Assert.False(resolution.StudentIdsTruncated);

        // The ceiling is still published, so a client can size itself against it before it has ever
        // seen a non-empty answer.
        Assert.Equal(AudienceResolutionLimits.MaxStudentIds, resolution.StudentIdLimit);

        // The students are all still there — this was the term default finding nothing, not an empty
        // database.
        var named = await ResolveAsync(client, Body(world.CurrentTermId));
        Assert.Equal(world.CurrentTermId, named.TermId);
        Assert.Equal(10, named.Count);
    }

    // ================================================ 7. soft deletes and cross-term enrolments

    /// <summary>
    /// A student the roster says does not exist cannot be invited to anything. Jimenez is a 2nd-year
    /// criminology student enrolled in <c>BSCRIM 2-A</c>, so they are one row away from matching every
    /// filter this test sends — which is what makes the exclusion falsifiable rather than incidental.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_student_is_excluded_from_every_arm()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        object[] rows =
        [
            Filter("College", world.CriminologyCollegeId.ToString()),
            Filter("Program", world.CriminologyProgramId.ToString()),
            Filter("YearLevel", "2"),
            Filter("Section", SecondYearSection),
            Filter("Course", world.SecondYearCourseId.ToString()),
        ];

        foreach (var row in rows)
        {
            var resolution = await ResolveAsync(client, Body(null, row));
            Assert.DoesNotContain(world.Jimenez, resolution.StudentIds);
        }

        // The unfiltered term too — the exclusion is on the root, not on any one arm.
        var whole = await ResolveAsync(client, Body(null));
        Assert.DoesNotContain(world.Jimenez, whole.StudentIds);
        Assert.Equal(10, whole.Count);
    }

    /// <summary>
    /// <b>Both <c>EXISTS</c> arms re-state the term, and Karganilla is the student who proves it.</b>
    ///
    /// <para>
    /// They hold a term record in <em>this</em> term — so they are in the root, and the college and
    /// programme filters find them — while their only enrolment is in <em>last</em> term's offering,
    /// which carries the same <c>SectionKey</c> and the same <c>CourseId</c> as this term's. Drop
    /// <c>CourseOffering.TermId == termId</c> from either predicate and this term's second-year section
    /// gains a student who is not in it, under a section name that looks entirely right.
    /// </para>
    ///
    /// <para>
    /// Lazaro is the other half: a term record in the prior term only, excluded by the root rather than
    /// by either <c>EXISTS</c>. Both are asserted here because either one alone is satisfied by half
    /// the implementation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_enrolment_in_another_terms_offering_does_not_leak_into_this_terms_audience()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var section = await ResolveAsync(client, Body(null, Filter("Section", SecondYearSection)));
        Assert.DoesNotContain(world.Karganilla, section.StudentIds);
        Assert.DoesNotContain(world.Lazaro, section.StudentIds);
        Assert.Equal(4, section.Count);

        var course = await ResolveAsync(
            client, Body(null, Filter("Course", world.SecondYearCourseId.ToString())));
        Assert.DoesNotContain(world.Karganilla, course.StudentIds);
        Assert.DoesNotContain(world.Lazaro, course.StudentIds);
        Assert.Equal(4, course.Count);

        // Karganilla is genuinely in the term and genuinely enrolled — so the two exclusions above are
        // the term predicate working, not a student who was never reachable.
        var byProgram = await ResolveAsync(
            client, Body(null, Filter("Program", world.CriminologyProgramId.ToString())));
        Assert.Contains(world.Karganilla, byProgram.StudentIds);

        var lastTerm = await ResolveAsync(
            client, Body(world.PriorTermId, Filter("Section", SecondYearSection)));
        Assert.Contains(world.Lazaro, lastTerm.StudentIds);
    }

    // ===================================================== 4. ordering, and the sample as a prefix

    /// <summary>
    /// The documented order is <c>StudentNumber</c> then <c>Id</c> — a total order, which is what makes
    /// "the sample is a prefix of the ids" a fact rather than a coincidence of how SQL Server returned
    /// two separate <c>TOP n</c> reads.
    /// </summary>
    [Fact]
    public async Task The_ids_are_ordered_by_student_number_and_the_sample_is_their_prefix()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var resolution = await ResolveAsync(client, Body(null));

        // Written down rather than computed from the response — an expectation derived from the thing
        // under test proves only that it agrees with itself.
        Guid[] expected =
        [
            world.Abad,       // 2025-0001
            world.Bautista,   // 2025-0002
            world.Cruz,       // 2025-0003
            world.Dizon,      // 2025-0004
            world.Espino,     // 2025-0005
            world.Flores,     // 2025-0006
            world.Garcia,     // 2025-0007
            world.Hilario,    // 2025-0008
            world.Ignacio,    // 2025-0009
            // 2025-0010 is Jimenez, soft-deleted, and 2024-0001 is Lazaro, last term's.
            world.Karganilla, // 2025-0011
        ];

        Assert.Equal(expected, resolution.StudentIds);

        Assert.Equal(
            resolution.StudentIds.Take(resolution.Sample.Count),
            resolution.Sample.Select(s => s.StudentId));

        // The preview carries the identity a person reads, composed from the parts because FullName is
        // a computed CLR property that does not translate.
        var first = resolution.Sample[0];
        Assert.Equal(world.Abad, first.StudentId);
        Assert.Equal("2025-0001", first.StudentNumber);
        Assert.Equal("Maria Reyes Abad", first.FullName);
    }

    // ============================================== 3. the ceilings, and the D-42 truncation flag

    /// <summary>
    /// Enough students to reach <see cref="AudienceResolutionLimits.MaxStudentIds"/> and one more, split
    /// across two colleges so a single arrange serves both sides of the boundary: the large college
    /// holds exactly the ceiling, and the whole term holds one over it.
    ///
    /// <para>
    /// Student numbers are zero-padded and the extra student's sorts <em>last</em>, so the bounded id
    /// list from the unfiltered query is exactly the large college's set — which is what lets the two
    /// requests be compared against each other rather than against two independently-written numbers.
    /// </para>
    ///
    /// <para>
    /// No enrolments and no offerings: the ceiling is a property of the response, not of any filter arm,
    /// and ten thousand rows are already the expensive part of this test.
    /// </para>
    /// </summary>
    private async Task<(Guid AtCeilingCollegeId, Guid OverflowStudentId)> ArrangeCeilingAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);

        var atCeiling = TestData.NewCollege(school.Id, "College of Criminal Justice", "CCJ");
        var overflow = TestData.NewCollege(school.Id, "College of Nursing", "CON");
        db.Colleges.AddRange(atCeiling, overflow);

        for (var i = 1; i <= AudienceResolutionLimits.MaxStudentIds; i++)
        {
            var student = TestData.NewStudent(school.Id, $"A-{i:D5}");
            db.Students.Add(student);
            db.StudentTermRecords.Add(
                TestData.NewTermRecord(student.Id, term.Id, collegeId: atCeiling.Id));
        }

        // Sorts after every 'A-' number, so it is the row the ceiling cuts off.
        var last = TestData.NewStudent(school.Id, "Z-00001");
        db.Students.Add(last);
        db.StudentTermRecords.Add(
            TestData.NewTermRecord(last.Id, term.Id, collegeId: overflow.Id));

        await db.SaveChangesAsync();

        return (atCeiling.Id, last.Id);
    }

    /// <summary>
    /// <b>D-42, verbatim.</b> <c>count</c> is a <c>COUNT(*)</c> over the composed filter and is bounded
    /// by nothing; <c>studentIds</c> is bounded by <see cref="AudienceResolutionLimits.MaxStudentIds"/>;
    /// and <c>studentIdsTruncated</c> is derived by comparing the two — <b>never by comparing the list's
    /// length against the ceiling</b>.
    ///
    /// <para>
    /// The live endpoint once shipped counters bounded by its read ceiling rather than by what it had
    /// actually returned, and the result was a headline number describing rows nobody received:
    /// internally consistent, entirely plausible, wrong. The two halves fail different wrong
    /// implementations and both are needed:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Exactly at the ceiling</b> catches <c>truncated = ids.Count == Max</c>, which would claim a caller was missing students that are all already in the list.</description></item>
    ///   <item><description><b>One over the ceiling</b> catches <c>count = ids.Count</c>, which is the D-42 defect itself — the count would come back 5000 for a filter matching 5001.</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Count_is_unbounded_while_the_id_list_is_capped_and_the_flag_compares_the_two()
    {
        var (atCeilingCollegeId, overflowStudentId) = await ArrangeCeilingAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        // Exactly the ceiling: a full list is not a truncated one.
        var atCeiling = await ResolveAsync(
            client, Body(null, Filter("College", atCeilingCollegeId.ToString())));

        Assert.Equal(AudienceResolutionLimits.MaxStudentIds, atCeiling.Count);
        Assert.Equal(AudienceResolutionLimits.MaxStudentIds, atCeiling.StudentIds.Count);
        Assert.False(
            atCeiling.StudentIdsTruncated,
            "A filter matching exactly the ceiling is not truncated. Saying it was sends a caller " +
            "looking for students that are all already in the list.");
        Assert.Equal(AudienceResolutionLimits.MaxStudentIds, atCeiling.StudentIdLimit);

        // One over: the count describes the filter, the list describes what was delivered, and the flag
        // is the difference between them.
        var overflowing = await ResolveAsync(client, Body(null));

        Assert.Equal(AudienceResolutionLimits.MaxStudentIds + 1, overflowing.Count);
        Assert.Equal(AudienceResolutionLimits.MaxStudentIds, overflowing.StudentIds.Count);
        Assert.True(overflowing.StudentIdsTruncated);
        Assert.Equal(AudienceResolutionLimits.MaxStudentIds, overflowing.StudentIdLimit);

        // The count is larger than the list *because* a student was cut, not because it counted
        // something the filter did not match.
        Assert.DoesNotContain(overflowStudentId, overflowing.StudentIds);
        Assert.Equal(atCeiling.StudentIds, overflowing.StudentIds);

        // And the sample is a genuine prefix here, where it is far shorter than the list it previews.
        Assert.Equal(AudienceResolutionLimits.SampleSize, overflowing.Sample.Count);
        Assert.Equal(
            overflowing.StudentIds.Take(AudienceResolutionLimits.SampleSize),
            overflowing.Sample.Select(s => s.StudentId));
    }

    // ================================================================ 8. the refusals, over the wire

    /// <summary>
    /// An unregistered field is a 400 carrying <c>UnknownAudienceField</c>, never a row that quietly
    /// stops applying. A filter that vanishes produces a count <em>larger</em> than the operator asked
    /// for, which looks entirely normal and is not discovered until the wrong people are in a hall.
    ///
    /// <para>
    /// <c>Status</c>, <c>Gender</c> and <c>HasCard</c> are in the list because all three were considered
    /// for the registry and deferred, so they are what a client will try first. The three ADR-001 D-2
    /// cache columns are here for the opposite reason: <c>students.section</c> and <c>students.course</c>
    /// are the columns the registry exists to keep out of reach, so an attempt to name one directly must
    /// be refused rather than resolved.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Nickname")]
    [InlineData("Banana")]
    [InlineData("Status")]
    [InlineData("Gender")]
    [InlineData("HasCard")]
    [InlineData("students.section")]
    [InlineData("Students.Course")]
    [InlineData("Year Level")]
    [InlineData("Sections")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unregistered_field_is_refused_over_the_wire(string field)
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var problem = await RefusedAsync(
            client,
            Body(null, Filter(field, "anything")),
            AudienceResolveOutcome.UnknownAudienceField);

        // The message names all five accepted fields, because a 400 that does not say what would have
        // worked is a 400 a client cannot act on.
        var detail = problem.GetProperty("detail").GetString()!;
        foreach (var accepted in AudienceField.All) Assert.Contains(accepted, detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>null</c> field member is refused, and <b>the property worth pinning is that it is refused at
    /// all</b> — a null that bound to an absent row would widen the audience silently, which is the one
    /// failure mode every refusal in this endpoint exists to prevent.
    ///
    /// <para>
    /// <b>It never reaches the resolver, and the response says so by omission.</b>
    /// <c>AudienceFilterDto.Field</c> is a non-nullable reference type, so <c>[ApiController]</c>'s
    /// automatic model validation rejects the body before the action runs. The result is a 400 carrying
    /// <c>errors</c> rather than the <c>code</c> token every other refusal on this route publishes — so
    /// a filter builder branching on <c>code</c> gets nothing here and has to fall back on the status.
    /// That asymmetry is reported as a finding; this test deliberately asserts <em>neither</em> that
    /// <c>code</c> is present nor that it is absent, because which of the two it should be is a decision
    /// rather than something a test should settle by being written first.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_null_field_is_refused_rather_than_widening_the_audience()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, new
        {
            termId = (Guid?)null,
            filters = new[] { new { field = (string?)null, values = new[] { "x" } } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());

        // And emphatically not a resolution: no count reached the caller under any name.
        Assert.False(body.RootElement.TryGetProperty("count", out _));
    }

    /// <summary>
    /// <b>The field is validated before the term is looked up, and before the values are read.</b>
    ///
    /// <para>
    /// Two consequences are asserted together. First: an unregistered field is refused even when the
    /// term default would have found nothing, so the same malformed request cannot answer 400 on one day
    /// and an empty 200 on the next — the least debuggable shape an API can have. Second: an
    /// unregistered field beats a bad value in the same request, so a client fixes the field it actually
    /// got wrong rather than chasing a value error into a field that does not exist.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unregistered_field_is_refused_before_the_term_and_before_the_values()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var term = await db.Terms.FindAsync(world.CurrentTermId);
            term!.IsCurrent = false;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        // No current term — the happy path here would be an empty 200 — and the field is still refused.
        await RefusedAsync(
            client,
            Body(null, Filter("Nickname", "anything")),
            AudienceResolveOutcome.UnknownAudienceField);

        // A bad value on a good field, behind a bad field. The field wins.
        await RefusedAsync(
            client,
            Body(null, Filter("Nickname", "anything"), Filter("College", "not-a-guid")),
            AudienceResolveOutcome.UnknownAudienceField);
    }

    /// <summary>
    /// An id field can only hold a GUID. Dropping the offending value instead would silently widen the
    /// row — <c>Program is any of (BSCRIM, "oops")</c> would resolve as <c>Program is BSCRIM</c> and
    /// report a count for a filter nobody built — so the whole row is refused.
    /// </summary>
    [Theory]
    [InlineData("College")]
    [InlineData("Program")]
    [InlineData("Course")]
    public async Task An_id_field_refuses_a_value_that_is_not_a_guid(string field)
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await RefusedAsync(
            client, Body(null, Filter(field, "not-a-guid")),
            AudienceResolveOutcome.InvalidAudienceFilterValue);

        await RefusedAsync(
            client, Body(null, Filter(field, "")),
            AudienceResolveOutcome.InvalidAudienceFilterValue);

        // One good value and one bad one — the case the "whole row is refused" rule is actually about.
        await RefusedAsync(
            client, Body(null, Filter(field, world.CriminologyCollegeId.ToString(), "oops")),
            AudienceResolveOutcome.InvalidAudienceFilterValue);
    }

    /// <summary>
    /// A year level is a non-blank value. A year outside the digits the derivation can produce is
    /// well-formed and simply matches nobody — asserted as a 200 in
    /// <see cref="The_year_level_arm_reads_the_derived_digit_and_a_null_year_matches_no_filter"/> — so
    /// blank is the only refusal, and the two cases are pinned on opposite sides of the same line.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_year_level_field_refuses_a_blank_value(string value)
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await RefusedAsync(
            client, Body(null, Filter("YearLevel", value)),
            AudienceResolveOutcome.InvalidAudienceFilterValue);
    }

    /// <summary>
    /// <b>The <c>(unspecified)</c> sentinel, sent exactly as a picker would send it back.</b>
    ///
    /// <para>
    /// <c>GET /academic/course-offerings</c> publishes <c>sectionKey: "(unspecified)"</c> verbatim for a
    /// blank-section offering, and that endpoint is where a filter builder's section values come from —
    /// so a picker listing sections <em>will</em> offer it and a client <em>will</em> post it back. It
    /// has to fail loudly. That key means "the source recorded no section"; it is the one audience
    /// <c>POST /events/{id}/attendees</c> refuses as <c>NotACohort</c>, and the builder must not be a way
    /// around that refusal.
    /// </para>
    ///
    /// <para>
    /// It takes two checks in the implementation and therefore two cases here. The literal
    /// <c>"(unspecified)"</c> <em>normalizes</em> to <c>"UNSPECIFIED"</c> — <c>AcademicKey</c> strips the
    /// parentheses — so only a raw comparison catches the verbatim spelling, while a blank or
    /// punctuation-only value catches the other way in, by normalizing onto the sentinel. Removing
    /// either check leaves the other case resolving as an ordinary section.
    /// </para>
    /// </summary>
    /// <para>
    /// <b>Only the exact published spelling is covered, deliberately.</b> The raw check is
    /// <c>StringComparison.Ordinal</c>, so <c>"(UNSPECIFIED)"</c> shouted is <em>not</em> refused — it
    /// normalizes to the ordinary key <c>UNSPECIFIED</c> and resolves to an empty 200. That is a real
    /// asymmetry with the case-insensitive field matching one level up, and it is reported as a finding
    /// rather than pinned here in either direction: whether the sentinel check should be
    /// case-insensitive is a decision, not something a test should settle by being written.
    /// </para>
    [Theory]
    [InlineData(AcademicKey.Unspecified)]  // verbatim, as GET /academic/course-offerings publishes it
    [InlineData("")]                       // normalizes onto the sentinel
    [InlineData("   ")]
    [InlineData("---")]                    // punctuation only, which Normalize reduces to nothing
    public async Task The_section_field_refuses_the_unspecified_sentinel(string value)
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var problem = await RefusedAsync(
            client, Body(null, Filter("Section", value)),
            AudienceResolveOutcome.InvalidAudienceFilterValue);

        Assert.Contains(
            AcademicKey.Unspecified, problem.GetProperty("detail").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentinel is refused even alongside a section that resolves perfectly well, because the whole
    /// row is refused rather than the offending value dropped. Dropping it would answer 200 with the
    /// count of a narrower filter than the operator built.
    /// </summary>
    [Fact]
    public async Task A_good_section_alongside_the_sentinel_is_still_refused()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await RefusedAsync(
            client, Body(null, Filter("Section", SecondYearSection, AcademicKey.Unspecified)),
            AudienceResolveOutcome.InvalidAudienceFilterValue);
    }

    /// <summary>
    /// Field names are matched case-insensitively over the wire. This is the pairing that makes the
    /// refusals above meaningful: <c>MatchingPlacements</c> switches on canonical spellings with
    /// ordinal <c>==</c>, so if normalization were removed a lower-cased field would reach the throwing
    /// arm and answer 500 — a request a filter builder sends on every keystroke.
    /// </summary>
    [Theory]
    [InlineData("section")]
    [InlineData("SECTION")]
    [InlineData("  Section  ")]
    public async Task A_field_name_in_any_casing_resolves_rather_than_being_refused(string field)
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        AssertResolvesTo(
            await ResolveAsync(client, Body(null, Filter(field, SecondYearSection))),
            world.SecondYears);
    }

    // ========================================================= 9. the row ceiling on the request

    /// <summary>
    /// <paramref name="count"/> copies of one filter row.
    ///
    /// <para>
    /// <b>Identical rows, and that is what keeps these three tests out of an open decision.</b> Whether
    /// two rows naming the same field intersect or union is undecided, and the class summary records
    /// that nothing in this file pins it. Repeating <em>the same row</em> resolves to the same students
    /// under either reading — the intersection of a predicate with itself and its union with itself are
    /// both that predicate — so the 200 below asserts a real audience without taking a side. Twenty
    /// distinct rows are not available to construct: there are only five fields.
    /// </para>
    /// </summary>
    private static object[] RepeatedRows(int count, string field, params string[] values) =>
        [.. Enumerable.Repeat(Filter(field, values), count)];

    /// <summary>
    /// <b>Exactly <see cref="AudienceResolutionLimits.MaxFilterRows"/> rows is a request, not a refusal
    /// — and it still answers the question.</b>
    ///
    /// <para>
    /// The boundary is asserted from the legal side as well as the illegal one, for the reason the
    /// <c>studentIds</c> ceiling is: a cap written <c>&gt;=</c> instead of <c>&gt;</c> refuses the last
    /// legitimate request, and only a test sitting exactly on the boundary notices. The resolution is
    /// checked too, so "at the cap" cannot pass as an empty 200 from a composition that fell apart under
    /// twenty <c>Where</c> clauses.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_filter_set_at_the_row_ceiling_is_answered()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var resolution = await ResolveAsync(client, Body(
            null,
            RepeatedRows(
                AudienceResolutionLimits.MaxFilterRows,
                "Program", world.CriminologyProgramId.ToString())));

        AssertResolvesTo(resolution,
            [.. world.FirstYears, .. world.SecondYears, world.Karganilla]);

        // The same answer the one-row form of this filter gives, which is what makes "twenty rows were
        // all applied" distinguishable from "twenty rows were quietly collapsed to something else".
        Assert.Equal(
            (await ResolveAsync(
                client, Body(null, Filter("Program", world.CriminologyProgramId.ToString())))).Count,
            resolution.Count);
    }

    /// <summary>
    /// <b>One row over the ceiling is <c>400 TooManyFilterRows</c>, and before this it was a 500.</b>
    ///
    /// <para>
    /// Each row composes another <c>Where</c> and nothing bounded how many there could be. Driven
    /// against the real endpoint: fifty rows answered 200 but cost about 3.2 seconds of SQL Server
    /// optimizer time for a 2KB body, and sixty answered <b>500</b> — Msg 8623, "the query processor ran
    /// out of internal resources", surfacing as an unhandled <c>SqlException</c> out of
    /// <c>ResolveAudienceAsync</c>. A well-formed request answering 500 is a bug on any route; on one
    /// that is unauthenticated in the pre-auth build and not rate-limited it is also the cheapest denial
    /// of service in the API.
    /// </para>
    ///
    /// <para>
    /// Asserted one over the cap rather than at sixty deliberately. The refusal is what is under test,
    /// and a test that reproduced the original failure would have to build the query that breaks SQL
    /// Server in order to prove it no longer runs — slow, and dependent on an optimizer threshold that
    /// is not ours to pin.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_filter_set_one_row_over_the_ceiling_is_refused()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var problem = await RefusedAsync(
            client,
            Body(null, RepeatedRows(
                AudienceResolutionLimits.MaxFilterRows + 1,
                "Program", world.CriminologyProgramId.ToString())),
            AudienceResolveOutcome.TooManyFilterRows);

        // Both numbers, because "too many" without them leaves the caller trimming rows one at a time to
        // find out how many were too many.
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains(
            AudienceResolutionLimits.MaxFilterRows.ToString(), detail, StringComparison.Ordinal);
        Assert.Contains(
            (AudienceResolutionLimits.MaxFilterRows + 1).ToString(), detail, StringComparison.Ordinal);

        // Refused, not truncated to the ceiling: no resolution reached the caller under any name. Rows
        // narrow each other, so silently answering the first twenty would publish a count for a wider
        // filter than the one that was sent.
        Assert.False(problem.TryGetProperty("count", out _));
    }

    /// <summary>
    /// <b>The row ceiling is checked before the term is looked up, on the same reasoning as
    /// <see cref="An_unregistered_field_is_refused_before_the_term_and_before_the_values"/>.</b>
    ///
    /// <para>
    /// Same technique as its sibling: the current-term flag is cleared, so the happy path for this
    /// request is an empty 200 and the refusal cannot be coming from anything the database was asked.
    /// Without the ordering, an oversized filter set would answer 400 on a day a term is current and 200
    /// on a day between semesters — the same request, two answers, neither of them wrong-looking.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_filter_set_over_the_ceiling_is_refused_before_the_term_is_resolved()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var term = await db.Terms.FindAsync(world.CurrentTermId);
            term!.IsCurrent = false;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await RefusedAsync(
            client,
            Body(null, RepeatedRows(
                AudienceResolutionLimits.MaxFilterRows + 1,
                "Program", world.CriminologyProgramId.ToString())),
            AudienceResolveOutcome.TooManyFilterRows);

        // The term default really is dead here — this is the empty 200 the request above would have got
        // had the ceiling been checked after the lookup.
        var underTheCeiling = await ResolveAsync(
            client, Body(null, Filter("Program", world.CriminologyProgramId.ToString())));
        Assert.Null(underTheCeiling.TermId);
        Assert.Equal(0, underTheCeiling.Count);
    }

    // ================================================================== the wire shape itself

    /// <summary>
    /// The JSON member names the SPA binds, read from the bytes rather than through a deserializer that
    /// would happily bind <c>StudentIdsTruncated</c> to <c>studentidstruncated</c> and hide a casing
    /// change. Everything else in this file goes through the typed DTO on the strength of this one test.
    /// </summary>
    [Fact]
    public async Task The_resolution_is_served_as_camel_cased_json()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Route, Body(null, Filter("Section", SecondYearSection)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(world.CurrentTermId, root.GetProperty("termId").GetGuid());
        Assert.Equal(4, root.GetProperty("count").GetInt32());
        Assert.Equal(4, root.GetProperty("studentIds").GetArrayLength());
        Assert.False(root.GetProperty("studentIdsTruncated").GetBoolean());
        Assert.Equal(
            AudienceResolutionLimits.MaxStudentIds, root.GetProperty("studentIdLimit").GetInt32());

        var first = root.GetProperty("sample").EnumerateArray().First();
        Assert.Equal(world.Dizon, first.GetProperty("studentId").GetGuid());
        Assert.Equal("2025-0004", first.GetProperty("studentNumber").GetString());
        Assert.Equal("Maria Reyes Dizon", first.GetProperty("fullName").GetString());

        // The preview is identity and a display name and deliberately nothing else — `section` in
        // particular is absent, because the only single-valued answer available for it is the ADR-001
        // D-2 cache column this whole feature exists to avoid reading.
        Assert.False(first.TryGetProperty("section", out _));
        Assert.False(first.TryGetProperty("yearLevel", out _));
        Assert.False(first.TryGetProperty("course", out _));
    }
}
