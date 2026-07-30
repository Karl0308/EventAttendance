using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Technical Plan §6.3's write surface: create, update, soft delete, and the status graph.
///
/// <para>
/// The status transitions get more attention than the field validation because they are the half that
/// is not obviously right. A rejected over-length name is a visible 400; a status change that should
/// have been refused writes a plausible row and is discovered months later, when a report is wrong.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventWriteTests : IntegrationTest
{
    public EventWriteTests(SqlServerFixture sql) : base(sql) { }

    private static EventWriteRequest Request(
        string name = "University Convocation 2026",
        string? description = "Annual convocation.",
        string? location = "USA Gymnasium",
        DateTime? startAt = null,
        DateTime? endAt = null,
        string? attendanceMode = null,
        int graceMinutes = 15,
        bool requireRegistration = false) =>
        new(name, description, location,
            startAt ?? TestData.Now, endAt ?? (startAt ?? TestData.Now).AddHours(3),
            attendanceMode, graceMinutes, requireRegistration);

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>Creates an event directly, bypassing the service, at a chosen status.</summary>
    private async Task<Guid> ArrangeEventAsync(Guid schoolId, string status)
    {
        await using var db = NewDbContext();
        var ev = TestData.NewEvent(schoolId, status);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    // ------------------------------------------------------------------------------- create

    [Fact]
    public async Task A_created_event_is_a_draft_and_carries_every_field_it_was_given()
    {
        var schoolId = await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).CreateAsync(Request(
            name: "  Foundation Day  ", location: "Quadrangle",
            attendanceMode: "timeinout", graceMinutes: 30, requireRegistration: true));

        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        Assert.NotNull(response.Event);

        await using var read = NewDbContext();
        var stored = await read.Events.AsNoTracking().SingleAsync();

        Assert.Equal(schoolId, stored.SchoolId);
        Assert.Equal(EventStatus.Draft, stored.Status);
        // Trimmed on the way in — a leading space in a name is invisible everywhere except a sort.
        Assert.Equal("Foundation Day", stored.Name);
        // Canonicalized, not rejected: liberal in what is accepted, canonical in what is stored, which
        // is the rule DomainValues states for every one of these sets.
        Assert.Equal(AttendanceMode.TimeInOut, stored.AttendanceMode);
        Assert.Equal(30, stored.GraceMinutes);
        Assert.True(stored.RequireRegistration);
        Assert.Equal("Quadrangle", stored.Location);
    }

    /// <summary>
    /// The one thing a create must not let a caller do. If a request could name its own status, an
    /// event could reach <c>Closed</c> without passing through the code that materializes its
    /// absentees — a closed event with an unfrozen roster, indistinguishable from one where everybody
    /// attended.
    /// </summary>
    [Fact]
    public async Task A_created_event_cannot_start_at_any_status_but_draft()
    {
        await ArrangeSchoolAsync();

        // Asserted structurally rather than by trying to send a status: there is no field to send.
        Assert.DoesNotContain(
            typeof(EventWriteRequest).GetProperties(),
            p => p.Name.Equals("Status", StringComparison.OrdinalIgnoreCase));

        await using var db = NewDbContext();
        var response = await EventsOn(db).CreateAsync(Request());

        Assert.Equal(EventStatus.Draft, response.Event!.Status);
    }

    /// <summary>
    /// §4.5's column widths and the one rule that is not a width. Each is a 400 rather than a SQL
    /// truncation 500 — the same defect class <c>AttendanceNotes</c> closed one endpoint over.
    /// </summary>
    [Theory]
    [InlineData("", "blank name")]
    [InlineData("   ", "whitespace name")]
    public async Task A_blank_name_is_rejected(string name, string _)
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).CreateAsync(Request(name: name));

        Assert.Equal(EventWriteOutcome.ValidationFailed, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.Events.CountAsync());
    }

    [Fact]
    public async Task Over_length_text_is_rejected_rather_than_truncated_by_sql_server()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var events = EventsOn(db);

        Assert.Equal(EventWriteOutcome.ValidationFailed,
            (await events.CreateAsync(Request(name: new string('n', EventText.NameMaxLength + 1)))).Outcome);
        Assert.Equal(EventWriteOutcome.ValidationFailed,
            (await events.CreateAsync(Request(
                description: new string('d', EventText.DescriptionMaxLength + 1)))).Outcome);
        Assert.Equal(EventWriteOutcome.ValidationFailed,
            (await events.CreateAsync(Request(
                location: new string('l', EventText.LocationMaxLength + 1)))).Outcome);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.Events.CountAsync());
    }

    /// <summary>
    /// An inverted or empty window is refused because it makes the capture path incoherent, not
    /// because it looks untidy: <c>TapAsync</c> decides Present versus Late from <c>StartAt</c> plus
    /// the grace period, so every arrival at such an event would be misjudged with nothing to explain
    /// it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task An_event_must_end_after_it_starts(int hoursAfterStart)
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).CreateAsync(Request(
            startAt: TestData.Now, endAt: TestData.Now.AddHours(hoursAfterStart)));

        Assert.Equal(EventWriteOutcome.ValidationFailed, response.Outcome);
    }

    /// <summary>
    /// The window is compared after both ends are normalized, because one JSON body can carry two
    /// different <c>DateTimeKind</c>s — a <c>Z</c> start and a <c>+08:00</c> end are the same three
    /// hours apart that this test asserts, and comparing them raw is off by the server's offset.
    /// </summary>
    [Fact]
    public async Task A_window_whose_ends_arrive_in_different_kinds_is_compared_correctly()
    {
        await ArrangeSchoolAsync();

        var startUtc = new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc);
        // 12:00+08:00 is 04:00Z — three hours after the start, but numerically *before* it.
        var endLocal = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.FromHours(8)).LocalDateTime;

        await using var db = NewDbContext();
        var response = await EventsOn(db).CreateAsync(Request(startAt: startUtc, endAt: endLocal));

        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);

        await using var read = NewDbContext();
        var stored = await read.Events.AsNoTracking().SingleAsync();
        Assert.Equal(TimeSpan.FromHours(3), stored.EndAt - stored.StartAt);
        Assert.Equal(DateTimeKind.Utc, stored.StartAt.Kind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(EventText.MaxGraceMinutes + 1)]
    public async Task A_grace_period_outside_the_documented_range_is_rejected(int graceMinutes)
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).CreateAsync(Request(graceMinutes: graceMinutes));

        Assert.Equal(EventWriteOutcome.ValidationFailed, response.Outcome);
    }

    [Fact]
    public async Task An_undocumented_attendance_mode_is_rejected()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).CreateAsync(Request(attendanceMode: "Continuous"));

        Assert.Equal(EventWriteOutcome.ValidationFailed, response.Outcome);
    }

    /// <summary>
    /// The pre-auth tenant fallback (ADR-001 D-6). With nothing pinned and two schools there is no
    /// honest answer, and filing the event under whichever row sorts first would be a guess nobody
    /// could later detect.
    /// </summary>
    [Fact]
    public async Task An_event_cannot_be_created_when_no_school_can_be_resolved()
    {
        await using (var db = NewDbContext())
        {
            db.Schools.Add(TestData.NewSchool("USA"));
            db.Schools.Add(TestData.NewSchool("CICSS"));
            await db.SaveChangesAsync();
        }

        await using var write = NewDbContext();
        var response = await EventsOn(write).CreateAsync(Request());

        Assert.Equal(EventWriteOutcome.NoSchoolResolved, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.Events.CountAsync());
    }

    /// <summary>And the same two schools, with one pinned: the pin decides, and nothing is ambiguous.</summary>
    [Fact]
    public async Task A_pinned_tenant_decides_which_school_a_new_event_belongs_to()
    {
        Guid pinnedId;
        await using (var db = NewDbContext())
        {
            var pinned = TestData.NewSchool("USA");
            db.Schools.Add(pinned);
            db.Schools.Add(TestData.NewSchool("CICSS"));
            await db.SaveChangesAsync();
            pinnedId = pinned.Id;
        }

        School.CurrentSchoolId = pinnedId;

        await using var write = NewDbContext();
        var response = await EventsOn(write).CreateAsync(Request());

        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(pinnedId, (await read.Events.AsNoTracking().SingleAsync()).SchoolId);
    }

    /// <summary>
    /// §6.4 calls the manual-override endpoint audited and <c>ICurrentUser</c> explains why the seam
    /// is wired before there is anything in it. §4.5's <c>OrganizerUserId</c> is the same argument: a
    /// row written today with a null organizer can never be attributed later.
    /// </summary>
    [Fact]
    public async Task A_created_event_records_who_organized_it()
    {
        var schoolId = await ArrangeSchoolAsync();

        Guid organizerId;
        await using (var db = NewDbContext())
        {
            var user = TestData.NewUser(schoolId);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            organizerId = user.Id;
        }

        CurrentUser.UserId = organizerId;

        await using (var db = NewDbContext())
            await EventsOn(db).CreateAsync(Request());

        await using var read = NewDbContext();
        Assert.Equal(organizerId, (await read.Events.AsNoTracking().SingleAsync()).OrganizerUserId);
    }

    // ------------------------------------------------------------------------------- update

    [Fact]
    public async Task An_update_replaces_the_events_own_fields_and_leaves_its_status_alone()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Open);

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).UpdateAsync(eventId, Request(
                name: "Renamed", description: null, location: null, graceMinutes: 0));
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        await using var read = NewDbContext();
        var stored = await read.Events.AsNoTracking().SingleAsync();
        Assert.Equal("Renamed", stored.Name);
        Assert.Null(stored.Location);
        Assert.Equal(0, stored.GraceMinutes);
        Assert.Equal(EventStatus.Open, stored.Status);
    }

    /// <summary>
    /// A closed event's window and grace period are the inputs that decided Present versus Late for
    /// every row already written. Editing them afterwards changes what those rows mean without
    /// changing the rows — which is the silent movement the whole freeze exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_closed_event_cannot_be_edited()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Closed);

        await using var db = NewDbContext();
        var response = await EventsOn(db).UpdateAsync(eventId, Request(name: "Renamed"));

        Assert.Equal(EventWriteOutcome.EventLocked, response.Outcome);

        await using var read = NewDbContext();
        Assert.NotEqual("Renamed", (await read.Events.AsNoTracking().SingleAsync()).Name);
    }

    /// <summary>
    /// A cancelled event's <em>descriptive</em> fields can still be edited. "Cancelled — venue flooded"
    /// is the legitimate edit the allowance exists for, and it costs a well-behaved caller nothing: a
    /// full PUT that leaves the scheduling fields as they are changes none of them.
    /// </summary>
    [Fact]
    public async Task A_cancelled_event_can_still_be_edited()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Cancelled);

        await using var db = NewDbContext();
        var response = await EventsOn(db).UpdateAsync(
            eventId, Request(description: "Cancelled — venue flooded."));

        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal("Cancelled — venue flooded.",
            (await read.Events.AsNoTracking().SingleAsync()).Description);
    }

    /// <summary>
    /// <b>But its window and grace period cannot.</b> The allowance used to be justified as "nothing was
    /// computed from a cancelled event", and that is false for <c>Open → Cancelled</c>: taps can already
    /// exist by then, and <c>StartAt + GraceMinutes</c> has already decided Present-versus-Late for every
    /// one of them. Moving them afterwards changes what those rows mean without changing the rows —
    /// which is the exact reason a <c>Closed</c> event refuses the same edit.
    /// </summary>
    [Theory]
    [InlineData("startAt")]
    [InlineData("graceMinutes")]
    [InlineData("attendanceMode")]
    public async Task A_cancelled_events_attendance_rules_cannot_be_edited(string field)
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Cancelled);

        var request = field switch
        {
            "startAt" => Request(startAt: TestData.Now.AddDays(1)),
            "graceMinutes" => Request(graceMinutes: 45),
            "attendanceMode" => Request(attendanceMode: AttendanceMode.TimeInOut),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unmapped field."),
        };

        await using var db = NewDbContext();
        var response = await EventsOn(db).UpdateAsync(eventId, request);

        Assert.Equal(EventWriteOutcome.EventLocked, response.Outcome);
        // The message names what the caller tried to change, so "why was this refused" is answerable
        // without reading the source.
        Assert.Contains(field, response.Message, StringComparison.OrdinalIgnoreCase);

        await using var read = NewDbContext();
        var stored = await read.Events.AsNoTracking().SingleAsync();
        Assert.Equal(TestData.Now, stored.StartAt);
        Assert.Equal(15, stored.GraceMinutes);
        Assert.Equal(AttendanceMode.Single, stored.AttendanceMode);
    }

    /// <summary>
    /// The rule is gated on status rather than on "does this event have attendance rows", so it holds
    /// for <c>Draft → Cancelled</c> too — where there are no taps, but no reason to move the window of
    /// something that never happened either. Pinned because a row-count gate would pass every other test
    /// here and fail this one.
    /// </summary>
    [Fact]
    public async Task A_cancelled_event_with_no_attendance_still_refuses_a_schedule_edit()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Draft);

        await using (var db = NewDbContext())
            await EventsOn(db).ChangeStatusAsync(eventId, EventStatus.Cancelled);

        await using var write = NewDbContext();
        Assert.Equal(0, await write.AttendanceRecords.CountAsync());

        var response = await EventsOn(write).UpdateAsync(
            eventId, Request(startAt: TestData.Now.AddDays(1)));

        Assert.Equal(EventWriteOutcome.EventLocked, response.Outcome);
    }

    /// <summary>
    /// A live event's scheduling fields remain fully editable — the control that shows the rule above is
    /// about the terminal status and not a blanket lock.
    /// </summary>
    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    public async Task A_live_events_attendance_rules_can_still_be_edited(string status)
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, status);

        await using var db = NewDbContext();
        var response = await EventsOn(db).UpdateAsync(
            eventId, Request(startAt: TestData.Now.AddDays(1), graceMinutes: 45));

        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(45, (await read.Events.AsNoTracking().SingleAsync()).GraceMinutes);
    }

    [Fact]
    public async Task An_update_to_an_unknown_or_deleted_event_is_not_found()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Draft);

        await using (var db = NewDbContext())
            await EventsOn(db).DeleteAsync(eventId);

        await using var write = NewDbContext();
        var events = EventsOn(write);

        Assert.Equal(EventWriteOutcome.NotFound, (await events.UpdateAsync(eventId, Request())).Outcome);
        Assert.Equal(EventWriteOutcome.NotFound,
            (await events.UpdateAsync(Guid.NewGuid(), Request())).Outcome);
    }

    // ------------------------------------------------------------------------------- delete

    /// <summary>
    /// §4.5's soft delete. The attendance trail is deliberately untouched — every FK in this model is
    /// <c>Restrict</c>, and a record of who attended must not vanish because someone tidied a calendar.
    /// </summary>
    [Fact]
    public async Task A_deleted_event_disappears_from_reads_but_keeps_its_attendance()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Open);

        await using (var db = NewDbContext())
        {
            var student = TestData.NewStudent(schoolId);
            db.Students.Add(student);
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = schoolId,
                EventId = eventId, StudentId = student.Id,
                CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDbContext())
            Assert.Equal(EventWriteOutcome.Saved, (await EventsOn(db).DeleteAsync(eventId)).Outcome);

        await using var read = NewDbContext();
        var events = EventsOn(read);

        Assert.Null(await events.GetAsync(eventId));
        Assert.Null(await events.GetSummaryAsync(eventId));
        Assert.Null(await events.GetRosterAsync(eventId));
        Assert.Empty((await events.ListAsync(null, PageRequest.Default)).Items);

        Assert.Equal(1, await read.AttendanceRecords.CountAsync());
        Assert.True((await read.Events.AsNoTracking().SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task Deleting_an_already_deleted_event_is_not_found()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Draft);

        await using (var db = NewDbContext())
            await EventsOn(db).DeleteAsync(eventId);

        await using var second = NewDbContext();
        Assert.Equal(EventWriteOutcome.NotFound, (await EventsOn(second).DeleteAsync(eventId)).Outcome);
    }

    // ------------------------------------------------------------------- the status graph

    /// <summary>
    /// Every legal edge of <c>EventStatusTransition</c>'s matrix, driven through the service.
    /// </summary>
    [Theory]
    [InlineData(EventStatus.Draft, EventStatus.Open)]
    [InlineData(EventStatus.Draft, EventStatus.Cancelled)]
    [InlineData(EventStatus.Open, EventStatus.Closed)]
    [InlineData(EventStatus.Open, EventStatus.Cancelled)]
    public async Task A_legal_transition_is_written(string from, string to)
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, from);

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).ChangeStatusAsync(eventId, to);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        await using var read = NewDbContext();
        Assert.Equal(to, (await read.Events.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>
    /// Every illegal edge, and the assertion that matters as much as the outcome: <b>nothing is
    /// written</b>. The failure mode this guards is not a wrong status code, it is a status column
    /// that moved anyway.
    /// </summary>
    [Theory]
    [InlineData(EventStatus.Draft, EventStatus.Closed)]
    [InlineData(EventStatus.Open, EventStatus.Draft)]
    [InlineData(EventStatus.Closed, EventStatus.Open)]
    [InlineData(EventStatus.Closed, EventStatus.Draft)]
    [InlineData(EventStatus.Closed, EventStatus.Cancelled)]
    [InlineData(EventStatus.Cancelled, EventStatus.Open)]
    [InlineData(EventStatus.Cancelled, EventStatus.Draft)]
    [InlineData(EventStatus.Cancelled, EventStatus.Closed)]
    public async Task An_illegal_transition_is_refused_and_writes_nothing(string from, string to)
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, from);

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).ChangeStatusAsync(eventId, to);
            Assert.Equal(EventWriteOutcome.IllegalTransition, response.Outcome);
            Assert.Null(response.Event);
        }

        await using var read = NewDbContext();
        Assert.Equal(from, (await read.Events.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>
    /// The re-open decision, stated as a test so it cannot be quietly reversed. <c>Closed</c> is
    /// terminal because the alternatives both break the freeze: deleting the materialized absentees
    /// destroys records an organizer may have edited by hand, and keeping them leaves an event that
    /// calls itself live while carrying a roster that no longer reflects who is enrolled.
    ///
    /// <para>
    /// The refusal message names the way out, because a dead end with no signposted alternative is how
    /// somebody ends up deleting the constant.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_closed_event_cannot_be_reopened_and_the_refusal_says_what_to_do_instead()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Closed);

        await using var db = NewDbContext();
        var response = await EventsOn(db).ChangeStatusAsync(eventId, EventStatus.Open);

        Assert.Equal(EventWriteOutcome.IllegalTransition, response.Outcome);
        Assert.Contains("terminal", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/attendance/manual", response.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the escape hatch actually works — asserted rather than merely promised in a message. A
    /// closed event's individual rows can still be corrected, deliberately and audited; what cannot
    /// happen is the denominator moving on its own.
    /// </summary>
    [Fact]
    public async Task A_closed_events_individual_records_can_still_be_corrected_by_an_override()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Closed);

        Guid studentId;
        await using (var db = NewDbContext())
        {
            var student = TestData.NewStudent(schoolId);
            db.Students.Add(student);
            await db.SaveChangesAsync();
            studentId = student.Id;
        }

        await using var write = NewDbContext();
        var response = await AttendanceOn(write).ManualAsync(
            eventId, studentId, AttendanceStatus.Excused, "Closed in error; student was present.");

        Assert.Equal(ManualOutcome.Saved, response.Outcome);
    }

    /// <summary>
    /// A retried <c>PATCH</c> names the status the event already reached. It must succeed and it must
    /// do nothing — the second half is the load-bearing one on the <c>Closed</c> self-edge, where
    /// re-running the freeze would insert an <c>Absent</c> row for every student who joined an attached
    /// section since. That is covered end to end in <see cref="EventCloseFreezeTests"/>; here it is the
    /// plain no-op.
    /// </summary>
    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    [InlineData(EventStatus.Closed)]
    [InlineData(EventStatus.Cancelled)]
    public async Task Patching_a_status_to_the_one_it_already_has_succeeds_and_changes_nothing(
        string status)
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, status);

        DateTime before;
        await using (var db = NewDbContext())
            before = (await db.Events.AsNoTracking().SingleAsync()).UpdatedAt;

        await using (var db = NewDbContext())
        {
            var response = await EventsOn(db).ChangeStatusAsync(eventId, status);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        await using var read = NewDbContext();
        var stored = await read.Events.AsNoTracking().SingleAsync();
        Assert.Equal(status, stored.Status);
        Assert.Equal(before, stored.UpdatedAt);
    }

    /// <summary>Casing is canonicalized, matching every other §4 value set in this system.</summary>
    [Fact]
    public async Task A_status_is_matched_case_insensitively_and_stored_canonically()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Draft);

        await using (var db = NewDbContext())
            Assert.Equal(EventWriteOutcome.Saved,
                (await EventsOn(db).ChangeStatusAsync(eventId, "  oPeN  ")).Outcome);

        await using var read = NewDbContext();
        Assert.Equal(EventStatus.Open, (await read.Events.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_status_outside_the_documented_set_is_a_validation_failure_not_a_transition_one()
    {
        var schoolId = await ArrangeSchoolAsync();
        var eventId = await ArrangeEventAsync(schoolId, EventStatus.Draft);

        await using var db = NewDbContext();
        var response = await EventsOn(db).ChangeStatusAsync(eventId, "Postponed");

        Assert.Equal(EventWriteOutcome.ValidationFailed, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(EventStatus.Draft, (await read.Events.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>
    /// The matrix as a pure unit assertion, independent of any database. It exists so the graph can be
    /// read in one place and so a future edit to <c>AllowedTargets</c> that contradicts the documented
    /// table fails here rather than in eight scattered integration cases.
    /// </summary>
    [Fact]
    public void The_documented_transition_matrix_is_the_one_the_domain_enforces()
    {
        Assert.Equal([EventStatus.Open, EventStatus.Cancelled],
            EventStatusTransition.AllowedFrom(EventStatus.Draft));
        Assert.Equal([EventStatus.Closed, EventStatus.Cancelled],
            EventStatusTransition.AllowedFrom(EventStatus.Open));
        Assert.Empty(EventStatusTransition.AllowedFrom(EventStatus.Closed));
        Assert.Empty(EventStatusTransition.AllowedFrom(EventStatus.Cancelled));

        Assert.True(EventStatusTransition.IsTerminal(EventStatus.Closed));
        Assert.True(EventStatusTransition.IsTerminal(EventStatus.Cancelled));

        // The self-edge is satisfiable but is not a freeze. Both halves matter: the first keeps a
        // retried PATCH from 400-ing, the second keeps it from re-materializing a frozen roster.
        Assert.True(EventStatusTransition.IsAllowed(EventStatus.Closed, EventStatus.Closed));
        Assert.False(EventStatusTransition.FreezesRoster(EventStatus.Closed, EventStatus.Closed));
        Assert.True(EventStatusTransition.FreezesRoster(EventStatus.Open, EventStatus.Closed));

        // An unknown current status offers no moves rather than inventing a rule for a corrupt row.
        Assert.Empty(EventStatusTransition.AllowedFrom("Postponed"));
        Assert.False(EventStatusTransition.IsAllowed("Postponed", EventStatus.Open));
    }
}
