using EAMS.Application.Dtos;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure;

/// <summary>
/// The one place a <see cref="PagedResult{T}"/> is built — <b>count and page against a single
/// queryable, for all nine admin list reads.</b>
///
/// <para>
/// <b>It was written once and then copy-pasted four times, which is the reason it now lives here.</b>
/// Phase 3b-3 landed this shape as a <c>private static</c> helper inside
/// <c>AcademicReferenceService</c>, covering its five reads, and open-coded the identical five lines
/// in <c>StudentService</c>, <c>EventService</c>, <c>AttendanceService</c> and
/// <c>StudentGroupService</c>. The helper's own documentation claimed the structure was what kept the
/// count and the page from describing different filters — a claim that covered five of nine reads,
/// and left the guard off the four where the next drift would land. Lifting it is removing four
/// duplications of an abstraction that already existed, not inventing one.
/// </para>
///
/// <para>
/// <b>What the shape does and does not guarantee.</b> Both overloads take exactly one
/// <c>filtered</c> queryable and use it for the <c>COUNT</c> and as the source of the page, so there
/// is no second predicate to write and get wrong — the ordinary way to produce a total that
/// disagrees with its rows. It is not airtight: nothing in the type system stops a callback adding a
/// <c>.Where(...)</c> of its own, which would narrow the page and not the total. No caller does, and
/// the fix if one ever wants to is to narrow <c>filtered</c> instead.
/// </para>
///
/// <para>
/// Ordering is deferred into the callback rather than applied by the caller, so it never reaches the
/// <c>COUNT</c>: SQL Server rejects <c>ORDER BY</c> in a subquery without <c>TOP</c>/<c>OFFSET</c>.
/// <b>Every callback must end its ordering on the paged row's own key</b> — an <c>OFFSET/FETCH</c>
/// over a non-total order lets consecutive pages repeat one row and skip another, silently, and
/// <c>PaginationTests.Every_paged_list_query_orders_by_a_unique_column</c> is what enforces it.
/// </para>
/// </summary>
internal static class PagedQuery
{
    /// <summary>
    /// For reads that <b>project to their DTO in SQL</b> — the academic reference lists and the group
    /// list, none of which materializes an entity.
    ///
    /// <para>
    /// The projection sits in the callback so it applies to the page only. Two of these lists carry a
    /// correlated subquery count per row (<c>EnrolledCount</c>, <c>MemberCount</c>); running those for
    /// rows that are being counted and then discarded is work bought for nothing.
    /// </para>
    /// </summary>
    /// <param name="filtered">The filter, and nothing else. Counted directly; also the page's source.</param>
    /// <param name="orderAndProject">
    /// Applies the total order and the DTO projection. Must not filter — see the class remarks.
    /// </param>
    internal static async Task<PagedResult<TDto>> ToPageAsync<TEntity, TDto>(
        this IQueryable<TEntity> filtered,
        Func<IQueryable<TEntity>, IQueryable<TDto>> orderAndProject,
        PageRequest page,
        CancellationToken ct)
    {
        var total = await filtered.CountAsync(ct);

        var items = await orderAndProject(filtered)
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(ct);

        return new PagedResult<TDto>(items, page.Page, page.PageSize, total);
    }

    /// <summary>
    /// For reads that <b>materialize the entity and map in memory</b> — students, events and
    /// attendance, whose <c>ToDto</c> walks loaded navigations (a student's cards, an attendance row's
    /// student) that no expression tree can translate.
    ///
    /// <para>
    /// <paramref name="order"/> is where any <c>Include</c> belongs, so the join lands on the page
    /// query and never on the <c>COUNT</c> — EF would drop it from a <c>COUNT</c> anyway, and keeping
    /// it off the shared source says so rather than relying on that.
    /// </para>
    /// </summary>
    /// <param name="filtered">The filter, and nothing else. Counted directly; also the page's source.</param>
    /// <param name="order">
    /// Applies the total order, plus any <c>Include</c> the mapping needs. Must not filter — see the
    /// class remarks.
    /// </param>
    /// <param name="map">Entity to DTO, run in memory over the page's rows.</param>
    internal static async Task<PagedResult<TDto>> ToPageAsync<TEntity, TDto>(
        this IQueryable<TEntity> filtered,
        Func<IQueryable<TEntity>, IQueryable<TEntity>> order,
        Func<TEntity, TDto> map,
        PageRequest page,
        CancellationToken ct)
    {
        var total = await filtered.CountAsync(ct);

        var rows = await order(filtered)
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(ct);

        return new PagedResult<TDto>(
            rows.Select(map).ToList(), page.Page, page.PageSize, total);
    }
}
