namespace EAMS.Domain;

// The closed value sets for the §4.12 / ADR-001 D-4 / D-5 import tables, following the same
// `const string` convention and the same reasoning as DomainValues.cs: these columns are nvarchar in
// the migration, and turning them into CLR enums later is a data migration rather than a tidy-up.
//
// Every addition below to a set the Technical Plan already defines is *additive* — no existing value
// is renamed or removed — which is what the global no-DROP/no-rename rule permits.

/// <summary>Technical Plan §4.12 — <c>SisImportBatches.Source</c>.</summary>
public static class SisImportSource
{
    /// <summary>
    /// An <c>.xlsx</c> upload. Added to §4.12's <c>Csv/DbLink/Api</c> because the roster that exists is
    /// a workbook, and calling it <c>Csv</c> would make the one column that says how a batch was
    /// produced lie about every batch this system has.
    /// </summary>
    public const string Excel = "Excel";

    public const string Csv = "Csv";
    public const string DbLink = "DbLink";
    public const string Api = "Api";

    public static readonly IReadOnlyList<string> All = [Excel, Csv, DbLink, Api];

    public static bool TryNormalize(string? value, out string canonical) =>
        SisValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// Technical Plan §4.12 — <c>SisImportBatches.Status</c>, with the two values ADR-001 D-5 left open.
///
/// <para>
/// <b>D-5's open question was whether a warning-only batch reports <c>Completed</c> or something
/// else.</b> It reports <see cref="CompletedWithWarnings"/>. The reason is the operator's actual
/// decision: after an import they need to know, from one field, whether to go and look at the rows.
/// Folding warnings into <c>Completed</c> means the only way to find out is to query the row detail of
/// every batch, which nobody does; folding them into <c>Failed</c> means a batch that imported
/// perfectly well is reported as broken and someone re-runs it.
/// </para>
///
/// <para>
/// <see cref="CompletedWithErrors"/> is separate from <see cref="CompletedWithWarnings"/> for the same
/// reason and is the stronger claim: rows were <em>lost</em>, not merely annotated. It is also distinct
/// from <see cref="Failed"/>, which means the run itself stopped — the difference between "515 of 536
/// rows are in" and "the file could not be read", which are not the same problem and do not have the
/// same fix.
/// </para>
/// </summary>
public static class SisImportStatus
{
    /// <summary>Uploaded and staged; nothing has been written to the academic tables yet.</summary>
    public const string Pending = "Pending";

    public const string Running = "Running";

    /// <summary>Every row imported, none warned, none failed.</summary>
    public const string Completed = "Completed";

    /// <summary>Every row imported; at least one carries a warning. Additive to §4.12 per ADR-001 D-5.</summary>
    public const string CompletedWithWarnings = "CompletedWithWarnings";

    /// <summary>The run finished, but at least one row failed and is not in the academic tables.</summary>
    public const string CompletedWithErrors = "CompletedWithErrors";

    /// <summary>The run stopped. Row counters describe how far it got.</summary>
    public const string Failed = "Failed";

    public static readonly IReadOnlyList<string> All =
        [Pending, Running, Completed, CompletedWithWarnings, CompletedWithErrors, Failed];

    public static bool TryNormalize(string? value, out string canonical) =>
        SisValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// Technical Plan §4.12 — <c>SisImportRows.Result</c>.
///
/// <para>
/// <b>The four §4.12 values plus <see cref="Pending"/>,</b> which is what a row is between upload and
/// run. The column is NOT NULL, so the alternative was to leave staged rows carrying one of the four
/// outcome values before any outcome existed — most likely <c>Skipped</c>, which is indistinguishable
/// from the outcome a re-import legitimately produces and would make the headline "a second import
/// changes nothing" assertion unfalsifiable.
/// </para>
///
/// <para>
/// <b><see cref="Skipped"/> means "this row asked for nothing that was not already true",</b> not "this
/// row was ignored". It is the expected outcome of every row of a re-import and is why
/// <c>Inserted + Updated + Failed + Skipped = TotalRows</c> is the reconciliation an operator can
/// actually run (ADR-001 D-5).
/// </para>
/// </summary>
public static class SisImportRowResult
{
    public const string Pending = "Pending";
    public const string Inserted = "Inserted";
    public const string Updated = "Updated";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";

