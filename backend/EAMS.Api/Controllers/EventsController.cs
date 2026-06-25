using EAMS.Api.Data;
using EAMS.Api.Domain;
using EAMS.Api.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Api.Controllers;

[ApiController]
[Route("api/v1/events")]
public class EventsController : ControllerBase
{
    private readonly EamsDbContext _db;
    public EventsController(EamsDbContext db) => _db = db;

    private static EventDto ToDto(Event e) => new(
        e.Id, e.Name, e.Location, e.StartAt, e.EndAt, e.AttendanceMode, e.GraceMinutes, e.Status);

    [HttpGet]
    public async Task<ActionResult<IEnumerable<EventDto>>> List([FromQuery] string? status)
    {
        var q = _db.Events.Where(e => !e.IsDeleted);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(e => e.Status == status);
        var list = await q.OrderByDescending(e => e.StartAt).ToListAsync();
        return Ok(list.Select(ToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EventDto>> Get(Guid id)
    {
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        return e is null ? NotFound() : Ok(ToDto(e));
    }

    // GET /events/{id}/summary — report aggregation from the Technical Plan §6.7 / §12.
    [HttpGet("{id:guid}/summary")]
    public async Task<ActionResult<EventSummaryDto>> Summary(Guid id)
    {
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (e is null) return NotFound();

        var records = await _db.AttendanceRecords.Where(a => a.EventId == id).ToListAsync();
        int present = records.Count(r => r.Status == "Present");
        int late = records.Count(r => r.Status == "Late");
        int absent = records.Count(r => r.Status == "Absent");
        int excused = records.Count(r => r.Status == "Excused");

        // No registration list in the core slice — use recorded students as the denominator.
        int expected = records.Count;
        double rate = expected == 0 ? 0 : Math.Round((double)(present + late) / expected * 100, 1);

        return Ok(new EventSummaryDto(e.Id, e.Name, expected, present, late, absent, excused, rate));
    }
}
