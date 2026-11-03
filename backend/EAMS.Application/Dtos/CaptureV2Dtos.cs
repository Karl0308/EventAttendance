namespace EAMS.Application.Dtos;

/// <summary>
/// <c>AttendanceDtoV2.personType</c>'s two values (ADR-015 D-99). Always present on a v2 record.
/// </summary>
public static class AttendeePersonType
{
    public const string Student = "Student";
    public const string Personnel = "Personnel";

    public static readonly IReadOnlyList<string> All = [Student, Personnel];

    /// <summary>
    /// Maps any casing of a value (edges trimmed) onto its canonical spelling; refuses anything else, and
    /// null/blank. The <c>GET /attendance?personType=</c> filter reads it.
    /// </summary>
    public static bool TryNormalize(string? value, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        var match = All.FirstOrDefault(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;

        canonical = match;
        return true;
    }
}

/// <summary>
/// Who already holds a card UID, carried on the duplicate-card 409 as the ProblemDetails extension
/// <c>holder</c> (ADR-015 D-98, JJ 2026-11-02 #706 F2) so the admin SPA can say "This card is already
/// assigned to &lt;name / number&gt;." Admin card routes only; never on a capture route.
/// </summary>
/// <param name="PersonType"><c>Student</c> or <c>Personnel</c> — <see cref="AttendeePersonType"/>.</param>
/// <param name="Number">The student number or personnel number.</param>
/// <param name="Name">The display name, "First Middle Last", as <c>Student.FullName</c> / <c>Personnel.FullName</c>.</param>
public sealed record CardUidHolderDto(string PersonType, string Number, string Name);

/// <summary>
/// The v2 attendance record (ADR-015 D-99): every <see cref="AttendanceDto"/> member, in the same order,
/// plus who the attendee is.
///
/// <para>
/// <b>The v1 records are never extended.</b> <see cref="AttendanceDto"/>, <see cref="TapResult"/>,
/// <see cref="TapBatchRowResult"/> and <see cref="TapBatchResult"/> stay byte-identical; a client that did
/// not send <c>X-EAMS-Capabilities: personnel</c> keeps receiving them, and a personnel record can never
/// be expressed in them — the v1 mapper answers <c>CardNotFound</c> instead.
/// </para>
/// </summary>
/// <param name="StudentId">Null on a personnel record.</param>
/// <param name="StudentName">Null on a personnel record.</param>
/// <param name="StudentNumber">Null on a personnel record.</param>
/// <param name="PersonType"><c>Student</c> or <c>Personnel</c>, always present.</param>
/// <param name="PersonnelId">Always present; null on a student record.</param>
/// <param name="PersonnelNumber">Always present; null on a student record.</param>
/// <param name="PersonnelName">Always present; null on a student record.</param>
public record AttendanceDtoV2(
    Guid Id, Guid EventId, Guid? StudentId, string? StudentName, string? StudentNumber,
    DateTime? CheckInAt, DateTime? CheckOutAt, string Status, string CaptureMethod,
    string PersonType, Guid? PersonnelId, string? PersonnelNumber, string? PersonnelName);

/// <summary>The v2 <see cref="TapResult"/>: identical but for the record's type.</summary>
public record TapResultV2(
    bool Success, string Message, AttendanceDtoV2? Record, string Code, DateTime ServerTime);

/// <summary>The v2 <see cref="TapBatchRowResult"/>: identical but for the record's type.</summary>
public record TapBatchRowResultV2(
    int Index, string? DeviceTapId, string Code, int Status, AttendanceDtoV2? Record, string Message);

/// <summary>The v2 <see cref="TapBatchResult"/>: identical but for the rows' type.</summary>
public record TapBatchResultV2(
    int Accepted, int Rejected, DateTime ServerTime, IReadOnlyList<TapBatchRowResultV2> Results);

/// <summary>
/// The v2 manifest (ADR-015 D-99): every <see cref="EventManifestDto"/> member, in the same order, plus
/// <see cref="Personnel"/>. Only a client that sent <c>X-EAMS-Capabilities: personnel</c> receives it; the
/// v1 record and its <c>version</c> stay exactly as they were.
/// </summary>
/// <param name="Version">
/// A <c>version</c> over this body, prefixed <see cref="EventManifestVersion.ContractShapePrefixV2"/>, so it
/// never equals a v1 version and a client that changes capability never revalidates against the other
/// family's cached body.
/// </param>
/// <param name="Personnel">
/// Always present, possibly empty. <c>Active</c>, not-deleted personnel in this event's audience only (the
/// D-95 countable set), ordered by <c>personnelId</c>. Offline validation against it is display-only, as it
/// is for <see cref="EventManifestDto.Attendees"/>: the server rules on every tap.
/// </param>
public record EventManifestDtoV2(
    EventManifestEventDto Event,
    IReadOnlyList<EventManifestGroupDto> Groups,
    IReadOnlyList<EventManifestAttendeeDto> Attendees,
    DateTime ServerTime,
    string Version,
    IReadOnlyList<EventManifestPersonnelDto> Personnel);

/// <summary>
/// One expected personnel member on a device, and <b>exactly these four members</b> (ADR-015 D-99, #705
/// guardrail 2). A device holds no email, position, classification, department, organization or status:
/// a stolen handset must carry the minimum a scan screen needs.
/// </summary>
/// <param name="PersonnelId">The identity to key the offline index by. Stable.</param>
/// <param name="PersonnelNumber">The institutional number. Never a tap identity (D-43).</param>
/// <param name="FullName">Prose. Display only.</param>
/// <param name="CardUids">
/// Normalized like <see cref="EventManifestAttendeeDto.CardUids"/>. Personnel hold at most one UID today, so
/// this has zero or one member; it is a list so a device indexes both populations the same way.
/// </param>
public record EventManifestPersonnelDto(
    Guid PersonnelId,
    string PersonnelNumber,
    string FullName,
    IReadOnlyList<string> CardUids);

/// <summary>
/// <c>GET /events/{id}/by-card/{cardUid}</c> (ADR-015 D-99): who holds this card, for one <c>Open</c> event.
/// Every member is always present, <c>null</c> where it does not apply.
///
/// <para>
/// <b>Deliberately not a school-wide lookup.</b> A personnel member is returned only if countable for this
/// event, so a device cannot enumerate staff cards. There is no status, email or department here for the
/// same reason there is none on the manifest.
/// </para>
/// </summary>
/// <param name="PersonType"><c>Student</c> or <c>Personnel</c>.</param>
/// <param name="CardUids">
/// The holder's active, normalized serials: every active card for a student, the one UID for personnel.
/// </param>
public record EventCardHolderDto(
    string PersonType,
    Guid? StudentId,
    string? StudentNumber,
    Guid? PersonnelId,
    string? PersonnelNumber,
    string FullName,
    IReadOnlyList<string> CardUids);
