using System.Reflection;
using EAMS.Application.Dtos;
using Microsoft.OpenApi.Models;
using DeviceKey = EAMS.Domain.DeviceKey;

namespace EAMS.Api.OpenApi;

/// <summary>
/// The generated OpenAPI document — <b>the deliverable of Phase 4</b> (Phase 4e).
///
/// <para>
/// <b>Why this exists at all.</b> The mobile capture client has been built against
/// <c>docs/api/attendance-contract-handoff.md</c>, a document written by hand and maintained by
/// remembering to. Every phase since has had to re-edit it, and nothing anywhere fails when an edit is
/// missed — a contract that has drifted from the code looks exactly like one that has not. This
/// document is produced <em>from</em> the code, so the two cannot disagree: the outcome tokens are
/// enumerated from the enums, their HTTP statuses come from the same total functions the controllers
/// dispatch on, and the prose is the XML documentation already sitting above each action.
/// </para>
///
/// <para>
/// <b>The UI stays Development-only; the document does not.</b> <c>Program.cs</c> gates
/// <c>UseSwagger</c>/<c>UseSwaggerUI</c> on <c>IsDevelopment()</c> because an unauthenticated, complete
/// description of an API whose admin surface is open (ADR-001 D-6) hands an attacker the map as well as
/// the door. The <em>generator</em> is registered unconditionally, so client tooling can still resolve
/// <c>ISwaggerProvider</c> and build the document in any environment —
/// <c>OpenApiDocumentTests.The_document_generates_in_production_even_though_it_is_not_served</c> is
/// what keeps those two halves from collapsing into one.
/// </para>
/// </summary>
public static class EamsOpenApi
{
    /// <summary>The document name, and therefore the route: <c>/swagger/v1/swagger.json</c>.</summary>
    public const string DocumentName = "v1";

    /// <summary>
    /// The security scheme id, deliberately the same string as the <c>Authorization</c> scheme itself
    /// (<see cref="DeviceKey.AuthenticationScheme"/>). A generated client names the scheme it is
    /// configuring after this id, so an id that did not match the wire format would be the one piece of
    /// the document a reader has to translate.
    /// </summary>
    public const string DeviceKeySecuritySchemeId = DeviceKey.AuthenticationScheme;

