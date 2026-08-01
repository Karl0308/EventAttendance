using System.Net;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// D-46's <c>GET /api/v1/events/{id}/manifest</c> — the offline capture cache a device pulls before it
/// scans — asserted on the bytes rather than through the service.
///
/// <para>
/// <b>Over HTTP, because most of what was frozen only exists there.</b> The conditional GET, the
/// <c>ETag</c> and <c>Cache-Control</c> headers a 304 must still carry, the refusal table's statuses
/// and <c>code</c> tokens, the device credential, and the fact that <c>cardUids</c> reaches the wire as
/// a JSON string with its leading zeros intact are all properties of the response and invisible to a
/// service-level test. <c>ApiContractTests</c> records why that gap matters.
/// </para>
///
/// <para>
/// The version's <em>movement</em> — what must and must not change it — lives in
/// <see cref="EventManifestVersionMovementTests"/>, and the hash's own arithmetic in
/// <c>Unit/EventManifestVersionTests</c>.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventManifestTests : IntegrationTest
{
    public EventManifestTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Stored with separators and in lower case on purpose. The published value must be
    /// <see cref="NormalizedUid"/>, and a fixture that stored the already-normalized form would assert
    /// nothing about normalization at all.
    /// </summary>
    private const string StoredUid = "00-12:50 33 01";

    /// <summary>
    /// <b>Ten decimal digits with two significant leading zeros.</b> Through any numeric type this is
    /// <c>12503301</c> — a different card that will never match — and it breaks identically on both
    /// sides of the wire, so nothing anywhere reports an error.
    /// </summary>
    private const string NormalizedUid = "0012503301";

    private const string ReissuedUid = "0012503999";

    private static string ManifestRoute(Guid eventId) => $"/api/v1/events/{eventId}/manifest";

    /// <summary>
    /// Two attached sections, a student in both, a student in one, and a student attached by hand —
    /// the four cases the contract calls out, in one world so a single pull exercises all of them.
    /// </summary>
    private sealed record World(
        Guid SchoolId, Guid EventId, Guid GroupA, Guid GroupB,
        Guid OnlyInA, Guid InBoth, Guid Individual, string ApiKey);

    private enum Audience { GroupsAndStudents, GroupsOnly, StudentsOnly, Empty }

    private async Task<World> ArrangeAsync(
        Audience audience = Audience.GroupsAndStudents, string status = EventStatus.Open)
    {
        Guid schoolId, eventId, groupA, groupB, onlyInA, inBoth, individual;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
            db.Schools.Add(school);

            var a = TestData.NewStudent(school.Id, "2023-0001", lastName: "Santos");
            var both = TestData.NewStudent(school.Id, "2023-0003", lastName: "Tan");
            var hand = TestData.NewStudent(school.Id, "2023-0002", middleName: null, lastName: "Cruz");
            db.Students.AddRange(a, both, hand);

            // One card on the first student, two active on the second — a reissue leaves the old row
            // active (ADR-001 D-3) — and none at all on the hand-attached one, which is today's
            // ordinary case rather than an anomaly (D-43).
            db.RfidCards.Add(TestData.NewCard(school.Id, a.Id, StoredUid));
            db.RfidCards.Add(TestData.NewCard(school.Id, both.Id, ReissuedUid));
            db.RfidCards.Add(TestData.NewCard(school.Id, both.Id, "AA:BB-cc", isActive: false));

            var sectionA = TestData.NewGroup(school.Id, "BSCRIM 2-A");
            var sectionB = TestData.NewGroup(school.Id, "BSCRIM 2-B");
            db.StudentGroups.AddRange(sectionA, sectionB);

            db.StudentGroupMembers.AddRange(
                new StudentGroupMember { StudentGroupId = sectionA.Id, StudentId = a.Id },
                new StudentGroupMember { StudentGroupId = sectionA.Id, StudentId = both.Id },
                new StudentGroupMember { StudentGroupId = sectionB.Id, StudentId = both.Id });

            var ev = TestData.NewEvent(school.Id, status);
            db.Events.Add(ev);

            if (audience is Audience.GroupsAndStudents or Audience.GroupsOnly)
            {
                db.EventGroups.AddRange(
                    new EventGroup { EventId = ev.Id, StudentGroupId = sectionA.Id },
                    new EventGroup { EventId = ev.Id, StudentGroupId = sectionB.Id });
            }

            if (audience is Audience.GroupsAndStudents or Audience.StudentsOnly)
                db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = hand.Id });

            await db.SaveChangesAsync();

            schoolId = school.Id;
            eventId = ev.Id;
            groupA = sectionA.Id;
            groupB = sectionB.Id;
            onlyInA = a.Id;
            inBoth = both.Id;
            individual = hand.Id;
        }

        return new World(
            schoolId, eventId, groupA, groupB, onlyInA, inBoth, individual,
            await IssueDeviceKeyAsync(schoolId));
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static JsonElement AttendeeFor(JsonElement body, Guid studentId) =>
        Assert.Single(
            body.GetProperty("attendees").EnumerateArray(),
            a => a.GetProperty("studentId").GetGuid() == studentId);

    // ------------------------------------------------------------------------- audience resolution

    /// <summary>
    /// Groups and hand-attached students together: three distinct students, one row each.
    /// </summary>
    [Fact]
    public async Task Groups_and_individually_attached_students_are_both_in_the_audience()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(ManifestRoute(world.EventId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        var ids = body.GetProperty("attendees").EnumerateArray()
            .Select(a => a.GetProperty("studentId").GetGuid())
            .ToList();

        Assert.Equal(3, ids.Count);
        Assert.Contains(world.OnlyInA, ids);
        Assert.Contains(world.InBoth, ids);
        Assert.Contains(world.Individual, ids);
    }

    /// <summary>Groups alone: the hand-attached student is simply absent, and the two sections are not.</summary>
    [Fact]
    public async Task Groups_alone_resolve_to_their_members()
    {
        var world = await ArrangeAsync(Audience.GroupsOnly);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));

        Assert.Equal(2, body.GetProperty("groups").GetArrayLength());
        var ids = body.GetProperty("attendees").EnumerateArray()
            .Select(a => a.GetProperty("studentId").GetGuid()).ToList();
        Assert.Equal(2, ids.Count);
        Assert.DoesNotContain(world.Individual, ids);
    }

    /// <summary>
    /// Students alone: one attendee, and <c>groups</c> is an empty array rather than null. Every list
    /// on this response is empty rather than null — a nullable list obliges every client to write a
    /// branch for a value we never emit, and one of them gets it wrong.
    /// </summary>
    [Fact]
    public async Task Individually_attached_students_alone_resolve_with_an_empty_groups_list()
    {
        var world = await ArrangeAsync(Audience.StudentsOnly);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));

        Assert.Equal(JsonValueKind.Array, body.GetProperty("groups").ValueKind);
        Assert.Equal(0, body.GetProperty("groups").GetArrayLength());

        var attendee = Assert.Single(body.GetProperty("attendees").EnumerateArray());
        Assert.Equal(world.Individual, attendee.GetProperty("studentId").GetGuid());
    }

    /// <summary>
    /// <b>An event with no audience is a 200 with two empty lists, not a refusal.</b> An organizer who
    /// opens an event before attaching its sections is an ordinary sequence, and a device that received
    /// an error there would stop pulling and never see the audience arrive.
    /// </summary>
    [Fact]
    public async Task An_event_with_no_audience_is_an_empty_manifest_and_not_a_refusal()
    {
        var world = await ArrangeAsync(Audience.Empty);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(ManifestRoute(world.EventId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(0, body.GetProperty("groups").GetArrayLength());
        Assert.Equal(0, body.GetProperty("attendees").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("version").GetString()));
    }

    /// <summary>
    /// <b>The de-duplication, which is contract rather than implementation detail.</b> A student in two
    /// attached sections appears exactly once, carrying <em>both</em> group ids on that single row.
    ///
    /// <para>
    /// A client keying its offline index by <c>studentId</c> against a doubled list either overwrites
    /// the row's <c>groupIds</c> — losing the second section — or double-counts the denominator it
    /// shows on screen. Both look plausible, and in the real dev data 53 of 54 students are in more
    /// than one group, with fifteen on the widest row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_student_in_two_attached_groups_appears_once_with_both_group_ids()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));

        // Single() is the assertion: a second row for the same student fails here rather than in a
        // later count that could be explained away.
        var attendee = AttendeeFor(body, world.InBoth);

        var groupIds = attendee.GetProperty("groupIds").EnumerateArray()
            .Select(g => g.GetGuid()).ToList();

        Assert.Equal(2, groupIds.Count);
        Assert.Contains(world.GroupA, groupIds);
        Assert.Contains(world.GroupB, groupIds);
    }

    /// <summary>
    /// <b><c>groupIds</c> empty is common and is not an anomaly</b> — it is the individually-attached
    /// student. A client filtering by group must keep an "all"/"ungrouped" view, or those students are
    /// invisible on the device while fully expected on the server.
    /// </summary>
    [Fact]
    public async Task An_individually_attached_student_carries_an_empty_group_id_list()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));
        var attendee = AttendeeFor(body, world.Individual);

        Assert.Equal(JsonValueKind.Array, attendee.GetProperty("groupIds").ValueKind);
        Assert.Equal(0, attendee.GetProperty("groupIds").GetArrayLength());
    }

    /// <summary>
    /// A group the event does not invite contributes neither a <c>groups</c> row nor an attendee, even
    /// though its members are in the same school.
    /// </summary>
    [Fact]
    public async Task A_group_outside_the_audience_contributes_nothing()
    {
        var world = await ArrangeAsync(Audience.GroupsOnly);

        Guid outsiderId;
        await using (var db = NewDbContext())
        {
            var outsider = TestData.NewStudent(world.SchoolId, "2023-9999", lastName: "Outside");
            db.Students.Add(outsider);
            var other = TestData.NewGroup(world.SchoolId, "BSIT 4-C");
            db.StudentGroups.Add(other);
            db.StudentGroupMembers.Add(
                new StudentGroupMember { StudentGroupId = other.Id, StudentId = outsider.Id });
            await db.SaveChangesAsync();
            outsiderId = outsider.Id;
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));

        Assert.Equal(2, body.GetProperty("groups").GetArrayLength());
        Assert.DoesNotContain(
            body.GetProperty("attendees").EnumerateArray(),
            a => a.GetProperty("studentId").GetGuid() == outsiderId);
    }

    /// <summary>A soft-deleted student is excluded, ADR-003 D-15, the same way the roster excludes one.</summary>
    [Fact]
    public async Task A_soft_deleted_student_is_not_in_the_manifest()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var deleted = await StudentsOn(db).DeleteAsync(world.OnlyInA);
            Assert.Equal(StudentWriteOutcome.Saved, deleted.Outcome);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));

        Assert.Equal(2, body.GetProperty("attendees").GetArrayLength());
        Assert.DoesNotContain(
            body.GetProperty("attendees").EnumerateArray(),
            a => a.GetProperty("studentId").GetGuid() == world.OnlyInA);
    }

    // ------------------------------------------------------------------------------ the denominator

    /// <summary>
    /// <b><c>attendees.length</c> equals the <c>expected</c> that <c>/summary</c> and <c>/roster</c>
    /// publish for the same event.</b> Same population, same D-15 exclusion of the soft-deleted, asked
    /// three ways.
    ///
    /// <para>
    /// <b>This is the test the contract explicitly asks for, and the reason is not tidiness.</b> A
    /// device showing a different denominator from the dashboard beside it is what an operator files as
    /// "the system is wrong", and neither number looks wrong on its own — so nobody can tell which one
    /// to fix. Asserted with the soft-delete applied as well as without it, because that exclusion is
    /// the specific place a second implementation of the population would drift first.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attendees_count_equals_the_expected_reported_by_summary_and_roster(
        bool softDeleteOne)
    {
        var world = await ArrangeAsync();

        if (softDeleteOne)
        {
            await using var db = NewDbContext();
            Assert.Equal(
                StudentWriteOutcome.Saved,
                (await StudentsOn(db).DeleteAsync(world.InBoth)).Outcome);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var device = factory.CreateClient().WithDeviceKey(world.ApiKey);

        // The admin surface is open under ADR-001 D-6, so the dashboard's two reads need no credential
        // — which is also the point: these are the numbers an operator is looking at.
        using var admin = factory.CreateClient();

        var manifest = await BodyOf(await device.GetAsync(ManifestRoute(world.EventId)));
        var summary = await BodyOf(await admin.GetAsync($"/api/v1/events/{world.EventId}/summary"));
        var roster = await BodyOf(await admin.GetAsync($"/api/v1/events/{world.EventId}/roster"));

        var attendees = manifest.GetProperty("attendees").GetArrayLength();

        Assert.Equal(summary.GetProperty("expected").GetInt32(), attendees);
        Assert.Equal(roster.GetProperty("expected").GetInt32(), attendees);
        Assert.Equal(softDeleteOne ? 2 : 3, attendees);
    }

    // ------------------------------------------------------------------------------------ card uids

    /// <summary>
    /// <b>Normalized — uppercase, separators stripped — and a JSON string.</b> The stored value carries
    /// a dash, a colon and spaces and is in lower case; the published one is neither.
    /// </summary>
    [Fact]
    public async Task Card_uids_are_published_normalized()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));
        var uids = AttendeeFor(body, world.OnlyInA).GetProperty("cardUids");

        var only = Assert.Single(uids.EnumerateArray());
        Assert.Equal(JsonValueKind.String, only.ValueKind);
        Assert.Equal(NormalizedUid, only.GetString());
    }

    /// <summary>
    /// <b>The leading zeros survive to the wire, asserted on the raw text rather than on a parsed
    /// value.</b> <c>GetString()</c> above would still pass if the value had been emitted as the JSON
    /// number <c>12503301</c> and re-read — this reads the response body as text and looks for the
    /// quoted serial, which is the only form that can be true.
    /// </summary>
    [Fact]
    public async Task A_card_uid_reaches_the_wire_as_a_quoted_string_with_its_leading_zeros()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var raw = await (await client.GetAsync(ManifestRoute(world.EventId)))
            .Content.ReadAsStringAsync();

        Assert.Contains($"\"{NormalizedUid}\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(
            NormalizedUid.TrimStart('0'), raw.Replace($"\"{NormalizedUid}\"", ""),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Plural, because a reissue leaves the old row active (ADR-001 D-3) — and a deactivated card is
    /// gone, because it must stop resolving on the device the moment it stops resolving on the server.
    /// </summary>
    [Fact]
    public async Task Only_active_cards_are_published_and_a_student_may_have_several()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var second = await StudentsOn(db).AddCardAsync(
                world.InBoth, new StudentCardRequest("0012504000", "Reissued"));
            Assert.Equal(StudentWriteOutcome.Saved, second.Outcome);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));
        var uids = AttendeeFor(body, world.InBoth).GetProperty("cardUids")
            .EnumerateArray().Select(u => u.GetString()).ToList();

        Assert.Equal(2, uids.Count);
        Assert.Contains(ReissuedUid, uids);
        Assert.Contains("0012504000", uids);
        Assert.DoesNotContain("AABBCC", uids);
    }

    /// <summary>
    /// <b>No card at all is today's ordinary case, not an error</b> — the supplied roster carries no
    /// RFID column (D-43), so every student currently imports cardless. Empty array, 200.
    /// </summary>
    [Fact]
    public async Task A_student_with_no_card_carries_an_empty_card_list()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));
        var uids = AttendeeFor(body, world.Individual).GetProperty("cardUids");

        Assert.Equal(JsonValueKind.Array, uids.ValueKind);
        Assert.Equal(0, uids.GetArrayLength());
    }

    // ------------------------------------------------------------------------------- the body shape

    /// <summary>
    /// The frozen field names, and the two that were nearly wrong: <c>startAt</c>/<c>endAt</c> rather
    /// than <c>startsAt</c>/<c>endsAt</c>, and <c>groups</c>/<c>groupIds</c> rather than
    /// <c>sections</c>/<c>sectionIds</c>. Also the absence of <c>generatedAt</c>, which was deliberately
    /// not published.
    /// </summary>
    [Fact]
    public async Task The_body_carries_the_frozen_field_names()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var body = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));

        var @event = body.GetProperty("event");
        Assert.Equal(world.EventId, @event.GetProperty("id").GetGuid());
        Assert.Equal("University Convocation 2026", @event.GetProperty("name").GetString());
        Assert.Equal(15, @event.GetProperty("graceMinutes").GetInt32());
        Assert.Equal(AttendanceMode.Single, @event.GetProperty("attendanceMode").GetString());
        Assert.Equal(EventStatus.Open, @event.GetProperty("status").GetString());
        Assert.True(@event.TryGetProperty("startAt", out _));
        Assert.True(@event.TryGetProperty("endAt", out _));
        Assert.False(@event.TryGetProperty("startsAt", out _));
        Assert.False(@event.TryGetProperty("endsAt", out _));

        Assert.True(body.TryGetProperty("serverTime", out _));
        Assert.False(body.TryGetProperty("generatedAt", out _));
        Assert.False(body.TryGetProperty("sections", out _));

        var group = body.GetProperty("groups").EnumerateArray().First();
        Assert.True(group.TryGetProperty("studentGroupId", out _));
        Assert.Equal("Section", group.GetProperty("type").GetString());

        var attendee = AttendeeFor(body, world.OnlyInA);
        Assert.Equal("2023-0001", attendee.GetProperty("studentNumber").GetString());
        Assert.Equal("Maria Reyes Santos", attendee.GetProperty("fullName").GetString());
        Assert.False(attendee.TryGetProperty("sectionIds", out _));
    }

    /// <summary>
    /// <b>The invitation, not the roster.</b> Nothing about who has already tapped — a manifest
    /// carrying attendance state would read as authoritative on the device, and the one thing this
    /// object must never be is authoritative.
    /// </summary>
    [Fact]
    public async Task The_manifest_carries_no_attendance_state()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var raw = await (await client.GetAsync(ManifestRoute(world.EventId)))
            .Content.ReadAsStringAsync();

        // Quoted property names rather than bare words: the version is 22 random base64url characters
        // and a substring search for "present" over the whole body would be a coin flip nobody could
        // reproduce.
        foreach (var forbidden in new[]
                 {
                     "\"checkInAt\"", "\"checkOutAt\"", "\"isExpected\"", "\"attendanceStatus\"",
                     "\"present\"", "\"late\"", "\"absent\"", "\"excused\"", "\"expected\"",
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>version</c> in the body is the <c>ETag</c> with <c>W/</c> and the quotes stripped. It is
    /// duplicated there because an offline client persists a parsed object and very often not the
    /// headers that came with it.
    /// </summary>
    [Fact]
    public async Task The_body_version_is_the_etag_without_its_weak_prefix_and_quotes()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(ManifestRoute(world.EventId));
        var version = (await BodyOf(response)).GetProperty("version").GetString();

        Assert.Equal(EventManifestVersion.ETagFor(version!), response.Headers.ETag?.ToString());
        Assert.StartsWith(
            EventManifestVersion.ContractShapePrefix, version, StringComparison.Ordinal);
    }

    /// <summary>
    /// The frozen cache headers. <c>no-cache</c> means "may store, must revalidate", which is what
    /// makes <c>If-None-Match</c> the mandated flow; <c>no-store</c> would forbid the device's own
    /// cache, which is the entire feature.
    /// </summary>
    [Fact]
    public async Task A_200_carries_the_frozen_cache_headers()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(ManifestRoute(world.EventId));

        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.False(response.Headers.CacheControl?.NoStore);
        Assert.Contains("Authorization", response.Headers.Vary);
        Assert.NotNull(response.Headers.ETag);
    }

    // -------------------------------------------------------------------------- the conditional GET

    /// <summary>
    /// Every spelling of the current validator is a 304. Real clients send all of these, and an HTTP
    /// stack in the middle may coalesce stored validators into one comma-separated header — refusing
    /// any of them would downgrade a correct conditional request into a full re-download of a body the
    /// device already holds, while telling nobody.
    /// </summary>
    [Theory]
    [InlineData("W/\"{0}\"")]
    [InlineData("w/\"{0}\"")]
    [InlineData("\"{0}\"")]
    [InlineData("{0}")]
    [InlineData("\"m1.notthisone\", W/\"{0}\"")]
    [InlineData("*")]
    public async Task A_matching_validator_is_304(string template)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var first = await client.GetAsync(ManifestRoute(world.EventId));
        var version = (await BodyOf(first)).GetProperty("version").GetString()!;

        using var conditional = new HttpRequestMessage(
            HttpMethod.Get, ManifestRoute(world.EventId));
        conditional.Headers.TryAddWithoutValidation(
            "If-None-Match", string.Format(template, version));

        var response = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadAsStringAsync()).Length);
    }

    /// <summary>
    /// <b>Every 304 carries the <c>ETag</c> and the cache headers.</b> A 304 without an <c>ETag</c>
    /// leaves a client that lost its stored version with nothing to revalidate against next time, which
    /// turns every subsequent pull into a full download of a body it already holds — the failure the
    /// conditional GET exists to prevent, arrived at through the conditional GET.
    /// </summary>
    [Fact]
    public async Task A_304_carries_the_etag_and_the_cache_headers()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var first = await client.GetAsync(ManifestRoute(world.EventId));
        var etag = first.Headers.ETag!.ToString();

        using var conditional = new HttpRequestMessage(
            HttpMethod.Get, ManifestRoute(world.EventId));
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);

        var response = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag?.ToString());
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Contains("Authorization", response.Headers.Vary);
    }

    /// <summary>
    /// A stale, absent, empty or near-miss validator is a full 200. <b>A near-miss must never 304</b>:
    /// that would pin a device to a manifest it can never refresh.
    /// </summary>
    [Theory]
    [InlineData("W/\"m1.completelydifferent\"")]
    [InlineData("\"\"")]
    [InlineData("")]
    public async Task A_stale_or_empty_validator_is_a_full_200(string ifNoneMatch)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestRoute(world.EventId));
        if (ifNoneMatch.Length > 0)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(0, (await response.Content.ReadAsStringAsync()).Length);
    }

    /// <summary>
    /// A validator that is the current version with one character changed. Split from the theory above
    /// because it has to be derived from a real pull rather than written down.
    /// </summary>
    [Fact]
    public async Task A_near_miss_validator_is_a_full_200()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var first = await client.GetAsync(ManifestRoute(world.EventId));
        var version = (await BodyOf(first)).GetProperty("version").GetString()!;
        var nearMiss = version[..^1] + (version[^1] == 'A' ? 'B' : 'A');

        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestRoute(world.EventId));
        request.Headers.TryAddWithoutValidation("If-None-Match", $"W/\"{nearMiss}\"");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    // ------------------------------------------------------------------------------ the refusal table

    /// <summary>
    /// <c>{id}</c> that is not a GUID is a 400 from model binding, <b>not a 404</b>.
    ///
    /// <para>
    /// The route is deliberately unconstrained for this: <c>{id:guid}</c> would make a malformed id
    /// fail to match the route at all, and the frozen table binds 404 to <c>EventNotFound</c>, whose
    /// published client reaction is "stop and tell the operator". A device that sent a malformed id has
    /// a bug on its own side and must be told so.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("00000000-0000-0000-0000")]
    public async Task A_non_guid_id_is_400(string id)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync($"/api/v1/events/{id}/manifest");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_event_is_404_event_not_found()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(ManifestRoute(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            nameof(ManifestOutcome.EventNotFound),
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// <b>D-27: an event in another school is indistinguishable from one that does not exist.</b> Both
    /// are 404 <c>EventNotFound</c>, and the two responses are compared field by field — a difference in
    /// the <c>detail</c> prose alone would be a cross-tenant existence oracle, which is the whole of
    /// what D-27 forbids.
    /// </summary>
    [Fact]
    public async Task An_event_in_another_school_is_indistinguishable_from_one_that_does_not_exist()
    {
        var world = await ArrangeAsync();

        Guid foreignEventId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewSchool($"OTH-{Guid.NewGuid():N}"[..12]);
            db.Schools.Add(other);
            var ev = TestData.NewEvent(other.Id);
            db.Events.Add(ev);
            await db.SaveChangesAsync();
            foreignEventId = ev.Id;
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var foreign = await client.GetAsync(ManifestRoute(foreignEventId));
        var missing = await client.GetAsync(ManifestRoute(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var foreignBody = await BodyOf(foreign);
        var missingBody = await BodyOf(missing);

        Assert.Equal(
            nameof(ManifestOutcome.EventNotFound), foreignBody.GetProperty("code").GetString());
        Assert.Equal(
            missingBody.GetProperty("title").GetString(), foreignBody.GetProperty("title").GetString());
        Assert.Equal(
            missingBody.GetProperty("detail").GetString(), foreignBody.GetProperty("detail").GetString());
        Assert.Equal(
            missingBody.GetProperty("status").GetInt32(), foreignBody.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// A soft-deleted event is the same 404, for the same reason.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_event_is_404()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var ev = await db.Events.FindAsync(world.EventId);
            ev!.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(ManifestRoute(world.EventId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            nameof(ManifestOutcome.EventNotFound),
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// <b><c>Draft</c> is 409 <c>EventNotOpen</c>, per JJ's decision 4.</b> Only <c>Open</c> is served:
    /// a draft manifest would let a device scan against an event whose audience is still being
    /// assembled and whose taps the capture endpoint refuses anyway, and that guarantee belongs on our
    /// side rather than in an external client.
    /// </summary>
    [Fact]
    public async Task A_draft_event_is_409_event_not_open()
    {
        var world = await ArrangeAsync(status: EventStatus.Draft);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(ManifestRoute(world.EventId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            nameof(ManifestOutcome.EventNotOpen),
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// <b>Both terminal statuses, not <c>Closed</c> alone.</b> The predicate is
    /// <c>EventStatusTransition.IsTerminal</c>, the same one <c>EventRosterDto.IsFrozen</c> publishes —
    /// and the client instruction on this token is the opposite way round from
    /// <c>EventNotOpen</c>: flush the queue first, then stop.
    /// </summary>
    [Theory]
    [InlineData(EventStatus.Closed)]
    [InlineData(EventStatus.Cancelled)]
    public async Task A_terminal_event_is_409_event_frozen(string status)
    {
        var world = await ArrangeAsync(status: status);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(ManifestRoute(world.EventId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(nameof(ManifestOutcome.EventFrozen), body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    /// <summary>
    /// A refusal carries the cache headers too. <b>A cacheable 409 is the nastier case</b>: an
    /// intermediary replaying it would pin a device at "that event is not open" long after the
    /// organizer had opened it.
    /// </summary>
    [Fact]
    public async Task A_refusal_still_carries_the_cache_headers()
    {
        var world = await ArrangeAsync(status: EventStatus.Draft);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(ManifestRoute(world.EventId));

        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Contains("Authorization", response.Headers.Vary);
    }

    // ------------------------------------------------------------------------------- the credential

    /// <summary>No credential at all is 401 <c>DeviceKeyMissing</c>, with an RFC 7807 body.</summary>
    [Fact]
    public async Task No_device_key_is_401_device_key_missing()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ManifestRoute(world.EventId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("DeviceKeyMissing", (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A key that does not parse is <c>DeviceKeyMalformed</c>; a well-formed key that does not resolve
    /// is <c>DeviceKeyInvalid</c>. Both 401, and the second deliberately does not distinguish an unknown
    /// key id from a wrong secret — telling them apart confirms that a key id exists.
    /// </summary>
    [Theory]
    [InlineData("not-a-key", "DeviceKeyMalformed")]
    [InlineData("eams_dk_", "DeviceKeyMalformed")]
    [InlineData("eams_dk_0123456789ab_0000000000000000000000000000000000000000000000000000000000000000",
                "DeviceKeyInvalid")]
    public async Task A_malformed_or_unknown_key_is_401(string apiKey, string code)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        var response = await client.GetAsync(ManifestRoute(world.EventId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(code, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A real key id with the wrong secret. Distinct in the code and deliberately indistinguishable on
    /// the wire from an unknown key id.
    /// </summary>
    [Fact]
    public async Task A_valid_key_id_with_the_wrong_secret_is_401()
    {
        var world = await ArrangeAsync();
        Assert.True(DeviceKey.TryParse(world.ApiKey, out var keyId, out var secret));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient()
            .WithDeviceKey($"eams_dk_{keyId}_{new string('a', secret.Length)}");

        var response = await client.GetAsync(ManifestRoute(world.EventId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("DeviceKeyInvalid", (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// Revoked and deactivated are <b>403, not 401</b>: the device was identified, it simply carries no
    /// <c>attendance.capture</c>. The two codes are different instructions to whoever holds the handset
    /// — "reactivate me" versus "re-enrol me".
    /// </summary>
    [Theory]
    [InlineData(true, "DeviceKeyRevoked")]
    [InlineData(false, "DeviceInactive")]
    public async Task A_revoked_or_deactivated_device_is_403(bool revoke, string code)
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            // Found by the key the test itself issued — never by a query over every device in the
            // database. A cleanup-style loop against a shared instance is how the local dev school
            // lost all eight of its keys.
            Assert.True(DeviceKey.TryParse(world.ApiKey, out var keyId, out _));
            var deviceId = await DeviceIdForAsync(db, keyId);

            var response = revoke
                ? await DevicesOn(db).RevokeKeyAsync(deviceId)
                : await DevicesOn(db).UpdateAsync(
                    deviceId, new DeviceWriteRequest("Test Kiosk", "Kiosk", null, IsActive: false));

            Assert.Equal(DeviceWriteOutcome.Saved, response.Outcome);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var refused = await client.GetAsync(ManifestRoute(world.EventId));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(code, (await BodyOf(refused)).GetProperty("code").GetString());
    }

    private static async Task<Guid> DeviceIdForAsync(
        EAMS.Infrastructure.Data.EamsDbContext db, string keyId)
    {
        var device = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(db.Devices, d => d.ApiKeyId == keyId);
        return device.Id;
    }

    // ------------------------------------------------------------------------------- clientClockAt

    /// <summary>
    /// <b><c>clientClockAt</c> can never cause a refusal, whatever it carries.</b> Frozen contract, and
    /// the reason it is a <see cref="string"/> parameter rather than a <see cref="DateTime"/>:
    /// <c>[ApiController]</c> turns a query value that fails to bind into an automatic 400, before any
    /// leniency in the action could help.
    ///
    /// <para>
    /// <b>The 500 case is the one that matters here.</b> ray-backend found that
    /// <c>DateTimeStyles.RoundtripKind | AdjustToUniversal</c> throws <see cref="ArgumentException"/>
    /// rather than failing to parse — so the whole green suite passed while the one field the contract
    /// says can never refuse a pull answered 500. Every value below goes through the parse, so a
    /// throwing style combination fails this test rather than a production log.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("%20%20%20")]
    [InlineData("2026-08-01")]
    [InlineData("2026-13-45T99:99:99Z")]
    [InlineData("not/a/date")]
    [InlineData("0")]
    [InlineData("2026-08-01T09:00:00Z")]
    [InlineData("2026-08-01T09:00:00%2B08:00")]
    [InlineData("2026-08-01T09:00:00")]
    [InlineData("1999-01-01T00:00:00Z")]
    [InlineData("2999-01-01T00:00:00Z")]
    public async Task Any_client_clock_at_still_returns_200(string clientClockAt)
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync(
            $"{ManifestRoute(world.EventId)}?clientClockAt={clientClockAt}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// And it changes nothing about the answer — same version with it, without it, and with a wildly
    /// skewed value. A parameter that is "measured, logged, never validated" must also be invisible in
    /// the body, or a device's clock would silently partition our own cache.
    /// </summary>
    [Fact]
    public async Task Client_clock_at_does_not_change_the_version()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var plain = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));
        var skewed = await BodyOf(await client.GetAsync(
            $"{ManifestRoute(world.EventId)}?clientClockAt=2999-01-01T00:00:00Z"));
        var nonsense = await BodyOf(await client.GetAsync(
            $"{ManifestRoute(world.EventId)}?clientClockAt=banana"));

        Assert.Equal(plain.GetProperty("version").GetString(), skewed.GetProperty("version").GetString());
        Assert.Equal(plain.GetProperty("version").GetString(), nonsense.GetProperty("version").GetString());
    }

    // ------------------------------------------------------------------------------------ serverTime

    /// <summary>
    /// <c>serverTime</c> is UTC and moves between pulls while the version does not. The device takes
    /// its clock offset from it before enabling scan mode; a 304 carries no body and therefore no
    /// <c>serverTime</c>, which is why the contract sends the client to the <c>Date</c> header there.
    /// </summary>
    [Fact]
    public async Task Server_time_is_utc_and_moves_while_the_version_does_not()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var first = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));
        var second = await BodyOf(await client.GetAsync(ManifestRoute(world.EventId)));

        var raw = first.GetProperty("serverTime").GetString()!;
        Assert.EndsWith("Z", raw, StringComparison.Ordinal);

        Assert.Equal(
            first.GetProperty("version").GetString(), second.GetProperty("version").GetString());
        Assert.True(
            second.GetProperty("serverTime").GetDateTime()
                >= first.GetProperty("serverTime").GetDateTime());
    }
}
