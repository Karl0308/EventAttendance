using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// Why the outcome is an enum rather than an exception or a bare bool — the same reasoning
/// <see cref="EventWriteOutcome"/> records: the write surface has several non-exceptional failures
/// that map to different HTTP statuses, and the service owns the decision while the controller owns
/// only the translation.
/// </summary>
public enum StudentWriteOutcome
{
    /// <summary>The write happened, or the request asked for a state the row was already in.</summary>
    Saved,

    /// <summary>No such student, or it is soft-deleted. 404.</summary>
    NotFound,

    /// <summary>
    /// A field failed a §4.3 rule — a blank or over-length student number or name, an over-length
    /// e-mail, an undocumented <c>Status</c>, a card UID that normalizes to nothing. 400, and the
    /// message names the field.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// The request supplied one of the ADR-001 D-2 derived cache columns
    /// (<c>Course</c>/<c>YearLevel</c>/<c>Section</c>). 400.
    ///
    /// <para>
    /// <b>Refused rather than ignored, which is the decision worth recording.</b> Dropping the field
    /// silently is what a deserializer does by default and it is the worse answer: the caller receives
    /// a 200 and believes it has set a student's section, while the academic tables — the actual source
    /// of truth — say something else. The divergence then surfaces months later as a report that is
    /// quietly wrong for the 23% of students who sit in more than one section. A named 400 tells the
    /// client to write <c>Enrollments</c> instead, on the day it is cheap to learn.
    /// </para>
    ///
    /// <para>
    /// It is also what <c>AcademicCacheWriteException</c> becomes if it ever escapes the service's own
    /// write path — that exception is a 500 by nature and must not be one here.
    /// </para>
    /// </summary>
    FieldIsDerived,

    /// <summary>
    /// <c>UNIQUE(SchoolId, StudentNumber)</c> already holds this number. 409 — including when the
    /// collision is only discovered by the index, because two concurrent creates of one number is a
    /// conflict and not a server fault.
    ///
    /// <para>
    /// The index is unfiltered, so a <em>soft-deleted</em> student still occupies its number. The
    /// message says which case it is; both are 409 because both mean "pick another number or restore
    /// the row you already have".
    /// </para>
    /// </summary>
    DuplicateStudentNumber,

    /// <summary>
    /// The card UID is already active on a different student in this school —
    /// <c>UX_RfidCards_SchoolId_CardUid_Active</c> (ADR-001 D-3). 409.
    ///
    /// <para>
    /// Adding a UID that is already active <em>on the same student</em> is not this: it is a no-op that
    /// answers <see cref="Saved"/>, so a retried form submission is safe.
    /// </para>
    /// </summary>
    CardUidInUse,

    /// <summary>No such card on this student. 404 — the card id is named by the URL.</summary>
    CardNotFound,

    /// <summary>
    /// A new student cannot be filed against a school. 409. Only reachable in the pre-auth build with
    /// no tenant pinned and zero or several <c>Schools</c> rows; Phase 6 makes it unreachable by
    /// resolving the tenant from claims before the request gets this far.
    /// </summary>
    NoSchoolResolved,
}

/// <summary>The result of a write against one student. <paramref name="Student"/> is null unless it saved.</summary>
public record StudentWriteResponse(StudentWriteOutcome Outcome, string Message, StudentDto? Student);

/// <summary>
/// Why <see cref="IStudentService.SearchCardsAsync"/> answers with an outcome rather than an empty page:
/// "no card matched" and "that is not a fragment anything could match" are different answers and only
/// one of them is a 200.
/// </summary>
public enum CardSearchOutcome
{
    /// <summary>The search ran. The page may still be empty — that is an ordinary answer.</summary>
    Matched,

