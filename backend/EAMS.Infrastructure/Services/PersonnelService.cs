using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// The Academic Community's Personnel tab — see <see cref="IPersonnelService"/>. Modelled on
/// <c>StudentService</c>: tenant resolved through <see cref="ISchoolContext"/> on create, reads bounded by
/// the global query filter, uniqueness decided by filtered indexes and caught behind a pre-check so a
/// race is a 409 rather than a 500.
/// </summary>
internal sealed class PersonnelService : IPersonnelService
{
    private readonly EamsDbContext _db;
    private readonly ISchoolContext _school;
    private readonly ILogger<PersonnelService> _logger;

    public PersonnelService(EamsDbContext db, ISchoolContext school, ILogger<PersonnelService> logger)
    {
        _db = db;
        _school = school;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------------------- reads

    public async Task<PagedResult<PersonnelDto>> ListAsync(
        PersonnelListFilter filter, PageRequest page, CancellationToken ct = default)
    {
        return await FilteredQuery(filter).ToPageAsync(Ordered, page, ct);
    }

    public async Task<IReadOnlyList<PersonnelDto>> ExportAsync(
        PersonnelListFilter filter, CancellationToken ct = default)
    {
        return await Ordered(FilteredQuery(filter)).ToListAsync(ct);
    }

    /// <summary>The list/export filter, shared so a CSV export and the paged screen cannot drift apart.</summary>
    private IQueryable<Personnel> FilteredQuery(PersonnelListFilter filter)
    {
        var query = _db.Personnel.AsNoTracking().Where(p => !p.IsDeleted);

        if (Fragment(filter.PersonnelNumber) is { } number)
            query = query.Where(p => p.PersonnelNumber.Contains(number));

        if (Fragment(filter.RfidUid) is { } rawUid)
        {
            // The stored UID is normalized, so the filter is compared against the normalized form too —
            // "normalize first, compare second" (CLAUDE.md).
            var uid = CardUid.Normalize(rawUid);
            if (uid.Length > 0) query = query.Where(p => p.RfidUid != null && p.RfidUid.Contains(uid));
        }

        if (Fragment(filter.Name) is { } name)
            query = query.Where(p =>
                p.FirstName.Contains(name) || p.LastName.Contains(name) ||
                (p.MiddleName != null && p.MiddleName.Contains(name)));

        if (Fragment(filter.Department) is { } dept)
            query = query.Where(p => p.Department != null && p.Department.Contains(dept));

        if (Fragment(filter.Position) is { } pos)
            query = query.Where(p => p.Position != null && p.Position.Contains(pos));

        if (Fragment(filter.Classification) is { } cls)
            query = query.Where(p => p.Classification != null && p.Classification.Contains(cls));

        if (Fragment(filter.Status) is { } status)
            query = query.Where(p => p.Status == status);

        return query;
    }

    private static IQueryable<PersonnelDto> Ordered(IQueryable<Personnel> q) =>
        q.OrderByDescending(p => p.Status == PersonnelStatus.Active)
            .ThenBy(p => p.LastName)
            .ThenBy(p => p.FirstName)
            .ThenBy(p => p.Id)
            .Select(p => ToDtoExpression(p));

    public async Task<PersonnelDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Personnel.AsNoTracking()
            .Where(p => p.Id == id && !p.IsDeleted)
            .Select(p => ToDtoExpression(p))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<string>> OrganizationsAsync(CancellationToken ct = default)
    {
        // The set of distinct organizations is tiny, so the null/empty filter and SQL-side distinct cut
        // it to a handful of rows, then the trim, whitespace-only exclusion and final distinct/order run
        // in memory. Doing the whitespace work in memory is deliberate: SQL Server's TRIM only strips
        // spaces (not tabs/newlines) and pads trailing spaces away in comparisons, so an all-in-SQL
        // version would be correct only by accident. "ABC" and "ABC " collapse into one entry here.
        var raw = await _db.Personnel.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Organization != null && p.Organization != "")
            .Select(p => p.Organization!)
            .Distinct()
            .ToListAsync(ct);

        return raw
            .Select(o => o.Trim())
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(o => o, StringComparer.Ordinal)
            .ToList();
    }

    // --------------------------------------------------------------------------------- create/edit

    public async Task<PersonnelWriteResponse> CreateAsync(
        PersonnelWriteRequest request, CancellationToken ct = default)
    {
        if (Normalize(request, out var fields, out var refused) is false) return refused!;

        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null) return NoSchool();

        if (await NumberTakenAsync(schoolId.Value, fields.Number, excluding: null, ct))
            return DuplicateNumber(fields.Number);
        if (fields.RfidUid is { } uid && await RfidTakenAsync(schoolId.Value, uid, excluding: null, ct))
            return DuplicateRfid(uid);

        var row = new Personnel
        {
            SchoolId = schoolId.Value,
            PersonnelNumber = fields.Number,
            FirstName = fields.FirstName,
            MiddleName = fields.MiddleName,
            LastName = fields.LastName,
            Email = fields.Email,
            Classification = fields.Classification,
            Department = fields.Department,
            Organization = fields.Organization,
            Position = fields.Position,
            RfidUid = fields.RfidUid,
            Status = fields.Status,
        };
        _db.Personnel.Add(row);

