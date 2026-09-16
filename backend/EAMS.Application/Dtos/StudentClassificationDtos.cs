namespace EAMS.Application.Dtos;

/// <summary>
/// <b>One classification a person holds, on one axis.</b>
/// </summary>
/// <param name="ClassificationId">
/// The vocabulary entry. <b>Identity is the GUID, never the name</b> — that is the whole reason the
/// vocabulary is a table, and it is why renaming <c>SUPERVISORY/MANAGERIAL</c> reassigns nobody.
/// </param>
/// <param name="Name">The display form, exactly as authored — slash, spacing and capitalisation intact.</param>
/// <param name="Axis">
/// Which of the source export's four category columns this classification came from — <c>Student</c>,
/// <c>Personnel</c>, <c>Friars</c> or <c>Special</c>.
///
/// <para>
/// <b>It is the slot, not a label.</b> <c>UX_StudentClassifications_Student_Axis</c> caps a person at
/// one classification per axis while leaving them free to hold several axes at once, so assigning a
/// second <c>Personnel</c> value <em>replaces</em> the first and assigning a <c>Friars</c> value does
/// not. A picker that does not group on this cannot tell a client which of its choices are mutually
/// exclusive.
/// </para>
/// </param>
/// <param name="IsActive">
/// Whether the classification is still offered for <em>new</em> assignments. <b><c>false</c> here is
/// ordinary and must be rendered, not filtered.</b> Retiring a classification deliberately leaves every
/// existing assignment standing, so a person can hold a withdrawn category indefinitely — and an edit
/// form that dropped it from its own display would blank it on the next save.
/// </param>
/// <param name="AssignedAt">
/// <b>When this person was filed under <em>this</em> classification on this axis — not when they first
/// acquired the axis.</b>
///
/// <para>
/// Replacing a classification within an axis re-points the existing row and <b>resets this stamp</b>, so
/// it always describes the assignment that is actually there. The alternative was live for one gate
/// round and was wrong in a way nothing would have caught: the replace wrote <c>ClassificationId</c> and
/// left <c>CreatedAt</c> alone, so somebody who had been <c>NAP</c> since 2024 and was made <c>ACAD</c>
/// this morning reported <c>assignedAt: 2024-03-01</c> for a category that had touched them minutes
/// earlier — a plausible, non-empty, wrong date.
/// </para>
///
/// <para>
/// <b>What it is therefore not: a history.</b> The previous classification's dates are gone, because
/// there is no assignment history table and this column is not a stand-in for one. Who changed it and
/// when belongs in <c>AuditLogs</c>, where the operator can be recorded too.
/// </para>
/// </param>
public record StudentClassificationDto(
    Guid ClassificationId,
    string Name,
    string Axis,
    bool IsActive,
    DateTime AssignedAt);

/// <summary>
/// <b>Everything one person is classified as.</b> The body of
/// <c>GET /students/{studentId}/classifications</c>, and the payload every write on that surface returns.
/// </summary>
/// <remarks>
/// <b>A list rather than a field, and that is the decision the junction table was built for.</b> QA
/// answered Q2 that classification is a multiple selection — one value per axis, across several axes —
/// and three people in the sampled export carry two at once. A scalar <c>classification</c> could only
/// ever have named one of them, non-empty and plausible and wrong, which is exactly what ADR-001 D-2
/// documents for the <c>Course</c>/<c>Section</c> cache.
/// </remarks>
/// <param name="StudentId">The person.</param>
/// <param name="Classifications">
/// What they hold, ordered by axis then by name. Empty is an ordinary answer — most people will hold
/// exactly one until Phase 1b's import runs.
/// </param>
public record StudentClassificationsDto(
    Guid StudentId, IReadOnlyList<StudentClassificationDto> Classifications);
