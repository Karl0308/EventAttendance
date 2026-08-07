namespace EAMS.Domain;

// The academic-structure layer approved by ADR-001 D-1: nine tables that carry the roster's real
// grain, *student × course × section × teacher*, which §4.3's single-valued Course/YearLevel/Section
// triple cannot represent. Twelve of the fifty-two students in the sample sit in more than one
// section; every one of them is unrepresentable without these tables.
//
// Two conventions run through the whole file and are the reason it works against a dirty source:
//
//   1. Every natural key stores a *normalized* key column beside its display value, computed by
//      EAMS.Domain.AcademicKey. `'SSCI 7'` and `'SSci7'` are one course because they share a
//      CodeKey, not because someone remembered to trim.
//   2. Every component of a natural key is NOT NULL, with an explicit sentinel where the source is
//      blank (AcademicKey.Unspecified). A SQL Server unique index admits exactly one NULL row, so a
//      nullable key component silently caps the table at one such row — 39 of the sample's rows have
//      a blank SECTION_NAME, and the 39th would have failed with a duplicate-key error naming
//      nothing useful.

// ---------------------------------------------------------------------------------------- Terms

/// <summary>
/// An academic term. Everything below is scoped to one — the same roster file is imported again next
/// semester and must not collide with this one (ADR-001 D-5 makes <c>TermId</c> a required input on
/// an import batch for the same reason).
///
/// <para>
/// <b><c>Code</c> is not normalized, unlike every other natural key here.</b> It is operator-authored
/// (<c>2025-2026-1</c>), not scraped from a spreadsheet cell, so it has no dirt to clean and
/// normalizing it would only make the stored value less readable than what the operator typed.
/// </para>
/// </summary>
public class Term : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>Operator-authored, unique per school — e.g. <c>2025-2026-1</c>.</summary>
    public string Code { get; set; } = "";

    public string SchoolYear { get; set; } = "";  // e.g. "2025-2026"
    public string Semester { get; set; } = "";    // e.g. "1st Semester"

    /// <summary>
    /// At most one per school, enforced by a filtered unique index rather than by convention: two
    /// "current" terms means every term-defaulting query silently picks one at random.
    /// </summary>
    public bool IsCurrent { get; set; }

    // DateOnly, not DateTime: a term boundary is a calendar date in the school's own timezone and has
    // no instant. Storing it as datetime2 would drag it through the UTC conversion convention and
    // shift it by eight hours in Manila for no gain.
    public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
}

/// <summary>
/// <c>Terms</c>' column rules, stated once for the D-53 admin write surface — the same job
/// <see cref="EventText"/> and <c>DeviceText</c> do for their tables, and here for the same reason: an
/// over-length value reaching SQL Server comes back as error 2628
/// (<c>String or binary data would be truncated</c>), which is a 500 on input the caller got wrong.
///
/// <para>
/// <b>Every length below mirrors the migration, and none of these methods normalizes.</b> A term
/// <c>Code</c> is operator-authored (D-53) — <c>2025-2026-1</c> is what the operator typed and is what
/// gets stored, with no case folding and no separator stripping, unlike every other natural key in
/// this file. <see cref="IsValidCode"/> therefore only asks whether the value is present and fits; it
/// is deliberately not <c>AcademicKey</c>'s job.
/// </para>
///
/// <para>
/// <b>Surrounding whitespace is refused rather than trimmed, and that is the one rule here worth
/// arguing.</b> Trimming would be normalizing the operator's value, which D-53 says not to do; storing
/// it verbatim would let <c>' 2025-2026-1'</c> and <c>'2025-2026-1'</c> live side by side, because
/// <c>UX_Terms_SchoolId_Code</c> sees two different strings — two terms that render identically in
/// every picker, with imports split between them and nothing on screen to explain it. (SQL Server's
/// comparison semantics ignore <em>trailing</em> spaces, so only the leading case actually duplicates;
/// both are refused, because a rule that holds on one side of a string and not the other is one nobody
/// can remember.) Refusing names the problem at the boundary and leaves the stored value exactly as
/// authored.
/// </para>
///
/// <para>
/// <b>That refusal is a deliberate exception to the house style, not the new house style.</b> The rest
/// of this codebase trims — <c>DeviceService</c> and <c>EventService</c> both <c>.Trim()</c> their
/// caller-supplied text and store the result — and a service author reading <c>TermAdminService</c> for
/// a precedent should not carry the refusal across. The exception is narrow and it is earned by one
/// property no other table has: a term <c>Code</c> is the operator's own string, stored verbatim by
/// D-53, so there is no normalization step in which a trim could hide. Everywhere the value is already
/// normalized on the way in, trimming is part of that normalization and belongs there.
/// </para>
/// </summary>
public static class TermText
{
    /// <summary>Matches <c>Terms.Code nvarchar(50)</c>.</summary>
    public const int CodeMaxLength = 50;

