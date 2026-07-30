using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>POST /attendance/tap/batch</c> — Technical Plan §8.2's offline queue flush (Phase 4d, D-31 to
/// D-33). The shape is frozen with an external mobile developer and published as the
/// <c>TapBatchRequest</c> / <c>TapBatchResult</c> schemas in the generated OpenAPI document, so
/// everything asserted here is somebody else's
/// interface rather than an implementation detail.
///
/// <para>
/// <b>The first test in this file is the one that matters most.</b> The endpoint's whole design rests
/// on it being the <em>same decision function</em> as the single tap, and this codebase has been bitten
/// twice by a duplicated write path — <c>ManualAsync</c> shipped as an undefended copy, and
/// <c>FindByEventStudentAsync</c>/<c>SaveNewRecordAsync</c> exist so it could not happen a third time.
/// A batch that quietly re-derived "is this a duplicate?" would pass every other test here.
/// </para>
///
/// <para>
/// Real SQL Server, for the reason <c>TapFlowTests</c> records: the guarantees under test are filtered
/// unique indexes and the unique-violation recovery around them, none of which exist in an in-memory
/// provider.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TapBatchTests : IntegrationTest
{
    public TapBatchTests(SqlServerFixture sql) : base(sql) { }

    private const string StoredUid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

    /// <summary>
    /// One school, one <em>live</em> open event, one student holding one active card.
    ///
    /// <para>
    /// <c>NewLiveEvent</c> rather than <c>NewEvent</c>, because these tests stamp <c>tappedAt</c> from
    /// the real clock: D-36's window compares a claimed time against the event's own
    /// <c>StartAt</c>/<c>EndAt</c>, and <c>TestData.Now</c> is a frozen instant that today's clock is
    /// nowhere near. See <c>TestData.NewLiveEvent</c>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The school code is unique per call. Two of the tests below need <em>two</em> worlds — the
    /// same-outcome pin and the cross-school memo test — and <c>Schools.Code</c> is globally unique, so
    /// a shared literal makes the second arrange fail on an index rather than on anything under test.
    /// Nothing else is made unique: <c>(SchoolId, StudentNumber)</c> and
    /// <c>(SchoolId, CardUid) WHERE IsActive = 1</c> are both tenant-scoped, and the pin test needs the
    /// same UID in both worlds to be comparing the same tap.
    /// </remarks>
    private async Task<World> ArrangeAsync(
        string attendanceMode = AttendanceMode.Single, int startedMinutesAgo = 5)
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
        db.Schools.Add(school);

        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StoredUid));

        var ev = TestData.NewLiveEvent(
            school.Id, EventStatus.Open, attendanceMode, startedMinutesAgo: startedMinutesAgo);
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

    private static TapRequest Row(
        Guid eventId, string cardUid, string? deviceTapId, DateTime? tappedAt = null) =>
        new(eventId, cardUid, DeviceId: null, deviceTapId, tappedAt);

    private static TapBatchRequest Batch(params TapRequest[] taps) =>
        new(ClientClockAt: DateTime.UtcNow, taps);

    /// <summary>Every row of a batch response, keyed by the request index it names.</summary>
    private static TapBatchRow At(TapBatchResponse response, int index) =>
        Assert.Single(response.Rows, r => r.Index == index);

    // ------------------------------------------------------- the constraint the endpoint exists under

    /// <summary>
    /// <b>The pin.</b> The same tap, through <c>/tap</c> and through <c>/tap/batch</c>, produces an
    /// identical outcome and an identical row.
    ///
    /// <para>
    /// Two separate worlds so neither tap can see the other's row, and every column that the capture
    /// path decides is compared: status (which is the Present/Late grace arithmetic), the timestamp that
    /// was stored, the capture method, the card the UID resolved to, the tenant, and the idempotency key
    /// itself. Only the identity columns and the audit timestamps are excluded, and those are the ones
    /// that <em>must</em> differ.
    /// </para>
    ///
    /// <para>
    /// <b>What this catches that nothing else does:</b> a batch implementation that re-derived any part
    /// of the decision. Every other test in this file would pass against a hand-rolled copy that agreed
    /// on the ordinary cases; this one fails the moment the two paths disagree about anything a row
    /// records.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, AttendanceStatus.Present)]   // inside the grace period.
    [InlineData(40, AttendanceStatus.Late)]     // past it — the boundary the two paths must agree on.
    public async Task A_batch_row_and_a_single_tap_produce_an_identical_outcome_and_an_identical_row(
        int startedMinutesAgo, string expectedStatus)
    {
        var single = await ArrangeAsync(startedMinutesAgo: startedMinutesAgo);
        var batched = await ArrangeAsync(startedMinutesAgo: startedMinutesAgo);

        // The same claimed instant in both worlds. Without this the two events' StartAt differ by the
        // milliseconds between the two arrange calls and a boundary case could land either side.
        var tappedAt = DateTime.UtcNow;

        // Each world is read under its own pinned tenant, which is what an authenticated device key
        // does in production. It is load-bearing here rather than decoration: the two taps carry the
        // *same* deviceTapId — they have to, or the rows being compared are not the same tap — and
        // UX_Attendance_Device_DeviceTapId is scoped (SchoolId, DeviceId, DeviceTapId). Unpinned, the
        // §11 filter is a no-op, so the second tap's idempotency pre-check matches the first world's row
        // across the tenant boundary and comes back DuplicateIgnored carrying the wrong school's
        // record. That is the documented unpinned behaviour (see AttendanceRecord.SchoolId), not a
        // defect in what is under test.
        TapResponse singleResponse;
        await using (var db = NewDbContext(new TestSchoolContext { CurrentSchoolId = single.SchoolId }))
        {
            singleResponse = await AttendanceOn(db).TapAsync(
                Row(single.EventId, StoredUid, "tap-0001", tappedAt));
        }

        TapBatchResponse batchResponse;
        await using (var db = NewDbContext(new TestSchoolContext { CurrentSchoolId = batched.SchoolId }))
        {
            batchResponse = await AttendanceOn(db).TapBatchAsync(
                Batch(Row(batched.EventId, StoredUid, "tap-0001", tappedAt)));
        }

        var batchedRow = At(batchResponse, 0);

        Assert.Equal(singleResponse.Outcome, batchedRow.Response.Outcome);
        Assert.Equal(singleResponse.Result.Code, batchedRow.Response.Result.Code);
        Assert.Equal(singleResponse.Result.Success, batchedRow.Response.Result.Success);
        Assert.Equal(expectedStatus, singleResponse.Result.Record!.Status);

        await using var read = NewDbContext();
        var singleRow = await read.AttendanceRecords.AsNoTracking()
            .SingleAsync(a => a.EventId == single.EventId);
        var batchedStored = await read.AttendanceRecords.AsNoTracking()
            .SingleAsync(a => a.EventId == batched.EventId);

        // Everything the capture path *decides*, compared directly. These are the columns a second
        // implementation would get subtly wrong.
        Assert.Equal(singleRow.Status, batchedStored.Status);
        Assert.Equal(singleRow.CheckInAt, batchedStored.CheckInAt);
        Assert.Equal(singleRow.CheckOutAt, batchedStored.CheckOutAt);
        Assert.Equal(singleRow.CaptureMethod, batchedStored.CaptureMethod);
        Assert.Equal(singleRow.DeviceTapId, batchedStored.DeviceTapId);
        Assert.Equal(singleRow.CheckOutDeviceTapId, batchedStored.CheckOutDeviceTapId);
        Assert.Equal(singleRow.DeviceId, batchedStored.DeviceId);
        Assert.Equal(singleRow.OccurrenceId, batchedStored.OccurrenceId);
        Assert.Equal(singleRow.RecordedByUserId, batchedStored.RecordedByUserId);
        Assert.Equal(singleRow.Notes, batchedStored.Notes);

        // Everything the capture path *resolves* has to be compared against each row's own world
        // instead — the two worlds are two schools, so equal values here would mean a tap had been
        // filed against the wrong tenant. D-35's denormalized SchoolId is the one that matters: it is
        // set from the event the writer just read, and a batch path that forgot it would put every
        // queued tap outside the §11 filter and outside UX_Attendance_Device_DeviceTapId's scope.
        Assert.Equal(single.SchoolId, singleRow.SchoolId);
        Assert.Equal(batched.SchoolId, batchedStored.SchoolId);
        Assert.Equal(single.StudentId, singleRow.StudentId);
        Assert.Equal(batched.StudentId, batchedStored.StudentId);
        Assert.NotNull(singleRow.RfidCardId);
        Assert.NotNull(batchedStored.RfidCardId);
    }

    // ------------------------------------------------------------------------ D-32, processing order

    /// <summary>
    /// <b>Rows are applied in ascending <c>tappedAt</c>, not in array order</b>, and this is the test
    /// the rule exists for.
    ///
    /// <para>
    /// A <c>TimeInOut</c> pair is flushed with the check-out first — which an offline queue can
    /// legitimately produce. Applied in array order, the check-out finds no existing row, takes the
    /// <em>create</em> branch, and writes a check-in stamped at the check-out's time; the check-in that
    /// follows is then absorbed as a duplicate. Nothing errors. The student's arrival is simply recorded
    /// an hour late, which is the direction that turns Present into Late.
    /// </para>
    ///
    /// <para>
    /// The assertion is on the stored timestamps rather than on the outcome tokens, because the tokens
    /// are the same either way — <c>Recorded</c> then <c>CheckedOut</c> — and that is exactly what makes
    /// the corruption silent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rows_are_applied_in_tappedAt_order_rather_than_array_order()
    {
        var world = await ArrangeAsync(AttendanceMode.TimeInOut, startedMinutesAgo: 90);

        var checkIn = DateTime.UtcNow.AddMinutes(-80);
        var checkOut = DateTime.UtcNow.AddMinutes(-10);

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(
            Row(world.EventId, StoredUid, "out-0001", checkOut),
            Row(world.EventId, StoredUid, "in-0001", checkIn)));

        await using var read = NewDbContext();
        var stored = await read.AttendanceRecords.AsNoTracking()
            .SingleAsync(a => a.EventId == world.EventId);

        Assert.Equal(
            checkIn.ToString("O"),
            stored.CheckInAt?.ToString("O"));
        Assert.Equal(
            checkOut.ToString("O"),
            stored.CheckOutAt?.ToString("O"));

        // The check-in arrived second in the array and is the row that created the record, so it holds
        // the check-in key; the check-out arrived first and holds its own. Both halves independently
        // retryable (D-34) regardless of the order the queue flushed them in.
        Assert.Equal("in-0001", stored.DeviceTapId);
        Assert.Equal("out-0001", stored.CheckOutDeviceTapId);

        Assert.Equal(TapOutcome.Recorded, At(response, 1).Response.Outcome);
        Assert.Equal(TapOutcome.CheckedOut, At(response, 0).Response.Outcome);
    }

    /// <summary>
    /// A row with no <c>tappedAt</c> means "now, on the server", so it sorts as the server's clock —
    /// after every queued row that named an earlier time. Asserted because the alternative rules
    /// (nulls first, nulls last) are both defensible-looking and both wrong.
    /// </summary>
    [Fact]
    public async Task A_row_with_no_tappedAt_is_applied_as_though_stamped_now()
    {
        var world = await ArrangeAsync(AttendanceMode.TimeInOut, startedMinutesAgo: 90);

        var queued = DateTime.UtcNow.AddMinutes(-80);

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(
            Row(world.EventId, StoredUid, "now-0001", tappedAt: null),
            Row(world.EventId, StoredUid, "queued-0001", queued)));

        // The queued row is older, so it is the check-in; the server-stamped one is the check-out.
        Assert.Equal(TapOutcome.Recorded, At(response, 1).Response.Outcome);
        Assert.Equal(TapOutcome.CheckedOut, At(response, 0).Response.Outcome);

        await using var read = NewDbContext();
        var stored = await read.AttendanceRecords.AsNoTracking()
            .SingleAsync(a => a.EventId == world.EventId);

        Assert.Equal("queued-0001", stored.DeviceTapId);
        Assert.Equal("now-0001", stored.CheckOutDeviceTapId);
    }

    // ------------------------------------------------------------------- one bad row, and duplicates

    /// <summary>
    /// <b>A rejected row is a result, not an abort.</b> The rows around it are recorded and the batch
    /// carries on.
    ///
    /// <para>
    /// <b>This is not a guard on the transaction shape, and it was originally written as though it
    /// were.</b> Wrapping the loop in one transaction and re-running left it green — a
    /// <c>CardNotFound</c> never reaches the database, so there is nothing for a rollback to undo. What
    /// it does pin is the control flow: an unrecognised card must not <c>continue</c> past the
    /// remaining rows, short-circuit the loop, or become an exception. See
    /// <c>AttendanceService.TapBatchAsync</c> for the arguments that failed their controls and the one
    /// that did not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rejected_row_does_not_discard_the_rows_around_it()
    {
        var world = await ArrangeAsync();
        var second = await AddStudentWithCardAsync(world.SchoolId, "2023-0002", "0BADC0DE");

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(
            Row(world.EventId, StoredUid, "good-0001", DateTime.UtcNow.AddMinutes(-3)),
            Row(world.EventId, "NOSUCHCARD", "bad-0001", DateTime.UtcNow.AddMinutes(-2)),
            Row(world.EventId, "0BADC0DE", "good-0002", DateTime.UtcNow.AddMinutes(-1))));

        Assert.Equal(TapOutcome.Recorded, At(response, 0).Response.Outcome);
        Assert.Equal(TapOutcome.CardNotFound, At(response, 1).Response.Outcome);
        Assert.Equal(TapOutcome.Recorded, At(response, 2).Response.Outcome);

        await using var read = NewDbContext();
        var stored = await read.AttendanceRecords.AsNoTracking()
            .Where(a => a.EventId == world.EventId)
            .Select(a => a.StudentId)
            .ToListAsync();

        Assert.Equal(2, stored.Count);
        Assert.Contains(world.StudentId, stored);
        Assert.Contains(second, stored);
    }

    /// <summary>
    /// <b>A duplicate <c>deviceTapId</c> inside one batch resolves as a duplicate.</b> A queue that
    /// re-enqueued a row it had already queued flushes both, and the second must not write a second
    /// attendance record or fail the batch.
    ///
    /// <para>
    /// It works because the second row's idempotency pre-check reads the first row's write on the same
    /// connection — which holds whether that write is committed or merely uncommitted-and-visible-to-
    /// itself. The pre-check is doing the work here, not the commit boundary: wrapping the loop in one
    /// transaction leaves this green. What would break it is a batch implementation that skipped the
    /// pre-check for rows it had "already seen", or one that deduplicated in memory instead of against
    /// the table.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_duplicate_deviceTapId_inside_one_batch_is_absorbed_as_a_duplicate()
    {
        var world = await ArrangeAsync();
        var tappedAt = DateTime.UtcNow.AddMinutes(-2);

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(
            Row(world.EventId, StoredUid, "same-0001", tappedAt),
            Row(world.EventId, StoredUid, "same-0001", tappedAt)));

        Assert.Equal(TapOutcome.Recorded, At(response, 0).Response.Outcome);
        Assert.Equal(TapOutcome.DuplicateIgnored, At(response, 1).Response.Outcome);

        // Both rows report the same record, which is what makes the duplicate reconcilable rather than
        // merely absorbed.
        Assert.Equal(
            At(response, 0).Response.Result.Record!.Id,
            At(response, 1).Response.Result.Record!.Id);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.AttendanceRecords.CountAsync(a => a.EventId == world.EventId));
    }

    /// <summary>
    /// The same batch sent twice — the retry the published contract tells a client is always safe. Every
    /// row comes back <c>DuplicateIgnored</c> and no second row is written.
    /// </summary>
    [Fact]
    public async Task Resending_the_whole_batch_writes_nothing_new()
    {
        var world = await ArrangeAsync();
        var second = await AddStudentWithCardAsync(world.SchoolId, "2023-0002", "0BADC0DE");
        var tappedAt = DateTime.UtcNow.AddMinutes(-2);

        var batch = Batch(
            Row(world.EventId, StoredUid, "flush-0001", tappedAt),
            Row(world.EventId, "0BADC0DE", "flush-0002", tappedAt));

        await using (var db = NewDbContext())
        {
            var first = await AttendanceOn(db).TapBatchAsync(batch);
            Assert.All(first.Rows, r => Assert.Equal(TapOutcome.Recorded, r.Response.Outcome));
        }

        await using (var db = NewDbContext())
        {
            var replay = await AttendanceOn(db).TapBatchAsync(batch);
            Assert.All(replay.Rows, r => Assert.Equal(TapOutcome.DuplicateIgnored, r.Response.Outcome));
        }

        await using var read = NewDbContext();
        var students = await read.AttendanceRecords.AsNoTracking()
            .Where(a => a.EventId == world.EventId).Select(a => a.StudentId).ToListAsync();

        Assert.Equal(2, students.Count);
        Assert.Contains(world.StudentId, students);
        Assert.Contains(second, students);
    }

    // --------------------------------------------------------------------------------- D-33 and caps

    /// <summary>
    /// D-33 — <c>deviceTapId</c> is required on this endpoint and optional on the single one. Refused
    /// per row, so the rest of the batch is unaffected.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_row_without_a_deviceTapId_is_refused_and_the_rest_are_not(string? deviceTapId)
    {
        var world = await ArrangeAsync();
        var second = await AddStudentWithCardAsync(world.SchoolId, "2023-0002", "0BADC0DE");

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(
            Row(world.EventId, StoredUid, deviceTapId, DateTime.UtcNow.AddMinutes(-2)),
            Row(world.EventId, "0BADC0DE", "good-0001", DateTime.UtcNow.AddMinutes(-1))));

        var refused = At(response, 0);
        Assert.Equal(TapOutcome.DeviceTapIdRequired, refused.Response.Outcome);
        Assert.Equal(nameof(TapOutcome.DeviceTapIdRequired), refused.Response.Result.Code);
        Assert.False(refused.Response.Result.Success);
        Assert.Null(refused.Response.Result.Record);

        Assert.Equal(TapOutcome.Recorded, At(response, 1).Response.Outcome);

        await using var read = NewDbContext();
        var stored = await read.AttendanceRecords.AsNoTracking()
            .Where(a => a.EventId == world.EventId).Select(a => a.StudentId).ToListAsync();

        Assert.Equal([second], stored);
    }

    /// <summary>
    /// <b>An over-length <c>deviceTapId</c> is a rejection, not a 500.</b> The Phase 4d review's
    /// CRITICAL, and the third appearance of one defect: a client-supplied string written to a bounded
    /// <c>nvarchar</c> with nothing checking its length reaches SQL Server as error 8152/2628, which is
    /// not a unique violation, so <c>SaveNewRecordAsync</c>'s filter correctly declines it and it
    /// propagates unhandled.
    ///
    /// <para>
    /// <b>Why it mattered enough to block the phase.</b> §8.2's queue retries a 5xx, the frozen contract
    /// makes <c>deviceTapId</c> required here, and its generation scheme is still an open question with
    /// the mobile developer — so one poison row 500s the whole batch, forever, with nothing in the
    /// response naming which row is bad. Exactly the permanently-wedged queue <c>DeviceNotRegistered</c>
    /// and D-35 were each created to close, arriving by a third route.
    /// </para>
    ///
    /// <para>
    /// One character over the limit, deliberately: a test at ten times the limit would pass against a
    /// guard with an off-by-one in it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_over_length_deviceTapId_is_refused_rather_than_500ing_the_batch()
    {
        var world = await ArrangeAsync();
        var second = await AddStudentWithCardAsync(world.SchoolId, "2023-0002", "0BADC0DE");

        var tooLong = new string('x', DeviceTapIds.MaxLength + 1);

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(
            Row(world.EventId, StoredUid, tooLong, DateTime.UtcNow.AddMinutes(-2)),
            Row(world.EventId, "0BADC0DE", "good-0001", DateTime.UtcNow.AddMinutes(-1))));

        var refused = At(response, 0);
        Assert.Equal(TapOutcome.DeviceTapIdRequired, refused.Response.Outcome);
        Assert.False(refused.Response.Result.Success);
        Assert.Null(refused.Response.Result.Record);
        Assert.Contains($"{DeviceTapIds.MaxLength}", refused.Response.Result.Message);

        // The rest of the batch is untouched — the whole point of refusing per row.
        Assert.Equal(TapOutcome.Recorded, At(response, 1).Response.Outcome);

        await using var read = NewDbContext();
        var stored = await read.AttendanceRecords.AsNoTracking()
            .Where(a => a.EventId == world.EventId).Select(a => a.StudentId).ToListAsync();
        Assert.Equal([second], stored);
    }

    /// <summary>Exactly at the limit is accepted — the bound is inclusive, like every other in §4.</summary>
    [Fact]
    public async Task A_deviceTapId_exactly_at_the_limit_is_accepted()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(
            Row(world.EventId, StoredUid, new string('x', DeviceTapIds.MaxLength),
                DateTime.UtcNow.AddMinutes(-2))));

        Assert.Equal(TapOutcome.Recorded, At(response, 0).Response.Outcome);
    }

    /// <summary>
    /// The same guard on <c>/tap</c>. Same mechanism and the same 500, without the queue amplification —
    /// which is why it is a rejection there too rather than being left to the batch endpoint.
    /// </summary>
    [Fact]
    public async Task The_single_endpoint_also_refuses_an_over_length_deviceTapId()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(
            Row(world.EventId, StoredUid, new string('x', DeviceTapIds.MaxLength + 1),
                DateTime.UtcNow.AddMinutes(-2)));

        Assert.Equal(TapOutcome.DeviceTapIdRequired, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.AttendanceRecords.CountAsync());
    }

    /// <summary>
    /// <b>A null element in <c>taps</c> is a rejected row, not a batch-level 500.</b>
    /// <c>{"taps": [null]}</c> is valid JSON and <c>System.Text.Json</c> puts a null in the list;
    /// ASP.NET's implicit-required-from-NRT covers bound parameters and properties and never covers
    /// collection elements, so nothing upstream rejects it.
    ///
    /// <para>
    /// It used to <c>NullReferenceException</c> inside the <c>tappedAt</c> sort — <em>before</em> any
    /// row had been processed, so the whole batch 500'd and §8.2 retried it forever. The fix is the
    /// order of the two passes: shape-check every row, then sort only the well-formed ones.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_null_row_is_refused_and_the_rest_of_the_batch_is_processed()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(new TapBatchRequest(
            DateTime.UtcNow,
            [null, Row(world.EventId, StoredUid, "after-null", DateTime.UtcNow.AddMinutes(-1))]));

        var refused = At(response, 0);
        Assert.Equal(TapOutcome.DeviceTapIdRequired, refused.Response.Outcome);
        Assert.Null(refused.DeviceTapId);
        Assert.Null(refused.Response.Result.Record);

        Assert.Equal(TapOutcome.Recorded, At(response, 1).Response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.AttendanceRecords.CountAsync(a => a.EventId == world.EventId));
    }

    /// <summary>
    /// A batch of nothing but null rows. Separate from the mixed case because the sort is what used to
    /// throw, and a batch with one good row could in principle survive a partially-fixed sort.
    /// </summary>
    [Fact]
    public async Task A_batch_of_only_null_rows_is_a_well_formed_batch_of_rejections()
    {
        await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(
            new TapBatchRequest(DateTime.UtcNow, [null, null]));

        Assert.Null(response.Refusal);
        Assert.Equal(2, response.Rows.Count);
        Assert.All(response.Rows, r =>
            Assert.Equal(TapOutcome.DeviceTapIdRequired, r.Response.Outcome));
    }

    /// <summary>
    /// The same tap without a <c>deviceTapId</c> is <em>accepted</em> on the single endpoint. Asserted
    /// beside the refusal because the asymmetry is the decision, and a future "consistency" change that
    /// made the single endpoint require one too would break every mock client built before 4b.
    /// </summary>
    [Fact]
    public async Task The_single_endpoint_still_accepts_a_tap_without_a_deviceTapId()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(
            Row(world.EventId, StoredUid, deviceTapId: null, DateTime.UtcNow.AddMinutes(-2)));

        Assert.Equal(TapOutcome.Recorded, response.Outcome);
    }

    /// <summary>
    /// D-31's row cap. <b>Nothing is recorded</b> — the refusal happens before the loop, rather than by
    /// truncating to the first 200, because a truncated batch would return a 200 whose results claim
    /// every submitted row landed and the client would delete the ones that did not.
    /// </summary>
    [Fact]
    public async Task A_batch_over_the_row_cap_is_refused_and_records_nothing()
    {
        var world = await ArrangeAsync();

        var taps = Enumerable.Range(0, TapBatchLimits.MaxRows + 1)
            .Select(i => Row(world.EventId, StoredUid, $"over-{i:0000}", DateTime.UtcNow.AddMinutes(-2)))
            .ToArray();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(taps));

        Assert.NotNull(response.Refusal);
        Assert.Equal(TapOutcome.BatchTooLarge, response.Refusal.Outcome);
        Assert.Equal(nameof(TapOutcome.BatchTooLarge), response.Refusal.Result.Code);
        Assert.Empty(response.Rows);
        Assert.Contains($"{TapBatchLimits.MaxRows}", response.Refusal.Result.Message);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.AttendanceRecords.CountAsync());
    }

    /// <summary>Exactly at the cap is accepted. The boundary is inclusive, like every other in §4.</summary>
    [Fact]
    public async Task A_batch_exactly_at_the_row_cap_is_accepted()
    {
        var world = await ArrangeAsync();

        // One real student and 199 rows naming a UID nobody holds: this test is about the cap, not about
        // writing two hundred attendance rows, and CardNotFound exercises the loop just as well.
        var taps = new[] { Row(world.EventId, StoredUid, "at-cap-0000", DateTime.UtcNow.AddMinutes(-2)) }
            .Concat(Enumerable.Range(1, TapBatchLimits.MaxRows - 1)
                .Select(i => Row(world.EventId, "NOSUCHCARD", $"at-cap-{i:0000}", DateTime.UtcNow.AddMinutes(-2))))
            .ToArray();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(taps));

        Assert.Null(response.Refusal);
        Assert.Equal(TapBatchLimits.MaxRows, response.Rows.Count);
        Assert.Equal(TapOutcome.Recorded, At(response, 0).Response.Outcome);
    }

    /// <summary>
    /// An empty batch is a 200 with no results, and so is a body that omits <c>taps</c> entirely. "The
    /// queue had nothing to send" is not a malformed request, and a client that 400s on its own quiet
    /// period will stop flushing.
    /// </summary>
    [Fact]
    public async Task An_empty_batch_is_accepted_with_no_results()
    {
        await ArrangeAsync();

        await using var db = NewDbContext();
        var attendance = AttendanceOn(db);

        var empty = await attendance.TapBatchAsync(new TapBatchRequest(DateTime.UtcNow, []));
        Assert.Null(empty.Refusal);
        Assert.Empty(empty.Rows);

        var absent = await attendance.TapBatchAsync(new TapBatchRequest(null, null));
        Assert.Null(absent.Refusal);
        Assert.Empty(absent.Rows);
    }

    // ------------------------------------------------------------------------------ result ordering

    /// <summary>
    /// <c>results[i].index == i</c>: rows come back in the request's array order even though they were
    /// processed in <c>tappedAt</c> order. A published guarantee that costs nothing and protects the
    /// cheap client — the one that zips <c>results</c> against <c>taps</c> positionally.
    /// </summary>
    [Fact]
    public async Task Results_are_returned_in_request_order_whatever_order_they_were_applied_in()
    {
        var world = await ArrangeAsync();
        var second = await AddStudentWithCardAsync(world.SchoolId, "2023-0002", "0BADC0DE");
        var third = await AddStudentWithCardAsync(world.SchoolId, "2023-0003", "0FEEDFACE");

        // Deliberately descending, so array order and processing order are exact opposites.
        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapBatchAsync(Batch(
            Row(world.EventId, "0FEEDFACE", "third", DateTime.UtcNow.AddMinutes(-1)),
            Row(world.EventId, "0BADC0DE", "second", DateTime.UtcNow.AddMinutes(-2)),
            Row(world.EventId, StoredUid, "first", DateTime.UtcNow.AddMinutes(-3))));

        Assert.Equal([0, 1, 2], response.Rows.Select(r => r.Index));
        Assert.Equal(["third", "second", "first"], response.Rows.Select(r => r.DeviceTapId));
        Assert.All(response.Rows, r => Assert.Equal(TapOutcome.Recorded, r.Response.Outcome));

        await using var read = NewDbContext();
        Assert.Equal(3, await read.AttendanceRecords.CountAsync(a => a.EventId == world.EventId));
        Assert.Equal(3, new[] { world.StudentId, second, third }.Distinct().Count());
    }

    // ---------------------------------------------------- 4c carry-over: the settings read is hoisted

    /// <summary>
    /// <b>The §4.13 tap-window settings are read once per batch, not once per row.</b> The 4c carry-over.
    ///
    /// <para>
    /// <b>How the count is observed.</b> A malformed settings value makes
    /// <c>ResolveTapWindowAsync</c> log a warning naming the key, once per read — so a school with one
    /// unreadable row gives exactly one warning per settings read. Three tap rows that each triggered
    /// their own read would give three. That is the only externally visible consequence of the hoist,
    /// and it is a real one: without a way to observe it, "resolved once per batch" is a claim no test
    /// could falsify.
    /// </para>
    ///
    /// <para>
    /// The malformed row is a <em>global</em> one, so the school falls through to the published default
    /// and every tap still succeeds — the test is about how often the read happens, not about what it
    /// returns.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_tap_window_settings_are_read_once_per_batch()
    {
        var world = await ArrangeAsync();
        await AddStudentWithCardAsync(world.SchoolId, "2023-0002", "0BADC0DE");
        await AddStudentWithCardAsync(world.SchoolId, "2023-0003", "0FEEDFACE");

        await using (var arrange = NewDbContext())
        {
            arrange.SystemSettings.Add(new SystemSetting
            {
                SchoolId = world.SchoolId,
                Key = TapTimeWindow.BeforeStartMinutesSettingKey,
                Value = "not-a-number",
            });
            await arrange.SaveChangesAsync();
        }

        var logger = new CapturingLogger<AttendanceService>();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db, logger).TapBatchAsync(Batch(
            Row(world.EventId, StoredUid, "hoist-0001", DateTime.UtcNow.AddMinutes(-3)),
            Row(world.EventId, "0BADC0DE", "hoist-0002", DateTime.UtcNow.AddMinutes(-2)),
            Row(world.EventId, "0FEEDFACE", "hoist-0003", DateTime.UtcNow.AddMinutes(-1))));

        Assert.All(response.Rows, r => Assert.Equal(TapOutcome.Recorded, r.Response.Outcome));

        var warnings = logger.At(LogLevel.Warning)
            .Where(e => e.Message.Contains(TapTimeWindow.BeforeStartMinutesSettingKey, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            warnings.Count == 1,
            $"The §4.13 tap window was resolved {warnings.Count} times for a 3-row batch in one " +
            "school. It must be resolved once and memoized for the rest of the batch — a 200-row " +
            "flush otherwise runs 200 identical settings queries, and the predicate is a disjunction " +
            "on the leading key column, so each one is a small scan rather than a seek. See " +
            "AttendanceService.ResolveTapWindowAsync's cache parameter.");
    }

    /// <summary>
    /// The memo is keyed by school, so two schools in one batch are two reads rather than one.
    ///
    /// <para>
    /// Unreachable through an authenticated device — its key pins one tenant, and a foreign event comes
    /// back <c>EventNotFound</c> — but this service is called directly by the import path and by tests
    /// with no tenant pinned, and this codebase has been wrong about "unreachable" on this exact class of
    /// assumption before (see <c>AttendanceRecord.SchoolId</c>). A memo keyed on nothing would apply one
    /// school's window to the other's event, silently.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_window_memo_does_not_leak_between_schools()
    {
        var first = await ArrangeAsync();
        var second = await ArrangeAsync();

        await using (var arrange = NewDbContext())
        {
            arrange.SystemSettings.Add(new SystemSetting
            {
                SchoolId = first.SchoolId,
                Key = TapTimeWindow.BeforeStartMinutesSettingKey,
                Value = "still-not-a-number",
            });
            arrange.SystemSettings.Add(new SystemSetting
            {
                SchoolId = second.SchoolId,
                Key = TapTimeWindow.BeforeStartMinutesSettingKey,
                Value = "also-not-a-number",
            });
            await arrange.SaveChangesAsync();
        }

        var logger = new CapturingLogger<AttendanceService>();

        await using var db = NewDbContext();
        await AttendanceOn(db, logger).TapBatchAsync(Batch(
            Row(first.EventId, StoredUid, "school-a", DateTime.UtcNow.AddMinutes(-2)),
            Row(second.EventId, StoredUid, "school-b", DateTime.UtcNow.AddMinutes(-1))));

        var warnings = logger.At(LogLevel.Warning)
            .Where(e => e.Message.Contains(TapTimeWindow.BeforeStartMinutesSettingKey, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            warnings.Count == 2,
            $"Two schools produced {warnings.Count} settings reads. The per-batch memo must be keyed " +
            "by school: keyed on nothing, one school's tap window would be applied to another " +
            "school's event.");
    }

    // ------------------------------------------------------------------------------- clock skew (§8.2)

    /// <summary>
    /// The published contract says we log on clock drift, and <c>clientClockAt</c> is the only value that
    /// measures it cleanly — a row's <c>tappedAt</c> is old for two reasons that cannot be told apart. A
    /// field the API accepted and discarded would be worse than no field.
    /// </summary>
    [Fact]
    public async Task A_drifted_client_clock_is_logged_and_refuses_nothing()
    {
        var world = await ArrangeAsync();
        var logger = new CapturingLogger<AttendanceService>();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db, logger).TapBatchAsync(new TapBatchRequest(
            ClientClockAt: DateTime.UtcNow.AddHours(8),
            [Row(world.EventId, StoredUid, "skewed-0001", DateTime.UtcNow.AddMinutes(-2))]));

        // The row's own tappedAt is fine, so the tap is recorded. Refusing the batch over its envelope
        // clock would discard rows whose timestamps we have no complaint about.
        Assert.Equal(TapOutcome.Recorded, At(response, 0).Response.Outcome);

        Assert.Contains(
            logger.At(LogLevel.Warning),
            e => e.Message.Contains("skew", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An in-tolerance clock says nothing, or the warning is noise nobody reads.</summary>
    [Fact]
    public async Task A_healthy_client_clock_logs_nothing()
    {
        var world = await ArrangeAsync();
        var logger = new CapturingLogger<AttendanceService>();

        await using var db = NewDbContext();
        await AttendanceOn(db, logger).TapBatchAsync(new TapBatchRequest(
            ClientClockAt: DateTime.UtcNow,
            [Row(world.EventId, StoredUid, "healthy-0001", DateTime.UtcNow.AddMinutes(-2))]));

        Assert.DoesNotContain(
            logger.At(LogLevel.Warning),
            e => e.Message.Contains("skew", StringComparison.OrdinalIgnoreCase));
    }
}