    /// <summary>
    /// Registers the API explorer and Swashbuckle with everything the published contract needs.
    ///
    /// <para>
    /// Called unconditionally from <c>Program.cs</c>. Nothing here serves an endpoint; the middleware
    /// that does is the part that is gated.
    /// </para>
    /// </summary>
    public static IServiceCollection AddEamsOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, Info);

            // The reasoning above each action, carried into the document rather than re-typed under it.
            foreach (var path in XmlCommentPaths())
            {
                // includeControllerXmlComments: the <summary> on a controller class becomes the tag
                // description, which is where "this whole surface is a device's" belongs.
                options.IncludeXmlComments(path, includeControllerXmlComments: true);
            }

            options.AddSecurityDefinition(DeviceKeySecuritySchemeId, DeviceKeyScheme);

            // Applies the requirement to exactly the endpoints that carry [Authorize] for this scheme,
            // rather than to every operation via AddSecurityRequirement — see the filter.
            options.OperationFilter<DeviceKeySecurityOperationFilter>();

            // The If-None-Match / ETag exchange, as parameters and response headers rather than as a
            // paragraph. A code generator reads the first two and not the third, and the endpoint whose
            // entire value is the 304 was published with no machine-readable trace of it.
            options.OperationFilter<ConditionalGetOperationFilter>();

            // §6's declared error shape, with the `code`/`serverTime` extensions that are folklore
            // until something writes them into the schema.
            options.SchemaFilter<ProblemDetailsSchemaFilter>();

            // Worked examples, and the one deprecation this phase publishes (TapResult.success).
            options.SchemaFilter<ContractSchemaFilter>();

            // The frozen outcome-token tables, generated from the enums and the controllers' own
            // outcome→status functions.
            options.DocumentFilter<OutcomeTokenDocumentFilter>();

            // Nullable reference annotations are already exhaustive in this codebase — `string?` versus
            // `string` on a DTO is a decision somebody made, not an accident — so publishing them costs
            // nothing and saves an integrator guessing which fields can be absent.
            options.SupportNonNullableReferenceTypes();
        });

        return services;
    }

    private static OpenApiInfo Info => new()
    {
        Title = "EAMS API",
        Version = "v1",
        Description = string.Join('\n',
        [
            "Events Attendance Monitoring System — attendance capture and administration.",
            "",
            "**This document supersedes `docs/api/attendance-contract-handoff.md` as the contract.**",
            "It is generated from the source, so the outcome tokens, status codes and payload shapes",
            "below are what the running build actually does.",
            "",
            "- **Base path** `/api/v1`. JSON in and out, camelCase field names.",
            "- **All timestamps are UTC**, ISO 8601, with an explicit `Z`. A bare or offset-bearing",
            "  local time misjudges the Present/Late boundary by the server's offset.",
            "- **Errors are RFC 7807** `application/problem+json`. Every body carries a `traceId` to",
            "  quote back and, on the attendance surface, a stable `code` to branch on — never the",
            "  human-readable `title` or `detail`, which are reworded freely.",
            "- **Capture endpoints require a device key** (`DeviceKey` below). Everything else is open",
            "  for now: authentication for human users is Phase 6, and until it lands this API must",
            "  stay on a local or trusted network.",
            "",
            "See the `TapOutcomeCode`, `ManualOutcomeCode` and `LiveOutcomeCode` schemas for the frozen",
            "token tables.",
        ]),
    };

    /// <summary>
    /// <c>Authorization: DeviceKey eams_dk_&lt;keyId&gt;_&lt;secret&gt;</c>, as OpenAPI can express it.
    ///
    /// <para>
    /// <b>Declared as an <c>apiKey</c> in the <c>Authorization</c> header rather than as
    /// <c>type: http, scheme: DeviceKey</c>, and the choice is a trade-off worth recording.</b> The
    /// <c>http</c> form is the semantically exact one — this really is an RFC 7235 auth scheme — but
    /// OpenAPI's <c>scheme</c> field is defined against the IANA registry, and <c>DeviceKey</c> is not
    /// in it; several code generators reject or silently drop an unregistered value, which would leave
    /// a generated client with no way to send the header at all. The <c>apiKey</c> form is understood
    /// everywhere and names the exact header. The cost is that the caller supplies the whole header
    /// value including the <c>DeviceKey </c> prefix, which is what the description below says twice.
    /// </para>
    /// </summary>
    private static OpenApiSecurityScheme DeviceKeyScheme => new()
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Scheme = DeviceKey.AuthenticationScheme,
        Description = string.Join('\n',
        [
            "The kiosk / capture credential, scoped to `attendance.capture` and nothing else.",
            "",
            "Send the **entire header value including the scheme word**:",
            "",
            "```",
            $"Authorization: {DeviceKey.AuthenticationScheme} eams_dk_<keyId>_<secret>",
            "```",
            "",
            "Issued once by `POST /api/v1/devices` and never retrievable afterwards — the server stores",
            "only a hash. Store it in the platform keystore (`expo-secure-store` / Android Keystore),",
            "never in `AsyncStorage`. Lose it and rotate with `POST /api/v1/devices/{id}/regenerate-key`.",
            "",
            "Case is significant and lower-case is the only accepted form for the token itself.",
            "",
            "Failures: `401 DeviceKeyMissing` / `DeviceKeyMalformed` / `DeviceKeyInvalid`,",
            "`403 DeviceKeyRevoked` / `DeviceInactive`, `429 RateLimited` (honour `Retry-After`).",
            "`DeviceKeyInvalid` deliberately does not distinguish an unknown key id from a wrong secret.",
        ]),
    };

    /// <summary>
    /// The XML documentation files feeding <c>IncludeXmlComments</c>: this assembly's (the controllers'
    /// reasoning) and <c>EAMS.Application</c>'s (the DTOs').
    ///
    /// <para>
    /// <b>A missing file throws rather than being skipped, and that is the point.</b> The failure mode
    /// this guards is silent: turn <c>GenerateDocumentationFile</c> off, or rename a project, and
    /// Swashbuckle would go on emitting a perfectly valid document with every description gone — which
    /// is precisely the drift between contract and code that publishing a generated document exists to
    /// end. It surfaces the moment the document is built, and
    /// <c>OpenApiDocumentTests</c> builds it on every test run.
    /// </para>
    /// </summary>
    internal static IEnumerable<string> XmlCommentPaths()
    {
        // EAMS.Application by type rather than by name, so a rename is a compile error here too.
        foreach (var assembly in new[] { typeof(EamsOpenApi).Assembly, typeof(TapResult).Assembly })
        {
            yield return XmlCommentPath(assembly);
        }
    }

    private static string XmlCommentPath(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        var path = Path.Combine(AppContext.BaseDirectory, $"{name}.xml");

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The OpenAPI document needs '{name}.xml' beside the assembly and it is not there. " +
                $"Set <GenerateDocumentationFile>true</GenerateDocumentationFile> in {name}.csproj. " +
                "Without it the document still generates and every description is silently empty, " +
                "which is the drift a generated contract exists to prevent.");
        }

        return path;
    }
}
