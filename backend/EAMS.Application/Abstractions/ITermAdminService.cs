using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// Why the outcome is an enum rather than an exception, and why every member maps to exactly one HTTP
/// status in the controller: the same reasoning <see cref="StudentWriteOutcome"/> and
/// <see cref="DeviceWriteOutcome"/> record. The service owns the decision; the controller owns only the
/// translation.
/// </summary>
public enum TermWriteOutcome
{
    /// <summary>The write happened, or the request asked for a state the row was already in.</summary>
    Saved,

    /// <summary>No term with that id in this tenant. 404.</summary>
    NotFound,

    /// <summary>
    /// A field failed a <c>Terms</c> column rule — a blank or over-length code, school year or
    /// semester, a value carrying surrounding whitespace, an end date before the start date, or a
    /// <c>PATCH /current</c> body that did not say which way. 400, and the message names the field.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// <c>UX_Terms_SchoolId_Code</c> already holds this code in this school. 409 — including when the
    /// collision is only discovered by the index, because two concurrent creates of one code is a
    /// conflict and not a server fault.
    ///
    /// <para>
    /// This is the operator's "cannot be duplicate" (D-53), and it is the reason a pre-check alone is
    /// not the implementation: the read and the insert are two statements, and the second operator to
    /// press Create in the same second wins or loses on the index rather than on the check.
    /// </para>
    /// </summary>
    TermCodeExists,

    /// <summary>
    /// A new term cannot be filed against a school. 409. Only reachable in the pre-auth build with no
    /// tenant pinned and zero or several <c>Schools</c> rows; Phase 6 makes it unreachable by resolving
    /// the tenant from claims before the request gets this far. The same refusal
    /// <c>StudentService</c>, <c>EventService</c> and <c>DeviceService</c> make, through the same
    /// helper.
    /// </summary>
    NoSchoolResolved,
}

/// <summary>The result of a write against one term. <paramref name="Term"/> is null unless it saved.</summary>
public record TermWriteResponse(TermWriteOutcome Outcome, string Message, TermDto? Term);

