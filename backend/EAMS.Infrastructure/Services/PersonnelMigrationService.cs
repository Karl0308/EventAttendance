using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// See <see cref="IPersonnelMigrationService"/> (JJ, 2026-11-25). Built from the pieces every other card
/// and personnel write already uses, so a moved student obeys the same rules as one typed in:
/// <see cref="PersonnelService.Normalize"/> for the personnel columns, the
/// <c>DELETE /students/{id}/cards/{cardId}</c> deactivation for the card, <c>StudentService.DeleteAsync</c>'s
/// soft delete for the student, and <see cref="CardUidClaims"/> (ADR-015 D-98) around all of it.
///
/// <para>
/// <b>Plan and run share one classifier.</b> A dry run loads a chunk and classifies it; a run does the same
/// inside a transaction, after taking the chunk's UID locks, and then applies what it classified. So a
/// preview cannot promise something the run would refuse, short of a concurrent write in between — which
/// the run re-checks under the lock.
/// </para>
///
/// <para>
/// <b>Past attendance is not touched.</b> Attendance rows keep their <c>StudentId</c>; a soft-deleted
/// student still resolves for them, and every linked event is Closed or Cancelled (the classifier refuses
/// any other), so those rows are frozen anyway.
/// </para>
/// </summary>
internal sealed class PersonnelMigrationService : IPersonnelMigrationService
{
    /// <summary>
    /// Students per transaction. Bounds the lock count, the IN lists and the work a fault throws away; a
    /// stopped run resumes from where it stopped because moved students no longer match.
    /// </summary>
    internal const int StudentsPerTransaction = 200;

    private readonly EamsDbContext _db;
    private readonly ISchoolContext _school;
    private readonly ICardUidWriteBarrier _cardUidBarrier;
    private readonly ILogger<PersonnelMigrationService> _logger;

    public PersonnelMigrationService(
        EamsDbContext db, ISchoolContext school, ICardUidWriteBarrier cardUidBarrier,
        ILogger<PersonnelMigrationService> logger)
    {
        _db = db;
        _school = school;
        _cardUidBarrier = cardUidBarrier;
        _logger = logger;
    }

    public async Task<PersonnelMigrationResponse> MigrateFromStudentsAsync(
        MigrateFromStudentsRequest request, CancellationToken ct = default)
    {
        var prefix = string.IsNullOrWhiteSpace(request.Prefix)
            ? MigrateFromStudentsRequest.DefaultPrefix
            : request.Prefix.Trim();

        if (prefix.Length < MigrateFromStudentsRequest.MinPrefixLength)
            return Refused($"The prefix must be at least {MigrateFromStudentsRequest.MinPrefixLength} characters; " +
                           "a shorter one would sweep most of the roster into Personnel.");
        if (prefix.Length > PersonnelText.NumberMaxLength)
            return Refused($"The prefix is longer than {PersonnelText.NumberMaxLength} characters, so no personnel ID could match it.");

        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null)
            return new PersonnelMigrationResponse(PersonnelWriteOutcome.NoSchoolResolved,
                "No school could be resolved, so there is no roster to move students from.", null);

        // Trimmed, as JJ ruled: a number imported with stray spaces still matches. A soft-deleted student
        // is out, which is what makes a re-run a no-op for everyone already moved.
        var candidateIds = await _db.Students.AsNoTracking()
            .Where(s => s.SchoolId == schoolId && !s.IsDeleted && s.StudentNumber.Trim().StartsWith(prefix))
            .OrderBy(s => s.StudentNumber).ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var rows = new List<MigrateFromStudentsRowDto>(candidateIds.Count);
        foreach (var chunk in candidateIds.Chunk(StudentsPerTransaction))
        {
            rows.AddRange(request.DryRun
                ? await PlanChunkAsync(schoolId.Value, chunk, ct)
                : await RunChunkAsync(schoolId.Value, chunk, ct));
        }

        var result = new MigrateFromStudentsResultDto(
            request.DryRun, prefix, rows.Count,
            rows.Count(r => r.Outcome == MigrateFromStudentsOutcome.Create),
            rows.Count(r => r.Outcome == MigrateFromStudentsOutcome.Update),
            rows.Count(r => r.Outcome == MigrateFromStudentsOutcome.Conflict),
            rows);

        if (!request.DryRun)
        {
            _logger.LogInformation(
                "Moved students with prefix '{Prefix}' to Personnel: {Matched} matched, {Created} created, " +
                "{Updated} updated, {Conflicts} left as conflicts.",
                prefix, result.Matched, result.Created, result.Updated, result.Conflicts);
        }

