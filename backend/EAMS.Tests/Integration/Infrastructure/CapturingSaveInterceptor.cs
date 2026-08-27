using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// A <see cref="SaveChangesInterceptor"/> that keeps what each <c>SaveChanges</c> call <em>contained</em>
/// — which entity types, in which <see cref="EntityState"/>, how many of each, and which properties the
/// resulting statements write — so a test can assert about save <b>boundaries</b> rather than about the
/// rows that eventually exist.
///
/// <para>
/// It exists for one class of finding: <b>where the unit of work was cut</b>. Two runs that write
/// byte-identical rows can differ in how many <c>SaveChanges</c> calls produced them and in what each
/// one held, and no read-back can tell them apart — the database looks the same afterwards either way.
/// That difference is not cosmetic. A save that inserts the whole fan-out <em>and</em> updates the batch
/// row holds an X lock on that batch row for the length of the longest phase in the import, which is
/// precisely the interval a progress poller exists to report on; splitting it is a correctness change
/// that is invisible to every assertion in the suite. Same for chunking: "the fan-out is written 500 at
/// a time" is a statement about save boundaries and nothing else.
/// </para>
///
/// <para>
/// <b>What it cannot see, and why that is the point.</b> <c>ExecuteUpdateAsync</c> and
/// <c>ExecuteDeleteAsync</c> bypass the change tracker and never call <c>SaveChanges</c>, so they do not
/// appear here at all. That absence is usable as an assertion: if progress is written the way it is
/// meant to be — one <c>UPDATE</c> against the batch row, touching no tracked graph — then
/// <see cref="PropertiesWrittenTo{T}(EntityState)"/> for the batch's progress columns stays empty
/// forever. A non-empty answer means somebody mutated the tracked batch and let it ride along on a save.
/// </para>
///
/// <para>
/// <b>Ordering note.</b> Recording happens in <c>SavingChanges</c>. Measured against EF Core 9 on this
/// suite, EF's own detect pass has already run by then — removing the
/// <see cref="ChangeTracker.DetectChanges"/> call below changes no result. It is kept anyway, guarded on
/// <see cref="ChangeTracker.AutoDetectChangesEnabled"/>, because that ordering is not part of EF's
/// contract and the failure it would cause is the silent kind: a mutated-but-undetected entity reads as
/// <see cref="EntityState.Unchanged"/>, so the recording would simply not mention the very batch-row
/// update this class exists to see. See the guard's own remarks for why it is conditional.
/// </para>
/// </summary>
internal sealed class CapturingSaveInterceptor : SaveChangesInterceptor
{
    private readonly List<SaveRecord> _saves = [];

    /// <summary>Every <c>SaveChanges</c> call seen, in order, <b>including ones that had nothing to do</b>
    /// — so <c>Saves.Count</c> is the number of calls, not the number of calls that wrote something.</summary>
    public IReadOnlyList<SaveRecord> Saves => _saves;

    /// <summary>
    /// Forgets everything recorded so far. For the common arrange shape where a fixture is built through
    /// the same intercepted context that the assertion is about: reset after the arrange, and
    /// <see cref="Saves"/> then describes the act alone.
    /// </summary>
    public void Reset() => _saves.Clear();

    /// <summary>
    /// Did any <em>single</em> save hold both of these at once? The question a "split that write off"
    /// change has to answer, and the only one a read-back cannot.
    /// </summary>
    public bool AnySaveWith<TFirst, TSecond>(EntityState first, EntityState second) =>
        _saves.Any(s => s.Count<TFirst>(first) > 0 && s.Count<TSecond>(second) > 0);

    /// <summary>
    /// The largest number of <typeparamref name="T"/> in <paramref name="state"/> that any one save
    /// carried — i.e. the effective chunk size. Zero when no save carried any.
    /// </summary>
    public int LargestSaveOf<T>(EntityState state) =>
        _saves.Count == 0 ? 0 : _saves.Max(s => s.Count<T>(state));

