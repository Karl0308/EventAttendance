using System.Data;
using EAMS.Application.Abstractions;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Data;

/// <summary>Which table holds a claimed card UID.</summary>
internal enum CardUidHolderKind
{
    /// <summary>An active <c>RfidCards</c> row — <c>UX_RfidCards_SchoolId_CardUid_Active</c>.</summary>
    Student,

    /// <summary>A live <c>Personnel.RfidUid</c> — <c>UX_Personnel_SchoolId_RfidUid</c>.</summary>
    Personnel,
}

/// <summary>
/// Who holds a claimed UID: the student or personnel record, its number for the refusal message, and —
/// for a student — the card row itself.
/// </summary>
internal sealed record CardUidHolder(
    CardUidHolderKind Kind, Guid HolderId, string HolderNumber, Guid? RfidCardId);

/// <summary>
/// ADR-015 D-98 — one card UID identifies one person in a school, across <c>RfidCards</c> and
/// <c>Personnel.RfidUid</c>.
///
/// <para>
/// <b>Each table's own filtered unique index only guards its own table</b>, so nothing in the schema
/// stops a student card and a personnel record from claiming the same UID. Every write that makes a UID
/// <em>claimed</em> therefore takes a transaction-owned <c>sp_getapplock</c> on
/// <see cref="LockResource"/>, re-checks both tables under it (<see cref="HoldersAsync"/>), and only then
/// writes and commits. The lock is the serialization point; the re-check is the decision. Releasing a
/// claim needs no lock.
/// </para>
///
/// <para>
/// <b>Deadlock rule.</b> A writer takes all of its UID locks before its first write, in ordinal UID
/// order — <see cref="LockAsync(EamsDbContext, Guid, IEnumerable{string}, CancellationToken)"/> sorts, so
/// a caller cannot get the order wrong. A single-UID writer takes exactly one.
/// </para>
/// </summary>
internal static class CardUidClaims
{
    /// <summary>D-98's <c>@LockTimeout</c>.</summary>
    public const int LockTimeoutMilliseconds = 10_000;

    /// <summary>
    /// How many <c>sp_getapplock</c> calls one command carries. Each takes one parameter, and SQL Server
    /// accepts at most 2,100 per command, so a roster's card pass is split into commands of this size —
    /// all inside the one transaction and all before any write, so the deadlock rule still holds.
    /// </summary>
    private const int LocksPerCommand = 1_000;

    /// <summary><c>sp_getapplock</c>'s <c>@Resource</c> is <c>nvarchar(255)</c>.</summary>
    private const int ResourceMaxLength = 255;

    /// <summary>
    /// The lock resource for one UID in one school. The UID must already be normalized — the lock and
    /// both unique indexes are all about the normalized form (CLAUDE.md: normalize first).
    /// </summary>
    public static string LockResource(Guid schoolId, string normalizedUid) =>
        $"eams/card-uid/{schoolId:N}/{normalizedUid}";

    /// <inheritdoc cref="LockAsync(EamsDbContext, Guid, IEnumerable{string}, int, CancellationToken)"/>
    public static Task LockAsync(
        EamsDbContext db, Guid schoolId, IEnumerable<string> normalizedUids, CancellationToken ct) =>
        LockAsync(db, schoolId, normalizedUids, LockTimeoutMilliseconds, ct);

    /// <summary>
    /// Takes an exclusive, transaction-owned app lock on every UID, in ordinal order, and throws
    /// <see cref="CardUidLockException"/> on any return code below zero.
    ///
    /// <para>
    /// <b>The return code is checked in SQL and reported back, never ignored.</b> <c>sp_getapplock</c>
    /// reports a timeout (-1), a cancellation (-2) and — the one that matters most — a deadlock victim
    /// (-3) as a <em>return value</em>, not as error 1205, so no execution strategy ever sees it and a
    /// caller that did not look would carry on and write without the lock it thinks it holds.
    /// </para>
    ///
    /// <para>
    /// The timeout overload exists for the return-code tests; production calls the one above.
    /// </para>
    /// </summary>
    internal static async Task LockAsync(
        EamsDbContext db, Guid schoolId, IEnumerable<string> normalizedUids, int timeoutMilliseconds,
        CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "A card-UID lock is owned by the transaction (ADR-015 D-98), so it must be taken inside " +
                "one. Begin the transaction inside CreateExecutionStrategy().ExecuteAsync first.");

        var resources = normalizedUids
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(uid => LockResource(schoolId, uid))
            .ToList();

