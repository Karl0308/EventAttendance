using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Sis;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Task 5 (QA MDVault #470 B1, #472 Q1/Q2): <c>GET /api/v1/sis/import/template</c>, the roster template
/// the school downloads, fills in and uploads back.
///
/// <para>
/// <b>Every test goes through the real HTTP surface and the real importer on SQL Server.</b> The claim the
/// template makes is "a file filled in from this imports", and the only honest proof of that is to fill
/// one in and import it. A test that compared the template to a hand-written header list would pass
/// against a template the importer rejects.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportTemplateTests : IntegrationTest
{
    public SisImportTemplateTests(SqlServerFixture sql) : base(sql) { }

    private const string TemplatePath = "/api/v1/sis/import/template";
    private const string UploadPath = "/api/v1/sis/import/upload";
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private sealed record World(Guid SchoolId, Guid TermId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        db.Classifications.AddRange(TestData.ClassificationVocabulary(school.Id));
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    /// <summary>
    /// The same world, plus an active import profile that maps the four category columns
    /// (<see cref="CategoryMappingProfile"/>). The built-in versions 4 and 5 map none (ADR-009, D-76), so the
    /// template's category section and vocabulary — kept for the day the feature returns — are only
    /// reachable through a profile like this one.
    /// </summary>
    private async Task<World> ArrangeWithCategoryMappingAsync()
    {
        var world = await ArrangeAsync();
        await using var db = NewDbContext();
        await CategoryMappingProfile.SeedCategoryMappingProfileAsync(db, world.SchoolId);
        return world;
    }

    /// <summary>Downloads the template, asserting the file-response contract on the way.</summary>
    private static async Task<XLWorkbook> DownloadAsync(HttpClient client)
    {
        var response = await client.GetAsync(TemplatePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(XlsxContentType, response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("EAMS-roster-template.xlsx", disposition.FileNameStar ?? disposition.FileName?.Trim('"'));

        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new XLWorkbook(new MemoryStream(bytes));
    }

    private static IXLWorksheet Roster(XLWorkbook workbook) =>
        workbook.Worksheet(SisImportTemplateWorkbook.RosterSheetName);

    private static List<string> HeadersOf(IXLWorksheet sheet) =>
        [.. sheet.Row(1).CellsUsed().Select(c => c.GetString())];

    private static int ColumnOf(IXLWorksheet sheet, string header) =>
        sheet.Row(1).CellsUsed().Single(c => c.GetString() == header).Address.ColumnNumber;

    /// <summary>
    /// Writes one row into the Roster sheet by header, the way a person filling it in would — which
    /// means a value whose column the template does not offer is simply not written. Throwing instead
    /// would make a template missing a column fail here, in the fixture, rather than at the importer,
    /// which is the thing the round trip exists to ask.
    /// </summary>
    private static void FillRow(XLWorkbook workbook, int row, IReadOnlyDictionary<string, string> values)
    {
        var sheet = Roster(workbook);
        var columns = sheet.Row(1).CellsUsed().ToDictionary(c => c.GetString(), c => c.Address.ColumnNumber);
        foreach (var (header, value) in values)
            if (columns.TryGetValue(header, out var column))
                sheet.Cell(row, column).SetValue(value);
    }

    private static MemoryStream Save(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    /// <summary>One valid student row. Every value-required column is filled, plus a category.</summary>
    private static Dictionary<string, string> ValidStudentRow(string regNo = "USA90001", string rfid = "") => new()
    {
        [SisRosterColumns.RegNo] = regNo,
        [SisRosterColumns.RfidCardSerial] = rfid,
        [SisRosterColumns.StudentFirstName] = "ROSA",
        [SisRosterColumns.StudentLastName] = "REYES",
        [SisRosterColumns.CollegeName] = "College of Criminal Justice Education",
        [SisRosterColumns.Program] = "BSCRIM",
        [SisRosterColumns.SectionName] = "CRIM1A",
        [SisRosterColumns.CourseCode] = "CRIM 101",
        [SisRosterColumns.CourseName] = "Introduction to Criminology",
        [SisRosterColumns.TeacherFullName] = "SANTOS, JOSE",
        [SisRosterColumns.StudentCategory] = "STUDENT",
    };

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid termId, Stream file)
    {
        using var form = new MultipartFormDataContent();
        var content = new StreamContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(XlsxContentType);
        form.Add(content, "file", "EAMS-roster-template.xlsx");
        form.Add(new StringContent(termId.ToString()), "termId");
        return await client.PostAsync(UploadPath, form);
    }

    /// <summary>The distinct source columns of a stored profile, in Ordinal order — what the importer reads.</summary>
    private async Task<List<string>> ProfileHeadersAsync(Guid profileId)
    {
        await using var db = NewDbContext();
        var rows = await db.SisImportProfileColumns.IgnoreQueryFilters()
            .Where(c => c.ProfileId == profileId)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.SourceColumn, c.SourceColumnKey })
            .ToListAsync();

        return [.. rows.DistinctBy(r => r.SourceColumnKey).Select(r => r.SourceColumn)];
    }

    // ============================================================================== the contract

    /// <summary>
    /// The built-in version 6 template, as a literal (ADR-010 D-77, ADR-011 D-80): QA's ten columns plus
    /// <c>RFID</c> and <c>ORGANIZATION</c>, in profile order. Typed out rather than derived from the template entries, so a profile change
    /// that drops a column — or puts a course, teacher or category column back — fails here.
    /// </summary>
    private static readonly IReadOnlyList<string> BuiltInTemplateColumns =
    [
        "REGNO", "RFID", "STUDENT FIRST NAME", "STUDENT MIDDLE NAME", "STUDENT LAST NAME", "FULL_NAME",
        "EMAIL_ID", "USA_EMAIL", "COLLEGE_NAME", "PROGRAM", "SECTION_NAME", "ORGANIZATION",
    ];

    /// <summary>
    /// The template's header row is the importer's own profile rows — read back from the database the
    /// importer pinned the upload to — distinct and in <c>Ordinal</c> order. Pinned to the literal
    /// 12-column list as well (<see cref="BuiltInTemplateColumns"/>: built-in version 6, ADR-011 D-80),
    /// so a profile change that silently drops a column — or puts a course, teacher or category column
    /// back — fails here too. And the columns whose value the profile requires are the ones marked.
    ///
    /// <para>
    /// The uploaded file still carries the course, teacher and category columns, so the template lacking
    /// them is the profile's doing and not the fixture's.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Template_headers_equal_the_active_profile_source_columns_in_order()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        // An upload is what writes the profile rows; after it, the template must read those rows.
        await using (var roster = SyntheticRoster.Build())
            Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, world.TermId, roster)).StatusCode);

        Guid profileId;
        await using (var db = NewDbContext())
        {
            profileId = await db.SisImportBatches.IgnoreQueryFilters()
                .Select(b => b.ImportProfileId!.Value).SingleAsync();
        }

        using var template = await DownloadAsync(client);
        var headers = HeadersOf(Roster(template));

        Assert.Equal(await ProfileHeadersAsync(profileId), headers);
        Assert.Equal(BuiltInTemplateColumns, headers);

        var marked = Roster(template).Row(1).CellsUsed()
            .Where(c => c.HasComment && c.GetComment().Text.Contains(SisImportTemplateWorkbook.RequiredHeaderComment))
            .Select(c => c.GetString())
            .ToList();

        Assert.Equal(
            [
                SisRosterColumns.RegNo, SisRosterColumns.StudentFirstName, SisRosterColumns.StudentLastName,
                SisRosterColumns.CollegeName, SisRosterColumns.Program,
            ],
            marked);
    }

    /// <summary>
    /// <b>AC-2 (ADR-010 D-77). The template names no course or teacher column on either sheet.</b> Not as
    /// a Roster header, and not in the Instructions sheet as a column to fill in, a vocabulary or a rule:
    /// the seven columns QA struck and the words for them. The Roster half is pinned exactly by
    /// <see cref="Template_headers_equal_the_active_profile_source_columns_in_order"/> (AC-1 and AC-3); this
    /// is the other half, which a header-list assertion cannot see. Every cell and every header comment of
    /// both sheets is read.
    /// </summary>
    [Fact]
    public async Task Template_names_no_course_or_teacher_column_on_either_sheet()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);

        // Positive control: both sheets exist and have content, so an empty sheet cannot pass this.
        Assert.NotEmpty(HeadersOf(Roster(template)));
        Assert.Contains("Columns", InstructionLinesOf(template));

        string[] struck =
        [
            SisRosterColumns.CourseCode, SisRosterColumns.CourseName, SisRosterColumns.TeacherFullName,
            SisRosterColumns.TeacherFirstName, SisRosterColumns.TeacherLastName, SisRosterColumns.TeacherSuffix,
            SisRosterColumns.TeacherCollege,
        ];
        string[] words = ["course", "teacher", "instructor"];

        var texts = new List<string>();
        foreach (var sheet in template.Worksheets)
        {
            foreach (var cell in sheet.CellsUsed())
            {
                texts.Add(cell.GetString());
                if (cell.HasComment) texts.Add(cell.GetComment().Text);
            }
        }

        Assert.NotEmpty(texts);
        foreach (var text in texts)
        {
            foreach (var column in struck)
                Assert.DoesNotContain(column, text, StringComparison.OrdinalIgnoreCase);
            foreach (var word in words)
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <b>The section-only template tells the school one row per student <em>per section</em>.</b> A
    /// student in a block section and an NSTP or ROTC section must appear twice, and under section-only
    /// the old wording ("one row per student") reads as once: a school following it would drop the second
    /// section's enrollment. The line is pinned to its exact text because the whole value is in the words.
    /// </summary>
    [Fact]
    public async Task Section_only_template_says_one_row_per_student_per_section()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);

        Assert.Contains(
            "2. One row per student per section. A student in more than one section (for example a block " +
            "section and an NSTP or ROTC section) appears on one row for each.",
            InstructionLinesOf(template));
    }

    /// <summary>
    /// QA #470 B1 asked for STUDENT, PERSONNEL and SPECIAL; #472 Q1 added FRIARS. The columns carry the
    /// importer's own names (ruling 14, #427), and the Instructions sheet lists what goes in each.
    ///
    /// <para>
    /// <b>Kept for the day the feature returns.</b> Built-in versions 4 and 5 map no category column, so this
    /// runs against a profile that does (<see cref="CategoryMappingProfile"/>): the template follows
    /// the profile, and a profile that maps a category column still gets its section, its vocabulary
    /// and the three rules that go with them. The contrast — versions 4 and 5 get none of it — is
    /// <see cref="Instructions_carry_no_category_section_and_no_vocabulary"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Template_for_a_profile_that_maps_category_columns_still_lists_them()
    {
        var world = await ArrangeWithCategoryMappingAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var headers = HeadersOf(Roster(template));

        Assert.Contains(SisRosterColumns.StudentCategory, headers);
        Assert.Contains(SisRosterColumns.PersonnelCategory, headers);
        Assert.Contains(SisRosterColumns.FriarsCategory, headers);
        Assert.Contains(SisRosterColumns.SpecialCategory, headers);

        var instructions = InstructionLinesOf(template);

        Assert.Contains("Category columns", instructions);
        Assert.Contains($"- {SisRosterColumns.FriarsCategory} (Friars): USA FRIARS", instructions);
        Assert.Contains($"- {SisRosterColumns.PersonnelCategory} (Personnel): ACAD, ANT, NAP, SUPERVISORY/MANAGERIAL", instructions);
        Assert.Contains($"- {SisRosterColumns.SpecialCategory} (Special): C2B2, CFI", instructions);
        Assert.Contains($"- {SisRosterColumns.StudentCategory} (Student): STUDENT", instructions);

        // The three category rules travel with the section. QA's blocker was that these sentences stop
        // the school hurting itself, so they are pinned exactly — and the constants are pinned to say
        // what they must, so pinning them is not pinning a blank.
        Assert.Contains(SisImportTemplateWorkbook.NeverReplacesRule, instructions);
        Assert.Contains(SisImportTemplateWorkbook.BlankCategoriesRule, instructions);
        Assert.Contains(SisImportTemplateWorkbook.WrongColumnRule, instructions);
        Assert.StartsWith("An import never replaces a category a person already has",
            SisImportTemplateWorkbook.NeverReplacesRule, StringComparison.Ordinal);
        Assert.EndsWith("Nobody is ever defaulted to STUDENT.", SisImportTemplateWorkbook.BlankCategoriesRule, StringComparison.Ordinal);
        Assert.StartsWith("A value typed in another group's column is not applied",
            SisImportTemplateWorkbook.WrongColumnRule, StringComparison.Ordinal);
    }

    /// <summary>
    /// The round trip, and the only test that proves the template's promise: download it, fill in one
    /// valid student row, upload it through the real endpoint, run it, and see it import cleanly — no
    /// missing-column rejection at upload, no <c>MissingRequiredValue</c> at run.
    ///
    /// <para>
    /// <b>No classification is applied</b> (ADR-009, D-76): the template carries no category column, so
    /// the <c>STUDENT</c> that <see cref="ValidStudentRow"/> offers has nowhere to go and is not
    /// written, and nothing is held for the person afterwards.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_template_filled_with_one_valid_row_imports_with_no_missing_column_errors()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        FillRow(template, 2, ValidStudentRow());
        await using var filled = Save(template);

        var upload = await UploadAsync(client, world.TermId, filled);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        Guid batchId;
        using (var body = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()))
        {
            var batch = body.RootElement.GetProperty("batch");
            Assert.Equal(SisImportTemplateWorkbook.RosterSheetName, batch.GetProperty("sourceSheetName").GetString());
            Assert.Equal(1, batch.GetProperty("totalRows").GetInt32());
            batchId = batch.GetProperty("id").GetGuid();
        }

        var run = await client.PostAsJsonAsync($"/api/v1/sis/import/{batchId}/run", new { termId = world.TermId });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        using (var body = JsonDocument.Parse(await run.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, body.RootElement.GetProperty("failedRows").GetInt32());
            Assert.Equal(1, body.RootElement.GetProperty("insertedRows").GetInt32());
        }

        var rows = await client.GetStringAsync($"/api/v1/sis/import/{batchId}/rows");
        Assert.DoesNotContain(SisImportFailureCode.MissingRequiredValue, rows, StringComparison.Ordinal);

        await using var read = NewDbContext();
        var student = await read.Students.IgnoreQueryFilters().SingleAsync(s => s.StudentNumber == "USA90001");
        var held = await read.StudentClassifications.IgnoreQueryFilters()
            .Where(a => a.StudentId == student.Id)
            .Select(a => a.Axis)
            .ToListAsync();
        Assert.Empty(held);
    }

    /// <summary>
    /// ADR-001 D-4: when an operator activates a newer profile version, the next upload is pinned to it
    /// (<c>EnsureBuiltInProfileAsync</c> defers to a newer active version) — so the template must follow
    /// it too. Version 4 here renames the card column and adds one; the template carries both, and the
    /// renamed card column is still the one formatted as Text.
    /// </summary>
    [Fact]
    public async Task Template_follows_a_newer_active_profile_version()
    {
        var world = await ArrangeAsync();

        const string renamedCard = "CARD SERIAL";
        const string extraColumn = "HOUSE";
        var builtIn = SisImportProfileTemplate.Entries
            .Select(e => (e.SourceColumn, e.TargetField, e.NormalizationRule, e.IsRequired))
            .ToList();
        var newer = builtIn
            .Select(e => e.SourceColumn == SisRosterColumns.RfidCardSerial ? e with { SourceColumn = renamedCard } : e)
            .Append((extraColumn, "(none)", "(not imported)", false))
            .ToList();

        await SeedProfileAsync(world.SchoolId, SisImportProfileTemplate.BuiltInVersion, isActive: false, builtIn);
        var newerId = await SeedProfileAsync(
            world.SchoolId, SisImportProfileTemplate.BuiltInVersion + 1, isActive: true, newer);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var headers = HeadersOf(Roster(template));

        Assert.Equal(await ProfileHeadersAsync(newerId), headers);
        Assert.Equal(renamedCard, headers[1]);
        Assert.Equal(extraColumn, headers[^1]);
        Assert.DoesNotContain(SisRosterColumns.RfidCardSerial, headers);
        Assert.Equal(
            SisImportTemplateWorkbook.TextNumberFormatId,
            Roster(template).Cell(2, ColumnOf(Roster(template), renamedCard)).Style.NumberFormat.NumberFormatId);
    }

    /// <summary>
    /// The Instructions sheet ships inside the file the school uploads back, so the reader must never
    /// mistake it for the roster. Proved both ways: it is the FIRST sheet and the upload still reads
    /// Roster; and with the Roster sheet removed, the Instructions sheet alone is rejected by name.
    /// </summary>
    [Fact]
    public async Task Instructions_sheet_is_never_selected_as_the_roster()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        Assert.Equal(SisImportTemplateWorkbook.InstructionsSheetName, template.Worksheets.First().Name);

        FillRow(template, 2, ValidStudentRow());
        await using (var filled = Save(template))
        {
            var upload = await UploadAsync(client, world.TermId, filled);
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
            using var body = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
            Assert.Equal(
                SisImportTemplateWorkbook.RosterSheetName,
                body.RootElement.GetProperty("batch").GetProperty("sourceSheetName").GetString());
        }

        // A fresh download rather than the workbook above: ClosedXML re-reads the stream it last saved
        // to on the next save, and that stream went with the upload.
        using var second = await DownloadAsync(client);
        second.Worksheet(SisImportTemplateWorkbook.RosterSheetName).Delete();
        await using var instructionsOnly = Save(second);

        var rejected = await UploadAsync(client, world.TermId, instructionsOnly);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Contains(
            $"'{SisImportTemplateWorkbook.InstructionsSheetName}' (missing",
            await rejected.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A card serial's leading zeros are the serial. The template formats the RFID column (and REGNO) as
    /// Text at column level, so every row a person types into inherits it — checked on a row far below
    /// anything written — and a zero-led serial typed into it reaches the staged row intact.
    /// </summary>
    [Fact]
    public async Task The_rfid_column_is_formatted_as_text_so_leading_zeros_survive()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var sheet = Roster(template);
        var rfid = ColumnOf(sheet, SisRosterColumns.RfidCardSerial);
        var regNo = ColumnOf(sheet, SisRosterColumns.RegNo);

        foreach (var row in new[] { 2, 500, 20000 })
        {
            Assert.Equal(SisImportTemplateWorkbook.TextNumberFormatId, sheet.Cell(row, rfid).Style.NumberFormat.NumberFormatId);
            Assert.Equal(SisImportTemplateWorkbook.TextNumberFormatId, sheet.Cell(row, regNo).Style.NumberFormat.NumberFormatId);
        }

        // A column that is not an identifier is left General — Text everywhere would be a different
        // template, not a safer one.
        Assert.NotEqual(
            SisImportTemplateWorkbook.TextNumberFormatId,
            sheet.Cell(2, ColumnOf(sheet, SisRosterColumns.SectionName)).Style.NumberFormat.NumberFormatId);

        const string zeroLed = "0012503326";
        FillRow(template, 2, ValidStudentRow(rfid: zeroLed));
        await using var filled = Save(template);

        var upload = await UploadAsync(client, world.TermId, filled);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        await using var read = NewDbContext();
        var raw = await read.SisImportRows.IgnoreQueryFilters().Select(r => r.RawData).SingleAsync();
        var cells = JsonSerializer.Deserialize<Dictionary<string, string>>(raw!)!;
        Assert.Equal(zeroLed, cells[SisRosterColumns.RfidCardSerial]);
    }

    /// <summary>
    /// The template sits under the permission the upload it feeds uses — <c>sis.import</c>, not a new
    /// code. An Organizer, whose role does not grant it, is forbidden.
    /// </summary>
    [Fact]
    public async Task Template_download_is_403_without_the_import_permission()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var organizer = await SignedInClientAsync(factory, world.SchoolId, EamsRoleNames.Organizer);

        Assert.Equal(HttpStatusCode.Forbidden, (await organizer.GetAsync(TemplatePath)).StatusCode);
    }

    /// <summary>No token, no template: the anonymous caller is challenged, not served.</summary>
    [Fact]
    public async Task Template_download_is_401_anonymous()
    {
        await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(TemplatePath)).StatusCode);
    }

    // ======================================================================= P10 rework: coverage

    private static List<string> InstructionLinesOf(XLWorkbook workbook) =>
        [.. workbook.Worksheet(SisImportTemplateWorkbook.InstructionsSheetName)
            .Column(1).CellsUsed().Select(c => c.GetString())];

    private static List<(string SourceColumn, string TargetField, string NormalizationRule, bool IsRequired)> BuiltInMapping() =>
        [.. SisImportProfileTemplate.Entries.Select(e => (e.SourceColumn, e.TargetField, e.NormalizationRule, e.IsRequired))];

    /// <summary>
    /// The reader picks the roster sheet by the fixed <see cref="SisRosterColumns.Required"/> list, not by
    /// the profile. A profile version that omits two of those headers must still yield a template the
    /// reader accepts: the omitted headers are appended, in that list's order, and a filled copy uploads.
    /// </summary>
    [Fact]
    public async Task Template_headers_always_include_every_header_the_reader_requires()
    {
        var world = await ArrangeAsync();

        // Two headers the reader requires, listed in Required's own order — that is the order they are
        // appended in. (Version 4's pair was SECTION_NAME and COURSE_NAME; COURSE_NAME is in neither the
        // version 5 mapping nor Required since ADR-010 D-77.)
        var omitted = new[] { SisRosterColumns.Program, SisRosterColumns.SectionName };
        var newer = BuiltInMapping().Where(m => !omitted.Contains(m.SourceColumn)).ToList();
        await SeedProfileAsync(world.SchoolId, SisImportProfileTemplate.BuiltInVersion, isActive: false, BuiltInMapping());
        await SeedProfileAsync(world.SchoolId, SisImportProfileTemplate.BuiltInVersion + 1, isActive: true, newer);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var headers = HeadersOf(Roster(template));

        foreach (var required in SisRosterColumns.Required)
            Assert.Contains(required, headers);
        Assert.Equal(omitted, headers.TakeLast(2));

        FillRow(template, 2, ValidStudentRow());
        await using var filled = Save(template);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, world.TermId, filled)).StatusCode);
    }

    /// <summary>
    /// QA's blocker: the sentences that stop the school hurting itself are pinned exactly, so a later edit
    /// cannot soften or drop one without a red test. The three category rules left this list with the
    /// category section (ADR-009, D-76); they are pinned, for the profile that still shows them, in
    /// <see cref="Template_for_a_profile_that_maps_category_columns_still_lists_them"/>.
    /// </summary>
    [Fact]
    public async Task Instructions_tell_the_school_the_rules_that_matter()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var lines = InstructionLinesOf(template);

        Assert.Contains(SisImportTemplateWorkbook.NoClassRule, lines);
        Assert.Contains(SisImportTemplateWorkbook.NoPlaceholderRule, lines);
        Assert.Contains(
            "The REGNO and RFID columns are " + SisImportTemplateWorkbook.LeadingZerosRuleTail, lines);
        Assert.Contains(SisImportTemplateWorkbook.PasteRule, lines);

        // And the constants say what they must, so pinning them is not pinning a blank.
        Assert.StartsWith("There is currently no way to import a person who has no college and program",
            SisImportTemplateWorkbook.NoClassRule, StringComparison.Ordinal);
        Assert.StartsWith("Do NOT type NA, N/A, NONE", SisImportTemplateWorkbook.NoPlaceholderRule, StringComparison.Ordinal);
        Assert.Contains("leading zeros are part of the value", SisImportTemplateWorkbook.LeadingZerosRuleTail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A database whose next upload has not happened yet: version 3 active (the one that maps the four
    /// category columns), the built-in version never written. The next upload creates the built-in
    /// version from its entries, so the template must be that version's columns — version 5's, without
    /// the categories and the course and teacher columns — and not v3's.
    /// </summary>
    [Fact]
    public async Task Template_uses_the_built_in_version_when_only_an_older_version_is_active()
    {
        var world = await ArrangeAsync();
        const int v3 = 3;
        var v3Mapping = BuiltInMapping()
            .Where(m => !m.TargetField.StartsWith(SisImportProfileTemplate.ClassificationTargetPrefix, StringComparison.Ordinal))
            .Concat(SisRosterColumns.ClassificationColumns.Select(c => (
                c.Column, SisImportProfileTemplate.ClassificationTargetFor(c.Axis), "RosterClassification.Resolve", false)))
            .ToList();
        await SeedProfileAsync(world.SchoolId, v3, isActive: true, v3Mapping);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);

        Assert.Equal(BuiltInTemplateColumns, HeadersOf(Roster(template)));
        Assert.Contains($"version {SisImportProfileTemplate.BuiltInVersion}.", InstructionLinesOf(template)[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The contrast with <see cref="Template_for_a_profile_that_maps_category_columns_still_lists_them"/>:
    /// the built-in version 5 (like 4) maps no category column, so its Instructions sheet has no category section
    /// and no vocabulary — a section explaining columns the file does not have would tell the school to
    /// fill in nothing, and a list of accepted values the import will not apply would be a lie. The
    /// school has a full vocabulary here (<see cref="ArrangeAsync"/> seeds it), so its absence from the
    /// sheet is the template's doing and not an empty table's.
    /// </summary>
    [Fact]
    public async Task Instructions_carry_no_category_section_and_no_vocabulary()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var lines = InstructionLinesOf(template);

        // Positive control: the sheet is there and has its other sections.
        Assert.Contains("Columns", lines);

        Assert.DoesNotContain("Category columns", lines);
        Assert.DoesNotContain(lines, l => l.Contains("NAP", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("USA FRIARS", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("SUPERVISORY/MANAGERIAL", StringComparison.Ordinal));
        Assert.DoesNotContain(SisImportTemplateWorkbook.NeverReplacesRule, lines);
        Assert.DoesNotContain(SisImportTemplateWorkbook.BlankCategoriesRule, lines);
        Assert.DoesNotContain(SisImportTemplateWorkbook.WrongColumnRule, lines);
        foreach (var (_, column) in SisRosterColumns.ClassificationColumns)
            Assert.DoesNotContain(lines, l => l.Contains(column, StringComparison.Ordinal));
    }

    /// <summary>
    /// The organization rule line (ADR-011, D-80) is written only when the profile maps
    /// <c>Student.Organization</c>. The v6 built-in carries it, with the mapped header and the exact rule
    /// text; a v5-shaped profile (the built-in minus that row) does not, and a rule about a column the
    /// file lacks would tell the school to fill in nothing.
    /// </summary>
    [Fact]
    public async Task Organization_instructions_line_appears_only_when_the_profile_maps_it()
    {
        const string expected =
            "ORGANIZATION is optional, up to 150 characters. A blank cell keeps the organization already " +
            "stored for the student; a filled-in cell replaces it, and a longer value is not applied and is " +
            "reported. For a student on several rows, the first filled-in cell is used.";

        var withOrg = await ArrangeAsync();
        using (var factory = new EamsApiFactory(Sql.ConnectionString))
        using (var client = await SignedInClientAsync(factory, withOrg.SchoolId))
        using (var template = await DownloadAsync(client))
        {
            var lines = InstructionLinesOf(template);
            Assert.Contains(SisRosterColumns.Organization, HeadersOf(Roster(template)));
            Assert.Contains(expected, lines);
        }

        // Same school (one per database): a newer active profile now supersedes the built-in.
        var v5Shaped = withOrg;
        var mapping = BuiltInMapping()
            .Where(m => m.TargetField != SisImportProfileTemplate.StudentOrganizationTarget)
            .ToList();
        Assert.True(mapping.Count < BuiltInMapping().Count, "the fixture must actually drop the organization row");
        // The built-in row must exist (inactive) for a newer active version to be preferred to it.
        await SeedProfileAsync(v5Shaped.SchoolId, SisImportProfileTemplate.BuiltInVersion, isActive: false, BuiltInMapping());
        await SeedProfileAsync(v5Shaped.SchoolId, SisImportProfileTemplate.BuiltInVersion + 1, isActive: true, mapping);

        using (var factory = new EamsApiFactory(Sql.ConnectionString))
        using (var client = await SignedInClientAsync(factory, v5Shaped.SchoolId))
        using (var template = await DownloadAsync(client))
        {
            var lines = InstructionLinesOf(template);
            // Positive control: the sheet is there and is the seeded profile's (the reader-required headers
            // keep ORGANIZATION on the Roster sheet whatever the profile maps, so the header is no signal).
            Assert.Contains("Columns", lines);
            Assert.Contains($"version {SisImportProfileTemplate.BuiltInVersion + 1}.", lines[1], StringComparison.Ordinal);
            Assert.DoesNotContain(lines, l => l.Contains(SisImportTemplateWorkbook.OrganizationRuleTail, StringComparison.Ordinal));
            Assert.DoesNotContain(expected, lines);
        }
    }

    /// <summary>
    /// Built-in version present but inactive, an older one active: the importer refuses the older one and pins to the built-in's
    /// stored rows, so the template reads those rows (told apart from the built-in entries by a marker).
    /// </summary>
    [Fact]
    public async Task Template_uses_the_built_in_version_when_it_exists_but_is_inactive_and_an_older_one_is_active()
    {
        var world = await ArrangeAsync();
        const string marker = "V3 MARKER";
        var v3 = BuiltInMapping().Append((marker, "(none)", "(not imported)", false)).ToList();
        var older = BuiltInMapping().ToList();
        var v3Id = await SeedProfileAsync(world.SchoolId, SisImportProfileTemplate.BuiltInVersion, isActive: false, v3);
        await SeedProfileAsync(world.SchoolId, SisImportProfileTemplate.BuiltInVersion - 1, isActive: true, older);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var headers = HeadersOf(Roster(template));

        Assert.Equal(await ProfileHeadersAsync(v3Id), headers);
        Assert.Equal(marker, headers[^1]);
    }

    /// <summary>
    /// With no school in the caller's context and more than one in the database, there is no honest
    /// choice: 409 with a problem body, never a template for somebody else's school.
    /// </summary>
    [Fact]
    public async Task Template_is_409_when_no_school_resolves()
    {
        var world = await ArrangeAsync();
        await using (var db = NewDbContext())
        {
            db.Schools.Add(TestData.NewSchool("OTHER"));
            await db.SaveChangesAsync();
        }

        using var baseFactory = new EamsApiFactory(Sql.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ISchoolContext>(new TestSchoolContext())));
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync(TemplatePath);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No school could be resolved.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The importer refuses a retired or merged-away category (ClassificationAssignment.IsAssignable), so
    /// listing one as accepted would send the school to type a value that is then rejected.
    /// </summary>
    [Fact]
    public async Task Retired_and_merged_classifications_are_not_listed_as_accepted_values()
    {
        var world = await ArrangeWithCategoryMappingAsync();
        await using (var db = NewDbContext())
        {
            var vocabulary = await db.Classifications.IgnoreQueryFilters()
                .Where(c => c.SchoolId == world.SchoolId).ToListAsync();
            vocabulary.Single(c => c.Name == "NAP").IsActive = false;
            var ant = vocabulary.Single(c => c.Name == "ANT");
            ant.IsActive = false;
            ant.MergedIntoClassificationId = vocabulary.Single(c => c.Name == "ACAD").Id;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);

        Assert.Contains(
            $"- {SisRosterColumns.PersonnelCategory} (Personnel): ACAD, SUPERVISORY/MANAGERIAL",
            InstructionLinesOf(template));
    }

    [Fact]
    public async Task A_school_with_no_classifications_says_so_per_group()
    {
        var world = await ArrangeWithCategoryMappingAsync();
        await using (var db = NewDbContext())
        {
            db.Classifications.RemoveRange(
                await db.Classifications.IgnoreQueryFilters().Where(c => c.SchoolId == world.SchoolId).ToListAsync());
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var lines = InstructionLinesOf(template);

        foreach (var (axis, column) in SisRosterColumns.ClassificationColumns)
            Assert.Contains($"- {column} ({axis}): (none defined yet; ask an EAMS administrator)", lines);
    }

    /// <summary>A person who is two things — STUDENT and NAP on one row, as three real people are — lands as both.</summary>
    [Fact]
    public async Task Round_trip_with_personnel_and_two_categories_on_one_row_applies_both()
    {
        var world = await ArrangeWithCategoryMappingAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var row = ValidStudentRow(regNo: "7200004273");
        row[SisRosterColumns.PersonnelCategory] = "NAP";
        FillRow(template, 2, row);

        await RunAsync(client, world.TermId, template);

        await using var read = NewDbContext();
        var student = await read.Students.IgnoreQueryFilters().SingleAsync(s => s.StudentNumber == "7200004273");
        var held = await read.StudentClassifications.IgnoreQueryFilters()
            .Where(a => a.StudentId == student.Id)
            .OrderBy(a => a.Axis)
            .Select(a => a.Axis + ":" + a.Classification!.Name)
            .ToListAsync();
        Assert.Equal([$"{ClassificationAxis.Personnel}:NAP", $"{ClassificationAxis.Student}:STUDENT"], held);
    }

    /// <summary>Personnel numbers like 0020242 carry significant zeros; the stored student number keeps them.</summary>
    [Fact]
    public async Task A_zero_led_REGNO_survives_the_round_trip()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        FillRow(template, 2, ValidStudentRow(regNo: "0020242"));

        await RunAsync(client, world.TermId, template);

        await using var read = NewDbContext();
        Assert.True(await read.Students.IgnoreQueryFilters().AnyAsync(s => s.StudentNumber == "0020242"));
        Assert.False(await read.Students.IgnoreQueryFilters().AnyAsync(s => s.StudentNumber == "20242"));
    }

    /// <summary>
    /// Vocabulary names are administrator-typed text written into a spreadsheet; one that looks like a
    /// formula must arrive as text, never as a formula Excel would evaluate.
    /// </summary>
    [Fact]
    public async Task A_classification_named_like_a_formula_is_written_as_text()
    {
        var world = await ArrangeWithCategoryMappingAsync();
        const string formulaLike = "=1+1";
        await using (var db = NewDbContext())
        {
            db.Classifications.Add(TestData.NewClassification(world.SchoolId, formulaLike, ClassificationAxis.Special));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadAsync(client);
        var cell = template.Worksheet(SisImportTemplateWorkbook.InstructionsSheetName)
            .Column(1).CellsUsed().Single(c => c.GetString().Contains(formulaLike, StringComparison.Ordinal));

        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.False(cell.HasFormula);
        Assert.Equal($"- {SisRosterColumns.SpecialCategory} (Special): {formulaLike}, C2B2, CFI", cell.GetString());
    }

    /// <summary>Uploads a filled template and runs it, asserting it finished with no failed row.</summary>
    private static async Task RunAsync(HttpClient client, Guid termId, XLWorkbook template)
    {
        await using var filled = Save(template);
        var upload = await UploadAsync(client, termId, filled);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        Guid batchId;
        using (var body = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()))
            batchId = body.RootElement.GetProperty("batch").GetProperty("id").GetGuid();

        var run = await client.PostAsJsonAsync($"/api/v1/sis/import/{batchId}/run", new { termId });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        using var result = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        Assert.Equal(0, result.RootElement.GetProperty("failedRows").GetInt32());
    }

    // ---------------------------------------------------------------------------------- fixtures

    private async Task<Guid> SeedProfileAsync(
        Guid schoolId, int version, bool isActive,
        IReadOnlyList<(string SourceColumn, string TargetField, string NormalizationRule, bool IsRequired)> mapping)
    {
        await using var db = NewDbContext();

        var profile = new SisImportProfile
        {
            SchoolId = schoolId,
            Name = SisImportProfileTemplate.ProfileName,
            NameKey = AcademicKey.NormalizeOrUnspecified(SisImportProfileTemplate.ProfileName),
            Version = version,
            Source = SisImportSource.Excel,
            IsActive = isActive,
            Description = $"Seeded version {version}.",
        };
        db.SisImportProfiles.Add(profile);

        for (var i = 0; i < mapping.Count; i++)
        {
            db.SisImportProfileColumns.Add(new SisImportProfileColumn
            {
                Profile = profile,
                SourceColumn = mapping[i].SourceColumn,
                SourceColumnKey = SisRosterColumns.HeaderKey(mapping[i].SourceColumn),
                TargetField = mapping[i].TargetField,
                NormalizationRule = mapping[i].NormalizationRule,
                IsRequired = mapping[i].IsRequired,
                Ordinal = i,
            });
        }

        await db.SaveChangesAsync();
        return profile.Id;
    }
}
