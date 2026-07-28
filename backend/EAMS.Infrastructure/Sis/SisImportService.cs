using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Sis;

/// <summary>
/// Technical Plan §10 — the school-year roster import. See <see cref="ISisImportService"/> for the
/// contract; this file is about how the three passes work and why they are three.
///
/// <para>
/// <b>Pass 1 is distinct-driven, and that is a correctness property before it is a performance one.</b>
/// The source's 536 rows describe 1 college, 1 programme, 21 courses, 18 teachers and 38 offerings.
/// Resolving those per row means asking "does this course exist?" 536 times, and — worse — creating it
/// 536 times inside one unit of work unless every creation is remembered, which is the same dictionary
/// this pass builds, only built accidentally and in the middle of the fact loop. Doing it deliberately
/// and first means the fact loop never decides whether a dimension exists.
/// </para>
///
/// <para>
/// <b>Pass 2 writes facts and nothing else.</b> Every dimension it needs is already resolved, so a row
/// is a lookup and at most five upserts.
/// </para>
///
/// <para>
/// <b>Pass 3 refreshes what is derived.</b> The ADR-001 D-2 student display cache, then
/// <see cref="IStudentGroupProjection"/>. Both are idempotent; neither is a source of truth.
/// </para>
///
/// <para>
/// <b>There is no wrapping transaction, deliberately.</b> Every write here is an upsert and the whole
/// run is idempotent, so an interrupted run is repaired by running it again — which is a recovery an
/// operator already knows how to perform. The alternatives are worse: a transaction spanning ~5,000
/// row-touches holds locks on the roster for the duration of the import, and it cannot be combined with
/// the retrying execution strategy this application registers (<c>EnableRetryOnFailure</c>) without
/// wrapping every phase in <c>IExecutionStrategy.ExecuteAsync</c>, which would silently replay
/// already-committed phases on a transient fault. Partial progress that is safe to re-run beats
/// all-or-nothing that is not safe to retry.
/// </para>
/// </summary>
internal sealed class SisImportService : ISisImportService
{
    private readonly EamsDbContext _db;
    private readonly IStudentGroupProjection _projection;
    private readonly ICurrentUser _currentUser;

    public SisImportService(
        EamsDbContext db, IStudentGroupProjection projection, ICurrentUser currentUser)
    {
        _db = db;
        _projection = projection;
        _currentUser = currentUser;
    }

    /// <summary>How many staged rows an upload preview returns. Enough to eyeball, small enough to render.</summary>
    private const int PreviewSampleSize = 25;

    // ==================================================================================== upload

    public async Task<SisImportPreviewDto> UploadAsync(
        SisImportUploadRequest request, CancellationToken ct = default)
    {
        var term = await LoadTermAsync(request.TermId, ct);
        var file = ExcelRosterReader.Read(request.Content);
        var profile = await EnsureBuiltInProfileAsync(term.SchoolId, ct);

        var batch = new SisImportBatch
        {
            SchoolId = term.SchoolId,
            TermId = term.Id,
            Source = SisImportSource.Excel,
            FileName = RosterText.Clean(request.FileName),
            SourceSheetName = file.SheetName,
            FileHash = file.FileHash,
            Status = SisImportStatus.Pending,
            TotalRows = file.Rows.Count,
            ImportProfileId = profile.Id,
            RunByUserId = _currentUser.UserId,
        };
        _db.SisImportBatches.Add(batch);

        foreach (var source in file.Rows)
        {
            // Keyed by the header as written, so a row dump is readable by whoever exported the file.
            // The pipeline re-keys through SisRosterColumns.HeaderKey when it reads this back, so the
            // legibility costs nothing at the point of use.
            var raw = new Dictionary<string, string>(file.Columns.Count, StringComparer.Ordinal);
            foreach (var column in file.Columns)
            {
                if (column.Length == 0) continue;
                raw[column] = source.Raw(column);
            }

            _db.SisImportRows.Add(new SisImportRow
            {
                Batch = batch,
                RowNumber = source.RowNumber,
                RawData = JsonSerializer.Serialize(raw),
                RowHash = RosterText.Fingerprint(file.Columns.Select(source.Raw)),
                Result = SisImportRowResult.Pending,
            });
        }

        await _db.SaveChangesAsync(ct);

        return await BuildPreviewAsync(batch, term, file, ct);
    }

    // ======================================================================================= run

    public async Task<SisImportBatchDto> RunAsync(
        Guid batchId, Guid termId, CancellationToken ct = default)
    {
        var batch = await _db.SisImportBatches.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(b => b.Id == batchId, ct)
                    ?? throw new SisImportBatchNotFoundException(
                        $"No import batch {batchId}. Upload the file to create one; a batch id is " +
                        "returned by the upload and is not something a caller invents.");

        if (batch.TermId != termId)
            throw new SisImportException(
                $"Batch {batchId} was uploaded for term {batch.TermId} but the run declared term " +
                $"{termId}. The two must match — see SisImportRunRequest for why the term is confirmed " +
                "at run time as well as at upload.");

        // A completed batch is a historical record, not a re-runnable script: re-running it would
        // rewrite its counters and destroy the evidence of what the first run did. Re-importing is
        // uploading the file again, which produces a second batch and leaves the first intact — and
        // that second batch reporting every row Skipped is the idempotency proof. A Failed batch is
        // different: nothing about it is worth preserving, and retrying is the obvious repair.
        if (batch.Status is not (SisImportStatus.Pending or SisImportStatus.Failed))
            throw new SisImportException(
                $"Batch {batchId} has already been run (status {batch.Status}). Upload the file again " +
                "to import it a second time; a completed batch is kept as the record of what that run " +
                "did and is not rewritten.");

        var term = await LoadTermAsync(termId, ct);

        var staged = await _db.SisImportRows.IgnoreQueryFilters()
            .Where(r => r.BatchId == batchId)
            .OrderBy(r => r.RowNumber)
            .ToListAsync(ct);

        batch.Status = SisImportStatus.Running;
        batch.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        try
        {
            await ExecuteAsync(batch, term, staged, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Marked and rethrown, never swallowed. The batch says the run stopped and how far it got;
            // the exception still reaches the caller, is logged, and becomes a 500 with a trace id. A
            // batch left in Running forever would be indistinguishable from one still in progress.
            //
            // ExecuteUpdate rather than SaveChanges, and that is the whole point of this block working.
            // The most likely way ExecuteAsync fails IS a SaveChanges failure — a truncation, a unique
            // violation — and at that moment the context still tracks every entity the failed pass
            // added. A recovery SaveChanges would re-attempt all of them, throw the same error from
            // inside this catch, replace the original exception with it, and never persist the status —
            // leaving the batch stuck in Running, which is exactly what this block exists to prevent.
            // ExecuteUpdate issues one UPDATE against the batch row and touches no tracked graph.
            batch.Status = SisImportStatus.Failed;
            batch.FinishedAt = DateTime.UtcNow;

            try
            {
                _db.ChangeTracker.Clear();
                await _db.SisImportBatches.IgnoreQueryFilters()
                    .Where(b => b.Id == batch.Id)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(b => b.Status, batch.Status)
                              .SetProperty(b => b.FinishedAt, batch.FinishedAt),
                        CancellationToken.None);
            }
            catch (Exception statusWriteFailure)
            {
                // Not swallowed, and not rethrown either: the original exception is the one that
                // explains the run, and losing it to a second failure is the defect above. The second
                // is attached to the first so it reaches whoever inspects the exception rather than
                // disappearing.
                ex.Data["SisImportStatusWriteFailure"] = statusWriteFailure.ToString();
            }

            throw;
        }

        return ToDto(batch, term);
    }

    private async Task ExecuteAsync(
        SisImportBatch batch, Term term, IReadOnlyList<SisImportRow> staged, CancellationToken ct)
    {
        // Only ever non-empty when this is a retry of a Failed batch. The fan-out is rebuilt from
        // scratch each run, so leaving the previous attempt's rows would double every entry and make
        // the trail — whose entire job is to say what happened — say it twice with different answers.
        // ExecuteDelete rather than loading and removing: these rows carry no guarded columns and there
        // can be several thousand of them.
        var rowIds = staged.Select(r => r.Id).ToList();
        await _db.SisImportRowEntities.IgnoreQueryFilters()
            .Where(e => rowIds.Contains(e.SisImportRowId))
            .ExecuteDeleteAsync(ct);

        var ledger = new RowLedger(staged);
        var parsed = ParseRows(staged, ledger);

        var dimensions = await ResolveDimensionsAsync(term, parsed, ledger, ct);
        await _db.SaveChangesAsync(ct);

        // The fact pass can fail rows of its own — a REGNO whose card UID belongs to another student —
        // and those rows are excluded from the derived cache for the same reason they were excluded
        // from the facts: a failed row must not have contributed anything, including a Section string.
        var mismatchedRows = await ResolveFactsAsync(term, parsed, dimensions, ledger, ct);
        await _db.SaveChangesAsync(ct);

        ledger.Apply(_db);
        Tally(batch, staged);
        batch.FinishedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await RefreshStudentCacheAsync(term, parsed, dimensions, mismatchedRows, ct);
        await _projection.SyncTermAsync(term.Id, ct);
    }

