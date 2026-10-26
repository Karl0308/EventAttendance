using System.Net;
using System.Net.Http.Json;
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
/// ADR-014 D-92 (built-in profile version 7): <c>COURSE_CODE</c> is back in the roster template, code
/// only — no <c>COURSE_NAME</c>, no <c>UA_FULLNAME</c>, no <c>TEACHER *</c> column (QA's answer on Task 2,
/// MDVault #651). Each acceptance criterion is a named test (Team X SOP section 8).
///
/// <para>
/// <b>Expected numbers, by hand from <see cref="SyntheticRoster.Rows"/></b> imported through the twelve
/// columns a version 7 file carries (the template's thirteen minus the optional ORGANIZATION, which
/// <see cref="SyntheticRoster"/> cannot write): five course codes (<c>SSCI 7</c> and <c>SSci7</c> are one
/// key), five offerings, eleven enrollments — the same as the frozen version 4 run in
/// <c>SisImportSectionOnlyTests</c>, because teachers and titles never decided either count. The
/// <c>SSCI 7</c> course holds Maria, Ana, Pedro, Rosa and Lucia.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportCourseCodeTests : IntegrationTest
{
    public SisImportCourseCodeTests(SqlServerFixture sql) : base(sql) { }

    private const string TemplatePath = "/api/v1/sis/import/template";
    private const string AudienceResolvePath = "/api/v1/events/audience/resolve";

    private const int ExpectedCourses = 5;
    private const int ExpectedEnrollments = 11;

    /// <summary>The version 7 template, as a literal. Typed out so a profile change fails here.</summary>
    private static readonly IReadOnlyList<string> VersionSevenColumns =
    [
        "REGNO", "RFID", "STUDENT FIRST NAME", "STUDENT MIDDLE NAME", "STUDENT LAST NAME", "FULL_NAME",
        "EMAIL_ID", "USA_EMAIL", "COLLEGE_NAME", "PROGRAM", "SECTION_NAME", "COURSE_CODE", "ORGANIZATION",
    ];

    /// <summary>The columns version 7 still leaves out: the course title and the five teacher columns.</summary>
    private static readonly string[] TitleAndTeacherColumns =
    [
        SisRosterColumns.CourseName, SisRosterColumns.TeacherFullName, SisRosterColumns.TeacherFirstName,
        SisRosterColumns.TeacherLastName, SisRosterColumns.TeacherSuffix, SisRosterColumns.TeacherCollege,
    ];

    /// <summary>A version 7 file: the template's columns that <see cref="SyntheticRoster"/> can write.</summary>
    private static readonly IReadOnlyList<string> VersionSevenFileColumns =
    [
        SisRosterColumns.RegNo, SisRosterColumns.RfidCardSerial, SisRosterColumns.StudentFirstName,
        SisRosterColumns.StudentMiddleName, SisRosterColumns.StudentLastName, SisRosterColumns.FullName,
        SisRosterColumns.EmailId, SisRosterColumns.UsaEmail, SisRosterColumns.CollegeName,
        SisRosterColumns.Program, SisRosterColumns.SectionName, SisRosterColumns.CourseCode,
    ];

    private sealed record World(Guid SchoolId, Guid TermId);

    /// <summary>School, term and vocabulary, no profile: the first upload creates built-in version 7.</summary>
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

    private static MemoryStream VersionSevenFile() =>
        SyntheticRoster.Build(SyntheticRoster.Rows(), VersionSevenFileColumns);

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

    private async Task<SisImportBatchDto> ImportVersionSevenAsync(World world)
    {
        await using var file = VersionSevenFile();
        var preview = await UploadAsync(world.TermId, file);
        return await RunAsync(preview.Batch.Id, world.TermId);
    }

    private async Task<List<SisImportRowDto>> RowsOfAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return (await SisImportOn(db).GetRowsAsync(batchId, result: null)).ToList();
    }

    // ================================================================================== the profile

    /// <summary>
    /// <b>The built-in profile is version 7 and maps COURSE_CODE — display value and key, both required —
    /// right after SECTION_NAME, and maps no course title and no teacher.</b> Checked on the entries and on
    /// the rows the first upload stores, which are what the importer reads.
    /// </summary>
    [Fact]
    public async Task Built_in_profile_v7_maps_course_code_and_no_title_or_teachers()
    {
        // The literal as well as the constant: asserting BuiltInVersion against itself passes at any value.
        Assert.Equal(7, SisImportProfileTemplate.BuiltInVersion);

        var world = await ArrangeAsync();
        await using (var file = VersionSevenFile())
            await UploadAsync(world.TermId, file);

        await using var read = NewDbContext();
        var profile = await read.SisImportProfiles.IgnoreQueryFilters()
            .SingleAsync(p => p.SchoolId == world.SchoolId && p.IsActive);
        Assert.Equal(7, profile.Version);

        var rows = await read.SisImportProfileColumns.IgnoreQueryFilters()
            .Where(c => c.ProfileId == profile.Id)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.SourceColumn, c.TargetField, c.NormalizationRule, c.IsRequired })
            .ToListAsync();

        var courseRows = rows.Where(r => r.TargetField.StartsWith("Course.", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            new[]
            {
                new { SourceColumn = "COURSE_CODE", TargetField = "Course.Code", NormalizationRule = "RosterText.Clean", IsRequired = true },
                new { SourceColumn = "COURSE_CODE", TargetField = "Course.CodeKey", NormalizationRule = "AcademicKey.NormalizeOrUnspecified", IsRequired = true },
            },
            courseRows);

        // Placed where the SIS export (and version 4) put it: after SECTION_NAME, before ORGANIZATION.
        var order = rows.Select(r => r.SourceColumn).Distinct().ToList();
        Assert.Equal(order.IndexOf(SisRosterColumns.SectionName) + 1, order.IndexOf(SisRosterColumns.CourseCode));
        Assert.Equal(order.IndexOf(SisRosterColumns.CourseCode) + 1, order.IndexOf(SisRosterColumns.Organization));

        Assert.DoesNotContain(rows, r => r.TargetField == SisImportProfileTemplate.CourseTitleTarget);
        Assert.DoesNotContain(rows, r => r.TargetField.StartsWith("Instructor.", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => TitleAndTeacherColumns.Contains(r.SourceColumn));
    }

    // ================================================================================== the template

    /// <summary>
    /// <b>The template lists thirteen columns, COURSE_CODE among them and marked required</b>, and its
    /// Instructions carry the course-code line — and say nothing about a course title or a teacher.
    /// </summary>
    [Fact]
    public async Task Template_lists_thirteen_columns_with_course_code()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync(TemplatePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var template = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));

        var roster = template.Worksheet(SisImportTemplateWorkbook.RosterSheetName);
        var headers = roster.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Equal(13, headers.Count);
        Assert.Equal(VersionSevenColumns, headers);

        var courseHeader = roster.Row(1).CellsUsed().Single(c => c.GetString() == SisRosterColumns.CourseCode);
        Assert.True(courseHeader.HasComment);
        Assert.Contains(SisImportTemplateWorkbook.RequiredHeaderComment, courseHeader.GetComment().Text);

        var lines = template.Worksheet(SisImportTemplateWorkbook.InstructionsSheetName)
            .Column(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains($"version {SisImportProfileTemplate.BuiltInVersion}.", lines[1], StringComparison.Ordinal);
        Assert.Contains(SisRosterColumns.CourseCode + SisImportTemplateWorkbook.CourseCodeRuleTail, lines);
        Assert.Contains($"- {SisRosterColumns.CourseCode}: MUST be filled in.", lines);
        Assert.Contains(SisImportTemplateWorkbook.NoPlaceholderRuleCourseMode, lines);
        Assert.DoesNotContain(SisImportTemplateWorkbook.BlankSectionRule, lines);

        foreach (var line in lines)
        {
            foreach (var column in TitleAndTeacherColumns)
                Assert.DoesNotContain(column, line, StringComparison.OrdinalIgnoreCase);
            foreach (var word in new[] { "title", "teacher", "instructor" })
                Assert.DoesNotContain(word, line, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ================================================================================== the import

    /// <summary>
    /// <b>A version 7 import files every section under its real course, not the section-only
    /// placeholder.</b> Five courses, keyed by code (two spellings of SSCI 7 are one), eleven enrollments,
    /// and no <c>(section)</c> course anywhere.
    /// </summary>
    [Fact]
    public async Task A_v7_import_places_sections_on_real_courses_not_the_placeholder()
    {
        var world = await ArrangeAsync();
        var batch = await ImportVersionSevenAsync(world);

        Assert.Equal(0, batch.FailedRows);

        await using var read = NewDbContext();
        var keys = await read.Courses.IgnoreQueryFilters().Select(c => c.CodeKey).ToListAsync();
        Assert.Equal(ExpectedCourses, keys.Count);
        Assert.DoesNotContain(AcademicKey.SectionPlaceholder, keys);
        Assert.Contains(AcademicKey.Normalize(SyntheticRoster.RizalCourseCode), keys);

        var offeringCourses = await read.CourseOfferings.IgnoreQueryFilters()
            .Select(o => o.Course!.CodeKey).Distinct().ToListAsync();
        Assert.DoesNotContain(AcademicKey.SectionPlaceholder, offeringCourses);
        Assert.Equal(ExpectedEnrollments, await read.Enrollments.IgnoreQueryFilters().CountAsync());

        var rows = await RowsOfAsync(batch.Id);
        Assert.Contains(rows, r => r.Entities.Any(e => e.EntityType == SisImportEntityType.Course));
    }

    /// <summary>
    /// <b>A version 7 import records no instructor</b>: no instructor, no offering-to-instructor link, no
    /// instructor touch on any row and no placeholder-teacher warning — the file has no teacher column and
    /// the profile maps none.
    /// </summary>
    [Fact]
    public async Task A_v7_import_records_no_instructors()
    {
        var world = await ArrangeAsync();
        var batch = await ImportVersionSevenAsync(world);

        Assert.Equal(0, batch.FailedRows);

        await using var read = NewDbContext();
        // Positive control: the import did write the course side.
        Assert.Equal(ExpectedCourses, await read.Courses.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await read.Instructors.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await read.CourseOfferingInstructors.IgnoreQueryFilters().CountAsync());

        var rows = await RowsOfAsync(batch.Id);
        Assert.DoesNotContain(rows, r => r.Entities.Any(e =>
            e.EntityType is SisImportEntityType.Instructor or SisImportEntityType.CourseOfferingInstructor));
        Assert.DoesNotContain(rows, r => r.WarningCode == SisImportWarningCode.InstructorPlaceholder);
    }

    /// <summary>
    /// <b>Course titles stay null when only the code is mapped</b> — even from a file that still carries
    /// COURSE_NAME, because version 7 maps no <c>Course.Title</c> row and the importer reads no cell it does
    /// not map. No title-alias warning either.
    /// </summary>
    [Fact]
    public async Task Course_title_stays_null_when_only_the_code_is_mapped()
    {
        var world = await ArrangeAsync();

        // The full export, titles and teachers included: what is left unread is the profile's doing.
        await using var file = SyntheticRoster.Build(SyntheticRoster.Rows(), SyntheticRoster.ColumnsWithoutCategories);
        var preview = await UploadAsync(world.TermId, file);
        var batch = await RunAsync(preview.Batch.Id, world.TermId);

        Assert.Equal(0, batch.FailedRows);

        await using var read = NewDbContext();
        var courses = await read.Courses.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(ExpectedCourses, courses.Count);
        Assert.All(courses, c => Assert.Null(c.Title));
        Assert.Equal(0, await read.Instructors.IgnoreQueryFilters().CountAsync());

        var rows = await RowsOfAsync(batch.Id);
        Assert.DoesNotContain(rows, r => r.WarningCode == SisImportWarningCode.CourseTitleAlias);
    }

    // ============================================================== the code is required (reviewer W-1)

    /// <summary>
    /// <b>A version 7 row with a blank COURSE_CODE fails with <c>MissingRequiredValue</c></b> — it is not
    /// filed under the section-only placeholder. Every other row imports.
    /// </summary>
    [Fact]
    public async Task A_v7_row_with_blank_course_code_fails_MissingRequiredValue()
    {
        var world = await ArrangeAsync();
        var rows = SyntheticRoster.Rows();
        rows[0][SyntheticRoster.ColumnIndex(SisRosterColumns.CourseCode)] = "";

        await using var file = SyntheticRoster.Build(rows, VersionSevenFileColumns);
        var preview = await UploadAsync(world.TermId, file);
        var batch = await RunAsync(preview.Batch.Id, world.TermId);

        Assert.Equal(1, batch.FailedRows);

        var failed = Assert.Single(await RowsOfAsync(batch.Id), r => r.Result == SisImportRowResult.Failed);
        Assert.Equal(2, failed.RowNumber);   // worksheet row 2 is the first data row
        Assert.Contains(SisImportFailureCode.MissingRequiredValue, failed.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains(SisRosterColumns.CourseCode, failed.ErrorMessage!, StringComparison.Ordinal);

        await using var read = NewDbContext();
        Assert.DoesNotContain(AcademicKey.SectionPlaceholder,
            await read.Courses.IgnoreQueryFilters().Select(c => c.CodeKey).ToListAsync());
    }

    /// <summary>
    /// <b>A version 7 batch over a file with no COURSE_CODE column fails every row</b> (JJ ruling D-94: no
    /// placeholder fallback). The header is not in <see cref="SisRosterColumns.Required"/>, so the upload
    /// is accepted; the run then rejects each row, and no <c>(section)</c> course is created.
    /// </summary>
    [Fact]
    public async Task A_v7_file_without_COURSE_CODE_column_fails_every_row()
    {
        var world = await ArrangeAsync();
        var columns = VersionSevenFileColumns.Where(c => c != SisRosterColumns.CourseCode).ToList();

        await using var file = SyntheticRoster.Build(SyntheticRoster.Rows(), columns);
        var preview = await UploadAsync(world.TermId, file);
        var batch = await RunAsync(preview.Batch.Id, world.TermId);

        Assert.Equal(SyntheticRoster.DataRowCount, batch.TotalRows);
        Assert.Equal(batch.TotalRows, batch.FailedRows);
        Assert.All(await RowsOfAsync(batch.Id), r =>
            Assert.Contains(SisImportFailureCode.MissingRequiredValue, r.ErrorMessage!, StringComparison.Ordinal));

        await using var read = NewDbContext();
        Assert.Equal(0, await read.Courses.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await read.Enrollments.IgnoreQueryFilters().CountAsync());
    }

    /// <summary>
    /// <b>A term imported in the version 6 era and re-imported under version 7 keeps each student in one
    /// section group per section</b> (ADR-014 R2). After both runs a student can hold the v6 placeholder
    /// offering and one or more v7 course offerings with the same section key; the section-group projection
    /// keys on the section, so each (group, student) pair is one membership and the three sections stay three
    /// groups. Maria (three BSCRIM 2-A offerings by the end) is in exactly one section group.
    /// </summary>
    [Fact]
    public async Task A_v6_era_term_reimported_under_v7_keeps_each_student_in_one_section_group()
    {
        const int v6 = 6;
        const int expectedSectionGroups = 3;   // BSCRIM 2-A, ROTC, BSN 1-B
        var world = await ArrangeAsync();

        Guid v6Id;
        await using (var db = NewDbContext())
        {
            v6Id = await CategoryMappingProfile.SeedProfileAsync(
                db, world.SchoolId, v6, isActive: true, SectionOnlyMappingProfile.Entries);
        }

        // The v6-era run: uploaded, pinned to version 6, run.
        await using (var file = VersionSevenFile())
        {
            var preview = await UploadAsync(world.TermId, file);
            await using (var repoint = NewDbContext())
            {
                var pinned = await repoint.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == preview.Batch.Id);
                pinned.ImportProfileId = v6Id;
                await repoint.SaveChangesAsync();
            }
            Assert.Equal(0, (await RunAsync(preview.Batch.Id, world.TermId)).FailedRows);
        }

        await using (var read = NewDbContext())
        {
            // Positive control: the first run really was section-only.
            Assert.Equal([AcademicKey.SectionPlaceholder],
                await read.Courses.IgnoreQueryFilters().Select(c => c.CodeKey).ToListAsync());
        }

        // The same term, re-imported under built-in version 7.
        Assert.Equal(0, (await ImportVersionSevenAsync(world)).FailedRows);

        await using var after = NewDbContext();
        Assert.Equal(ExpectedCourses + 1, await after.Courses.IgnoreQueryFilters().CountAsync());

        var sectionGroups = await after.StudentGroups.IgnoreQueryFilters()
            .Where(g => g.SchoolId == world.SchoolId && g.SourceEntityType == GroupSourceEntityType.Section)
            .Select(g => g.Id).ToListAsync();
        Assert.Equal(expectedSectionGroups, sectionGroups.Count);

        var members = await after.StudentGroupMembers.IgnoreQueryFilters()
            .Where(m => sectionGroups.Contains(m.StudentGroupId))
            .Select(m => new { m.StudentGroupId, m.StudentId })
            .ToListAsync();
        Assert.NotEmpty(members);
        Assert.Equal(members.Count, members.Distinct().Count());

        var mariaId = await after.Students.IgnoreQueryFilters()
            .Where(s => s.StudentNumber == SyntheticRoster.MariaRegNo).Select(s => s.Id).SingleAsync();
        Assert.Single(members, m => m.StudentId == mariaId);
    }

    // ============================================================================= ADR-001 D-4 pinning

    /// <summary>
    /// <b>A batch pinned to version 6 stays section-only.</b> A school whose active profile is version 6:
    /// the next upload creates version 7 and supersedes version 6 without touching its rows, and a batch
    /// repointed at version 6 — one uploaded before the deploy and run after it — still files every section
    /// under the placeholder course and reads no course code, even from a file that carries one.
    /// </summary>
    [Fact]
    public async Task A_batch_pinned_to_v6_stays_section_only()
    {
        const int v6 = 6;
        var world = await ArrangeAsync();

        Guid v6Id;
        await using (var db = NewDbContext())
        {
            v6Id = await CategoryMappingProfile.SeedProfileAsync(
                db, world.SchoolId, v6, isActive: true, SectionOnlyMappingProfile.Entries);
        }

        await using var file = VersionSevenFile();
        var preview = await UploadAsync(world.TermId, file);

        await using (var read = NewDbContext())
        {
            var profiles = await read.SisImportProfiles.IgnoreQueryFilters()
                .Where(p => p.SchoolId == world.SchoolId).OrderBy(p => p.Version).ToListAsync();
            Assert.Equal(new[] { v6, SisImportProfileTemplate.BuiltInVersion }, profiles.Select(p => p.Version));
            Assert.False(profiles[0].IsActive);
            Assert.True(profiles[1].IsActive);
            Assert.Equal(SectionOnlyMappingProfile.Entries.Count,
                await read.SisImportProfileColumns.IgnoreQueryFilters().CountAsync(c => c.ProfileId == v6Id));
            Assert.Equal(profiles[1].Id, (await read.SisImportBatches.IgnoreQueryFilters()
                .SingleAsync(b => b.Id == preview.Batch.Id)).ImportProfileId);
        }

        await using (var repoint = NewDbContext())
        {
            var pinned = await repoint.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == preview.Batch.Id);
            pinned.ImportProfileId = v6Id;
            await repoint.SaveChangesAsync();
        }

        var batch = await RunAsync(preview.Batch.Id, world.TermId);
        Assert.Equal(0, batch.FailedRows);

        await using var after = NewDbContext();
        Assert.Equal(
            [AcademicKey.SectionPlaceholder],
            await after.Courses.IgnoreQueryFilters().Select(c => c.CodeKey).ToListAsync());
        Assert.Equal(0, await after.Instructors.IgnoreQueryFilters().CountAsync());
    }

    // ===================================================================================== audience

    /// <summary>
    /// <b>A by-course audience reaches students imported with version 7.</b> The Course filter refuses the
    /// section-only placeholder (ADR-010 D-77), so under version 6 no import could feed it; under version 7
    /// the real <c>SSCI 7</c> course resolves to the five students enrolled in it.
    /// </summary>
    [Fact]
    public async Task A_by_course_audience_reaches_students_imported_with_v7()
    {
        var world = await ArrangeAsync();
        var batch = await ImportVersionSevenAsync(world);
        Assert.Equal(0, batch.FailedRows);

        Guid rizalId;
        await using (var db = NewDbContext())
        {
            var rizalKey = AcademicKey.Normalize(SyntheticRoster.RizalCourseCode);
            rizalId = await db.Courses.IgnoreQueryFilters()
                .Where(c => c.SchoolId == world.SchoolId && c.CodeKey == rizalKey)
                .Select(c => c.Id).SingleAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.PostAsJsonAsync(AudienceResolvePath, new
        {
            termId = world.TermId,
            filters = new[] { new { field = "Course", values = new[] { rizalId.ToString() } } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolution = await response.Content.ReadFromJsonAsync<AudienceResolutionDto>();
        Assert.NotNull(resolution);

        await using var read = NewDbContext();
        var reached = await read.Students.IgnoreQueryFilters()
            .Where(s => resolution.StudentIds.Contains(s.Id))
            .Select(s => s.StudentNumber)
            .ToListAsync();

        Assert.Equal(
            new[]
            {
                SyntheticRoster.MariaRegNo, SyntheticRoster.AnaRegNo, SyntheticRoster.PedroRegNo,
                SyntheticRoster.RosaRegNo, SyntheticRoster.LuciaRegNo,
            }.Order(StringComparer.Ordinal),
            reached.Order(StringComparer.Ordinal));
        Assert.Equal(5, resolution.Count);
    }
}