    /// <summary>Matches <c>Terms.SchoolYear nvarchar(20)</c> — e.g. <c>2025-2026</c>.</summary>
    public const int SchoolYearMaxLength = 20;

    /// <summary>Matches <c>Terms.Semester nvarchar(30)</c> — e.g. <c>1st Semester</c>.</summary>
    public const int SemesterMaxLength = 30;

    public static bool IsValidCode(string? value) => IsExactAndWithin(value, CodeMaxLength);

    public static bool IsValidSchoolYear(string? value) => IsExactAndWithin(value, SchoolYearMaxLength);

    public static bool IsValidSemester(string? value) => IsExactAndWithin(value, SemesterMaxLength);

    /// <summary>
    /// A term that ends before it starts. Both dates are optional — the SIS export has no term-date
    /// columns, so a real term carries neither — but a pair that runs backwards is a typo the operator
    /// can fix now, and accepting it would put a term in the system that contains no days at all.
    /// </summary>
    public static bool IsValidRange(DateOnly? startsOn, DateOnly? endsOn) =>
        startsOn is not { } start || endsOn is not { } end || start <= end;

    /// <summary>
    /// Present, within <paramref name="maxLength"/>, and identical to its own trimmed form. See the
    /// type remarks for why the last clause is a refusal rather than a silent trim.
    /// </summary>
    private static bool IsExactAndWithin(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maxLength
        && value == value.Trim();
}

// -------------------------------------------------------------------------------------- Colleges

/// <summary>
/// A college, keyed on the normalized <c>COLLEGE_NAME</c>. There is no college code in the source —
/// <see cref="Code"/> is optional and exists so a real one can be recorded later without a migration.
/// </summary>
public class College : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>Display form, as it arrived in <c>COLLEGE_NAME</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary><see cref="AcademicKey.NormalizeOrUnspecified"/> of <see cref="Name"/>.</summary>
    public string NameKey { get; set; } = "";

    public string? Code { get; set; }

    public ICollection<AcademicProgram> Programs { get; set; } = new List<AcademicProgram>();
}

// -------------------------------------------------------------------------------------- Programs

/// <summary>
/// A degree programme — BSCRIM, BSFS, BSN. Table name is <c>Programs</c>; the CLR type is
/// <c>AcademicProgram</c> deliberately.
///
/// <para>
/// <b>Why the type is not called <c>Program</c>.</b> The API entry point is a top-level-statements
/// <c>Program</c> class in the global namespace, and <c>EAMS.Tests</c> reaches for it as
/// <c>typeof(Program).Assembly</c> in a file that also has <c>using EAMS.Domain;</c>. A domain type of
/// that name makes the reference ambiguous (CS0104) in every such file. The table keeps the plan's
/// name; only the CLR identifier moves.
/// </para>
/// </summary>
public class AcademicProgram : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    // Required. The roster carries COLLEGE_NAME on every row, so a programme's college is always
    // derivable at import; a nullable one would let an unattributed programme accumulate silently.
    public Guid CollegeId { get; set; }
    public College? College { get; set; }

    public string Code { get; set; } = "";     // display form, e.g. "BSCRIM"
    public string CodeKey { get; set; } = "";  // AcademicKey.NormalizeOrUnspecified(Code)
    public string? Name { get; set; }
}

// --------------------------------------------------------------------------------------- Courses

