using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EAMS.Api.OpenApi;

/// <summary>
/// Marks an operation as one whose conditional GET is part of the contract, so
/// <see cref="ConditionalGetOperationFilter"/> publishes the headers that make it usable.
///
/// <para>
/// <b>An opt-in marker rather than "every GET that happens to send an ETag".</b> A filter that inferred
/// the flow — from the presence of a 304 declaration, say — would publish an <c>If-None-Match</c>
/// parameter on any action that ever acquired one, including one where revalidation is incidental. Here
/// it is the design: a device is <em>required</em> to send the header, and the attribute is what says
/// the action's author knew that.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ConditionalGetAttribute : Attribute;

/// <summary>
/// Publishes the conditional-GET flow as machine-readable OpenAPI rather than as prose.
///
/// <para>
/// <b>What this fixes, and why prose was not enough.</b> The manifest's <c>If-None-Match</c> /
/// <c>ETag</c> exchange was described at length in the action's XML documentation and appeared in the
/// generated document only inside <c>description</c> strings. A code generator — openapi-generator,
/// NSwag, a Swift or Kotlin client — reads parameters and response headers, not paragraphs: it saw a
/// 200 with a body, a 304 with nothing, and no affordance anywhere for the mechanism that makes the
/// endpoint worth having. The predictable client is one that re-pulls the entire manifest on every
/// refresh, spending its rate budget and the handset's radio precisely where the design said it would
/// not, while every response stays a valid 200 and nothing reports an error.
/// </para>
///
/// <para>
/// <b>Everything here is additive.</b> It declares headers the pipeline already emits —
/// <c>EventManifestController</c> sets <c>ETag</c> and <c>Cache-Control</c> on both the 200 and the 304,
/// and the rate limiter sets <c>Retry-After</c> on the 429 — so the document is being brought up to what
/// the build does, not the other way round. A header declared here that stopped being sent would be a
/// contract lie in the other direction; that pairing is what the integration tests assert.
/// </para>
/// </summary>
internal sealed class ConditionalGetOperationFilter : IOperationFilter
{
    private const string IfNoneMatch = "If-None-Match";
    private const string ETag = "ETag";
    private const string CacheControl = "Cache-Control";
    private const string RetryAfter = "Retry-After";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var conditional = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<ConditionalGetAttribute>()
            .Any();

        if (!conditional) return;

        operation.Parameters ??= [];
        operation.Parameters.Add(IfNoneMatchParameter);

        // On the 200 as well as the 304. A client that has just been served a body needs the validator
        // that came with it, and a 304 without one leaves a client that has lost its stored version with
        // nothing to revalidate against — which turns every subsequent pull into a full download of a
        // body it already holds.
        foreach (var status in new[] { StatusCodes.Status200OK, StatusCodes.Status304NotModified })
        {
            Describe(operation, status, ETag, ETagHeader);
            Describe(operation, status, CacheControl, CacheControlHeader);
        }

        Describe(operation, StatusCodes.Status429TooManyRequests, RetryAfter, RetryAfterHeader);
    }

    /// <summary>
    /// The request half of the flow. <b>Optional in the schema and mandatory in the contract</b> — a
    /// pull without it is a correct request that simply costs a full body, so declaring it
    /// <c>required</c> would make a generated client refuse to perform the very first pull, which by
    /// definition has no validator to send.
    /// </summary>
    private static OpenApiParameter IfNoneMatchParameter => new()
    {
        Name = IfNoneMatch,
        In = ParameterLocation.Header,
        Required = false,
        Schema = new OpenApiSchema { Type = "string" },
        Example = new OpenApiString("W/\"m1.kQ9x2Vb7Lm4nP0sT6yZ1cW\""),
        Description = string.Join('\n',
        [
            "The `version` (or the `ETag`) from your cached copy. **Send it on every pull.**",
            "",
            "Unchanged is a `304` with no body — keep what you have. Omit it and you get the whole",
            "manifest back, which is a correct response and a wasted download.",
            "",
            "The comparison is weak (`W/`) and lenient about spelling: `W/\"m1.abc\"`, `\"m1.abc\"`,",
            "a bare `m1.abc`, and a comma-separated list matching any member are all accepted, as is",
            "`*`. Echoing the `ETag` header back verbatim always works.",
            "",
            "**Never parse the value and never order it** — it is opaque, and equality is the only",
            "comparison defined on it.",
        ]),
    };

    private static OpenApiHeader ETagHeader => new()
    {
        Schema = new OpenApiSchema { Type = "string" },
        Example = new OpenApiString("W/\"m1.kQ9x2Vb7Lm4nP0sT6yZ1cW\""),
        Description = string.Join('\n',
        [
            "The weak validator for this manifest — the body's `version` with `W/` and the quotes",
            "around it. Store it and send it back as `If-None-Match` on the next pull.",
            "",
            "It is a hash of the published content, so it moves when and only when the manifest",
            "changes. It is **not** ordered and carries no time: compare it for equality only.",
        ]),
    };

    private static OpenApiHeader CacheControlHeader => new()
    {
        Schema = new OpenApiSchema { Type = "string" },
        Example = new OpenApiString("private, no-cache"),
        Description = string.Join('\n',
        [
            "`private, no-cache` — **may store, must revalidate.** Caching the body on the device is",
            "the entire feature; what is forbidden is serving it again without asking us first.",
            "",
            "`private` keeps a shared intermediary out of it: the manifest is a function of the",
            "credential, not of the URL.",
        ]),
    };

    /// <summary>
    /// Typed as a string because RFC 9110 defines two forms — <c>delay-seconds</c> and an HTTP-date —
    /// and the rate limiter emits the first. An <c>integer</c> schema would be right today and would
    /// silently mis-generate the day anything on this API answered with a date.
    /// </summary>
    private static OpenApiHeader RetryAfterHeader => new()
    {
        Schema = new OpenApiSchema { Type = "string" },
        Example = new OpenApiString("60"),
        Description = string.Join('\n',
        [
            "How long to wait before pulling again — seconds, per RFC 9110 (an HTTP-date is also a",
            "legal form of this header and is not currently sent).",
            "",
            "**Honour it.** A `429` is not an error and nothing about it stops tap capture: keep",
            "capturing and queueing, and retry the pull when the window has passed.",
        ]),
    };

    /// <summary>
    /// Adds the header to a response that the operation already declares, and does nothing at all if it
    /// does not.
    ///
    /// <para>
    /// <b>Silent on a missing response deliberately, because the alternative is worse.</b> Declaring the
    /// response here as well would let this filter invent a status the action does not actually produce
    /// — a 304 on an endpoint with no conditional path, say — and a document that promises a status the
    /// pipeline never returns is the drift a generated contract exists to end. The status set stays the
    /// action's own <c>[ProducesResponseType]</c> list, asserted exactly by
    /// <c>OpenApiDocumentTests</c>; this only annotates what is there.
    /// </para>
    /// </summary>
    private static void Describe(OpenApiOperation operation, int status, string name, OpenApiHeader header)
    {
        if (!operation.Responses.TryGetValue(status.ToString(CultureInfo.InvariantCulture), out var response))
        {
            return;
        }

        response.Headers ??= new Dictionary<string, OpenApiHeader>(StringComparer.Ordinal);
        response.Headers[name] = header;
    }
}
