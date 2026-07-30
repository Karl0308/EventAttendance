namespace EAMS.Application.Dtos;

/// <summary>
/// The paging limits every admin list read obeys, named once.
///
/// <para>
/// <b>In <c>EAMS.Application</c> rather than <c>EAMS.Domain</c>, and the split is the same one
/// <c>TapBatchLimits</c> sits on the other side of.</b> A tap window is a rule about attendance — it
/// decides whether an observation is admissible, so it is domain. A page size decides nothing about
/// the data; it bounds how much of a read crosses the wire. It belongs with the DTOs it bounds, in the
/// one assembly both <c>EAMS.Api</c> (which binds the query string and publishes the schema) and
/// <c>EAMS.Infrastructure</c> (which turns it into <c>Skip</c>/<c>Take</c>) already reference.
/// </para>
/// </summary>
public static class Paging
{
    /// <summary>
    /// <b>Pages are 1-based.</b> <c>?page=1</c> is the first page.
    ///
    /// <para>
    /// 0-based would make the default value of an unsupplied <c>int</c> — zero — a legal page number,
    /// so "the caller asked for the first page" and "the caller asked for nothing" would be the same
    /// request and a clamping bug in either direction would be invisible. 1-based makes every
    /// out-of-range value strictly less than the first legal one, which is what lets
    /// <see cref="PageRequest.From"/> treat "absent", "zero" and "negative" as one case with one
    /// answer. It is also what a human reading a URL expects, and these are admin-grid reads.
    /// </para>
    /// </summary>
    public const int FirstPage = 1;

    /// <summary>
    /// What <c>?pageSize=</c> means when it is absent. Sized for an admin grid: large enough that the
    /// common school's event list or college list arrives in one request, small enough that the
    /// default never ships a roster.
    /// </summary>
    public const int DefaultPageSize = 50;

    /// <summary>
    /// The ceiling. A larger <c>?pageSize=</c> is clamped to this rather than refused — see
    /// <see cref="PageRequest.From"/>.
    ///
    /// <para>
    /// It exists because without it the page size <em>is</em> the unpaged read: <c>?pageSize=100000</c>
    /// reinstates every problem paging was added to fix, and does it on a request that looks like it is
    /// paging. That is worse than no paging at all, because the endpoint's documentation would say it
    /// is bounded.
    /// </para>
    /// </summary>
    public const int MaxPageSize = 200;

    /// <summary>The smallest page a caller may ask for. Zero is not a page; it is a count query.</summary>
    public const int MinPageSize = 1;
}

/// <summary>
/// A validated page request — <b>the only shape a service accepts</b>, so the clamping happens once
/// and cannot differ between the nine endpoints that page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Constructed through <see cref="From"/>, never through the constructor, and the reason is the
/// same one <c>TapResponse.For</c> records.</b> A <c>PageRequest(0, 100000)</c> is expressible and
/// would page nothing and cap nothing; making the factory the only sanctioned path means the invariant
/// "page ≥ 1 and 1 ≤ pageSize ≤ MaxPageSize" is established in one function that one test covers,
/// rather than asserted at nine call sites.
/// </para>
/// </remarks>
public record PageRequest
{
    private PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    /// <summary>1-based. Always at least <see cref="Paging.FirstPage"/>.</summary>
    public int Page { get; }

    /// <summary>Always between <see cref="Paging.MinPageSize"/> and <see cref="Paging.MaxPageSize"/>.</summary>
    public int PageSize { get; }

    /// <summary>
    /// Rows to skip. Computed here so no caller writes <c>(page - 1) * pageSize</c> again.
    ///
    /// <para>
    /// <b>The multiply is widened to <c>long</c> and saturated, because <see cref="Page"/> is clamped
    /// from below and not from above.</b> <c>?page=42949674</c> is a legal request; at the default
    /// page size the <c>int</c> product wraps to <b>-2,147,483,646</b>, SQL Server refuses a negative
    /// <c>OFFSET</c> (Msg 10743), and every one of the nine paged reads answers 500 — unauthenticated,
    /// since the admin surface is open under ADR-001 D-6. Saturating at
    /// <see cref="int.MaxValue"/> degrades it to the case this class already documents and tests: a
    /// page past the end, which is an empty page with a truthful <c>Total</c> and
    /// <c>HasMore: false</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The same hazard on the same input was already found and fixed once, one property over.</b>
    /// <see cref="PagedResult{T}.HasMore"/> widens its own <c>Page * PageSize</c> to <c>long</c> and
    /// its comment says "nothing clamps the page from above" — and this site was left <c>int</c>.
    /// Diagnosing an overflow and fixing one of its two arithmetic sites is the shape of the miss;
    /// both are pinned by tests now.
    /// </para>
    ///
    /// <para>
    /// Clamping <see cref="Page"/> itself was the alternative and is worse: it would answer a page
    /// number the caller did not ask for, which is the thing <see cref="From"/> deliberately refuses
    /// to do at the top end so a client walking pages can tell when it has run off the end.
    /// </para>
    /// </summary>
    public int Skip =>
        (int)Math.Min((long)(Page - Paging.FirstPage) * PageSize, int.MaxValue);

