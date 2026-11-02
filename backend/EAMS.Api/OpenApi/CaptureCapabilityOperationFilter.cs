using EAMS.Application.Abstractions;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EAMS.Api.OpenApi;

/// <summary>
/// Marks an operation whose <c>200</c> changes family under <c>X-EAMS-Capabilities: personnel</c>
/// (ADR-015 D-100), so <see cref="CaptureCapabilityOperationFilter"/> publishes the v2 schema beside it.
/// An opt-in marker for the reason <see cref="ConditionalGetAttribute"/> records: the action's author
/// says the family exists, and the type is the one the action really returns.
/// </summary>
/// <param name="responseType">The v2 body this operation answers a capable client with.</param>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class CaptureCapabilityResponseAttribute(Type responseType) : Attribute
{
    public Type ResponseType { get; } = responseType;
}

/// <summary>
/// Publishes the v2 capture schemas as components and points at them from each capture operation, leaving
/// that operation's documented <c>200</c> as v1 (ADR-015 D-99).
///
/// <para>
/// <b>Why an extension and not a second response or a <c>oneOf</c>.</b> Either would change what a code
/// generator emits for a client that never opted in, and D-99 promises such a client regenerates unchanged.
/// Generators ignore <c>x-</c> members, so this is invisible to it.
/// </para>
///
/// <para>
/// <b>The cost of that, stated because #707 must state it.</b> A client that does opt in still gets each
/// operation's return type generated as v1, whose schema is <c>additionalProperties: false</c> and so fails
/// on <c>personType</c>. The generated v2 <em>models</em> are usable; the generated <em>operation return
/// type</em> is not, and an opted-in client must deserialize v2 itself. No <c>X-EAMS-Capabilities</c>
/// parameter is declared for the same reason: it would add an argument to every regenerated call.
/// </para>
///
/// <para>
/// <b>Generated, never hand-edited.</b> <c>OpenApiDocumentTests.The_committed_contract_is_the_generated_one</c>
/// fails on any hand edit of <c>docs/api/openapi.json</c>.
/// </para>
/// </summary>
internal sealed class CaptureCapabilityOperationFilter : IOperationFilter
{
    /// <summary>The extension's name, on each operation it applies to.</summary>
    internal const string ExtensionName = "x-eams-capability-response";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var marker = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<CaptureCapabilityResponseAttribute>()
            .SingleOrDefault();

        if (marker is null) return;

        // Registers the component, and everything it references, as a side effect. It is that registration
        // which puts the v2 schemas in the document.
        var schema = context.SchemaGenerator.GenerateSchema(marker.ResponseType, context.SchemaRepository);

        operation.Extensions[ExtensionName] = new OpenApiObject
        {
            ["header"] = new OpenApiString(CaptureCapabilities.HeaderName),
            ["token"] = new OpenApiString(CaptureCapabilities.PersonnelToken),
            ["status"] = new OpenApiInteger(StatusCodes.Status200OK),
            ["schema"] = new OpenApiObject { ["$ref"] = new OpenApiString(schema.Reference.ReferenceV3) },
            ["description"] = new OpenApiString(string.Join('\n',
            [
                "Send this header with this token and a `200` from this operation is this schema instead",
                "of the documented one. Without it nothing changes, and access is identical either way.",
                "",
                "Code generators ignore `x-` members, so a generated client still types this operation's",
                "return as the documented schema: **deserialize this one yourself.**",
            ])),
        };
    }
}
