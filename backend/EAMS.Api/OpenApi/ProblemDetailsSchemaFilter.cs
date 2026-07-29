using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EAMS.Api.OpenApi;

/// <summary>
/// Puts §6's declared error shape into the schema instead of leaving it to folklore (Phase 4e).
///
/// <para>
/// <b>What this fixes.</b> <c>ProblemDetails</c> generates from the framework type, so the document
/// described exactly the five RFC 7807 members and nothing else — while every error body this API
/// emits carries a <c>traceId</c>, and every error on the attendance surface carries the <c>code</c>
/// that the published contract tells clients to branch on. Both were real, both were undiscoverable
/// from the document, and an integrator reading only the document would have written the branch on
/// <c>title</c> that the contract spends a paragraph warning against.
/// </para>
///
/// <para>
/// <b>They are documented as extensions that may be absent, not as guarantees.</b> <c>traceId</c> is
/// universal — <c>TracedProblemDetailsFactory</c> and <c>AddProblemDetails</c> between them stamp it on
/// every path. <c>code</c> and <c>serverTime</c> are not: the attendance, students and devices surfaces
/// write them and <c>EventsController</c> does not. Publishing them as required would be the same class
/// of plausible-but-wrong statement the ADRs keep catching — a client would read <c>body.code</c> off a
/// <c>409 EventLocked</c> and get nothing.
/// </para>
///
/// <para>
/// <c>AdditionalPropertiesAllowed</c> is left as the generator set it, so a strict client generator
/// still tolerates the extensions this filter does not name (<c>maxBatchRows</c> on a
/// <c>BatchTooLarge</c> refusal, and the validation <c>errors</c> map).
/// </para>
/// </summary>
internal sealed class ProblemDetailsSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (!typeof(ProblemDetails).IsAssignableFrom(context.Type)) return;
        if (schema.Properties is null || schema.Properties.Count == 0) return;

        Describe(schema, "traceId", new OpenApiSchema
        {
            Type = "string",
            Nullable = true,
            Description = string.Join('\n',
            [
                "**On every error body this API produces.** The correlation handle: the distributed",
                "trace id when tracing is on, the per-connection request id otherwise.",
                "",
                "Quote it back when reporting a problem — it is the only value that finds the one log",
                "line explaining an otherwise opaque failure.",
            ]),
        });

        Describe(schema, "code", new OpenApiSchema
        {
            Type = "string",
            Nullable = true,
            Description = string.Join('\n',
            [
                "The stable, machine-readable outcome token. **Branch on this, never on `title` or",
                "`detail`** — those are prose written for a person and are reworded freely.",
                "",
                "Present on the attendance, students and devices surfaces, and on authentication and",
                "rate-limit refusals. Absent on the events write surface. See the `TapOutcomeCode`,",
                "`ManualOutcomeCode` and `LiveOutcomeCode` schemas for the frozen token tables.",
                "",
                "On the attendance surface it is the **same field, with the same values**, that a",
                "success body carries — one accessor works across success and failure alike.",
            ]),
        });

        Describe(schema, "serverTime", new OpenApiSchema
        {
            Type = "string",
            Format = "date-time",
            Nullable = true,
            Description = string.Join('\n',
            [
                "The server's UTC clock when this response was produced. Present on tap and",
                "manual-override failures.",
                "",
                "It is on rejections deliberately, and it matters most on `TappedAtOutOfRange`: that",
                "response tells a device its clock is wrong, so it has to say what the right one is in",
                "the same body. Compute an offset from it and apply that before enqueueing.",
            ]),
        });
    }

    /// <summary>
    /// Adds the property, or annotates it in place if the generator already produced one. Never
    /// replaces a schema wholesale — <c>ValidationProblemDetails</c> also passes through here, and
    /// clobbering a member the framework declared would trade one wrong document for another.
    /// </summary>
    private static void Describe(OpenApiSchema schema, string name, OpenApiSchema described)
    {
        if (schema.Properties.TryGetValue(name, out var existing))
        {
            existing.Description = described.Description;
            return;
        }

        schema.Properties[name] = described;
    }
}
