using EAMS.Application.Abstractions;

namespace EAMS.Infrastructure.MultiTenancy;

/// <summary>
/// The pre-auth <see cref="ISchoolContext"/> (ADR-001 D-6). There are no claims yet, so the tenant is
/// resolved once at startup from the rows that exist and pinned for the process lifetime.
///
/// <para>
/// Resolution happens in <c>InitializeEamsDatabaseAsync</c>, after migrate and seed, and is
/// <em>announced in the log</em> — including how many schools were found and, when more than one
/// exists, that the others are now hidden. The requirement this satisfies: a developer who has never
/// heard of the query filter must not lose rows without being told why.
/// </para>
///
/// <para>
/// <b>Phase 4b demoted this from "the tenant" to "the fallback".</b> The registered
/// <see cref="ISchoolContext"/> is now <c>ClaimsSchoolContext</c>, which reads the <c>school_id</c>
/// claim a device key produces and comes here only when a request carries no credentials — which is
/// still every request outside the four device-gated endpoints (ADR-001 D-6). Hence
/// <see cref="IPinnedSchoolContext"/>: the fallback needs its own name so the claims implementation
/// can ask for it without asking for itself.
/// </para>
///
/// <para>
/// Phase 6 deletes this and the fallback branch that consults it. Nothing else changes: the filters
/// are expressed against <see cref="ISchoolContext"/>.
/// </para>
/// </summary>
internal sealed class DevelopmentSchoolContext : IPinnedSchoolContext
{
    // Written once during host initialization, before the server accepts a request, then only read.
    // Interlocked gives the write a release barrier so query threads cannot observe a torn or stale
    // value — a plain field would very probably be fine here, but "probably" is not a concurrency
    // argument. Guid? is not eligible for `volatile`, hence the boxed reference.
    private object? _pinned;

    public Guid? CurrentSchoolId => (Guid?)Volatile.Read(ref _pinned);

    /// <summary>Pins the tenant for the process. Called once, from database initialization.</summary>
    public void Pin(Guid? schoolId) => Volatile.Write(ref _pinned, schoolId);
}
