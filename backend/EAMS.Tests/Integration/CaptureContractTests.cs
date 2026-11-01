using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The old-contract snapshot: what a capture client that has never opted into anything new receives
/// from the tap, tap-batch and manifest routes, compared against a frozen copy of the published
/// schemas (<c>Fixtures/capture-contract.v1.json</c>).
///
/// <para>
/// <b>Why it exists.</b> The published OpenAPI document is closed (<c>additionalProperties: false</c>)
/// and the mobile client is built by someone else; a client generated in strict mode crashes on one
/// unexpected field. Personnel attendance adds fields, so a request that does not opt in must keep
/// receiving exactly today's bodies. This is that guarantee, and it was green before the capture path
/// changed.
/// </para>
///
/// <para>
/// <b>It reads the frozen file, never <c>docs/api/openapi.json</c>.</b> The live document follows
/// every future change and would protect nothing. Updating the fixture is a deliberate contract
/// decision, not a way to make this test pass.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CaptureContractTests : IntegrationTest
{
    public CaptureContractTests(SqlServerFixture sql) : base(sql) { }

    private const string StoredUid = "04A7B8C9";
    private const string PersonnelUid = "05D4E5F6";
    private const string TapRoute = "/api/v1/attendance/tap";
    private const string BatchRoute = "/api/v1/attendance/tap/batch";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId, string ApiKey);

    private async Task<World> ArrangeAsync()
    {
        Guid schoolId, eventId, studentId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
            db.Schools.Add(school);
            var student = TestData.NewStudent(school.Id);
            db.Students.Add(student);
            db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, StoredUid));
            var ev = TestData.NewLiveEvent(school.Id, EventStatus.Open);
            db.Events.Add(ev);
            await db.SaveChangesAsync();
            schoolId = school.Id;
            eventId = ev.Id;
            studentId = student.Id;
        }

        return new World(schoolId, eventId, studentId, await IssueDeviceKeyAsync(schoolId));
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertConforms(JsonElement body, string schemaName)
    {
        var violations = new List<string>();
        FrozenContract.Check(body, schemaName, "$", violations);
        Assert.True(
            violations.Count == 0,
            $"Response no longer matches frozen schema '{schemaName}':{Environment.NewLine}"
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public async Task CaptureContract_recorded_student_tap_matches_the_frozen_TapResult()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(TapRoute, new
        {
            eventId = world.EventId, cardUid = StoredUid, deviceTapId = "cc-0001",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("Recorded", body.GetProperty("code").GetString());
        AssertConforms(body, "TapResult");
    }

    [Fact]
    public async Task CaptureContract_unknown_card_tap_matches_the_frozen_CardNotFound_problem()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(TapRoute, new
        {
            eventId = world.EventId, cardUid = "NOSUCHCARD", deviceTapId = "cc-0002",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("CardNotFound", body.GetProperty("code").GetString());
        AssertConforms(body, "CardNotFoundProblem");
    }

    [Fact]
    public async Task CaptureContract_mixed_batch_matches_the_frozen_TapBatchResult()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(BatchRoute, new
        {
            clientClockAt = DateTime.UtcNow,
            taps = new[]
            {
                new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = "cc-0003" },
                new { eventId = world.EventId, cardUid = "NOSUCHCARD", deviceTapId = "cc-0004" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        var codes = body.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("code").GetString()).ToList();
        Assert.Equal(["Recorded", "CardNotFound"], codes);
        AssertConforms(body, "TapBatchResult");
    }

    [Fact]
    public async Task CaptureContract_open_event_manifest_matches_the_frozen_EventManifestDto()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        // Audience: one section and the student in it, so groups, attendees, groupIds and cardUids are
        // all non-empty and the comparer actually descends into every nested schema.
        await using (var db = NewDbContext())
        {
            var group = TestData.NewGroup(world.SchoolId, "CC-2-A");
            db.StudentGroups.Add(group);
            db.StudentGroupMembers.Add(
                new StudentGroupMember { StudentGroupId = group.Id, StudentId = world.StudentId });
            db.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentGroupId = group.Id });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/v1/events/{world.EventId}/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Single(body.GetProperty("groups").EnumerateArray());
        var attendee = Assert.Single(body.GetProperty("attendees").EnumerateArray());
        Assert.Single(attendee.GetProperty("groupIds").EnumerateArray());
        Assert.Single(attendee.GetProperty("cardUids").EnumerateArray());
        AssertConforms(body, "EventManifestDto");
    }

    /// <summary>
    /// ADR-015 D-99: <c>GET /students/by-card/{cardUid}</c> and its <c>StudentDto</c> are unchanged, and
    /// there is never a school-wide personnel lookup. The personnel card is the point of the second half:
    /// this route must keep answering as if it does not exist.
    /// </summary>
    [Fact]
    public async Task CaptureContract_students_by_card_matches_the_frozen_StudentDto()
    {
        var world = await ArrangeAsync();
        await AddPersonnelCardAsync(world);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.GetAsync($"/api/v1/students/by-card/{StoredUid}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Single(body.GetProperty("cards").EnumerateArray());
        AssertConforms(body, "StudentDto");

        var personnelCard = await client.GetAsync($"/api/v1/students/by-card/{PersonnelUid}");
        Assert.Equal(HttpStatusCode.NotFound, personnelCard.StatusCode);
    }

    /// <summary>
    /// #705 old-client test 1: a capture response produced without the header validates against the frozen
    /// snapshot <b>including a personnel tap</b>, which is the case the snapshot was taken to protect.
    /// </summary>
    [Fact]
    public async Task CaptureContract_personnel_tap_without_the_header_matches_the_frozen_CardNotFound_problem()
    {
        var world = await ArrangeAsync();
        await AddPersonnelCardAsync(world);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(TapRoute, new
        {
            eventId = world.EventId, cardUid = PersonnelUid, deviceTapId = "cc-0005",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("CardNotFound", body.GetProperty("code").GetString());
        AssertConforms(body, "CardNotFoundProblem");
    }

    /// <inheritdoc cref="CaptureContract_personnel_tap_without_the_header_matches_the_frozen_CardNotFound_problem"/>
    [Fact]
    public async Task CaptureContract_batch_with_a_personnel_row_without_the_header_matches_the_frozen_TapBatchResult()
    {
        var world = await ArrangeAsync();
        await AddPersonnelCardAsync(world);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(world.ApiKey);

        var response = await client.PostAsJsonAsync(BatchRoute, new
        {
            clientClockAt = DateTime.UtcNow,
            taps = new[]
            {
                new { eventId = world.EventId, cardUid = StoredUid, deviceTapId = "cc-0006" },
                new { eventId = world.EventId, cardUid = PersonnelUid, deviceTapId = "cc-0007" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(
            ["Recorded", "CardNotFound"],
            body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("code").GetString()));
        AssertConforms(body, "TapBatchResult");
    }

    /// <summary>
    /// The fixture's <c>enum</c> is only worth having if the comparer reads it. Neither personnel token may
    /// ever reach a client that did not opt in, on a success body or a refusal.
    /// </summary>
    [Theory]
    [InlineData("PersonnelInactive")]
    [InlineData("PersonnelNotInAudience")]
    public void CaptureContract_a_personnel_token_is_outside_the_frozen_code_enum(string token)
    {
        using var document = JsonDocument.Parse(
            $$"""{"status":404,"title":"t","detail":"d","traceId":"x","code":"{{token}}","serverTime":"2026-10-31T00:00:00Z"}""");

        var violations = new List<string>();
        FrozenContract.Check(document.RootElement, "CardNotFoundProblem", "$", violations);

        Assert.Contains(violations, v => v.Contains(token, StringComparison.Ordinal));
    }

    private async Task AddPersonnelCardAsync(World world)
    {
        await using var db = NewDbContext();
        db.Personnel.Add(TestData.NewPersonnel(world.SchoolId, rfidUid: PersonnelUid));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Recursive comparer over the frozen file. Exact property set (extra and missing both fail),
    /// JSON value kind, <c>format</c> for uuid/date-time, and nullability as declared. Deliberately
    /// small: it understands only the keywords the frozen schemas use, and throws on any other so a
    /// fixture edit cannot quietly disable a check.
    /// </summary>
    private static class FrozenContract
    {
        private static readonly JsonElement Schemas = Load();

        private static JsonElement Load()
        {
            var path = Path.Combine(
                AppContext.BaseDirectory, "Integration", "Fixtures", "capture-contract.v1.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("schemas").Clone();
        }

        private static readonly HashSet<string> KnownKeywords =
            ["type", "properties", "additionalProperties", "items", "format", "nullable", "$ref", "enum"];

        public static void Check(JsonElement value, string schemaName, string path, List<string> errors) =>
            Check(value, Schemas.GetProperty(schemaName), path, errors);

        private static bool IsNullable(JsonElement schema) =>
            schema.TryGetProperty("nullable", out var n) && n.GetBoolean();

        private static void Check(JsonElement value, JsonElement schema, string path, List<string> errors)
        {
            foreach (var keyword in schema.EnumerateObject())
            {
                if (!KnownKeywords.Contains(keyword.Name))
                    throw new InvalidOperationException(
                        $"Fixture uses unsupported keyword '{keyword.Name}' at {path}.");
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!IsNullable(schema))
                    errors.Add($"{path}: null where the schema is not nullable");
                return;
            }

            if (schema.TryGetProperty("$ref", out var reference))
            {
                var text = reference.GetString()!;
                Check(value, Schemas.GetProperty(text[(text.LastIndexOf('/') + 1)..]), path, errors);
                return;
            }

            var type = schema.GetProperty("type").GetString();
            switch (type)
            {
                case "object":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"{path}: expected object, got {value.ValueKind}");
                        return;
                    }

                    var declared = schema.GetProperty("properties").EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value);
                    var actual = value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                    foreach (var extra in actual.Keys.Except(declared.Keys).Order(StringComparer.Ordinal))
                        errors.Add($"{path}: unexpected extra property '{extra}'");
                    foreach (var missing in declared.Keys.Except(actual.Keys).Order(StringComparer.Ordinal))
                        errors.Add($"{path}: missing property '{missing}'");
                    foreach (var (name, child) in declared)
                    {
                        if (actual.TryGetValue(name, out var childValue))
                            Check(childValue, child, $"{path}.{name}", errors);
                    }

                    break;

                case "array":
                    if (value.ValueKind != JsonValueKind.Array)
                    {
                        errors.Add($"{path}: expected array, got {value.ValueKind}");
                        return;
                    }

                    var items = schema.GetProperty("items");
                    var i = 0;
                    foreach (var element in value.EnumerateArray())
                        Check(element, items, $"{path}[{i++}]", errors);
                    break;

                case "string":
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        errors.Add($"{path}: expected string, got {value.ValueKind}");
                        return;
                    }

                    if (schema.TryGetProperty("enum", out var allowed)
                        && allowed.EnumerateArray().All(token => token.GetString() != value.GetString()))
                    {
                        errors.Add($"{path}: '{value.GetString()}' is not a token a v1 client was promised");
                    }

                    if (schema.TryGetProperty("format", out var format))
                    {
                        var s = value.GetString()!;
                        var ok = format.GetString() switch
                        {
                            "uuid" => Guid.TryParse(s, out _),
                            "date-time" => DateTime.TryParse(
                                s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
                            var other => throw new InvalidOperationException(
                                $"Unsupported string format '{other}' at {path}."),
                        };
                        if (!ok) errors.Add($"{path}: '{s}' is not a valid {format.GetString()}");
                    }

                    break;

                case "integer":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
                        errors.Add($"{path}: expected integer, got {value.ValueKind}");
                    break;

                case "boolean":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        errors.Add($"{path}: expected boolean, got {value.ValueKind}");
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported type '{type}' at {path}.");
            }
        }
    }
}
