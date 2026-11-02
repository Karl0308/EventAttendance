using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EAMS.Api.Reports;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 phase B3 against real SQL Server. D-101's event-level reads carry personnel: the roster, the
/// event report and its CSV, each with content asserted (<c>Every_admin_read_of_the_event_survives_a_personnel_row</c>
/// proves only the absence of a 500). Its student-only features behave as if personnel rows were absent.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelReportReadTests : IntegrationTest
{
    public PersonnelReportReadTests(SqlServerFixture sql) : base(sql) { }

    private static readonly DateTime In = TestData.Now;

    private async Task<PersonnelEventWorld> ArrangeAsync(
        string eventStatus = EventStatus.Open, string mode = AttendanceMode.Single)
    {
        await using var db = NewDbContext();
        var world = await PersonnelEventWorld.ArrangeAsync(db, eventStatus);

        var ev = await db.Events.SingleAsync(e => e.Id == world.EventId);
        ev.AttendanceMode = mode;
        await db.SaveChangesAsync();
        return world;
    }

    private async Task<Guid> AddPersonnelAsync(Guid schoolId, string number, string lastName = "Rizal")
    {
        await using var db = NewDbContext();
        var person = TestData.NewPersonnel(schoolId, number, lastName: lastName);
        db.Personnel.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    private async Task<Guid> AddStudentAsync(PersonnelEventWorld world, string number, bool attached)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(world.SchoolId, number);
        db.Students.Add(student);
        if (attached) db.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentId = student.Id });
        await db.SaveChangesAsync();
        return student.Id;
    }

    private async Task AddRowAsync(
        Guid schoolId, Guid eventId, string status,
        Guid? studentId = null, Guid? personnelId = null, DateTime? checkIn = null, DateTime? checkOut = null)
    {
        await using var db = NewDbContext();
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = schoolId, EventId = eventId, StudentId = studentId, PersonnelId = personnelId,
            CheckInAt = checkIn, CheckOutAt = checkOut, Status = status, CaptureMethod = CaptureMethod.Manual,
        });
        await db.SaveChangesAsync();
    }

    private async Task PointDefinitionAtAsync(PersonnelEventWorld world, params Guid[] personnelIds)
    {
        await using var db = NewDbContext();
        var definition = await db.AudienceDefinitions.SingleAsync(d => d.Id == world.DefinitionId);
        definition.AudienceType = AudienceType.SpecificIndividuals;
        definition.CriteriaJson = JsonSerializer.Serialize(
            new AudienceCriteriaDto(PersonnelIds: personnelIds),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.SaveChangesAsync();
    }

    private async Task CloseAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        Assert.Equal(EventWriteOutcome.Saved,
            (await EventsOn(db).ChangeStatusAsync(eventId, EventStatus.Closed)).Outcome);
    }

    private static async Task<string> BodyAsync(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {route}: {(int)response.StatusCode} {text}");
        return text;
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string route, object? body)
    {
        var response = body is null
            ? await client.PostAsync(route, null)
            : await client.PostAsJsonAsync(route, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {route}: {(int)response.StatusCode} {text}");

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<List<EventReportAttendeeDto>> AttendeesAsync(HttpClient client, Guid eventId)
    {
        var page = await client.GetFromJsonAsync<PagedResult<EventReportAttendeeDto>>(
            $"/api/v1/reports/event/{eventId}/attendees");
        Assert.NotNull(page);
        Assert.Equal(page.Items.Count, page.Total);
        return [.. page.Items];
    }

    // ------------------------------------------------------------------------------------- the roster

    /// <summary>
    /// The digest's named test. Each figure is both a chosen number and what the entries add up to, so a
    /// population dropped from the entries, or a header counted apart from them, goes red.
    /// </summary>
    [Fact]
    public async Task Roster_header_is_what_its_entries_add_up_to_on_a_mixed_event()
    {
        var world = await ArrangeAsync();
        var notRecordedStudent = await AddStudentAsync(world, "2023-0002", attached: true);
        var walkInStudent = await AddStudentAsync(world, "2023-0003", attached: false);

        var late = world.PersonnelId;
        var absent = await AddPersonnelAsync(world.SchoolId, "EMP-0002");
        var excused = await AddPersonnelAsync(world.SchoolId, "EMP-0003");
        var notRecorded = await AddPersonnelAsync(world.SchoolId, "EMP-0004");
        var walkIn = await AddPersonnelAsync(world.SchoolId, "EMP-0005");
        await PointDefinitionAtAsync(world, late, absent, excused, notRecorded);

        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, world.StudentId, checkIn: In);
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Late, walkInStudent, checkIn: In);
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Late, personnelId: late, checkIn: In);
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Absent, personnelId: absent);
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Excused, personnelId: excused);
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, personnelId: walkIn, checkIn: In);

        await using var db = NewDbContext();
        var roster = (await EventsOn(db).GetRosterAsync(world.EventId))!;
        var summary = (await EventsOn(db).GetSummaryAsync(world.EventId))!;
        var e = roster.Entries;

        Assert.Equal(8, e.Count);
        Assert.Equal((6, 2, 4), (roster.Expected, roster.ExpectedStudents, roster.ExpectedPersonnel));
        Assert.Equal((2, 2, 1, 1), (roster.Present, roster.Late, roster.Absent, roster.Excused));
        Assert.Equal((2, 2), (roster.NotRecorded, roster.Unexpected));

        Assert.Equal(e.Count(x => x.IsExpected), roster.Expected);
        Assert.Equal(e.Count(x => x.Status == AttendanceStatus.Present), roster.Present);
        Assert.Equal(e.Count(x => x.Status == AttendanceStatus.Late), roster.Late);
        Assert.Equal(e.Count(x => x.Status == AttendanceStatus.Absent), roster.Absent);
        Assert.Equal(e.Count(x => x.Status == AttendanceStatus.Excused), roster.Excused);
        Assert.Equal(e.Count(x => x.IsExpected && x.Status is null), roster.NotRecorded);
        Assert.Equal(e.Count(x => !x.IsExpected), roster.Unexpected);

        Assert.Equal(
            (summary.Expected, summary.Present, summary.Late, summary.Absent, summary.Excused, summary.Unexpected),
            (roster.Expected, roster.Present, roster.Late, roster.Absent, roster.Excused, roster.Unexpected));

        var line = Assert.Single(e, x => x.PersonnelId == late);
        Assert.Equal(AttendeePersonType.Personnel, line.PersonType);
        Assert.Equal(PersonnelEventWorld.PersonnelNumber, line.PersonnelNumber);
        Assert.Equal(PersonnelEventWorld.PersonnelName, line.FullName);
        Assert.Equal((null, null, null), (line.StudentId, line.StudentNumber, line.Section));
        Assert.Equal((AttendanceStatus.Late, In), (line.Status, line.CheckInAt!.Value));
        Assert.True(line.IsExpected);

        Assert.False(Assert.Single(e, x => x.PersonnelId == walkIn).IsExpected);
        Assert.Null(Assert.Single(e, x => x.PersonnelId == notRecorded).Status);

        var student = Assert.Single(e, x => x.StudentId == notRecordedStudent);
        Assert.Equal(AttendeePersonType.Student, student.PersonType);
        Assert.Equal((null, null), (student.PersonnelId, student.PersonnelNumber));
    }

    /// <summary>
    /// The roster's <c>recorded</c> set counts any personnel row, an occurrence row included, as the
    /// summary's <c>RecordedPersonnelIds</c> does: an uninvited member whose only row has an occurrence is
    /// listed as unexpected, so the header's Unexpected matches the summary's. The line's status stays
    /// empty because the record projection reads the event-level row only.
    /// </summary>
    [Fact]
    public async Task Roster_counts_a_personnel_row_with_an_occurrence_as_recorded_on_a_mixed_event()
    {
        var world = await ArrangeAsync();
        var walkIn = await AddPersonnelAsync(world.SchoolId, "EMP-0002", lastName: "Abad");
        await PointDefinitionAtAsync(world, world.PersonnelId);
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, world.StudentId, checkIn: In);

        await using (var db = NewDbContext())
        {
            var occurrence = new EventSchedule
            {
                EventId = world.EventId, OccurrenceStartAt = In, OccurrenceEndAt = In.AddHours(3),
            };
            db.EventSchedules.Add(occurrence);
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId, EventId = world.EventId, PersonnelId = walkIn, OccurrenceId = occurrence.Id,
                CheckInAt = In, Status = AttendanceStatus.Present, CaptureMethod = CaptureMethod.Manual,
            });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var roster = (await EventsOn(read).GetRosterAsync(world.EventId))!;
        var summary = (await EventsOn(read).GetSummaryAsync(world.EventId))!;

        var line = Assert.Single(roster.Entries, e => e.PersonnelId == walkIn);
        Assert.False(line.IsExpected);
        Assert.Null(line.Status);

        // The world's own personnel member is expected and has no row: the one NotRecorded.
        Assert.Equal((2, 1, 1), (roster.Expected, roster.NotRecorded, roster.Unexpected));
        Assert.Equal(roster.Entries.Count(e => e.IsExpected && e.Status is null), roster.NotRecorded);
        Assert.Equal((summary.Expected, summary.Unexpected), (roster.Expected, roster.Unexpected));
    }

    // -------------------------------------------------------------------------------- the event report

    /// <summary>Headline numbers, the time-out totals, the paged list and the CSV describe one population.</summary>
    [Fact]
    public async Task Report_figures_and_lists_agree_on_a_mixed_event()
    {
        var world = await ArrangeAsync(mode: AttendanceMode.TimeInOut);
        var abad = await AddPersonnelAsync(world.SchoolId, "EMP-0002", lastName: "Abad");
        var absent = await AddPersonnelAsync(world.SchoolId, "EMP-0003", lastName: "Zamora");

        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, world.StudentId,
            checkIn: In, checkOut: In.AddHours(1));
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Late, personnelId: world.PersonnelId,
            checkIn: In);
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, personnelId: abad,
            checkIn: In, checkOut: In.AddMinutes(30));
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Absent, personnelId: absent);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var detail = (await client.GetFromJsonAsync<EventDetailReportDto>(
            $"/api/v1/reports/event/{world.EventId}/detail"))!;
        var s = detail.Summary;
        Assert.Equal((4, 1, 3), (s.Expected, s.ExpectedStudents, s.ExpectedPersonnel));
        Assert.Equal((3, 2, 1, 1), (s.Attended, s.Present, s.Late, s.Absent));
        Assert.Equal(75, s.AttendanceRate);

        var attendees = await AttendeesAsync(client, world.EventId);
        Assert.Equal(s.Present + s.Late, attendees.Count);
        Assert.Equal(detail.TimeInOut!.TappedIn, attendees.Count);
        Assert.Equal(detail.TimeInOut.WithTimeOut, attendees.Count(a => a.TimeOut is not null));

        // The two populations sort as one list, by name.
        Assert.Equal(["Abad", "Rizal", "Santos"], attendees.Select(a => a.LastName));
        Assert.Equal(
            [AttendeePersonType.Personnel, AttendeePersonType.Personnel, AttendeePersonType.Student],
            attendees.Select(a => a.PersonType));

        var rizal = attendees[1];
        Assert.Equal((world.PersonnelId, PersonnelEventWorld.PersonnelNumber), (rizal.PersonnelId, rizal.PersonnelNumber));
        Assert.Equal(("Jose", "P"), (rizal.FirstName, rizal.MiddleName));
        Assert.Equal((null, null), (rizal.StudentId, rizal.StudentNumber));
        Assert.Equal(1800, attendees[0].DurationSeconds);

        var santos = attendees[2];
        Assert.Equal((world.StudentId, "2023-0001"), (santos.StudentId, santos.StudentNumber));
        Assert.Equal((null, null), (santos.PersonnelId, santos.PersonnelNumber));

        var csv = await BodyAsync(client, $"/api/v1/reports/event/{world.EventId}/export.csv");
        Assert.Contains("\"Expected\",\"4\"\r\n\"Expected students\",\"1\"\r\n\"Expected personnel\",\"3\"\r\n", csv);
        Assert.Contains($"\"{PersonnelEventWorld.PersonnelNumber}\",\"Rizal\",\"Jose\",\"P\",", csv);
        Assert.Contains("\"2023-0001\",\"Santos\",\"Maria\",\"Reyes\",", csv);
        Assert.Equal(attendees.Count, csv.Split(EventReportCsv.Time(In)).Length - 1);
    }

    /// <summary>
    /// Frozen means frozen: the row and the name outlive the person, on the roster and the report, as a
    /// student's do. The global filter is tenancy alone; this pins that no read added a delete filter.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_personnel_member_keeps_row_and_name_on_the_frozen_lists()
    {
        var world = await ArrangeAsync();
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, personnelId: world.PersonnelId, checkIn: In);
        await CloseAsync(world.EventId);

        await using (var db = NewDbContext())
        {
            var person = await db.Personnel.SingleAsync(p => p.Id == world.PersonnelId);
            person.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using (var db = NewDbContext())
        {
            var roster = (await EventsOn(db).GetRosterAsync(world.EventId))!;
            var line = Assert.Single(roster.Entries, e => e.PersonnelId == world.PersonnelId);
            Assert.Equal((PersonnelEventWorld.PersonnelName, true), (line.FullName, line.IsExpected));
            Assert.Equal(AttendanceStatus.Present, line.Status);
            Assert.Equal(1, roster.ExpectedPersonnel);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var row = Assert.Single(await AttendeesAsync(client, world.EventId));
        Assert.Equal((world.PersonnelId, "Rizal"), (row.PersonnelId, row.LastName));
    }

    // -------------------------------------------------------------------------------------- query cost

    private async Task<(int Roster, int Attendees, IReadOnlyList<string> Commands)> ListStatementsAsync(Guid eventId)
    {
        var probe = new CommandProbe();
        await using var db = Sql.NewProductionShapedDbContext(School, probe);

        var events = EventsOn(db);
        await events.GetRosterAsync(eventId);
        var roster = probe.Commands.Count;

        var reports = new ReportService(db, (IEventSummaryFigures)events);
        await reports.GetEventAttendeesAsync(eventId, PageRequest.From(null, null));

        return (roster, probe.Commands.Count - roster, probe.Commands);
    }

    /// <summary>No N+1: more rows cost no more statements, and nothing leans on OPENJSON (compatibility 110).</summary>
    [Fact]
    public async Task Roster_and_attendee_lists_cost_the_same_statements_however_many_personnel_rows()
    {
        var world = await ArrangeAsync();
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, world.StudentId, checkIn: In);
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, personnelId: world.PersonnelId, checkIn: In);

        var before = await ListStatementsAsync(world.EventId);
        Assert.Equal(3, before.Attendees);

        for (var i = 2; i < 7; i++)
        {
            var id = await AddPersonnelAsync(world.SchoolId, $"EMP-000{i}");
            await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, personnelId: id, checkIn: In);
        }

        var after = await ListStatementsAsync(world.EventId);
        Assert.Equal((before.Roster, before.Attendees), (after.Roster, after.Attendees));
        Assert.DoesNotContain(after.Commands, c => c.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------- the student-only features (Q8)

    /// <summary>Generate twice around a personnel row, then email: the row adds no attendee and no failure.</summary>
    [Fact]
    public async Task Attendance_codes_are_unchanged_by_a_personnel_row()
    {
        var world = await ArrangeAsync();
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, world.StudentId, checkIn: In);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var route = $"/api/v1/events/{world.EventId}/attendance-codes";
        var first = await PostJsonAsync(client, $"{route}/generate", null);
        Assert.Equal(1, first.GetProperty("created").GetInt32());
        var codes = await BodyAsync(client, route);

        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, personnelId: world.PersonnelId, checkIn: In);

        var second = await PostJsonAsync(client, $"{route}/generate", null);
        Assert.Equal((0, 1), (second.GetProperty("created").GetInt32(), second.GetProperty("alreadyHad").GetInt32()));
        Assert.Equal(codes, await BodyAsync(client, route));

        var email = await PostJsonAsync(client, $"{route}/email", new { mode = "All" });
        Assert.Equal(1, email.GetProperty("requested").GetInt32());
    }

    /// <summary>
    /// Certificates are not built; the only thing that exists is the flag, which reads no attendance. So
    /// this pins what can be pinned: the certificates-only edit on a closed event with a personnel row.
    /// </summary>
    [Fact]
    public async Task Certificates_flag_is_unchanged_by_a_personnel_row()
    {
        var world = await ArrangeAsync();
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, personnelId: world.PersonnelId, checkIn: In);
        await CloseAsync(world.EventId);

        Event stored;
        await using (var db = NewDbContext())
            stored = await db.Events.AsNoTracking().SingleAsync(e => e.Id == world.EventId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.PutAsJsonAsync($"/api/v1/events/{world.EventId}", new
        {
            name = stored.Name, description = stored.Description, location = stored.Location,
            startAt = stored.StartAt, endAt = stored.EndAt, attendanceMode = stored.AttendanceMode,
            graceMinutes = stored.GraceMinutes, requireRegistration = stored.RequireRegistration,
            issuesCertificates = true,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var read = NewDbContext();
        Assert.True((await read.Events.AsNoTracking().SingleAsync(e => e.Id == world.EventId)).IssuesCertificates);
    }

    /// <summary>A student's clearance is byte-for-byte the same before and after staff tap the same event.</summary>
    [Fact]
    public async Task Clearance_is_unchanged_by_a_personnel_row()
    {
        var world = await ArrangeAsync(EventStatus.Closed);
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, world.StudentId, checkIn: In);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        async Task<string> EventsAsync()
        {
            using var body = JsonDocument.Parse(await BodyAsync(client, $"/api/v1/clearance/students/{world.StudentId}"));
            var events = body.RootElement.GetProperty("events");
            Assert.Equal("Attended", Assert.Single(events.EnumerateArray()).GetProperty("attendance").GetString());
            return events.GetRawText();
        }

        var before = await EventsAsync();

        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, personnelId: world.PersonnelId, checkIn: In);

        Assert.Equal(before, await EventsAsync());
    }

    /// <summary>
    /// A personnel row on a shared event, and another on an event only staff attended. Neither adds a
    /// "person", a total, or a row. The negative control is removing BOTH root predicates,
    /// <c>StudentId != null</c> and <c>!Student.IsDeleted</c>: either one alone still excludes the row (the
    /// second is false over the null left join), so removing only the first stays green. With both gone the
    /// personnel row reaches <c>StudentId!.Value</c> and the read fails with a 500 ("Nullable object must
    /// have a value").
    /// </summary>
    [Fact]
    public async Task Analytics_are_unchanged_by_personnel_rows()
    {
        var world = await ArrangeAsync();
        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, world.StudentId, checkIn: In);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        const string route = "/api/v1/reports/attendance-analytics";
        var byEvent = await BodyAsync(client, $"{route}?groupBy=Event");
        var byCourse = await BodyAsync(client, $"{route}?groupBy=Course");

        await AddRowAsync(world.SchoolId, world.EventId, AttendanceStatus.Present, personnelId: world.PersonnelId, checkIn: In);

        await using (var db = NewDbContext())
        {
            var staffOnly = TestData.NewEvent(world.SchoolId, EventStatus.Closed);
            db.Events.Add(staffOnly);
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId, EventId = staffOnly.Id, PersonnelId = world.PersonnelId,
                CheckInAt = In, Status = AttendanceStatus.Present, CaptureMethod = CaptureMethod.Manual,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(byEvent, await BodyAsync(client, $"{route}?groupBy=Event"));
        Assert.Equal(byCourse, await BodyAsync(client, $"{route}?groupBy=Course"));

        using var report = JsonDocument.Parse(byEvent);
        var totals = report.RootElement.GetProperty("totals");
        Assert.Equal((1, 1), (totals.GetProperty("people").GetInt32(), totals.GetProperty("total").GetInt32()));
    }

    // ----------------------------------------------------------------------------------------- helpers

    /// <summary>Every command one context sent.</summary>
    private sealed class CommandProbe : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
