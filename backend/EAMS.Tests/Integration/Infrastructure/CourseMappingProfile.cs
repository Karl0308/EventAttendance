using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Sis;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// The import profile the course- and instructor-asserting suites run through: a <b>frozen literal
/// copy</b> of built-in version 4 (the last mapping that carried the course and teacher columns).
///
/// <para>
/// <b>Why it exists.</b> Built-in version 5 drops <c>COURSE_CODE</c>, <c>COURSE_NAME</c>,
/// <c>UA_FULLNAME</c> and the four <c>TEACHER *</c> columns, and the importer goes section-only. A suite
/// that imports through the built-in profile and asserts on courses, offerings, instructors or their
/// warnings would then silently stop testing courses at all. Those suites are pinned to this profile
/// instead, so they keep exercising the course code path for as long as it exists.
/// </para>
///
/// <para>
/// <b>It is typed out, never built from <see cref="SisImportProfileTemplate.Entries"/>.</b> <c>Entries</c>
/// changes when the built-in version does; a copy derived from it would change with it and the pin would
/// be worth nothing. Do not "tidy" <see cref="Entries"/> into a projection of the template.
/// </para>
///
/// <para>
/// <b>The built-in row is seeded too (inactive).</b> <c>FindCurrentAsync</c> only defers to a newer
/// operator profile when the row for the <em>current</em> built-in version already exists — the same
/// load-bearing detail <see cref="CategoryMappingProfile"/> documents. Both versions are expressed
/// relative to <see cref="SisImportProfileTemplate.BuiltInVersion"/>, so this works at 4 and at 5.
/// </para>
/// </summary>
internal static class CourseMappingProfile
{
    public static readonly IReadOnlyList<(string SourceColumn, string TargetField, string NormalizationRule, bool IsRequired)> Entries =
    [
        ("REGNO", "Student.StudentNumber", "RosterText.Clean", true),
        ("RFID", "RfidCard.CardUid", "CardUid.Normalize", false),
        ("STUDENT FIRST NAME", "Student.FirstName", "RosterText.CleanName", true),
        ("STUDENT MIDDLE NAME", "Student.MiddleName", "RosterText.CleanName", false),
        ("STUDENT LAST NAME", "Student.LastName", "RosterText.CleanName", true),
        ("FULL_NAME", "(none)", "(not imported)", false),
        ("EMAIL_ID", "Student.AlternateEmail", "RosterText.CleanEmail", false),
        ("USA_EMAIL", "Student.Email", "RosterText.CleanEmail", false),
        ("COLLEGE_NAME", "College.Name", "RosterText.Clean", true),
        ("COLLEGE_NAME", "College.NameKey", "AcademicKey.NormalizeOrUnspecified", true),
        ("PROGRAM", "Program.Code", "RosterText.Clean", true),
        ("PROGRAM", "Program.CodeKey", "AcademicKey.NormalizeOrUnspecified", true),
        ("SECTION_NAME", "CourseOffering.SectionName", "RosterText.Clean", false),
        ("SECTION_NAME", "CourseOffering.SectionKey", "AcademicKey.NormalizeOrUnspecified", false),
        ("COURSE_CODE", "Course.Code", "RosterText.Clean", true),
        ("COURSE_CODE", "Course.CodeKey", "AcademicKey.NormalizeOrUnspecified", true),
        ("COURSE_NAME", "Course.Title", "RosterText.Clean", false),
        ("UA_FULLNAME", "Instructor.DisplayName", "TeacherNames.Parse", false),
        ("TEACHER FIRST NAME", "Instructor.DisplayName", "TeacherNames.Parse", false),
        ("TEACHER LAST NAME", "Instructor.DisplayName", "TeacherNames.Parse", false),
        ("TEACHER SUFFIX", "(none — honorific)", "TeacherNames.Parse", false),
        ("TEACHER COLLEGE", "(none)", "(not imported)", false),
    ];

    /// <summary>
    /// Only the rows that carry the section, course and teacher columns — for a hand-built profile that
    /// maps something else (e.g. a mistyped category axis) and must still leave the course path on.
    /// </summary>
    public static IReadOnlyList<(string SourceColumn, string TargetField, string NormalizationRule, bool IsRequired)> CourseRows { get; } =
        [.. Entries.Where(e => e.SourceColumn is "SECTION_NAME" or "COURSE_CODE" or "COURSE_NAME"
            or "UA_FULLNAME" or "TEACHER FIRST NAME" or "TEACHER LAST NAME" or "TEACHER SUFFIX" or "TEACHER COLLEGE")];

    /// <summary>The operator version: one above the built-in, so it is the one the importer resolves.</summary>
    public static int Version => SisImportProfileTemplate.BuiltInVersion + 1;

    /// <summary>
    /// Seeds the built-in version (inactive) and a newer active version, both carrying the frozen
    /// version 4 mapping. Returns the id of the active one.
    /// </summary>
    public static async Task<Guid> SeedCourseMappingProfileAsync(EamsDbContext db, Guid schoolId)
    {
        await CategoryMappingProfile.SeedProfileAsync(
            db, schoolId, SisImportProfileTemplate.BuiltInVersion, isActive: false, Entries);
        return await CategoryMappingProfile.SeedProfileAsync(db, schoolId, Version, isActive: true, Entries);
    }
}
