using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// Who is classified as what — see <see cref="IStudentClassificationService"/> for why this is its own
/// service and what Phase 1b's importer reuses from it.
///
/// <para>
/// <b>The one idea this class is built around: the axis is a slot, and every operation here is about a
/// slot rather than about a row.</b> <c>UX_StudentClassifications_Student_Axis</c> caps a person at one
/// classification per axis, so "assign" means "put this in its axis's slot, displacing whatever was
/// there" and never "insert another row". Written the other way round — insert, catch the duplicate,
/// decide what to do — the replacement case would have been a recovery path rather than the main one,
/// and the main one is what an edit form does every time it saves.
/// </para>
///
/// <para>
/// <b>It takes no <c>ISchoolContext</c>, unlike the write services around it.</b> Those create rows and
/// have to decide which school to file them under; this one only ever joins two rows that already exist
/// and already carry a tenant, so the decision it makes instead is that the two must <em>agree</em> —
/// which it checks explicitly, because both lookups are by primary key and a primary-key lookup is
/// scoped by nothing while no tenant is pinned (ADR-001 D-6).
/// </para>
/// </summary>
internal sealed class StudentClassificationService : IStudentClassificationService
{
    private readonly EamsDbContext _db;

    /// <summary>
    /// Where the two things this service <em>recovers from</em> get recorded: losing the axis-slot race
    /// to <c>UX_StudentClassifications_Student_Axis</c>, and losing the classification itself to a
    /// concurrent delete. Both are answerable conflicts individually and ordinary enough not to alert
    /// on; a <em>rate</em> of either is the difference between two operators colliding and a client
    /// retrying a write it already won, and nothing else in the system can tell those apart.
    /// </summary>
    private readonly ILogger<StudentClassificationService> _logger;

    public StudentClassificationService(
        EamsDbContext db, ILogger<StudentClassificationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ------------------------------------------------------------------------------------- reads

    public async Task<StudentClassificationsDto?> GetAsync(
        Guid studentId, CancellationToken ct = default)
    {
        // Existence is established separately from the assignment read, so "this person holds nothing"
        // and "there is no such person" are different answers. Collapsing them — returning an empty
        // list for a student id that does not resolve — would let an edit form silently save against a
        // deleted row.
        var exists = await _db.Students.AsNoTracking()
            .AnyAsync(s => s.Id == studentId && !s.IsDeleted, ct);

        return exists ? await HeldDtoAsync(studentId, ct) : null;
    }

    /// <summary>
    /// The person's classifications, <b>through the same query, ordering and projection the students
    /// grid and <c>GET /students/{id}</c> use</b> — see <see cref="StudentClassificationReads"/>.
    ///
    /// <para>
    /// Shared rather than re-written here because this surface and the grid render the same values on
    /// the same screen: the form opens from a grid row. Two implementations could order differently, or
    /// one could filter retired entries and the other not — and that second case is not cosmetic, it
    /// would mean opening the form silently drops a classification and saving commits the drop.
    /// </para>
    /// </summary>
    private Task<IReadOnlyList<StudentClassificationDto>> HeldAsync(
        Guid studentId, CancellationToken ct) =>
        StudentClassificationReads.ForStudentAsync(_db, studentId, ct);

    // ------------------------------------------------------------------------------------ writes

    /// <summary>
    /// <inheritdoc cref="IStudentClassificationService.AssignAsync" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The replacement is an <c>UPDATE</c> of the existing row, not a delete-and-insert, and that is
    /// what makes it collision-free.</b> Only <c>ClassificationId</c> changes, so <c>(StudentId,
    /// Axis)</c> is bit-identical before and after and <c>UX_StudentClassifications_Student_Axis</c>
    /// cannot be violated by it — the same reasoning <c>ClassificationService.MergeAsync</c>'s repoint
    /// rests on. Only the <em>insert</em> branch can race, and it races against that index, which is
    /// caught below.
    /// </para>
    ///
    /// <para>
    /// <b>The "already holds it" check runs before the assignable check, deliberately.</b> A form that
    /// round-trips what it read must keep working after somebody retires a category, and that is a
    /// no-op: the person's state is already what the request asks for. Refusing it would mean an
    /// administrator editing a <em>name</em> gets a 409 about a classification they never touched.
    /// </para>
    /// </remarks>
    public async Task<StudentClassificationWriteResponse> AssignAsync(
        Guid studentId, Guid classificationId, CancellationToken ct = default)
    {
        try
        {
            return await AssignCoreAsync(studentId, classificationId, ct);
        }
        catch (Exception ex) when (SqlServerErrors.IsDeadlockVictim(ex))
        {
            return DeadlockVictim(ex, studentId, "assignment", classificationId);
        }
    }

    /// <summary>
    /// <inheritdoc cref="AssignAsync" path="/summary"/>
    ///
    /// <para>
    /// Separated from <see cref="AssignAsync"/> only so the deadlock net can wrap <b>every</b> statement
    /// rather than the writes alone — see <see cref="DeadlockVictim"/> for why the reads need it too.
    /// </para>
    /// </summary>
    private async Task<StudentClassificationWriteResponse> AssignCoreAsync(
        Guid studentId, Guid classificationId, CancellationToken ct)
    {
        var student = await _db.Students.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == studentId && !s.IsDeleted, ct);

        if (student is null) return StudentNotFound();

        var classification = await _db.Classifications.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == classificationId, ct);

