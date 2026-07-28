namespace EAMS.Domain;

// The closed value sets the Technical Plan §4 defines for the enum-ish `nvarchar` columns.
//
// **Why `const string` and not a CLR enum.** These columns are `nvarchar(20)` in the §4 baseline
// migration and rows already exist in them. Converting to an enum means an `ALTER COLUMN TYPE` plus
// a value rewrite, which is a data migration under the global no-DROP/no-rename rule — a scheduled
// piece of work, not a side effect of closing a validation gap. CLAUDE.md says the same thing.
//
// **Why they exist at all.** `status=Banana` reached the database because there was nowhere to look
// up the valid set: the only statement of it was a trailing comment on an entity property and a
// paragraph in §4.9, neither of which a caller or a validator can reference. Every literal that has
// to agree with a §4 value set now names one of these, so "what is valid?" has exactly one answer
// and adding a value is one edit rather than a search for string literals.
//
// **Matching is case-insensitive, storage is canonical.** `TryNormalize` accepts any casing and
// hands back the canonical spelling, which is what gets written. Two reasons: the summary buckets in
// `EventService` count with C# `==` (ordinal, case-sensitive), so a stored `"present"` would land in
// no bucket and reproduce the very defect this closes; and rejecting a client over casing alone is a
// 400 that teaches nothing. Liberal in what is accepted, canonical in what is stored.

/// <summary>Technical Plan §4.9 — <c>AttendanceRecords.Status</c>.</summary>
public static class AttendanceStatus
{
    public const string Present = "Present";
    public const string Late = "Late";
    public const string Absent = "Absent";
    public const string Excused = "Excused";

    public static readonly IReadOnlyList<string> All = [Present, Late, Absent, Excused];

    /// <summary>
    /// Maps any casing of a documented status onto its canonical spelling. Returns <c>false</c> for
    /// anything outside the set — including null, blank, and the over-length strings that used to
    /// reach SQL Server and come back as a truncation error.
    /// </summary>
    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// Technical Plan §4.9 — <c>AttendanceRecords.Notes</c>.
///
/// <para>
/// A length, not a value set, but it belongs here for the same reason the sets do: it is a §4 column
/// constraint that callers have to agree with, and the only previous statement of it was the column
/// definition itself. Unbounded <c>notes</c> reached SQL Server and came back as error 2628 (<c>String
/// or binary data would be truncated</c>) — a 500 on a caller's malformed input, and the second half
/// of the same defect the status set closed.
/// </para>
/// </summary>
public static class AttendanceNotes
{
    /// <summary>Matches <c>AttendanceRecords.Notes nvarchar(500)</c> in the §4 baseline migration.</summary>
    public const int MaxLength = 500;

    /// <summary>Null and empty are valid — <c>Notes</c> is nullable.</summary>
    public static bool IsValid(string? value) => value is null || value.Length <= MaxLength;
}

/// <summary>
/// Technical Plan §4.7 — <c>StudentGroups.Name</c>. A length rather than a value set, here for the
/// same reason <see cref="AttendanceNotes"/> is: the ADR-001 D-1 projection <em>composes</em> this
/// name (label plus term) instead of copying a user's input, so it can produce an over-length value
/// from perfectly ordinary data — a long college name in a long-coded term — and the failure would be
/// SQL Server error 2628 mid-projection rather than anything a reader could act on.
/// </summary>
public static class GroupName
{
    /// <summary>Matches <c>StudentGroups.Name nvarchar(150)</c> in the §4 baseline migration.</summary>
    public const int MaxLength = 150;

    /// <summary>
    /// Trims a composed group name to fit, marking the cut with an ellipsis so a truncated name is
    /// visibly truncated rather than merely odd.
    ///
    /// <para>
    /// Truncating is safe <em>here</em> and would not be on a key: the projection identifies a group
    /// by <c>StudentGroups.SourceKey</c>, so two names colliding after a trim is a cosmetic clash and
    /// not a merged group. Contrast <see cref="AcademicKey"/>, which deliberately never truncates.
    /// </para>
    /// </summary>
    public static string Clamp(string name) =>
        name.Length <= MaxLength ? name : string.Concat(name.AsSpan(0, MaxLength - 1), "…");
}

/// <summary>Technical Plan §4.9 — <c>AttendanceRecords.CaptureMethod</c>.</summary>
public static class CaptureMethod
{
    public const string Rfid = "Rfid";
    public const string Manual = "Manual";
    public const string Import = "Import";

