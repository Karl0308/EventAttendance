using System.Diagnostics.CodeAnalysis;

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
/// Technical Plan §4.9 — <c>AttendanceRecords.DeviceTapId</c> and <c>CheckOutDeviceTapId</c>.
///
/// <para>
/// <b>The third instance of exactly the same defect <see cref="AttendanceNotes"/> records</b>, found at
/// the Phase 4d review gate. A client-supplied string was written to a bounded <c>nvarchar</c> column
/// with nothing checking its length, so an over-length value reached SQL Server and came back as error
/// 8152/2628 (<c>String or binary data would be truncated</c>). EF sizes an over-length parameter as
/// <c>nvarchar(max)</c> rather than truncating it, so nothing upstream clips it either — and a
/// truncation is not a unique violation, so <c>AttendanceService.SaveNewRecordAsync</c>'s filter
/// correctly declines to swallow it and it propagates as an unhandled 500.
/// </para>
///
/// <para>
/// <b>Why this one was worse than the <c>Notes</c> case, and worth a CRITICAL rather than a tidy-up.</b>
/// <c>Notes</c> arrives from an organizer typing into a form, one request at a time. A
/// <c>deviceTapId</c> arrives from an offline queue that the frozen contract makes <em>required</em>
/// on the batch endpoint, whose generation scheme is still an open question with the mobile developer,
/// and §8.2 tells that client to retry a 5xx. One poison row therefore 500s the whole batch, forever,
/// with nothing in the response naming which row is bad — the exact permanently-wedged queue that
/// <see cref="EAMS.Application.Abstractions.TapOutcome.DeviceNotRegistered"/> and ADR-001 D-35 were
/// each created to close, arriving through a third route.
/// </para>
///
/// <para>
/// <b>Both tap-id columns share this limit, and they must.</b> They hold the same kind of value from
/// the same client and are read by one lookup, so a length one column accepted and the other refused
/// would reject a check-out whose check-in had been accepted — see <c>EamsDbContext</c>, which sizes
/// them identically for the same reason.
/// </para>
/// </summary>
public static class DeviceTapIds
{
    /// <summary>
    /// Matches <c>AttendanceRecords.DeviceTapId nvarchar(100)</c> — and
    /// <c>CheckOutDeviceTapId nvarchar(100)</c> — in the §4 baseline and the Phase 4c migration.
    /// </summary>
    public const int MaxLength = 100;

    /// <summary>
    /// Whether a tap id is one this system can store and key on.
    ///
    /// <para>
    /// <b>Blank is invalid here and that is not the same statement as "required".</b> Whether a caller
    /// may <em>omit</em> the field is an endpoint's decision — the batch endpoint requires one (D-33),
    /// the single tap does not — but a value that is present and unusable is unusable on both. This
    /// predicate answers the second question only, which is why <c>null</c> is <c>false</c>: the
    /// callers that permit omission check for it themselves before asking.
    /// </para>
    /// </summary>
    public static bool IsUsable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxLength;
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

/// <summary>
/// Technical Plan §4.3 — <c>Students.Status</c>.
///
/// <para>
/// The set was written down in exactly two places before this existed: a trailing comment on
/// <c>Student.Status</c> and the "Active/Inactive/Graduated" cell in §4.3's table. Neither is
/// referenceable from a validator, which is how <c>status=Banana</c> reached
/// <c>AttendanceRecords</c> and would have reached this column the moment a write endpoint existed.
/// </para>
/// </summary>
public static class StudentStatus
{
    public const string Active = "Active";
    public const string Inactive = "Inactive";
    public const string Graduated = "Graduated";

    /// <summary>§4.3's column default, and what a request that omits the field resolves to.</summary>
    public const string Default = Active;