        if (classification is null) return ClassificationNotFound();

        // Explicit, because both reads above are primary-key lookups and a primary-key lookup is not
        // scoped by anything while no tenant is pinned. Without it, one institution's person could be
        // filed under another institution's category — and the composite foreign key would happily
        // allow it, because it constrains the axis and says nothing about the school.
        if (classification.SchoolId != student.SchoolId) return CrossSchool();

        var axis = classification.Axis;

        // AsNoTracking: the replace below is a conditional ExecuteUpdate rather than a tracked mutation,
        // so nothing here is ever saved through the change tracker and a tracked copy would only be a
        // stale one for the recovery paths to read back.
        var held = await _db.StudentClassifications.AsNoTracking()
            .FirstOrDefaultAsync(sc => sc.StudentId == studentId && sc.Axis == axis, ct);

        if (held is not null && held.ClassificationId == classificationId)
        {
            // Idempotent and short-circuited rather than re-written: bumping UpdatedAt on a no-op would
            // make an audit column record clicks rather than edits. Same reading as
            // ClassificationService.SetActiveAsync and TermAdminService.SetCurrentAsync.
            return await SavedAsync(
                studentId,
                $"'{classification.Name}' is already this person's {axis} classification. Nothing was " +
                "changed.",
                replaced: null,
                ct);
        }

        if (classification.MergedIntoClassificationId is { } absorbedBy)
        {
            var survivorName = await _db.Classifications.AsNoTracking()
                .Where(c => c.Id == absorbedBy)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);

