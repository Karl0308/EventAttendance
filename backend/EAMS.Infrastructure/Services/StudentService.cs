using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// Technical Plan §6.2 — the students read and write surface, plus §4.4 card assignment.
///
/// <para>
/// <b>The one idea this class is built around: a student row is edited, never replaced.</b> Every
/// write path here loads the tracked entity and mutates the fields a caller owns. Nothing attaches a
/// detached <c>Student</c> and calls <c>Update()</c>, because that marks every property modified —
/// including the three ADR-001 D-2 derived cache columns — and populates <c>OriginalValues</c> from the
/// entity's own current values, so a changed <c>Section</c> arrives indistinguishable from an unchanged
/// one. <c>EamsDbContext.GuardDerivedAcademicFields</c> names that exact shape, "the shape a REST PUT
/// handler takes", as the path it was written to catch.
/// </para>
/// </summary>
internal sealed class StudentService : IStudentService
{
    private readonly EamsDbContext _db;

    /// <summary>
    /// Which tenant a newly created student is filed under, and the school a card's denormalized
    /// <c>SchoolId</c> (ADR-001 D-3) must agree with. Read through
    /// <see cref="SchoolResolution.ResolveSchoolIdAsync"/> so this agrees with the tenant the startup
    /// log announced.
    /// </summary>
    private readonly ISchoolContext _school;

    /// <summary>
    /// Where the two things this service <em>recovers from</em> get recorded.
    ///
    /// <para>
    /// Both are invisible without it. A tripped academic-cache guard is an internal-invariant breach
    /// that the caller sees as a 400 — a status that reads like "you sent something wrong" when the
    /// truth is "our own unit of work wrote a column it must not", so without a log the one signal
    /// that a programming error occurred is delivered to the only party who cannot act on it. The
    /// unique-violation arms are the opposite case: each individually is ordinary and expected, but a
    /// <em>rate</em> of them is the difference between two operators occasionally colliding and a
    /// client retrying in a loop, and nothing else in the system could tell those apart.
    /// </para>
    /// </summary>
    private readonly ILogger<StudentService> _logger;

    public StudentService(EamsDbContext db, ISchoolContext school, ILogger<StudentService> logger)
    {
        _db = db;
        _school = school;
        _logger = logger;
    }

    internal static StudentDto ToDto(Student s) => new(
        s.Id, s.StudentNumber, s.FullName,
        s.FirstName, s.MiddleName, s.LastName,
        s.Email, s.Gender, s.PhotoUrl,
        s.Course, s.YearLevel, s.Section, s.Status,
        s.Cards.Select(ToDto).ToList());

    internal static CardDto ToDto(RfidCard c) => new(c.Id, c.CardUid, c.Label, c.IsActive);

    // ------------------------------------------------------------------------------------ reads

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

    /// <summary>
    /// UID→student resolution for the mobile scan screen.
    ///
    /// <para>
    /// <b><c>!c.Student!.IsDeleted</c> is the surviving half of <c>KnownDefectTests</c> DEFECT 1.</b>
    /// That defect was closed by adding the same predicate to <c>AttendanceService.TapAsync</c>, and
    /// the comment left at that call site says the fix was needed because "StudentService filters
    /// soft-deleted students on all three of its queries; this one resolved the student through the
    /// card and did not". Two of the three filtered. This was the third, and it resolves the student
    /// through the card in exactly the way the tap path did — the fix landed on the caller that had a
    /// test rather than on the one that did not.
    /// </para>
    ///
    /// <para>
    /// Harmless until Phase 3b-1, because nothing could produce a soft-deleted student through the
    /// API; <c>DELETE /students/{id}</c> is what made it reachable. It matters more here than
    /// anywhere else in this class: §6.2 gives this one endpoint <c>attendance.capture</c> precisely
    /// so a kiosk can resolve a UID <em>without</em> being trusted to browse the roster, so under §11
    /// it is the least-trusted caller's only student read — and it was the least filtered one,
    /// returning a deleted student's full name, e-mail and photo URL while
    /// <c>GET /students/{id}</c> answered 404 for the same person.
    /// </para>
    /// </summary>
    public async Task<StudentDto?> GetByCardUidAsync(string cardUid, CancellationToken ct = default)
    {
        var uid = CardUid.Normalize(cardUid);
        var card = await _db.RfidCards.Include(c => c.Student!).ThenInclude(s => s.Cards)
            .FirstOrDefaultAsync(c => c.CardUid == uid && c.IsActive && !c.Student!.IsDeleted, ct);
        return card?.Student is null ? null : ToDto(card.Student);
    }

