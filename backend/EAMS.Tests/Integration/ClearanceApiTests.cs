using System.Net;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The Clearance Checker over real HTTP (Clearance-Checker-Module.docx): a student's eligible events with
/// attendance mapped to Attended / Missed / Excused / Late, no clearance status computed, and an audit
/// row per check.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ClearanceApiTests : IntegrationTest
{
    public ClearanceApiTests(SqlServerFixture sql) : base(sql) { }

    private sealed record World(Guid SchoolId, Guid StudentId);

    /// <summary>
    /// One student with four events: Attended (Present record), Late (Late record), Missed (expected,
    /// no record), and a Cancelled event they were expected at, which must not appear.
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var student = TestData.NewStudent(school.Id, "2023-5001");
        db.Students.Add(student);

        Event Ev(string name, string status, DateTime start) =>
            new()
            {
                SchoolId = school.Id, Name = name, Status = status,
                StartAt = start, EndAt = start.AddHours(2), AttendanceMode = AttendanceMode.Single,
            };

        var attended = Ev("Orientation", EventStatus.Closed, new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc));
        var late = Ev("Sports Fest", EventStatus.Closed, new DateTime(2026, 9, 15, 1, 0, 0, DateTimeKind.Utc));
        var missed = Ev("Seminar", EventStatus.Closed, new DateTime(2026, 10, 5, 1, 0, 0, DateTimeKind.Utc));
        var cancelled = Ev("Cancelled Rally", EventStatus.Cancelled, new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc));
        db.Events.AddRange(attended, late, missed, cancelled);

        // Expected at all four (individual attach = a frozen roster row).
        foreach (var e in new[] { attended, late, missed, cancelled })
            db.EventGroups.Add(new EventGroup { EventId = e.Id, StudentId = student.Id });

        AttendanceRecord Rec(Event e, string status, DateTime checkIn) =>
            new()
            {
                SchoolId = school.Id, EventId = e.Id, StudentId = student.Id,
                CheckInAt = checkIn, Status = status, CaptureMethod = CaptureMethod.Import,
            };

        db.AttendanceRecords.Add(Rec(attended, AttendanceStatus.Present, attended.StartAt.AddMinutes(1)));
        db.AttendanceRecords.Add(Rec(late, AttendanceStatus.Late, late.StartAt.AddMinutes(30)));
        // 'missed' has no record; 'cancelled' has none either.

        await db.SaveChangesAsync();
        return new World(school.Id, student.Id);
    }

    [Fact]
    public async Task A_clearance_report_lists_eligible_events_with_mapped_attendance()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync($"/api/v1/clearance/students/{world.StudentId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2023-5001", body.RootElement.GetProperty("student").GetProperty("studentNumber").GetString());

        var events = body.RootElement.GetProperty("events").EnumerateArray()
            .ToDictionary(e => e.GetProperty("eventName").GetString()!, e => e.GetProperty("attendance").GetString());

        Assert.Equal("Attended", events["Orientation"]);
        Assert.Equal("Late", events["Sports Fest"]);
        Assert.Equal("Missed", events["Seminar"]);
        // The cancelled event was not held — it must not appear as "missed".
        Assert.DoesNotContain("Cancelled Rally", events.Keys);
    }

    [Fact]
    public async Task A_clearance_check_writes_an_audit_row()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await client.GetAsync($"/api/v1/clearance/students/{world.StudentId}");

        await using var db = NewDbContext();
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == ClearanceService.AuditAction && a.EntityId == world.StudentId)
            .SingleOrDefaultAsync();

        Assert.NotNull(audit);
        Assert.Equal(nameof(Student), audit!.EntityType);
    }

    [Fact]
    public async Task A_date_range_filters_the_events()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        // Only September onward → Sports Fest and Seminar, not the August Orientation.
        var response = await client.GetAsync(
            $"/api/v1/clearance/students/{world.StudentId}?dateFrom=2026-09-01T00:00:00Z");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var names = body.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("eventName").GetString()).ToList();

        Assert.DoesNotContain("Orientation", names);
        Assert.Contains("Sports Fest", names);
    }

    [Fact]
    public async Task The_report_exports_as_csv()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync($"/api/v1/clearance/students/{world.StudentId}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("2023-5001", csv);
        Assert.Contains("Orientation", csv);
        Assert.Contains("Attended", csv);
    }

    [Fact]
    public async Task An_unknown_student_is_a_404()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync($"/api/v1/clearance/students/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
