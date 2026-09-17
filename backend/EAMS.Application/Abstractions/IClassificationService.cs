using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// Why the outcome is an enum rather than an exception, and why every member maps to exactly one HTTP
/// status in the controller: the same reasoning <see cref="TermWriteOutcome"/>,
/// <see cref="StudentWriteOutcome"/> and <see cref="DeviceWriteOutcome"/> record. The service owns the
/// decision; the controller owns only the translation.
/// </summary>
public enum ClassificationWriteOutcome
{
    /// <summary>The write happened, or the request asked for a state the row was already in.</summary>
    Saved,

    /// <summary>No classification with that id in this tenant. 404.</summary>
    NotFound,

    /// <summary>
    /// The payload is wrong under every circumstance — a blank, over-length or whitespace-padded name,
    /// a name with no letter or digit in it, an omitted <c>isActive</c> or <c>intoClassificationId</c>,
    /// or a merge of a classification into itself. 400, and the message names the field.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// <c>UX_Classifications_SchoolId_NameKey</c> already holds this name's normalized key in this
    /// school. 409 — including when the collision is only discovered by the index, because two
    /// concurrent creates of one name is a conflict and not a server fault.
    ///
    /// <para>
    /// <b>The key, not the name, is what collides</b>, and the message says so. <c>USA FRIARS</c> and
    /// <c>USA-Friars</c> both normalize to <c>USAFRIARS</c>, so an administrator told "that name is
    /// taken" by a row that does not look identical needs to be told why — otherwise the natural next
    /// move is to try a third spelling, which collides too.
    /// </para>
    /// </summary>
    NameExists,

    /// <summary>
    /// A new classification cannot be filed against a school. 409. The same refusal
    /// <c>TermAdminService</c>, <c>StudentService</c>, <c>EventService</c> and <c>DeviceService</c>
    /// make, through the same helper.
    /// </summary>
    NoSchoolResolved,

    /// <summary>
    /// <b>The delete was refused because something still points at the row.</b> 409, and this member is
    /// the whole of the no-data-loss acceptance criterion.
    ///
    /// <para>
    /// A delete that cascaded, or that nulled the referring column to make itself succeed, would be a
    /// data-loss migration wearing a CRUD costume — it destroys assignments the operator never asked to
    /// destroy, and there is nothing to restore from. So the row survives and the caller is told to
    /// retire it (<c>PATCH /classifications/{id}/active</c>) or merge it
    /// (<c>POST /classifications/{id}/merge</c>) instead, both of which keep every assignment.
    /// </para>
    /// </summary>
    InUse,

    /// <summary>
    /// The merge cannot be performed as asked, because of the state of one of the two rows rather than
    /// the shape of the request. 409.
    ///
    /// <para>
    /// Three cases, all of which would otherwise produce a chain nothing can resolve: the surviving
    /// classification is retired (the merge would move a live population onto a row no picker offers),
    /// the surviving classification has itself already been merged away (a two-hop tombstone), or the
    /// classification being merged has already been merged into something.
    /// </para>
    /// </summary>
    NotMergeable,

    /// <summary>
    /// <b>The two classifications sit on different axes.</b> 409.
    ///
    /// <para>
    /// It is not a near-miss of <see cref="NotMergeable"/>; it is a different mistake with a different
    /// remedy, so it gets a code a client can branch on. The others say "this row is in the wrong
    /// state, fix the state"; this one says the request confuses two unrelated questions — merging
    /// <c>NAP</c> (what staff role someone has) into <c>STUDENT</c> (whether they are enrolled) does
    /// not tidy a duplicate, it deletes an answer to one question by overwriting it with the answer to
    /// another.
    /// </para>
    ///
    /// <para>
    /// <b>And it is what makes the repoint safe.</b> Because a loser and its survivor always share an
    /// axis, <c>UX_StudentClassifications_Student_Axis</c> guarantees no person can hold both — so the
    /// repoint cannot collide with a row they already have. Allow a cross-axis merge and every merge
    /// needs conflict handling for a case that currently cannot arise.
    /// </para>
    /// </summary>
    CrossAxis,

    /// <summary>
    /// <b>The delete was refused because the startup seed would put the row straight back.</b> 409, and
    /// the caller is told to retire it instead.
    ///
    /// <para>
    /// It is deliberately not <see cref="InUse"/>, which is the near neighbour. Nothing references a
    /// seeded row an administrator wants gone — that is exactly the case where the delete would have
    /// succeeded — so "still in use" would send them looking for a holder that does not exist. The
    /// refusal here is about what happens after the next restart, not about what points at the row
    /// today, and a client that wants to say so has to be able to tell the two apart.
    /// </para>
    ///
    /// <para>
    /// The reasoning, and the reason retiring is the answer rather than a consolation, is on
    /// <c>ClassificationService.DeleteAsync</c>.
    /// </para>
    /// </summary>
    SeedProtected,
}

