using Microsoft.Extensions.Logging;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that keeps what it was told, so a test can assert that
/// something was recorded rather than only that a caller got the right answer.
///
/// <para>
/// It exists for one class of finding: a failure the service <em>recovers from</em>. Those are
/// invisible to an ordinary assertion by construction — the caller receives a tidy 400 or 409 and the
/// test passes whether or not anybody was told. The academic-cache guard tripping is the sharp case:
/// it means code inside our own unit of work wrote a column it must not, and the only party who finds
/// out is the client, who cannot act on it. Without this, "we log it now" is a claim no test can
/// falsify — which is the same shape as the comment that turned out to be wrong at the review gate.
/// </para>
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<Entry> _entries = [];

    /// <summary>One recorded call. <paramref name="Message"/> is already formatted.</summary>
    internal readonly record struct Entry(LogLevel Level, string Message, Exception? Exception);

    public IReadOnlyList<Entry> Entries => _entries;

    public IReadOnlyList<Entry> At(LogLevel level) =>
        _entries.Where(e => e.Level == level).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
}
