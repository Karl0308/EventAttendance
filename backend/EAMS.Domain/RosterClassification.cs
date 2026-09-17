namespace EAMS.Domain;

/// <summary>
/// <b>How a roster row answers "what kind of person is this".</b> The Task 5 derivation, in the domain
/// and pure, for the reason <see cref="ClassificationAssignment"/> is: the importer is not the only
/// caller that will ever need it, and the rule is the part worth pinning independently of a database.
///
/// <para>
/// <b>The resolution is two-tier, and the tiering is the decision.</b>
/// </para>
///
/// <list type="number">
///   <item>
///     <description>
///     <b>The row's own category columns.</b> The client's export carries four of them — one per
///     <see cref="ClassificationAxis"/> — and they are the truth. A person holds at most one value per
///     axis and may hold several axes at once.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>The <see cref="PersonnelNumberPrefix"/> on the registration number</b>, for a row that carries
///     no category in any column. It says <em>personnel</em> and nothing more precise — see
///     <see cref="Outcome.PersonnelNumberOnly"/> — so it resolves to no classification and is reported.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Neither: no classification and a warning.</b> Never a default to <c>STUDENT</c>. A
///     21,497-row import that guessed would invent 34 students, and nothing downstream could tell them
///     from the 20,861 the file actually names.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// <b>Why the prefix is the fallback and not the rule, which is the whole content of this type.</b>
/// QA's answer to Q3 is that personnel are recognised by a registration number shaped
/// <c>720000XXXX</c>. Measured against the 21,497-row export that rule is wrong in both directions:
/// <b>21 of the 292 NAP staff</b> carry numbers like <c>0020255</c>, <c>0020240</c> and <c>0000000</c>,
/// and <b>one row carrying a <c>720000</c> number is flagged <c>STUDENT</c></b> and nothing else. A
/// prefix-first derivation misfiles all 22. The category columns misfile none, because the source
/// already answered the question and reading the column beats interpreting the number — the same
/// lesson <see cref="ClassificationSeedValues"/> records about inferring an axis from what a word
/// looks like.
/// </para>
///
/// <para>
/// <b>And the prefix cannot name a value even where it fires.</b> Among rows carrying it the personnel
/// column holds <c>NAP</c> (271), <c>ACAD</c> (271), <c>ANT</c> (7) and
/// <c>SUPERVISORY/MANAGERIAL</c> (1), and 12 rows carrying it are <c>USA FRIARS</c>. So "this number
/// looks like personnel" narrows the answer to one of five and is not an answer. Tier 2 therefore
/// reports rather than resolves, which is the one thing it can do that is not a guess.
/// </para>
///
/// <para>
/// <b>271 for <c>NAP</c> counts the 2 dual <c>NAP</c>+<c>STUDENT</c> rows, and an earlier draft of this
/// paragraph said 269 by leaving them out.</b> Worth the sentence because the mistake is structural
/// rather than arithmetic: 269 is the population of the NAP-<em>only</em> stratum, which is what a
/// cross-tabulation prints, while the number this paragraph needs is everyone the personnel column
/// says NAP about. A person holding two categories belongs to both counts, which is the whole premise
/// of the axis model — so any tally that partitions rows will undercount every value the 3 dual rows
/// touch.
/// </para>
/// </summary>
public static class RosterClassification
{
    /// <summary>
    /// The registration-number prefix QA named as the personnel marker (Q3). Kept as a constant because
    /// two places have to agree about it — this derivation and the message that explains it — and
    /// because a literal <c>"720000"</c> in either would read as a magic number rather than as a rule
    /// somebody stated.
    /// </summary>
    public const string PersonnelNumberPrefix = "720000";

    /// <summary>What tier answered, which is what a caller reports on.</summary>
    public enum Outcome
    {
        /// <summary>
        /// Tier 1. At least one category column carried a value, and <see cref="Resolution.Categories"/>
        /// lists every one of them.
        /// </summary>
        Categorised,