    /// <summary>
    /// Every property of <typeparamref name="T"/> that some save's statements wrote, across all saves,
    /// for entities in <paramref name="state"/>.
    ///
    /// <para>
    /// <b>The two states answer different questions and are deliberately not merged.</b> For
    /// <see cref="EntityState.Modified"/> this is the set EF marked modified — the columns an
    /// <c>UPDATE</c> actually carries, which is the meaningful "did this value ride along on a save?"
    /// question. For <see cref="EntityState.Added"/> it is every property, because an <c>INSERT</c>
    /// writes every column; asking it there tells you only that a row of this type was inserted.
    /// <see cref="EntityState.Deleted"/> writes no columns and returns empty.
    /// </para>
    /// </summary>
    public IReadOnlySet<string> PropertiesWrittenTo<T>(EntityState state)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var save in _saves) names.UnionWith(save.PropertiesWrittenTo<T>(state));
        return names;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Record(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Record(DbContext? context)
    {
        if (context is null)
        {
            // Not silently skipped: a null context here means the recording would be a lie of omission,
            // and a save this class did not see is worse than one it refused to file.
            throw new InvalidOperationException(
                $"{nameof(CapturingSaveInterceptor)} was invoked without a DbContext, so it cannot say " +
                "what the save contained.");
        }

        // Guarded rather than unconditional, and the guard is the load-bearing part: a context that has
        // switched auto-detect off has decided that an unnoticed mutation is not to be saved, and a
        // detect pass here would silently promote it to Modified and make EF write a column the code
        // under test did not ask it to. A recording double that changes what the system writes is worse
        // than one that misses something. When auto-detect is on, EF runs its own pass either side of
        // this call and the result is identical - so this only ever guards against the ordering, never
        // against the caller's intent.
        if (context.ChangeTracker.AutoDetectChangesEnabled)
        {
            context.ChangeTracker.DetectChanges();
        }

        var changes = new Dictionary<(Type Clr, EntityState State), Change>();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var key = (entry.Metadata.ClrType, entry.State);

            if (!changes.TryGetValue(key, out var change))
            {
                change = new Change(new HashSet<string>(StringComparer.Ordinal));
                changes[key] = change;
            }

            change.Count++;

            foreach (var property in WrittenProperties(entry))
            {
                change.Properties.Add(property);
            }
        }

        _saves.Add(new SaveRecord(changes.ToDictionary(
            kvp => kvp.Key,
            kvp => (Count: kvp.Value.Count, Properties: (IReadOnlySet<string>)kvp.Value.Properties))));
    }

    private static IEnumerable<string> WrittenProperties(EntityEntry entry) => entry.State switch
    {
        // An INSERT writes every column. Reporting only the ones somebody assigned would claim a
        // distinction the generated SQL does not make.
        EntityState.Added => entry.Properties.Select(p => p.Metadata.Name),
        EntityState.Modified => entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name),
        _ => [],
    };

    /// <summary>Mutable accumulator; never escapes <see cref="Record"/>.</summary>
    private sealed class Change(HashSet<string> properties)
    {
        public int Count { get; set; }
        public HashSet<string> Properties { get; } = properties;
    }

    /// <summary>What one <c>SaveChanges</c> call contained.</summary>
    internal sealed class SaveRecord(
        IReadOnlyDictionary<(Type Clr, EntityState State), (int Count, IReadOnlySet<string> Properties)> changes)
    {
        /// <summary>True when the call had nothing to write. EF still invokes the interceptor.</summary>
        public bool IsEmpty => changes.Count == 0;

        /// <summary>The distinct (type, state) pairs this call carried — for a failure message worth reading.</summary>
        public IEnumerable<(Type Clr, EntityState State, int Count)> Contents =>
            changes.Select(kvp => (kvp.Key.Clr, kvp.Key.State, kvp.Value.Count));

        public int Count<T>(EntityState state) =>
            changes.TryGetValue((typeof(T), state), out var change) ? change.Count : 0;

        public IReadOnlySet<string> PropertiesWrittenTo<T>(EntityState state) =>
            changes.TryGetValue((typeof(T), state), out var change)
                ? change.Properties
                : new HashSet<string>(StringComparer.Ordinal);

        public override string ToString() =>
            IsEmpty
                ? "(empty save)"
                : string.Join(", ", Contents
                    .OrderBy(c => c.Clr.Name, StringComparer.Ordinal)
                    .Select(c => $"{c.Count}x {c.Clr.Name} {c.State}"));
    }
}
