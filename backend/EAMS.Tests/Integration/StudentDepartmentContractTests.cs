using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// BACKOFFICE Task 4 Phase 1: <c>StudentDto</c> gains a nullable <c>Department</c>, last in the record -
/// the <c>College.Name</c> of the programme the student's <c>Course</c> names, derived at read time and
/// never stored. The export writes it as the column immediately before Organization.
///
/// <para>
/// HTTP-level tests assert on the raw JSON, not a deserialized DTO, for the reason
/// <see cref="StudentOrganizationContractTests"/> gives: a DTO without the member would ignore it. The
/// null case is asserted as a PRESENT key holding a JSON null.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentDepartmentContractTests : IntegrationTest
{
    public StudentDepartmentContractTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/students";
    private const string ExportRoute = Route + "/export.csv";
    private const string Property = "department";
    private const string EngineeringCollege = "College of Engineering";
    private const string BusinessCollege = "College of Business";
    private const string ComputingCollege = "College of Computing";

    private static void AssertDepartment(JsonElement student, string? expected, string body)
    {
        Assert.True(student.TryGetProperty(Property, out var value),
            $"The student object has no '{Property}' key. Body: {body}");
        if (expected is null) Assert.Equal(JsonValueKind.Null, value.ValueKind);
        else Assert.Equal(expected, value.GetString());
    }

    private async Task<(Guid SchoolId, Guid CollegeId)> SchoolWithCollegeAsync(
        string schoolCode, string collegeName, params string[] programmeCodes)
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(schoolCode);
        var college = TestData.NewCollege(school.Id, collegeName, code: null);
        db.Schools.Add(school);
        db.Colleges.Add(college);
        foreach (var code in programmeCodes)
            db.Programs.Add(TestData.NewProgram(school.Id, college.Id, code));
        await db.SaveChangesAsync();
        return (school.Id, college.Id);
    }

    private async Task<Student> AddStudentAsync(Guid schoolId, string number, string? course, string last = "Alpha")
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, number, lastName: last);
        student.Course = course;
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student;
    }

    private static async Task<List<JsonElement>> ListItemsAsync(HttpClient client, JsonDocumentHolder holder)
    {
        var raw = await client.GetStringAsync($"{Route}?pageSize=100");
        holder.Document = JsonDocument.Parse(raw);
        return holder.Document.RootElement.GetProperty("items").EnumerateArray().ToList();
    }

    private sealed class JsonDocumentHolder : IDisposable
    {
        public JsonDocument? Document { get; set; }
        public void Dispose() => Document?.Dispose();
    }

    // ================================================================================ 1

    [Fact]
    public async Task List_carries_the_college_name_of_the_students_programme()
    {
        var (schoolId, collegeId) = await SchoolWithCollegeAsync("USA", EngineeringCollege, "BSCE");
        await using (var db = NewDbContext())
        {
            var business = TestData.NewCollege(schoolId, BusinessCollege, code: null);
            db.Colleges.Add(business);
            db.Programs.Add(TestData.NewProgram(schoolId, business.Id, "BSBA"));
            await db.SaveChangesAsync();
        }
        await AddStudentAsync(schoolId, "D-001", "BSCE", "Alpha");
        await AddStudentAsync(schoolId, "D-002", "BSBA", "Bravo");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        using var holder = new JsonDocumentHolder();

        var items = await ListItemsAsync(client, holder);
        var raw = holder.Document!.RootElement.ToString();

        Assert.Equal(2, items.Count);
        AssertDepartment(items.Single(i => i.GetProperty("studentNumber").GetString() == "D-001"),
            EngineeringCollege, raw);
        AssertDepartment(items.Single(i => i.GetProperty("studentNumber").GetString() == "D-002"),
            BusinessCollege, raw);
        Assert.NotEqual(collegeId, Guid.Empty);
    }

    // ================================================================================ 2

    /// <summary>
    /// Course holds the programme's display code, so it is matched on the normalized key: punctuation,
    /// spacing and case all fall away. "BS-IT" has to find the programme coded "BSIT".
    /// </summary>
    [Fact]
    public async Task Department_matches_the_programme_by_normalized_key()
    {
        var (schoolId, _) = await SchoolWithCollegeAsync("USA", ComputingCollege, "BSIT");
        await AddStudentAsync(schoolId, "D-001", "BS-IT", "Alpha");
        await AddStudentAsync(schoolId, "D-002", "bs it", "Bravo");
        await AddStudentAsync(schoolId, "D-003", "BSIT", "Charlie");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        using var holder = new JsonDocumentHolder();

        var items = await ListItemsAsync(client, holder);
        var raw = holder.Document!.RootElement.ToString();

        Assert.Equal(3, items.Count);
        foreach (var item in items) AssertDepartment(item, ComputingCollege, raw);
    }

    // ================================================================================ 3

    /// <summary>
    /// Every case runs in a school that HAS real programmes ("BSIT", one coded "SECTION", and one with a
    /// punctuation-only code whose stored key is "(unspecified)"), so a null answer is the resolver
    /// refusing, not an empty programme table.
    /// </summary>
    [Theory]
    [InlineData(null, "no course")]
    [InlineData("", "empty course")]
    [InlineData("   ", "blank course")]
    [InlineData("NOSUCH", "course naming no known programme")]
    [InlineData("(section)", "raw section placeholder, with a programme coded SECTION present")]
    [InlineData("(unspecified)", "raw unspecified placeholder")]
    [InlineData("---", "punctuation-only course, with a blank-coded programme present")]
    public async Task Department_is_null_when(string? course, string because)
    {
        var (schoolId, _) = await SchoolWithCollegeAsync("USA", ComputingCollege, "BSIT", "SECTION", "-");
        await AddStudentAsync(schoolId, "D-001", course);
        // Positive control in the same school: a resolvable student, so the seed really can resolve.
        await AddStudentAsync(schoolId, "D-002", "BSIT", "Bravo");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        using var holder = new JsonDocumentHolder();

        var items = await ListItemsAsync(client, holder);
        var raw = holder.Document!.RootElement.ToString();

        AssertDepartment(items.Single(i => i.GetProperty("studentNumber").GetString() == "D-002"),
            ComputingCollege, raw);
        var subject = items.Single(i => i.GetProperty("studentNumber").GetString() == "D-001");
        Assert.True(subject.TryGetProperty(Property, out var value), $"no '{Property}' key: {raw}");
        Assert.True(value.ValueKind == JsonValueKind.Null,
            $"Expected a null department for a {because}, got {value}.");
    }

    // ================================================================================ 4

    /// <summary>
    /// UNIQUE(SchoolId, CodeKey) is per tenant: two schools can both own a programme "BSIT" in different
    /// colleges. With no tenant pinned a read spans both, and each student must get ITS school's college.
    /// </summary>
    [Fact]
    public async Task Department_does_not_resolve_across_schools()
    {
        var (schoolA, _) = await SchoolWithCollegeAsync("AAA", EngineeringCollege, "BSIT");
        var (schoolB, _) = await SchoolWithCollegeAsync("BBB", BusinessCollege, "BSIT");
        await AddStudentAsync(schoolA, "A-001", "BSIT", "Alpha");
        await AddStudentAsync(schoolB, "B-001", "BSIT", "Bravo");
        // A school with students but NO such programme must not borrow either neighbour's college.
        var schoolC = (await SchoolWithCollegeAsync("CCC", ComputingCollege, "BSCS")).SchoolId;
        await AddStudentAsync(schoolC, "C-001", "BSIT", "Charlie");

        await using var db = NewDbContext(); // unpinned: sees every school
        var page = await StudentsOn(db).ListAsync(null, null, null, PageRequest.From(1, 50));

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(EngineeringCollege, page.Items.Single(s => s.StudentNumber == "A-001").Department);
        Assert.Equal(BusinessCollege, page.Items.Single(s => s.StudentNumber == "B-001").Department);
        Assert.Null(page.Items.Single(s => s.StudentNumber == "C-001").Department);
    }

    // ================================================================================ 5

    [Fact]
    public async Task Every_student_read_and_write_response_carries_department()
    {
        var (schoolId, _) = await SchoolWithCollegeAsync("USA", ComputingCollege, "BSIT");
        var student = await AddStudentAsync(schoolId, "D-001", "BSIT");
        var bare = await AddStudentAsync(schoolId, "D-002", null, "Bravo");
        await using (var db = NewDbContext())
        {
            db.RfidCards.Add(TestData.NewCard(schoolId, student.Id, "04A7B8C9"));
            await db.SaveChangesAsync();
        }
        var apiKey = await IssueDeviceKeyAsync(schoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        // detail, resolving and non-resolving
        var detailRaw = await client.GetStringAsync($"{Route}/{student.Id}");
        using (var body = JsonDocument.Parse(detailRaw))
            AssertDepartment(body.RootElement, ComputingCollege, detailRaw);
        var bareRaw = await client.GetStringAsync($"{Route}/{bare.Id}");
        using (var body = JsonDocument.Parse(bareRaw))
            AssertDepartment(body.RootElement, null, bareRaw);

        // by-card under a device key
        using var device = factory.CreateClient().WithDeviceKey(apiKey);
        var byCard = await device.GetAsync($"{Route}/by-card/04A7B8C9");
        Assert.Equal(HttpStatusCode.OK, byCard.StatusCode);
        var byCardRaw = await byCard.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(byCardRaw))
        {
            Assert.Equal("D-001", body.RootElement.GetProperty("studentNumber").GetString());
            AssertDepartment(body.RootElement, ComputingCollege, byCardRaw);
        }

        // update response
        var update = await client.PutAsJsonAsync($"{Route}/{student.Id}", new
        {
            studentNumber = "D-001", firstName = "Maricel", middleName = (string?)null,
            lastName = "Alpha", email = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updatedRaw = await update.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(updatedRaw))
        {
            Assert.Equal("Maricel", body.RootElement.GetProperty("firstName").GetString());
            AssertDepartment(body.RootElement, ComputingCollege, updatedRaw);
        }
    }

    // ================================================================================ 6

    /// <summary>Records the SQL EF sent, so the number of programme lookups is observable.</summary>
    private sealed class CapturedSql : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task List_resolves_departments_in_one_programmes_query_per_page()
    {
        var (schoolId, _) = await SchoolWithCollegeAsync("USA", ComputingCollege, "BSIT", "BSCS", "BSIS");
        string[] courses = ["BSIT", "BSCS", "BSIS"];
        for (var i = 0; i < 25; i++)
            await AddStudentAsync(schoolId, $"D-{i:000}", courses[i % 3], $"Last{i:000}");

        var captured = new CapturedSql();
        await using var db = new EamsDbContext(
            new DbContextOptionsBuilder<EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString)
                .AddInterceptors(captured)
                .Options,
            School);

        var page = await StudentsOn(db).ListAsync(null, null, null, PageRequest.From(1, 25));

        Assert.Equal(25, page.Items.Count);
        // Positive controls: the interceptor sees the students read, and the lookup really resolved.
        Assert.Contains(captured.Statements, s => s.Contains("[Students]", StringComparison.Ordinal));
        Assert.All(page.Items, s => Assert.Equal(ComputingCollege, s.Department));

        var programmeQueries = captured.Statements
            .Where(s => s.Contains("[Programs]", StringComparison.Ordinal))
            .ToList();
        Assert.True(programmeQueries.Count == 1,
            $"Expected exactly one programmes query for a 25-student page, saw {programmeQueries.Count}: " +
            string.Join(" || ", programmeQueries));
    }

    // ================================================================================ 7

    private static List<List<string>> Parse(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { record.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                record.Add(cell.ToString());
                cell.Clear();
                records.Add(record);
                record = [];
                i++;
            }
            else Assert.Fail($"Unquoted character '{c}' at offset {i}: every cell must be quoted.");
        }
        Assert.False(quoted, "The CSV ends inside an open quote.");
        return records;
    }

    [Fact]
    public async Task Export_writes_department_before_organization()
    {
        var (schoolId, _) = await SchoolWithCollegeAsync("USA", ComputingCollege, "BSIT");
        var resolved = await AddStudentAsync(schoolId, "D-001", "BS-IT", "Alpha");
        await using (var db = NewDbContext())
        {
            var tracked = await db.Students.SingleAsync(s => s.Id == resolved.Id);
            tracked.Organization = "CICT Society";
            await db.SaveChangesAsync();
        }
        await AddStudentAsync(schoolId, "D-002", "NOSUCH", "Bravo");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.GetAsync(ExportRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        var records = Parse(text);

        var header = records[0];
        var department = header.IndexOf("Department");
        var organization = header.IndexOf("Organization");
        Assert.True(department >= 0, "No Department column in the export header.");
        Assert.Equal(organization - 1, department);

        var withValue = records.Single(r => r[0] == "D-001");
        Assert.Equal(ComputingCollege, withValue[department]);
        Assert.Equal("CICT Society", withValue[organization]);

        var withoutValue = records.Single(r => r[0] == "D-002");
        Assert.Equal("", withoutValue[department]);
        // Empty AND quoted: the raw text carries ,"", at that position, never a bare ,, (a space-splitting
        // reader would shift every later column).
        Assert.Contains("\"NOSUCH\",\"3rd Year\",\"A\",\"\",\"\",\"Active\"", text);
        Assert.All(records, r => Assert.Equal(header.Count, r.Count));
    }
}
