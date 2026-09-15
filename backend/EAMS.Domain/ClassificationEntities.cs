namespace EAMS.Domain;

/// <summary>
/// <b>The institution's own vocabulary for "what kind of person is this row".</b> Not in Technical Plan
/// §4 at all — additive, recorded as drift against ADR-001 D-2 (see the phase report).
///
/// <para>
/// <b>Why it is a table and not a <c>string</c> column on <c>Students</c>.</b> Every other enum-ish
/// value in this schema is a <c>string</c> the code owns (<c>Status</c>, <c>AttendanceMode</c>,
/// <c>CaptureMethod</c>) — a closed set decided by us, where a new member is a code change. A
/// classification is the opposite on both counts: QA's answer to Q2 is that the list is <b>not</b>
/// fixed and <b>an administrator edits it</b>, and the eight values it starts with are not a design —
/// they are whatever the client's access-control export happened to contain. A denormalized string
/// column would make a rename a mass <c>UPDATE</c> over 21,466 student rows with nothing to roll back
/// to, and two spellings of one category indistinguishable from two categories.
/// </para>
///
/// <para>
/// <b>Identity is <see cref="Entity.Id"/>, never <see cref="Name"/>, and that is the whole point of the
/// type.</b> Whatever ends up holding a person's classification holds this GUID, so renaming
/// <c>SUPERVISORY/MANAGERIAL</c> to <c>Supervisory / Managerial</c> changes one row and no person
/// loses their category. A natural-key foreign key would have made an admin rename a silent
/// reassignment.
/// </para>
///
/// <para>
/// <b>This is the vocabulary only. Nothing here assigns a classification to a person, and that
/// absence is deliberate.</b> The source carries four category <em>axes</em>, and real people carry
/// two classifications at once — a single scalar column on <c>Students</c> could only name one of
/// them, and would answer with a plausible, non-empty, wrong value. That is the identical failure
/// ADR-001 D-2 documents for the <c>Course</c>/<c>YearLevel</c>/<c>Section</c> cache, where 23% of
/// students sit in more than one section and the single-valued column silently misses them. The
/// assignment mechanism is being ruled on separately; <b>this table is correct under every candidate
/// shape</b>, which is why it ships on its own.
/// </para>
///
/// <para>
/// <b>An <c>Axis</c> column drops in additively, and the schema here is chosen so that it does.</b>
/// It would be one nullable <c>nvarchar</c> with a <c>CHECK</c> constraint, backfilled for the eight
/// seeded rows and then tightened — the three-step the project's own migration rules prescribe.
/// Crucially it does <em>not</em> disturb <c>UX_Classifications_SchoolId_NameKey</c>: all eight
/// seeded names are distinct across all four axes, so school-wide name uniqueness stays true and
/// stays the stricter of the two candidate rules. Relaxing it later to
/// <c>(SchoolId, Axis, NameKey)</c> — which only becomes necessary if two axes genuinely need the
/// same word — is an index change someone has to ask for, rather than a looseness shipped on
/// speculation.
/// </para>
///
/// <para>
/// <b><c>SUPERVISORY/MANAGERIAL</c> contains a slash, and nothing here treats a name as a path,
/// a slug or a route segment.</b> Rows are addressed by GUID in every route
/// (<c>/api/v1/classifications/{id:guid}</c>), matched by <see cref="NameKey"/> in every query, and the
/// name itself only ever travels in a JSON body. That is deliberate: the one value most likely to
/// break a URL assumption is in the seed precisely so a build that made one cannot pass.
/// </para>
/// </summary>
public class Classification : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>
    /// Display form, stored exactly as the administrator authored it — <c>SUPERVISORY/MANAGERIAL</c>,
    /// slash and all. Never trimmed on the way in (see <see cref="ClassificationText.IsValidName"/>):
    /// a trimmed <c>' NAP'</c> and <c>'NAP'</c> would be two rows that render identically in a picker.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Which of the source's four category columns this value came out of — see
    /// <see cref="ClassificationAxis"/>. Required, and <b>immutable once the row exists</b>.
    ///
    /// <para>
    /// <b>The axis is a fact read off the export, not a judgement about the word.</b> It is the column
    /// the value appears in: <c>ANT</c> and <c>SUPERVISORY/MANAGERIAL</c> are <c>PERSONNEL</c> values
    /// because they appear in the <c>PERSONNEL</c> column, and nothing about what <c>ANT</c>
    /// abbreviates is needed to place it. Recorded because the first attempt at this table inferred the
    /// axes from the strings and their populations and put both of those in <c>Special</c> — a
    /// plausible, confident, wrong answer, produced exactly the way ADR-001 D-2's section cache
    /// produces one.
    /// </para>
    ///
    /// <para>
    /// <b>Why it cannot be edited after creation.</b> <c>UX_StudentClassifications_Student_Axis</c> caps
    /// a person at one classification per axis, and <see cref="StudentClassification.Axis"/> is
    /// denormalized from this column so that index can exist at all. Moving a classification between
    /// axes would therefore have to rewrite every assignment row's axis in the same breath, and could
    /// collide with a classification the person already holds on the destination axis — a rename that
    /// silently fails for some people and not others. <c>PUT /classifications/{id}</c> renames and
    /// nothing else; a value filed under the wrong axis is fixed by creating the right one and merging.
    /// </para>
    /// </summary>
    public string Axis { get; set; } = "";

    /// <summary>
    /// <see cref="AcademicKey.Normalize"/> of <see cref="Name"/> — letters and digits only, upper-cased.
    /// The column <c>UX_Classifications_SchoolId_NameKey</c> is built on, and the column Phase 1b will
    /// match the roster's own category strings against.
    ///
    /// <para>
    /// <b>Why the uniqueness is on the key rather than on the name.</b> The source is a messy access
    /// -control export: <c>SUPERVISORY/MANAGERIAL</c>, <c>Supervisory / Managerial</c> and
    /// <c>supervisory-managerial</c> are one category spelled three ways, and they all normalize to
    /// <c>SUPERVISORYMANAGERIAL</c>. Keying on the display name would let an administrator create the
    /// second and third by hand and split one population across three rows, which is exactly the
    /// mess this phase exists to let them clean up rather than to reproduce.
    /// </para>
    ///
    /// <para>
    /// <b>Deliberately not <see cref="AcademicKey.NormalizeOrUnspecified"/>.</b> A vocabulary entry
    /// that normalizes to nothing — <c>'///'</c>, <c>'   -'</c> — is not a category with a blank name,
    /// it is a typo, and it is refused at the boundary rather than filed under a sentinel.
    /// </para>
    /// </summary>
    public string NameKey { get; set; } = "";

    /// <summary>
    /// Whether the classification is offered for new assignments. <c>false</c> is <b>retired</b>: it
    /// disappears from pickers and <b>every student already carrying it keeps carrying it</b>.
    ///
    /// <para>
    /// <b>This is the answer to "delete", and the distinction has teeth.</b> A delete that cascaded, or
    /// that nulled the referring column to make itself succeed, would be a data-loss migration wearing
    /// a CRUD costume — 20,861 people silently uncategorised by one click, and nothing to restore from.
    /// So the hard delete is guarded (refused with 409 while anything references the row) and retiring
    /// is the operation that is always available.
    /// </para>
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When <see cref="IsActive"/> last went false. Null while active, and cleared again on
    /// reactivation, so it always describes the row's current state rather than its history — an
    /// audit trail of vocabulary edits is <c>AuditLogs</c>' job, not this column's.
    /// </summary>
    public DateTime? RetiredAt { get; set; }

    /// <summary>
    /// Set when this classification was collapsed into another one. Never points at itself
    /// (<c>CK_Classifications_NoSelfMerge</c>), and a merged row is always retired
    /// (<c>CK_Classifications_MergedIsRetired</c>).
    ///
    /// <para>
    /// <b>It is a tombstone, not a redirect.</b> The merge repoints every student onto the survivor in
    /// the same transaction, so nothing reads this column to resolve a student's category — it exists
    /// so an administrator looking at a retired row can see <em>why</em> it was retired and what
    /// absorbed it. Without it, a merge and a plain retirement are indistinguishable afterwards, and
    /// the seeded vocabulary came from a source messy enough that merges are expected rather than
    /// exceptional.
    /// </para>
    /// </summary>
    public Guid? MergedIntoClassificationId { get; set; }
    public Classification? MergedIntoClassification { get; set; }
}

