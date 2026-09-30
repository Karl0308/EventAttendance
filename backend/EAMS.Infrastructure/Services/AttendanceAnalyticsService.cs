using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// Attendance analytics — see <see cref="IAttendanceAnalyticsService"/>. Pulls the scoped attendance rows
/// (a small projection, not whole entities) and aggregates them in memory, because the metrics that matter
/// here — distinct-event and distinct-student counts alongside per-status tallies, all in one pass — are the
/// exact shape EF's <c>GROUP BY</c> translation cannot express together (a grouped distinct-count does not
/// translate). Every read is tenant-scoped by the query filters on the event and student tables.
/// </summary>
internal sealed class AttendanceAnalyticsService : IAttendanceAnalyticsService
{
    private readonly EamsDbContext _db;

    public AttendanceAnalyticsService(EamsDbContext db) => _db = db;

    private sealed record Row(
        string? Course, string? YearLevel, string? Section,
        Guid EventId, string EventName, DateTime EventDate,
        Guid StudentId, string Status);

    public async Task<AttendanceAnalyticsResult> GetReportAsync(
        AttendanceAnalyticsQuery query, CancellationToken ct = default)
    {
        var groupBy = query.GroupBy;
        if (string.IsNullOrWhiteSpace(groupBy)) groupBy = AttendanceGroupBy.Course;
        if (!AttendanceGroupBy.TryNormalize(groupBy, out var group))
            return new AttendanceAnalyticsResult(
                AttendanceAnalyticsOutcome.ValidationFailed,
                $"groupBy must be one of {string.Join(", ", AttendanceGroupBy.All)}.", null);

        if (query.From is { } f && query.To is { } t && f > t)
            return new AttendanceAnalyticsResult(
                AttendanceAnalyticsOutcome.ValidationFailed,
                "The 'from' date is after the 'to' date.", null);

        var q = _db.AttendanceRecords.AsNoTracking()
            .Where(a => !a.Event!.IsDeleted && !a.Student!.IsDeleted);

        if (!query.IncludeCancelled)
            q = q.Where(a => a.Event!.Status != EventStatus.Cancelled);

        if (query.From is { } from) q = q.Where(a => a.Event!.StartAt >= from);
        if (query.To is { } to) q = q.Where(a => a.Event!.StartAt <= to);

        // Drill-down filters — each narrows to one group value from the level above.
        if (!string.IsNullOrWhiteSpace(query.Course)) q = q.Where(a => a.Student!.Course == query.Course);
        if (!string.IsNullOrWhiteSpace(query.YearLevel)) q = q.Where(a => a.Student!.YearLevel == query.YearLevel);
        if (!string.IsNullOrWhiteSpace(query.Section)) q = q.Where(a => a.Student!.Section == query.Section);

        var rows = await q.Select(a => new Row(
                a.Student!.Course, a.Student!.YearLevel, a.Student!.Section,
                a.EventId, a.Event!.Name, a.Event!.StartAt,
                a.StudentId, a.Status))
            .ToListAsync(ct);

        var report = new AttendanceAnalyticsReportDto(
            group, Aggregate(rows, group), Totals(rows));

        return new AttendanceAnalyticsResult(AttendanceAnalyticsOutcome.Ok, "OK", report);
    }

    private const string NoValue = "(none)";

    private static IReadOnlyList<AttendanceAnalyticsRowDto> Aggregate(List<Row> rows, string group)
    {
        if (group == AttendanceGroupBy.Event)
        {
            return rows
                .GroupBy(r => r.EventId)
                .Select(g =>
                {
                    var first = g.First();
                    return BuildRow(first.EventName, g.ToList(), first.EventId, first.EventDate);
                })
                .OrderByDescending(r => r.EventDate)
                .ThenBy(r => r.Key)
                .ToList();
        }

        Func<Row, string> key = group switch
        {
            AttendanceGroupBy.YearLevel => r => r.YearLevel ?? NoValue,
            AttendanceGroupBy.Section => r => r.Section ?? NoValue,
            _ => r => r.Course ?? NoValue,
        };

        return rows
            .GroupBy(key)
            .Select(g => BuildRow(g.Key, g.ToList(), null, null))
            .OrderBy(r => r.Key)
            .ToList();
    }

    private static AttendanceAnalyticsRowDto Totals(List<Row> rows) =>
        BuildRow("All", rows, null, null);

    private static AttendanceAnalyticsRowDto BuildRow(
        string key, IReadOnlyCollection<Row> rows, Guid? eventId, DateTime? eventDate)
    {
        var present = rows.Count(r => r.Status == AttendanceStatus.Present);
        var late = rows.Count(r => r.Status == AttendanceStatus.Late);
        var absent = rows.Count(r => r.Status == AttendanceStatus.Absent);
        var excused = rows.Count(r => r.Status == AttendanceStatus.Excused);
        var total = rows.Count;
        var rate = total == 0 ? 0d : Math.Round((present + late) * 100d / total, 1);

        return new AttendanceAnalyticsRowDto(
            key,
            eventId,
            eventDate,
            rows.Select(r => r.EventId).Distinct().Count(),
            rows.Select(r => r.StudentId).Distinct().Count(),
            present, late, absent, excused, total, rate);
    }
}