    /// <summary>
    /// <b>The fragment carries no letter or digit, so it normalizes to the empty string.</b> 400.
    ///
    /// <para>
    /// It is refused rather than run, and the reason is not tidiness. Stored UIDs are normalized, so a
    /// search has to normalize before it compares (CLAUDE.md's rule) — and the normalized form of
    /// <c>'-'</c>, <c>'::'</c> or <c>'   '</c> is <c>""</c>, which <c>LIKE '%%'</c> matches for
    /// <em>every card in the school</em>. A caller who typed a separator by accident would be handed
    /// the entire card registry, one page at a time, as if it were a result.
    /// </para>
    /// </summary>
    FragmentUnusable,
}

/// <summary>
/// The result of a card lookup. <paramref name="Matches"/> is null unless the search actually ran.
/// </summary>
/// <param name="Matches">
/// <b>A page of matches, never a single card, and that is load-bearing rather than defensive.</b> See
/// <see cref="CardMatchDto"/>: inactive cards are deliberately outside
/// <c>UX_RfidCards_SchoolId_CardUid_Active</c>, so one UID can name several cards held by several
/// people. A caller that wants "the current holder" filters on <c>isActive</c>; the API does not pick
/// for them.
/// </param>
public record CardSearchResponse(
    CardSearchOutcome Outcome, string Message, PagedResult<CardMatchDto>? Matches);

/// <summary>The result of a write against one card. <paramref name="Card"/> is null unless it saved.</summary>
public record StudentCardResponse(StudentWriteOutcome Outcome, string Message, CardDto? Card);

/// <summary>Technical Plan §6.2. Implemented in EAMS.Infrastructure; controllers see only this.</summary>
public interface IStudentService
{
    /// <summary>
    /// §6.2 <c>GET /students</c> — "Paged list", as the plan says at §6.2 and as this did not do until
    /// Phase 3b-3. Ordered by last name, then by <c>Id</c>: the tiebreaker is not decoration, it is
    /// what stops two students sharing a surname from swapping places between page 1 and page 2 and
    /// being served twice or not at all.
    /// </summary>
    /// <param name="search">
    /// Matches first name, last name, student number <b>or any card serial the student holds</b>,
    /// active or not.
    ///
    /// <para>
    /// <b>The card arm normalizes the fragment before it compares and the name arms do not</b>, because
    /// the two columns store different things: <c>RfidCards.CardUid</c> holds the uppercased,
    /// punctuation-stripped form, so <c>25-03</c> has to become <c>2503</c> to find <c>0012503326</c>
    /// (QA Q6), while <c>StudentNumber</c> holds the registrar's value verbatim and normalizing it would
    /// stop <c>2023-0001</c> matching itself.
    /// </para>
    /// </param>
    Task<PagedResult<StudentDto>> ListAsync(
        string? search, string? course, string? status, PageRequest page,
        CancellationToken ct = default);

    Task<StudentDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>UID→student resolution for the mobile scan screen. Normalizes <paramref name="cardUid"/> first.</summary>
    Task<StudentDto?> GetByCardUidAsync(string cardUid, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /cards?cardUid=</c> — <b>"whose card is this?", answered honestly when the answer is more
    /// than one person.</b>
    ///
    /// <para>
    /// <b>It is not a variant of <see cref="GetByCardUidAsync"/> and must not be folded into one.</b>
    /// That method serves the kiosk: it resolves a whole UID to the one student who may tap with it
    /// right now, so it filters <c>IsActive</c> and a single student is the correct shape. This one
    /// serves an administrator holding a card, and QA answered (Q5) that a <em>withdrawn</em> card must
    /// still name its student. The moment inactive rows are in scope the answer is multi-valued by
    /// construction — ADR-001 D-3 constrains only the active ones — so this returns a page of
    /// <see cref="CardMatchDto"/> and lets the caller see which matched and what state each is in.
    /// </para>
    ///
    /// <para>
    /// <b>Substring, not equality</b> (QA Q6: typing <c>2503</c> must surface <c>0012503326</c>), and
    /// therefore a <c>LIKE '%fragment%'</c>, which no index can seek — see the implementation for what
    /// that costs and why it is accepted at this volume.
    /// </para>
    /// </summary>
    /// <param name="cardUid">
    /// A whole serial or any fragment of one, in any reader format. Normalized before comparison, so
    /// <c>25-03</c>, <c>25:03</c> and <c>2503</c> are one search. A fragment that normalizes to nothing
    /// is <see cref="CardSearchOutcome.FragmentUnusable"/> rather than a match-everything.
    /// </param>
    /// <param name="page">Paged like every other admin list read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CardSearchResponse> SearchCardsAsync(
        string? cardUid, PageRequest page, CancellationToken ct = default);

