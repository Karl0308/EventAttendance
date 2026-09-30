using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// The Clearance Checker — see <see cref="IClearanceService"/>. Reads are bounded to the caller's school
/// by the global query filter. Every fetch writes a <c>clearance.check</c> audit row.
/// </summary>
internal sealed class ClearanceService : IClearanceService
{
    /// <summary>The audit action a clearance check leaves behind — a constant so an operator can search it.</summary>
    internal const string AuditAction = "clearance.check";

    private readonly EamsDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ClearanceService(EamsDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<ClearanceReportDto?> GetReportAsync(
        Guid studentId, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct = default)
    {
        var student = await _db.Students.AsNoTracking()
            .Where(s => s.Id == studentId && !s.IsDeleted)
            .Select(s => new
            {
                s.Id, s.SchoolId, s.StudentNumber, s.FirstName, s.MiddleName, s.LastName,
                s.Course, s.YearLevel, s.Section,
            })
            .FirstOrDefaultAsync(ct);

        if (student is null) return null;

        // The college the student's current-term programme belongs to, best-effort — null when there is
        // no current-term record to resolve it from. Department and Program both come from the roster's
        // cached Course (the spec's sample shows them equal).
        var college = await _db.StudentTermRecords.AsNoTracking()
            .Where(str => str.StudentId == studentId && str.Term!.IsCurrent && str.College != null)
            .Select(str => str.College!.Name)
            .FirstOrDefaultAsync(ct);

        // Every event the student was eligible for: attached individually or via a group (and every frozen
        // event whose roster was materialized as an individual EventGroups row names them directly), plus
        // any event they have an attendance record for (a walk-in still shows on their own report).
        var directEventIds = _db.EventGroups.Where(eg => eg.StudentId == studentId).Select(eg => eg.EventId);

        var groupIds = _db.StudentGroupMembers.Where(m => m.StudentId == studentId).Select(m => m.StudentGroupId);
        var viaGroupEventIds = _db.EventGroups
            .Where(eg => eg.StudentGroupId != null && groupIds.Contains(eg.StudentGroupId.Value))
            .Select(eg => eg.EventId);

        var expectedIds = await directEventIds.Union(viaGroupEventIds).Distinct().ToListAsync(ct);

        var records = await _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.StudentId == studentId)
            .Select(a => new { a.EventId, a.Status, a.CheckInAt, a.CheckOutAt })
            .ToListAsync(ct);

        // One attendance row per (event, student) — UX_Attendance_Event_Student — so keying by event is safe.
        var recordByEvent = records.ToDictionary(r => r.EventId);

        var allIds = expectedIds.Concat(records.Select(r => r.EventId)).Distinct().ToList();

        var events = await _db.Events.AsNoTracking()
            .Where(e => allIds.Contains(e.Id)
                     && !e.IsDeleted
                     && e.Status != EventStatus.Cancelled
                     && (dateFrom == null || e.StartAt >= dateFrom)
                     && (dateTo == null || e.StartAt <= dateTo))
            .Select(e => new { e.Id, e.Name, e.StartAt })
            .ToListAsync(ct);

        var eventDtos = events
            .OrderByDescending(e => e.StartAt)
            .Select(e =>
            {
                recordByEvent.TryGetValue(e.Id, out var rec);
                return new ClearanceEventDto(
                    e.Id, e.Name, e.StartAt,
                    Attendance(rec?.Status),
                    rec?.CheckInAt,
                    rec?.CheckOutAt);
            })
            .ToList();

        await WriteAuditAsync(student.SchoolId, studentId, dateFrom, dateTo, eventDtos.Count, ct);

        var fullName = student.LastName + ", " + student.FirstName +
                       (student.MiddleName is null ? "" : " " + student.MiddleName);

        return new ClearanceReportDto(
            new ClearanceStudentDto(
                student.Id, student.StudentNumber, fullName,
                Department: student.Course, Program: student.Course, College: college,
                student.YearLevel, student.Section),
            eventDtos);
    }

    /// <summary>Maps the §4.9 attendance status to the clearance vocabulary. No record means Missed.</summary>
    private static string Attendance(string? status) => status switch
    {
        AttendanceStatus.Present => "Attended",
        AttendanceStatus.Late => "Late",
        AttendanceStatus.Excused => "Excused",
        _ => "Missed", // Absent, or no record at all
    };

    private async Task WriteAuditAsync(
        Guid schoolId, Guid studentId, DateTime? from, DateTime? to, int eventCount, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            SchoolId = schoolId,
            UserId = _currentUser.UserId,
            Action = AuditAction,
            EntityType = nameof(Student),
            EntityId = studentId,
            Changes = JsonSerializer.Serialize(new
            {
                dateFrom = from,
                dateTo = to,
                events = eventCount,
            }),
        });

        await _db.SaveChangesAsync(ct);
    }
}
