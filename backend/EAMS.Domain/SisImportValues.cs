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
/// Where a <em>running</em> import has got to — <c>SisImportBatches.ProgressPhase</c>.
///
/// <para>
/// <b>This is not a second status column and must not be read as one.</b>
/// <see cref="SisImportStatus"/> says what became of the batch and is what every existing reader,
/// index and reconciliation depends on. This says which step of a run is currently executing, and it
/// only means anything while the status is <see cref="SisImportStatus.Running"/>. The two answer
/// different questions: "did it work?" versus "is it stuck, and on what?".
/// </para>
///
/// <para>
/// <b>The list is ordered, and the order is load-bearing.</b> <see cref="All"/> is written in the
/// exact sequence a run passes through, so <c>All.IndexOf(phase) + 1</c> is the phase number a
/// progress reader shows and <c>All.Count</c> — minus the terminal <see cref="Done"/> — is the count
/// it shows it out of. A caller that re-orders this list changes what every stored
/// <c>ProgressPhaseNumber</c> meant, which is why the numbers are written to the row rather than
/// derived from this list at read time.
/// </para>
///
/// <para>
/// <b>The mapping to <c>SisImportService.ExecuteAsync</c>, which is the contract between this list and
/// the code that writes it.</b> Each phase names one step of that method, in that method's own order:
/// </para>
///
/// <list type="table">
///   <item>
///     <term><see cref="ClearingPreviousRun"/></term>
///     <description>
///     The <c>ExecuteDelete</c> of this batch's <c>SisImportRowEntities</c>. Non-empty only when this
///     is a retry, and the one phase that can be instantaneous on a first run.
///     </description>
///   </item>
///   <item>
///     <term><see cref="ParsingRows"/></term>
///     <description>Resolving the batch's RFID source column, then <c>ParseRows</c> over the staged rows.</description>
///   </item>
///   <item>
///     <term><see cref="ResolvingDimensions"/></term>
///     <description>
///     <c>ResolveDimensionsAsync</c> — colleges, programmes, courses, instructors, offerings — and the
///     <c>SaveChanges</c> that lands them.
///     </description>
///   </item>
///   <item>
///     <term><see cref="ResolvingFacts"/></term>
///     <description>
///     <c>ResolveFactsAsync</c> — students, cards, enrollments, term records. The longest phase on a
///     full roster, and the reason a run needs progress at all.
///     </description>
///   </item>
///   <item>
///     <term><see cref="WritingFacts"/></term>
///     <description>
///     The <c>SaveChanges</c> that lands the fact pass. Separate from
///     <see cref="ResolvingFacts"/> because it is one long database call rather than per-row work —
///     a run sitting here is blocked on SQL Server, which is a different diagnosis.
///     </description>
///   </item>
///   <item>
///     <term><see cref="RecordingRowResults"/></term>
///     <description>
///     Applying the row ledger, tallying the batch counters, stamping <c>FinishedAt</c>, and saving.
///     </description>
///   </item>
///   <item>
///     <term><see cref="RefreshingStudentCache"/></term>
///     <description><c>RefreshStudentCacheAsync</c> — the ADR-001 D-2 denormalized student columns.</description>
///   </item>
///   <item>
///     <term><see cref="SyncingStudentGroups"/></term>
///     <description>
///     The derived-group projection for the term. Last, and outside the row results, so a batch can
///     read <c>Completed</c> while this is still running — which is precisely why it is named.
///     </description>
///   </item>
///   <item>
///     <term><see cref="Done"/></term>
///     <description>Terminal. Every step above finished; the status column carries the outcome.</description>
///   </item>
/// </list>
///
/// <para>
/// <b>Nothing writes these yet.</b> The column, the constants and the DTO fields land together so the
/// schema change is one migration rather than three; the writer is a later phase. Until then every
/// batch's <c>ProgressPhase</c> is NULL, which honestly reads as "this run predates progress
/// reporting".
/// </para>
/// </summary>
public static class SisImportPhase
{
    /// <summary>Deleting the previous attempt's row-entity fan-out.</summary>
    public const string ClearingPreviousRun = "ClearingPreviousRun";

