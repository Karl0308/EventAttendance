using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Phase 4c, D-36 — when a tap may claim to have happened.
///
/// <para>
/// <b>The rule these all serve: <c>tappedAt</c> is never silently rewritten.</b> The tempting
/// alternative was to clamp a skewed timestamp into the event's window and carry on, and it was refused
/// for the reason this codebase already applies elsewhere — <c>RecordsAnArrival</c> declines to stamp a
/// <c>CheckInAt</c> on an <c>Absent</c> row because inventing a timestamp fabricates an observation, and
/// clamping fabricates one on the single field that decides Present vs Late. So an unbelievable
/// timestamp is refused with a token that says which of the two reasons applies, and the response
/// carries <c>serverTime</c> so a drifted client can correct itself and resend.
/// </para>
///
/// <para>
/// <b>Why the two directions are not symmetric.</b> The future is bounded at five minutes because no
/// device can observe a card that has not been presented yet, and because forward skew is the direction
/// that benefits the student. The past is <em>never</em> age-limited: an old queued tap is the entire
/// point of §8.2's offline sync. What bounds the past is the event window, which bounds it by the event
/// rather than by age — which is also why a tap can be a year old and still be accepted.
/// </para>
///
/// <para>
/// These run against real SQL Server rather than in the unit suite because the per-school configuration
/// half is a §4.13 <c>SystemSettings</c> read with a global-versus-school precedence rule, and that is a
/// query, not a calculation. <see cref="EAMS.Domain.TapTimeWindow"/>'s arithmetic is exercised through
/// the service for the same reason the rest of this suite is: the interface is the contract.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TapTimeWindowTests : IntegrationTest
{
    public TapTimeWindowTests(SqlServerFixture sql) : base(sql) { }

    private const string StoredUid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

    /// <summary>
    /// One open event running from <paramref name="startAt"/> for three hours — so the default window
    /// is <c>[startAt − 60min, startAt + 4h]</c>.
    /// </summary>
    private async Task<World> ArrangeAsync(DateTime? startAt = null)
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StoredUid));
        var ev = TestData.NewEvent(school.Id, startAt: startAt ?? TestData.Now);
        db.Events.Add(ev);

        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id);
    }

    private async Task AddSettingAsync(Guid? schoolId, string key, string? value)
    {
        await using var db = NewDbContext();
        db.SystemSettings.Add(new SystemSetting
        {
            SchoolId = schoolId,
            Key = key,
            Value = value,
            DataType = "int",
            Description = "Phase 4c D-36 tap window.",
        });
        await db.SaveChangesAsync();
    }

    private async Task<TapResponse> TapAsync(Guid eventId, DateTime? tappedAt)
    {
        await using var db = NewDbContext();
        return await AttendanceOn(db).TapAsync(
            new TapRequest(eventId, StoredUid, null, null, tappedAt));
    }

    private async Task<int> RecordCountAsync()
    {
        await using var db = NewDbContext();
        return await db.AttendanceRecords.CountAsync();
    }

    // ---------------------------------------------------------------- the implausible future

    /// <summary>
    /// The one bound that is about the device's clock rather than about the event. Asserted at a value
    /// unambiguously past it rather than at the boundary, because the boundary is <em>not</em>
    /// reachable from here: the comparison is against <c>DateTime.UtcNow</c> read inside
    /// <c>TapAsync</c>, so the margin has already moved by however long the arrange step took. The
    /// exact five-minute boundary — the <c>&gt;</c>-versus-<c>&gt;=</c> that would ship silently — is
    /// pinned in <c>TapTimeWindowBoundaryTests</c>, where both operands can be supplied.
    /// </summary>
    [Fact]
    public async Task A_tap_well_in_the_future_is_rejected()
    {
        var world = await ArrangeAsync(startAt: DateTime.UtcNow.AddMinutes(-30));

        var response = await TapAsync(world.EventId, DateTime.UtcNow.AddMinutes(30));

        Assert.Equal(TapOutcome.TappedAtOutOfRange, response.Outcome);
        Assert.False(response.Result.Success);
        Assert.Null(response.Result.Record);
        Assert.Equal(0, await RecordCountAsync());
    }

    /// <summary>
    /// Inside the tolerance, so it records — and the stored instant is the <em>claimed</em> one, not the
    /// server's. That second assertion is the whole of "we never rewrite tappedAt": a implementation
    /// that quietly substituted <c>DateTime.UtcNow</c> for anything it found suspicious would pass every
    /// rejection test in this file and fail only here.
    /// </summary>
    [Fact]
    public async Task A_tap_just_inside_the_future_tolerance_is_accepted_unmodified()
    {
        var world = await ArrangeAsync(startAt: DateTime.UtcNow.AddMinutes(-30));
        var claimed = DateTime.UtcNow.AddMinutes(TapTimeWindow.FutureToleranceMinutes - 1);

        var response = await TapAsync(world.EventId, claimed);

        Assert.Equal(TapOutcome.Recorded, response.Outcome);
        Assert.Equal(claimed, response.Result.Record!.CheckInAt);
    }

    /// <summary>
    /// A rejected future tap still carries <c>serverTime</c>, and that is not decoration: this is
    /// precisely the response whose reader has been told its clock is wrong, so it is the one response
    /// that must say what the right one is. Without it the client's only recovery is to guess.
    /// </summary>
    [Fact]
    public async Task A_rejected_future_tap_still_reports_the_server_clock()
    {
        var world = await ArrangeAsync(startAt: DateTime.UtcNow.AddMinutes(-30));
        var before = DateTime.UtcNow;

        var response = await TapAsync(world.EventId, DateTime.UtcNow.AddHours(8));

        Assert.Equal(TapOutcome.TappedAtOutOfRange, response.Outcome);
        Assert.InRange(response.Result.ServerTime, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    // ---------------------------------------------------------------- the past is not age-limited

    /// <summary>
    /// The assertion that keeps offline sync possible. A tap a year old is accepted without hesitation
    /// as long as it names a time inside its own event's window — the queue exists exactly so that a
    /// device out of contact for a long time can still deliver what it saw. Any rule of the form
    /// "nothing older than N" would discard the evidence the queue was built to preserve, and this is
    /// what would fail if one were added.
    /// </summary>
    [Fact]
    public async Task A_tap_from_a_year_ago_inside_its_events_window_is_accepted()
    {
        var longAgo = DateTime.UtcNow.AddYears(-1);
        var world = await ArrangeAsync(startAt: longAgo);

        var response = await TapAsync(world.EventId, longAgo.AddMinutes(5));

        Assert.Equal(TapOutcome.Recorded, response.Outcome);
        Assert.Equal(longAgo.AddMinutes(5), response.Result.Record!.CheckInAt);
    }

    // ---------------------------------------------------------------- the event window

    [Fact]
    public async Task A_tap_before_the_window_opens_is_rejected()
    {
        var world = await ArrangeAsync();

        var response = await TapAsync(
            world.EventId,
            TestData.Now.AddMinutes(-TapTimeWindow.DefaultBeforeStartMinutes).AddMinutes(-1));

        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, response.Outcome);
        Assert.Equal(0, await RecordCountAsync());
    }

    /// <summary>
    /// The boundary is inclusive at both ends, pinned for the reason the grace-deadline boundary is:
    /// "60 minutes either side" reads as a closed interval to everyone who is not writing the
    /// comparison, and an off-by-one here refuses a tap the published contract promises to accept.
    /// </summary>
    [Fact]
    public async Task A_tap_exactly_on_the_opening_boundary_is_accepted()
    {
        var world = await ArrangeAsync();

        var response = await TapAsync(
            world.EventId, TestData.Now.AddMinutes(-TapTimeWindow.DefaultBeforeStartMinutes));

        Assert.Equal(TapOutcome.Recorded, response.Outcome);
    }

    /// <summary>
    /// The closing boundary, measured from <c>EndAt</c> — three hours after <c>StartAt</c> in this
    /// fixture. Asserted as a pair with the rejection a minute later so the test cannot pass by the
    /// window being unbounded on that side.
    /// </summary>
    /// <remarks>
    /// The second tap runs against the same world, and the row the first one wrote does not get in the
    /// way: the window is validated before the card is resolved and long before the event/student
    /// guard, so a tap past the boundary is refused for its timestamp rather than reported as
    /// <c>AlreadyRecorded</c>. That ordering is itself pinned, below.
    /// </remarks>
    [Fact]
    public async Task A_tap_exactly_on_the_closing_boundary_is_accepted_and_a_minute_later_is_not()
    {
        var world = await ArrangeAsync();
        var closes = TestData.Now.AddHours(3).AddMinutes(TapTimeWindow.DefaultAfterEndMinutes);

        var onTheBoundary = await TapAsync(world.EventId, closes);
        Assert.Equal(TapOutcome.Recorded, onTheBoundary.Outcome);

        var past = await TapAsync(world.EventId, closes.AddMinutes(1));
        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, past.Outcome);
    }

    /// <summary>
    /// <b>The bypass this closes.</b> <c>tappedAt: null</c> means "now, on the server", and it is
    /// checked against the window exactly like a client-supplied value. Exempting it would have made
    /// omitting one field a way around the whole mechanism — the first thing anyone holding a guessed
    /// card UID would try, and a card UID is a short serial running in near-sequential blocks.
    /// </summary>
    [Fact]
    public async Task A_server_stamped_tap_outside_the_window_is_rejected_too()
    {
        var world = await ArrangeAsync(startAt: DateTime.UtcNow.AddDays(-3));

        var response = await TapAsync(world.EventId, tappedAt: null);

        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, response.Outcome);
        Assert.Equal(0, await RecordCountAsync());
    }

    /// <summary>
    /// The control for the test above: with the event actually running, a server-stamped tap records.
    /// Without it, "server-stamped taps are checked" would pass identically on a build that refused
    /// every one of them.
    /// </summary>
    [Fact]
    public async Task A_server_stamped_tap_inside_the_window_is_recorded()
    {
        var world = await ArrangeAsync(startAt: DateTime.UtcNow.AddMinutes(-30));

        var response = await TapAsync(world.EventId, tappedAt: null);

        Assert.Equal(TapOutcome.Recorded, response.Outcome);
        Assert.Equal(1, await RecordCountAsync());
    }

    /// <summary>
    /// Ordering, pinned for the same reason <c>Event_state_is_reported_before_card_resolution</c> is:
    /// a client with two problems must be told about the one it can act on, consistently, and a silent
    /// reordering would change which error an operator sees. Device mismatch first, then event state,
    /// then the timestamp, then the card.
    ///
    /// <para>
    /// <b>The <c>DeviceMismatch</c> rung is the one that is load-bearing beyond this test.</b> That
    /// guard sits ahead of the event read — <c>AttendanceService</c>'s own remarks contemplate it
    /// moving — and two tests in this suite currently reach their assertion <em>because</em> it does:
    /// <c>KnownDefectTests.A_tap_from_an_unregistered_device_is_not_a_500</c> and
    /// <c>DeviceAuthenticationTests.The_service_reports_a_device_mismatch_as_its_own_outcome</c>. Both
    /// were given timestamps inside their event's window so they no longer depend on it, and the
    /// dependency is asserted here rather than assumed anywhere. A refusal that arrives before the
    /// event is even read is also the right behaviour on its own terms: a client whose body contradicts
    /// its own key has a bug that no amount of correct event id or timestamp makes acceptable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_device_mismatch_is_reported_before_the_event_the_timestamp_or_the_card()
    {
        var world = await ArrangeAsync();

        // Authenticated as one device, claiming another in the body — plus an unknown event, a
        // five-year-out timestamp and an unknown card, none of which may be what gets reported.
        Guid authenticatedDeviceId;
        await using (var db = NewDbContext())
        {
            var device = TestData.NewDevice(world.SchoolId, "MISMATCH-DEVICE");
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            authenticatedDeviceId = device.Id;
        }

        Device.DeviceId = authenticatedDeviceId;

        await using (var db = NewDbContext())
        {
            var response = await AttendanceOn(db).TapAsync(new TapRequest(
                Guid.NewGuid(), "DEADBEEF", Guid.NewGuid(), null, TestData.Now.AddYears(5)));

            Assert.Equal(TapOutcome.DeviceMismatch, response.Outcome);
        }

        // The control: with the body agreeing with the principal, the same request falls through to
        // the next rung. Without this, "mismatch wins" would pass on a build that returned
        // DeviceMismatch for everything.
        await using (var db = NewDbContext())
        {
            var response = await AttendanceOn(db).TapAsync(new TapRequest(
                Guid.NewGuid(), "DEADBEEF", authenticatedDeviceId, null, TestData.Now.AddYears(5)));

            Assert.Equal(TapOutcome.EventNotFound, response.Outcome);
        }
    }

    /// <inheritdoc cref="A_device_mismatch_is_reported_before_the_event_the_timestamp_or_the_card"/>
    [Fact]
    public async Task Event_state_is_reported_before_the_timestamp_and_the_timestamp_before_the_card()
    {
        var world = await ArrangeAsync();

        Guid closedEventId;
        await using (var db = NewDbContext())
        {
            var closed = TestData.NewEvent(world.SchoolId, EventStatus.Closed, startAt: TestData.Now);
            db.Events.Add(closed);
            await db.SaveChangesAsync();
            closedEventId = closed.Id;
        }

        // Three things wrong at once — a closed event, a timestamp five years out, an unknown card —
        // and the event is what gets reported.
        await using (var db = NewDbContext())
        {
            var onAClosedEvent = await AttendanceOn(db).TapAsync(new TapRequest(
                closedEventId, "DEADBEEF", null, null, TestData.Now.AddYears(5)));
            Assert.Equal(TapOutcome.EventNotOpen, onAClosedEvent.Outcome);
        }

        // Two left, on an open event, and now the timestamp is what gets reported rather than the card.
        await using (var db = NewDbContext())
        {
            var withABadTimeAndABadCard = await AttendanceOn(db).TapAsync(new TapRequest(
                world.EventId, "DEADBEEF", null, null, TestData.Now.AddYears(5)));
            Assert.Equal(TapOutcome.TappedAtOutOfRange, withABadTimeAndABadCard.Outcome);
        }

        // And with the timestamp fixed, the card finally surfaces — which is what makes the two
        // assertions above statements about ordering rather than about which check exists.
        await using (var last = NewDbContext())
        {
            var withOnlyABadCard = await AttendanceOn(last).TapAsync(new TapRequest(
                world.EventId, "DEADBEEF", null, null, TestData.Now));
            Assert.Equal(TapOutcome.CardNotFound, withOnlyABadCard.Outcome);
        }
    }

    // ---------------------------------------------------------------- §4.13 configuration

    /// <summary>
    /// A school that runs long queues widens its window and the tap that was refused a moment ago is
    /// accepted. This is the assertion that proves the settings are read at all — the defaults would
    /// give the opposite answer.
    /// </summary>
    [Fact]
    public async Task A_school_can_widen_its_window()
    {
        var world = await ArrangeAsync();
        var late = TestData.Now.AddHours(3).AddMinutes(90);

        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, (await TapAsync(world.EventId, late)).Outcome);

        await AddSettingAsync(world.SchoolId, TapTimeWindow.AfterEndMinutesSettingKey, "120");

        Assert.Equal(TapOutcome.Recorded, (await TapAsync(world.EventId, late)).Outcome);
    }

    /// <summary>
    /// <b>The direction that makes the settings read unconditional.</b> Checking the defaults first and
    /// only loading the configuration when that check <em>failed</em> would cost nothing on the happy
    /// path and would silently ignore this case — a school tightening a rule, which is the direction an
    /// administrator is most likely to move it. This is the test that would fail under that
    /// optimization.
    /// </summary>
    [Fact]
    public async Task A_school_can_narrow_its_window()
    {
        var world = await ArrangeAsync();
        var early = TestData.Now.AddMinutes(-45);

        Assert.Equal(TapOutcome.Recorded, (await TapAsync(world.EventId, early)).Outcome);

        await AddSettingAsync(world.SchoolId, TapTimeWindow.BeforeStartMinutesSettingKey, "15");

        // The same instant, the same event, the same student — and now refused. The row written by the
        // first tap cannot account for the difference: it would produce AlreadyRecorded, and the window
        // is checked before anything reads it.
        Assert.Equal(
            TapOutcome.TappedAtOutsideEventWindow,
            (await TapAsync(world.EventId, early)).Outcome);
    }

    /// <summary>
    /// §4.13's "a NULL <c>SchoolId</c> is the global scope", read as a default rather than as an
    /// override: a global row applies to a school that has none of its own.
    /// </summary>
    [Fact]
    public async Task A_global_setting_applies_to_a_school_with_no_row_of_its_own()
    {
        var world = await ArrangeAsync();
        await AddSettingAsync(null, TapTimeWindow.AfterEndMinutesSettingKey, "180");

        var response = await TapAsync(world.EventId, TestData.Now.AddHours(3).AddMinutes(150));

        Assert.Equal(TapOutcome.Recorded, response.Outcome);
    }

    /// <summary>
    /// …and the school's own row beats it. The other reading — global as an override — would make a
    /// per-school setting unwritable in practice, which is the opposite of what §4.13 is for.
    /// </summary>
    [Fact]
    public async Task A_school_row_beats_the_global_one()
    {
        var world = await ArrangeAsync();
        await AddSettingAsync(null, TapTimeWindow.AfterEndMinutesSettingKey, "180");
        await AddSettingAsync(world.SchoolId, TapTimeWindow.AfterEndMinutesSettingKey, "0");

        var response = await TapAsync(world.EventId, TestData.Now.AddHours(3).AddMinutes(30));

        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, response.Outcome);
    }

    /// <summary>
    /// Only one of the two keys is configured, so the other must still be the published default rather
    /// than zero or whatever the configured one said.
    /// </summary>
    [Fact]
    public async Task Configuring_one_bound_leaves_the_other_at_its_default()
    {
        var world = await ArrangeAsync();
        await AddSettingAsync(world.SchoolId, TapTimeWindow.AfterEndMinutesSettingKey, "0");

        var beforeStart = await TapAsync(
            world.EventId, TestData.Now.AddMinutes(-TapTimeWindow.DefaultBeforeStartMinutes));

        Assert.Equal(TapOutcome.Recorded, beforeStart.Outcome);
    }

    /// <summary>
    /// A settings row nobody can read as a number falls back to the published default <em>and says so</em>.
    ///
    /// <para>
    /// Both halves are load-bearing. Throwing would let one typo in one row stop every tap on a campus,
    /// which is a far worse failure than a window that is 60 minutes when someone meant 90. But a silent
    /// fallback would make a mis-typed setting indistinguishable from a working one — the class of
    /// defect that surfaces from an attendance report six weeks later, if at all. The
    /// <see cref="CapturingLogger{T}"/> is what turns "we log it" from a claim into an assertion.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ninety")]
    [InlineData("-30")]
    [InlineData("90.5")]
    [InlineData("")]
    public async Task An_unreadable_window_setting_falls_back_to_the_default_and_is_logged(string value)
    {
        var world = await ArrangeAsync();
        await AddSettingAsync(world.SchoolId, TapTimeWindow.AfterEndMinutesSettingKey, value);

        var logger = new CapturingLogger<EAMS.Infrastructure.Services.AttendanceService>();

        TapResponse insideTheDefault, outsideIt;
        await using (var db = NewDbContext())
            insideTheDefault = await AttendanceOn(db, logger).TapAsync(new TapRequest(
                world.EventId, StoredUid, null, null, TestData.Now.AddHours(3).AddMinutes(30)));

        await using (var db = NewDbContext())
            outsideIt = await AttendanceOn(db, logger).TapAsync(new TapRequest(
                world.EventId, StoredUid, null, null, TestData.Now.AddHours(3).AddMinutes(90)));

        // Inside the 60-minute default, so it records; outside it, so it is refused — which is the
        // assertion that proves the unreadable value was not applied. Had "ninety" been honoured, the
        // second tap would have been inside the window and would have come back AlreadyRecorded.
        Assert.Equal(TapOutcome.Recorded, insideTheDefault.Outcome);
        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, outsideIt.Outcome);

        var warnings = logger.At(LogLevel.Warning);
        Assert.NotEmpty(warnings);
        Assert.All(warnings, w => Assert.Contains(
            TapTimeWindow.AfterEndMinutesSettingKey, w.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>An unreadable school row falls through to the global row, not to the published default.</b>
    ///
    /// <para>
    /// The scenario, which is the whole reason this is a test and not a tidy-up: a campus sets a global
    /// <c>afterEndMinutes</c> of 10 to tighten capture, one school's override is typed <c>"ninty"</c>,
    /// and the version of this code that went to review gave that school <b>60</b> — six times wider
    /// than the rule an administrator deliberately narrowed, silently, in the permissive direction. The
    /// unconditional settings read exists to stop exactly that class of failure; taking the published
    /// constant on a parse failure reintroduced it one layer down. Precedence has to survive a
    /// malformed row or it is not precedence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unreadable_school_row_falls_through_to_the_global_row()
    {
        var world = await ArrangeAsync();
        await AddSettingAsync(null, TapTimeWindow.AfterEndMinutesSettingKey, "10");
        await AddSettingAsync(world.SchoolId, TapTimeWindow.AfterEndMinutesSettingKey, "ninty");

        // Inside the global 10 minutes, so it records under either reading — the control that keeps
        // the assertion below from passing on a build that refuses everything.
        Assert.Equal(
            TapOutcome.Recorded,
            (await TapAsync(world.EventId, TestData.Now.AddHours(3).AddMinutes(5))).Outcome);

        // Outside the global 10 and inside the published default 60. Refused, because the global row
        // is what applies — accepted, under the bug.
        Assert.Equal(
            TapOutcome.TappedAtOutsideEventWindow,
            (await TapAsync(world.EventId, TestData.Now.AddHours(3).AddMinutes(30))).Outcome);
    }

    /// <summary>
    /// Both scopes unreadable, so the published default is what is left — and both are reported, not
    /// just the last one. "The school row is wrong and we used the global one" and "both are wrong and
    /// we used the default" are different operational situations and the log has to separate them.
    /// </summary>
    [Fact]
    public async Task Two_unreadable_rows_fall_through_to_the_default_and_both_are_logged()
    {
        var world = await ArrangeAsync();
        await AddSettingAsync(null, TapTimeWindow.AfterEndMinutesSettingKey, "ten");
        await AddSettingAsync(world.SchoolId, TapTimeWindow.AfterEndMinutesSettingKey, "ninty");

        var logger = new CapturingLogger<EAMS.Infrastructure.Services.AttendanceService>();

        await using (var db = NewDbContext())
        {
            var response = await AttendanceOn(db, logger).TapAsync(new TapRequest(
                world.EventId, StoredUid, null, null, TestData.Now.AddHours(3).AddMinutes(30)));

            Assert.Equal(TapOutcome.Recorded, response.Outcome);
        }

        var warnings = logger.At(LogLevel.Warning);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("ninty", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("ten", StringComparison.Ordinal));
    }

    /// <summary>
    /// A settings row belonging to <em>another</em> school must not move this school's window. The
    /// resolution filters on <c>SchoolId</c> explicitly rather than relying on the §11 query filter,
    /// which is a no-op on this path — the service is called directly here, exactly as the §10 import
    /// path and any future worker call it.
    /// </summary>
    [Fact]
    public async Task Another_schools_setting_does_not_move_this_schools_window()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool("CICSS");
            db.Schools.Add(other);
            await db.SaveChangesAsync();
            db.SystemSettings.Add(new SystemSetting
            {
                SchoolId = other.Id,
                Key = TapTimeWindow.AfterEndMinutesSettingKey,
                Value = "600",
            });
            await db.SaveChangesAsync();
        }

        var response = await TapAsync(world.EventId, TestData.Now.AddHours(3).AddMinutes(90));

        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, response.Outcome);
    }
}
