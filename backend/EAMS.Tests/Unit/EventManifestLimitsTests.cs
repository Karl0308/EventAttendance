using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The manifest ceiling, pinned against a roster that exists rather than against one nobody has
/// described.
///
/// <para>
/// <b>Why this file exists.</b> <c>MaxAttendees</c> was 20,000, justified in its own doc comment as
/// "roughly two orders of magnitude above the largest institution-wide event anyone has described".
/// That was true of the descriptions and false of the institution. On 2026-08-28 the first real
/// registrar export imported 21,493 students, an event invited all of them, and every device pulling
/// the manifest got <c>413 ManifestTooLarge</c> while students queued at the scanner.
/// </para>
///
/// <para>
/// A boundary test over the real endpoint would have to build twenty thousand students and is not
/// worth its runtime. What is worth pinning is cheap and is the thing that actually went wrong: the
/// ceiling must stay above the roster this system is known to hold. If someone lowers it back — or
/// the observed roster is updated after a bigger import and the ceiling is not revisited — this fails
/// and names the incident.
/// </para>
/// </summary>
public class EventManifestLimitsTests
{
    [Fact]
    public void The_ceiling_is_above_the_largest_roster_this_system_has_actually_held()
    {
        Assert.True(
            EventManifestLimits.MaxAttendees > EventManifestLimits.LargestObservedRoster,
            $"A manifest carries at most {EventManifestLimits.MaxAttendees} attendees, but the roster " +
            $"in production holds {EventManifestLimits.LargestObservedRoster}. An event inviting the " +
            "whole school would refuse with 413 and no device could pull a manifest. This is the " +
            "2026-08-28 incident; see EventManifestLimits for what the previous ceiling was justified " +
            "by and why that justification failed.");
    }

    /// <summary>
    /// Headroom, not merely "above". A ceiling one intake above the roster is a ceiling that gets
    /// re-tripped in a term, at which point it is discovered the same way this one was — by students
    /// standing in a queue.
    /// </summary>
    [Fact]
    public void The_ceiling_leaves_room_for_the_roster_to_grow()
    {
        Assert.True(
            EventManifestLimits.MaxAttendees >= EventManifestLimits.LargestObservedRoster * 2,
            $"{EventManifestLimits.MaxAttendees} is less than twice the {EventManifestLimits.LargestObservedRoster} " +
            "students already on file. The roster grows every intake, and this limit is only ever " +
            "found by reaching it in production.");
    }

    /// <summary>
    /// Still a ceiling, and this is the arm that stops "make it bigger" being repeated until the check
    /// is decorative. The manifest is never paged, so the whole body is composed in memory and hashed;
    /// at roughly a quarter-kilobyte of JSON per attendee the bound below is about a hundred and
    /// twenty-five megabytes, which no phone should be asked to hold. Passing this test is not
    /// permission to raise the number again without deciding about response size.
    /// </summary>
    [Fact]
    public void The_ceiling_is_a_real_bound_rather_than_an_effective_infinity()
    {
        Assert.True(
            EventManifestLimits.MaxAttendees <= 500_000,
            $"{EventManifestLimits.MaxAttendees} attendees is not a limit any device could act on. A " +
            "manifest is never paged and never truncated (D-46), so this bound is also the size of a " +
            "response the server composes in memory and a phone parses.");
    }
}
