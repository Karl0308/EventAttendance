using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-001 D-2: <c>Students.Course</c> / <c>YearLevel</c> / <c>Section</c> are a derived read-only
/// cache, and the guard that makes "read-only" true rather than aspirational.
///
/// <para>
/// <b>What is actually being defended.</b> Twelve of the fifty-two students in the real roster sit in
/// more than one section, so a single-valued <c>Section</c> is wrong for 23% of them by construction.
/// It survives only because the SPA binds it. The danger is not that it is wrong today — it is that
/// the Phase 2 importer has <c>SECTION_NAME</c> sitting right there in the source row and every reason
/// to write it, at which point there are two disagreeing answers to "what section is this student in"
/// and no error anywhere. The guard makes that write impossible instead of discouraged.
/// </para>
///
/// <para>
/// The guard lives in <c>SaveChanges</c> rather than in a service because there is no student write
/// endpoint to put it in front of, and because the writer it is aimed at is not an endpoint.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AcademicCacheGuardTests : IntegrationTest
{
    public AcademicCacheGuardTests(SqlServerFixture sql) : base(sql) { }

    private async Task<Guid> ArrangeStudentAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    [Theory]
    [InlineData(nameof(Student.Course), "BSCRIM")]
    [InlineData(nameof(Student.YearLevel), "2nd Year")]
    [InlineData(nameof(Student.Section), "BSFS 2-A")]
    public async Task Updating_a_derived_cache_column_is_refused(string propertyName, string newValue)
    {
        var studentId = await ArrangeStudentAsync();

        await using var db = NewDbContext();
        var student = await db.Students.SingleAsync(s => s.Id == studentId);
        switch (propertyName)
        {
            case nameof(Student.Course): student.Course = newValue; break;
            case nameof(Student.YearLevel): student.YearLevel = newValue; break;
            default: student.Section = newValue; break;
        }

        var rejected = await Assert.ThrowsAsync<AcademicCacheWriteException>(() => db.SaveChangesAsync());

        Assert.Equal(propertyName, rejected.PropertyName);
        Assert.Equal("2023-0001", rejected.StudentNumber);
    }

    /// <summary>
    /// The message has to teach, because the person who hits this is mid-import and does not yet know
    /// the academic layer exists. It names the tables to write instead and the seam that permits the
    /// refresh — everything needed to act, without opening the ADR.
    /// </summary>
    [Fact]
    public async Task The_refusal_names_the_tables_to_write_instead()
    {
        var studentId = await ArrangeStudentAsync();

        await using var db = NewDbContext();
        (await db.Students.SingleAsync(s => s.Id == studentId)).Section = "BSFS 2-A";

        var rejected = await Assert.ThrowsAsync<AcademicCacheWriteException>(() => db.SaveChangesAsync());

        Assert.Contains("Enrollments", rejected.Message);
        Assert.Contains("StudentTermRecords", rejected.Message);
        Assert.Contains("BeginAcademicCacheRefresh", rejected.Message);
    }

    /// <summary>Nothing is written — the guard runs before <c>SaveChanges</c> reaches the database.</summary>
    [Fact]
    public async Task A_refused_update_writes_nothing()
    {
        var studentId = await ArrangeStudentAsync();

        await using (var db = NewDbContext())
        {
            var student = await db.Students.SingleAsync(s => s.Id == studentId);
            student.Section = "BSFS 2-A";
            student.Email = "changed@test.local";
            await Assert.ThrowsAsync<AcademicCacheWriteException>(() => db.SaveChangesAsync());
        }

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync(s => s.Id == studentId);
        Assert.Equal("A", stored.Section);
        Assert.Equal("2023-0001@test.local", stored.Email);
    }

    /// <summary>
    /// Inserts are allowed. The §4 seed and every fixture populate these columns on creation, and a new
    /// row carrying a display value is not the failure mode — divergence over time is, and that needs
    /// an update.
    /// </summary>
    [Fact]
    public async Task Inserting_a_student_with_the_cache_columns_populated_is_allowed()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        db.Students.Add(TestData.NewStudent(school.Id, "2023-0009", lastName: "Flores"));
        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal("A", (await read.Students.SingleAsync(s => s.StudentNumber == "2023-0009")).Section);
    }

    /// <summary>
    /// The guard is narrow. Everything else about a student is still writable, or it would have turned
    /// a read-only cache into a read-only table.
    /// </summary>
    [Fact]
    public async Task Updating_any_other_student_column_is_unaffected()
    {
        var studentId = await ArrangeStudentAsync();

        await using (var db = NewDbContext())
        {
            var student = await db.Students.SingleAsync(s => s.Id == studentId);
            student.Email = "new@test.local";
            student.Status = "Graduated";
            student.AcademicCacheUpdatedAt = TestData.Now;
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync(s => s.Id == studentId);
        Assert.Equal("new@test.local", stored.Email);
        Assert.Equal("Graduated", stored.Status);
        Assert.Equal(TestData.Now, stored.AcademicCacheUpdatedAt);
    }

    /// <summary>
    /// Assigning the value it already holds is not a change, and refusing it would be a guard that
    /// fires on nothing happening — which is how a guard gets deleted.
    /// </summary>
    [Fact]
    public async Task Rewriting_a_cache_column_with_its_current_value_is_not_a_change()
    {
        var studentId = await ArrangeStudentAsync();

        await using var db = NewDbContext();
        var student = await db.Students.SingleAsync(s => s.Id == studentId);
        var unchanged = student.Section;
        student.Section = unchanged;
        student.Email = "touched@test.local";

        await db.SaveChangesAsync();

        await using var read = NewDbContext();
        Assert.Equal("touched@test.local",
            (await read.Students.AsNoTracking().SingleAsync(s => s.Id == studentId)).Email);
    }

    /// <summary>
    /// The bypass the guard originally had, pinned shut.
    ///
    /// <para>
    /// <c>DbSet.Update(detached)</c> is the shape a REST PUT handler takes, and it is exactly the
    /// shape that used to slip through. Attaching a detached entity populates <c>OriginalValues</c>
    /// from the entity's <em>own current values</em> — there is no snapshot to compare against — so a
    /// genuinely changed <c>Section</c> arrived with <c>Current == Original</c>, and a
    /// value-equality skip in the guard waved it through without a sound.
    /// </para>
    ///
    /// <para>
    /// Phase 3 adds student edit endpoints, so this is not a hypothetical path — it is the next one
    /// to be written. Without this test the guard would read as covering it while doing the
    /// opposite.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Updating_a_detached_student_cannot_slip_past_the_guard()
    {
        var studentId = await ArrangeStudentAsync();

        Student detached;
        await using (var load = NewDbContext())
            detached = await load.Students.AsNoTracking().SingleAsync(s => s.Id == studentId);

        var original = detached.Section;
        detached.Section = "BSFS 2-A";

        await using var db = NewDbContext();
        db.Students.Update(detached);

        var ex = await Assert.ThrowsAsync<AcademicCacheWriteException>(() => db.SaveChangesAsync());

        // It names Course, not Section, and that is correct rather than a near-miss: Update() marks
        // every property modified, so all three cache columns are in violation and the guard reports
        // the first one it reaches. Asserting "Section" specifically would pin an iteration order
        // that carries no meaning — what matters is that a detached update is refused by name.
        Assert.Contains(ex.Message.Split(' ')[0].Split('.')[^1], Student.DerivedAcademicPropertyNames);

        await using var read = NewDbContext();
        Assert.Equal(original, (await read.Students.AsNoTracking().SingleAsync(s => s.Id == studentId)).Section);
    }

    /// <summary>
    /// The refresh seam. It exists so that the day a refresher is written the rule is <em>narrowed</em>
    /// rather than deleted — a writer that needs these columns names this method, and the write path
    /// stays greppable.
    /// </summary>
    [Fact]
    public async Task The_refresh_scope_permits_the_write_and_closes_behind_itself()
    {
        var studentId = await ArrangeStudentAsync();

        await using var db = NewDbContext();
        var student = await db.Students.SingleAsync(s => s.Id == studentId);

        using (db.BeginAcademicCacheRefresh())
        {
            student.Section = "BSFS 2-A";
            student.AcademicCacheUpdatedAt = TestData.Now;
            await db.SaveChangesAsync();
        }

        // Scope disposed: the guard is back on, and this is what proves it is not a one-way switch.
        student.Section = "BS Chem 2-A";
        await Assert.ThrowsAsync<AcademicCacheWriteException>(() => db.SaveChangesAsync());

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync(s => s.Id == studentId);
        Assert.Equal("BSFS 2-A", stored.Section);
        Assert.Equal(TestData.Now, stored.AcademicCacheUpdatedAt);
    }

    /// <summary>
    /// Nothing in production opens the refresh scope today, and that is the correct state: no refresher
    /// was written, the trigger is still an open ADR-001 follow-up, and the columns hold whatever the
    /// §4 seed or import left. Asserted so that when one appears it is a visible change rather than a
    /// quiet one — and so nobody reads the seam's existence as a claim that the cache is being kept up
    /// to date.
    /// </summary>
    [Fact]
    public async Task No_student_row_has_a_refreshed_academic_cache_yet()
    {
        await ArrangeStudentAsync();

        await using var read = NewDbContext();
        Assert.Empty(await read.Students.Where(s => s.AcademicCacheUpdatedAt != null).ToListAsync());
    }
}
