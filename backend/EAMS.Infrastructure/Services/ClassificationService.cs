using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// The administrator-owned classification vocabulary — see <see cref="IClassificationService"/> for why
/// it is a table rather than a string column, and why assignment is deliberately not here.
///
/// <para>
/// <b>One index decides the duplicate question and it is never allowed to surface as a 500.</b>
/// <c>UX_Classifications_SchoolId_NameKey</c> is unfiltered and rejects a repeated normalized name;
/// this class checks first <em>and</em> catches the violation, because the check and the insert are two
/// statements and the second administrator to press Create in the same second is decided by the index
/// rather than by the check. Exactly the shape <c>TermAdminService</c> records against
/// <c>UX_Terms_SchoolId_Code</c>.
/// </para>
///
/// <para>
/// <b>Every statement whose result set could span schools carries an explicit <c>SchoolId</c>
/// predicate</b> rather than leaning on the global query filter, which is inert whenever no tenant is
/// pinned — design time, most of the test suite, any unseeded start. That is the duplicate check and
/// the merge's sibling lookup. The remaining reads are primary-key lookups, which resolve one row by
/// identity and have no cross-school set to scope.
/// </para>
/// </summary>
internal sealed class ClassificationService : IClassificationService
{
    private readonly EamsDbContext _db;

    /// <summary>
    /// Which tenant a newly created classification is filed under, through
    /// <see cref="SchoolResolution.ResolveSchoolIdAsync"/> so this agrees with the tenant the startup
    /// log announced rather than inventing a second answer.
    /// </summary>
    private readonly ISchoolContext _school;

    /// <summary>
    /// Where the two things this service <em>recovers from</em> get recorded: a create or a rename that
    /// lost the duplicate-name race to the index, and a completed merge. The merge is logged because it
    /// is the one operation here that moves other tables' rows, and "how did these people end up under
    /// that category" is a question somebody will ask months later with nothing else to go on.
    /// </summary>
    private readonly ILogger<ClassificationService> _logger;

    public ClassificationService(
        EamsDbContext db, ISchoolContext school, ILogger<ClassificationService> logger)
    {
        _db = db;
        _school = school;
        _logger = logger;
    }

    /// <summary>
    /// <paramref name="studentCount"/> is passed in rather than read from a navigation, so every DTO
    /// this class produces states where its number came from. A lazily loaded count would be an N+1 on
    /// the list read and a silent zero everywhere else.
    /// </summary>
    private static ClassificationDto ToDto(Classification c, int studentCount) => new(
        c.Id, c.Name, c.NameKey, c.Axis, c.IsActive, c.RetiredAt, c.MergedIntoClassificationId,
        studentCount);

    // ---------------------------------------------------------------------------------------- reads

    public async Task<PagedResult<ClassificationDto>> ListAsync(
        bool includeRetired, PageRequest page, CancellationToken ct = default)
    {
        var filtered = includeRetired
            ? _db.Classifications.AsNoTracking()
            : _db.Classifications.AsNoTracking().Where(c => c.IsActive);

        // Through PagedQuery rather than open-coded, because this is the tenth read of a shape that
        // was lifted into a helper precisely after being copy-pasted four times. Writing the count and
        // the page as two statements here would reintroduce the one defect the helper exists to
        // prevent — a total and a set of rows that describe different filters.
        //
        // Ordering: active first, then by AXIS, then by name. The axis level is what a picker needs,
        // since a person holds at most one classification per axis and the groups are the choices;
        // ordering by name alone would interleave four unrelated lists. The name sort is the display
        // name rather than the key, because a human reads it and 'SUPERVISORY/MANAGERIAL' sorts where
        // they expect only with its punctuation intact. Id ends the order so it is total — two
        // classifications cannot share a name within a school, but an OFFSET/FETCH over a non-total
        // order silently repeats one row and skips another between pages, which is what
        // PaginationTests.Every_paged_list_query_orders_by_a_unique_column enforces.
        //
        // The per-row count is a correlated subquery inside the same projection rather than a round
        // trip per row — the N+1 this repository's own lessons are about — and it sits in the callback
        // so it runs for the page only, never for the rows the COUNT discards. It is scoped by the
        // junction's own tenant filter, which reaches a school through the Student.
        return await filtered.ToPageAsync(
            q => q
                .OrderByDescending(c => c.IsActive)
                .ThenBy(c => c.Axis)
                .ThenBy(c => c.Name)
                .ThenBy(c => c.Id)
                .Select(c => new ClassificationDto(
                    c.Id, c.Name, c.NameKey, c.Axis, c.IsActive, c.RetiredAt,
                    c.MergedIntoClassificationId,
                    _db.StudentClassifications.Count(sc => sc.ClassificationId == c.Id))),
            page,
            ct);
    }

