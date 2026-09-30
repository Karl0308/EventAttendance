using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The per-event attendance grace periods (EventGracePeriod.docx): the three configurable periods are
/// persisted by the event write surface and enforced on the tap path. Rejections reuse the frozen
/// <see cref="TapOutcome.TappedAtOutsideEventWindow"/> token and carry the spec's prompt text.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventGracePeriodTests : IntegrationTest
{
    public EventGracePeriodTests(SqlServerFixture sql) : base(sql) { }

    private static readonly DateTime Start = TestData.Now;

    /// <summary>School + one open event carrying the given grace periods, plus a helper to add a student
    /// with a card so each tap scenario uses a fresh person (no idempotency collisions).</summary>
    private async Task<Guid> ArrangeEventAsync(int? beforeStart, int? afterEnd, int graceMinutes = 15)
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var ev = TestData.NewEvent(school.Id, "Open", "Single", graceMinutes, Start);
        ev.GraceBeforeStartMinutes = beforeStart;
        ev.GraceAfterEndMinutes = afterEnd;
        db.Events.Add(ev);

        await db.SaveChangesAsync();
        return ev.Id;
    }

    private async Task<(Guid EventId, string Uid)> AddPersonAsync(Guid eventId, string number, string uid)
    {
        await using var db = NewDbContext();
        var schoolId = await db.Events.Where(e => e.Id == eventId).Select(e => e.SchoolId).SingleAsync();
        var student = TestData.NewStudent(schoolId, number);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(schoolId, student.Id, uid));
        await db.SaveChangesAsync();
        return (eventId, uid);
    }

    private async Task<TapResponse> TapAsync(Guid eventId, string uid, DateTime tappedAt)
    {
        await using var db = NewDbContext();
        return await AttendanceOn(db).TapAsync(
            new TapRequest(eventId, uid, null, $"tap-{Guid.NewGuid():N}", tappedAt));
    }

    [Fact]
    public async Task A_tap_before_the_before_start_grace_is_rejected_as_too_early()
    {
        var eventId = await ArrangeEventAsync(beforeStart: 10, afterEnd: 10);
        await AddPersonAsync(eventId, "2023-1001", "0AAA0001");

        var result = await TapAsync(eventId, "0AAA0001", Start.AddMinutes(-11));

        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, result.Outcome);
        Assert.False(result.Result.Success);
        Assert.Contains("Too Early", result.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_tap_within_the_before_start_grace_is_recorded_present()
    {
        var eventId = await ArrangeEventAsync(beforeStart: 10, afterEnd: 10);
        await AddPersonAsync(eventId, "2023-1002", "0AAA0002");

        var result = await TapAsync(eventId, "0AAA0002", Start.AddMinutes(-5));

        Assert.Equal(TapOutcome.Recorded, result.Outcome);
        Assert.Equal(AttendanceStatus.Present, result.Result.Record!.Status);
    }

    [Fact]
    public async Task A_tap_after_the_after_end_grace_is_rejected_as_closed()
    {
        var eventId = await ArrangeEventAsync(beforeStart: 10, afterEnd: 10);
        // The event ends three hours after Start (TestData.NewEvent). afterEnd = 10, so End + 11 is closed
        // but still inside the 60-minute forgery window, so it reaches the grace check.
        await AddPersonAsync(eventId, "2023-1003", "0AAA0003");

        var end = Start.AddHours(3);
        var result = await TapAsync(eventId, "0AAA0003", end.AddMinutes(11));

        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, result.Outcome);
        Assert.False(result.Result.Success);
        Assert.Contains("Attendance Closed", result.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_grace_periods_leave_the_school_window_governing()
    {
        // No per-event limit: a tap 30 minutes early (inside the 60-minute school window) is still
        // recorded, exactly as before this feature existed.
        var eventId = await ArrangeEventAsync(beforeStart: null, afterEnd: null);
        await AddPersonAsync(eventId, "2023-1004", "0AAA0004");

        var result = await TapAsync(eventId, "0AAA0004", Start.AddMinutes(-30));

        Assert.Equal(TapOutcome.Recorded, result.Outcome);
    }

    [Fact]
    public async Task The_event_write_surface_round_trips_the_grace_periods()
    {
        await using (var arrange = NewDbContext())
        {
            var school = TestData.NewSchool();
            arrange.Schools.Add(school);
            await arrange.SaveChangesAsync();
            School.CurrentSchoolId = school.Id;
        }

        await using var scoped = NewDbContext();
        var events = EventsOn(scoped);

        var created = await events.CreateAsync(new EventWriteRequest(
            Name: "Graced Event",
            Description: null, Location: null,
            StartAt: Start, EndAt: Start.AddHours(2),
            AttendanceMode: "Single", GraceMinutes: 15, RequireRegistration: false,
            IssuesCertificates: null,
            GraceBeforeStartMinutes: 20, GraceAfterEndMinutes: 5));

        Assert.Equal(EventWriteOutcome.Saved, created.Outcome);
        Assert.Equal(20, created.Event!.GraceBeforeStartMinutes);
        Assert.Equal(5, created.Event.GraceAfterEndMinutes);

        // An out-of-range value is refused.
        var bad = await events.CreateAsync(new EventWriteRequest(
            Name: "Bad", Description: null, Location: null,
            StartAt: Start, EndAt: Start.AddHours(2),
            AttendanceMode: "Single", GraceMinutes: 15, RequireRegistration: false,
            IssuesCertificates: null, GraceBeforeStartMinutes: 99999, GraceAfterEndMinutes: null));
        Assert.Equal(EventWriteOutcome.ValidationFailed, bad.Outcome);
    }
}
