namespace EAMS.Domain;

/// <summary>
/// <b>A member of the institution's personnel</b> — faculty and employees — as the Academic Community's
/// Personnel tab manages them (StudentsEmployees.docx). The parallel of <see cref="Student"/> for the
/// people who run and attend events but are not enrolled.
///
/// <para>
/// <b>Modelled on <see cref="Student"/>, and deliberately simpler in one respect: the card is a single
/// column here, not a <see cref="RfidCard"/> table.</b> A student may hold several cards over time and the
/// history matters for resolving past taps (ADR-001 D-3); a personnel record carries one working
/// <see cref="RfidUid"/>. The spec lists "RFID UID" as a column of the Personnel tab, so that is what it
/// is — normalized by <see cref="CardUid.Normalize"/>, unique per school among live rows, and nullable
/// because a person can exist on the roster before a card is encoded. If personnel ever need card history
/// too, that is an additive change to a table of its own, exactly as the students' is.
/// </para>
///
/// <para>
/// <b>Soft-deleted, like <see cref="Student"/>, rather than hard-deleted.</b> Personnel will be event
/// attendees, and an attendance row must never point at a vanished person — so removal clears
/// <see cref="IsDeleted"/> and the row stays. <see cref="Status"/> is the separate HR distinction
/// (Active/Inactive), the same split students carry.
/// </para>
/// </summary>
public class Personnel : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>The institutional identifier — the roster's Personnel/Employee ID. Unique per school.</summary>
    public string PersonnelNumber { get; set; } = "";

    public string FirstName { get; set; } = "";
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = "";

    public string? Email { get; set; }

    /// <summary>
    /// The person's classification — NAP, ACAD, and the like. A <c>string</c> here rather than a foreign
    /// key to the <see cref="Classification"/> vocabulary: this tab is master data an administrator types
    /// or imports, and wiring it to the person-axis <see cref="StudentClassification"/> machinery is a
    /// separate concern. Stored as authored.
    /// </summary>
    public string? Classification { get; set; }

    public string? Department { get; set; }
    public string? Organization { get; set; }

    /// <summary>The job title/position — a filter field in the spec, so it is a column here.</summary>
    public string? Position { get; set; }

    /// <summary>
    /// The working card serial, normalized by <see cref="CardUid.Normalize"/> (letters and digits,
    /// upper-cased) or null. Unique per school among live rows.
    /// </summary>
    public string? RfidUid { get; set; }

    /// <summary>Active or Inactive — see <see cref="PersonnelStatus"/>.</summary>
    public string Status { get; set; } = PersonnelStatus.Active;

    public bool IsDeleted { get; set; }
}

/// <summary>
/// The personnel HR statuses, as a <c>string</c> with a <c>CHECK</c>-friendly set, exactly like
/// <see cref="AttendanceStatus"/> and <c>StudentStatus</c>.
/// </summary>
public static class PersonnelStatus
{
    public const string Active = "Active";
    public const string Inactive = "Inactive";

    public static readonly IReadOnlyList<string> All = [Active, Inactive];

    /// <summary>Maps any casing onto the canonical spelling; refuses anything else, and null/blank.</summary>
    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// The <c>Personnel</c> column rules, in the domain so the importer that lands later inherits them rather
/// than re-deriving them — the same placement <see cref="ClassificationText"/> and
/// <see cref="EventClassificationText"/> use.
/// </summary>
public static class PersonnelText
{
    public const int NumberMaxLength = 50;
    public const int NameMaxLength = 100;
    public const int EmailMaxLength = 256;
    public const int ClassificationMaxLength = 100;
    public const int DepartmentMaxLength = 150;
    public const int OrganizationMaxLength = 150;
    public const int PositionMaxLength = 150;
    public const int RfidUidMaxLength = 128;

    /// <summary>Present and within length. Not trimmed here — callers decide; the service trims names.</summary>
    public static bool IsPresent(string? value) => !string.IsNullOrWhiteSpace(value);
}
