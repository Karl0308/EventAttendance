using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// <b>"What is this person classified as", as one query, one ordering and one projection — used by every
/// read in the system that answers it.</b>
///
/// <para>
/// <b>Its one caller today is <c>GET /students/{id}/classifications</c>.</b> Student classification was
/// withdrawn from the product surface on 2026-10-23 (ADR-009, D-76): <see cref="StudentDto"/> no longer
/// carries it, so the students grid, <c>GET /students/{id}</c>, <c>GET /students/by-card/{uid}</c> and
/// the student write responses stopped calling here. The tables and this dormant route stay. Were the
/// field ever to return to a student read, it should come back through this class, so the same person's
/// categories cannot be ordered or filtered differently in two places.
/// </para>
///
/// <para>
/// <b>Nothing here filters on <see cref="Classification.IsActive"/>, deliberately.</b> Retiring a
/// category withdraws it from pickers and leaves every existing assignment standing, so a person can
/// hold a withdrawn one indefinitely. Each row carries its own <c>isActive</c> so a client can render it
/// as held-but-not-offered; hiding it is how an edit form would blank it on the next save.
/// </para>
/// </summary>
internal static class StudentClassificationReads
{
    /// <summary>
    /// Every classification held by any of <paramref name="studentIds"/>, keyed by student.
    ///
    /// <para>
    /// <b>One query for any number of students — the N+1 this repository's own lessons are about.</b>
    /// Shaped for a page read (one round trip whether it holds one student or two hundred); since
    /// 2026-10-23 its only caller is <see cref="ForStudentAsync"/>, after the students list stopped
    /// returning classifications.
    /// </para>
    ///
    /// <para>
    /// <b>A student who holds nothing is absent from the dictionary rather than present with an empty
    /// list</b>, and callers use <see cref="ForStudentAsync"/>'s empty default. Thirty-four people in
    /// the sampled roster are uncategorised; that is an ordinary state, not a missing row to guard
    /// against.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<StudentClassificationDto>>>
        ForStudentsAsync(
            EamsDbContext db, IReadOnlyCollection<Guid> studentIds, CancellationToken ct)
    {
        if (studentIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<StudentClassificationDto>>();

        var rows = await Held(db, studentIds).ToListAsync(ct);

        return rows
            .GroupBy(r => r.StudentId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<StudentClassificationDto>)g.Select(r => r.Held).ToList());
    }

    /// <summary>
    /// One person's classifications, in the same order and shape as the page read's.
    /// </summary>
    internal static async Task<IReadOnlyList<StudentClassificationDto>> ForStudentAsync(
        EamsDbContext db, Guid studentId, CancellationToken ct)
    {
        var held = await ForStudentsAsync(db, [studentId], ct);

        return held.TryGetValue(studentId, out var rows) ? rows : [];
    }

    /// <summary>
    /// The single query. <b>Ordered by axis and then by display name</b> so a person's two categories
    /// come back in the same order on every request — a grid whose cell reshuffles between page loads
    /// reads as data changing when nothing has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One id is a parameterized equality rather than a one-element <c>IN</c>, and that is not
    /// micro-optimisation.</b> <see cref="DependencyInjection.SqlServerCompatibilityLevel"/> pins EF to
    /// the SQL Server 2012 dialect, under which a parameterized <c>.Contains(collection)</c> is
    /// <em>inlined as literals</em> rather than sent as <c>OPENJSON</c>. A one-element <c>IN</c> would
    /// therefore put the student's GUID into the SQL text and give every student in the school their own
    /// cached plan — on <c>GET /students/by-card/{uid}</c>, which is the capture path.
    /// </para>
    ///
    /// <para>
    /// The many-id branch pays exactly that cost, knowingly: a page of up to
    /// <see cref="Paging.MaxPageSize"/> ids becomes that many literals, so an admin grid page compiles
    /// its own plan. That is the documented, accepted trade the compatibility pin already makes at
    /// eighteen other query sites, and it buys one round trip per page instead of one per row.
    /// </para>
    /// </remarks>
    private static IQueryable<(Guid StudentId, StudentClassificationDto Held)> Held(
        EamsDbContext db, IReadOnlyCollection<Guid> studentIds)
    {
        var rows = db.StudentClassifications.AsNoTracking();

        if (studentIds.Count == 1)
        {
            var only = studentIds.First();
            rows = rows.Where(sc => sc.StudentId == only);
        }
        else
        {
            rows = rows.Where(sc => studentIds.Contains(sc.StudentId));
        }

        return rows
            .OrderBy(sc => sc.Axis)
            .ThenBy(sc => sc.Classification!.Name)
            .Select(sc => new ValueTuple<Guid, StudentClassificationDto>(
                sc.StudentId,
                new StudentClassificationDto(
                    sc.ClassificationId,
                    sc.Classification!.Name,
                    sc.Axis,
                    sc.Classification.IsActive,
                    sc.CreatedAt)));
    }
}
