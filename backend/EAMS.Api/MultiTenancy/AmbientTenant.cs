namespace EAMS.Api.MultiTenancy;

/// <summary>
/// The tenant of a DI scope that has no <c>HttpContext</c> to read one from (ADR-004 <b>D-54.3</b>).
///
/// <para>
/// <b>The defect this exists for.</b> <see cref="ClaimsSchoolContext"/> answers <c>null</c> when there
/// is no <c>HttpContext</c>, and <c>ISchoolContext</c> documents <c>null</c> as <em>"do not filter"</em>.
/// That is correct for the two cases it was written for — startup migration/seeding and design-time
/// model building — and silently catastrophic for a third that did not exist when it was written: a
/// <c>BackgroundService</c>'s scope. A roster import running in one would have switched off every
/// global query filter in <c>EamsDbContext</c> for the whole run and upserted across every school in
/// the database. Not a leak — a write. And it would have passed the entire suite, because the suite
/// builds one school and with one school a filtered query and an unfiltered one return the same rows.
/// </para>
///
/// <para>
/// <b>Two operations, not one, and that separation is the design decision.</b> The obvious API is a
/// single <c>Pin(schoolId)</c> that both declares and supplies. It is rejected, because the runner
/// cannot know the tenant at the instant it opens the scope — it has a batch id and has to get from
/// that to a school. A one-call API forces that lookup to happen in some *other* scope, and that other
/// scope declares nothing, so it runs unfiltered: the defect relocated rather than fixed.
/// </para>
///
/// <para>
/// Splitting them means the window between "this scope is background" and "this scope knows its
/// school" is a window in which <b>every query throws</b>. Choose the marker by which failures it
/// makes impossible, not by which call sequence is shortest:
/// <list type="bullet">
///   <item>
///     <b>Declared, not yet pinned → throws.</b> The only alternative answers are <c>null</c>
///     (unfiltered — the original defect, restored) and the process pin (a tenant belonging to a
///     different question entirely). An import that cannot say which school it is for must not run,
///     and the exception it raises is a failure mode the <c>Failed</c> status already knows how to
///     report.
///   </item>
///   <item>
///     <b>Pinned twice → throws.</b> A scope has exactly one tenant. Re-pinning would move the tenant
///     under work the same scope has already done, and the rows written before and after would be
///     filed under different schools with nothing in the log to say so.
///   </item>
///   <item>
///     <b><see cref="Guid.Empty"/> → throws.</b> It is the default, not a tenant. Filtering on it
///     makes every query return zero rows for reasons invisible at the call site, which is the exact
///     hazard <c>ISchoolContext</c>'s own documentation gives as the reason <c>null</c> means
///     "unfiltered" rather than "empty".
///   </item>
///   <item>
///     <b>Declared nothing → <c>null</c>, unchanged.</b> Startup migration, seeding, <c>create-admin</c>
///     and design-time model building never touch this type, so they keep today's behaviour and the
///     host still boots. A blanket "throw when there is no <c>HttpContext</c>" would have traded a
///     silent data defect for a dead site.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// <b>What it does not close.</b> A future background scope that declares <em>nothing at all</em> is
/// still unfiltered. That hole cannot be closed from here without breaking startup, which is
/// legitimately untenanted and reaches this same branch. It is closed by the caller declaring first —
/// hence <see cref="DeclareBackgroundScope"/> being a separate, callable-before-anything-else step
/// rather than a parameter on the pin.
/// </para>
///
/// <para>
/// <b>Scoped, and only consulted when there is no <c>HttpContext</c>.</b> Resolving this inside a
/// request scope and pinning it is therefore inert — <see cref="ClaimsSchoolContext"/> never reaches
/// the ambient branch while a request is in flight. That is deliberate: a request's tenant comes from
/// its claims, and nothing running inside one should be able to move it.
/// </para>
/// </summary>
internal sealed class AmbientTenant
{
    /// <summary>
    /// The whole state, in one reference, so a reader can never observe "declared" and the school it
    /// was declared with out of order. <c>null</c> means never declared. <c>Volatile</c> for the same
    /// reason <c>DevelopmentSchoolContext</c> gives: a plain field would very probably be fine, and
    /// "probably" is not a concurrency argument.
    /// </summary>
    private sealed record Declaration(Guid? SchoolId);

    private object? _declaration;

    private Declaration? Current => (Declaration?)Volatile.Read(ref _declaration);

    /// <summary>
    /// Declares that this scope is a background scope: it has no <c>HttpContext</c>, and it is not
    /// startup. From here until <see cref="PinSchool"/> is called, resolving the tenant <b>throws</b>.
    ///
    /// <para>
    /// Call it first, before anything in the scope touches the database. Idempotent, and it never
    /// clears a school already pinned.
    /// </para>
    /// </summary>
    public void DeclareBackgroundScope()
    {
        if (Current is not null) return;

        Volatile.Write(ref _declaration, new Declaration(null));
    }

    /// <summary>
    /// Names the school this scope's work belongs to. Implies <see cref="DeclareBackgroundScope"/>,
    /// so a caller that can name its tenant up front needs only this call.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="schoolId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="InvalidOperationException">This scope already has a school.</exception>
    public void PinSchool(Guid schoolId)
    {
        if (schoolId == Guid.Empty)
        {
            throw new ArgumentException(
                "Guid.Empty is not a tenant, it is the default value. Filtering on it makes every " +
                "query in this scope return zero rows for reasons invisible at the call site. Pass " +
                "the school the work actually belongs to.",
                nameof(schoolId));
        }

        if (Current is { SchoolId: { } existing })
        {
            throw new InvalidOperationException(
                $"This scope is already pinned to school {existing} and cannot be re-pinned to " +
                $"{schoolId}. A DI scope has exactly one tenant; moving it part-way through would " +
                "file the rows written before and after under different schools. Open a second scope " +
                "for the second tenant.");
        }

        Volatile.Write(ref _declaration, new Declaration(schoolId));
    }

    /// <summary>
    /// The tenant for a scope with no <c>HttpContext</c>: the pinned school, or <c>null</c> — meaning
    /// "do not filter" — when nothing was ever declared.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The scope declared itself a background scope and never named a school. Deliberate: see the
    /// type's remarks.
    /// </exception>
    public Guid? Resolve()
    {
        var declaration = Current;

        if (declaration is null) return null;
        if (declaration.SchoolId is { } schoolId) return schoolId;

        throw new InvalidOperationException(
            $"This DI scope called {nameof(AmbientTenant)}.{nameof(DeclareBackgroundScope)}() but " +
            $"never called {nameof(PinSchool)}(schoolId), so there is no honest answer to which " +
            "school its queries belong to. Pin the tenant — for a roster import run, from " +
            "SisImportBatch.SchoolId — before the scope touches the database. Answering null here " +
            "would mean 'do not filter', which would let this work read and write across every " +
            "school in the database (ADR-004 D-54.3).");
    }
}
