namespace EAMS.Domain;

/// <summary>
/// <b>A reusable Event Audience definition (EventAudience.docx)</b> — a named audience, filed under an
/// <see cref="EventClassification"/>, with an <see cref="AudienceType"/> and the criteria that pick its
/// eligible attendees out of the existing Academic Community data.
///
/// <para>
/// <b>Named <c>AudienceDefinition</c>, not <c>EventAudience</c>, on purpose.</b> The DTOs called
/// <c>EventAudienceDto</c>/<c>EventAudienceRequest</c> already exist and mean something different — "who
/// is attached to this one event". This is the master vocabulary an organizer builds once and reuses; the
/// two are kept apart in name so nothing conflates them.
/// </para>
///
/// <para>
/// <b>The criteria are stored as JSON, not as duplicated master data (spec §7).</b> The definition holds
/// references — department names, programme codes, year levels, sections, classifications, organizations,
/// or specific person ids — and resolves them against the live roster each time, so an update to a student
/// flows through without re-maintaining anything here.
/// </para>
///
/// <para>
/// <b>The link from an <c>Event</c> to a chosen definition is not here yet</b>, exactly as
/// <see cref="EventClassification"/> shipped before the event link: this module delivers the reusable
/// master and the attendee resolution; wiring a definition into event creation and the roster freeze is an
/// additive follow-on.
/// </para>
/// </summary>
public class AudienceDefinition : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>The event classification this audience belongs to. Only audiences under the event's
    /// classification are offered during event creation (spec §5).</summary>
    public Guid EventClassificationId { get; set; }
    public EventClassification? EventClassification { get; set; }

    public string Name { get; set; } = "";

    /// <summary><see cref="AcademicKey.Normalize"/> of <see cref="Name"/>; the uniqueness key per school.</summary>
    public string NameKey { get; set; } = "";

    /// <summary>One of <see cref="AudienceType"/>.</summary>
    public string AudienceType { get; set; } = "";

    /// <summary>The selection, as JSON (<c>AudienceCriteriaDto</c>). Interpreted per <see cref="AudienceType"/>.</summary>
    public string CriteriaJson { get; set; } = "";

    public bool IsActive { get; set; } = true;
    public DateTime? RetiredAt { get; set; }
}

/// <summary>
/// <b>The link that attaches a reusable <see cref="AudienceDefinition"/> to one <see cref="Event"/></b>
/// (ADR-007 D-69). The §4.8-group analogue for definitions: an event's expected audience is the deduped
/// union of its attached sections <em>and</em> its attached definitions, each resolved live until the
/// terminal freeze.
///
/// <para>
/// <b>A link by id, not pre-resolved rows</b> — exactly the relationship <see cref="EventGroup"/> has to
/// a <c>StudentGroup</c>. The definition resolves to its current students on every read, so a student
/// enrolled tomorrow is expected tomorrow; at the transition to a terminal status the resolved students
/// are flattened into <c>EventGroups.StudentId</c> rows (ADR-003 D-13) and these link rows are
/// <em>kept</em> as the historical record of which definition was invited — like the group rows, no query
/// resolves them on a terminal event.
/// </para>
///
/// <para>
/// <b>A junction, so no <c>CreatedAt</c>/<c>UpdatedAt</c></b> — it extends <see cref="Entity"/>, the same
/// base <see cref="EventGroup"/> uses. Idempotency is a schema property: <c>UX_EventAudienceDefinitions
/// _Event_Definition</c> is a standard unique index over the two NOT NULL columns, mirroring ADR-003
/// D-12's idempotency-by-constraint. No <c>SchoolId</c> column — tenancy reaches it through
/// <see cref="Event"/>, as it does for <see cref="EventGroup"/>.
/// </para>
/// </summary>
public class EventAudienceDefinition : Entity
{
    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    public Guid AudienceDefinitionId { get; set; }
    public AudienceDefinition? AudienceDefinition { get; set; }
}

/// <summary>
/// The nine audience types (EventAudience.docx §2). <c>string</c> with a set, like every other enum-ish
/// value in this schema. University-wide's Students/Employees/Both split lives in the criteria's scope
/// rather than in three separate types, so this stays the documented nine.
/// </summary>
public static class AudienceType
{
    public const string UniversityWide = "UniversityWide";
    public const string Department = "Department";
    public const string Program = "Program";
    public const string YearLevel = "YearLevel";
    public const string Section = "Section";
    public const string EmployeeClassification = "EmployeeClassification";
    public const string Organization = "Organization";
    public const string SpecificIndividuals = "SpecificIndividuals";
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<string> All =
    [
        UniversityWide, Department, Program, YearLevel, Section,
        EmployeeClassification, Organization, SpecificIndividuals, Custom,
    ];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>The scope of a <see cref="AudienceType.UniversityWide"/> audience.</summary>
public static class AudienceScope
{
    public const string Students = "Students";
    public const string Employees = "Employees";
    public const string Both = "Both";

    public static readonly IReadOnlyList<string> All = [Students, Employees, Both];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>The <c>AudienceDefinitions.Name</c> rules, in the domain like <see cref="EventClassificationText"/>.</summary>
public static class AudienceText
{
    public const int NameMaxLength = 150;

    public static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= NameMaxLength
        && value == value.Trim()
        && AcademicKey.Normalize(value).Length > 0;

    public static string KeyFor(string name) => AcademicKey.Normalize(name);
}
