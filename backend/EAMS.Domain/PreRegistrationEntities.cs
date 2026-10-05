namespace EAMS.Domain;

/// <summary>
/// <b>A pre-registration session (PreRegistration.docx).</b> An administrator opens one against an
/// <see cref="AudienceDefinition"/>, sets a <see cref="Capacity"/>, and then registers attendees into it —
/// by RFID tap or by manual selection — up to that capacity, before the event.
///
/// <para>
/// Filed under the audience, not the event, exactly as the spec frames it ("selects the applicable Event
/// Audience and creates a pre-registration session"). The link from a session's registrants into an event's
/// attendance process is a follow-on, the same additive shape the audience-to-event link is.
/// </para>
/// </summary>
public class PreRegistrationSession : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public Guid AudienceDefinitionId { get; set; }
    public AudienceDefinition? AudienceDefinition { get; set; }

    /// <summary>
    /// The <see cref="Event"/> whose expected audience this session's registrants become once linked
    /// (ADR-008 D-71/D-73), or <c>null</c> while the session is not linked to any event. A session links
    /// to at most one event (the single FK column); an event may have many linked sessions (N:1), and its
    /// expected set is the union of their student registrants. Nullable and <c>OnDelete(Restrict)</c>:
    /// linking is non-destructive and fully reversible — clearing this restores the event's section +
    /// definition union with no data loss.
    /// </summary>
    public Guid? EventId { get; set; }
    public Event? Event { get; set; }

    /// <summary>A short label so an audience with several sessions stays legible.</summary>
    public string Name { get; set; } = "";

    /// <summary>The maximum number of attendees accepted. Registration is refused once reached (§Capacity).</summary>
    public int Capacity { get; set; }

    /// <summary>Closed sessions accept no further registrations. Reopenable.</summary>
    public bool IsClosed { get; set; }

    public ICollection<PreRegistration> Registrations { get; set; } = [];
}

/// <summary>
/// <b>One attendee pre-registered into a <see cref="PreRegistrationSession"/>.</b> The attendee is exactly
/// one of a <see cref="Student"/> or a <see cref="Personnel"/> — the XOR the check constraint enforces — and
/// each may appear at most once per session, whether they were tapped or added manually (the two filtered
/// unique indexes).
/// </summary>
public class PreRegistration : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public Guid SessionId { get; set; }
    public PreRegistrationSession? Session { get; set; }

    /// <summary>Set when the attendee is a student; null when they are personnel.</summary>
    public Guid? StudentId { get; set; }
    public Student? Student { get; set; }

    /// <summary>Set when the attendee is personnel; null when they are a student.</summary>
    public Guid? PersonnelId { get; set; }
    public Personnel? Personnel { get; set; }

    /// <summary>One of <see cref="PreRegistrantType"/> — denormalized so a read need not test which id is set.</summary>
    public string AttendeeType { get; set; } = "";

    /// <summary>One of <see cref="PreRegistrationMethod"/>.</summary>
    public string Method { get; set; } = "";

    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Where an event's expected audience is resolved from, published on the audience read so the numbers
/// explain themselves (ADR-008 D-71). <see cref="PreRegistration"/> when a session is linked and its
/// student registrants drive the denominator; <see cref="Audience"/> for the section + definition union.
/// </summary>
public static class ExpectedAudienceSource
{
    public const string PreRegistration = "PreRegistration";
    public const string Audience = "Audience";
}

/// <summary>Which kind of Academic Community member a pre-registration is for.</summary>
public static class PreRegistrantType
{
    public const string Student = "Student";
    public const string Personnel = "Personnel";

    public static readonly IReadOnlyList<string> All = [Student, Personnel];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>How an attendee was pre-registered (PreRegistration.docx: "Tapped/Manual").</summary>
public static class PreRegistrationMethod
{
    public const string Tapped = "Tapped";
    public const string Manual = "Manual";

    public static readonly IReadOnlyList<string> All = [Tapped, Manual];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>The <c>PreRegistrationSessions.Name</c> and capacity rules, in the domain like the sibling texts.</summary>
public static class PreRegistrationText
{
    public const int NameMaxLength = 150;

    /// <summary>An arbitrary but sane ceiling — a capacity above this is a typo, not a venue.</summary>
    public const int MaxCapacity = 1_000_000;

    public static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= NameMaxLength
        && value == value.Trim();

    public static bool IsValidCapacity(int value) => value is > 0 and <= MaxCapacity;
}
