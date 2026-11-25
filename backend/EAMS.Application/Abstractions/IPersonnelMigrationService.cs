using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// The result of <see cref="IPersonnelMigrationService.MigrateFromStudentsAsync"/>. <see cref="Result"/> is
/// null unless <see cref="Outcome"/> is <see cref="PersonnelWriteOutcome.Saved"/>; per-student conflicts are
/// rows inside a saved result, never a failed outcome.
/// </summary>
public record PersonnelMigrationResponse(
    PersonnelWriteOutcome Outcome, string Message, MigrateFromStudentsResultDto? Result);

/// <summary>
/// <b>Moves people who were imported as students into Personnel</b> (JJ, 2026-11-25): staff whose student
/// number starts with a prefix (7200…) were enrolled as students with active cards, and since ADR-015 D-98
/// the personnel import refuses their UIDs. Per student: upsert the personnel record by number, move the
/// active card's UID onto it, and soft-delete the student — under the same card-UID lock every other card
/// write takes. Past attendance stays on the student id.
/// </summary>
public interface IPersonnelMigrationService
{
    /// <summary>
    /// <c>POST /personnel/migrate-from-students</c>. A dry run (the default) writes nothing and returns the
    /// per-student plan; a run applies it in chunks and is idempotent — a moved student is soft-deleted, so
    /// a re-run no longer matches it.
    /// </summary>
    Task<PersonnelMigrationResponse> MigrateFromStudentsAsync(
        MigrateFromStudentsRequest request, CancellationToken ct = default);
}
