using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Sis;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// The import profile the section-only suites run through: a <b>frozen literal copy</b> of built-in
/// version 6 (ADR-010 D-77 plus ADR-011 D-80's ORGANIZATION row — the last built-in mapping with no
/// <c>Course.CodeKey</c> row).
///
/// <para>
/// <b>Why it exists.</b> Built-in version 7 maps <c>COURSE_CODE</c> again (code only), so the importer
/// is back in course mode. A suite that imports through the built-in profile and asserts on the
/// section-only path — the placeholder course, no course touch, the blank-section rule — would then
/// silently stop testing that path. Those suites are pinned to this profile instead, the way
/// <see cref="CourseMappingProfile"/> pinned the course suites to version 4 when version 5 dropped courses.
/// </para>
///
/// <para>
/// <b>It is typed out, never built from <see cref="SisImportProfileTemplate.Entries"/></b>, for the reason
/// <see cref="CourseMappingProfile"/> gives: a copy derived from the live entries changes when they do.
/// </para>
/// </summary>
internal static class SectionOnlyMappingProfile
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
        ("ORGANIZATION", "Student.Organization", "OrganizationText.Normalize", false),
    ];

    /// <summary>Version 5: version 6 without its ORGANIZATION row — derived from the frozen copy, not the live one.</summary>
    public static IReadOnlyList<(string SourceColumn, string TargetField, string NormalizationRule, bool IsRequired)> VersionFiveEntries { get; } =
        [.. Entries.Where(e => e.TargetField != SisImportProfileTemplate.StudentOrganizationTarget)];

    /// <summary>The operator version: one above the built-in, so it is the one the importer resolves.</summary>
    public static int Version => SisImportProfileTemplate.BuiltInVersion + 1;

    /// <summary>
    /// Seeds the built-in version (inactive) and a newer active version, both carrying the frozen
    /// version 6 mapping — the same load-bearing pair <see cref="CourseMappingProfile"/> seeds, because
    /// <c>FindCurrentAsync</c> defers to a newer version only once the built-in row exists. Returns the id
    /// of the active one.
    /// </summary>
    public static async Task<Guid> SeedSectionOnlyProfileAsync(EamsDbContext db, Guid schoolId)
    {
        await CategoryMappingProfile.SeedProfileAsync(
            db, schoolId, SisImportProfileTemplate.BuiltInVersion, isActive: false, Entries);
        return await CategoryMappingProfile.SeedProfileAsync(db, schoolId, Version, isActive: true, Entries);
    }
}
