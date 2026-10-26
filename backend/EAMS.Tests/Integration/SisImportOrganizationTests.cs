using ClosedXML.Excel;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Sis;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-011 D-80 (built-in profile version 6, the optional last template column <c>ORGANIZATION</c>): the
/// acceptance criteria that live in the importer, each pinned as a named test so a later clean merge
/// cannot delete one silently (Team X SOP section 8).
///
/// <para>
/// The first AC, "Organization is an optional last template column", is pinned where the template is:
/// <c>SisImportTemplateTests.Template_headers_equal_the_active_profile_source_columns_in_order</c> asserts
/// the literal header list (ORGANIZATION last; 13 columns since built-in version 7, ADR-014 D-92) and the
/// columns marked required, so ORGANIZATION is optional by exactly that assertion.
/// </para>
///
/// <para>
/// The files here are built by <see cref="Workbook"/> rather than <see cref="SyntheticRoster"/>: the
/// synthetic roster is parallel to <c>SisRosterColumns.All</c>, which does not carry the optional
/// ORGANIZATION column, and a one-or-two-student file makes every expected value readable at a glance.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportOrganizationTests : IntegrationTest
{
    public SisImportOrganizationTests(SqlServerFixture sql) : base(sql) { }

    private const string College = "College of Criminal Justice";
    private const string Program = "BSCRIM";
    private const string Section = "BSCRIM 2-A";

    private sealed record World(Guid SchoolId, Guid TermId);

    /// <summary>One data row. A <c>null</c> cell is left empty in the worksheet.</summary>
    private sealed record Line(string RegNo, string? Organization, string? Rfid = null, string? Section = Section);

    private async Task<World> ArrangeAsync(string schoolCode = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(schoolCode);
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        db.Classifications.AddRange(TestData.ClassificationVocabulary(school.Id));
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    /// <summary>
    /// A roster with the five required columns, SECTION_NAME, COURSE_CODE (built-in version 7 is course
    /// mode, code only — ADR-014 D-92), and optionally RFID and ORGANIZATION (last, as in the template).
    /// <paramref name="courseColumns"/> adds the course title and teacher cells too, for a batch with no
    /// profile (course mode with every course column).
    /// </summary>
    private static MemoryStream Workbook(
        IReadOnlyList<Line> lines, bool organizationColumn = true, bool rfidColumn = false,
        bool courseColumns = false)
    {
        var headers = new List<string>
        {
            SisRosterColumns.RegNo, SisRosterColumns.StudentFirstName, SisRosterColumns.StudentLastName,
            SisRosterColumns.CollegeName, SisRosterColumns.Program, SisRosterColumns.SectionName,
        };
        if (rfidColumn) headers.Add(SisRosterColumns.RfidCardSerial);
        if (courseColumns)
        {
            headers.AddRange([
                SisRosterColumns.CourseCode, SisRosterColumns.CourseName, SisRosterColumns.TeacherFullName,
                SisRosterColumns.TeacherFirstName, SisRosterColumns.TeacherLastName,
                SisRosterColumns.TeacherSuffix, SisRosterColumns.TeacherCollege]);
        }
        else
        {
            headers.Add(SisRosterColumns.CourseCode);
        }
        if (organizationColumn) headers.Add(SisRosterColumns.Organization);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(SyntheticRoster.SheetName);
        for (var c = 0; c < headers.Count; c++) sheet.Cell(1, c + 1).Value = headers[c];

        for (var r = 0; r < lines.Count; r++)
        {
            var line = lines[r];
            var cells = new Dictionary<string, string?>
            {
                [SisRosterColumns.RegNo] = line.RegNo,
                [SisRosterColumns.StudentFirstName] = "First" + line.RegNo,
                [SisRosterColumns.StudentLastName] = "Last" + line.RegNo,
                [SisRosterColumns.CollegeName] = College,
                [SisRosterColumns.Program] = Program,
                [SisRosterColumns.SectionName] = line.Section,
                [SisRosterColumns.RfidCardSerial] = line.Rfid,
                [SisRosterColumns.CourseCode] = "SSCI 7",
                [SisRosterColumns.CourseName] = "Life and Works of Rizal",
                [SisRosterColumns.TeacherFullName] = "THERESA GALANG NAVARRO",
                [SisRosterColumns.TeacherFirstName] = "THERESA",
                [SisRosterColumns.TeacherLastName] = "NAVARRO",
                [SisRosterColumns.TeacherSuffix] = "Ms.",
                [SisRosterColumns.TeacherCollege] = "College of Law",
                [SisRosterColumns.Organization] = line.Organization,
            };
            for (var c = 0; c < headers.Count; c++)
                if (cells[headers[c]] is { } value) sheet.Cell(r + 2, c + 1).Value = value;
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private async Task<SisImportPreviewDto> UploadAsync(Guid termId, Stream file)
    {
        file.Position = 0;
        await using var db = NewDbContext();
        return await SisImportOn(db).UploadAsync(new SisImportUploadRequest(file, "roster.xlsx", termId));
    }

    private async Task<SisImportBatchDto> RunAsync(Guid batchId, Guid termId)
    {
        await using var db = NewDbContext();
        return await SisImportOn(db).RunAsync(batchId, termId);
    }

    private async Task<SisImportBatchDto> ImportAsync(Guid termId, Stream file)
    {
        var preview = await UploadAsync(termId, file);
        return await RunAsync(preview.Batch.Id, termId);
    }

    private async Task<List<SisImportRowDto>> RowsOfAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return (await SisImportOn(db).GetRowsAsync(batchId, result: null)).ToList();
    }

    private static SisImportRowDto Row(IEnumerable<SisImportRowDto> rows, int rowNumber) =>
        rows.Single(r => r.RowNumber == rowNumber);

    private static string StudentTouch(SisImportRowDto row) =>
        row.Entities.Single(e => e.EntityType == SisImportEntityType.Student).Action;

    private async Task SeedStudentAsync(Guid schoolId, string regNo, string? organization)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, regNo, firstName: "First" + regNo, lastName: "Last" + regNo);
        student.Organization = organization;
        db.Students.Add(student);
        await db.SaveChangesAsync();
    }

    private async Task<string?> OrganizationOfAsync(string regNo)
    {
        await using var db = NewDbContext();
        return await db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.StudentNumber == regNo)
            .Select(s => s.Organization)
            .SingleAsync();
    }

    private async Task<DateTime> UpdatedAtOfAsync(string regNo)
    {
        await using var db = NewDbContext();
        return await db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.StudentNumber == regNo)
            .Select(s => s.UpdatedAt)
            .SingleAsync();
    }

    // ============================================================================ the cell rules

    /// <summary>
    /// D-80 point 4, first row: a non-blank cell is stored through <c>OrganizationText.Normalize</c> —
    /// trimmed, internal whitespace collapsed, casing kept — for a student the file creates.
    /// </summary>
    [Fact]
    public async Task A_non_blank_organization_cell_is_stored_normalized()
    {
        var world = await ArrangeAsync();

        await using var file = Workbook([new Line("ORG-0001", "  Cict   Society\t Guild ")]);
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("Cict Society Guild", await OrganizationOfAsync("ORG-0001"));
    }

    /// <summary>
    /// D-80 point 4: a non-blank cell that differs (ordinally, on the normalized value) overwrites the
    /// stored one — including a case-only difference, which is NOT treated as "the same".
    /// </summary>
    [Fact]
    public async Task A_non_blank_organization_overwrites_a_different_stored_one()
    {
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "ORG-0002", "SSG");
        await SeedStudentAsync(world.SchoolId, "ORG-0003", "CICT");

        await using var file = Workbook([new Line("ORG-0002", "CICT Society"), new Line("ORG-0003", "cict")]);
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("CICT Society", await OrganizationOfAsync("ORG-0002"));
        Assert.Equal("cict", await OrganizationOfAsync("ORG-0003"));
    }

    /// <summary>
    /// D-80 point 4: a blank cell means "this file does not say" — the stored value is kept. Both an empty
    /// cell and a whitespace-only one, in a file that DOES carry the column.
    /// </summary>
    [Fact]
    public async Task A_blank_organization_cell_keeps_the_stored_value()
    {
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "ORG-0004", "SSG");
        await SeedStudentAsync(world.SchoolId, "ORG-0005", "CICT");

        await using var file = Workbook([new Line("ORG-0004", null), new Line("ORG-0005", "   ")]);
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("SSG", await OrganizationOfAsync("ORG-0004"));
        Assert.Equal("CICT", await OrganizationOfAsync("ORG-0005"));
    }

    /// <summary>
    /// A file with no ORGANIZATION column at all (every Task 2 file) changes no stored organization and
    /// imports without a warning about it.
    /// </summary>
    [Fact]
    public async Task A_file_without_the_organization_column_changes_no_stored_organization()
    {
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "ORG-0006", "SSG");

        await using var file = Workbook([new Line("ORG-0006", "IGNORED")], organizationColumn: false);
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("SSG", await OrganizationOfAsync("ORG-0006"));
        Assert.Null(Row(await RowsOfAsync(batch.Id), 2).WarningCode);
    }

    // =============================================================== the profile decides (ADR-001 D-4)

    /// <summary>
    /// Built-in version 5: the frozen version 6 minus the <c>Student.Organization</c> row. Frozen, not the
    /// live entries, since version 7 (ADR-014 D-92) put the course code back in them.
    /// </summary>
    private static IReadOnlyList<(string SourceColumn, string TargetField, string NormalizationRule, bool IsRequired)>
        VersionFiveEntries() => SectionOnlyMappingProfile.VersionFiveEntries;

    /// <summary>
    /// D-80 point 3: a batch pinned to version 5 reads NO organization cell even from a file that carries
    /// the column — neither onto a new student nor over a stored value. The batch is pinned the way a batch
    /// uploaded before the deploy and run after it is: upload, then repoint at the older profile.
    /// </summary>
    [Fact]
    public async Task A_batch_pinned_to_version_5_reads_no_organization_cell_even_when_the_file_has_one()
    {
        Assert.Equal(7, SisImportProfileTemplate.BuiltInVersion); // the literal: version 5 is below it
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "ORG-0007", "SSG");

        Guid v5Id;
        await using (var db = NewDbContext())
        {
            v5Id = await CategoryMappingProfile.SeedProfileAsync(
                db, world.SchoolId, version: 5, isActive: true, VersionFiveEntries());
        }

        await using var file = Workbook([new Line("ORG-0007", "CICT"), new Line("ORG-0008", "CICT")]);
        var preview = await UploadAsync(world.TermId, file);

        await using (var repoint = NewDbContext())
        {
            var pinned = await repoint.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == preview.Batch.Id);
            pinned.ImportProfileId = v5Id;
            await repoint.SaveChangesAsync();
        }

        var batch = await RunAsync(preview.Batch.Id, world.TermId);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("SSG", await OrganizationOfAsync("ORG-0007"));   // stored value untouched
        Assert.Null(await OrganizationOfAsync("ORG-0008"));           // new student gets none
    }

    /// <summary>
    /// D-80 point 3: a batch with no pinned profile (<c>NoMappedColumns</c>) reads no organization cell.
    /// It runs in course mode (ADR-010 D-77), so the file carries the course and teacher cells too.
    /// </summary>
    [Fact]
    public async Task A_batch_with_no_profile_reads_no_organization_cell()
    {
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "ORG-0009", "SSG");

        await using var file = Workbook(
            [new Line("ORG-0009", "CICT"), new Line("ORG-0010", "CICT")], courseColumns: true);
        var preview = await UploadAsync(world.TermId, file);

        await using (var unpin = NewDbContext())
        {
            var pinned = await unpin.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == preview.Batch.Id);
            pinned.ImportProfileId = null;
            await unpin.SaveChangesAsync();
        }

        var batch = await RunAsync(preview.Batch.Id, world.TermId);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("SSG", await OrganizationOfAsync("ORG-0009"));
        Assert.Null(await OrganizationOfAsync("ORG-0010"));
    }

    /// <summary>
    /// Version 6 is a section-only profile (ADR-010 D-77): organization is read in that mode, and the batch
    /// still files no course, instructor or assignment. Pinned to the frozen version 6, since built-in
    /// version 7 is course mode again (ADR-014 D-92).
    /// </summary>
    [Fact]
    public async Task Organization_imports_under_section_only_mode()
    {
        var world = await ArrangeAsync();
        await using (var db = NewDbContext())
            await SectionOnlyMappingProfile.SeedSectionOnlyProfileAsync(db, world.SchoolId);

        await using var file = Workbook([new Line("ORG-0011", "CICT")], courseColumns: true);
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("CICT", await OrganizationOfAsync("ORG-0011"));

        var row = Row(await RowsOfAsync(batch.Id), 2);
        Assert.DoesNotContain(row.Entities, e =>
            e.EntityType is SisImportEntityType.Course or SisImportEntityType.Instructor
                or SisImportEntityType.CourseOfferingInstructor);
    }

    // ======================================================================================= fan-out

    /// <summary>
    /// D-80 point 6: Organization is a <c>Student</c> column, so it records no touch of its own — a clean
    /// row still fans out to exactly the eight named entities with a card and seven without, with an
    /// organization on it. (Eight since built-in version 7 maps the course code: ADR-014 D-92.)
    /// </summary>
    [Fact]
    public async Task Organization_adds_no_entity_touch_to_the_fan_out()
    {
        string[] withCard =
        [
            SisImportEntityType.College, SisImportEntityType.Program, SisImportEntityType.Course,
            SisImportEntityType.CourseOffering, SisImportEntityType.Student, SisImportEntityType.RfidCard,
            SisImportEntityType.StudentTermRecord, SisImportEntityType.Enrollment,
        ];

        var world = await ArrangeAsync();
        await using var file = Workbook(
            [new Line("ORG-0012", "CICT", Rfid: "0012500001"), new Line("ORG-0013", "CICT")], rfidColumn: true);
        var batch = await ImportAsync(world.TermId, file);
        Assert.Equal(0, batch.FailedRows);

        var rows = await RowsOfAsync(batch.Id);
        var card = Row(rows, 2);
        var noCard = Row(rows, 3);

        Assert.Equal(withCard.Order(StringComparer.Ordinal), card.Entities.Select(e => e.EntityType).Order(StringComparer.Ordinal));
        Assert.Equal(
            withCard.Where(t => t != SisImportEntityType.RfidCard).Order(StringComparer.Ordinal),
            noCard.Entities.Select(e => e.EntityType).Order(StringComparer.Ordinal));
        Assert.Equal("CICT", await OrganizationOfAsync("ORG-0012")); // positive control: it was read
    }

    /// <summary>
    /// D-80 point 6: an organization-only change moves the row's Student touch from Unchanged to Updated and
    /// bumps <c>UpdatedAt</c>. The first import sets a name and section; the second changes ONLY the cell.
    /// </summary>
    [Fact]
    public async Task An_organization_only_change_reports_the_student_Updated()
    {
        var world = await ArrangeAsync();
        await using (var first = Workbook([new Line("ORG-0014", "SSG")]))
            Assert.Equal(0, (await ImportAsync(world.TermId, first)).FailedRows);
        var stampBefore = await UpdatedAtOfAsync("ORG-0014");

        await using var second = Workbook([new Line("ORG-0014", "CICT")]);
        var batch = await ImportAsync(world.TermId, second);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(SisImportEntityAction.Updated, StudentTouch(Row(await RowsOfAsync(batch.Id), 2)));
        Assert.Equal("CICT", await OrganizationOfAsync("ORG-0014"));
        Assert.True(await UpdatedAtOfAsync("ORG-0014") > stampBefore);
    }

    /// <summary>
    /// The counterpart: the same file imported twice leaves the Student touch Unchanged and does not move
    /// <c>UpdatedAt</c> — so the Updated above is the organization's doing and not every run's.
    /// </summary>
    [Fact]
    public async Task The_same_organization_re_imported_reports_Unchanged()
    {
        var world = await ArrangeAsync();
        await using (var first = Workbook([new Line("ORG-0015", "CICT")]))
            Assert.Equal(0, (await ImportAsync(world.TermId, first)).FailedRows);
        var stampBefore = await UpdatedAtOfAsync("ORG-0015");

        await using var second = Workbook([new Line("ORG-0015", "CICT")]);
        var batch = await ImportAsync(world.TermId, second);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(SisImportEntityAction.Unchanged, StudentTouch(Row(await RowsOfAsync(batch.Id), 2)));
        Assert.Equal(stampBefore, await UpdatedAtOfAsync("ORG-0015"));
    }

    // ================================================================== a student on several rows

    /// <summary>
    /// D-80 point 5. Two rows, two organizations: the first non-blank is kept and the second row warns
    /// <c>StudentIdentityConflict</c> naming both values and the row whose value was kept. A second student
    /// whose FIRST row is blank proves the message names the row that carries the kept value, not
    /// <c>group.First</c>'s row.
    /// </summary>
    [Fact]
    public async Task Two_rows_with_different_organizations_keep_the_first_non_blank_and_warn()
    {
        var world = await ArrangeAsync();

        // Rows (worksheet numbers): 2,3 are student A; 4,5,6 are student B whose first row is blank.
        await using var file = Workbook(
        [
            new Line("ORG-0016", "CICT"),
            new Line("ORG-0016", "SSG"),
            new Line("ORG-0017", null),
            new Line("ORG-0017", "CCS"),
            new Line("ORG-0017", "SSG"),
        ]);
        var batch = await ImportAsync(world.TermId, file);
        Assert.Equal(0, batch.FailedRows);

        Assert.Equal("CICT", await OrganizationOfAsync("ORG-0016"));
        Assert.Equal("CCS", await OrganizationOfAsync("ORG-0017"));

        var rows = await RowsOfAsync(batch.Id);

        var conflictA = Row(rows, 3);
        Assert.Equal(SisImportWarningCode.StudentIdentityConflict, conflictA.WarningCode);
        Assert.Contains("organization 'SSG' vs 'CICT'", conflictA.WarningMessage);
        Assert.Contains("row 2", conflictA.WarningMessage);

        var conflictB = Row(rows, 6);
        Assert.Equal(SisImportWarningCode.StudentIdentityConflict, conflictB.WarningCode);
        Assert.Contains("organization 'SSG' vs 'CCS'", conflictB.WarningMessage);
        Assert.Contains("row 5", conflictB.WarningMessage);          // the row whose value was kept...
        Assert.DoesNotContain("row 4", conflictB.WarningMessage);    // ...not the blank first row

        // The rows that agree with the kept value raise nothing.
        Assert.Null(Row(rows, 2).WarningCode);
        Assert.Null(Row(rows, 5).WarningCode);
    }

    /// <summary>D-80 point 5: a case-only variant on a later row is the same organization, not a conflict.</summary>
    [Fact]
    public async Task A_case_variant_on_a_second_row_is_not_a_conflict()
    {
        var world = await ArrangeAsync();

        await using var file = Workbook([new Line("ORG-0018", "CICT"), new Line("ORG-0018", " cict  ")]);
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("CICT", await OrganizationOfAsync("ORG-0018"));   // the first row's spelling is the one kept

        var rows = await RowsOfAsync(batch.Id);
        Assert.Null(Row(rows, 2).WarningCode);
        Assert.Null(Row(rows, 3).WarningCode);
    }

    /// <summary>
    /// D-80 point 5, implementer note: a blank first row is "this row does not say", so the value comes from
    /// the first row that carries one — it is not read as "keep what is stored" while a later row speaks.
    /// The student already has a stored organization, so reading <c>group.First</c> would keep it.
    /// </summary>
    [Fact]
    public async Task A_blank_first_row_takes_the_organization_from_a_later_row()
    {
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "ORG-0019", "SSG");

        await using var file = Workbook([new Line("ORG-0019", null), new Line("ORG-0019", "CICT")]);
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("CICT", await OrganizationOfAsync("ORG-0019"));
    }

    // ============================================================================ too long

    /// <summary>
    /// D-80 point 4 / 8: a cell over 150 characters after normalization is treated as blank — the stored
    /// value is kept — and the row warns <c>OrganizationTooLong</c> and still imports. The 150-character
    /// boundary itself is stored whole. A student whose over-long row is followed by a valid one takes the
    /// valid one, because the over-long cell counts as "this row does not say".
    /// </summary>
    [Fact]
    public async Task An_organization_over_150_characters_warns_and_keeps_the_stored_value()
    {
        var world = await ArrangeAsync();
        await SeedStudentAsync(world.SchoolId, "ORG-0020", "SSG");
        var exactly150 = new string('A', OrganizationText.MaxLength);
        var tooLong = new string('B', OrganizationText.MaxLength + 1);

        await using var file = Workbook(
        [
            new Line("ORG-0020", tooLong),     // row 2: stored value kept, warned
            new Line("ORG-0021", exactly150),  // row 3: the boundary, stored whole, no warning
            new Line("ORG-0022", tooLong),     // row 4: over-long, then...
            new Line("ORG-0022", "CICT"),      // row 5: ...a valid value supplies the organization
        ]);
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal("SSG", await OrganizationOfAsync("ORG-0020"));
        Assert.Equal(exactly150, await OrganizationOfAsync("ORG-0021"));
        Assert.Equal("CICT", await OrganizationOfAsync("ORG-0022"));

        var rows = await RowsOfAsync(batch.Id);
        var warned = Row(rows, 2);
        Assert.Equal(SisImportWarningCode.OrganizationTooLong, warned.WarningCode);
        Assert.NotEqual(SisImportRowResult.Failed, warned.Result);
        Assert.Contains("151", warned.WarningMessage);
        Assert.Null(Row(rows, 3).WarningCode);
        Assert.Equal(SisImportWarningCode.OrganizationTooLong, Row(rows, 4).WarningCode);
    }

    /// <summary>
    /// The <c>OrganizationTooLong</c> rank in <c>RowLedger.WarningPrecedence</c> (above
    /// <c>SectionUnspecified</c>, the lowest). A row that is both over-long and section-blank files under
    /// the over-long code and its message leads with it; an unranked code would sort last and lose to
    /// <c>SectionUnspecified</c>. The positive control is a blank-section row with no organization problem,
    /// which must still file under <c>SectionUnspecified</c>, so the blank section really does warn here.
    /// </summary>
    [Fact]
    public async Task A_too_long_organization_outranks_a_blank_section_in_the_row_warning_code()
    {
        var world = await ArrangeAsync();
        var tooLong = new string('B', OrganizationText.MaxLength + 1);

        await using var file = Workbook(
        [
            new Line("ORG-0030", tooLong, Section: null),  // row 2: both findings
            new Line("ORG-0031", "CICT", Section: null),   // row 3: blank section only (positive control)
        ]);
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        var rows = await RowsOfAsync(batch.Id);
        Assert.Equal(SisImportWarningCode.SectionUnspecified, Row(rows, 3).WarningCode);

        var both = Row(rows, 2);
        Assert.Equal(SisImportWarningCode.OrganizationTooLong, both.WarningCode);
        Assert.StartsWith($"[{SisImportWarningCode.OrganizationTooLong}] ", both.WarningMessage, StringComparison.Ordinal);
    }
}
