using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// §6.7's reports module (QA Q15/Q16, MDVault #463 Part D), end to end over HTTP on real SQL Server:
/// who may read it, that its per-event numbers are the event summary's own, and that its multi-event
/// totals are pooled.
///
/// <para>
/// <b>The numbers are compared with <c>GET /events/{id}/summary</c>, not with literals alone.</b>
/// ADR-003 D-12/D-13 record that a copied denominator drifts silently. A report that computed its own
/// figures could agree with a hand-written literal and still disagree with the event page, which is
/// the failure that matters.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ReportsApiTests : IntegrationTest
{
    public ReportsApiTests(SqlServerFixture sql) : base(sql) { }

    private static string SingleRoute(Guid eventId) => $"/api/v1/reports/event/{eventId}/summary";

    private static string MultiRoute(IEnumerable<Guid> eventIds) =>
        "/api/v1/reports/events/summary?" + string.Join("&", eventIds.Select(id => $"eventId={id}"));

    // ------------------------------------------------------------------------------ arrangement

    private async Task<Guid> NewSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>
    /// An open event with <paramref name="expected"/> individually attached students, the first
    /// <paramref name="attended"/> of whom have a <c>Present</c> record, plus
    /// <paramref name="walkIns"/> students who were not invited and tapped anyway.
    /// </summary>
    private async Task<Guid> NewEventAsync(
        Guid schoolId, string name, int expected, int attended, int walkIns = 0, int startOffsetHours = 0)
    {
        await using var db = NewDbContext();

        var ev = TestData.NewEvent(schoolId, EventStatus.Open,
            startAt: TestData.Now.AddHours(startOffsetHours));
        ev.Name = name;
        db.Events.Add(ev);

        for (var i = 0; i < expected + walkIns; i++)
        {
            var student = TestData.NewStudent(schoolId, $"{name}-{i:D3}");
            db.Students.Add(student);

            var invited = i < expected;
            if (invited)
                db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = student.Id });

            if (!invited || i < attended)
                db.AttendanceRecords.Add(PresentRecord(schoolId, ev.Id, student.Id));
        }

        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static AttendanceRecord PresentRecord(Guid schoolId, Guid eventId, Guid studentId) => new()
    {
        SchoolId = schoolId,
        EventId = eventId,
        StudentId = studentId,
        CheckInAt = TestData.Now,
        Status = AttendanceStatus.Present,
        CaptureMethod = CaptureMethod.Manual,
    };

    private async Task<HttpClient> AdminClientAsync(EamsApiFactory factory, Guid schoolId) =>
        await SignedInClientAsync(factory, schoolId, EamsRoleNames.SchoolAdmin);

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty(ReportsController.ErrorCodeProperty, out var code)
            ? code.GetString()
            : null;
    }

    // ------------------------------------------------------------------------------ authorization

    [Fact]
    public async Task Anonymous_gets_401_on_every_reports_route()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, "Convocation", expected: 1, attended: 1);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        foreach (var route in new[] { SingleRoute(eventId), MultiRoute([eventId]) })
        {
            var response = await client.GetAsync(route);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"GET {route} with no credential answered {(int)response.StatusCode}; it must be 401.");
        }
    }

    /// <summary>
    /// Q16: administrators only. §11 would have given report reading to both of these roles, which is
    /// why the refusal is pinned over HTTP and not only in the grant matrix — the matrix could be
    /// right while the route's policy named a code both roles hold.
    /// </summary>
    [Fact]
    public async Task Viewer_and_Organizer_get_403_on_every_reports_route()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, "Convocation", expected: 1, attended: 1);

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        foreach (var role in new[] { EamsRoleNames.Viewer, EamsRoleNames.Organizer })
        {
            using var client = await SignedInClientAsync(factory, schoolId, role);

            foreach (var route in new[] { SingleRoute(eventId), MultiRoute([eventId]) })
            {
                var response = await client.GetAsync(route);
                Assert.True(
                    response.StatusCode == HttpStatusCode.Forbidden,
                    $"A signed-in {role} was answered {(int)response.StatusCode} on GET {route}. " +
                    "Reports are admin-only (QA Q16); it must be 403 — not 401, which would tell a " +
                    "signed-in operator to sign in again.");
            }
        }
    }

    // ------------------------------------------------------------------------------ one event

    [Fact]
    public async Task Single_event_report_equals_GET_events_summary()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, "Convocation", expected: 5, attended: 3, walkIns: 2);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        var summary = await client.GetFromJsonAsync<EventSummaryDto>($"/api/v1/events/{eventId}/summary");
        var report = await client.GetFromJsonAsync<EventReportRowDto>(SingleRoute(eventId));

        Assert.NotNull(summary);
        Assert.NotNull(report);

        Assert.Equal(summary.EventId, report.EventId);
        Assert.Equal(summary.EventName, report.EventName);
        Assert.Equal(summary.Expected, report.Expected);
        Assert.Equal(summary.Present, report.Present);
        Assert.Equal(summary.Late, report.Late);
        Assert.Equal(summary.Absent, report.Absent);
        Assert.Equal(summary.Excused, report.Excused);
        Assert.Equal(summary.Unexpected, report.Unexpected);
        Assert.Equal(summary.AttendanceRate, report.AttendanceRate);

        // And the numbers are the arranged ones, so the equality above is not two zeros agreeing.
        Assert.Equal(5, report.Expected);
        Assert.Equal(3, report.Attended);
        Assert.Equal(5, report.Present);   // three invited plus two walk-ins
        Assert.Equal(2, report.Unexpected);
        Assert.Equal(60.0, report.AttendanceRate);
        Assert.Equal(EventStatus.Open, report.Status);
    }

    // ------------------------------------------------------------------------------ several events

    /// <summary>
    /// <b>Pooled, not averaged.</b> 1 of 4 and 1 of 1 pool to 2 of 5 = 40%. The mean of the two rates
    /// would be 62.5%, so the assertion cannot pass by the wrong arithmetic.
    /// </summary>
    [Fact]
    public async Task Multi_event_report_returns_one_row_per_event_and_pooled_totals()
    {
        var schoolId = await NewSchoolAsync();
        var assembly = await NewEventAsync(schoolId, "Assembly", expected: 4, attended: 1, walkIns: 1, startOffsetHours: 0);
        var seminar = await NewEventAsync(schoolId, "Seminar", expected: 1, attended: 1, startOffsetHours: 24);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        // Asked for in reverse start order: the rows come back by start time regardless.
        var report = await client.GetFromJsonAsync<MultiEventReportDto>(MultiRoute([seminar, assembly]));
        Assert.NotNull(report);

        Assert.Equal(new[] { assembly, seminar }, report.Events.Select(r => r.EventId));

        var assemblyRow = report.Events[0];
        Assert.Equal((4, 1, 25.0), (assemblyRow.Expected, assemblyRow.Attended, assemblyRow.AttendanceRate));
        var seminarRow = report.Events[1];
        Assert.Equal((1, 1, 100.0), (seminarRow.Expected, seminarRow.Attended, seminarRow.AttendanceRate));

        var totals = report.Totals;
        Assert.Equal(2, totals.EventCount);
        Assert.Equal(5, totals.Expected);
        Assert.Equal(2, totals.Attended);
        Assert.Equal(3, totals.Present);     // 1 + 1 walk-in on the assembly, 1 on the seminar
        Assert.Equal(1, totals.Unexpected);
        Assert.True(
            totals.AttendanceRate == 40.0,
            $"Pooled rate was {totals.AttendanceRate}. Expected 40 (2 attended of 5 expected). " +
            "62.5 would be the average of the per-event rates, which lets a one-person event swing " +
            "the headline as much as a whole assembly.");

        // Each row is exactly what the event page says about that event.
        foreach (var row in report.Events)
        {
            var summary = await client.GetFromJsonAsync<EventSummaryDto>($"/api/v1/events/{row.EventId}/summary");
            Assert.NotNull(summary);
            Assert.Equal((summary.Expected, summary.AttendanceRate), (row.Expected, row.AttendanceRate));
        }
    }

    [Fact]
    public async Task Multi_event_report_counts_a_duplicated_event_id_once()
    {
        var schoolId = await NewSchoolAsync();
        var assembly = await NewEventAsync(schoolId, "Assembly", expected: 4, attended: 2);
        var seminar = await NewEventAsync(schoolId, "Seminar", expected: 2, attended: 1, startOffsetHours: 24);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        var report = await client.GetFromJsonAsync<MultiEventReportDto>(
            MultiRoute([assembly, assembly, seminar, assembly]));
        Assert.NotNull(report);

        Assert.Equal(2, report.Events.Count);
        Assert.Equal(2, report.Totals.EventCount);
        Assert.Equal(6, report.Totals.Expected);
        Assert.Equal(3, report.Totals.Attended);
        Assert.Equal(50.0, report.Totals.AttendanceRate);
    }

    [Fact]
    public async Task Multi_event_report_refuses_an_empty_selection()
    {
        var schoolId = await NewSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        var response = await client.GetAsync("/api/v1/reports/events/summary");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(MultiEventReportOutcome.SelectionEmpty), await ProblemCodeAsync(response));
    }

    /// <summary>
    /// Pinned against <see cref="ReportLimits.MaxEventsPerReport"/>, never a literal: one over is
    /// refused as too large, and exactly the maximum — including a selection that only reaches one
    /// over by repeating an id — is not. Those two are answered 404 because the ids name no event,
    /// which proves they got past the size check without arranging fifty events.
    /// </summary>
    [Fact]
    public async Task Multi_event_report_refuses_more_than_the_maximum()
    {
        var schoolId = await NewSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        var tooMany = Enumerable.Range(0, ReportLimits.MaxEventsPerReport + 1).Select(_ => Guid.NewGuid()).ToList();
        var refused = await client.GetAsync(MultiRoute(tooMany));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(nameof(MultiEventReportOutcome.SelectionTooLarge), await ProblemCodeAsync(refused));

        var atTheLimit = tooMany.Take(ReportLimits.MaxEventsPerReport).ToList();
        var accepted = await client.GetAsync(MultiRoute(atTheLimit));
        Assert.Equal(HttpStatusCode.NotFound, accepted.StatusCode);
        Assert.Equal(nameof(MultiEventReportOutcome.EventNotFound), await ProblemCodeAsync(accepted));

        var repeated = atTheLimit.Append(atTheLimit[0]).ToList();
        var dedupedToTheLimit = await client.GetAsync(MultiRoute(repeated));
        Assert.Equal(HttpStatusCode.NotFound, dedupedToTheLimit.StatusCode);
    }

    /// <summary>
    /// Another school's event is a tenant miss: 404, like one that does not exist, and the whole
    /// report is refused rather than totalled over the events that were found.
    /// </summary>
    [Fact]
    public async Task Multi_event_report_treats_another_schools_event_as_not_found()
    {
        var ours = await NewSchoolAsync("USA");
        var theirs = await NewSchoolAsync("CPU");
        var ownEvent = await NewEventAsync(ours, "Assembly", expected: 2, attended: 1);
        var foreignEvent = await NewEventAsync(theirs, "Foreign", expected: 3, attended: 3);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, ours);

        var response = await client.GetAsync(MultiRoute([ownEvent, foreignEvent]));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(nameof(MultiEventReportOutcome.EventNotFound), await ProblemCodeAsync(response));

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var missing = body.RootElement.GetProperty(ReportsController.MissingEventIdsProperty)
            .EnumerateArray().Select(e => e.GetGuid()).ToList();
        Assert.Equal(new[] { foreignEvent }, missing);

        // The single-event route agrees.
        var single = await client.GetAsync(SingleRoute(foreignEvent));
        Assert.Equal(HttpStatusCode.NotFound, single.StatusCode);
    }

    // ------------------------------------------------------------------------------ audience by status

    private const string Section = "BSCRIM 2-A";

    private sealed record SectionWorld(
        Guid SchoolId, Guid TermId, Guid OfferingId, Guid GroupId, Guid Tapper, IReadOnlyList<Guid> EventIds);

    /// <summary>
    /// One section of two enrolled students, projected into a group, attached as the audience of one
    /// event per requested status (each created <c>Draft</c> or <c>Open</c>). The first student has a
    /// <c>Present</c> record on every event that is not a <c>Draft</c>.
    /// </summary>
    private async Task<SectionWorld> ArrangeSectionEventsAsync(params string[] createdAs)
    {
        Guid schoolId, termId, offeringId, tapper;
        var eventIds = new List<Guid>();

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            var term = TestData.NewTerm(school.Id);
            db.Terms.Add(term);
            var course = TestData.NewCourse(school.Id);
            db.Courses.Add(course);
            var offering = TestData.NewOffering(term.Id, course.Id, Section);
            db.CourseOfferings.Add(offering);

            var present = TestData.NewStudent(school.Id, "2023-0001", lastName: "Santos");
            var missing = TestData.NewStudent(school.Id, "2023-0002", lastName: "Cruz");
            db.Students.AddRange(present, missing);
            db.Enrollments.AddRange(
                TestData.NewEnrollment(present.Id, offering.Id),
                TestData.NewEnrollment(missing.Id, offering.Id));

            for (var i = 0; i < createdAs.Length; i++)
            {
                var ev = TestData.NewEvent(school.Id, createdAs[i], startAt: TestData.Now.AddDays(i));
                ev.Name = $"Seminar {i}";
                db.Events.Add(ev);
                eventIds.Add(ev.Id);
            }

            await db.SaveChangesAsync();
            (schoolId, termId, offeringId, tapper) = (school.Id, term.Id, offering.Id, present.Id);

            await ProjectionOn(db).SyncTermAsync(term.Id);
        }

        Guid groupId;
        await using (var read = NewDbContext())
        {
            groupId = await read.StudentGroups.AsNoTracking()
                .Where(g => g.SourceEntityType == GroupSourceEntityType.Section)
                .Select(g => g.Id)
                .SingleAsync();
        }

        await using (var db = NewDbContext())
        {
            for (var i = 0; i < eventIds.Count; i++)
            {
                var attached = await EventsOn(db).AttachAudienceAsync(
                    eventIds[i], new EventAudienceRequest([groupId], null));
                Assert.Equal(EventWriteOutcome.Saved, attached.Outcome);

                if (createdAs[i] != EventStatus.Draft)
                    db.AttendanceRecords.Add(PresentRecord(schoolId, eventIds[i], tapper));
            }

            await db.SaveChangesAsync();
        }

        return new SectionWorld(schoolId, termId, offeringId, groupId, tapper, eventIds);
    }

    private async Task MoveToAsync(Guid eventId, string status)
    {
        await using var db = NewDbContext();
        var moved = await EventsOn(db).ChangeStatusAsync(eventId, status);
        Assert.Equal(EventWriteOutcome.Saved, moved.Outcome);
    }

    /// <summary>A later import: a third student joins the section, and the group is re-projected.</summary>
    private async Task EnrolLatecomerAsync(SectionWorld world)
    {
        await using var db = NewDbContext();
        var latecomer = TestData.NewStudent(world.SchoolId, "2023-0003", lastName: "Latecomer");
        db.Students.Add(latecomer);
        db.Enrollments.Add(TestData.NewEnrollment(latecomer.Id, world.OfferingId));
        await db.SaveChangesAsync();
        await ProjectionOn(db).SyncTermAsync(world.TermId);
    }

    /// <summary>
    /// Reads one event through both report routes and through <c>GET /events/{id}/summary</c>, asserts
    /// all three agree on every published figure, and returns the multi-event route's row.
    /// </summary>
    private static async Task<EventReportRowDto> ReportAgreeingWithSummaryAsync(HttpClient client, Guid eventId)
    {
        var summary = await client.GetFromJsonAsync<EventSummaryDto>($"/api/v1/events/{eventId}/summary");
        var single = await client.GetFromJsonAsync<EventReportRowDto>(SingleRoute(eventId));
        var multi = await client.GetFromJsonAsync<MultiEventReportDto>(MultiRoute([eventId]));

        Assert.NotNull(summary);
        Assert.NotNull(single);
        Assert.NotNull(multi);
        var row = Assert.Single(multi.Events);

        foreach (var report in new[] { single, row })
        {
            Assert.Equal(summary.Expected, report.Expected);
            Assert.Equal(summary.Present, report.Present);
            Assert.Equal(summary.Absent, report.Absent);
            Assert.Equal(summary.Excused, report.Excused);
            Assert.Equal(summary.Late, report.Late);
            Assert.Equal(summary.Unexpected, report.Unexpected);
            Assert.Equal(summary.AttendanceRate, report.AttendanceRate);
        }

        Assert.Equal(summary.AttendanceRate, multi.Totals.AttendanceRate);
        return row;
    }

    /// <summary>
    /// A closed event reports the audience written down when it closed. A student enrolled into the
    /// section afterwards moves an open event attached to the same section — the contrast that proves
    /// the enrolment really landed — and does not move the closed one.
    /// </summary>
    [Fact]
    public async Task Closed_event_in_a_report_uses_its_frozen_audience()
    {
        var world = await ArrangeSectionEventsAsync(EventStatus.Open, EventStatus.Open);
        var (closedId, openId) = (world.EventIds[0], world.EventIds[1]);

        await MoveToAsync(closedId, EventStatus.Closed);
        await EnrolLatecomerAsync(world);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, world.SchoolId);

        var closedRow = await ReportAgreeingWithSummaryAsync(client, closedId);
        var openRow = await ReportAgreeingWithSummaryAsync(client, openId);

        Assert.Equal(EventStatus.Closed, closedRow.Status);
        Assert.True(
            closedRow.Expected == 2,
            $"The closed event reported {closedRow.Expected} expected. Its audience was frozen at 2 " +
            "when it closed; a student enrolled afterwards must not move a past event's denominator.");
        Assert.Equal(1, closedRow.Attended);
        Assert.Equal(1, closedRow.Absent);
        Assert.Equal(50.0, closedRow.AttendanceRate);

        Assert.Equal(3, openRow.Expected);
    }

    /// <summary>
    /// <c>Open → Cancelled</c> freezes the audience too (<c>EventStatusTransition.HasFrozenAudience</c>)
    /// but materializes no absentees. The report must hold the denominator at the cancellation, as the
    /// event summary does, while the still-open contrast event moves.
    /// </summary>
    [Fact]
    public async Task Cancelled_event_in_a_report_uses_its_frozen_audience()
    {
        var world = await ArrangeSectionEventsAsync(EventStatus.Open, EventStatus.Open);
        var (cancelledId, openId) = (world.EventIds[0], world.EventIds[1]);

        await MoveToAsync(cancelledId, EventStatus.Cancelled);
        await EnrolLatecomerAsync(world);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, world.SchoolId);

        var cancelledRow = await ReportAgreeingWithSummaryAsync(client, cancelledId);
        var openRow = await ReportAgreeingWithSummaryAsync(client, openId);

        Assert.Equal(EventStatus.Cancelled, cancelledRow.Status);
        Assert.True(
            cancelledRow.Expected == 2,
            $"The cancelled event reported {cancelledRow.Expected} expected. A cancelled event's audience " +
            "is frozen at the transition; a later enrolment must not move it.");
        Assert.Equal(1, cancelledRow.Attended);
        Assert.Equal(0, cancelledRow.Absent);   // Cancelled snapshots; it does not materialize absentees
        Assert.Equal(50.0, cancelledRow.AttendanceRate);

        Assert.Equal(3, openRow.Expected);
    }

    /// <summary>
    /// A <c>Draft</c> event's audience is live: a student enrolled after the audience was attached is
    /// expected, in the report exactly as on the event page.
    /// </summary>
    [Fact]
    public async Task Draft_event_in_a_report_uses_live_audience()
    {
        var world = await ArrangeSectionEventsAsync(EventStatus.Draft);
        var draftId = world.EventIds[0];

        await EnrolLatecomerAsync(world);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, world.SchoolId);

        var row = await ReportAgreeingWithSummaryAsync(client, draftId);

        Assert.Equal(EventStatus.Draft, row.Status);
        Assert.Equal(3, row.Expected);
        Assert.Equal(0, row.Attended);
        Assert.Equal(0.0, row.AttendanceRate);
    }

    // ------------------------------------------------------------------------------ SuperAdmin

    /// <summary>AC3's other half: SuperAdmin holds <c>reports.read</c> and reads both routes.</summary>
    [Fact]
    public async Task SuperAdmin_can_read_both_reports_routes()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, "Convocation", expected: 2, attended: 1);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId, EamsRoleNames.SuperAdmin);

        foreach (var route in new[] { SingleRoute(eventId), MultiRoute([eventId]) })
        {
            var response = await client.GetAsync(route);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"A signed-in SuperAdmin was answered {(int)response.StatusCode} on GET {route}. " +
                "SuperAdmin holds reports.read (QA Q16); a 403 means the grant or the policy is missing.");
        }
    }

    /// <summary>
    /// Tenancy is school-scoped for every role, SuperAdmin included (JJ, P3): there is no cross-school
    /// report view, so another school's event is a 404 on both routes, as it is for a SchoolAdmin.
    /// </summary>
    [Fact]
    public async Task SuperAdmin_reporting_on_another_schools_event_is_also_404()
    {
        var ours = await NewSchoolAsync("USA");
        var theirs = await NewSchoolAsync("CPU");
        var foreignEvent = await NewEventAsync(theirs, "Foreign", expected: 2, attended: 2);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, ours, EamsRoleNames.SuperAdmin);

        foreach (var route in new[] { SingleRoute(foreignEvent), MultiRoute([foreignEvent]) })
        {
            var response = await client.GetAsync(route);
            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"A SuperAdmin of another school was answered {(int)response.StatusCode} on GET {route}. " +
                "Reports are school-scoped for every role; another school's event must be a 404.");
        }
    }

    // ------------------------------------------------------------------------------ edge cases

    /// <summary>
    /// An event with no audience attached expects nobody. That is an honest zero, not a division by
    /// zero and not a 500 — on both routes, and in the pooled totals.
    /// </summary>
    [Fact]
    public async Task Report_of_an_event_with_nobody_expected_reports_a_zero_rate_not_an_error()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, "Empty", expected: 0, attended: 0, walkIns: 1);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        var row = await ReportAgreeingWithSummaryAsync(client, eventId);
        Assert.Equal((0, 0, 0.0), (row.Expected, row.Attended, row.AttendanceRate));
        Assert.Equal(1, row.Unexpected);

        var report = await client.GetFromJsonAsync<MultiEventReportDto>(MultiRoute([eventId]));
        Assert.NotNull(report);
        Assert.Equal((0, 0, 0.0), (report.Totals.Expected, report.Totals.Attended, report.Totals.AttendanceRate));
    }

    /// <summary>
    /// Exactly <see cref="ReportLimits.MaxEventsPerReport"/> real events succeed, and every one is
    /// counted. One school, one expected student per event, every other one attending — the cheapest
    /// arrangement that still makes a wrong sum visible.
    /// </summary>
    [Fact]
    public async Task Multi_event_report_succeeds_at_exactly_the_maximum_with_real_events()
    {
        var schoolId = await NewSchoolAsync();
        var eventIds = new List<Guid>();

        await using (var db = NewDbContext())
        {
            for (var i = 0; i < ReportLimits.MaxEventsPerReport; i++)
            {
                var ev = TestData.NewEvent(schoolId, EventStatus.Open, startAt: TestData.Now.AddHours(i));
                ev.Name = $"Event {i:D2}";
                db.Events.Add(ev);
                eventIds.Add(ev.Id);

                var student = TestData.NewStudent(schoolId, $"MAX-{i:D2}");
                db.Students.Add(student);
                db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = student.Id });
                if (i % 2 == 0) db.AttendanceRecords.Add(PresentRecord(schoolId, ev.Id, student.Id));
            }

            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        var response = await client.GetAsync(MultiRoute(eventIds));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<MultiEventReportDto>();
        Assert.NotNull(report);

        var attending = (ReportLimits.MaxEventsPerReport + 1) / 2;
        Assert.Equal(ReportLimits.MaxEventsPerReport, report.Events.Count);
        Assert.Equal(ReportLimits.MaxEventsPerReport, report.Totals.EventCount);
        Assert.Equal(ReportLimits.MaxEventsPerReport, report.Totals.Expected);
        Assert.Equal(attending, report.Totals.Attended);
        Assert.Equal(attending, report.Totals.Present);
        Assert.Equal(
            Math.Round((double)attending / ReportLimits.MaxEventsPerReport * 100, 1),
            report.Totals.AttendanceRate);
        Assert.Equal(eventIds.ToHashSet(), report.Events.Select(r => r.EventId).ToHashSet());
    }

    /// <summary>
    /// <c>Absent</c> and <c>Excused</c> rows on two events are summed into the totals, and neither
    /// counts as attended — only <c>Present</c> and <c>Late</c> do.
    /// </summary>
    [Fact]
    public async Task Multi_event_report_pools_excused_and_absent_across_events()
    {
        var schoolId = await NewSchoolAsync();
        var first = await NewEventWithRecordsAsync(schoolId, "First", AttendanceMode.Single,
            [AttendanceStatus.Present, AttendanceStatus.Absent, AttendanceStatus.Excused]);
        var second = await NewEventWithRecordsAsync(schoolId, "Second", AttendanceMode.Single,
            [AttendanceStatus.Late, AttendanceStatus.Excused, AttendanceStatus.Absent, AttendanceStatus.Absent],
            startOffsetHours: 24);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        await ReportAgreeingWithSummaryAsync(client, first);
        await ReportAgreeingWithSummaryAsync(client, second);

        var report = await client.GetFromJsonAsync<MultiEventReportDto>(MultiRoute([first, second]));
        Assert.NotNull(report);

        var t = report.Totals;
        Assert.Equal(7, t.Expected);
        Assert.Equal(1, t.Present);
        Assert.Equal(1, t.Late);
        Assert.Equal(3, t.Absent);
        Assert.Equal(2, t.Excused);
        Assert.Equal(2, t.Attended);           // Present + Late only
        Assert.Equal(28.6, t.AttendanceRate);  // 2 / 7
        Assert.Equal(report.Events.Sum(r => r.Absent), t.Absent);
        Assert.Equal(report.Events.Sum(r => r.Excused), t.Excused);
    }

    /// <summary>
    /// A <c>TimeInOut</c> event — rows carrying a check-out as well as a check-in — reports exactly
    /// what its summary does. The mode changes what a row records, not who is counted.
    /// </summary>
    [Fact]
    public async Task Report_of_a_TimeInOut_event_matches_its_summary()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventWithRecordsAsync(schoolId, "Workshop", AttendanceMode.TimeInOut,
            [AttendanceStatus.Present, AttendanceStatus.Late, null],
            checkOut: true);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        var row = await ReportAgreeingWithSummaryAsync(client, eventId);
        Assert.Equal((3, 2, 66.7), (row.Expected, row.Attended, row.AttendanceRate));
    }

    /// <summary>
    /// A malformed id is refused at model binding with a 400 — the framework's validation problem, which
    /// carries no <c>code</c> extension — before any query runs.
    /// </summary>
    [Fact]
    public async Task Multi_event_report_refuses_a_malformed_event_id()
    {
        var schoolId = await NewSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminClientAsync(factory, schoolId);

        var response = await client.GetAsync("/api/v1/reports/events/summary?eventId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The framework's validation problem: an `errors` map keyed by the parameter, and no `code`
        // extension — that token is only stamped on the refusals the report service decides.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("eventId", out _));
        Assert.False(body.RootElement.TryGetProperty(ReportsController.ErrorCodeProperty, out _));
    }

    /// <summary>
    /// An open event whose invited students carry the given statuses, one student per entry; a
    /// <c>null</c> entry is an invited student with no record.
    /// </summary>
    private async Task<Guid> NewEventWithRecordsAsync(
        Guid schoolId, string name, string mode, IReadOnlyList<string?> statuses,
        int startOffsetHours = 0, bool checkOut = false)
    {
        await using var db = NewDbContext();

        var ev = TestData.NewEvent(schoolId, EventStatus.Open, mode, startAt: TestData.Now.AddHours(startOffsetHours));
        ev.Name = name;
        db.Events.Add(ev);

        for (var i = 0; i < statuses.Count; i++)
        {
            var student = TestData.NewStudent(schoolId, $"{name}-{i:D3}");
            db.Students.Add(student);
            db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = student.Id });

            if (statuses[i] is not { } recorded) continue;

            var record = PresentRecord(schoolId, ev.Id, student.Id);
            record.Status = recorded;
            if (recorded is AttendanceStatus.Absent or AttendanceStatus.Excused) record.CheckInAt = null;
            if (checkOut && record.CheckInAt is { } checkIn) record.CheckOutAt = checkIn.AddHours(2);
            db.AttendanceRecords.Add(record);
        }

        await db.SaveChangesAsync();
        return ev.Id;
    }
}
