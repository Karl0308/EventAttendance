using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// P7 — the two changes to the core tap decision, against real SQL Server.
///
/// <para>
/// <b>Task 7 (client QA #470 B6):</b> the same card cannot count twice within the minimum tap interval,
/// enforced on the server, per card; the student sees nothing (<c>TooSoonIgnored</c>, HTTP 200).
/// <b>Task 9 (client QA #472 Q5):</b> in <c>TimeInOut</c>, every later accepted tap moves
/// <c>CheckOutAt</c> forward — the last one is the time out — and nothing ever moves it back.
/// </para>
///
/// <para>
/// Every interval is taken from <see cref="TapInterval"/>, never written as a literal. The races use the
/// same <see cref="TaskCompletionSource"/> gate as <c>TapFlowTests</c>, for the reason recorded there:
/// without it the tasks start one after another and the race never happens.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TapIntervalFlowTests : IntegrationTest
{
    public TapIntervalFlowTests(SqlServerFixture sql) : base(sql) { }

    private const string StoredUid = "04A7B8C9";
    private const string OtherUid = "04F7081A";
    private const string BatchRoute = "/api/v1/attendance/tap/batch";

    private static readonly TimeSpan Interval = TapInterval.Default;
    private static readonly TimeSpan JustUnder = Interval - TimeSpan.FromMilliseconds(1);

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

    private async Task<World> ArrangeAsync(string attendanceMode = AttendanceMode.TimeInOut)
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
        db.Schools.Add(school);

        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StoredUid));

        var ev = TestData.NewEvent(school.Id, EventStatus.Open, attendanceMode, startAt: TestData.Now);
        db.Events.Add(ev);

        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id);
    }

    private async Task<Guid> AddStudentWithCardAsync(Guid schoolId, string studentNumber, string cardUid)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, studentNumber, lastName: "Flores");
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(schoolId, student.Id, cardUid));
        await db.SaveChangesAsync();
        return student.Id;
    }

    private async Task<Guid> AddDeviceAsync(Guid schoolId, string name)
    {
        await using var db = NewDbContext();
        var device = TestData.NewDevice(schoolId, name);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task AddSettingAsync(Guid? schoolId, string key, string value)
    {
        await using var db = NewDbContext();
        db.SystemSettings.Add(new SystemSetting
        {
            SchoolId = schoolId, Key = key, Value = value, DataType = "int",
            Description = "P7 B6 minimum tap interval.",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Every tap goes through here, so the code-equals-outcome invariant runs on each response.</summary>
    private async Task<TapResponse> TapAsync(TapRequest request)
    {
        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(request);
        Assert.Equal(response.Outcome.ToString(), response.Result.Code);
        return response;
    }

    private Task<TapResponse> TapAsync(Guid eventId, string? tapId, DateTime tappedAt, Guid? deviceId = null) =>
        TapAsync(new TapRequest(eventId, StoredUid, deviceId, tapId, tappedAt));

    private async Task<AttendanceRecord> OnlyRecordAsync()
    {
        await using var db = NewDbContext();
        return Assert.Single(await db.AttendanceRecords.AsNoTracking().ToListAsync());
    }

    private async Task<List<AuditLog>> AuditRowsAsync(string action)
    {
        await using var db = NewDbContext();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action).ToListAsync();
    }

    private static void AssertSuppressed(TapResponse response, Guid recordId)
    {
        Assert.Equal(TapOutcome.TooSoonIgnored, response.Outcome);
        Assert.True(response.Result.Success);
        Assert.Equal(recordId, response.Result.Record!.Id);
    }

    // ============================================================== Task 7 — the minimum tap interval

    /// <summary>
    /// <b>The core acceptance criterion.</b> Negative control: delete the <c>IsTooSoon</c> guard in
    /// <c>AttendanceService.ApplyLaterTapAsync</c> and the second tap comes back <c>CheckedOut</c>.
    /// </summary>
    [Fact]
    public async Task In_TimeInOut_a_second_tap_within_the_interval_does_not_check_out()
    {
        var world = await ArrangeAsync();

        var checkIn = await TapAsync(world.EventId, "in-0001", TestData.Now);
        var second = await TapAsync(world.EventId, "quick-0001", TestData.Now + JustUnder);

        Assert.Equal(TapOutcome.Recorded, checkIn.Outcome);
        AssertSuppressed(second, checkIn.Result.Record!.Id);
        Assert.Null(second.Result.Record!.CheckOutAt);

        var record = await OnlyRecordAsync();
        Assert.Null(record.CheckOutAt);
        Assert.Null(record.CheckOutDeviceTapId);
        Assert.Equal(TestData.Now, record.CheckInAt);
    }

    [Fact]
    public async Task In_TimeInOut_a_tap_at_the_interval_after_check_in_checks_out()
    {
        var world = await ArrangeAsync();

        await TapAsync(world.EventId, "in-0001", TestData.Now);
        var second = await TapAsync(world.EventId, "out-0001", TestData.Now + Interval);

        Assert.Equal(TapOutcome.CheckedOut, second.Outcome);
        Assert.Equal(TestData.Now + Interval, (await OnlyRecordAsync()).CheckOutAt);
    }

    [Fact]
    public async Task A_different_card_within_the_interval_is_unaffected()
    {
        var world = await ArrangeAsync();
        var otherStudentId = await AddStudentWithCardAsync(world.SchoolId, "2023-0006", OtherUid);

        await TapAsync(world.EventId, "in-a", TestData.Now);
        var other = await TapAsync(new TapRequest(
            world.EventId, OtherUid, null, "in-b", TestData.Now + TimeSpan.FromMilliseconds(1)));

        Assert.Equal(TapOutcome.Recorded, other.Outcome);
        Assert.Equal(otherStudentId, other.Result.Record!.StudentId);
        Assert.Empty(await AuditRowsAsync(ScanLog.SuppressedAction));
    }

    /// <summary>Single mode: a later tap never changes the row, so the guard never runs and the wire is unchanged.</summary>
    [Fact]
    public async Task In_Single_mode_a_quick_second_tap_is_still_AlreadyRecorded()
    {
        var world = await ArrangeAsync(AttendanceMode.Single);

        await TapAsync(world.EventId, "in-0001", TestData.Now);
        var second = await TapAsync(world.EventId, "quick-0001", TestData.Now + TimeSpan.FromMilliseconds(1));

        Assert.Equal(TapOutcome.AlreadyRecorded, second.Outcome);
        Assert.Null((await OnlyRecordAsync()).CheckOutAt);
        Assert.Empty(await AuditRowsAsync(ScanLog.SuppressedAction));
    }

    /// <summary>
    /// The replay check stays first: a retry of a counted tap is <c>DuplicateIgnored</c>, never
    /// <c>TooSoonIgnored</c> — for the check-in and for the check-out alike, each inside its own interval.
    /// </summary>
    [Fact]
    public async Task Replaying_the_check_in_tap_id_inside_the_interval_is_still_DuplicateIgnored()
    {
        var world = await ArrangeAsync();

        var checkIn = await TapAsync(world.EventId, "in-0001", TestData.Now);
        var checkInReplay = await TapAsync(world.EventId, "in-0001", TestData.Now + JustUnder);

        Assert.Equal(TapOutcome.DuplicateIgnored, checkInReplay.Outcome);
        Assert.Equal(checkIn.Result.Record!.Id, checkInReplay.Result.Record!.Id);

        var checkOutAt = TestData.Now.AddHours(1);
        await TapAsync(world.EventId, "out-0001", checkOutAt);
        var checkOutReplay = await TapAsync(world.EventId, "out-0001", checkOutAt + JustUnder);

        Assert.Equal(TapOutcome.DuplicateIgnored, checkOutReplay.Outcome);
        Assert.Equal(checkOutAt, (await OnlyRecordAsync()).CheckOutAt);
        Assert.Empty(await AuditRowsAsync(ScanLog.SuppressedAction));
    }

    /// <summary>
    /// A suppressed tap stores no tap id, so its retry is judged again — and, with nothing moved in
    /// between, gets the same answer. It never becomes attendance.
    /// </summary>
    [Fact]
    public async Task Replaying_a_suppressed_tap_returns_TooSoonIgnored_again_and_writes_no_attendance()
    {
        var world = await ArrangeAsync();

        var checkIn = await TapAsync(world.EventId, "in-0001", TestData.Now);
        var suppressed = await TapAsync(world.EventId, "quick-0001", TestData.Now + JustUnder);
        var replay = await TapAsync(world.EventId, "quick-0001", TestData.Now + JustUnder);

        AssertSuppressed(suppressed, checkIn.Result.Record!.Id);
        AssertSuppressed(replay, checkIn.Result.Record!.Id);

        var record = await OnlyRecordAsync();
        Assert.Null(record.CheckOutAt);
        Assert.Null(record.CheckOutDeviceTapId);
        Assert.Equal("in-0001", record.DeviceTapId);
    }

    /// <summary>
    /// The live poll's cursor is <c>RowVersion</c>; a suppressed tap wrote nothing, so a dashboard polling
    /// for changes must see none.
    /// </summary>
    [Fact]
    public async Task A_suppressed_tap_does_not_move_the_live_cursor()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId, "in-0001", TestData.Now);
        var before = (await OnlyRecordAsync()).RowVersion;

        await TapAsync(world.EventId, "quick-0001", TestData.Now + JustUnder);

        Assert.Equal(before, (await OnlyRecordAsync()).RowVersion);
    }

    [Fact]
    public async Task A_suppressed_tap_writes_one_suppressed_scan_audit_row_naming_its_anchor()
    {
        var world = await ArrangeAsync();
        var deviceId = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-A");
        var suppressedAt = TestData.Now + JustUnder;

        await TapAsync(world.EventId, "in-0001", TestData.Now, deviceId);
        await TapAsync(new TapRequest(world.EventId, "04:a7:b8:c9", deviceId, "quick-0001", suppressedAt));

        var row = Assert.Single(await AuditRowsAsync(ScanLog.SuppressedAction));
        Assert.Equal(ScanLog.EventEntityType, row.EntityType);
        Assert.Equal(world.EventId, row.EntityId);
        Assert.Equal(world.SchoolId, row.SchoolId);

        using var payload = JsonDocument.Parse(row.Changes!);
        var body = payload.RootElement;
        Assert.Equal(StoredUid, body.GetProperty("cardUid").GetString()); // normalized, not as scanned
        Assert.Equal("quick-0001", body.GetProperty("deviceTapId").GetString());
        Assert.Equal(deviceId, body.GetProperty("deviceId").GetGuid());
        Assert.Equal(suppressedAt, body.GetProperty("tappedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(world.StudentId, body.GetProperty("studentId").GetGuid());
        Assert.Equal(TestData.Now, body.GetProperty("anchorAt").GetDateTime().ToUniversalTime());
        Assert.Equal(nameof(TapAnchorKind.CheckIn), body.GetProperty("anchorKind").GetString());
        Assert.Equal(Interval.TotalSeconds, body.GetProperty("intervalSeconds").GetDouble());
        Assert.Equal(nameof(TapOutcome.TooSoonIgnored), body.GetProperty("serverOutcome").GetString());

        // The unresolved-scan action is untouched by a card that resolved.
        Assert.Empty(await AuditRowsAsync(ScanLog.UnresolvedAction));
    }

    /// <summary>
    /// The Unresolved Scans panel reads <see cref="ScanLog.UnresolvedAction"/> alone. Non-vacuous: a
    /// suppressed row exists in the same event's audit trail and an unresolved one is listed beside it.
    /// </summary>
    [Fact]
    public async Task Suppressed_taps_never_appear_in_the_unresolved_scan_log()
    {
        var world = await ArrangeAsync();

        await TapAsync(world.EventId, "in-0001", TestData.Now);
        await TapAsync(world.EventId, "quick-0001", TestData.Now + JustUnder);
        await TapAsync(new TapRequest(world.EventId, "DEADBEEF01", null, "unknown-0001", TestData.Now));

        Assert.Single(await AuditRowsAsync(ScanLog.SuppressedAction));

        await using var db = NewDbContext();
        var log = await EventsOn(db).GetScanLogAsync(world.EventId);

        Assert.NotNull(log);
        var scan = Assert.Single(log.Scans);
        Assert.Equal("DEADBEEF01", scan.CardUid);
        Assert.Equal(1, log.TotalScans);
    }

    /// <summary>
    /// No device term in the rule: one card on two readers inside the interval is one tap. Only as exact
    /// as the two devices' clocks agree — published in the contract handoff.
    /// </summary>
    [Fact]
    public async Task The_same_card_on_two_devices_within_the_interval_counts_once()
    {
        var world = await ArrangeAsync();
        var deviceA = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-A");
        var deviceB = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-B");

        var fromA = await TapAsync(world.EventId, "a-0001", TestData.Now, deviceA);
        var fromB = await TapAsync(world.EventId, "b-0001", TestData.Now + JustUnder, deviceB);

        Assert.Equal(TapOutcome.Recorded, fromA.Outcome);
        AssertSuppressed(fromB, fromA.Result.Record!.Id);

        var record = await OnlyRecordAsync();
        Assert.Equal(deviceA, record.DeviceId);
        Assert.Null(record.CheckOutAt);
    }

    /// <summary>
    /// The §4.13 override is read (school row honoured, here widening to ten seconds) and capped (a value
    /// above <see cref="TapInterval.MaxMinTapIntervalSeconds"/> falls through to the default, loudly).
    /// </summary>
    [Fact]
    public async Task Setting_override_is_honoured_and_capped()
    {
        const int widened = 10;
        var honoured = await ArrangeAsync();
        await AddSettingAsync(honoured.SchoolId, TapInterval.MinTapIntervalSecondsSettingKey, widened.ToString());

        await TapAsync(honoured.EventId, "in-0001", TestData.Now);
        var insideTheOverride = await TapAsync(honoured.EventId, "t-0002", TestData.Now + Interval);
        var atTheOverride = await TapAsync(honoured.EventId, "t-0003", TestData.Now.AddSeconds(widened));

        Assert.Equal(TapOutcome.TooSoonIgnored, insideTheOverride.Outcome);
        Assert.Equal(TapOutcome.CheckedOut, atTheOverride.Outcome);

        // A second school whose row is above the cap: unreadable, so the default applies and it says so.
        var capped = await ArrangeAsync();
        await AddSettingAsync(
            capped.SchoolId, TapInterval.MinTapIntervalSecondsSettingKey,
            (TapInterval.MaxMinTapIntervalSeconds * 10).ToString());

        var logger = new CapturingLogger<AttendanceService>();
        TapResponse cappedCheckOut;
        await using (var db = NewDbContext())
            await AttendanceOn(db, logger).TapAsync(new TapRequest(capped.EventId, StoredUid, null, "in-c", TestData.Now));
        await using (var db = NewDbContext())
            cappedCheckOut = await AttendanceOn(db, logger).TapAsync(
                new TapRequest(capped.EventId, StoredUid, null, "out-c", TestData.Now + Interval));

        Assert.Equal(TapOutcome.CheckedOut, cappedCheckOut.Outcome);
        Assert.Contains(
            logger.At(LogLevel.Warning),
            w => w.Message.Contains(TapInterval.MinTapIntervalSecondsSettingKey, StringComparison.Ordinal));
    }

    // =========================================================== Task 9 — last tap wins, forward only

    /// <summary>Client QA #472 Q5, verbatim: 14:05 then 14:07 → Time Out = 14:07.</summary>
    [Fact]
    public async Task In_TimeInOut_the_last_tap_is_the_time_out()
    {
        var world = await ArrangeAsync();
        var at1405 = TestData.Now.AddHours(2).AddMinutes(5);
        var at1407 = TestData.Now.AddHours(2).AddMinutes(7);

        await TapAsync(world.EventId, "in-0001", TestData.Now);
        var first = await TapAsync(world.EventId, "out-1405", at1405);
        var last = await TapAsync(world.EventId, "out-1407", at1407);

        Assert.Equal(TapOutcome.CheckedOut, first.Outcome);
        Assert.Equal(TapOutcome.CheckedOut, last.Outcome);
        Assert.Equal(at1407, last.Result.Record!.CheckOutAt);

        var record = await OnlyRecordAsync();
        Assert.Equal(at1407, record.CheckOutAt);
        Assert.Equal("out-1407", record.CheckOutDeviceTapId);
        Assert.Equal(TestData.Now, record.CheckInAt);
    }

    [Fact]
    public async Task Every_later_tap_moves_check_out_forward()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId, "in-0001", TestData.Now);

        for (var i = 1; i <= 5; i++)
        {
            var at = TestData.Now.AddMinutes(10 * i);
            var response = await TapAsync(world.EventId, $"out-{i}", at);

            Assert.Equal(TapOutcome.CheckedOut, response.Outcome);
            var record = await OnlyRecordAsync();
            Assert.Equal(at, record.CheckOutAt);
            Assert.Equal($"out-{i}", record.CheckOutDeviceTapId);
        }
    }

    /// <summary>An out-of-order offline replay, more than the interval earlier, never moves the time out back.</summary>
    [Fact]
    public async Task A_stale_tap_earlier_than_the_current_check_out_never_moves_it_back()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId, "in-0001", TestData.Now);
        await TapAsync(world.EventId, "out-late", TestData.Now.AddHours(2));

        var stale = await TapAsync(world.EventId, "out-early", TestData.Now.AddHours(1));

        Assert.Equal(TapOutcome.AlreadyRecorded, stale.Outcome);
        var record = await OnlyRecordAsync();
        Assert.Equal(TestData.Now.AddHours(2), record.CheckOutAt);
        Assert.Equal("out-late", record.CheckOutDeviceTapId);
    }

    /// <summary>
    /// <b>The published difference.</b> Last-tap-wins overwrites <c>CheckOutDeviceTapId</c>, so an
    /// intermediate check-out's id is no longer on the row: its replay is not recognised as a replay,
    /// lands as <c>AlreadyRecorded</c> (it is earlier than the current check-out), and moves nothing —
    /// not the time, not the tap id, not the live cursor.
    /// </summary>
    [Fact]
    public async Task A_replay_of_an_intermediate_check_out_tap_is_AlreadyRecorded_and_moves_nothing()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId, "in-0001", TestData.Now);
        await TapAsync(world.EventId, "out-1", TestData.Now.AddHours(1));
        await TapAsync(world.EventId, "out-2", TestData.Now.AddHours(2));
        var before = await OnlyRecordAsync();

        var replay = await TapAsync(world.EventId, "out-1", TestData.Now.AddHours(1));

        Assert.Equal(TapOutcome.AlreadyRecorded, replay.Outcome);
        var after = await OnlyRecordAsync();
        Assert.Equal(TestData.Now.AddHours(2), after.CheckOutAt);
        Assert.Equal("out-2", after.CheckOutDeviceTapId);
        Assert.Equal(before.RowVersion, after.RowVersion);
    }

    [Fact]
    public async Task A_tap_after_the_window_closes_is_rejected_and_check_out_is_unchanged()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId, "in-0001", TestData.Now);
        await TapAsync(world.EventId, "out-0001", TestData.Now.AddHours(2));

        // TestData.NewEvent ends three hours after it starts.
        var afterTheWindow = TestData.Now.AddHours(3)
            .AddMinutes(TapTimeWindow.DefaultAfterEndMinutes).AddMinutes(1);
        var late = await TapAsync(world.EventId, "out-late", afterTheWindow);

        Assert.Equal(TapOutcome.TappedAtOutsideEventWindow, late.Outcome);
        var record = await OnlyRecordAsync();
        Assert.Equal(TestData.Now.AddHours(2), record.CheckOutAt);
        Assert.Equal("out-0001", record.CheckOutDeviceTapId);
    }

    [Fact]
    public async Task A_tap_on_a_closed_event_is_rejected_and_check_out_is_unchanged()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId, "in-0001", TestData.Now);
        await TapAsync(world.EventId, "out-0001", TestData.Now.AddHours(2));

        await using (var db = NewDbContext())
        {
            var ev = await db.Events.SingleAsync(e => e.Id == world.EventId);
            ev.Status = EventStatus.Closed;
            await db.SaveChangesAsync();
        }

        var afterClose = await TapAsync(world.EventId, "out-late", TestData.Now.AddHours(2).AddMinutes(30));

        Assert.Equal(TapOutcome.EventNotOpen, afterClose.Outcome);
        var record = await OnlyRecordAsync();
        Assert.Equal(TestData.Now.AddHours(2), record.CheckOutAt);
        Assert.Equal("out-0001", record.CheckOutDeviceTapId);
    }

    // ======================================================================================= batch

    private async Task<TapBatchResponse> BatchAsync(params TapRequest[] taps)
    {
        await using var db = NewDbContext();
        return await AttendanceOn(db).TapBatchAsync(new TapBatchRequest(DateTime.UtcNow, taps));
    }

    private static TapOutcome OutcomeAt(TapBatchResponse response, int index) =>
        Assert.Single(response.Rows, r => r.Index == index).Response.Outcome;

    /// <summary>
    /// Rows are applied in <c>tappedAt</c> order, and the rule measures distance, so the earlier capture
    /// is the one that counts whichever position it holds in the array.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_with_two_taps_of_one_card_inside_the_interval_counts_the_earlier_one_whatever_the_array_order(
        bool laterFirst)
    {
        var world = await ArrangeAsync();
        var earlier = new TapRequest(world.EventId, StoredUid, null, "early", TestData.Now);
        var later = new TapRequest(world.EventId, StoredUid, null, "late", TestData.Now + JustUnder);

        var response = laterFirst ? await BatchAsync(later, earlier) : await BatchAsync(earlier, later);

        var (earlyIndex, lateIndex) = laterFirst ? (1, 0) : (0, 1);
        Assert.Equal(TapOutcome.Recorded, OutcomeAt(response, earlyIndex));
        Assert.Equal(TapOutcome.TooSoonIgnored, OutcomeAt(response, lateIndex));

        var record = await OnlyRecordAsync();
        Assert.Equal(TestData.Now, record.CheckInAt);
        Assert.Equal("early", record.DeviceTapId);
        Assert.Null(record.CheckOutAt);
    }

    /// <summary>
    /// <b>The arrival-time trap.</b> Both rows reach the server in the same request, a millisecond apart
    /// in processing — but they were captured well over the interval apart, and it is the capture time
    /// the rule measures. A rule on arrival time would swallow the check-out.
    /// </summary>
    [Fact]
    public async Task Two_taps_uploaded_together_but_captured_more_than_the_interval_apart_both_count()
    {
        var world = await ArrangeAsync();
        var checkOutAt = TestData.Now + Interval + Interval;

        var response = await BatchAsync(
            new TapRequest(world.EventId, StoredUid, null, "in-0001", TestData.Now),
            new TapRequest(world.EventId, StoredUid, null, "out-0001", checkOutAt));

        Assert.Equal(TapOutcome.Recorded, OutcomeAt(response, 0));
        Assert.Equal(TapOutcome.CheckedOut, OutcomeAt(response, 1));
        Assert.Equal(checkOutAt, (await OnlyRecordAsync()).CheckOutAt);
    }

    /// <summary>
    /// An online tap lands first; an offline reader's tap of the same card, captured just after — or just
    /// before — it, arrives later in a batch. Absolute distance means both are the same double tap.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task A_late_offline_tap_captured_within_the_interval_of_an_online_tap_is_ignored(int direction)
    {
        var world = await ArrangeAsync();
        var online = await AddDeviceAsync(world.SchoolId, "ONLINE");
        var offline = await AddDeviceAsync(world.SchoolId, "OFFLINE");

        var live = await TapAsync(world.EventId, "live-0001", TestData.Now, online);
        var response = await BatchAsync(new TapRequest(
            world.EventId, StoredUid, offline, "queued-0001", TestData.Now + direction * JustUnder));

        Assert.Equal(TapOutcome.TooSoonIgnored, OutcomeAt(response, 0));
        var record = await OnlyRecordAsync();
        Assert.Equal(live.Result.Record!.Id, record.Id);
        Assert.Equal(TestData.Now, record.CheckInAt);
        Assert.Null(record.CheckOutAt);
    }

    /// <summary>Over HTTP, because the per-row status and the accepted count are the controller's projection.</summary>
    [Fact]
    public async Task A_suppressed_batch_row_is_status_200_TooSoonIgnored_and_counted_as_accepted()
    {
        var world = await ArrangeAsync();
        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await client.PostAsJsonAsync(BatchRoute, new
        {
            clientClockAt = DateTime.UtcNow,
            taps = new[]
            {
                new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = "api-in", tappedAt = TestData.Now },
                new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = "api-quick", tappedAt = TestData.Now + JustUnder },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.GetProperty("rejected").GetInt32());

        var suppressed = body.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("index").GetInt32() == 1);
        Assert.Equal(nameof(TapOutcome.TooSoonIgnored), suppressed.GetProperty("code").GetString());
        Assert.Equal(200, suppressed.GetProperty("status").GetInt32());
        Assert.Equal(world.StudentId, suppressed.GetProperty("record").GetProperty("studentId").GetGuid());
        Assert.Equal(JsonValueKind.Null, suppressed.GetProperty("record").GetProperty("checkOutAt").ValueKind);
    }

    // ======================================================================================= races

    private async Task<TapResponse[]> RaceAsync(IEnumerable<TapRequest> requests)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = requests.Select(request => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = NewDbContext();
            return await AttendanceOn(db).TapAsync(request);
        })).ToArray();

        gate.SetResult();
        return await Task.WhenAll(attempts);
    }

    /// <summary>
    /// Eight check-out taps inside one interval of each other, racing. Exactly one lands; the others are
    /// stopped by the compare-and-set, re-read the winner, and are double taps of it. Negative control:
    /// make the check-out <c>UPDATE</c> unconditional on <c>CheckOutAt</c> and more than one
    /// <c>CheckedOut</c> appears.
    /// </summary>
    [Fact]
    public async Task Concurrent_check_out_taps_within_the_interval_record_exactly_one_check_out()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId, "in-0001", TestData.Now);

        var spacing = Interval / 16;
        var requests = Enumerable.Range(0, 8)
            .Select(i => new TapRequest(
                world.EventId, StoredUid, null, $"out-{i}", TestData.Now.AddHours(1) + i * spacing))
            .ToArray();

        var responses = await RaceAsync(requests);

        Assert.All(responses, r => Assert.True(r.Result.Success, $"{r.Outcome} / {r.Result.Message}"));
        var winnerIndex = Array.FindIndex(responses, r => r.Outcome == TapOutcome.CheckedOut);
        Assert.Single(responses, r => r.Outcome == TapOutcome.CheckedOut);
        Assert.All(
            responses.Where((_, i) => i != winnerIndex),
            r => Assert.Equal(TapOutcome.TooSoonIgnored, r.Outcome));

        var record = await OnlyRecordAsync();
        Assert.Equal(requests[winnerIndex].DeviceTapId, record.CheckOutDeviceTapId);
        Assert.Equal(requests[winnerIndex].TappedAt, record.CheckOutAt);

        // Each loser's retry gets its first answer back; nothing moved in between.
        for (var i = 0; i < requests.Length; i++)
        {
            if (i == winnerIndex) continue;
            Assert.Equal(TapOutcome.TooSoonIgnored, (await TapAsync(requests[i])).Outcome);
        }

        Assert.Equal(TapOutcome.DuplicateIgnored, (await TapAsync(requests[winnerIndex])).Outcome);
        Assert.Equal(requests[winnerIndex].DeviceTapId, (await OnlyRecordAsync()).CheckOutDeviceTapId);
    }

    /// <summary>
    /// Eight check-out taps well over the interval apart, racing. Whatever order they commit in, the
    /// time out ends on the latest capture — a lost update would leave an earlier one on the row.
    /// </summary>
    [Fact]
    public async Task Concurrent_check_outs_leave_the_latest_tappedAt_and_no_lost_update()
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId, "in-0001", TestData.Now);

        var spacing = Interval * 4;
        var requests = Enumerable.Range(0, 8)
            .Select(i => new TapRequest(
                world.EventId, StoredUid, null, $"out-{i}", TestData.Now.AddHours(1) + i * spacing))
            .ToArray();

        var responses = await RaceAsync(requests);

        Assert.All(responses, r => Assert.True(
            r.Outcome is TapOutcome.CheckedOut or TapOutcome.AlreadyRecorded, $"{r.Outcome} / {r.Result.Message}"));
        Assert.Equal(TapOutcome.CheckedOut, responses[^1].Outcome);

        var record = await OnlyRecordAsync();
        Assert.Equal(requests[^1].TappedAt, record.CheckOutAt);
        Assert.Equal(requests[^1].DeviceTapId, record.CheckOutDeviceTapId);
    }

    /// <summary>
    /// Eight first taps inside one interval, racing to create the row. One is the check-in; every insert
    /// loser is re-dispatched against it and is a double tap — none becomes a check-out.
    /// </summary>
    [Fact]
    public async Task Concurrent_first_taps_in_TimeInOut_record_one_check_in_and_no_check_out()
    {
        var world = await ArrangeAsync();

        var spacing = Interval / 16;
        var requests = Enumerable.Range(0, 8)
            .Select(i => new TapRequest(world.EventId, StoredUid, null, $"first-{i}", TestData.Now + i * spacing))
            .ToArray();

        var responses = await RaceAsync(requests);

        Assert.Single(responses, r => r.Outcome == TapOutcome.Recorded);
        Assert.All(
            responses.Where(r => r.Outcome != TapOutcome.Recorded),
            r => Assert.Equal(TapOutcome.TooSoonIgnored, r.Outcome));
        Assert.Single(responses.Select(r => r.Result.Record!.Id).Distinct());

        var record = await OnlyRecordAsync();
        Assert.Null(record.CheckOutAt);
        Assert.Null(record.CheckOutDeviceTapId);
    }

    /// <summary>
    /// Deterministic: the check-in is written from another context between this tap's read and its insert,
    /// so this tap loses the insert race on <c>UX_Attendance_Event_Student_Occurrence</c>. It was captured
    /// an hour later, so it is a check-out — which the old code returned as <c>AlreadyRecorded</c> and lost.
    /// </summary>
    [Fact]
    public async Task Insert_race_loser_beyond_the_interval_is_re_dispatched_through_the_check_out_path()
    {
        var (raced, response) = await LoseTheInsertRaceAsync(TestData.Now.AddHours(1));

        Assert.True(raced, "The race never fired, so this test proved nothing about it.");
        Assert.Equal(TapOutcome.CheckedOut, response.Outcome);

        var record = await OnlyRecordAsync();
        Assert.Equal(TestData.Now, record.CheckInAt);
        Assert.Equal("winner-in", record.DeviceTapId);
        Assert.Equal(TestData.Now.AddHours(1), record.CheckOutAt);
        Assert.Equal("loser-0001", record.CheckOutDeviceTapId);
    }

    /// <summary>The same deterministic race, with the loser inside the interval: a double tap, nothing written.</summary>
    [Fact]
    public async Task Insert_race_loser_within_the_interval_is_TooSoonIgnored()
    {
        var (raced, response) = await LoseTheInsertRaceAsync(TestData.Now + JustUnder);

        Assert.True(raced, "The race never fired, so this test proved nothing about it.");
        Assert.Equal(TapOutcome.TooSoonIgnored, response.Outcome);
        Assert.Null((await OnlyRecordAsync()).CheckOutAt);
    }

    /// <summary>
    /// <b>Found by the P7 flakiness runs.</b> A retry of a counted tap whose original commits
    /// <em>between</em> the retry's replay check (a miss, because nothing was there yet) and its read of
    /// the row (which now carries the original). Judged on the row alone it is a zero-distance double
    /// tap — <c>TooSoonIgnored</c> — which breaks "a retry is always <c>DuplicateIgnored</c>". It was
    /// caught ~1 run in 4 by the gated check-out race; this pins it deterministically by committing the
    /// original from a command interceptor at exactly that point. Negative control: remove the replay
    /// re-check before the no-write answers in <c>ApplyLaterTapAsync</c> and both cases return
    /// <c>TooSoonIgnored</c>.
    /// </summary>
    /// <remarks>
    /// The Single case was added in the P7 rework (W1): the Single-mode existing-row branch returned
    /// <c>AlreadyRecorded</c> without the re-check, so the same race answered <c>AlreadyRecorded</c> there.
    /// </remarks>
    [Theory]
    [InlineData(AttendanceMode.TimeInOut, false)] // the retried tap is the check-in
    [InlineData(AttendanceMode.TimeInOut, true)]  // the retried tap is the check-out
    [InlineData(AttendanceMode.Single, false)]    // W1: a Single event's only counted tap
    public async Task A_retry_whose_original_commits_during_the_replay_check_is_DuplicateIgnored(
        string mode, bool checkOut)
    {
        var world = await ArrangeAsync(mode);
        var tappedAt = checkOut ? TestData.Now.AddHours(1) : TestData.Now;
        var retried = new TapRequest(world.EventId, StoredUid, null, "shared-0001", tappedAt);

        if (checkOut) await TapAsync(world.EventId, "in-0001", TestData.Now);

        var original = new CommitAfterReplayCheck(async () =>
        {
            await using var other = NewDbContext();
            var first = await AttendanceOn(other).TapAsync(retried);
            Assert.Equal(checkOut ? TapOutcome.CheckedOut : TapOutcome.Recorded, first.Outcome);
        });

        await using var db = new EAMS.Infrastructure.Data.EamsDbContext(
            new DbContextOptionsBuilder<EAMS.Infrastructure.Data.EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString).AddInterceptors(original).Options,
            School);

        var retry = await AttendanceOn(db).TapAsync(retried);

        Assert.True(original.Fired, "The original never committed mid-retry, so this proved nothing.");
        Assert.Equal(TapOutcome.DuplicateIgnored, retry.Outcome);

        var record = await OnlyRecordAsync();
        Assert.Equal("shared-0001", checkOut ? record.CheckOutDeviceTapId : record.DeviceTapId);
        Assert.Empty(await AuditRowsAsync(ScanLog.SuppressedAction));
    }

    /// <summary>
    /// Runs <paramref name="commit"/> once, just before the first command that follows the replay check's
    /// <c>UNION</c> — i.e. after the retry has looked for its own tap id and found nothing, and before it
    /// reads the attendance row.
    /// </summary>
    private sealed class CommitAfterReplayCheck(Func<Task> commit)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private bool _sawReplayCheck;

        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UNION", StringComparison.Ordinal))
            {
                _sawReplayCheck = true;
            }
            else if (_sawReplayCheck && !Fired)
            {
                Fired = true;
                await commit();
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // ============================================== the rewritten write paths, deterministically (W3)

    /// <summary>
    /// Runs <paramref name="act"/> once, from another connection, immediately before the first
    /// check-out compare-and-set <c>UPDATE</c> this context sends — and records the rows every such
    /// <c>UPDATE</c> affected, so a test can prove a loss happened rather than infer it.
    /// </summary>
    private sealed class BeforeCheckOutUpdate(Func<Task> act)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public List<int> RowsAffected { get; } = [];

        private static bool IsCheckOutUpdate(System.Data.Common.DbCommand command) =>
            command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
            && command.CommandText.Contains("[CheckOutAt]", StringComparison.Ordinal);

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsCheckOutUpdate(command) && !Fired)
            {
                Fired = true;
                await act();
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsCheckOutUpdate(command)) RowsAffected.Add(result);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private EAMS.Infrastructure.Data.EamsDbContext NewInterceptedContext(
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<EAMS.Infrastructure.Data.EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString).AddInterceptors(interceptor).Options,
            School);

    private async Task MoveCheckOutAsync(Guid recordId, DateTime to, string tapId)
    {
        await using var other = NewDbContext();
        var moved = await other.AttendanceRecords.Where(a => a.Id == recordId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.CheckOutAt, to)
                .SetProperty(a => a.CheckOutDeviceTapId, tapId));
        Assert.Equal(1, moved);
    }

    /// <summary>
    /// The compare-and-set loses, deterministically: between this tap reading the row (check-out at
    /// +1h) and its <c>UPDATE</c>, another tap moves the check-out. The first <c>UPDATE</c> must affect
    /// zero rows, and the tap must then be decided against the value it reloaded — not the one it read.
    /// Each case gives a different answer that only the NEW value can produce.
    /// </summary>
    [Theory]
    // Moved to within the interval after this tap: the reload makes it a double tap. Against the old
    // value (+1h) it would have checked out.
    [InlineData(2 * 3600 + 1, "TooSoonIgnored", new[] { 0 })]
    // Moved well past this tap: the reload makes it stale. Against the old value it would have checked out.
    [InlineData(3 * 3600, "AlreadyRecorded", new[] { 0 })]
    // Moved forward but still earlier than this tap by more than the interval: compare-and-set again
    // against the NEW value, and this time it lands.
    [InlineData(3600 + 1800, "CheckedOut", new[] { 0, 1 })]
    public async Task A_check_out_that_loses_the_compare_and_set_re_decides_against_the_new_value(
        int movedToSecondsAfterStart, string expected, int[] expectedRowsAffected)
    {
        var world = await ArrangeAsync();
        await TapAsync(world.EventId, "in-0001", TestData.Now);
        await TapAsync(world.EventId, "out-0001", TestData.Now.AddHours(1));
        var recordId = (await OnlyRecordAsync()).Id;
        var movedTo = TestData.Now.AddSeconds(movedToSecondsAfterStart);

        var interceptor = new BeforeCheckOutUpdate(() => MoveCheckOutAsync(recordId, movedTo, "moved-0001"));
        await using var db = NewInterceptedContext(interceptor);

        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(world.EventId, StoredUid, null, "out-0002", TestData.Now.AddHours(2)));

        Assert.True(interceptor.Fired, "The check-out was never moved mid-tap, so this proved nothing.");
        Assert.Equal(expectedRowsAffected, interceptor.RowsAffected);
        Assert.Equal(expected, response.Result.Code);

        var record = await OnlyRecordAsync();
        if (expected == nameof(TapOutcome.CheckedOut))
        {
            Assert.Equal(TestData.Now.AddHours(2), record.CheckOutAt);
            Assert.Equal("out-0002", record.CheckOutDeviceTapId);
        }
        else
        {
            // The loser wrote nothing: the row is exactly what the other tap left, and the response
            // carries that reloaded row rather than the stale +1h the loser first read.
            Assert.Equal(movedTo, record.CheckOutAt);
            Assert.Equal("moved-0001", record.CheckOutDeviceTapId);
            Assert.Equal(movedTo, response.Result.Record!.CheckOutAt);
        }
    }

    /// <summary>
    /// <c>CheckOutAt = @seen</c> is an exact equality on a <c>datetime2(7)</c> column, so the value read
    /// back has to round-trip to the tick. Sub-second ticks on every stored value make the second and
    /// third moves depend on that: a parameter sent at <c>datetime</c> precision (1/300 s) would never
    /// equal the stored value, and every compare-and-set after the first would lose.
    /// </summary>
    [Fact]
    public async Task Check_out_moves_forward_from_a_sub_second_value()
    {
        var world = await ArrangeAsync();
        var checkIn = TestData.Now.AddTicks(1_234_567);
        var first = TestData.Now.AddHours(1).AddTicks(1_234_567);
        var second = TestData.Now.AddHours(2).AddTicks(7_654_321);
        var third = TestData.Now.AddHours(2).AddMinutes(1).AddTicks(9_999_999);

        Assert.Equal(TapOutcome.Recorded, (await TapAsync(world.EventId, "in-0001", checkIn)).Outcome);
        Assert.Equal(TapOutcome.CheckedOut, (await TapAsync(world.EventId, "out-1", first)).Outcome);
        Assert.Equal(first, (await OnlyRecordAsync()).CheckOutAt);

        Assert.Equal(TapOutcome.CheckedOut, (await TapAsync(world.EventId, "out-2", second)).Outcome);
        Assert.Equal(second, (await OnlyRecordAsync()).CheckOutAt);

        Assert.Equal(TapOutcome.CheckedOut, (await TapAsync(world.EventId, "out-3", third)).Outcome);
        var record = await OnlyRecordAsync();
        Assert.Equal(third, record.CheckOutAt);
        Assert.Equal("out-3", record.CheckOutDeviceTapId);
    }

    /// <summary>
    /// The one index the check-out <c>UPDATE</c> can violate, forced deterministically: device D is about
    /// to record check-out tap <c>out-x</c> on student A's row, and just before the <c>UPDATE</c> the same
    /// device's <c>out-x</c> lands on student B's row. <c>UX_Attendance_Device_CheckOutDeviceTapId</c>
    /// rejects A's update with 2601, which <c>ExecuteUpdateAsync</c> raises as an unwrapped
    /// <c>SqlException</c>. It must be caught and resolved as a replay of B's tap — the same answer
    /// <c>TapFlowTests.One_device_reusing_a_check_out_tap_id_for_another_student_is_a_replay</c> pins for
    /// the non-racing case — and A must not be checked out.
    /// </summary>
    [Fact]
    public async Task A_unique_violation_at_check_out_resolves_as_a_replay()
    {
        var world = await ArrangeAsync();
        var otherStudentId = await AddStudentWithCardAsync(world.SchoolId, "2023-0006", OtherUid);
        var device = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-D");

        await TapAsync(world.EventId, "in-a", TestData.Now, device);
        await TapAsync(new TapRequest(world.EventId, OtherUid, device, "in-b", TestData.Now));

        Guid otherRecordId;
        await using (var read = NewDbContext())
            otherRecordId = (await read.AttendanceRecords.AsNoTracking()
                .SingleAsync(a => a.StudentId == otherStudentId)).Id;

        var interceptor = new BeforeCheckOutUpdate(
            () => MoveCheckOutAsync(otherRecordId, TestData.Now.AddHours(1), "out-x"));
        await using var db = NewInterceptedContext(interceptor);

        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(world.EventId, StoredUid, device, "out-x", TestData.Now.AddHours(1)));

        Assert.True(interceptor.Fired, "The conflicting check-out never landed mid-tap, so this proved nothing.");
        Assert.Empty(interceptor.RowsAffected); // the UPDATE threw; it never completed
        Assert.Equal(TapOutcome.DuplicateIgnored, response.Outcome);
        Assert.Equal(otherStudentId, response.Result.Record!.StudentId);

        await using var check = NewDbContext();
        var mine = await check.AttendanceRecords.AsNoTracking().SingleAsync(a => a.StudentId == world.StudentId);
        Assert.Null(mine.CheckOutAt);
        Assert.Null(mine.CheckOutDeviceTapId);
    }

    // ====================================================== the setting end to end, and a late replay

    [Fact]
    public async Task Interval_setting_of_zero_disables_the_rule_end_to_end()
    {
        var world = await ArrangeAsync();
        await AddSettingAsync(world.SchoolId, TapInterval.MinTapIntervalSecondsSettingKey, "0");

        await TapAsync(world.EventId, "in-0001", TestData.Now);
        var immediate = await TapAsync(world.EventId, "out-0001", TestData.Now.AddTicks(1));

        Assert.Equal(TapOutcome.CheckedOut, immediate.Outcome);
        Assert.Equal(TestData.Now.AddTicks(1), (await OnlyRecordAsync()).CheckOutAt);
        Assert.Empty(await AuditRowsAsync(ScanLog.SuppressedAction));
    }

    [Fact]
    public async Task Interval_setting_at_the_cap_applies_end_to_end()
    {
        var world = await ArrangeAsync();
        await AddSettingAsync(
            world.SchoolId, TapInterval.MinTapIntervalSecondsSettingKey,
            TapInterval.MaxMinTapIntervalSeconds.ToString());
        var cap = TimeSpan.FromSeconds(TapInterval.MaxMinTapIntervalSeconds);

        var logger = new CapturingLogger<AttendanceService>();
        TapResponse inside, atTheCap;
        await using (var db = NewDbContext())
            await AttendanceOn(db, logger).TapAsync(new TapRequest(world.EventId, StoredUid, null, "in-0001", TestData.Now));
        await using (var db = NewDbContext())
            inside = await AttendanceOn(db, logger).TapAsync(
                new TapRequest(world.EventId, StoredUid, null, "t-0002", TestData.Now + cap - TimeSpan.FromSeconds(1)));
        await using (var db = NewDbContext())
            atTheCap = await AttendanceOn(db, logger).TapAsync(
                new TapRequest(world.EventId, StoredUid, null, "t-0003", TestData.Now + cap));

        Assert.Equal(TapOutcome.TooSoonIgnored, inside.Outcome);
        Assert.Equal(TapOutcome.CheckedOut, atTheCap.Outcome);
        Assert.Empty(logger.At(LogLevel.Warning)); // the cap itself is a readable value, not a fallback
    }

    /// <summary>
    /// A suppressed tap stores no tap id, so its replay is decided afresh against whatever the row is now.
    /// Here the suppressed tap was one second after the check-in and a check-out has landed since; the
    /// replay is still within the interval of the <em>check-in</em> anchor, so it is
    /// <c>TooSoonIgnored</c> again — and the check-out it did not cause is untouched.
    /// </summary>
    [Fact]
    public async Task Replaying_a_suppressed_tap_after_the_check_out_moved_changes_nothing()
    {
        var world = await ArrangeAsync();
        var quick = new TapRequest(world.EventId, StoredUid, null, "quick-0001", TestData.Now + JustUnder);

        await TapAsync(world.EventId, "in-0001", TestData.Now);
        Assert.Equal(TapOutcome.TooSoonIgnored, (await TapAsync(quick)).Outcome);
        Assert.Equal(TapOutcome.CheckedOut, (await TapAsync(world.EventId, "out-0001", TestData.Now.AddHours(1))).Outcome);
        var before = await OnlyRecordAsync();

        var replay = await TapAsync(quick);

        Assert.Equal(TapOutcome.TooSoonIgnored, replay.Outcome);
        var after = await OnlyRecordAsync();
        Assert.Equal(TestData.Now.AddHours(1), after.CheckOutAt);
        Assert.Equal("out-0001", after.CheckOutDeviceTapId);
        Assert.Equal(TestData.Now, after.CheckInAt);
        Assert.Equal(before.RowVersion, after.RowVersion);
    }

    private async Task<(bool Raced, TapResponse Response)> LoseTheInsertRaceAsync(DateTime loserTappedAt)
    {
        var world = await ArrangeAsync();

        Guid cardId;
        await using (var read = NewDbContext())
            cardId = (await read.RfidCards.AsNoTracking().SingleAsync(c => c.StudentId == world.StudentId)).Id;

        await using var db = NewDbContext();
        var raced = false;
        db.SavingChanges += (_, _) =>
        {
            if (raced) return;
            raced = true;
            using var winner = NewDbContext();
            winner.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId, EventId = world.EventId, StudentId = world.StudentId,
                RfidCardId = cardId, CheckInAt = TestData.Now, Status = AttendanceStatus.Present,
                CaptureMethod = CaptureMethod.Rfid, DeviceTapId = "winner-in",
            });
            winner.SaveChanges();
        };

        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(world.EventId, StoredUid, null, "loser-0001", loserTappedAt));

        return (raced, response);
    }
}