    // ----------------------------------------------------------------------------- create/edit

    public async Task<StudentWriteResponse> CreateAsync(
        StudentWriteRequest request, CancellationToken ct = default)
    {
        if (Reject(request, out var stored) is { } refused) return refused;

        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null)
        {
            return new StudentWriteResponse(StudentWriteOutcome.NoSchoolResolved,
                "No school could be resolved for this student. In the pre-auth build the tenant is " +
                "the single seeded school (ADR-001 D-6); with none or several, there is nothing to " +
                "file the student under.", null);
        }

        var number = stored.StudentNumber;

        if (await TakenStudentNumberAsync(schoolId.Value, number, excluding: null, ct) is { } taken)
            return taken;

        var student = new Student { SchoolId = schoolId.Value };
        Apply(stored, student);

        _db.Students.Add(student);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // The read above lost a race to a concurrent create of the same number. That is a
            // conflict, not a server fault: the index decided it, and the second caller must pick
            // another number. Detached first because EF leaves a failed insert Added, and a retained
            // Added row would shadow every later read of that key through identity resolution — the
            // same detach the freeze and the audience attach perform, for the same reason.
            _db.Entry(student).State = EntityState.Detached;

            // Information rather than Warning: one of these is a normal outcome of two people adding
            // the same student at once, and the index did its job. It is the rate that carries the
            // signal — a steady stream means a client is retrying a create it already won.
            _logger.LogInformation(
                "A create of student number {StudentNumber} lost the race to " +
                "IX_Students_SchoolId_StudentNumber and was answered 409.", number);

