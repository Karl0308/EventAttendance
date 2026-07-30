namespace EAMS.Domain;

/// <summary>
/// The two limits <c>POST /attendance/tap/batch</c> refuses on (Phase 4d, D-31), named once so the
/// endpoint, the problem body that echoes the limit, and the tests that probe the boundary cannot
/// disagree about what it is.
/// </summary>
public static class TapBatchLimits
{
    /// <summary>
    /// Rows per request. Published as 200 in the generated OpenAPI document and in
    /// <c>docs/api/attendance-contract-handoff.md</c>, and the
    /// number is echoed in every <c>BatchTooLarge</c> body so a client can chunk to it without a
    /// release — which is the whole reason it is a published number rather than a private constant.
    ///
    /// <para>
    /// <b>Why a cap exists at all, given every row is idempotent.</b> The endpoint holds one request open
    /// for the duration of the whole batch, and each row costs at least two round trips to SQL Server.
    /// Uncapped, a client flushing a week-long queue would hold a connection, a <c>DbContext</c> and a
    /// rate-limiter slot for minutes, and a timeout anywhere in that window makes the client retry the
    /// entire thing. Bounded work per request is what keeps "retry the whole batch" a cheap instruction.
    /// </para>
    /// </summary>
    public const int MaxRows = 200;

    /// <summary>
    /// The request body ceiling, enforced at the endpoint before any of it is read.
    ///
    /// <para>
    /// <b>It is not the same guard as <see cref="MaxRows"/> and neither one replaces the other.</b> The
    /// row cap is checked after the body has been buffered and deserialized — by which point a
    /// multi-megabyte payload has already been read into memory, whatever the row count turns out to
    /// be. A <c>cardUid</c> is <c>nvarchar(128)</c> and a <c>deviceTapId</c> <c>nvarchar(100)</c>, so
    /// 200 honest rows are a few tens of kilobytes; a quarter of a megabyte is generous headroom for
    /// that and still refuses the payload whose only purpose is to be large.
    /// </para>
    /// </summary>
    public const int MaxRequestBytes = 256 * 1024;
}
