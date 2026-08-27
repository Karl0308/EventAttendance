using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Sis;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The §10 roster import, end to end against real SQL Server.
///
/// <para>
/// <b>Every expected number in this file was worked out by hand from
/// <see cref="SyntheticRoster.Rows"/>,</b> not computed from the source data by the same rules the
/// pipeline uses. An expectation derived by the implementation's own logic proves only that the code
/// agrees with itself — which is exactly the failure mode of a roster importer, where the output is
/// thousands of rows nobody counts.
/// </para>
///
/// <para>
/// The fixture is generated in memory (see <see cref="SyntheticRoster"/>); the real roster is never
/// committed.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportPipelineTests : IntegrationTest
{
    public SisImportPipelineTests(SqlServerFixture sql) : base(sql) { }

    // Worked out by hand from the twelve fixture rows. See SyntheticRoster for what each row covers.
    private const int ExpectedColleges = 1;
    private const int ExpectedPrograms = 2;      // 'BSci - Crim' and 'BS Criminology'
    private const int ExpectedCourses = 5;       // SSCI7 (two spellings), CA2, GEELECT2, MS32, NSTP2
    private const int ExpectedInstructors = 4;   // four named teachers; the placeholder is not one
    private const int ExpectedOfferings = 5;
    private const int ExpectedAssignments = 6;
    private const int ExpectedStudents = 8;
    private const int ExpectedEnrollments = 11;  // 12 rows, minus the one placeholder duplicate

    private sealed record World(Guid SchoolId, Guid TermId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    private async Task<Guid> AddTermAsync(Guid schoolId, string code)
    {
        await using var db = NewDbContext();
        var term = TestData.NewTerm(schoolId, code, isCurrent: false);
        db.Terms.Add(term);
        await db.SaveChangesAsync();
        return term.Id;
    }

    /// <summary>
    /// Uploads and runs one import, on its own context, and returns the finished batch.
    ///
    /// <para>
    /// A caller-supplied stream is rewound but <b>not</b> disposed, so the same bytes can be imported
    /// twice. That matters for more than tidiness: saving an equivalent workbook twice does not produce
    /// identical bytes — OOXML is a zip, and zip entries carry timestamps — so a test about
    /// <c>FileHash</c> has to reuse one stream rather than build two workbooks.
    /// </para>
    /// </summary>
    private async Task<SisImportBatchDto> ImportAsync(Guid termId, Stream? file = null)
    {
        var owned = file is null ? SyntheticRoster.Build() : null;
        var content = file ?? owned!;

        try
        {
            content.Position = 0;

            await using var db = NewDbContext();
            var service = SisImportOn(db);

            var preview = await service.UploadAsync(
                new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", termId));

            return await service.RunAsync(preview.Batch.Id, termId);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private async Task<List<SisImportRowDto>> RowsOfAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return (await SisImportOn(db).GetRowsAsync(batchId, result: null)).ToList();
    }

    private static SisImportRowDto Row(IEnumerable<SisImportRowDto> rows, int rowNumber) =>
        rows.Single(r => r.RowNumber == rowNumber);

    // ====================================================================== reading the workbook

    /// <summary>
    /// The redundant sheet is skipped <b>at batch level</b>. It is written first in the fixture, so
    /// taking the first worksheet would pick the wrong one; selection is by header content.
    ///
    /// <para>
    /// Skipping it per row instead would stage twelve more rows that exist only to be skipped, double
    /// <c>TotalRows</c>, and bury the one genuine skip among them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Only_the_sheet_carrying_the_roster_columns_is_read()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        await using var content = SyntheticRoster.Build();
        var preview = await SisImportOn(db).UploadAsync(
            new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", world.TermId));

        Assert.Equal(SyntheticRoster.SheetName, preview.Batch.SourceSheetName);
        Assert.Equal(SyntheticRoster.DataRowCount, preview.Batch.TotalRows);
        Assert.Equal(SisRosterColumns.All.Count, preview.Columns.Count);
    }

    /// <summary>
    /// The preview exists so an operator can see the wrong file before it is written, so its counts
    /// have to be the ones they would check. The instructor count excludes the placeholder — reporting
    /// five teachers when one of them is <c>'TO BE ANNOUNCE'</c> is the same lie the pipeline refuses
    /// to write into the <c>Instructors</c> table.
    /// </summary>
    [Fact]
    public async Task The_upload_preview_reports_what_is_in_the_file_without_writing_anything()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        await using var content = SyntheticRoster.Build();
        var preview = await SisImportOn(db).UploadAsync(
            new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", world.TermId));

        Assert.Equal(ExpectedStudents, preview.DistinctStudents);
        Assert.Equal(ExpectedColleges, preview.DistinctColleges);
        Assert.Equal(ExpectedPrograms, preview.DistinctPrograms);
        Assert.Equal(ExpectedCourses, preview.DistinctCourses);
        // BSCRIM2A, ROTC, BSN1B. Three, not four: the row whose section carries a non-breaking space
        // folds into BSCRIM2A, and the blank one is not a section at all. Both of those are the point.
        Assert.Equal(3, preview.DistinctSections);
        Assert.Equal(ExpectedInstructors, preview.DistinctInstructors);
        Assert.Equal(1, preview.BlankSectionRows);
        Assert.Equal(2, preview.PlaceholderInstructorRows);
        Assert.Equal(SisImportStatus.Pending, preview.Batch.Status);

        // Staged, not imported.
        await using var read = NewDbContext();
        Assert.Equal(0, await read.Students.CountAsync());
        Assert.Equal(0, await read.Courses.CountAsync());
        Assert.Equal(SyntheticRoster.DataRowCount, await read.SisImportRows.CountAsync());
        Assert.All(await read.SisImportRows.ToListAsync(),
            r => Assert.Equal(SisImportRowResult.Pending, r.Result));
    }

    // ============================================================================ the first import

    [Fact]
    public async Task One_import_produces_the_dimensions_the_file_describes()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        Assert.Equal(ExpectedColleges, await read.Colleges.CountAsync());
        Assert.Equal(ExpectedPrograms, await read.Programs.CountAsync());
        Assert.Equal(ExpectedCourses, await read.Courses.CountAsync());
        Assert.Equal(ExpectedInstructors, await read.Instructors.CountAsync());
        Assert.Equal(ExpectedOfferings, await read.CourseOfferings.CountAsync());
        Assert.Equal(ExpectedAssignments, await read.CourseOfferingInstructors.CountAsync());
    }

    [Fact]
    public async Task One_import_produces_the_facts_the_file_describes()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
        Assert.Equal(ExpectedStudents, await read.RfidCards.CountAsync());
        Assert.Equal(ExpectedStudents, await read.StudentTermRecords.CountAsync());
        Assert.Equal(ExpectedEnrollments, await read.Enrollments.CountAsync());
        Assert.Equal(1, await read.Terms.CountAsync());
    }

    /// <summary>
    /// ADR-001 D-5's reconciliation, which is the check an operator actually runs to decide whether an
    /// import worked. Before <c>SkippedRows</c> existed this could not balance.
    /// </summary>
    [Fact]
    public async Task The_batch_counters_reconcile_to_the_row_count()
    {
        var world = await ArrangeAsync();
        var batch = await ImportAsync(world.TermId);

        Assert.True(batch.CountersReconcile,
            $"{batch.InsertedRows} + {batch.UpdatedRows} + {batch.FailedRows} + {batch.SkippedRows} " +
            $"!= {batch.TotalRows}");

        Assert.Equal(11, batch.InsertedRows);
        Assert.Equal(0, batch.UpdatedRows);
        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(1, batch.SkippedRows);
        Assert.Equal(6, batch.WarningRows);

        // A warned batch says so in one field, so nobody has to query the row detail of every import
        // to find out whether to look (ADR-001 D-5's open question, answered).
        Assert.Equal(SisImportStatus.CompletedWithWarnings, batch.Status);
    }

    // ================================================================== THE HEADLINE ACCEPTANCE TEST

    /// <summary>
    /// <b>The single assertion this phase exists to make true.</b> Importing the identical file a second
    /// time changes nothing and says so: nothing inserted, nothing updated, nothing failed, and every
    /// row skipped.
    ///
    /// <para>
    /// It is asserted through the same code path as the first run — the outcome is folded from what
    /// each row's entities actually did, not short-circuited by noticing the file hash matched — so it
    /// proves the upsert logic is idempotent rather than proving a fast path exists.
    /// </para>
    ///
    /// <para>
    /// The database counts are asserted alongside the batch counters, because a batch that reports
    /// nothing happened while quietly duplicating enrollments would satisfy the counters alone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Re_running_the_identical_import_changes_nothing_and_skips_every_row()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        var second = await ImportAsync(world.TermId);

        Assert.Equal(0, second.InsertedRows);
        Assert.Equal(0, second.UpdatedRows);
        Assert.Equal(0, second.FailedRows);
        Assert.Equal(SyntheticRoster.DataRowCount, second.SkippedRows);
        Assert.Equal(SyntheticRoster.DataRowCount, second.TotalRows);
        Assert.True(second.CountersReconcile);

        await using var read = NewDbContext();
        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
        Assert.Equal(ExpectedStudents, await read.RfidCards.CountAsync());
        Assert.Equal(ExpectedStudents, await read.StudentTermRecords.CountAsync());
        Assert.Equal(ExpectedEnrollments, await read.Enrollments.CountAsync());
        Assert.Equal(ExpectedCourses, await read.Courses.CountAsync());
        Assert.Equal(ExpectedOfferings, await read.CourseOfferings.CountAsync());
        Assert.Equal(ExpectedAssignments, await read.CourseOfferingInstructors.CountAsync());
        Assert.Equal(ExpectedInstructors, await read.Instructors.CountAsync());
    }

    /// <summary>
    /// And a skip is never a shrug: every one of them names its reason, so "Skipped" on a re-import is
    /// distinguishable from a row that was quietly dropped.
    /// </summary>
    [Fact]
    public async Task Every_skipped_row_of_a_re_import_records_why()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);
        var second = await ImportAsync(world.TermId);

        var rows = await RowsOfAsync(second.Id);

        Assert.All(rows, r => Assert.Equal(SisImportRowResult.Skipped, r.Result));
        Assert.All(rows, r => Assert.NotNull(r.SkipReason));

        // The two placeholder-teacher rows keep the more specific reason.
        Assert.Equal(SisImportSkipReason.InstructorPlaceholder, Row(rows, 3).SkipReason);
        Assert.Equal(SisImportSkipReason.InstructorPlaceholder, Row(rows, 8).SkipReason);
        Assert.Equal(SisImportSkipReason.NoChange, Row(rows, 2).SkipReason);
    }

    /// <summary>
    /// The other half of "upsert, never replace": a genuine change is reported as an update, and only
    /// the rows that carry it.
    /// </summary>
    [Fact]
    public async Task A_corrected_name_in_the_source_produces_an_update_and_not_an_insert()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        var corrected = SyntheticRoster.Rows();
        var lastNameColumn = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.StudentLastName);
        var regNoColumn = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.RegNo);
        foreach (var row in corrected.Where(r => r[regNoColumn] == SyntheticRoster.MariaRegNo))
            row[lastNameColumn] = "Santos-Cruz";

        var second = await ImportAsync(world.TermId, SyntheticRoster.Build(corrected));

        Assert.Equal(0, second.InsertedRows);
        Assert.Equal(1, second.UpdatedRows);
        Assert.Equal(0, second.FailedRows);
        Assert.Equal(SyntheticRoster.DataRowCount - 1, second.SkippedRows);

        await using var read = NewDbContext();
        var maria = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        Assert.Equal("Santos-Cruz", maria.LastName);
        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
    }

    /// <summary>
    /// <b>Upsert-only: a student who disappears from the file is not removed.</b> The roster is a
    /// snapshot of who is in a class, not an instruction to unenrol anyone — an export truncated by a
    /// registrar's filter would otherwise silently empty a term, and every attendance record hanging
    /// off those enrollments would lose its context. Removing a student is a deliberate back-office
    /// action with a person behind it.
    /// </summary>
    [Fact]
    public async Task A_student_missing_from_a_later_file_is_never_deleted_or_unenrolled()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        var regNoColumn = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.RegNo);
        var shortened = SyntheticRoster.Rows()
            .Where(r => r[regNoColumn] != SyntheticRoster.OmarRegNo)
            .ToList();

        var second = await ImportAsync(world.TermId, SyntheticRoster.Build(shortened));

        Assert.Equal(SyntheticRoster.DataRowCount - 1, second.TotalRows);

        await using var read = NewDbContext();
        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
        Assert.Equal(ExpectedEnrollments, await read.Enrollments.CountAsync());

        var omar = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.OmarRegNo);
        Assert.Equal("Active", omar.Status);
        Assert.False(omar.IsDeleted);
        Assert.Equal(1, await read.Enrollments.CountAsync(e => e.StudentId == omar.Id));
    }

    // ==================================================================== a second term, in parallel

    /// <summary>
    /// Importing the same roster under a second term creates a parallel set and leaves the first
    /// exactly as it was — which is what makes ADR-001 D-5's operator-declared <c>TermId</c> worth the
    /// extra field. Students are institution-scoped and are <em>not</em> duplicated; offerings,
    /// enrollments and term records are term-scoped and are.
    /// </summary>
    [Fact]
    public async Task Importing_under_a_second_term_leaves_the_first_untouched_and_adds_no_students()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        Guid[] firstTermEnrollments;
        await using (var before = NewDbContext())
        {
            firstTermEnrollments = await before.Enrollments
                .Where(e => e.CourseOffering!.TermId == world.TermId)
                .Select(e => e.Id)
                .OrderBy(id => id)
                .ToArrayAsync();
        }

        var secondTermId = await AddTermAsync(world.SchoolId, "2025-2026-2");
        var second = await ImportAsync(secondTermId);

        Assert.Equal(SyntheticRoster.DataRowCount - 1, second.InsertedRows);
        Assert.Equal(1, second.SkippedRows);

        await using var read = NewDbContext();

        // Institution-scoped: unchanged.
        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
        Assert.Equal(ExpectedStudents, await read.RfidCards.CountAsync());
        Assert.Equal(ExpectedCourses, await read.Courses.CountAsync());
        Assert.Equal(ExpectedInstructors, await read.Instructors.CountAsync());
        Assert.Equal(ExpectedColleges, await read.Colleges.CountAsync());

        // Term-scoped: doubled, with the first term's rows byte-for-byte where they were.
        Assert.Equal(ExpectedOfferings * 2, await read.CourseOfferings.CountAsync());
        Assert.Equal(ExpectedEnrollments * 2, await read.Enrollments.CountAsync());
        Assert.Equal(ExpectedStudents * 2, await read.StudentTermRecords.CountAsync());

        var stillThere = await read.Enrollments
            .Where(e => e.CourseOffering!.TermId == world.TermId)
            .Select(e => e.Id)
            .OrderBy(id => id)
            .ToArrayAsync();

        Assert.Equal(firstTermEnrollments, stillThere);
    }

    // ======================================================================== normalization rules

    /// <summary>
    /// <c>'CA  2'</c> — the double space is stripped from the key by <see cref="AcademicKey"/> and
    /// folded to one space in the stored display code by <see cref="RosterText"/>.
    /// </summary>
    [Fact]
    public async Task A_double_spaced_course_code_is_folded_in_the_display_value_and_the_key()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();
        var course = await read.Courses.SingleAsync(c => c.CodeKey == "CA2");

        Assert.Equal("CA 2", course.Code);
    }

    /// <summary>
    /// <c>'SSCI 7'</c> and <c>'SSci7'</c> are the same course, and the whole academic layer's key design
    /// exists so this is one row rather than a class split across two.
    /// </summary>
    [Fact]
    public async Task Two_spellings_of_one_course_code_resolve_to_a_single_course_and_offering()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var course = await read.Courses.SingleAsync(c => c.CodeKey == "SSCI7");
        Assert.Equal(SyntheticRoster.RizalCourseCode, course.Code); // first seen wins the display form

        var offerings = await read.CourseOfferings.CountAsync(o => o.CourseId == course.Id);
        Assert.Equal(1, offerings);
    }

    /// <summary>
    /// The middle-name placeholder. 200 of the real file's rows carry <c>'-'</c>; stored as written,
    /// <c>Student.FullName</c> renders "Maria - Santos" for two-fifths of the roster.
    /// </summary>
    [Fact]
    public async Task Placeholder_and_blank_middle_names_become_null_and_real_initials_survive()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var dashed = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        Assert.Null(dashed.MiddleName);
        Assert.Equal("Maria Santos", dashed.FullName);

        var blank = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.JuanRegNo);
        Assert.Null(blank.MiddleName);

        var initial = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.AnaRegNo);
        Assert.Equal(SyntheticRoster.MiddleInitial, initial.MiddleName);
    }

    /// <summary>
    /// The honorific never reaches the stored name and therefore never reaches the key — otherwise
    /// <c>'SR. CLARA BENITEZ MORALES'</c> and <c>'CLARA BENITEZ MORALES'</c> would be two teachers.
    /// </summary>
    [Fact]
    public async Task A_teachers_honorific_is_kept_out_of_the_stored_name_and_the_generational_suffix_is_kept_in()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();
        var instructors = await read.Instructors.ToListAsync();

        var sister = instructors.Single(i => i.NameKey == "CLARABENITEZMORALES");
        Assert.Equal(SyntheticRoster.HonorificTeacherExpectedDisplayName, sister.DisplayName);
        Assert.DoesNotContain("SR.", sister.DisplayName, StringComparison.OrdinalIgnoreCase);

        var third = instructors.Single(i => i.DisplayName == SyntheticRoster.GenerationalTeacherFullName);
        Assert.Contains("III", third.DisplayName, StringComparison.Ordinal);

        // No honorific from the TEACHER SUFFIX column reached any name.
        Assert.All(instructors, i =>
        {
            Assert.DoesNotContain("Ms.", i.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Mrs.", i.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Mr.", i.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// <b>The placeholder creates no instructor row.</b> A synthetic "TBA" teacher would be the largest
    /// instructor in the institution — 66 of the real file's 536 rows — and would top every report
    /// built on <c>CourseOfferingInstructors</c>. The honest representation of "no teacher named yet"
    /// is an offering with no assignment, which the many-to-many junction makes representable.
    /// </summary>
    [Fact]
    public async Task The_placeholder_teacher_creates_no_instructor_and_leaves_the_offering_unstaffed()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        Assert.Equal(0, await read.Instructors
            .CountAsync(i => i.NameKey == TeacherNames.PlaceholderKey));
        Assert.Equal(ExpectedInstructors, await read.Instructors.CountAsync());
        Assert.DoesNotContain(
            await read.Instructors.Select(i => i.DisplayName).ToListAsync(),
            n => n.Contains("ANNOUNCE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A blank section is a real offering under the <see cref="AcademicKey.Unspecified"/> sentinel, not
    /// a dropped row, and the row that produced it is warned so an operator knows those students get no
    /// section group.
    /// </summary>
    [Fact]
    public async Task A_blank_section_becomes_the_unspecified_sentinel_and_warns()
    {
        var world = await ArrangeAsync();
        var batch = await ImportAsync(world.TermId);

        await using var read = NewDbContext();
        var offering = await read.CourseOfferings
            .SingleAsync(o => o.SectionKey == AcademicKey.Unspecified);

        Assert.Null(offering.SectionName);
        Assert.Equal(1, await read.Enrollments.CountAsync(e => e.CourseOfferingId == offering.Id));

        var rows = await RowsOfAsync(batch.Id);
        Assert.Equal(SisImportWarningCode.SectionUnspecified, Row(rows, 4).WarningCode);
        Assert.Equal(SisImportRowResult.Inserted, Row(rows, 4).Result);
    }

    /// <summary>
    /// <c>USA_EMAIL</c> is what the institution controls and can vouch for, so it is the address the
    /// system will send to and match on. <c>EMAIL_ID</c> is personal, kept, and never a key.
    /// </summary>
    [Fact]
    public async Task The_institutional_email_becomes_the_primary_and_the_personal_one_the_alternate()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();
        var maria = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);

        Assert.Equal("maria.santos@usa.edu.ph", maria.Email);
        Assert.Equal("maria.santos@gmail.com", maria.AlternateEmail);

        var noPersonal = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.JuanRegNo);
        Assert.Equal("juan.cruz@usa.edu.ph", noPersonal.Email);
        Assert.Null(noPersonal.AlternateEmail);
    }

    /// <summary>
    /// Invisible characters are stripped before anything is keyed, and the case that matters is the
    /// section: had the non-breaking space survived, this student would have landed in an offering of
    /// their own — a section of one, with no error anywhere.
    /// </summary>
    [Fact]
    public async Task Zero_width_and_non_breaking_characters_are_stripped_before_anything_is_keyed()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var lucia = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.LuciaRegNo);
        Assert.Equal(SyntheticRoster.LuciaFirstName, lucia.FirstName);
        Assert.Equal(SyntheticRoster.LuciaLastName, lucia.LastName);

        // The point of the test: same offering as everyone else in BSCRIM 2-A.
        var rizal = await read.Courses.SingleAsync(c => c.CodeKey == "SSCI7");
        var offering = await read.CourseOfferings.SingleAsync(o => o.CourseId == rizal.Id);
        Assert.Equal("BSCRIM2A", offering.SectionKey);
        Assert.Equal(1, await read.Enrollments
            .CountAsync(e => e.StudentId == lucia.Id && e.CourseOfferingId == offering.Id));
    }

    /// <summary>
    /// <b>REGNO is stored verbatim in both shapes, and the card UID comes from the RFID column — not
    /// from REGNO.</b>
    ///
    /// <para>
    /// Until 2026-07-30 this pipeline derived <c>RfidCards.CardUid</c> from REGNO, and this test
    /// asserted the two were equal. The client corrected us: they are separate columns, and the serial
    /// is the physical card's own identity. The REGNO half of the old assertion survives unchanged — 51
    /// are <c>USA#####</c> text, one is a ten-digit number Excel stores numerically, and neither is
    /// reformatted into the other or arrives as <c>2.021005781E+09</c>. What is rewritten is the claim
    /// that the card carries the same value.
    /// </para>
    ///
    /// <para>
    /// <b>The leading zeros are the point of the card half.</b> <c>0012503326</c> is a different card
    /// from <c>12503326</c>, and a normalizer or a cell reader that dropped the zeros would produce a
    /// UID no reader can ever match — a student who simply never registers a tap, with nothing anywhere
    /// to say why. The negative assertion is what makes it falsifiable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Regno_is_stored_verbatim_and_the_card_uid_comes_from_the_rfid_column()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var legacy = await read.Students
            .SingleAsync(s => s.StudentNumber == SyntheticRoster.PedroRegNo);
        Assert.Equal("2021005781", legacy.StudentNumber);

        var legacyCard = await read.RfidCards.SingleAsync(c => c.StudentId == legacy.Id);
        Assert.Equal(SyntheticRoster.PedroRfid, legacyCard.CardUid);
        Assert.True(legacyCard.IsActive);
        Assert.Equal(world.SchoolId, legacyCard.SchoolId); // ADR-001 D-3 denormalization

        var modern = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        Assert.Equal("USA00001", modern.StudentNumber);

        var modernCard = await read.RfidCards.SingleAsync(c => c.StudentId == modern.Id);
        Assert.Equal(SyntheticRoster.MariaRfid, modernCard.CardUid);

        // The separation, stated as a negative: no card carries a student number, and no student number
        // carries a serial. This is the assertion that fails the moment anything re-derives one from the
        // other.
        var studentNumbers = await read.Students.Select(s => s.StudentNumber).ToListAsync();
        Assert.All(await read.RfidCards.ToListAsync(),
            c => Assert.DoesNotContain(c.CardUid, studentNumbers));

        // Leading zeros survived the cell read, RosterText.Clean and CardUid.Normalize.
        Assert.StartsWith("00", modernCard.CardUid, StringComparison.Ordinal);
        Assert.Equal(0, await read.RfidCards
            .CountAsync(c => c.CardUid == SyntheticRoster.MariaRfidWithLeadingZerosLost));

        Assert.All(await read.RfidCards.ToListAsync(),
            c => Assert.DoesNotContain("E+", c.CardUid, StringComparison.OrdinalIgnoreCase));
    }

    // ====================================================== one code, two titles: alias with a warning

    /// <summary>
    /// <c>'GE Elect 2'</c> carries two titles. One course row on the key, first-seen title kept, the
    /// other reported as an alias — and the row that carried it <b>still imports</b>. Merging without a
    /// trace is what this refuses to do; refusing the row would be worse.
    /// </summary>
    [Fact]
    public async Task One_course_code_with_two_titles_keeps_the_first_and_warns_without_losing_the_row()
    {
        var world = await ArrangeAsync();
        var batch = await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var elective = await read.Courses.SingleAsync(c => c.CodeKey == "GEELECT2");
        Assert.Equal(SyntheticRoster.ElectiveTitleFirstSeen, elective.Title);

        var rows = await RowsOfAsync(batch.Id);
        var aliasRow = Row(rows, 6);

        Assert.Equal(SisImportWarningCode.CourseTitleAlias, aliasRow.WarningCode);
        Assert.Contains(SyntheticRoster.ElectiveTitleAlias, aliasRow.WarningMessage!, StringComparison.Ordinal);
        Assert.Contains(SyntheticRoster.ElectiveTitleFirstSeen, aliasRow.WarningMessage!, StringComparison.Ordinal);

        // The row imported: warnings ride alongside the outcome, they do not replace it.
        Assert.Equal(SisImportRowResult.Inserted, aliasRow.Result);

        // And the discarded title is still recoverable from the row's own verbatim copy.
        Assert.Contains(SyntheticRoster.ElectiveTitleAlias, aliasRow.RawData!, StringComparison.Ordinal);
    }

    // ============================================== the placeholder "duplicates" that are not duplicates

    /// <summary>
    /// <b>The 27 <c>(REGNO, COURSE_CODE)</c> pairs in the real file are one enrollment described
    /// twice</b> — once with a teacher, once with the placeholder — because the source's grain includes
    /// the teacher and an enrollment's does not. Against this schema the teacher belongs to the
    /// <em>offering</em>, so the pair produces one enrollment plus at most one assignment, and the
    /// placeholder row correctly asks for nothing and says so.
    /// </summary>
    [Fact]
    public async Task A_placeholder_partner_row_is_one_enrollment_not_a_duplicate()
    {
        var world = await ArrangeAsync();
        var batch = await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var maria = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        var rizal = await read.Courses.SingleAsync(c => c.CodeKey == "SSCI7");
        var offering = await read.CourseOfferings.SingleAsync(o => o.CourseId == rizal.Id);

        Assert.Equal(1, await read.Enrollments
            .CountAsync(e => e.StudentId == maria.Id && e.CourseOfferingId == offering.Id));
        Assert.Equal(1, await read.CourseOfferingInstructors
            .CountAsync(a => a.CourseOfferingId == offering.Id));

        var rows = await RowsOfAsync(batch.Id);

        Assert.Equal(SisImportRowResult.Inserted, Row(rows, 2).Result);          // the real teacher
        Assert.Equal(SisImportRowResult.Skipped, Row(rows, 3).Result);           // the placeholder
        Assert.Equal(SisImportSkipReason.InstructorPlaceholder, Row(rows, 3).SkipReason);
        Assert.Equal(SisImportWarningCode.InstructorPlaceholder, Row(rows, 3).WarningCode);
    }

    /// <summary>
    /// The mirror case, and the reason the placeholder suppresses the <em>instructor</em> rather than
    /// the row: 39 of the real file's 66 placeholder rows are the only row for their enrollment. Losing
    /// them would lose 39 real enrollments.
    /// </summary>
    [Fact]
    public async Task A_placeholder_row_that_is_the_only_row_for_its_enrollment_still_imports()
    {
        var world = await ArrangeAsync();
        var batch = await ImportAsync(world.TermId);

        await using var read = NewDbContext();
        var pedro = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.PedroRegNo);
        Assert.Equal(1, await read.Enrollments.CountAsync(e => e.StudentId == pedro.Id));

        var rows = await RowsOfAsync(batch.Id);
        Assert.Equal(SisImportRowResult.Inserted, Row(rows, 8).Result);
        Assert.Equal(SisImportWarningCode.InstructorPlaceholder, Row(rows, 8).WarningCode);
    }

    // ======================================================================== collision detection

    /// <summary>
    /// <b>The Phase 1 review's hard requirement.</b> <c>Courses</c> is keyed
    /// <c>UNIQUE(SchoolId, CodeKey)</c> — institution-wide — and <c>CollegeId</c> is single-valued, so
    /// resolving a code to a course belonging to a different college merges two colleges' courses with
    /// no way back: the column cannot hold both values, and un-merging means re-keying every offering,
    /// enrollment and attendance record hanging off it.
    ///
    /// <para>
    /// So the rows fail, loudly, naming both colleges — and the existing course is left exactly as it
    /// was. Every row naming the code fails, not just the first, because importing some of them would
    /// leave a course half-populated under a college it may not belong to.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_course_code_that_already_belongs_to_another_college_fails_rather_than_merging()
    {
        var world = await ArrangeAsync();

        Guid otherCollegeId, existingCourseId;
        await using (var arrange = NewDbContext())
        {
            var otherCollege = TestData.NewCollege(world.SchoolId, "College of Nursing", "CON");
            arrange.Colleges.Add(otherCollege);
            var existing = TestData.NewCourse(
                world.SchoolId, SyntheticRoster.RizalCourseCode, "Rizal (Nursing)", otherCollege.Id);
            arrange.Courses.Add(existing);
            await arrange.SaveChangesAsync();
            otherCollegeId = otherCollege.Id;
            existingCourseId = existing.Id;
        }

        var batch = await ImportAsync(world.TermId);

        // Six fixture rows name SSCI7 (in both spellings); all six fail.
        Assert.Equal(6, batch.FailedRows);
        Assert.Equal(6, batch.InsertedRows);
        Assert.Equal(0, batch.SkippedRows);
        Assert.True(batch.CountersReconcile);
        Assert.Equal(SisImportStatus.CompletedWithErrors, batch.Status);

        var rows = await RowsOfAsync(batch.Id);
        var failed = rows.Where(r => r.Result == SisImportRowResult.Failed).ToList();
        Assert.Equal(6, failed.Count);
        Assert.All(failed, r => Assert.StartsWith(
            SisImportFailureCode.CourseCollegeCollision, r.ErrorMessage!, StringComparison.Ordinal));
        Assert.All(failed, r => Assert.Contains(
            SyntheticRoster.CollegeName, r.ErrorMessage!, StringComparison.Ordinal));

        await using var read = NewDbContext();

        // Never silently resolved to the existing row: it is untouched, and no offering or enrollment
        // was attached to it.
        var untouched = await read.Courses.SingleAsync(c => c.Id == existingCourseId);
        Assert.Equal(otherCollegeId, untouched.CollegeId);
        Assert.Equal("Rizal (Nursing)", untouched.Title);
        Assert.Equal(0, await read.CourseOfferings.CountAsync(o => o.CourseId == existingCourseId));

        // The three students whose only row named that course were never created — a failed row is
        // genuinely not imported, rather than partially imported.
        Assert.Equal(5, await read.Students.CountAsync());
        Assert.Equal(0, await read.Students.CountAsync(s => s.StudentNumber == SyntheticRoster.AnaRegNo));
    }

    /// <summary>
    /// The section half of the same requirement, and it warns where the course case fails.
    ///
    /// <para>
    /// The asymmetry is deliberate: <c>CourseOfferings</c> is keyed <c>(TermId, CourseId, SectionKey)</c>
    /// with the course <em>in</em> the key, so two programmes sharing a section name only collide when
    /// they are also the same course — usually genuinely the same class. What is not acceptable is
    /// silence: a section name shared across programmes is the leading indicator that the roster has
    /// outgrown institution-wide keys.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_section_name_used_by_two_programmes_warns_and_does_not_merge_anything()
    {
        var world = await ArrangeAsync();
        var batch = await ImportAsync(world.TermId);

        var rows = await RowsOfAsync(batch.Id);

        foreach (var rowNumber in new[] { 12, 13 })
        {
            Assert.Equal(SisImportWarningCode.SectionSpansPrograms, Row(rows, rowNumber).WarningCode);
            Assert.Equal(SisImportRowResult.Inserted, Row(rows, rowNumber).Result);
            Assert.Contains(SyntheticRoster.SecondaryProgram,
                Row(rows, rowNumber).WarningMessage!, StringComparison.Ordinal);
        }

        // Rows whose section belongs to one programme are not warned about it.
        Assert.NotEqual(SisImportWarningCode.SectionSpansPrograms, Row(rows, 2).WarningCode);

        await using var read = NewDbContext();
        var nstp = await read.Courses.SingleAsync(c => c.CodeKey == "NSTP2");
        Assert.Equal(1, await read.CourseOfferings.CountAsync(o => o.CourseId == nstp.Id));
        Assert.Equal(2, await read.Programs.CountAsync());
    }

    // ================================================================== the derived student cache

    /// <summary>
    /// The importer is the writer the ADR-001 D-2 <c>SaveChanges</c> guard was built for — the source
    /// column is sitting right there in the row — and it is the first caller of
    /// <c>BeginAcademicCacheRefresh</c>. This proves the cache is written <em>and</em> that it is
    /// written through the sanctioned door rather than around it.
    /// </summary>
    [Fact]
    public async Task The_derived_student_cache_is_refreshed_through_the_guarded_escape_hatch()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();
        var maria = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);

        Assert.Equal(SyntheticRoster.PrimaryProgram, maria.Course);
        Assert.Equal(SyntheticRoster.PrimarySectionName, maria.Section);
        Assert.NotNull(maria.AcademicCacheUpdatedAt);

        // The cache follows the programme, not the college.
        var omar = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.OmarRegNo);
        Assert.Equal(SyntheticRoster.SecondaryProgram, omar.Course);
        Assert.Equal(SyntheticRoster.NursingSectionName, omar.Section);

        // And the guard is still armed for everyone else: a write outside the refresh scope throws.
        maria.Section = "TAMPERED";
        await Assert.ThrowsAsync<AcademicCacheWriteException>(() => read.SaveChangesAsync());
    }

    /// <summary>
    /// A student in two sections gets one cached section — the cache is lossy by construction (D-2) —
    /// but deterministically, and the authoritative answer stays in <c>Enrollments</c>. The tie between
    /// this student's two single-appearance sections is broken by the lower key ordinally, so two runs
    /// over the same file can never disagree.
    /// </summary>
    [Fact]
    public async Task A_multi_section_student_gets_one_deterministic_cached_section_and_keeps_both_enrollments()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();
        var rosa = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.RosaRegNo);

        Assert.Equal(SyntheticRoster.PrimarySectionName, rosa.Section); // BSCRIM2A < ROTC, ordinally
        Assert.Equal(2, await read.Enrollments.CountAsync(e => e.StudentId == rosa.Id));

        var record = await read.StudentTermRecords
            .SingleAsync(r => r.StudentId == rosa.Id && r.TermId == world.TermId);
        Assert.Equal("BSCRIM2A", record.HomeSectionKey);

        // No year level is invented from the section name: the export has no such column.
        Assert.Null(record.YearLevel);
    }

    // ============================================================== the projection, and the fan-out

    /// <summary>
    /// The third pass calls the existing idempotent projection rather than reimplementing it, so an
    /// imported roster is immediately usable as an event audience (§4.8) with nothing downstream
    /// learning that the academic tables exist.
    /// </summary>
    [Fact]
    public async Task The_import_projects_the_terms_student_groups()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var section = await read.StudentGroups
            .Include(g => g.Members)
            .SingleAsync(g => g.SourceEntityType == GroupSourceEntityType.Section
                           && g.SourceKey == "BSCRIM2A");

        // Everyone enrolled in any BSCRIM 2-A offering: Maria, Juan, Ana, Pedro, Rosa, Lucia.
        Assert.Equal(6, section.Members.Count);
        Assert.All(section.Members, m => Assert.Equal(GroupSourceType.Derived, m.SourceType));
        Assert.Contains(SyntheticRoster.PrimarySectionName, section.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row-level fan-out — one of the three tables ADR-001 D-1 counted and Phase 1 did not land.
    /// §4.12's lone nullable <c>StudentId</c> can name one of the ten entities a row touches; this is
    /// the answer to "row 3 says Skipped, against what?".
    /// </summary>
    [Fact]
    public async Task Every_entity_a_row_touched_is_recorded_with_what_it_did_to_it()
    {
        var world = await ArrangeAsync();
        var batch = await ImportAsync(world.TermId);

        var rows = await RowsOfAsync(batch.Id);

        // Row 2 creates the world: college, programme, course, offering, instructor, assignment,
        // student, card, term record, enrollment.
        var first = Row(rows, 2);
        Assert.Equal(10, first.Entities.Count);
        Assert.All(first.Entities, e => Assert.Equal(SisImportEntityAction.Inserted, e.Action));
        Assert.Contains(first.Entities, e => e.EntityType == SisImportEntityType.Course);
        Assert.Contains(first.Entities, e => e.EntityType == SisImportEntityType.RfidCard);
        Assert.NotNull(first.StudentId);

        // Row 3 names the same things and creates none of them — Unchanged is recorded rather than
        // omitted, which is the positive statement that makes a re-import's proof possible.
        var placeholder = Row(rows, 3);
        Assert.All(placeholder.Entities, e => Assert.Equal(SisImportEntityAction.Unchanged, e.Action));
        Assert.DoesNotContain(placeholder.Entities,
            e => e.EntityType == SisImportEntityType.Instructor);
        Assert.Equal(first.StudentId, placeholder.StudentId);
    }

    // ============================================================================ operator guards

    [Fact]
    public async Task Running_a_batch_under_a_different_term_than_it_was_uploaded_for_is_refused()
    {
        var world = await ArrangeAsync();
        var otherTermId = await AddTermAsync(world.SchoolId, "2025-2026-2");

        await using var db = NewDbContext();
        var service = SisImportOn(db);
        await using var content = SyntheticRoster.Build();
        var preview = await service.UploadAsync(
            new SisImportUploadRequest(content, "roster.xlsx", world.TermId));

        var refused = await Assert.ThrowsAsync<SisImportException>(
            () => service.RunAsync(preview.Batch.Id, otherTermId));

        Assert.Contains("term", refused.Message, StringComparison.OrdinalIgnoreCase);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.Students.CountAsync());
    }

    /// <summary>
    /// A completed batch is the record of what that run did, not a re-runnable script. Re-importing is
    /// uploading the file again, which is what the headline idempotency test does.
    /// </summary>
    [Fact]
    public async Task A_completed_batch_cannot_be_run_again()
    {
        var world = await ArrangeAsync();
        var batch = await ImportAsync(world.TermId);

        await using var db = NewDbContext();
        var refused = await Assert.ThrowsAsync<SisImportException>(
            () => SisImportOn(db).RunAsync(batch.Id, world.TermId));

        Assert.Contains("already been run", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uploading_against_a_term_that_does_not_exist_is_refused_before_anything_is_staged()
    {
        await ArrangeAsync();

        await using var db = NewDbContext();
        await using var content = SyntheticRoster.Build();

        await Assert.ThrowsAsync<SisImportException>(() => SisImportOn(db).UploadAsync(
            new SisImportUploadRequest(content, "roster.xlsx", Guid.NewGuid())));

        await using var read = NewDbContext();
        Assert.Equal(0, await read.SisImportBatches.CountAsync());
    }

    [Fact]
    public async Task A_workbook_with_no_roster_sheet_is_refused_with_the_columns_it_wanted()
    {
        var world = await ArrangeAsync();

        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var sheet = workbook.AddWorksheet("Something Else");
        sheet.Cell(1, 1).Value = "NAME";
        sheet.Cell(2, 1).Value = "Maria";
        await using var content = new MemoryStream();
        workbook.SaveAs(content);
        content.Position = 0;

        await using var db = NewDbContext();
        var refused = await Assert.ThrowsAsync<SisImportException>(() => SisImportOn(db).UploadAsync(
            new SisImportUploadRequest(content, "wrong.xlsx", world.TermId)));

        Assert.Contains(SisRosterColumns.RegNo, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Something Else", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unrecognised row filter returns nothing rather than everything: <c>?result=failure</c>
    /// silently returning all twelve rows is how an operator concludes a clean import failed entirely.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_row_filter_returns_nothing_rather_than_everything()
    {
        var world = await ArrangeAsync();
        var batch = await ImportAsync(world.TermId);

        await using var db = NewDbContext();
        var service = SisImportOn(db);

        Assert.Empty(await service.GetRowsAsync(batch.Id, "failure"));
        Assert.Single(await service.GetRowsAsync(batch.Id, "skipped"));   // matched case-insensitively
        Assert.Equal(SyntheticRoster.DataRowCount, (await service.GetRowsAsync(batch.Id, null)).Count);
    }

    // =============================================================== ADR-001 D-4 versioned profile

    /// <summary>
    /// Every batch points at the mapping version it executed under, and that version's rules are rows
    /// rather than a string in <c>SystemSettings</c>. Without this D-4 would be an empty table and
    /// batches would point at nothing — the exact state it was written to end.
    /// </summary>
    [Fact]
    public async Task Each_batch_records_the_versioned_mapping_it_ran_under()
    {
        var world = await ArrangeAsync();

        // One stream, imported twice, so the two batches really are the same bytes — see ImportAsync.
        await using var file = SyntheticRoster.Build();
        await ImportAsync(world.TermId, file);
        await ImportAsync(world.TermId, file);

        await using var read = NewDbContext();

        var profiles = await read.SisImportProfiles.Include(p => p.Columns).ToListAsync();
        var profile = Assert.Single(profiles);          // idempotent: the second upload reuses the version
        Assert.True(profile.IsActive);
        Assert.NotEmpty(profile.Columns);

        // REGNO maps to the student number and to nothing else. Until 2026-07-30 it also fed
        // RfidCard.CardUid and this asserted so; the client corrected us that the serial is its own
        // column, and this is the surviving rule.
        var regNoRules = profile.Columns
            .Where(c => c.SourceColumnKey == SisRosterColumns.HeaderKey(SisRosterColumns.RegNo))
            .ToList();
        var regNoRule = Assert.Single(regNoRules);
        Assert.Equal("Student.StudentNumber", regNoRule.TargetField);
        Assert.True(regNoRule.IsRequired);

        // The card UID is mapped, from the RFID column, and optionally — a file without that column
        // must still import. This row is the seam: the pipeline finds the RFID source column by looking
        // this TargetField up here, so a client export naming the column something else is a new profile
        // version rather than a code change.
        var cardRule = Assert.Single(profile.Columns, c => c.TargetField == "RfidCard.CardUid");
        Assert.Equal(
            SisRosterColumns.HeaderKey(SisRosterColumns.RfidCardSerial), cardRule.SourceColumnKey);
        Assert.False(cardRule.IsRequired);
        Assert.Equal("CardUid.Normalize", cardRule.NormalizationRule);

        var batches = await read.SisImportBatches.ToListAsync();
        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.Equal(profile.Id, b.ImportProfileId));
        Assert.All(batches, b => Assert.NotNull(b.FileHash));

        // Same bytes, same fingerprint. Recorded for future use only — nothing reads FileHash, and in
        // particular no fast path skips a matching re-import; idempotency comes from the upserts, which
        // is the stronger claim. This asserts the column is populated and stable, not that anything
        // depends on it.
        Assert.Single(batches.Select(b => b.FileHash).Distinct());
    }

    // ================== CRITICAL: v1 → v2 supersession, the path that runs on every existing database

    /// <summary>One row of a mapping, as it is authored rather than as it is stored.</summary>
    private sealed record MappingRow(
        string SourceColumn, string TargetField, string NormalizationRule, bool IsRequired);

    /// <summary>
    /// <b>Version 1 of the built-in mapping, spelled out rather than derived from
    /// <see cref="SisImportProfileTemplate"/>.</b> The template <em>is</em> version 2 now, so building
    /// version 1 out of it would make this fixture say whatever today's code says — and the whole point
    /// of a versioned profile is that the superseded version keeps saying what it said before the
    /// correction.
    ///
    /// <para>
    /// The second row is the one that matters: <c>REGNO → RfidCard.CardUid</c>, <b>required</b>. That
    /// was the mapping the client corrected on 2026-07-30, and it is the row every database that
    /// already has data is still holding.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<MappingRow> VersionOneMapping =
    [
        new(SisRosterColumns.RegNo, "Student.StudentNumber", "RosterText.Clean", IsRequired: true),
        new(SisRosterColumns.RegNo, "RfidCard.CardUid", "CardUid.Normalize", IsRequired: true),

        new(SisRosterColumns.StudentFirstName, "Student.FirstName", "RosterText.CleanName", true),
        new(SisRosterColumns.StudentMiddleName, "Student.MiddleName", "RosterText.CleanName", false),
        new(SisRosterColumns.StudentLastName, "Student.LastName", "RosterText.CleanName", true),
        new(SisRosterColumns.FullName, "(none)", "(not imported)", false),

        new(SisRosterColumns.EmailId, "Student.AlternateEmail", "RosterText.CleanEmail", false),
        new(SisRosterColumns.UsaEmail, "Student.Email", "RosterText.CleanEmail", false),

        new(SisRosterColumns.CollegeName, "College.Name", "RosterText.Clean", true),
        new(SisRosterColumns.CollegeName, "College.NameKey", "AcademicKey.NormalizeOrUnspecified", true),
        new(SisRosterColumns.Program, "Program.Code", "RosterText.Clean", true),
        new(SisRosterColumns.Program, "Program.CodeKey", "AcademicKey.NormalizeOrUnspecified", true),

        new(SisRosterColumns.SectionName, "CourseOffering.SectionName", "RosterText.Clean", false),
        new(SisRosterColumns.SectionName, "CourseOffering.SectionKey",
            "AcademicKey.NormalizeOrUnspecified", false),

        new(SisRosterColumns.CourseCode, "Course.Code", "RosterText.Clean", true),
        new(SisRosterColumns.CourseCode, "Course.CodeKey", "AcademicKey.NormalizeOrUnspecified", true),
        new(SisRosterColumns.CourseName, "Course.Title", "RosterText.Clean", false),

        new(SisRosterColumns.TeacherFullName, "Instructor.DisplayName", "TeacherNames.Parse", false),
        new(SisRosterColumns.TeacherFirstName, "Instructor.DisplayName", "TeacherNames.Parse", false),
        new(SisRosterColumns.TeacherLastName, "Instructor.DisplayName", "TeacherNames.Parse", false),
        new(SisRosterColumns.TeacherSuffix, "(none — honorific)", "TeacherNames.Parse", false),
        new(SisRosterColumns.TeacherCollege, "(none)", "(not imported)", false),
    ];

    /// <summary>
    /// The current built-in mapping, taken from the template. Deriving <em>this</em> one is correct
    /// where deriving <see cref="VersionOneMapping"/> would not be: it stands in for a version an
    /// operator would author from what the pipeline does today, so agreeing with the code is the point.
    /// </summary>
    private static List<MappingRow> BuiltInMapping() =>
    [
        .. SisImportProfileTemplate.Entries.Select(e =>
            new MappingRow(e.SourceColumn, e.TargetField, e.NormalizationRule, e.IsRequired)),
    ];

    /// <summary>
    /// Writes one version of the built-in profile straight to the database, under the same
    /// <c>NameKey</c> <see cref="SisImportProfileTemplate"/> uses — which is what makes it a
    /// <em>version</em> of that profile rather than an unrelated one, and therefore what puts it inside
    /// <c>UX_SisImportProfiles_School_Name_Active</c>'s reach.
    /// </summary>
    private async Task<Guid> SeedProfileAsync(
        Guid schoolId, int version, bool isActive, IReadOnlyList<MappingRow> mapping)
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

    /// <summary>A batch that already ran, pinned to a profile version — the history D-4 has to keep.</summary>
    private async Task<Guid> SeedCompletedBatchAsync(Guid schoolId, Guid termId, Guid profileId)
    {
        await using var db = NewDbContext();

        var batch = new SisImportBatch
        {
            SchoolId = schoolId,
            TermId = termId,
            Source = SisImportSource.Excel,
            FileName = "Copy-of-CCJ-july.xlsx",
            Status = SisImportStatus.Completed,
            ImportProfileId = profileId,
        };
        db.SisImportBatches.Add(batch);
        await db.SaveChangesAsync();
        return batch.Id;
    }

    /// <summary>
    /// <b>The first upload after the 2026-07-30 bump, on a database that already holds an active
    /// version 1 — which is every tenant with data, and the one code path CI's always-fresh database
    /// never executes.</b>
    ///
    /// <para>
    /// <c>Each_batch_records_the_versioned_mapping_it_ran_under</c> proves idempotence at version 2 on a
    /// clean database: it never reaches the supersession at all. The risk is entirely in the ordering —
    /// <c>UX_SisImportProfiles_School_Name_Active</c> is <c>UNIQUE(SchoolId, NameKey) WHERE
    /// IsActive = 1</c>, so an INSERT of the new active version issued before the UPDATE that clears the
    /// old one is a raw unique violation, and it would land on the operator's first upload after deploy,
    /// on every tenant at once.
    /// </para>
    ///
    /// <para>
    /// <b>Asserted against the database rather than against the model.</b> The counts and the flag are
    /// read back on a fresh context, and version 1's column rows are compared field by field against the
    /// mapping that was seeded — because "superseded, not deleted" is a data-preservation claim and the
    /// only way it can fail is silently. The index itself is pinned by
    /// <c>SisImportSchemaTests.A_profile_may_have_many_versions_but_only_one_active_one</c>; this test
    /// pins that the pipeline stays on the right side of it.
    /// </para>
    /// </summary>
    /// <summary>
    /// <b>An OLDER active version must not be handed back to a new upload.</b>
    ///
    /// <para>
    /// <c>EnsureBuiltInProfileAsync</c>'s own summary says the version-matched lookup exists so that a
    /// stale version 1 cannot "take the card serial from the REGNO column and reinstate the defect". Its
    /// fallback used to accept <em>any</em> active version, older ones included — so the defect the
    /// lookup prevents arrived through the fallback instead. This is the arrangement that reaches it:
    /// the built-in version present but deactivated, and version 1 active.
    /// </para>
    ///
    /// <para>
    /// <b>Worse than the batch-replay case, which is deliberate and pinned elsewhere.</b> That one only
    /// re-runs history under the rules it ran under. This one silently applies version 1's mapping to a
    /// <em>new</em> upload of live data, so cards would be issued carrying student numbers today. The
    /// assertion is therefore on the cards, not on which profile row came back — a client cannot see
    /// profile resolution, and the serial is what the defect actually produces.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_older_active_profile_does_not_supply_the_mapping_for_a_new_upload()
    {
        var world = await ArrangeAsync();

        // The built-in version, present but not live — the state that sends resolution to the fallback.
        await SeedProfileAsync(
            world.SchoolId, SisImportProfileTemplate.BuiltInVersion, isActive: false, BuiltInMapping());
        await SeedProfileAsync(world.SchoolId, version: 1, isActive: true, VersionOneMapping);

        var batch = await ImportAsync(world.TermId);

        Assert.Equal(0, batch.FailedRows);

        await using var read = NewDbContext();
        var cards = await read.RfidCards.IgnoreQueryFilters()
            .Where(c => c.SchoolId == world.SchoolId).ToListAsync();

        // Version 1 maps the card UID from REGNO, so under the old fallback every card here carried a
        // student number. Naming both sides makes the failure say which mapping ran.
        Assert.DoesNotContain(cards, c => c.CardUid == SyntheticRoster.MariaRegNo);
        Assert.Contains(cards, c => c.CardUid == SyntheticRoster.MariaRfid);
    }

    [Fact]
    public async Task An_active_v1_profile_is_superseded_by_v2_and_keeps_every_row_it_had()
    {
        var world = await ArrangeAsync();

        var v1Id = await SeedProfileAsync(world.SchoolId, version: 1, isActive: true, VersionOneMapping);
        var julyBatchId = await SeedCompletedBatchAsync(world.SchoolId, world.TermId, v1Id);

        // The upload that would have thrown a DbUpdateException if the ordering argument were wrong.
        var batch = await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var profiles = await read.SisImportProfiles.IgnoreQueryFilters()
            .Include(p => p.Columns).ToListAsync();
        Assert.Equal(2, profiles.Count);

        var v1 = profiles.Single(p => p.Id == v1Id);
        var v2 = profiles.Single(p => p.Id != v1Id);

        Assert.Equal(1, v1.Version);
        Assert.Equal(SisImportProfileTemplate.BuiltInVersion, v2.Version);

        // At most one active version, which is the invariant the whole manoeuvre exists to preserve.
        Assert.False(v1.IsActive);
        Assert.True(v2.IsActive);
        Assert.Single(profiles, p => p.IsActive);

        // Superseded, not deleted, and not edited: every column row version 1 had is still there, in
        // order, with the same four values it was written with.
        Assert.Equal(
            VersionOneMapping.Select(m =>
                (m.SourceColumn, m.TargetField, m.NormalizationRule, m.IsRequired)),
            v1.Columns.OrderBy(c => c.Ordinal).Select(c =>
                (c.SourceColumn, c.TargetField, c.NormalizationRule, c.IsRequired)));

        // Including the row the correction was about. Version 1 still says the card UID came from
        // REGNO, because that is what the batches that ran under it actually did.
        var v1Card = Assert.Single(
            v1.Columns, c => c.TargetField == SisImportProfileTemplate.RfidCardUidTarget);
        Assert.Equal(SisRosterColumns.HeaderKey(SisRosterColumns.RegNo), v1Card.SourceColumnKey);

        // And version 2 says it does not.
        var v2Card = Assert.Single(
            v2.Columns, c => c.TargetField == SisImportProfileTemplate.RfidCardUidTarget);
        Assert.Equal(SisRosterColumns.HeaderKey(SisRosterColumns.RfidCardSerial), v2Card.SourceColumnKey);

        // The July batch still resolves to version 1 and to version 1's mapping. Nothing historical was
        // rewritten to match the new rules.
        var july = await read.SisImportBatches.IgnoreQueryFilters()
            .SingleAsync(b => b.Id == julyBatchId);
        Assert.Equal(v1Id, july.ImportProfileId);

        // The new batch ran the new mapping: eight serials, taken from the RFID column.
        var imported = await read.SisImportBatches.IgnoreQueryFilters()
            .SingleAsync(b => b.Id == batch.Id);
        Assert.Equal(v2.Id, imported.ImportProfileId);
        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(ExpectedStudents, await read.RfidCards.CountAsync());
        Assert.Equal(0, await read.RfidCards
            .CountAsync(c => c.CardUid == SyntheticRoster.MariaRegNo));
    }

    /// <summary>
    /// <b>And the other half of "kept, superseded": a batch pinned to version 1 still <em>executes</em>
    /// version 1's mapping.</b> Preserving the rows is only worth something if they still explain — and
    /// re-run — the batch that pointed at them.
    ///
    /// <para>
    /// So this one reproduces the old defect on purpose. Version 1 maps <c>REGNO → RfidCard.CardUid</c>,
    /// so a batch running under it issues cards carrying student numbers and never reads the file's RFID
    /// column at all — even though this file has one, and even though the code that ships today would
    /// read it. That is ADR-001 D-4 working: a batch is explained by the rules it ran under, not by
    /// whatever is live when someone re-runs it.
    /// </para>
    ///
    /// <para>
    /// The batch is repointed after the upload because the upload necessarily supersedes version 1 —
    /// that is the previous test's subject. Repointing is the only way to reach the state a batch
    /// uploaded in July and re-run in August is genuinely in.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_batch_pinned_to_v1_executes_v1s_mapping_and_not_the_live_one()
    {
        var world = await ArrangeAsync();
        var v1Id = await SeedProfileAsync(world.SchoolId, version: 1, isActive: true, VersionOneMapping);

        Guid batchId;
        await using (var db = NewDbContext())
        {
            await using var content = SyntheticRoster.Build();
            var preview = await SisImportOn(db).UploadAsync(
                new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", world.TermId));
            batchId = preview.Batch.Id;
        }

        await using (var repoint = NewDbContext())
        {
            var staged = await repoint.SisImportBatches.IgnoreQueryFilters()
                .SingleAsync(b => b.Id == batchId);
            staged.ImportProfileId = v1Id;
            await repoint.SaveChangesAsync();
        }

        await using (var run = NewDbContext())
            Assert.Equal(0, (await SisImportOn(run).RunAsync(batchId, world.TermId)).FailedRows);

        await using var read = NewDbContext();

        var maria = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        var card = Assert.Single(await read.RfidCards.Where(c => c.StudentId == maria.Id).ToListAsync());
        Assert.Equal(SyntheticRoster.MariaRegNo, card.CardUid);

        // The file's own RFID column was never read, so no card anywhere carries a serial from it.
        Assert.Equal(0, await read.RfidCards.CountAsync(c => c.CardUid == SyntheticRoster.MariaRfid));
        Assert.Equal(ExpectedStudents, await read.RfidCards.CountAsync());

        // D-4 lets the old mapping run; it does not let it run quietly. Without this the only evidence
        // that a re-run minted student-number cards would be the serials themselves, which is precisely
        // the thing nobody re-reads. The operator gets one line per affected row in the batch report.
        var warned = await read.SisImportRows.IgnoreQueryFilters()
            .Where(r => r.BatchId == batchId
                     && r.WarningCode == SisImportWarningCode.RfidCardFromLegacyMapping)
            .ToListAsync();

        Assert.NotEmpty(warned);
        Assert.All(warned, r => Assert.Contains(SisRosterColumns.RfidCardSerial, r.WarningMessage!));
    }

    /// <summary>
    /// <b>The RFID source column is read from the batch's profile and from nowhere else — proved by a
    /// mapping that names no such column while the file is full of serials.</b>
    ///
    /// <para>
    /// An operator-authored version 3 is active and simply has no <c>RfidCard.CardUid</c> row. The file
    /// is the ordinary eighteen-column workbook with every student's serial in it, and not one card is
    /// issued. That is the ADR-001 D-4 seam doing the job it exists for: when the client's export
    /// finally arrives calling the column something nobody has guessed, the fix is a profile version and
    /// not a deploy — and the corollary, tested here, is that the constant
    /// <see cref="SisRosterColumns.RfidCardSerial"/> genuinely supplies only the built-in default.
    /// </para>
    ///
    /// <para>
    /// Version 2 is seeded alongside it, inactive, because that is the state a real database is in once
    /// an operator has authored version 3: <c>EnsureBuiltInProfileAsync</c> finds its own version
    /// present, declines to reactivate it, and hands back the live one. Omitting it would instead
    /// exercise the insert path and re-supersede version 3, which is a different question.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_profile_naming_no_rfid_column_imports_every_student_with_no_card()
    {
        var world = await ArrangeAsync();

        await SeedProfileAsync(
            world.SchoolId, SisImportProfileTemplate.BuiltInVersion, isActive: false, BuiltInMapping());

        var v3Id = await SeedProfileAsync(
            world.SchoolId, version: 3, isActive: true,
            [.. BuiltInMapping()
                .Where(m => m.TargetField != SisImportProfileTemplate.RfidCardUidTarget)]);

        var batch = await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        var ran = await read.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == batch.Id);
        Assert.Equal(v3Id, ran.ImportProfileId);

        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
        Assert.Equal(ExpectedEnrollments, await read.Enrollments.CountAsync());
        Assert.Equal(0, await read.RfidCards.CountAsync());

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(6, batch.WarningRows);
        Assert.True(batch.CountersReconcile);

        Assert.All(await RowsOfAsync(batch.Id), r => Assert.DoesNotContain(
            r.Entities, e => e.EntityType == SisImportEntityType.RfidCard));
    }

    // ============================================ CRITICAL 1: the fan-out points where it says it does

    /// <summary>
    /// <b>Every id in the fan-out is an id of the table its <c>EntityType</c> names.</b> Nothing
    /// asserted <c>EntityId</c> values at all before this, which is how the <c>Unchanged</c> branches
    /// came to record <c>offering.Id</c> under <c>EntityType = Enrollment</c> and
    /// <c>CourseOfferingInstructor</c>: the prefetches were sets of natural keys with no entity id in
    /// scope, so there was nothing else to hand the ledger.
    ///
    /// <para>
    /// It is asserted on the <b>second</b> run deliberately. A re-import is the steady state and its
    /// touches are <em>all</em> <c>Unchanged</c> — so on the wrong code every enrollment touch in this
    /// batch pointed into the wrong table, and
    /// <c>IX_SisImportRowEntities_Entity (EntityType, EntityId)</c> answered "which rows touched
    /// enrollment X?" with nothing while Phase 3's row-detail screen rendered it.
    /// </para>
    ///
    /// <para>
    /// The check is a whole-table map rather than a spot check on <c>Enrollment</c>, because the defect
    /// is a class — "the id in scope is not the id being recorded" — and one table's worth of proof
    /// would not have caught the assignment case sitting four lines above it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_fan_out_id_names_a_row_that_exists_in_the_table_its_type_names()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);
        var second = await ImportAsync(world.TermId);

        var rows = await RowsOfAsync(second.Id);
        var touches = rows.SelectMany(r => r.Entities).ToList();

        // The branch under test: on a re-import nothing is created, so every touch is Unchanged.
        Assert.All(touches, t => Assert.Equal(SisImportEntityAction.Unchanged, t.Action));

        await using var read = NewDbContext();

        var idsByType = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal)
        {
            [SisImportEntityType.College] = [.. await read.Colleges.Select(x => x.Id).ToListAsync()],
            [SisImportEntityType.Program] = [.. await read.Programs.Select(x => x.Id).ToListAsync()],
            [SisImportEntityType.Course] = [.. await read.Courses.Select(x => x.Id).ToListAsync()],
            [SisImportEntityType.Instructor] = [.. await read.Instructors.Select(x => x.Id).ToListAsync()],
            [SisImportEntityType.CourseOffering] =
                [.. await read.CourseOfferings.Select(x => x.Id).ToListAsync()],
            [SisImportEntityType.CourseOfferingInstructor] =
                [.. await read.CourseOfferingInstructors.Select(x => x.Id).ToListAsync()],
            [SisImportEntityType.Student] = [.. await read.Students.Select(x => x.Id).ToListAsync()],
            [SisImportEntityType.RfidCard] = [.. await read.RfidCards.Select(x => x.Id).ToListAsync()],
            [SisImportEntityType.Enrollment] = [.. await read.Enrollments.Select(x => x.Id).ToListAsync()],
            [SisImportEntityType.StudentTermRecord] =
                [.. await read.StudentTermRecords.Select(x => x.Id).ToListAsync()],
        };

        foreach (var touch in touches)
        {
            Assert.True(
                idsByType.TryGetValue(touch.EntityType, out var ids),
                $"Fan-out records EntityType '{touch.EntityType}', which no table in this map covers. " +
                "Add it here as well as to SisImportEntityType — an unmapped type is an unchecked id.");

            Assert.True(
                ids!.Contains(touch.EntityId),
                $"EntityType '{touch.EntityType}' recorded EntityId {touch.EntityId}, which is not the " +
                "id of any row in that table. The fan-out is pointing at the wrong entity.");
        }

        // Not vacuous: the enrollment and assignment touches — the two that were wrong — are present in
        // the numbers the fixture describes. Every row names exactly one enrollment, so twelve; and
        // every row except the two placeholder ones (rows 3 and 8) names exactly one assignment, so
        // ten. Both are counts of *touches*, not of distinct entities — several rows resolve to the
        // same enrollment or assignment and each records its own Unchanged trail entry, which is the
        // whole reason Unchanged is recorded rather than omitted.
        Assert.Equal(
            SyntheticRoster.DataRowCount,
            touches.Count(t => t.EntityType == SisImportEntityType.Enrollment));
        Assert.Equal(
            SyntheticRoster.DataRowCount - 2,
            touches.Count(t => t.EntityType == SisImportEntityType.CourseOfferingInstructor));
    }

    // ======================================= CRITICAL 2: a card is never moved between two students

    /// <summary>
    /// <b>An active card whose serial matches but whose student does not is refused, not reassigned.</b>
    ///
    /// <para>
    /// <b>What arms this changed on 2026-07-30; that it is reachable did not.</b> The old premise was
    /// that REGNO <em>was</em> the card UID, so <c>USA-00001</c> and <c>USA00001</c> were two students
    /// by <c>UNIQUE(SchoolId, StudentNumber)</c> sharing one UID by <see cref="CardUid.Normalize"/>.
    /// The client corrected us — the serial is its own column — and that case is gone. What replaces it
    /// is more ordinary, not less: a serial already issued to somebody else, which is what a recycled
    /// card, a cloned card or a mis-keyed export cell looks like. <c>UX_RfidCards_SchoolId_CardUid_Active</c>
    /// is keyed on the serial and not on the student, so the file is free to say two students hold one
    /// card and the database is not.
    /// </para>
    ///
    /// <para>
    /// Silently moving the card would cost the first student their active card, re-attribute every
    /// subsequent tap on that physical card to the wrong person, and leave every
    /// <c>AttendanceRecords.RfidCardId</c> already written pointing at a card whose <c>StudentId</c>
    /// moved underneath it — the history destruction ADR-001 D-3 exists to prevent. That is unchanged.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_card_serial_that_already_identifies_another_student_fails_the_row_and_moves_nothing()
    {
        var world = await ArrangeAsync();

        Guid incumbentId, cardId;
        await using (var arrange = NewDbContext())
        {
            // A different student entirely, holding the card whose serial the file hands to Maria.
            var incumbent = TestData.NewStudent(world.SchoolId, "USA09999", "Original", null, "Owner");
            arrange.Students.Add(incumbent);
            var issued = TestData.NewCard(world.SchoolId, incumbent.Id, SyntheticRoster.MariaRfid);
            arrange.RfidCards.Add(issued);
            await arrange.SaveChangesAsync();
            incumbentId = incumbent.Id;
            cardId = issued.Id;
        }

        var batch = await ImportAsync(world.TermId);

        // Maria's four rows (2, 3, 4, 5) all carry that serial and all fail.
        var rows = await RowsOfAsync(batch.Id);
        var failed = rows.Where(r => r.Result == SisImportRowResult.Failed).ToList();
        Assert.Equal(4, failed.Count);
        Assert.All(failed, r => Assert.StartsWith(
            SisImportFailureCode.RfidCardStudentMismatch, r.ErrorMessage!, StringComparison.Ordinal));

        // The message names both students and the serial, which is the whole of what an operator needs.
        Assert.All(failed, r => Assert.Contains("USA09999", r.ErrorMessage!, StringComparison.Ordinal));
        Assert.All(failed, r => Assert.Contains(
            SyntheticRoster.MariaRegNo, r.ErrorMessage!, StringComparison.Ordinal));
        Assert.All(failed, r => Assert.Contains(
            SyntheticRoster.MariaRfid, r.ErrorMessage!, StringComparison.Ordinal));

        Assert.Equal(4, batch.FailedRows);
        Assert.True(batch.CountersReconcile);
        Assert.Equal(SisImportStatus.CompletedWithErrors, batch.Status);

        await using var read = NewDbContext();

        // The card did not move, was not deactivated, and has no competitor.
        var card = await read.RfidCards.SingleAsync(c => c.Id == cardId);
        Assert.Equal(incumbentId, card.StudentId);
        Assert.True(card.IsActive);
        Assert.Equal(1, await read.RfidCards.CountAsync(c => c.CardUid == SyntheticRoster.MariaRfid));

        // A failed row is genuinely not imported rather than partially imported: no USA00001 student
        // was created, and none of Maria's four enrollments exist.
        Assert.Equal(0, await read.Students.CountAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo));
        Assert.Equal(0, await read.Enrollments.CountAsync(e => e.StudentId == incumbentId));

        // And the incumbent's own record is untouched — including the D-2 cache, which a failed row
        // must not have contributed a section to.
        var owner = await read.Students.SingleAsync(s => s.Id == incumbentId);
        Assert.Equal("Original", owner.FirstName);
        Assert.Null(owner.AcademicCacheUpdatedAt);
    }

    /// <summary>
    /// The same rule with no card in the database at all: <b>two students inside one file carrying the
    /// same RFID serial</b>. Without the check the second would be handed the first one's card in the
    /// fan-out and issued none of their own — the same misattribution, arriving on the first import
    /// rather than the second.
    ///
    /// <para>
    /// The old version of this test produced the collision by re-spelling one REGNO as a punctuated
    /// variant of another, which was a real case only while the UID was derived from REGNO. It is not a
    /// case any more, so the test is rewritten to the surviving rule rather than deleted: a duplicated
    /// serial is what a cloned card or a copy-paste in the export actually looks like, and it is now
    /// <em>more</em> reachable than the old shape ever was, because nothing about a serial is
    /// constrained by the student columns.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_students_in_one_file_sharing_an_rfid_serial_fail_the_later_one()
    {
        var world = await ArrangeAsync();

        var rfidColumn = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.RfidCardSerial);
        var rows = SyntheticRoster.Rows();

        // Row 7 (index 5) is Ana. Give her Maria's serial: a different student, the same physical card.
        rows[5][rfidColumn] = SyntheticRoster.MariaRfid;

        var batch = await ImportAsync(world.TermId, SyntheticRoster.Build(rows));

        var staged = await RowsOfAsync(batch.Id);
        var failed = staged.Where(r => r.Result == SisImportRowResult.Failed).ToList();

        // Maria's rows come first in the worksheet, so she is the serial's first claimant and keeps it.
        var loser = Assert.Single(failed);
        Assert.Equal(7, loser.RowNumber);
        Assert.StartsWith(
            SisImportFailureCode.RfidCardStudentMismatch, loser.ErrorMessage!, StringComparison.Ordinal);

        await using var read = NewDbContext();

        // Ana is not imported at all — a failed row leaves no trace, including no student row.
        Assert.Equal(0, await read.Students.CountAsync(s => s.StudentNumber == SyntheticRoster.AnaRegNo));

        // And exactly one card carries the serial, held by the first claimant.
        var card = Assert.Single(
            await read.RfidCards.Where(c => c.CardUid == SyntheticRoster.MariaRfid).ToListAsync());
        var maria = await read.Students
            .SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        Assert.Equal(maria.Id, card.StudentId);
    }

    /// <summary>
    /// <b>One student carrying two different serials across two rows of one file. Permitted, and
    /// reported here as an open decision rather than silently ruled on.</b>
    ///
    /// <para>
    /// This shape became possible only on 2026-07-30: while the UID was derived from REGNO, one student
    /// had exactly one UID by construction. With a separate serial column nothing prevents row 2 saying
    /// serial A and row 4 saying serial B for the same REGNO — a mid-year replacement card that the
    /// export lists twice, or a student legitimately holding two cards.
    /// </para>
    ///
    /// <para>
    /// <b>It is allowed, deliberately.</b> <c>UX_RfidCards_SchoolId_CardUid_Active</c> is keyed on the
    /// serial and not on the student, so two active cards for one student are legal at the schema level;
    /// refusing them would be inventing a rule the client has never stated, and doing it inside an
    /// importer is the worst place to invent one. No new warning code is raised either — a published
    /// value needs JJ's approval, not a unilateral addition.
    /// </para>
    ///
    /// <para>
    /// What this test pins is that the permissive path is <em>safe</em>: it does not throw, it does not
    /// fail either row, and each row's fan-out points at its own card rather than both rows collapsing
    /// onto whichever serial was seen first. If JJ later rules that a second serial should warn or fail,
    /// this test is the one to change, and it will say why it was permitted in the first place.
    /// </para>
    /// </summary>
    [Fact]
    public async Task One_student_named_with_two_serials_gets_both_cards_and_a_correct_fan_out()
    {
        var world = await ArrangeAsync();

        var rfidColumn = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.RfidCardSerial);
        var rows = SyntheticRoster.Rows();

        // Rows 2..5 are all Maria. Give row 4 (index 2) a second, unused serial.
        const string secondSerial = "0012509999";
        rows[2][rfidColumn] = secondSerial;

        var batch = await ImportAsync(world.TermId, SyntheticRoster.Build(rows));

        Assert.Equal(0, batch.FailedRows);
        Assert.True(batch.CountersReconcile);

        await using var read = NewDbContext();

        var maria = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        var cards = await read.RfidCards.AsNoTracking()
            .Where(c => c.StudentId == maria.Id).ToListAsync();

        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.True(c.IsActive));
        Assert.Contains(cards, c => c.CardUid == SyntheticRoster.MariaRfid);
        Assert.Contains(cards, c => c.CardUid == secondSerial);

        // The fan-out is per row and points at that row's own card — not both rows at the first serial,
        // which is what a grouping that lost the second value would produce.
        var staged = await RowsOfAsync(batch.Id);

        Guid CardTouchOn(int rowNumber) => Assert.Single(
            Row(staged, rowNumber).Entities, e => e.EntityType == SisImportEntityType.RfidCard)
            .EntityId;

        Assert.Equal(cards.Single(c => c.CardUid == SyntheticRoster.MariaRfid).Id, CardTouchOn(2));
        Assert.Equal(cards.Single(c => c.CardUid == secondSerial).Id, CardTouchOn(4));
    }

    // ============================================== WARNING 3: a revoked card is not resurrected

    /// <summary>
    /// <b>A card deactivated <em>without</em> a replacement stays deactivated.</b>
    ///
    /// <para>
    /// The prefetch used to filter on <c>IsActive</c>, so a card revoked because it was lost, stolen or
    /// the student was suspended was invisible and the next import issued a fresh active card carrying
    /// the same serial. A serial names one physical card, so that makes the revoked card work again —
    /// silently reversing an operator's deliberate revocation, on a schedule.
    /// </para>
    ///
    /// <para>
    /// Separating the serial from REGNO on 2026-07-30 strengthened this rather than weakening it. The
    /// export is a snapshot of who is enrolled and has no column saying why a card was revoked, so a
    /// serial the registrar still lists against a student is exactly what a lost or stolen card looks
    /// like in the next file — the roster cannot answer the only question that matters, so it does not
    /// get to decide.
    /// </para>
    ///
    /// <para>
    /// ADR-001 D-3 only contemplated deactivate-then-reissue, where the same student was handed a
    /// replacement. Re-issuing is a back-office action with a person behind it, so this reports and
    /// declines — and the student still imports, because losing a card is not losing an enrollment.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_revoked_card_is_not_resurrected_and_the_row_still_imports()
    {
        var world = await ArrangeAsync();

        Guid studentId, revokedCardId;
        await using (var arrange = NewDbContext())
        {
            var student = TestData.NewStudent(
                world.SchoolId, SyntheticRoster.MariaRegNo, "Maria", null, "Santos");
            arrange.Students.Add(student);
            var revoked = TestData.NewCard(
                world.SchoolId, student.Id, SyntheticRoster.MariaRfid, isActive: false);
            arrange.RfidCards.Add(revoked);
            await arrange.SaveChangesAsync();
            studentId = student.Id;
            revokedCardId = revoked.Id;
        }

        var batch = await ImportAsync(world.TermId);

        await using var read = NewDbContext();

        // Still exactly one card for that serial, still deactivated, still the same row.
        var card = Assert.Single(await read.RfidCards
            .Where(c => c.CardUid == SyntheticRoster.MariaRfid)
            .ToListAsync());
        Assert.Equal(revokedCardId, card.Id);
        Assert.False(card.IsActive);

        // The student imported: enrolled, term record written, nothing failed. Three enrollments from
        // four rows — rows 2 and 3 are the placeholder pair and describe one.
        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(3, await read.Enrollments.CountAsync(e => e.StudentId == studentId));
        Assert.Equal(1, await read.StudentTermRecords.CountAsync(r => r.StudentId == studentId));

        // Every row naming the UID says so, and none of them records a card touch — this row touched
        // no card, which is a different statement from touching one and changing nothing.
        var rows = await RowsOfAsync(batch.Id);
        foreach (var rowNumber in new[] { 2, 3, 4, 5 })
        {
            var row = Row(rows, rowNumber);
            Assert.Contains(
                SisImportWarningCode.RfidCardRevoked, row.WarningMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain(row.Entities, e => e.EntityType == SisImportEntityType.RfidCard);
        }

        // Everyone else got their card as usual — the rule is per UID, not a global switch.
        Assert.Equal(ExpectedStudents - 1, await read.RfidCards.CountAsync(c => c.IsActive));
    }

    // ============================= CRITICAL: the roster we actually hold has no RFID column at all

    /// <summary>
    /// <b>A file with no RFID column imports every student, issues no card, fails nothing and complains
    /// about nothing.</b> This is not an edge case. It is the only roster in hand, so it is the shape
    /// every import has until the client's RFID-bearing export arrives — and a student with no card is
    /// the normal outcome rather than an anomaly.
    ///
    /// <para>
    /// <b>The assertion carrying the weight is the absent <see cref="SisImportEntityType.RfidCard"/>
    /// touch on every row.</b> ADR-001 D-2's answer to "how do we know this student has no card?" is
    /// that the fan-out says so by omission: recording nothing is a different statement from recording a
    /// card that changed nothing, and it is queryable in exactly the same way. A pipeline that gathered
    /// the card-less rows under one blank key would satisfy several counts above it and fail this one —
    /// it would issue a single card carrying an empty UID and fail every student but the first for
    /// sharing it.
    /// </para>
    ///
    /// <para>
    /// <b>The warning count is identical to the RFID-bearing batch's six</b>, which is the second half
    /// of the same decision. A warning per card-less row would fire on 100% of the rows of every batch,
    /// make <c>CompletedWithWarnings</c> the permanent status of every import, and bury the six genuine
    /// warnings under twelve noise ones.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_roster_with_no_rfid_column_imports_every_student_and_issues_no_card()
    {
        var world = await ArrangeAsync();

        await using var file = SyntheticRoster.BuildWithoutRfidColumn();
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(11, batch.InsertedRows);
        Assert.Equal(0, batch.UpdatedRows);
        Assert.Equal(1, batch.SkippedRows);
        Assert.True(batch.CountersReconcile);

        // Six, exactly as The_batch_counters_reconcile_to_the_row_count reports for the same twelve rows
        // *with* their serials. Removing the column added no warning to any row.
        Assert.Equal(6, batch.WarningRows);

        await using var read = NewDbContext();

        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
        Assert.Equal(ExpectedStudents, await read.StudentTermRecords.CountAsync());
        Assert.Equal(ExpectedEnrollments, await read.Enrollments.CountAsync());
        Assert.Equal(ExpectedOfferings, await read.CourseOfferings.CountAsync());

        // Not one card — and in particular not one card carrying an empty UID.
        Assert.Equal(0, await read.RfidCards.CountAsync());

        var rows = await RowsOfAsync(batch.Id);
        Assert.Equal(SyntheticRoster.DataRowCount, rows.Count);
        Assert.All(rows, r => Assert.DoesNotContain(
            r.Entities, e => e.EntityType == SisImportEntityType.RfidCard));

        // Nor was any row warned about a card. The six warnings are the blank section, the two
        // placeholder teachers, the title alias and the two-programme section pair; none names a serial.
        Assert.All(rows, r => Assert.DoesNotContain(
            SisRosterColumns.RfidCardSerial, r.WarningMessage ?? "", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>The same file cut down to the three rows that warn about nothing else, so the status is
    /// falsifiable.</b> A card-less batch reports <c>Completed</c> — not <c>CompletedWithWarnings</c>.
    ///
    /// <para>
    /// The full twelve rows cannot make this claim. They carry six warnings of their own, so
    /// <c>CompletedWithWarnings</c> is the right answer there whether or not a missing card warns, and a
    /// test asserting it on the full roster would pass against exactly the pipeline this rules out. See
    /// <see cref="SyntheticRoster.RowsWithoutWarnings"/> for why the subset is picked by index.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_card_less_batch_that_warns_about_nothing_else_completes_silently()
    {
        var world = await ArrangeAsync();

        await using var file = SyntheticRoster.BuildWithoutRfidColumn(
            SyntheticRoster.RowsWithoutWarnings());
        var batch = await ImportAsync(world.TermId, file);

        Assert.Equal(0, batch.WarningRows);
        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(SisImportStatus.Completed, batch.Status);
        Assert.NotEqual(SisImportStatus.CompletedWithWarnings, batch.Status);

        Assert.Equal(3, batch.TotalRows);
        Assert.Equal(3, batch.InsertedRows);
        Assert.True(batch.CountersReconcile);

        await using var read = NewDbContext();

        // Maria, Ana and Rosa: three students, three enrollments in the one BSCRIM 2-A / SSCI7
        // offering, and no cards at all.
        Assert.Equal(3, await read.Students.CountAsync());
        Assert.Equal(3, await read.Enrollments.CountAsync());
        Assert.Equal(1, await read.CourseOfferings.CountAsync());
        Assert.Equal(0, await read.RfidCards.CountAsync());

        Assert.All(await RowsOfAsync(batch.Id), r =>
        {
            Assert.Null(r.WarningCode);
            Assert.DoesNotContain(r.Entities, e => e.EntityType == SisImportEntityType.RfidCard);
        });
    }

    /// <summary>
    /// <b>The transitional file: an RFID column that is present but blank on some rows.</b> The rows
    /// carrying a serial get a card, the blank ones get none, nobody fails and nothing is warned.
    ///
    /// <para>
    /// It reaches the parse by a different route from an absent column — a cell dictionary holding
    /// <c>""</c> rather than no entry at all — and both have to land in the same place, because both
    /// mean "this row names no card". It is also the shape the first real export is most likely to have:
    /// a column added to the registrar's report and filled in as the cards are issued.
    /// </para>
    ///
    /// <para>
    /// <b>Rosa is the sharp case.</b> Her two rows are one student and only the second is blanked, so
    /// she still holds exactly one card — and the fan-out records it against the row that named it and
    /// records nothing against the row that did not. A grouping that folded the blanks together would
    /// instead fail her second row for claiming a card that belongs to Juan.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_blank_rfid_cell_costs_that_row_a_card_and_nobody_else_anything()
    {
        var world = await ArrangeAsync();

        var rfidColumn = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.RfidCardSerial);
        var rows = SyntheticRoster.Rows();

        // Juan (worksheet row 6) and Omar (row 13) lose their serial outright; Rosa keeps hers on row 9
        // and loses it on row 10. Worked out by hand: eight students, two of them card-less, six cards.
        rows[4][rfidColumn] = "";
        rows[8][rfidColumn] = "";
        rows[11][rfidColumn] = "";

        var batch = await ImportAsync(world.TermId, SyntheticRoster.Build(rows));

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(6, batch.WarningRows);      // unchanged — a blank serial is not one of them
        Assert.True(batch.CountersReconcile);

        await using var read = NewDbContext();

        Assert.Equal(ExpectedStudents, await read.Students.CountAsync());
        Assert.Equal(ExpectedEnrollments, await read.Enrollments.CountAsync());
        Assert.Equal(6, await read.RfidCards.CountAsync());

        foreach (var regNo in new[] { SyntheticRoster.JuanRegNo, SyntheticRoster.OmarRegNo })
        {
            var cardless = await read.Students.SingleAsync(s => s.StudentNumber == regNo);
            Assert.Equal(0, await read.RfidCards.CountAsync(c => c.StudentId == cardless.Id));
        }

        var rosa = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.RosaRegNo);
        var rosaCard = Assert.Single(
            await read.RfidCards.Where(c => c.StudentId == rosa.Id).ToListAsync());
        Assert.Equal(SyntheticRoster.RosaRfid, rosaCard.CardUid);

        var staged = await RowsOfAsync(batch.Id);

        Assert.Equal(rosaCard.Id, Assert.Single(
            Row(staged, 9).Entities, e => e.EntityType == SisImportEntityType.RfidCard).EntityId);

        foreach (var rowNumber in new[] { 6, 10, 13 })
            Assert.DoesNotContain(
                Row(staged, rowNumber).Entities, e => e.EntityType == SisImportEntityType.RfidCard);
    }

    /// <summary>
    /// <b>The leading zeros, followed past the card row to the only thing that consumes it: a tap.</b>
    ///
    /// <para>
    /// <see cref="Regno_is_stored_verbatim_and_the_card_uid_comes_from_the_rfid_column"/> asserts that
    /// <c>0012503326</c> reached <c>RfidCards.CardUid</c> intact, and
    /// <c>TapFlowTests.A_tap_resolves_a_decimal_serial_without_losing_its_leading_zeros</c> asserts that
    /// a card carrying that value can be tapped — but the card in that test is written by a fixture, so
    /// the two halves have never been joined. This joins them: the card is the one the <em>import</em>
    /// produced, and it is resolved through <see cref="IAttendanceService"/> rather than by a query the
    /// test writes for itself.
    /// </para>
    ///
    /// <para>
    /// <b>The negative half is what makes it falsifiable.</b>
    /// <see cref="SyntheticRoster.MariaRfidWithLeadingZerosLost"/> is the value a numeric cell read — or
    /// an integer round trip anywhere along the path — would have produced, and it must resolve to
    /// nobody, or "the zeros survived" is a claim about a string rather than about a card that works.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_imported_serial_resolves_a_tap_and_its_truncation_resolves_nothing()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        Guid eventId, mariaId;
        await using (var arrange = NewDbContext())
        {
            var convocation = TestData.NewEvent(world.SchoolId);
            arrange.Events.Add(convocation);
            await arrange.SaveChangesAsync();

            eventId = convocation.Id;
            mariaId = (await arrange.Students
                .SingleAsync(s => s.StudentNumber == SyntheticRoster.MariaRegNo)).Id;
        }

        await using var db = NewDbContext();
        var attendance = AttendanceOn(db);

        var recorded = await attendance.TapAsync(
            new TapRequest(eventId, SyntheticRoster.MariaRfid, null, null, TestData.Now));

        Assert.Equal(TapOutcome.Recorded, recorded.Outcome);
        Assert.Equal(mariaId, recorded.Result.Record!.StudentId);
        Assert.Equal(SyntheticRoster.MariaRegNo, recorded.Result.Record!.StudentNumber);

        var truncated = await attendance.TapAsync(new TapRequest(
            eventId, SyntheticRoster.MariaRfidWithLeadingZerosLost, null, null, TestData.Now));

        Assert.Equal(TapOutcome.CardNotFound, truncated.Outcome);
    }

    // ================================= WARNING 1: an absent cell is not an instruction to erase

    /// <summary>
    /// <b>A blank optional cell means "this file does not say", never "clear what you have".</b>
    ///
    /// <para>
    /// <see cref="RosterText.Clean"/> returns <c>null</c> for a blank cell, so an unconditional assign
    /// wrote <c>NULL</c> over a stored value and reported it as <c>Updated</c> — a replace-with-nothing
    /// driven by the <em>absence</em> of data. That collides head-on with the stated contract that a
    /// re-import updates: a student whose personal e-mail was typed in by hand in the back office would
    /// lose it on the next run of a file that never carried that column.
    /// </para>
    ///
    /// <para>
    /// The other half is asserted alongside: a column the roster <em>is</em> authoritative for must
    /// still change, or the fix would have bought data safety by making re-imports inert.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_blank_cell_does_not_clear_a_stored_value_but_a_filled_one_still_updates()
    {
        var world = await ArrangeAsync();
        await ImportAsync(world.TermId);

        Guid mariaId;
        await using (var backOffice = NewDbContext())
        {
            // Typed in by a human after the first import, in columns Juan's rows leave blank.
            var maria = await backOffice.Students
                .SingleAsync(s => s.StudentNumber == SyntheticRoster.JuanRegNo);
            maria.MiddleName = "Bautista";
            maria.AlternateEmail = "juan.personal@example.com";
            await backOffice.SaveChangesAsync();
            mariaId = maria.Id;
        }

        var lastNameColumn = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.StudentLastName);
        var regNoColumn = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.RegNo);
        var corrected = SyntheticRoster.Rows();
        foreach (var row in corrected.Where(r => r[regNoColumn] == SyntheticRoster.JuanRegNo))
            row[lastNameColumn] = "Cruz-Reyes";

        var second = await ImportAsync(world.TermId, SyntheticRoster.Build(corrected));

        await using var read = NewDbContext();
        var juan = await read.Students.SingleAsync(s => s.Id == mariaId);

        // The blanks did not erase anything.
        Assert.Equal("Bautista", juan.MiddleName);
        Assert.Equal("juan.personal@example.com", juan.AlternateEmail);

        // And the column the file does own still changed, on that row alone.
        Assert.Equal("Cruz-Reyes", juan.LastName);
        Assert.Equal(1, second.UpdatedRows);
        Assert.Equal(0, second.InsertedRows);

        // The offering's display name is the same rule: still present after a run whose section cell
        // for that offering is blank.
        Assert.Equal(
            SyntheticRoster.PrimarySectionName,
            (await read.CourseOfferings.FirstAsync(o => o.SectionKey == "BSCRIM2A")).SectionName);
    }

    // ======================== WARNING 2: a run that dies mid-pass still records that it died

    /// <summary>
    /// <b>A batch never gets stuck in <c>Running</c>, and the exception that explains the run is the
    /// one the caller receives.</b>
    ///
    /// <para>
    /// The failure handler used to set the status and call <c>SaveChanges</c> on a context that still
    /// tracked everything the failed pass had added. The most likely way the pass dies <em>is</em> a
    /// <c>SaveChanges</c> failure, so the recovery save re-attempted the same entities, threw the same
    /// error from inside the catch, replaced the original exception with it, and never persisted the
    /// status — leaving the batch in <c>Running</c>, which is exactly what that block exists to
    /// prevent and is indistinguishable from a run still in progress.
    /// </para>
    ///
    /// <para>
    /// The fault is a first name longer than <c>Students.FirstName nvarchar(100)</c>: a real
    /// <c>DbUpdateException</c> from the real database, raised by the fact pass's own save, rather than
    /// a stubbed throw that would prove only that the catch block runs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_run_that_fails_mid_pass_records_Failed_and_surfaces_the_original_exception()
    {
        var world = await ArrangeAsync();

        var firstNameColumn = SisRosterColumns.All.ToList().IndexOf(SisRosterColumns.StudentFirstName);
        var poisoned = SyntheticRoster.Rows();
        poisoned[0][firstNameColumn] = new string('X', 300);

        Guid batchId;
        await using var db = NewDbContext();
        var service = SisImportOn(db);

        await using (var content = SyntheticRoster.Build(poisoned))
        {
            var preview = await service.UploadAsync(
                new SisImportUploadRequest(content, "poisoned.xlsx", world.TermId));
            batchId = preview.Batch.Id;
        }

        // The original fault, not whatever a doomed recovery save would have thrown over it.
        var thrown = await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => service.RunAsync(batchId, world.TermId));
        Assert.NotNull(thrown.InnerException);

        // Read on a fresh context: the status has to be in the database, not merely on the instance
        // the failed run was holding.
        await using var read = NewDbContext();
        var batch = await read.SisImportBatches.SingleAsync(b => b.Id == batchId);

        Assert.Equal(SisImportStatus.Failed, batch.Status);
        Assert.NotNull(batch.FinishedAt);

        // Nothing the failed pass had staged in memory was committed by the recovery write.
        Assert.Equal(0, await read.Students.CountAsync());
    }

    /// <summary>
    /// A run cancelled after it has committed <c>Running</c> ends <c>Failed</c>, and is therefore
    /// re-runnable.
    ///
    /// <para>
    /// <b>This is the regression test for two batches that could not be finished or retried.</b> On the
    /// deployment VM a 21,497-row roster ran past the SPA's 15-second budget; the browser aborted,
    /// MVC's <c>CancellationToken</c> is bound to <c>HttpContext.RequestAborted</c>, and the resulting
    /// <see cref="OperationCanceledException"/> was excluded by <c>RunAsync</c>'s catch filter. So the
    /// <c>Failed</c> status was never written and the batch sat in <c>Running</c> with a null
    /// <c>FinishedAt</c> - a status the re-run guard rejects, because it is in neither
    /// <see cref="SisImportStatus.Pending"/> nor <see cref="SisImportStatus.Failed"/>. The import could
    /// only be recovered with hand-written SQL.
    /// </para>
    ///
    /// <para>
    /// <b>Cancellation is triggered from a real interceptor rather than by pre-cancelling the token</b>,
    /// and the difference is the whole test. A token cancelled before the call throws while
    /// <c>RunAsync</c> is still loading the batch, before <c>Running</c> is written, and leaves it
    /// <c>Pending</c> - which was always retryable, so it proves nothing. Cancelling from
    /// <c>SavedChangesAsync</c> after the first save puts the cancellation exactly where the browser
    /// put it: past the status write, inside <c>ExecuteAsync</c>.
    /// </para>
    ///
    /// <para>
    /// The controller no longer hands the request's token to this method at all
    /// (<c>SisImportController.Run</c>), so the browser can no longer cause this. That is the fix; this
    /// is the belt to its braces, because a run is still cancellable from anywhere else that holds a
    /// token, and the state it must not be left in is the same one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_run_cancelled_after_it_started_records_Failed_and_can_be_run_again()
    {
        var world = await ArrangeAsync();

        using var cancelAfterFirstSave = new CancellationTokenSource();
        var interceptor = new CancelAfterFirstSave(cancelAfterFirstSave);

        var options = new DbContextOptionsBuilder<EamsDbContext>()
            .UseSqlServer(Sql.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        Guid batchId;

        await using (var db = new EamsDbContext(options, School))
        {
            var service = SisImportOn(db);

            await using (var content = SyntheticRoster.Build())
            {
                var preview = await service.UploadAsync(
                    new SisImportUploadRequest(content, "cancelled.xlsx", world.TermId));
                batchId = preview.Batch.Id;
            }

            // The upload saved too, so the counter is armed only now - otherwise the run would be
            // cancelled before it had loaded anything.
            interceptor.Arm();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.RunAsync(batchId, world.TermId, cancelAfterFirstSave.Token));
        }

        await using (var read = NewDbContext())
        {
            var batch = await read.SisImportBatches.SingleAsync(b => b.Id == batchId);

            // Failed, not Running. Running is the state that cannot be recovered.
            Assert.Equal(SisImportStatus.Failed, batch.Status);
            Assert.NotNull(batch.FinishedAt);
        }

        // The point of Failed rather than Running: the batch is retryable, and the retry completes.
        await using (var retry = NewDbContext())
        {
            var batch = await SisImportOn(retry).RunAsync(batchId, world.TermId);

            // The same terminal status an uninterrupted run of this fixture reaches - the warnings are
            // the roster's own (see the first test in this file), not a mark left by the cancellation.
            // Asserting the student count as well is what says the retry actually imported rather than
            // merely finishing: ExecuteAsync clears the previous attempt's fan-out and rebuilds it, so
            // a half-written first pass must not survive into the second.
            Assert.Equal(SisImportStatus.CompletedWithWarnings, batch.Status);
            Assert.Equal(ExpectedStudents, await retry.Students.CountAsync());
        }
    }

    /// <summary>
    /// Cancels a token once the context has committed one <c>SaveChanges</c>, which in
    /// <c>RunAsync</c> is the first save inside <c>ExecuteAsync</c> — the dimension pass.
    ///
    /// <para>
    /// It used to be the <c>Running</c> status write. That is now the ADR-004 D-54.4 claim, an
    /// <c>ExecuteUpdate</c> that never reaches the save pipeline and so cannot be counted here. What
    /// the test needs is unchanged either way: the cancellation has to land after the batch is
    /// <c>Running</c> and inside the run, and both saves are.
    /// </para>
    ///
    /// <para>
    /// <c>SavedChangesAsync</c> rather than <c>SavingChangesAsync</c>: the status has to be
    /// <em>committed</em> before the cancellation lands, or the test is arranging a different failure
    /// from the one it is named after.
    /// </para>
    /// </summary>
    private sealed class CancelAfterFirstSave(CancellationTokenSource source) : SaveChangesInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                source.Cancel();
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }
}
