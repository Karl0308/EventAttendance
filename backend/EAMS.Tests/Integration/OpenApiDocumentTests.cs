using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EAMS.Api.OpenApi;
using EAMS.Application.Abstractions;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The generated OpenAPI document — the deliverable of Phase 4 (4e).
///
/// <para>
/// <b>Asserted against the bytes the host actually serves, not against the object model.</b> The
/// document is only worth anything to the mobile developer and the SPA as JSON on the wire, and the
/// two failure modes that matter — a generator that throws, and a generator that quietly emits a
/// document missing the thing you were relying on — are both invisible from inside
/// <c>SwaggerGenOptions</c>. A contract that 500s on generation is worse than no contract at all,
/// because everything upstream of it looks fine.
/// </para>
///
/// <para>
/// Development is the environment that serves it (ADR-001 D-6: an unauthenticated complete description
/// of an API whose admin surface is open hands an attacker the map). The one Production assertion here
/// is the other half of that split — the <em>document</em> must still be buildable for tooling even
/// where the route is gone.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class OpenApiDocumentTests : IntegrationTest
{
    public OpenApiDocumentTests(SqlServerFixture sql) : base(sql) { }

    private const string DocumentRoute = "/swagger/v1/swagger.json";

    private const string Tap = "/api/v1/attendance/tap";
    private const string TapBatch = "/api/v1/attendance/tap/batch";
    private const string Manual = "/api/v1/attendance/manual";
    private const string Live = "/api/v1/attendance/live/{eventId}";
    private const string ByCard = "/api/v1/students/by-card/{cardUid}";
    private const string Heartbeat = "/api/v1/devices/{id}/heartbeat";
    private const string Summary = "/api/v1/events/{id}/summary";
    private const string Manifest = "/api/v1/events/{id}/manifest";

    /// <summary>
    /// The endpoints gated behind a device key — D-28's four plus D-46's manifest pull — and the whole
    /// of that list. Transcribed
    /// rather than derived from the attributes, for the reason the outcome-token tests record: deriving
    /// it would assert that the pipeline equals itself.
    /// </summary>
    private static readonly (string Path, string Method)[] GatedOperations =
    [
        (Tap, "post"),
        (TapBatch, "post"),
        (ByCard, "get"),
        (Heartbeat, "post"),
        (Manifest, "get"),
    ];

    /// <summary>Fetches and parses the served document. Fails loudly if it is not 200 JSON.</summary>
    private async Task<JsonDocument> DocumentAsync()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(DocumentRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"The OpenAPI document did not generate: {(int)response.StatusCode}. Body: {body}");

        return JsonDocument.Parse(body);
    }

    private static JsonElement Operation(JsonDocument document, string path, string method)
    {
        var paths = document.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty(path, out var item), $"'{path}' is not in the document.");
        Assert.True(item.TryGetProperty(method, out var operation), $"'{method} {path}' is not in the document.");

        return operation;
    }

    private static JsonElement Schema(JsonDocument document, string name)
    {
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.True(schemas.TryGetProperty(name, out var schema), $"Schema '{name}' is not in the document.");
        return schema;
    }

    // ------------------------------------------------------------------------------- it generates

    /// <summary>
    /// <b>The document generates, and it is JSON.</b> The cheapest assertion here and the one worth
    /// most: a schema filter that indexes a property that is not there, an operation filter that
    /// dereferences a null, or a missing XML file all surface as a 500 on this route — and nothing
    /// upstream of it fails, so a build could ship with the contract broken while every other test
    /// stayed green.
    /// </summary>
    [Fact]
    public async Task The_document_generates()
    {
        using var document = await DocumentAsync();

        Assert.Equal("EAMS API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.NotEmpty(document.RootElement.GetProperty("paths").EnumerateObject());
    }

    /// <summary>
    /// <b>The UI is Development-only; the document is not.</b> <c>HostPipelineTests</c> pins the route
    /// away in Production. This pins the other half: the generator stays registered there, so client
    /// tooling can still build the contract from a production binary. Collapse the two — gate
    /// <c>AddSwaggerGen</c> as well as <c>UseSwagger</c> — and this is the only test that notices.
    /// </summary>
    [Fact]
    public void The_document_generates_in_production_even_though_it_is_not_served()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient(); // Builds the host.

        using var scope = factory.Services.CreateScope();
        var swagger = scope.ServiceProvider.GetRequiredService<ISwaggerProvider>();

        var document = swagger.GetSwagger(EamsOpenApi.DocumentName);

        Assert.NotNull(document);
        Assert.NotEmpty(document.Paths);
    }

    // ----------------------------------------------------------------------------- what it covers

    /// <summary>
    /// Every endpoint the published contract names is in the document. Path by path rather than by
    /// count, so adding a route cannot make a missing one pass.
    /// </summary>
    [Theory]
    [InlineData(Tap, "post")]
    [InlineData(TapBatch, "post")]
    [InlineData(Manual, "post")]
    [InlineData(Live, "get")]
    [InlineData(ByCard, "get")]
    [InlineData(Heartbeat, "post")]
    [InlineData(Summary, "get")]
    [InlineData("/api/v1/attendance", "get")]
    [InlineData("/api/v1/events/{id}/roster", "get")]
    public async Task The_document_describes_the_published_surface(string path, string method)
    {
        using var document = await DocumentAsync();
        Operation(document, path, method);
    }

    /// <summary>
    /// The reasoning above each action is <em>in</em> the document. This is what
    /// <c>GenerateDocumentationFile</c> and <c>IncludeXmlComments</c> buy, and the failure mode without
    /// them is silent: Swashbuckle emits a perfectly valid document with every description empty.
    /// </summary>
    [Fact]
    public async Task The_document_carries_the_xml_documentation()
    {
        using var document = await DocumentAsync();

        var tap = Operation(document, Tap, "post");

        Assert.False(
            string.IsNullOrWhiteSpace(tap.GetProperty("summary").GetString()),
            "POST /attendance/tap has no summary. IncludeXmlComments is not reaching this assembly's " +
            "XML file — see EamsOpenApi.XmlCommentPaths.");

        var description = tap.GetProperty("description").GetString();
        Assert.Contains("deviceTapId", description!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Every operation, not just the one above.</b> <c>CS1591</c> ("missing XML comment") is
    /// suppressed across both projects — for a good reason, since it fires on every DTO member and
    /// answering it buries the comments that carry a decision. The cost of that suppression is the
    /// build-level signal that an <em>action</em> shipped undocumented, and this is what buys it back:
    /// an endpoint added without a <c>&lt;summary&gt;</c> lands in the published contract with an empty
    /// description, which is the same drift <see cref="EamsOpenApi.XmlCommentPaths"/> throws to prevent,
    /// one granularity down.
    /// </summary>
    [Fact]
    public async Task Every_operation_carries_a_summary()
    {
        using var document = await DocumentAsync();

        var undocumented = document.RootElement.GetProperty("paths").EnumerateObject()
            // The probe controllers live in this test project, so the test host discovers them and the
            // real one never does. They are not the published surface.
            .Where(path => !path.Name.StartsWith("/test-only/", StringComparison.Ordinal))
            .SelectMany(path => path.Value.EnumerateObject()
                .Select(operation => (Route: $"{operation.Name.ToUpperInvariant()} {path.Name}", operation.Value)))
            .Where(o => !o.Value.TryGetProperty("summary", out var s)
                     || string.IsNullOrWhiteSpace(s.GetString()))
            .Select(o => o.Route)
            .ToList();

        Assert.True(
            undocumented.Count == 0,
            "These operations ship in the published contract with no summary: " +
            string.Join(", ", undocumented) +
            ". Add a /// <summary> to the action. CS1591 is suppressed project-wide, so the compiler " +
            "will not tell you — this test is the replacement for that warning.");
    }

    /// <summary>
    /// <b>Each capture operation publishes exactly the responses it documents, with the right body.</b>
    ///
    /// <para>
    /// The gap this closes is silent and it was open for the whole of 4e. Swashbuckle attaches a
    /// <c>&lt;response code="N"&gt;</c> paragraph only to a response the <c>ApiDescription</c> already
    /// declares — so a <c>&lt;response&gt;</c> tag with no matching <c>[ProducesResponseType]</c>
    /// produces no warning, no error, and no entry: the paragraph simply evaporates. That is exactly how
    /// <c>POST /attendance/manual</c> came to document a problem body while the document advertised
    /// <c>TapResult</c> for its failures, which is a generated client deserializing an RFC 7807 body into
    /// the wrong type and reading <c>success</c> off a field that is not there.
    /// </para>
    ///
    /// <para>
    /// The status set is asserted <em>exactly</em> rather than as a subset. A missing declaration and a
    /// stray one are both drift, and only an exact comparison catches the first — which is the direction
    /// that actually bit.
    /// </para>
    ///
    /// <para>
    /// Transcribed, never derived, for the reason the outcome-token tests record: reading the expected
    /// set off the attributes would assert that the pipeline equals itself. <c>401</c>, <c>403</c> and
    /// <c>429</c> carry no body by design — they are produced by the authentication handler and the rate
    /// limiter, not by an action returning a DTO — so they are declared here with an empty schema.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(Tap, "post", "200:TapResult,400:ProblemDetails,404:ProblemDetails,401:,403:,429:")]
    [InlineData(TapBatch, "post", "200:TapBatchResult,400:ProblemDetails,401:,403:,429:")]
    [InlineData(Manual, "post", "200:TapResult,400:ProblemDetails,404:ProblemDetails")]
    [InlineData(Live, "get", "200:AttendanceLiveDto,400:ProblemDetails,404:ProblemDetails,429:")]
    public async Task Each_capture_operation_publishes_exactly_its_documented_responses(
        string path, string method, string expected)
    {
        using var document = await DocumentAsync();

        var declared = Operation(document, path, method).GetProperty("responses");

        var expectedByStatus = expected.Split(',')
            .Select(pair => pair.Split(':'))
            .ToDictionary(parts => parts[0], parts => parts[1]);

        Assert.Equal(
            expectedByStatus.Keys.OrderBy(s => s, StringComparer.Ordinal),
            declared.EnumerateObject().Select(r => r.Name).OrderBy(s => s, StringComparer.Ordinal));

        foreach (var (status, schema) in expectedByStatus.Where(e => e.Value.Length > 0))
        {
            Assert.True(
                schema == SchemaRefOf(declared, status),
                $"{method.ToUpperInvariant()} {path} publishes '{SchemaRefOf(declared, status)}' as its " +
                $"{status} body, but the contract documents '{schema}'. A client generated from this " +
                "document deserializes the wrong type — silently, since both are objects.");
        }
    }

    // -------------------------------------------------------------------------- the security scheme

    /// <summary>
    /// <c>Authorization: DeviceKey …</c> is discoverable from the document rather than from a markdown
    /// file somebody has to be sent.
    /// </summary>
    [Fact]
    public async Task The_device_key_security_scheme_is_published()
    {
        using var document = await DocumentAsync();

        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");

        Assert.True(
            schemes.TryGetProperty(EamsOpenApi.DeviceKeySecuritySchemeId, out var scheme),
            "The DeviceKey security scheme is not published, so nothing in the document says how a " +
            "capture client authenticates.");

        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("Authorization", scheme.GetProperty("name").GetString());

        // The exact prefix a client has to send, in the text a developer will read.
        Assert.Contains("DeviceKey eams_dk_", scheme.GetProperty("description").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Exactly the four D-28 endpoints require the key, and no others.</b>
    ///
    /// <para>
    /// Both directions matter and they fail differently. Marking too few leaves an integrator building
    /// a client that sends no credential to an endpoint that will 401 it. Marking too many — which is
    /// what a document-level <c>AddSecurityRequirement</c> would do — publishes a claim about
    /// authentication that the pipeline does not make, on an API whose admin surface is deliberately
    /// open until Phase 6. The second is the more dangerous, because it reads as the safer document.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Exactly_the_gated_operations_require_a_device_key()
    {
        using var document = await DocumentAsync();

        var required = new List<string>();

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("security", out var security)) continue;
                if (security.GetArrayLength() == 0) continue;

                required.Add($"{operation.Name} {path.Name}");
            }
        }

        var expected = GatedOperations.Select(o => $"{o.Method} {o.Path}").Order().ToList();

        Assert.Equal(expected, required.Order().ToList());
    }

    // ------------------------------------------------------------------------------ the error shape

    /// <summary>
    /// §6's declared error shape, with the extensions that were previously folklore. An integrator
    /// reading only the document used to see the five RFC 7807 members and nothing else, so the branch
    /// they would write is the one on <c>title</c> that the contract spends a paragraph warning against.
    /// </summary>
    [Theory]
    [InlineData("traceId")]
    [InlineData("code")]
    [InlineData("serverTime")]
    public async Task The_problem_details_schema_documents_its_extensions(string property)
    {
        using var document = await DocumentAsync();

        var properties = Schema(document, "ProblemDetails").GetProperty("properties");

        Assert.True(
            properties.TryGetProperty(property, out var described),
            $"ProblemDetails does not publish '{property}', which every error body on the attendance " +
            "surface actually carries.");

        Assert.False(string.IsNullOrWhiteSpace(described.GetProperty("description").GetString()));
    }

    /// <summary>
    /// <b><c>POST /attendance/manual</c>'s 400 is a problem body, and the document now says so.</b>
    ///
    /// <para>
    /// This action carried no <c>[ProducesResponseType]</c> at all until 4e, so the generated document
    /// inferred <c>TapResult</c> for every status it produced — which 4c made untrue when tap and
    /// manual failures became RFC 7807. A generated client would have deserialized a problem body into
    /// <c>TapResult</c> and read <c>success: false</c> off a field that is not there.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_manual_override_publishes_a_problem_body_for_its_failures()
    {
        using var document = await DocumentAsync();

        var responses = Operation(document, Manual, "post").GetProperty("responses");

        Assert.Equal("TapResult", SchemaRefOf(responses, "200"));
        Assert.Equal("ProblemDetails", SchemaRefOf(responses, "400"));
        Assert.Equal("ProblemDetails", SchemaRefOf(responses, "404"));
    }

    /// <summary>The component schema name a response body references, whatever media type carries it.</summary>
    private static string SchemaRefOf(JsonElement responses, string status)
    {
        Assert.True(responses.TryGetProperty(status, out var response), $"No {status} response is declared.");
        Assert.True(response.TryGetProperty("content", out var content), $"The {status} response declares no body.");

        var reference = content.EnumerateObject()
            .Select(media => media.Value.GetProperty("schema"))
            .Select(schema => schema.TryGetProperty("$ref", out var r) ? r.GetString() : null)
            .FirstOrDefault(r => r is not null);

        Assert.NotNull(reference);
        return reference!.Split('/')[^1];
    }

    // ---------------------------------------------------------------------------- the token tables

    /// <summary>
    /// <b>The frozen token tables are published as machine-readable schemas.</b> A markdown table in a
    /// document nobody can compile is what Phase 4 has been living with; an <c>enum</c> in the contract
    /// is something a generated client turns into a switchable type.
    ///
    /// <para>
    /// The published list is compared against the enum, which is what makes the document and the code
    /// unable to drift. The enum itself is pinned against the transcribed frozen table by
    /// <c>TapOutcomeContractTests</c> and <c>ManualOutcomeContractTests</c> — that is the half this one
    /// deliberately does not repeat.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_outcome_token_tables_are_published()
    {
        using var document = await DocumentAsync();

        AssertTokens(document, "TapOutcomeCode", Enum.GetNames<TapOutcome>());
        AssertTokens(document, "ManualOutcomeCode", Enum.GetNames<ManualOutcome>());
        AssertTokens(document, "LiveOutcomeCode", Enum.GetNames<LiveOutcome>());
    }

    private static void AssertTokens(JsonDocument document, string schemaName, string[] expected)
    {
        var schema = Schema(document, schemaName);

        Assert.Equal("string", schema.GetProperty("type").GetString());

        var published = schema.GetProperty("enum").EnumerateArray()
            .Select(t => t.GetString())
            .Order()
            .ToList();

        Assert.Equal(expected.Order().ToList(), published!);

        // The (code, HTTP) pair is the frozen unit — a token published without its status leaves the
        // client's first branch, on the status line, undocumented.
        Assert.Contains("| HTTP |", schema.GetProperty("description").GetString()!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- deprecation and examples

    /// <summary>
    /// <b><c>TapResult.success</c> is deprecated and still there.</b> Both halves are the assertion.
    ///
    /// <para>
    /// It has been redundant with <c>code</c> since 4c, and removing it was proposed for this phase and
    /// refused: the one consumer we cannot recompile has an open question about whether 4c's
    /// problem-body change already broke him, and a second breaking change to the same body before he
    /// answers is not a thing to do unasked. So the field stays on the wire and the document says what
    /// to read instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_redundant_success_flag_is_deprecated_rather_than_removed()
    {
        using var document = await DocumentAsync();

        var properties = Schema(document, "TapResult").GetProperty("properties");

        Assert.True(
            properties.TryGetProperty("success", out var success),
            "TapResult.success was removed. That is a breaking change to a body an external client is " +
            "already reading, and it was explicitly deferred — deprecate it, do not delete it.");

        Assert.True(success.GetProperty("deprecated").GetBoolean());
        Assert.Contains("`code`", success.GetProperty("description").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Worked examples for the batch flush. The batch shape is the part of the contract an integrator
    /// gets wrong — dense <c>results</c>, per-row <c>status</c> under a transport status that is always
    /// 200 — and one piece of JSON says all of it faster than the four paragraphs it replaces.
    /// </summary>
    [Theory]
    [InlineData("TapRequest")]
    [InlineData("TapResult")]
    [InlineData("TapBatchRequest")]
    [InlineData("TapBatchResult")]
    [InlineData("AttendanceLiveDto")]
    public async Task The_capture_payloads_carry_worked_examples(string schemaName)
    {
        using var document = await DocumentAsync();

        Assert.True(
            Schema(document, schemaName).TryGetProperty("example", out var example),
            $"{schemaName} publishes no example.");

        Assert.Equal(JsonValueKind.Object, example.ValueKind);
    }

    // ------------------------------------------------------------------ the checked-in artifact

    /// <summary>
    /// The environment variable that rewrites the committed document instead of asserting against it:
    /// <c>EAMS_UPDATE_OPENAPI=1 dotnet test --filter The_committed_contract_is_the_generated_one</c>.
    /// </summary>
    private const string UpdateVariable = "EAMS_UPDATE_OPENAPI";

    private const string CommittedContract = "docs/api/openapi.json";

    /// <summary>
    /// How the committed contract is written.
    ///
    /// <para>
    /// <b><see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> is the point of this, and the
    /// default is wrong here.</b> The default encoder escapes every non-ASCII character, so the
    /// descriptions in this document — which are the XML comments, written as markdown for a human —
    /// come out as <c>—</c> for an em dash and <c>`</c> for a backtick. Still valid JSON and
    /// still parsed correctly by codegen, but <c>docs/api/openapi.json</c> is read directly by the
    /// external mobile developer, and a paragraph of escape sequences is not a paragraph.
    /// </para>
    ///
    /// <para>
    /// "Unsafe" names the HTML-injection risk of relaxed escaping. It does not apply: this is a file on
    /// disk, not a string interpolated into a page.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions PublishedContractOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// <b><c>docs/api/openapi.json</c> is what the build actually serves.</b>
    ///
    /// <para>
    /// The file exists because the mobile developer cannot be told to run our backend to obtain the
    /// contract — he generates his client from a file in the repository. But a generated artifact
    /// checked in beside the code is precisely the drift Phase 4e existed to end: it can go stale
    /// against the source with nothing failing, which is the failure mode of the hand-written document
    /// it replaced. Committing it without this test would reintroduce the problem one directory away
    /// from where it was solved.
    /// </para>
    ///
    /// <para>
    /// Compared as canonical JSON — object keys sorted recursively — so a reordering by whatever wrote
    /// the file is not a failure, while any change of substance is. The <c>/test-only/</c> probes are
    /// stripped first: those controllers live in this test project, so the test host serves them and the
    /// real application never does.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_committed_contract_is_the_generated_one()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var served = Published(await client.GetStringAsync(DocumentRoute));
        var path = RepoPath(CommittedContract);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, served.ToJsonString(PublishedContractOptions));
            return;
        }

        Assert.True(File.Exists(path), $"{CommittedContract} is missing. Regenerate it: {Refresh}");

        Assert.True(
            Canonical(served) == Canonical(Published(await File.ReadAllTextAsync(path))),
            $"{CommittedContract} is not what this build serves — the published contract has drifted " +
            $"from the code, which is the one thing generating it was supposed to make impossible. " +
            $"Regenerate it in the same change that moved the contract: {Refresh}");
    }

    private static string Refresh =>
        $"{UpdateVariable}=1 dotnet test EAMS.sln --filter The_committed_contract_is_the_generated_one";

    /// <summary>The served document minus the probes this test project contributes to the host.</summary>
    private static JsonNode Published(string json)
    {
        var document = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("The OpenAPI document is not JSON.");

        if (document["paths"] is JsonObject paths)
        {
            foreach (var route in paths.Select(p => p.Key)
                         .Where(k => k.StartsWith("/test-only/", StringComparison.Ordinal))
                         .ToList())
            {
                paths.Remove(route);
            }
        }

        return document;
    }

    /// <summary>Key-sorted JSON, so only differences of substance compare unequal.</summary>
    private static string Canonical(JsonNode node) =>
        // Sorted() returns null only for a null input, and this one cannot be. Stated as a throw rather
        // than a `!` so an impossible case stays impossible out loud.
        (Sorted(node) ?? throw new InvalidOperationException("Sorting a non-null document returned null."))
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonNode? Sorted(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var sorted = new JsonObject();
                foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[key] = Sorted(value?.DeepClone());
                }

                return sorted;

            case JsonArray a:
                var items = new JsonArray();
                foreach (var item in a)
                {
                    items.Add(Sorted(item?.DeepClone()));
                }

                return items;

            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// A repository-relative path resolved from the test binary, by walking up to the directory holding
    /// <c>EAMS.sln</c> — the same anchor CI restores from, so this does not depend on the working
    /// directory a runner happens to choose.
    /// </summary>
    private static string RepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EAMS.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "EAMS.sln is not above the test binary; cannot locate the repository.");

        return Path.Combine(directory!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
