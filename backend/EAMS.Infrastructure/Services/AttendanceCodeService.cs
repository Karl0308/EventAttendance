using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// The Live Attendance attendance-code surface — see <see cref="IAttendanceCodeService"/>. Generates one
/// code per attendee per event, lists them, and emails them through <see cref="IEmailSender"/> (a logging
/// no-op in this build).
///
/// <para>
/// Every statement carries an explicit <c>EventId</c> predicate; the tenant is enforced by the global query
/// filter on the event and code tables, so a gated caller only ever reaches its own school's rows. New code
/// rows take their <c>SchoolId</c> from the event they belong to, so filter and constraint agree.
/// </para>
/// </summary>
internal sealed class AttendanceCodeService : IAttendanceCodeService
{
    private const int MaxSaveRetries = 3;

    private readonly EamsDbContext _db;
    private readonly IEmailSender _email;
    private readonly ILogger<AttendanceCodeService> _logger;

    public AttendanceCodeService(
        EamsDbContext db, IEmailSender email, ILogger<AttendanceCodeService> logger)
    {
        _db = db;
        _email = email;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------------------- reads

    public async Task<AttendanceCodeListResult> ListAsync(Guid eventId, CancellationToken ct = default)
    {
        if (!await EventExistsAsync(eventId, ct))
            return new AttendanceCodeListResult(AttendanceCodeOutcome.EventNotFound, []);

        return new AttendanceCodeListResult(AttendanceCodeOutcome.Ok, await ReadCodesAsync(eventId, ct));
    }

    // ------------------------------------------------------------------------------------- generate

    public async Task<AttendanceCodeGenerateResult> GenerateAsync(
        Guid eventId, bool regenerate, CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Id, e.SchoolId })
            .FirstOrDefaultAsync(ct);
        if (ev is null)
            return new AttendanceCodeGenerateResult(AttendanceCodeOutcome.EventNotFound, 0, 0, 0, []);

        for (var attempt = 1; ; attempt++)
        {
            // The attendees are the students with a record for this event — the Live Attendance list.
            var attendeeIds = await _db.AttendanceRecords.AsNoTracking()
                .Where(a => a.EventId == eventId)
                .Select(a => a.StudentId)
                .Distinct()
                .ToListAsync(ct);

            var existing = await _db.EventAttendanceCodes
                .Where(c => c.EventId == eventId)
                .ToListAsync(ct);
            var byStudent = existing.ToDictionary(c => c.StudentId);
            var used = new HashSet<string>(existing.Select(c => c.Code), StringComparer.OrdinalIgnoreCase);

            var created = 0;
            var regenerated = 0;
            var alreadyHad = 0;
            var now = DateTime.UtcNow;

            foreach (var studentId in attendeeIds)
            {
                if (byStudent.TryGetValue(studentId, out var row))
                {
                    if (regenerate)
                    {
                        used.Remove(row.Code);
                        row.Code = UniqueCode(used);
                        row.UpdatedAt = now;
                        regenerated++;
                    }
                    else
                    {
                        alreadyHad++;
                    }
                }
                else
                {
                    _db.EventAttendanceCodes.Add(new EventAttendanceCode
                    {
                        SchoolId = ev.SchoolId,
                        EventId = eventId,
                        StudentId = studentId,
                        Code = UniqueCode(used),
                    });
                    created++;
                }
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex) && attempt < MaxSaveRetries)
            {
                // A concurrent generate on the same event took a code or a slot. Detach what this attempt
                // staged and retry from a fresh read.
                _logger.LogInformation(
                    "Attendance-code generate for event {EventId} lost a uniqueness race; retrying.", eventId);
                foreach (var entry in _db.ChangeTracker.Entries<EventAttendanceCode>().ToList())
                {
                    entry.State = EntityState.Detached;
                }
                continue;
            }

