using System.Net;
using System.Text.Json;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 phase B1c-2: the admin reads with a personnel row on the event (D-99, D-101). Until this phase
/// <c>GET /attendance</c> answered 500 the moment such a row existed, and live left it out.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceAdminPersonnelReadTests : IntegrationTest
{
    public AttendanceAdminPersonnelReadTests(SqlServerFixture sql) : base(sql) { }

    private async Task<PersonnelEventWorld> ArrangeWithRowsAsync()
    {
        await using var db = NewDbContext();
        var world = await PersonnelEventWorld.ArrangeAsync(db);

        var checkInAt = DateTime.UtcNow.AddMinutes(-1);

        db.AttendanceRecords.AddRange(
            new AttendanceRecord
            {
                SchoolId = world.SchoolId, EventId = world.EventId, StudentId = world.StudentId,
                CheckInAt = checkInAt, Status = AttendanceStatus.Present, CaptureMethod = CaptureMethod.Manual,
            },
            new AttendanceRecord
            {
                SchoolId = world.SchoolId, EventId = world.EventId, PersonnelId = world.PersonnelId,
                CheckInAt = checkInAt, Status = AttendanceStatus.Late, CaptureMethod = CaptureMethod.Manual,
            });

        await db.SaveChangesAsync();
        return world;
    }

    private static async Task<JsonElement> JsonFromAsync(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {route}: {(int)response.StatusCode} {text}");

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonElement RowOf(IEnumerable<JsonElement> rows, string personType) =>
        Assert.Single(rows, r => r.GetProperty("personType").GetString() == personType);

    private static void AssertIsThePersonnelRow(JsonElement row, PersonnelEventWorld world)
    {
        Assert.Equal(world.PersonnelId, row.GetProperty("personnelId").GetGuid());
        Assert.Equal(PersonnelEventWorld.PersonnelNumber, row.GetProperty("personnelNumber").GetString());
        Assert.Equal(PersonnelEventWorld.PersonnelName, row.GetProperty("personnelName").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("studentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("studentName").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("studentNumber").ValueKind);
    }

    private static void AssertIsTheStudentRow(JsonElement row, PersonnelEventWorld world)
    {
        Assert.Equal(world.StudentId, row.GetProperty("studentId").GetGuid());
        Assert.Equal("Maria Reyes Santos", row.GetProperty("studentName").GetString());
        Assert.Equal("2023-0001", row.GetProperty("studentNumber").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("personnelId").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("personnelNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("personnelName").ValueKind);
    }

    /// <summary>The defect itself: this was a 500, from <c>StudentIdOf</c>, once a personnel row existed.</summary>
    [Fact]
    public async Task List_answers_200_with_personnel_present_and_personType_on_every_row()
    {
        var world = await ArrangeWithRowsAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var body = await JsonFromAsync(client, $"/api/v1/attendance?eventId={world.EventId}");

        var rows = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        AssertIsThePersonnelRow(RowOf(rows, AttendeePersonType.Personnel), world);
        AssertIsTheStudentRow(RowOf(rows, AttendeePersonType.Student), world);
    }

    /// <summary>A student filter names a student, so it never produces a personnel row.</summary>
    [Fact]
    public async Task List_filtered_by_student_excludes_personnel_rows()
    {
        var world = await ArrangeWithRowsAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var body = await JsonFromAsync(
            client, $"/api/v1/attendance?eventId={world.EventId}&studentId={world.StudentId}");

        Assert.Equal(
            AttendeePersonType.Student,
            Assert.Single(body.GetProperty("items").EnumerateArray()).GetProperty("personType").GetString());
    }

    /// <summary>The live snapshot: B1a's <c>StudentId != null</c> before the <c>Take</c> left this row out.</summary>
    [Fact]
    public async Task Live_snapshot_carries_the_personnel_row_with_personType()
    {
        var world = await ArrangeWithRowsAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var body = await JsonFromAsync(client, $"/api/v1/attendance/live/{world.EventId}");

        var rows = body.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        AssertIsThePersonnelRow(RowOf(rows, AttendeePersonType.Personnel), world);
        AssertIsTheStudentRow(RowOf(rows, AttendeePersonType.Student), world);
    }

    /// <summary>The delta: a personnel row written after a cursor arrives on the next poll.</summary>
    [Fact]
    public async Task Live_delta_carries_a_personnel_row_written_after_the_cursor()
    {
        await using var arrange = NewDbContext();
        var world = await PersonnelEventWorld.ArrangeAsync(arrange);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var snapshot = await JsonFromAsync(client, $"/api/v1/attendance/live/{world.EventId}");
        Assert.Empty(snapshot.GetProperty("entries").EnumerateArray());

        arrange.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = world.SchoolId, EventId = world.EventId, PersonnelId = world.PersonnelId,
            CheckInAt = DateTime.UtcNow, Status = AttendanceStatus.Present, CaptureMethod = CaptureMethod.Manual,
        });
        await arrange.SaveChangesAsync();

        var cursor = Uri.EscapeDataString(snapshot.GetProperty("cursor").GetString()!);
        var delta = await JsonFromAsync(client, $"/api/v1/attendance/live/{world.EventId}?since={cursor}");

        AssertIsThePersonnelRow(Assert.Single(delta.GetProperty("changes").EnumerateArray()), world);
    }

    /// <summary>
    /// The sweep behind the defect: every admin read of an event that holds a personnel row answers 200.
    /// What each returns for that row is B2's and B3's; here, none of them may fall over. A route that
    /// assumed a non-null <c>StudentId</c> is a 500 for the whole event the moment one member of staff taps.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/events/{event}/summary")]
    [InlineData("/api/v1/events/{event}/roster")]
    [InlineData("/api/v1/events/{event}/attendees")]
    [InlineData("/api/v1/events/{event}/scans")]
    [InlineData("/api/v1/events/{event}/attendance-codes")]
    [InlineData("/api/v1/reports/event/{event}/summary")]
    [InlineData("/api/v1/reports/event/{event}/detail")]
    [InlineData("/api/v1/reports/event/{event}/attendees")]
    [InlineData("/api/v1/reports/event/{event}/export.csv")]
    [InlineData("/api/v1/reports/attendance-analytics?groupBy=Event")]
    [InlineData("/api/v1/clearance/students/{student}")]
    public async Task Every_admin_read_of_the_event_survives_a_personnel_row(string route)
    {
        var world = await ArrangeWithRowsAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync(route
            .Replace("{event}", world.EventId.ToString(), StringComparison.Ordinal)
            .Replace("{student}", world.StudentId.ToString(), StringComparison.Ordinal));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {route}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    // ===================================================== #706 F2: personType filter and personnel search

    private async Task<List<JsonElement>> ListRowsAsync(PersonnelEventWorld world, string query)
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var body = await JsonFromAsync(client, $"/api/v1/attendance?eventId={world.EventId}{query}");
        var rows = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(rows.Count, body.GetProperty("total").GetInt32());
        return rows;
    }

    [Theory]
    [InlineData("Student")]
    [InlineData("student")]
    public async Task List_personType_Student_returns_only_student_rows(string value)
    {
        var world = await ArrangeWithRowsAsync();

        var rows = await ListRowsAsync(world, $"&personType={value}");

        AssertIsTheStudentRow(Assert.Single(rows), world);
    }

    [Theory]
    [InlineData("Personnel")]
    [InlineData(" PERSONNEL ")]
    public async Task List_personType_Personnel_returns_only_personnel_rows(string value)
    {
        var world = await ArrangeWithRowsAsync();

        var rows = await ListRowsAsync(world, $"&personType={Uri.EscapeDataString(value)}");

        AssertIsThePersonnelRow(Assert.Single(rows), world);
    }

    [Theory]
    [InlineData("")]
    [InlineData("&personType=")]
    public async Task List_without_personType_returns_both_populations(string query)
    {
        var world = await ArrangeWithRowsAsync();

        var rows = await ListRowsAsync(world, query);

        Assert.Equal(2, rows.Count);
        AssertIsThePersonnelRow(RowOf(rows, AttendeePersonType.Personnel), world);
        AssertIsTheStudentRow(RowOf(rows, AttendeePersonType.Student), world);
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("Students")]
    public async Task List_refuses_an_unknown_personType_with_400(string value)
    {
        var world = await ArrangeWithRowsAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync($"/api/v1/attendance?eventId={world.EventId}&personType={value}");
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode} {text}");
        using var document = JsonDocument.Parse(text);
        Assert.Equal("UnknownPersonType", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task List_number_fragment_matches_the_personnel_number_on_a_personnel_row()
    {
        var world = await ArrangeWithRowsAsync();

        var rows = await ListRowsAsync(world, "&studentNumber=EMP-00");

        AssertIsThePersonnelRow(Assert.Single(rows), world);
    }

    [Fact]
    public async Task List_number_fragment_still_matches_the_student_number_on_a_student_row()
    {
        var world = await ArrangeWithRowsAsync();

        var rows = await ListRowsAsync(world, "&studentNumber=2023-00");

        AssertIsTheStudentRow(Assert.Single(rows), world);
    }

    [Theory]
    [InlineData("rizal")]
    [InlineData("Jose P Rizal")]
    [InlineData("Jose Rizal")]
    public async Task List_name_fragment_matches_the_personnel_name_on_a_personnel_row(string fragment)
    {
        var world = await ArrangeWithRowsAsync();

        var rows = await ListRowsAsync(world, $"&studentName={Uri.EscapeDataString(fragment)}");

        AssertIsThePersonnelRow(Assert.Single(rows), world);
    }

    [Fact]
    public async Task List_personType_ands_with_the_number_filter()
    {
        var world = await ArrangeWithRowsAsync();

        var rows = await ListRowsAsync(world, "&personType=Student&studentNumber=EMP-00");

        Assert.Empty(rows);
    }
}