    public static readonly IReadOnlyList<string> All = [Pending, Inserted, Updated, Failed, Skipped];

    /// <summary>The four terminal outcomes. <see cref="Pending"/> is not one — a finished run has none.</summary>
    public static readonly IReadOnlyList<string> Terminal = [Inserted, Updated, Failed, Skipped];

    public static bool TryNormalize(string? value, out string canonical) =>
        SisValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// ADR-001 D-5's warning codes: a row that imported <em>and</em> has something wrong with it.
///
/// <para>
/// A warning never blocks a row. That is the whole point of the column set — before it existed the only
/// ways to report an anomaly were to fail the row (losing data over a cosmetic problem) or to say
/// nothing (which is how a silent merge becomes permanent).
/// </para>
/// </summary>
public static class SisImportWarningCode
{
    /// <summary>
    /// The row's <c>COURSE_NAME</c> differs from the title already recorded for its course code, and the
    /// first-seen title was kept. <c>'GE Elect 2'</c> carries two titles in the sample, so one of them is
    /// necessarily lost from <c>Courses.Title</c>; the warning message names both, and the row's own
    /// <c>RawData</c> keeps the original verbatim. The alternative — merging with no trace — is the
    /// failure this code exists to make impossible.
    /// </summary>
    public const string CourseTitleAlias = "CourseTitleAlias";

    /// <summary>
    /// An existing course had no college recorded and this row supplied one, so it was adopted. Distinct
    /// from a collision (which fails the row): filling a blank is an enrichment, but it still changes
    /// what a shared row means, so it leaves a trace.
    /// </summary>
    public const string CourseCollegeAdopted = "CourseCollegeAdopted";

    /// <summary>
    /// The row's teacher is <c>'TO BE ANNOUNCE'</c>, so the offering was left unstaffed and no instructor
    /// row was created. 66 of the sample's 536 rows carry this; it is the single most common warning and
    /// is the honest report of a real gap in the source.
    /// </summary>
    public const string InstructorPlaceholder = "InstructorPlaceholder";

    /// <summary>
    /// One section name is used by more than one programme inside this batch. Harmless today — a section
    /// is only ever resolved together with a course — but it is the leading indicator of the collision
    /// <see cref="SisImportFailureCode.CourseCollegeCollision"/> guards against, so it is surfaced before
    /// it becomes one.
    /// </summary>
    public const string SectionSpansPrograms = "SectionSpansPrograms";

    /// <summary>
    /// The row named no section, so its offering was filed under <see cref="AcademicKey.Unspecified"/>.
    /// 39 sample rows do this. Not an error — the offering is real and its students are enrolled — but
    /// those students get no section group from the projection, which is worth an operator knowing.
    /// </summary>
    public const string SectionUnspecified = "SectionUnspecified";

    /// <summary>
    /// Two rows carrying the same REGNO describe the student differently, and the first row's values
    /// were kept.
    ///
    /// <para>
    /// REGNO functionally determines name and both e-mail addresses across all 536 sample rows with
    /// zero violations, which is what makes "first row wins" a safe rule. This code is what says so out
    /// loud on the day that stops being true, instead of the later rows being discarded in silence —
    /// which would look exactly like a correct import.
    /// </para>
    /// </summary>
    public const string StudentIdentityConflict = "StudentIdentityConflict";

