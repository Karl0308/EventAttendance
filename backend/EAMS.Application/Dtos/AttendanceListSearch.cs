using EAMS.Domain;

namespace EAMS.Application.Dtos;

/// <summary>
/// The three text filters on <c>GET /attendance</c> — Live Attendance's search boxes (client QA Q7,
/// MDVault #427/#463): <b>Student No.</b>, <b>Name</b> and <b>RFID card number</b>.
///
/// <para>
/// <b>A request-side shape only.</b> <see cref="AttendanceDto"/> is deliberately untouched: devices
/// receive that same record from <c>POST /attendance/tap</c> and <c>/tap/batch</c>, and its published
/// schema is closed (<c>additionalProperties: false</c>). Filtering happens in the query string, never
/// by widening the row.
/// </para>
///
/// <para>
/// Every member is an optional <em>fragment</em> — a "contains" match — and they combine with AND.
/// Absent or blank means that filter is not applied. The filters only ever narrow attendance rows, so
/// a student with no row for the event can never appear through them ("do not show students who have
/// not yet tapped").
/// </para>
/// </summary>
/// <param name="StudentNumber">
/// A fragment of the registrar's student number, compared verbatim (after trimming the edges). Not
/// card-normalized: <c>2023-0001</c> must go on matching itself, the reasoning
/// <c>StudentService.ListAsync</c> records for its own search.
/// </param>
/// <param name="StudentName">
/// A fragment of the student's name — any one name part, or a run across them in display order
/// (<c>First Last</c> or <c>First Middle Last</c>). Case-insensitive under the database's collation.
/// </param>
/// <param name="CardUid">
/// A fragment of the serial of <b>the card that made the tap</b> — the row's own <c>RfidCardId</c>,
/// not any card the student holds or has held. Normalized with <c>CardUid.Normalize</c> before it is
/// compared, so <c>25-03</c>, <c>25:03</c> and <c>2503</c> are one search. A row with no card (a manual
/// entry, an import-materialized absence) never matches.
/// </param>
public sealed record AttendanceListSearch(string? StudentNumber, string? StudentName, string? CardUid)
{
    /// <summary>
    /// The longest student-number fragment accepted. A fragment longer than the column
    /// (<see cref="StudentText.StudentNumberMaxLength"/>) cannot be contained in any stored value, so
    /// the cap refuses nothing that could have matched.
    /// </summary>
    public const int StudentNumberMaxLength = StudentText.StudentNumberMaxLength;

    /// <summary>
    /// The longest name fragment accepted: three name parts at their column width
    /// (<see cref="StudentText.NameMaxLength"/>) joined by the two spaces of the display form — the
    /// longest string the name filter can compare against.
    /// </summary>
    public const int StudentNameMaxLength = (NamePartsInDisplayForm * StudentText.NameMaxLength)
        + (NamePartsInDisplayForm - 1);

    /// <summary>
    /// The longest <em>raw</em> card fragment accepted. Measured before normalization because that is
    /// what arrives, and reader formats spend a separator per byte (<c>04:A7:B8:C9</c> is eleven
    /// characters for eight stored ones), so the column's own
    /// <see cref="RfidCardText.CardUidMaxLength"/> would refuse a legitimate whole serial written with
    /// separators. Twice the column covers one separator per stored character with room to spare.
    /// </summary>
    public const int CardUidMaxLength = RawToStoredCardUidAllowance * RfidCardText.CardUidMaxLength;

    /// <summary>First, middle, last — the parts <c>Student.FullName</c> joins.</summary>
    private const int NamePartsInDisplayForm = 3;

    /// <summary>See <see cref="CardUidMaxLength"/>.</summary>
    private const int RawToStoredCardUidAllowance = 2;

    /// <summary>No filter at all — the list as it was before Q7.</summary>
    public static AttendanceListSearch None { get; } = new(null, null, null);
}