            return new StudentClassificationWriteResponse(
                StudentClassificationWriteOutcome.ClassificationMerged,
                $"'{classification.Name}' was merged into '{survivorName ?? absorbedBy.ToString()}', so " +
                "it is a record of where its people went rather than a category anyone can be filed " +
                $"under. Assign '{survivorName ?? absorbedBy.ToString()}' ({absorbedBy}) instead — that " +
                "is where everyone this one described now sits.",
                null, null);
        }

        if (!ClassificationAssignment.IsAssignable(classification))
        {
            return new StudentClassificationWriteResponse(
                StudentClassificationWriteOutcome.ClassificationRetired,
                $"'{classification.Name}' is retired, so it is not offered for new assignments. " +
                "Everyone already filed under it keeps it — retiring reassigns nobody — but adding " +
                "somebody new would grow a category that was deliberately withdrawn. Reactivate it " +
                $"(PATCH /classifications/{classification.Id}/active with isActive true) or pick " +
                $"another {axis} classification.",
                null, null);
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------ the occupied-axis case
        if (held is { } occupant)
        {
            return await ReplaceAsync(studentId, axis, occupant.ClassificationId, classification, now, ct);
        }

        // ------------------------------------------------------------------- the empty-axis case
        //
        // Through the domain helper rather than an object initializer here, so that this path and
        // Phase 1b's importer establish Axis == Classification.Axis the same way rather than twice.
        _db.StudentClassifications.Add(ClassificationAssignment.For(studentId, classification));

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // Somebody else filled this person's axis slot between the read above and this insert.
            // UX_StudentClassifications_Student_Axis is the arbiter, exactly as it should be: the
            // pre-check and the insert are two statements and only the index sees both.
            Detach(studentId, axis, classificationId);

            // Through the same recovery the replace branch uses, so the two cannot drift about what a
            // lost race means or what the loser is told.
            return await RacedAsync(studentId, axis, classification, ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsConstraintConflict(ex))
        {
            // 547 is FOREIGN KEY *and* CHECK, and SqlServerErrors deliberately refuses to guess which —
            // its own documentation warns that swallowing a check-constraint failure on a write would
            // hide a bug in the service that wrote the row. So this arm establishes which one fired
            // instead of assuming.
            //
            // Only one of the two is a conflict this service can answer: the composite
            // (ClassificationId, Axis) reference fails when the classification row was deleted between
            // the read above and this write. CK_StudentClassifications_Axis cannot fire — the axis was
            // copied off a parent that already satisfies CK_Classifications_Axis — so if the
            // classification is still there, this 547 is not ours and rethrowing is the honest answer.
            Detach(studentId, axis, classificationId);

            if (await _db.Classifications.AsNoTracking().AnyAsync(c => c.Id == classificationId, ct))
                throw;

            _logger.LogInformation(
                "An assignment of classification {ClassificationId} to student {StudentId} was refused " +
                "by the composite foreign key because the classification was deleted concurrently; it " +
                "was answered 404.", classificationId, studentId);

            return ClassificationNotFound();
        }

        return await SavedAsync(
            studentId,
            $"'{classification.Name}' is now this person's {axis} classification. Their other axes are " +
            "untouched — a person holds one classification per axis and may hold several axes at once.",
            replaced: null,
            ct);
    }

    /// <summary>
    /// Moves an <em>occupied</em> axis slot onto a new classification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>WHERE</c> names the classification this request read, and that clause is the whole
    /// point of the method.</b> Without it two concurrent assignments onto one occupied axis both
    /// update the same row, both report 200 naming their own value, and the first registrar's
    /// correction is gone with nobody told — while <see cref="IStudentClassificationService"/> promises
    /// in as many words that a disagreement is never resolved by letting the later caller win silently.
    /// Nothing in the schema could have caught it: <c>(StudentId, Axis)</c> is unchanged by either
    /// write, so <c>UX_StudentClassifications_Student_Axis</c> is satisfied by the corrupt outcome. The
    /// application-side condition <em>is</em> the guard — the same shape, and the same reasoning, as
    /// <c>ClassificationService.MergeAsync</c>'s conditional retire.
    /// </para>
    ///
    /// <para>
    /// <b>An untracked <c>ExecuteUpdate</c> rather than a tracked mutation</b>, because the affected-row
    /// count is the answer and <c>SaveChanges</c> does not hand it back usefully: a tracked update whose
    /// row has vanished raises <c>DbUpdateConcurrencyException</c>, which carries no inner
    /// <c>SqlException</c> and so matches none of the catch filters on this class — a caller-level
    /// conflict that would have shipped as a 500. Here that case is simply <c>0</c>.
    /// </para>
    ///
    /// <para>
    /// <b><c>CreatedAt</c> is rewritten, and that is a fix rather than a flourish.</b> It is what
    /// <see cref="StudentClassificationDto.AssignedAt"/> is projected from, and the row is re-pointed
    /// rather than re-created — so leaving it alone reported a person as classified <c>ACAD</c> since
    /// the date they were made <c>NAP</c>, years earlier, for a category that touched them seconds ago.
    /// Two doc blocks already said it resets; this is the code catching up with them.
    /// </para>
    ///
    /// <para>
    /// <c>Axis</c> is deliberately not written: it is already this classification's axis — that is how
    /// the slot was found — so the denormalized column still equals the new parent's, which is exactly
    /// what the composite foreign key requires.
    /// </para>
    /// </remarks>
    private async Task<StudentClassificationWriteResponse> ReplaceAsync(
        Guid studentId, string axis, Guid replacedId, Classification classification,
        DateTime now, CancellationToken ct)
    {
        int moved;

        try
        {
            moved = await _db.StudentClassifications
                .Where(sc => sc.StudentId == studentId
                          && sc.Axis == axis
                          && sc.ClassificationId == replacedId)
                .ExecuteUpdateAsync(
                    u => u.SetProperty(sc => sc.ClassificationId, classification.Id)
                          .SetProperty(sc => sc.CreatedAt, now)
                          .SetProperty(sc => sc.UpdatedAt, now)
                          // Cleared in the same statement that re-points the row. The value records
                          // which roster disagreement has already been reported against THIS
                          // assignment (see StudentClassification.ReportedRosterValue); once the
                          // assignment is a different one, nothing has been reported about it yet, and
                          // an administrator who has just re-classified somebody should hear from the
                          // next import if the file still disagrees. Carrying it over would suppress
                          // exactly the announcement they need.
                          .SetProperty(sc => sc.ReportedRosterValue, (string?)null),
                    ct);
        }
        catch (Exception ex) when (SqlServerErrors.IsConstraintConflict(ex))
        {
            // The same discriminate-then-rethrow the insert branch performs, for the same reason and
            // against the same constraint: the composite (ClassificationId, Axis) reference fails when
            // the classification we just read was deleted underneath us. Nothing was written — a
            // refused UPDATE is atomic — so there is nothing to undo.
            if (await _db.Classifications.AsNoTracking().AnyAsync(c => c.Id == classification.Id, ct))
                throw;

            _logger.LogInformation(
                "A replacement of student {StudentId}'s {Axis} classification with " +
                "{ClassificationId} was refused by the composite foreign key because the " +
                "classification was deleted concurrently; it was answered 404.",
                studentId, axis, classification.Id);

            return ClassificationNotFound();
        }

        if (moved == 1)
        {
            return await SavedAsync(
                studentId,
                $"'{classification.Name}' is now this person's {axis} classification, replacing what " +
                "they held on that axis. A person holds only one classification per axis, so this was " +
                "a replacement rather than an addition; replacedClassificationId names what went.",
                replacedId,
                ct);
        }

        // Zero rows: between this request's read and its write somebody else moved or cleared the same
        // slot. Which of those it was decides the answer, and only the database knows, so it is re-read
        // rather than assumed — exactly as the insert branch does.
        return await RacedAsync(studentId, axis, classification, ct);
    }

    /// <summary>
    /// The one recovery both write branches share: re-read the axis slot and answer for what is
    /// actually there.
    ///
    /// <para>
    /// <b>A caller who lost to somebody assigning the <em>same</em> classification is told it saved</b>,
    /// because the postcondition they asked for holds — that is not a courtesy, it is what keeps a
    /// double-submitted form from reporting a conflict it did not cause. Anyone else is told the truth:
    /// nothing of theirs was written, and the two requests disagree about what this person is.
    /// </para>
    /// </summary>
    private async Task<StudentClassificationWriteResponse> RacedAsync(
        Guid studentId, string axis, Classification classification, CancellationToken ct)
    {
        var winner = await _db.StudentClassifications.AsNoTracking()
            .FirstOrDefaultAsync(sc => sc.StudentId == studentId && sc.Axis == axis, ct);

        _logger.LogInformation(
            "An assignment of classification {ClassificationId} to student {StudentId} lost the race " +
            "for the {Axis} axis slot; it is now held by {WinnerId}.",
            classification.Id, studentId, axis, winner?.ClassificationId);

        if (winner?.ClassificationId == classification.Id)
        {
            return await SavedAsync(
                studentId,
                $"'{classification.Name}' was assigned as this person's {axis} classification by " +
                "another request at the same moment. The result is what this one asked for.",
                replaced: null,
                ct);
        }

        return new StudentClassificationWriteResponse(
            StudentClassificationWriteOutcome.ConcurrentAssignment,
            $"Somebody else changed this person's {axis} classification while this request was being " +
            "prepared, and a person holds only one classification per axis. Nothing was changed. " +
            "Re-read their classifications and decide against what is there now — this was not retried " +
            "automatically, because the two requests disagree about what this person is and letting " +
            "the later one win silently is how a correction disappears.",
            await HeldDtoAsync(studentId, ct),
            null);
    }

    public async Task<StudentClassificationWriteResponse> ClearAsync(
        Guid studentId, Guid classificationId, CancellationToken ct = default)
    {
        try
        {
            return await ClearCoreAsync(studentId, classificationId, ct);
        }
        catch (Exception ex) when (SqlServerErrors.IsDeadlockVictim(ex))
        {
            return DeadlockVictim(ex, studentId, "clear", classificationId);
        }
    }

    /// <summary>
    /// <inheritdoc cref="ClearAsync" path="/summary"/>
    ///
    /// <para>
    /// Separated from <see cref="ClearAsync"/> for the reason <see cref="AssignCoreAsync"/> is: the
    /// deadlock net has to cover the reads as well as the delete.
    /// </para>
    /// </summary>
    private async Task<StudentClassificationWriteResponse> ClearCoreAsync(
        Guid studentId, Guid classificationId, CancellationToken ct)
    {
        var exists = await _db.Students.AsNoTracking()
            .AnyAsync(s => s.Id == studentId && !s.IsDeleted, ct);

        if (!exists) return StudentNotFound();

        // Matched on the pair rather than on the axis, so the caller says what they are removing. A
        // clear addressed by axis would silently take away whatever happened to be in that slot,
        // including a value somebody else put there after this client last read.
        //
        // The classification's own state is deliberately not consulted: a retired or merged-away
        // category must still be removable, or a person could be permanently stuck holding one.
        var held = await _db.StudentClassifications
            .FirstOrDefaultAsync(
                sc => sc.StudentId == studentId && sc.ClassificationId == classificationId, ct);

        if (held is null)
        {
            return NotAssigned(
                "This person does not hold that classification, so there is nothing to clear. A second " +
                "delete of the same assignment lands here too — by then the row is invisible to every " +
                "read in the system, and reporting success for something the caller can no longer see " +
                "would be the misleading answer.");
        }

        var axis = held.Axis;

        // The junction row and nothing else. The classification is shared by everyone filed under it,
        // and a clear that reached it would uncategorise every one of them — which is the data-loss
        // shape DELETE /classifications/{id} is guarded against for the same reason.
        _db.StudentClassifications.Remove(held);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody else cleared the same assignment between the read above and this delete, so
            // SaveChanges affected zero rows where it expected one.
            //
            // Caught BEFORE any DbUpdateException filter would be, and that ordering is load-bearing:
            // DbUpdateConcurrencyException derives from DbUpdateException, so a broader arm placed
            // first would swallow it — and, worse here, the two filters this class uses both reach for
            // an inner SqlException, which a concurrency exception does not have. Both would evaluate
            // false and a caller-level conflict would ship as a 500, on the one surface whose own unit
            // test says nothing here may answer 5xx.
            //
            // Detached so the failed Remove does not leave the row marked Deleted on this context.
            //
            // NOT because of a read-back — this path returns NotAssigned, which carries no DTO and
            // reads nothing. (An earlier version of this comment said otherwise; the justification was
            // copied from the paths that do re-read, and was wrong here.) The reason is that a retained
            // Deleted entry is a staged write: anything that later calls SaveChanges on this context —
            // another service sharing the request's unit of work, a future caller of this method that
            // does more than return — would re-issue the delete and raise this same exception again,
            // from a place with no handler for it.
            _db.Entry(held).State = EntityState.Detached;

            _logger.LogInformation(
                "A clear of student {StudentId}'s classification {ClassificationId} found the row " +
                "already deleted by a concurrent request; it was answered 404.",
                studentId, classificationId);

            return NotAssigned(
                "This person does not hold that classification — another request cleared the same " +
                "assignment while this one was being prepared. The end state is the one you asked " +
                "for, but nothing here removed it.");
        }

        return await SavedAsync(
            studentId,
            $"This person no longer has a {axis} classification. The classification itself is " +
            "untouched and everybody else filed under it keeps it.",
            replaced: null,
            ct);
    }

    // ---------------------------------------------------------------------------------- plumbing

    /// <summary>
    /// Undoes the failed <em>insert</em> on the change tracker before anything is re-read.
    ///
    /// <para>
    /// EF leaves a failed insert <c>Added</c>, and a retained <c>Added</c> row shadows every later read
    /// of that key through identity resolution — so the re-read in <see cref="RacedAsync"/> would report
    /// this request's own losing row as the winner, which is the opposite of what re-reading is for. The
    /// same detach <c>StudentService.AddCardAsync</c> and <c>ClassificationService.CreateAsync</c>
    /// perform, for the same reason.
    /// </para>
    ///
    /// <para>
    /// <b>Only the insert branch needs it.</b> The replace is an untracked <c>ExecuteUpdate</c> and
    /// stages nothing, which is one of the reasons it is written that way.
    /// </para>
    /// </summary>
    private void Detach(Guid studentId, string axis, Guid classificationId)
    {
        // The staged entity is found on the tracker rather than passed down, by the key it was built
        // with — which is the pair the index just rejected.
        var staged = _db.ChangeTracker.Entries<StudentClassification>()
            .FirstOrDefault(e => e.State == EntityState.Added
                              && e.Entity.StudentId == studentId
                              && e.Entity.Axis == axis
                              && e.Entity.ClassificationId == classificationId);

        if (staged is not null) staged.State = EntityState.Detached;
    }

    private async Task<StudentClassificationWriteResponse> SavedAsync(
        Guid studentId, string message, Guid? replaced, CancellationToken ct) =>
        new(StudentClassificationWriteOutcome.Saved, message,
            await HeldDtoAsync(studentId, ct), replaced);

    private async Task<StudentClassificationsDto> HeldDtoAsync(Guid studentId, CancellationToken ct) =>
        new(studentId, await HeldAsync(studentId, ct));

    private static StudentClassificationWriteResponse StudentNotFound() =>
        new(StudentClassificationWriteOutcome.StudentNotFound, "Student not found.", null, null);

    private static StudentClassificationWriteResponse ClassificationNotFound() =>
        new(StudentClassificationWriteOutcome.ClassificationNotFound,
            "Classification not found. The id must name a classification in this school's vocabulary " +
            "(GET /classifications).",
            null, null);

    /// <summary>
    /// <b>SQL Server picked this request to roll back so two others could proceed. Nothing was written,
    /// so it is a conflict the caller can simply repeat — not a server fault.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The deadlock is ordinary, not exotic, and that is why the net is needed.</b>
    /// <c>AssignAsync</c> reaches a person's row through <c>UX_StudentClassifications_Student_Axis</c>
    /// — keyed <c>(StudentId, Axis)</c> — while <c>ClearAsync</c> reaches the same row filtering on
    /// <c>(StudentId, ClassificationId)</c>, which that index does not serve. The two therefore take the
    /// unique index and the clustered index in <em>opposite orders</em>, which is the textbook shape. It
    /// takes one registrar editing a classification while another clears it, on the same person.
    /// </para>
    ///
    /// <para>
    /// <b>It wraps the reads as well as the writes, deliberately.</b> The deadlock that exposed this
    /// was raised by the axis-slot <em>read</em>, not by a write — a lock-ordering cycle does not care
    /// which statement of yours is the one holding the second lock, so a net that covered only the
    /// <c>SaveChanges</c> and the <c>ExecuteUpdate</c> would have missed the case that actually
    /// happened.
    /// </para>
    ///
    /// <para>
    /// <b>Caught here rather than left to <c>EnableRetryOnFailure</c></b>, for the reason
    /// <c>ClassificationService.MergeAsync</c> records against the same error number: the production
    /// registration does retry 1205, so this would usually be invisible there — but that makes the
    /// answer depend on how the host was composed, and the hosts that do <em>not</em> enable retries
    /// (the test host, a console tool, anything composing <c>AddEamsInfrastructure</c> directly) are the
    /// ones least able to explain a 500. A service should not be answerable only when its host is
    /// configured a particular way.
    /// </para>
    ///
    /// <para>
    /// <b>No re-read, and that is on purpose.</b> Every other conflict on this service reports the
    /// person's current state alongside the refusal; this one does not, because the statement that would
    /// fetch it is the same kind of statement that just lost a deadlock, and an exception raised while
    /// composing an error response is a 500 wearing a different hat. The caller is being told to re-read
    /// anyway — and on a failure outcome the controller answers RFC 7807, which never carries the DTO.
    /// </para>
    ///
    /// <para>
    /// <b>Both operations answer <see cref="StudentClassificationWriteOutcome.ConcurrentAssignment"/>,
    /// including the clear</b> — see the deviation note in the phase report. A deadlocked clear did not
    /// discover the row missing; it discovered nothing at all, because its transaction was rolled back
    /// before it could look. Answering <see cref="StudentClassificationWriteOutcome.NotAssigned"/> —
    /// a 404 — would assert that the person does not hold a classification they almost certainly still
    /// hold, which is a plausible, non-empty, wrong answer of exactly the kind this project keeps a
    /// register of. 409 says what is true: nothing happened, try again.
    /// </para>
    /// </remarks>
    private StudentClassificationWriteResponse DeadlockVictim(
        Exception ex, Guid studentId, string operation, Guid classificationId)
    {
        // Information, not Error: being the victim means the server resolved a contention the only way
        // it can, and the caller's request had no effect at all. It is the RATE that carries signal —
        // a steady stream means two surfaces are fighting over one person, which is a design finding
        // rather than an incident.
        _logger.LogInformation(
            ex,
            "A classification {Operation} of {ClassificationId} for student {StudentId} was chosen as " +
            "a deadlock victim and was answered 409 with nothing written. AssignAsync reaches the row " +
            "by (StudentId, Axis) and ClearAsync by (StudentId, ClassificationId), so the two take the " +
            "unique and clustered indexes in opposite orders.",
            operation, classificationId, studentId);

        return new StudentClassificationWriteResponse(
            StudentClassificationWriteOutcome.ConcurrentAssignment,
            "Another request was changing this person's classifications at the same moment, and this " +
            "one was rolled back to break the tie. Nothing was changed — no assignment was written, " +
            "moved or removed. Re-read their classifications and try again.",
            null,
            null);
    }

    private static StudentClassificationWriteResponse NotAssigned(string message) =>
        new(StudentClassificationWriteOutcome.NotAssigned, message, null, null);

    private static StudentClassificationWriteResponse CrossSchool() =>
        new(StudentClassificationWriteOutcome.CrossSchool,
            "That student and that classification belong to different schools. A classification " +
            "describes the people of the institution that owns it, and an assignment cannot cross a " +
            "tenant.",
            null, null);
}