        /// <summary>
        /// Tier 2. No category column carried anything, but the registration number starts with
        /// <see cref="PersonnelNumberPrefix"/>.
        ///
        /// <para>
        /// <b><see cref="Resolution.Categories"/> is empty, deliberately, and that is not tier 2
        /// failing to do its job.</b> The prefix narrows the answer to "one of the four personnel
        /// values, or possibly a friar" and the vocabulary has no word for that — the eight
        /// classifications are the ones the export contains, and inventing a ninth to hold a guess
        /// would put a value in an administrator's picker that no source document ever used. So this
        /// says loudly which four rows are affected and leaves the assignment to a person.
        /// </para>
        /// </summary>
        PersonnelNumberOnly,

        /// <summary>
        /// Tier 3. No category column and no prefix. 30 of the sample's 34 uncategorised rows, of which
        /// 4 are junk (<c>Personnel No</c> equal to <c>Last Name</c>) and 26 look like students whose
        /// flag was never set. They are reported, never defaulted.
        /// </summary>
        None,
    }

    /// <summary>One axis and the value the row carried in that axis's column.</summary>
    /// <param name="Axis">A <see cref="ClassificationAxis"/> value.</param>
    /// <param name="Value">
    /// The category as the file spells it, cleaned but <b>not</b> normalized — matching against the
    /// vocabulary is <see cref="ClassificationText.KeyFor"/>'s job, and keeping the display form here
    /// is what lets a warning quote the cell rather than its key.
    /// </param>
    public record Category(string Axis, string Value);

    /// <param name="Outcome">Which tier answered.</param>
    /// <param name="Categories">
    /// The row's categories, in <see cref="ClassificationAxis.All"/> order so two runs over one file
    /// never disagree about which one is reported first. Empty unless
    /// <see cref="Outcome.Categorised"/>.
    /// </param>
    public record Resolution(Outcome Outcome, IReadOnlyList<Category> Categories);

    private static readonly Resolution NoneResolution = new(Outcome.None, []);
    private static readonly Resolution PersonnelOnlyResolution = new(Outcome.PersonnelNumberOnly, []);

    /// <summary>
    /// Applies the three tiers to one row.
    /// </summary>
    /// <param name="regNo">
    /// The row's registration number, already cleaned. Only consulted when no category column carried
    /// anything — which is the demotion this type exists to express.
    /// </param>
    /// <param name="valueByAxis">
    /// The row's four category cells, keyed by <see cref="ClassificationAxis"/>. A blank, whitespace or
    /// absent entry means "this column said nothing" and is skipped; <b>it never means "clear what is
    /// stored"</b>, which is the <c>AssignIfPresent</c> rule applied to a column the roster shares with
    /// a back-office screen.
    /// </param>
    public static Resolution Resolve(string? regNo, IReadOnlyDictionary<string, string?> valueByAxis)
    {
        List<Category>? categories = null;

        // Iterated over the canonical axis list rather than over the dictionary, so the output order is
        // a property of the domain and not of whichever order a caller happened to build its cells in.
        foreach (var axis in ClassificationAxis.All)
        {
            if (!valueByAxis.TryGetValue(axis, out var value)) continue;

            var cleaned = RosterText.Clean(value);
            if (cleaned is null) continue;

            categories ??= [];
            categories.Add(new Category(axis, cleaned));
        }

        if (categories is not null) return new Resolution(Outcome.Categorised, categories);

        return SuggestsPersonnel(regNo) ? PersonnelOnlyResolution : NoneResolution;
    }

    /// <summary>
    /// Whether a registration number carries the <see cref="PersonnelNumberPrefix"/>.
    ///
    /// <para>
    /// <b>Public, and only ever consulted after every category column has come back blank.</b> It is
    /// exposed so a message can explain <em>why</em> a row was singled out, not so a second caller can
    /// reintroduce the prefix as a primary rule — the type remarks record the 22 rows that would
    /// misfile.
    /// </para>
    /// </summary>
    public static bool SuggestsPersonnel(string? regNo) =>
        regNo is not null
        && regNo.StartsWith(PersonnelNumberPrefix, StringComparison.Ordinal);
}
