using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using DeviceKey = EAMS.Domain.DeviceKey;

namespace EAMS.Api.OpenApi;

/// <summary>
/// Marks the operations that actually require a device key, and only those (Phase 4e).
///
/// <para>
/// <b>Derived from the endpoint's own <c>[Authorize]</c> metadata rather than from a list.</b> The
/// alternative — <c>options.AddSecurityRequirement(...)</c> at document level, or a hand-kept array of
/// route names here — publishes a claim about authentication that nothing checks against the pipeline.
/// D-28 narrowed enforcement to four endpoints and Phase 6 will widen it again; a document that says
/// "everything needs a key" while ten endpoints are open is worse than one that says nothing, because
/// an integrator would build a client that sends a credential it does not hold to endpoints that would
/// reject it if they ever started looking.
/// </para>
///
/// <para>
/// The scheme name on the attribute is compared, not merely the attribute's presence: a future
/// <c>[Authorize]</c> naming Phase 6's <c>Bearer</c> must not be published as needing a device key.
/// </para>
/// </summary>
internal sealed class DeviceKeySecurityOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var gated = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<AuthorizeAttribute>()
            .Any(a => a.AuthenticationSchemes is { } schemes
                   && schemes.Split(',')
                       .Select(s => s.Trim())
                       .Contains(DeviceKey.AuthenticationScheme, StringComparer.Ordinal));

        if (!gated) return;

        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = EamsOpenApi.DeviceKeySecuritySchemeId,
                },
            }] = [],
        });
    }
}
