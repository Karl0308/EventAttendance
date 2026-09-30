using System.Security.Cryptography;

namespace EAMS.Domain;

/// <summary>
/// <b>One attendee's unique attendance code for one event (LiveAttendance.docx §3).</b> Generated in the
/// Live Attendance back office and, optionally, emailed to the attendee (§4).
///
/// <para>
/// <b>Unique two ways, both index-backed.</b> A code is unique within its event (§3: "each code must be
/// unique across all attendees for the applicable event"), and an attendee holds at most one code per event
/// (§5: "do not generate duplicate codes for the same attendee unless a deliberate regeneration process is
/// implemented"). The first is <c>UX_EventAttendanceCodes_EventId_Code</c>; the second is
/// <c>UX_EventAttendanceCodes_EventId_StudentId</c>. Regeneration rewrites the <see cref="Code"/> on the
/// existing row rather than inserting a second.
/// </para>
///
/// <para>
/// <b>An attendee is a student with an attendance record for the event</b> — the people Live Attendance
/// lists. The code is keyed on the student, not the attendance record, because a <c>TimeInOut</c> event has
/// one attendee across two taps.
/// </para>
///
/// <para>
/// <b>The event FK is <c>Restrict</c>, like every FK in this model.</b> An event that has issued codes
/// cannot be hard-deleted, exactly as one with attendance records cannot — the record must not disappear
/// behind a cascade.
/// </para>
/// </summary>
public class EventAttendanceCode : AuditableEntity
{
    /// <summary>The owning tenant, denormalized from <see cref="Event"/> — the same shape every additive
    /// table under this event carries, so its indexes can be tenant-scoped without a join.</summary>
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    /// <summary>The code itself — see <see cref="AttendanceCode"/> for the alphabet and length.</summary>
    public string Code { get; set; } = "";

    /// <summary>When the code was last emailed to the attendee, or null if it never has been.</summary>
    public DateTime? LastEmailedAt { get; set; }

    /// <summary>How many times the code has been emailed — for the "prevent unintended duplicate emails"
    /// display (§5), never a hard block.</summary>
    public int EmailCount { get; set; }
}

/// <summary>
/// The attendance-code token rules. Codes are short, human-transcribable, and drawn from an alphabet with
/// no visually ambiguous characters (no <c>0/O</c>, <c>1/I/L</c>), so an attendee reading one off an email
/// and typing it back does not fail on a misread glyph.
/// </summary>
public static class AttendanceCode
{
    /// <summary>Crockford-style alphabet minus the ambiguous glyphs — 30 symbols.</summary>
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public const int Length = 8;

    public const int MaxLength = 16;

    /// <summary>A fresh random code. Uniqueness is decided by the index; the caller retries on collision.</summary>
    public static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(chars);
    }
}

/// <summary>Who an attendance-code email goes to (LiveAttendance.docx §4).</summary>
public static class AttendanceCodeEmailMode
{
    /// <summary>Every attendee with a code and a valid email address.</summary>
    public const string All = "All";

    /// <summary>Only the attendees the administrator ticked.</summary>
    public const string Selected = "Selected";

    public static readonly IReadOnlyList<string> AllModes = [All, Selected];

    public static bool TryNormalize(string? value, out string canonical) =>
        DomainValueSet.TryNormalize(AllModes, value, out canonical);
}
