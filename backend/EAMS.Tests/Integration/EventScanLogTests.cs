using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The per-event scan log: scans that reached the server and resolved to no student.
///
/// <para>
/// <b>What these tests exist to protect.</b> <c>CardNotFound</c> is a rejection rather than a row, so
/// an unrecognised card used to leave no trace anywhere - the device was told and nothing was kept.
/// "Nobody scanned" and "somebody scanned a card we could not place" were the same absence of
/// evidence, and the second is the one worth investigating. The scan log is that evidence, and it is
/// only evidence while it is written on the path that discards the tap.
/// </para>
///
/// <para>
/// The other half is <see cref="A_local_outcome_cannot_change_what_the_server_decides"/>. The device
/// now sends an opinion about the card, and the moment that opinion can move an outcome, attendance
/// becomes something a handset can assert rather than something the server determines.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventScanLogTests : IntegrationTest
{
    public EventScanLogTests(SqlServerFixture sql) : base(sql) { }

    private const string KnownUid = "04A7B8C9";
    private const string UnknownUid = "DEADBEEF01";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, KnownUid, isActive: true));

        var ev = TestData.NewEvent(school.Id, "Open", "Single", 15, null);
        db.Events.Add(ev);

        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id);
    }

    private async Task<TapResponse> TapAsync(TapRequest request)
    {
        await using var db = NewDbContext();
        return await AttendanceOn(db).TapAsync(request);
    }

    private async Task<EventScanLogDto?> ScanLogAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        return await EventsOn(db).GetScanLogAsync(eventId);
    }

    // ------------------------------------------------------------------ the gap being closed

    [Fact]
    public async Task An_unresolved_scan_is_recorded_against_the_event()
    {
        var world = await ArrangeAsync();

        var response = await TapAsync(
            new TapRequest(world.EventId, UnknownUid, null, Guid.NewGuid().ToString(), TestData.Now));

        Assert.Equal(TapOutcome.CardNotFound, response.Outcome);

        var log = await ScanLogAsync(world.EventId);

        Assert.NotNull(log);
        Assert.Equal(1, log!.TotalScans);
        Assert.Equal(1, log.DistinctCards);

        var scan = Assert.Single(log.Scans);
        Assert.Equal(UnknownUid, scan.CardUid);
        Assert.Equal(nameof(TapOutcome.CardNotFound), scan.ServerOutcome);
    }

    /// <summary>
    /// A resolved tap belongs in attendance and must not also appear here.
    ///
    /// <para>
    /// Writing both would double-count every scan in any report that reads the two together, and
    /// would make the scan log grow at the rate of attendance rather than at the rate of the problem
    /// it exists to describe.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_resolved_tap_is_not_in_the_scan_log()
    {
        var world = await ArrangeAsync();

        var response = await TapAsync(
            new TapRequest(world.EventId, KnownUid, null, Guid.NewGuid().ToString(), TestData.Now));

        Assert.Equal(TapOutcome.Recorded, response.Outcome);

        var log = await ScanLogAsync(world.EventId);

        Assert.NotNull(log);
        Assert.Equal(0, log!.TotalScans);
        Assert.Empty(log.Scans);
    }

    // ------------------------------------------------------------------ the device's claim

    /// <summary>
    /// <b>The claim is recorded and is not believed.</b>
    ///
    /// <para>
    /// A device asserting it found this card while the server cannot resolve it is exactly the
    /// disagreement worth catching - a stale manifest, a sync that never ran, a cloned card. It is
    /// stored so somebody can see it, and the tap is still refused.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_local_outcome_cannot_change_what_the_server_decides()
    {
        var world = await ArrangeAsync();

        var response = await TapAsync(new TapRequest(
            world.EventId, UnknownUid, null, Guid.NewGuid().ToString(), TestData.Now,
            LocalOutcome: "found"));

        Assert.True(
            response.Outcome == TapOutcome.CardNotFound,
            $"The device claimed 'found' for a card no student holds and the server answered " +
            $"{response.Outcome}. A device's opinion about a card must never move an outcome: if it " +
            "can, attendance becomes something a handset asserts rather than something the server " +
            "determines, and any device able to reach this endpoint can mark anyone present.");

        var scan = Assert.Single((await ScanLogAsync(world.EventId))!.Scans);
        Assert.Equal("found", scan.LocalOutcome);
        Assert.Equal(nameof(TapOutcome.CardNotFound), scan.ServerOutcome);
    }

    /// <summary>
    /// An unrecognised value is stored as it arrived rather than refused.
    ///
    /// <para>
    /// The vocabulary belongs to the mobile client and is not fixed. Validating it into a closed set
    /// would mean a value we have not seen yet could reject an entire flush - and a field that exists
    /// only for reporting must never be able to fail a capture.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unrecognised_local_outcome_is_stored_verbatim_and_refuses_nothing()
    {
        var world = await ArrangeAsync();

        var response = await TapAsync(new TapRequest(
            world.EventId, UnknownUid, null, Guid.NewGuid().ToString(), TestData.Now,
            LocalOutcome: "a-value-nobody-agreed-on"));

        Assert.Equal(TapOutcome.CardNotFound, response.Outcome);

        var scan = Assert.Single((await ScanLogAsync(world.EventId))!.Scans);
        Assert.Equal("a-value-nobody-agreed-on", scan.LocalOutcome);
    }

    [Fact]
    public async Task An_overlong_local_outcome_is_truncated_rather_than_refused()
    {
        var world = await ArrangeAsync();
        var overlong = new string('x', TapRequestLimits.MaxLocalOutcomeLength * 4);

        var response = await TapAsync(new TapRequest(
            world.EventId, UnknownUid, null, Guid.NewGuid().ToString(), TestData.Now,
            LocalOutcome: overlong));

        Assert.Equal(TapOutcome.CardNotFound, response.Outcome);

        var scan = Assert.Single((await ScanLogAsync(world.EventId))!.Scans);
        Assert.Equal(TapRequestLimits.MaxLocalOutcomeLength, scan.LocalOutcome!.Length);
    }

    // ------------------------------------------------------------------ counting

    /// <summary>
    /// Repeat presentations of one card are separate rows, and the two counts differ.
    ///
    /// <para>
    /// The idempotency key already absorbs replays of a single tap, so two rows for one card mean the
    /// card was genuinely presented twice - which is what somebody does when a reader is not working,
    /// and is the signal rather than the noise. <c>DistinctCards</c> lets a caller collapse them
    /// without the underlying rows lying about how often it happened.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Repeat_presentations_are_separate_rows_but_one_distinct_card()
    {
        var world = await ArrangeAsync();

        for (var i = 0; i < 3; i++)
        {
            await TapAsync(new TapRequest(
                world.EventId, UnknownUid, null, Guid.NewGuid().ToString(), TestData.Now.AddSeconds(i)));
        }

        var log = await ScanLogAsync(world.EventId);

        Assert.Equal(3, log!.TotalScans);
        Assert.Equal(1, log.DistinctCards);
    }

    /// <summary>
    /// A replayed tap - the same <c>deviceTapId</c> twice - must not become two scans.
    ///
    /// <para>
    /// Re-flushing a queue after a dropped connection is ordinary and is the reason the idempotency
    /// key exists. If it de-duplicated attendance but not the scan log, every retry would inflate the
    /// report, and a device with a flaky connection would look like a card being presented over and
    /// over.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_replayed_unresolved_tap_is_not_counted_twice()
    {
        var world = await ArrangeAsync();
        var tapId = Guid.NewGuid().ToString();
        var request = new TapRequest(world.EventId, UnknownUid, null, tapId, TestData.Now);

        await TapAsync(request);
        await TapAsync(request);

        var log = await ScanLogAsync(world.EventId);

        Assert.True(
            log!.TotalScans == 1,
            $"The same deviceTapId was flushed twice and produced {log.TotalScans} scan-log rows. A " +
            "re-flush after a dropped connection is ordinary, and the idempotency key exists so it " +
            "changes nothing - a report that inflates on retry describes the network, not the cards.");
    }

    // ------------------------------------------------------------------ scoping

    [Fact]
    public async Task Scans_are_scoped_to_their_own_event()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            db.Events.Add(TestData.NewEvent(world.SchoolId, "Open", "Single", 15, null));
            await db.SaveChangesAsync();
        }

        Guid otherEventId;
        await using (var db = NewDbContext())
        {
            otherEventId = await db.Events.AsNoTracking()
                .Where(e => e.Id != world.EventId)
                .Select(e => e.Id).FirstAsync();
        }

        await TapAsync(new TapRequest(
            world.EventId, UnknownUid, null, Guid.NewGuid().ToString(), TestData.Now));

        Assert.Equal(1, (await ScanLogAsync(world.EventId))!.TotalScans);
        Assert.Equal(0, (await ScanLogAsync(otherEventId))!.TotalScans);
    }

    [Fact]
    public async Task An_unknown_event_has_no_scan_log()
    {
        Assert.Null(await ScanLogAsync(Guid.NewGuid()));
    }
}
