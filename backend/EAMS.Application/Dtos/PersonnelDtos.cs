namespace EAMS.Application.Dtos;

/// <summary>
/// One personnel record, as <c>GET /personnel</c> and <c>GET /personnel/{id}</c> publish them.
/// </summary>
/// <param name="FullName">Composed by the server (<c>Last, First Middle</c>); read-only.</param>
public record PersonnelDto(
    Guid Id,
    string PersonnelNumber,
    string FullName,
    string FirstName,
    string? MiddleName,
    string LastName,
    string? Email,
    string? Classification,
    string? Department,
    string? Organization,
    string? Position,
    string? RfidUid,
    string Status);

/// <summary>
/// The body of <c>POST /personnel</c> and <c>PUT /personnel/{id}</c> — one type, checked by one method.
/// A full replacement on update, so an edit form fills it from the record it is editing.
/// </summary>
/// <param name="RfidUid">
/// The card serial. Normalized server-side (letters and digits, upper-cased) before it is stored or
/// compared — send it as read; null or blank means "no card". Unique per school among live rows.
/// </param>
/// <param name="Status"><c>Active</c> or <c>Inactive</c>; null or blank takes the <c>Active</c> default.</param>
public record PersonnelWriteRequest(
    string PersonnelNumber,
    string FirstName,
    string? MiddleName,
    string LastName,
    string? Email,
    string? Classification,
    string? Department,
    string? Organization,
    string? Position,
    string? RfidUid,
    string? Status);

/// <summary>
/// The optional filters on <c>GET /personnel</c>. Each is a "contains" fragment except <c>Status</c>,
/// which is exact; all combine with AND, and a blank one applies no filter for that column.
/// </summary>
public record PersonnelListFilter(
    string? PersonnelNumber = null,
    string? RfidUid = null,
    string? Name = null,
    string? Department = null,
    string? Position = null,
    string? Classification = null,
    string? Status = null);

/// <summary>
/// The body of <c>POST /personnel/import</c> — a bulk upsert. Each row is a <see cref="PersonnelWriteRequest"/>
/// (the same shape and rules as a single create/edit); rows are matched to existing records by
/// <c>PersonnelNumber</c> within the school — an existing number updates, a new one creates.
/// </summary>
public record PersonnelImportRequest(IReadOnlyList<PersonnelWriteRequest> Rows);

/// <summary>One row that could not be imported, by its 1-based position in the request.</summary>
public record PersonnelImportErrorDto(int Row, string? PersonnelNumber, string Message);

/// <summary>The outcome of a bulk import — how many rows created, updated, and failed, with per-row reasons.</summary>
public record PersonnelImportResultDto(
    int Total,
    int Created,
    int Updated,
    int Failed,
    IReadOnlyList<PersonnelImportErrorDto> Errors);
