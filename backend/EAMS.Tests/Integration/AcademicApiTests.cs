using System.Net;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The Phase 3b-2 read surface over real HTTP.
///
/// <para>
/// <see cref="AcademicReferenceTests"/> and <see cref="StudentGroupReadTests"/> own the behaviour;
/// this file owns what is invisible from there and what a client actually codes against — the status
/// code, the RFC 7807 body on the one endpoint that can fail, and the JSON member names the SPA will
/// bind. The argument is the one <see cref="ApiContractTests"/> already makes for §6.4: a 404 quietly
/// becoming a 200-with-null would break a client's error handling without failing a single
/// service-level test.
/// </para>
///
/// <para>
/// <b>These routes are the whole reason the phase exists.</b> The tables and the import that fills
/// them have been in place since Phase 2 and nothing exposed them, so the admin SPA could not build an
/// event-audience picker at all — <c>POST /events/{id}/attendees</c> accepted group ids that no
/// endpoint published.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AcademicApiTests : IntegrationTest
{
    public AcademicApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Academic = "/api/v1/academic";
    private const string Groups = "/api/v1/student-groups";

    /// <summary>
    /// One school with a term, a college, a programme, a course, two sections of that course, and a
    /// student enrolled in one of them — the smallest arrangement in which every one of the six routes
    /// returns a row.
    /// </summary>
    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
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

        var course = TestData.NewCourse(school.Id, collegeId: college.Id);
        db.Courses.Add(course);
        var offering = TestData.NewOffering(term.Id, course.Id, "BSFS 2-A");
        db.CourseOfferings.AddRange(
            offering, TestData.NewOffering(term.Id, course.Id, "BSFS 2-B"));

        var student = TestData.NewStudent(school.Id, $"{code}-0001");
        db.Students.Add(student);
        db.Enrollments.Add(TestData.NewEnrollment(student.Id, offering.Id));
        db.StudentTermRecords.Add(TestData.NewTermRecord(
            student.Id, term.Id, program.Id, college.Id, homeSection: "BSFS 2-A"));

        db.StudentGroups.Add(TestData.NewGroup(school.Id, $"SSC Officers ({code})"));

        await db.SaveChangesAsync();
        await ProjectionOn(db).SyncTermAsync(term.Id);

        return school.Id;
    }

    private static async Task<JsonElement> ArrayAsync(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);
        response.EnsureSuccessStatusCode();

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        return body.Clone();
    }

    // ------------------------------------------------------------------------- every route answers

    /// <summary>
    /// All six routes return 200 and a non-empty array. Route by route rather than by count, so a
    /// missing one cannot be masked by another returning rows.
    /// </summary>
    [Theory]
    [InlineData($"{Academic}/terms")]
    [InlineData($"{Academic}/colleges")]
    [InlineData($"{Academic}/programs")]
    [InlineData($"{Academic}/courses")]
    [InlineData($"{Academic}/course-offerings")]
    [InlineData(Groups)]
    public async Task Every_reference_route_answers_with_rows(string route)
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.NotEqual(0, (await ArrayAsync(client, route)).GetArrayLength());
    }

    /// <summary>
    /// The JSON member names the SPA binds, asserted on the wire. .NET's camelCase policy is applied by
    /// the host rather than by the DTO, so a property renamed in C# changes the contract silently — the
    /// same class of drift <c>StudentsApiTests</c> pins for §6.2.
    /// </summary>
    [Fact]
    public async Task The_offering_shape_publishes_the_section_grain_a_picker_binds()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var offerings = await ArrayAsync(client, $"{Academic}/course-offerings");

        // One course, two sections, two rows — the grain the whole endpoint exists for.
        Assert.Equal(2, offerings.GetArrayLength());

        var first = offerings[0];
        foreach (var member in new[]
                 {
                     "id", "termId", "termCode", "courseId", "courseCode", "courseTitle",
                     "sectionName", "sectionKey", "enrolledCount",
                 })
        {
            Assert.True(first.TryGetProperty(member, out _), $"course-offerings does not publish '{member}'.");
        }

        Assert.Equal(
            ["BSFS 2-A", "BSFS 2-B"],
            offerings.EnumerateArray().Select(o => o.GetProperty("sectionName").GetString()).Order());

        Assert.Equal(1, offerings.EnumerateArray().Sum(o => o.GetProperty("enrolledCount").GetInt32()));
    }

    [Fact]
    public async Task The_group_shape_publishes_the_provenance_a_picker_groups_by()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var groups = await ArrayAsync(client, Groups);

        var first = groups[0];
        foreach (var member in new[]
                 {
                     "id", "name", "type", "sourceType", "sourceEntityType",
                     "termId", "termCode", "memberCount", "lastSyncedAt",
                 })
        {
            Assert.True(first.TryGetProperty(member, out _), $"student-groups does not publish '{member}'.");
        }

        Assert.Contains(
            groups.EnumerateArray(),
            g => g.GetProperty("sourceType").GetString() == GroupSourceType.Derived);
    }

    /// <summary>
    /// A term boundary is a calendar date, and it stays one on the wire — <c>YYYY-MM-DD</c>, not a UTC
    /// instant. Serialized as an instant it would shift by eight hours in Manila and make "does this
    /// term contain today" answerable two different ways.
    /// </summary>
    [Fact]
    public async Task Term_dates_are_published_as_calendar_dates_not_instants()
    {
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            var term = TestData.NewTerm(school.Id);
            term.StartsOn = new DateOnly(2025, 8, 11);
            term.EndsOn = new DateOnly(2025, 12, 20);
            db.Terms.Add(term);
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var published = (await ArrayAsync(client, $"{Academic}/terms"))[0];

        Assert.Equal("2025-08-11", published.GetProperty("startsOn").GetString());
        Assert.Equal("2025-12-20", published.GetProperty("endsOn").GetString());
    }

    // ------------------------------------------------------------------------------ terms/current

    [Fact]
    public async Task The_current_term_is_200_when_one_is_flagged()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Academic}/terms/current");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2025-2026-1", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("isCurrent").GetBoolean());
    }

    /// <summary>
    /// <b>404 with a problem body, not a 200 carrying null.</b> "No term is flagged current" is a
    /// configuration state an administrator fixes — it is what exists between semesters — and a client
    /// defaulting a term picker has to tell it apart from "here is the current term, it just has no
    /// fields". The <c>traceId</c> is asserted for the reason <c>EventsApiTests</c> asserts it: §6
    /// declares RFC 7807 and <c>TracedProblemDetailsFactory</c> exists so every body carries one an
    /// operator can quote, including the ones <c>[ApiController]</c> synthesizes from a bare
    /// <c>NotFound()</c>.
    /// </summary>
    [Fact]
    public async Task The_current_term_is_404_with_a_problem_body_when_none_is_flagged()
    {
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            db.Terms.Add(TestData.NewTerm(school.Id, isCurrent: false));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Academic}/terms/current");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("traceId").GetString()));

        // The list still answers 200 with the same term — the 404 is about the flag, not the data.
        Assert.Single((await ArrayAsync(client, $"{Academic}/terms")).EnumerateArray());
    }

    // ----------------------------------------------------------------------------------- filters

    /// <summary>
    /// Every filter, over HTTP, including the binding — a <c>Guid?</c> query parameter that failed to
    /// bind would be silently null and the endpoint would return everything, which is the exact failure
    /// the service-level suite pins from the other side.
    /// </summary>
    [Fact]
    public async Task Every_query_filter_narrows_over_http()
    {
        var schoolId = await ArrangeSchoolAsync();

        Guid collegeId;
        Guid courseId;
        Guid termId;
        await using (var db = NewDbContext())
        {
            collegeId = (await db.Colleges.SingleAsync(c => c.SchoolId == schoolId)).Id;
            courseId = (await db.Courses.SingleAsync(c => c.SchoolId == schoolId)).Id;
            termId = (await db.Terms.SingleAsync(t => t.SchoolId == schoolId)).Id;
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.NotEqual(0, (await ArrayAsync(client, $"{Academic}/programs?collegeId={collegeId}")).GetArrayLength());
        Assert.Equal(0, (await ArrayAsync(client, $"{Academic}/programs?collegeId={Guid.NewGuid()}")).GetArrayLength());

        Assert.NotEqual(0, (await ArrayAsync(client, $"{Academic}/courses?collegeId={collegeId}")).GetArrayLength());
        Assert.NotEqual(0, (await ArrayAsync(client, $"{Academic}/courses?search=SSCI")).GetArrayLength());
        Assert.NotEqual(0, (await ArrayAsync(client, $"{Academic}/courses?search=Criminology")).GetArrayLength());
        Assert.Equal(0, (await ArrayAsync(client, $"{Academic}/courses?search=Astrophysics")).GetArrayLength());

        Assert.Equal(2, (await ArrayAsync(client, $"{Academic}/course-offerings?termId={termId}")).GetArrayLength());
        Assert.Equal(2, (await ArrayAsync(client, $"{Academic}/course-offerings?courseId={courseId}")).GetArrayLength());
        Assert.Equal(0, (await ArrayAsync(client, $"{Academic}/course-offerings?termId={Guid.NewGuid()}")).GetArrayLength());

        // Normalized: the caller sends the display form and the space and hyphen are irrelevant.
        Assert.Equal(1, (await ArrayAsync(client, $"{Academic}/course-offerings?section=BSFS%202-A")).GetArrayLength());
        Assert.Equal(1, (await ArrayAsync(client, $"{Academic}/course-offerings?section=bsfs2a")).GetArrayLength());
        Assert.Equal(0, (await ArrayAsync(client, $"{Academic}/course-offerings?section=NOPE")).GetArrayLength());

        Assert.NotEqual(0, (await ArrayAsync(client, $"{Groups}?sourceType=Derived")).GetArrayLength());
        Assert.NotEqual(0, (await ArrayAsync(client, $"{Groups}?termId={termId}")).GetArrayLength());
        Assert.Equal(0, (await ArrayAsync(client, $"{Groups}?sourceType=Banana")).GetArrayLength());
        Assert.Equal(0, (await ArrayAsync(client, $"{Groups}?termId={Guid.NewGuid()}")).GetArrayLength());
    }

    // ----------------------------------------------------------------------------------- tenancy

    /// <summary>
    /// <b>Tenancy through the real pipeline, not through a test-pinned context.</b>
    ///
    /// <para>
    /// The host resolves its tenant at startup by pinning the lowest school <c>Code</c> (ADR-001 D-6's
    /// development stand-in for §11's claims), so arranging <c>AAA</c> before <c>ZZZ</c> means every
    /// request below runs as <c>AAA</c> and <c>ZZZ</c>'s rows must be invisible. That is worth asserting
    /// separately from the service-level tenancy tests because it exercises the filter as the
    /// application composes it — including on <c>course-offerings</c>, the one table here with no
    /// <c>SchoolId</c> column of its own.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_reference_route_returns_another_schools_rows()
    {
        var pinned = await ArrangeSchoolAsync("AAA");
        await ArrangeSchoolAsync("ZZZ");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        // One school's worth of everything, not two.
        Assert.Single((await ArrayAsync(client, $"{Academic}/terms")).EnumerateArray());
        Assert.Single((await ArrayAsync(client, $"{Academic}/colleges")).EnumerateArray());
        Assert.Single((await ArrayAsync(client, $"{Academic}/programs")).EnumerateArray());
        Assert.Single((await ArrayAsync(client, $"{Academic}/courses")).EnumerateArray());

        var offerings = await ArrayAsync(client, $"{Academic}/course-offerings");
        Assert.Equal(2, offerings.GetArrayLength());

        var groups = await ArrayAsync(client, Groups);
        Assert.Contains(groups.EnumerateArray(), g => g.GetProperty("name").GetString() == "SSC Officers (AAA)");
        Assert.DoesNotContain(groups.EnumerateArray(), g => g.GetProperty("name").GetString() == "SSC Officers (ZZZ)");

        // And the rows that did come back belong to the pinned school, checked against the database
        // rather than inferred from the count.
        await using var unscoped = NewDbContext(new TestSchoolContext());
        foreach (var offering in offerings.EnumerateArray())
        {
            var termId = offering.GetProperty("termId").GetGuid();
            Assert.Equal(pinned, (await unscoped.Terms.SingleAsync(t => t.Id == termId)).SchoolId);
        }
    }

    // --------------------------------------------------------------------------- the D-2 tripwire

    /// <summary>
    /// <b>No route added by this phase publishes an ADR-001 D-2 derived cache column.</b>
    ///
    /// <para>
    /// <c>Students.Course</c>, <c>YearLevel</c> and <c>Section</c> are a single-valued display cache and
    /// are wrong for roughly a quarter of the real roster — twelve of fifty-two students sit in more
    /// than one section. An audience endpoint that answered from them would invite the wrong people,
    /// and the mistake is invisible until after the event.
    /// </para>
    ///
    /// <para>
    /// Asserted against the member names on the wire, over every row of every route, rather than
    /// against the DTO types: the check is about what a client can read and therefore build on.
    /// <c>sectionName</c> and <c>sectionKey</c> on an offering are deliberately <em>not</em> matches —
    /// they are the offering's own section, which is the authoritative grain, not the student's cached
    /// one — so the comparison is on the exact name rather than a substring.
    /// <see cref="AcademicReferenceTests.A_student_in_two_sections_counts_in_both_because_enrollments_not_the_cache_are_read"/>
    /// covers the behavioural half, which is the one that actually decides who gets invited.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData($"{Academic}/terms")]
    [InlineData($"{Academic}/colleges")]
    [InlineData($"{Academic}/programs")]
    [InlineData($"{Academic}/courses")]
    [InlineData($"{Academic}/course-offerings")]
    [InlineData(Groups)]
    public async Task No_reference_route_publishes_a_derived_cache_column(string route)
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var rows = await ArrayAsync(client, route);
        Assert.NotEqual(0, rows.GetArrayLength());

        foreach (var row in rows.EnumerateArray())
        {
            foreach (var member in row.EnumerateObject())
            {
                Assert.DoesNotContain(
                    Student.DerivedAcademicPropertyNames,
                    derived => string.Equals(derived, member.Name, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
