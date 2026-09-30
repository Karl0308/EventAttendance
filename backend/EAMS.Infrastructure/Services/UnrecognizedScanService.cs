using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// Manual ID entry for unrecognized RFID scans — see <see cref="IUnrecognizedScanService"/>. Records are
/// kept as <c>AuditLogs</c> rows (<see cref="ScanLog.ManualIdEntryAction"/>), the same store the
/// unresolved-scan log uses. <c>AuditLogs</c> is not tenant-query-filtered, so both the write's SchoolId
/// and the read's filter are set explicitly from the resolved tenant.
/// </summary>
internal sealed class UnrecognizedScanService : IUnrecognizedScanService
{
    /// <summary>The two person kinds a manual entry may name. Stored as-is so the record is identifiable.</summary>
    internal const string StudentType = "Student";
    internal const string EmployeeType = "Employee";

    private readonly EamsDbContext _db;
    private readonly ISchoolContext _school;
    private readonly ICurrentUser _currentUser;

    public UnrecognizedScanService(EamsDbContext db, ISchoolContext school, ICurrentUser currentUser)
    {
        _db = db;
        _school = school;
        _currentUser = currentUser;
    }

    /// <summary>The JSON payload stored in <c>AuditLog.Changes</c>. Its own record so read and write agree.</summary>
    private sealed record Payload(
        string CardUid, string IdNumber, string PersonType, bool IsResolved, string? ResolvedName,
        Guid? EventId, string? Note);

    public async Task<ManualIdEntryResponse> RecordAsync(
        ManualIdEntryRequest request, CancellationToken ct = default)
    {
        var uid = CardUid.Normalize(request.CardUid ?? "");
        var idNumber = request.IdNumber?.Trim() ?? "";
        var personType = NormalizePersonType(request.PersonType);

        if (uid.Length == 0)
            return Invalid("A card UID is required — this records the scan that was not recognized.");
        if (idNumber.Length == 0)
            return Invalid("An ID number is required before the record can be saved.");
        if (personType is null)
            return Invalid($"Person type must be '{StudentType}' or '{EmployeeType}'.");

        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null)
            return new ManualIdEntryResponse(
                ManualIdEntryOutcome.NoSchoolResolved,
                "No school could be resolved for this record.", null);

        // Best-effort resolution against the roster it names, so the review can tell "typed an ID that
        // exists" from "typed an ID nobody has". An unresolved entry is still saved — capturing the
        // operator's input for later reconciliation is the whole point.
        var (resolvedId, resolvedName) = await ResolveAsync(schoolId.Value, personType, idNumber, ct);

        var payload = new Payload(
            uid, idNumber, personType, resolvedId is not null, resolvedName, request.EventId,
            string.IsNullOrWhiteSpace(request.Note) ? null : request.Note!.Trim());

        var row = new AuditLog
        {
            SchoolId = schoolId,
            UserId = _currentUser.UserId,
            Action = ScanLog.ManualIdEntryAction,
            EntityType = personType,
            EntityId = resolvedId,
            Changes = JsonSerializer.Serialize(payload),
        };
        _db.AuditLogs.Add(row);
        await _db.SaveChangesAsync(ct);

        return new ManualIdEntryResponse(
            ManualIdEntryOutcome.Saved,
            resolvedId is not null
                ? $"Recorded and matched to {resolvedName}."
                : "Recorded. The ID matched no one in the roster yet — it is kept for reconciliation.",
            ToDto(row.Id, row.CreatedAt, payload));
    }

    public async Task<PagedResult<ManualIdEntryDto>> ListAsync(
        PageRequest page, CancellationToken ct = default)
    {
        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);

        // A read decides nothing to file under, but AuditLogs is unfiltered, so a null tenant would list
        // every school's entries — scope to the resolved school, and to none when none is resolved.
        var query = _db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == ScanLog.ManualIdEntryAction && a.SchoolId == schoolId);

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .Select(a => new { a.Id, a.CreatedAt, a.Changes })
            .ToListAsync(ct);

        var items = rows
            .Select(r =>
            {
                var payload = r.Changes is null
                    ? null
                    : JsonSerializer.Deserialize<Payload>(r.Changes);
                return payload is null ? null : ToDto(r.Id, r.CreatedAt, payload);
            })
            .Where(dto => dto is not null)
            .Select(dto => dto!)
            .ToList();

        return new PagedResult<ManualIdEntryDto>(items, page.Page, page.PageSize, total);
    }

    private async Task<(Guid? Id, string? Name)> ResolveAsync(
        Guid schoolId, string personType, string idNumber, CancellationToken ct)
    {
        if (personType == StudentType)
        {
            var s = await _db.Students.AsNoTracking()
                .Where(x => x.SchoolId == schoolId && !x.IsDeleted && x.StudentNumber == idNumber)
                .Select(x => new { x.Id, x.FirstName, x.LastName })
                .FirstOrDefaultAsync(ct);
            return s is null ? (null, null) : (s.Id, $"{s.LastName}, {s.FirstName}");
        }

        var p = await _db.Personnel.AsNoTracking()
            .Where(x => x.SchoolId == schoolId && !x.IsDeleted && x.PersonnelNumber == idNumber)
            .Select(x => new { x.Id, x.FirstName, x.LastName })
            .FirstOrDefaultAsync(ct);
        return p is null ? (null, null) : (p.Id, $"{p.LastName}, {p.FirstName}");
    }

    private static ManualIdEntryDto ToDto(Guid id, DateTime recordedAt, Payload p) => new(
        id, p.CardUid, p.IdNumber, p.PersonType, p.IsResolved, p.ResolvedName, p.EventId, p.Note, recordedAt);

    /// <summary>Canonical person type (case-insensitive) or null for anything else.</summary>
    private static string? NormalizePersonType(string? value)
    {
        if (string.Equals(value, StudentType, StringComparison.OrdinalIgnoreCase)) return StudentType;
        if (string.Equals(value, EmployeeType, StringComparison.OrdinalIgnoreCase)) return EmployeeType;
        return null;
    }

    private static ManualIdEntryResponse Invalid(string message) =>
        new(ManualIdEntryOutcome.ValidationFailed, message, null);
}
