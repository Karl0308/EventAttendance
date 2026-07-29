using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Technical Plan §6.2's write surface: create, update, soft delete, and §4.4 card assignment.
///
/// <para>
/// Two things get more attention than the field validation, because they are the halves that are not
/// obviously right. The first is the ADR-001 D-2 derived cache: a rejected over-length name is a
/// visible 400, while a silently accepted <c>section</c> writes nothing, answers 200, and is
/// discovered months later when a report disagrees with <c>Enrollments</c> for the 23% of students who
/// sit in more than one. The second is uniqueness under concurrency: both the student number and the
/// card UID are defended by an index rather than by a read, and an unhandled violation is a 500 on the
/// ordinary case of a double-submitted form.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentWriteTests : IntegrationTest
{
    public StudentWriteTests(SqlServerFixture sql) : base(sql) { }

    private static StudentWriteRequest Request(
        string studentNumber = "2023-0001",
        string firstName = "Maria",
        string? middleName = "Reyes",
        string lastName = "Santos",
        string? email = "Maria.Santos@usa.edu.ph",
        string? gender = "Female",
        string? photoUrl = null,
        string? status = null,
        IDictionary<string, JsonElement>? unmapped = null) =>
        new(studentNumber, firstName, middleName, lastName, email, gender, photoUrl, status)
        {
            UnmappedFields = unmapped,
        };

    /// <summary>
    /// What a client that echoed the object it read back at a PUT actually sends: members this
    /// contract does not model, landing in <c>UnmappedFields</c>.
    /// </summary>
    private static IDictionary<string, JsonElement> Sent(params (string Name, string Value)[] members) =>
        members.ToDictionary(m => m.Name, m => JsonSerializer.SerializeToElement(m.Value));

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> ArrangeStudentAsync(Guid schoolId, string studentNumber = "2023-0001")
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, studentNumber);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    // -------------------------------------------------------------------------------- create

    [Fact]
    public async Task A_created_student_carries_every_field_it_was_given()
    {
        var schoolId = await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request(
            studentNumber: "  2023-0042  ", firstName: "  Juan  ", lastName: "Dela Cruz",
            gender: "Male", status: "graduated"));

        Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
        Assert.NotNull(response.Student);

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync();

        Assert.Equal(schoolId, stored.SchoolId);
        // Cleaned on the way in, through the same RosterText the §10 importer uses — a manual entry
        // and an imported one have to produce the same string or they are two students.
        Assert.Equal("2023-0042", stored.StudentNumber);
        Assert.Equal("Juan", stored.FirstName);
        Assert.Equal("Dela Cruz", stored.LastName);
        // Canonicalized, not rejected: liberal in what is accepted, canonical in what is stored.
        Assert.Equal(StudentStatus.Graduated, stored.Status);
        // Lower-cased for the reason RosterText.CleanEmail records: the roster spells one address
        // both ways and every comparison here is ordinal.
        Assert.Equal("maria.santos@usa.edu.ph", stored.Email);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task A_created_student_defaults_to_the_section_4_3_status()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request(status: null));

        Assert.Equal(StudentStatus.Active, response.Student!.Status);
    }

    /// <summary>
    /// The derived cache columns are never populated by a create, however the request was shaped.
    /// A new student's course and section come from <c>Enrollments</c>, or from nowhere.
    /// </summary>
    [Fact]
    public async Task A_created_student_has_no_derived_cache_values()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        await StudentsOn(db).CreateAsync(Request());

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync();

        Assert.Null(stored.Course);
        Assert.Null(stored.YearLevel);
        Assert.Null(stored.Section);
        Assert.Null(stored.AcademicCacheUpdatedAt);
    }

    [Theory]
    [InlineData("", "blank student number")]
    [InlineData("   ", "whitespace student number")]
    public async Task A_blank_student_number_is_rejected(string number, string _)
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request(studentNumber: number));

        Assert.Equal(StudentWriteOutcome.ValidationFailed, response.Outcome);
    }

    [Fact]
    public async Task A_blank_required_name_is_rejected()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();

        Assert.Equal(StudentWriteOutcome.ValidationFailed,
            (await StudentsOn(db).CreateAsync(Request(firstName: "  "))).Outcome);
        Assert.Equal(StudentWriteOutcome.ValidationFailed,
            (await StudentsOn(db).CreateAsync(Request(lastName: ""))).Outcome);
    }

    /// <summary>
    /// §4.3's widths, as a 400 rather than the SQL truncation 500 (error 2628) they used to be
    /// everywhere before <c>DomainValues</c> started naming column lengths.
    /// </summary>
    [Fact]
    public async Task An_over_length_field_is_rejected_rather_than_truncated()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(
            Request(firstName: new string('a', StudentText.NameMaxLength + 1)));

        Assert.Equal(StudentWriteOutcome.ValidationFailed, response.Outcome);
        Assert.Contains("FirstName", response.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Validation and storage must share one definition of "empty".</b>
    ///
    /// <para>
    /// They did not. <c>Validate</c> ran <c>string.IsNullOrWhiteSpace</c> + <c>Trim()</c> on the raw
    /// request while the writer stored <c>RosterText.Clean(...)</c>, which additionally strips
    /// zero-width characters. Those are Unicode category <c>Cf</c>, <b>not</b> whitespace — so
    /// <c>char.IsWhiteSpace('​')</c> is false and <c>"​".Trim().Length</c> is 1. A name of
    /// one zero-width space passed validation, cleaned to <c>null</c>, and hit a NOT NULL
    /// <c>nvarchar(100)</c> as SQL Server error 515 — an unhandled <c>DbUpdateException</c> and a
    /// <b>500 on malformed input</b>, which is the exact defect class the length constants exist to
    /// remove.
    /// </para>
    ///
    /// <para>
    /// All five characters <c>RosterText</c> strips are covered, and all three NOT NULL columns,
    /// because the gap belongs to the pair of rules rather than to any one character or field.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("​")] // zero-width space
    [InlineData("‌")] // zero-width non-joiner
    [InlineData("‍")] // zero-width joiner
    [InlineData("⁠")] // word joiner
    [InlineData("﻿")] // byte-order mark
    public async Task A_required_field_of_only_zero_width_characters_is_a_400_and_not_a_500(
        string invisible)
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var students = StudentsOn(db);

        Assert.Equal(StudentWriteOutcome.ValidationFailed,
            (await students.CreateAsync(Request(firstName: invisible))).Outcome);
        Assert.Equal(StudentWriteOutcome.ValidationFailed,
            (await students.CreateAsync(Request(lastName: invisible))).Outcome);
        Assert.Equal(StudentWriteOutcome.ValidationFailed,
            (await students.CreateAsync(Request(studentNumber: invisible))).Outcome);

        await using var read = NewDbContext();
        Assert.Empty(await read.Students.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The other half of the same disagreement: length is measured on the value that gets written.
    ///
    /// <para>
    /// <c>RosterText</c> composes to NFC, and NFC can <em>expand</em> — U+0344 (combining Greek
    /// dialytika tonos) becomes U+0308 U+0301, one character into two. A name measured before
    /// composition therefore under-counts, and a request that validation calls exactly at the limit
    /// arrives at the column over it, as SQL Server error 2628. Measuring after composition is what
    /// makes the number the check compares against the number of characters actually stored.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_name_that_expands_under_nfc_is_measured_after_composition()
    {
        await ArrangeSchoolAsync();

        // 60 characters before composition, 120 after — over nvarchar(100) either way you look at it,
        // but only visible to a check that composes first.
        var expanding = new string('̈́', 60);
        Assert.True(expanding.Length <= StudentText.NameMaxLength);

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request(firstName: expanding));

        Assert.Equal(StudentWriteOutcome.ValidationFailed, response.Outcome);

        await using var read = NewDbContext();
        Assert.Empty(await read.Students.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task An_undocumented_status_is_rejected()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request(status: "Banana"));

        Assert.Equal(StudentWriteOutcome.ValidationFailed, response.Outcome);
    }

    // ------------------------------------------------------------------- ADR-001 D-2 cache

    /// <summary>
    /// The refusal this whole contract is shaped around.
    ///
    /// <para>
    /// <c>GET /students/{id}</c> returns <c>course</c>, <c>yearLevel</c> and <c>section</c>, so a client
    /// that reads a student, edits the name and PUTs the object back sends all three. The deserializer's
    /// default is to drop them silently and answer 200, which tells the caller it wrote a section it did
    /// not write. Named 400 instead, on the day the mistake is cheap to learn.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("course", "BSCRIM")]
    [InlineData("yearLevel", "2nd Year")]
    [InlineData("section", "BSFS 2-A")]
    [InlineData("Section", "BSFS 2-A")] // Pascal too: the wire is camelCase, the property is not.
    public async Task Supplying_a_derived_cache_field_is_refused_by_name(string member, string value)
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(
            Request(unmapped: Sent((member, value))));

        Assert.Equal(StudentWriteOutcome.FieldIsDerived, response.Outcome);
        Assert.Contains("Enrollments", response.Message, StringComparison.Ordinal);
        Assert.Contains("StudentTermRecords", response.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused <em>before</em> anything is written. A create that 400s must not leave a half-made
    /// student behind, and an update that 400s must not have applied the fields it did understand.
    /// </summary>
    [Fact]
    public async Task A_refused_derived_field_writes_nothing()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using (var db = NewDbContext())
        {
            var response = await StudentsOn(db).UpdateAsync(studentId, Request(
                firstName: "Renamed", unmapped: Sent(("section", "BSFS 2-A"))));

            Assert.Equal(StudentWriteOutcome.FieldIsDerived, response.Outcome);
        }

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync(s => s.Id == studentId);

        Assert.Equal("Maria", stored.FirstName);
        Assert.Equal("A", stored.Section);
    }

    /// <summary>
    /// Members this contract does not model but that are not derived — <c>id</c>, <c>fullName</c>,
    /// <c>cards</c> — stay ignored. Refusing those would make the round trip the tripwire exists to
    /// protect impossible to perform at all.
    /// </summary>
    [Fact]
    public async Task Unknown_members_that_are_not_derived_are_still_ignored()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request(
            unmapped: Sent(("id", Guid.NewGuid().ToString()), ("fullName", "Maria Reyes Santos"))));

        Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
    }

    /// <summary>
    /// The safety net, proved rather than asserted in a comment.
    ///
    /// <para>
    /// <c>AcademicCacheWriteException</c> is thrown from <c>SaveChanges</c> and is a 500 by nature. The
    /// service's own <c>Apply</c> never names a cache column, so the only way to reach it is another
    /// writer touching a tracked <c>Student</c> in the same unit of work — which is exactly what a
    /// future cache refresher will be. That must come back as the same named 400 a supplied field does,
    /// not as an unhandled exception.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_cache_guard_exception_never_escapes_the_write_path()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using var db = NewDbContext();

        // Someone else in this unit of work writes the cache column directly.
        (await db.Students.SingleAsync(s => s.Id == studentId)).Section = "BSFS 2-A";

        var response = await StudentsOn(db).UpdateAsync(studentId, Request(firstName: "Renamed"));

        Assert.Equal(StudentWriteOutcome.FieldIsDerived, response.Outcome);
        Assert.Contains("ADR-001 D-2", response.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The breach is <em>recorded</em>, not just handled.
    ///
    /// <para>
    /// A tripped cache guard means another writer mutated a tracked <c>Student</c> inside this unit of
    /// work — a programming error. The caller is given a 400, which is the right answer to give a
    /// client and the wrong place to leave the only trace: nobody reads someone else's 4xx as a bug
    /// report about their own code, so without a log the one signal that an invariant broke is
    /// delivered exclusively to the party who cannot act on it. Asserted at <c>Error</c>, carrying the
    /// exception, because a <c>Warning</c> in a stream of them is not a signal.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tripped_cache_guard_is_logged_as_an_error_and_not_only_returned()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        var logger = new CapturingLogger<StudentService>();

        await using var db = NewDbContext();
        (await db.Students.SingleAsync(s => s.Id == studentId)).Section = "BSFS 2-A";

        var response = await StudentsOn(db, logger).UpdateAsync(studentId, Request(firstName: "Renamed"));

        Assert.Equal(StudentWriteOutcome.FieldIsDerived, response.Outcome);

        var error = Assert.Single(logger.At(LogLevel.Error));
        Assert.IsType<AcademicCacheWriteException>(error.Exception);
        Assert.Contains(nameof(Student.Section), error.Message, StringComparison.Ordinal);
        Assert.Contains("2023-0001", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card writes have the same net, and need it for a reason that is easy to miss.
    ///
    /// <para>
    /// Neither <c>AddCardAsync</c> nor <c>DeactivateCardAsync</c> touches a <c>Students</c> column — but
    /// both save the unit of work that holds the tracked <c>Student</c> they loaded, so a cache column
    /// mutated on it anywhere else is raised by <em>their</em> <c>SaveChanges</c>. Without the net that
    /// is a 500 from a method that never mentioned the table.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_cache_guard_exception_never_escapes_the_card_writes_either()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        Guid cardId;
        await using (var arrange = NewDbContext())
        {
            var card = TestData.NewCard(schoolId, studentId, "04A7B8C9");
            arrange.RfidCards.Add(card);
            await arrange.SaveChangesAsync();
            cardId = card.Id;
        }

        await using (var db = NewDbContext())
        {
            var students = StudentsOn(db);
            // Load the student into this unit of work the way the card path does, then let something
            // else write the derived cache on it.
            await students.GetAsync(studentId);
            (await db.Students.SingleAsync(s => s.Id == studentId)).Section = "BSFS 2-A";

            var response = await students.AddCardAsync(
                studentId, new StudentCardRequest("0499FFEE", null));

            Assert.Equal(StudentWriteOutcome.FieldIsDerived, response.Outcome);
        }

        await using (var db = NewDbContext())
        {
            var students = StudentsOn(db);
            await students.GetAsync(studentId);
            (await db.Students.SingleAsync(s => s.Id == studentId)).Section = "BSFS 2-A";

            var response = await students.DeactivateCardAsync(studentId, cardId);

            Assert.Equal(StudentWriteOutcome.FieldIsDerived, response.Outcome);
        }
    }

    // ----------------------------------------------------------------------------- uniqueness

    [Fact]
    public async Task A_duplicate_student_number_is_a_conflict_and_not_a_second_row()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeStudentAsync(schoolId, "2023-0001");

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request(studentNumber: "2023-0001"));

        Assert.Equal(StudentWriteOutcome.DuplicateStudentNumber, response.Outcome);

        await using var read = NewDbContext();
        Assert.Single(await read.Students.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// <c>IX_Students_SchoolId_StudentNumber</c> is not filtered on <c>IsDeleted</c>, so a deleted
    /// student still holds its number. Named explicitly, because the fix differs from the ordinary
    /// clash: restore the row rather than pick another number.
    /// </summary>
    [Fact]
    public async Task A_student_number_held_by_a_deleted_student_is_still_taken()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId, "2023-0001");

        await using (var db = NewDbContext())
            await StudentsOn(db).DeleteAsync(studentId);

        await using var create = NewDbContext();
        var response = await StudentsOn(create).CreateAsync(Request(studentNumber: "2023-0001"));

        Assert.Equal(StudentWriteOutcome.DuplicateStudentNumber, response.Outcome);
        Assert.Contains("deleted", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same number in another school is a different student. <c>UNIQUE(SchoolId, StudentNumber)</c>
    /// is tenant-scoped, and a pre-check that forgot the school half would refuse a legitimate create
    /// for a reason nobody could see.
    /// </summary>
    [Fact]
    public async Task The_same_student_number_in_another_school_is_not_a_conflict()
    {
        var otherSchoolId = await ArrangeSchoolAsync("OTHER");
        await ArrangeStudentAsync(otherSchoolId, "2023-0001");

        var schoolId = await ArrangeSchoolAsync("USA");
        // Without a pinned tenant, "the only school" resolution has two candidates and refuses.
        School.CurrentSchoolId = schoolId;

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request(studentNumber: "2023-0001"));

        Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
    }

    /// <summary>
    /// Two operators adding the same new student at once, and the double-submitted form that produces
    /// the same thing on one operator's machine.
    ///
    /// <para>
    /// The read-then-insert in <c>CreateAsync</c> is not the guarantee —
    /// <c>IX_Students_SchoolId_StudentNumber</c> is — and the point of this test is that losing to the
    /// index is a 409 rather than an unhandled <c>DbUpdateException</c>. The
    /// <see cref="TaskCompletionSource"/> gate is load-bearing for the reason
    /// <c>TapFlowTests</c> records: without it the tasks start in sequence as the enumerable is
    /// materialized, the first wins comfortably, and the test passes without the race ever occurring.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_creates_of_one_student_number_write_exactly_one_row()
    {
        await ArrangeSchoolAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = NewDbContext();
            return await StudentsOn(db).CreateAsync(Request(studentNumber: "2023-0001"));
        })).ToArray();

        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        Assert.Single(responses, r => r.Outcome == StudentWriteOutcome.Saved);
        Assert.All(responses.Where(r => r.Outcome != StudentWriteOutcome.Saved),
            r => Assert.Equal(StudentWriteOutcome.DuplicateStudentNumber, r.Outcome));

        await using var read = NewDbContext();
        Assert.Single(await read.Students.AsNoTracking().ToListAsync());
    }

    // -------------------------------------------------------------------------------- update

    [Fact]
    public async Task An_updated_student_keeps_its_derived_cache_and_its_school()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using (var db = NewDbContext())
        {
            var response = await StudentsOn(db).UpdateAsync(studentId, Request(
                studentNumber: "2023-0002", firstName: "Maricel", middleName: "-",
                lastName: "Santos", email: null, status: "Inactive"));

            Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
        }

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync(s => s.Id == studentId);

        Assert.Equal("2023-0002", stored.StudentNumber);
        Assert.Equal("Maricel", stored.FirstName);
        // The roster's '-' placeholder folds to null exactly as an import would, so "Maricel - Santos"
        // never renders. See RosterText.CleanName.
        Assert.Null(stored.MiddleName);
        Assert.Null(stored.Email);
        Assert.Equal(StudentStatus.Inactive, stored.Status);

        // Untouched by a full-replacement PUT, which is the whole reason they are not on the request.
        Assert.Equal(schoolId, stored.SchoolId);
        Assert.Equal("BSIT", stored.Course);
        Assert.Equal("A", stored.Section);
    }

    [Fact]
    public async Task Updating_an_unknown_student_is_not_found()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).UpdateAsync(Guid.NewGuid(), Request());

        Assert.Equal(StudentWriteOutcome.NotFound, response.Outcome);
    }

    [Fact]
    public async Task Updating_a_student_onto_a_taken_number_is_a_conflict()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeStudentAsync(schoolId, "2023-0001");
        var secondId = await ArrangeStudentAsync(schoolId, "2023-0002");

        await using var db = NewDbContext();
        var response = await StudentsOn(db).UpdateAsync(secondId, Request(studentNumber: "2023-0001"));

        Assert.Equal(StudentWriteOutcome.DuplicateStudentNumber, response.Outcome);
    }

    /// <summary>
    /// The self-collision. A PUT that re-sends the student's own number must not be refused by the
    /// duplicate check — which is what an <c>excluding</c>-less lookup would do, making every edit
    /// that did not rename the student impossible.
    /// </summary>
    [Fact]
    public async Task Updating_a_student_without_changing_its_number_is_not_a_conflict()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId, "2023-0001");

        await using var db = NewDbContext();
        var response = await StudentsOn(db).UpdateAsync(
            studentId, Request(studentNumber: "2023-0001", firstName: "Maricel"));

        Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
    }

    // -------------------------------------------------------------------------------- delete

    [Fact]
    public async Task A_deleted_student_is_invisible_to_get_and_list_but_its_row_survives()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using (var db = NewDbContext())
        {
            var response = await StudentsOn(db).DeleteAsync(studentId);
            Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
        }

        await using var read = NewDbContext();
        var students = StudentsOn(read);

        Assert.Null(await students.GetAsync(studentId));
        Assert.Empty(await students.ListAsync(null, null, null));

        var stored = await read.Students.AsNoTracking().SingleAsync(s => s.Id == studentId);
        Assert.True(stored.IsDeleted);
    }

    [Fact]
    public async Task Deleting_a_deleted_student_is_not_found()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using (var db = NewDbContext())
            await StudentsOn(db).DeleteAsync(studentId);

        await using var second = NewDbContext();
        Assert.Equal(StudentWriteOutcome.NotFound, (await StudentsOn(second).DeleteAsync(studentId)).Outcome);
    }

    // --------------------------------------------------------------------------------- cards

    [Fact]
    public async Task An_assigned_card_is_stored_in_its_normalized_form()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using var db = NewDbContext();
        var response = await StudentsOn(db).AddCardAsync(
            studentId, new StudentCardRequest("04:a7:b8:c9", "Primary ID"));

        Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
        Assert.Equal("04A7B8C9", response.Card!.CardUid);

        await using var read = NewDbContext();
        var stored = await read.RfidCards.AsNoTracking().SingleAsync();

        Assert.Equal("04A7B8C9", stored.CardUid);
        Assert.True(stored.IsActive);
        Assert.Null(stored.DeactivatedAt);
        // ADR-001 D-3: denormalized from the owner, and the filtered unique index is only correct
        // while the two agree.
        Assert.Equal(schoolId, stored.SchoolId);
    }

    [Fact]
    public async Task A_card_uid_that_normalizes_to_nothing_is_rejected()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using var db = NewDbContext();
        var response = await StudentsOn(db).AddCardAsync(studentId, new StudentCardRequest(" :-: ", null));

        Assert.Equal(StudentWriteOutcome.ValidationFailed, response.Outcome);

        await using var read = NewDbContext();
        Assert.Empty(await read.RfidCards.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A null UID is the same refusal a blank one gets, not a <c>NullReferenceException</c>.
    ///
    /// <para>
    /// <c>StudentCardRequest.CardUid</c> is non-nullable by annotation, and that annotation binds
    /// nothing: <c>{"label":"x"}</c> deserializes it to null and <c>CardUid.Normalize</c> dereferences
    /// its argument. MVC answers 400 first over HTTP — <c>StudentsApiTests</c> pins that — but this
    /// asserts the service refuses it on its own, because <c>IStudentService</c> is what Phase 4
    /// publishes to the mobile developer and <c>Reject</c>'s own rule is that a guard living only at
    /// the HTTP boundary leaves the next non-HTTP caller unprotected.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_null_card_uid_is_refused_rather_than_dereferenced()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using var db = NewDbContext();
        var response = await StudentsOn(db).AddCardAsync(studentId, new StudentCardRequest(null!, null));

        Assert.Equal(StudentWriteOutcome.ValidationFailed, response.Outcome);

        await using var read = NewDbContext();
        Assert.Empty(await read.RfidCards.AsNoTracking().ToListAsync());
    }

    /// <summary>Re-posting a card a student already has is a no-op, so a double-submitted form is safe.</summary>
    [Fact]
    public async Task Assigning_a_card_a_student_already_has_writes_no_second_row()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using var db = NewDbContext();
        var first = await StudentsOn(db).AddCardAsync(studentId, new StudentCardRequest("04A7B8C9", null));
        var second = await StudentsOn(db).AddCardAsync(studentId, new StudentCardRequest("04-a7-b8-c9", null));

        Assert.Equal(StudentWriteOutcome.Saved, second.Outcome);
        Assert.Equal(first.Card!.Id, second.Card!.Id);

        await using var read = NewDbContext();
        Assert.Single(await read.RfidCards.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_card_uid_active_on_another_student_is_a_conflict()
    {
        var schoolId = await ArrangeSchoolAsync();
        var ownerId = await ArrangeStudentAsync(schoolId, "2023-0001");
        var claimantId = await ArrangeStudentAsync(schoolId, "2023-0002");

        await using (var arrange = NewDbContext())
        {
            arrange.RfidCards.Add(TestData.NewCard(schoolId, ownerId, "04A7B8C9"));
            await arrange.SaveChangesAsync();
        }

        await using var db = NewDbContext();
        var response = await StudentsOn(db).AddCardAsync(
            claimantId, new StudentCardRequest("04:A7:B8:C9", null));

        Assert.Equal(StudentWriteOutcome.CardUidInUse, response.Outcome);

        await using var read = NewDbContext();
        Assert.Single(await read.RfidCards.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// ADR-001 D-3's reason for existing, from the other side: uniqueness is scoped to active cards, so
    /// a UID freed by a deactivation can be issued again — to a different student, or to the same one
    /// on a replacement card carrying the same REGNO. A global <c>UNIQUE(CardUid)</c> would make this
    /// impossible to record without erasing the old row.
    /// </summary>
    [Fact]
    public async Task A_deactivated_card_uid_can_be_issued_again()
    {
        var schoolId = await ArrangeSchoolAsync();
        var ownerId = await ArrangeStudentAsync(schoolId, "2023-0001");
        var claimantId = await ArrangeStudentAsync(schoolId, "2023-0002");

        await using (var arrange = NewDbContext())
        {
            arrange.RfidCards.Add(TestData.NewCard(schoolId, ownerId, "04A7B8C9", isActive: false));
            await arrange.SaveChangesAsync();
        }

        await using var db = NewDbContext();
        var response = await StudentsOn(db).AddCardAsync(claimantId, new StudentCardRequest("04A7B8C9", null));

        Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(2, (await read.RfidCards.AsNoTracking().ToListAsync()).Count);
    }

    /// <summary>
    /// Two students being issued the same UID at the same instant. The read in <c>AddCardAsync</c> is
    /// not the guarantee — <c>UX_RfidCards_SchoolId_CardUid_Active</c> is — and losing to it must be a
    /// 409 rather than an unhandled <c>DbUpdateException</c>.
    /// </summary>
    [Fact]
    public async Task Concurrent_assignments_of_one_card_uid_leave_exactly_one_active_card()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentIds = new[]
        {
            await ArrangeStudentAsync(schoolId, "2023-0001"),
            await ArrangeStudentAsync(schoolId, "2023-0002"),
            await ArrangeStudentAsync(schoolId, "2023-0003"),
            await ArrangeStudentAsync(schoolId, "2023-0004"),
        };

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = studentIds.Select(studentId => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = NewDbContext();
            return await StudentsOn(db).AddCardAsync(studentId, new StudentCardRequest("04A7B8C9", null));
        })).ToArray();

        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        Assert.Single(responses, r => r.Outcome == StudentWriteOutcome.Saved);
        Assert.All(responses.Where(r => r.Outcome != StudentWriteOutcome.Saved),
            r => Assert.Equal(StudentWriteOutcome.CardUidInUse, r.Outcome));

        await using var read = NewDbContext();
        Assert.Single(await read.RfidCards.AsNoTracking().Where(c => c.IsActive).ToListAsync());
    }

    /// <summary>
    /// Deactivation keeps the row. That is the entire point of ADR-001 D-3: a past tap's
    /// <c>AttendanceRecords.RfidCardId</c> points here, and a hard delete would make "which physical
    /// card was presented" unanswerable while also being refused outright by <c>DeleteBehavior.Restrict</c>.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_card_preserves_the_row_and_frees_the_uid()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        Guid cardId;
        await using (var arrange = NewDbContext())
        {
            var card = TestData.NewCard(schoolId, studentId, "04A7B8C9");
            arrange.RfidCards.Add(card);
            await arrange.SaveChangesAsync();
            cardId = card.Id;
        }

        await using (var db = NewDbContext())
        {
            var response = await StudentsOn(db).DeactivateCardAsync(studentId, cardId);
            Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
            Assert.False(response.Card!.IsActive);
        }

        await using var read = NewDbContext();
        var stored = await read.RfidCards.AsNoTracking().SingleAsync(c => c.Id == cardId);

        Assert.False(stored.IsActive);
        Assert.NotNull(stored.DeactivatedAt);
        // The UID no longer resolves to anybody — the hot path filters on IsActive (§4.4).
        Assert.Null(await StudentsOn(read).GetByCardUidAsync("04A7B8C9"));
    }

    [Fact]
    public async Task Deactivating_an_already_inactive_card_is_a_no_op()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        Guid cardId;
        await using (var arrange = NewDbContext())
        {
            var card = TestData.NewCard(schoolId, studentId, "04A7B8C9", isActive: false);
            arrange.RfidCards.Add(card);
            await arrange.SaveChangesAsync();
            cardId = card.Id;
        }

        await using var db = NewDbContext();
        Assert.Equal(StudentWriteOutcome.Saved,
            (await StudentsOn(db).DeactivateCardAsync(studentId, cardId)).Outcome);
    }

    /// <summary>
    /// A card that belongs to somebody else is a 404 through this URL, not a silent cross-roster edit.
    /// </summary>
    [Fact]
    public async Task Deactivating_another_students_card_is_not_found()
    {
        var schoolId = await ArrangeSchoolAsync();
        var ownerId = await ArrangeStudentAsync(schoolId, "2023-0001");
        var otherId = await ArrangeStudentAsync(schoolId, "2023-0002");

        Guid cardId;
        await using (var arrange = NewDbContext())
        {
            var card = TestData.NewCard(schoolId, ownerId, "04A7B8C9");
            arrange.RfidCards.Add(card);
            await arrange.SaveChangesAsync();
            cardId = card.Id;
        }

        await using var db = NewDbContext();
        var response = await StudentsOn(db).DeactivateCardAsync(otherId, cardId);

        Assert.Equal(StudentWriteOutcome.CardNotFound, response.Outcome);

        await using var read = NewDbContext();
        Assert.True((await read.RfidCards.AsNoTracking().SingleAsync(c => c.Id == cardId)).IsActive);
    }

    [Fact]
    public async Task Assigning_a_card_to_an_unknown_student_is_not_found()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).AddCardAsync(
            Guid.NewGuid(), new StudentCardRequest("04A7B8C9", null));

        Assert.Equal(StudentWriteOutcome.NotFound, response.Outcome);
    }
}
