using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// The Event Audience master — see <see cref="IAudienceDefinitionService"/>. CRUD mirrors
/// <see cref="EventClassificationService"/> (school resolution, duplicate pre-check plus index catch); the
/// interesting parts are criteria validation per type and resolving criteria to attendees from the live
/// roster.
/// </summary>
internal sealed class AudienceDefinitionService : IAudienceDefinitionService
{
    /// <summary>How many attendees the resolve read returns inline; the counts are always the true totals.</summary>
    private const int AttendeeSampleCap = 200;

    private readonly EamsDbContext _db;
    private readonly ISchoolContext _school;
    private readonly ILogger<AudienceDefinitionService> _logger;

    public AudienceDefinitionService(
        EamsDbContext db, ISchoolContext school, ILogger<AudienceDefinitionService> logger)
    {
        _db = db;
        _school = school;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------------------------------- reads

    public async Task<PagedResult<AudienceDefinitionDto>> ListAsync(
        Guid? eventClassificationId, bool includeInactive, PageRequest page, CancellationToken ct = default)
    {
        var query = _db.AudienceDefinitions.AsNoTracking().AsQueryable();
        if (!includeInactive) query = query.Where(d => d.IsActive);
        if (eventClassificationId is { } cid) query = query.Where(d => d.EventClassificationId == cid);

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(d => d.IsActive)
            .ThenBy(d => d.Name)
            .ThenBy(d => d.Id)
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .Select(d => new Raw(
                d.Id, d.Name, d.EventClassificationId, d.EventClassification!.Name,
                d.AudienceType, d.CriteriaJson, d.IsActive))
            .ToListAsync(ct);

        return new PagedResult<AudienceDefinitionDto>(
            rows.Select(ToDto).ToList(), page.Page, page.PageSize, total);
    }

    public async Task<AudienceDefinitionDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.AudienceDefinitions.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new Raw(
                d.Id, d.Name, d.EventClassificationId, d.EventClassification!.Name,
                d.AudienceType, d.CriteriaJson, d.IsActive))
            .FirstOrDefaultAsync(ct);

