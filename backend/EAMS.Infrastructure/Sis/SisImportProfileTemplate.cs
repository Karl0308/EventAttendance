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
    /// <b>Bumped to 4 on 2026-10-23 (ADR-009, D-76)</b>, when student classification was withdrawn from
    /// the product surface: version 4 is version 3 without the four category rows. With no
    /// <c>StudentClassification.*</c> target in the mapping, a version 4 batch reads no category cell,
    /// assigns no classification and warns about none — exactly as a version 2 batch does. Version 3 is
    /// superseded, not deleted, and stays behind to explain the batches that ran under it, as version 2
    /// does; the classification tables and their data are untouched.
    /// </para>
    ///
    /// <para>
    /// <b>Bumped to 5 on 2026-10-23 (ADR-010, D-77)</b>, when QA's job order stripped the course and
    /// teacher columns from the template: version 5 is version 4 without the seven rows for
    /// <c>COURSE_CODE</c>, <c>COURSE_NAME</c>, <c>UA_FULLNAME</c>, <c>TEACHER FIRST NAME</c>,
    /// <c>TEACHER LAST NAME</c>, <c>TEACHER SUFFIX</c> and <c>TEACHER COLLEGE</c>. With no
    /// <see cref="CourseCodeKeyTarget"/> row the mapping is <b>section-only</b>: the pipeline reads no
    /// course or teacher cell, even from a file that carries them, and files each section under one
    /// placeholder course per school (<see cref="AcademicKey.SectionPlaceholder"/>). Version 4 and earlier
    /// are superseded, not deleted, and a batch pinned to one of them still creates courses, instructors
    /// and assignments — D-4 working, as in ADR-009.
    /// </para>
    ///
    /// <para>
    /// <b>Bumped to 6 on 2026-10-23 (ADR-011, D-80)</b>, when QA's job order added Organization to
    /// students: version 6 is version 5 plus one last row, <see cref="SisRosterColumns.Organization"/>
    /// to <see cref="StudentOrganizationTarget"/>. A batch pinned to version 5 or earlier maps no
    /// organization column and reads no organization cell, even from a file that carries one — D-4
    /// working, as for the RFID and category rows.
    /// </para>
    ///
    /// <para>
    /// <b>Bumped to 7 on 2026-10-26 (ADR-014, D-92)</b>, when QA's answer on Task 2 put the course code
    /// back: version 7 is version 6 plus the two <see cref="SisRosterColumns.CourseCode"/> rows,
    /// <c>Course.Code</c> and <see cref="CourseCodeKeyTarget"/>, placed after <c>SECTION_NAME</c> as in the
    /// SIS export and version 4. Code only: no <c>Course.Title</c> and no <c>Instructor.*</c> row, so the
    /// pipeline is in course mode again but reads no title and no teacher cell (ADR-010 D-77's switch).
    /// Version 6 is superseded, not deleted, and a batch pinned to it stays section-only — D-4 working.
    /// </para>
    ///
    /// <para>
    /// Bumping is not cosmetic in any case. A database still holding version 1 would read the card
    /// serial out of the REGNO column — the exact defect version 2 removes — while a freshly seeded one
    /// would not. A version is what makes the two agree.
    /// </para>
    /// </summary>
    public const int BuiltInVersion = 7;

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
    /// The dotted target of the organization column (ADR-011, D-80). The pipeline reads a student's
    /// organization only when the batch's profile maps a column here, first by ordinal as for
    /// <see cref="RfidCardUidTarget"/>; named once for the same reason, so the profile row and the lookup
    /// cannot drift apart.
    /// </summary>
    public const string StudentOrganizationTarget = "Student.Organization";

    /// <summary>
    /// The dotted target whose presence makes a profile <b>course mode</b> (ADR-010, D-77). The pipeline
    /// reads the course code — display value and key — from this row's source column; a profile with no
    /// such row is section-only and reads no course or teacher cell at all. Named once for the reason
    /// <see cref="RfidCardUidTarget"/> is: the profile row and the lookup must agree, and a drift between
    /// two literals would silently turn every course-mode batch section-only.
    /// </summary>
    public const string CourseCodeKeyTarget = "Course.CodeKey";

    /// <summary>
    /// The dotted target of the course title column. Read only in course mode, from this row's source
    /// column; a course-mode profile without it imports courses with no title.
    /// </summary>
    public const string CourseTitleTarget = "Course.Title";

    /// <summary>
    /// The dotted target the teacher columns map to. In course mode the pipeline reads the teacher cells
    /// only when the profile maps at least one column here. Which cell is the full name, the first name
    /// or the last name cannot be told from this target (all three share it), so those cells are still
    /// read from the <see cref="SisRosterColumns"/> headers.
    /// </summary>
    public const string InstructorDisplayNameTarget = "Instructor.DisplayName";

    /// <summary>
    /// The dotted target that marks a category column, one per <see cref="ClassificationAxis"/>: the
    /// axis is the part after the dot, so <c>StudentClassification.Personnel</c> is the row that says
    /// "this source column holds Personnel-axis categories".
    ///
    /// <para>
    /// <b>Kept although <see cref="Entries"/> no longer maps one.</b> Version 3 batches, and any
    /// operator-authored version that maps a category column, are still read through it by the importer
    /// and the roster template.
    /// </para>
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
        "REGNO (the client's 2026-07-30 correction); version 3 added the four Task 5 category columns; " +
        "version 4 removes them again, because student classification was withdrawn from the product " +
        "(2026-10-23, ADR-009 D-76); version 5 removes the course and teacher columns and imports " +
        "sections only, under one placeholder course per school (2026-10-23, ADR-010 D-77); version 6 " +
        "adds the optional ORGANIZATION column (2026-10-23, ADR-011 D-80); version 7 maps COURSE_CODE " +
        "again, code only (2026-10-26, ADR-014 D-92). Earlier versions are kept, superseded, to explain " +
        "the batches that ran under them: only version 3 classifies anyone, version 4 and earlier import " +
        "teachers, and versions 5 and 6 import sections only. Seeded from " +
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
    private const string Organization = "OrganizationText.Normalize";
    private const string Unused = "(not imported)";

    /// <summary>
    /// The thirteen template columns, in file order. Columns that feed nothing are listed too — an absent
    /// row is indistinguishable from a forgotten one, and "we read this column and deliberately do nothing
    /// with it" is the more useful record. The four category columns version 3 added are not listed
    /// since version 4, and the seven course and teacher columns not since version 5 — of which version 7
    /// brings back <c>COURSE_CODE</c> alone: a file that still carries the other six imports with those
    /// cells unread.
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

        // The section is the enrollment's only placement under versions 5 and 6. Optional as a value (a blank
        // one writes no offering and no enrollment, and warns), required as a header.
        new(SisRosterColumns.SectionName, "CourseOffering.SectionName", Verbatim, IsRequired: false),
        new(SisRosterColumns.SectionName, "CourseOffering.SectionKey", Key, IsRequired: false),

        // Version 7 (ADR-014, D-92). After SECTION_NAME, where the SIS export and version 4 put it.
        // Required as a value, as in version 4: the course-mode pipeline rejects a row whose code is blank
        // whatever this flag says, so the template must mark it. Not in SisRosterColumns.Required (the
        // header list), so a file without the column still selects the worksheet — its rows then fail at
        // run time, not at upload (ADR-010 D-77). The CourseCodeKeyTarget row is the course-mode switch.
        new(SisRosterColumns.CourseCode, "Course.Code", Verbatim, IsRequired: true),
        new(SisRosterColumns.CourseCode, CourseCodeKeyTarget, Key, IsRequired: true),

        // Version 6 (ADR-011, D-80). Last; version 7's COURSE_CODE rows sit before it. Optional as a
        // header and as a value: a blank cell keeps the stored organization, and a file without the
        // column (every Task 2 file) still selects the worksheet and imports.
        new(SisRosterColumns.Organization, StudentOrganizationTarget, Organization, IsRequired: false),
    ];
}
