using EAMS.Api.Data;
using EAMS.Api.Domain;
using EAMS.Api.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Api.Controllers;

[ApiController]
[Route("api/v1/students")]
public class StudentsController : ControllerBase
{
    private readonly EamsDbContext _db;
    public StudentsController(EamsDbContext db) => _db = db;

    private static StudentDto ToDto(Student s) => new(
        s.Id, s.StudentNumber, s.FullName, s.Email, s.Course, s.YearLevel, s.Section, s.Status,
        s.Cards.Select(c => new CardDto(c.Id, c.CardUid, c.Label, c.IsActive)));

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StudentDto>>> List(
        [FromQuery] string? search, [FromQuery] string? course, [FromQuery] string? status)
    {
        var q = _db.Students.Include(s => s.Cards).Where(s => !s.IsDeleted);

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(s => s.FirstName.Contains(search) || s.LastName.Contains(search)
                          || s.StudentNumber.Contains(search));
        if (!string.IsNullOrWhiteSpace(course)) q = q.Where(s => s.Course == course);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(s => s.Status == status);

        var list = await q.OrderBy(s => s.LastName).ToListAsync();
        return Ok(list.Select(ToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<StudentDto>> Get(Guid id)
    {
        var s = await _db.Students.Include(x => x.Cards).FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        return s is null ? NotFound() : Ok(ToDto(s));
    }

    // GET /students/by-card/{cardUid} — UID→student resolution for the mobile scan screen.
    [HttpGet("by-card/{cardUid}")]
    public async Task<ActionResult<StudentDto>> ByCard(string cardUid)
    {
        var uid = Normalize(cardUid);
        var card = await _db.RfidCards.Include(c => c.Student!).ThenInclude(s => s.Cards)
            .FirstOrDefaultAsync(c => c.CardUid == uid && c.IsActive);
        return card?.Student is null ? NotFound("No active card matches that UID.") : Ok(ToDto(card.Student));
    }

    internal static string Normalize(string uid) =>
        new string(uid.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
