using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Data;

/// <summary>
/// Runs a query filtered by an in-memory key list in pieces SQL Server can plan.
///
/// <para>
/// <b>Why this exists.</b> <see cref="DependencyInjection.SqlServerCompatibilityLevel"/> pins EF to the
/// 2012 dialect, which has no OPENJSON, so a <c>.Where(x =&gt; keys.Contains(x.Key))</c> is inlined as
/// one literal per element. The optimizer's cost for that is superlinear in the list length. Measured on
/// a compatibility-110 database (2026-11-24): about 0.1 ms of compile per literal up to 1,000, 0.16 ms at
/// 2,000, and 0.26 ms at 10,000. A 100,000-element list compiled for 68 seconds and then failed with
/// error 8623 ("the query processor ran out of internal resources"). That was the first statement of a
/// 100,000-row roster import, so a roster that size could not import at all.
/// </para>
///
/// <para>
/// <b>Prefer a server-side subquery or join when the key set already lives in the database</b> — the
/// batch's own rows, the term's own offerings. That is a constant-size statement. This helper is for
/// keys that exist only in memory (the student numbers and card serials read from an uploaded file).
/// </para>
///
/// <para>
/// <b>Results are de-duplicated by the result type's default equality.</b> The caller distincts its keys
/// with C#'s comparer, but SQL Server compares under the column's case-insensitive collation, so two keys
/// that differ only by case can land in different chunks and return the same row twice where one IN list
/// returned it once. A tracked entity is one instance per row (identity resolution), and a projection that
/// carries the row's <c>Id</c> compares by value, so both collapse back to one. A no-tracking entity query
/// does not, and must not be passed here.
/// </para>
/// </summary>
internal static class InListChunks
{
    /// <summary>
    /// Literals per statement. At 1,000 the compile cost is still linear (about 110 ms of CPU); at 2,000 it
    /// is already 2.9 times that. A 100,000-key lookup is then 100 statements and roughly 11–15 seconds,
    /// against a single statement that never finishes.
    /// </summary>
    public const int Size = 1000;

    /// <summary>
    /// Materializes <paramref name="query"/> once per chunk of <paramref name="keys"/> and returns the union.
    /// No statement is issued for an empty key list.
    /// </summary>
    public static async Task<List<TResult>> ToListInChunksAsync<TKey, TResult>(
        IReadOnlyCollection<TKey> keys,
        Func<TKey[], IQueryable<TResult>> query,
        CancellationToken ct,
        int chunkSize = Size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        var results = new List<TResult>();
        if (keys.Count == 0) return results;

        if (keys.Count <= chunkSize)
        {
            results.AddRange(await query(keys as TKey[] ?? keys.ToArray()).ToListAsync(ct));
            return results;
        }

        var seen = new HashSet<TResult>();
        foreach (var chunk in keys.Chunk(chunkSize))
        {
            foreach (var row in await query(chunk).ToListAsync(ct))
            {
                if (seen.Add(row)) results.Add(row);
            }
        }

        return results;
    }
}
