using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>Technical Plan §6.2. Implemented in EAMS.Infrastructure; controllers see only this.</summary>
public interface IStudentService
{
    Task<IReadOnlyList<StudentDto>> ListAsync(
        string? search, string? course, string? status, CancellationToken ct = default);

    Task<StudentDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>UID→student resolution for the mobile scan screen. Normalizes <paramref name="cardUid"/> first.</summary>
    Task<StudentDto?> GetByCardUidAsync(string cardUid, CancellationToken ct = default);
}