    /// <summary>
    /// §6.2 <c>POST /students</c>. The student is created in the resolved tenant's school; the request
    /// does not get to name one. See <see cref="StudentWriteRequest"/> for what a caller may and may
    /// not supply.
    /// </summary>
    Task<StudentWriteResponse> CreateAsync(
        StudentWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// §6.2 <c>PUT /students/{id}</c>. A full replacement of the student's own editable fields; it does
    /// not touch <c>SchoolId</c>, <c>IsDeleted</c>, the §10 import columns, or the ADR-001 D-2 derived
    /// cache.
    ///
    /// <para>
    /// <b>The tracked entity is loaded and mutated — never attached and <c>Update()</c>d.</b> Attaching
    /// a detached student populates <c>OriginalValues</c> from the entity's own current values, so a
    /// changed <c>Section</c> would arrive with <c>Current == Original</c>; the cache guard's own
    /// comment names <c>db.Students.Update(entity)</c>, "the shape a REST PUT handler takes", as the
    /// path it was written to catch, and <c>AcademicCacheGuardTests</c> pins it shut.
    /// </para>
    /// </summary>
    Task<StudentWriteResponse> UpdateAsync(
        Guid id, StudentWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// §6.2 <c>DELETE /students/{id}</c> — soft, per §4.3's <c>IsDeleted</c>. Attendance rows are left
    /// exactly where they are: every FK in this model is <c>Restrict</c>, and an attendance trail must
    /// not vanish because someone tidied a roster.
    ///
    /// <para>
    /// A second delete is a 404. By then the student is invisible to every read in the system, and
    /// reporting success for a row the caller can no longer see would be the misleading answer — the
    /// line <c>EventService.DeleteAsync</c> already draws.
    /// </para>
    /// </summary>
    Task<StudentWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// §6.2 <c>POST /students/{id}/cards</c> — assign an RFID card.
    ///
    /// <para>
    /// <b>Idempotent for the same student.</b> Re-posting a UID that is already active on this student
    /// changes nothing and answers <see cref="StudentWriteOutcome.Saved"/> with the existing card, so a
    /// double-submitted form is safe. The same UID active on a <em>different</em> student is
    /// <see cref="StudentWriteOutcome.CardUidInUse"/>, decided by
    /// <c>UX_RfidCards_SchoolId_CardUid_Active</c> rather than by this method remembering to check.
    /// </para>
    /// </summary>
    Task<StudentCardResponse> AddCardAsync(
        Guid id, StudentCardRequest request, CancellationToken ct = default);

    /// <summary>
    /// §6.2 <c>DELETE /students/{id}/cards/{cardId}</c> — <b>deactivate, never delete</b>.
    ///
    /// <para>
    /// ADR-001 D-3 is the whole reason: <c>AttendanceRecords.RfidCardId</c> points at the row that
    /// produced a past tap, and a serial that is revoked today may be issued again tomorrow — to the
    /// same student on a re-encoded card, or to a different one when a serial is recycled. Removing the
    /// row would destroy the issuance history and make "which physical card was presented" unanswerable
    /// — which is precisely what the filtered unique index was introduced to keep possible.
    /// </para>
    ///
    /// <para>
    /// Deactivating an already-inactive card is a no-op that answers
    /// <see cref="StudentWriteOutcome.Saved"/>; the postcondition holds either way, so a retry is safe.
    /// </para>
    /// </summary>
    Task<StudentCardResponse> DeactivateCardAsync(
        Guid id, Guid cardId, CancellationToken ct = default);
}
