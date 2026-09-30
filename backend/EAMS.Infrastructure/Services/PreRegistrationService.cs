using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// The Pre-Registration surface — see <see cref="IPreRegistrationService"/>. Sessions against an
/// <see cref="AudienceDefinition"/>, into which attendees are pre-registered by RFID tap or manual selection.
///
/// <para>
/// The duplicate and capacity rules apply equally to both registration methods (spec §Validation/§Capacity):
/// resolution differs (a card vs. a chosen id), but once an attendee is resolved both paths run the same
/// guard. Duplicates are refused by a read <em>and</em> by the two filtered unique indexes, so a double-tap
/// that races the read still cannot make two rows.
/// </para>
/// </summary>
internal sealed class PreRegistrationService : IPreRegistrationService
{
    private readonly EamsDbContext _db;
    private readonly ISchoolContext _school;
    private readonly ILogger<PreRegistrationService> _logger;

    public PreRegistrationService(
        EamsDbContext db, ISchoolContext school, ILogger<PreRegistrationService> logger)
    {
        _db = db;
        _school = school;
        _logger = logger;
    }

    // -------------------------------------------------------------------------------------- sessions

    public async Task<IReadOnlyList<PreRegistrationSessionDto>> ListSessionsAsync(CancellationToken ct = default) =>
        await _db.PreRegistrationSessions.AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new PreRegistrationSessionDto(
                s.Id, s.Name, s.AudienceDefinitionId, s.AudienceDefinition!.Name,
                s.Capacity, s.Registrations.Count, s.IsClosed, s.Registrations.Count >= s.Capacity))
            .ToListAsync(ct);

    public async Task<PreRegistrationSessionDto?> GetSessionAsync(Guid id, CancellationToken ct = default) =>
        await _db.PreRegistrationSessions.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new PreRegistrationSessionDto(
                s.Id, s.Name, s.AudienceDefinitionId, s.AudienceDefinition!.Name,
                s.Capacity, s.Registrations.Count, s.IsClosed, s.Registrations.Count >= s.Capacity))
            .FirstOrDefaultAsync(ct);

    public async Task<PreRegistrationSessionWriteResult> CreateSessionAsync(
        PreRegistrationSessionCreateRequest request, CancellationToken ct = default)
    {
        if (!PreRegistrationText.IsValidName(request.Name))
            return SessionInvalid(
                $"Name is required, {PreRegistrationText.NameMaxLength} characters or fewer, and not " +
                "whitespace-padded.");

        if (!PreRegistrationText.IsValidCapacity(request.Capacity))
            return SessionInvalid(
                $"Capacity must be between 1 and {PreRegistrationText.MaxCapacity:N0}.");

        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null)
            return new PreRegistrationSessionWriteResult(
                PreRegistrationSessionOutcome.NoSchoolResolved,
                "No school could be resolved for this session.", null);

        var audienceActive = await _db.AudienceDefinitions.AsNoTracking()
            .Where(a => a.Id == request.AudienceDefinitionId && a.SchoolId == schoolId)
            .Select(a => (bool?)a.IsActive)
            .FirstOrDefaultAsync(ct);
        if (audienceActive is null or false)
            return new PreRegistrationSessionWriteResult(
                PreRegistrationSessionOutcome.AudienceUnavailable,
                audienceActive is null
                    ? "The event audience does not exist in this school."
                    : "That event audience is deactivated. Reactivate it, or pick another, before opening a "
                      + "pre-registration session against it.",
                null);

        var row = new PreRegistrationSession
        {
            SchoolId = schoolId.Value,
            AudienceDefinitionId = request.AudienceDefinitionId,
            Name = request.Name,
            Capacity = request.Capacity,
        };
        _db.PreRegistrationSessions.Add(row);
        await _db.SaveChangesAsync(ct);

        return new PreRegistrationSessionWriteResult(
            PreRegistrationSessionOutcome.Saved, "Pre-registration session created.",
            (await GetSessionAsync(row.Id, ct))!);
    }

    public async Task<PreRegistrationSessionWriteResult> SetClosedAsync(
        Guid id, bool isClosed, CancellationToken ct = default)
    {
        var row = await _db.PreRegistrationSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row is null)
            return new PreRegistrationSessionWriteResult(
                PreRegistrationSessionOutcome.NotFound, "Pre-registration session not found.", null);

        if (row.IsClosed != isClosed)
        {
            row.IsClosed = isClosed;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return new PreRegistrationSessionWriteResult(
            PreRegistrationSessionOutcome.Saved,
            isClosed ? "Session closed." : "Session reopened.",
            (await GetSessionAsync(id, ct))!);
    }

    // ---------------------------------------------------------------------------------- registration

    public async Task<PreRegisterResult> RegisterAsync(
        Guid sessionId, PreRegisterRequest request, CancellationToken ct = default)
    {
        var session = await _db.PreRegistrationSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
            return Refuse(PreRegisterOutcome.SessionNotFound, "Pre-registration session not found.", 0, 0);

        if (session.IsClosed)
            return await RefuseAsync(PreRegisterOutcome.SessionClosed,
                "This session is closed and accepts no further registrations.", session, ct);

        if (!PreRegistrationMethod.TryNormalize(request.Method, out var method))
            return await RefuseAsync(PreRegisterOutcome.ValidationFailed,
                $"Method must be one of {string.Join(", ", PreRegistrationMethod.All)}.", session, ct);

        // Resolve the attendee — a card for a tap, a chosen id for a manual add.
        var resolution = method == PreRegistrationMethod.Tapped
            ? await ResolveByCardAsync(request.CardUid, ct)
            : await ResolveManualAsync(request.Type, request.AttendeeId, ct);
        if (resolution.Outcome != PreRegisterOutcome.Registered)
            return await RefuseAsync(resolution.Outcome, resolution.Message, session, ct);

        var studentId = resolution.StudentId;
        var personnelId = resolution.PersonnelId;

        // Duplicate — the same attendee already in this session, whichever way they were added.
        var already = studentId is { } sid
            ? await _db.PreRegistrations.AnyAsync(p => p.SessionId == sessionId && p.StudentId == sid, ct)
            : await _db.PreRegistrations.AnyAsync(
                p => p.SessionId == sessionId && p.PersonnelId == personnelId, ct);
        if (already)
            return await RefuseAsync(PreRegisterOutcome.Duplicate,
                "This attendee is already registered in this session.", session, ct);

        // Capacity — counted at the last moment before the insert.
        var count = await _db.PreRegistrations.CountAsync(p => p.SessionId == sessionId, ct);
        if (count >= session.Capacity)
            return await RefuseAsync(PreRegisterOutcome.CapacityReached,
                $"This session has reached its capacity of {session.Capacity}.", session, ct);

        var row = new PreRegistration
        {
            SchoolId = session.SchoolId,
            SessionId = sessionId,
            StudentId = studentId,
            PersonnelId = personnelId,
            AttendeeType = studentId is not null ? PreRegistrantType.Student : PreRegistrantType.Personnel,
            Method = method,
            RegisteredAt = DateTime.UtcNow,
        };
        _db.PreRegistrations.Add(row);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // Lost the duplicate race to a filtered unique index — the same tidy 409 the read path gives.
            _db.Entry(row).State = EntityState.Detached;
            _logger.LogInformation(
                "Pre-registration into session {SessionId} lost the duplicate race.", sessionId);
            return await RefuseAsync(PreRegisterOutcome.Duplicate,
                "This attendee is already registered in this session.", session, ct);
        }

        var registrant = (await ReadRegistrantsAsync(sessionId, ct)).First(r => r.Id == row.Id);
        var newCount = await _db.PreRegistrations.CountAsync(p => p.SessionId == sessionId, ct);
        return new PreRegisterResult(
            PreRegisterOutcome.Registered, "Attendee registered.", newCount, session.Capacity, registrant);
    }

    public async Task<PreRegistrantListResult> ListRegistrantsAsync(
        Guid sessionId, CancellationToken ct = default)
    {
        if (!await _db.PreRegistrationSessions.AsNoTracking().AnyAsync(s => s.Id == sessionId, ct))
            return new PreRegistrantListResult(PreRegistrationSessionOutcome.NotFound, []);

        return new PreRegistrantListResult(
            PreRegistrationSessionOutcome.Saved, await ReadRegistrantsAsync(sessionId, ct));
    }

    public async Task<PreRegisterResult> RemoveRegistrantAsync(
        Guid sessionId, Guid registrantId, CancellationToken ct = default)
    {
        var session = await _db.PreRegistrationSessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.Capacity })
            .FirstOrDefaultAsync(ct);
        if (session is null)
            return Refuse(PreRegisterOutcome.SessionNotFound, "Pre-registration session not found.", 0, 0);

        var row = await _db.PreRegistrations
            .FirstOrDefaultAsync(p => p.Id == registrantId && p.SessionId == sessionId, ct);
        if (row is null)
        {
            var current = await _db.PreRegistrations.CountAsync(p => p.SessionId == sessionId, ct);
            return Refuse(PreRegisterOutcome.AttendeeNotFound,
                "That registration was not found in this session.", current, session.Capacity);
        }

        _db.PreRegistrations.Remove(row);
        await _db.SaveChangesAsync(ct);

        var count = await _db.PreRegistrations.CountAsync(p => p.SessionId == sessionId, ct);
        return new PreRegisterResult(
            PreRegisterOutcome.Registered, "The attendee was removed.", count, session.Capacity, null);
    }

    // --------------------------------------------------------------------------------------- resolve

    private readonly record struct Resolution(
        PreRegisterOutcome Outcome, string Message, Guid? StudentId, Guid? PersonnelId);

    private static Resolution ResolvedStudent(Guid id) =>
        new(PreRegisterOutcome.Registered, "", id, null);

    private static Resolution ResolvedPersonnel(Guid id) =>
        new(PreRegisterOutcome.Registered, "", null, id);

    private async Task<Resolution> ResolveByCardAsync(string? cardUid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardUid))
            return new Resolution(PreRegisterOutcome.ValidationFailed,
                "A card UID is required for a tapped registration.", null, null);

        // Normalize first, compare second (CLAUDE.md).
        var uid = CardUid.Normalize(cardUid);
        if (uid.Length == 0)
            return new Resolution(PreRegisterOutcome.ValidationFailed,
                $"The card '{cardUid}' contains no letter or digit.", null, null);

        var studentId = await _db.RfidCards.AsNoTracking()
            .Where(c => c.CardUid == uid && c.IsActive && !c.Student!.IsDeleted)
            .Select(c => (Guid?)c.StudentId)
            .FirstOrDefaultAsync(ct);
        if (studentId is { } sid) return ResolvedStudent(sid);

        var personnelId = await _db.Personnel.AsNoTracking()
            .Where(p => p.RfidUid == uid && !p.IsDeleted)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (personnelId is { } pid) return ResolvedPersonnel(pid);

        return new Resolution(PreRegisterOutcome.UnrecognizedCard,
            $"No student or employee is registered to card {uid}.", null, null);
    }

    private async Task<Resolution> ResolveManualAsync(string? type, Guid? attendeeId, CancellationToken ct)
    {
        if (!PreRegistrantType.TryNormalize(type, out var canonical))
            return new Resolution(PreRegisterOutcome.ValidationFailed,
                $"Type must be one of {string.Join(", ", PreRegistrantType.All)}.", null, null);

        if (attendeeId is not { } id || id == Guid.Empty)
            return new Resolution(PreRegisterOutcome.ValidationFailed,
                "An attendee id is required for a manual registration.", null, null);

        if (canonical == PreRegistrantType.Student)
        {
            var exists = await _db.Students.AsNoTracking().AnyAsync(s => s.Id == id && !s.IsDeleted, ct);
            return exists
                ? ResolvedStudent(id)
                : new Resolution(PreRegisterOutcome.AttendeeNotFound, "Student not found.", null, null);
        }

        var pExists = await _db.Personnel.AsNoTracking().AnyAsync(p => p.Id == id && !p.IsDeleted, ct);
        return pExists
            ? ResolvedPersonnel(id)
            : new Resolution(PreRegisterOutcome.AttendeeNotFound, "Employee not found.", null, null);
    }

    // --------------------------------------------------------------------------------------- reads

    private async Task<IReadOnlyList<PreRegistrantDto>> ReadRegistrantsAsync(
        Guid sessionId, CancellationToken ct)
    {
        var rows = await _db.PreRegistrations.AsNoTracking()
            .Where(p => p.SessionId == sessionId)
            .Include(p => p.Student)
            .Include(p => p.Personnel)
            .OrderBy(p => p.RegisteredAt)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);

        // A student's card is not a single column, so the active-card UID is fetched in one batch and
        // joined in memory rather than through a fragile per-row correlated projection.
        var studentIds = rows.Where(r => r.StudentId is not null)
            .Select(r => r.StudentId!.Value).Distinct().ToList();
        var cardByStudent = studentIds.Count == 0
            ? []
            : (await _db.RfidCards.AsNoTracking()
                .Where(c => c.IsActive && studentIds.Contains(c.StudentId))
                .Select(c => new { c.StudentId, c.CardUid })
                .ToListAsync(ct))
                .GroupBy(c => c.StudentId)
                .ToDictionary(g => g.Key, g => g.First().CardUid);

        return rows.Select(r => r.StudentId is { } sid
            ? new PreRegistrantDto(
                r.Id, PreRegistrantType.Student, sid,
                r.Student!.StudentNumber,
                $"{r.Student!.LastName}, {r.Student!.FirstName}",
                cardByStudent.GetValueOrDefault(sid),
                r.Student!.Course,
                r.Method, r.RegisteredAt)
            : new PreRegistrantDto(
                r.Id, PreRegistrantType.Personnel, r.PersonnelId!.Value,
                r.Personnel!.PersonnelNumber,
                $"{r.Personnel!.LastName}, {r.Personnel!.FirstName}",
                r.Personnel!.RfidUid,
                r.Personnel!.Department,
                r.Method, r.RegisteredAt))
            .ToList();
    }

    // --------------------------------------------------------------------------------------- plumbing

    private static PreRegistrationSessionWriteResult SessionInvalid(string message) =>
        new(PreRegistrationSessionOutcome.ValidationFailed, message, null);

    private static PreRegisterResult Refuse(
        PreRegisterOutcome outcome, string message, int count, int capacity) =>
        new(outcome, message, count, capacity, null);

    private async Task<PreRegisterResult> RefuseAsync(
        PreRegisterOutcome outcome, string message, PreRegistrationSession session, CancellationToken ct)
    {
        var count = await _db.PreRegistrations.CountAsync(p => p.SessionId == session.Id, ct);
        return new PreRegisterResult(outcome, message, count, session.Capacity, null);
    }
}