    // =========================================================================== parse (per row)

    /// <summary>
    /// One staged row, interpreted. Every value here has already been through <see cref="RosterText"/>
    /// and, where it is a key, <see cref="AcademicKey"/> — nothing downstream re-normalizes, so there is
    /// exactly one place a normalization rule is applied.
    /// </summary>
    private sealed record ParsedRow(
        SisImportRow Staged,
        string RegNo,
        string CardUid,
        string FirstName,
        string? MiddleName,
        string LastName,
        string? Email,
        string? AlternateEmail,
        string CollegeName,
        string CollegeKey,
        string ProgramCode,
        string ProgramKey,
        string? SectionName,
        string SectionKey,
        string CourseCode,
        string CourseKey,
        string? CourseTitle,
        TeacherName Teacher,
        string? TeacherKey);

    private List<ParsedRow> ParseRows(IReadOnlyList<SisImportRow> staged, RowLedger ledger)
    {
        var parsed = new List<ParsedRow>(staged.Count);

        foreach (var row in staged)
        {
            var cells = ReadCells(row);
            string Raw(string column) =>
                cells.TryGetValue(SisRosterColumns.HeaderKey(column), out var v) ? v : "";

            // REGNO is the one column without which the row names nobody: it is the student number, the
            // card UID, and the key every fact in the row hangs off.
            var regNo = RosterText.Clean(Raw(SisRosterColumns.RegNo));
            if (regNo is null)
            {
                ledger.Fail(row, SisImportFailureCode.MissingRequiredValue,
                    $"{SisRosterColumns.RegNo} is blank. It is the student number and the RFID card " +
                    "UID; nothing in the row can be placed without it.");
                continue;
            }

            var firstName = RosterText.CleanName(Raw(SisRosterColumns.StudentFirstName));
            var lastName = RosterText.CleanName(Raw(SisRosterColumns.StudentLastName));
            if (firstName is null || lastName is null)
            {
                ledger.Fail(row, SisImportFailureCode.MissingRequiredValue,
                    $"{SisRosterColumns.StudentFirstName} and {SisRosterColumns.StudentLastName} are " +
                    $"both required (Students has them NOT NULL); REGNO {regNo} supplied " +
                    $"'{Raw(SisRosterColumns.StudentFirstName)}' / '{Raw(SisRosterColumns.StudentLastName)}'.");
                continue;
            }

            var collegeName = RosterText.Clean(Raw(SisRosterColumns.CollegeName));
            var programCode = RosterText.Clean(Raw(SisRosterColumns.Program));
            var courseCode = RosterText.Clean(Raw(SisRosterColumns.CourseCode));
            if (collegeName is null || programCode is null || courseCode is null)
            {
                ledger.Fail(row, SisImportFailureCode.MissingRequiredValue,
                    $"{SisRosterColumns.CollegeName}, {SisRosterColumns.Program} and " +
                    $"{SisRosterColumns.CourseCode} are required to place an enrollment; REGNO {regNo} " +
                    "left at least one blank.");
                continue;
            }

            var sectionName = RosterText.Clean(Raw(SisRosterColumns.SectionName));
            var keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SisRosterColumns.CollegeName] = AcademicKey.NormalizeOrUnspecified(collegeName),
                [SisRosterColumns.Program] = AcademicKey.NormalizeOrUnspecified(programCode),
                [SisRosterColumns.CourseCode] = AcademicKey.NormalizeOrUnspecified(courseCode),
                [SisRosterColumns.SectionName] = AcademicKey.NormalizeOrUnspecified(sectionName),
            };

            // Checked here rather than left to SQL Server, whose truncation error (2628) names neither
            // the row nor the column — see AcademicKey.IsWithinLength.
            var overlong = keys.FirstOrDefault(k => !AcademicKey.IsWithinLength(k.Value));
            if (overlong.Key is not null)
            {
                ledger.Fail(row, SisImportFailureCode.KeyTooLong,
                    $"{overlong.Key} normalizes to {overlong.Value.Length} characters, over the " +
                    $"{AcademicKey.MaxLength}-character key limit.");
                continue;
            }

            var teacher = TeacherNames.Parse(
                Raw(SisRosterColumns.TeacherFullName),
                Raw(SisRosterColumns.TeacherFirstName),
                Raw(SisRosterColumns.TeacherLastName),
                Raw(SisRosterColumns.TeacherSuffix));

            parsed.Add(new ParsedRow(
                Staged: row,
                RegNo: regNo,
                // Normalized as a UID because that is what every reader lookup normalizes to. Both
                // roster shapes — USA00962 and the legacy 2021005781 — are already letters and digits
                // only, so this is the identity function on them and neither is reformatted into the
                // other. StudentNumber keeps the verbatim value; the two columns are deliberately
                // separate (ADR-001, "Accepted Context").
                CardUid: CardUid.Normalize(regNo),
                FirstName: firstName,
                MiddleName: RosterText.CleanName(Raw(SisRosterColumns.StudentMiddleName)),
                LastName: lastName,
                Email: RosterText.CleanEmail(Raw(SisRosterColumns.UsaEmail)),
                AlternateEmail: RosterText.CleanEmail(Raw(SisRosterColumns.EmailId)),
                CollegeName: collegeName,
                CollegeKey: keys[SisRosterColumns.CollegeName],
                ProgramCode: programCode,
                ProgramKey: keys[SisRosterColumns.Program],
                SectionName: sectionName,
                SectionKey: keys[SisRosterColumns.SectionName],
                CourseCode: courseCode,
                CourseKey: keys[SisRosterColumns.CourseCode],
                CourseTitle: RosterText.Clean(Raw(SisRosterColumns.CourseName)),
                Teacher: teacher,
                TeacherKey: teacher.DisplayName is null
                    ? null
                    : AcademicKey.NormalizeOrUnspecified(teacher.DisplayName)));

            if (sectionName is null)
                ledger.Warn(row, SisImportWarningCode.SectionUnspecified,
                    $"{SisRosterColumns.SectionName} is blank; the offering is filed under " +
                    $"'{AcademicKey.Unspecified}' and its students get no section group.");

