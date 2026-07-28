using System.Globalization;
using System.Text;

namespace EAMS.Domain;

/// <summary>
/// The first thing every roster value passes through, before <see cref="AcademicKey"/> and before any
/// comparison, key, or display value is derived from it.
///
/// <para>
/// <b>Why it runs first, and why it is separate from <see cref="AcademicKey"/>.</b> <c>AcademicKey</c>
/// throws away everything that is not a letter or a digit, so it is immune to invisible characters by
/// accident — a non-breaking space and an ordinary space both vanish. Every <em>display</em> column is
/// not: <c>Students.FirstName</c>, <c>Courses.Title</c>, <c>CourseOfferings.SectionName</c> and
/// <c>Instructors.DisplayName</c> all store what the source said, and two values that a human reads as
/// identical must not differ by a U+00A0 nobody can see. <c>'CA  2'</c> is the visible half of the same
/// problem and is handled here too, by collapsing runs of whitespace to one space.
/// </para>
///
/// <para>
/// <b>Why NFC.</b> Excel exports from a Mac carry decomposed forms — <c>ñ</c> as <c>n</c> + U+0303 —
/// which is a different string from the composed <c>ñ</c> a Windows export produces, sorts differently,
/// and compares unequal under every ordinal comparison this codebase uses. Composing first means the
/// two exports of one roster produce one student rather than two. The real CICSS sample happens to
/// contain no such character today; that is a property of one file, not of the pipeline's input, and
/// discovering the difference on the day a Mac export arrives would mean a duplicated roster with no
/// visible cause.
/// </para>
///
/// <para>
/// <b>Order is load-bearing.</b> Zero-width characters are removed and NBSP is folded to a plain space
/// <em>before</em> whitespace is collapsed. Reversed, <c>"A&#160; B"</c> would collapse to
/// <c>"A&#160; B"</c> unchanged (a run of one NBSP and one space is not a run of the same character to
/// a naive collapse) and the invisible character would survive into the stored value.
/// </para>
/// </summary>
public static class RosterText
{
    /// <summary>
    /// Characters that carry no width and no meaning in a roster cell, and whose only effect is to make
    /// two visually identical values compare unequal. Zero-width space/non-joiner/joiner, word joiner,
    /// and the byte-order mark, which arrives as a stray character in the middle of a string often
    /// enough to be worth naming.
    /// </summary>
    /// <remarks>
    /// Spelled as numeric casts rather than as character literals on purpose: a literal zero-width
    /// character in source is invisible in every diff, every editor and every review, so the one place
    /// they are named must be the one place they are readable.
    /// </remarks>
    private static readonly char[] ZeroWidth =
    [
        (char)0x200B, // zero-width space
        (char)0x200C, // zero-width non-joiner
        (char)0x200D, // zero-width joiner
        (char)0x2060, // word joiner
        (char)0xFEFF, // byte-order mark / zero-width no-break space
    ];

    /// <summary>
    /// Cleans a source cell for storage in a display column: NFC-composed, stripped of zero-width
    /// characters, whitespace-folded to single ASCII spaces, and trimmed. Returns <c>null</c> — never
    /// the empty string — when nothing survives, so a blank cell and an absent one are the same thing
    /// to every caller and nullable columns get <c>NULL</c> rather than <c>''</c>.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Composed first: the fold below treats U+00A0 as whitespace, and a decomposed sequence could
        // otherwise be split across the fold.
        var composed = value.Normalize(NormalizationForm.FormC);

        var builder = new StringBuilder(composed.Length);
        var pendingSpace = false;

        foreach (var ch in composed)
        {
            if (Array.IndexOf(ZeroWidth, ch) >= 0) continue;

            // char.IsWhiteSpace covers U+00A0, U+2007, U+202F and the rest of the space separators as
            // well as tabs and newlines, so every one of them folds to the same single ASCII space.
            if (char.IsWhiteSpace(ch))
            {
                // Deferred rather than appended: a trailing run then costs nothing to drop, which is
                // what makes the trim implicit instead of a second pass.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// <see cref="Clean"/> for a column that holds a <em>name</em>, where the source spells "absent" as
    /// a placeholder rather than as a blank.
    ///
    /// <para>
    /// <b>Why this is not just <see cref="Clean"/>.</b> 200 of the sample's 536 rows carry a literal
    /// <c>'-'</c> in <c>STUDENT MIDDLE NAME</c> and another 175 are blank; both mean the same thing.
    /// Stored as written, <c>Student.FullName</c> renders "Maria - Santos" on 200 students and every
    /// name search for a middle initial matches a hyphen. The test is "does anything remain once
    /// punctuation is discarded" rather than a list of known placeholders, so <c>'--'</c>, <c>'.'</c>
    /// and <c>'/'</c> are covered without anyone having to have seen them first — while <c>'E.'</c>,
    /// which is a real initial, keeps its period.
    /// </para>
    /// </summary>
    public static string? CleanName(string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is null) return null;

        // Reuses the key rule rather than restating "letter or digit", so a change to what counts as
        // meaningful content cannot drift between the key columns and the display columns.
        return AcademicKey.Normalize(cleaned).Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Cleans an e-mail address: <see cref="Clean"/> plus lower-casing, because the domain half is
    /// case-insensitive by RFC and the roster spells the same address both ways across sheets. Stored
    /// lower so <c>UX</c>-style comparisons and any later uniqueness check agree with each other.
    /// </summary>
    public static string? CleanEmail(string? value) =>
        Clean(value)?.ToLowerInvariant();

    /// <summary>
    /// The stable fingerprint of one source row, over its values in a caller-supplied order.
    ///
    /// <para>
    /// Written from the <em>raw</em> cells, not the cleaned ones, and that is the point: it answers "is
    /// this byte-for-byte the row we saw last time", which is what makes a re-upload of the same file
    /// recognisable before any interpretation has been applied. A hash over cleaned values would call
    /// two genuinely different files identical.
    /// </para>
    ///
    /// <para>
    /// U+001F (unit separator) joins the values because it cannot occur in a spreadsheet cell, so no
    /// combination of values can forge a different row's hash by containing the delimiter.
    /// </para>
    /// </summary>
    /// <summary>
    /// U+001F, the ASCII unit separator, spelled numerically for the same reason the zero-width set is:
    /// a control character typed literally into source is invisible in every diff.
    /// </summary>
    private const char UnitSeparator = (char)0x1F;

    public static string Fingerprint(IEnumerable<string?> orderedValues)
    {
        var joined = string.Join(UnitSeparator, orderedValues.Select(v => v ?? ""));
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// The textual form of a spreadsheet cell that holds a number, for the columns this system treats
    /// as identifiers rather than as quantities.
    ///
    /// <para>
    /// <b>This exists because of one student.</b> 51 of the 52 REGNOs are <c>USA#####</c> and arrive as
    /// text; one is the legacy <c>2021005781</c>, which Excel stores as a <em>number</em>. .NET's
    /// default double formatting renders that as <c>2.021005781E+09</c>, and a student number of that
    /// shape is silently wrong in the one column that is also the RFID card UID — the tap would never
    /// match. Integral values are therefore formatted through <see cref="long"/>, invariantly, with no
    /// exponent and no group separators.
    /// </para>
    /// </summary>
    public static string FormatNumericCell(double value) =>
        value == Math.Floor(value) && Math.Abs(value) <= 9_007_199_254_740_992d
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
}
