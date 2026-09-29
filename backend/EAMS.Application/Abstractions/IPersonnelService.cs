using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>The outcome of a personnel write. An enum, for the reason every write surface uses one.</summary>
public enum PersonnelWriteOutcome
{
    /// <summary>The write happened.</summary>
    Saved,

    /// <summary>No such personnel record, or it is soft-deleted. 404.</summary>
    NotFound,

    /// <summary>A field failed a column rule or the status is undocumented. 400; the message names which.</summary>
    ValidationFailed,

    /// <summary><c>IX_Personnel_SchoolId_PersonnelNumber</c> already holds that number in this school. 409.</summary>
    DuplicateNumber,

    /// <summary>Another live personnel record in this school already holds that RFID UID. 409.</summary>
    DuplicateRfid,

    /// <summary>No school could be resolved to file the record under. 409.</summary>
    NoSchoolResolved,
}

/// <summary>The result of a personnel write. <see cref="Personnel"/> is null unless it saved.</summary>
public record PersonnelWriteResponse(PersonnelWriteOutcome Outcome, string Message, PersonnelDto? Personnel);

/// <summary>
/// <b>The Academic Community's Personnel tab (StudentsEmployees.docx) — faculty and employees master
/// data.</b> The parallel of <see cref="IStudentService"/>, tenant-scoped by the global query filter, and
/// gated by the same <c>students.*</c> permissions because it is the same module curated by the same
/// administrator.
///
/// <para>
/// Import and export are a separate, later increment; this is the manual CRUD, filters and list.
/// </para>
/// </summary>
public interface IPersonnelService
{
    /// <summary>
    /// <c>GET /personnel</c> — one page of the school's personnel, active first then by name, filtered.
    /// </summary>
    Task<PagedResult<PersonnelDto>> ListAsync(
        PersonnelListFilter filter, PageRequest page, CancellationToken ct = default);

    /// <summary><c>GET /personnel/{id}</c> — one record, or null when this tenant has none / it is deleted.</summary>
    Task<PersonnelDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary><c>POST /personnel</c> — create a personnel record under the resolved tenant.</summary>
    Task<PersonnelWriteResponse> CreateAsync(PersonnelWriteRequest request, CancellationToken ct = default);

    /// <summary><c>PUT /personnel/{id}</c> — a full replacement of the record's fields.</summary>
    Task<PersonnelWriteResponse> UpdateAsync(
        Guid id, PersonnelWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>DELETE /personnel/{id}</c> — soft-delete: the row is marked deleted and disappears from reads,
    /// but is kept so a future attendance row can never point at a vanished person. Its number and RFID
    /// UID are freed for reuse (both uniqueness indexes are filtered to live rows).
    /// </summary>
    Task<PersonnelWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default);
}