    public static readonly IReadOnlyList<string> All = [Rfid, Manual, Import];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>Technical Plan §4.5 — <c>Events.Status</c>.</summary>
public static class EventStatus
{
    public const string Draft = "Draft";
    public const string Open = "Open";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = [Draft, Open, Closed, Cancelled];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// Technical Plan §4.5 — the bounded <c>nvarchar</c> columns on <c>Events</c>.
///
/// <para>
/// Lengths rather than value sets, here for the reason <see cref="AttendanceNotes"/> is: they are §4
/// column constraints a caller has to agree with, and until the events write surface existed the only
/// statement of them was the migration. An unbounded <c>name</c> reaching SQL Server comes back as
/// error 2628 (<c>String or binary data would be truncated</c>) — a 500 on input the caller got wrong,
/// which is the exact defect <see cref="AttendanceNotes"/> was added to close one endpoint over.
/// </para>
///
/// <para>
/// <b><see cref="MinGraceMinutes"/> is a rule, not a column width.</b> §4.5 types <c>GraceMinutes</c>
/// as <c>int</c> with no lower bound, and a negative one is accepted by SQL Server and quietly
/// poisonous: the tap path computes <c>when &lt;= StartAt.AddMinutes(GraceMinutes)</c>, so −30 marks a
/// student Late for arriving twenty minutes early, with nothing anywhere to explain it.
/// </para>
/// </summary>
public static class EventText
{
    /// <summary>Matches <c>Events.Name nvarchar(200)</c>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Matches <c>Events.Description nvarchar(2000)</c>.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>Matches <c>Events.Location nvarchar(300)</c>.</summary>
    public const int LocationMaxLength = 300;

    /// <summary>A grace period cannot run backwards. See the type remarks.</summary>
    public const int MinGraceMinutes = 0;

    /// <summary>
    /// Twenty-four hours. Not a column constraint — an upper bound that keeps a fat-fingered
    /// <c>GraceMinutes</c> from silently making every arrival Present forever, which reproduces the
    /// meaningless-but-plausible number this whole phase exists to remove.
    /// </summary>
    public const int MaxGraceMinutes = 24 * 60;

    /// <summary><c>Events.Name</c> is NOT NULL and is what every list renders. Blank is not a name.</summary>
    public static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= NameMaxLength;

    public static bool IsValidDescription(string? value) =>
        value is null || value.Length <= DescriptionMaxLength;

    public static bool IsValidLocation(string? value) =>
        value is null || value.Length <= LocationMaxLength;

    public static bool IsValidGraceMinutes(int value) =>
        value is >= MinGraceMinutes and <= MaxGraceMinutes;
}

/// <summary>Technical Plan §4.5 — <c>Events.AttendanceMode</c>.</summary>
public static class AttendanceMode
{
    public const string Single = "Single";
    public const string TimeInOut = "TimeInOut";

    public static readonly IReadOnlyList<string> All = [Single, TimeInOut];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// Technical Plan §4.7 — <c>StudentGroups.Type</c>.
///
/// <para>
/// <b><see cref="College"/> and <see cref="Program"/> are additions to §4.7's set</b>
/// (<c>Course/Section/Org/Custom</c>), needed because the ADR-001 D-1 projection materializes a group
/// per college and per programme and neither maps honestly onto an existing value. Adding a value to
/// a <c>nvarchar</c> value set is additive and safe under the global no-rename rule; the four
/// original values are untouched, so no existing row changes meaning.
/// </para>
/// </summary>
public static class StudentGroupType
{
    public const string Course = "Course";
    public const string Section = "Section";
    public const string Org = "Org";
    public const string Custom = "Custom";
    public const string College = "College";
    public const string Program = "Program";

    public static readonly IReadOnlyList<string> All = [Course, Section, Org, Custom, College, Program];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// Where a <c>StudentGroup</c> or a <c>StudentGroupMember</c> came from.
///
/// <para>
/// This is the distinction the academic projection is built on, and it is on the <em>membership</em>
/// row as well as the group: a derived group can legitimately carry hand-added members (an adviser
/// added to a section's group), and the projection must never remove them. Without provenance per
/// member there is no way to tell a row the projection owns from a row a human wrote, so a set-diff
/// would silently delete the human's work on the next run.
/// </para>
/// </summary>
public static class GroupSourceType
{
    /// <summary>Created by a person. The projection never adds, removes, or edits these.</summary>
    public const string Manual = "Manual";

    /// <summary>Materialized from the academic tables. Owned by the projection; safe to re-derive.</summary>
    public const string Derived = "Derived";

    public static readonly IReadOnlyList<string> All = [Manual, Derived];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// Which academic concept a derived <c>StudentGroup</c> projects.
///
/// <para>
/// <see cref="None"/> is the sentinel for a manual group rather than a NULL, so the column is NOT NULL
/// and the derived-group unique index has no nullable component to reason about.
/// </para>
///
/// <para>
/// <see cref="Section"/> is the odd one out and the reason <c>StudentGroups.SourceKey</c> exists: a
/// college, a programme and an offering are each a real row with an <c>Id</c>, but a "section" is a
/// <em>key</em> shared by many offerings within a term (<c>BSFS2A</c> spans every course that cohort
/// takes) and has no row of its own to point at.
/// </para>
/// </summary>
public static class GroupSourceEntityType
{
    public const string None = "None";
    public const string College = "College";
    public const string Program = "Program";
    public const string Section = "Section";
    public const string CourseOffering = "CourseOffering";

    public static readonly IReadOnlyList<string> All = [None, College, Program, Section, CourseOffering];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// The one implementation of "is this one of the documented values, and what is it spelled like".
/// Private to the domain: callers use the set they mean (<see cref="AttendanceStatus"/> and
/// friends) so the compiler stops a status being validated against the event-status set.
/// </summary>
internal static class DomainValueSet
{
    public static bool TryNormalize(IReadOnlyList<string> all, string? value, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        foreach (var candidate in all)
        {
            if (!string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase)) continue;
            canonical = candidate;
            return true;
        }

        return false;
    }
}
