using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Sis;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Pins what "Organization" meant BEFORE <c>Students.Organization</c> (ADR-011), and what must not move
/// with it: personnel matching, the advisory personnel split (ADR-007 D-70) and a blank-keeps import.
///
/// <para>
/// This class was written as survivors plus three <c>Today_</c> tests that recorded behaviour the change
/// reversed on purpose. The change landed, and those three were REPLACED, not fixed, by the named tests that
/// pin the new behaviour: <see cref="OrganizationAudienceStudentTests"/> (an Organization audience
/// resolving students, Custom narrowing them, the merged options list) and
/// <c>SisImportOrganizationTests</c> (the import). One of them,
/// <c>Today_an_organization_audience_resolves_no_students</c>, was a weak pin even before the flip: its
/// students carried no organization, so it could not tell "no student side" from "no student has the org".
/// </para>
///
/// <para>
/// SURVIVOR cited, not duplicated: an Organization audience resolving the same personnel, trim and
/// case-insensitively (QA #543), is pinned end to end by
/// <c>OrganizationNormalizationApiTests.Whitespace_and_case_variants_all_resolve_into_the_canonical_organization_audience</c>.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class OrganizationMeaningPinTests : IntegrationTest
{
    public OrganizationMeaningPinTests(SqlServerFixture sql) : base(sql) { }

    private const string AudienceRoute = "/api/v1/event-audiences";
    private const string TemplatePath = "/api/v1/sis/import/template";
    private const string UploadPath = "/api/v1/sis/import/upload";
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private sealed record World(Guid SchoolId, Guid ClassificationId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var classification = TestData.NewEventClassification(school.Id);
        db.EventClassifications.Add(classification);
        await db.SaveChangesAsync();
        return new World(school.Id, classification.Id);
    }

    /// <summary>A personnel row with an Organization stored exactly as given (bypassing the service, so a raw
    /// legacy value can be placed under test).</summary>
    private async Task SeedPersonnelAsync(
        Guid schoolId, string number, string? organization, string department = "CICT")
    {
        await using var db = NewDbContext();
        var person = TestData.NewPersonnel(schoolId, number, department: department);
        person.Organization = organization;
        db.Personnel.Add(person);
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> CreateAudienceAsync(
        HttpClient client, Guid classificationId, string name, string type, object criteria)
    {
        var created = await client.PostAsJsonAsync(AudienceRoute, new
        {
            name,
            eventClassificationId = classificationId,
            audienceType = type,
            criteria,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private sealed record Resolved(int StudentCount, int PersonnelCount, IReadOnlyList<string> PersonnelNumbers);

    private static async Task<Resolved> ResolveAsync(HttpClient client, Guid audienceId)
    {
        using var body = JsonDocument.Parse(
            await (await client.GetAsync($"{AudienceRoute}/{audienceId}/attendees")).Content.ReadAsStringAsync());
        var root = body.RootElement;
        var personnel = root.GetProperty("attendees").EnumerateArray()
            .Where(a => a.GetProperty("type").GetString() == "Personnel")
            .Select(a => a.GetProperty("number").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new Resolved(
            root.GetProperty("studentCount").GetInt32(), root.GetProperty("personnelCount").GetInt32(), personnel);
    }

    // ============================================================================ SURVIVORS

    /// <summary>
    /// SURVIVOR: a Custom definition's Organization criterion narrows PERSONNEL exactly as today. Four
    /// personnel across department x organization; Departments [CICT] AND Organizations [CICT] keeps only the
    /// two in both (one stored with a case/whitespace variant), and drops the one in the wrong organization
    /// and the one in the wrong department. Asserts the personnel set only, so it holds whatever the change
    /// does for students.
    /// </summary>
    [Fact]
    public async Task Custom_organization_criterion_narrows_personnel_with_the_other_criteria()
    {
        var world = await ArrangeAsync();
        await SeedPersonnelAsync(world.SchoolId, "EMP-A", "CICT", department: "CICT");     // in
        await SeedPersonnelAsync(world.SchoolId, "EMP-B", " cict ", department: "CICT");   // in (variant)
        await SeedPersonnelAsync(world.SchoolId, "EMP-C", "CCS", department: "CICT");      // wrong organization
        await SeedPersonnelAsync(world.SchoolId, "EMP-D", "CICT", department: "CCS");      // wrong department

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var id = await CreateAudienceAsync(client, world.ClassificationId, "Custom CICT members",
            AudienceType.Custom, new { scope = AudienceScope.Both, departments = new[] { "CICT" }, organizations = new[] { "CICT" } });

        var resolved = await ResolveAsync(client, id);

        Assert.Equal(2, resolved.PersonnelCount);
        Assert.Equal(["EMP-A", "EMP-B"], resolved.PersonnelNumbers);
    }

    /// <summary>
    /// SURVIVOR (ADR-007 D-70): personnel resolved by an Organization audience are ADVISORY and never enter
    /// the student denominator. Through the real event path: the audience is attached to an event that also
    /// has one individually-attached student. The denominator (<c>Expected</c>, on both the audience read and
    /// the summary) is that one student; the two personnel appear only as <c>AdvisoryPersonnelCount</c> and
    /// as the definition's <c>PersonnelCount</c>. The school's other students carry no organization, so the
    /// numbers hold whether or not Organization audiences also match students after the change.
    /// </summary>
    [Fact]
    public async Task Organization_audience_personnel_are_advisory_and_not_in_the_student_denominator()
    {
        var world = await ArrangeAsync();

        Guid eventId, definitionId, attachedStudentId;
        await using (var db = NewDbContext())
        {
            var ev = TestData.NewEvent(world.SchoolId, EventStatus.Open);
            ev.EventClassificationId = world.ClassificationId;
            db.Events.Add(ev);

            var student = TestData.NewStudent(world.SchoolId, "2023-0001");
            db.Students.Add(student);
            db.Students.Add(TestData.NewStudent(world.SchoolId, "2023-0002")); // in the school, not attached

            await db.SaveChangesAsync();
            eventId = ev.Id;
            attachedStudentId = student.Id;
        }

        await SeedPersonnelAsync(world.SchoolId, "EMP-A", "CICT");
        await SeedPersonnelAsync(world.SchoolId, "EMP-B", "cict ");
        await SeedPersonnelAsync(world.SchoolId, "EMP-C", "CCS");

        await using (var db = NewDbContext())
        {
            var def = new AudienceDefinition
            {
                SchoolId = world.SchoolId,
                EventClassificationId = world.ClassificationId,
                Name = "CICT members",
                NameKey = AudienceText.KeyFor("CICT members"),
                AudienceType = AudienceType.Organization,
                CriteriaJson = JsonSerializer.Serialize(
                    new AudienceCriteriaDto(Organizations: ["CICT"]), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                IsActive = true,
            };
            db.AudienceDefinitions.Add(def);
            await db.SaveChangesAsync();
            definitionId = def.Id;
        }

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(
                eventId, new EventAudienceRequest(null, [attachedStudentId], [definitionId]));
        }

        await using var read = NewDbContext();
        var audience = await EventsOn(read).GetAudienceAsync(eventId);

        Assert.Equal(1, audience!.Expected);               // the attached student only
        Assert.Equal(2, audience.AdvisoryPersonnelCount);  // EMP-A and EMP-B, surfaced but NOT counted

        var definition = Assert.Single(audience.Definitions);
        Assert.Equal(2, definition.PersonnelCount);

        await using var readSummary = NewDbContext();
        Assert.Equal(1, (await EventsOn(readSummary).GetSummaryAsync(eventId))!.Expected);
    }

    /// <summary>
    /// SURVIVOR — the baseline for "a blank keeps". The built-in template (version 6 now, with its
    /// <c>ORGANIZATION</c> column), filled with one student row that supplies only what the template asks
    /// for (REGNO, names, college, program, section — no middle name, no e-mail, no alternate e-mail and a
    /// BLANK organization), is imported over an existing student carrying all of those values AND a stored
    /// organization. Every student field the import touches, the organization included, is unchanged
    /// afterwards (and, the lookup being a <c>Single</c> on the student number, no second student appeared).
    /// Renamed from <c>V5_template_import_...</c>: the template it downloads is version 6.
    /// </summary>
    [Fact]
    public async Task Template_import_leaves_an_existing_students_fields_unchanged()
    {
        var world = await ArrangeImportWorldAsync();
        const string regNo = "USA90001";

        await using (var db = NewDbContext())
        {
            var student = TestData.NewStudent(world.SchoolId, regNo, firstName: "ROSA", middleName: "Q", lastName: "REYES",
                course: "BSCRIM");
            student.Section = "CRIM1A";
            student.AlternateEmail = "rosa.personal@example.com";
            student.Organization = "CICT Society";
            db.Students.Add(student);
            await db.SaveChangesAsync();
        }

        var before = await ReadStudentAsync(regNo);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        using var template = await DownloadTemplateAsync(client);
        FillRow(template, 2, new Dictionary<string, string>
        {
            [SisRosterColumns.RegNo] = regNo,
            [SisRosterColumns.StudentFirstName] = "ROSA",
            [SisRosterColumns.StudentLastName] = "REYES",
            [SisRosterColumns.CollegeName] = "College of Criminal Justice Education",
            [SisRosterColumns.Program] = "BSCRIM",
            [SisRosterColumns.SectionName] = "CRIM1A",
        });

        using var run = await UploadAndRunAsync(client, world.TermId, template);

        Assert.Equal(0, run.RootElement.GetProperty("failedRows").GetInt32());

        var after = await ReadStudentAsync(regNo);
        Assert.Equal(before, after);
        // Positive control: the fixture really carried the values a blank must not erase.
        Assert.Equal("Q", after.MiddleName);
        Assert.Equal("rosa.personal@example.com", after.AlternateEmail);
        Assert.Equal("CICT Society", after.Organization);
        Assert.Equal($"{regNo}@test.local", after.Email);
    }

    // ================================================================================= fixtures

    private sealed record ImportWorld(Guid SchoolId, Guid TermId);

    private async Task<ImportWorld> ArrangeImportWorldAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        await db.SaveChangesAsync();
        return new ImportWorld(school.Id, term.Id);
    }

    /// <summary>The scalar student fields the import could touch, as a comparable value.</summary>
    private sealed record StudentSnapshot(
        Guid Id, string FirstName, string? MiddleName, string LastName, string? Email, string? AlternateEmail,
        string? Course, string? YearLevel, string? Section, string Status, bool IsDeleted,
        string? Organization);

    private async Task<StudentSnapshot> ReadStudentAsync(string studentNumber)
    {
        await using var db = NewDbContext();
        return await db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.StudentNumber == studentNumber)
            .Select(s => new StudentSnapshot(
                s.Id, s.FirstName, s.MiddleName, s.LastName, s.Email, s.AlternateEmail,
                s.Course, s.YearLevel, s.Section, s.Status, s.IsDeleted, s.Organization))
            .SingleAsync();
    }

    private static async Task<XLWorkbook> DownloadTemplateAsync(HttpClient client)
    {
        var response = await client.GetAsync(TemplatePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
    }

    private static void FillRow(XLWorkbook workbook, int row, IReadOnlyDictionary<string, string> values)
    {
        var sheet = workbook.Worksheet(SisImportTemplateWorkbook.RosterSheetName);
        var columns = sheet.Row(1).CellsUsed().ToDictionary(c => c.GetString(), c => c.Address.ColumnNumber);
        foreach (var (header, value) in values)
            if (columns.TryGetValue(header, out var column))
                sheet.Cell(row, column).SetValue(value);
    }

    private static async Task<JsonDocument> UploadAndRunAsync(HttpClient client, Guid termId, XLWorkbook template)
    {
        var stream = new MemoryStream();
        template.SaveAs(stream);
        stream.Position = 0;

        using var form = new MultipartFormDataContent();
        var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(XlsxContentType);
        form.Add(content, "file", "EAMS-roster-template.xlsx");
        form.Add(new StringContent(termId.ToString()), "termId");
        var upload = await client.PostAsync(UploadPath, form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        Guid batchId;
        using (var body = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()))
            batchId = body.RootElement.GetProperty("batch").GetProperty("id").GetGuid();

        var run = await client.PostAsJsonAsync($"/api/v1/sis/import/{batchId}/run", new { termId });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        return JsonDocument.Parse(await run.Content.ReadAsStringAsync());
    }
}
