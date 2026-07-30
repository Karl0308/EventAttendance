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

    /// <summary>
    /// The version <see cref="Entries"/> describes. <b>Bumped to 2 on 2026-07-30</b>, when the client
    /// corrected us that the RFID card serial is its own column and not REGNO: version 1 mapped
    /// <c>REGNO → RfidCard.CardUid</c>, version 2 maps <c>REGNO → Student.StudentNumber</c> only and
    /// reads the serial from <see cref="SisRosterColumns.RfidCardSerial"/>.
    ///
    /// <para>
    /// Bumping it is not cosmetic. The pipeline resolves the RFID source column <em>from the profile</em>
    /// rather than from a constant, so a database still holding version 1 would read the serial out of
    /// the REGNO column — the exact defect this change removes — while a freshly seeded database would
    /// not. A version is what makes the two agree.
    /// </para>
    /// </summary>
    public const int BuiltInVersion = 2;

    /// <summary>
    /// The dotted target that marks the RFID source column, named once because two places must agree
    /// about it: the entry below that records the mapping, and the pipeline that looks the mapping up.
    /// A literal in both would let them drift, and the drift would be silent — a profile whose RFID row
    /// nothing matches simply imports every student with no card.
    /// </summary>
    public const string RfidCardUidTarget = "RfidCard.CardUid";

    public const string Description =
        "Built-in mapping for the CICSS roster export. Version 2 separates the RFID card serial from " +
        "REGNO (the client's 2026-07-30 correction); version 1 derived the card UID from REGNO and is " +
        "kept, superseded, to explain the batches that ran under it. Seeded from " +
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
    /// All eighteen columns, in file order. Columns that feed nothing are listed too — an absent row is
    /// indistinguishable from a forgotten one, and "we read this column and deliberately do nothing with
    /// it" is the more useful record.
    /// </summary>
    public static readonly IReadOnlyList<Entry> Entries =
    [
        // REGNO is the student number and nothing else, and it is the only genuinely required column:
        // without it a row names nobody. It is NOT the card UID — that was version 1's mapping and the
        // client corrected it on 2026-07-30.
        new(SisRosterColumns.RegNo, "Student.StudentNumber", Verbatim, IsRequired: true),

        // The card serial, and the row that makes this table load-bearing rather than decorative. The
        // pipeline finds the RFID source column by looking for THIS TargetField among the batch's
        // profile columns — so when the client's export finally arrives with the column called
        // something other than 'RFID', the fix is a new profile version with a different SourceColumn
        // and not a line of code. Optional, because the roster in hand has no such column at all and a
        // student with no card must import cleanly.
        new(SisRosterColumns.RfidCardSerial, RfidCardUidTarget, Uid, IsRequired: false),

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
