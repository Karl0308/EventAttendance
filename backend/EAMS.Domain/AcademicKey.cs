namespace EAMS.Domain;

/// <summary>
/// The one rule that turns a messy roster string into the stored key a natural-key index is built on.
///
/// <para>
/// <b>Why this exists at all.</b> The real CICSS export does not spell its own values consistently:
/// <c>'SSCI 7'</c> and <c>'SSci7'</c> are the same course, <c>'CA  2'</c> carries a double space, and
/// section names arrive with and without the hyphen in <c>'BSFS 2-A'</c>. Every one of those pairs
/// must resolve to a single row, and the only way that holds is if <em>every</em> writer — the Phase 2
/// importer, a manual-entry screen, a fixture, a repair script — derives the key the same way. So the
/// rule lives here, in the domain, exactly like <see cref="CardUid"/>, and the entities store the
/// result in a <c>*Key</c> column beside the display value.
/// </para>
///
/// <para>
/// <b>Why the key is stored rather than computed by SQL Server.</b> A computed column cannot express
/// this: T-SQL has no "keep letters and digits" primitive, so the normalization would have to be a
/// scalar UDF, which is non-deterministic to the optimizer and cannot be indexed without extra
/// ceremony. Storing it also means the key is visible in a row dump, which is what makes a bad import
/// diagnosable.
/// </para>
///
/// <para>
/// <b>Why case folding is not left to the collation.</b> SQL Server's default
/// <c>SQL_Latin1_General_CP1_CI_AS</c> is case-insensitive, so on a default server the folding here is
/// belt-and-braces. It is done anyway because the collation is a property of the deployment, not of
/// the model — on a case-sensitive server <c>SSci7</c> and <c>SSCI7</c> would silently become two
/// courses — and because the application compares these keys in C# with ordinal <c>==</c>, where no
/// collation applies at all. Same argument as <see cref="AttendanceStatus"/>'s canonical spelling.
/// </para>
///
/// <para>
/// <b>Nothing is truncated.</b> A key longer than <see cref="MaxLength"/> is returned in full and is
/// rejected by SQL Server on write. Truncating instead would merge two genuinely different courses
/// into one row and there would be no evidence it happened; a loud error at the boundary is the
/// cheaper failure. Callers that want to fail earlier use <see cref="IsWithinLength"/>.
/// </para>
/// </summary>
public static class AcademicKey
{
    /// <summary>
    /// Matches the <c>nvarchar(200)</c> of every <c>*Key</c> column in the academic tables.
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>
    /// The stand-in written when the source has no value for a <em>key</em> component — 39 rows of
    /// the sample roster have a blank <c>SECTION_NAME</c>.
    ///
    /// <para>
    /// <b>It is a sentinel, not a null, because a SQL Server unique index permits exactly one NULL
    /// row.</b> A nullable <c>SectionKey</c> would mean one blank-section offering per term across the
    /// entire institution, and the 39th row would fail with a duplicate-key error that says nothing
    /// about the real cause.
    /// </para>
    ///
    /// <para>
    /// <b>The parentheses are load-bearing.</b> <see cref="Normalize"/> discards every character that
    /// is not a letter or a digit, so no source string — including a section literally named
    /// "unspecified" — can ever normalize onto this value. The sentinel is unforgeable by
    /// construction rather than by being an unlikely word.
    /// </para>
    /// </summary>
    public const string Unspecified = "(unspecified)";

    /// <summary>
    /// The canonical key for a source value: letters and digits only, upper-cased invariantly.
    /// Returns the empty string for null, blank, or punctuation-only input — see
    /// <see cref="NormalizeOrUnspecified"/> for the key-column form that never returns empty.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    /// <summary>
    /// <see cref="Normalize"/> for a column that is part of a natural key: a source value that
    /// normalizes to nothing becomes <see cref="Unspecified"/> rather than an empty string, so the
    /// blank rows group together under one honest placeholder instead of colliding on <c>''</c>.
    /// </summary>
    public static string NormalizeOrUnspecified(string? value)
    {
        var key = Normalize(value);
        return key.Length == 0 ? Unspecified : key;
    }

    /// <summary>
    /// Whether a normalized key fits its column. Intended for the import boundary, where a row can be
    /// reported as failed with its own row number instead of taking the whole batch down with a
    /// truncation error from SQL Server.
    /// </summary>
    public static bool IsWithinLength(string key) => key.Length <= MaxLength;
}
