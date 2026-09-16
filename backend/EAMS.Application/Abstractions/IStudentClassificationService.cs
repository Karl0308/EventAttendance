using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// Why the outcome is an enum rather than an exception, and why every member maps to exactly one HTTP
/// status in the controller: the same reasoning <see cref="ClassificationWriteOutcome"/>,
/// <see cref="StudentWriteOutcome"/> and <see cref="TermWriteOutcome"/> record. The service owns the
/// decision; the controller owns only the translation.
/// </summary>
public enum StudentClassificationWriteOutcome
{
    /// <summary>The write happened, or the request asked for a state the person was already in.</summary>
    Saved,

    /// <summary>No such student, or it is soft-deleted. 404.</summary>
    StudentNotFound,

    /// <summary>No classification with that id in this tenant. 404.</summary>
    ClassificationNotFound,

    /// <summary>
    /// The student and the classification belong to different schools. 404.
    ///
    /// <para>
    /// Both ids resolve, so this is not <see cref="ClassificationNotFound"/> as a matter of fact — but
    /// it is as a matter of what the caller may know, and the same 404 with the same explicit message
    /// <c>ClassificationService.MergeAsync</c> gives for a cross-school merge. Filing one institution's
    /// person under another's category is not a conflict to resolve; it is a request about a pair that
    /// does not exist.
    /// </para>
    /// </summary>
    CrossSchool,

    /// <summary>
    /// The classification is retired, so it is not offered for new assignments. 409.
    ///
    /// <para>
    /// A conflict rather than a 400: the payload is well formed and would be accepted the moment the
    /// classification is reactivated (<c>PATCH /classifications/{id}/active</c>). Note that this cannot
    /// fire for somebody who <em>already</em> holds it — re-sending an assignment they have is a no-op
    /// that answers <see cref="Saved"/>, so an edit form that round-trips what it read keeps working
    /// after a retirement.
    /// </para>
    /// </summary>
    ClassificationRetired,

    /// <summary>
    /// The classification was merged into another one, so it is a tombstone rather than a category. 409.
    ///
    /// <para>
    /// Separated from <see cref="ClassificationRetired"/> because the remedy is different and a client
    /// can act on it without asking anyone: everyone this row described already sits under the survivor,
    /// so the thing to assign is the survivor, and the message names it. Reactivating a merged row is
    /// refused outright by the vocabulary surface for the same reason.
    /// </para>
    /// </summary>
    ClassificationMerged,

    /// <summary>
    /// <c>DELETE</c> named a classification this person does not hold. 404 — the URL claims they do.
    ///
    /// <para>
    /// A second <c>DELETE</c> of the same assignment lands here, deliberately. It matches
    /// <c>DELETE /students/{id}</c>, and it is the honest answer: by then the row is invisible to every
    /// read in the system, and reporting success for something the caller can no longer see is the
    /// misleading one.
    /// </para>
    /// </summary>
    NotAssigned,

    /// <summary>
    /// <b>Another request was changing this person's classifications at the same moment, and this one
    /// did not happen. 409, and nothing was written.</b>
    ///
    /// <para>
    /// Three ways to get here, all meaning the same thing to a caller — nothing of yours landed, re-read
    /// and decide again:
    /// </para>
    ///
    /// <list type="number">
    ///   <item>Two assignments onto an <em>empty</em> axis slot. Decided by
    ///   <c>UX_StudentClassifications_Student_Axis</c> rather than by this service remembering to check
    ///   — the same "check first, catch anyway" every other write path here uses, because the check and
    ///   the insert are two statements.</item>
    ///   <item>Two assignments onto an <em>occupied</em> one. No index can object — <c>(StudentId,
    ///   Axis)</c> is unchanged by either write — so the guard is the <c>ClassificationId</c> predicate
    ///   in the replacement's <c>WHERE</c>, and the loser is the one whose zero rows matched.</item>
    ///   <item><b>A deadlock.</b> An assign reaches the row by <c>(StudentId, Axis)</c> and a clear by
    ///   <c>(StudentId, ClassificationId)</c>, so the two take the unique and clustered indexes in
    ///   opposite orders; SQL Server rolls one back. <b>A deadlocked <em>clear</em> answers this too</b>
    ///   rather than <see cref="NotAssigned"/>: it never discovered the row missing, it discovered
    ///   nothing at all, and a 404 would assert the person does not hold something they still do.</item>
    /// </list>
    ///
    /// <para>
    /// <b>None of the three is retried automatically.</b> For the first two the callers disagree about
    /// what this person <em>is</em>, and silently letting the later one win is how one operator's
    /// correction disappears without either of them seeing it. The deadlock could safely be retried, and
    /// the production host's <c>EnableRetryOnFailure</c> often will — but the answer must not depend on
    /// how the host was composed, so the service gives one a caller can act on either way.
    /// </para>
    /// </summary>
    ConcurrentAssignment,
}

