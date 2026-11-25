namespace EAMS.Application.Dtos;

/// <summary>
/// The body of <c>POST /personnel/migrate-from-students</c>.
/// </summary>
/// <param name="Prefix">
/// Students whose trimmed student number starts with this are moved. Null or blank takes
/// <see cref="DefaultPrefix"/>. Trimmed; at least <see cref="MinPrefixLength"/> characters.
/// </param>
/// <param name="DryRun">True (the default, also when omitted) previews and writes nothing.</param>
public record MigrateFromStudentsRequest(string? Prefix = MigrateFromStudentsRequest.DefaultPrefix, bool DryRun = true)
{
    /// <summary>JJ, 2026-11-25: the staff numbers imported as students are 7200xxxxxx.</summary>
    public const string DefaultPrefix = "7200";

    /// <summary>
    /// The shortest prefix accepted. A one- or two-character prefix would sweep most of the roster into
    /// Personnel in one call, so it is refused rather than previewed.
    /// </summary>
    public const int MinPrefixLength = 4;
}

/// <summary>
/// What happened (or, on a dry run, would happen) to one matched student. The values of
/// <see cref="MigrateFromStudentsRowDto.Outcome"/>.
/// </summary>
public static class MigrateFromStudentsOutcome
{
    /// <summary>A new personnel record is created with the student's details and card.</summary>
    public const string Create = "Create";

    /// <summary>A personnel record with this number exists; its values are kept and only an empty RFID UID is filled.</summary>
    public const string Update = "Update";

    /// <summary>The student is left untouched; <see cref="MigrateFromStudentsRowDto.ConflictCode"/> says why.</summary>
    public const string Conflict = "Conflict";
}

/// <summary>Why a matched student was left untouched. The values of <see cref="MigrateFromStudentsRowDto.ConflictCode"/>.</summary>
public static class MigrateFromStudentsConflict
{
    /// <summary>The student is linked to an event that is not Closed or Cancelled (attendance, audience or pre-registration).</summary>
    public const string ActiveEvent = "ActiveEvent";

    /// <summary>The card's UID is already held by a different live personnel record.</summary>
    public const string UidHeldByOtherPersonnel = "UidHeldByOtherPersonnel";

    /// <summary>The existing personnel record with this number already holds a different UID.</summary>
    public const string PersonnelHasDifferentCard = "PersonnelHasDifferentCard";

    /// <summary>The student holds more than one active card; a personnel record carries one.</summary>
    public const string MultipleActiveCards = "MultipleActiveCards";

    /// <summary>The student has no first or last name, which a personnel record requires.</summary>
    public const string NameMissing = "NameMissing";

    /// <summary>A value breaks a personnel column rule (length); the message names which.</summary>
    public const string Invalid = "Invalid";

    /// <summary>Another write changed this student, card or personnel number mid-run. Nothing was written; run again.</summary>
    public const string ConcurrentChange = "ConcurrentChange";
}

/// <summary>One matched student and its outcome.</summary>
/// <param name="RfidUid">The normalized UID of the student's active card, or null when it has none.</param>
/// <param name="ConflictCode">A <see cref="MigrateFromStudentsConflict"/> value; null unless <c>Outcome</c> is <c>Conflict</c>.</param>
/// <param name="PersonnelId">The personnel record written to; null on a dry-run create and on a conflict.</param>
public record MigrateFromStudentsRowDto(
    Guid StudentId,
    string StudentNumber,
    string FullName,
    string? RfidUid,
    string Outcome,
    string? ConflictCode,
    string Message,
    Guid? PersonnelId);

/// <summary>
/// The outcome of a migration call. On a dry run the counts are what a run would do.
/// </summary>
/// <param name="Matched">Live students whose trimmed number starts with <c>Prefix</c>.</param>
public record MigrateFromStudentsResultDto(
    bool DryRun,
    string Prefix,
    int Matched,
    int Created,
    int Updated,
    int Conflicts,
    IReadOnlyList<MigrateFromStudentsRowDto> Rows);
