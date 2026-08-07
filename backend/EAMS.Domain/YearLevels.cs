namespace EAMS.Domain;

/// <summary>
/// D-47's rule: a student's year level, read out of the sections they are enrolled in — or read out as
/// nothing, which is the outcome this class exists to make easy to produce.
///
/// <para>
/// <b>Why anything has to be derived at all.</b> The registrar's eighteen columns
/// (<c>SisRosterColumns.All</c>) carry no year in any form, so <c>StudentTermRecord.YearLevel</c> has
/// nowhere to come from. The only column that carries year information is <c>SECTION_NAME</c>, and
/// reading it naively is the defect this whole rule exists to avoid.
/// </para>
///
/// <para>
/// <b>The trap, spelled out, because it is invisible in the data.</b> Real section names from the roster
/// come in two shapes that a digit-scanner cannot tell apart:
/// </para>
/// <list type="table">
///   <item>
///     <term><c>BSCRIM 2-A</c>, <c>BSN 1-B</c>, <c>BSIT 3-A</c></term>
///     <description>a <em>programme cohort</em> — the digit is the year level.</description>
///   </item>
///   <item>
///     <term><c>NSTP 2</c>, <c>GE 8</c>, <c>SSCI 7</c>, <c>CHEM 1</c></term>
///     <description>a <em>subject block</em> — the digit is a block number, and the block mixes
///     students from every year. <c>ROTC</c> has no digit at all.</description>
///   </item>
/// </list>
/// <para>
/// <c>NSTP 2</c> and <c>BSCRIM 2-A</c> both contain a <c>2</c> and only one of them means "2nd year". A
/// subject block mixes years by construction, so parsing its digit mislabels every student in it.
/// </para>
///
/// <para>
/// <b>The separator is the student's own programme code, not a list of subject codes.</b> A
/// <em>home section</em> is one whose <c>SectionKey</c> begins with the <c>CodeKey</c> of the programme
/// on that student's own <c>StudentTermRecord</c>: <c>BSCRIM2A</c> starts with <c>BSCRIM</c>,
/// <c>NSTP2</c> and <c>ROTC</c> do not. The alternative — enumerating the subject codes to exclude —
/// rots on the first new GE offering the registrar invents, and rots silently, because a missed code
/// produces a plausible year rather than an error.
/// </para>
///
/// <para>
/// <b>Ambiguity yields <c>null</c>, never a guess (D-47).</b> A student in no year group is a visible
/// gap somebody can go and fix; a student in the <em>wrong</em> year group is an invisible wrong
/// denominator that nothing downstream can distinguish from a right one. This is the same judgement
/// <c>Students.Course/YearLevel/Section</c> already lost once — see ADR-001 D-2.
/// </para>
///
/// <para>
/// <b>A derived year is written only to <c>StudentTermRecord.YearLevel</c> (D-48).</b> It is never
/// promoted to <c>Students.YearLevel</c>, which stays the D-2 display cache <c>EamsDbContext</c>
/// refuses writes to; deriving a value does not make it a join key.
/// </para>
/// </summary>
public static class YearLevels
{
    /// <summary>Matches <c>StudentTermRecords.YearLevel nvarchar(50)</c> in the academic-layer migration.</summary>
    /// <remarks>
    /// <see cref="Derive"/> can only ever produce a single character, so nothing it writes can approach
    /// this. The constant is here because the column's width is a fact a future writer of this field —
    /// a real year column in a later export, say — has to agree with, and until now the only statement
    /// of it was the migration.
    /// </remarks>
    public const int MaxLength = 50;

