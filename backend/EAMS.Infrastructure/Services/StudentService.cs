using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

internal sealed class StudentService : IStudentService
{
    private readonly EamsDbContext _db;
    public StudentService(EamsDbContext db) => _db = db;

    internal static StudentDto ToDto(Student s) => new(
        s.Id, s.StudentNumber, s.FullName, s.Email, s.Course, s.YearLevel, s.Section, s.Status,
        s.Cards.Select(c => new CardDto(c.Id, c.CardUid, c.Label, c.IsActive)).ToList());

    public async Task<IReadOnlyList<StudentDto>> ListAsync(
        string? search, string? course, string? status, CancellationToken ct = default)
    {
        var q = _db.Students.Include(s => s.Cards).Where(s => !s.IsDeleted);

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(s => s.FirstName.Contains(search) || s.LastName.Contains(search)
                          || s.StudentNumber.Contains(search));
        if (!string.IsNullOrWhiteSpace(course)) q = q.Where(s => s.Course == course);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(s => s.Status == status);

        var list = await q.OrderBy(s => s.LastName).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<StudentDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var s = await _db.Students.Include(x => x.Cards)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        return s is null ? null : ToDto(s);
    }

    public async Task<StudentDto?> GetByCardUidAsync(string cardUid, CancellationToken ct = default)
    {
        var uid = CardUid.Normalize(cardUid);
        var card = await _db.RfidCards.Include(c => c.Student!).ThenInclude(s => s.Cards)
            .FirstOrDefaultAsync(c => c.CardUid == uid && c.IsActive, ct);
        return card?.Student is null ? null : ToDto(card.Student);
    }
}