/// <summary>
/// The four category axes the client's access-control export carries, one per source column.
///
/// <para>
/// <b>A person holds at most one classification per axis and may hold several axes at once</b> — which
/// is the whole reason this concept exists rather than a single column on the roster. In the sampled
/// export three people carry two at once (two are <c>STUDENT</c> + <c>NAP</c>, one is <c>STUDENT</c> +
/// <c>C2B2</c>), and a scalar <c>Students.ClassificationId</c> could only ever have named one of them —
/// returning a plausible, non-empty, wrong answer, which is ADR-001 D-2's failure repeated on a new
/// column.
/// </para>
///
/// <para>
/// <b>Stored as a <c>string</c> with a <c>CHECK</c> constraint rather than a database enum</b>, exactly
/// like <see cref="AttendanceStatus"/> and <c>AttendanceMode</c>: a real enum makes adding a fifth axis
/// an <c>ALTER TYPE</c>, which is the data-loss-shaped migration the project's hard rules forbid
/// without a preserving script.
/// </para>
/// </summary>
public static class ClassificationAxis
{
    /// <summary>The <c>STUDENTTEMP</c> column. <c>STUDENT</c> — 20,861 rows in the sample.</summary>
    public const string Student = "Student";

    /// <summary>
    /// The <c>PERSONNEL</c> column: <c>NAP</c> (292), <c>ACAD</c> (271), <c>ANT</c> (7) and
    /// <c>SUPERVISORY/MANAGERIAL</c> (1).
    ///
    /// <para>
    /// The last two are here <b>because that is the column they appear in</b>, not because of what they
    /// look like. A population of one reads like a special case and is not one.
    /// </para>
    /// </summary>
    public const string Personnel = "Personnel";

