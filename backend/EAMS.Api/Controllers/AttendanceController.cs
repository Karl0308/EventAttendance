using EAMS.Api.Data;
using EAMS.Api.Domain;
using EAMS.Api.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Api.Controllers;

[ApiController]
[Route("api/v1/attendance")]
public class AttendanceController : ControllerBase
{
    private readonly EamsDbContext _db;
    public AttendanceController(EamsDbContext db) => _db = db;

    private static AttendanceDto ToDto(AttendanceRecord a) => new(
        a.Id, a.EventId, a.StudentId,
        a.Student?.FullName ?? "", a.Student?.StudentNumber ?? "",
        a.CheckInAt, a.CheckOutAt, a.Status, a.CaptureMethod);

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AttendanceDto>>> List(
        [FromQuery] Guid? eventId, [FromQuery] Guid? studentId, [FromQuery] string? status)
    {
        var q = _db.AttendanceRecords.Include(a => a.Student).AsQueryable();
        if (eventId is not null) q = q.Where(a => a.EventId == eventId);
        if (studentId is not null) q = q.Where(a => a.StudentId == studentId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status);
        var list = await q.OrderByDescending(a => a.CheckInAt).ToListAsync();
        return Ok(list.Select(ToDto));
    }

    // POST /attendance/tap — the core capture path (Technical Plan §6.4).
    // Resolves UID→student, validates the event window, upserts idempotently, computes status.
    [HttpPost("tap")]
    public async Task<ActionResult<TapResult>> Tap([FromBody] TapRequest req)
    {
        var when = req.TappedAt ?? DateTime.UtcNow;

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId && !e.IsDeleted);
        if (ev is null) return NotFound(new TapResult(false, "Event not found.", null));
        if (ev.Status != "Open") return BadRequest(new TapResult(false, $"Event is {ev.Status}, not Open.", null));

        var uid = StudentsController.Normalize(req.CardUid);
        var card = await _db.RfidCards.Include(c => c.Student)
            .FirstOrDefaultAsync(c => c.CardUid == uid && c.IsActive);
        if (card?.Student is null)
            return NotFound(new TapResult(false, $"No active card matches UID {uid}.", null));

        var student = card.Student;

        // Idempotency: same device tap id → return the existing record, no duplicate.
        if (!string.IsNullOrWhiteSpace(req.DeviceTapId))
        {
            var dup = await _db.AttendanceRecords.Include(a => a.Student)
                .FirstOrDefaultAsync(a => a.DeviceTapId == req.DeviceTapId);
            if (dup is not null)
                return Ok(new TapResult(true, "Duplicate tap ignored (idempotent).", ToDto(dup)));
        }

        var existing = await _db.AttendanceRecords.Include(a => a.Student)
            .FirstOrDefaultAsync(a => a.EventId == ev.Id && a.StudentId == student.Id);

        if (existing is null)
        {
            var status = when <= ev.StartAt.AddMinutes(ev.GraceMinutes) ? "Present" : "Late";
            var rec = new AttendanceRecord
            {
                EventId = ev.Id, StudentId = student.Id, RfidCardId = card.Id,
                CheckInAt = when, Status = status, CaptureMethod = "Rfid",
                DeviceId = req.DeviceId, DeviceTapId = req.DeviceTapId,
                Student = student,
            };
            _db.AttendanceRecords.Add(rec);
            await _db.SaveChangesAsync();
            return Ok(new TapResult(true, $"Checked in ({status}).", ToDto(rec)));
        }

        // Already present: in TimeInOut mode, a second tap records check-out.
        if (ev.AttendanceMode == "TimeInOut" && existing.CheckOutAt is null)
        {
            existing.CheckOutAt = when;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new TapResult(true, "Checked out.", ToDto(existing)));
        }

        return Ok(new TapResult(true, "Already recorded.", ToDto(existing)));
    }

    // POST /attendance/manual — organizer override (Technical Plan §6.4).
    [HttpPost("manual")]
    public async Task<ActionResult<TapResult>> Manual(
        [FromQuery] Guid eventId, [FromQuery] Guid studentId,
        [FromQuery] string status = "Present", [FromQuery] string? notes = null)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId && !e.IsDeleted);
        if (ev is null) return NotFound(new TapResult(false, "Event not found.", null));
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId && !s.IsDeleted);
        if (student is null) return NotFound(new TapResult(false, "Student not found.", null));

        var rec = await _db.AttendanceRecords.Include(a => a.Student)
            .FirstOrDefaultAsync(a => a.EventId == eventId && a.StudentId == studentId);
        if (rec is null)
        {
            rec = new AttendanceRecord
            {
                EventId = eventId, StudentId = studentId, CheckInAt = DateTime.UtcNow,
                Status = status, CaptureMethod = "Manual", Notes = notes, Student = student,
            };
            _db.AttendanceRecords.Add(rec);
        }
        else
        {
            rec.Status = status;
            rec.Notes = notes;
            rec.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new TapResult(true, "Manual entry saved.", ToDto(rec)));
    }
}