        return new PersonnelMigrationResponse(PersonnelWriteOutcome.Saved,
            request.DryRun ? "Preview only; nothing was written." : "Migration applied.", result);
    }

    // ------------------------------------------------------------------------------------ dry run

    private async Task<IReadOnlyList<MigrateFromStudentsRowDto>> PlanChunkAsync(
        Guid schoolId, Guid[] studentIds, CancellationToken ct)
    {
        try
        {
            var snapshot = await LoadAsync(schoolId, studentIds, ct);
            // A preview names no id for a record it has not created.
            return ClassifyAll(snapshot, lockedUids: null)
                .Select(d => d.Action == MigrateFromStudentsOutcome.Create ? d.Row with { PersonnelId = null } : d.Row)
                .ToList();
        }
        finally
        {
            // The snapshot is loaded tracked so the run and the preview read identically; a preview must
            // not leave those entities behind for a later save on this context.
            _db.ChangeTracker.Clear();
        }
    }

    // ---------------------------------------------------------------------------------------- run

    private async Task<IReadOnlyList<MigrateFromStudentsRowDto>> RunChunkAsync(
        Guid schoolId, Guid[] studentIds, CancellationToken ct)
    {
        var rows = new List<MigrateFromStudentsRowDto>();

        try
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                // A replay starts from the database, not from what a failed attempt left tracked.
                _db.ChangeTracker.Clear();
                rows.Clear();

                await using var transaction = await _db.Database.BeginTransactionAsync(ct);

                // D-98: every UID this chunk will release and re-claim is locked before the first write, in
                // the order LockAsync imposes. Read before the lock, so the classifier re-checks under it
                // that each student's active card is still one of the locked UIDs.
                var rawUids = await _db.RfidCards.AsNoTracking()
                    .Where(c => c.SchoolId == schoolId && c.IsActive && studentIds.Contains(c.StudentId))
                    .Select(c => c.CardUid)
                    .ToListAsync(ct);
                var lockedUids = rawUids.Select(CardUid.Normalize).Where(u => u.Length > 0)
                    .ToHashSet(StringComparer.Ordinal);

                await CardUidClaims.LockAsync(_db, schoolId, lockedUids, ct);

                var snapshot = await LoadAsync(schoolId, studentIds, ct);
                var decisions = ClassifyAll(snapshot, lockedUids);

                await _cardUidBarrier.BetweenCheckAndWriteAsync(ct);

                var now = DateTime.UtcNow;
                foreach (var decision in decisions)
                {
                    Apply(decision, schoolId, now);
                    rows.Add(decision.Row);
                }

                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            });
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // A personnel number or UID was claimed by a concurrent write between the re-check and the
            // save. The transaction rolled back, so nothing in this chunk moved; a re-run reclassifies it.
            _logger.LogWarning(ex,
                "A chunk of {Count} students lost a uniqueness race while moving to Personnel; it was rolled back.",
                studentIds.Length);
            // A row that was already a conflict keeps its own reason; only the rows that would have moved
            // are rewritten, since those are the ones the rollback undid.
            return rows.Select(r => r.Outcome == MigrateFromStudentsOutcome.Conflict ? r : r with
            {
                Outcome = MigrateFromStudentsOutcome.Conflict,
                ConflictCode = MigrateFromStudentsConflict.ConcurrentChange,
                Message = "Another change claimed this personnel ID or card while the migration ran. " +
                          "Nothing in this batch was written; run the migration again.",
                PersonnelId = null,
            }).ToList();
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }

        return rows;
    }

    private void Apply(Decision decision, Guid schoolId, DateTime now)
    {
        if (decision.Action == MigrateFromStudentsOutcome.Conflict) return;

        // The DELETE /students/{id}/cards/{cardId} deactivation: the row is kept for the issuance history
        // and any past tap that names it (ADR-001 D-3); only its claim on the UID ends.
        if (decision.Card is { } card)
        {
            card.IsActive = false;
            card.DeactivatedAt = now;
            card.UpdatedAt = now;
        }

        if (decision.Action == MigrateFromStudentsOutcome.Create)
        {
            var fields = decision.Fields!;
            _db.Personnel.Add(new Personnel
            {
                Id = decision.Row.PersonnelId!.Value,
                SchoolId = schoolId,
                PersonnelNumber = fields.Number,
                FirstName = fields.FirstName,
                MiddleName = fields.MiddleName,
                LastName = fields.LastName,
                Email = fields.Email,
                Department = fields.Department,
                Organization = fields.Organization,
                RfidUid = fields.RfidUid,
                Status = PersonnelStatus.Active,
            });
        }
        else if (decision.Existing is { } existing && existing.RfidUid is null && decision.Row.RfidUid is { } uid)
        {
            // JJ: an existing record keeps its own values; the move only fills the empty card.
            existing.RfidUid = uid;
            existing.UpdatedAt = now;
        }

        // StudentService.DeleteAsync's soft delete.
        decision.Student.IsDeleted = true;
        decision.Student.UpdatedAt = now;
    }

    // ------------------------------------------------------------------------------- the snapshot

    private sealed record Snapshot(
        IReadOnlyList<Student> Students,
        ILookup<Guid, RfidCard> ActiveCards,
        IReadOnlyDictionary<string, Personnel> PersonnelByNumber,
        IReadOnlyDictionary<string, Personnel> PersonnelByUid,
        IReadOnlySet<Guid> InUnfrozenEvent,
        IReadOnlyDictionary<(Guid SchoolId, string Course), string> Departments);

    /// <summary>
    /// Everything the classifier needs for one chunk, set-based — a fixed handful of statements per chunk,
    /// never one per student. Tracked, so a run can write what it read.
    /// </summary>
    private async Task<Snapshot> LoadAsync(Guid schoolId, Guid[] studentIds, CancellationToken ct)
    {
        // Bounded by StudentsPerTransaction, so these id lists are well inside one planned IN list.
        var students = await _db.Students
            .Where(s => s.SchoolId == schoolId && !s.IsDeleted && studentIds.Contains(s.Id))
            .OrderBy(s => s.StudentNumber).ThenBy(s => s.Id)
            .ToListAsync(ct);
        var ids = students.Select(s => s.Id).ToList();

        var cards = await _db.RfidCards
            .Where(c => c.SchoolId == schoolId && c.IsActive && ids.Contains(c.StudentId))
            .ToListAsync(ct);

        var numbers = students.Select(s => s.StudentNumber.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var uids = cards.Select(c => CardUid.Normalize(c.CardUid)).Where(u => u.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();

        // InListChunks for the string keys (CLAUDE.md / compatibility 110). Tracked entities, so the
        // helper's de-duplication is by identity and the two lookups share instances.
        var byNumber = await InListChunks.ToListInChunksAsync(numbers, chunk => _db.Personnel
            .Where(p => p.SchoolId == schoolId && !p.IsDeleted && chunk.Contains(p.PersonnelNumber)), ct);
        var byUid = await InListChunks.ToListInChunksAsync(uids, chunk => _db.Personnel
            .Where(p => p.SchoolId == schoolId && !p.IsDeleted && p.RfidUid != null && chunk.Contains(p.RfidUid)), ct);

        // "Only closed events are safe" (JJ): any attendance, audience (direct, section or definition) or
        // pre-registration on an event that is not Closed or Cancelled holds the student back.
        var inAttendance = await _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.StudentId != null && ids.Contains(a.StudentId.Value)
                        && !a.Event!.IsDeleted
                        && a.Event.Status != EventStatus.Closed && a.Event.Status != EventStatus.Cancelled)
            .Select(a => a.StudentId!.Value).Distinct().ToListAsync(ct);
        var inAudience = await _db.EventGroups.AsNoTracking()
            .Where(g => g.StudentId != null && ids.Contains(g.StudentId.Value)
                        && !g.Event!.IsDeleted
                        && g.Event.Status != EventStatus.Closed && g.Event.Status != EventStatus.Cancelled)
            .Select(g => g.StudentId!.Value).Distinct().ToListAsync(ct);
        var inPreRegistration = await _db.PreRegistrations.AsNoTracking()
            .Where(r => r.StudentId != null && ids.Contains(r.StudentId.Value)
                        && r.Session!.Event != null && !r.Session.Event.IsDeleted
                        && r.Session.Event.Status != EventStatus.Closed
                        && r.Session.Event.Status != EventStatus.Cancelled)
            .Select(r => r.StudentId!.Value).Distinct().ToListAsync(ct);

        // Section audiences: an attached group reaches the student through its current membership, which is
        // how a non-terminal event's expected roster reads it (EventService.GroupMemberStudentIds).
        var inGroupAudience = await (
                from g in _db.EventGroups.AsNoTracking()
                where g.StudentGroupId != null && !g.Event!.IsDeleted
                      && g.Event.Status != EventStatus.Closed && g.Event.Status != EventStatus.Cancelled
                join m in _db.StudentGroupMembers.AsNoTracking() on g.StudentGroupId equals (Guid?)m.StudentGroupId
                where ids.Contains(m.StudentId)
                select m.StudentId)
            .Distinct().ToListAsync(ct);

        var inDefinitionAudience = await DefinitionAudienceStudentIdsAsync(schoolId, ids, ct);

        var departments = await StudentService.DepartmentsAsync(
            _db, students.Select(s => (s.SchoolId, s.Course)), ct);

        // Case-insensitive, like the column collation both personnel indexes compare under.
        var personnelByNumber = new Dictionary<string, Personnel>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in byNumber) personnelByNumber.TryAdd(p.PersonnelNumber, p);
        var personnelByUid = new Dictionary<string, Personnel>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in byUid) personnelByUid.TryAdd(p.RfidUid!, p);

        return new Snapshot(
            students,
            cards.ToLookup(c => c.StudentId),
            personnelByNumber,
            personnelByUid,
            inAttendance.Concat(inAudience).Concat(inPreRegistration)
                .Concat(inGroupAudience).Concat(inDefinitionAudience).ToHashSet(),
            departments);
    }

    /// <summary>
    /// The chunk's students that an audience definition attached to a non-terminal event resolves to —
    /// resolved, not guessed: each distinct definition's criteria go through the one
    /// <see cref="AudienceResolution"/> (ADR-003 D-19), narrowed to the chunk, and UNIONed into a single
    /// statement. Bounded by the number of definitions on open/draft events, never by the student count.
    /// </summary>
    private async Task<List<Guid>> DefinitionAudienceStudentIdsAsync(
        Guid schoolId, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var definitions = await _db.EventAudienceDefinitions.AsNoTracking()
            .Where(link => link.AudienceDefinition!.SchoolId == schoolId && !link.Event!.IsDeleted
                           && link.Event.Status != EventStatus.Closed && link.Event.Status != EventStatus.Cancelled)
            .Select(link => new
            {
                link.AudienceDefinitionId, link.AudienceDefinition!.AudienceType, link.AudienceDefinition.CriteriaJson,
            })
            .Distinct()
            .ToListAsync(ct);

        IQueryable<Guid>? union = null;
        foreach (var definition in definitions)
        {
            var criteria = AudienceResolution.ParseCriteria(definition.CriteriaJson);
            var (students, _) = AudienceResolution.BuildQueries(
                _db, definition.AudienceType, criteria, includeDeleted: false);
            if (students is null) continue;

            var matched = students.Where(s => s.SchoolId == schoolId && ids.Contains(s.Id)).Select(s => s.Id);
            union = union is null ? matched : union.Union(matched);
        }

        return union is null ? [] : await union.ToListAsync(ct);
    }

    // ----------------------------------------------------------------------------- the classifier

    private sealed record Decision(
        MigrateFromStudentsRowDto Row,
        string Action,
        Student Student,
        RfidCard? Card,
        Personnel? Existing,
        PersonnelService.Fields? Fields);

    private static List<Decision> ClassifyAll(Snapshot snapshot, IReadOnlySet<string>? lockedUids)
    {
        // Two students whose numbers trim to the same value would both create one personnel ID; the second
        // is held back so the first can land, and a re-run then finds it as an existing record.
        var creating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The same for an EXISTING record two students trim onto: the classifier reads the record's
        // RfidUid as it was before the batch, so both would plan to fill it, the second card would be
        // deactivated and its UID never stored. The first one in wins; a later one with a card is held back.
        var targeted = new HashSet<Guid>();
        var decisions = new List<Decision>(snapshot.Students.Count);

        foreach (var student in snapshot.Students)
        {
            var decision = Classify(student, snapshot, lockedUids, creating, targeted);
            if (decision.Action == MigrateFromStudentsOutcome.Create) creating.Add(decision.Fields!.Number);
            if (decision.Action == MigrateFromStudentsOutcome.Update) targeted.Add(decision.Existing!.Id);
            decisions.Add(decision);
        }

        return decisions;
    }

    private static Decision Classify(
        Student student, Snapshot snapshot, IReadOnlySet<string>? lockedUids, IReadOnlySet<string> creating,
        IReadOnlySet<Guid> targeted)
    {
        var number = student.StudentNumber.Trim();
        var cards = snapshot.ActiveCards[student.Id].ToList();

        Decision Conflict(string code, string message, string? uid = null) => new(
            new MigrateFromStudentsRowDto(student.Id, student.StudentNumber, student.FullName, uid,
                MigrateFromStudentsOutcome.Conflict, code, message, null),
            MigrateFromStudentsOutcome.Conflict, student, null, null, null);

        if (cards.Count > 1)
            return Conflict(MigrateFromStudentsConflict.MultipleActiveCards,
                $"The student holds {cards.Count} active cards and a personnel record carries one. " +
                "Deactivate the extra cards on the Students tab, then run again.");

        var card = cards.SingleOrDefault();
        var uid = card is null ? null : CardUid.Normalize(card.CardUid);
        if (uid is { Length: 0 }) uid = null;

        // Under the lock, a card that is not among the locked UIDs was issued after they were read.
        if (lockedUids is not null && uid is not null && !lockedUids.Contains(uid))
            return Conflict(MigrateFromStudentsConflict.ConcurrentChange,
                "The student's card changed while the migration ran. Nothing was written; run again.", uid);

        if (snapshot.InUnfrozenEvent.Contains(student.Id))
            return Conflict(MigrateFromStudentsConflict.ActiveEvent,
                "The student is linked to an event that is not Closed or Cancelled. Only students whose " +
                "events are all closed can be moved; close the event first.", uid);

        snapshot.PersonnelByNumber.TryGetValue(number, out var existing);

        if (uid is not null && snapshot.PersonnelByUid.TryGetValue(uid, out var holder) && holder.Id != existing?.Id)
            return Conflict(MigrateFromStudentsConflict.UidHeldByOtherPersonnel,
                $"Card UID '{uid}' is already held by personnel record '{holder.PersonnelNumber}'. " +
                "One UID identifies one person (ADR-015 D-98); resolve that record first.", uid);

        if (existing is not null)
        {
            if (uid is not null && targeted.Contains(existing.Id))
                return Conflict(MigrateFromStudentsConflict.ConcurrentChange,
                    $"Another student in this batch also trims to personnel ID '{existing.PersonnelNumber}' and " +
                    "is moved onto that record first. Nothing was written for this one; resolve which card is " +
                    "right, then run again.", uid);

            if (uid is not null && existing.RfidUid is not null
                && !string.Equals(existing.RfidUid, uid, StringComparison.OrdinalIgnoreCase))
                return Conflict(MigrateFromStudentsConflict.PersonnelHasDifferentCard,
                    $"Personnel record '{existing.PersonnelNumber}' already holds card UID '{existing.RfidUid}', " +
                    $"not this student's '{uid}'. Its card is kept; resolve which card is right first.", uid);

            var message = uid is null
                ? "Personnel record exists and is kept as it is; the student has no active card to move."
                : existing.RfidUid is null
                    ? "Personnel record exists and is kept as it is; the student's card UID is filled in."
                    : "Personnel record exists and already holds this card UID; it is kept as it is.";

            return new Decision(
                new MigrateFromStudentsRowDto(student.Id, student.StudentNumber, student.FullName, uid,
                    MigrateFromStudentsOutcome.Update, null, message, existing.Id),
                MigrateFromStudentsOutcome.Update, student, card, existing, null);
        }

        if (string.IsNullOrWhiteSpace(student.FirstName) || string.IsNullOrWhiteSpace(student.LastName))
            return Conflict(MigrateFromStudentsConflict.NameMissing,
                "A personnel record needs a first and a last name; the student is missing one.", uid);

        var department = StudentService.DepartmentOf(snapshot.Departments, student.SchoolId, student.Course);
        var request = new PersonnelWriteRequest(
            number, student.FirstName, student.MiddleName, student.LastName, student.Email,
            Classification: null, department, student.Organization, Position: null, uid, PersonnelStatus.Active);

        if (!PersonnelService.Normalize(request, out var fields, out var refused))
            return Conflict(MigrateFromStudentsConflict.Invalid, refused!.Message, uid);

        if (creating.Contains(fields.Number))
            return Conflict(MigrateFromStudentsConflict.ConcurrentChange,
                $"Another student in this batch also trims to personnel ID '{fields.Number}'; it is moved " +
                "first. Run again to fold this one into that record.", uid);

        // Minted here so the run can report the id it is about to insert.
        var personnelId = Guid.NewGuid();
        return new Decision(
            new MigrateFromStudentsRowDto(student.Id, student.StudentNumber, student.FullName, uid,
                MigrateFromStudentsOutcome.Create, null,
                uid is null ? "A personnel record is created; the student has no active card to move."
                            : "A personnel record is created with the student's card UID.",
                personnelId),
            MigrateFromStudentsOutcome.Create, student, card, null, fields);
    }

    private static PersonnelMigrationResponse Refused(string message) =>
        new(PersonnelWriteOutcome.ValidationFailed, message, null);
}
