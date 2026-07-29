namespace EAMS.Application.Abstractions;

/// <summary>
/// Technical Plan §11's multi-tenant guard: <em>"every query is filtered by <c>SchoolId</c> from the
/// user's claims (EF Core global query filter)"</em>. ADR-001 D-6 defers auth but installs this seam
/// now, because a missing tenant filter is the one thing that cannot be retrofitted cheaply — it
/// means re-reviewing every query ever written, and getting it wrong is a cross-tenant data leak.
///
/// <para>
/// The global query filters read this interface, so Phase 6 replaces one DI registration with a
/// claims-reading implementation and no entity configuration changes.
/// </para>
///
/// <para>
/// <b><see cref="CurrentSchoolId"/> is nullable, and null means "do not filter".</b> That is a
/// deliberate, temporary property of the pre-auth build, not a permission model: with no claims to
/// read there is no honest tenant answer, and the alternative — filtering on a default
/// <c>Guid.Empty</c> — would make every query return zero rows for reasons invisible at the call
/// site. Whichever state is in force is written to the log at startup, so it is never a silent
/// condition. Phase 6 tightens this to a non-null value sourced from claims, at which point an
/// unauthenticated request must be rejected before it reaches a query rather than run unfiltered.
/// </para>
/// </summary>
public interface ISchoolContext
{
    /// <summary>
    /// The tenant every query is scoped to, or <c>null</c> while no tenant is resolved (pre-auth
    /// development, and design-time model building). Read once per query execution.
    /// </summary>
    Guid? CurrentSchoolId { get; }
}

/// <summary>
/// The <em>pinned</em> pre-auth tenant, kept separately resolvable now that <see cref="ISchoolContext"/>
/// itself reads claims (Phase 4a design, D-23).
///
/// <para>
/// Phase 4b replaced the registered <see cref="ISchoolContext"/> with a claims-reading implementation
/// — the one Phase 6 will ship — but a request that carries no credentials still has to resolve to
/// <em>something</em>, because every endpoint outside the four device-gated ones is still open under
/// ADR-001 D-6. This interface is that fallback: the single school the host pinned at startup, with
/// the log line that announced it.
/// </para>
///
/// <para>
/// <b>It is the one line Phase 6 deletes.</b> When §11 lands in full, an unauthenticated request never
/// reaches a query, so the fallback has no caller and the claims implementation is complete on its
/// own. Keeping it behind its own interface is what makes that deletion a two-file change rather than
/// an archaeology exercise.
/// </para>
/// </summary>
public interface IPinnedSchoolContext : ISchoolContext
{
}
