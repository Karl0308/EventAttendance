using EAMS.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Data;

/// <summary>
/// The one place that decides which school a newly created row belongs to in the pre-auth build.
///
/// <para>
/// Extracted from <c>EventService</c> when the §6.2 student write surface needed the same answer.
/// Duplicating it would have been six lines and a slow divergence, for the reason
/// <see cref="SqlServerErrors"/> records about itself: two copies of a tenant rule are two rules, and
/// the day one of them learns that a second school exists while the other does not, rows start being
/// filed under a school at random with nothing in the log to say so.
/// </para>
/// </summary>
internal static class SchoolResolution
{
    /// <summary>
    /// The pinned tenant when there is one. Otherwise the only school, if there is exactly one — the
    /// same rule <c>DependencyInjection.PinDevelopmentSchoolAsync</c> already applies at startup, so
    /// this agrees with what the log said rather than inventing a second answer. With zero or several
    /// and nothing pinned there is no honest choice, and guessing would file the row under a school at
    /// random; the caller gets <c>null</c> and turns it into a refusal.
    ///
    /// <para>
    /// Phase 6 makes the fallback dead code: the tenant arrives in the claims and an unauthenticated
    /// request never reaches here.
    /// </para>
    /// </summary>
    public static async Task<Guid?> ResolveSchoolIdAsync(
        this EamsDbContext db, ISchoolContext school, CancellationToken ct)
    {
        if (school.CurrentSchoolId is { } pinned) return pinned;

        // Take(2) rather than a Count: the question is "is there exactly one", and two rows is enough
        // to answer it on a table that could hold many.
        var candidates = await db.Schools.AsNoTracking()
            .OrderBy(s => s.Code).Select(s => s.Id).Take(2).ToListAsync(ct);

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
