namespace EAMS.Domain;

// Technical Plan §4.6 (EventSchedules), §4.7 (StudentGroups / StudentGroupMembers), §4.8 (EventGroups).

// §4.6 EventSchedules — materialized occurrences of a recurring Event.
public class EventSchedule : AuditableEntity
{
    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    public string? RecurrenceRule { get; set; } // iCal RRULE
    public DateTime OccurrenceStartAt { get; set; }
    public DateTime OccurrenceEndAt { get; set; }
    public bool IsCancelled { get; set; }
}

// §4.7 StudentGroups
/// <summary>
/// A named set of students. §4.8 <c>EventGroups</c> points at these to say who is invited to an event,
/// and the "expected attendees" denominator §4.5/§12 describe is counted from them.
///
/// <para>
/// <b>The provenance columns (ADR-001 D-1) are what let the academic layer arrive without changing any
/// of that.</b> Colleges, programmes, sections and course offerings are <em>projected</em> into this
/// table as ordinary group rows marked <c>SourceType = Derived</c>, so <c>EventGroups</c>, the
/// denominator and every existing query keep working unchanged — they never learn that the academic
/// tables exist. The alternative, teaching <c>EventGroups</c> to target an offering directly, would
/// mean a nullable-FK-per-target-type union on the hottest reporting path in the system.
/// </para>
///
/// <para>
/// Manual groups (<c>SourceType = Manual</c>) — "SSC Officers", "Dean's Listers" — have no academic
/// counterpart and are untouched by the projection.
/// </para>
/// </summary>
public class StudentGroup : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    /// <summary>
    /// Display name. For a derived group this <b>carries the term</b> — <c>"BSFS 2-A (2025-2026-1)"</c>
    /// — so an <c>EventGroups</c> row written last semester still reads as the cohort it actually
    /// invited once the same section name is reused next semester against a different set of students.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>See <see cref="StudentGroupType"/>. §4.7's set plus College/Program.</summary>
    public string Type { get; set; } = "";

    // ------------------------------------------------------------------ ADR-001 D-1 provenance

    /// <summary>See <see cref="GroupSourceType"/>. Manual by default: a row written by any existing
    /// code path predates the projection and belongs to a person.</summary>
    public string SourceType { get; set; } = GroupSourceType.Manual;

    /// <summary>See <see cref="GroupSourceEntityType"/>. <c>None</c> on a manual group.</summary>
    public string SourceEntityType { get; set; } = GroupSourceEntityType.None;

    /// <summary>
    /// The academic row this group projects, when one exists — a college, a programme, or a course
    /// offering. Null for a manual group and for a <c>Section</c> group, which projects a key rather
    /// than a row.
    /// </summary>
    public Guid? SourceEntityId { get; set; }

    /// <summary>
    /// The projection's identity for this group within <c>(SchoolId, TermId, SourceEntityType)</c>,
    /// and the column its unique index is built on. The entity id in <c>D</c> format where there is
    /// one; the normalized section key for a <c>Section</c> group. Empty on a manual group, which the
    /// index excludes.
    ///
    /// <para>
    /// A string rather than reusing <see cref="SourceEntityId"/> because a section has no row to point
    /// at, and a synthesized GUID for it would be stable but unreadable — this way a bad projection is
    /// diagnosable from a row dump.
    /// </para>
    /// </summary>
    public string SourceKey { get; set; } = "";

    /// <summary>The term a derived group belongs to. Null on a manual group.</summary>
    public Guid? TermId { get; set; }
    public Term? Term { get; set; }

    /// <summary>
    /// When the projection last reconciled this group — stamped on every run, including runs that
    /// changed nothing, so "synced, no change" is distinguishable from "never synced".
    /// </summary>
    public DateTime? LastSyncedAt { get; set; }

    public ICollection<StudentGroupMember> Members { get; set; } = new List<StudentGroupMember>();
}

// §4.7 StudentGroupMembers — junction. §4's column list carries no CreatedAt/UpdatedAt.
public class StudentGroupMember : Entity
{
    public Guid StudentGroupId { get; set; }
    public StudentGroup? StudentGroup { get; set; }

    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    /// <summary>
    /// Who put this student in this group. <b>The projection only ever removes rows marked
    /// <see cref="GroupSourceType.Derived"/>.</b> Provenance lives on the membership rather than only
    /// on the group because a derived group can legitimately carry hand-added members, and a set-diff
    /// with no way to tell them apart would delete them on the next run.
    ///
    /// <para>
    /// Defaults to <see cref="GroupSourceType.Manual"/>, which is also what the migration backfills
    /// onto existing rows — every membership written before the projection existed was written by a
    /// person, so the default is the true statement rather than a convenient one.
    /// </para>
    /// </summary>
    public string SourceType { get; set; } = GroupSourceType.Manual;
}

// §4.8 EventGroups — associates an Event with either a StudentGroup or an individual Student (XOR).
public class EventGroup : Entity
{
    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    public Guid? StudentGroupId { get; set; }
    public StudentGroup? StudentGroup { get; set; }

    public Guid? StudentId { get; set; }
    public Student? Student { get; set; }
}