    public static readonly IReadOnlyList<string> All = [Active, Inactive, Graduated];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(All, value, out canonical);
}

/// <summary>
/// Technical Plan §4.3 — the bounded <c>nvarchar</c> columns on <c>Students</c> that a caller
/// supplies.
///
/// <para>
/// Lengths rather than value sets, here for the reason <see cref="AttendanceNotes"/> and
/// <see cref="EventText"/> are: they are §4 column constraints a caller has to agree with, and an
/// over-length value reaching SQL Server comes back as error 2628 (<c>String or binary data would be
/// truncated</c>) — a 500 on input the caller got wrong.
/// </para>
///
/// <para>
/// <b>Deliberately absent: <c>Course</c>, <c>YearLevel</c> and <c>Section</c>.</b> They are the
/// ADR-001 D-2 derived cache and no caller may supply them, so there is nothing for a caller to
/// validate against and a length constant here would read as permission.
/// </para>
/// </summary>
public static class StudentText
{
    /// <summary>Matches <c>Students.StudentNumber nvarchar(50)</c>.</summary>
    public const int StudentNumberMaxLength = 50;

    /// <summary>Matches <c>Students.FirstName / MiddleName / LastName nvarchar(100)</c>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Matches <c>Students.Email nvarchar(256)</c>.</summary>
    public const int EmailMaxLength = 256;

    /// <summary>Matches <c>Students.Gender nvarchar(20)</c>.</summary>
    public const int GenderMaxLength = 20;

    /// <summary>Matches <c>Students.PhotoUrl nvarchar(1000)</c>.</summary>
    public const int PhotoUrlMaxLength = 1000;

    /// <summary>
    /// A NOT NULL column, judged on the <b>cleaned</b> value — what <see cref="RosterText.Clean"/>
    /// returns, which is exactly what will be stored.
    ///
    /// <para>
    /// <b>Taking the cleaned value rather than the raw one is the whole point, and it fixes a 500.</b>
    /// These checks used to run on the request as it arrived, using
    /// <c>string.IsNullOrWhiteSpace</c> + <c>Trim()</c>, while the writer stored
    /// <c>RosterText.Clean(value)</c>. Those are two different definitions of "empty" and the gap
    /// between them was reachable: a zero-width space (U+200B, and the four others
    /// <see cref="RosterText"/> strips) is Unicode category <c>Cf</c>, <em>not</em> whitespace, so
    /// <c>char.IsWhiteSpace</c> is false and <c>"​".Trim().Length</c> is 1. A first name of one
    /// zero-width space therefore passed validation, cleaned to <c>null</c>, and hit a NOT NULL column
    /// as SQL Server error 515 — an unhandled <c>DbUpdateException</c> and a 500 on input the caller
    /// got wrong, which is the exact defect class these constants exist to remove.
    /// </para>
    ///
    /// <para>
    /// <b>Length is measured on the cleaned value for a second reason.</b> <see cref="RosterText"/>
    /// composes to NFC, and NFC can <em>expand</em> a string — U+0344 becomes U+0308 U+0301, one
    /// character into two. A 100-character name measured before composition can be 200 after it, so a
    /// pre-clean length check under-counts and the column raises 2628. Measured after, the number this
    /// compares against is the number of characters that will be written.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <see cref="NotNullWhenAttribute"/> is what turns this from a convention into a compiler-checked
    /// guarantee: a caller that passes the check gets a non-nullable <c>string</c> back through flow
    /// analysis, so the write path needs no null-forgiving operator and cannot acquire one by
    /// accident. The previous version's safety lived in a comment, and the comment was wrong.
    /// </remarks>
    public static bool IsValidRequiredValue(
        [NotNullWhen(true)] string? cleaned, int maxLength = NameMaxLength) =>
        cleaned is not null && cleaned.Length <= maxLength;