/// <summary>The result of a write against one classification. Null unless it saved.</summary>
public record ClassificationWriteResponse(
    ClassificationWriteOutcome Outcome, string Message, ClassificationDto? Classification);

/// <summary>
/// The result of a merge. Both rows are returned when it succeeds, because the operator needs to see
/// both halves of what happened: the survivor is what the population is now filed under, and the
/// retired row is the tombstone proving nothing was deleted.
/// </summary>
/// <param name="StudentsRepointed">
/// How many assignments moved onto the survivor. <b>The number an operator needs to sanity-check a
/// merge they cannot undo</b> — "moved 271 people" against an expectation of three is the only signal
/// that the wrong pair was named, and it arrives while they still remember what they clicked.
/// </param>
public record ClassificationMergeResponse(
    ClassificationWriteOutcome Outcome,
    string Message,
    ClassificationDto? Survivor,
    ClassificationDto? Merged,
    int StudentsRepointed);

/// <summary>
/// <b>The administrator's vocabulary of classifications — <c>STUDENT</c>, <c>NAP</c>, <c>ACAD</c> and
/// the five other values the client's access-control export carries.</b>
///
/// <para>
/// <b>Why an editable table exists at all.</b> QA answered Q2 (MDVault #404): the list is <em>not</em>
/// fixed, and "Admin can edit them". Every other enum-ish value in this schema is a <c>string</c> the
/// code owns and a new member is a code change; this one is owned by the institution, and its eight
/// starting values are not a design — they are whatever their export happened to contain, spelling and
/// all.
/// </para>
///
/// <para>
/// <b>This interface is the vocabulary and nothing else.</b> How a <em>person</em> is assigned a
/// classification is deliberately absent, and its absence is the current state of an open architecture
/// question rather than an oversight: the source has four category axes and at least three real people
/// carry two classifications at once, so a single scalar column would return a plausible, non-empty,
/// wrong answer — the same failure ADR-001 D-2 documents for the <c>Course</c>/<c>Section</c> cache.
/// Assignment lands in its own additive change once that is ruled on. <b>The vocabulary is correct
/// under either outcome, which is why it ships first.</b>
/// </para>
///
/// <para>
/// <b>Three operations keep data and one can destroy it, so only one of them is guarded.</b> Renaming
/// (<see cref="RenameAsync"/>) touches one row and no assignment, because identity is the GUID.
/// Retiring (<see cref="SetActiveAsync"/>) takes the row out of pickers and leaves every assignment
/// standing. Merging (<see cref="MergeAsync"/>) moves assignments onto a survivor and retires the
/// loser without deleting it. <see cref="DeleteAsync"/> is the only one that removes a row, and it is
/// refused outright while anything references it.
/// </para>
/// </summary>
public interface IClassificationService
{
    /// <summary>
    /// <c>GET /classifications</c> — the vocabulary, active entries first and then by name.
    /// </summary>
    /// <param name="includeRetired">
    /// <c>false</c> — the default a picker wants — returns only entries still offered for new
    /// assignments. <c>true</c> returns retired and merged-away rows as well, which is what an
    /// administration screen and any historical report need: a retired classification still describes
    /// the people who carry it, so a report that filtered it out would under-count them silently.
    /// </param>
    /// <param name="page">Paged like every other admin list read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<ClassificationDto>> ListAsync(
        bool includeRetired, PageRequest page, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /classifications/{id}</c> — one entry, or null when this tenant has no such row.
    /// </summary>
    Task<ClassificationDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /classifications</c> — add a classification to the resolved tenant's vocabulary.
    ///
    /// <para>
    /// It is always created active. There is no "create it retired" — a vocabulary entry nobody may use
    /// is not a thing an administrator sets out to make, and offering it would mean a second way to
    /// reach the state <see cref="SetActiveAsync"/> already owns.
    /// </para>
    /// </summary>
    Task<ClassificationWriteResponse> CreateAsync(
        ClassificationCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PUT /classifications/{id}</c> — change the display name.
    ///
    /// <para>
    /// <b>This is the operation the whole table exists for, and it is safe by construction.</b> A
    /// student's classification is held by GUID, so renaming <c>SUPERVISORY/MANAGERIAL</c> changes one
    /// row and reassigns nobody. Had the vocabulary been a denormalized string column, this would have
    /// been a mass <c>UPDATE</c> with no way back.
    /// </para>
    ///
    /// <para>
    /// A rename onto a name whose normalized key another classification in the school already holds is
    /// <see cref="ClassificationWriteOutcome.NameExists"/>, decided by
    /// <c>UX_Classifications_SchoolId_NameKey</c> rather than by this method remembering to check.
    /// <b>Two entries that should be one are merged, not renamed onto each other.</b>
    /// </para>
    ///
    /// <para>
    /// A retired classification can be renamed. Fixing the spelling of a category that is no longer
    /// offered is exactly as legitimate as fixing a live one — the people filed under it still have to
    /// be described correctly in a report.
    /// </para>
    /// </summary>
    Task<ClassificationWriteResponse> RenameAsync(
        Guid id, ClassificationRenameRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PATCH /classifications/{id}/active</c> — retire a classification, or bring it back.
    ///
    /// <para>
    /// <b>Retiring is the safe half of "delete", and it is always available.</b> The row stops being
    /// offered for new assignments and every existing assignment is untouched. That is the acceptance
    /// criterion in one sentence: <em>a retired classification stops appearing in pickers but existing
    /// assignments survive</em>.
    /// </para>
    ///
    /// <para>
    /// <b>Reactivation exists because the uniqueness index is unfiltered.</b> A retired
    /// <c>NAP</c> still occupies the key <c>NAP</c>, so an administrator who retired one by mistake
    /// cannot simply re-create it — they would be told the name is taken, by a row they cannot see in
    /// the picker. Without this route that is a dead end.
    /// </para>
    ///
    /// <para>
    /// Idempotent: retiring a retired classification, or reactivating a live one, changes nothing and
    /// answers <see cref="ClassificationWriteOutcome.Saved"/>.
    /// </para>
    /// </summary>
    /// <param name="isActive">
    /// <c>false</c> retires. <c>true</c> brings it back — <b>except</b> for a row that was merged away,
    /// which answers <see cref="ClassificationWriteOutcome.NotMergeable"/>: its population has already
    /// moved to the survivor, so reactivating it would offer an empty category that looks like the one
    /// people remember. Un-merging is a new merge in the other direction, not a flag flip.
    /// </param>
    Task<ClassificationWriteResponse> SetActiveAsync(
        Guid id, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// <c>DELETE /classifications/{id}</c> — <b>a guarded hard delete: it removes the row only when
    /// nothing references it, and refuses with
    /// <see cref="ClassificationWriteOutcome.InUse"/> otherwise.</b>
    ///
    /// <para>
    /// <b>Why a hard delete exists at all, when <c>Terms</c> deliberately has none.</b> A term always
    /// has an import behind it; a classification does not. An administrator who typo'd <c>STUDNET</c>
    /// into the vocabulary thirty seconds ago wants it gone, not retired forever in a list of eight
    /// real categories — and a retire-only surface would grow permanent clutter that every picker then
    /// has to filter. The unreferenced case is the only case where deleting destroys nothing, so it is
    /// the only case that is allowed.
    /// </para>
    ///
    /// <para>
    /// <b>Why it is refused rather than made to work.</b> The two ways to make a delete "succeed" while
    /// something references the row are a cascade and a null-out, and both destroy assignments the
    /// operator never asked to destroy — they are the hard-rule data-loss migration, issued one HTTP
    /// verb at a time. The refusal names what to do instead: retire it, or merge it into the
    /// classification that should have absorbed it.
    /// </para>
    ///
    /// <para>
    /// <b>The database refuses it a second time, independently.</b> Every foreign key in this model is
    /// <c>DeleteBehavior.Restrict</c>, so a future referrer that this service has not been taught about
    /// produces a loud constraint violation rather than a silent orphan.
    /// </para>
    /// </summary>
    Task<ClassificationWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <c>POST /classifications/{id}/merge</c> — <b>collapse two classifications into one. Assignments
    /// move; nothing is deleted.</b>
    ///
    /// <para>
    /// <b>Administrators will need this, and that is a fact about the source rather than a nice-to-have.</b>
    /// The seeded vocabulary came out of an access-control export that spells things inconsistently, and
    /// the first thing anyone does with an editable list is add a row that turns out to duplicate one
    /// already there. Without a merge the only recoveries are "retire one and leave the population
    /// split" or "delete it", which the guard correctly refuses.
    /// </para>
    ///
    /// <para>
    /// <b>What it does, in order, in one transaction.</b> Every assignment on
    /// <paramref name="id"/> is repointed onto <paramref name="intoClassificationId"/>; then
    /// <paramref name="id"/> is retired and stamped with
    /// <c>MergedIntoClassificationId</c>. The order is not incidental: repointing first means that a
    /// failure between the two steps leaves a live classification with a smaller population and a
    /// re-runnable merge, whereas retiring first would leave assignments stranded on a row no picker
    /// offers.
    /// </para>
    ///
    /// <para>
    /// <b>The losing row is retired, never deleted</b>, and that is the acceptance criterion: an
    /// administrator who merged the wrong pair can see exactly which row went where, and a report run
    /// against last semester still resolves the classification it recorded.
    /// </para>
    /// </summary>
    /// <param name="id">The classification being collapsed. It ends up retired and tombstoned.</param>
    /// <param name="intoClassificationId">
    /// The survivor. It must be a live, un-merged classification in the same school — merging into a
    /// retired or already-merged row is
    /// <see cref="ClassificationWriteOutcome.NotMergeable"/>, because it would move a live population
    /// somewhere no picker offers or build a chain nothing resolves.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClassificationMergeResponse> MergeAsync(
        Guid id, Guid intoClassificationId, CancellationToken ct = default);
}
