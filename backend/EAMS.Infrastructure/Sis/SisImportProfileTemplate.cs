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
    /// The version <see cref="Entries"/> describes.
    ///
    /// <para>
    /// <b>Bumped to 2 on 2026-07-30</b>, when the client corrected us that the RFID card serial is its
    /// own column and not REGNO: version 1 mapped <c>REGNO → RfidCard.CardUid</c>, version 2 maps
    /// <c>REGNO → Student.StudentNumber</c> only and reads the serial from
    /// <see cref="SisRosterColumns.RfidCardSerial"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Bumped to 3 for Task 5</b>, which adds the four category columns. The pipeline resolves those
    /// source columns from the profile exactly as it resolves the RFID one, so the bump is what decides
    /// whether a batch reads them at all: a batch pinned to version 2 maps no category column, reads no
    /// category cell and assigns no classification — even from a file that carries all four. That is
    /// D-4 working rather than a gap. A roster imported in August is still explained by August's rules,
    /// and re-importing the file is what brings it under the new ones.
    /// </para>
    ///
    /// <para>
    /// Bumping is not cosmetic in either case. A database still holding version 1 would read the card
    /// serial out of the REGNO column — the exact defect version 2 removes — while a freshly seeded one
    /// would not. A version is what makes the two agree.
    /// </para>
    /// </summary>
    public const int BuiltInVersion = 3;

    /// <summary>
    /// The dotted target that marks the RFID source column, named once because two places must agree
    /// about it: the entry below that records the mapping, and the pipeline that looks the mapping up.
    /// A literal in both would let them drift, and the drift would be silent — a profile whose RFID row
    /// nothing matches simply imports every student with no card.
    /// </summary>
    public const string RfidCardUidTarget = "RfidCard.CardUid";

    /// <summary>
    /// The dotted target of the student-number column. Named for the same reason as
    /// <see cref="RfidCardUidTarget"/>: the roster template finds the REGNO column by it (to format it as
    /// Text), and a literal in two places would drift silently.
    /// </summary>
    public const string StudentNumberTarget = "Student.StudentNumber";

    /// <summary>
    /// The dotted target that marks a category column, one per <see cref="ClassificationAxis"/>: the
    /// axis is the part after the dot, so <c>StudentClassification.Personnel</c> is the row that says
    /// "this source column holds Personnel-axis categories".
    ///
    /// <para>
    /// <b>The axis lives in the <c>TargetField</c> rather than in a column of its own</b> because
    /// <c>SisImportProfileColumns</c> has no axis column and adding one would be a migration in service
    /// of a single mapping. Dotted targets are already how this table names a destination
    /// (<c>Student.StudentNumber</c>, <c>Course.CodeKey</c>), and the four axis names are a closed set
    /// the domain validates, so the encoding is parseable rather than conventional.
    /// </para>
    /// </summary>
    public const string ClassificationTargetPrefix = "StudentClassification.";

    /// <summary>The profile target that records the category column for one axis.</summary>
    public static string ClassificationTargetFor(string axis) => ClassificationTargetPrefix + axis;

    public const string Description =
        "Built-in mapping for the CICSS roster export. Version 2 separated the RFID card serial from " +
        "REGNO (the client's 2026-07-30 correction); version 3 adds the four Task 5 category columns, " +
        "one per classification axis. Earlier versions are kept, superseded, to explain the batches " +
        "that ran under them — a version 2 batch maps no category column and therefore classifies " +
        "nobody, which is the guarantee rather than a gap. Seeded from " +
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
    private const string Category = "RosterClassification.Resolve";
    private const string Unused = "(not imported)";

    /// <summary>
    /// All twenty-two columns, in file order. Columns that feed nothing are listed too — an absent row
    /// is indistinguishable from a forgotten one, and "we read this column and deliberately do nothing
    /// with it" is the more useful record.
    /// </summary>
    public static readonly IReadOnlyList<Entry> Entries =
    [
        // REGNO is the student number and nothing else, and it is the only genuinely required column:
        // without it a row names nobody. It is NOT the card UID — that was version 1's mapping and the
        // client corrected it on 2026-07-30.
        new(SisRosterColumns.RegNo, StudentNumberTarget, Verbatim, IsRequired: true),

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

        // ------------------------------------------------------------- Task 5: the category columns
        //
        // The rows that make this table load-bearing for classification, exactly as the RfidCard row
        // above does for cards: the pipeline finds each category column by looking for THESE
        // TargetFields among the batch's profile columns, so a client export that spells the header
        // 'PERSONNEL' rather than 'PERSONNEL_CATEGORY' is a new profile version and not a line of code.
        //
        // OPTIONAL, every one of them. A Required addition would reject every roster file that exists
        // today, all of which predate these columns — and a person with no category is a first-class
        // outcome that reports itself (SisImportWarningCode.ClassificationMissing) rather than an error
        // that loses the row.
        //
        // The NormalizationRule names RosterClassification.Resolve rather than a text cleaner, because
        // the interesting rule is not how the cell is tidied but how the four cells are read together:
        // the column value wins, the 720000 registration-number prefix is only a fallback, and neither
        // ever defaults to STUDENT.
        .. SisRosterColumns.ClassificationColumns.Select(c =>
            new Entry(c.Column, ClassificationTargetFor(c.Axis), Category, IsRequired: false)),
    ];
}