        return row is null ? null : ToDto(row);
    }

    private sealed record Raw(
        Guid Id, string Name, Guid ClassificationId, string ClassificationName,
        string AudienceType, string CriteriaJson, bool IsActive);

    private static AudienceDefinitionDto ToDto(Raw r) => new(
        r.Id, r.Name, r.ClassificationId, r.ClassificationName, r.AudienceType,
        ParseCriteria(r.CriteriaJson), r.IsActive);

    private static AudienceCriteriaDto ParseCriteria(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AudienceCriteriaDto();
        try
        {
            return JsonSerializer.Deserialize<AudienceCriteriaDto>(json, Json) ?? new AudienceCriteriaDto();
        }
        catch (JsonException)
        {
            return new AudienceCriteriaDto();
        }
    }

    // --------------------------------------------------------------------------------- create/edit

    public async Task<AudienceWriteResponse> CreateAsync(
        AudienceDefinitionWriteRequest request, CancellationToken ct = default)
    {
        if (Validate(request, out var type) is { } refused) return refused;

        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null) return NoSchool();

        if (await ClassificationRefusal(schoolId.Value, request.EventClassificationId, ct) is { } bad) return bad;

        var key = AudienceText.KeyFor(request.Name);
        if (await TakenKeyAsync(schoolId.Value, key, null, ct)) return Duplicate(request.Name);

        var row = new AudienceDefinition
        {
            SchoolId = schoolId.Value,
            EventClassificationId = request.EventClassificationId,
            Name = request.Name,
            NameKey = key,
            AudienceType = type,
            CriteriaJson = JsonSerializer.Serialize(request.Criteria, Json),
            IsActive = true,
        };
        _db.AudienceDefinitions.Add(row);

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            _db.Entry(row).State = EntityState.Detached;
            _logger.LogInformation("Audience '{Name}' lost the name race.", request.Name);
            return Duplicate(request.Name);
        }

        return new AudienceWriteResponse(AudienceWriteOutcome.Saved, "Event audience created.",
            (await GetAsync(row.Id, ct))!);
    }

    public async Task<AudienceWriteResponse> UpdateAsync(
        Guid id, AudienceDefinitionWriteRequest request, CancellationToken ct = default)
    {
        if (Validate(request, out var type) is { } refused) return refused;

        var row = await _db.AudienceDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return NotFound();

        if (await ClassificationRefusal(row.SchoolId, request.EventClassificationId, ct) is { } bad) return bad;

        var key = AudienceText.KeyFor(request.Name);
        if (!string.Equals(key, row.NameKey, StringComparison.Ordinal)
            && await TakenKeyAsync(row.SchoolId, key, id, ct))
        {
            return Duplicate(request.Name);
        }

        row.Name = request.Name;
        row.NameKey = key;
        row.EventClassificationId = request.EventClassificationId;
        row.AudienceType = type;
        row.CriteriaJson = JsonSerializer.Serialize(request.Criteria, Json);
        row.UpdatedAt = DateTime.UtcNow;

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            return Duplicate(request.Name);
        }

        return new AudienceWriteResponse(AudienceWriteOutcome.Saved, "Event audience updated.",
            (await GetAsync(id, ct))!);
    }

    public async Task<AudienceWriteResponse> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        var row = await _db.AudienceDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return NotFound();

        if (row.IsActive != isActive)
        {
            row.IsActive = isActive;
            row.RetiredAt = isActive ? null : DateTime.UtcNow;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return new AudienceWriteResponse(AudienceWriteOutcome.Saved,
            isActive ? "Event audience activated." : "Event audience deactivated.",
            (await GetAsync(id, ct))!);
    }

    public async Task<AudienceWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.AudienceDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return NotFound();

        var dto = await GetAsync(id, ct);
        _db.AudienceDefinitions.Remove(row);
        await _db.SaveChangesAsync(ct);

        return new AudienceWriteResponse(AudienceWriteOutcome.Saved, "Event audience deleted.", dto);
    }

    // ------------------------------------------------------------------------------------ resolution

    public async Task<ResolvedAudienceDto?> ResolveAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.AudienceDefinitions.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new { d.Id, d.AudienceType, d.CriteriaJson })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var criteria = ParseCriteria(row.CriteriaJson);
        var (students, personnel) = BuildQueries(row.AudienceType, criteria);

        var attendees = new List<AudienceAttendeeDto>();
        var studentCount = 0;
        var personnelCount = 0;

        if (students is not null)
        {
            studentCount = await students.CountAsync(ct);
            attendees.AddRange(await students
                .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.Id)
                .Take(AttendeeSampleCap)
                .Select(s => new AudienceAttendeeDto(
                    "Student", s.Id, s.StudentNumber, s.LastName + ", " + s.FirstName, s.Course))
                .ToListAsync(ct));
        }

        if (personnel is not null)
        {
            personnelCount = await personnel.CountAsync(ct);
            attendees.AddRange(await personnel
                .OrderBy(p => p.LastName).ThenBy(p => p.FirstName).ThenBy(p => p.Id)
                .Take(AttendeeSampleCap)
                .Select(p => new AudienceAttendeeDto(
                    "Personnel", p.Id, p.PersonnelNumber, p.LastName + ", " + p.FirstName, p.Department))
                .ToListAsync(ct));
        }

        return new ResolvedAudienceDto(id, studentCount, personnelCount, attendees);
    }

    /// <summary>
    /// The student and personnel queries a type + criteria select. A null side means that side contributes
    /// nobody. Criteria lists are materialized to <c>List</c> locals so EF's collection <c>Contains</c>
    /// translation (inlined as literals on the SQL 2012 dialect) has something to bind.
    /// </summary>
    private (IQueryable<Student>? Students, IQueryable<Personnel>? Personnel) BuildQueries(
        string type, AudienceCriteriaDto c)
    {
        var students = _db.Students.AsNoTracking().Where(s => !s.IsDeleted);
        var personnel = _db.Personnel.AsNoTracking().Where(p => !p.IsDeleted);

        List<string> L(IReadOnlyList<string>? v) => v is null ? [] : [.. v];
        List<Guid> G(IReadOnlyList<Guid>? v) => v is null ? [] : [.. v];

        switch (type)
        {
            case AudienceType.UniversityWide:
                AudienceScope.TryNormalize(c.Scope, out var scope);
                return (scope == AudienceScope.Employees ? null : students,
                        scope == AudienceScope.Students ? null : personnel);

            case AudienceType.Department:
            {
                var d = L(c.Departments);
                return (students.Where(s => s.Course != null && d.Contains(s.Course)),
                        personnel.Where(p => p.Department != null && d.Contains(p.Department)));
            }

            case AudienceType.Program:
            {
                var pr = L(c.Programs);
                return (students.Where(s => s.Course != null && pr.Contains(s.Course)), null);
            }

            case AudienceType.YearLevel:
            {
                var y = L(c.YearLevels);
                return (students.Where(s => s.YearLevel != null && y.Contains(s.YearLevel)), null);
            }

            case AudienceType.Section:
            {
                var se = L(c.Sections);
                return (students.Where(s => s.Section != null && se.Contains(s.Section)), null);
            }

            case AudienceType.EmployeeClassification:
            {
                var cl = L(c.Classifications);
                return (null, personnel.Where(p => p.Classification != null && cl.Contains(p.Classification)));
            }

            case AudienceType.Organization:
            {
                var o = L(c.Organizations);
                return (null, personnel.Where(p => p.Organization != null && o.Contains(p.Organization)));
            }

            case AudienceType.SpecificIndividuals:
            {
                var sids = G(c.StudentIds);
                var pids = G(c.PersonnelIds);
                return (sids.Count > 0 ? students.Where(s => sids.Contains(s.Id)) : null,
                        pids.Count > 0 ? personnel.Where(p => pids.Contains(p.Id)) : null);
            }

            case AudienceType.Custom:
            {
                var sApplied = false;
                var sq = students;
                if (L(c.Departments) is { Count: > 0 } cd) { sq = sq.Where(s => s.Course != null && cd.Contains(s.Course)); sApplied = true; }
                if (L(c.Programs) is { Count: > 0 } cp) { sq = sq.Where(s => s.Course != null && cp.Contains(s.Course)); sApplied = true; }
                if (L(c.YearLevels) is { Count: > 0 } cy) { sq = sq.Where(s => s.YearLevel != null && cy.Contains(s.YearLevel)); sApplied = true; }
                if (L(c.Sections) is { Count: > 0 } cs) { sq = sq.Where(s => s.Section != null && cs.Contains(s.Section)); sApplied = true; }
                if (G(c.StudentIds) is { Count: > 0 } csi) { sq = sq.Where(s => csi.Contains(s.Id)); sApplied = true; }

                var pApplied = false;
                var pq = personnel;
                if (L(c.Departments) is { Count: > 0 } pd) { pq = pq.Where(p => p.Department != null && pd.Contains(p.Department)); pApplied = true; }
                if (L(c.Classifications) is { Count: > 0 } pc) { pq = pq.Where(p => p.Classification != null && pc.Contains(p.Classification)); pApplied = true; }
                if (L(c.Organizations) is { Count: > 0 } po) { pq = pq.Where(p => p.Organization != null && po.Contains(p.Organization)); pApplied = true; }
                if (G(c.PersonnelIds) is { Count: > 0 } cpi) { pq = pq.Where(p => cpi.Contains(p.Id)); pApplied = true; }

                return (sApplied ? sq : null, pApplied ? pq : null);
            }

            default:
                return (null, null);
        }
    }

    public async Task<AudienceOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var courses = await _db.Students.AsNoTracking()
            .Where(s => !s.IsDeleted && s.Course != null).Select(s => s.Course!).Distinct().OrderBy(x => x).ToListAsync(ct);
        var years = await _db.Students.AsNoTracking()
            .Where(s => !s.IsDeleted && s.YearLevel != null).Select(s => s.YearLevel!).Distinct().OrderBy(x => x).ToListAsync(ct);
        var sections = await _db.Students.AsNoTracking()
            .Where(s => !s.IsDeleted && s.Section != null).Select(s => s.Section!).Distinct().OrderBy(x => x).ToListAsync(ct);
        var pDepts = await _db.Personnel.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Department != null).Select(p => p.Department!).Distinct().OrderBy(x => x).ToListAsync(ct);
        var classifications = await _db.Personnel.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Classification != null).Select(p => p.Classification!).Distinct().OrderBy(x => x).ToListAsync(ct);
        var orgs = await _db.Personnel.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Organization != null).Select(p => p.Organization!).Distinct().OrderBy(x => x).ToListAsync(ct);

        // Departments span both rosters: a student's Course and a personnel Department are both "department"
        // in the audience sense.
        var departments = courses.Concat(pDepts).Distinct().OrderBy(x => x).ToList();

        return new AudienceOptionsDto(departments, courses, years, sections, classifications, orgs);
    }

    // --------------------------------------------------------------------------------------- plumbing

    private AudienceWriteResponse? Validate(AudienceDefinitionWriteRequest request, out string type)
    {
        type = "";
        if (!AudienceText.IsValidName(request.Name))
        {
            return Invalid($"Name is required, {AudienceText.NameMaxLength} characters or fewer, not " +
                "whitespace-padded, and must contain a letter or digit.");
        }

        if (!AudienceType.TryNormalize(request.AudienceType, out type))
            return Invalid($"Audience type must be one of {string.Join(", ", AudienceType.All)}.");

        return CriteriaRefusal(type, request.Criteria);
    }

    /// <summary>Refuses criteria that do not fit the type — a Department audience with no departments, etc.</summary>
    private AudienceWriteResponse? CriteriaRefusal(string type, AudienceCriteriaDto c)
    {
        static bool Any(IReadOnlyList<string>? v) => v is { Count: > 0 };
        static bool AnyG(IReadOnlyList<Guid>? v) => v is { Count: > 0 };

        return type switch
        {
            AudienceType.UniversityWide when !AudienceScope.TryNormalize(c.Scope, out _) =>
                Invalid($"A university-wide audience needs a scope: {string.Join(", ", AudienceScope.All)}."),
            AudienceType.Department when !Any(c.Departments) => Invalid("Select at least one department."),
            AudienceType.Program when !Any(c.Programs) => Invalid("Select at least one program."),
            AudienceType.YearLevel when !Any(c.YearLevels) => Invalid("Select at least one year level."),
            AudienceType.Section when !Any(c.Sections) => Invalid("Select at least one section."),
            AudienceType.EmployeeClassification when !Any(c.Classifications) => Invalid("Select at least one classification."),
            AudienceType.Organization when !Any(c.Organizations) => Invalid("Select at least one organization."),
            AudienceType.SpecificIndividuals when !AnyG(c.StudentIds) && !AnyG(c.PersonnelIds) =>
                Invalid("Select at least one student or employee."),
            AudienceType.Custom when
                !Any(c.Departments) && !Any(c.Programs) && !Any(c.YearLevels) && !Any(c.Sections)
                && !Any(c.Classifications) && !Any(c.Organizations) && !AnyG(c.StudentIds) && !AnyG(c.PersonnelIds) =>
                Invalid("A custom audience needs at least one criterion."),
            _ => null,
        };
    }

    /// <summary>The classification must exist, be active, and be in this school (spec §8).</summary>
    private async Task<AudienceWriteResponse?> ClassificationRefusal(
        Guid schoolId, Guid classificationId, CancellationToken ct)
    {
        var isActive = await _db.EventClassifications.AsNoTracking()
            .Where( c => c.Id == classificationId && c.SchoolId == schoolId)
            .Select(c => (bool?)c.IsActive)
            .FirstOrDefaultAsync(ct);

        return isActive switch
        {
            null => new AudienceWriteResponse(AudienceWriteOutcome.ClassificationUnavailable,
                "The event classification does not exist in this school.", null),
            false => new AudienceWriteResponse(AudienceWriteOutcome.ClassificationUnavailable,
                "That event classification is deactivated. Only active classifications can be assigned to " +
                "a new or edited audience; existing audiences keep the one they recorded.", null),
            _ => null,
        };
    }

    private async Task<bool> TakenKeyAsync(Guid schoolId, string key, Guid? excluding, CancellationToken ct) =>
        await _db.AudienceDefinitions.AsNoTracking().AnyAsync(
            d => d.SchoolId == schoolId && d.NameKey == key && (excluding == null || d.Id != excluding), ct);

    private static AudienceWriteResponse Invalid(string message) =>
        new(AudienceWriteOutcome.ValidationFailed, message, null);

    private static AudienceWriteResponse NotFound() =>
        new(AudienceWriteOutcome.NotFound, "Event audience not found.", null);

    private static AudienceWriteResponse NoSchool() =>
        new(AudienceWriteOutcome.NoSchoolResolved, "No school could be resolved for this audience.", null);

    private static AudienceWriteResponse Duplicate(string name) =>
        new(AudienceWriteOutcome.NameExists,
            $"This school already has an event audience named '{name}' (compared without punctuation, " +
            "spacing or case).", null);
}