        foreach (var chunk in resources.Chunk(LocksPerCommand))
        {
            var sql = new StringBuilder("DECLARE @r int;\n");
            var parameters = new List<SqlParameter>(chunk.Length + 3);

            for (var i = 0; i < chunk.Length; i++)
            {
                sql.Append("EXEC @r = sys.sp_getapplock @Resource = @res").Append(i)
                    .Append(", @LockMode = N'Exclusive', @LockOwner = N'Transaction', " +
                            "@LockTimeout = @timeout;\n");
                sql.Append("IF @r < 0 BEGIN SET @rc = @r; SET @failedAt = ").Append(i)
                    .Append("; RETURN; END;\n");

                parameters.Add(new SqlParameter($"@res{i}", SqlDbType.NVarChar, ResourceMaxLength)
                {
                    Value = chunk[i],
                });
            }

            sql.Append("SET @rc = 0; SET @failedAt = -1;");

            var rc = new SqlParameter("@rc", SqlDbType.Int) { Direction = ParameterDirection.Output };
            var failedAt = new SqlParameter("@failedAt", SqlDbType.Int) { Direction = ParameterDirection.Output };
            parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = timeoutMilliseconds });
            parameters.Add(rc);
            parameters.Add(failedAt);

            await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, ct);

            var returnCode = (int)rc.Value;
            if (returnCode < 0) ThrowIfNotGranted(returnCode, chunk[(int)failedAt.Value]);
        }
    }

    /// <summary>
    /// The return-code rule on its own: 0 (granted) and 1 (granted after waiting) pass; anything below
    /// zero throws, naming the code.
    /// </summary>
    internal static void ThrowIfNotGranted(int returnCode, string resource)
    {
        if (returnCode >= 0) return;
        throw new CardUidLockException(returnCode, resource);
    }

    /// <summary>The holder of one UID, or <c>null</c> when it is unclaimed in both tables.</summary>
    public static async Task<CardUidHolder?> HolderAsync(
        EamsDbContext db, Guid schoolId, string normalizedUid, Guid? excludingPersonnelId,
        CancellationToken ct)
    {
        var holders = await HoldersAsync(db, schoolId, [normalizedUid], excludingPersonnelId, ct);
        return holders.GetValueOrDefault(normalizedUid);
    }

    /// <summary>
    /// The re-check: every UID in <paramref name="normalizedUids"/> that either table already claims,
    /// using <b>each table's own unique-index predicate</b> — students
    /// <c>RfidCards.IsActive = 1</c>, personnel <c>Personnel.IsDeleted = 0 AND RfidUid IS NOT NULL</c>,
    /// both on <c>SchoolId</c>.
    ///
    /// <para>
    /// <b><c>IgnoreQueryFilters</c>, with the school stated explicitly.</b> The index does not care which
    /// tenant a context is pinned to, or whether a student is soft-deleted — a deleted student's active
    /// card still holds its slot — so the re-check must not either.
    /// </para>
    ///
    /// <para>
    /// A UID both tables already hold (a pre-D-98 collision the go-live audit exists to find) is reported
    /// as the student's, because the tap reads student cards first.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, CardUidHolder>> HoldersAsync(
        EamsDbContext db, Guid schoolId, IReadOnlyCollection<string> normalizedUids,
        Guid? excludingPersonnelId, CancellationToken ct)
    {
        // Case-insensitive like the column collation the two indexes compare under, so a legacy value
        // stored in another case is still found by its normalized key.
        var holders = new Dictionary<string, CardUidHolder>(StringComparer.OrdinalIgnoreCase);
        if (normalizedUids.Count == 0) return holders;

        var uids = normalizedUids.Distinct(StringComparer.Ordinal).ToList();

        var cards = await db.RfidCards.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.SchoolId == schoolId && c.IsActive && uids.Contains(c.CardUid))
            .Select(c => new { c.CardUid, c.Id, c.StudentId, c.Student!.StudentNumber })
            .ToListAsync(ct);

        foreach (var card in cards)
            holders.TryAdd(card.CardUid, new CardUidHolder(
                CardUidHolderKind.Student, card.StudentId, card.StudentNumber, card.Id));

        var personnel = await db.Personnel.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.SchoolId == schoolId && !p.IsDeleted && p.RfidUid != null
                        && uids.Contains(p.RfidUid)
                        && (excludingPersonnelId == null || p.Id != excludingPersonnelId))
            .Select(p => new { p.RfidUid, p.Id, p.PersonnelNumber })
            .ToListAsync(ct);

        foreach (var person in personnel)
        {
            if (person.RfidUid is not { } uid) continue;
            holders.TryAdd(uid, new CardUidHolder(
                CardUidHolderKind.Personnel, person.Id, person.PersonnelNumber, null));
        }

        return holders;
    }
}

/// <summary>
/// <c>sp_getapplock</c> refused a card-UID lock (ADR-015 D-98). Nothing was written: the caller was still
/// before its first write, and the transaction the lock belonged to is rolled back as this propagates.
///
/// <para>
/// <b>Typed, and deliberately not transient.</b> A retrying execution strategy replays only the errors
/// it recognizes, and this is not one, so a -1 timeout or a -3 deadlock victim surfaces as a failure
/// naming its code rather than being retried into a write that never held the lock.
/// </para>
///
/// <para>
/// <b>The client sees 503 + <c>Retry-After</c>, not 500</b> (JJ, 2026-10-28, decision 5): it derives from
/// <see cref="WriteLockUnavailableException"/>, which the API boundary maps. The lock resource (school id
/// and UID) stays in the log, never in the response.
/// </para>
/// </summary>
internal sealed class CardUidLockException : WriteLockUnavailableException
{
    public CardUidLockException(int returnCode, string resource)
        : base(MessageFor(returnCode, resource))
    {
        ReturnCode = returnCode;
        Resource = resource;
    }

    /// <summary>The <c>sp_getapplock</c> return code: -1, -2, -3 or -999.</summary>
    public int ReturnCode { get; }

    /// <summary>The lock resource that was refused.</summary>
    public string Resource { get; }

    private static string MessageFor(int returnCode, string resource)
    {
        var meaning = returnCode switch
        {
            -1 => "the lock request timed out",
            -2 => "the lock request was cancelled",
            -3 => "the lock request was chosen as a deadlock victim",
            -999 => "a parameter validation or other call error",
            _ => "an undocumented refusal",
        };

        return $"sp_getapplock returned {returnCode} ({meaning}) for '{resource}'. The card-UID claim " +
               "lock was not taken, so this card write did not happen (ADR-015 D-98). The request can " +
               "be repeated.";
    }
}
