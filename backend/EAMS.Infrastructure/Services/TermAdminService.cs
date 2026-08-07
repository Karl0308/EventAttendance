using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// D-53's admin write surface over <c>Terms</c> — see <see cref="ITermAdminService"/> for why terms are
/// the one academic table a human authors and why these writes are not on
/// <see cref="IAcademicReferenceService"/>.
///
/// <para>
/// <b>Two indexes decide everything in this class, and they decide it differently.</b>
/// <c>UX_Terms_SchoolId_Code</c> is unfiltered and rejects a duplicate code: this class checks first
/// <em>and</em> catches the violation, because the check and the insert are two statements and the
/// second operator to press Create in the same second is decided by the index, not by the check.
/// <c>UX_Terms_SchoolId_Current</c> is filtered (<c>WHERE [IsCurrent] = 1</c>) and is never allowed to
/// reject anything: <see cref="SetCurrentAsync"/> moves the flag with a single statement that cannot
/// transiently break it, so there is no violation to recover from.
/// </para>
///
/// <para>
/// <b>Every statement here whose result set could span schools carries an explicit <c>SchoolId</c>
/// predicate</b> rather than leaning on the global query filter, which is inert whenever no tenant is
/// pinned — design time, most of the test suite, any unseeded start. That is two of them, and they are
/// the two the reasoning is about: <see cref="TakenCodeAsync"/>, whose duplicate check would otherwise
/// search every school and report another tenant's term as the conflict, and the flag move in
/// <see cref="SetCurrentAsync"/>, whose <c>WHERE</c> matches on <c>IsCurrent</c> and would otherwise
/// clear a flag belonging to somebody else entirely. The remaining reads — the loads in
/// <see cref="UpdateAsync"/> and <see cref="SetCurrentAsync"/>, and <c>ReadBackAsync</c> — are primary-key
/// lookups, which resolve one row by identity and have no cross-school set to scope; the rule does not
/// reach them and adding it there would say nothing.
/// </para>
/// </summary>
internal sealed class TermAdminService : ITermAdminService
{
    private readonly EamsDbContext _db;

    /// <summary>
    /// Which tenant a newly created term is filed under. Read through
    /// <see cref="SchoolResolution.ResolveSchoolIdAsync"/> so this agrees with the tenant the startup
    /// log announced rather than inventing a second answer.
    /// </summary>
    private readonly ISchoolContext _school;

    /// <summary>
    /// Where the one thing this service <em>recovers from</em> gets recorded: a create or a rename that
    /// lost the duplicate-code race to the index. Each one individually is ordinary — two operators
    /// setting up next semester at the same time — but a rate of them is the difference between that
    /// and a client retrying a request it already won, and nothing else in the system could tell those
    /// apart.
    /// </summary>
    private readonly ILogger<TermAdminService> _logger;

    public TermAdminService(EamsDbContext db, ISchoolContext school, ILogger<TermAdminService> logger)
    {
        _db = db;
        _school = school;
        _logger = logger;
    }

    private static TermDto ToDto(Term t) => new(
        t.Id, t.Code, t.SchoolYear, t.Semester, t.IsCurrent, t.StartsOn, t.EndsOn);

    // ------------------------------------------------------------------------------ create/edit

    public async Task<TermWriteResponse> CreateAsync(
        TermWriteRequest request, CancellationToken ct = default)
    {
        if (Validate(request) is { } refused) return refused;

        // The tenant comes from the context, never from the request — the same refusal StudentService,
        // EventService and DeviceService make, through the same helper.
        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null)
        {
            // NoSchoolResolved, not ValidationFailed, and the difference is the whole point of the
            // member existing: nothing the operator can type on this form will fix it, so answering
            // 400 "check your input" sends them to re-read a body that was never the problem. The
            // SPA's NewTermDialog branches on this token to say "this needs somebody with database
            // access" instead — a branch that was unreachable while this returned ValidationFailed.
            return new TermWriteResponse(
                TermWriteOutcome.NoSchoolResolved,
                "No school could be resolved for this term. In the pre-auth build the tenant is the " +
                "single seeded school (ADR-001 D-6); with none or several, there is nothing to file " +
                "the term under.",
                null);
        }