    public async Task<ClassificationDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.Classifications.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row is null ? null : ToDto(row, await AssignmentCountAsync(id, ct));
    }

    /// <summary>How many people hold this classification. Scoped by the junction's own tenant filter.</summary>
    private Task<int> AssignmentCountAsync(Guid classificationId, CancellationToken ct) =>
        _db.StudentClassifications.CountAsync(sc => sc.ClassificationId == classificationId, ct);

    // --------------------------------------------------------------------------------- create/edit

    public async Task<ClassificationWriteResponse> CreateAsync(
        ClassificationCreateRequest request, CancellationToken ct = default)
    {
        if (ValidateName(request.Name) is { } refused) return refused;

        // Case-insensitive in, canonical out — the same contract AttendanceStatus.TryNormalize offers.
        // A caller sending "personnel" gets a row spelled "Personnel", so the CHECK constraint and
        // every C# comparison against ClassificationAxis agree without the caller having to know.
        if (!ClassificationAxis.TryNormalize(request.Axis, out var axis))
        {
            return new ClassificationWriteResponse(
                ClassificationWriteOutcome.ValidationFailed,
                $"Axis is required and must be one of {string.Join(", ", ClassificationAxis.All)}. It " +
                "names which of the source export's four category columns this value came from, and " +
                "there is deliberately no default — a personnel category created without one would " +
                "land on the student axis, compete for the single slot a person has there, and say " +
                "nothing about it. It cannot be changed afterwards.",
                null);
        }

        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null) return NoSchool();

        var key = ClassificationText.KeyFor(request.Name);

        if (await TakenKeyAsync(schoolId.Value, key, excluding: null, ct) is { } taken) return taken;

        var row = new Classification
        {
            SchoolId = schoolId.Value,

            // Stored exactly as authored — the slash in SUPERVISORY/MANAGERIAL survives, and so does
            // the operator's capitalisation. Only the key beside it is normalized.
            Name = request.Name,
            NameKey = key,
            Axis = axis,
            IsActive = true,
        };

        _db.Classifications.Add(row);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // Detached first because EF leaves a failed insert Added, and a retained Added row would
            // shadow every later read of that key through identity resolution — the same detach
            // TermAdminService and StudentService both perform, for the same reason.
            _db.Entry(row).State = EntityState.Detached;

            _logger.LogInformation(
                "A create of classification '{Name}' (key {NameKey}) lost the race to " +
                "UX_Classifications_SchoolId_NameKey and was answered 409.", request.Name, key);

            return Duplicate(request.Name, key);
        }

        return new ClassificationWriteResponse(
            ClassificationWriteOutcome.Saved,
            $"Classification created on the {axis} axis, and active. The axis cannot be changed later " +
            "— if it is wrong, create the right one and merge this into it. Retire it with " +
            "PATCH /classifications/{id}/active if it should not be offered for new assignments.",
            ToDto(row, studentCount: 0));
    }

    public async Task<ClassificationWriteResponse> RenameAsync(
        Guid id, ClassificationRenameRequest request, CancellationToken ct = default)
    {
        if (ValidateName(request.Name) is { } refused) return refused;

        var row = await _db.Classifications.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound();

        var key = ClassificationText.KeyFor(request.Name);

        // Only when the key actually moved. A PUT that re-sends the row's own name is the ordinary
        // case (the form round-trips every field), and checking it against the index would report the
        // row as its own duplicate. Note this is the *key*, not the name: fixing the punctuation of
        // 'SUPERVISORY/MANAGERIAL' to 'Supervisory / Managerial' changes the display and not the key,
        // and must not be refused as a duplicate of itself.
        if (!string.Equals(key, row.NameKey, StringComparison.Ordinal)
            && await TakenKeyAsync(row.SchoolId, key, excluding: row.Id, ct) is { } taken)
        {
            return taken;
        }

        row.Name = request.Name;
        row.NameKey = key;
        row.UpdatedAt = DateTime.UtcNow;

        // IsActive and MergedIntoClassificationId are deliberately untouched. Editing a display name
        // must not bring a retired category back into every picker as a side effect — that is
        // SetActiveAsync, and it is a different statement about a different subject.

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            _logger.LogInformation(
                "A rename of classification {ClassificationId} onto '{Name}' (key {NameKey}) lost the " +
                "race to UX_Classifications_SchoolId_NameKey and was answered 409.",
                row.Id, request.Name, key);

            return Duplicate(request.Name, key);
        }

        return new ClassificationWriteResponse(
            ClassificationWriteOutcome.Saved,
            "Classification renamed. Nobody was reassigned: a person's classification is held by id, " +
            "which is the reason this vocabulary is a table rather than a column of text. The axis is " +
            "unchanged and cannot be changed.",
            ToDto(row, await AssignmentCountAsync(row.Id, ct)));
    }

    // ------------------------------------------------------------------------------------ retiring

    public async Task<ClassificationWriteResponse> SetActiveAsync(
        Guid id, bool isActive, CancellationToken ct = default)
    {
        var row = await _db.Classifications.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound();

        // Read once, before any branch: every exit below reports it, and retiring does not change it
        // — which is the whole claim this operation makes.
        var assigned = await AssignmentCountAsync(row.Id, ct);

        if (isActive && row.MergedIntoClassificationId is not null)
        {
            // Refused rather than quietly allowed, because the flag is not the whole of what a merge
            // did. The population moved to the survivor, so reactivating this row offers an *empty*
            // category under a name people still recognise — and the next person to be filed under it
            // is separated from everyone the category used to mean. CK_Classifications_MergedIsRetired
            // says the same thing at the database, so this is the readable half of a refusal that
            // would otherwise arrive as a constraint violation.
            return new ClassificationWriteResponse(
                ClassificationWriteOutcome.NotMergeable,
                "That classification was merged into another one, so it cannot simply be reactivated: " +
                "everyone it described now sits under the survivor, and bringing it back would offer " +
                "an empty category under a familiar name. Merge the survivor back into it if the " +
                "merge was the wrong way round.",
                ToDto(row, assigned));
        }

        if (row.IsActive == isActive)
        {
            // Idempotent, and short-circuited rather than re-written: bumping UpdatedAt on a no-op
            // would make an audit column record clicks rather than edits. Same reading as
            // TermAdminService.SetCurrentAsync.
            return new ClassificationWriteResponse(
                ClassificationWriteOutcome.Saved,
                isActive
                    ? "That classification was already active."
                    : "That classification was already retired.",
                ToDto(row, assigned));
        }

        row.IsActive = isActive;

        // Cleared on reactivation rather than kept as history, so the column always describes the
        // row's current state. An audit trail of vocabulary edits belongs in AuditLogs, where it can
        // record who did it; a single nullable timestamp that sometimes means "retired then" and
        // sometimes means "retired once, long ago" is the worse of the two.
        row.RetiredAt = isActive ? null : DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return new ClassificationWriteResponse(
            ClassificationWriteOutcome.Saved,
            isActive
                ? "Classification reactivated. It is offered for new assignments again."
                : $"Classification retired. It is no longer offered for new assignments, and the " +
                  $"{assigned} person(s) already filed under it keep it — retiring reassigns nobody " +
                  "and deletes nothing.",
            ToDto(row, assigned));
    }

    // -------------------------------------------------------------------------------- guarded delete

    /// <summary>
    /// <inheritdoc cref="IClassificationService.DeleteAsync" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two kinds of thing can reference a classification, and the refusal names them separately
    /// because they call for different remedies.</b> People hold it
    /// (<c>StudentClassifications</c>) — those are moved by a merge. Retired classifications point at
    /// it as the row that absorbed them — that is somebody else's history, and the answer is to leave
    /// this row alone. A single "3 references" would have sent an operator looking for three people who
    /// do not exist.
    /// </para>
    ///
    /// <para>
    /// <b>The count is not the only defence and is not trusted to be.</b> Between the count and the
    /// delete there are two statements, so a concurrent merge could file a tombstone against a row this
    /// method has already decided is free. The foreign key is <c>DeleteBehavior.Restrict</c>, so that
    /// race ends in a constraint violation rather than a dangling reference, and it is caught below and
    /// answered as the conflict it is — the same "check first, catch anyway" the duplicate-name path
    /// uses, for the same reason.
    /// </para>
    /// </remarks>
    public async Task<ClassificationWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.Classifications.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound();

        // A merged-away classification is refused before any count is taken, because the thing that
        // makes it undeletable is not a referrer — it is the row itself.
        //
        // After a merge, the loser has zero assignments (they moved) and zero tombstones pointing at
        // it, so a guard written purely as "does anything reference this" waves it straight through
        // and the response says "nothing referenced it, which is the only circumstance in which
        // deleting one destroys nothing". That sentence is false here. This row IS the record that a
        // merge happened: delete it and the people who were on it become indistinguishable from the
        // people who were always on the survivor, and the merge cannot be audited, explained or
        // reversed. Both this type's own remarks and the controller's call the tombstone load-bearing
        // history; this is the guard that makes those claims true.
        if (row.MergedIntoClassificationId is { } absorbedBy)
        {
            var survivorName = await _db.Classifications.AsNoTracking()
                .Where(c => c.Id == absorbedBy)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);

            return new ClassificationWriteResponse(
                ClassificationWriteOutcome.InUse,
                $"'{row.Name}' cannot be deleted: it was merged into " +
                $"'{survivorName ?? absorbedBy.ToString()}'" +
                (row.RetiredAt is { } when ? $" on {when:yyyy-MM-dd}" : "") +
                ". It holds nobody now precisely because everyone it described moved to the " +
                "survivor, and the row is what records that they did — deleting it would make those " +
                "people indistinguishable from people who were always on the survivor, and leave the " +
                "merge unexplainable. A merged classification is history and is meant to stay.",
                ToDto(row, studentCount: 0));
        }

        var assigned = await AssignmentCountAsync(row.Id, ct);
        var tombstones = await TombstoneCountAsync(row.Id, ct);
        if (assigned > 0 || tombstones > 0) return InUse(row, assigned, tombstones);

        _db.Classifications.Remove(row);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsConstraintConflict(ex))
        {
            // Lost the race described in the remarks. Detached so the failed Remove does not keep the
            // row marked Deleted on this context, which would make a subsequent read on the same
            // context report it as gone.
            _db.Entry(row).State = EntityState.Detached;

            _logger.LogInformation(
                "A delete of classification {ClassificationId} ('{Name}') lost the race to a " +
                "concurrent reference and was answered 409 by the foreign key rather than by the " +
                "pre-check.", row.Id, row.Name);

            return InUse(
                row,
                assigned: await AssignmentCountAsync(row.Id, ct),
                tombstones: await TombstoneCountAsync(row.Id, ct));
        }

        return new ClassificationWriteResponse(
            ClassificationWriteOutcome.Saved,
            $"Classification '{row.Name}' was deleted. Nobody held it and nothing referenced it, " +
            "which is the only circumstance in which deleting one destroys nothing.",
            ToDto(row, studentCount: 0));
    }

    /// <summary>
    /// How many retired classifications name this one as the row that absorbed them. The second half
    /// of the delete guard, kept separate from <see cref="AssignmentCountAsync"/> so the refusal can
    /// say which kind of referrer it found.
    /// </summary>
    private Task<int> TombstoneCountAsync(Guid id, CancellationToken ct) =>
        _db.Classifications.CountAsync(c => c.MergedIntoClassificationId == id, ct);

    // ----------------------------------------------------------------------------------------- merge

    /// <summary>
    /// <inheritdoc cref="IClassificationService.MergeAsync" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both writes run inside one transaction, and that is a departure from
    /// <c>TermAdminService.SetCurrentAsync</c> worth stating.</b> That method got away with no
    /// transaction because it could express its whole change as a single <c>UPDATE</c> against one
    /// table. A merge cannot: it repoints rows in one place and retires a row in another, and a
    /// failure between them is not a harmless window. Retiring without repointing would strand a
    /// population on a row no picker offers.
    /// </para>
    ///
    /// <para>
    /// <b>It goes through <c>CreateExecutionStrategy</c> rather than opening the transaction
    /// directly.</b> The production registration enables <c>EnableRetryOnFailure</c>, and EF refuses a
    /// user-initiated transaction under a retrying execution strategy — an omission that would work on
    /// every developer machine and throw on the deployed host, which is the worst available place to
    /// find out.
    /// </para>
    ///
    /// <para>
    /// <b>Both writes inside the delegate are <c>ExecuteUpdateAsync</c>, and using tracked entities
    /// there would be a real bug rather than a style choice.</b> A retrying strategy re-runs the whole
    /// delegate, and <c>SaveChanges</c> marks its entities <c>Unchanged</c> the moment it succeeds — so
    /// a transaction that committed-then-failed would, on retry, find nothing to write, issue no
    /// statements, commit an empty transaction and report success while the database still held the
    /// pre-merge state. Untracked statements have no such memory: each attempt re-issues
    /// <c>UPDATE ... WHERE Id = @id</c>, which is idempotent, so the delegate is re-entrant by
    /// construction. The rows are read back afterwards for the same reason
    /// <c>TermAdminService.SetCurrentAsync</c> reads back — the caller is told what the database holds
    /// rather than what this method intended.
    /// </para>
    /// </remarks>
    public async Task<ClassificationMergeResponse> MergeAsync(
        Guid id, Guid intoClassificationId, CancellationToken ct = default)
    {
        if (id == intoClassificationId)
        {
            // 400 rather than 409: no state of the database makes this request meaningful, so it is
            // the payload that is wrong. CK_Classifications_NoSelfMerge says the same thing one layer
            // down, and would otherwise surface as a 500.
            return MergeInvalid(
                "A classification cannot be merged into itself. Send the id of the classification " +
                "that should survive as intoClassificationId.");
        }

        // AsNoTracking on both, deliberately: the write below is an ExecuteUpdateAsync inside a
        // retrying execution strategy, and a tracked entity would make the retry unsound — see the
        // remarks.
        var loser = await _db.Classifications.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (loser is null) return MergeNotFound("The classification to merge does not exist.");

        var survivor = await _db.Classifications.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == intoClassificationId, ct);

        if (survivor is null)
        {
            return MergeNotFound(
                "The classification to merge into does not exist. Both ids must name classifications " +
                "in this school.");
        }

        // Explicit, because the two rows are fetched by primary key and a primary-key lookup is not
        // scoped by anything when no tenant is pinned. Merging across schools would move one
        // institution's people onto another institution's category.
        if (loser.SchoolId != survivor.SchoolId)
        {
            return MergeNotFound(
                "Those two classifications belong to different schools. A merge moves people between " +
                "categories and cannot cross a tenant.");
        }

        if (!string.Equals(loser.Axis, survivor.Axis, StringComparison.Ordinal))
        {
            // Checked before the state guards below, because it is a statement about what was asked
            // rather than about the condition of either row: told "that one is retired" first, an
            // operator would reactivate it and try again, and the second attempt would fail for this
            // reason anyway.
            return MergeRefused(
                ClassificationWriteOutcome.CrossAxis,
                loser, survivor,
                $"'{loser.Name}' is on the {loser.Axis} axis and '{survivor.Name}' is on the " +
                $"{survivor.Axis} axis, so they cannot be merged. An axis is one of the source " +
                "export's category columns, and the two answer different questions — merging them " +
                "would not tidy a duplicate, it would overwrite one answer with another. A person may " +
                "hold one classification on each axis at the same time.");
        }

        if (loser.MergedIntoClassificationId is not null)
        {
            return MergeRefused(ClassificationWriteOutcome.NotMergeable,
                loser, survivor,
                "That classification has already been merged into another one. Merging it again would " +
                "build a chain, and everyone it used to describe is already filed under the survivor.");
        }

        if (await TombstoneCountAsync(loser.Id, ct) > 0)
        {
            return MergeRefused(
                ClassificationWriteOutcome.NotMergeable,
                loser, survivor,
                $"'{loser.Name}' has already absorbed another classification, so it cannot itself be " +
                "merged away. That would leave the absorbed row naming a tombstone, and 'where did " +
                "these people go' would take two hops to answer — with the middle row retired and " +
                "invisible.\n\n" +
                $"This is permanent: absorbing a classification fixes '{loser.Name}' as the answer " +
                "for everyone it took in, and nothing undoes that. There is no sequence of merges " +
                "that reaches the state you asked for.\n\n" +
                $"If the goal is to end up with '{loser.Name}' and '{survivor.Name}' as one " +
                $"classification, merge '{survivor.Name}' INTO '{loser.Name}' — the other direction " +
                $"— and then rename '{loser.Name}' if '{survivor.Name}' was the name you wanted.");
        }

        if (survivor.MergedIntoClassificationId is not null)
        {
            return MergeRefused(ClassificationWriteOutcome.NotMergeable,
                loser, survivor,
                "The classification you are merging into has itself been merged away, so it is a " +
                "tombstone rather than a destination. Merge into the classification that absorbed it.");
        }

        if (!survivor.IsActive)
        {
            return MergeRefused(ClassificationWriteOutcome.NotMergeable,
                loser, survivor,
                "The classification you are merging into is retired, so the merge would move a live " +
                "population onto a category no picker offers. Reactivate it first " +
                "(PATCH /classifications/{id}/active) if that is really where these people belong.");
        }

        var now = DateTime.UtcNow;
        var survivorId = survivor.Id;

        // Both assigned inside the retried delegate and both reset at its top, so a retry reports what
        // the winning attempt did rather than accumulating across attempts.
        var repointed = 0;
        ClassificationMergeResponse? raced = null;

        var strategy = _db.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                repointed = 0;
                raced = null;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // 0. Re-establish the survivor's eligibility INSIDE the transaction, as a conditional
            // write rather than a read.
            //
            // The guards above ran AsNoTracking and outside any transaction, so by now they describe
            // the past. The race they miss: merge L into V while somebody else merges V into W. Both
            // callers pass their own guards, both commit, and the result is the two-hop chain this
            // method exists to refuse — with L's people stranded on V, which is by then retired and
            // absent from every picker. CK_Classifications_MergedIsRetired does not catch it, because
            // each row individually ends up in a perfectly legal state.
            //
            // It is an UPDATE and not a SELECT deliberately. A re-read under READ COMMITTED holds no
            // lock once it returns, so it would narrow the window without closing it; a conditional
            // UPDATE tests the predicate and takes an exclusive lock on the survivor's row for the
            // rest of the transaction in one statement. A concurrent merge trying to retire that same
            // survivor blocks until this one commits, and then fails its own condition.
            //
            // Bumping UpdatedAt is not a side effect invented to carry the lock: the survivor really
            // is changing — it is about to absorb a population.
            var survivorStillEligible = await _db.Classifications
                .Where(c => c.Id == survivorId
                         && c.IsActive
                         && c.MergedIntoClassificationId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, now), ct);

            if (survivorStillEligible != 1)
            {
                // No commit — the transaction rolls back on dispose, so nothing partial survives.
                raced = MergeRefused(
                    ClassificationWriteOutcome.NotMergeable,
                    loser, survivor,
                    $"'{survivor.Name}' was retired or merged away by someone else while this merge " +
                    "was being prepared, so it is no longer somewhere a population can be moved to. " +
                    "Nothing was changed. Re-read the vocabulary and merge into whichever " +
                    "classification absorbed it.");
                return;
            }

            // 1. Repoint every assignment from the loser onto the survivor, FIRST — before the
            // retire below — so that a failure between the two steps leaves a live category with a
            // smaller population and a re-runnable merge, rather than a retired category with people
            // stranded on it.
            //
            // ExecuteUpdateAsync rather than a load-and-save: the losing category can hold 20,861
            // rows, and materializing them to change one column would be the N+1 this repository's
            // own lessons are about.
            //
            // Axis is deliberately NOT written, and it does not need to be: the cross-axis guard
            // above means the survivor shares the loser's axis, so the denormalized column already
            // equals the new parent's.
            //
            // It is worth being precise about what that guard does and does not buy, because the
            // earlier version of this comment got it backwards. This UPDATE cannot collide with
            // UX_StudentClassifications_Student_Axis under ANY circumstance: it writes only
            // ClassificationId, so (StudentId, Axis) is bit-identical before and after, and an index
            // on those two columns cannot be violated by a statement that changes neither. The guard
            // is not what makes this safe.
            //
            // What the guard buys is the invariant Axis == Classification.Axis, which the composite
            // foreign key (ClassificationId, Axis) -> Classifications(Id, Axis) now enforces outright.
            // The trap in the old wording: "the index catches collisions" invites a maintainer to
            // allow cross-axis merges and add SetProperty(sc => sc.Axis, survivorAxis) here — and THAT
            // statement can genuinely collide, for a person holding both axes, surfacing as a raw 2601
            // through a method with no unique-violation handler.
            repointed = await _db.StudentClassifications
                .Where(sc => sc.ClassificationId == id)
                .ExecuteUpdateAsync(
                    u => u.SetProperty(sc => sc.ClassificationId, survivorId)
                          .SetProperty(sc => sc.UpdatedAt, now),
                    ct);

            // 2. Retire the loser and record what absorbed it, in one untracked statement. Never a
            // delete: the tombstone is what lets an administrator see a mistaken merge, and what lets
            // a report run against an earlier term still resolve the category it recorded.
            //
            // Conditional on the loser not having been merged already, and the affected row count is
            // checked rather than discarded. Two callers merging the SAME loser into two different
            // survivors would otherwise both succeed, and the second would silently overwrite the
            // first's tombstone — leaving a row that names a survivor its population never went to.
            // Unlike the create and rename paths, which lose their races to an index, and the delete
            // path, which loses its race to a foreign key, there is no constraint standing behind this
            // one: the application-side condition IS the guard.
            // The pre-check above is a courtesy; THIS is the guard, and the second clause is what
            // closes the chain race. SQL Server re-evaluates both after this statement acquires the
            // row's exclusive lock, so a merge that was legal when its guards ran and became illegal
            // while it waited matches zero rows instead of committing — which is precisely the
            // interleaving that built a chain: a concurrent merge INTO this row commits while we are
            // blocked here, and the subquery then sees the tombstone it left behind.
            var retired = await _db.Classifications
                .Where(c => c.Id == id
                         && c.MergedIntoClassificationId == null
                         && !_db.Classifications.Any(t => t.MergedIntoClassificationId == c.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.IsActive, false)
                          .SetProperty(c => c.RetiredAt, (DateTime?)now)
                          .SetProperty(c => c.MergedIntoClassificationId, (Guid?)survivorId)
                          .SetProperty(c => c.UpdatedAt, now),
                    ct);

            if (retired != 1)
            {
                raced = MergeRefused(
                    ClassificationWriteOutcome.NotMergeable,
                    loser, survivor,
                    $"'{loser.Name}' was merged into another classification, or absorbed one, while " +
                    "this merge was being prepared. Nothing was changed — including the assignments " +
                    "this attempt had already repointed, which roll back with it.");
                return;
            }

            await tx.CommitAsync(ct);
            });
        }
        catch (Exception ex) when (SqlServerErrors.IsDeadlockVictim(ex))
        {
            // Two merges wanted the same rows at the same time and SQL Server picked this one to roll
            // back. Nothing was written — that is what being the victim means — so this is a conflict
            // the caller can simply repeat, not a server fault.
            //
            // Caught here rather than left to EnableRetryOnFailure: the production registration would
            // usually retry it, but that makes the answer depend on how the host was composed, and the
            // hosts that do not enable retries are the ones least able to explain a 500.
            _logger.LogInformation(
                "A merge of classification {LoserId} into {SurvivorId} was chosen as a deadlock " +
                "victim and was answered 409 with nothing written.", id, survivorId);

            return MergeRefused(
                ClassificationWriteOutcome.NotMergeable,
                loser, survivor,
                "Another merge was touching the same classifications at the same moment, and this one " +
                "was rolled back to break the tie. Nothing was changed — no assignment moved and no " +
                "row was retired. Re-read the vocabulary and try again.");
        }

        if (raced is not null)
        {
            _logger.LogInformation(
                "A merge of classification {LoserId} into {SurvivorId} lost a race to a concurrent " +
                "merge and was answered 409 with nothing written.", id, survivorId);

            return raced;
        }

        _logger.LogInformation(
            "Classification {LoserId} ('{LoserName}') was merged into {SurvivorId} ('{SurvivorName}'). " +
            "{Repointed} assignment(s) moved. The losing row was retired and tombstoned, not deleted.",
            loser.Id, loser.Name, survivor.Id, survivor.Name, repointed);

        // Read back rather than composed from the entities above: nothing was tracked, and the row the
        // caller is told about should be the row the statement produced. Both are asserted present —
        // neither can have vanished, because this operation deletes nothing, so a null here would be a
        // genuine surprise and is reported as one rather than becoming a quiet 404.
        var mergedRow = await _db.Classifications.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        var survivorRow = await _db.Classifications.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == survivorId, ct);

        if (mergedRow is null || survivorRow is null)
        {
            throw new InvalidOperationException(
                $"A classification vanished during a merge of {id} into {survivorId}. This operation " +
                "retires and tombstones; it never deletes, so neither row can be absent afterwards.");
        }

        return new ClassificationMergeResponse(
            ClassificationWriteOutcome.Saved,
            $"'{mergedRow.Name}' was merged into '{survivorRow.Name}' on the {survivorRow.Axis} " +
            $"axis. {repointed} assignment(s) moved. The losing classification is retired and records " +
            "what absorbed it; no row was deleted.",
            ToDto(survivorRow, await AssignmentCountAsync(survivorId, ct)),
            ToDto(mergedRow, studentCount: 0),
            repointed);
    }

    // --------------------------------------------------------------------------------------- plumbing

    /// <summary>
    /// The <c>Classifications</c> column rules, checked in the service rather than the controller so
    /// the next caller that is not HTTP inherits them — the same placement, and the same reason,
    /// <c>TermAdminService.Validate</c> and <c>DeviceService.Validate</c> record.
    /// </summary>
    private static ClassificationWriteResponse? ValidateName(string name)
    {
        if (ClassificationText.IsValidName(name)) return null;

        return new ClassificationWriteResponse(
            ClassificationWriteOutcome.ValidationFailed,
            $"Name is required, must be {ClassificationText.NameMaxLength} characters or fewer, may " +
            "not start or end with whitespace, and must contain at least one letter or digit. It is " +
            "stored exactly as typed — punctuation and capitalisation included, so " +
            "'SUPERVISORY/MANAGERIAL' keeps its slash — but two names that differ only in " +
            "punctuation, spacing or case are the same classification and the second is refused.",
            null);
    }

    /// <summary>
    /// The duplicate-name pre-check. A courtesy, not the guard: the guard is
    /// <c>UX_Classifications_SchoolId_NameKey</c>, and both write paths catch its violation as well.
    /// What this buys is a message that names the conflict before a row is staged.
    /// </summary>
    private async Task<ClassificationWriteResponse?> TakenKeyAsync(
        Guid schoolId, string nameKey, Guid? excluding, CancellationToken ct)
    {
        var clash = await _db.Classifications.AsNoTracking()
            .Where(c => c.SchoolId == schoolId && c.NameKey == nameKey)
            .Where(c => excluding == null || c.Id != excluding)
            // Projected to a nullable pair rather than to the entity, and to a nullable id rather than
            // to Guid, so "no row" is null instead of Guid.Empty — a default-valued struct cannot be
            // told apart from a real row whose key happens to be that value.
            .Select(c => new { Id = (Guid?)c.Id, c.Name, c.IsActive })
            .FirstOrDefaultAsync(ct);

        if (clash is null) return null;

        // The held-by row is named, and whether it is retired is said out loud. A retired row still
        // occupies its key (the index is unfiltered, deliberately — see ConfigureClassifications), so
        // without this sentence the administrator is told a name is taken by something they cannot
        // find in any picker, and the only recovery they can guess at is a third spelling.
        var held = clash.IsActive
            ? $"'{clash.Name}'"
            : $"'{clash.Name}', which is retired — reactivate it with " +
              $"PATCH /classifications/{clash.Id}/active rather than creating a second row";

        return new ClassificationWriteResponse(
            ClassificationWriteOutcome.NameExists,
            $"This school already has a classification with the key '{nameKey}': {held}. Names are " +
            "compared with punctuation, spacing and case removed, so 'USA FRIARS', 'USA-Friars' and " +
            "'usafriars' are one classification. To combine two that already exist, merge them.",
            null);
    }

    private static ClassificationWriteResponse Duplicate(string name, string nameKey) =>
        new(ClassificationWriteOutcome.NameExists,
            $"A classification with the key '{nameKey}' already exists in this school, so '{name}' " +
            "cannot be added. Names are unique per school once punctuation, spacing and case are " +
            "removed (UX_Classifications_SchoolId_NameKey).",
            null);

    /// <summary>
    /// The refusal, naming the two kinds of referrer separately because they call for different
    /// remedies: people are moved by a merge, whereas a merge tombstone pointing here means this row
    /// is somebody else's history and should simply be left alone.
    /// </summary>
    private static ClassificationWriteResponse InUse(Classification row, int assigned, int tombstones) =>
        new(ClassificationWriteOutcome.InUse,
            $"'{row.Name}' cannot be deleted: {assigned} person(s) are filed under it and " +
            $"{tombstones} merged classification(s) point at it. Deleting it would mean either " +
            "destroying those references or silently blanking them, and both lose data nobody asked " +
            "to lose. Retire it instead " +
            $"(PATCH /classifications/{row.Id}/active with isActive false), which keeps every " +
            "assignment, or merge it into another classification on the same axis " +
            $"(POST /classifications/{row.Id}/merge), which moves them.",
            ToDto(row, assigned));

    private static ClassificationWriteResponse NotFound() =>
        new(ClassificationWriteOutcome.NotFound, "Classification not found.", null);

    private static ClassificationWriteResponse NoSchool() =>
        new(ClassificationWriteOutcome.NoSchoolResolved,
            "No school could be resolved for this classification. In the pre-auth build the tenant is " +
            "the single seeded school (ADR-001 D-6); with none or several, there is nothing to file " +
            "the classification under.",
            null);

    private static ClassificationMergeResponse MergeInvalid(string message) =>
        new(ClassificationWriteOutcome.ValidationFailed, message, null, null, 0);

    private static ClassificationMergeResponse MergeNotFound(string message) =>
        new(ClassificationWriteOutcome.NotFound, message, null, null, 0);

    /// <summary>
    /// A refused merge. Both rows come back so the operator can see what they actually named — the
    /// commonest cause of a cross-axis refusal is picking the right word from the wrong list, and the
    /// axes in the payload are what make that obvious. Counts are zero because nothing was read or
    /// moved: a refusal reports the request, not the population.
    /// </summary>
    private static ClassificationMergeResponse MergeRefused(
        ClassificationWriteOutcome outcome,
        Classification loser, Classification survivor, string message) =>
        new(outcome, message, ToDto(survivor, 0), ToDto(loser, 0), 0);
}
