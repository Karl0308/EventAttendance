using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The UTC invariant, end to end.
///
/// <para>
/// <b>Why the HTTP assertions are the ones that count.</b> §4 stores timestamps as
/// <c>datetime2</c>, which carries no offset, so EF materializes
/// <see cref="DateTimeKind.Unspecified"/> and <c>System.Text.Json</c> writes it with no <c>Z</c> —
/// which JavaScript's <c>new Date()</c> then reads as <em>local</em>, rendering every persisted
/// timestamp eight hours out in Manila with nothing in the payload to reveal it. The write path made
/// it worse rather than obvious: a freshly created record was returned from the tracked entity,
/// where <c>DateTime.UtcNow</c> still carried <c>Kind = Utc</c>, so <b>the same field serialized
/// differently on create than on read-back</b>. An assertion on the entity alone would have passed
/// throughout. So the tests below compare the two payloads to each other, not just each to a value.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class UtcRoundTripTests : IntegrationTest
{
    public UtcRoundTripTests(SqlServerFixture sql) : base(sql) { }

    private const string StoredUid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

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

    // ---------------------------------------------------------------- persistence layer

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task A_timestamp_written_and_re_read_comes_back_as_Utc_with_the_same_instant(
        DateTimeKind writtenKind)
    {
        var written = DateTime.SpecifyKind(new DateTime(2026, 7, 28, 2, 17, 11, 980), writtenKind);
        var world = await ArrangeAsync(startAt: written);

        await using var read = NewDbContext();
        var stored = await read.Events.AsNoTracking().SingleAsync(e => e.Id == world.EventId);

        Assert.Equal(DateTimeKind.Utc, stored.StartAt.Kind);
        Assert.Equal(written.Ticks, stored.StartAt.Ticks);
    }

    /// <summary>
    /// A <c>Local</c> value must be stored as the same <em>instant</em>, not the same clock reading.
    /// Comparing ticks would pass for the broken behaviour on a UTC machine, so this asserts against
    /// <see cref="DateTime.ToUniversalTime"/> instead.
    /// </summary>
    [Fact]
    public async Task A_timestamp_written_in_local_time_is_stored_as_the_same_instant_in_utc()
    {
        var local = new DateTime(2026, 7, 28, 18, 0, 0, DateTimeKind.Local);
        var world = await ArrangeAsync(startAt: local);

        await using var read = NewDbContext();
        var stored = await read.Events.AsNoTracking().SingleAsync(e => e.Id == world.EventId);

        Assert.Equal(DateTimeKind.Utc, stored.StartAt.Kind);
        Assert.Equal(local.ToUniversalTime().Ticks, stored.StartAt.Ticks);
    }

    [Fact]
    public async Task A_null_timestamp_survives_the_round_trip_as_null()
    {
        var world = await ArrangeAsync();

        await using (var write = NewDbContext())
        {
            write.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId,
                EventId = world.EventId, StudentId = world.StudentId,
                CheckInAt = TestData.Now, CheckOutAt = null, Status = "Present",
            });
            await write.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var record = await read.AttendanceRecords.AsNoTracking().SingleAsync();

        Assert.Null(record.CheckOutAt);
        Assert.Equal(DateTimeKind.Utc, record.CheckInAt!.Value.Kind);
    }

    /// <summary>
    /// The convention is applied to <c>DateTime</c> globally, not per property, so entities added
    /// later (ADR-001 D-1's academic layer) inherit it. Sampling several unrelated columns is what
    /// makes this a test of the convention rather than of three lucky properties.
    /// </summary>
    [Fact]
    public async Task Every_timestamp_column_on_a_re_read_entity_is_Utc()
    {
        var world = await ArrangeAsync();

        await using var read = NewDbContext();
        var ev = await read.Events.AsNoTracking().SingleAsync();
        var card = await read.RfidCards.AsNoTracking().SingleAsync();
        var school = await read.Schools.AsNoTracking().SingleAsync();

        Assert.Equal(DateTimeKind.Utc, ev.StartAt.Kind);
        Assert.Equal(DateTimeKind.Utc, ev.EndAt.Kind);
        Assert.Equal(DateTimeKind.Utc, ev.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, ev.UpdatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, card.IssuedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, school.CreatedAt.Kind);
        Assert.Equal(world.EventId, ev.Id);
    }

    // ---------------------------------------------------------------- HTTP layer

    /// <summary>
    /// The assertion that would actually have caught the bug: the JSON a browser receives.
    /// <c>checkInAt</c> must carry <c>Z</c> on the create response <em>and</em> on the subsequent
    /// read, and the two must be byte-identical — the failure mode was one field with two
    /// serializations, which no single-payload assertion detects.
    /// </summary>
    [Fact]
    public async Task The_same_timestamp_serializes_identically_on_create_and_on_read_back()
    {
        var tappedAt = TestData.Now.AddMinutes(5);
        var world = await ArrangeAsync(startAt: TestData.Now);

        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var tap = await client.PostAsJsonAsync("/api/v1/attendance/tap", new
        {
            eventId = world.EventId,
            cardUid = StoredUid,
            tappedAt,
        });
        tap.EnsureSuccessStatusCode();

        using var created = JsonDocument.Parse(await tap.Content.ReadAsStringAsync());
        var onCreate = created.RootElement.GetProperty("record").GetProperty("checkInAt").GetString();

        var list = await client.GetAsync($"/api/v1/attendance?eventId={world.EventId}");
        list.EnsureSuccessStatusCode();

        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var onRead = listed.RootElement.GetProperty("items")[0].GetProperty("checkInAt").GetString();

        Assert.EndsWith("Z", onCreate);
        Assert.EndsWith("Z", onRead);
        Assert.Equal(onCreate, onRead);
        Assert.Equal(tappedAt, DateTime.Parse(onRead!, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    /// <summary>
    /// The same guarantee on a read-only path, which is where the bug was visible first — every
    /// timestamp the SPA renders comes back from a fresh read, never from a tracked entity.
    /// </summary>
    [Fact]
    public async Task Event_timestamps_are_emitted_with_a_Z_suffix()
    {
        var world = await ArrangeAsync(startAt: TestData.Now);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/events/{world.EventId}");
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.EndsWith("Z", body.RootElement.GetProperty("startAt").GetString());
        Assert.EndsWith("Z", body.RootElement.GetProperty("endAt").GetString());
    }

    /// <summary>
    /// A client that sends an explicit offset must get the same instant back, not the same wall
    /// clock. This is the round trip the mobile client performs in §8.2's offline sync.
    /// </summary>
    [Fact]
    public async Task A_tap_sent_with_an_offset_is_returned_as_the_equivalent_utc_instant()
    {
        var world = await ArrangeAsync(startAt: TestData.Now);

        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        // 17:05 +08:00 is 09:05 UTC — five minutes into the event, inside the 15-minute grace.
        var tap = await client.PostAsJsonAsync("/api/v1/attendance/tap", new
        {
            eventId = world.EventId,
            cardUid = StoredUid,
            tappedAt = "2026-07-28T17:05:00+08:00",
        });
        tap.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await tap.Content.ReadAsStringAsync());
        var record = body.RootElement.GetProperty("record");

        Assert.Equal("Present", record.GetProperty("status").GetString());
        Assert.Equal(
            TestData.Now.AddMinutes(5),
            DateTime.Parse(
                record.GetProperty("checkInAt").GetString()!,
                null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    /// <summary>
    /// A payload with no offset at all is read as UTC and <em>not</em> shifted by the server's
    /// zone — the documented contract in <see cref="UtcTime"/>. Clients that mean local time must
    /// send an offset, and this is the test that says so out loud.
    /// </summary>
    [Fact]
    public async Task A_tap_sent_without_an_offset_is_taken_as_utc()
    {
        var world = await ArrangeAsync(startAt: TestData.Now);

        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var tap = await client.PostAsJsonAsync("/api/v1/attendance/tap", new
        {
            eventId = world.EventId,
            cardUid = StoredUid,
            tappedAt = "2026-07-28T09:05:00",
        });
        tap.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await tap.Content.ReadAsStringAsync());
        var checkInAt = body.RootElement.GetProperty("record").GetProperty("checkInAt").GetString();

        Assert.EndsWith("Z", checkInAt);
        Assert.Equal(
            TestData.Now.AddMinutes(5),
            DateTime.Parse(checkInAt!, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