        if (await TakenCodeAsync(schoolId.Value, request.Code, excluding: null, ct) is { } taken)
            return taken;

        var term = new Term
        {
            SchoolId = schoolId.Value,

            // Stored exactly as authored. D-53 keeps a term code un-normalized, so nothing here calls
            // AcademicKey — the value that was validated is the value that is written.
            Code = request.Code,
            SchoolYear = request.SchoolYear,
            Semester = request.Semester,
            StartsOn = request.StartsOn,
            EndsOn = request.EndsOn,

            // Never current on creation, and not because the flag was forgotten: moving it is a
            // two-row operation under a filtered unique index, so it belongs to SetCurrentAsync alone.
            // Keeping it out here is also what makes the catch below unambiguous — with IsCurrent
            // fixed at false, UX_Terms_SchoolId_Code is the only index an insert can break, so
            // answering TermCodeExists is a fact rather than a guess between two indexes.
            IsCurrent = false,
        };

        _db.Terms.Add(term);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // The read above lost a race to a concurrent create of the same code. That is a conflict,
            // not a server fault: the index decided it, and the second caller must pick another code.
            // Detached first because EF leaves a failed insert Added, and a retained Added row would
            // shadow every later read of that key through identity resolution — the same detach
            // StudentService, the freeze and the audience attach all perform, for the same reason.
            _db.Entry(term).State = EntityState.Detached;

            _logger.LogInformation(
                "A create of term code {TermCode} lost the race to UX_Terms_SchoolId_Code and was " +
                "answered 409.", request.Code);

