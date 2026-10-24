using System.Net;
using System.Text;
using System.Text.Json;
using EAMS.Api.Authorization;
using EAMS.Api.Reports;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-011 D-83: <c>GET /students/export.csv</c> over real HTTP. The file is the Students grid as a CSV —
/// the same filters through the one shared query, the same order, soft-deleted students excluded — with
/// Organization added, every active card joined, formulas neutralised and no caching.
///
/// <para>
/// The "every cell is quoted" claim, which is what keeps a space-splitting reader from breaking
/// <c>Last Name</c> into two columns, is pinned in <see cref="CsvExportSpaceSplittingTests"/> beside the
/// other exports, where the space-splitting reader lives.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentExportApiTests : IntegrationTest
{
    public StudentExportApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/students";
    private const string ExportRoute = Route + "/export.csv";

    private sealed record World(Guid SchoolId);

    /// <summary>
    /// Four students, in last-name order: Alpha (BSIT, Active, in an organization), Bravo (BSCS, Inactive,
    /// none), Charlie (BSIT, Active, soft-deleted - must never appear), Delta (BSIT, Active, none).
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var alpha = TestData.NewStudent(school.Id, "EXP-001", firstName: "Maria Clara", lastName: "Alpha", course: "BSIT");
        alpha.Organization = "CICT Society";
        var bravo = TestData.NewStudent(school.Id, "EXP-002", lastName: "Bravo", course: "BSCS", status: "Inactive");
        var charlie = TestData.NewStudent(school.Id, "EXP-003", lastName: "Charlie", course: "BSIT");
        charlie.IsDeleted = true;
        charlie.Organization = "Ghost Club";
        var delta = TestData.NewStudent(school.Id, "EXP-004", lastName: "Delta", course: "BSIT");
        db.Students.AddRange(alpha, bravo, charlie, delta);
        await db.SaveChangesAsync();
        return new World(school.Id);
    }

    /// <summary>
    /// Every cell is quoted, so a comma-only parser is correct: <c>""</c> inside a quoted cell is a literal
    /// quote, and a CRLF outside quotes ends the record. Independent of the writer.
    /// </summary>
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
        Assert.Empty(record);
        return records;
    }

    private static async Task<List<List<string>>> ExportAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync(ExportRoute + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
    }

    private static List<string> NumbersOf(List<List<string>> records) =>
        [.. records.Skip(1).Select(r => r[0])];

    // ============================================================================== the contents

    /// <summary>
    /// The unfiltered export is the header plus every non-deleted student in list order (last name, then
    /// id), each row carrying all eleven D-83 columns plus Task 4's Department, Organization among them.
    /// </summary>
    [Fact]
    public async Task The_export_lists_every_filtered_student_with_organization()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var records = await ExportAsync(client);

        Assert.Equal(StudentCsv.Header, records[0]);
        Assert.Equal(
            ["Student No.", "RFID UID (active)", "Last Name", "First Name", "Middle Name", "Email",
             "Course", "Year Level", "Section", "Department", "Organization", "Status"],
            records[0]);
        Assert.Equal(["EXP-001", "EXP-002", "EXP-004"], NumbersOf(records));

        Assert.Equal(
            ["EXP-001", "", "Alpha", "Maria Clara", "Reyes", "EXP-001@test.local", "BSIT", "3rd Year", "A",
             "", "CICT Society", "Active"],
            records[1]);
        Assert.Equal("", records[2][10]);         // Bravo: no organization is an empty cell
        Assert.Equal("Inactive", records[2][11]); // and the status filter column is the student's own
        Assert.All(records, r => Assert.Equal(12, r.Count));
    }

    /// <summary>
    /// The export and the list share one filtered query, so for each filter the export holds exactly the
    /// students the grid shows. Each case is also pinned to a literal set, so two equally-wrong answers
    /// cannot agree: the soft-deleted student (BSIT, Active) is in no set.
    /// </summary>
    [Theory]
    [InlineData("?search=Alpha", "EXP-001")]
    [InlineData("?search=EXP-00", "EXP-001,EXP-002,EXP-004")]
    [InlineData("?course=BSIT", "EXP-001,EXP-004")]
    [InlineData("?course=BSCS", "EXP-002")]
    [InlineData("?status=Inactive", "EXP-002")]
    [InlineData("?status=Active", "EXP-001,EXP-004")]
    [InlineData("?course=BSIT&status=Active&search=Delta", "EXP-004")]
    [InlineData("?course=BSCS&status=Active", "")]
    public async Task The_export_applies_the_same_filters_as_the_list(string query, string expectedCsv)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        string[] expected = expectedCsv.Length == 0 ? [] : expectedCsv.Split(',');

        var exported = NumbersOf(await ExportAsync(client, query));
        Assert.Equal(expected, exported);

        // The grid, asked the same question, in the same order.
        var separator = query.Length == 0 ? "?" : "&";
        using var list = JsonDocument.Parse(await client.GetStringAsync($"{Route}{query}{separator}pageSize=50"));
        var listed = list.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("studentNumber").GetString()!).ToList();
        Assert.Equal(listed, exported);
    }

    [Fact]
    public async Task Soft_deleted_students_are_not_exported()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync(ExportRoute);
        var text = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("EXP-003", text);
        Assert.DoesNotContain("Charlie", text);
        Assert.DoesNotContain("Ghost Club", text);       // nor the organization only the deleted one carries
        Assert.Contains("EXP-001", text);                 // positive control: the file is not simply empty
    }

    // =============================================================================== the response

    [Fact]
    public async Task The_export_is_not_cacheable()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync(ExportRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^EAMS-students-\d{4}-\d{2}-\d{2}\.csv$", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal([0xEF, 0xBB, 0xBF], (await response.Content.ReadAsByteArrayAsync()).Take(3).ToArray());
    }

    /// <summary>
    /// 401 with no credential, challenged with Bearer; 403 for a signed-in caller whose token holds other
    /// permissions but not <c>students.read</c> (every seeded role holds it, so the token is minted with
    /// the signing key to name a permission set that does not).
    /// </summary>
    [Fact]
    public async Task The_export_requires_students_read()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using var anonymous = factory.CreateClient();
        var unauthenticated = await anonymous.GetAsync(ExportRoute);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("Bearer", unauthenticated.Headers.WwwAuthenticate.Single().Scheme);

        var token = ForgedJwt.Mint(ForgedJwt.ClaimsFor(Guid.NewGuid(), world.SchoolId, EamsPermissions.StudentsWrite));
        using var withoutRead = factory.CreateClient().WithBearer(token);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutRead.GetAsync(ExportRoute)).StatusCode);

        // The same token shape WITH students.read is served, so the 403 above is about the permission.
        var allowed = ForgedJwt.Mint(ForgedJwt.ClaimsFor(Guid.NewGuid(), world.SchoolId, EamsPermissions.StudentsRead));
        using var withRead = factory.CreateClient().WithBearer(allowed);
        Assert.Equal(HttpStatusCode.OK, (await withRead.GetAsync(ExportRoute)).StatusCode);
    }

    // ======================================================================================= cells

    /// <summary>
    /// Several active cards: every one is listed, in ordinal order (not insertion or database order), joined
    /// by <c>"; "</c>. An inactive card is not. Five active cards inserted out of order, so a missing sort
    /// cannot pass by luck.
    /// </summary>
    [Fact]
    public async Task The_rfid_cell_joins_every_active_card_in_ordinal_order()
    {
        var world = await ArrangeAsync();
        await using (var db = NewDbContext())
        {
            var alpha = db.Students.Single(s => s.StudentNumber == "EXP-001");
            foreach (var uid in new[] { "04E5", "04C3", "04A1", "04D4", "04B2" })
                db.RfidCards.Add(TestData.NewCard(world.SchoolId, alpha.Id, uid));
            db.RfidCards.Add(TestData.NewCard(world.SchoolId, alpha.Id, "04F6", isActive: false));

            var delta = db.Students.Single(s => s.StudentNumber == "EXP-004");
            db.RfidCards.Add(TestData.NewCard(world.SchoolId, delta.Id, "04ZZ"));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var records = await ExportAsync(client);

        Assert.Equal("04A1; 04B2; 04C3; 04D4; 04E5", records.Single(r => r[0] == "EXP-001")[1]);
        Assert.Equal("04ZZ", records.Single(r => r[0] == "EXP-004")[1]);   // one card: no separator
        Assert.Equal("", records.Single(r => r[0] == "EXP-002")[1]);       // none: empty
    }

    /// <summary>
    /// An organization (or any text cell) that opens with a formula trigger is written with a leading
    /// apostrophe, inside the quotes, so a spreadsheet shows it as text instead of running it.
    /// </summary>
    [Fact]
    public async Task A_formula_leading_organization_is_neutralised()
    {
        var world = await ArrangeAsync();
        string[] organizations = ["=HYPERLINK(\"http://evil.test\",\"x\")", "+1+1", "@SUM(A1)"];
        await using (var db = NewDbContext())
        {
            // The three non-deleted students, in student-number order.
            var students = db.Students.Where(s => !s.IsDeleted).OrderBy(s => s.StudentNumber).ToList();
            Assert.Equal(organizations.Length, students.Count);
            for (var i = 0; i < students.Count; i++) students[i].Organization = organizations[i];
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var cells = (await ExportAsync(client)).Skip(1).Select(r => r[10]).ToList();

        Assert.Equal(3, cells.Count);
        Assert.All(cells, c => Assert.StartsWith("'", c));
        Assert.Contains("'=HYPERLINK(\"http://evil.test\",\"x\")", cells);
        Assert.Contains("'+1+1", cells);
        Assert.Contains("'@SUM(A1)", cells);
    }
}