    /// <summary>
    /// This row's REGNO matches a card that exists but is <em>deactivated</em>, so no card was created
    /// and the revoked one was left revoked.
    ///
    /// <para>
    /// <b>Why not simply issue a new one.</b> A card is deactivated by a person, for a reason the roster
    /// has no column for — lost, stolen, or a suspended student. Because the UID is the REGNO, creating
    /// a fresh active card with the same UID makes the revoked <em>physical</em> card work again, which
    /// silently reverses that decision on the next import. ADR-001 D-3 contemplated deactivate-then-
    /// reissue, where a new card carries the same UID because the same student was handed a replacement;
    /// it did not contemplate a revocation with no replacement. Re-issuing is a back-office action with
    /// a person behind it, exactly like un-deleting a student, so this reports and declines.
    /// </para>
    /// </summary>
    public const string RfidCardRevoked = "RfidCardRevoked";

    public static readonly IReadOnlyList<string> All =
    [
        CourseTitleAlias, CourseCollegeAdopted, InstructorPlaceholder,
        SectionSpansPrograms, SectionUnspecified, StudentIdentityConflict, RfidCardRevoked,
    ];
}

/// <summary>
/// Why a row produced no change. Recorded on <c>SisImportRows.SkipReason</c> so that
/// <c>Skipped</c> — the outcome of every row of a re-import — is never a shrug.
/// </summary>
public static class SisImportSkipReason
{
    /// <summary>
    /// Everything this row names already existed with these values. The expected outcome for all 536
    /// rows of a second import of the same file.
    /// </summary>
    public const string NoChange = "NoChange";

    /// <summary>
    /// The row's only distinguishing content was a <c>'TO BE ANNOUNCE'</c> teacher, and the enrollment it
    /// names was already recorded by the row that named the real teacher.
    ///
    /// <para>
    /// <b>This is the 27 "duplicate" pairs, and they are not duplicates.</b> Each is one enrollment
    /// described twice, once with a teacher and once without, because the source's grain includes the
    /// teacher and the enrollment's does not. Against this schema the teacher belongs to the
    /// <em>offering</em> (<c>CourseOfferingInstructors</c>), so the second row correctly asks for
    /// nothing — and says so here rather than being counted as a duplicate that was thrown away.
    /// </para>
    ///
    /// <para>
    /// <b>Which row of a pair carries this reason rather than <see cref="NoChange"/> is order-dependent,
    /// and that is benign — it is not a defect to "fix".</b> Whichever row is seen first creates the
    /// enrollment and the other skips, so across runs the two labels can swap. Both rows of a pair
    /// resolve to the identical entity set and the identical outcome (<c>Skipped</c> on a re-import);
    /// only which of two accurate reasons is printed differs. Pinning it would mean ranking the pair by
    /// something the source does not order them by — a rule invented to buy a cosmetic property.
    /// </para>
    /// </summary>
    public const string InstructorPlaceholder = "InstructorPlaceholder";

    public static readonly IReadOnlyList<string> All = [NoChange, InstructorPlaceholder];
}

/// <summary>
/// Stable prefixes for <c>SisImportRows.ErrorMessage</c> on the failures that are a property of the data
/// rather than of the run.
///
/// <para>
/// A prefix rather than a column: §4.12 gives rows an <c>ErrorMessage</c> and no error code, and adding
/// one to carry two values would be a column that exists for this file. The prefix is greppable, stable,
/// and testable, which is what the code was wanted for.
/// </para>
/// </summary>
public static class SisImportFailureCode
{
    /// <summary>
    /// The row's course code already exists under a <em>different</em> college.
    ///
    /// <para>
    /// <b>Why this fails the row instead of warning.</b> <c>Courses</c> is keyed
    /// <c>UNIQUE(SchoolId, CodeKey)</c> — a course code is unique institution-wide — and
    /// <c>Courses.CollegeId</c> is single-valued. Resolving to the existing row would silently merge two
    /// different colleges' courses into one, and there is no way back: the column cannot hold both
    /// values, and un-merging means re-keying every <c>CourseOffering</c>, <c>Enrollment</c> and
    /// attendance record that hangs off it. A failed row loses one row's data and is re-runnable after
    /// the source is fixed. A silent merge loses the distinction permanently, and nothing in the system
    /// would ever report it. See the remarks on <see cref="Course"/> for the widening to
    /// <c>UNIQUE(SchoolId, CollegeId, CodeKey)</c> that this failure is the trigger to schedule.
    /// </para>
    /// </summary>
    public const string CourseCollegeCollision = "COURSE_COLLEGE_COLLISION";

