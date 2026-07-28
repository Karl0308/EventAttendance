using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The ADR-001 D-1 academic layer's guards, asserted against the migrated database rather than
/// against the EF model.
///
/// <para>
/// <b>Why the metadata half exists at all.</b> The SQL Server provider silently appends
/// <c>IS NOT NULL</c> to any unique index that covers a nullable column. That is not a theoretical
/// hazard here — it shipped once on <c>UX_Attendance_Event_Student_Occurrence</c> and left the entire
/// attendance table unconstrained while the index still appeared in every schema diff and every model
/// snapshot. These indexes are the only thing standing between a dirty roster and duplicate courses,
/// duplicate sections and duplicate enrollments, so each one's <c>filter_definition</c> is read back
/// out of <c>sys.indexes</c> and asserted. A model-level assertion would have passed in that incident.
/// </para>
///
/// <para>
/// Each metadata assertion is paired with the behaviour it buys, attempted for real and required to be
/// rejected. Neither half is sufficient: behaviour alone does not say <em>why</em> a guard broke, and
/// metadata alone does not prove the guard covers the rows that actually exist.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AcademicSchemaTests : IntegrationTest
{
    public AcademicSchemaTests(SqlServerFixture sql) : base(sql) { }

    private static readonly int[] UniqueViolation = [2601, 2627];
    private static readonly int[] ConstraintViolation = [547];

    private sealed record IndexInfo(bool IsUnique, bool HasFilter, string? FilterDefinition);

    private async Task<IndexInfo> ReadIndexAsync(string indexName)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT is_unique, has_filter, filter_definition FROM sys.indexes WHERE name = @name;",
            connection);
        command.Parameters.AddWithValue("@name", indexName);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Index {indexName} does not exist in the migrated schema.");

        return new IndexInfo(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<DbUpdateException> AssertRejectedAsync(Func<Task> act) =>
        await Assert.ThrowsAsync<DbUpdateException>(act);

    private static SqlException AssertSqlError(DbUpdateException ex, int[] expectedNumbers)
    {
        var sql = Assert.IsType<SqlException>(ex.InnerException);
        Assert.Contains(sql.Number, expectedNumbers);
        return sql;
    }

    private sealed record World(Guid SchoolId, Guid TermId, Guid CollegeId, Guid CourseId, Guid StudentId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        var college = TestData.NewCollege(school.Id);
        db.Colleges.Add(college);
        var course = TestData.NewCourse(school.Id);
        db.Courses.Add(course);
        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id, college.Id, course.Id, student.Id);
    }

    // ------------------------------------------------------- every unique index, from sys.indexes

    /// <summary>
    /// The natural-key indexes that must be <b>unfiltered</b>. Every key component of each is NOT NULL
    /// by design (see <see cref="AcademicKey.Unspecified"/>), so the provider has no nullable column to
    /// attach an <c>IS NOT NULL</c> to — and this test is what proves that stayed true. Making any one
    /// of these columns nullable in a later change would silently reintroduce a filter and the index
    /// would stop constraining the rows it was built for.
    /// </summary>
    [Theory]
    [InlineData("UX_Terms_SchoolId_Code")]
    [InlineData("UX_Colleges_SchoolId_NameKey")]
    [InlineData("UX_Programs_SchoolId_CodeKey")]
    [InlineData("UX_Courses_SchoolId_CodeKey")]
    [InlineData("UX_Instructors_SchoolId_NameKey")]
    [InlineData("UX_CourseOfferings_Term_Course_Section")]
    [InlineData("UX_CourseOfferingInstructors_Offering_Instructor")]
    [InlineData("UX_Enrollments_Student_CourseOffering")]
    [InlineData("UX_StudentTermRecords_Student_Term")]
    public async Task The_natural_key_index_is_unique_and_unfiltered(string indexName)
    {
        var index = await ReadIndexAsync(indexName);

        Assert.True(index.IsUnique, $"{indexName} is not unique.");
        Assert.False(
            index.HasFilter,
            $"{indexName} is filtered on {index.FilterDefinition}. Nothing in this index's key is " +
            "nullable, so a filter can only have come from the SQL Server provider appending " +
            "IS NOT NULL to a column that has since been made nullable — which would leave the rows " +
            "with a NULL in that column entirely unconstrained.");
        Assert.Null(index.FilterDefinition);
    }

    /// <summary>
    /// Filtered on purpose: a school has at most one current term, and every other term is
    /// unconstrained. Without the filter this would allow one term per school, full stop.
    /// </summary>
    [Fact]
    public async Task The_current_term_index_is_unique_and_filtered_to_the_current_term()
    {
        var index = await ReadIndexAsync("UX_Terms_SchoolId_Current");

        Assert.True(index.IsUnique);
        Assert.True(index.HasFilter);
        Assert.Contains("IsCurrent", index.FilterDefinition!);
    }

    /// <summary>
    /// Filtered on purpose, and the same reading §4.10's nullable <c>Devices.ApiKey</c> needed: SQL
    /// Server treats NULLs as equal inside a unique index, so an unfiltered version would permit
    /// exactly one instructor with no SIS id — which is all nineteen of them.
    /// </summary>
    [Fact]
    public async Task The_instructor_external_id_index_is_unique_and_filtered_to_rows_that_have_one()
    {
        var index = await ReadIndexAsync("UX_Instructors_SchoolId_ExternalId");

        Assert.True(index.IsUnique);
        Assert.True(index.HasFilter);
        Assert.Contains("ExternalId", index.FilterDefinition!);
        Assert.Contains("IS NOT NULL", index.FilterDefinition!);
    }

    /// <summary>
    /// The projection's upsert key. Two things have to be true and only one of them is obvious.
    ///
    /// <para>
    /// It must be filtered to <c>SourceType = 'Derived'</c>, because manual groups all share
    /// <c>SourceEntityType = 'None'</c> and <c>SourceKey = ''</c> and would collide with each other
    /// instantly. And the filter must be <em>that</em> and not the provider's — <c>TermId</c> is
    /// nullable, so left alone the provider would have written
    /// <c>[TermId] IS NOT NULL</c>, producing an index that appears to work (derived groups always
    /// have a term) while placing no constraint on the thing it was created to constrain.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_derived_group_index_is_filtered_on_the_source_type_and_not_on_the_nullable_term()
    {
        var index = await ReadIndexAsync("UX_StudentGroups_Derived_Source");

        Assert.True(index.IsUnique);
        Assert.True(index.HasFilter);
        Assert.Contains("SourceType", index.FilterDefinition!);
        Assert.Contains("Derived", index.FilterDefinition!);
        Assert.DoesNotContain("TermId", index.FilterDefinition!);
    }

    /// <summary>
    /// The index EF wanted to drop when the composite above appeared. A filtered index cannot serve an
    /// unfiltered predicate, so without this one every §11 tenant-scoped read of a manual group would
    /// have gone to a table scan — a performance regression with no visible symptom.
    /// </summary>
    [Fact]
    public async Task The_plain_school_index_on_student_groups_survives_the_new_composite()
    {
        var index = await ReadIndexAsync("IX_StudentGroups_SchoolId");

        Assert.False(index.IsUnique);
        Assert.False(index.HasFilter);
    }

    // ------------------------------------------------------------------- the behaviour they buy

    /// <summary>
    /// The single most important assertion in this file. <c>'SSCI 7'</c> and <c>'SSci7'</c> are the
    /// same course in the real export; if the schema accepted both, the roster would silently carry
    /// two courses, two sets of offerings, and one class split across them.
    /// </summary>
    [Fact]
    public async Task Two_spellings_of_one_course_code_cannot_both_exist()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        db.Courses.Add(TestData.NewCourse(world.SchoolId, "SSci7", title: "Intro to Criminology"));

        var rejected = await AssertRejectedAsync(() => db.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_Courses_SchoolId_CodeKey", sql.Message);
    }

    /// <summary>
    /// The tenant half of the same index, matching <c>UNIQUE(SchoolId, StudentNumber)</c> on Students:
    /// two universities may each teach a course called <c>SSCI 7</c>.
    /// </summary>
    [Fact]
    public async Task The_same_course_code_is_allowed_in_a_different_school()
    {
        await ArrangeAsync();

        await using var db = NewDbContext();
        var other = TestData.NewSchool("CICSS");
        db.Schools.Add(other);
        db.Courses.Add(TestData.NewCourse(other.Id, "SSCI 7"));
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(2, await read.Courses.CountAsync(c => c.CodeKey == "SSCI7"));
    }

    /// <summary>
    /// The blank-section case, which is what the sentinel exists for. Two offerings of the <em>same</em>
    /// course with no section named are the same offering and must collide.
    /// </summary>
    [Fact]
    public async Task Two_blank_section_offerings_of_one_course_collide()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.CourseOfferings.Add(TestData.NewOffering(world.TermId, world.CourseId, sectionName: null));
            await db.SaveChangesAsync();
        }

        await using var second = NewDbContext();
        second.CourseOfferings.Add(TestData.NewOffering(world.TermId, world.CourseId, sectionName: "  "));

        var rejected = await AssertRejectedAsync(() => second.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_CourseOfferings_Term_Course_Section", sql.Message);
    }

    /// <summary>
    /// And the negative that proves the sentinel is not a cap. 39 sample rows have a blank section
    /// across many different courses; under a nullable <c>SectionKey</c> the first would have been
    /// accepted and the other 38 rejected by SQL Server's one-NULL rule.
    /// </summary>
    [Fact]
    public async Task Many_courses_may_each_have_a_blank_section_offering()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var second = TestData.NewCourse(world.SchoolId, "CA  2", title: "Criminalistics 2");
        var third = TestData.NewCourse(world.SchoolId, "GE Elect 2", title: "Elective");
        db.Courses.AddRange(second, third);
        db.CourseOfferings.Add(TestData.NewOffering(world.TermId, world.CourseId, null));
        db.CourseOfferings.Add(TestData.NewOffering(world.TermId, second.Id, null));
        db.CourseOfferings.Add(TestData.NewOffering(world.TermId, third.Id, ""));
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(3, await read.CourseOfferings.CountAsync(o => o.SectionKey == AcademicKey.Unspecified));
    }

    /// <summary>
    /// The whole reason the layer exists: one student, two sections. Impossible in §4.3's
    /// single-valued <c>Students.Section</c>, ordinary here.
    /// </summary>
    [Fact]
    public async Task One_student_may_be_enrolled_in_offerings_from_two_different_sections()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var chemistry = TestData.NewCourse(world.SchoolId, "CHEM 1", title: "General Chemistry");
        db.Courses.Add(chemistry);
        var criminology = TestData.NewOffering(world.TermId, world.CourseId, "BSFS 2-A");
        var chem = TestData.NewOffering(world.TermId, chemistry.Id, "BS Chem 2-A");
        db.CourseOfferings.AddRange(criminology, chem);
        db.Enrollments.Add(TestData.NewEnrollment(world.StudentId, criminology.Id));
        db.Enrollments.Add(TestData.NewEnrollment(world.StudentId, chem.Id));
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(2, await read.Enrollments.CountAsync(e => e.StudentId == world.StudentId));
    }

    [Fact]
    public async Task The_same_student_cannot_be_enrolled_in_one_offering_twice()
    {
        var world = await ArrangeAsync();

        Guid offeringId;
        await using (var db = NewDbContext())
        {
            var offering = TestData.NewOffering(world.TermId, world.CourseId);
            db.CourseOfferings.Add(offering);
            db.Enrollments.Add(TestData.NewEnrollment(world.StudentId, offering.Id));
            await db.SaveChangesAsync();
            offeringId = offering.Id;
        }

        await using var second = NewDbContext();
        second.Enrollments.Add(TestData.NewEnrollment(world.StudentId, offeringId));

        var rejected = await AssertRejectedAsync(() => second.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_Enrollments_Student_CourseOffering", sql.Message);
    }

    /// <summary>
    /// A team-taught section arrives as several otherwise identical source rows differing only in the
    /// teacher. The junction is what turns them into one offering with several teachers; the unique
    /// index is what stops a re-import turning them into several copies of the same link.
    /// </summary>
    [Fact]
    public async Task An_offering_may_have_several_instructors_but_not_the_same_one_twice()
    {
        var world = await ArrangeAsync();

        Guid offeringId, instructorId;
        await using (var db = NewDbContext())
        {
            var offering = TestData.NewOffering(world.TermId, world.CourseId);
            db.CourseOfferings.Add(offering);
            var first = TestData.NewInstructor(world.SchoolId, "Dela Cruz, Juan");
            var second = TestData.NewInstructor(world.SchoolId, "TO BE ANNOUNCE");
            db.Instructors.AddRange(first, second);
            db.CourseOfferingInstructors.Add(new CourseOfferingInstructor
            {
                CourseOfferingId = offering.Id, InstructorId = first.Id,
            });
            db.CourseOfferingInstructors.Add(new CourseOfferingInstructor
            {
                CourseOfferingId = offering.Id, InstructorId = second.Id,
            });
            await db.SaveChangesAsync();
            offeringId = offering.Id;
            instructorId = first.Id;
        }

        await using var replay = NewDbContext();
        replay.CourseOfferingInstructors.Add(new CourseOfferingInstructor
        {
            CourseOfferingId = offeringId, InstructorId = instructorId,
        });

        var rejected = await AssertRejectedAsync(() => replay.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_CourseOfferingInstructors_Offering_Instructor", sql.Message);
    }

    /// <summary>
    /// Nineteen instructors, none of whom has a SIS id. The filtered index is the only reason more
    /// than one of them can exist.
    /// </summary>
    [Fact]
    public async Task Many_instructors_may_have_no_external_id_but_two_may_not_share_one()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.Instructors.Add(TestData.NewInstructor(world.SchoolId, "Dela Cruz, Juan"));
            db.Instructors.Add(TestData.NewInstructor(world.SchoolId, "Santos, Maria"));
            db.Instructors.Add(TestData.NewInstructor(world.SchoolId, "TO BE ANNOUNCE"));
            db.Instructors.Add(TestData.NewInstructor(world.SchoolId, "Lim, Andrea", externalId: "T-001"));
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        Assert.Equal(3, await read.Instructors.CountAsync(i => i.ExternalId == null));

        await using var clash = NewDbContext();
        clash.Instructors.Add(TestData.NewInstructor(world.SchoolId, "Tan, Miguel", externalId: "T-001"));

        var rejected = await AssertRejectedAsync(() => clash.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_Instructors_SchoolId_ExternalId", sql.Message);
    }

    /// <summary>
    /// The soft-merge pointer must not point at itself — an instructor merged into themselves is a
    /// cycle of length one, and every reader that follows the pointer would loop.
    /// </summary>
    [Fact]
    public async Task An_instructor_cannot_be_merged_into_themselves()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var instructor = TestData.NewInstructor(world.SchoolId, "Dela Cruz, Juan");
        db.Instructors.Add(instructor);
        await db.SaveChangesAsync();

        instructor.MergedIntoInstructorId = instructor.Id;

        var rejected = await AssertRejectedAsync(() => db.SaveChangesAsync());
        var sql = AssertSqlError(rejected, ConstraintViolation);
        Assert.Contains("CK_Instructors_NoSelfMerge", sql.Message);
    }

    /// <summary>
    /// The soft merge itself, which is the point of never deleting: both rows survive and the
    /// historical <c>CourseOfferingInstructors</c> link keeps resolving.
    /// </summary>
    [Fact]
    public async Task A_duplicate_instructor_can_be_merged_without_losing_either_row()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var canonical = TestData.NewInstructor(world.SchoolId, "Dela Cruz, Juan");
        var duplicate = TestData.NewInstructor(world.SchoolId, "Dela Cruz, Juan P.");
        db.Instructors.AddRange(canonical, duplicate);
        await db.SaveChangesAsync();

        duplicate.MergedIntoInstructorId = canonical.Id;
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(2, await read.Instructors.CountAsync());
        Assert.Equal(canonical.Id, (await read.Instructors.SingleAsync(i => i.Id == duplicate.Id))
            .MergedIntoInstructorId);
    }

    [Fact]
    public async Task A_school_may_have_only_one_current_term()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        db.Terms.Add(TestData.NewTerm(world.SchoolId, "2025-2026-2", isCurrent: true));

        var rejected = await AssertRejectedAsync(() => db.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_Terms_SchoolId_Current", sql.Message);
    }

    [Fact]
    public async Task A_school_may_have_many_terms_that_are_not_current()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        db.Terms.Add(TestData.NewTerm(world.SchoolId, "2024-2025-1", isCurrent: false));
        db.Terms.Add(TestData.NewTerm(world.SchoolId, "2024-2025-2", isCurrent: false));
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal(3, await read.Terms.CountAsync(t => t.SchoolId == world.SchoolId));
        Assert.Single(await read.Terms.Where(t => t.SchoolId == world.SchoolId && t.IsCurrent).ToListAsync());
    }

    /// <summary>
    /// A student has one placement per term, and many across terms. The natural key is
    /// <c>(StudentId, TermId)</c>, so re-running an import for the same term updates rather than
    /// duplicating.
    /// </summary>
    [Fact]
    public async Task A_student_has_at_most_one_record_per_term()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.StudentTermRecords.Add(
                TestData.NewTermRecord(world.StudentId, world.TermId, collegeId: world.CollegeId));
            await db.SaveChangesAsync();
        }

        await using var second = NewDbContext();
        second.StudentTermRecords.Add(TestData.NewTermRecord(world.StudentId, world.TermId));

        var rejected = await AssertRejectedAsync(() => second.SaveChangesAsync());
        var sql = AssertSqlError(rejected, UniqueViolation);
        Assert.Contains("UX_StudentTermRecords_Student_Term", sql.Message);
    }

    /// <summary>
    /// Hard deletes stay restricted across the new tables too (the model applies
    /// <c>DeleteBehavior.Restrict</c> to every foreign key). Removing a course that has offerings must
    /// fail loudly rather than cascade away a term's enrollments.
    /// </summary>
    [Fact]
    public async Task Deleting_a_course_that_has_offerings_is_refused_rather_than_cascaded()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.CourseOfferings.Add(TestData.NewOffering(world.TermId, world.CourseId));
            await db.SaveChangesAsync();
        }

        await using var remove = NewDbContext();
        remove.Courses.Remove(await remove.Courses.SingleAsync(c => c.Id == world.CourseId));

        var rejected = await AssertRejectedAsync(() => remove.SaveChangesAsync());
        AssertSqlError(rejected, ConstraintViolation);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.CourseOfferings.CountAsync());
    }

    [Fact]
    public async Task The_migration_history_records_the_academic_layer_on_top_of_the_baseline()
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM __EFMigrationsHistory " +
            "WHERE MigrationId LIKE '%Section4Baseline' OR MigrationId LIKE '%AcademicLayer';",
            connection);

        Assert.Equal(2, (int)(await command.ExecuteScalarAsync())!);
    }
}
