using System.Reflection;
using EAMS.Application.Dtos;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <see cref="EventManifestVersion"/> — the D-46 content hash that <c>GET /events/{id}/manifest</c>
/// publishes as its <c>ETag</c> and as <c>version</c> in the body, and the lenient
/// <c>If-None-Match</c> comparison beside it.
///
/// <para>
/// <b>Pure logic, deliberately: none of this needs a database, and the failure modes it guards are
/// arithmetic rather than storage.</b> What a database <em>is</em> needed for — that the version moves
/// when a membership row is written by a path carrying no timestamp, and that it does not move when a
/// tap is recorded — lives in <c>Integration/EventManifestVersionMovementTests</c>.
/// </para>
/// </summary>
public class EventManifestVersionTests
{
    private static readonly Guid EventId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupOne = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GroupTwo = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid StudentOne = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid StudentTwo = new("55555555-5555-5555-5555-555555555555");

    private static readonly DateTime StartAt = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndAt = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A fresh graph on every call — never a shared static. Two calls produce two object graphs that
    /// share no reference at all, which is what lets the stability tests compare independently
    /// constructed content rather than the same instance twice.
    /// </summary>
    private static (EventManifestEventDto Event,
                    IReadOnlyList<EventManifestGroupDto> Groups,
                    IReadOnlyList<EventManifestAttendeeDto> Attendees) Sample() =>
    (
        new EventManifestEventDto(
            EventId, "University Convocation 2026", StartAt, EndAt, 15, "Single", "Open"),
        [
            new EventManifestGroupDto(GroupOne, "BSCRIM 2-A", "Section"),
            new EventManifestGroupDto(GroupTwo, "BSCRIM 2-B", "Section"),
        ],
        [
            new EventManifestAttendeeDto(
                StudentOne, "2023-0001", "Maria Reyes Santos",
                [GroupOne, GroupTwo], ["0012503301"]),
            new EventManifestAttendeeDto(
                StudentTwo, "2023-0002", "Jose Cruz", [], []),
        ]
    );

    private static string VersionOf(
        (EventManifestEventDto Event,
         IReadOnlyList<EventManifestGroupDto> Groups,
         IReadOnlyList<EventManifestAttendeeDto> Attendees) content) =>
        Compute(content.Event, content.Groups, content.Attendees);

    /// <summary>
    /// The version of one manifest's content, assembled the way <c>EventService</c> assembles it: the
    /// whole <see cref="EventManifestDto"/> goes into the hash and the two excluded properties are
    /// dropped structurally.
    ///
    /// <para>
    /// <b><see cref="DateTime.UtcNow"/> rather than a fixed instant, deliberately.</b> Every call here
    /// therefore passes a different <c>serverTime</c>, so the whole of this file is also a standing
    /// assertion that it does not reach the hash — if it ever did, the equality tests below would fail
    /// on the first run rather than the day a device stopped receiving 304s.
    /// </para>
    /// </summary>
    private static string Compute(
        EventManifestEventDto @event,
        IReadOnlyList<EventManifestGroupDto> groups,
        IReadOnlyList<EventManifestAttendeeDto> attendees) =>
        EventManifestVersion.Compute(new EventManifestDto(
            @event, groups, attendees,
            ServerTime: DateTime.UtcNow,
            Version: EventManifestVersion.PendingVersion));

    // ------------------------------------------------------------------- stability across processes

    /// <summary>
    /// <b>The version of a known manifest, written down.</b> This is the automated form of the check
    /// ray-backend performed by hand — kill the process, restart it, confirm the version is the same —
    /// and it is the only test here that can fail on a <c>string.GetHashCode()</c> implementation.
    ///
    /// <para>
    /// <b>Why a recorded literal is a genuine cross-process assertion and not a trick.</b> .NET
    /// randomizes string hashing <em>per process</em>, so a <c>GetHashCode</c>-derived version is a
    /// different value in every run. The constant below was produced by one process and is compared in
    /// every later one, so any implementation whose output depends on the process cannot match it twice
    /// — it would fail on the very next run, and on CI, and on another machine. A same-process test that
    /// hashes twice and compares cannot do this: <c>GetHashCode</c> is perfectly stable <em>within</em>
    /// a process, which is exactly why the bug survives a green dev suite.
    /// </para>
    ///
    /// <para>
    /// <b>Updating this constant is a contract change, not a fix.</b> If it fails, either the canonical
    /// form changed — in which case every cached device must be told, which is what
    /// <see cref="EventManifestVersion.ContractShapePrefix"/> is for — or the hash is no longer a hash.
    /// Re-recording the value to make the build green discards both signals.
    /// </para>
    ///
    /// <para>
    /// <b>It has been re-recorded exactly once, and this is the reason.</b> The previous value,
    /// <c>m1.3R7oFr7l998m15MqVWTj0W</c>, was the hash of a hand-written mirror of the published fields.
    /// That mirror is gone: the whole <see cref="EventManifestDto"/> is now hashed with <c>serverTime</c>
    /// and <c>version</c> dropped structurally, so a field added to the DTO cannot escape the hash — the
    /// failure the mirror made possible and no test could see. The canonical bytes changed with it (the
    /// top-level members are ordered by name now, not by the mirror's declaration order), so this
    /// constant legitimately moved. <b>The published body did not change by a byte and the DTO's shape
    /// did not change</b>, which is why <see cref="EventManifestVersion.ContractShapePrefix"/> stays at
    /// <c>m1.</c>: every cached device takes one needless 200 and then goes on 304ing, which is the safe
    /// direction of this failure.
    /// </para>
    /// </summary>
    [Fact]
    public void The_version_of_a_known_manifest_is_a_recorded_constant()
    {
        Assert.Equal("m1.mR3uiJlDfK4zQvEHyXV9JQ", VersionOf(Sample()));
    }

