using System.Text.Json;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// <b>The single implementation of "which people does a stored audience definition resolve to".</b>
///
/// <para>
/// ADR-003 D-19 records that a second copy of a denominator-shaped query drifts <em>silently</em> — every
/// copy stays plausible. The criteria→person-set derivation is exactly that kind of query, so it lives in
/// one place and both callers use it: <see cref="AudienceDefinitionService"/> (the <c>GET
/// /event-audiences/{id}/attendees</c> resolve) and <see cref="EventService"/> (the event denominator and
/// the terminal freeze, ADR-007 D-69/D-70). Neither re-derives it.
/// </para>
///
/// <para>
/// Static over a passed <see cref="EamsDbContext"/> rather than a service dependency, so the queries it
/// returns compose on the caller's own context — which is what lets <see cref="EventService"/>
/// <c>UNION</c> a definition's students into <c>ExpectedStudentIds</c> as one SQL statement.
/// </para>
/// </summary>
internal static class AudienceResolution
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses a definition's stored <c>CriteriaJson</c>. An empty or malformed string resolves to empty
    /// criteria rather than throwing — a definition row that cannot be read must not take down the one
    /// endpoint reading it, exactly as <see cref="AudienceDefinitionService"/> treated it before this moved.
    /// </summary>
    public static AudienceCriteriaDto ParseCriteria(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AudienceCriteriaDto();
        try
        {
            return JsonSerializer.Deserialize<AudienceCriteriaDto>(json, Json) ?? new AudienceCriteriaDto();
        }
        catch (JsonException)
        {
            return new AudienceCriteriaDto();
        }
    }

    /// <summary>
    /// The student and personnel queries a type + criteria select. A null side means that side contributes
    /// nobody. Criteria lists are materialized to <c>List</c> locals so EF's collection <c>Contains</c>
    /// translation (inlined as literals on the SQL 2012 dialect) has something to bind.
    ///
    /// <para>
    /// <paramref name="includeDeleted"/> is the only behavioural knob: the live denominator and the
    /// resolve read exclude soft-deleted people (<c>false</c>), while the terminal freeze snapshot resolves
    /// with them included (<c>true</c>) so the write and the frozen read are provably one set — ADR-003
    /// D-15, generalised to the definition source by ADR-007.
    /// </para>
    /// </summary>
    public static (IQueryable<Student>? Students, IQueryable<Personnel>? Personnel) BuildQueries(
        EamsDbContext db, string type, AudienceCriteriaDto c, bool includeDeleted)
    {
        IQueryable<Student> students = db.Students.AsNoTracking();
        IQueryable<Personnel> personnel = db.Personnel.AsNoTracking();
        if (!includeDeleted)
        {
            students = students.Where(s => !s.IsDeleted);
            personnel = personnel.Where(p => !p.IsDeleted);
        }

        List<string> L(IReadOnlyList<string>? v) => v is null ? [] : [.. v];
        List<Guid> G(IReadOnlyList<Guid>? v) => v is null ? [] : [.. v];

        // Organization criteria are matched trim + case-insensitively (QA #543, Option A): a person stored
        // as "CICT " (trailing space) or "cict" (different case) resolves into a definition whose criterion
        // is "CICT". The criterion keys are folded in memory via OrganizationText.MatchKey; the stored side
        // is folded in SQL below with Organization.Trim().ToUpper() (translates to LTRIM(RTRIM(UPPER(..))),
        // so the match holds regardless of the database collation — not merely by SQL Server's default
        // case-/trailing-space-insensitive collation, which also misses leading spaces.
        List<string> OrgKeys(IReadOnlyList<string>? v) =>
            v is null ? [] : [.. v.Select(OrganizationText.MatchKey).OfType<string>().Distinct()];

        switch (type)
        {
            case AudienceType.UniversityWide:
                AudienceScope.TryNormalize(c.Scope, out var scope);
                return (scope == AudienceScope.Employees ? null : students,
                        scope == AudienceScope.Students ? null : personnel);

            case AudienceType.Department:
            {
                var d = L(c.Departments);
                return (students.Where(s => s.Course != null && d.Contains(s.Course)),
                        personnel.Where(p => p.Department != null && d.Contains(p.Department)));
            }

            case AudienceType.Program:
            {
                var pr = L(c.Programs);
                return (students.Where(s => s.Course != null && pr.Contains(s.Course)), null);
            }

            case AudienceType.YearLevel:
            {
                var y = L(c.YearLevels);
                return (students.Where(s => s.YearLevel != null && y.Contains(s.YearLevel)), null);
            }

            case AudienceType.Section:
            {
                var se = L(c.Sections);
                return (students.Where(s => s.Section != null && se.Contains(s.Section)), null);
            }

            case AudienceType.EmployeeClassification:
            {
                var cl = L(c.Classifications);
                return (null, personnel.Where(p => p.Classification != null && cl.Contains(p.Classification)));
            }

            case AudienceType.Organization:
            {
                var o = OrgKeys(c.Organizations);
                return (null, personnel.Where(p => p.Organization != null && o.Contains(p.Organization.Trim().ToUpper())));
            }

            case AudienceType.SpecificIndividuals:
            {
                var sids = G(c.StudentIds);
                var pids = G(c.PersonnelIds);
                return (sids.Count > 0 ? students.Where(s => sids.Contains(s.Id)) : null,
                        pids.Count > 0 ? personnel.Where(p => pids.Contains(p.Id)) : null);
            }

            case AudienceType.Custom:
            {
                var sApplied = false;
                var sq = students;
                if (L(c.Departments) is { Count: > 0 } cd) { sq = sq.Where(s => s.Course != null && cd.Contains(s.Course)); sApplied = true; }
                if (L(c.Programs) is { Count: > 0 } cp) { sq = sq.Where(s => s.Course != null && cp.Contains(s.Course)); sApplied = true; }
                if (L(c.YearLevels) is { Count: > 0 } cy) { sq = sq.Where(s => s.YearLevel != null && cy.Contains(s.YearLevel)); sApplied = true; }
                if (L(c.Sections) is { Count: > 0 } cs) { sq = sq.Where(s => s.Section != null && cs.Contains(s.Section)); sApplied = true; }
                if (G(c.StudentIds) is { Count: > 0 } csi) { sq = sq.Where(s => csi.Contains(s.Id)); sApplied = true; }

                var pApplied = false;
                var pq = personnel;
                if (L(c.Departments) is { Count: > 0 } pd) { pq = pq.Where(p => p.Department != null && pd.Contains(p.Department)); pApplied = true; }
                if (L(c.Classifications) is { Count: > 0 } pc) { pq = pq.Where(p => p.Classification != null && pc.Contains(p.Classification)); pApplied = true; }
                if (OrgKeys(c.Organizations) is { Count: > 0 } po) { pq = pq.Where(p => p.Organization != null && po.Contains(p.Organization.Trim().ToUpper())); pApplied = true; }
                if (G(c.PersonnelIds) is { Count: > 0 } cpi) { pq = pq.Where(p => cpi.Contains(p.Id)); pApplied = true; }

                return (sApplied ? sq : null, pApplied ? pq : null);
            }

            default:
                return (null, null);
        }
    }
}