    /// <summary>
    /// The year level implied by a student's enrolled sections, or <c>null</c> when the sections do not
    /// imply exactly one.
    /// </summary>
    /// <param name="programCodeKey">
    /// The <c>CodeKey</c> of the programme on the student's own <c>StudentTermRecord</c> — the anchor
    /// the whole rule turns on. A student with no programme has no anchor and therefore no year:
    /// <c>null</c>, blank and <see cref="AcademicKey.Unspecified"/> all yield <c>null</c> rather than
    /// falling back to "any section with a digit", which is precisely the naive reading D-47 refuses.
    /// </param>
    /// <param name="enrolledSectionKeys">
    /// The normalized <c>CourseOfferings.SectionKey</c> of every offering the student is enrolled in for
    /// the term. Duplicates are harmless; order is irrelevant.
    /// </param>
    /// <returns>
    /// A single digit as a string (<c>"1"</c>…<c>"9"</c>) when exactly one distinct year digit appears
    /// across the home sections; <c>null</c> when none does, when the student has no home section, or
    /// when two home sections disagree (<c>BSIT 2-A</c> <em>and</em> <c>BSIT 3-A</c> is a roster error,
    /// not a tie to be broken).
    /// </returns>
    public static string? Derive(string? programCodeKey, IEnumerable<string> enrolledSectionKeys)
    {
        // Both guards yield null rather than widening. `Unspecified` is the sentinel a blank source
        // value becomes, and it is a claim that the programme was not recorded — not a programme code
        // that happens to prefix nothing.
        if (string.IsNullOrEmpty(programCodeKey) || programCodeKey == AcademicKey.Unspecified)
        {
            return null;
        }

        string? found = null;

        foreach (var sectionKey in enrolledSectionKeys)
        {
            if (!TryReadYearDigit(programCodeKey, sectionKey, out var year)) continue;

            if (found is null)
            {
                found = year;
                continue;
            }

            // Two home sections naming different years. Returning either would be picking one, and a
            // deterministic pick is still a wrong answer half the time — with nothing on screen to say
            // a choice was made. The gap is the honest result.
            if (!string.Equals(found, year, StringComparison.Ordinal)) return null;
        }

        return found;
    }

    /// <summary>
    /// The year digit a single section key implies for a student of this programme, if any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both keys arrive already normalized by <see cref="AcademicKey"/> — upper-cased, letters and
    /// digits only — so the prefix test is ordinal and needs no culture or case argument. Comparing raw
    /// display values here would fail on <c>'BSCRIM 2-A'</c> versus <c>'BSCRIM'</c> while looking
    /// entirely correct.
    /// </para>
    /// <para>
    /// <b>The digit must be the very next character, and the character after it must not be a digit.</b>
    /// Both halves are load-bearing and both exist to refuse rather than to accept:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Immediately after the code.</b> A programme code is frequently a prefix of another
    ///     programme's — <c>BSN</c> prefixes <c>BSNED1A</c> — and scanning further into the remainder
    ///     would read a neighbouring programme's cohort as this student's year. A remainder that starts
    ///     with letters is a different cohort, not this one, so it contributes nothing.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Exactly one digit.</b> D-47 says "year digit", singular, and a two-digit run
    ///     (<c>BSIT 12-A</c>) is a shape nobody has explained. Taking its first character would invent
    ///     a year 1 out of something that is not one; refusing it yields <c>null</c>, which is the
    ///     outcome the rule prefers whenever it is unsure.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <c>0</c> is excluded for the same reason: there is no zeroth year, so a section keyed
    /// <c>BSIT0A</c> is a data problem and not a cohort.
    /// </para>
    /// </remarks>
    private static bool TryReadYearDigit(string programCodeKey, string sectionKey, out string year)
    {
        year = "";

        if (!sectionKey.StartsWith(programCodeKey, StringComparison.Ordinal)) return false;

        var remainder = sectionKey.AsSpan(programCodeKey.Length);

        if (remainder.Length == 0) return false;
        if (remainder[0] is < '1' or > '9') return false;
        if (remainder.Length > 1 && char.IsAsciiDigit(remainder[1])) return false;

        year = remainder[..1].ToString();
        return true;
    }

    /// <summary>
    /// How a year level reads to a person — <c>"2"</c> becomes <c>"2nd Year"</c>.
    /// </summary>
    /// <remarks>
    /// Only the display half. The stored value stays the bare digit, because that is what a group's
    /// <c>SourceKey</c> is matched on across runs and an ordinal suffix is a rendering decision that
    /// must not become part of a key. Anything <see cref="Derive"/> could not have produced is returned
    /// unchanged rather than mangled into a suffix that would be wrong: this labels values, it does not
    /// validate them, and a group named after a value nobody recognises is easier to investigate than
    /// one named "13th Year".
    /// </remarks>
    public static string DisplayName(string yearLevel) => yearLevel switch
    {
        "1" => "1st Year",
        "2" => "2nd Year",
        "3" => "3rd Year",
        "4" => "4th Year",
        "5" => "5th Year",
        "6" => "6th Year",
        "7" => "7th Year",
        "8" => "8th Year",
        "9" => "9th Year",
        _ => yearLevel,
    };
}