            return Duplicate(number, deleted: false);
        }
        catch (AcademicCacheWriteException ex)
        {
            return DerivedGuardTripped(ex);
        }

        return Saved(student, "Student created.");
    }

    public async Task<StudentWriteResponse> UpdateAsync(
        Guid id, StudentWriteRequest request, CancellationToken ct = default)
    {
        if (Reject(request, out var stored) is { } refused) return refused;

        var student = await FindAsync(id, ct);
        if (student is null) return NotFound();

        var number = stored.StudentNumber;

        if (!string.Equals(number, student.StudentNumber, StringComparison.Ordinal)
            && await TakenStudentNumberAsync(student.SchoolId, number, excluding: student.Id, ct)
                is { } taken)
        {
            return taken;
        }

        Apply(stored, student);
        student.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // Logged for the same reason the create arm is, and the reason applies at least as
            // strongly here: a rename racing another rename onto one free number is rarer than two
            // creates colliding, so a rate of them is more likely to mean a client is retrying a PUT
            // it already won than that two operators genuinely collided.
            _logger.LogInformation(
                "A rename of student {StudentId} onto number {StudentNumber} lost the race to " +
                "IX_Students_SchoolId_StudentNumber and was answered 409.", student.Id, number);

            return Duplicate(number, deleted: false);
        }
        catch (AcademicCacheWriteException ex)
        {
            return DerivedGuardTripped(ex);
        }

        return Saved(student, "Student updated.");
    }

    /// <summary>
    /// Soft delete (§4.3 <c>IsDeleted</c>).
    ///
    /// <para>
    /// <b>The student's cards are deliberately left as they are.</b> A soft-deleted student is already
    /// unreachable from the tap path — <c>AttendanceService</c> resolves a UID through
    /// <c>!c.Student!.IsDeleted</c> — so an active card cannot record attendance for them, and
    /// deactivating it here would silently rewrite the issuance history ADR-001 D-3 exists to preserve
    /// on an operation the caller did not ask for. The one consequence worth knowing is that the card
    /// keeps its slot in <c>UX_RfidCards_SchoolId_CardUid_Active</c>: re-issuing that UID to a new
    /// student needs the old card deactivated first, which is one explicit
    /// <c>DELETE /students/{id}/cards/{cardId}</c> — and that call deliberately still reaches a
    /// deleted student, or this sentence would describe a route that 404s. See
    /// <see cref="DeactivateCardAsync"/>.
    /// </para>
    ///
    /// <para>
    /// <b>What is still stranded, stated so it is not mistaken for handled:</b> the student
    /// <em>number</em>. It stays taken by the deleted row (the index is unfiltered), and
    /// <see cref="Duplicate"/> advises restoring that student — which §6.2 defines no endpoint to do.
    /// Pinned by <c>StudentSoftDeleteStrandingTests</c>.
    /// </para>
    /// </summary>
    public async Task<StudentWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var student = await FindAsync(id, ct);
        if (student is null) return NotFound();

        student.IsDeleted = true;
        student.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (AcademicCacheWriteException ex)
        {
            return DerivedGuardTripped(ex);
        }

        return Saved(student, "Student deleted.");
    }

    // ---------------------------------------------------------------------------------- cards

    public async Task<StudentCardResponse> AddCardAsync(
        Guid id, StudentCardRequest request, CancellationToken ct = default)
    {
        var student = await FindAsync(id, ct);
        if (student is null) return CardFailure(StudentWriteOutcome.NotFound, "Student not found.");

        // Normalize first, compare second (CLAUDE.md). Everything below — the existence check, the
        // stored value, and the index that decides the race — is about this value, never the raw one.
        //
        // The null coalesce is not defensive noise. `StudentCardRequest.CardUid` is non-nullable by
        // annotation, and a JSON body is not bound by annotations: `{"label":"x"}` deserializes it to
        // null, and `CardUid.Normalize` dereferences its argument. MVC's implicit-required validation
        // does answer 400 before this over HTTP — but that is exactly the reasoning `Reject` refuses to
        // accept, that a guard living only at the HTTP boundary leaves the next non-HTTP caller
        // unprotected, and `IStudentService` is the contract Phase 4 publishes to the mobile developer.
        // Null and blank mean the same thing here — nothing that can be stored — so both fall into the
        // one refusal below rather than one being a 400 and the other a NullReferenceException 500.
        var uid = CardUid.Normalize(request.CardUid ?? "");

        if (!RfidCardText.IsValidNormalizedCardUid(uid))
        {
            return CardFailure(StudentWriteOutcome.ValidationFailed,
                $"'{request.CardUid}' is not a usable card UID. UIDs are stored uppercase with " +
                "separators stripped, so it must contain at least one letter or digit and normalize " +
                $"to {RfidCardText.CardUidMaxLength} characters or fewer (this one normalized to " +
                $"{uid.Length}).");
        }

        var label = RosterText.Clean(request.Label);
        if (label is not null && label.Length > RfidCardText.LabelMaxLength)
        {
            return CardFailure(StudentWriteOutcome.ValidationFailed,
                $"Label must be {RfidCardText.LabelMaxLength} characters or fewer (got {label.Length}).");
        }

        // The SchoolId predicate is explicit rather than left to the global query filter, which is
        // inert whenever no tenant is pinned — design time, most of the test suite, any unseeded
        // start. Without it this read would search every school and could report another tenant's
        // card as the conflict, which is both wrong and a disclosure.
        if (await ActiveCardAsync(student.SchoolId, uid, ct) is { } existing)
        {
            return existing.StudentId == student.Id
                ? new StudentCardResponse(StudentWriteOutcome.Saved,
                    "That card is already active for this student.", ToDto(existing))
                : CardUidInUse(uid);
        }

        var card = new RfidCard
        {
            // ADR-001 D-3: denormalized from the student so the filtered unique index can be a single
            // index. It must equal the owner's, and it is taken from the owner rather than resolved
            // again so it cannot drift.
            SchoolId = student.SchoolId,
            StudentId = student.Id,
            CardUid = uid,
            Label = label,
            IsActive = true,
            IssuedAt = DateTime.UtcNow,
        };

        _db.RfidCards.Add(card);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // The read above lost to a concurrent assignment of the same UID. Re-read rather than
            // assume: whether this is a conflict or a no-op depends on who won, and only the database
            // knows. Detach first — EF leaves the failed insert Added, and it would otherwise shadow
            // the re-read through identity resolution and report our own losing row as the winner.
            _db.Entry(card).State = EntityState.Detached;

            var winner = await ActiveCardAsync(student.SchoolId, uid, ct);

            _logger.LogInformation(
                "An assignment of card UID {CardUid} to student {StudentId} lost the race to " +
                "UX_RfidCards_SchoolId_CardUid_Active; the active card is now held by {WinnerId}.",
                uid, student.Id, winner?.StudentId);

            return winner is not null && winner.StudentId == student.Id
                ? new StudentCardResponse(StudentWriteOutcome.Saved,
                    "That card is already active for this student.", ToDto(winner))
                : CardUidInUse(uid);
        }
        catch (AcademicCacheWriteException ex)
        {
            // The card write does not touch a Student column — but it saves the same unit of work,
            // which holds the tracked Student that FindAsync loaded. Anything else that mutated a cache
            // column on it surfaces here, as a 500, from a method that never mentioned Students.
            return CardDerivedGuardTripped(ex);
        }

        return new StudentCardResponse(StudentWriteOutcome.Saved, "Card assigned.", ToDto(card));
    }

    public async Task<StudentCardResponse> DeactivateCardAsync(
        Guid id, Guid cardId, CancellationToken ct = default)
    {
        // Deliberately NOT FindAsync: this is the one write that must still reach a soft-deleted
        // student, and resolving the owner through the !IsDeleted filter made it a dead end.
        //
        // The sequence that exposed it is an ordinary registrar correction: a student created wrongly,
        // soft-deleted, and re-created. Deleting the bad row does not release their card — the card
        // stays active, DeleteAsync explains why it must — so it keeps the only active slot in
        // UX_RfidCards_SchoolId_CardUid_Active and handing that same physical card to the corrected
        // student record answers CardUidInUse. That
        // refusal tells the operator to deactivate the existing card first, which is correct advice;
        // through FindAsync it answered 404, so the API refused an action and then refused the
        // recovery it had just recommended. Only direct SQL could clear it.
        //
        // Narrow on purpose: FindAsync is unchanged for every other caller, so AddCardAsync still
        // refuses to issue a *new* card to a deleted student. Releasing what they hold is a
        // deactivation — which ADR-001 D-3 designs to destroy nothing — while issuing to them would
        // be pretending they are on the roster.
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return CardFailure(StudentWriteOutcome.NotFound, "Student not found.");

        var card = await _db.RfidCards
            .FirstOrDefaultAsync(c => c.Id == cardId && c.StudentId == student.Id, ct);

        if (card is null)
        {
            return CardFailure(StudentWriteOutcome.CardNotFound,
                "No such card on this student. A card belongs to exactly one student; deactivating " +
                "somebody else's through this URL would be a silent cross-roster edit.");
        }

        if (!card.IsActive)
        {
            return new StudentCardResponse(StudentWriteOutcome.Saved,
                "That card was already deactivated.", ToDto(card));
        }

        card.IsActive = false;
        card.DeactivatedAt = DateTime.UtcNow;
        card.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (AcademicCacheWriteException ex)
        {
            // Same net as AddCardAsync's, for the same reason: the tracked Student loaded above shares
            // this unit of work, so a cache column mutated on it anywhere else is raised here.
            return CardDerivedGuardTripped(ex);
        }

        return new StudentCardResponse(StudentWriteOutcome.Saved,
            "Card deactivated. The row is kept so the issuance history and any past tap that names " +
            "it survive (ADR-001 D-3); the UID is now free to be re-issued.", ToDto(card));
    }

    // --------------------------------------------------------------------------------- plumbing

    private Task<Student?> FindAsync(Guid id, CancellationToken ct) =>
        _db.Students.Include(s => s.Cards)
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);

    private Task<RfidCard?> ActiveCardAsync(Guid schoolId, string normalizedUid, CancellationToken ct) =>
        _db.RfidCards.FirstOrDefaultAsync(
            c => c.SchoolId == schoolId && c.CardUid == normalizedUid && c.IsActive, ct);

    /// <summary>
    /// Both refusals a request can earn before anything is read: a supplied derived cache column, and
    /// a §4.3 field rule. Returns null when the request is good, and hands back the exact values that
    /// will be stored.
    ///
    /// <para>
    /// In the service rather than the controller for the reason <c>EventService.Validate</c> records:
    /// the service is what writes the columns, so a guard on the HTTP boundary alone would leave the
    /// next non-HTTP caller — the §10 importer, a future refresher — unprotected.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="DerivedFieldsSupplied"/> runs first, deliberately.</b> A request that both names a
    /// derived column and gets a field wrong is told about the derived column, because that is the one
    /// the caller must stop sending — the field error is fixable by editing a value, the derived one
    /// is only fixable by understanding ADR-001 D-2.
    /// </para>
    /// </summary>
    private static StudentWriteResponse? Reject(StudentWriteRequest request, out StoredStudent stored)
    {
        if (DerivedFieldsSupplied(request) is { } derived)
        {
            stored = default;
            return derived;
        }

        return Validate(request, out stored);
    }

    /// <summary>
    /// A request's values in exactly the form they will be written — cleaned, normalized, and already
    /// checked against §4.3's widths.
    ///
    /// <para>
    /// <b>Its purpose is to make the old bug unrepresentable rather than merely fixed.</b> Validation
    /// and storage used to derive their values independently: <c>Validate</c> inspected
    /// <c>request.FirstName</c> while <c>Apply</c> stored <c>RosterText.Clean(request.FirstName)</c>,
    /// so "the value that was checked" and "the value that was written" were two different strings and
    /// nothing made them agree. Cleaning once, here, and carrying the result through means the three
    /// required columns are <c>string</c> rather than <c>string?</c> by the time anything can store
    /// them — so the null-forgiving operators that used to paper over the gap are gone, and the
    /// compiler enforces what a comment used to assert.
    /// </para>
    /// </summary>
    private readonly record struct StoredStudent(
        string StudentNumber, string FirstName, string? MiddleName, string LastName,
        string? Email, string? Gender, string? PhotoUrl, string Status);

    /// <summary>
    /// ADR-001 D-2, enforced at the contract rather than at the column.
    ///
    /// <para>
    /// The three names are read from <see cref="Student.DerivedAcademicPropertyNames"/> — the same list
    /// the <c>SaveChanges</c> guard uses — so the two cannot disagree about which columns are derived.
    /// The comparison is case-insensitive because the wire format is camelCase (<c>yearLevel</c>) and
    /// the property names are Pascal; matching only one of them would leave the other silently
    /// accepted, which is the failure this whole check exists to remove.
    /// </para>
    /// </summary>
    private static StudentWriteResponse? DerivedFieldsSupplied(StudentWriteRequest request)
    {
        if (request.UnmappedFields is not { Count: > 0 } sent) return null;

        var derived = Student.DerivedAcademicPropertyNames
            .Where(name => sent.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (derived.Count == 0) return null;

        return new StudentWriteResponse(StudentWriteOutcome.FieldIsDerived,
            $"{string.Join(", ", derived.Select(d => $"Students.{d}"))} " +
            $"{(derived.Count == 1 ? "is a" : "are")} derived read-only cache (ADR-001 D-2) and " +
            "cannot be written through this endpoint. It is single-valued, and 12 of the 52 students " +
            "in the real roster sit in more than one section, so it cannot represent them and must " +
            "never be a source of truth or a join key. Write the academic tables instead: Enrollments " +
            "(student × CourseOffering) for sections and courses, StudentTermRecords for programme, " +
            "college and year level. Re-send this request without " +
            $"{string.Join(", ", derived.Select(d => $"'{Camel(d)}'"))}.", null);
    }

    /// <summary>The wire spelling of a property name, so the message names what the client actually sent.</summary>
    private static string Camel(string propertyName) =>
        char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    /// <summary>
    /// §4.3's column rules, applied to the values that will actually be written. Returns null when the
    /// request is good, and yields those values through <paramref name="stored"/>.
    ///
    /// <para>
    /// <b>Every check runs on the cleaned value, and that is the fix rather than a tidy-up.</b> Each
    /// field is cleaned once, here, and both the check and the write use that one string. Previously
    /// the check ran on the request and the write ran on <c>RosterText.Clean(request…)</c> — two
    /// definitions of "empty" and two of "too long", with a reachable gap between them: a zero-width
    /// space is not whitespace, so it survived <c>IsNullOrWhiteSpace</c>, cleaned to <c>null</c>, and
    /// reached a NOT NULL column as a 500. <see cref="StudentText.IsValidRequiredValue"/> carries the
    /// full account, including the NFC-expansion half.
    /// </para>
    ///
    /// <para>
    /// <c>Status</c> is the one field checked on the raw request, because it is the one whose rule is
    /// about the request rather than the column: null and blank are §4.3's default (a create form with
    /// no status field must still produce a valid row), and anything else must be one of the documented
    /// three. <see cref="RosterText"/> is not involved — it is a closed value set, not free text.
    /// </para>
    /// </summary>
    private static StudentWriteResponse? Validate(StudentWriteRequest request, out StoredStudent stored)
    {
        stored = default;

        // Cleaned, never uppercased, and deliberately not CardUid.Normalize'd. A student number is not
        // a card UID — they are separate columns fed by separate source columns — and the two carry
        // different rules on purpose: StudentNumber holds the registrar's value verbatim, while
        // RfidCards.CardUid holds the uppercased, punctuation-stripped form a reader can be matched
        // against. The §10 importer stores them exactly this way, and a manual create that normalized
        // the number differently would produce a student the next import cannot match.
        var number = RosterText.Clean(request.StudentNumber);
        if (!StudentText.IsValidRequiredValue(number, StudentText.StudentNumberMaxLength))
        {
            return Invalid(
                "StudentNumber is required and must be " +
                $"{StudentText.StudentNumberMaxLength} characters or fewer.");
        }

        var firstName = RosterText.Clean(request.FirstName);
        if (!StudentText.IsValidRequiredValue(firstName))
            return Invalid($"FirstName is required and must be {StudentText.NameMaxLength} characters or fewer.");

        var lastName = RosterText.Clean(request.LastName);
        if (!StudentText.IsValidRequiredValue(lastName))
            return Invalid($"LastName is required and must be {StudentText.NameMaxLength} characters or fewer.");

        // CleanName, not Clean: it additionally folds the roster's '-' placeholder to null, so a manual
        // entry and an import agree about "no middle name" instead of rendering "Maria - Santos".
        var middleName = RosterText.CleanName(request.MiddleName);
        if (!StudentText.IsValidOptionalValue(middleName, StudentText.NameMaxLength))
            return Invalid($"MiddleName must be {StudentText.NameMaxLength} characters or fewer.");

        var email = RosterText.CleanEmail(request.Email);
        if (!StudentText.IsValidOptionalValue(email, StudentText.EmailMaxLength))
            return Invalid($"Email must be {StudentText.EmailMaxLength} characters or fewer.");

        var gender = RosterText.Clean(request.Gender);
        if (!StudentText.IsValidOptionalValue(gender, StudentText.GenderMaxLength))
            return Invalid($"Gender must be {StudentText.GenderMaxLength} characters or fewer.");

        var photoUrl = RosterText.Clean(request.PhotoUrl);
        if (!StudentText.IsValidOptionalValue(photoUrl, StudentText.PhotoUrlMaxLength))
            return Invalid($"PhotoUrl must be {StudentText.PhotoUrlMaxLength} characters or fewer.");

        if (!string.IsNullOrWhiteSpace(request.Status)
            && !StudentStatus.TryNormalize(request.Status, out _))
        {
            return Invalid(
                $"Status '{request.Status}' is not one of {string.Join(", ", StudentStatus.All)}.");
        }

        stored = new StoredStudent(
            number, firstName, middleName, lastName, email, gender, photoUrl,
            StudentStatus.TryNormalize(request.Status, out var status) ? status : StudentStatus.Default);

        return null;
    }

    /// <summary>
    /// Copies the validated values onto the entity. Deliberately does not touch <c>SchoolId</c>,
    /// <c>IsDeleted</c>, the ADR-001 D-2 cache columns, or the §10 import columns
    /// (<c>SisExternalId</c>, <c>LastSyncedAt</c>, <c>AlternateEmail</c>) — one method, so create and
    /// update cannot drift about which fields a caller owns.
    ///
    /// <para>
    /// It performs no cleaning and no defaulting of its own: <see cref="Validate"/> produced these
    /// exact strings and checked these exact strings. That is deliberate — the previous version cleaned
    /// here and validated elsewhere, which is precisely how the two drifted apart. Nothing is
    /// null-forgiven, because <see cref="StoredStudent"/> types the required three as non-nullable.
    /// </para>
    /// </summary>
    private static void Apply(StoredStudent stored, Student student)
    {
        student.StudentNumber = stored.StudentNumber;
        student.FirstName = stored.FirstName;
        student.MiddleName = stored.MiddleName;
        student.LastName = stored.LastName;
        student.Email = stored.Email;
        student.Gender = stored.Gender;
        student.PhotoUrl = stored.PhotoUrl;
        student.Status = stored.Status;
    }

    /// <summary>
    /// The <c>UNIQUE(SchoolId, StudentNumber)</c> pre-check.
    ///
    /// <para>
    /// <b>Soft-deleted students are included, and that is not an oversight.</b> The index is
    /// unfiltered, so a deleted row still holds its number; excluding them here would produce a clean
    /// pre-check followed by a unique violation, and the caller would be told "conflict" by the
    /// database with no idea which row it collided with. Named explicitly instead, because the fix
    /// differs: pick another number, or restore the student who already has it.
    /// </para>
    /// </summary>
    private async Task<StudentWriteResponse?> TakenStudentNumberAsync(
        Guid schoolId, string number, Guid? excluding, CancellationToken ct)
    {
        var clash = await _db.Students.AsNoTracking()
            .Where(s => s.SchoolId == schoolId && s.StudentNumber == number)
            .Where(s => excluding == null || s.Id != excluding)
            .Select(s => new { s.IsDeleted })
            .FirstOrDefaultAsync(ct);

        return clash is null ? null : Duplicate(number, clash.IsDeleted);
    }

    private static StudentWriteResponse Saved(Student student, string message) =>
        new(StudentWriteOutcome.Saved, message, ToDto(student));

    private static StudentWriteResponse NotFound() =>
        new(StudentWriteOutcome.NotFound, "Student not found.", null);

    private static StudentWriteResponse Invalid(string message) =>
        new(StudentWriteOutcome.ValidationFailed, message, null);

    private static StudentWriteResponse Duplicate(string number, bool deleted) =>
        new(StudentWriteOutcome.DuplicateStudentNumber,
            deleted
                ? $"Student number '{number}' belongs to a student that has been deleted. The " +
                  "UNIQUE(SchoolId, StudentNumber) index is not filtered on IsDeleted, so the number " +
                  "is still taken — restore that student rather than creating a second one under the " +
                  "same number."
                : $"Student number '{number}' is already in use in this school.",
            null);

    /// <summary>
    /// The safety net under <see cref="Apply"/>, not the guard.
    ///
    /// <para>
    /// <c>AcademicCacheWriteException</c> is a programming error by design and would otherwise leave
    /// this service as a 500. Nothing on these paths writes a cache column — <see cref="Apply"/> does
    /// not name one, and <see cref="DerivedFieldsSupplied"/> has already refused a request that asked
    /// for one — so reaching here means some other writer mutated a tracked <c>Student</c> in the same
    /// unit of work. The caller still gets a 400 that names the rule and the tables to write instead,
    /// because the exception's own message is the best explanation anyone has of it.
    /// </para>
    /// </summary>
    private StudentWriteResponse DerivedGuardTripped(AcademicCacheWriteException ex)
    {
        LogDerivedGuardTripped(ex);
        return new StudentWriteResponse(StudentWriteOutcome.FieldIsDerived, ex.Message, null);
    }

    /// <summary>
    /// <inheritdoc cref="DerivedGuardTripped"/>
    ///
    /// <para>
    /// The card-shaped twin. Both card writes save a unit of work that holds a tracked
    /// <c>Student</c>, so both can raise this exception without ever naming a <c>Students</c> column —
    /// which is precisely why the net belongs on them too rather than only on the three methods that
    /// obviously touch the table.
    /// </para>
    /// </summary>
    private StudentCardResponse CardDerivedGuardTripped(AcademicCacheWriteException ex)
    {
        LogDerivedGuardTripped(ex);
        return new StudentCardResponse(StudentWriteOutcome.FieldIsDerived, ex.Message, null);
    }

    /// <summary>
    /// One log call for both response shapes, so the two cannot drift about what an invariant breach
    /// is worth saying or at what level.
    /// </summary>
    private void LogDerivedGuardTripped(AcademicCacheWriteException ex) =>
        // Error, not Warning: reaching here means an invariant this class believes it maintains was
        // broken by code running inside its own unit of work. The caller's 400 is the right answer to
        // give a client and the wrong place to leave the only record of it — nobody reads another
        // party's 4xx as a bug report about our code.
        _logger.LogError(ex,
            "The ADR-001 D-2 academic cache guard refused a write on Students.{PropertyName} for " +
            "student {StudentNumber} during a student write. Nothing in StudentService writes those " +
            "columns, so another writer mutated a tracked Student in this unit of work — that is a " +
            "programming error, not caller input, even though the caller receives a 400.",
            ex.PropertyName, ex.StudentNumber);

    private static StudentCardResponse CardFailure(StudentWriteOutcome outcome, string message) =>
        new(outcome, message, null);

    private static StudentCardResponse CardUidInUse(string uid) =>
        new(StudentWriteOutcome.CardUidInUse,
            $"Card UID '{uid}' is already active on another student in this school. ADR-001 D-3 " +
            "scopes uniqueness to UNIQUE(SchoolId, CardUid) WHERE IsActive = 1, so one UID identifies " +
            "one student at a time; deactivate the existing card first and this one can be issued.",
            null);
}
