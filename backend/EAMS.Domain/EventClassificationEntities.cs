namespace EAMS.Domain;

/// <summary>
/// <b>The institution's vocabulary for "what kind of event is this" — Institutional, Departmental,
/// Organizational, and whatever the client adds later.</b> Not in Technical Plan §4; additive, recorded
/// as drift.
///
/// <para>
/// <b>A table rather than a <c>string</c> column on <c>Events</c>, and for the same reason
/// <see cref="Classification"/> is one.</b> The three starting values are a client requirement, not a
/// closed set the code owns: the spec says explicitly that "additional classifications [may] be added
/// in the future", and QA curates the list. A denormalized string column would make a rename a mass
/// <c>UPDATE</c> over every event carrying it, and two spellings of one classification indistinguishable
/// from two classifications. Identity is <see cref="Entity.Id"/>, never <see cref="Name"/>, so an event
/// (once it references one — that link lands with the Event Audience module) holds the GUID and survives
/// every rename.
/// </para>
///
/// <para>
/// <b>This is the vocabulary only. Nothing here links an event to a classification yet</b>, and that
/// absence is deliberate: it mirrors how <see cref="Classification"/> shipped its vocabulary before the
/// person-to-classification assignment, so the master list is correct and testable on its own. The
/// <c>Events.EventClassificationId</c> foreign key, and the "classification then audience" event-creation
/// flow the spec describes, arrive with the Event Audience module that depends on this one.
/// </para>
///
/// <para>
/// <b>Distinct from <see cref="Classification"/> despite the shared word.</b> That type is the
/// <em>person</em> axis the access-control export carries (<c>STUDENT</c>, <c>NAP</c>, <c>ACAD</c>…);
/// this one classifies <em>events</em>. They share no rows, no table and no route — a name collision
/// worth stating once so nobody folds them together.
/// </para>
/// </summary>
public class EventClassification : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>
    /// Display form, stored exactly as authored. Never trimmed on the way in
    /// (see <see cref="EventClassificationText.IsValidName"/>): a trimmed <c>' Institutional Events'</c>
    /// and <c>'Institutional Events'</c> would be two rows that render identically in a picker.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// <see cref="AcademicKey.Normalize"/> of <see cref="Name"/> — letters and digits only, upper-cased.
    /// The column <c>UX_EventClassifications_SchoolId_NameKey</c> is built on, so that
    /// <c>Departmental Events</c>, <c>departmental-events</c> and <c>DEPARTMENTALEVENTS</c> are one
    /// classification rather than three spellings of a split population.
    /// </summary>
    public string NameKey { get; set; } = "";

    /// <summary>
    /// The optional description the spec lists as a field — "Captures attendance for all university
    /// stakeholders" and the like. Nullable: a classification an administrator types in a hurry does not
    /// have to be documented before it can be used.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether the classification is offered for new events. <c>false</c> is <b>deactivated</b>: it
    /// disappears from the event-creation picker and every event already recorded under it keeps it.
    /// The spec's "Activate/Deactivate" in one flag, and the safe half of "delete".
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When <see cref="IsActive"/> last went false. Null while active, cleared again on reactivation, so
    /// it always describes the row's current state rather than its history — an audit trail belongs in
    /// <c>AuditLogs</c>, not this column.
    /// </summary>
    public DateTime? RetiredAt { get; set; }
}

/// <summary>
/// The <c>EventClassifications</c> column rules, in the domain for the reason <see cref="ClassificationText"/>
/// is: the next writer that is not an HTTP request — a seed, a repair script — inherits them rather than
/// re-deriving them.
/// </summary>
public static class EventClassificationText
{
    /// <summary>Matches <c>EventClassifications.Name nvarchar(100)</c>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Matches <c>EventClassifications.Description nvarchar(1000)</c>.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>
    /// Present, within <see cref="NameMaxLength"/>, identical to its own trimmed form, and carrying at
    /// least one letter or digit so it has a <see cref="EventClassification.NameKey"/> at all.
    ///
    /// <para>
    /// The trim clause is a <em>refusal</em> rather than a silent fix, exactly as
    /// <see cref="ClassificationText"/>'s and <see cref="TermText"/>'s are: silently trimming would
    /// accept two requests that differ and store one value, so an administrator who typed a trailing
    /// space would be told nothing and would then wonder why their "second" classification was reported
    /// as a duplicate.
    /// </para>
    /// </summary>
    public static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= NameMaxLength
        && value == value.Trim()
        && AcademicKey.Normalize(value).Length > 0;

    /// <summary>Whether a description, if supplied, fits its column. A null description is valid.</summary>
    public static bool IsValidDescription(string? value) =>
        value is null || value.Length <= DescriptionMaxLength;

    /// <summary>The stored key for a display name — <see cref="AcademicKey.Normalize"/>, named once.</summary>
    public static string KeyFor(string name) => AcademicKey.Normalize(name);
}

/// <summary>
/// The three event classifications the client's specification lists as the initial set, with the
/// description each ships with.
///
/// <para>
/// <b>In the domain rather than in <c>SeedData</c> because two callers need them and only one is a
/// seed.</b> The startup seed writes them; the tests assert on them. A second copy in the test project
/// is exactly the drift <see cref="ClassificationSeedValues"/> and <c>SeedData.DevelopmentCardUid</c>
/// record having been bitten by.
/// </para>
///
/// <para>
/// <b>The spec is explicit that the list grows</b>, so this is a starting point, not a closed set: an
/// administrator adds a fourth through <c>POST /event-classifications</c>, and it is as real as these.
/// </para>
/// </summary>
public static class EventClassificationSeedValues
{
    /// <summary>One seeded value: its display name and its description, both as the spec spells them.</summary>
    public record Seed(string Name, string Description);

    public static IReadOnlyList<Seed> All { get; } =
    [
        new("Institutional Events", "Captures attendance for all university stakeholders."),
        new("Departmental Events", "Captures attendance for students and personnel of a specific department."),
        new("Organizational Events", "Captures attendance for members of a designated organization."),
    ];

    /// <summary>The display names alone, for the assertions and reads that do not care about descriptions.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(s => s.Name)];

    private static readonly HashSet<string> Keys =
        All.Select(s => EventClassificationText.KeyFor(s.Name)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="nameKey"/> is one of the three the seed puts back — keyed on
    /// <see cref="EventClassification.NameKey"/> for the reason <see cref="ClassificationSeedValues.IsSeededKey"/>
    /// is: it answers exactly "would the seed re-create this row?" and so cannot drift from the seed's own
    /// guard. <c>EventClassificationService.DeleteAsync</c> is the caller.
    /// </summary>
    public static bool IsSeededKey(string nameKey) => Keys.Contains(nameKey);
}
