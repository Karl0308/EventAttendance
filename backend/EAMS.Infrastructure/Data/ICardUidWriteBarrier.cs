namespace EAMS.Infrastructure.Data;

/// <summary>
/// <b>A test seam with no production behaviour.</b> Every ADR-015 D-98 card-UID writer awaits this
/// between its re-check under the lock and its write, so <c>CardUidRaceTests</c> can park one writer
/// there and prove a writer on the other table blocks on the lock rather than slipping through.
///
/// <para>
/// Production registers <see cref="NoCardUidWriteBarrier"/>, which completes synchronously and does
/// nothing. Only a test constructs a service with anything else.
/// </para>
/// </summary>
internal interface ICardUidWriteBarrier
{
    Task BetweenCheckAndWriteAsync(CancellationToken ct);
}

/// <summary>The production <see cref="ICardUidWriteBarrier"/>: a no-op.</summary>
internal sealed class NoCardUidWriteBarrier : ICardUidWriteBarrier
{
    public static readonly NoCardUidWriteBarrier Instance = new();

    private NoCardUidWriteBarrier() { }

    public Task BetweenCheckAndWriteAsync(CancellationToken ct) => Task.CompletedTask;
}
