namespace EAMS.Application.Abstractions;

/// <summary>
/// A unit of work was refused before its first write because a lock it needed was not granted, so that
/// unit wrote nothing and the same request may simply be sent again (JJ, 2026-10-28, decision 5). An
/// import that commits in steps keeps its earlier steps; see <c>WriteLockUnavailableExceptionHandler</c>.
///
/// <para>
/// <b>Public and abstract, and that is the only reason it exists.</b> The concrete exception
/// (<c>CardUidLockException</c>, ADR-015 D-98) is internal to <c>EAMS.Infrastructure</c> like every other
/// type there, so the API boundary cannot name it. It names this instead, and answers
/// <c>503 Service Unavailable</c> with <c>Retry-After</c> rather than the 500 an unhandled exception
/// becomes: the refusal is transient and the request was correct.
/// </para>
/// </summary>
public abstract class WriteLockUnavailableException : Exception
{
    /// <summary>
    /// The <c>Retry-After</c> the boundary sends, in seconds. A refusal means another writer held the lock
    /// for the whole wait (D-98's 10-second <c>@LockTimeout</c>) or this one lost a deadlock, so a short,
    /// fixed back-off is enough; it is not a measured queue depth.
    /// </summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>The machine-readable <c>code</c> on the 503 problem body.</summary>
    public const string Code = "WriteLockUnavailable";

    protected WriteLockUnavailableException(string message) : base(message) { }
}
