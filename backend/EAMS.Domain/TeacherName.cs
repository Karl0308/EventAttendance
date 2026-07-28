namespace EAMS.Domain;

/// <summary>
/// What the roster's five teacher columns actually mean, resolved into the two things the schema needs:
/// a display name whose <see cref="AcademicKey"/> identifies one person, and a flag saying the row names
/// nobody at all.
///
/// <para>
/// <b>The source has no teacher id.</b> Nineteen distinct <c>UA_FULLNAME</c> values, matched on the name
/// and nothing else (see <see cref="Instructor"/>). That makes the name the natural key, and it makes
/// every stray token in it a potential second copy of one teacher — which is why the parsing rules below
/// are rules rather than tidying.
/// </para>
/// </summary>
/// <param name="DisplayName">
/// The name to store, honorifics removed and whitespace folded. <c>null</c> when the row names nobody.
/// </param>
/// <param name="Honorific">
/// The courtesy title the source supplied, recognised and set aside. <b>It is never part of
/// <see cref="DisplayName"/></b> and therefore never part of the key.
/// </param>
/// <param name="GenerationalSuffix">
/// <c>Jr.</c>, <c>III</c> and friends, when one was found. Unlike an honorific this <em>is</em> part of
/// the person's name and stays inside <see cref="DisplayName"/>; it is surfaced separately only so a
/// caller can tell that the trailing token was understood rather than merely tolerated.
/// </param>
/// <param name="IsPlaceholder">
/// True when the row's teacher is the <c>'TO BE ANNOUNCE'</c> sentinel. See
/// <see cref="TeacherNames.PlaceholderKey"/> for why this must not become an instructor row.
/// </param>
public sealed record TeacherName(
    string? DisplayName,
    string? Honorific,
    string? GenerationalSuffix,
    bool IsPlaceholder);

/// <summary>
/// The parsing rules for the roster's teacher columns. Domain-level and pure, exactly like
/// <see cref="AcademicKey"/> and <see cref="CardUid"/>, so the importer, a manual-entry screen and a
/// fixture cannot disagree about who two rows are naming.
/// </summary>
public static class TeacherNames
{
    /// <summary>
    /// <c>'TO BE ANNOUNCE'</c> after <see cref="AcademicKey.Normalize"/>, which is how it is compared —
    /// the source also spells it with different spacing and casing across the five teacher columns.
    ///
    /// <para>
    /// <b>It must never become an <see cref="Instructor"/> row.</b> It fills 66 of the sample's 536 rows.
    /// A synthetic "TBA" instructor would therefore be, by a wide margin, the largest teacher in the
    /// institution: it would top every "sections per instructor" report, appear in the teacher picker
    /// above every real person, and be indistinguishable from a genuine assignment at the point where
    /// someone acts on it. The honest representation of "no teacher has been named" is an offering with
    /// no <c>CourseOfferingInstructors</c> row — the junction is many-to-many precisely so that zero is
    /// a representable number of teachers.
    /// </para>
    ///
    /// <para>
    /// This deliberately revises the note on <see cref="Instructor"/>, which recorded the sentinel as
    /// "not special-cased here" and left the decision to the import phase. This is that decision.
    /// </para>
    /// </summary>
    public const string PlaceholderKey = "TOBEANNOUNCE";

    /// <summary>
    /// Courtesy titles, matched as a <em>leading</em> token and stripped.
    ///
    /// <para>
    /// <b>Why stripping is required rather than cosmetic.</b> The key is derived from the display name,
    /// so <c>'Ms. Theresa Navarro'</c> keys as <c>MSTHERESANAVARRO</c> and <c>'Theresa Navarro'</c> keys as
    /// <c>THERESANAVARRO</c> — two instructors, one person, and the split is silent. The real sample proves
    /// this is live: <c>TEACHER SUFFIX</c> carries <c>Ms.</c>/<c>Mr.</c>/<c>Mrs.</c> on every row, and
    /// <c>'SR. CLARA BENITEZ MORALES'</c> carries a religious title inside the name itself.
    /// </para>
    ///
    /// <para>
    /// Stored without the trailing period so <c>Ms</c> and <c>Ms.</c> are one entry.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Honorifics = new(StringComparer.OrdinalIgnoreCase)
    {
        "MR", "MRS", "MS", "MISS", "MX",
        "DR", "PROF", "ENGR", "ATTY", "HON", "ARCH",
        "SR", "SIS", "FR", "BR", "REV", "RT REV", "MSGR", "PSTR",
    };

    /// <summary>
    /// Generational suffixes, matched as a <em>trailing</em> token and kept.
    ///
    /// <para>
    /// <b><c>SR</c> is in both this set and <see cref="Honorifics"/>, and position is what tells them
    /// apart.</b> Leading, it is "Sister"; trailing, it is "Senior". The sample contains both shapes —
    /// <c>'SR. CLARA'</c> and the generational <c>'ROBERTO III'</c> — so a single membership test would
    /// get one of them wrong whichever way it was written.
    /// </para>
    ///
    /// <para>
    /// <b><c>V</c> is deliberately absent.</b> A bare <c>V</c> between two names is overwhelmingly a
    /// middle initial in a Filipino roster and only vanishingly a regnal number, so including it would
    /// mislabel real initials to catch a case that does not occur. <c>I</c> is absent for the same
    /// reason. Nothing downstream depends on this field being exhaustive — it is reported, not keyed —
    /// so the cheaper error is to under-detect.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Generational = new(StringComparer.OrdinalIgnoreCase)
    {
        "JR", "SR", "II", "III", "IV",
    };

