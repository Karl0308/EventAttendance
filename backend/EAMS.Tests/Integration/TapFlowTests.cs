using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>POST /attendance/tap</c> is the keystone contract: Technical Plan §8.2 publishes its
/// idempotency rules to an external mobile developer in Phase 4, at which point the behaviour
/// asserted here stops being an implementation detail and becomes an interface someone else's code
/// depends on. Everything below is written against the behaviour that actually ships, so a change
/// that breaks a mobile client fails here first.
///
/// <para>
/// These run against real SQL Server for a specific reason: the dedupe key is enforced by a unique
/// index over a <em>nullable</em> <c>DeviceId</c>, and SQL Server's NULL-equals-NULL-inside-a-unique-index
/// rule is what makes device-less taps constrained at all. No in-memory provider reproduces that, so
/// an in-memory version of this file would pass while the shipped system lost taps.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TapFlowTests : IntegrationTest
{
    public TapFlowTests(SqlServerFixture sql) : base(sql) { }

    private const string StoredUid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId, Guid CardId);

    /// <summary>One school, one open event, one student holding one active card.</summary>
    private async Task<World> ArrangeAsync(
        string eventStatus = "Open", string attendanceMode = "Single", int graceMinutes = 15,
        DateTime? startAt = null, string cardUid = StoredUid, bool cardActive = true,
        bool eventDeleted = false)
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);

        var card = TestData.NewCard(school.Id, student.Id, cardUid, cardActive);
        db.RfidCards.Add(card);

        var ev = TestData.NewEvent(school.Id, eventStatus, attendanceMode, graceMinutes, startAt);
        ev.IsDeleted = eventDeleted;
        db.Events.Add(ev);

        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id, card.Id);
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

    private async Task<TapResponse> TapAsync(TapRequest request)
    {
        await using var db = NewDbContext();
        return await AttendanceOn(db).TapAsync(request);
    }

    private async Task<List<AttendanceRecord>> AllRecordsAsync()
    {
        await using var db = NewDbContext();
        return await db.AttendanceRecords.AsNoTracking().ToListAsync();
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public async Task Replaying_a_tap_from_the_same_device_returns_the_existing_record()
    {
        var world = await ArrangeAsync();
        var deviceId = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-A");
        var tapId = Guid.NewGuid().ToString();
        var request = new TapRequest(world.EventId, StoredUid, deviceId, tapId, TestData.Now);

        var first = await TapAsync(request);
        var replay = await TapAsync(request);

        Assert.Equal(TapOutcome.Recorded, first.Outcome);
        Assert.Equal(TapOutcome.DuplicateIgnored, replay.Outcome);
        Assert.True(replay.Result.Success);
        Assert.Equal(first.Result.Record!.Id, replay.Result.Record!.Id);
        Assert.Single(await AllRecordsAsync());
    }

    /// <summary>
    /// The regression this suite exists for. Two devices flushing offline queues can emit the same
    /// client-generated tap id; a dedupe keyed on the tap id alone read device B's tap as a replay
    /// of device A's, returned <em>another student's</em> record, and never wrote the real one.
    /// Verified against the live database when it was found — the student was silently marked absent.
    ///
    /// <para>
    /// The assertion is deliberately on the returned <c>StudentId</c>, not just on the row count. A
    /// future regression that wrote a row but returned the wrong one would still be the same bug —
    /// the mobile client shows the name it is handed back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_devices_sending_the_same_deviceTapId_produce_two_separate_records()
    {
        var world = await ArrangeAsync();
        const string otherUid = "04F7081A";
        var otherStudentId = await AddStudentWithCardAsync(world.SchoolId, "2023-0006", otherUid);

        var deviceA = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-A");
        var deviceB = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-B");
        var sharedTapId = "collision-0001";

        var fromA = await TapAsync(new TapRequest(world.EventId, StoredUid, deviceA, sharedTapId, TestData.Now));
        var fromB = await TapAsync(new TapRequest(world.EventId, otherUid, deviceB, sharedTapId, TestData.Now));

        Assert.Equal(TapOutcome.Recorded, fromA.Outcome);
        Assert.Equal(TapOutcome.Recorded, fromB.Outcome);
        Assert.Equal(world.StudentId, fromA.Result.Record!.StudentId);
        Assert.Equal(otherStudentId, fromB.Result.Record!.StudentId);

        var records = await AllRecordsAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(
            new[] { otherStudentId, world.StudentId }.OrderBy(id => id),
            records.Select(r => r.StudentId).OrderBy(id => id));
    }

    /// <summary>
    /// The documented flip side of the pair key: <em>one</em> device reusing a tap id is a replay,
    /// whatever card it claims. §8.2 makes the tap id a client-generated UUID, so this can only
    /// happen if a client reuses one — and returning the first record is the safe reading, because
    /// the alternative (writing a second row) would double-count a retried tap.
    /// </summary>
    [Fact]
    public async Task One_device_reusing_a_tap_id_for_another_card_is_treated_as_a_replay()
    {
        var world = await ArrangeAsync();
        const string otherUid = "04F7081A";
        await AddStudentWithCardAsync(world.SchoolId, "2023-0006", otherUid);
        var deviceId = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-A");
        const string tapId = "reused-0001";

        var first = await TapAsync(new TapRequest(world.EventId, StoredUid, deviceId, tapId, TestData.Now));
        var second = await TapAsync(new TapRequest(world.EventId, otherUid, deviceId, tapId, TestData.Now));

        Assert.Equal(TapOutcome.DuplicateIgnored, second.Outcome);
        Assert.Equal(first.Result.Record!.Id, second.Result.Record!.Id);
        Assert.Single(await AllRecordsAsync());
    }

    /// <summary>
    /// The mock flow and any pre-registration mobile client send no <c>DeviceId</c>. Dedupe still
    /// has to work, and it does because SQL Server compares NULL as equal inside a unique index and
    /// EF compiles <c>DeviceId == null</c> to <c>IS NULL</c> — query and constraint select the same
    /// bucket. This is the assertion that would silently pass on a provider with PostgreSQL-style
    /// NULL semantics while the shipped system wrote duplicates.
    /// </summary>
    [Fact]
    public async Task A_device_less_tap_still_dedupes_on_replay()
    {
        var world = await ArrangeAsync();
        var request = new TapRequest(world.EventId, StoredUid, null, "deviceless-0001", TestData.Now);

        var first = await TapAsync(request);
        var replay = await TapAsync(request);

        Assert.Equal(TapOutcome.Recorded, first.Outcome);
        Assert.Equal(TapOutcome.DuplicateIgnored, replay.Outcome);
        Assert.Equal(first.Result.Record!.Id, replay.Result.Record!.Id);
        Assert.Single(await AllRecordsAsync());
    }

    /// <summary>
    /// The null device is one bucket among devices, not a wildcard: a registered device reusing a
    /// device-less tap id must not be read as a replay of it. Same failure shape as the two-device
    /// bug, reached from the other side.
    /// </summary>
    [Fact]
    public async Task A_registered_device_does_not_replay_a_device_less_tap_id()
    {
        var world = await ArrangeAsync();
        const string otherUid = "04F7081A";
        var otherStudentId = await AddStudentWithCardAsync(world.SchoolId, "2023-0006", otherUid);
        var deviceId = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-A");
        const string tapId = "shared-with-null-device";

        var deviceless = await TapAsync(new TapRequest(world.EventId, StoredUid, null, tapId, TestData.Now));
        var registered = await TapAsync(new TapRequest(world.EventId, otherUid, deviceId, tapId, TestData.Now));

        Assert.Equal(TapOutcome.Recorded, deviceless.Outcome);
        Assert.Equal(TapOutcome.Recorded, registered.Outcome);
        Assert.Equal(otherStudentId, registered.Result.Record!.StudentId);
        Assert.Equal(2, (await AllRecordsAsync()).Count);
    }

    [Fact]
    public async Task A_tap_without_a_tap_id_falls_back_to_the_event_student_guard()
    {
        var world = await ArrangeAsync();
        var request = new TapRequest(world.EventId, StoredUid, null, null, TestData.Now);

        var first = await TapAsync(request);
        var second = await TapAsync(request);

        Assert.Equal(TapOutcome.Recorded, first.Outcome);
        Assert.Equal(TapOutcome.AlreadyRecorded, second.Outcome);
        Assert.Single(await AllRecordsAsync());
    }

    // ---------------------------------------------------------------- concurrency

    /// <summary>
    /// Two taps of one card milliseconds apart is the ordinary case, and §8.2's retry-on-timeout
    /// produces it deliberately. Both idempotency checks in the service are reads, so the window
    /// between them and the insert is real; what must never happen is a 500 or a second row.
    ///
    /// <para>
    /// The <see cref="TaskCompletionSource"/> gate is load-bearing. Without it the tasks start in
    /// sequence as the enumerable is materialized, the first one wins comfortably, and the test
    /// passes without the race ever occurring — a green test that proves nothing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]   // one retried tap: the (DeviceId, DeviceTapId) index rejects the losers
    [InlineData(false)]  // distinct taps of one card: the (Event, Student, Occurrence) index does
    public async Task Concurrent_taps_for_one_student_all_succeed_and_write_exactly_one_record(
        bool sameTapId)
    {
        var world = await ArrangeAsync();
        var sharedTapId = Guid.NewGuid().ToString();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = NewDbContext();
            return await AttendanceOn(db).TapAsync(new TapRequest(
                world.EventId, StoredUid, null,
                sameTapId ? sharedTapId : $"tap-{i}", TestData.Now));
        })).ToArray();

        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        Assert.All(responses, r => Assert.True(
            r.Result.Success, $"A concurrent tap failed with: {r.Outcome} / {r.Result.Message}"));
        Assert.All(responses, r => Assert.NotNull(r.Result.Record));
        Assert.Single(await AllRecordsAsync());
        Assert.Single(responses.Select(r => r.Result.Record!.Id).Distinct());
    }

    // ---------------------------------------------------------------- grace period

    [Fact]
    public async Task A_tap_inside_the_grace_window_is_Present()
    {
        var world = await ArrangeAsync(graceMinutes: 15, startAt: TestData.Now);

        var response = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, null, TestData.Now.AddMinutes(14)));

        Assert.Equal(TapOutcome.Recorded, response.Outcome);
        Assert.Equal("Present", response.Result.Record!.Status);
    }

    /// <summary>
    /// The boundary is inclusive (<c>when &lt;= StartAt + GraceMinutes</c>). Pinned explicitly
    /// because an off-by-one here is invisible in review and marks a punctual student late.
    /// </summary>
    [Fact]
    public async Task A_tap_exactly_on_the_grace_deadline_is_Present()
    {
        var world = await ArrangeAsync(graceMinutes: 15, startAt: TestData.Now);

        var response = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, null, TestData.Now.AddMinutes(15)));

        Assert.Equal("Present", response.Result.Record!.Status);
    }

    [Fact]
    public async Task A_tap_one_tick_past_the_grace_deadline_is_Late()
    {
        var world = await ArrangeAsync(graceMinutes: 15, startAt: TestData.Now);

        var response = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, null, TestData.Now.AddMinutes(15).AddTicks(1)));

        Assert.Equal("Late", response.Result.Record!.Status);
    }

    [Fact]
    public async Task A_tap_well_after_the_grace_deadline_is_Late()
    {
        var world = await ArrangeAsync(graceMinutes: 15, startAt: TestData.Now);

        var response = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, null, TestData.Now.AddHours(1)));

        Assert.Equal("Late", response.Result.Record!.Status);
    }

    [Fact]
    public async Task With_no_grace_configured_a_tap_after_the_start_is_Late()
    {
        var world = await ArrangeAsync(graceMinutes: 0, startAt: TestData.Now);

        var response = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, null, TestData.Now.AddSeconds(1)));

        Assert.Equal("Late", response.Result.Record!.Status);
    }

    /// <summary>
    /// A <c>"+08:00"</c> payload deserializes to <see cref="DateTimeKind.Local"/>. The service must
    /// convert it before comparing against <c>StartAt</c>, or a Manila tap is judged eight hours out.
    ///
    /// <para>
    /// The assertion is on the stored instant rather than on Present/Late, because Present/Late only
    /// distinguishes the two behaviours on a machine whose offset is non-zero — CI runs in UTC,
    /// where <c>Local</c> and <c>Utc</c> coincide and any status assertion would be vacuous.
    /// Instant equality is meaningful on both.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tap_timestamped_in_local_time_is_stored_as_the_same_utc_instant()
    {
        var world = await ArrangeAsync(graceMinutes: 15, startAt: TestData.Now);
        var instant = TestData.Now.AddMinutes(5);

        var response = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, null, instant.ToLocalTime()));

        Assert.Equal(instant, response.Result.Record!.CheckInAt!.Value);
        Assert.Equal("Present", response.Result.Record!.Status);
    }

    /// <summary>
    /// A bare <c>"2026-07-28T09:05:00"</c> deserializes to <see cref="DateTimeKind.Unspecified"/> and
    /// is read as already-UTC — <em>not</em> shifted by the server's offset. Asserting the instant is
    /// unchanged is what separates the two.
    /// </summary>
    [Fact]
    public async Task A_tap_timestamped_without_an_offset_is_taken_as_utc_unshifted()
    {
        var world = await ArrangeAsync(graceMinutes: 15, startAt: TestData.Now);
        var instant = TestData.Now.AddMinutes(5);

        var response = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, null,
            DateTime.SpecifyKind(instant, DateTimeKind.Unspecified)));

        Assert.Equal(instant, response.Result.Record!.CheckInAt!.Value);
        Assert.Equal("Present", response.Result.Record!.Status);
    }

    // ---------------------------------------------------------------- rejections

    [Fact]
    public async Task An_unknown_card_is_rejected_as_not_found()
    {
        var world = await ArrangeAsync();

        var response = await TapAsync(new TapRequest(world.EventId, "DEADBEEF", null, null, TestData.Now));

        Assert.Equal(TapOutcome.CardNotFound, response.Outcome);
        Assert.False(response.Result.Success);
        Assert.Null(response.Result.Record);
        Assert.Empty(await AllRecordsAsync());
    }

    /// <summary>
    /// A deactivated card must not resolve. This is the reissue path from ADR-001 D-3: the old row
    /// survives so historical taps stay explainable, which only works if lookups filter on
    /// <c>IsActive</c> — the ADR calls that out as the cost of the filtered index.
    /// </summary>
    [Fact]
    public async Task A_deactivated_card_is_rejected_as_not_found()
    {
        var world = await ArrangeAsync(cardActive: false);

        var response = await TapAsync(new TapRequest(world.EventId, StoredUid, null, null, TestData.Now));

        Assert.Equal(TapOutcome.CardNotFound, response.Outcome);
        Assert.Empty(await AllRecordsAsync());
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Closed")]
    [InlineData("Cancelled")]
    public async Task A_tap_on_an_event_that_is_not_Open_is_rejected(string status)
    {
        var world = await ArrangeAsync(eventStatus: status);

        var response = await TapAsync(new TapRequest(world.EventId, StoredUid, null, null, TestData.Now));

        Assert.Equal(TapOutcome.EventNotOpen, response.Outcome);
        Assert.False(response.Result.Success);
        Assert.Contains(status, response.Result.Message);
        Assert.Empty(await AllRecordsAsync());
    }

    [Fact]
    public async Task A_tap_on_an_unknown_event_is_rejected_as_not_found()
    {
        await ArrangeAsync();

        var response = await TapAsync(new TapRequest(Guid.NewGuid(), StoredUid, null, null, TestData.Now));

        Assert.Equal(TapOutcome.EventNotFound, response.Outcome);
        Assert.Empty(await AllRecordsAsync());
    }

    [Fact]
    public async Task A_tap_on_a_soft_deleted_event_is_rejected_as_not_found()
    {
        var world = await ArrangeAsync(eventDeleted: true);

        var response = await TapAsync(new TapRequest(world.EventId, StoredUid, null, null, TestData.Now));

        Assert.Equal(TapOutcome.EventNotFound, response.Outcome);
        Assert.Empty(await AllRecordsAsync());
    }

    /// <summary>
    /// Event validation runs before card resolution, so an unknown card on a closed event reports
    /// the event. Pinned because the mobile client branches on the message it is given, and a
    /// silent reordering would change which of two errors an operator sees.
    /// </summary>
    [Fact]
    public async Task Event_state_is_reported_before_card_resolution()
    {
        var world = await ArrangeAsync(eventStatus: "Closed");

        var response = await TapAsync(new TapRequest(world.EventId, "DEADBEEF", null, null, TestData.Now));

        Assert.Equal(TapOutcome.EventNotOpen, response.Outcome);
    }

    // ---------------------------------------------------------------- attendance modes

    [Fact]
    public async Task In_TimeInOut_mode_a_second_tap_records_a_check_out()
    {
        var world = await ArrangeAsync(attendanceMode: "TimeInOut", startAt: TestData.Now);

        var checkIn = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, "in-0001", TestData.Now));
        var checkOut = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, "out-0001", TestData.Now.AddHours(2)));

        Assert.Equal(TapOutcome.Recorded, checkIn.Outcome);
        Assert.Equal(TapOutcome.CheckedOut, checkOut.Outcome);
        Assert.Equal(TestData.Now, checkOut.Result.Record!.CheckInAt!.Value);
        Assert.Equal(TestData.Now.AddHours(2), checkOut.Result.Record!.CheckOutAt!.Value);
        Assert.Single(await AllRecordsAsync());
    }

    [Fact]
    public async Task In_TimeInOut_mode_a_third_tap_is_already_recorded()
    {
        var world = await ArrangeAsync(attendanceMode: "TimeInOut", startAt: TestData.Now);

        await TapAsync(new TapRequest(world.EventId, StoredUid, null, "in-0001", TestData.Now));
        await TapAsync(new TapRequest(world.EventId, StoredUid, null, "out-0001", TestData.Now.AddHours(2)));
        var third = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, "third-0001", TestData.Now.AddHours(2).AddMinutes(1)));

        Assert.Equal(TapOutcome.AlreadyRecorded, third.Outcome);
        Assert.True(third.Result.Success);
        Assert.Equal(TestData.Now.AddHours(2), third.Result.Record!.CheckOutAt!.Value);
        Assert.Single(await AllRecordsAsync());
    }

    [Fact]
    public async Task In_Single_mode_a_second_tap_is_already_recorded_and_writes_no_check_out()
    {
        var world = await ArrangeAsync(attendanceMode: "Single", startAt: TestData.Now);

        await TapAsync(new TapRequest(world.EventId, StoredUid, null, "in-0001", TestData.Now));
        var second = await TapAsync(new TapRequest(
            world.EventId, StoredUid, null, "again-0001", TestData.Now.AddHours(1)));

        Assert.Equal(TapOutcome.AlreadyRecorded, second.Outcome);
        Assert.Equal("Already recorded.", second.Result.Message);
        Assert.Null(second.Result.Record!.CheckOutAt);
        Assert.Single(await AllRecordsAsync());
    }

    // ---------------------------------------------------------------- uid normalization

    [Theory]
    [InlineData("04:a7:b8:c9")]
    [InlineData("04-A7-B8-C9")]
    [InlineData("04 a7 b8 c9")]
    [InlineData("04a7b8c9")]
    public async Task A_tap_resolves_a_card_whatever_separators_the_reader_used(string asScanned)
    {
        var world = await ArrangeAsync(cardUid: StoredUid);

        var response = await TapAsync(new TapRequest(world.EventId, asScanned, null, null, TestData.Now));

        Assert.Equal(TapOutcome.Recorded, response.Outcome);
        Assert.Equal(world.StudentId, response.Result.Record!.StudentId);
    }

    /// <summary>REGNO is the card UID (ADR-001), so the lower-cased REGNO a reader emits must resolve.</summary>
    [Fact]
    public async Task A_tap_resolves_a_REGNO_shaped_card_uid_case_insensitively()
    {
        var world = await ArrangeAsync(cardUid: "USA00962");

        var response = await TapAsync(new TapRequest(world.EventId, "usa00962", null, null, TestData.Now));

        Assert.Equal(TapOutcome.Recorded, response.Outcome);
        Assert.Equal(world.StudentId, response.Result.Record!.StudentId);
    }

    // ---------------------------------------------------------------- record contents

    [Fact]
    public async Task A_recorded_tap_stores_the_capture_provenance()
    {
        var world = await ArrangeAsync();
        var deviceId = await AddDeviceAsync(world.SchoolId, "TEST-DEVICE-A");

        await TapAsync(new TapRequest(world.EventId, StoredUid, deviceId, "tap-0001", TestData.Now));

        var record = Assert.Single(await AllRecordsAsync());
        Assert.Equal(world.EventId, record.EventId);
        Assert.Equal(world.StudentId, record.StudentId);
        Assert.Equal(world.CardId, record.RfidCardId);
        Assert.Equal(deviceId, record.DeviceId);
        Assert.Equal("tap-0001", record.DeviceTapId);
        Assert.Equal("Rfid", record.CaptureMethod);
        Assert.Null(record.OccurrenceId);
    }

    [Fact]
    public async Task A_recorded_tap_returns_the_student_identity_the_client_displays()
    {
        var world = await ArrangeAsync();

        var response = await TapAsync(new TapRequest(world.EventId, StoredUid, null, null, TestData.Now));

        Assert.Equal("Maria Reyes Santos", response.Result.Record!.StudentName);
        Assert.Equal("2023-0001", response.Result.Record!.StudentNumber);
    }

    /// <summary>
    /// Omitting <c>TappedAt</c> is legal — §6.4 lets the server timestamp the tap. Asserted loosely
    /// on purpose: the exact instant is <c>DateTime.UtcNow</c> and pinning it would test the clock.
    /// </summary>
    [Fact]
    public async Task A_tap_without_a_timestamp_is_stamped_by_the_server_in_utc()
    {
        var world = await ArrangeAsync(startAt: DateTime.UtcNow.AddMinutes(-1));
        var before = DateTime.UtcNow;

        var response = await TapAsync(new TapRequest(world.EventId, StoredUid, null, null, null));

        var checkInAt = response.Result.Record!.CheckInAt;
        Assert.NotNull(checkInAt);
        Assert.InRange(checkInAt!.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }
}
