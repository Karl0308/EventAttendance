using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// Records every command whose <c>IN (...)</c> value list exceeds a ceiling.
///
/// <para>
/// The failing test for the 2026-11-24 error 8623: under compatibility level 110 a collection
/// <c>Contains</c> is inlined as one literal per element, and a 100,000-literal list cannot be planned.
/// A 100,000-row fixture is too slow for the suite, so the guard asserts the <em>shape</em> instead: no
/// statement may carry more than <c>InListChunks.Size</c> values in one list. Only meaningful on a context
/// built with <c>UseCompatibilityLevel(110)</c> — without it EF emits OPENJSON and there is no list to count.
/// </para>
///
/// <para>
/// Recorded rather than thrown, so a breach is reported as an assertion naming the statement instead of
/// surfacing as a failed batch somewhere inside the import.
/// </para>
///
/// <para>
/// Parameters are not counted: the card-lock command carries one per lock by design (bounded by SQL
/// Server's 2,100-parameter limit, which fails loudly), and parameters are not what error 8623 is about.
/// </para>
/// </summary>
internal sealed partial class InListSizeGuard(int ceiling) : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _breaches = new();

    public IReadOnlyCollection<string> Breaches => _breaches;

    /// <summary>The largest value list seen, so a test can prove the guard saw a list at all.</summary>
    public int Largest { get; private set; }

    [GeneratedRegex(@"\bIN\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex InListOpening();

    private void Inspect(DbCommand command)
    {
        var sql = command.CommandText;
        var largest = 0;

        foreach (Match match in InListOpening().Matches(sql))
        {
            var start = match.Index + match.Length;
            if (sql.AsSpan(start).TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) continue;
            largest = Math.Max(largest, CountValues(sql, start));
        }

        Largest = Math.Max(Largest, largest);
        if (largest > ceiling)
            _breaches.Enqueue($"{largest} values: {sql[..Math.Min(sql.Length, 200)]}");
    }

    /// <summary>Top-level comma-separated items from <paramref name="start"/> to the matching ')'.</summary>
    private static int CountValues(string sql, int start)
    {
        var depth = 0;
        var inString = false;
        var count = 1;
        for (var i = start; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'') inString = !inString;
            if (inString) continue;
            if (c == '(') depth++;
            else if (c == ')' && depth-- == 0) return count;
            else if (c == ',' && depth == 0) count++;
        }

        return count;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Inspect(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Inspect(command);
        return result;
    }
}