            if (teacher.IsPlaceholder)
                ledger.Warn(row, SisImportWarningCode.InstructorPlaceholder,
                    $"Teacher is the '{Raw(SisRosterColumns.TeacherFullName)}' placeholder; the " +
                    "offering is left unstaffed and no instructor row was created.");
        }

        return parsed;
    }

    private static Dictionary<string, string> ReadCells(SisImportRow row)
    {
        var raw = row.RawData is null
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(row.RawData) ?? [];

        var cells = new Dictionary<string, string>(raw.Count, StringComparer.Ordinal);
        foreach (var (header, value) in raw)
        {
            var key = SisRosterColumns.HeaderKey(header);
            if (key.Length > 0) cells.TryAdd(key, value);
        }

        return cells;
    }

    // ============================================================================ pass 1: dimensions

    /// <summary>
    /// A dimension row plus the credit for creating it: which staged row first named it, and what
    /// happened to it then. Every <em>other</em> row that names it reports <c>Unchanged</c>, which is
    /// what stops 536 rows all claiming to have created the same college.
    /// </summary>
    private sealed record Touched<T>(T Entity, string Action, int RowNumber)
    {
        public string ActionFor(int rowNumber) =>
            rowNumber == RowNumber ? Action : SisImportEntityAction.Unchanged;
    }

    private sealed record Dimensions(
        IReadOnlyDictionary<string, Touched<College>> Colleges,
        IReadOnlyDictionary<string, Touched<AcademicProgram>> Programs,
        IReadOnlyDictionary<string, Touched<Course>> Courses,
        IReadOnlyDictionary<string, Touched<Instructor>> Instructors,
        IReadOnlyDictionary<(Guid CourseId, string SectionKey), Touched<CourseOffering>> Offerings,
        IReadOnlySet<int> CollidedRows);

    private async Task<Dimensions> ResolveDimensionsAsync(
        Term term, IReadOnlyList<ParsedRow> parsed, RowLedger ledger, CancellationToken ct)
    {
        var schoolId = term.SchoolId;

        // Every read below carries an explicit SchoolId (or TermId) predicate and ignores the ambient
        // query filter, for the reason StudentGroupProjection spells out: the filter is inert whenever
        // no tenant is pinned, and an import that silently resolved another school's course would write
        // a cross-tenant row into the roster. The scope is not dropped, it is stated.
        var colleges = await _db.Colleges.IgnoreQueryFilters()
            .Where(c => c.SchoolId == schoolId).ToDictionaryAsync(c => c.NameKey, ct);
        var programs = await _db.Programs.IgnoreQueryFilters()
            .Where(p => p.SchoolId == schoolId).ToDictionaryAsync(p => p.CodeKey, ct);
        var courses = await _db.Courses.IgnoreQueryFilters()
            .Where(c => c.SchoolId == schoolId).ToDictionaryAsync(c => c.CodeKey, ct);
        var instructors = await _db.Instructors.IgnoreQueryFilters()
            .Where(i => i.SchoolId == schoolId).ToDictionaryAsync(i => i.NameKey, ct);

        var resolvedColleges = ResolveColleges(schoolId, parsed, colleges);
        var resolvedPrograms = ResolvePrograms(schoolId, parsed, programs, resolvedColleges);
        var (resolvedCourses, collidedRows) =
            ResolveCourses(schoolId, parsed, courses, resolvedColleges, ledger);
        var resolvedInstructors = ResolveInstructors(schoolId, parsed, instructors);

        WarnOnSectionsSpanningPrograms(parsed, ledger);

        // Offerings depend on the courses above, including ones that do not exist in the database yet.
        // That works because Entity assigns its Id in the initializer, so a pending course already has
        // the value the offering's FK needs and EF orders the two inserts by the graph.
        await _db.SaveChangesAsync(ct);

        var offerings = await ResolveOfferingsAsync(term, parsed, resolvedCourses, collidedRows, ct);

        return new Dimensions(
            resolvedColleges, resolvedPrograms, resolvedCourses, resolvedInstructors,
            offerings, collidedRows);
    }

    private Dictionary<string, Touched<College>> ResolveColleges(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed, Dictionary<string, College> existing)
    {
        var resolved = new Dictionary<string, Touched<College>>(StringComparer.Ordinal);

        foreach (var group in GroupRows(parsed, r => r.CollegeKey))
        {
            var first = group.First;

            if (existing.TryGetValue(first.CollegeKey, out var college))
            {
                var changed = Assign(college.Name, first.CollegeName, v => college.Name = v);
                if (changed) college.UpdatedAt = DateTime.UtcNow;
                resolved[first.CollegeKey] = new Touched<College>(
                    college, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            college = new College
            {
                SchoolId = schoolId,
                Name = first.CollegeName,
                NameKey = first.CollegeKey,
            };
            _db.Colleges.Add(college);
            existing[first.CollegeKey] = college;
            resolved[first.CollegeKey] =
                new Touched<College>(college, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    private Dictionary<string, Touched<AcademicProgram>> ResolvePrograms(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed,
        Dictionary<string, AcademicProgram> existing,
        IReadOnlyDictionary<string, Touched<College>> colleges)
    {
        var resolved = new Dictionary<string, Touched<AcademicProgram>>(StringComparer.Ordinal);

        foreach (var group in GroupRows(parsed, r => r.ProgramKey))
        {
            var first = group.First;
            var collegeId = colleges[first.CollegeKey].Entity.Id;

            if (existing.TryGetValue(first.ProgramKey, out var program))
            {
                var changed = Assign(program.Code, first.ProgramCode, v => program.Code = v);
                // A programme's college is a required FK, so an existing row always has one. Moving it
                // is a real change and is reported as an update rather than being applied quietly.
                changed |= Assign(program.CollegeId, collegeId, v => program.CollegeId = v);
                if (changed) program.UpdatedAt = DateTime.UtcNow;
                resolved[first.ProgramKey] = new Touched<AcademicProgram>(
                    program, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            program = new AcademicProgram
            {
                SchoolId = schoolId,
                CollegeId = collegeId,
                Code = first.ProgramCode,
                CodeKey = first.ProgramKey,
                Name = first.ProgramCode,
            };
            _db.Programs.Add(program);
            existing[first.ProgramKey] = program;
            resolved[first.ProgramKey] = new Touched<AcademicProgram>(
                program, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    /// <summary>
    /// Resolves courses, and is where the two hardest source defects are decided.
    ///
    /// <para>
    /// <b>One code, two titles.</b> <c>'GE Elect 2'</c> resolves to <em>The Entrepreneurial Mind</em> on
    /// 25 rows and <em>Gender and Society / Entrepreneurial Mind</em> on 6. <c>Courses.Title</c> is
    /// single-valued and is not identity (see <see cref="Course"/>), so one of them is necessarily lost
    /// from that column. First-seen wins — deterministic, because the rows are processed in worksheet
    /// order — and every row carrying the losing title imports normally with a
    /// <see cref="SisImportWarningCode.CourseTitleAlias"/> naming both. The row's own <c>RawData</c>
    /// keeps the original verbatim, so nothing is destroyed; what is refused is doing it silently.
    /// </para>
    ///
    /// <para>
    /// <b>One code, two colleges.</b> This one fails the row. See
    /// <see cref="SisImportFailureCode.CourseCollegeCollision"/> for why a silent resolve to the
    /// existing course is unrecoverable where a failed row is merely annoying.
    /// </para>
    /// </summary>
    private (Dictionary<string, Touched<Course>> Courses, HashSet<int> CollidedRows) ResolveCourses(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed, Dictionary<string, Course> existing,
        IReadOnlyDictionary<string, Touched<College>> colleges, RowLedger ledger)
    {
        var resolved = new Dictionary<string, Touched<Course>>(StringComparer.Ordinal);
        var collided = new HashSet<int>();

        foreach (var group in GroupRows(parsed, r => r.CourseKey))
        {
            var first = group.First;
            var collegeId = colleges[first.CollegeKey].Entity.Id;

            if (existing.TryGetValue(first.CourseKey, out var course))
            {
                if (course.CollegeId is { } owner && owner != collegeId)
                {
                    // Every row naming this course code fails, not just the first. Importing some of
                    // them would leave a course half-populated under a college it may not belong to,
                    // which is the ambiguous middle state this check exists to prevent.
                    foreach (var row in group.All)
                    {
                        collided.Add(row.Staged.RowNumber);
                        ledger.Fail(row.Staged, SisImportFailureCode.CourseCollegeCollision,
                            $"Course code '{first.CourseCode}' (key {first.CourseKey}) already exists " +
                            $"under a different college. Existing CollegeId {owner}; this row says " +
                            $"'{row.CollegeName}' ({collegeId}). This FAILS rather than warns because " +
                            "Courses are keyed UNIQUE(SchoolId, CodeKey) — institution-wide — and " +
                            "Courses.CollegeId is single-valued: resolving to the existing row merges " +
                            "two colleges' courses irreversibly, because the column cannot then hold " +
                            "both values. ADR-002 D-11 records this key as a deliberate hedge whose " +
                            "widening path (backfill CollegeId, make it NOT NULL, re-index to " +
                            "UNIQUE(SchoolId, CollegeId, CodeKey)) is data-preserving ONLY while no " +
                            "merged data exists — after a silent merge it becomes a split that has to " +
                            "re-key live CourseOfferings. Do not downgrade this to a warning to clear " +
                            "a blocked batch; fix the source, or widen the key first, then re-import.");
                    }

                    continue;
                }

                var changed = Assign(course.Code, first.CourseCode, v => course.Code = v);

                if (course.CollegeId is null)
                {
                    // Filling a blank rather than overwriting a value: an enrichment, not a conflict.
                    // Still warned, because it changes what a shared row means and someone should be
                    // able to see when it happened.
                    course.CollegeId = collegeId;
                    changed = true;
                    foreach (var row in group.All)
                        ledger.Warn(row.Staged, SisImportWarningCode.CourseCollegeAdopted,
                            $"Course '{first.CourseCode}' had no college recorded; adopted " +
                            $"'{first.CollegeName}' from this import.");
                }

                if (first.CourseTitle is not null && course.Title is null)
                {
                    course.Title = first.CourseTitle;
                    changed = true;
                }

                if (changed) course.UpdatedAt = DateTime.UtcNow;
                resolved[first.CourseKey] = new Touched<Course>(
                    course, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
            }
            else
            {
                course = new Course
                {
                    SchoolId = schoolId,
                    CollegeId = collegeId,
                    Code = first.CourseCode,
                    CodeKey = first.CourseKey,
                    Title = first.CourseTitle,
                };
                _db.Courses.Add(course);
                existing[first.CourseKey] = course;
                resolved[first.CourseKey] = new Touched<Course>(
                    course, SisImportEntityAction.Inserted, first.Staged.RowNumber);
            }

            WarnOnTitleAliases(group, course, ledger);
        }

        return (resolved, collided);
    }

    private static void WarnOnTitleAliases(RowGroup group, Course course, RowLedger ledger)
    {
        foreach (var row in group.All)
        {
            if (row.CourseTitle is null) continue;
            if (string.Equals(row.CourseTitle, course.Title, StringComparison.Ordinal)) continue;

            ledger.Warn(row.Staged, SisImportWarningCode.CourseTitleAlias,
                $"Course code '{row.CourseCode}' is recorded as '{course.Title}' but this row calls it " +
                $"'{row.CourseTitle}'. The first-seen title is kept on Courses.Title and this one is " +
                "recorded here as an alias; the row imported normally. Courses.Title is not identity " +
                "(see the Course type remarks) — the code is.");
        }
    }

    private Dictionary<string, Touched<Instructor>> ResolveInstructors(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed, Dictionary<string, Instructor> existing)
    {
        var resolved = new Dictionary<string, Touched<Instructor>>(StringComparer.Ordinal);

        // The placeholder is filtered out here, once, which is the whole implementation of "no
        // synthetic TBA instructor". Nothing downstream has to remember: there is simply no instructor
        // to link, and CourseOfferingInstructors is many-to-many so zero teachers is representable.
        var named = parsed.Where(r => !r.Teacher.IsPlaceholder && r.TeacherKey is not null).ToList();

        foreach (var group in GroupRows(named, r => r.TeacherKey!))
        {
            var first = group.First;
            var key = first.TeacherKey!;

            if (existing.TryGetValue(key, out var instructor))
            {
                var changed = Assign(
                    instructor.DisplayName, first.Teacher.DisplayName!, v => instructor.DisplayName = v);
                if (changed) instructor.UpdatedAt = DateTime.UtcNow;
                resolved[key] = new Touched<Instructor>(
                    instructor,
                    changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            instructor = new Instructor
            {
                SchoolId = schoolId,
                DisplayName = first.Teacher.DisplayName!,
                NameKey = key,
            };
            _db.Instructors.Add(instructor);
            existing[key] = instructor;
            resolved[key] = new Touched<Instructor>(
                instructor, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    /// <summary>
    /// The section half of the Phase 1 review's collision requirement.
    ///
    /// <para>
    /// <b>It warns where the course collision fails, and the asymmetry is deliberate.</b>
    /// <c>CourseOfferings</c> is keyed <c>(TermId, CourseId, SectionKey)</c> — the course is <em>in</em>
    /// the key — so two programmes using the section name <c>2-A</c> only collide when they are also
    /// the same course, which is usually genuinely the same class. <c>Courses</c> is keyed
    /// <c>(SchoolId, CodeKey)</c> with no such qualifier, which is why that one is unrecoverable and
    /// this one is not. What is not acceptable is silence: a section name shared across programmes is
    /// the leading indicator that the roster has outgrown institution-wide keys, and this is where it
    /// becomes visible before it becomes a merge.
    /// </para>
    /// </summary>
    private static void WarnOnSectionsSpanningPrograms(IReadOnlyList<ParsedRow> parsed, RowLedger ledger)
    {
        var programsBySection = parsed
            .Where(r => r.SectionKey != AcademicKey.Unspecified)
            .GroupBy(r => r.SectionKey, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.ProgramCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        foreach (var row in parsed)
        {
            if (!programsBySection.TryGetValue(row.SectionKey, out var programCodes)) continue;
            if (programCodes.Count < 2) continue;

            ledger.Warn(row.Staged, SisImportWarningCode.SectionSpansPrograms,
                $"Section '{row.SectionName ?? row.SectionKey}' appears under {programCodes.Count} " +
                $"programmes in this file ({string.Join(", ", programCodes)}). This WARNS rather than " +
                "fails — and the asymmetry with COURSE_COLLEGE_COLLISION is deliberate, not an " +
                "inconsistency to tidy up. CourseOfferings is keyed (TermId, CourseId, SectionKey) " +
                "with the course IN the key (ADR-002 D-11), so two programmes sharing a section name " +
                "collide only when they are also the same course, which is usually genuinely the same " +
                "class; nothing merges and no column has to hold two values. Courses has no such " +
                "qualifier, which is why that one is unrecoverable and this one is not. It is still " +
                "reported because a section name shared across programmes is how institution-wide " +
                "keys start to collide.");
        }
    }

    private async Task<Dictionary<(Guid, string), Touched<CourseOffering>>> ResolveOfferingsAsync(
        Term term, IReadOnlyList<ParsedRow> parsed,
        IReadOnlyDictionary<string, Touched<Course>> courses,
        IReadOnlySet<int> collidedRows, CancellationToken ct)
    {
        var existing = await _db.CourseOfferings.IgnoreQueryFilters()
            .Where(o => o.TermId == term.Id)
            .ToDictionaryAsync(o => (o.CourseId, o.SectionKey), ct);

        var resolved = new Dictionary<(Guid, string), Touched<CourseOffering>>();

        var eligible = parsed
            .Where(r => !collidedRows.Contains(r.Staged.RowNumber) && courses.ContainsKey(r.CourseKey))
            .ToList();

        foreach (var group in GroupRows(eligible, r => (courses[r.CourseKey].Entity.Id, r.SectionKey)))
        {
            var first = group.First;
            var key = (courses[first.CourseKey].Entity.Id, first.SectionKey);

            if (existing.TryGetValue(key, out var offering))
            {
                // AssignIfPresent: the offering is already keyed by SectionKey, so a row reaching this
                // branch with a blank SECTION_NAME is one whose section normalized to the same key from
                // a cell that says nothing — it is not an instruction to forget the display name that
                // is on the row. See AssignIfPresent for the general rule.
                var changed = AssignIfPresent(
                    offering.SectionName, first.SectionName, v => offering.SectionName = v);
                if (changed) offering.UpdatedAt = DateTime.UtcNow;
                resolved[key] = new Touched<CourseOffering>(
                    offering, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            offering = new CourseOffering
            {
                TermId = term.Id,
                CourseId = key.Item1,
                SectionKey = first.SectionKey,
                SectionName = first.SectionName,
            };
            _db.CourseOfferings.Add(offering);
            existing[key] = offering;
            resolved[key] = new Touched<CourseOffering>(
                offering, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    // ================================================================================ pass 2: facts

    /// <summary>
    /// Writes the facts, and returns the rows it <em>refused</em> to write — the ones whose REGNO
    /// resolves to a card UID owned by another student. They are the fact pass's own equivalent of
    /// <see cref="Dimensions.CollidedRows"/>, and the caller excludes them from the derived cache too.
    /// </summary>
    private async Task<IReadOnlySet<int>> ResolveFactsAsync(
        Term term, IReadOnlyList<ParsedRow> parsed, Dimensions dimensions,
        RowLedger ledger, CancellationToken ct)
    {
        var schoolId = term.SchoolId;
        var live = parsed.Where(r => !dimensions.CollidedRows.Contains(r.Staged.RowNumber)).ToList();
        if (live.Count == 0) return new HashSet<int>();

        var regNos = live.Select(r => r.RegNo).Distinct(StringComparer.Ordinal).ToList();
        var cardUids = live.Select(r => r.CardUid).Distinct(StringComparer.Ordinal).ToList();

        var students = await _db.Students.IgnoreQueryFilters()
            .Where(s => s.SchoolId == schoolId && regNos.Contains(s.StudentNumber))
            .ToDictionaryAsync(s => s.StudentNumber, StringComparer.Ordinal, ct);

        // Every card carrying one of these UIDs, active or not — see ResolveCards for why the inactive
        // ones have to be visible. Include(Student) so a mismatch can name the other student rather
        // than quoting a GUID at an operator: the owner's StudentNumber is very often absent from this
        // file (that is how the two rows drifted apart), so it cannot be recovered from `students`.
        var cards = (await _db.RfidCards.IgnoreQueryFilters()
                .Include(c => c.Student)
                .Where(c => c.SchoolId == schoolId && cardUids.Contains(c.CardUid))
                .ToListAsync(ct))
            .GroupBy(c => c.CardUid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // The one refusal that has to happen before anything is written. Detected from the rows and the
        // cards alone, deliberately ahead of ResolveStudents: failing a row after its student has been
        // created leaves the half-imported state that CourseCollegeCollision's own pre-filter exists to
        // avoid — a student row with no card and no enrollment, from a row reported as Failed.
        var mismatched = FailRowsWhoseCardBelongsToAnotherStudent(live, students, cards, ledger);
        if (mismatched.Count > 0)
            live = live.Where(r => !mismatched.Contains(r.Staged.RowNumber)).ToList();
        if (live.Count == 0) return mismatched;

        var termRecords = await _db.StudentTermRecords.IgnoreQueryFilters()
            .Where(r => r.TermId == term.Id && r.Student!.SchoolId == schoolId)
            .ToDictionaryAsync(r => r.StudentId, ct);

        var offeringIds = dimensions.Offerings.Values.Select(o => o.Entity.Id).ToList();

        // Keyed by the natural key, valued by the row's *own* Id — not a HashSet of keys.
        //
        // A set answers "does this enrollment exist?", which is all the upsert needs, and that is how
        // the fan-out came to record `offering.Id` under EntityType = Enrollment on the Unchanged
        // branch: there was no enrollment id in scope to record. On a re-import — the steady state —
        // that is every enrollment touch pointing into the wrong table, so
        // IX_SisImportRowEntities_Entity answers "which rows touched enrollment X?" with nothing.
        var enrollments = (await _db.Enrollments.IgnoreQueryFilters()
                .Where(e => offeringIds.Contains(e.CourseOfferingId))
                .Select(e => new { e.Id, e.StudentId, e.CourseOfferingId })
                .ToListAsync(ct))
            .ToDictionary(e => (e.StudentId, e.CourseOfferingId), e => e.Id);

        var assignments = (await _db.CourseOfferingInstructors.IgnoreQueryFilters()
                .Where(a => offeringIds.Contains(a.CourseOfferingId))
                .Select(a => new { a.Id, a.CourseOfferingId, a.InstructorId })
                .ToListAsync(ct))
            .ToDictionary(a => (a.CourseOfferingId, a.InstructorId), a => a.Id);

        var resolvedStudents = ResolveStudents(schoolId, live, students, ledger);
        var resolvedCards = ResolveCards(schoolId, live, resolvedStudents, cards, ledger);
        var resolvedRecords = ResolveTermRecords(term, live, dimensions, resolvedStudents, termRecords);

        foreach (var row in live)
        {
            var rowNumber = row.Staged.RowNumber;
            var student = resolvedStudents[row.RegNo];
            var offering = dimensions.Offerings[(dimensions.Courses[row.CourseKey].Entity.Id, row.SectionKey)];

            ledger.Touch(row.Staged, SisImportEntityType.College,
                dimensions.Colleges[row.CollegeKey].Entity.Id,
                dimensions.Colleges[row.CollegeKey].ActionFor(rowNumber));
            ledger.Touch(row.Staged, SisImportEntityType.Program,
                dimensions.Programs[row.ProgramKey].Entity.Id,
                dimensions.Programs[row.ProgramKey].ActionFor(rowNumber));
            ledger.Touch(row.Staged, SisImportEntityType.Course,
                dimensions.Courses[row.CourseKey].Entity.Id,
                dimensions.Courses[row.CourseKey].ActionFor(rowNumber));
            ledger.Touch(row.Staged, SisImportEntityType.CourseOffering,
                offering.Entity.Id, offering.ActionFor(rowNumber));
            ledger.Touch(row.Staged, SisImportEntityType.Student,
                student.Entity.Id, student.ActionFor(rowNumber));

            // TryGetValue, not an indexer: a UID whose only card is deactivated resolves to no card at
            // all (SisImportWarningCode.RfidCardRevoked). Recording nothing is the honest trail — this
            // row touched no card, which is a different statement from touching one and changing
            // nothing, and is exactly the distinction the fan-out exists to keep.
            if (resolvedCards.TryGetValue(row.CardUid, out var card))
                ledger.Touch(row.Staged, SisImportEntityType.RfidCard,
                    card.Entity.Id, card.ActionFor(rowNumber));

            ledger.Touch(row.Staged, SisImportEntityType.StudentTermRecord,
                resolvedRecords[row.RegNo].Entity.Id, resolvedRecords[row.RegNo].ActionFor(rowNumber));

            // §4.12's own StudentId link, kept because it is the one entity a human looks for.
            row.Staged.StudentId = student.Entity.Id;

            // The teacher belongs to the *offering*, not to the enrollment — which is the whole reason
            // the 27 "duplicate" (REGNO, COURSE_CODE) pairs are not duplicates. Both rows of a pair
            // describe one enrollment; only the one naming a real teacher adds an assignment, and the
            // placeholder row correctly adds nothing.
            if (!row.Teacher.IsPlaceholder && row.TeacherKey is not null
                && dimensions.Instructors.TryGetValue(row.TeacherKey, out var instructor))
            {
                ledger.Touch(row.Staged, SisImportEntityType.Instructor,
                    instructor.Entity.Id, instructor.ActionFor(rowNumber));

                var assignmentKey = (offering.Entity.Id, instructor.Entity.Id);
                if (!assignments.TryGetValue(assignmentKey, out var assignmentId))
                {
                    var assignment = new CourseOfferingInstructor
                    {
                        CourseOfferingId = offering.Entity.Id,
                        InstructorId = instructor.Entity.Id,
                    };
                    _db.CourseOfferingInstructors.Add(assignment);

                    // Remembered so the *next* row naming this pair reports Unchanged against this same
                    // id rather than re-inserting it. Entity assigns its Id in the initializer, so the
                    // id is real before SaveChanges.
                    assignments[assignmentKey] = assignment.Id;
                    ledger.Touch(row.Staged, SisImportEntityType.CourseOfferingInstructor,
                        assignment.Id, SisImportEntityAction.Inserted);
                }
                else
                {
                    ledger.Touch(row.Staged, SisImportEntityType.CourseOfferingInstructor,
                        assignmentId, SisImportEntityAction.Unchanged);
                }
            }

            var enrollmentKey = (student.Entity.Id, offering.Entity.Id);
            if (!enrollments.TryGetValue(enrollmentKey, out var enrollmentId))
            {
                var enrollment = new Enrollment
                {
                    StudentId = student.Entity.Id,
                    CourseOfferingId = offering.Entity.Id,
                };
                _db.Enrollments.Add(enrollment);
                enrollments[enrollmentKey] = enrollment.Id;
                ledger.Touch(row.Staged, SisImportEntityType.Enrollment,
                    enrollment.Id, SisImportEntityAction.Inserted);
            }
            else
            {
                ledger.Touch(row.Staged, SisImportEntityType.Enrollment,
                    enrollmentId, SisImportEntityAction.Unchanged);
            }
        }

        return mismatched;
    }

    /// <summary>
    /// Fails every row whose REGNO normalizes to a card UID that already belongs to someone else, and
    /// returns their row numbers so the caller can drop them before a single fact is written.
    ///
    /// <para>
    /// <b>Two shapes, one rule.</b> The UID's owner is the student on its active card if there is one,
    /// and otherwise the first row in the file to claim it. Any row naming a different student fails.
    /// That covers both the case the reviewer found — an active card in the database owned by another
    /// student — and its twin, which has no card in the database at all: two REGNOs <em>inside one
    /// file</em> that differ only by case or punctuation are two students by
    /// <c>UNIQUE(SchoolId, StudentNumber)</c> and one card UID by <see cref="CardUid.Normalize"/>, so
    /// without this the second student would silently be handed the first one's card in the fan-out and
    /// receive none of their own.
    /// </para>
    ///
    /// <para>
    /// Students are compared by identity where one exists and by REGNO otherwise, which is exact:
    /// <c>StudentNumber</c> is unique per school, so two distinct REGNOs are two distinct students
    /// whether or not either has been created yet.
    /// </para>
    /// </summary>
    private static HashSet<int> FailRowsWhoseCardBelongsToAnotherStudent(
        IReadOnlyList<ParsedRow> live,
        IReadOnlyDictionary<string, Student> students,
        IReadOnlyDictionary<string, List<RfidCard>> cards,
        RowLedger ledger)
    {
        var mismatched = new HashSet<int>();

        foreach (var group in GroupRows(live, r => r.CardUid))
        {
            var uid = group.First.CardUid;
            var active = cards.TryGetValue(uid, out var forUid)
                ? forUid.FirstOrDefault(c => c.IsActive)
                : null;

            // Named for the message. The active card's own owner is the authority when there is one;
            // otherwise the file's first claimant is, and every later REGNO is the intruder.
            var ownerRegNo = active?.Student?.StudentNumber ?? group.First.RegNo;
            var ownerId = active?.StudentId;

            foreach (var row in group.All)
            {
                var claimant = students.TryGetValue(row.RegNo, out var existing) ? existing.Id : (Guid?)null;

                var isOwner = active is not null
                    ? claimant == ownerId
                    : string.Equals(row.RegNo, ownerRegNo, StringComparison.Ordinal);

                if (isOwner) continue;

                mismatched.Add(row.Staged.RowNumber);
                ledger.Fail(row.Staged, SisImportFailureCode.RfidCardStudentMismatch,
                    $"REGNO '{row.RegNo}' normalizes to card UID '{uid}', which already identifies " +
                    $"student '{ownerRegNo}'" +
                    (ownerId is null ? " (first claimant in this file)" : $" (StudentId {ownerId})") +
                    ". REGNO is both Students.StudentNumber — stored verbatim, unique per school — and " +
                    "RfidCards.CardUid, which is uppercased with punctuation stripped, so two student " +
                    "numbers differing only in case or punctuation are two students sharing one card. " +
                    "Moving the card would cost the first student their active card and re-attribute " +
                    "every past and future tap on that physical card to the wrong person; issuing a " +
                    "second active card would violate UX_RfidCards_SchoolId_CardUid_Active and fail " +
                    "the whole batch. The card was left exactly as it was. Correct the REGNO in the " +
                    "source — or merge the duplicate student in the back office — and re-import.");
            }
        }

        return mismatched;
    }

    private Dictionary<string, Touched<Student>> ResolveStudents(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed,
        Dictionary<string, Student> existing, RowLedger ledger)
    {
        var resolved = new Dictionary<string, Touched<Student>>(StringComparer.Ordinal);

        foreach (var group in GroupRows(parsed, r => r.RegNo))
        {
            var first = group.First;
            WarnOnIdentityConflicts(group, ledger);

            if (existing.TryGetValue(first.RegNo, out var student))
            {
                // Two rules, and which column gets which is a statement about what the file is
                // authoritative for. See Assign and AssignIfPresent.
                //
                // Assign — the file is the source of truth and a differing value replaces the stored
                // one. FirstName and LastName qualify because the parse refuses a row without them, so
                // a value here is always a real name and never an absent cell.
                var changed = Assign(student.FirstName, first.FirstName, v => student.FirstName = v);
                changed |= Assign(student.LastName, first.LastName, v => student.LastName = v);

                // AssignIfPresent — optional contact and display columns, where the roster is one
                // source among several. A blank MIDDLE NAME / USA_EMAIL / EMAIL_ID cell says "this
                // file does not carry that", not "clear what you have". Overwriting with null here
                // would report Updated for erasing a value a person typed in the back office, and the
                // whole stated point of a re-import is that it *updates*.
                changed |= AssignIfPresent(student.MiddleName, first.MiddleName, v => student.MiddleName = v);
                changed |= AssignIfPresent(student.Email, first.Email, v => student.Email = v);
                changed |= AssignIfPresent(
                    student.AlternateEmail, first.AlternateEmail, v => student.AlternateEmail = v);

                // Deliberately NOT written here: Course, YearLevel and Section. They are the ADR-001
                // D-2 derived cache and the SaveChanges guard refuses them outside the refresh scope —
                // this importer is precisely the writer that guard was aimed at. They are set in
                // RefreshStudentCacheAsync, from the enrollments this run just wrote.

                // Stamped on every run, including one that changes nothing, and deliberately outside
                // the `changed` flag so it neither bumps UpdatedAt nor counts as a row outcome. Same
                // reasoning as AcademicCacheUpdatedAt in RefreshStudentCacheAsync: "the SIS confirmed
                // this student on this date" has to be distinguishable from "never synced", and if the
                // stamp counted as work then a no-op import would report 52 updates and the counters
                // would stop meaning anything. The accepted cost is that a no-op run still issues one
                // UPDATE per student here and one in the cache refresh — real, bounded by the roster,
                // and the price of the column meaning what it says.
                student.LastSyncedAt = DateTime.UtcNow;

                if (changed) student.UpdatedAt = DateTime.UtcNow;
                resolved[first.RegNo] = new Touched<Student>(
                    student, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            student = new Student
            {
                SchoolId = schoolId,
                // Verbatim, both shapes. 51 REGNOs are USA##### and one is the legacy 2021005781;
                // neither is reformatted into the other's shape, here or anywhere downstream.
                StudentNumber = first.RegNo,
                FirstName = first.FirstName,
                MiddleName = first.MiddleName,
                LastName = first.LastName,
                Email = first.Email,
                AlternateEmail = first.AlternateEmail,
                Status = "Active",
                LastSyncedAt = DateTime.UtcNow,
            };
            _db.Students.Add(student);
            existing[first.RegNo] = student;
            resolved[first.RegNo] = new Touched<Student>(
                student, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    /// <summary>
    /// REGNO functionally determines the student's name and both e-mail addresses across all 536 sample
    /// rows, with zero violations — so the first row for a REGNO can be taken as that student's
    /// identity. This is what says so out loud if it ever stops being true, rather than the later rows
    /// being dropped without a word.
    /// </summary>
    private static void WarnOnIdentityConflicts(RowGroup group, RowLedger ledger)
    {
        var first = group.First;

        foreach (var row in group.All)
        {
            if (row.Staged.RowNumber == first.Staged.RowNumber) continue;

            var differences = new List<string>();
            if (!string.Equals(row.FirstName, first.FirstName, StringComparison.Ordinal))
                differences.Add($"first name '{row.FirstName}' vs '{first.FirstName}'");
            if (!string.Equals(row.LastName, first.LastName, StringComparison.Ordinal))
                differences.Add($"last name '{row.LastName}' vs '{first.LastName}'");
            if (!string.Equals(row.Email, first.Email, StringComparison.Ordinal))
                differences.Add($"institutional e-mail '{row.Email}' vs '{first.Email}'");

            if (differences.Count == 0) continue;

            ledger.Warn(row.Staged, SisImportWarningCode.StudentIdentityConflict,
                $"REGNO {row.RegNo} is described differently here than on row " +
                $"{first.Staged.RowNumber}: {string.Join("; ", differences)}. The first row's values " +
                "were kept.");
        }
    }

    /// <summary>
    /// Resolves each REGNO's card, and <b>never moves or resurrects one</b>.
    ///
    /// <para>
    /// <b>An active card whose student differs is refused, not reassigned.</b> That refusal happens
    /// upstream in <see cref="FailRowsWhoseCardBelongsToAnotherStudent"/>, before any student is
    /// created, so by the time a group reaches here its student is the card's owner. The check below is
    /// an assertion rather than a branch: reaching it means the pre-filter has a hole, and a loud stop
    /// beats reassigning a card and rewriting whose taps those were.
    /// </para>
    ///
    /// <para>
    /// <b>A card that exists but is deactivated is left deactivated</b>, and the row is warned rather
    /// than handed a new one — see <see cref="SisImportWarningCode.RfidCardRevoked"/>. This is why the
    /// prefetch loads inactive cards: filtered to <c>IsActive</c>, a card revoked without a replacement
    /// is invisible here and the next import creates a fresh active card with the same UID, which —
    /// since the UID is the REGNO — makes the revoked physical card work again.
    /// </para>
    /// </summary>
    private Dictionary<string, Touched<RfidCard>> ResolveCards(
        Guid schoolId, IReadOnlyList<ParsedRow> parsed,
        IReadOnlyDictionary<string, Touched<Student>> students,
        Dictionary<string, List<RfidCard>> existing, RowLedger ledger)
    {
        var resolved = new Dictionary<string, Touched<RfidCard>>(StringComparer.Ordinal);

        foreach (var group in GroupRows(parsed, r => r.CardUid))
        {
            var first = group.First;
            var studentId = students[first.RegNo].Entity.Id;
            existing.TryGetValue(first.CardUid, out var forUid);

            if (forUid?.FirstOrDefault(c => c.IsActive) is { } card)
            {
                if (card.StudentId != studentId)
                    throw new SisImportException(
                        $"Card UID '{first.CardUid}' belongs to student {card.StudentId} but row " +
                        $"{first.Staged.RowNumber} resolved it to {studentId}. This is refused before " +
                        "the fact pass runs (SisImportFailureCode.RfidCardStudentMismatch), so " +
                        "reaching this point means that pre-filter and this resolver disagree about " +
                        "who owns a UID. Stopping the run: the alternative is silently moving an " +
                        "active card between students, which re-attributes every tap on it.");

                resolved[first.CardUid] = new Touched<RfidCard>(
                    card, SisImportEntityAction.Unchanged, first.Staged.RowNumber);
                continue;
            }

            if (forUid is { Count: > 0 })
            {
                // Deactivated, and it stays that way. Reported against every row naming the UID, and
                // resolved to nothing at all — the fact loop records no RfidCard touch for these rows,
                // which is the truthful trail: this row touched no card.
                var revoked = forUid.OrderByDescending(c => c.DeactivatedAt ?? c.IssuedAt).First();
                foreach (var row in group.All)
                    ledger.Warn(row.Staged, SisImportWarningCode.RfidCardRevoked,
                        $"Card UID '{first.CardUid}' exists but is deactivated" +
                        (revoked.DeactivatedAt is { } at ? $" (since {at:yyyy-MM-dd})" : "") +
                        (revoked.StudentId == studentId
                            ? ", for this same student."
                            : $", for student {revoked.StudentId}.") +
                        " No card was created and the revoked one was left revoked. Because the UID is " +
                        "the REGNO, issuing a replacement here would make that physical card work " +
                        "again and silently undo whoever revoked it — lost, stolen or suspended is not " +
                        "something the roster has a column for. Re-issue it in the back office if that " +
                        "is what is wanted; the student imported normally and is enrolled either way.");

                continue;
            }

            var issued = new RfidCard
            {
                // ADR-001 D-3: denormalized from the student so the filtered unique index
                // (SchoolId, CardUid) WHERE IsActive = 1 can be a single index. Must equal the
                // student's own SchoolId, which it does by construction here.
                SchoolId = schoolId,
                StudentId = studentId,
                CardUid = first.CardUid,
                Label = "REGNO",
                IsActive = true,
                IssuedAt = DateTime.UtcNow,
            };
            _db.RfidCards.Add(issued);
            existing[first.CardUid] = [issued];
            resolved[first.CardUid] = new Touched<RfidCard>(
                issued, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    private Dictionary<string, Touched<StudentTermRecord>> ResolveTermRecords(
        Term term, IReadOnlyList<ParsedRow> parsed, Dimensions dimensions,
        IReadOnlyDictionary<string, Touched<Student>> students,
        Dictionary<Guid, StudentTermRecord> existing)
    {
        var resolved = new Dictionary<string, Touched<StudentTermRecord>>(StringComparer.Ordinal);
        var homeSections = HomeSections(parsed);

        foreach (var group in GroupRows(parsed, r => r.RegNo))
        {
            var first = group.First;
            var studentId = students[first.RegNo].Entity.Id;
            var (sectionKey, sectionName) = homeSections[first.RegNo];
            var collegeId = dimensions.Colleges[first.CollegeKey].Entity.Id;
            var programId = dimensions.Programs[first.ProgramKey].Entity.Id;

            if (existing.TryGetValue(studentId, out var record))
            {
                var changed = Assign(record.CollegeId, collegeId, v => record.CollegeId = v);
                changed |= Assign(record.ProgramId, programId, v => record.ProgramId = v);
                changed |= Assign(record.HomeSectionKey, sectionKey, v => record.HomeSectionKey = v);
                changed |= Assign(record.HomeSectionName, sectionName, v => record.HomeSectionName = v);
                // YearLevel is deliberately left alone: the export has no year-level column, and
                // deriving one from a section name ('BSFS 2-A' looks like year 2) would be a guess
                // stored as a fact. A null here means "not recorded", which is true.
                if (changed) record.UpdatedAt = DateTime.UtcNow;
                resolved[first.RegNo] = new Touched<StudentTermRecord>(
                    record, changed ? SisImportEntityAction.Updated : SisImportEntityAction.Unchanged,
                    first.Staged.RowNumber);
                continue;
            }

            record = new StudentTermRecord
            {
                StudentId = studentId,
                TermId = term.Id,
                CollegeId = collegeId,
                ProgramId = programId,
                HomeSectionKey = sectionKey,
                HomeSectionName = sectionName,
            };
            _db.StudentTermRecords.Add(record);
            existing[studentId] = record;
            resolved[first.RegNo] = new Touched<StudentTermRecord>(
                record, SisImportEntityAction.Inserted, first.Staged.RowNumber);
        }

        return resolved;
    }

    /// <summary>
    /// Each student's home section: the one they appear under most often in this file, ties broken by
    /// the lowest key ordinally.
    ///
    /// <para>
    /// <b>This answers ADR-001's open follow-up "define the which-enrollment-shows rule".</b> 12 of the
    /// 52 sample students sit in more than one section, so any single-valued home section is lossy by
    /// construction (D-2 says so). What matters is that the loss is <em>deterministic</em>: the mode is
    /// the student's actual cohort in every real case — a BSCRIM student taking one ROTC class is in
    /// BSCRIM — and the ordinal tie-break means two runs over the same file never disagree, which is
    /// what the idempotency contract needs. Blank sections are excluded from the vote; a student whose
    /// every row is blank gets <c>null</c>, meaning "not recorded", which is a different statement from
    /// <c>CourseOfferings.SectionKey</c>'s <c>(unspecified)</c>.
    /// </para>
    /// </summary>
    private static Dictionary<string, (string? Key, string? Name)> HomeSections(
        IReadOnlyList<ParsedRow> parsed) =>
        parsed
            .GroupBy(r => r.RegNo, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var named = g.Where(r => r.SectionKey != AcademicKey.Unspecified).ToList();
                    if (named.Count == 0) return ((string?)null, (string?)null);

                    var winner = named
                        .GroupBy(r => r.SectionKey, StringComparer.Ordinal)
                        .OrderByDescending(x => x.Count())
                        .ThenBy(x => x.Key, StringComparer.Ordinal)
                        .First();

                    return ((string?)winner.Key, winner.First().SectionName);
                },
                StringComparer.Ordinal);

    // =========================================================================== pass 3: derived

    /// <summary>
    /// Refreshes the ADR-001 D-2 display cache on <c>Students</c> — the one place in this codebase that
    /// writes those three columns, and the reason
    /// <see cref="EamsDbContext.BeginAcademicCacheRefresh"/> exists.
    ///
    /// <para>
    /// <b><c>YearLevel</c> is not written.</b> The export has no year-level column, so there is nothing
    /// to refresh it from; writing null would erase whatever a previous source put there, and deriving
    /// a value from the section name would be a guess stored where a fact is expected.
    /// </para>
    ///
    /// <para>
    /// <b><c>AcademicCacheUpdatedAt</c> is stamped on every run, including one that changes nothing,</b>
    /// and it deliberately does not bump <c>UpdatedAt</c> or count as a row outcome. Same reasoning as
    /// the projection's <c>LastSyncedAt</c>: "refreshed, unchanged" has to be distinguishable from
    /// "never refreshed", and if the stamp counted as work then a no-op import would report 52 updates
    /// and the counters would stop meaning anything.
    /// </para>
    /// </summary>
    private async Task RefreshStudentCacheAsync(
        Term term, IReadOnlyList<ParsedRow> parsed, Dimensions dimensions,
        IReadOnlySet<int> mismatchedRows, CancellationToken ct)
    {
        // Both exclusions, for one reason: a row reported Failed must not have left a trace anywhere,
        // and a cached Section derived from a row that did not import is exactly such a trace.
        var live = parsed
            .Where(r => !dimensions.CollidedRows.Contains(r.Staged.RowNumber)
                        && !mismatchedRows.Contains(r.Staged.RowNumber))
            .ToList();
        if (live.Count == 0) return;

        var homeSections = HomeSections(live);
        var regNos = live.Select(r => r.RegNo).Distinct(StringComparer.Ordinal).ToList();
        var programByRegNo = live
            .GroupBy(r => r.RegNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ProgramCode, StringComparer.Ordinal);

        var students = await _db.Students.IgnoreQueryFilters()
            .Where(s => s.SchoolId == term.SchoolId && regNos.Contains(s.StudentNumber))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        using (_db.BeginAcademicCacheRefresh())
        {
            foreach (var student in students)
            {
                if (programByRegNo.TryGetValue(student.StudentNumber, out var programCode))
                    student.Course = programCode;

                if (homeSections.TryGetValue(student.StudentNumber, out var section))
                    student.Section = section.Name ?? section.Key;

                student.AcademicCacheUpdatedAt = now;
            }

            // Inside the scope: the guard runs in SaveChanges, so a save after the scope closes is
            // exactly the write it is built to refuse.
            await _db.SaveChangesAsync(ct);
        }
    }

    // ==================================================================================== counters

    private static void Tally(SisImportBatch batch, IReadOnlyList<SisImportRow> staged)
    {
        batch.TotalRows = staged.Count;
        batch.InsertedRows = staged.Count(r => r.Result == SisImportRowResult.Inserted);
        batch.UpdatedRows = staged.Count(r => r.Result == SisImportRowResult.Updated);
        batch.FailedRows = staged.Count(r => r.Result == SisImportRowResult.Failed);
        batch.SkippedRows = staged.Count(r => r.Result == SisImportRowResult.Skipped);
        batch.WarningRows = staged.Count(r => r.WarningCode is not null);

        batch.Status = batch.FailedRows > 0
            ? SisImportStatus.CompletedWithErrors
            : batch.WarningRows > 0
                ? SisImportStatus.CompletedWithWarnings
                : SisImportStatus.Completed;
    }

    // ======================================================================================= reads

    public async Task<SisImportBatchDto?> GetAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await _db.SisImportBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return null;

        var term = await _db.Terms.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == batch.TermId, ct);

        return ToDto(batch, term);
    }

    public async Task<IReadOnlyList<SisImportRowDto>> GetRowsAsync(
        Guid batchId, string? result, CancellationToken ct = default)
    {
        var query = _db.SisImportRows.AsNoTracking()
            .Include(r => r.Entities)
            .Where(r => r.BatchId == batchId);

        if (!string.IsNullOrWhiteSpace(result))
        {
            // An unrecognised filter matches nothing rather than everything. "?result=failure" silently
            // returning all 536 rows is how an operator concludes a clean import failed completely.
            if (!SisImportRowResult.TryNormalize(result, out var canonical)) return [];
            query = query.Where(r => r.Result == canonical);
        }

        var rows = await query.OrderBy(r => r.RowNumber).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    // ======================================================================================= plumbing

    private async Task<Term> LoadTermAsync(Guid termId, CancellationToken ct) =>
        await _db.Terms.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == termId, ct)
        ?? throw new SisImportException(
            $"No term {termId}. The term is required and operator-declared (ADR-001 D-5): it is not " +
            "in the file and must not be inferred from the filename or the upload date.");

    private async Task<SisImportPreviewDto> BuildPreviewAsync(
        SisImportBatch batch, Term term, ExcelRosterReader.RosterFile file, CancellationToken ct)
    {
        var sample = await _db.SisImportRows.AsNoTracking()
            .Include(r => r.Entities)
            .Where(r => r.BatchId == batch.Id)
            .OrderBy(r => r.RowNumber)
            .Take(PreviewSampleSize)
            .ToListAsync(ct);

        int Distinct(string column) => file.Rows
            .Select(r => AcademicKey.Normalize(r.Raw(column)))
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new SisImportPreviewDto(
            Batch: ToDto(batch, term),
            Columns: file.Columns.Where(c => c.Length > 0).ToList(),
            DistinctStudents: Distinct(SisRosterColumns.RegNo),
            DistinctColleges: Distinct(SisRosterColumns.CollegeName),
            DistinctPrograms: Distinct(SisRosterColumns.Program),
            DistinctCourses: Distinct(SisRosterColumns.CourseCode),
            DistinctSections: Distinct(SisRosterColumns.SectionName),
            // The placeholder is excluded: reporting 19 teachers when one of them is 'TO BE ANNOUNCE'
            // is the same lie the pipeline refuses to write into the Instructors table.
            DistinctInstructors: file.Rows
                .Select(r => AcademicKey.Normalize(r.Raw(SisRosterColumns.TeacherFullName)))
                .Where(v => v.Length > 0 && v != TeacherNames.PlaceholderKey)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            BlankSectionRows: file.Rows.Count(r =>
                AcademicKey.Normalize(r.Raw(SisRosterColumns.SectionName)).Length == 0),
            PlaceholderInstructorRows: file.Rows.Count(r =>
                TeacherNames.IsPlaceholder(r.Raw(SisRosterColumns.TeacherFullName))),
            SampleRows: sample.Select(ToDto).ToList());
    }

    /// <summary>
    /// Ensures the built-in ADR-001 D-4 profile exists for this school and returns it. Idempotent: the
    /// version is part of the natural key, so a second call finds the row rather than creating a
    /// version 2 that differs from version 1 in nothing.
    /// </summary>
    private async Task<SisImportProfile> EnsureBuiltInProfileAsync(Guid schoolId, CancellationToken ct)
    {
        var nameKey = AcademicKey.NormalizeOrUnspecified(SisImportProfileTemplate.ProfileName);

        var existing = await _db.SisImportProfiles.IgnoreQueryFilters()
            .Where(p => p.SchoolId == schoolId && p.NameKey == nameKey && p.IsActive)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

        if (existing is not null) return existing;

        var profile = new SisImportProfile
        {
            SchoolId = schoolId,
            Name = SisImportProfileTemplate.ProfileName,
            NameKey = nameKey,
            Version = SisImportProfileTemplate.BuiltInVersion,
            Source = SisImportSource.Excel,
            IsActive = true,
            Description = SisImportProfileTemplate.Description,
        };
        _db.SisImportProfiles.Add(profile);

        for (var i = 0; i < SisImportProfileTemplate.Entries.Count; i++)
        {
            var entry = SisImportProfileTemplate.Entries[i];
            _db.SisImportProfileColumns.Add(new SisImportProfileColumn
            {
                Profile = profile,
                SourceColumn = entry.SourceColumn,
                SourceColumnKey = SisRosterColumns.HeaderKey(entry.SourceColumn),
                TargetField = entry.TargetField,
                NormalizationRule = entry.NormalizationRule,
                IsRequired = entry.IsRequired,
                Ordinal = i,
            });
        }

        return profile;
    }

    private static SisImportBatchDto ToDto(SisImportBatch batch, Term term) => new(
        batch.Id, batch.TermId, term.Code, batch.Source, batch.FileName, batch.SourceSheetName,
        batch.FileHash, batch.Status, batch.TotalRows, batch.InsertedRows, batch.UpdatedRows,
        batch.FailedRows, batch.SkippedRows, batch.WarningRows, batch.StartedAt, batch.FinishedAt);

    private static SisImportRowDto ToDto(SisImportRow row) => new(
        row.Id, row.RowNumber, row.Result, row.SkipReason, row.WarningCode, row.WarningMessage,
        row.ErrorMessage, row.StudentId, row.RawData,
        row.Entities
            .OrderBy(e => e.EntityType, StringComparer.Ordinal)
            .Select(e => new SisImportRowEntityDto(e.EntityType, e.EntityId, e.Action))
            .ToList());

    // ---------------------------------------------------------------------------- small helpers

    /// <summary>
    /// Assigns only when the value actually differs, and reports whether it did.
    ///
    /// <para>
    /// This is what makes <c>Updated</c> mean something. Assigning unconditionally would mark every
    /// entity modified on every run — EF's change tracker compares values, so it would not issue the
    /// UPDATE, but this pipeline's own counters would report one, and the headline "a second import
    /// changes nothing" assertion would be false while the database was in fact untouched.
    /// </para>
    /// </summary>
    private static bool Assign<T>(T current, T value, Action<T> set)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        set(value);
        return true;
    }

    /// <summary>
    /// <see cref="Assign"/> for a column the file is <em>not</em> authoritative for: a null source
    /// value means "this file does not say", never "clear what is stored".
    ///
    /// <para>
    /// <b>Why the two rules are not one.</b> <see cref="RosterText.Clean"/> returns <c>null</c> for a
    /// blank cell, so plain <see cref="Assign"/> turns an absent column into a <c>NULL</c> written over
    /// a populated one — and reports it as <c>Updated</c>, which is a replace-with-nothing driven by
    /// the absence of data. On the optional contact and display columns that collides head-on with the
    /// stated contract that a re-import <em>updates</em>: a student whose e-mail was typed in by hand,
    /// or a section display name a later export stopped carrying, would be erased by the next run of a
    /// file that simply never had that column filled in.
    /// </para>
    ///
    /// <para>
    /// It is deliberately <em>not</em> the default. Columns the roster owns outright — a corrected
    /// surname, a course's college, a programme's code — must still be able to change, and a required
    /// column's value is never null anyway because the parse fails the row first. The rule is therefore
    /// per column and stated at each call site, not inferred from nullability.
    /// </para>
    /// </summary>
    private static bool AssignIfPresent<T>(T? current, T? value, Action<T?> set) where T : class
    {
        if (value is null) return false;
        return Assign(current, value, set);
    }

    /// <summary>One distinct key's rows, and the first of them in worksheet order.</summary>
    private sealed record RowGroup(ParsedRow First, IReadOnlyList<ParsedRow> All);

    /// <summary>
    /// Groups rows by a key, preserving worksheet order so "first seen" is deterministic. The whole of
    /// pass 1 is built on this: 536 rows in, one group per college, programme, course, teacher and
    /// offering out, and the fact loop never has to decide whether a dimension exists.
    /// </summary>
    private static IEnumerable<RowGroup> GroupRows<TKey>(
        IReadOnlyList<ParsedRow> rows, Func<ParsedRow, TKey> key) where TKey : notnull =>
        rows.GroupBy(key)
            .Select(g => new RowGroup(g.First(), g.ToList()));
}