            return Duplicate(request.Code);
        }

        return new TermWriteResponse(
            TermWriteOutcome.Saved,
            "Term created. It is not the current term — PATCH /academic/terms/{id}/current makes it " +
            "one, which is also what clears whichever term holds the flag now.",
            ToDto(term));
    }

    public async Task<TermWriteResponse> UpdateAsync(
        Guid id, TermWriteRequest request, CancellationToken ct = default)
    {
        if (Validate(request) is { } refused) return refused;

        var term = await _db.Terms.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (term is null) return NotFound();

        // Ordinal, and only when the code actually moved: a PUT that re-sends the term's own code is
        // the ordinary case (the form round-trips every field), and checking it against the index
        // would report the row as its own duplicate.
        if (!string.Equals(request.Code, term.Code, StringComparison.Ordinal)
            && await TakenCodeAsync(term.SchoolId, request.Code, excluding: term.Id, ct) is { } taken)
        {
            return taken;
        }

        term.Code = request.Code;
        term.SchoolYear = request.SchoolYear;
        term.Semester = request.Semester;
        term.StartsOn = request.StartsOn;
        term.EndsOn = request.EndsOn;

        // IsCurrent is deliberately untouched. Editing a term's display fields must not move the
        // school's current-term flag as a side effect — that is SetCurrentAsync, and it is a different
        // statement about a different subject.
        term.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            // Logged for the same reason the create arm is, and the reason applies at least as
            // strongly here: two renames onto one free code is rarer than two creates colliding, so a
            // rate of them more likely means a client is retrying a PUT it already won.
            _logger.LogInformation(
                "A rename of term {TermId} onto code {TermCode} lost the race to " +
                "UX_Terms_SchoolId_Code and was answered 409.", term.Id, request.Code);

            return Duplicate(request.Code);
        }

        return new TermWriteResponse(TermWriteOutcome.Saved, "Term updated.", ToDto(term));
    }

    // --------------------------------------------------------------------------- the current flag

    /// <summary>
    /// <inheritdoc cref="ITermAdminService.SetCurrentAsync" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One <c>UPDATE</c> statement, and that is the whole design of this method.</b> Setting the flag
    /// means clearing the incumbent and setting this term, and
    /// <c>UX_Terms_SchoolId_Current</c> — <c>UNIQUE(SchoolId) WHERE [IsCurrent] = 1</c> — rejects any
    /// instant in which both hold it. Tracked writes cannot express that safely: EF orders same-table
    /// commands by its own graph rules rather than by index safety, so the <c>UPDATE</c> that sets this
    /// term can be issued before the one that clears the incumbent, and the save fails with a
    /// duplicate-key error that has nothing to do with what the operator asked for.
    /// <c>SisImportService.EnsureBuiltInProfileAsync</c> hit exactly this against its own
    /// <c>WHERE IsActive = 1</c> index and records the same reasoning.
    /// </para>
    ///
    /// <para>
    /// <b>Both rows move in one statement rather than in two, which is the difference from that
    /// precedent.</b> Two statements (clear, then set) are each atomic but the pair is not — the
    /// production registration enables <c>EnableRetryOnFailure</c>, and EF refuses user-initiated
    /// transactions under a retrying execution strategy, so wrapping them would mean threading
    /// <c>CreateExecutionStrategy</c> through for a two-row write. Between the two statements the school
    /// has <em>no</em> current term, and every term-defaulting read in the system — the roster import
    /// picker, <c>GET /academic/course-offerings</c> — answers empty in that window rather than
    /// answering wrongly. One statement removes the window instead of shrinking it: SQL Server checks a
    /// unique index at statement boundaries, so the row leaving the filtered set and the row entering it
    /// are one indivisible change.
    /// </para>
    ///
    /// <para>
    /// The consequence of not going through <c>SaveChanges</c> is that nothing is tracked, so the DTO
    /// below is read back from the database rather than composed from the entity that was written. That
    /// is the honest source anyway: the row a caller is told about is the row the statement produced.
    /// </para>
    /// </remarks>
    public async Task<TermWriteResponse> SetCurrentAsync(
        Guid id, bool isCurrent, CancellationToken ct = default)
    {
        var term = await _db.Terms.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.Id, t.SchoolId, t.IsCurrent, t.Code })
            .FirstOrDefaultAsync(ct);

        if (term is null) return NotFound();

        if (term.IsCurrent == isCurrent)
        {
            // Idempotent, and short-circuited rather than re-written: a term that is already in the
            // asked-for state has nothing to change, and bumping UpdatedAt on a no-op would make an
            // audit column record clicks rather than edits. A retried request is safe.
            return await ReadBackAsync(
                id,
                isCurrent
                    ? "That term is already the current one."
                    : "That term was already not the current one.",
                ct);
        }

        if (!isCurrent)
        {
            // Retiring: one row, and no index question at all — a row leaving the filtered set can
            // never collide with anything. This is the whole of "retiring a term" (D-53 offers no
            // deletion, because a term with a batch imported against it cannot be removed without data
            // loss), and it leaves the school with no current term until one is set.
            await _db.Terms
                .Where(t => t.Id == id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.IsCurrent, false)
                          .SetProperty(t => t.UpdatedAt, DateTime.UtcNow),
                    ct);

            return await ReadBackAsync(
                id,
                "That term is no longer the current one. The school now has no current term: the " +
                "roster-import picker will not default, and GET /academic/terms/current answers 404 " +
                "until a term is made current.",
                ct);
        }

        // The incumbent and the new holder in one statement — see the remarks. The SchoolId predicate
        // scopes it to this term's own school; without it, an unpinned tenant would clear the current
        // term of every school in the database. IsCurrent is assigned from the row's own identity
        // rather than as two literals, which is what keeps it a single statement.
        await _db.Terms
            .Where(t => t.SchoolId == term.SchoolId && (t.Id == id || t.IsCurrent))
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.IsCurrent, t => t.Id == id)
                      .SetProperty(t => t.UpdatedAt, DateTime.UtcNow),
                ct);

        return await ReadBackAsync(
            id,
            "That term is now the current one; any term that held the flag no longer does.",
            ct);
    }

    // --------------------------------------------------------------------------------- plumbing

    /// <summary>
    /// Re-reads the row a statement produced, so the response describes the database rather than this
    /// method's expectation of it. The row cannot have vanished — there is no delete route — so a null
    /// here would be a genuine surprise and is reported as one rather than silently becoming a 404.
    /// </summary>
    private async Task<TermWriteResponse> ReadBackAsync(Guid id, string message, CancellationToken ct)
    {
        var term = await _db.Terms.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

        return term is null
            ? NotFound()
            : new TermWriteResponse(TermWriteOutcome.Saved, message, ToDto(term));
    }

    /// <summary>
    /// The <c>Terms</c> column rules, checked in the service rather than the controller so the next
    /// caller that is not HTTP inherits them — the same placement, and the same reason,
    /// <c>DeviceService.Validate</c> and <c>EventService.Validate</c> record.
    ///
    /// <para>
    /// Null is "the request is good". Every message names the field and the limit, because the caller
    /// is an operator typing into a form and "invalid request" is not something they can act on.
    /// </para>
    /// </summary>
    private static TermWriteResponse? Validate(TermWriteRequest request)
    {
        if (!TermText.IsValidCode(request.Code))
        {
            return Invalid(
                $"Code is required, must be {TermText.CodeMaxLength} characters or fewer, and may not " +
                "start or end with whitespace. It is stored exactly as typed — a term code is " +
                "operator-authored and is deliberately not normalized — so ' 2025-2026-1' and " +
                "'2025-2026-1' would be two terms that look identical in every picker.");
        }

        if (!TermText.IsValidSchoolYear(request.SchoolYear))
        {
            return Invalid(
                $"SchoolYear is required and must be {TermText.SchoolYearMaxLength} characters or " +
                "fewer, with no surrounding whitespace — e.g. '2025-2026'.");
        }

        if (!TermText.IsValidSemester(request.Semester))
        {
            return Invalid(
                $"Semester is required and must be {TermText.SemesterMaxLength} characters or fewer, " +
                "with no surrounding whitespace — e.g. '1st Semester'.");
        }

        if (!TermText.IsValidRange(request.StartsOn, request.EndsOn))
        {
            return Invalid(
                $"EndsOn ({request.EndsOn:yyyy-MM-dd}) is before StartsOn " +
                $"({request.StartsOn:yyyy-MM-dd}). Both dates are optional — the SIS export has no " +
                "term-date columns, so a real term usually carries neither — but a term that ends " +
                "before it starts contains no days at all.");
        }

        return null;
    }

    /// <summary>
    /// The duplicate-code pre-check. It is a courtesy, not the guard: the guard is
    /// <c>UX_Terms_SchoolId_Code</c>, and both write paths catch its violation as well. What this buys
    /// is a message that names the conflict before a row is staged, which is the answer an operator can
    /// act on.
    /// </summary>
    private async Task<TermWriteResponse?> TakenCodeAsync(
        Guid schoolId, string code, Guid? excluding, CancellationToken ct)
    {
        // Compared exactly as stored, because that is what the index compares. Note that SQL Server's
        // collation makes this comparison case-insensitive and trailing-space-insensitive, so
        // '2025-2026-1' and '2025-2026-1 ' are already one code to the index — the check and the index
        // agree because both are the database's own comparison rather than a rule written here.
        var clash = await _db.Terms.AsNoTracking()
            .Where(t => t.SchoolId == schoolId && t.Code == code)
            .Where(t => excluding == null || t.Id != excluding)
            // Projected to a nullable id rather than to Guid, so "no row" is null instead of
            // Guid.Empty — a default-valued struct cannot be told apart from a real row whose key
            // happens to be that value, which is the shape of bug this repo's paging tiebreakers exist
            // to avoid one level down.
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

        return clash is null ? null : Duplicate(code);
    }

    private static TermWriteResponse Duplicate(string code) =>
        new(TermWriteOutcome.TermCodeExists,
            $"A term with code '{code}' already exists in this school. Term codes are unique per " +
            "school (UX_Terms_SchoolId_Code) — pick another code, or edit the term that already " +
            "holds this one.",
            null);

    private static TermWriteResponse NotFound() =>
        new(TermWriteOutcome.NotFound, "Term not found.", null);

    private static TermWriteResponse Invalid(string message) =>
        new(TermWriteOutcome.ValidationFailed, message, null);
}
