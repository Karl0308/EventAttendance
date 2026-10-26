using ClosedXML.Excel;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Sis;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-010 D-77 (built-in profile version 5): the roster import is <b>section-only</b>. These are the
/// acceptance criteria that live in the importer, each pinned as a named test so a later clean merge cannot
/// delete one silently (Team X SOP section 8).
///
/// <para>
/// <b>Every expected number was worked out by hand from <see cref="SyntheticRoster.Rows"/>.</b> Under
/// version 5 the twelve rows give: 1 college, 2 programmes, 3 sections (<c>BSCRIM2A</c>, <c>ROTC</c>,
/// <c>BSN1B</c>), 8 students and 9 enrollments, because Maria's rows 2, 3 and 5 are one student in one
/// section and row 4 has a blank section (no offering, no enrollment). Three rows warn: row 4
/// (<c>SectionUnspecified</c>) and rows 12 and 13 (<c>SectionSpansPrograms</c>, the one section under two
/// programmes). The file the template ships is 11 columns; the 18-column file is the pre-version-5 export
/// and is imported too, because an old-format file must read no course or teacher cell.
/// </para>
///
/// <para>
/// The course-carrying side of every comparison is <see cref="CourseMappingProfile"/>, the frozen version 4
/// mapping, never the live template.
/// </para>
///
/// <para>
/// <b>The section-only side is <see cref="SectionOnlyMappingProfile"/>, the frozen version 6 mapping</b>,
/// seeded as the school's current profile by <see cref="ArrangeAsync"/>. Built-in version 7 maps
/// <c>COURSE_CODE</c> again (ADR-014 D-92), so the live template no longer exercises this path; the version
/// numbers in the test names are the mapping each test runs, not the built-in constant.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportSectionOnlyTests : IntegrationTest
{
    public SisImportSectionOnlyTests(SqlServerFixture sql) : base(sql) { }

    // ----------------------------------------------------------- the numbers, by hand (see class doc)

    private const int ExpectedStudents = 8;
    private const int ExpectedSections = 3;
    private const int ExpectedSectionEnrollments = 9;

    // The course-mode (version 4) numbers, for the files imported under the frozen mapping.
    private const int CourseModeCourses = 5;
    private const int CourseModeOfferings = 5;
    private const int CourseModeInstructors = 4;
    private const int CourseModeAssignments = 6;
    private const int CourseModeEnrollments = 11;

    /// <summary>The template's eleven columns (ADR-010 D-77): QA's ten plus RFID.</summary>
    private static readonly IReadOnlyList<string> TemplateColumns =
    [
        SisRosterColumns.RegNo, SisRosterColumns.RfidCardSerial, SisRosterColumns.StudentFirstName,
        SisRosterColumns.StudentMiddleName, SisRosterColumns.StudentLastName, SisRosterColumns.FullName,
        SisRosterColumns.EmailId, SisRosterColumns.UsaEmail, SisRosterColumns.CollegeName,
        SisRosterColumns.Program, SisRosterColumns.SectionName,
    ];

    private static readonly IReadOnlyList<string> TemplateColumnsWithoutRfid =
        [.. TemplateColumns.Where(c => c != SisRosterColumns.RfidCardSerial)];

    /// <summary>The pre-version-5 export: eighteen columns, with the course and teacher ones.</summary>
    private static readonly IReadOnlyList<string> OldFormatColumns = SyntheticRoster.ColumnsWithoutCategories;

    private static readonly string[] CourseAndTeacherColumns =
    [
        SisRosterColumns.CourseCode, SisRosterColumns.CourseName, SisRosterColumns.TeacherFullName,
        SisRosterColumns.TeacherFirstName, SisRosterColumns.TeacherLastName, SisRosterColumns.TeacherSuffix,
        SisRosterColumns.TeacherCollege,
    ];

    /// <summary>
    /// The version the frozen version 4 (course-carrying) mapping is seeded at: below the built-in, so it
    /// can only ever be reached by a batch pinned to it.
    /// </summary>
    private static int PreviousVersion => SisImportProfileTemplate.BuiltInVersion - 1;

    private sealed record World(Guid SchoolId, Guid TermId);

    /// <summary>
    /// School, term and vocabulary, and the frozen version 6 (section-only) mapping as the profile every
    /// upload is pinned to (<see cref="SectionOnlyMappingProfile"/>).
    /// </summary>
    private async Task<World> ArrangeAsync(string schoolCode = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(schoolCode);
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        db.Classifications.AddRange(TestData.ClassificationVocabulary(school.Id));
        await db.SaveChangesAsync();
        await SectionOnlyMappingProfile.SeedSectionOnlyProfileAsync(db, school.Id);
        return new World(school.Id, term.Id);
    }

    private static MemoryStream TemplateFile(IReadOnlyList<string[]>? rows = null) =>
        SyntheticRoster.Build(rows ?? SyntheticRoster.Rows(), TemplateColumns);

    private static MemoryStream OldFormatFile(IReadOnlyList<string[]>? rows = null) =>
        SyntheticRoster.Build(rows ?? SyntheticRoster.Rows(), OldFormatColumns);

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

    private static HashSet<string> EntityTypes(SisImportRowDto row) =>
        [.. row.Entities.Select(e => e.EntityType)];

    /// <summary>
    /// Seeds the frozen version 4 (course-carrying) mapping, superseded (inactive), unless the school
    /// already has it — the state of every school that imported before version 5. Only a batch repointed
    /// at it runs it; the section-only profile stays the one an upload is pinned to.
    /// </summary>
    private async Task<Guid> EnsureCourseProfileAsync(Guid schoolId)
    {
        await using var db = NewDbContext();
        var existing = await db.SisImportProfiles.IgnoreQueryFilters()
            .Where(p => p.SchoolId == schoolId && p.Version == PreviousVersion)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync();
        if (existing is { } id) return id;

        return await CategoryMappingProfile.SeedProfileAsync(
            db, schoolId, PreviousVersion, isActive: false, CourseMappingProfile.Entries);
    }

    /// <summary>
    /// Imports a file the way a batch uploaded before the bump and run after it behaves: upload (which
    /// pins to the then-current profile), repoint the batch at the previous version, run. The repoint is
    /// the same device <c>A_batch_pinned_to_v1_executes_v1s_mapping_and_not_the_live_one</c> uses.
    /// </summary>
    private async Task<SisImportBatchDto> ImportUnderCourseProfileAsync(World world, Stream file)
    {
        var previousId = await EnsureCourseProfileAsync(world.SchoolId);
        var preview = await UploadAsync(world.TermId, file);

        await using (var repoint = NewDbContext())
        {
            var batch = await repoint.SisImportBatches.IgnoreQueryFilters()
                .SingleAsync(b => b.Id == preview.Batch.Id);
            batch.ImportProfileId = previousId;
            await repoint.SaveChangesAsync();
        }

        return await RunAsync(preview.Batch.Id, world.TermId);
    }

    private async Task<List<Course>> PlaceholdersAsync()
    {
        await using var db = NewDbContext();
        return await db.Courses.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.CodeKey == AcademicKey.SectionPlaceholder)
            .ToListAsync();
    }

    // =================================================================================== AC-4 / AC-5

    /// <summary>
    /// <b>AC-4. An upload pins the section-only profile and leaves version 4 untouched.</b> The section-only
    /// mapping maps no <c>Course.*</c> or <c>Instructor.*</c> target, version 4 keeps every one of its rows
    /// in order, and the batch that ran under version 4 still points at it. (The supersession half of the
    /// original AC — an upload deactivating the older active version — is now pinned for the version 6 to 7
    /// bump by <c>SisImportCourseCodeTests.A_batch_pinned_to_v6_stays_section_only</c>.)
    /// </summary>
    [Fact]
    public async Task Upload_pins_section_only_profile_and_leaves_v4_untouched()
    {
        var world = await ArrangeAsync();
        var v4Id = await EnsureCourseProfileAsync(world.SchoolId);

        Guid julyBatchId;
        await using (var db = NewDbContext())
        {
            var july = new SisImportBatch
            {
                SchoolId = world.SchoolId,
                TermId = world.TermId,
                Source = SisImportSource.Excel,
                FileName = "july.xlsx",
                Status = SisImportStatus.Completed,
                ImportProfileId = v4Id,
            };
            db.SisImportBatches.Add(july);
            await db.SaveChangesAsync();
            julyBatchId = july.Id;
        }

        async Task<List<string>> ColumnRowsAsync(Guid profileId)
        {
            await using var db = NewDbContext();
            return await db.SisImportProfileColumns.IgnoreQueryFilters()
                .Where(c => c.ProfileId == profileId)
                .OrderBy(c => c.Ordinal)
                .Select(c => c.Ordinal + "|" + c.SourceColumn + "|" + c.TargetField + "|"
                             + c.NormalizationRule + "|" + c.IsRequired)
                .ToListAsync();
        }

        var v4Before = await ColumnRowsAsync(v4Id);
        Assert.Equal(CourseMappingProfile.Entries.Count, v4Before.Count);
        Assert.Contains(v4Before, r => r.Contains("|Course.CodeKey|", StringComparison.Ordinal));

        await using var file = TemplateFile();
        var preview = await UploadAsync(world.TermId, file);

        await using var read = NewDbContext();
        var profiles = await read.SisImportProfiles.IgnoreQueryFilters().Include(p => p.Columns)
            .Where(p => p.SchoolId == world.SchoolId).OrderBy(p => p.Version).ToListAsync();

        Assert.Equal(
            new[] { PreviousVersion, SisImportProfileTemplate.BuiltInVersion, SectionOnlyMappingProfile.Version },
            profiles.Select(p => p.Version));
        var v4 = profiles.Single(p => p.Id == v4Id);
        var v5 = profiles.Single(p => p.Version == SectionOnlyMappingProfile.Version);
        Assert.False(v4.IsActive);
        Assert.True(v5.IsActive);

        // The section-only mapping maps no course and no teacher. "Course." with the dot: CourseOffering.* is the section.
        Assert.DoesNotContain(v5.Columns, c =>
            c.TargetField.StartsWith("Course.", StringComparison.Ordinal)
            || c.TargetField.StartsWith("Instructor.", StringComparison.Ordinal));
        Assert.Contains(v5.Columns, c => c.TargetField == "CourseOffering.SectionKey");
        Assert.DoesNotContain(v5.Columns, c => CourseAndTeacherColumns.Contains(c.SourceColumn));

        // Superseded, not edited: version 4's rows are byte-for-byte what they were.
        Assert.Equal(v4Before, await ColumnRowsAsync(v4Id));

        // The new batch is on version 5; the old one still explains itself with version 4.
        Assert.Equal(v5.Id, (await read.SisImportBatches.IgnoreQueryFilters()
            .SingleAsync(b => b.Id == preview.Batch.Id)).ImportProfileId);
        Assert.Equal(v4Id, (await read.SisImportBatches.IgnoreQueryFilters()
            .SingleAsync(b => b.Id == julyBatchId)).ImportProfileId);
    }

    /// <summary>
    /// <b>AC-5a. The eleven-column file the template ships uploads and imports.</b> No missing-column
    /// rejection at upload, no failed row at run.
    /// </summary>
    [Fact]
    public async Task Upload_accepts_eleven_column_v5_file()
    {
        var world = await ArrangeAsync();

        await using var file = TemplateFile();
        var preview = await UploadAsync(world.TermId, file);

        Assert.Equal(SyntheticRoster.SheetName, preview.Batch.SourceSheetName);
        Assert.Equal(SyntheticRoster.DataRowCount, preview.Batch.TotalRows);
        Assert.Equal(TemplateColumns.Count, preview.Columns.Count);
        Assert.DoesNotContain(preview.Columns, c => CourseAndTeacherColumns.Contains(c));

        var batch = await RunAsync(preview.Batch.Id, world.TermId);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(9, batch.InsertedRows);
        Assert.Equal(3, batch.SkippedRows);
        Assert.True(batch.CountersReconcile);
    }

    /// <summary>
    /// <b>AC-5b. Shrinking <c>Required</c> did not turn the reader into a pushover.</b> A sheet of names
    /// and e-mails (the shape of the redundant <c>Sheet1</c>) is still not a roster, and an otherwise-full
    /// template missing one of the six still-required headers is refused naming it.
    /// </summary>
    [Fact]
    public async Task Reader_still_rejects_name_email_only_sheet()
    {
        var world = await ArrangeAsync();

        // Names and e-mails only: FULL_NAME, EMAIL_ID, USA_EMAIL, with their real header spellings.
        await using var namesOnly = SyntheticRoster.Build(
            SyntheticRoster.Rows(),
            [SisRosterColumns.FullName, SisRosterColumns.EmailId, SisRosterColumns.UsaEmail]);
        var refused = await Assert.ThrowsAsync<SisImportException>(
            () => UploadAsync(world.TermId, namesOnly));
        foreach (var required in SisRosterColumns.Required)
            Assert.Contains(required, refused.Message, StringComparison.Ordinal);

        // Every required header but SECTION_NAME: refused, and the message names the one that is missing.
        await using var noSection = SyntheticRoster.Build(
            SyntheticRoster.Rows(), [.. TemplateColumns.Where(c => c != SisRosterColumns.SectionName)]);
        var refusedSection = await Assert.ThrowsAsync<SisImportException>(
            () => UploadAsync(world.TermId, noSection));
        Assert.Contains($"missing {SisRosterColumns.SectionName}", refusedSection.Message, StringComparison.Ordinal);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.SisImportBatches.CountAsync());
    }

    // ======================================================================================= AC-6

    /// <summary>
    /// <b>AC-6. A version 5 import writes one section offering per (term, section) and no course or
    /// instructor.</b> The only course is the placeholder; no Instructor or CourseOfferingInstructor row
    /// exists; every offering hangs off the placeholder; and the section groups (not course groups) are
    /// what the projection materializes.
    /// </summary>
    [Fact]
    public async Task V5_import_writes_one_section_offering_per_term_section_and_no_course_or_instructor()
    {
        var world = await ArrangeAsync();

        await using var file = TemplateFile();
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(9, batch.InsertedRows);
        Assert.Equal(3, batch.SkippedRows);
        Assert.Equal(3, batch.WarningRows);
        Assert.Equal(SisImportStatus.CompletedWithWarnings, batch.Status);

        await using var read = NewDbContext();

        var course = await read.Courses.SingleAsync();
        Assert.Equal(AcademicKey.SectionPlaceholder, course.CodeKey);
        Assert.Equal(AcademicKey.SectionPlaceholder, course.Code);
        Assert.Null(course.CollegeId);
        Assert.Null(course.Title);
        Assert.Equal(world.SchoolId, course.SchoolId);

        Assert.Equal(0, await read.Instructors.CountAsync());
        Assert.Equal(0, await read.CourseOfferingInstructors.CountAsync());

        var offerings = await read.CourseOfferings.ToListAsync();
        Assert.Equal(ExpectedSections, offerings.Count);
        Assert.All(offerings, o => Assert.Equal(course.Id, o.CourseId));
        Assert.All(offerings, o => Assert.Equal(world.TermId, o.TermId));
        Assert.Equal(["BSCRIM2A", "BSN1B", "ROTC"], offerings.Select(o => o.SectionKey).Order(StringComparer.Ordinal));

        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
        Assert.Equal(ExpectedSectionEnrollments, await read.Enrollments.CountAsync());
        Assert.Equal(1, await read.Colleges.CountAsync());
        Assert.Equal(2, await read.Programs.CountAsync());

        // The projection ran: a Section group per section, and no Course-type group for any of them.
        var groups = await read.StudentGroups.AsNoTracking()
            .Where(g => g.SourceType == GroupSourceType.Derived).ToListAsync();
        Assert.Equal(ExpectedSections, groups.Count(g => g.SourceEntityType == GroupSourceEntityType.Section));
        Assert.Equal(0, groups.Count(g => g.SourceEntityType == GroupSourceEntityType.CourseOffering));
        Assert.DoesNotContain(groups, g => g.Name.Contains(AcademicKey.SectionPlaceholder, StringComparison.Ordinal));
    }

    /// <summary>
    /// Not listed as an AC, but the property it depends on: the identical v5 import twice changes nothing
    /// and skips every row, so the placeholder and the section offerings are found, not duplicated.
    /// </summary>
    [Fact]
    public async Task Re_running_the_identical_v5_import_changes_nothing_and_skips_every_row()
    {
        var world = await ArrangeAsync();
        await using var file = TemplateFile();
        await ImportAsync(world.TermId, file);

        var second = await ImportAsync(world.TermId, file);

        Assert.Equal(0, second.InsertedRows);
        Assert.Equal(0, second.UpdatedRows);
        Assert.Equal(0, second.FailedRows);
        Assert.Equal(SyntheticRoster.DataRowCount, second.SkippedRows);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.Courses.CountAsync());
        Assert.Equal(ExpectedSections, await read.CourseOfferings.CountAsync());
        Assert.Equal(ExpectedSectionEnrollments, await read.Enrollments.CountAsync());
    }

    // ======================================================================================= AC-7

    /// <summary>
    /// <b>AC-7. One section name under two programmes is one shared offering, and the warning says so.</b>
    /// Rows 12 and 13 put <c>BSN 1-B</c> under two programmes. Under version 4 that warning said nothing
    /// merges (the course was in the key); under version 5 it must say the programmes share one offering,
    /// because they do.
    /// </summary>
    [Fact]
    public async Task Shared_section_key_across_programs_shares_one_offering_and_warns()
    {
        var world = await ArrangeAsync();
        await using var file = TemplateFile();
        var batch = await ImportAsync(world.TermId, file);

        var rows = await RowsOfAsync(batch.Id);
        foreach (var rowNumber in new[] { 12, 13 })
        {
            var row = Row(rows, rowNumber);
            Assert.Equal(SisImportWarningCode.SectionSpansPrograms, row.WarningCode);
            Assert.Equal(SisImportRowResult.Inserted, row.Result);
            Assert.Contains(SyntheticRoster.SecondaryProgram, row.WarningMessage!, StringComparison.Ordinal);

            // The v5 wording: shared offering, sections only. Not the v4 reassurance that nothing merges.
            Assert.Contains("ONE SHARED", row.WarningMessage!, StringComparison.Ordinal);
            Assert.Contains("sections only", row.WarningMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("WARNS rather than", row.WarningMessage!, StringComparison.Ordinal);
        }

        Assert.NotEqual(SisImportWarningCode.SectionSpansPrograms, Row(rows, 2).WarningCode);

        await using var read = NewDbContext();
        var offering = await read.CourseOfferings.SingleAsync(o => o.SectionKey == "BSN1B");
        var enrolled = await read.Enrollments.Where(e => e.CourseOfferingId == offering.Id)
            .Select(e => e.Student!.StudentNumber).ToListAsync();
        Assert.Equal(
            [SyntheticRoster.NinaRegNo, SyntheticRoster.OmarRegNo],
            enrolled.Order(StringComparer.Ordinal));
        Assert.Equal(2, await read.Programs.CountAsync());
    }

    // ======================================================================================= AC-8

    /// <summary>
    /// <b>AC-8. A pre-version-5 file (eighteen columns, all the course and teacher cells filled in)
    /// imported under version 5 reads none of them.</b> Not as a course, not as an instructor, not as a
    /// warning about either: the file's COURSE_CODE and teacher cells are not this batch's business. The
    /// numbers equal the template file's, because the extra columns changed nothing.
    /// </summary>
    [Fact]
    public async Task V4_format_file_under_v5_reads_no_course_or_teacher_cell()
    {
        var world = await ArrangeAsync();

        await using var file = OldFormatFile();
        var preview = await UploadAsync(world.TermId, file);

        // Positive control: the file really does carry the cells this test says are ignored.
        foreach (var column in CourseAndTeacherColumns)
            Assert.Contains(column, preview.Columns);

        var batch = await RunAsync(preview.Batch.Id, world.TermId);
        var rows = await RowsOfAsync(batch.Id);

        // First, the direct evidence that no course or teacher cell was read: the course-mode warnings
        // and skip reason are unreachable (this fixture would raise all three), and no row touched a
        // Course, an Instructor or an assignment.
        Assert.DoesNotContain(rows, r => r.WarningCode is SisImportWarningCode.CourseTitleAlias
            or SisImportWarningCode.CourseCollegeAdopted or SisImportWarningCode.InstructorPlaceholder);
        Assert.DoesNotContain(rows, r => r.SkipReason == SisImportSkipReason.InstructorPlaceholder);
        Assert.All(rows, r => Assert.DoesNotContain(r.Entities, e =>
            e.EntityType is SisImportEntityType.Course or SisImportEntityType.Instructor
                or SisImportEntityType.CourseOfferingInstructor));

        // The repeat rows of a student already in the section are NoChange, as ADR-010 says.
        Assert.Equal(SisImportSkipReason.NoChange, Row(rows, 3).SkipReason);
        Assert.Equal(SisImportSkipReason.NoChange, Row(rows, 5).SkipReason);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(9, batch.InsertedRows);
        Assert.Equal(3, batch.SkippedRows);

        await using var read = NewDbContext();

        // The placeholder and nothing else: none of SSCI7, CA2, GEELECT2, MS32, NSTP2.
        Assert.Equal([AcademicKey.SectionPlaceholder], await read.Courses.Select(c => c.CodeKey).ToListAsync());
        Assert.Equal(0, await read.Instructors.CountAsync());
        Assert.Equal(0, await read.CourseOfferingInstructors.CountAsync());
        Assert.Equal(ExpectedSections, await read.CourseOfferings.CountAsync());
        Assert.Equal(ExpectedSectionEnrollments, await read.Enrollments.CountAsync());
    }

    // ======================================================================================= AC-9

    /// <summary>
    /// <b>AC-9 (ADR-001 D-4). A batch pinned to version 4 and run after the bump still creates courses,
    /// instructors and assignments.</b> The file is the old-format one, uploaded when version 4 was
    /// current; the numbers are the course-mode ones and there is no placeholder.
    /// </summary>
    [Fact]
    public async Task Batch_pinned_to_v4_still_imports_courses_after_bump()
    {
        var world = await ArrangeAsync();

        await using var file = OldFormatFile();
        var batch = await ImportUnderCourseProfileAsync(world, file);

        Assert.Equal(0, batch.FailedRows);

        await using var read = NewDbContext();
        Assert.Equal(CourseModeCourses, await read.Courses.CountAsync());
        Assert.Equal(CourseModeOfferings, await read.CourseOfferings.CountAsync());
        Assert.Equal(CourseModeInstructors, await read.Instructors.CountAsync());
        Assert.Equal(CourseModeAssignments, await read.CourseOfferingInstructors.CountAsync());
        Assert.Equal(CourseModeEnrollments, await read.Enrollments.CountAsync());
        Assert.False(await read.Courses.AnyAsync(c => c.CodeKey == AcademicKey.SectionPlaceholder));
    }

    /// <summary>
    /// ADR-010 D-77, point 3: section-only is a property a <em>profile states</em>, never one inferred from
    /// the absence of a profile. A batch with no pinned profile stays in course mode, matching what
    /// <c>NoMappedColumns</c> has always meant.
    /// </summary>
    [Fact]
    public async Task Batch_with_no_pinned_profile_stays_in_course_mode()
    {
        var world = await ArrangeAsync();

        await using var file = OldFormatFile();
        var preview = await UploadAsync(world.TermId, file);

        await using (var unpin = NewDbContext())
        {
            var batch = await unpin.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == preview.Batch.Id);
            batch.ImportProfileId = null;
            await unpin.SaveChangesAsync();
        }

        var result = await RunAsync(preview.Batch.Id, world.TermId);

        Assert.Equal(0, result.FailedRows);
        await using var read = NewDbContext();
        Assert.Equal(CourseModeCourses, await read.Courses.CountAsync());
        Assert.False(await read.Courses.AnyAsync(c => c.CodeKey == AcademicKey.SectionPlaceholder));
        Assert.Equal(CourseModeInstructors, await read.Instructors.CountAsync());
    }

    // ====================================================================================== AC-10

    /// <summary>
    /// <b>AC-10. No data loss.</b> A school with a roster imported under version 4 re-imports the same
    /// term under version 5. Every v4 enrollment, offering, course, instructor and assignment is still
    /// there with the same id; the v5 import only <em>adds</em> the section offerings and enrollments
    /// (ADR-010 D-77, point 10: a student ends up in both).
    /// </summary>
    [Fact]
    public async Task V5_reimport_same_term_leaves_v4_enrollments_offerings_and_instructors_untouched()
    {
        var world = await ArrangeAsync();

        await using var oldFile = OldFormatFile();
        var v4Batch = await ImportUnderCourseProfileAsync(world, oldFile);
        Assert.Equal(0, v4Batch.FailedRows);

        async Task<Snapshot> SnapshotAsync()
        {
            await using var db = NewDbContext();
            return new Snapshot(
                await db.Enrollments.Select(e => e.Id + "|" + e.StudentId + "|" + e.CourseOfferingId).ToListAsync(),
                await db.CourseOfferings.Select(o => o.Id + "|" + o.CourseId + "|" + o.SectionKey + "|" + o.TermId).ToListAsync(),
                await db.Courses.Select(c => c.Id + "|" + c.CodeKey).ToListAsync(),
                await db.Instructors.Select(i => i.Id + "|" + i.NameKey).ToListAsync(),
                await db.CourseOfferingInstructors
                    .Select(a => a.Id + "|" + a.CourseOfferingId + "|" + a.InstructorId).ToListAsync());
        }

        var before = await SnapshotAsync();

        // Non-vacuous: the v4 world is the course-mode one.
        Assert.Equal(CourseModeEnrollments, before.Enrollments.Count);
        Assert.Equal(CourseModeInstructors, before.Instructors.Count);
        Assert.Equal(CourseModeAssignments, before.Assignments.Count);

        // The bump: the next upload resolves to the built-in version 5, not version 4.
        await using var templateFile = TemplateFile();
        var v5Batch = await ImportAsync(world.TermId, templateFile);
        Assert.Equal(0, v5Batch.FailedRows);

        var after = await SnapshotAsync();

        // Nothing the v4 import wrote was deleted, replaced or re-pointed.
        Assert.Empty(before.Enrollments.Except(after.Enrollments));
        Assert.Empty(before.Offerings.Except(after.Offerings));
        Assert.Empty(before.Courses.Except(after.Courses));
        Assert.Equal(before.Instructors.Order(), after.Instructors.Order());
        Assert.Equal(before.Assignments.Order(), after.Assignments.Order());

        // What v5 added, and only that: the placeholder, three section offerings, nine enrollments.
        Assert.Equal(before.Courses.Count + 1, after.Courses.Count);
        Assert.Equal(before.Offerings.Count + ExpectedSections, after.Offerings.Count);
        Assert.Equal(before.Enrollments.Count + ExpectedSectionEnrollments, after.Enrollments.Count);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.Courses.CountAsync(c => c.CodeKey == AcademicKey.SectionPlaceholder));
        Assert.Equal(CourseModeEnrollments, await read.Enrollments.CountAsync(e => e.CourseOffering!.Course!.CodeKey != AcademicKey.SectionPlaceholder));
    }

    private sealed record Snapshot(
        List<string> Enrollments, List<string> Offerings, List<string> Courses,
        List<string> Instructors, List<string> Assignments);

    // ====================================================================================== AC-16

    /// <summary>
    /// <b>AC-16a. The version 5 import refreshes the student display cache.</b> <c>Students.Course</c> holds
    /// the programme code and <c>Students.Section</c> the section, written through the guarded escape
    /// hatch exactly as before: consumers of the cache (the Students grid, clearance, mobile by-card) see
    /// no difference.
    /// </summary>
    [Fact]
    public async Task V5_import_refreshes_student_course_and_section_cache()
    {
        var world = await ArrangeAsync();
        await using var file = TemplateFile();
        await ImportAsync(world.TermId, file);

        await using var read = NewDbContext();

        var maria = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        Assert.Equal(SyntheticRoster.PrimaryProgram, maria.Course);
        Assert.Equal(SyntheticRoster.PrimarySectionName, maria.Section);
        Assert.NotNull(maria.AcademicCacheUpdatedAt);

        var omar = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.OmarRegNo);
        Assert.Equal(SyntheticRoster.SecondaryProgram, omar.Course);
        Assert.Equal(SyntheticRoster.NursingSectionName, omar.Section);

        // The multi-section student still gets one deterministic cached section and keeps both enrollments.
        var rosa = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.RosaRegNo);
        Assert.Equal(SyntheticRoster.PrimarySectionName, rosa.Section);
        Assert.Equal(2, await read.Enrollments.CountAsync(e => e.StudentId == rosa.Id));

        // And the guard is still armed for everyone else.
        maria.Section = "TAMPERED";
        await Assert.ThrowsAsync<AcademicCacheWriteException>(() => read.SaveChangesAsync());
    }

    // ====================================================================================== AC-17

    /// <summary>
    /// <b>AC-17. A version 5 batch raises no course or instructor code of any kind.</b> The file is the
    /// old-format one, with a course (<c>SSCI 7</c>) already in the database under another college, which
    /// fails six rows with <c>COURSE_COLLEGE_COLLISION</c> under version 4, two titles for one course, and
    /// placeholder teachers. Under version 5 none of it exists. Run under the frozen version 4 mapping
    /// first, in another school, as the control that the file really would raise every one of those codes.
    /// </summary>
    [Fact]
    public async Task V5_batch_raises_no_course_or_instructor_code()
    {
        // SSCI 7 already exists under another college, as in
        // A_course_code_that_already_belongs_to_another_college_fails_rather_than_merging: the arrangement
        // that fails six rows with COURSE_COLLEGE_COLLISION under the course-carrying mapping.
        async Task<Guid> SeedForeignCourseAsync(Guid schoolId)
        {
            await using var db = NewDbContext();
            var nursing = TestData.NewCollege(schoolId, "College of Nursing", "CON");
            db.Colleges.Add(nursing);
            var existing = TestData.NewCourse(
                schoolId, SyntheticRoster.RizalCourseCode, "Rizal (Nursing)", nursing.Id);
            db.Courses.Add(existing);
            await db.SaveChangesAsync();
            return existing.Id;
        }

        // Control: the frozen v4 mapping raises the collision failure and the other course-mode codes.
        var control = await ArrangeAsync("CTL");
        await SeedForeignCourseAsync(control.SchoolId);
        await using (var controlFile = OldFormatFile())
        {
            var v4 = await ImportUnderCourseProfileAsync(control, controlFile);
            var v4Rows = await RowsOfAsync(v4.Id);
            Assert.Equal(6, v4.FailedRows);
            Assert.Contains(v4Rows, r => r.ErrorMessage is not null
                && r.ErrorMessage.StartsWith(SisImportFailureCode.CourseCollegeCollision, StringComparison.Ordinal));
            Assert.Contains(v4Rows, r => r.WarningCode == SisImportWarningCode.CourseTitleAlias);
            Assert.Contains(v4Rows, r => r.WarningCode == SisImportWarningCode.InstructorPlaceholder);
        }

        var world = await ArrangeAsync("USA");
        var existingCourseId = await SeedForeignCourseAsync(world.SchoolId);
        await using var file = OldFormatFile();
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        var rows = await RowsOfAsync(batch.Id);
        Assert.DoesNotContain(rows, r => r.ErrorMessage is not null
            && r.ErrorMessage.Contains(SisImportFailureCode.CourseCollegeCollision, StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.WarningCode is SisImportWarningCode.CourseTitleAlias
            or SisImportWarningCode.CourseCollegeAdopted or SisImportWarningCode.InstructorPlaceholder);
        Assert.DoesNotContain(rows, r => r.SkipReason == SisImportSkipReason.InstructorPlaceholder);

        // The rows the v4 control failed imported: Ana and Pedro, who have only SSCI 7 rows, exist.
        await using var read = NewDbContext();
        Assert.Equal(ExpectedStudents, await read.Students.IgnoreQueryFilters().CountAsync(s => s.SchoolId == world.SchoolId));

        // And the foreign course was never touched: no offering, no new title, still Nursing's.
        var untouched = await read.Courses.IgnoreQueryFilters().SingleAsync(c => c.Id == existingCourseId);
        Assert.Equal("Rizal (Nursing)", untouched.Title);
        Assert.Equal(0, await read.CourseOfferings.IgnoreQueryFilters().CountAsync(o => o.CourseId == existingCourseId));
    }

    // ====================================================================================== AC-18

    /// <summary>
    /// <b>AC-18. A clean version 5 row fans out to exactly seven named entities with a card, six without.</b>
    /// Named, not counted: College, Program, CourseOffering, Student, RfidCard, StudentTermRecord and
    /// Enrollment. Never a Course, an Instructor or a CourseOfferingInstructor (ADR-010 D-77, point 4).
    /// </summary>
    [Fact]
    public async Task Clean_v5_row_fans_out_to_seven_named_entities()
    {
        string[] withCard =
        [
            SisImportEntityType.College, SisImportEntityType.Program, SisImportEntityType.CourseOffering,
            SisImportEntityType.Student, SisImportEntityType.RfidCard, SisImportEntityType.StudentTermRecord,
            SisImportEntityType.Enrollment,
        ];

        // With a card: Maria's row, which has one, in the silent three-row subset.
        var world = await ArrangeAsync();
        await using var cardFile = TemplateFile(SyntheticRoster.RowsWithoutWarnings());
        var batch = await ImportAsync(world.TermId, cardFile);
        Assert.Equal(SisImportStatus.Completed, batch.Status);

        var first = Row(await RowsOfAsync(batch.Id), 2);
        Assert.Equal(7, first.Entities.Count);
        Assert.Equal(withCard.Order(StringComparer.Ordinal), EntityTypes(first).Order(StringComparer.Ordinal));
        Assert.All(first.Entities, e => Assert.Equal(SisImportEntityAction.Inserted, e.Action));

        // Without a card: the same file with no RFID column. Six, and the card is the one that goes.
        var other = await ArrangeAsync("NOCARD");
        await using var noCardFile = SyntheticRoster.Build(SyntheticRoster.RowsWithoutWarnings(), TemplateColumnsWithoutRfid);
        var noCardBatch = await ImportAsync(other.TermId, noCardFile);
        Assert.Equal(SisImportStatus.Completed, noCardBatch.Status);

        var noCard = Row(await RowsOfAsync(noCardBatch.Id), 2);
        Assert.Equal(6, noCard.Entities.Count);
        Assert.Equal(
            withCard.Where(t => t != SisImportEntityType.RfidCard).Order(StringComparer.Ordinal),
            EntityTypes(noCard).Order(StringComparer.Ordinal));
    }

    // ====================================================================================== AC-19

    /// <summary>
    /// <b>AC-19. The placeholder is per school.</b> Two schools import the same file: each gets its own
    /// placeholder course and its own section offerings, no offering references the other school's
    /// course, and a context pinned to one school reads only that school's placeholder.
    /// </summary>
    [Fact]
    public async Task Section_placeholder_course_is_per_school()
    {
        var first = await ArrangeAsync("AAA");
        var second = await ArrangeAsync("ZZZ");

        await using var firstFile = TemplateFile();
        await ImportAsync(first.TermId, firstFile);
        await using var secondFile = TemplateFile();
        await ImportAsync(second.TermId, secondFile);

        var placeholders = await PlaceholdersAsync();
        Assert.Equal(2, placeholders.Count);
        var firstPlaceholder = Assert.Single(placeholders, c => c.SchoolId == first.SchoolId);
        var secondPlaceholder = Assert.Single(placeholders, c => c.SchoolId == second.SchoolId);
        Assert.NotEqual(firstPlaceholder.Id, secondPlaceholder.Id);

        await using (var read = NewDbContext())
        {
            var offerings = await read.CourseOfferings.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            Assert.Equal(ExpectedSections * 2, offerings.Count);
            Assert.All(offerings.Where(o => o.TermId == first.TermId),
                o => Assert.Equal(firstPlaceholder.Id, o.CourseId));
            Assert.All(offerings.Where(o => o.TermId == second.TermId),
                o => Assert.Equal(secondPlaceholder.Id, o.CourseId));
        }

        // Pinned to the second school, the first school's placeholder does not exist.
        School.CurrentSchoolId = second.SchoolId;
        await using var pinned = NewDbContext();
        var visible = await pinned.Courses.AsNoTracking().ToListAsync();
        Assert.Equal(secondPlaceholder.Id, Assert.Single(visible).Id);
        Assert.Equal(ExpectedSections, await pinned.CourseOfferings.CountAsync());
        Assert.Equal(
            ExpectedSections,
            await pinned.CourseOfferings.CountAsync(o => o.CourseId == secondPlaceholder.Id));
    }

    // ====================================================================================== AC-20

    /// <summary>
    /// <b>AC-20. Two first-ever version 5 runs for one school may collide on the placeholder, and the
    /// documented outcome holds either way.</b> ADR-010 D-77 point 11 accepts the race: the loser's batch
    /// is marked <c>Failed</c> and a retry succeeds. The race cannot be forced from a test, so this
    /// asserts the invariants that hold <em>whether or not it fires</em>: any batch that threw is
    /// <c>Failed</c>, a retry of it completes, and at the end there is exactly one
    /// placeholder for the school, every offering hangs off it, and nothing is duplicated. It cannot go
    /// flaky: it never asserts that a collision happened.
    /// </summary>
    [Fact]
    public async Task Concurrent_first_v5_runs_one_placeholder_course()
    {
        var world = await ArrangeAsync();

        var batchIds = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            await using var file = TemplateFile();
            batchIds.Add((await UploadAsync(world.TermId, file)).Batch.Id);
        }

        var outcomes = await Task.WhenAll(batchIds.Select(async id =>
        {
            try
            {
                await RunAsync(id, world.TermId);
                return (Id: id, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Id: id, Error: ex);
            }
        }));

        foreach (var (id, error) in outcomes.Where(o => o.Error is not null))
        {
            await using var db = NewDbContext();
            var failed = await db.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == id);
            Assert.True(
                failed.Status == SisImportStatus.Failed,
                $"A run that threw ({error!.GetType().Name}) must leave its batch Failed, not {failed.Status}.");

            // The retry: a Failed batch can be run again, and now finds the placeholder.
            var retry = await RunAsync(id, world.TermId);
            Assert.Equal(0, retry.FailedRows);
        }

        Assert.Single(await PlaceholdersAsync());

        await using var read = NewDbContext();
        Assert.All(
            await read.SisImportBatches.IgnoreQueryFilters().Where(b => batchIds.Contains(b.Id)).ToListAsync(),
            b => Assert.NotEqual(SisImportStatus.Failed, b.Status));

        var placeholder = await read.Courses.SingleAsync();
        Assert.Equal(ExpectedSections, await read.CourseOfferings.CountAsync(o => o.CourseId == placeholder.Id));
        Assert.Equal(ExpectedSections, await read.CourseOfferings.CountAsync());
        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
        Assert.Equal(ExpectedSectionEnrollments, await read.Enrollments.CountAsync());
    }

    // ====================================================================================== AC-21

    /// <summary>
    /// <b>AC-21. The preview follows the pinned profile.</b> Courses, instructors and placeholder-teacher
    /// rows are 0 under version 5, for the template file and for an old-format file that carries all
    /// of them. The counts that remain are the file's true ones.
    /// </summary>
    [Fact]
    public async Task Preview_under_v5_reports_no_courses_or_instructors()
    {
        var world = await ArrangeAsync();

        foreach (var build in new Func<MemoryStream>[] { () => TemplateFile(), () => OldFormatFile() })
        {
            await using var file = build();
            var preview = await UploadAsync(world.TermId, file);

            Assert.Equal(0, preview.DistinctCourses);
            Assert.Equal(0, preview.DistinctInstructors);
            Assert.Equal(0, preview.PlaceholderInstructorRows);

            Assert.Equal(ExpectedStudents, preview.DistinctStudents);
            Assert.Equal(1, preview.DistinctColleges);
            Assert.Equal(2, preview.DistinctPrograms);
            Assert.Equal(ExpectedSections, preview.DistinctSections);
            Assert.Equal(1, preview.BlankSectionRows);
            Assert.Equal(SisImportStatus.Pending, preview.Batch.Status);
        }

        // Staged, not imported: a preview writes nothing to the academic tables.
        await using var read = NewDbContext();
        Assert.Equal(0, await read.Students.CountAsync());
        Assert.Equal(0, await read.Courses.CountAsync());
    }

    // ====================================================================================== AC-23

    /// <summary>
    /// <b>AC-23. A blank section under version 5 writes no offering and no enrollment, and warns.</b>
    /// The student, the term record and any card still import: five entities with a card (College,
    /// Program, Student, RfidCard, StudentTermRecord), four without. A file of only blank-section rows
    /// creates no placeholder either, because nothing needs one.
    /// </summary>
    [Fact]
    public async Task Blank_section_under_v5_writes_no_enrollment_and_warns()
    {
        List<string[]> BlankSection() =>
        [
            .. SyntheticRoster.RowsWithoutWarnings().Take(1).Select(r =>
            {
                r[SyntheticRoster.ColumnIndex(SisRosterColumns.SectionName)] = "";
                return r;
            }),
        ];

        var world = await ArrangeAsync();
        await using var file = TemplateFile(BlankSection());
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(1, batch.InsertedRows);
        Assert.Equal(1, batch.WarningRows);

        var row = Row(await RowsOfAsync(batch.Id), 2);
        Assert.Equal(SisImportWarningCode.SectionUnspecified, row.WarningCode);
        Assert.Contains("imports sections only", row.WarningMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain(AcademicKey.Unspecified, row.WarningMessage!, StringComparison.Ordinal);

        Assert.Equal(5, row.Entities.Count);
        Assert.Equal(
            new[]
            {
                SisImportEntityType.College, SisImportEntityType.Program, SisImportEntityType.Student,
                SisImportEntityType.RfidCard, SisImportEntityType.StudentTermRecord,
            }.Order(StringComparer.Ordinal),
            EntityTypes(row).Order(StringComparer.Ordinal));

        await using (var read = NewDbContext())
        {
            Assert.Equal(1, await read.Students.CountAsync());
            Assert.Equal(1, await read.StudentTermRecords.CountAsync());
            Assert.Equal(1, await read.RfidCards.CountAsync());
            Assert.Equal(0, await read.Enrollments.CountAsync());
            Assert.Equal(0, await read.CourseOfferings.CountAsync());
            Assert.Equal(0, await read.Courses.CountAsync());
        }

        // Without a card: four.
        var other = await ArrangeAsync("NOCARD");
        await using var noCardFile = SyntheticRoster.Build(BlankSection(), TemplateColumnsWithoutRfid);
        var noCardBatch = await ImportAsync(other.TermId, noCardFile);
        var noCard = Row(await RowsOfAsync(noCardBatch.Id), 2);
        Assert.Equal(4, noCard.Entities.Count);
        Assert.DoesNotContain(noCard.Entities, e => e.EntityType == SisImportEntityType.RfidCard);
    }

    // ====================================================================================== AC-24

    /// <summary>
    /// <b>AC-24, the half that is a test: the importer needs no schema change.</b> The placeholder is an
    /// ordinary <c>Courses</c> row under the existing unique index, and a section-only offering is an
    /// ordinary <c>CourseOfferings</c> row, so the migration-built schema (this suite runs the migrations
    /// against real SQL Server) takes a whole v5 import with no new column. The other half, that
    /// <c>Migrations/</c> has no diff, is a repository fact checked with <c>git</c>, not by a test.
    /// </summary>
    [Fact]
    public async Task V5_import_needs_no_schema_beyond_the_existing_migrations()
    {
        var world = await ArrangeAsync();
        await using var file = TemplateFile();
        var batch = await ImportAsync(world.TermId, file);
        Assert.Equal(0, batch.FailedRows);

        await using var db = NewDbContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
