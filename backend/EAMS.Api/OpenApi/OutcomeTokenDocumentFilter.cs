using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EAMS.Api.OpenApi;

/// <summary>
/// Publishes the outcome-token tables as real schemas (Phase 4e).
///
/// <para>
/// <b>These tables were the most valuable thing in the hand-written handoff document and the most
/// dangerous.</b> Valuable because <c>code</c> is what a client branches on, and a token list is
/// useless unless it is exhaustive; dangerous because it was transcribed by hand, so a token added or
/// renamed in the enum could silently stop matching what the one consumer we cannot recompile was
/// branching on. <c>TapOutcomeContractTests</c> and <c>ManualOutcomeContractTests</c> pin the enums
/// against the frozen list so a rename fails the build — and this filter closes the other half by
/// generating the <em>published</em> list from the enum rather than from a person's memory.
/// </para>
///
/// <para>
/// <b>The HTTP status comes from the controllers' own <c>StatusCodeFor</c>.</b> Not from a second
/// table: those functions are the projection the dispatcher actually uses, so a status published here
/// is by construction the status a client receives. §8.2's offline queue branches on the status before
/// it looks at the token — a rejection published as a 200 is a tap the client deletes as delivered —
/// which makes the pair, not the token, the thing worth publishing.
/// </para>
///
/// <para>
/// The three schemas are added to <c>components</c> and referenced by description rather than by
/// <c>$ref</c> from <c>code</c>, because one <c>TapResult</c> serves both the tap and the manual
/// surfaces and its <c>code</c> therefore ranges over two different token sets depending on the route.
/// A <c>$ref</c> would have to name one of them and would be wrong on the other endpoint.
/// </para>
/// </summary>
internal sealed class OutcomeTokenDocumentFilter : IDocumentFilter
{
    /// <summary>The frozen tap/batch token table. Every row of it is on the wire.</summary>
    internal const string TapOutcomeSchemaId = "TapOutcomeCode";

    /// <summary>
    /// The manual-override token table.
    ///
    /// <para>
    /// <b>Unpublished until 4e, and that was the gap.</b> <c>ManualOutcome</c> travels on the wire
    /// through exactly the same <c>code</c> field as <see cref="TapOutcomeSchemaId"/>, is asserted by
    /// <c>ApiContractTests</c>, and is what the admin SPA branches on when an organizer's override is
    /// refused — but nothing published it and nothing pinned it, so renaming <c>InvalidNotes</c> would
    /// have broken the SPA exactly as renaming <c>DuplicateIgnored</c> breaks the mobile client, with
    /// a full green suite.
    /// </para>
    /// </summary>
    internal const string ManualOutcomeSchemaId = "ManualOutcomeCode";

    /// <summary>The live-poll token table. Two members, and both are ones a dashboard has to handle.</summary>
    internal const string LiveOutcomeSchemaId = "LiveOutcomeCode";

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        swaggerDoc.Components ??= new OpenApiComponents();
        swaggerDoc.Components.Schemas ??= new Dictionary<string, OpenApiSchema>();

        swaggerDoc.Components.Schemas[TapOutcomeSchemaId] = TokenSchema(
            Enum.GetValues<TapOutcome>(),
            AttendanceController.StatusCodeFor,
            string.Join('\n',
            [
                "The `code` returned by `POST /attendance/tap` and by every row of",
                "`POST /attendance/tap/batch`, on success bodies and RFC 7807 problem bodies alike.",
                "",
                "**This list is frozen published contract.** A rename here is a breaking change for a",
                "client we cannot recompile, and the symptom is a queue that quietly stops reconciling",
                "rather than anything that looks like a failure. A build-gating test pins it.",
                "",
                "Queue guidance: a `2xx` row is done — drop it. A `4xx` row will be refused identically",
                "forever — stop retrying rather than skipping past it, and flush strictly in order.",
                "`BatchTooLarge` is the one exception: chunk to `maxBatchRows` and resend.",
            ]));

        swaggerDoc.Components.Schemas[ManualOutcomeSchemaId] = TokenSchema(
            Enum.GetValues<ManualOutcome>(),
            AttendanceController.StatusCodeFor,
            string.Join('\n',
            [
                "The `code` returned by `POST /attendance/manual` — the organizer override, which is",
                "not a device path.",
                "",
                "**Frozen published contract on the same terms as `TapOutcomeCode`**, and pinned by the",
                "same kind of test. It travels through the identical `code` field, so one accessor",
                "reads both.",
                "",
                "`POST /attendance/manual` deliberately keeps working on a `Closed` event: it is the",
                "only recovery route out of a terminal status.",
            ]));

        swaggerDoc.Components.Schemas[LiveOutcomeSchemaId] = TokenSchema(
            Enum.GetValues<LiveOutcome>(),
            AttendanceController.StatusCodeFor,
            string.Join('\n',
            [
                "The `code` returned by `GET /attendance/live/{eventId}` when the poll is refused.",
                "`Ok` never appears in a body — a successful poll is the `AttendanceLiveDto` itself.",
                "",
                "On `InvalidCursor`, re-poll with **no** `since` and take a fresh snapshot. The cursor",
                "is refused rather than silently downgraded to a snapshot, because a downgrade would",
                "look like \"nothing changed\" forever while the client's cursor stayed broken.",
            ]));
    }

    /// <summary>
    /// One token table: the enum's names as the schema's <c>enum</c>, and the (token, HTTP) pairs as a
    /// markdown table under the caller's prose.
    ///
    /// <para>
    /// The <c>enum</c> member is what a code generator turns into a switchable type; the table is what
    /// a person reads. Both come from the same two sources — <see cref="Enum.GetValues{T}()"/> and the
    /// controller's projection — so they cannot disagree with each other or with the dispatcher.
    /// </para>
    /// </summary>
    private static OpenApiSchema TokenSchema<TOutcome>(
        TOutcome[] outcomes, Func<TOutcome, int> statusFor, string description)
        where TOutcome : struct, Enum
    {
        var rows = outcomes
            .Select(o => $"| `{o}` | {statusFor(o)} |")
            .Prepend("|---|---|")
            .Prepend("| `code` | HTTP |");

        return new OpenApiSchema
        {
            Type = "string",
            Description = string.Join('\n', [description, "", .. rows]),
            Enum = [.. outcomes.Select(o => (IOpenApiAny)new OpenApiString(o.ToString()))],
        };
    }
}