    /// <summary>
    /// Clamps a caller's query-string values into the legal range.
    ///
    /// <para>
    /// <b>Clamped, not rejected, and that is a decision rather than leniency.</b> Rejecting would mean
    /// a 400 and a new failure branch on nine read endpoints that have none — a claim that is only
    /// true because <see cref="Skip"/> saturates rather than overflowing, which it did not on first
    /// writing, and which is why that property carries the longer comment of the two — and it
    /// would buy a caller nothing, because the clamp is not silent: <see cref="PagedResult{T}.Page"/>
    /// and <see cref="PagedResult{T}.PageSize"/> echo the values actually used, so a client that asked
    /// for 100 000 rows and received 200 can see that it did. A refusal is the right answer when
    /// honouring the request would fabricate something (the reason <c>tappedAt</c> is never clamped);
    /// here honouring it is merely impossible, and answering with the largest legal page is what the
    /// caller wanted a prefix of anyway.
    /// </para>
    ///
    /// <para>
    /// <b>A page past the end is <em>not</em> clamped to the last page.</b> It returns an empty page,
    /// and <see cref="PagedResult{T}.Total"/> says how far past the end it was. Clamping it would
    /// answer a different question than the one asked — a client walking pages until it sees an empty
    /// one would never stop, because page 900 would keep returning page 12's rows.
    /// </para>
    /// </summary>
    /// <param name="page">
    /// 1-based. Null, zero and negative are all "the caller did not name a page" and all give
    /// <see cref="Paging.FirstPage"/>.
    /// </param>
    /// <param name="pageSize">
    /// Null and anything below <see cref="Paging.MinPageSize"/> give
    /// <see cref="Paging.DefaultPageSize"/>; anything above <see cref="Paging.MaxPageSize"/> gives the
    /// maximum.
    /// </param>
    public static PageRequest From(int? page, int? pageSize) => new(
        page is { } p && p > Paging.FirstPage ? p : Paging.FirstPage,
        pageSize switch
        {
            null => Paging.DefaultPageSize,
            < Paging.MinPageSize => Paging.DefaultPageSize,
            > Paging.MaxPageSize => Paging.MaxPageSize,
            var size => size.Value,
        });

    /// <summary>The request an internal caller makes when it wants the first page at the default size.</summary>
    public static PageRequest Default => From(null, null);
}

/// <summary>
/// One page of an admin list read, plus what the caller needs to ask for the next one.
///
/// <para>
/// <b>Every §6.2/§6.3 admin list returns this; <c>GET /attendance/live/{eventId}</c> deliberately does
/// not.</b> That endpoint pages by cursor (<c>since</c>/<c>cursor</c>/<c>hasMore</c>) because it is a
/// polling delta rather than a list — a client asks "what changed after this point", not "give me rows
/// 40 to 60" — and its shape is frozen published contract under D-29/D-30/D-42. Offset paging over a
/// live feed also drops rows: an insert below the cursor shifts every subsequent row up by one and the
/// next page skips it. The two mechanisms coexisting is correct, not an inconsistency to tidy.
/// </para>
/// </summary>
/// <typeparam name="T">The row type, unchanged from what the unpaged read used to return.</typeparam>
/// <param name="Items">
/// This page's rows, in the endpoint's documented order. Empty is an ordinary answer — for an empty
/// filter and for a page past the end alike.
/// </param>
/// <param name="Page">
/// The 1-based page actually served, which is <em>not</em> necessarily the one requested: an
/// out-of-range <c>?page=</c> is clamped and this is where a client sees that it was.
/// </param>
/// <param name="PageSize">
/// The page size actually applied, after the <see cref="Paging.MaxPageSize"/> clamp. The field that
/// makes clamping discoverable rather than silent.
/// </param>
/// <param name="Total">
/// Rows matching the filter across every page, counted in the database against the same filter that
/// produced <paramref name="Items"/>. This is what a grid needs to render a page count; without it a
/// caller can only discover the end by walking into it.
/// </param>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    /// <summary>
    /// Whether another page exists after this one.
    ///
    /// <para>
    /// <b>Derived here rather than left to the client, because the client gets it wrong.</b>
    /// <c>page * pageSize &lt; total</c> is right and <c>items.Count == pageSize</c> is the version
    /// people write — which reports a further page whenever the last page happens to be exactly full,
    /// so a paging loop makes one extra request every time the total is a multiple of the page size.
    /// Computing it once, on the side that already holds the total, is a byte on the wire against a
    /// class of off-by-one nobody finds in review.
    /// </para>
    /// </summary>
    public bool HasMore => (long)Page * PageSize < Total;
}