    /// <inheritdoc cref="IsValidRequiredValue"/>
    public static bool IsValidOptionalValue(string? cleaned, int maxLength) =>
        cleaned is null || cleaned.Length <= maxLength;
}

/// <summary>
/// Technical Plan §4.4 — the bounded <c>nvarchar</c> columns on <c>RfidCards</c>.
///
/// <para>
/// <c>CardUid</c> is measured <em>after</em> <see cref="CardUid.Normalize"/>, because that is the form
/// that is stored: a reader that sends <c>04:A7:B8:C9</c> must be judged on the eight characters that
/// land in the column, not on the eleven it typed.
/// </para>
/// </summary>
public static class RfidCardText
{
    /// <summary>Matches <c>RfidCards.CardUid nvarchar(128)</c>.</summary>
    public const int CardUidMaxLength = 128;

    /// <summary>Matches <c>RfidCards.Label nvarchar(100)</c>.</summary>
    public const int LabelMaxLength = 100;

    /// <summary>
    /// True when the normalized UID is storable. Blank is refused rather than accepted as an empty
    /// string: a card row whose UID is <c>""</c> would occupy the one active slot for the empty UID in
    /// its school and match no tap ever, which is a silent, permanent hole in the roster.
    /// </summary>
    public static bool IsValidNormalizedCardUid(string normalizedUid) =>
        normalizedUid.Length is > 0 and <= CardUidMaxLength;
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
///
/// <para>
/// <b><see cref="YearLevel"/> is the third such addition, and it is the whole of D-49's cost.</b> Year
/// becomes an invitable audience by being projected into this table like a section, rather than by
/// teaching <c>EventGroups</c> a new target type — the alternative is another nullable FK on the
/// hottest reporting path, when every existing audience, denominator, freeze and manifest query
/// already understands a <c>StudentGroup</c>. One constant here and one projection block is the price.
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

    /// <summary>A cohort of everyone whose derived <see cref="YearLevels"/> value is the same (D-49).</summary>
    public const string YearLevel = "YearLevel";

    public static readonly IReadOnlyList<string> All =
        [Course, Section, Org, Custom, College, Program, YearLevel];

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
/// <see cref="Section"/> and <see cref="YearLevel"/> are the odd ones out, and they are the reason
/// <c>StudentGroups.SourceKey</c> exists: a college, a programme and an offering are each a real row
/// with an <c>Id</c>, but a "section" is a <em>key</em> shared by many offerings within a term
/// (<c>BSFS2A</c> spans every course that cohort takes) and a "year level" is a derived
/// <c>StudentTermRecords.YearLevel</c> value shared by many students. Neither has a row to point at, so
/// both carry a null <c>SourceEntityId</c> and are identified by their <c>SourceKey</c> alone.
/// </para>
///
/// <para>
/// <b>A year group is not filed under <see cref="None"/>, and that was the choice worth making.</b>
/// <see cref="None"/> is the sentinel that means "this group projects no academic concept at all" —
/// it is what every manual group carries, and it is what a reader tests to answer "did the projection
/// build this?". Reusing it for a derived group would make the answer to that question wrong for the
/// one group type that has no other identifying column, and it would put year groups into the same
/// <c>(SourceEntityType, SourceKey)</c> key space the projection reconciles manual-shaped rows in.
/// <see cref="Section"/> already proves the "no row of its own" shape is representable without
/// borrowing the sentinel; <see cref="YearLevel"/> is the second instance of it, not a new idea.
/// </para>
/// </summary>
public static class GroupSourceEntityType
{
    public const string None = "None";
    public const string College = "College";
    public const string Program = "Program";
    public const string Section = "Section";
    public const string CourseOffering = "CourseOffering";

    /// <summary>
    /// A derived <c>StudentTermRecords.YearLevel</c> value (D-47/D-49). <c>SourceKey</c> is the year
    /// itself — <c>"2"</c> — because there is no year-level table and D-49 deliberately does not add one.
    /// </summary>
    public const string YearLevel = "YearLevel";

    public static readonly IReadOnlyList<string> All =
        [None, College, Program, Section, CourseOffering, YearLevel];

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
