namespace EAMS.Application.Dtos;

/// <summary>
/// The body of <c>POST /attendance/tap/batch</c> — Technical Plan §8.2's offline queue flush, and the
/// shape frozen with the external mobile developer, published as the <c>TapBatchRequest</c> /
/// <c>TapBatchResult</c> schemas in the generated OpenAPI document (Phase 4d, D-31).
/// </summary>
/// <param name="ClientClockAt">
/// The device's own clock at the moment it sent this batch.
///
/// <para>
/// <b>It is measured, not validated.</b> Its difference from our clock is <em>pure skew</em> — unlike a
/// row's <c>tappedAt</c>, which also carries however long that row sat in the queue — so it is the one
/// value that can tell a drifted device from a slow flush. A batch is never refused because of it:
/// the clock rules that <em>do</em> refuse (D-36) are per row and apply to <c>tappedAt</c> alone. This
/// is logged, and a client that omits it simply is not measured.
/// </para>
/// </param>
/// <param name="Taps">
/// The queued taps, in whatever order the queue holds them. Each row is exactly the
/// <see cref="TapRequest"/> that <c>POST /attendance/tap</c> takes, <c>eventId</c> included.
///
/// <para>
/// <b><c>eventId</c> stays per row rather than being hoisted onto the envelope</b>, and that is a
/// decision rather than an oversight: a device that switched events while offline holds a queue
/// spanning both, and hoisting would force it to either split the flush or lie about one of them.
/// </para>
///
/// <para>
/// Null and empty are the same thing and both are a 200 with no results — "the queue had nothing to
/// send" is not a malformed request.
/// </para>
///
/// <para>
/// <b>The element type is nullable, and that annotation is load-bearing rather than pedantic.</b>
/// <c>{"taps": [null]}</c> is valid JSON, and <c>System.Text.Json</c> puts a null in the list:
/// ASP.NET's implicit-required-from-nullable-reference-types covers bound parameters and properties
/// and never covers <em>collection elements</em>, so nothing upstream rejects it. Declared
/// non-nullable, the compiler agreed the elements could not be null and the batch loop dereferenced
/// one straight into a <c>NullReferenceException</c> — a batch-level 500 that §8.2's queue retries
/// forever. Saying <c>TapRequest?</c> is what makes the null a case the code has to answer.
/// </para>
/// </param>
public record TapBatchRequest(DateTime? ClientClockAt, IReadOnlyList<TapRequest?>? Taps);

/// <summary>
/// One row of a batch response.
/// </summary>
/// <param name="Index">
/// The row's position in the request's <c>taps</c> array — <em>not</em> its position in the order the
/// server processed it, and not necessarily its position in <c>results</c> either, though today those
/// two agree. See <see cref="TapBatchResult.Results"/>.
/// </param>
/// <param name="DeviceTapId">
/// The row's idempotency key, echoed back. The second correlation handle: a client that has lost track
/// of its own array can still reconcile, which matters because the array is exactly what a crashed
/// client no longer has.
/// </param>
/// <param name="Code">
/// The frozen outcome token — the same value, from the same table, that <c>POST /attendance/tap</c>
/// returns for the identical tap. <b>Branch on this.</b>
/// </param>
/// <param name="Status">
/// The HTTP status this row <em>would</em> have received as a single tap. The transport status is
/// always 200 for a well-formed batch, so this is the field §8.2's queue reads to decide drop, stop or
/// retry — the same 2xx/4xx reasoning it already applies to a single tap's status line.
/// </param>
/// <param name="Message">
/// Prose, never parsed, and present on success rows as well as failures — the same string the single
/// endpoint puts in <c>TapResult.message</c> or in a problem body's <c>detail</c>.
/// </param>
public record TapBatchRowResult(
    int Index, string? DeviceTapId, string Code, int Status, AttendanceDto? Record, string Message);

/// <summary>
/// The body of a well-formed <c>POST /attendance/tap/batch</c>. <b>Always HTTP 200.</b>
///
/// <para>
/// <b>Not 207 Multi-Status, and the reason is about clients rather than about correctness.</b> Proxies
/// and HTTP libraries handle 207 inconsistently, and — the part that actually bites — a client that
/// sees any non-2xx transport status is liable to retry the <em>whole</em> batch. Retrying is safe
/// here, because every row is idempotent, but it is pure waste when 197 of 200 rows landed. The batch
/// status describes the batch; each row's <see cref="TapBatchRowResult.Status"/> describes that tap.
/// </para>
///
/// <para>
/// <b>A batch-level <c>4xx</c> is transport, auth or size only</b> — a malformed body,
/// <c>BatchTooLarge</c>, a missing or bad key, a revoked one. Nothing about the content of a row can
/// produce one.
/// </para>
///
/// <para>
/// <b>On a <c>5xx</c> the client retries the whole batch, and that is always safe</b> — every row is
/// keyed by <c>deviceTapId</c> and index-guarded, so a resend converges on the same rows however much
/// of the first attempt landed. The published contract words this as "a 5xx means nothing was
/// committed", which is stronger than what per-row commits provide; see
/// <c>AttendanceService.TapBatchAsync</c> for the discrepancy and why idempotency rather than
/// atomicity is the property the client actually depends on.
/// </para>
/// </summary>
/// <param name="Accepted">Rows whose <see cref="TapBatchRowResult.Status"/> is below 400.</param>
/// <param name="Rejected">
/// The rest. A rejected row is one this API will refuse identically on every retry — the client drops
/// it and, per the published guidance, stops the queue rather than skipping past it.
/// </param>
/// <param name="Results">
/// One entry per submitted tap, <b>in the request's array order</b>, so <c>results[i].index == i</c>.
///
/// <para>
/// That is a stronger guarantee than the published contract asks for, and it is free: rows are
/// <em>processed</em> in ascending <c>tappedAt</c> and then written back into their original slots. It
/// is stated because the cheap client is the one that zips <c>results</c> against <c>taps</c>
/// positionally, and a contract that merely says "correlate by index" would let a future change break
/// that client silently. Correlating by <see cref="TapBatchRowResult.Index"/> or by
/// <see cref="TapBatchRowResult.DeviceTapId"/> works regardless and is what the document tells clients
/// to do.
/// </para>
/// </param>
/// <param name="ServerTime">
/// Read once for the whole batch. A client computes its clock offset from this exactly as it does from
/// a single tap's <c>serverTime</c>.
/// </param>
public record TapBatchResult(
    int Accepted, int Rejected, DateTime ServerTime, IReadOnlyList<TapBatchRowResult> Results);
