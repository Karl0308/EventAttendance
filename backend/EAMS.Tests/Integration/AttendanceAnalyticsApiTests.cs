using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The RPT-01 attendance-analytics report over real HTTP — grouping by course and by event, the drill-down
/// filter, the exclude-cancelled default, the validation 400s, and the CSV export. Counts are asserted
/// against a hand-built arrangement so the aggregation is pinned, not merely exercised.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceAnalyticsApiTests : IntegrationTest
{
    public AttendanceAnalyticsApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/reports/attendance-analytics";

    /// <summary>
    /// One school; an Open event E1 with three attendances (BSIT Present, BSIT Late, BSCS Absent) and a
    /// Cancelled event E2 with one attendance (a BSIT student Present).
    /// </summary>
    private async Task ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var open = TestData.NewEvent(school.Id, status: EventStatus.Open);
        var cancelled = TestData.NewEvent(school.Id, status: EventStatus.Cancelled);
        db.Events.Add(open);
        db.Events.Add(cancelled);

        var s1 = TestData.NewStudent(school.Id, "2023-0001", lastName: "Alpha", course: "BSIT");
        var s2 = TestData.NewStudent(school.Id, "2023-0002", lastName: "Bravo", course: "BSIT");
        var s3 = TestData.NewStudent(school.Id, "2023-0003", lastName: "Charlie", course: "BSCS");
        db.Students.AddRange(s1, s2, s3);

        void Rec(Event ev, Student s, string status) => db.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = school.Id,
            EventId = ev.Id,
            StudentId = s.Id,
            Status = status,
            CheckInAt = status == AttendanceStatus.Absent ? null : TestData.Now,
        });

        Rec(open, s1, AttendanceStatus.Present);
        Rec(open, s2, AttendanceStatus.Late);
        Rec(open, s3, AttendanceStatus.Absent);
        Rec(cancelled, s1, AttendanceStatus.Present);

        await db.SaveChangesAsync();
    }

    private static JsonElement FindRow(JsonDocument doc, string key) =>
        doc.RootElement.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Grouping_by_course_excludes_cancelled_by_default_and_tallies_statuses()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}?groupBy=Course")).Content.ReadAsStringAsync());

        var bsit = FindRow(doc, "BSIT");
        Assert.Equal(1, bsit.GetProperty("present").GetInt32());
        Assert.Equal(1, bsit.GetProperty("late").GetInt32());
        Assert.Equal(0, bsit.GetProperty("absent").GetInt32());
        Assert.Equal(2, bsit.GetProperty("total").GetInt32());
        Assert.Equal(2, bsit.GetProperty("people").GetInt32());
        Assert.Equal(1, bsit.GetProperty("totalEvents").GetInt32());
        Assert.Equal(100.0, bsit.GetProperty("attendanceRate").GetDouble());

        var bscs = FindRow(doc, "BSCS");
        Assert.Equal(1, bscs.GetProperty("absent").GetInt32());
        Assert.Equal(0.0, bscs.GetProperty("attendanceRate").GetDouble());

        // Totals over E1 only: 3 rows, 3 people, 1 event, (Present+Late)/Total = 2/3.
        var totals = doc.RootElement.GetProperty("totals");
        Assert.Equal(3, totals.GetProperty("total").GetInt32());
        Assert.Equal(1, totals.GetProperty("totalEvents").GetInt32());
        Assert.Equal(66.7, totals.GetProperty("attendanceRate").GetDouble());
    }

    [Fact]
    public async Task Including_cancelled_brings_the_cancelled_event_into_the_tally()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}?groupBy=Course&includeCancelled=true")).Content
                .ReadAsStringAsync());

        var bsit = FindRow(doc, "BSIT");
        Assert.Equal(2, bsit.GetProperty("present").GetInt32()); // E1 + E2
        Assert.Equal(2, bsit.GetProperty("totalEvents").GetInt32());
        Assert.Equal(3, bsit.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Grouping_by_event_carries_the_event_id_for_the_drill_down_link()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}?groupBy=Event")).Content.ReadAsStringAsync());

        var rows = doc.RootElement.GetProperty("rows");
        Assert.Equal(1, rows.GetArrayLength()); // only the Open event
        var row = rows[0];
        Assert.False(string.IsNullOrEmpty(row.GetProperty("eventId").GetString()));
        Assert.False(string.IsNullOrEmpty(row.GetProperty("eventDate").GetString()));
        Assert.Equal(3, row.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_course_filter_narrows_the_report()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}?groupBy=Event&course=BSCS")).Content.ReadAsStringAsync());

        // Only the BSCS student's Absent row in E1 remains.
        var totals = doc.RootElement.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("total").GetInt32());
        Assert.Equal(1, totals.GetProperty("absent").GetInt32());
        Assert.Equal(0, totals.GetProperty("present").GetInt32());
    }

    [Theory]
    [InlineData("?groupBy=Nonsense")]
    [InlineData("?from=2030-01-01T00:00:00Z&to=2020-01-01T00:00:00Z")]
    public async Task A_bad_query_is_a_400(string query)
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.GetAsync($"{Route}{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(AttendanceAnalyticsOutcome.ValidationFailed),
            body.RootElement.GetProperty(ReportsController.ErrorCodeProperty).GetString());
    }

    [Fact]
    public async Task The_csv_export_returns_a_downloadable_report()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.GetAsync($"{Route}/export.csv?groupBy=Course");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("BSIT", text);
        Assert.Contains("TOTAL", text);
    }

    [Fact]
    public async Task An_empty_school_reports_no_rows_and_zero_totals()
    {
        await using (var db = NewDbContext())
        {
            db.Schools.Add(TestData.NewSchool());
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}?groupBy=Course")).Content.ReadAsStringAsync());

        Assert.Equal(0, doc.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("totals").GetProperty("total").GetInt32());
        Assert.Equal(0.0, doc.RootElement.GetProperty("totals").GetProperty("attendanceRate").GetDouble());
    }
}