    /// <summary>Reading the staged rows' cells into parsed rows.</summary>
    public const string ParsingRows = "ParsingRows";

    /// <summary>Colleges, programmes, courses, instructors and offerings.</summary>
    public const string ResolvingDimensions = "ResolvingDimensions";

    /// <summary>Students, cards, enrollments and term records.</summary>
    public const string ResolvingFacts = "ResolvingFacts";

    /// <summary>The single save that lands the fact pass.</summary>
    public const string WritingFacts = "WritingFacts";

    /// <summary>Row outcomes, batch counters and <c>FinishedAt</c>.</summary>
    public const string RecordingRowResults = "RecordingRowResults";

    /// <summary>The ADR-001 D-2 denormalized student columns.</summary>
    public const string RefreshingStudentCache = "RefreshingStudentCache";

    /// <summary>The derived student-group projection for the term.</summary>
    public const string SyncingStudentGroups = "SyncingStudentGroups";

    /// <summary>
    /// The run is over. Terminal, and deliberately <em>in</em> <see cref="All"/> rather than expressed
    /// as a NULL: a batch whose phase is NULL has never reported progress at all, and a batch that
    /// finished is not the same thing. See <see cref="Working"/> for the eight that are steps.
    /// </summary>
    public const string Done = "Done";

    /// <summary>
    /// The eight working phases followed by <see cref="Done"/>, <b>in run order</b> — see the type
    /// remarks. Re-ordering this list re-interprets every <c>ProgressPhaseNumber</c> already stored.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        ClearingPreviousRun, ParsingRows, ResolvingDimensions, ResolvingFacts, WritingFacts,
        RecordingRowResults, RefreshingStudentCache, SyncingStudentGroups, Done,
    ];

    /// <summary>
    /// The eight phases that are actual work, in run order. This — not <see cref="All"/> — is the
    /// denominator of "phase 4 of 8": counting <see cref="Done"/> as a step would make a finished run
    /// report 9 of 9 and a run on its last real step report 8 of 9, which reads as "one step left"
    /// forever.
    /// </summary>
    public static readonly IReadOnlyList<string> Working =
    [
        ClearingPreviousRun, ParsingRows, ResolvingDimensions, ResolvingFacts, WritingFacts,
        RecordingRowResults, RefreshingStudentCache, SyncingStudentGroups,
    ];

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
    /// This row's RFID serial matches a card that exists but is <em>deactivated</em>, so no card was
    /// created and the revoked one was left revoked.
    ///
    /// <para>
    /// <b>Why not simply issue a new one.</b> A card is deactivated by a person, for a reason the roster
    /// has no column for — lost, stolen, or a suspended student. The serial names one physical card, so
    /// creating a fresh active card carrying it makes that same physical card work again and silently
    /// reverses the decision on the next import. ADR-001 D-3 contemplated deactivate-then-reissue, where
    /// a replacement card is issued to the same student; it did not contemplate a revocation with no
    /// replacement. Re-issuing is a back-office action with a person behind it, exactly like un-deleting
    /// a student, so this reports and declines.
    /// </para>
    ///
    /// <para>
    /// <b>Separating the serial from REGNO strengthened this, it did not weaken it.</b> While the UID
    /// was derived from the student number, a revoked card reappearing was arguably an artefact of that
    /// derivation. It is not: the export is a snapshot of who is enrolled, it has no column saying why a
    /// card was revoked, and a serial the registrar still lists against a student is exactly what a lost
    /// or stolen card looks like in the next file. The roster cannot answer the only question that
    /// matters here, so it does not get to decide.
    /// </para>
    /// </summary>
    public const string RfidCardRevoked = "RfidCardRevoked";

    /// <summary>
    /// This batch's profile maps <c>RfidCard.CardUid</c> from a column that is <em>not</em> the RFID
    /// serial — so the card issued for this row carries whatever that column held, which for every
    /// profile authored before 2026-07-30 is the student number.
    ///
    /// <para>
    /// <b>Why this warns rather than refuses.</b> ADR-001 D-4 is explicit that a batch executes the
    /// rules it was uploaded under, and that guarantee is the whole reason profiles are versioned —
    /// re-running a July batch has to reproduce July, or the audit trail is a fiction. So the old
    /// mapping is allowed to run. What was missing is that it ran <em>silently</em>: the client's
    /// 2026-07-30 correction established that REGNO is not a card serial, and a re-run of an older batch
    /// would go on minting student-number cards with nothing in the batch report saying so.
    /// </para>
    ///
    /// <para>
    /// <b>It cannot fire on an ordinary import.</b> The live profile maps the RFID column, and a roster
    /// with no RFID column resolves to no mapping at all — which is silence, not this. Reaching this
    /// code means a batch is pinned to a pre-correction profile version, which is exactly the case worth
    /// one line in the report.
    /// </para>
    /// </summary>
    public const string RfidCardFromLegacyMapping = "RfidCardFromLegacyMapping";

    /// <summary>
    /// A category column named a value this school's vocabulary cannot assign: it is not in
    /// <c>Classifications</c> at all, or it is there but retired or merged away. The person imported and
    /// is <b>not</b> classified on that axis.
    ///
    /// <para>
    /// <b>The importer does not create the classification, and that asymmetry with colleges and
    /// programmes is deliberate.</b> Every other dimension in this pipeline is minted from the file
    /// because nobody else owns it. The vocabulary is different on both counts: QA's Q2 says an
    /// administrator edits the list, and <c>UX_Classifications_SchoolId_NameKey</c> makes a row created
    /// from a mis-keyed cell permanent — there is no delete while anything references it, only a merge
    /// somebody has to perform. A roster whose <c>STUDENT_CATEGORY</c> column is filled with
    /// <c>'NA'</c>, which is exactly what a hastily generated export does, would otherwise mint a
    /// category called NA and file 21,497 people under it. Reporting costs one warning; minting costs an
    /// administrator a merge and leaves the population mis-filed in the meantime.
    /// </para>
    ///
    /// <para>
    /// A <em>retired</em> value reports here too, rather than reactivating: retiring is a decision a
    /// person made, and the roster does not get to reverse it — the same rule
    /// <see cref="RfidCardRevoked"/> applies to a revoked card.
    /// </para>
    /// </summary>
    public const string ClassificationUnavailable = "ClassificationUnavailable";

    /// <summary>
    /// The file names a classification on an axis where this person already holds a <em>different</em>
    /// one. <b>The stored assignment is kept and the file's value is not applied.</b>
    ///
    /// <para>
    /// <b>Why the import yields to what is already there.</b> A classification can now be set by hand
    /// (<c>PUT /students/{id}/classifications/{id}</c>), and nothing on the junction row records which
    /// writer put it there — so "overwrite unless a human set it" is not a rule this schema can express.
    /// Of the two rules it can express, import-wins silently reverts every manual correction on the next
    /// run, for ever, which makes the back-office surface a formality. First-write-wins instead leaves a
    /// genuine source correction unapplied — a real cost, and the reason it is not silent: this warning
    /// names both values so an operator can see the disagreement and settle it. Stale and visible beats
    /// fresh and destructive.
    /// </para>
    ///
    /// <para>
    /// It cannot fire on an ordinary re-import. A person holding the value the file names is
    /// <c>Unchanged</c>, not a conflict — see <c>SisImportService.ResolveClassifications</c>.
    /// </para>
    /// </summary>
    public const string ClassificationConflict = "ClassificationConflict";

    /// <summary>
    /// The row carried no category in any of the four columns, and its registration number does not
    /// carry <see cref="RosterClassification.PersonnelNumberPrefix"/>. <b>No classification was
    /// assigned and none was guessed.</b>
    ///
    /// <para>
    /// 30 of the sample's 34 uncategorised rows: 4 junk (<c>Personnel No</c> equal to <c>Last Name</c>,
    /// first name literally <c>STUDENT</c>) and 26 that look like students whose flag was never set, two
    /// of those with a malformed number. Defaulting them to <c>STUDENT</c> would be right about roughly
    /// 26 and would invent the rest, with nothing downstream able to tell the invented from the stated —
    /// which is why the rule is that a classification is read, never inferred.
    /// </para>
    ///
    /// <para>
    /// <b>It cannot fire on a file that carries no category column at all</b>, which is today's roster:
    /// a warning on 100% of every batch's rows would make <c>CompletedWithWarnings</c> the permanent
    /// status of every import and bury the genuine warnings, exactly as <c>SisImportService.ParseRows</c>
    /// records for a missing RFID column. Silence about a column that is not there is the honest report.
    /// </para>
    /// </summary>
    public const string ClassificationMissing = "ClassificationMissing";

    /// <summary>
    /// The row carried no category in any column, but its registration number starts with
    /// <see cref="RosterClassification.PersonnelNumberPrefix"/> — so it is very probably personnel.
    /// <b>Still no classification was assigned.</b>
    ///
    /// <para>
    /// <b>Separate from <see cref="ClassificationMissing"/> because the follow-up is different.</b> This
    /// is a personnel record whose category column the registrar left blank — 4 rows in the sample, all
    /// fixable at source. <see cref="ClassificationMissing"/> is mostly students whose flag was never
    /// set. An operator filtering the batch by code gets the two piles separately, which is the whole
    /// reason a code exists beside the message.
    /// </para>
    ///
    /// <para>
    /// <b>Why it does not simply assign a personnel category.</b> The prefix narrows the answer to one
    /// of <c>NAP</c>, <c>ACAD</c>, <c>ANT</c>, <c>SUPERVISORY/MANAGERIAL</c> — and, on 12 sample rows,
    /// <c>USA FRIARS</c> — and picking among them is a guess stored where a fact is expected. See
    /// <see cref="RosterClassification.Outcome.PersonnelNumberOnly"/>.
    /// </para>
    /// </summary>
    public const string ClassificationRegNoSuggestsPersonnel = "ClassificationRegNoSuggestsPersonnel";

    /// <summary>
    /// This batch's import profile maps a source column to a category target whose axis is not one of
    /// the four — <c>StudentClassification.Faculty</c>, say, or a target that is the prefix and nothing
    /// after it. <b>That column was not read, and nobody in this batch is classified on the axis the
    /// operator was naming.</b>
    ///
    /// <para>
    /// <b>It carries a second, narrower fault under the same code: a target whose axis is real but
    /// whose profile row names no readable source column</b> — neither a source column name nor a
    /// source key — and which no later row for that axis rescues. The message says which of the two it
    /// is; the code does not distinguish them because nothing an operator does with it differs. Both
    /// are one profile row that reads nothing and files nobody, and both are repaired by correcting
    /// that row and running the batch again. A second code would split one pile in the batch report
    /// for a distinction that changes no action.
    /// </para>
    ///
    /// <para>
    /// <b>This is an operator's typo rather than a fact about the data, and it was the one fault in this
    /// pipeline that was silent in both directions.</b> The unrecognised target is skipped, so no column
    /// is read for it; and because <c>SisImportService.ParseRows</c> sets <c>CategoryColumnsPresent</c>
    /// only from a column that <em>was</em> read, <see cref="ClassificationMissing"/> could not fire for
    /// it either — to the importer the file looks exactly like one that never carried the column. A
    /// batch authored against <c>Faculty</c> therefore finished <c>Completed</c>, clean, with 21,497
    /// people unclassified on the axis the whole mapping existed to read. That is the cost of encoding
    /// the axis in a string (<c>SisImportProfileTemplate.ClassificationTargetPrefix</c>), and this code
    /// is the price paid back.
    /// </para>
    ///
    /// <para>
    /// <b>Why it warns rather than refusing the run.</b> ADR-001 D-4 makes a batch execute the mapping it
    /// was uploaded under, and <see cref="RfidCardFromLegacyMapping"/> has already settled what to do
    /// with a mapping this system disagrees with: let it run, and stop being quiet about it. Refusing
    /// would spend an entire roster — every student, offering and enrollment in the file — on one
    /// mistyped word in a supplementary column, and it would turn the re-run of a historical batch into
    /// a failure, which is the one thing D-4 exists to prevent.
    /// </para>
    ///
    /// <para>
    /// <b>Why every row carries it, where <see cref="ClassificationMissing"/> deliberately does not.</b>
    /// The standing objection to a warning on 100% of a batch's rows — it makes
    /// <c>CompletedWithWarnings</c> the permanent status of every import and buries the genuine
    /// warnings — is an objection about a condition that is the <em>normal</em> case. This one cannot
    /// arise from a correctly authored profile at all, so every batch it fires on is a batch that needs
    /// looking at. And the fault is a property of the batch rather than of any row, which is exactly why
    /// it is said on all of them: <c>WarningRows == TotalRows</c> is the signature that says "this is
    /// the mapping, not the data" from the batch counters alone. It is the batch-level report this
    /// schema has nowhere else to put — <c>SisImportBatch.FailureReason</c> is meaningful only on a run
    /// that did not finish, and a batch-level warning column would be a migration for one sentence.
    /// </para>
    /// </summary>
    public const string ClassificationAxisUnknown = "ClassificationAxisUnknown";

    /// <summary>
    /// A category column named a value that IS in this school's vocabulary, but on another axis —
    /// <c>NAP</c> typed under <c>STUDENT_CATEGORY</c>. <b>The value is not applied, on either axis, and
    /// nothing the person already holds is changed.</b> The message names the column it was found in and
    /// the column it belongs in.
    ///
    /// <para>
    /// <b>Why it is refused rather than filed on its real axis.</b> The column and the value contradict
    /// each other, and either could be the mistake: NAP in the student column may be a staff member
    /// pasted one column left, or a student whose value is wrong. Filing it under Personnel picks one
    /// reading silently.
    /// </para>
    ///
    /// <para>
    /// <b>And applying it was a crash, not merely a wrong answer.</b> Before this code existed the
    /// importer checked "already holds something on this axis" by the column's axis but inserted on the
    /// value's, so a row naming NAP under STUDENT_CATEGORY and ACAD under PERSONNEL_CATEGORY — or NAP
    /// under STUDENT_CATEGORY for somebody already holding ACAD, or the second import of any file with NAP
    /// in the student column — inserted a second Personnel row and violated
    /// <c>UX_StudentClassifications_Student_Axis</c>, failing the whole batch with no row number.
    /// </para>
    /// </summary>
    public const string ClassificationWrongAxis = "ClassificationWrongAxis";

    public static readonly IReadOnlyList<string> All =
    [
        CourseTitleAlias, CourseCollegeAdopted, InstructorPlaceholder,
        SectionSpansPrograms, SectionUnspecified, StudentIdentityConflict, RfidCardRevoked,
        RfidCardFromLegacyMapping, ClassificationUnavailable, ClassificationConflict,
        ClassificationMissing, ClassificationRegNoSuggestsPersonnel, ClassificationAxisUnknown,
        ClassificationWrongAxis,
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
    /// The row's RFID serial identifies a card that belongs to a <em>different</em> student.
    ///
    /// <para>
    /// <b>How it is reachable, now that the serial is its own column.</b> Two shapes, both real. First,
    /// two rows <em>inside one file</em> carrying the same serial for two different REGNOs: a mis-keyed
    /// export, a cloned card, or a serial recycled onto a new card while the old holder is still listed.
    /// Second, a serial whose active card in the database already belongs to somebody else — the same
    /// recycling seen across two imports instead of within one. Nothing in the export prevents either:
    /// <c>UX_RfidCards_SchoolId_CardUid_Active</c> is keyed on the serial, not on the student, so the
    /// file is free to say two students hold one card and the database is not.
    /// </para>
    ///
    /// <para>
    /// <b>Why this fails the row instead of warning.</b> Resolving it by moving the card is
    /// unrecoverable: the original student loses their active card, every subsequent tap on that
    /// physical card is attributed to the wrong person, and every <c>AttendanceRecords.RfidCardId</c>
    /// already written points at a card whose <c>StudentId</c> moved underneath it — history rewritten
    /// with nothing to say it happened, which is what ADR-001 D-3 exists to prevent. Issuing a competing
    /// active card instead is not an option either: it violates
    /// <c>UX_RfidCards_SchoolId_CardUid_Active</c> and takes the whole batch down with a raw constraint
    /// error in place of a row-level message. So the row fails, naming both students, and the card is
    /// left exactly as it was — the source is then fixable and the import re-runnable.
    /// </para>
    ///
    /// <para>
    /// <b>The pre-2026-07-30 rationale is gone and is not what this code means any more.</b> It used to
    /// read: REGNO <em>was</em> the card UID, and <c>usa00962</c> / <c>USA-00962</c> were two students
    /// by <c>UNIQUE(SchoolId, StudentNumber)</c> and one card by <see cref="CardUid.Normalize"/>. The
    /// client corrected that — the serial is a separate column — so the case-and-punctuation collision
    /// no longer exists. The code is kept because the consequences above are unchanged and it is a
    /// published value; what changed is only what arms it.
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

    /// <summary>
    /// A row of the <c>StudentClassifications</c> junction — one person's category on one axis. A source
    /// row can touch several, because a person holds several: 3 of the sample's 21,497 carry two.
    /// </summary>
    public const string StudentClassification = "StudentClassification";

    public static readonly IReadOnlyList<string> All =
    [
        College, Program, Course, Instructor, CourseOffering, CourseOfferingInstructor,
        Student, RfidCard, Enrollment, StudentTermRecord, StudentClassification,
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
/// The columns of the CICSS <c>Faculty Evaluation Report</c> sheet, named once.
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

    /// <summary>
    /// The physical card's own serial — <c>RfidCards.CardUid</c>, and what a tap authenticates on.
    ///
    /// <para>
    /// <b>It is not REGNO, and this constant is only a default.</b> The roster we hold today has no such
    /// column at all; it is expected in a later client export under a header nobody has seen yet. The
    /// column the pipeline actually reads is resolved per batch from the ADR-001 D-4 profile — the row
    /// whose <c>TargetField</c> is <c>RfidCard.CardUid</c> — so when the real file arrives with the
    /// column called something else, an operator authors a new profile version and no code changes. This
    /// constant supplies the built-in version's default header and nothing more.
    /// </para>
    ///
    /// <para>
    /// <b>Deliberately absent from <see cref="Required"/>.</b> A file without it must still select the
    /// roster sheet and import every student — that is the normal case today, and a student with no card
    /// is a first-class outcome, not an error.
    /// </para>
    ///
    /// <para>
    /// <b>The value is a string of decimal digits and its leading zeros are significant.</b>
    /// <c>0012503326</c> and <c>12503326</c> are different cards. See
    /// <see cref="RosterText.FormatNumericCell"/> for the integer round-trip hazard that makes a cell
    /// Excel stores as a <em>number</em> unsafe for this column.
    /// </para>
    /// </summary>
    public const string RfidCardSerial = "RFID";

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

    // ------------------------------------------------------------------ Task 5: the category columns
    //
    // Four columns, one per ClassificationAxis, mirroring the four the client's access-control export
    // already carries (STUDENTTEMP / PERSONNEL / FRIARS / SPECIAL in Personnel.xlsx).
    //
    // FOUR, and not the two QA's Q3 described. A person holds at most one value per axis and several
    // axes at once — 3 of the sample's 21,497 rows carry two categories — and two columns cannot say
    // so without one of the two silently winning. That is the identical failure ADR-001 D-2 documents
    // for the single-valued section cache, and it is the reason StudentClassifications is a junction
    // rather than a column on Students; carrying the same shape through the file is what keeps the two
    // people who are two things two things.
    //
    // NAMED BY US, not copied from the export, because these ship in the import TEMPLATE the client
    // fills in — so '_CATEGORY' reads as a category everywhere, where 'STUDENTTEMP' is an internal
    // spelling nobody outside their access-control system can explain. A client who bolts their own
    // column names on instead is not stuck: header matching is by AcademicKey, and the profile
    // (ADR-001 D-4) resolves the source column per batch, so a different header is a new profile
    // version rather than a code change — exactly the seam RfidCardSerial documents.
    //
    // NONE of them is in Required, and that is load-bearing: every roster file that exists today lacks
    // all four, and a required addition would reject the only file anyone has.

    /// <summary><see cref="ClassificationAxis.Student"/>. Holds <c>STUDENT</c> — 20,861 sample rows.</summary>
    public const string StudentCategory = "STUDENT_CATEGORY";

    /// <summary>
    /// <see cref="ClassificationAxis.Personnel"/>. Holds <c>NAP</c> (292), <c>ACAD</c> (271),
    /// <c>ANT</c> (7) and <c>SUPERVISORY/MANAGERIAL</c> (1).
    /// </summary>
    public const string PersonnelCategory = "PERSONNEL_CATEGORY";

    /// <summary><see cref="ClassificationAxis.Friars"/>. Holds <c>USA FRIARS</c> — 13 rows.</summary>
    public const string FriarsCategory = "FRIARS_CATEGORY";

    /// <summary><see cref="ClassificationAxis.Special"/>. Holds <c>C2B2</c> (16) and <c>CFI</c> (5).</summary>
    public const string SpecialCategory = "SPECIAL_CATEGORY";

    /// <summary>
    /// Which column carries which axis, in <see cref="ClassificationAxis.All"/> order. Named once
    /// because three places must agree: this list, the built-in profile's rows, and the parse that
    /// reads the cells.
    /// </summary>
    public static readonly IReadOnlyList<(string Axis, string Column)> ClassificationColumns =
    [
        (ClassificationAxis.Student, StudentCategory),
        (ClassificationAxis.Personnel, PersonnelCategory),
        (ClassificationAxis.Friars, FriarsCategory),
        (ClassificationAxis.Special, SpecialCategory),
    ];

    /// <summary>
    /// All twenty-two, in the order the export writes them — with <see cref="RfidCardSerial"/> placed
    /// beside <see cref="RegNo"/> because the two are the row's identity columns and nothing more
    /// specific is known: no export carrying an RFID column has been seen, so its real position is a
    /// guess and only the fixture and the upload preview read this order at all.
    ///
    /// <para>
    /// The four category columns are <b>appended</b> rather than grouped with the identity columns they
    /// describe. Nothing reads this list positionally except the fixture, but an existing file's columns
    /// keep their ordinals this way, so the template's diff is additive in the same sense the profile
    /// version bump is.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        RegNo, RfidCardSerial, StudentFirstName, StudentMiddleName, StudentLastName, FullName,
        EmailId, UsaEmail, CollegeName, Program, SectionName, CourseCode, CourseName,
        TeacherFullName, TeacherFirstName, TeacherLastName, TeacherSuffix, TeacherCollege,
        StudentCategory, PersonnelCategory, FriarsCategory, SpecialCategory,
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
    /// Deliberately not all eighteen: a required-column list that includes every optional column turns
    /// a harmless export change into a total failure. These nine are the ones without which a row cannot
    /// be placed.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="RfidCardSerial"/> is deliberately not here</b>, and that absence is load-bearing
    /// rather than an oversight. The roster in hand carries no RFID column; listing it would make every
    /// worksheet in every file fail this test and the import would reject the only file that exists.
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