    /// <summary>Whether a source value is the <c>'TO BE ANNOUNCE'</c> sentinel.</summary>
    public static bool IsPlaceholder(string? value) =>
        AcademicKey.Normalize(value) == PlaceholderKey;

    /// <summary>
    /// Resolves the roster's five teacher columns into one <see cref="TeacherName"/>.
    /// </summary>
    /// <param name="fullName">
    /// <c>UA_FULLNAME</c> — the authoritative column. It has exactly nineteen distinct values in the
    /// sample, which is the count the registrar recognises, so it is preferred over recomposing the
    /// name from the parts.
    /// </param>
    /// <param name="firstName">
    /// <c>TEACHER FIRST NAME</c>. Used only when <paramref name="fullName"/> is blank, and read for the
    /// generational suffix, which is where the sample keeps it (<c>'ROBERTO III'</c>).
    /// </param>
    /// <param name="lastName"><c>TEACHER LAST NAME</c>. Used only when <paramref name="fullName"/> is blank.</param>
    /// <param name="suffix">
    /// <c>TEACHER SUFFIX</c>. <b>Despite the column name this is an honorific</b> — its four values in
    /// the sample are <c>Mr.</c>, <c>Mrs.</c>, <c>Ms.</c> and the placeholder — so it is never appended
    /// to the name. Reading it as a name suffix would put <c>Ms.</c> at the end of a teacher's name and
    /// into their key.
    /// </param>
    public static TeacherName Parse(string? fullName, string? firstName, string? lastName, string? suffix)
    {
        var placeholder = IsPlaceholder(fullName)
            || (RosterText.Clean(fullName) is null && IsPlaceholder(firstName) && IsPlaceholder(lastName));

        if (placeholder) return new TeacherName(null, null, null, IsPlaceholder: true);

        // The honorific is recognised or it is discarded, and either way it does not reach the name.
        // An unrecognised value is not promoted to a name suffix on the strength of the column's
        // title: the raw row is preserved verbatim in SisImportRows.RawData, so nothing is lost by
        // declining to guess, whereas guessing would corrupt the key.
        var honorific = NormalizeHonorific(suffix);

        var composed = RosterText.Clean(fullName)
            ?? Compose(RosterText.Clean(firstName), RosterText.Clean(lastName));

        if (composed is null) return new TeacherName(null, honorific, null, IsPlaceholder: false);

        var stripped = StripLeadingHonorifics(composed);
        if (stripped is null) return new TeacherName(null, honorific, null, IsPlaceholder: false);

        return new TeacherName(
            stripped,
            honorific,
            // Read from the first-name column when there is one, because that is where the sample puts
            // it, and otherwise from the composed name's own tokens.
            FindGenerational(RosterText.Clean(firstName)) ?? FindGenerational(stripped),
            IsPlaceholder: false);
    }

    /// <summary>
    /// The recognised courtesy title in canonical form, or <c>null</c>. Canonical means "as the source
    /// wrote it, cleaned" rather than a fixed spelling: nothing is keyed on it, so preserving
    /// <c>Ms.</c> over <c>MS</c> costs nothing and reads better if it is ever surfaced.
    /// </summary>
    private static string? NormalizeHonorific(string? suffix)
    {
        if (IsPlaceholder(suffix)) return null;

        var cleaned = RosterText.Clean(suffix);
        if (cleaned is null) return null;

        return Honorifics.Contains(cleaned.TrimEnd('.')) ? cleaned : null;
    }

    private static string? Compose(string? firstName, string? lastName) =>
        string.Join(' ', new[] { firstName, lastName }.Where(part => part is not null)) is { Length: > 0 } joined
            ? joined
            : null;

    /// <summary>
    /// Removes leading courtesy titles, one at a time so <c>'Rev. Fr. Juan Cruz'</c> loses both.
    ///
    /// <para>
    /// It stops while at least one token remains: a teacher recorded as nothing but <c>'Ms.'</c> would
    /// otherwise strip to the empty string and take an offering's only teacher reference with it. In
    /// that case the honorific-only value is returned unchanged and becomes an instructor whose name is
    /// visibly wrong — which is a data-quality problem someone can see and fix, unlike a silently
    /// dropped assignment.
    /// </para>
    /// </summary>
    private static string? StripLeadingHonorifics(string name)
    {
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (tokens.Count > 1 && Honorifics.Contains(tokens[0].TrimEnd('.')))
            tokens.RemoveAt(0);

        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    /// <summary>
    /// The generational suffix inside a name, wherever it sits among the tokens.
    ///
    /// <para>
    /// Not restricted to the final token, because the sample's <c>'ROBERTO III MENDOZA SALCEDO'</c>
    /// puts it second of four — the export concatenates <c>first middle last</c> and the suffix travels
    /// with the first name. A trailing-token-only rule would find nothing there, which is exactly the
    /// case this method exists for.
    /// </para>
    ///
    /// <para>
    /// The first token is never considered: a name beginning <c>'V ...'</c> is an initial, not a
    /// regnal number.
    /// </para>
    /// </summary>
    private static string? FindGenerational(string? name)
    {
        if (name is null) return null;

        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 1; i < tokens.Length; i++)
        {
            var candidate = tokens[i].TrimEnd('.', ',');
            if (Generational.Contains(candidate)) return candidate;
        }

        return null;
    }
}