            return new AttendanceCodeGenerateResult(
                AttendanceCodeOutcome.Ok, created, regenerated, alreadyHad,
                await ReadCodesAsync(eventId, ct));
        }
    }

    // ---------------------------------------------------------------------------------------- email

    public async Task<AttendanceCodeEmailOutcome> EmailAsync(
        Guid eventId, AttendanceCodeEmailRequest request, CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Id, e.Name, e.StartAt })
            .FirstOrDefaultAsync(ct);
        if (ev is null)
            return new AttendanceCodeEmailOutcome(
                AttendanceCodeOutcome.EventNotFound, "Event not found.", null);

        if (!AttendanceCodeEmailMode.TryNormalize(request.Mode, out var mode))
            return new AttendanceCodeEmailOutcome(
                AttendanceCodeOutcome.ValidationFailed,
                $"Mode must be one of {string.Join(", ", AttendanceCodeEmailMode.AllModes)}.", null);

        // Who the send targets. All → every attendee (students with a record); Selected → the chosen ids.
        List<Guid> targetIds;
        if (mode == AttendanceCodeEmailMode.Selected)
        {
            targetIds = (request.StudentIds ?? []).Distinct().ToList();
            if (targetIds.Count == 0)
                return new AttendanceCodeEmailOutcome(
                    AttendanceCodeOutcome.ValidationFailed,
                    "Select at least one attendee to email, or use mode \"All\".", null);
        }
        else
        {
            targetIds = await _db.AttendanceRecords.AsNoTracking()
                .Where(a => a.EventId == eventId)
                .Select(a => a.StudentId)
                .Distinct()
                .ToListAsync(ct);
        }

        var codeRows = await _db.EventAttendanceCodes
            .Where(c => c.EventId == eventId)
            .ToListAsync(ct);
        var byStudent = codeRows.ToDictionary(c => c.StudentId);

        var students = await _db.Students.AsNoTracking()
            .Where(s => targetIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Email, s.FirstName, s.LastName })
            .ToListAsync(ct);
        var byId = students.ToDictionary(s => s.Id);

        var sent = 0;
        var skippedNoEmail = 0;
        var skippedNoCode = 0;
        var failed = 0;
        var now = DateTime.UtcNow;

        foreach (var studentId in targetIds)
        {
            if (!byStudent.TryGetValue(studentId, out var codeRow))
            {
                skippedNoCode++;
                continue;
            }

            if (!byId.TryGetValue(studentId, out var student) || string.IsNullOrWhiteSpace(student.Email))
            {
                skippedNoEmail++;
                continue;
            }

            var name = $"{student.LastName}, {student.FirstName}";
            var message = new EmailMessage(
                student.Email!, name,
                $"Your attendance code for {ev.Name}",
                $"Hello {student.FirstName},\n\n" +
                $"Your attendance code for \"{ev.Name}\" on {ev.StartAt:yyyy-MM-dd} is: {codeRow.Code}\n\n" +
                "Please keep this code for your records.");

            try
            {
                await _email.SendAsync(message, ct);
                codeRow.LastEmailedAt = now;
                codeRow.EmailCount++;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to send attendance code to student {StudentId} for event {EventId}.",
                    studentId, eventId);
                failed++;
            }
        }

        if (sent > 0) await _db.SaveChangesAsync(ct);

        var result = new AttendanceCodeEmailResultDto(
            targetIds.Count, sent, skippedNoEmail, skippedNoCode, failed);

        return new AttendanceCodeEmailOutcome(
            AttendanceCodeOutcome.Ok,
            $"Sent {sent} of {targetIds.Count} attendance code email(s).",
            result);
    }

    // --------------------------------------------------------------------------------------- plumbing

    private Task<bool> EventExistsAsync(Guid eventId, CancellationToken ct) =>
        _db.Events.AsNoTracking().AnyAsync(e => e.Id == eventId, ct);

    private async Task<IReadOnlyList<AttendanceCodeDto>> ReadCodesAsync(Guid eventId, CancellationToken ct) =>
        await _db.EventAttendanceCodes.AsNoTracking()
            .Where(c => c.EventId == eventId)
            .OrderBy(c => c.Student!.LastName)
            .ThenBy(c => c.Student!.FirstName)
            .ThenBy(c => c.Id)
            .Select(c => new AttendanceCodeDto(
                c.Id,
                c.StudentId,
                c.Student!.StudentNumber,
                c.Student!.LastName + ", " + c.Student!.FirstName,
                c.Student!.Email,
                c.Code,
                c.LastEmailedAt,
                c.EmailCount))
            .ToListAsync(ct);

    private static string UniqueCode(HashSet<string> used)
    {
        for (var i = 0; i < 1000; i++)
        {
            var candidate = AttendanceCode.Generate();
            if (used.Add(candidate)) return candidate;
        }
        // 1000 collisions over a 30^8 space is not chance; it is a broken RNG. Fail loudly.
        throw new InvalidOperationException("Could not generate a unique attendance code.");
    }
}