        return await SaveAsync(row, "Personnel record created.", ct);
    }

    public async Task<PersonnelWriteResponse> UpdateAsync(
        Guid id, PersonnelWriteRequest request, CancellationToken ct = default)
    {
        if (Normalize(request, out var fields, out var refused) is false) return refused!;

        var row = await _db.Personnel.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
        if (row is null) return NotFound();

        if (await NumberTakenAsync(row.SchoolId, fields.Number, excluding: id, ct))
            return DuplicateNumber(fields.Number);
        if (fields.RfidUid is { } uid && await RfidTakenAsync(row.SchoolId, uid, excluding: id, ct))
            return DuplicateRfid(uid);

        row.PersonnelNumber = fields.Number;
        row.FirstName = fields.FirstName;
        row.MiddleName = fields.MiddleName;
        row.LastName = fields.LastName;
        row.Email = fields.Email;
        row.Classification = fields.Classification;
        row.Department = fields.Department;
        row.Organization = fields.Organization;
        row.Position = fields.Position;
        row.RfidUid = fields.RfidUid;
        row.Status = fields.Status;
        row.UpdatedAt = DateTime.UtcNow;

        return await SaveAsync(row, "Personnel record updated.", ct);
    }

    public async Task<PersonnelWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.Personnel.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
        if (row is null) return NotFound();

        row.IsDeleted = true;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new PersonnelWriteResponse(
            PersonnelWriteOutcome.Saved,
            "Personnel record removed. It is soft-deleted — kept so any future attendance can still " +
            "resolve, and its ID and card are freed for reuse.",
            ToDto(row));
    }

    // ----------------------------------------------------------------------------------------- import

    public async Task<PersonnelImportResultDto> ImportAsync(
        IReadOnlyList<PersonnelWriteRequest> rows, CancellationToken ct = default)
    {
        var created = 0;
        var updated = 0;
        var errors = new List<PersonnelImportErrorDto>();

        // Each row goes through the same CreateAsync/UpdateAsync as a single write, so import cannot admit a
        // record that the manual form would reject, and every uniqueness rule is enforced identically. A
        // failed row is tallied and the import continues (spec: distinguish success from each failure kind).
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var line = i + 1;
            var number = row.PersonnelNumber?.Trim() ?? "";

            if (number.Length == 0)
            {
                errors.Add(new PersonnelImportErrorDto(line, null, "A personnel ID is required."));
                continue;
            }

            // Upsert key: an existing live record with this number in the tenant is updated, else created.
            var existingId = await _db.Personnel.AsNoTracking()
                .Where(p => !p.IsDeleted && p.PersonnelNumber == number)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(ct);

            var response = existingId is { } id
                ? await UpdateAsync(id, row, ct)
                : await CreateAsync(row, ct);

            if (response.Outcome == PersonnelWriteOutcome.Saved)
            {
                if (existingId is null) created++;
                else updated++;
            }
            else
            {
                errors.Add(new PersonnelImportErrorDto(line, number, response.Message));
            }
        }

        return new PersonnelImportResultDto(rows.Count, created, updated, errors.Count, errors);
    }

    // --------------------------------------------------------------------------------------- plumbing

    private async Task<PersonnelWriteResponse> SaveAsync(Personnel row, string message, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            _db.Entry(row).State = row.Id == default ? EntityState.Detached : EntityState.Unchanged;

            // Two filtered unique indexes could have been the one that fired. Re-derive which so the
            // caller gets the specific 409 rather than a generic one.
            if (await NumberTakenAsync(row.SchoolId, row.PersonnelNumber, excluding: NullIfNew(row), ct))
            {
                _logger.LogInformation(
                    "A personnel write lost the race on the number index for '{Number}'.", row.PersonnelNumber);
                return DuplicateNumber(row.PersonnelNumber);
            }

            _logger.LogInformation("A personnel write lost the race on the RFID index for '{Uid}'.", row.RfidUid);
            return DuplicateRfid(row.RfidUid ?? "");
        }

        return new PersonnelWriteResponse(PersonnelWriteOutcome.Saved, message, ToDto(row));
    }

    private static Guid? NullIfNew(Personnel row) => row.Id == default ? null : row.Id;

    private Task<bool> NumberTakenAsync(Guid schoolId, string number, Guid? excluding, CancellationToken ct) =>
        _db.Personnel.AsNoTracking().AnyAsync(
            p => p.SchoolId == schoolId && !p.IsDeleted && p.PersonnelNumber == number
                 && (excluding == null || p.Id != excluding), ct);

    private Task<bool> RfidTakenAsync(Guid schoolId, string uid, Guid? excluding, CancellationToken ct) =>
        _db.Personnel.AsNoTracking().AnyAsync(
            p => p.SchoolId == schoolId && !p.IsDeleted && p.RfidUid == uid
                 && (excluding == null || p.Id != excluding), ct);

    private static string? Fragment(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Fields(
        string Number, string FirstName, string? MiddleName, string LastName, string? Email,
        string? Classification, string? Department, string? Organization, string? Position,
        string? RfidUid, string Status);

    /// <summary>
    /// Trims, length-checks, normalizes the status and the RFID UID, and yields the values to store —
    /// or a 400 naming the field. <c>false</c> return means <paramref name="refused"/> is set.
    /// </summary>
    private static bool Normalize(
        PersonnelWriteRequest request, out Fields fields, out PersonnelWriteResponse? refused)
    {
        fields = null!;
        refused = null;

        var number = request.PersonnelNumber?.Trim() ?? "";
        var first = request.FirstName?.Trim() ?? "";
        var last = request.LastName?.Trim() ?? "";

        if (number.Length == 0) return Fail("A personnel ID is required.", out refused);
        if (number.Length > PersonnelText.NumberMaxLength)
            return Fail($"The personnel ID is longer than {PersonnelText.NumberMaxLength} characters.", out refused);
        if (first.Length == 0) return Fail("A first name is required.", out refused);
        if (first.Length > PersonnelText.NameMaxLength)
            return Fail($"The first name is longer than {PersonnelText.NameMaxLength} characters.", out refused);
        if (last.Length == 0) return Fail("A last name is required.", out refused);
        if (last.Length > PersonnelText.NameMaxLength)
            return Fail($"The last name is longer than {PersonnelText.NameMaxLength} characters.", out refused);

        var middle = Trimmed(request.MiddleName, PersonnelText.NameMaxLength, "middle name", ref refused);
        var email = Trimmed(request.Email, PersonnelText.EmailMaxLength, "e-mail", ref refused);
        var classification = Trimmed(request.Classification, PersonnelText.ClassificationMaxLength, "classification", ref refused);
        var department = Trimmed(request.Department, PersonnelText.DepartmentMaxLength, "department", ref refused);
        var organization = Trimmed(request.Organization, PersonnelText.OrganizationMaxLength, "organization", ref refused);
        var position = Trimmed(request.Position, PersonnelText.PositionMaxLength, "position", ref refused);
        if (refused is not null) return false;

        string? rfid = null;
        if (!string.IsNullOrWhiteSpace(request.RfidUid))
        {
            rfid = CardUid.Normalize(request.RfidUid);
            if (rfid.Length == 0)
                return Fail("The RFID UID has no letters or digits — it is not a card serial.", out refused);
            if (rfid.Length > PersonnelText.RfidUidMaxLength)
                return Fail($"The RFID UID is longer than {PersonnelText.RfidUidMaxLength} characters.", out refused);
        }

        if (!PersonnelStatus.TryNormalize(
                string.IsNullOrWhiteSpace(request.Status) ? PersonnelStatus.Active : request.Status,
                out var status))
        {
            return Fail(
                $"Status must be one of {string.Join(", ", PersonnelStatus.All)}.", out refused);
        }

        fields = new Fields(number, first, middle, last, email, classification, department, organization,
            position, rfid, status);
        return true;
    }

    private static string? Trimmed(string? value, int max, string field, ref PersonnelWriteResponse? refused)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > max)
            refused ??= new PersonnelWriteResponse(
                PersonnelWriteOutcome.ValidationFailed,
                $"The {field} is longer than {max} characters.", null);
        return trimmed;
    }

    private static bool Fail(string message, out PersonnelWriteResponse? refused)
    {
        refused = new PersonnelWriteResponse(PersonnelWriteOutcome.ValidationFailed, message, null);
        return false;
    }

    // The DTO projection, as an expression EF can translate (FullName composed in SQL) and reused for
    // the in-memory ToDto below via compilation-free duplication kept deliberately tiny.
    private static PersonnelDto ToDtoExpression(Personnel p) => new(
        p.Id, p.PersonnelNumber,
        p.LastName + ", " + p.FirstName + (p.MiddleName == null ? "" : " " + p.MiddleName),
        p.FirstName, p.MiddleName, p.LastName, p.Email, p.Classification, p.Department, p.Organization,
        p.Position, p.RfidUid, p.Status);

    private static PersonnelDto ToDto(Personnel p) => ToDtoExpression(p);

    private static PersonnelWriteResponse NotFound() =>
        new(PersonnelWriteOutcome.NotFound, "Personnel record not found.", null);

    private static PersonnelWriteResponse NoSchool() =>
        new(PersonnelWriteOutcome.NoSchoolResolved,
            "No school could be resolved for this personnel record. With no tenant pinned there is " +
            "nothing to file it under.", null);

    private static PersonnelWriteResponse DuplicateNumber(string number) =>
        new(PersonnelWriteOutcome.DuplicateNumber,
            $"This school already has a personnel record with ID '{number}'.", null);

    private static PersonnelWriteResponse DuplicateRfid(string uid) =>
        new(PersonnelWriteOutcome.DuplicateRfid,
            $"This school already has an active personnel record with RFID UID '{uid}'. Remove it from " +
            "the other record first, or use a different card.", null);
}
