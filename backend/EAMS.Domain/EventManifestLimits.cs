namespace EAMS.Domain;

/// <summary>
/// The one limit <c>GET /events/{id}/manifest</c> refuses on (D-46), named once so the endpoint, the
/// problem body that echoes it, and the tests that probe the boundary cannot disagree about what it is.
/// </summary>
public static class EventManifestLimits
{
    /// <summary>
    /// Attendees per manifest. Over this the endpoint is a loud <c>413 ManifestTooLarge</c>.
    ///
    /// <para>
    /// <b>Unreachable today, and the number exists so the never-truncate rule is enforceable rather
    /// than aspirational.</b> A manifest is never paged and never trimmed: a short list is
    /// indistinguishable from a small event, and its consequence is legitimately-invited students
    /// displaying on the device as unknown cards — the exact failure this endpoint exists to prevent.
    /// So there has to be a point at which the server says "this is more than the contract covers"
    /// out loud, and 20,000 is roughly two orders of magnitude above the largest institution-wide
    /// event anyone has described.
    /// </para>
    ///
    /// <para>
    /// <b>Unlike <see cref="TapBatchLimits.MaxRows"/> there is nothing for the client to halve.</b> A
    /// batch that is too large is chunked and resent; a manifest that is too large cannot be asked for
    /// in halves without becoming a page, and an ETag over a page is not an ETag over a manifest. The
    /// published client instruction is therefore "stop, tell the operator, report it to us".
    /// </para>
    /// </summary>
    public const int MaxAttendees = 20_000;
}