    /// <summary>The row is missing a value the import cannot proceed without — REGNO above all.</summary>
    public const string MissingRequiredValue = "MISSING_REQUIRED_VALUE";

    /// <summary>
    /// A normalized key exceeded <see cref="AcademicKey.MaxLength"/>. Reported per row rather than left
    /// to SQL Server, whose truncation error names neither the row nor the column.
    /// </summary>
    public const string KeyTooLong = "KEY_TOO_LONG";

    /// <summary>
    /// The row's REGNO normalizes to a card UID that belongs to a <em>different</em> student.
    ///
    /// <para>
    /// <b>Why this fails the row instead of warning, and how it is reachable.</b> REGNO is both
    /// <c>Students.StudentNumber</c> and <c>RfidCards.CardUid</c>, but the two are not stored the same
    /// way: <see cref="CardUid.Normalize"/> uppercases and strips punctuation, while
    /// <c>StudentNumber</c> is kept verbatim (ADR-001, "Accepted Context"). So <c>usa00962</c>,
    /// <c>USA-00962</c> and <c>USA00962&#160;</c> are three <em>different</em> students — the unique
    /// index is on the verbatim number — sharing one card UID. Phase 3's hand-entered students make a
    /// stray trailing space enough to arm it.
    /// </para>
    ///
    /// <para>
    /// Resolving it by moving the card is unrecoverable: the original student loses their active card,
    /// every subsequent tap on that physical card is attributed to the wrong person, and every
    /// <c>AttendanceRecords.RfidCardId</c> already written points at a card whose <c>StudentId</c> moved
    /// underneath it — history rewritten with nothing to say it happened, which is what ADR-001 D-3
    /// exists to prevent. Issuing a competing active card instead is not an option either: it violates
    /// <c>UX_RfidCards_SchoolId_CardUid_Active</c> and takes the whole batch down with a raw constraint
    /// error in place of a row-level message. So the row fails, naming both students, and the card is
    /// left exactly as it was — the source is then fixable and the import re-runnable.
    /// </para>
    /// </summary>
    public const string RfidCardStudentMismatch = "RFID_CARD_STUDENT_MISMATCH";

    public static readonly IReadOnlyList<string> All =
        [CourseCollegeCollision, MissingRequiredValue, KeyTooLong, RfidCardStudentMismatch];
}

/// <summary>
/// Which table a <c>SisImportRowEntity</c> row points at.
///
/// <para>
/// Strings rather than a discriminated FK per table: the fan-out is a diagnostic trail, and eleven
/// nullable foreign keys on one table to express "this row touched one of eleven things" would cost
/// eleven indexes to serve a query nobody runs on a hot path.
/// </para>
/// </summary>
public static class SisImportEntityType
{
    public const string College = "College";
    public const string Program = "Program";
    public const string Course = "Course";
    public const string Instructor = "Instructor";
    public const string CourseOffering = "CourseOffering";
    public const string CourseOfferingInstructor = "CourseOfferingInstructor";
    public const string Student = "Student";
    public const string RfidCard = "RfidCard";
    public const string Enrollment = "Enrollment";
    public const string StudentTermRecord = "StudentTermRecord";

    public static readonly IReadOnlyList<string> All =
    [
        College, Program, Course, Instructor, CourseOffering, CourseOfferingInstructor,
        Student, RfidCard, Enrollment, StudentTermRecord,
    ];
}

/// <summary>
/// What a source row did to one entity.
///
/// <para>
/// <see cref="Unchanged"/> is recorded, not omitted, and that is what makes the trail worth having: it
/// is the difference between "this row referenced the course and found it already correct" and "this
/// row never mentioned a course". Only the first proves a re-import was a genuine no-op.
/// </para>
/// </summary>
public static class SisImportEntityAction
{
    public const string Inserted = "Inserted";
    public const string Updated = "Updated";
    public const string Unchanged = "Unchanged";

