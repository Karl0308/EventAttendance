using EAMS.Application.Dtos;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The paging contract, in the one place it is decided.
///
/// <para>
/// Nine endpoints page and every one of them builds its <see cref="PageRequest"/> through
/// <see cref="PageRequest.From"/>, so these assertions cover all nine. That is the reason the clamp
/// lives in a factory rather than in each controller: a per-endpoint clamp is nine chances to write
/// <c>Math.Min</c> where <c>Math.Max</c> belonged, and eight of them would be found by nobody.
/// </para>
/// </summary>
public class PageRequestTests
{
    // ------------------------------------------------------------------------------ the page number

    /// <summary>
    /// <b>Absent, zero and negative are one case with one answer</b>, which is the property 1-based
    /// paging buys. Under 0-based numbering <c>page=0</c> would be a legal request and a caller that
    /// sent nothing would be indistinguishable from one that asked for the first page — so a clamp
    /// that had quietly stopped working would look identical to one that was working.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void An_out_of_range_page_clamps_to_the_first_page(int? page) =>
        Assert.Equal(Paging.FirstPage, PageRequest.From(page, null).Page);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1_000)]
    public void A_page_at_or_past_the_first_is_taken_as_asked(int page) =>
        Assert.Equal(page, PageRequest.From(page, null).Page);

    /// <summary>
    /// <b>A page past the end is not clamped to the last page</b>, which is the half of the decision
    /// that is easy to get backwards. There is no total in scope here to clamp against, and that is
    /// deliberate: answering page 900 with page 12's rows would make a client walking pages until it
    /// saw an empty one never stop.
    /// </summary>
    [Fact]
    public void A_page_far_past_the_end_is_preserved_rather_than_pulled_back()
    {
        var request = PageRequest.From(900, 20);

        Assert.Equal(900, request.Page);
        Assert.Equal(899 * 20, request.Skip);
    }

    // -------------------------------------------------------------------------------- the page size

    [Fact]
    public void An_absent_page_size_is_the_default() =>
        Assert.Equal(Paging.DefaultPageSize, PageRequest.From(null, null).PageSize);

    /// <summary>
    /// Below the floor takes the <em>default</em> and not the floor. <c>pageSize=0</c> is a caller
    /// that meant nothing by it — a one-row page is a strange thing to ask for by accident — so the
    /// answer that serves them is the one they would have got by omitting the parameter.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_page_size_below_the_floor_is_the_default(int pageSize) =>
        Assert.Equal(Paging.DefaultPageSize, PageRequest.From(1, pageSize).PageSize);

    /// <summary>
    /// <b>The cap is the whole point of having a page size at all.</b> Without it
    /// <c>?pageSize=100000</c> is the unpaged read this phase removed, arriving through an endpoint
    /// whose documentation says it is bounded — which is worse than not paging, because the claim is
    /// now false rather than absent.
    /// </summary>
    [Theory]
    [InlineData(Paging.MaxPageSize + 1)]
    [InlineData(100_000)]
    [InlineData(int.MaxValue)]
    public void A_page_size_above_the_cap_is_the_cap(int pageSize) =>
        Assert.Equal(Paging.MaxPageSize, PageRequest.From(1, pageSize).PageSize);

    [Theory]
    [InlineData(Paging.MinPageSize)]
    [InlineData(20)]
    [InlineData(Paging.MaxPageSize)]
    public void A_page_size_inside_the_range_is_taken_as_asked(int pageSize) =>
        Assert.Equal(pageSize, PageRequest.From(1, pageSize).PageSize);

    // -------------------------------------------------------------------------------------- skip

    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(2, 20, 20)]
    [InlineData(3, 50, 100)]
    public void Skip_is_the_offset_of_the_requested_page(int page, int size, int expected) =>
        Assert.Equal(expected, PageRequest.From(page, size).Skip);

    /// <summary>
    /// The first page skips nothing. Stated on its own because an off-by-one here does not throw and
    /// does not look wrong — it silently drops the first row of every list in the system.
    /// </summary>
    [Fact]
    public void The_first_page_skips_nothing() => Assert.Equal(0, PageRequest.Default.Skip);

    /// <summary>
    /// <b><c>Skip</c> saturates instead of overflowing, and this is the assertion that stops a 500 on
    /// all nine paged endpoints.</b>
    ///
    /// <para>
    /// <see cref="PageRequest.From"/> clamps the page from below only — deliberately, so a client
    /// walking pages can run off the end and see that it has — so a caller is free to send
    /// <c>?page=42949674</c>. Multiplied by the default page size in <c>int</c> arithmetic that wraps
    /// to <b>-2,147,483,646</b>; SQL Server refuses a negative <c>OFFSET</c> with Msg 10743 and the
    /// pipeline turns it into a 500, on an endpoint that is open under ADR-001 D-6.
    /// </para>
    ///
    /// <para>
    /// The values below are the two real ones: the smallest page that overflows at the default size,
    /// and the smallest that overflows at the cap. <c>int.MaxValue</c> is the degenerate case.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(42_949_674, Paging.DefaultPageSize)]
    [InlineData(10_737_420, Paging.MaxPageSize)]
    [InlineData(int.MaxValue, Paging.MaxPageSize)]
    [InlineData(int.MaxValue, Paging.MinPageSize)]
    public void An_absurd_page_number_saturates_rather_than_overflowing_into_a_negative_offset(
        int page, int pageSize)
    {
        var skip = PageRequest.From(page, pageSize).Skip;

        Assert.True(
            skip >= 0,
            $"page={page} pageSize={pageSize} produced Skip={skip}. A negative value reaches SQL " +
            "Server as a negative OFFSET (Msg 10743) and becomes an unauthenticated 500 on every " +
            "paged endpoint. The multiply must be widened to long and saturated at int.MaxValue.");
    }

    /// <summary>
    /// Saturation lands on the largest legal offset rather than on some arbitrary ceiling, so the
    /// answer is the documented "page past the end" — empty rows, truthful total — and not a page of
    /// data the caller did not ask for.
    /// </summary>
    [Fact]
    public void The_saturated_offset_is_the_largest_legal_one() =>
        Assert.Equal(int.MaxValue, PageRequest.From(int.MaxValue, Paging.MaxPageSize).Skip);

    [Fact]
    public void The_default_request_is_the_first_page_at_the_default_size()
    {
        Assert.Equal(Paging.FirstPage, PageRequest.Default.Page);
        Assert.Equal(Paging.DefaultPageSize, PageRequest.Default.PageSize);
    }
}