/// <summary>
/// A course, keyed on the normalized <c>COURSE_CODE</c>.
///
/// <para>
/// <b>OPEN RISK — the key may have to become college-scoped, and <see cref="CollegeId"/> exists so
/// that is a widening rather than a rebuild.</b> The current key is
/// <c>UNIQUE(SchoolId, CodeKey)</c>, i.e. a course code is unique across the whole institution. The
/// sample file already shows why that is optimistic: <c>'GE Elect 2'</c> resolves to two different
/// <c>COURSE_NAME</c> values inside a <em>single</em> college's export, and generic codes of that
/// shape (<c>GE Elect n</c>, <c>PE n</c>, <c>NSTP n</c>) are exactly the ones different colleges are
/// most likely to reuse for different subjects. A second college's export has been requested; until it
/// arrives this is undecided on purpose and must not be "fixed" by guessing.
/// </para>
///
/// <para>
/// <b>The contingency, spelled out so it is a scheduled change and not a surprise.</b> If codes turn
/// out to collide university-wide, the key becomes <c>UNIQUE(SchoolId, CollegeId, CodeKey)</c>. With
/// <see cref="CollegeId"/> already present that is: backfill it, make it NOT NULL (the three-step
/// add-nullable/backfill/constrain, since a filtered index is not an option for a key component —
/// see <see cref="AcademicKey.Unspecified"/> for why NULL and unique indexes do not mix), swap the
/// index. No table rebuild, no data movement, no FK churn in <c>CourseOfferings</c>. Without the
/// column now it would instead be a new column on a populated table plus a re-key of every child row.
/// </para>
///
/// <para>
/// <b><see cref="Title"/> is nullable and is not identity.</b> Because <c>COURSE_CODE →
/// COURSE_NAME</c> is not 1:1 today, two source titles collapse onto one row and one of them is lost.
/// That loss is real and is the Phase 2 importer's decision to report (ADR-001 D-5 gives it warning
/// columns for exactly this class of anomaly); it is recorded here so nobody reads a missing or
/// surprising title as a bug in the schema.
/// </para>
/// </summary>
public class Course : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>Nullable today; see the type remarks for the widening this column exists to enable.</summary>
    public Guid? CollegeId { get; set; }
    public College? College { get; set; }

    public string Code { get; set; } = "";     // display form, e.g. "GE Elect 2"
    public string CodeKey { get; set; } = "";  // AcademicKey.NormalizeOrUnspecified(Code)
    public string? Title { get; set; }         // COURSE_NAME; not identity, see remarks

    public ICollection<CourseOffering> Offerings { get; set; } = new List<CourseOffering>();
}

// ----------------------------------------------------------------------------------- Instructors

/// <summary>
/// A teacher. The source has <b>no teacher id column at all</b> — nineteen distinct names, matched on
/// the name and nothing else — which is why this table is built to survive being wrong about identity.
///
/// <para>
/// <b>Rows are never deleted.</b> A name-keyed identity will occasionally split one person into two
/// rows (a middle initial appears, a name is misspelled) or merge two people into one. Deleting the
/// loser of a merge would orphan every <c>CourseOfferingInstructors</c> row that pointed at it and
/// destroy the record of who was listed at the time. Instead the loser stays and points at the winner
/// through <see cref="MergedIntoInstructorId"/>; readers follow the pointer, history stays intact,
/// and a merge is reversible.
/// </para>
///
/// <para>
/// <b><see cref="ExternalId"/> is reserved for a real SIS teacher id</b> and is unique only where
/// present (a filtered unique index — the same reading §4.10's nullable <c>Devices.ApiKey</c> needed,
/// for the same reason: SQL Server treats NULLs as equal inside a unique index, so an unfiltered one
/// would permit exactly one instructor without an external id).
/// </para>
///
/// <para>
/// <b>Note on <c>'TO BE ANNOUNCE'</c>.</b> It appears 66 times in the sample and is a placeholder, not
/// a person. It is deliberately <em>not</em> special-cased here: it normalizes like any other name and
/// becomes one ordinary instructor row, so nothing is lost and no rule has to be invented before the
/// registrar has been asked what they want it to mean. Whether to flag or exclude it is a Phase 2
/// import decision.
/// </para>
/// </summary>
public class Instructor : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>Display form, as it arrived in the source.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary><see cref="AcademicKey.NormalizeOrUnspecified"/> of <see cref="DisplayName"/>.</summary>
    public string NameKey { get; set; } = "";

    /// <summary>A real SIS teacher id once one exists. Unique when present, absent by default.</summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// Set when this row was found to be the same person as another. Never points at itself
    /// (<c>CK_Instructors_NoSelfMerge</c>).
    /// </summary>
    public Guid? MergedIntoInstructorId { get; set; }
    public Instructor? MergedIntoInstructor { get; set; }
}

// ------------------------------------------------------------------------------- CourseOfferings

/// <summary>
/// The real "section": one course, in one term, taught to one named section. This is the row the
/// spreadsheet is really about, and the reason §4.3's <c>Students.Section</c> cannot do the job.
///
/// <para>
/// <b><see cref="SectionKey"/> is NOT NULL and blanks become <see cref="AcademicKey.Unspecified"/>.</b>
/// 39 sample rows have an empty <c>SECTION_NAME</c>; under a nullable key column SQL Server's unique
/// index would have accepted the first and rejected the other 38.
/// </para>
///
/// <para>
/// The teacher is deliberately <em>not</em> a column here — see
/// <see cref="CourseOfferingInstructor"/>.
/// </para>
/// </summary>
public class CourseOffering : AuditableEntity
{
    public Guid TermId { get; set; }
    public Term? Term { get; set; }

    public Guid CourseId { get; set; }
    public Course? Course { get; set; }