/// <summary>
/// The result of a write against one person's classifications.
/// </summary>
/// <param name="Classifications">
/// <b>The person's full set after the write, not just the row that changed</b>, and null unless it
/// saved. A multi-axis picker has to re-render all of it anyway — the point of the axes is that a write
/// to one leaves the others alone, and the only way a client can <em>see</em> that is to be told all of
/// them.
/// </param>
/// <param name="ReplacedClassificationId">
/// What this assignment displaced on its axis, if anything.
///
/// <para>
/// <b>The field exists because the replacement is otherwise silent.</b> A person holds one
/// classification per axis, so assigning <c>ACAD</c> to somebody who is <c>NAP</c> removes <c>NAP</c> —
/// correct, requested, and completely invisible in a response that shows only the new state. An
/// operator who thought they were adding a second personnel category needs to see which one went.
/// </para>
/// </param>
public record StudentClassificationWriteResponse(
    StudentClassificationWriteOutcome Outcome,
    string Message,
    StudentClassificationsDto? Classifications,
    Guid? ReplacedClassificationId);

/// <summary>
/// <b>Who is classified as what.</b> The write surface the classification vocabulary deliberately shipped
/// without.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is separate from <see cref="IClassificationService"/>.</b> That interface owns the
/// <em>vocabulary</em> — the eight categories an administrator renames, retires and merges — and its own
/// documentation records that assignment was left out because the shape was an open question. This is
/// the answer to that question, and it is a different subject with a different lifecycle: the vocabulary
/// outlives every person filed under it, and an assignment is a fact about a person.
/// </para>
///
/// <para>
/// <b>Why it is separate from <see cref="IStudentService"/>.</b> That service's every write path loads a
/// tracked <c>Student</c> and mutates columns a caller owns, and it carries a guard
/// (<c>AcademicCacheWriteException</c>) that fires when anything in its unit of work touches the
/// ADR-001 D-2 derived cache. An assignment writes a different table entirely and touches no
/// <c>Students</c> column; folding it in would have put a second subject inside a class whose
/// documentation opens by saying it is built around one idea.
/// </para>
///
/// <para>
/// <b>The axis is never a parameter, on any operation here.</b> It is read off the classification the
/// caller named, because <c>StudentClassifications.Axis</c> is denormalized from
/// <c>Classifications.Axis</c> and the two must stay equal — the composite foreign key
/// <c>(ClassificationId, Axis)</c> → <c>Classifications(Id, Axis)</c> makes a disagreeing row
/// unwritable, so a caller-supplied axis could only ever be right or be a 547. Taking it from the parent
/// through <see cref="EAMS.Domain.ClassificationAssignment.For"/> means the question never arises, and
/// means Phase 1b's importer establishes the same invariant the same way.
/// </para>
///
/// <para>
/// <b>What Phase 1b reuses, and what it does not.</b> The importer resolves thousands of roster rows and
/// saves once; these methods read and save per person, so it cannot call them in a loop without paying a
/// round trip per student. It reuses the two decisions instead —
/// <see cref="EAMS.Domain.ClassificationAssignment.For"/> for the axis and
/// <see cref="EAMS.Domain.ClassificationAssignment.IsAssignable"/> for "may this category still receive
/// people" — which is the whole of the invariant. That split is deliberate: the part that must not be
/// re-derived lives in the domain, and the part that legitimately differs (one unit of work per person
/// versus one per batch) is left to differ.
/// </para>
///
/// <para>
/// <b>There is no assignment history and this interface does not pretend otherwise.</b> Replacing a
/// person's classification on an axis re-points the existing row, so
/// <see cref="StudentClassificationDto.AssignedAt"/> resets and what they used to be is gone. An audit
/// trail of who changed it and when belongs in <c>AuditLogs</c>, where it can record the operator; a
/// second timestamp column that sometimes means "since" and sometimes means "since the last edit" would
/// be the worse of the two.
/// </para>
/// </remarks>
public interface IStudentClassificationService
{
    /// <summary>
    /// <c>GET /students/{studentId}/classifications</c> — everything this person is classified as, or
    /// null when there is no such student.
    ///
    /// <para>
    /// <b>Retired classifications are included.</b> Retiring withdraws a category from pickers and
    /// leaves every assignment standing, so filtering them out here would make an edit form show fewer
    /// classifications than the person actually has — and then save that.
    /// </para>
    /// </summary>
    Task<StudentClassificationsDto?> GetAsync(Guid studentId, CancellationToken ct = default);