    /// <summary>The <c>FRIARS</c> column. <c>USA FRIARS</c> — 13 rows.</summary>
    public const string Friars = "Friars";

    /// <summary>The <c>SPECIAL</c> column, which holds <c>C2B2</c> (16) and <c>CFI</c> (5) and nothing else.</summary>
    public const string Special = "Special";

    public static readonly IReadOnlyList<string> All = [Student, Personnel, Friars, Special];

    /// <summary>
    /// Maps any casing of a documented axis onto its canonical spelling, and refuses everything else —
    /// including null, blank and over-length input. The same entry point
    /// <see cref="AttendanceStatus.TryNormalize"/> offers, through the same helper.
    /// </summary>
    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// <b>A person's classification on one axis.</b> The join table that replaces the scalar
/// <c>Students.ClassificationId</c> that was never built.
///
/// <para>
/// <b>Why a junction rather than a column.</b> Real people carry two classifications at once, on
/// different axes. A scalar column cannot say so, and — this is the part that decides it — it does not
/// fail loudly when asked: it returns one of the two, non-empty and plausible, with nothing to notice.
/// ADR-001 D-2 documents the same mechanism on <c>Students.Section</c>, where 23% of students sit in
/// more than one section and the single-valued column silently misses roughly a quarter of any
/// section-filtered query. That column survives only because the SPA binds it and dropping a populated
/// column is forbidden; this one was never created, which is the cheapest that lesson has ever been
/// available.
/// </para>
/// </summary>
public class StudentClassification : AuditableEntity
{
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    public Guid ClassificationId { get; set; }
    public Classification? Classification { get; set; }

    /// <summary>
    /// Denormalized from <see cref="Classification.Axis"/>, and <b>it must stay equal to it</b>.
    ///
    /// <para>
    /// <b>It exists so that an index can, exactly as <c>RfidCards.SchoolId</c> does under ADR-001
    /// D-3.</b> The rule worth enforcing is "one classification per person per axis", and SQL Server
    /// can only express that as <c>UNIQUE(StudentId, Axis)</c> on this table — a unique index cannot
    /// reach through a foreign key to read the parent's column. Without this column the rule could only
    /// live in application code, where the second writer forgets it.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing may change a classification's axis, which is what keeps the two in step.</b> See
    /// <see cref="Classification.Axis"/>: the column is immutable after creation precisely so that this
    /// denormalization cannot drift, and a merge is refused across axes for the same reason.
    /// </para>
    /// </summary>
    public string Axis { get; set; } = "";
}

/// <summary>
/// The <c>Classifications</c> column rules, in the domain for the reason <see cref="TermText"/> is:
/// the next writer that is not an HTTP request — Phase 1b's importer, a repair script — inherits them
/// instead of re-deriving them.
/// </summary>
public static class ClassificationText
{
    /// <summary>Matches <c>Classifications.Name nvarchar(100)</c>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>
    /// Matches <c>Classifications.Axis nvarchar(20)</c> and <c>StudentClassifications.Axis</c>. Sized
    /// for the four documented values with room to spare; the <c>CHECK</c> constraint, not the length,
    /// is what actually restricts the column.
    /// </summary>
    public const int AxisMaxLength = 20;