/// <summary>
/// <b>The admin write surface over <c>Terms</c> — and over nothing else in the academic layer
/// (D-53).</b>
///
/// <para>
/// <b>Why this is a second interface rather than three more methods on
/// <see cref="IAcademicReferenceService"/>.</b> That interface's reads-only stance is a decision with a
/// reason attached: the §10 roster import owns colleges, programmes, courses and offerings, so a
/// hand-authored row there is matched by the importer on its normalized key and either silently
/// overwritten or duplicated into a second row that splits the enrollments. D-53's carve-out is
/// narrower than reversing that. The importer only ever <em>reads</em> <c>Terms</c> — it takes a
/// <c>TermId</c> as input (ADR-001 D-5) and writes no row of that table — so a term authored by hand
/// has nothing to lose a conflict to. Widening the reference service would have made its stated reason
/// false for five entity families in order to change one; a separate abstraction keeps both statements
/// true, and keeps "which academic tables are writable" answerable by looking at a type name.
/// </para>
///
/// <para>
/// <b>A term is the one academic row that has no source in the roster file.</b> Every import requires
/// one to exist first, and until this surface landed nothing in the product could create one — the
/// documented procedure was hand-written SQL against <c>dbo.Terms</c> (see the project <c>CLAUDE.md</c>),
/// which is not a procedure an operator can be given.
/// </para>
///
/// <para>
/// <b>There is no delete, and its absence is the decision.</b> A term with a batch imported against it
/// cannot be removed without taking that batch's enrolments, offerings and term records with it, which
/// the global no-data-loss rule forbids. Retiring a term is
/// <see cref="SetCurrentAsync"/> with <c>false</c>.
/// </para>
/// </summary>
public interface ITermAdminService
{
    /// <summary>
    /// <c>POST /academic/terms</c> — create a term in the resolved tenant's school.
    ///
    /// <para>
    /// The new term is never current. Making it current is <see cref="SetCurrentAsync"/>, so that the
    /// filtered unique index is moved by the one operation written to move it — see
    /// <see cref="TermWriteRequest"/> for why the flag is not on the create body at all.
    /// </para>
    /// </summary>
    Task<TermWriteResponse> CreateAsync(TermWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PUT /academic/terms/{id}</c> — a full replacement of the term's own authored fields.
    ///
    /// <para>
    /// <b><c>Code</c> is editable, deliberately.</b> It is a display value the operator typed, and the
    /// single most likely edit anyone makes here is fixing a typo in it. A rename onto a code another
    /// term in the school already holds is <see cref="TermWriteOutcome.TermCodeExists"/>, decided by
    /// <c>UX_Terms_SchoolId_Code</c> rather than by this method remembering to check.
    /// </para>
    ///
    /// <para>
    /// <b>What a rename does not update</b>, stated because it is invisible from here: derived
    /// <c>StudentGroup</c> display names embed the term code at projection time
    /// (<c>"BSFS 2-A (2025-2026-1)"</c>), so groups projected before a rename keep the old text until
    /// the next import re-runs the projection. Nothing resolves an audience through that text — the
    /// groups are keyed on ids — so this is stale wording rather than a wrong invitation.
    /// </para>
    ///
    /// <para>
    /// <b>Two terms swapping codes is the sharp edge of that.</b> Rename <c>2025-2026-1</c> to
    /// something free, rename <c>2025-2026-2</c> onto <c>2025-2026-1</c>, and the first term's derived
    /// groups still read <c>"BSFS 2-A (2025-2026-1)"</c> while a different, existing term now answers to
    /// that code — no longer merely out of date, but naming a real term the group does not belong to.
    /// The ids still resolve correctly, so nothing selects the wrong students; the label is what
    /// misleads, and an operator reading it would act on it. Re-run the import for both terms after a
    /// swap, or do the swap through a free intermediate code and stop there.
    /// </para>
    ///
    /// <para>
    /// It does not touch <c>SchoolId</c> or <c>IsCurrent</c>; the second is
    /// <see cref="SetCurrentAsync"/>.
    /// </para>
    /// </summary>
    Task<TermWriteResponse> UpdateAsync(
        Guid id, TermWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>PATCH /academic/terms/{id}/current</c> — move the current-term flag onto this term, or clear
    /// it.
    ///
    /// <para>
    /// <b>Its own route rather than a field on the PUT, for the same reason
    /// <c>PATCH /events/{id}/status</c> is.</b> <c>IsCurrent</c> is not a property of the row, it is a
    /// claim about the school: <c>UX_Terms_SchoolId_Current</c> caps it at one term per school, so
    /// setting it is a two-row operation — the incumbent has to be cleared in the same breath — and a
    /// PUT that carried the flag would be one resource's payload silently rewriting a different
    /// resource. The failure that shape produces is the one the index exists to prevent: a body that
    /// merely echoed <c>isCurrent: true</c> back on a save would either be a no-op nobody notices or a
    /// duplicate-key 500, depending on which term was current at the time.
    /// </para>
    ///
    /// <para>
    /// Idempotent. Setting a term that is already current, or clearing one that is not, changes nothing
    /// and answers <see cref="TermWriteOutcome.Saved"/>; the postcondition holds either way, so a
    /// retried click is safe.
    /// </para>
    /// </summary>
    /// <param name="isCurrent">
    /// <c>true</c> makes this the school's current term, clearing whichever term held the flag.
    /// <c>false</c> retires this term, leaving the school with no current term — an ordinary state the
    /// read surface already answers for, and the only retirement D-53 offers since deletion would be
    /// data loss.
    /// </param>
    Task<TermWriteResponse> SetCurrentAsync(
        Guid id, bool isCurrent, CancellationToken ct = default);
}