    public static readonly IReadOnlyList<string> All = [Inserted, Updated, Unchanged];
}

/// <summary>
/// The seventeen columns of the CICSS <c>Faculty Evaluation Report</c> sheet, named once.
///
/// <para>
/// <b>Why the column names live in the domain.</b> They are the contract with the registrar's export,
/// they appear in the reader, in the versioned import profile (ADR-001 D-4) and in every fixture, and a
/// typo in any one of them is a column silently read as blank. Naming them here means the profile rows
/// and the reader cannot disagree about what the file is supposed to contain.
/// </para>
/// </summary>
public static class SisRosterColumns
{
    public const string RegNo = "REGNO";
    public const string StudentFirstName = "STUDENT FIRST NAME";
    public const string StudentMiddleName = "STUDENT MIDDLE NAME";
    public const string StudentLastName = "STUDENT LAST NAME";
    public const string FullName = "FULL_NAME";
    public const string EmailId = "EMAIL_ID";
    public const string UsaEmail = "USA_EMAIL";
    public const string CollegeName = "COLLEGE_NAME";
    public const string Program = "PROGRAM";
    public const string SectionName = "SECTION_NAME";
    public const string CourseCode = "COURSE_CODE";
    public const string CourseName = "COURSE_NAME";
    public const string TeacherFullName = "UA_FULLNAME";
    public const string TeacherFirstName = "TEACHER FIRST NAME";
    public const string TeacherLastName = "TEACHER LAST NAME";
    public const string TeacherSuffix = "TEACHER SUFFIX";
    public const string TeacherCollege = "TEACHER COLLEGE";

    /// <summary>All seventeen, in the order the export writes them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        RegNo, StudentFirstName, StudentMiddleName, StudentLastName, FullName, EmailId, UsaEmail,
        CollegeName, Program, SectionName, CourseCode, CourseName, TeacherFullName,
        TeacherFirstName, TeacherLastName, TeacherSuffix, TeacherCollege,
    ];

    /// <summary>
    /// The columns a sheet must have before this pipeline will read it, and therefore the test that
    /// picks the right worksheet out of a workbook.
    ///
    /// <para>
    /// <b>This is how the workbook's second sheet is skipped at batch level rather than per row.</b> The
    /// sample's <c>Sheet1</c> is a three-column projection of names and e-mails; it has no course, no
    /// section and no teacher, so it fails this test and is never opened. Skipping it per row would mean
    /// 536 more staged rows, 536 more skip reasons, and a batch whose <c>TotalRows</c> is twice the
    /// roster.
    /// </para>
    ///
    /// <para>
    /// Deliberately not all seventeen: a required-column list that includes every optional column turns
    /// a harmless export change into a total failure. These nine are the ones without which a row cannot
    /// be placed.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Required =
    [
        RegNo, StudentFirstName, StudentLastName,
        CollegeName, Program, SectionName, CourseCode, CourseName, TeacherFullName,
    ];

    /// <summary>
    /// Header matching is by <see cref="AcademicKey"/>, so <c>'COURSE_CODE'</c>, <c>'Course Code'</c> and
    /// <c>'course  code'</c> are the same column. The export's own header row is not stable enough to
    /// match literally — it already mixes underscores and spaces between otherwise identical names.
    /// </summary>
    public static string HeaderKey(string? header) => AcademicKey.Normalize(header);
}

/// <summary>
/// The same case-insensitive-in, canonical-out matcher <c>DomainValues.cs</c> uses, kept internal for
/// the same reason: callers name the set they mean, so the compiler stops a row result being validated
/// against the batch status set.
/// </summary>
internal static class SisValueSet
{
    public static bool TryNormalize(IReadOnlyList<string> all, string? value, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        foreach (var candidate in all)
        {
            if (!string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase)) continue;
            canonical = candidate;
            return true;
        }

        return false;
    }
}
