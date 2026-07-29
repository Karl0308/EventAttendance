using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The §6.3 write surface over real HTTP.
///
/// <para>
/// The service-level suites (<see cref="EventWriteTests"/>, <see cref="EventRosterTests"/>,
/// <see cref="EventCloseFreezeTests"/>) own the behaviour; this file owns the parts that are invisible
/// from there and that a client codes against — the status code, the <c>Location</c> header, the RFC
/// 7807 body, and the JSON property names the SPA will bind. A 409 quietly becoming a 200 would break
/// a client's error handling without failing a single service-level test, which is the argument
/// <see cref="ApiContractTests"/> already makes for §6.4.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventsApiTests : IntegrationTest
{
    public EventsApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/events";

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private static object ValidEvent(
        string name = "University Convocation 2026",
        string? attendanceMode = "Single",
        int graceMinutes = 15) => new
        {
            name,
            description = "Annual convocation.",
            location = "USA Gymnasium",
            startAt = "2026-08-01T01:00:00Z",
            endAt = "2026-08-01T04:00:00Z",
            attendanceMode,
            graceMinutes,
            requireRegistration = false,
        };

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    // -------------------------------------------------------------------------------- create

    [Fact]
    public async Task A_created_event_is_201_with_a_location_header_that_resolves()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, ValidEvent());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        // The header is not decoration: it is how a client learns the id of a resource it just made.
        var followed = await client.GetAsync(response.Headers.Location);
        followed.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await followed.Content.ReadAsStringAsync());
        Assert.Equal(EventStatus.Draft, body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Annual convocation.", body.RootElement.GetProperty("description").GetString());
        Assert.False(body.RootElement.GetProperty("requireRegistration").GetBoolean());
    }

    /// <summary>
    /// §6 declares RFC 7807 for errors, and <c>TracedProblemDetailsFactory</c> exists so every body
    /// carries a <c>traceId</c> an operator can quote. Asserted here because these actions build their
    /// ProblemDetails as an action result, which is exactly the path that used to miss the stamp.
    /// </summary>
    [Fact]
    public async Task A_rejected_write_is_a_problem_details_body_carrying_a_trace_id()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, ValidEvent(name: ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("detail").GetString()));
    }

    // --------------------------------------------------------------------------- status patch

    [Fact]
    public async Task The_status_lifecycle_runs_end_to_end_over_http()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidEvent());
        var id = await CreatedIdAsync(created);

        var opened = await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Open" });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        var closed = await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        using var body = JsonDocument.Parse(await closed.Content.ReadAsStringAsync());
        Assert.Equal(EventStatus.Closed, body.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// The brief's explicit requirement: an illegal transition is a 400, not a silent write. Both
    /// halves are asserted — the code, and the column.
    /// </summary>
    [Fact]
    public async Task An_illegal_transition_is_400_and_the_status_column_does_not_move()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidEvent());
        var id = await CreatedIdAsync(created);

        // Draft cannot go straight to Closed: it recorded nothing, so closing it would mark its whole
        // audience Absent for something that never happened.
        var response = await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Closed" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var read = NewDbContext();
        Assert.Equal(EventStatus.Draft, (await read.Events.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Reopening_a_closed_event_is_400_over_http()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidEvent());
        var id = await CreatedIdAsync(created);
        await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Open" });
        await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Closed" });

        var response = await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Open" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------------------------------------------------------------- update/delete

    /// <summary>
    /// <c>PUT</c> is a full replacement, so a client has to be able to read every field it will send
    /// back. This is why <c>EventDto</c> gained <c>description</c> and <c>requireRegistration</c> —
    /// without them a UI round trip would blank the description of every event it touched.
    /// </summary>
    [Fact]
    public async Task An_event_can_be_read_edited_and_put_back_without_losing_a_field()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidEvent());
        var id = await CreatedIdAsync(created);

        var fetched = await client.GetFromJsonAsync<JsonElement>($"{Route}/{id}");

        var put = await client.PutAsJsonAsync($"{Route}/{id}", new
        {
            name = "Renamed Convocation",
            description = fetched.GetProperty("description").GetString(),
            location = fetched.GetProperty("location").GetString(),
            startAt = fetched.GetProperty("startAt").GetDateTime(),
            endAt = fetched.GetProperty("endAt").GetDateTime(),
            attendanceMode = fetched.GetProperty("attendanceMode").GetString(),
            graceMinutes = fetched.GetProperty("graceMinutes").GetInt32(),
            requireRegistration = fetched.GetProperty("requireRegistration").GetBoolean(),
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        await using var read = NewDbContext();
        var stored = await read.Events.AsNoTracking().SingleAsync();
        Assert.Equal("Renamed Convocation", stored.Name);
        Assert.Equal("Annual convocation.", stored.Description);
        Assert.Equal(15, stored.GraceMinutes);
    }

    [Fact]
    public async Task Editing_a_closed_event_is_409()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidEvent());
        var id = await CreatedIdAsync(created);
        await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Open" });
        await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Closed" });

        var response = await client.PutAsJsonAsync($"{Route}/{id}", ValidEvent(name: "Renamed"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_deleted_event_is_204_then_404_everywhere()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidEvent());
        var id = await CreatedIdAsync(created);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Route}/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{id}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{id}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Route}/{id}")).StatusCode);
    }

    // ------------------------------------------------------------------------------ audience

    /// <summary>
    /// The whole product flow over HTTP — JJ's sentence, executed: create an event, attach a section,
    /// tap, close, and read a roster whose denominator is real.
    /// </summary>
    [Fact]
    public async Task The_core_flow_runs_over_http_from_create_to_a_frozen_roster()
    {
        var schoolId = await ArrangeSchoolAsync();

        Guid groupId;
        await using (var db = NewDbContext())
        {
            var term = TestData.NewTerm(schoolId);
            db.Terms.Add(term);
            var course = TestData.NewCourse(schoolId);
            db.Courses.Add(course);
            var offering = TestData.NewOffering(term.Id, course.Id, "BSCRIM 2-A");
            db.CourseOfferings.Add(offering);

            var tapper = TestData.NewStudent(schoolId, "2023-0001", lastName: "Santos");
            var absentee = TestData.NewStudent(schoolId, "2023-0002", lastName: "Cruz");
            db.Students.AddRange(tapper, absentee);
            db.RfidCards.Add(TestData.NewCard(schoolId, tapper.Id, "04A7B8C9"));
            db.Enrollments.AddRange(
                TestData.NewEnrollment(tapper.Id, offering.Id),
                TestData.NewEnrollment(absentee.Id, offering.Id));
            await db.SaveChangesAsync();

            await ProjectionOn(db).SyncTermAsync(term.Id);

            groupId = await db.StudentGroups
                .Where(g => g.SourceEntityType == GroupSourceEntityType.Section)
                .Select(g => g.Id).SingleAsync();
        }

        // The one tap in this flow goes through a gated endpoint (D-28). Everything else here — create,
        // attach, status, roster, summary — is still open.
        var apiKey = await IssueDeviceKeyAsync(schoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var created = await client.PostAsJsonAsync(Route, ValidEvent(graceMinutes: 0));
        var id = await CreatedIdAsync(created);

        var attach = await client.PostAsJsonAsync(
            $"{Route}/{id}/attendees", new { studentGroupIds = new[] { groupId } });
        Assert.Equal(HttpStatusCode.OK, attach.StatusCode);

        using (var body = JsonDocument.Parse(await attach.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, body.RootElement.GetProperty("groupsAttached").GetInt32());
            Assert.Equal(2, body.RootElement.GetProperty("expected").GetInt32());
            Assert.Empty(body.RootElement.GetProperty("warnings").EnumerateArray());
        }

        await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Open" });

        var tap = await client.PostAsJsonAsync(
            "/api/v1/attendance/tap", new { eventId = id, cardUid = "04A7B8C9" });
        tap.EnsureSuccessStatusCode();

        await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Closed" });

        var roster = await client.GetFromJsonAsync<JsonElement>($"{Route}/{id}/roster");
        Assert.True(roster.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(2, roster.GetProperty("expected").GetInt32());
        Assert.Equal(1, roster.GetProperty("absent").GetInt32());
        Assert.Equal(0, roster.GetProperty("notRecorded").GetInt32());
        Assert.Equal(2, roster.GetProperty("entries").GetArrayLength());

        var summary = await client.GetFromJsonAsync<JsonElement>($"{Route}/{id}/summary");
        Assert.Equal(2, summary.GetProperty("expected").GetInt32());
        Assert.Equal(50, summary.GetProperty("attendanceRate").GetDouble());
    }

    [Fact]
    public async Task Attaching_an_unknown_group_is_400_and_a_locked_event_is_409()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidEvent());
        var id = await CreatedIdAsync(created);

        var unknown = await client.PostAsJsonAsync(
            $"{Route}/{id}/attendees", new { studentGroupIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        await client.PatchAsJsonAsync($"{Route}/{id}/status", new { status = "Cancelled" });

        var locked = await client.PostAsJsonAsync(
            $"{Route}/{id}/attendees", new { studentIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
    }

    [Fact]
    public async Task Detaching_is_204_and_is_safe_to_repeat()
    {
        var schoolId = await ArrangeSchoolAsync();

        Guid studentId;
        await using (var db = NewDbContext())
        {
            var student = TestData.NewStudent(schoolId);
            db.Students.Add(student);
            await db.SaveChangesAsync();
            studentId = student.Id;
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Route, ValidEvent());
        var id = await CreatedIdAsync(created);

        await client.PostAsJsonAsync(
            $"{Route}/{id}/attendees", new { studentIds = new[] { studentId } });

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"{Route}/{id}/attendees/students/{studentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"{Route}/{id}/attendees/students/{studentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"{Route}/{Guid.NewGuid()}/attendees/students/{studentId}"))
                .StatusCode);

        var summary = await client.GetFromJsonAsync<JsonElement>($"{Route}/{id}/summary");
        Assert.Equal(0, summary.GetProperty("expected").GetInt32());
    }

    [Fact]
    public async Task An_unknown_event_has_no_roster_over_http()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"{Route}/{Guid.NewGuid()}/roster")).StatusCode);
    }
}
