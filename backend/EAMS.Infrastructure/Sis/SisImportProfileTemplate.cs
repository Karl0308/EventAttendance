using EAMS.Domain;

namespace EAMS.Infrastructure.Sis;

/// <summary>
/// The built-in mapping this pipeline executes, expressed as the ADR-001 D-4 profile rows it is
/// recorded as.
///
/// <para>
/// <b>Why the profile is seeded from code rather than left for an operator to author.</b> D-4's value is
/// that a batch can be explained months later against the rules that actually ran. A profile table that
/// nothing writes buys none of that — it would be an empty table and every batch would point at
/// nothing, which is exactly the "no way to prove which rules produced a given batch" state D-4 was
/// written to end. Seeding version 1 from the code that implements it means the recorded mapping is
/// true on day one; a later operator-edited version 2 supersedes it through the same
/// <c>IsActive</c> mechanism, and version 1 stays behind to explain every batch that ran under it.
/// </para>
///
/// <para>
/// <b><see cref="Entries"/> names methods, not prose.</b> <c>NormalizationRule</c> holds
/// <c>RosterText.CleanName</c>, not "trim and drop placeholders". A description drifts from the code
/// the first time the code changes; a method name does not survive being renamed, so the drift becomes
/// a compile-time conversation instead of a silent lie in an audit table.
/// </para>
/// </summary>
internal static class SisImportProfileTemplate
{
    /// <summary>
    /// The operator-facing name of the built-in mapping. Part of its natural key, so changing this
    /// string creates a second profile rather than renaming this one — which is the correct behaviour
    /// for a versioned, immutable record and the reason it is a constant rather than a literal.
    /// </summary>
    public const string ProfileName = "CICSS Faculty Evaluation Report";

    public const int BuiltInVersion = 1;

    public const string Description =
        "Built-in mapping for the CICSS 17-column roster export. Version 1 is seeded from " +
        nameof(SisImportProfileTemplate) + " so every batch points at the rules that actually ran.";

    /// <param name="SourceColumn">The header in the file.</param>
    /// <param name="TargetField">Where the value lands, dotted.</param>
    /// <param name="NormalizationRule">The domain method applied on the way.</param>
    /// <param name="IsRequired">Whether a row missing this value can still be imported.</param>
    internal sealed record Entry(
        string SourceColumn, string TargetField, string NormalizationRule, bool IsRequired);

    private const string Verbatim = "RosterText.Clean";
    private const string Name = "RosterText.CleanName";
    private const string Email = "RosterText.CleanEmail";
    private const string Key = "AcademicKey.NormalizeOrUnspecified";
    private const string Uid = "CardUid.Normalize";
    private const string Teacher = "TeacherNames.Parse";
    private const string Unused = "(not imported)";

    /// <summary>
    /// All seventeen columns, in file order. Columns that feed nothing are listed too — an absent row is
    /// indistinguishable from a forgotten one, and "we read this column and deliberately do nothing with
    /// it" is the more useful record.
    /// </summary>
    public static readonly IReadOnlyList<Entry> Entries =
    [
        // REGNO is the student number *and* the card UID, and it is the only genuinely required column:
        // without it a row names nobody. Two target fields, two rules, so it appears twice.
        new(SisRosterColumns.RegNo, "Student.StudentNumber", Verbatim, IsRequired: true),
        new(SisRosterColumns.RegNo, "RfidCard.CardUid", Uid, IsRequired: true),

        new(SisRosterColumns.StudentFirstName, "Student.FirstName", Name, IsRequired: true),
        new(SisRosterColumns.StudentMiddleName, "Student.MiddleName", Name, IsRequired: false),
        new(SisRosterColumns.StudentLastName, "Student.LastName", Name, IsRequired: true),

        // FULL_NAME is redundant with the three name parts and is not stored: Student.FullName is
        // computed in the domain, so importing this would create a second spelling of the same name
        // that nothing keeps in step with the first.
        new(SisRosterColumns.FullName, "(none)", Unused, IsRequired: false),

        new(SisRosterColumns.EmailId, "Student.AlternateEmail", Email, IsRequired: false),
        new(SisRosterColumns.UsaEmail, "Student.Email", Email, IsRequired: false),

        new(SisRosterColumns.CollegeName, "College.Name", Verbatim, IsRequired: true),
        new(SisRosterColumns.CollegeName, "College.NameKey", Key, IsRequired: true),
        new(SisRosterColumns.Program, "Program.Code", Verbatim, IsRequired: true),
        new(SisRosterColumns.Program, "Program.CodeKey", Key, IsRequired: true),

        new(SisRosterColumns.SectionName, "CourseOffering.SectionName", Verbatim, IsRequired: false),
        new(SisRosterColumns.SectionName, "CourseOffering.SectionKey", Key, IsRequired: false),

        new(SisRosterColumns.CourseCode, "Course.Code", Verbatim, IsRequired: true),
        new(SisRosterColumns.CourseCode, "Course.CodeKey", Key, IsRequired: true),
        new(SisRosterColumns.CourseName, "Course.Title", Verbatim, IsRequired: false),

        new(SisRosterColumns.TeacherFullName, "Instructor.DisplayName", Teacher, IsRequired: false),
        new(SisRosterColumns.TeacherFirstName, "Instructor.DisplayName", Teacher, IsRequired: false),
        new(SisRosterColumns.TeacherLastName, "Instructor.DisplayName", Teacher, IsRequired: false),

        // Read, understood, and deliberately discarded: the column is an honorific despite its name, and
        // letting it reach Instructor.DisplayName would put "Ms." into NameKey and split one teacher
        // into two. See TeacherNames.Parse.
        new(SisRosterColumns.TeacherSuffix, "(none — honorific)", Teacher, IsRequired: false),

        // The teacher's own college, which is not the student's and is not what Courses.CollegeId means.
        // Storing it there would attribute a course to whichever faculty happened to staff it.
        new(SisRosterColumns.TeacherCollege, "(none)", Unused, IsRequired: false),
    ];
}
