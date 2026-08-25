using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using DeviceKey = EAMS.Domain.DeviceKey;

namespace EAMS.Api.OpenApi;

/// <summary>
/// Marks each operation with the security scheme <em>it</em> actually requires, and only that one
/// (Phase 4e; extended in Phase 6b when a second scheme arrived).
///
/// <para>
/// <b>Derived from the endpoint's own <c>[Authorize]</c> metadata rather than from a list.</b> The
/// alternative — <c>options.AddSecurityRequirement(...)</c> at document level, or a hand-kept array of
/// route names here — publishes a claim about authentication that nothing checks against the pipeline.
/// D-28 narrowed enforcement to four endpoints and a later phase will widen it again; a document that
/// says "everything needs a key" while ten endpoints are open is worse than one that says nothing,
/// because an integrator would build a client that sends a credential it does not hold to endpoints
/// that would reject it if they ever started looking.
/// </para>
///
/// <para>
/// <b>The scheme <em>name</em> on the attribute is compared, not merely the attribute's presence, and
/// that is what Phase 6b needed rather than a rewrite.</b> Two schemes now reach this host: a kiosk's
/// <c>DeviceKey</c> and a person's <c>Bearer</c>. Publishing the <c>/auth</c> routes as requiring a
/// device key would tell the mobile developer that a kiosk credential signs a human in, and publishing
/// the capture routes as accepting a Bearer token would tell the SPA it can tap. Each operation is
/// therefore marked with exactly the scheme its <c>[Authorize]</c> names — and an operation carrying
/// neither, which is still most of this API under ADR-001 D-6, is marked with nothing at all.
/// </para>
/// </summary>
internal sealed class DeviceKeySecurityOperationFilter : IOperationFilter
{
    /// <summary>
    /// The schemes this document can describe. Keyed by the name that appears on an
    /// <c>[Authorize(AuthenticationSchemes = …)]</c>, which is the same string the pipeline registers
    /// the handler under — so a scheme that is added to the host and not to this map publishes as
    /// "open", and one that is renamed publishes as "open" too. Both are visible in
    /// <c>OpenApiDocumentTests</c>, which asserts the gated operations name a scheme.
    /// </summary>
    private static readonly string[] KnownSchemes =
    [
        DeviceKey.AuthenticationScheme,
        JwtBearerDefaults.AuthenticationScheme,
    ];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var declared = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<AuthorizeAttribute>()
            .SelectMany(a => (a.AuthenticationSchemes ?? "").Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var scheme in KnownSchemes.Where(s => declared.Contains(s, StringComparer.Ordinal)))
        {
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = scheme,
                    },
                }] = [],
            });
        }
    }
}
