namespace EAMS.Domain;

/// <summary>
/// The one limit <c>GET /events/{id}/manifest</c> refuses on (D-46), named once so the endpoint, the
/// problem body that echoes it, and the tests that probe the boundary cannot disagree about what it is.
/// </summary>
public static class EventManifestLimits
{
    /// <summary>
    /// The largest roster this system has actually held, measured rather than estimated.
    ///
    /// <para>
    /// 21,493 students, from the first real registrar export imported on 2026-08-28. It is recorded
    /// here because it is the number that invalidated the previous ceiling, and because a limit
    /// justified against "the largest event anyone has described" needs the description kept next to
    /// it — the last one was wrong by a factor of a thousand and nothing in the code said so.
    /// </para>
    ///
    /// <para>
    /// <b>Not itself a limit.</b> Nothing refuses on it; it exists so <see cref="MaxAttendees"/> can
    /// be compared against something real, and so a test can fail if the ceiling is ever moved back
    /// underneath it.
    /// </para>
    /// </summary>
    public const int LargestObservedRoster = 21_493;

    /// <summary>
    /// Attendees per manifest. Over this the endpoint is a loud <c>413 ManifestTooLarge</c>.
    ///
    /// <para>
    /// <b>The number exists so the never-truncate rule is enforceable rather than aspirational.</b> A
    /// manifest is never paged and never trimmed: a short list is indistinguishable from a small
    /// event, and its consequence is legitimately-invited students displaying on the device as unknown
    /// cards — the exact failure this endpoint exists to prevent. So there has to be a point at which
    /// the server says "this is more than the contract covers" out loud.
    /// </para>
    ///
    /// <para>
    /// <b>It was 20,000, and it was reached on the day the first real roster arrived.</b> That figure
    /// was justified as "roughly two orders of magnitude above the largest institution-wide event
    /// anyone has described", which was true of the descriptions and false of the institution: the
    /// roster imported on 2026-08-28 is <see cref="LargestObservedRoster"/> students, and an event
    /// inviting all of them refused with <c>413</c> while students queued at the scanner. The lesson
    /// is in the justification rather than the arithmetic — a ceiling defended by what nobody has
    /// described yet is defended by an absence of evidence, and the roster grows every intake.
    /// </para>
    ///
    /// <para>
    /// <b>50,000 is deliberately more than double the roster</b>, so it survives several intakes and
    /// is not re-tripped in a term. It is still a real ceiling rather than an effective infinity: at
    /// roughly a quarter-kilobyte of JSON per attendee, a manifest at this bound is around twelve
    /// megabytes, which is the honest reason not to simply remove the check. Raising it further is a
    /// decision about response size and device memory, not about whether the event is legitimate.
    /// </para>
    ///
    /// <para>
    /// <b>Unlike <see cref="TapBatchLimits.MaxRows"/> there is nothing for the client to halve.</b> A
    /// batch that is too large is chunked and resent; a manifest that is too large cannot be asked for
    /// in halves without becoming a page, and an ETag over a page is not an ETag over a manifest. The
    /// published client instruction is therefore "stop, tell the operator, report it to us".
    /// </para>
    /// </summary>
    public const int MaxAttendees = 50_000;
}