    /// <summary><see cref="AcademicKey.NormalizeOrUnspecified"/> of the source section name.</summary>
    public string SectionKey { get; set; } = AcademicKey.Unspecified;

    /// <summary>Display form of the section, as it arrived. Null when the source was blank.</summary>
    public string? SectionName { get; set; }

    public ICollection<CourseOfferingInstructor> Instructors { get; set; } = new List<CourseOfferingInstructor>();
    public ICollection<Enrollment> Enrollments { get; set; } = new List<Enrollment>();
}

/// <summary>
/// Who teaches an offering. Many-to-many on purpose, and this is the table that dissolves the source's
/// duplicate teacher rows <em>structurally</em>: a team-taught section arrives as several otherwise
/// identical spreadsheet rows differing only in the teacher name, and against this shape they become
/// one offering with several instructor links instead of several offerings or a lost teacher.
///
/// <para>
/// A junction, so it carries no <c>CreatedAt</c>/<c>UpdatedAt</c> — the same convention §4.7's
/// <c>StudentGroupMembers</c> and §4.11's <c>UserRoles</c> follow.
/// </para>
/// </summary>
public class CourseOfferingInstructor : Entity
{
    public Guid CourseOfferingId { get; set; }
    public CourseOffering? CourseOffering { get; set; }

    public Guid InstructorId { get; set; }
    public Instructor? Instructor { get; set; }
}

// ----------------------------------------------------------------------------------- Enrollments

/// <summary>
/// One student in one offering — the spreadsheet's row grain, and the only correct answer to "who is
/// in this section?".
/// </summary>
public class Enrollment : AuditableEntity
{
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    public Guid CourseOfferingId { get; set; }
    public CourseOffering? CourseOffering { get; set; }
}

// ---------------------------------------------------------------------------- StudentTermRecords

/// <summary>
/// A student's placement for one term: their programme, college, year level and home section. This is
/// the per-term, authoritative version of the three §4.3 columns that ADR-001 D-2 demotes to a cache.
///
/// <para>
/// The FK columns are nullable because the source can be blank in any of them, and none of them is
/// part of the natural key — <c>(StudentId, TermId)</c> is. A student with no recorded college simply
/// does not appear in that term's college group; they are not silently filed under a placeholder.
/// </para>
/// </summary>
public class StudentTermRecord : AuditableEntity
{
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    public Guid TermId { get; set; }
    public Term? Term { get; set; }

    public Guid? ProgramId { get; set; }
    public AcademicProgram? Program { get; set; }

    public Guid? CollegeId { get; set; }
    public College? College { get; set; }

    /// <summary>
    /// The student's year level for this term, as a bare digit — <c>"2"</c>, not <c>"2nd Year"</c>.
    ///
    /// <para>
    /// <b>Derived, and owned by <c>StudentGroupProjection</c> (D-47).</b> The roster export has no year
    /// column, so this is computed from the student's <em>home</em> sections — those whose
    /// <c>SectionKey</c> begins with their own programme's <c>CodeKey</c> — by
    /// <see cref="YearLevels.Derive"/>. The importer deliberately never writes it: derivation runs
    /// inside the projection so that a corrected roster moves students between year groups on the next
    /// import with no manual step, which is also how a student progresses between terms.
    /// </para>
    ///
    /// <para>
    /// <b><c>null</c> is a first-class outcome, not a failure.</b> A student whose only sections are
    /// subject blocks (<c>NSTP 2</c>, <c>ROTC</c>) gets no year, joins no year group, and stays
    /// invitable by programme, section, course and individually. See <see cref="YearLevels"/> for why a
    /// gap beats a guess here.
    /// </para>
    ///
    /// <para>
    /// <b>It is never copied to <c>Students.YearLevel</c> (D-48).</b> That column is the ADR-001 D-2
    /// display cache <c>EamsDbContext</c> refuses writes to; deriving a value does not promote it to a
    /// join key, and every audience and denominator still resolves through <c>Enrollments</c> and this
    /// table.
    /// </para>
    ///
    /// <para>
    /// A <c>string</c> rather than an <c>int</c> or an enum, like <c>Status</c> and
    /// <c>CaptureMethod</c> — promoting it is a schema migration under the global no-rename rule.
    /// </para>
    /// </summary>
    public string? YearLevel { get; set; }

    /// <summary>
    /// The cohort section the student belongs to, as a normalized key — <c>BSFS2A</c>. Nullable
    /// because it is not a key component here; absence means "not recorded", which is a different
    /// statement from <c>CourseOfferings.SectionKey</c>'s <see cref="AcademicKey.Unspecified"/>
    /// ("recorded as blank, and still has to be a distinct row").
    /// </summary>
    public string? HomeSectionKey { get; set; }

    public string? HomeSectionName { get; set; }
}