    /// <summary>
    /// <c>PUT /students/{studentId}/classifications/{classificationId}</c> — <b>give this person this
    /// classification, replacing whatever they held on its axis.</b>
    ///
    /// <para>
    /// <b>Idempotent.</b> Assigning a classification somebody already holds changes nothing, does not
    /// bump an audit column, and answers <see cref="StudentClassificationWriteOutcome.Saved"/> — so a
    /// double-submitted form is safe, and so is re-sending a set that a retirement has since touched.
    /// </para>
    ///
    /// <para>
    /// <b>Replacement within an axis is the whole semantic, and it is why this is a <c>PUT</c> on the
    /// assignment rather than a <c>POST</c> to a collection.</b> A person holds at most one
    /// classification per axis (<c>UX_StudentClassifications_Student_Axis</c>), so "add <c>ACAD</c>" to
    /// somebody who is <c>NAP</c> cannot mean "have both" — it means <c>ACAD</c> instead. The displaced
    /// row is reported in <see cref="StudentClassificationWriteResponse.ReplacedClassificationId"/>
    /// rather than left for the caller to notice.
    /// </para>
    ///
    /// <para>
    /// Assignments on <em>other</em> axes are untouched. That is the case the junction table exists for,
    /// and the response returns the person's whole set so a client can see it held.
    /// </para>
    /// </summary>
    Task<StudentClassificationWriteResponse> AssignAsync(
        Guid studentId, Guid classificationId, CancellationToken ct = default);

    /// <summary>
    /// <c>DELETE /students/{studentId}/classifications/{classificationId}</c> — <b>take this
    /// classification off this person. The classification itself is untouched.</b>
    ///
    /// <para>
    /// <b>It removes the junction row and nothing else</b>, which is the only thing "clearing" can
    /// safely mean: the vocabulary entry is shared by everybody filed under it, and a clear that reached
    /// it would uncategorise every one of them. Deleting the classification is a different operation on
    /// a different surface, and it is refused outright while anybody holds it.
    /// </para>
    ///
    /// <para>
    /// A classification this person does not hold is
    /// <see cref="StudentClassificationWriteOutcome.NotAssigned"/> — a 404, including on a second
    /// delete. The classification's own state is irrelevant here: a retired or merged-away category can
    /// always be cleared, or somebody could be permanently stuck holding one.
    /// </para>
    /// </summary>
    Task<StudentClassificationWriteResponse> ClearAsync(
        Guid studentId, Guid classificationId, CancellationToken ct = default);
}