    /// <summary>
    /// Present, within <see cref="NameMaxLength"/>, identical to its own trimmed form, and carrying at
    /// least one letter or digit so it has a <see cref="Classification.NameKey"/> at all.
    ///
    /// <para>
    /// The trim clause is a <em>refusal</em> rather than a silent fix, exactly as
    /// <see cref="TermText"/>'s is: silently trimming would accept two requests that differ and store
    /// one value, so an administrator who typed a trailing space would be told nothing and would then
    /// wonder why their "second" classification was reported as a duplicate.
    /// </para>
    /// </summary>
    public static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= NameMaxLength
        && value == value.Trim()
        && AcademicKey.Normalize(value).Length > 0;

    /// <summary>
    /// The stored key for a display name. Thin on purpose — it exists so that every writer names one
    /// function rather than remembering which of <see cref="AcademicKey"/>'s two entry points applies
    /// here (it is <see cref="AcademicKey.Normalize"/>, not the <c>OrUnspecified</c> form; see
    /// <see cref="Classification.NameKey"/>).
    /// </summary>
    public static string KeyFor(string name) => AcademicKey.Normalize(name);
}

/// <summary>
/// The eight classifications the client's access-control export actually contains, with the population
/// each carried when it was sampled.
///
/// <para>
/// <b>In the domain rather than in <c>SeedData</c> because two callers need them and only one of them
/// is a seed.</b> The startup seed writes them; the tests assert on them. A second copy in the test
/// project is exactly the drift <c>SeedData.DevelopmentCardUid</c> exists to record having been bitten
/// by — the seed's card serials changed and an assertion in another assembly went on testing the old
/// literal, red for a reason no reader of either file could see.
/// </para>
///
/// <para>
/// <b>All eight, not the two QA named (MDVault #404 answers Q2 with <c>STUDENT</c> and <c>NAP</c>).</b>
/// JJ's call: the other six exist in the data whether or not anyone listed them, and a vocabulary that
/// omits them makes Phase 1b's import warn on 313 rows it could have categorised.
/// </para>
///
/// <para>
/// <b>Each value's axis is the source column it appears in — tallied from the export, not inferred.</b>
/// <c>Personnel.xlsx</c> sheet <c>Report</c>, 21,497 rows: <c>STUDENTTEMP</c> holds <c>STUDENT</c>;
/// <c>PERSONNEL</c> holds <c>NAP</c>, <c>ACAD</c>, <c>ANT</c> and <c>SUPERVISORY/MANAGERIAL</c>;
/// <c>FRIARS</c> holds <c>USA FRIARS</c>; <c>SPECIAL</c> holds <c>C2B2</c> and <c>CFI</c> and nothing
/// else.
/// </para>
///
/// <para>
/// <b>This replaced a guess, and the guess is worth recording because it was a confident one.</b> The
/// first pass at this list reasoned from the strings and their populations and placed <c>ANT</c> and
/// <c>SUPERVISORY/MANAGERIAL</c> under <c>Special</c> — the latter because a population of <b>one</b>
/// does not look like a personnel rank. Both are <c>PERSONNEL</c> values. Nobody needs to know what
/// <c>ANT</c> abbreviates in order to place it correctly; the source already placed it, and reading
/// the column beats interpreting the word. <em>(What <c>ANT</c> stands for is still an open QA
/// question — its axis is not.)</em>
/// </para>
/// </summary>
public static class ClassificationSeedValues
{
    /// <summary>One seeded value: its display name exactly as the export spells it, and its axis.</summary>
    public record Seed(string Name, string Axis);

    /// <summary>
    /// Ordered by the population each held in the sampled export — 20,861 / 292 / 271 / 7 / 1 / 13 /
    /// 16 / 5 — rather than alphabetically, so the row an administrator is most likely to be looking
    /// for is first and the order is a fact about the data rather than a formatting choice.
    ///
    /// <para>
    /// <c>SUPERVISORY/MANAGERIAL</c> is in the list verbatim, slash included. It is the value most
    /// likely to break a naive slug or route assumption, and it is seeded rather than sanitised so
    /// that a build which made one cannot pass its own tests.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Seed> All { get; } =
    [
        new("STUDENT", ClassificationAxis.Student),
        new("NAP", ClassificationAxis.Personnel),
        new("ACAD", ClassificationAxis.Personnel),
        new("ANT", ClassificationAxis.Personnel),
        new("SUPERVISORY/MANAGERIAL", ClassificationAxis.Personnel),
        new("USA FRIARS", ClassificationAxis.Friars),
        new("C2B2", ClassificationAxis.Special),
        new("CFI", ClassificationAxis.Special),
    ];

    /// <summary>The display names alone, for the assertions and reads that do not care about axes.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(s => s.Name)];
}
