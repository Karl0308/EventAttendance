using System.Net;
using System.Text.Json;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>Live Attendance's search</b> — client QA Q7 (MDVault #427/#463): filter by Student No., Name and
/// RFID card number, and never show a student who has not tapped.
///
/// <para>
/// <b>Through HTTP, signed in, on SQL Server.</b> The filters are <c>LIKE</c> predicates whose case
/// behaviour is the column collation's and whose tenant scope is the global query filter under a real
/// token's <c>school_id</c> — none of which exists under EF InMemory, and all of which is what QA will
/// exercise.
/// </para>
///
/// <para>
/// <b>Every arrange includes a student who would match and has no attendance row.</b> That is the half
/// of Q7 a filter can silently break: a search that joined from students instead of from attendance
/// would pass every positive assertion here and still list people who never came.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceListFilterTests : IntegrationTest
{
    public AttendanceListFilterTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/attendance";

    // ------------------------------------------------------------------------------------- arrange

    private sealed record World(Guid SchoolId, Guid EventId);

    private async Task<World> NewWorldAsync(string schoolCode = "USA")
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool(schoolCode);
        db.Schools.Add(school);

        var ev = TestData.NewEvent(school.Id);
        db.Events.Add(ev);

        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id);
    }

    /// <summary>A student holding one card, and nothing recorded against them.</summary>
    private async Task<(Guid StudentId, Guid CardId)> AddStudentAsync(
        World world, string studentNumber, string firstName, string? middleName, string lastName,
        string cardUid, bool cardActive = true)
    {
        await using var db = NewDbContext();

        var student = TestData.NewStudent(
            world.SchoolId, studentNumber, firstName, middleName, lastName);
        db.Students.Add(student);

        var card = TestData.NewCard(world.SchoolId, student.Id, cardUid, cardActive);
        db.RfidCards.Add(card);

        await db.SaveChangesAsync();
        return (student.Id, card.Id);
    }

    /// <summary>A card tap: the row carries the card that made it, as the capture path writes it.</summary>
    private async Task TapAsync(World world, Guid studentId, Guid cardId)
    {
        await using var db = NewDbContext();
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = world.SchoolId,
            EventId = world.EventId,
            StudentId = studentId,
            RfidCardId = cardId,
            CheckInAt = TestData.Now,
            Status = AttendanceStatus.Present,
            CaptureMethod = CaptureMethod.Rfid,
            DeviceTapId = Guid.NewGuid().ToString("N"),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>An organizer's manual entry: no card, as <c>ManualAsync</c> creates it.</summary>
    private async Task ManualEntryAsync(World world, Guid studentId)
    {
        await using var db = NewDbContext();
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = world.SchoolId,
            EventId = world.EventId,
            StudentId = studentId,
            RfidCardId = null,
            CheckInAt = TestData.Now,
            Status = AttendanceStatus.Present,
            CaptureMethod = CaptureMethod.Manual,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Deactivates a card, the way a replacement does — the old row survives to explain its taps.</summary>
    private async Task DeactivateCardAsync(Guid cardId)
    {
        await using var db = NewDbContext();
        var card = await db.RfidCards.FindAsync(cardId);
        Assert.NotNull(card);
        card.IsActive = false;
        card.DeactivatedAt = TestData.Now;
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------------------------------ act

    /// <summary>
    /// The student ids on the one page served, asserted to be the whole result (<c>total</c> agrees),
    /// so a count answering a different filter than the rows cannot hide here.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> StudentsListedAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"{Route}?{query}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {Route}?{query} answered {(int)response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items");
        var ids = items.EnumerateArray().Select(i => i.GetProperty("studentId").GetGuid()).ToList();

        Assert.Equal(json.RootElement.GetProperty("total").GetInt32(), ids.Count);
        return ids;
    }

    private static string Q(string name, string value) => $"{name}={Uri.EscapeDataString(value)}";

    // ------------------------------------------------------------------------------ the filters

    [Fact]
    public async Task Attendance_list_filters_by_student_number_fragment()
    {
        var world = await NewWorldAsync();
        var match = await AddStudentAsync(world, "2023-0001", "Maria", "Reyes", "Santos", "0012503326");
        var other = await AddStudentAsync(world, "2024-0555", "Juan", null, "Cruz", "04A7B8C9");
        await AddStudentAsync(world, "2023-0002", "Ana", null, "Lim", "0077777777"); // no row
        await TapAsync(world, match.StudentId, match.CardId);
        await TapAsync(world, other.StudentId, other.CardId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        // A fragment from the middle, dash included and matched as written.
        var listed = await StudentsListedAsync(
            client, $"eventId={world.EventId}&{Q("studentNumber", "23-00")}");

        Assert.Equal([match.StudentId], listed);
    }

    [Fact]
    public async Task Attendance_list_filters_by_student_name_fragment_case_insensitively()
    {
        var world = await NewWorldAsync();
        var withMiddle = await AddStudentAsync(world, "2023-0001", "Maria", "Reyes", "Santos", "0012503326");
        var noMiddle = await AddStudentAsync(world, "2024-0555", "Juan", null, "Dela Cruz", "04A7B8C9");
        var other = await AddStudentAsync(world, "2024-0600", "Pedro", null, "Garcia", "0055555555");
        await TapAsync(world, withMiddle.StudentId, withMiddle.CardId);
        await TapAsync(world, noMiddle.StudentId, noMiddle.CardId);
        await TapAsync(world, other.StudentId, other.CardId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        // One part, in the wrong case.
        Assert.Equal([withMiddle.StudentId], await StudentsListedAsync(
            client, $"eventId={world.EventId}&{Q("studentName", "sANTOS")}"));

        // A run across first, middle and last in display order, lower-cased.
        Assert.Equal([withMiddle.StudentId], await StudentsListedAsync(
            client, $"eventId={world.EventId}&{Q("studentName", "maria reyes sa")}"));

        // A run across first and last when there is no middle name — the null middle must not break it.
        Assert.Equal([noMiddle.StudentId], await StudentsListedAsync(
            client, $"eventId={world.EventId}&{Q("studentName", "JUAN DELA")}"));
    }

    [Fact]
    public async Task Attendance_list_filters_by_card_uid_fragment_after_normalization()
    {
        var world = await NewWorldAsync();
        var match = await AddStudentAsync(world, "2024-0555", "Juan", null, "Cruz", "04A7B8C9");
        var other = await AddStudentAsync(world, "2023-0001", "Maria", "Reyes", "Santos", "0012503326");
        await AddStudentAsync(world, "2023-0002", "Ana", null, "Lim", "FFA7B8EE"); // no row, would match
        await TapAsync(world, match.StudentId, match.CardId);
        await TapAsync(world, other.StudentId, other.CardId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        // Reader formats: lower case, colons, dashes, spaces — all one search for 'A7B8'.
        foreach (var fragment in new[] { "a7:b8", "A7-B8", "a7 b8", "a7b8" })
        {
            Assert.Equal([match.StudentId], await StudentsListedAsync(
                client, $"eventId={world.EventId}&{Q("cardUid", fragment)}"));
        }
    }

    /// <summary>
    /// <b>JJ's ruling on Q7: the card that made the tap, not a card the student holds.</b> The student
    /// tapped with a card that has since been replaced. The old serial still finds the tap; the new
    /// serial — which never tapped this event — finds nothing.
    /// </summary>
    [Fact]
    public async Task Attendance_list_card_filter_matches_the_card_that_tapped_not_a_card_the_student_holds_now()
    {
        var world = await NewWorldAsync();
        var (studentId, oldCardId) =
            await AddStudentAsync(world, "2023-0001", "Maria", "Reyes", "Santos", "0011110000");
        await TapAsync(world, studentId, oldCardId);

        // The replacement: old card withdrawn, a new active one issued.
        await DeactivateCardAsync(oldCardId);
        await using (var db = NewDbContext())
        {
            db.RfidCards.Add(TestData.NewCard(world.SchoolId, studentId, "0022220000"));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal([studentId], await StudentsListedAsync(
            client, $"eventId={world.EventId}&{Q("cardUid", "1111")}"));

        Assert.Empty(await StudentsListedAsync(
            client, $"eventId={world.EventId}&{Q("cardUid", "2222")}"));
    }

    /// <summary>
    /// A manual entry carries no card, so no card fragment can find it — even a fragment of a card the
    /// student really holds. The unfiltered read is asserted too, so the empty answer is the filter's
    /// and not a missing row's.
    /// </summary>
    [Fact]
    public async Task Attendance_list_card_filter_never_matches_a_manual_entry()
    {
        var world = await NewWorldAsync();
        var (studentId, _) =
            await AddStudentAsync(world, "2023-0777", "Rosa", null, "Mendoza", "0099887766");
        await ManualEntryAsync(world, studentId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal([studentId], await StudentsListedAsync(client, $"eventId={world.EventId}"));

        Assert.Empty(await StudentsListedAsync(
            client, $"eventId={world.EventId}&{Q("cardUid", "998877")}"));
    }

    [Fact]
    public async Task Attendance_list_filters_combine_with_and()
    {
        var world = await NewWorldAsync();
        var both = await AddStudentAsync(world, "2023-0001", "Maria", "Reyes", "Santos", "0012503326");
        var numberOnly = await AddStudentAsync(world, "2023-0450", "Juan", null, "Cruz", "04A7B8C9");
        var nameOnly = await AddStudentAsync(world, "2024-0100", "Lito", null, "Santos", "0012599999");
        foreach (var s in new[] { both, numberOnly, nameOnly })
        {
            await TapAsync(world, s.StudentId, s.CardId);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal([both.StudentId], await StudentsListedAsync(
            client, $"eventId={world.EventId}&{Q("studentNumber", "2023")}&{Q("studentName", "santos")}"));

        // All three at once: the card narrows 'Santos' + '0012' down to the one whose tap card has '2503'.
        Assert.Equal([both.StudentId], await StudentsListedAsync(
            client,
            $"eventId={world.EventId}&{Q("studentName", "Santos")}&{Q("cardUid", "0012-50")}&{Q("studentNumber", "0")}"));

        // AND, not OR: a number that only one matches and a name that only the other matches is nobody.
        Assert.Empty(await StudentsListedAsync(
            client, $"eventId={world.EventId}&{Q("studentNumber", "0450")}&{Q("studentName", "Lito")}"));
    }

    /// <summary>
    /// The student who matches every filter and never tapped. Each filter alone, and all three
    /// together, must still leave them out — Q7's "do not show students who have not yet tapped".
    /// </summary>
    [Fact]
    public async Task Attendance_list_filter_never_returns_a_student_with_no_attendance_row()
    {
        var world = await NewWorldAsync();
        var tapped = await AddStudentAsync(world, "2023-0001", "Maria", "Reyes", "Santos", "0012503326");
        var absent = await AddStudentAsync(world, "2023-0002", "Maria", "Reyes", "Santos", "0012503327");
        await TapAsync(world, tapped.StudentId, tapped.CardId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var queries = new[]
        {
            Q("studentNumber", "2023-000"),
            Q("studentName", "Maria Reyes Santos"),
            Q("cardUid", "00125033"),
            $"{Q("studentNumber", "2023")}&{Q("studentName", "Santos")}&{Q("cardUid", "0012")}",
        };

        foreach (var query in queries)
        {
            var listed = await StudentsListedAsync(client, $"eventId={world.EventId}&{query}");
            Assert.DoesNotContain(absent.StudentId, listed);
            Assert.Equal([tapped.StudentId], listed);
        }
    }

    /// <summary>
    /// Another school holds a row that matches every filter. Without an <c>eventId</c> — the widest
    /// query a caller can make — the signed-in operator still sees only their own school's.
    /// </summary>
    [Fact]
    public async Task Attendance_list_filter_does_not_reach_another_school()
    {
        var mine = await NewWorldAsync("USA");
        var theirs = await NewWorldAsync("ZZZ");

        var own = await AddStudentAsync(mine, "2023-0001", "Maria", "Reyes", "Santos", "0012503326");
        var foreign = await AddStudentAsync(theirs, "2023-0001", "Maria", "Reyes", "Santos", "0012503326");
        await TapAsync(mine, own.StudentId, own.CardId);
        await TapAsync(theirs, foreign.StudentId, foreign.CardId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, mine.SchoolId);

        var queries = new[]
        {
            Q("studentNumber", "2023-0001"),
            Q("studentName", "Santos"),
            Q("cardUid", "2503"),
        };

        foreach (var query in queries)
        {
            Assert.Equal([own.StudentId], await StudentsListedAsync(client, query));
        }
    }

    // ---------------------------------------------------------------------------------- boundary

    /// <summary>
    /// One character past each named limit is a 400 in §6's problem shape; exactly at the limit is
    /// served. The limit comes from <see cref="AttendanceListSearch"/>, not a literal, so a change to
    /// the constant moves this test with it.
    /// </summary>
    [Theory]
    [InlineData("studentNumber", AttendanceListSearch.StudentNumberMaxLength)]
    [InlineData("studentName", AttendanceListSearch.StudentNameMaxLength)]
    [InlineData("cardUid", AttendanceListSearch.CardUidMaxLength)]
    public async Task Attendance_list_refuses_an_overlong_fragment(string parameter, int maxLength)
    {
        var world = await NewWorldAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var atLimit = await client.GetAsync($"{Route}?{Q(parameter, new string('1', maxLength))}");
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);

        var over = await client.GetAsync($"{Route}?{Q(parameter, new string('1', maxLength + 1))}");
        var body = await over.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal("application/problem+json", over.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(body);
        Assert.Equal((int)HttpStatusCode.BadRequest, json.RootElement.GetProperty("status").GetInt32());
        Assert.True(
            json.RootElement.GetProperty("errors").TryGetProperty(parameter, out _),
            $"The validation problem did not name '{parameter}': {body}");
    }

    /// <summary>
    /// '-' is short enough and still unusable: it normalizes to nothing, and <c>Contains("")</c> would
    /// match every tapped row. Refused with the same code <c>GET /cards</c> uses for the same input.
    /// </summary>
    [Theory]
    [InlineData("-")]
    [InlineData(":: --")]
    public async Task Attendance_list_refuses_a_card_fragment_with_no_letter_or_digit(string fragment)
    {
        var world = await NewWorldAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await client.GetAsync($"{Route}?{Q("cardUid", fragment)}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("FragmentUnusable", json.RootElement.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------------ the device contract

    /// <summary>
    /// <b>The Q7 filters must not have widened the row.</b> Devices receive this same
    /// <c>AttendanceDto</c> from <c>POST /attendance/tap</c> and <c>/tap/batch</c>, and its published
    /// schema is closed (<c>additionalProperties: false</c>) — a property added for the admin list would
    /// be a property a strict device client rejects. The set is transcribed, not derived, so a change
    /// has to be made here on purpose.
    /// </summary>
    [Fact]
    public async Task AttendanceDto_published_shape_is_unchanged()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"The OpenAPI document did not generate: {body}");

        using var document = JsonDocument.Parse(body);
        var schema = document.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("AttendanceDto");

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        var published = schema.GetProperty("properties").EnumerateObject()
            .Select(p => (
                p.Name,
                Type: p.Value.GetProperty("type").GetString(),
                Format: p.Value.TryGetProperty("format", out var f) ? f.GetString() : null,
                Nullable: p.Value.TryGetProperty("nullable", out var n) && n.GetBoolean()))
            .ToList();

        (string Name, string? Type, string? Format, bool Nullable)[] expected =
        [
            ("id", "string", "uuid", false),
            ("eventId", "string", "uuid", false),
            ("studentId", "string", "uuid", false),
            ("studentName", "string", null, false),
            ("studentNumber", "string", null, false),
            ("checkInAt", "string", "date-time", true),
            ("checkOutAt", "string", "date-time", true),
            ("status", "string", null, false),
            ("captureMethod", "string", null, false),
        ];

        Assert.Equal(expected, published);
    }
}
