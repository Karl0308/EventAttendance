namespace EAMS.Application.Dtos;

/// <summary>
/// <b>One card that matched a search, and the person who holds it.</b> The row type of
/// <c>GET /cards?cardUid=</c>.
///
/// <para>
/// <b>The card is the subject and the student is an attribute of it, which is the opposite of every
/// other read on this API and is the whole design.</b> ADR-001 D-3 scopes card uniqueness to
/// <c>UNIQUE(SchoolId, CardUid) WHERE IsActive = 1</c> — deliberately leaving <em>inactive</em> rows
/// unconstrained, because a serial that is revoked today may be re-encoded and issued to somebody else
/// tomorrow and the old row has to survive to explain the taps it produced. A lookup that reaches
/// inactive cards is therefore multi-valued by construction: one UID can legitimately name several
/// cards across several students, and none of them is "the" answer. A shape with a single
/// <c>student</c> on it could not have said so.
/// </para>
/// </summary>
/// <param name="CardId">
/// The card row. <b>This is the identity a caller acts on</b> — it is what
/// <c>DELETE /students/{studentId}/cards/{cardId}</c> takes, so a match is directly actionable rather
/// than something to go and look up again by UID (which is the lookup that is ambiguous).
/// </param>
/// <param name="CardUid">
/// The stored, normalized serial — uppercase with separators stripped. <b>Leading zeros are
/// significant and are never trimmed:</b> the CICSS export writes card numbers as decimal digits
/// (<c>0012503326</c>), so <c>0012503326</c> and <c>12503326</c> are two different cards and both can
/// exist at once.
/// </param>
/// <param name="Label">Whatever the issuing operator wrote on the card, if anything.</param>
/// <param name="IsActive">
/// <c>false</c> is <b>deactivated</b>: the card has been withdrawn and cannot record attendance, but it
/// still names the student it was issued to. QA's answer to Q5 is that this case must resolve — "so
/// admin can still be able to track the card's association with the student" — so a deactivated card is
/// returned as a match rather than hidden, and this flag plus
/// <paramref name="DeactivatedAt"/> is what stops a client mistaking it for a live one.
/// </param>
/// <param name="IssuedAt">When the card was assigned to this student.</param>
/// <param name="DeactivatedAt">
/// When it was withdrawn, or null while it is active. Present because "this card is not in use" and
/// "this card stopped being in use last September" are different answers to the person holding a card
/// they found on the floor.
/// </param>
/// <param name="StudentId">The holder's primary key — what <c>GET /students/{id}</c> takes.</param>
/// <param name="StudentNumber">The registrar's number, verbatim. Not the card serial: separate columns, separate rules.</param>
/// <param name="FullName">The holder's display name.</param>
/// <param name="StudentStatus">
/// The holder's §4.3 status — <c>Active</c>, <c>Inactive</c> or <c>Graduated</c>. A card found on the
/// floor very often belongs to somebody who has already left, and the status is what says so without a
/// second request.
/// </param>
public record CardMatchDto(
    Guid CardId,
    string CardUid,
    string? Label,
    bool IsActive,
    DateTime IssuedAt,
    DateTime? DeactivatedAt,
    Guid StudentId,
    string StudentNumber,
    string FullName,
    string StudentStatus);