    /// <summary>
    /// Two graphs built independently — separate <see cref="Sample"/> calls, no shared reference —
    /// agree. Weaker than the recorded constant above and kept because it is the property clients
    /// actually depend on, stated directly.
    /// </summary>
    [Fact]
    public void Two_independently_constructed_manifests_of_identical_content_share_a_version() =>
        Assert.Equal(VersionOf(Sample()), VersionOf(Sample()));

    /// <summary>
    /// The published shape: the contract prefix, then 22 base64url characters and nothing else. A
    /// padding <c>=</c> or a <c>+</c>/<c>/</c> from plain base64 survives quoting and proxying less
    /// reliably, and the width is what keeps a collision out of reach.
    /// </summary>
    [Fact]
    public void The_version_is_the_contract_prefix_and_twenty_two_base64url_characters()
    {
        var version = VersionOf(Sample());

        Assert.StartsWith(EventManifestVersion.ContractShapePrefix, version, StringComparison.Ordinal);

        var digest = version[EventManifestVersion.ContractShapePrefix.Length..];
        Assert.Equal(22, digest.Length);
        Assert.All(digest, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_',
            $"'{c}' is outside the base64url alphabet; the ETag must not carry +, / or =."));
    }

    /// <summary>
    /// <b><c>Compute</c> takes the whole published manifest, not a hand-picked list of its parts.</b>
    ///
    /// <para>
    /// This is the shape that makes exclusion the enumerated thing. Given the assembled DTO, everything
    /// on it is hashed except the two properties named in <c>ExcludedFromTheHash</c> — so a field added
    /// to <see cref="EventManifestDto"/> next quarter is inside the version by default. A signature
    /// taking the components separately reads as equivalent and is not: the new field would simply not
    /// be passed, the hash would not move, nobody would bump the contract prefix, and every cached
    /// device would 304 forever against a shape that no longer exists.
    /// </para>
    ///
    /// <para>
    /// The other half — that <c>serverTime</c> still cannot reach the hash even though it now arrives
    /// inside the parameter — is asserted by every equality test in this file, since <see cref="Compute"/>
    /// passes a fresh <see cref="DateTime.UtcNow"/> on each call.
    /// </para>
    /// </summary>
    [Fact]
    public void Compute_takes_the_whole_published_manifest()
    {
        var parameters = typeof(EventManifestVersion)
            .GetMethod(nameof(EventManifestVersion.Compute), BindingFlags.Public | BindingFlags.Static)!
            .GetParameters();

        var parameter = Assert.Single(parameters);
        Assert.Equal(typeof(EventManifestDto), parameter.ParameterType);
    }

    // ------------------------------------------------------------------------- content sensitivity

    /// <summary>
    /// Every published field is inside the hash. One case per field rather than one omnibus edit,
    /// so a field that silently stopped being hashed names itself.
    /// </summary>
    public static TheoryData<string, EventManifestEventDto> ChangedEvents() => new()
    {
        { "id", new EventManifestEventDto(Guid.NewGuid(), "University Convocation 2026", StartAt, EndAt, 15, "Single", "Open") },
        { "name", new EventManifestEventDto(EventId, "Renamed Convocation", StartAt, EndAt, 15, "Single", "Open") },
        { "startAt", new EventManifestEventDto(EventId, "University Convocation 2026", StartAt.AddMinutes(1), EndAt, 15, "Single", "Open") },
        { "endAt", new EventManifestEventDto(EventId, "University Convocation 2026", StartAt, EndAt.AddMinutes(1), 15, "Single", "Open") },
        { "graceMinutes", new EventManifestEventDto(EventId, "University Convocation 2026", StartAt, EndAt, 20, "Single", "Open") },
        { "attendanceMode", new EventManifestEventDto(EventId, "University Convocation 2026", StartAt, EndAt, 15, "TimeInOut", "Open") },
        { "status", new EventManifestEventDto(EventId, "University Convocation 2026", StartAt, EndAt, 15, "Single", "Closed") },
    };

    [Theory]
    [MemberData(nameof(ChangedEvents))]
    public void A_change_to_any_published_event_field_moves_the_version(
        string field, EventManifestEventDto changed)
    {
        var (_, groups, attendees) = Sample();

        Assert.True(
            VersionOf(Sample()) != Compute(changed, groups, attendees),
            $"event.{field} changed and the version did not. A device would 304 forever against a " +
            "manifest that no longer describes the event.");
    }

    public static TheoryData<string, EventManifestGroupDto> ChangedGroups() => new()
    {
        { "studentGroupId", new EventManifestGroupDto(Guid.NewGuid(), "BSCRIM 2-A", "Section") },
        { "name", new EventManifestGroupDto(GroupOne, "BSCRIM 2-A (renamed)", "Section") },
        { "type", new EventManifestGroupDto(GroupOne, "BSCRIM 2-A", "Program") },
    };

    [Theory]
    [MemberData(nameof(ChangedGroups))]
    public void A_change_to_an_attached_group_moves_the_version(
        string field, EventManifestGroupDto changed)
    {
        var (@event, groups, attendees) = Sample();
        IReadOnlyList<EventManifestGroupDto> edited = [changed, groups[1]];

        Assert.True(
            VersionOf(Sample()) != Compute(@event, edited, attendees),
            $"groups[].{field} changed and the version did not.");
    }

    public static TheoryData<string, EventManifestAttendeeDto> ChangedAttendees() => new()
    {
        { "studentId", new EventManifestAttendeeDto(Guid.NewGuid(), "2023-0001", "Maria Reyes Santos", [GroupOne, GroupTwo], ["0012503301"]) },
        { "studentNumber", new EventManifestAttendeeDto(StudentOne, "2023-9999", "Maria Reyes Santos", [GroupOne, GroupTwo], ["0012503301"]) },
        { "fullName", new EventManifestAttendeeDto(StudentOne, "2023-0001", "Maria R. Santos", [GroupOne, GroupTwo], ["0012503301"]) },
        { "groupIds (removed)", new EventManifestAttendeeDto(StudentOne, "2023-0001", "Maria Reyes Santos", [GroupOne], ["0012503301"]) },
        { "cardUids (reissued)", new EventManifestAttendeeDto(StudentOne, "2023-0001", "Maria Reyes Santos", [GroupOne, GroupTwo], ["0012503301", "0012503999"]) },
        { "cardUids (deactivated)", new EventManifestAttendeeDto(StudentOne, "2023-0001", "Maria Reyes Santos", [GroupOne, GroupTwo], []) },
    };

    [Theory]
    [MemberData(nameof(ChangedAttendees))]
    public void A_change_to_a_listed_attendee_moves_the_version(
        string field, EventManifestAttendeeDto changed)
    {
        var (@event, groups, attendees) = Sample();
        IReadOnlyList<EventManifestAttendeeDto> edited = [changed, attendees[1]];

        Assert.True(
            VersionOf(Sample()) != Compute(@event, groups, edited),
            $"attendees[].{field} changed and the version did not.");
    }

    /// <summary>
    /// A student leaving the manifest entirely moves it — the delete case the rejected
    /// <c>max(UpdatedAt)</c> watermark could not see, because the row that carried the timestamp is the
    /// row that went away.
    /// </summary>
    [Fact]
    public void Removing_an_attendee_moves_the_version()
    {
        var (@event, groups, attendees) = Sample();

        Assert.NotEqual(
            VersionOf(Sample()),
            Compute(@event, groups, [attendees[0]]));
    }

    /// <summary>
    /// <b>The canonical ordering is load-bearing, and this is what says so.</b> The hash is over bytes,
    /// so a differently-ordered list of the same rows is a different version — which is why the service
    /// sorts in memory (SQL Server orders <c>uniqueidentifier</c> by a byte order that is not
    /// <see cref="Guid.CompareTo(Guid)"/>'s) rather than trusting an <c>ORDER BY</c>.
    ///
    /// <para>
    /// If this ever starts failing because the hash was made order-insensitive, that is not an
    /// improvement: it would also stop distinguishing two manifests that genuinely publish their rows
    /// in a different order, and the ordering is part of the published body.
    /// </para>
    /// </summary>
    [Fact]
    public void The_hash_is_over_the_bytes_and_therefore_over_the_order()
    {
        var (@event, groups, attendees) = Sample();

        Assert.NotEqual(
            VersionOf(Sample()),
            Compute(@event, groups, [attendees[1], attendees[0]]));
    }

    /// <summary>
    /// The same instant with a different <see cref="DateTimeKind"/> is the same manifest.
    ///
    /// <para>
    /// <b>This is the two-hosts-disagree case.</b> <c>"O"</c> renders <c>Utc</c> with a trailing
    /// <c>Z</c> and <c>Unspecified</c> without one, so without normalization two instances reading the
    /// same row through different paths would publish two versions of an identical manifest and every
    /// device between them would re-download on every pull — while nothing anywhere reported an error.
    /// </para>
    /// </summary>
    [Fact]
    public void The_datetime_kind_does_not_change_the_version()
    {
        var (_, groups, attendees) = Sample();

        var unspecified = new EventManifestEventDto(
            EventId, "University Convocation 2026",
            DateTime.SpecifyKind(StartAt, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(EndAt, DateTimeKind.Unspecified),
            15, "Single", "Open");

        Assert.Equal(
            VersionOf(Sample()),
            Compute(unspecified, groups, attendees));
    }

    /// <summary>
    /// <c>0012503301</c> keeps its leading zeros through the hash, because it is hashed as a string.
    /// The two UIDs below are the same number and different cards; a numeric conversion anywhere in the
    /// pipeline collapses them, and it collapses them identically on both sides of the wire, so nothing
    /// reports an error.
    /// </summary>
    [Fact]
    public void A_card_uid_that_differs_only_in_leading_zeros_is_a_different_manifest()
    {
        var (@event, groups, attendees) = Sample();

        var withoutZeros = new EventManifestAttendeeDto(
            StudentOne, "2023-0001", "Maria Reyes Santos", [GroupOne, GroupTwo], ["12503301"]);

        Assert.NotEqual(
            VersionOf(Sample()),
            Compute(@event, groups, [withoutZeros, attendees[1]]));
    }

    // ------------------------------------------------------------------------- the ETag and matching

    /// <summary>The header form: weak, quoted, and carrying the version verbatim.</summary>
    [Fact]
    public void The_etag_is_the_weak_quoted_form_of_the_version() =>
        Assert.Equal("W/\"m1.abc\"", EventManifestVersion.ETagFor("m1.abc"));

    /// <summary>
    /// The header we emit, sent back to us unchanged, matches. The obvious property, and the one a
    /// device gets for free by echoing the <c>ETag</c> it was given.
    /// </summary>
    [Fact]
    public void The_etag_we_emit_matches_when_it_is_sent_straight_back() =>
        Assert.True(EventManifestVersion.Matches(
            [EventManifestVersion.ETagFor("m1.abc")], "m1.abc"));

    /// <summary>
    /// Every spelling a real client or an intermediary produces. Refusing any of these would downgrade
    /// a correct conditional request into a full re-download of a body the device already holds — the
    /// entire feature — while telling nobody.
    /// </summary>
    [Theory]
    [InlineData("W/\"m1.abc\"")]
    [InlineData("w/\"m1.abc\"")]
    [InlineData("\"m1.abc\"")]
    [InlineData("m1.abc")]
    [InlineData("  W/\"m1.abc\"  ")]
    [InlineData("W/ \"m1.abc\"")]
    [InlineData("*")]
    [InlineData("\"m1.stale\", W/\"m1.abc\"")]
    [InlineData("W/\"m1.stale\",W/\"m1.abc\"")]
    [InlineData("m1.stale, m1.abc")]
    public void A_matching_validator_in_any_accepted_spelling_matches(string ifNoneMatch) =>
        Assert.True(EventManifestVersion.Matches([ifNoneMatch], "m1.abc"));

    /// <summary>
    /// Several header lines rather than one comma-separated value — the other way an HTTP stack can
    /// present the same set.
    /// </summary>
    [Fact]
    public void A_match_on_any_of_several_header_lines_matches() =>
        Assert.True(EventManifestVersion.Matches(["W/\"m1.stale\"", "W/\"m1.abc\""], "m1.abc"));

    /// <summary>
    /// The negatives. <b>A near-miss must not match</b> — a version that 304'd on a value close to the
    /// real one would pin a device to a manifest it can never refresh, which is worse than never
    /// caching at all.
    /// </summary>
    [Theory]
    [InlineData("W/\"m1.stale\"")]
    [InlineData("\"m1.ABC\"")]
    [InlineData("m1.abcd")]
    [InlineData("m1.ab")]
    [InlineData("m0.abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData("W/\"\"")]
    public void A_stale_empty_or_near_miss_validator_does_not_match(string ifNoneMatch) =>
        Assert.False(EventManifestVersion.Matches([ifNoneMatch], "m1.abc"));

    [Fact]
    public void No_header_at_all_does_not_match() =>
        Assert.False(EventManifestVersion.Matches([], "m1.abc"));

    [Fact]
    public void A_null_header_value_does_not_match() =>
        Assert.False(EventManifestVersion.Matches([null], "m1.abc"));
}
