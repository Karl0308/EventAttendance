using System.Text;

namespace EAMS.Domain;

/// <summary>
/// Normalization for the free-text Organization columns — <see cref="Personnel.Organization"/> and
/// <see cref="Student.Organization"/> (QA ruling MDVault #543, Option A; ADR-011 D-78). Organization has no
/// master entity, FK or seed — it stays free text an administrator types or imports — but a whitespace/case
/// variant must not silently drop a person from an Event Audience. The two halves of that guarantee live here
/// so every write path and the audience matcher share one definition:
/// <list type="bullet">
/// <item><see cref="Normalize"/> — the STORAGE form: trim leading/trailing whitespace and collapse internal
/// runs to a single space, letter casing preserved. Deterministic and collation-independent.</item>
/// <item><see cref="MatchKey"/> — the case-insensitive COMPARISON key: the storage form, upper-cased.</item>
/// </list>
/// Casing is preserved on store (display), folded only for comparison — so "CICT " and "cict" store as they
/// were typed yet both resolve into a "CICT" audience criterion. No backfill is needed for case or
/// leading/trailing whitespace: matching folds both sides (SQL UPPER(LTRIM(RTRIM(..))) vs MatchKey). The ONE
/// exception: the SQL side cannot collapse INTERNAL whitespace, so a legacy value stored with an internal
/// multi-space before this change (e.g. "Faculty  Union") resolves against a collapsed criterion
/// ("Faculty Union") only after it is next saved. New/updated/imported rows are collapsed on save. The miss is
/// conservative (drops, never false-merges) and self-heals on re-save.
/// </summary>
public static class OrganizationText
{
    /// <summary>
    /// The column length on both rosters. <see cref="PersonnelText.OrganizationMaxLength"/> points here, so the
    /// personnel and student columns cannot drift apart (ADR-011 D-78).
    /// </summary>
    public const int MaxLength = 150;

    /// <summary>
    /// The value to STORE: trimmed, with internal whitespace runs collapsed to one space and casing kept.
    /// Null/blank (or whitespace-only) returns null — Organization is optional (QA #543 Q4).
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) pendingSpace = true;   // never leads; defer so trailing runs vanish
                continue;
            }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// The case-insensitive match key: <see cref="Normalize"/> upper-cased (invariant), or null for a
    /// value that is not an organization. Pair this (criteria side, in memory) with a SQL-translatable
    /// <c>Organization.Trim().ToUpper()</c> on the stored side so the comparison is correct regardless of
    /// the database collation and in LINQ-to-objects alike.
    /// </summary>
    public static string? MatchKey(string? value) => Normalize(value)?.ToUpperInvariant();

    /// <summary>
    /// The distinct organizations to SHOW in a list: each value normalized, blanks dropped, grouped
    /// case-insensitively, the ordinally-first spelling kept per group, sorted case-insensitively.
    /// Taking the ordinally-first spelling rather than the first seen keeps the result deterministic
    /// whatever order the source rows arrive in (an unordered SQL <c>DISTINCT</c>, typically).
    /// </summary>
    public static IReadOnlyList<string> DistinctForDisplay(IEnumerable<string?> values) =>
        values
            .Select(Normalize)                              // trim + collapse, casing preserved
            .OfType<string>()                               // drops null and whitespace-only
            .GroupBy(o => o, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(o => o, StringComparer.Ordinal).First())
            .OrderBy(o => o, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
