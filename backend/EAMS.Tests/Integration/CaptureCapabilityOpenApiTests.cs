using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Cors;
using EAMS.Application.Abstractions;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 phase B1c-2, what the generated contract says about the capability split (D-99): the v2 schemas
/// are components reached through <c>x-eams-capability-response</c>, and each device operation's documented
/// <c>200</c> stays v1, so a client regenerated without opting in is unchanged. Plus the two header
/// promises the same phase made: <c>503</c> on the lock routes, and <c>Retry-After</c> readable by the SPA.
/// <c>OpenApiDocumentTests</c> holds the rest of the document.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CaptureCapabilityOpenApiTests : IntegrationTest
{
    public CaptureCapabilityOpenApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Extension = "x-eams-capability-response";

    private const string Tap = "/api/v1/attendance/tap";
    private const string TapBatch = "/api/v1/attendance/tap/batch";
    private const string Manifest = "/api/v1/events/{id}/manifest";
    private const string ByCard = "/api/v1/events/{id}/by-card/{cardUid}";

    private async Task<JsonDocument> DocumentAsync()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        return JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
    }

    private static JsonElement Operation(JsonDocument document, string path, string method) =>
        document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static string SchemaRefOf(JsonElement responses, string status) =>
        responses.GetProperty(status).GetProperty("content").EnumerateObject()
            .Select(media => media.Value.GetProperty("schema").GetProperty("$ref").GetString()!)
            .First()
            .Split('/')[^1];

    /// <summary>D-99: the operation points at v2 and its documented 200 stays v1.</summary>
    [Theory]
    [InlineData(Tap, "post", "TapResult", "TapResultV2")]
    [InlineData(TapBatch, "post", "TapBatchResult", "TapBatchResultV2")]
    [InlineData(Manifest, "get", "EventManifestDto", "EventManifestDtoV2")]
    public async Task Each_capture_operation_keeps_a_v1_200_and_references_its_v2_schema_by_extension(
        string path, string method, string v1, string v2)
    {
        using var document = await DocumentAsync();
        var operation = Operation(document, path, method);

        Assert.Equal(v1, SchemaRefOf(operation.GetProperty("responses"), "200"));

        var extension = operation.GetProperty(Extension);
        Assert.Equal(CaptureCapabilities.HeaderName, extension.GetProperty("header").GetString());
        Assert.Equal(CaptureCapabilities.PersonnelToken, extension.GetProperty("token").GetString());
        Assert.Equal(200, extension.GetProperty("status").GetInt32());
        Assert.Equal(
            $"#/components/schemas/{v2}", extension.GetProperty("schema").GetProperty("$ref").GetString());

        Assert.True(
            document.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty(v2, out _),
            $"{v2} is referenced but is not a component.");
    }

    /// <summary>
    /// A declared parameter would add an argument to every regenerated call, which is the change D-99
    /// says a client that did not opt in must not see.
    /// </summary>
    [Theory]
    [InlineData(Tap, "post")]
    [InlineData(TapBatch, "post")]
    [InlineData(Manifest, "get")]
    public async Task The_capability_header_is_not_a_declared_parameter(string path, string method)
    {
        using var document = await DocumentAsync();
        var operation = Operation(document, path, method);

        if (!operation.TryGetProperty("parameters", out var parameters)) return;

        Assert.DoesNotContain(
            parameters.EnumerateArray(),
            p => p.GetProperty("name").GetString() == CaptureCapabilities.HeaderName);
    }

    /// <summary>Only the routes that honour the header carry it: D-100 says it is ignored elsewhere.</summary>
    [Theory]
    [InlineData(ByCard, "get")]
    [InlineData("/api/v1/attendance/manual", "post")]
    [InlineData("/api/v1/attendance", "get")]
    public async Task A_route_that_ignores_the_header_carries_no_extension(string path, string method)
    {
        using var document = await DocumentAsync();

        Assert.False(Operation(document, path, method).TryGetProperty(Extension, out _));
    }

    /// <summary>#705 guardrail 2, in the contract: the published item is exactly the four members.</summary>
    [Fact]
    public async Task The_published_personnel_item_is_exactly_the_four_permitted_members()
    {
        using var document = await DocumentAsync();

        var properties = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("EventManifestPersonnelDto").GetProperty("properties");

        Assert.Equal(
            ["cardUids", "fullName", "personnelId", "personnelNumber"],
            properties.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_event_card_lookup_publishes_its_responses_and_the_device_key()
    {
        using var document = await DocumentAsync();
        var operation = Operation(document, ByCard, "get");
        var responses = operation.GetProperty("responses");

        Assert.Equal(
            ["200", "400", "401", "403", "404", "429"],
            responses.EnumerateObject().Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal("EventCardHolderDto", SchemaRefOf(responses, "200"));
        Assert.Equal("ProblemDetails", SchemaRefOf(responses, "404"));

        var requirement = Assert.Single(operation.GetProperty("security").EnumerateArray());
        Assert.Equal("DeviceKey", Assert.Single(requirement.EnumerateObject()).Name);
    }

    /// <summary>The five writes that take the card-UID lock (ADR-015 D-98; JJ, 2026-10-28, decision 5).</summary>
    [Theory]
    [InlineData("/api/v1/students/{id}/cards", "post")]
    [InlineData("/api/v1/personnel", "post")]
    [InlineData("/api/v1/personnel/{id}", "put")]
    [InlineData("/api/v1/personnel/import", "post")]
    [InlineData("/api/v1/sis/import/{batchId}/run", "post")]
    public async Task Each_lock_route_declares_its_503(string path, string method)
    {
        using var document = await DocumentAsync();

        var responses = Operation(document, path, method).GetProperty("responses");

        Assert.Equal("ProblemDetails", SchemaRefOf(responses, "503"));
        Assert.Contains(
            "Retry-After", responses.GetProperty("503").GetProperty("description").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// JJ accepted: the SPA has to read how long a 503 says to wait, and CORS hides any header not on this
    /// list from JavaScript while it stays visible to every non-browser test.
    /// </summary>
    [Fact]
    public async Task Retry_after_is_exposed_to_the_browser_beside_location()
    {
        await using (var db = NewDbContext())
        {
            db.Schools.Add(TestData.NewSchool());
            await db.SaveChangesAsync();
        }

        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/students")
        {
            Content = JsonContent.Create(new { studentNumber = "2023-0777", firstName = "Juan", lastName = "Dela Cruz" }),
        };
        request.Headers.Add("Origin", LocalDevelopmentCors.ViteDevServerOrigin);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposed));

        var names = exposed.SelectMany(v => v.Split(',')).Select(h => h.Trim()).ToList();
        Assert.Contains("Retry-After", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Location", names, StringComparer.OrdinalIgnoreCase);
    }
}