/// <summary>
/// <see cref="PagedResult{T}.HasMore"/> — one derived boolean, and the one place a client would have
/// got it wrong.
/// </summary>
public class PagedResultTests
{
    private static PagedResult<int> Result(int page, int pageSize, int total) =>
        new([], page, pageSize, total);

    [Fact]
    public void A_partial_first_page_is_the_whole_result() =>
        Assert.False(Result(page: 1, pageSize: 20, total: 7).HasMore);

    /// <summary>
    /// <b>The case the naive check gets wrong.</b> Twenty rows in total, twenty per page: the page is
    /// exactly full, so <c>items.Count == pageSize</c> — the version people write — reports another
    /// page, and a paging loop makes one extra request every time the total is a multiple of the page
    /// size. Comparing against the total is what makes it right, and holding the total is why this
    /// belongs on the server.
    /// </summary>
    [Fact]
    public void An_exactly_full_last_page_does_not_claim_another_one() =>
        Assert.False(Result(page: 1, pageSize: 20, total: 20).HasMore);

    [Fact]
    public void A_full_page_with_rows_behind_it_claims_another_one() =>
        Assert.True(Result(page: 1, pageSize: 20, total: 21).HasMore);

    [Fact]
    public void The_last_page_of_several_does_not_claim_another_one()
    {
        Assert.True(Result(page: 2, pageSize: 20, total: 45).HasMore);
        Assert.False(Result(page: 3, pageSize: 20, total: 45).HasMore);
    }

    /// <summary>
    /// A page past the end claims nothing further. It is reachable — an out-of-range page is served
    /// rather than clamped back — so a client walking pages has to be able to stop on it.
    /// </summary>
    [Fact]
    public void A_page_past_the_end_claims_nothing_further() =>
        Assert.False(Result(page: 900, pageSize: 20, total: 45).HasMore);

    [Fact]
    public void An_empty_result_claims_nothing() =>
        Assert.False(Result(page: 1, pageSize: 20, total: 0).HasMore);

    /// <summary>
    /// <c>Page * PageSize</c> is widened to a <c>long</c> before the comparison. An
    /// <c>int</c> multiply overflows at a page number a caller is free to send — nothing clamps the
    /// page from above — and a negative product would answer <c>HasMore: true</c> for a result that
    /// has no more, on a page that is empty.
    /// </summary>
    [Fact]
    public void An_absurd_page_number_does_not_overflow_into_claiming_more() =>
        Assert.False(Result(page: int.MaxValue, pageSize: Paging.MaxPageSize, total: 45).HasMore);
}
