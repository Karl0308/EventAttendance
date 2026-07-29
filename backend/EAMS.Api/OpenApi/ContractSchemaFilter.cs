using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EAMS.Api.OpenApi;

/// <summary>
/// Worked examples for the capture payloads, and the one field this phase deprecates (Phase 4e).
///
/// <para>
/// <b>Why examples and not more prose.</b> The batch shape is the part of the contract an integrator
/// gets wrong: <c>results</c> is dense rather than sparse, each row carries its own <c>status</c> while
/// the transport status is always 200, and <c>record</c> is null on a rejected row rather than absent.
/// Every one of those is a sentence in the handoff document and a glance at the JSON below.
/// </para>
///
/// <para>
/// Examples are set on the <em>schema</em> rather than per operation, so they appear wherever the type
/// does — including inside <c>TapBatchResult.results[]</c>, where a reader is most likely to be looking
/// when the question comes up.
/// </para>
/// </summary>
internal sealed class ContractSchemaFilter : ISchemaFilter
{
    private const string EventId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string StudentId = "0b8f3d5a-9c21-4a67-9c1e-6d2f0a4b7e33";
    private const string AttendanceId = "9d1c7b44-2f0e-4a8b-93a6-5c7e1f2d3a44";
    private const string DeviceTapId = "a7f3c1e2-8b44-4d19-9f0a-2c6d5e8b17aa";

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(TapRequest)) schema.Example = TapRequestExample;
        else if (context.Type == typeof(TapResult)) DescribeTapResult(schema);
        else if (context.Type == typeof(TapBatchRequest)) schema.Example = TapBatchRequestExample;
        else if (context.Type == typeof(TapBatchResult)) schema.Example = TapBatchResultExample;
        else if (context.Type == typeof(AttendanceLiveDto)) schema.Example = LiveSnapshotExample;
    }

    /// <summary>
    /// <b><c>success</c> is deprecated, and deliberately not removed.</b>
    ///
    /// <para>
    /// It has been redundant since Phase 4c gave every body a <c>code</c>: it is <c>false</c> on
    /// exactly the outcomes that arrive as a <c>4xx</c>, and a <c>4xx</c> is a problem body where the
    /// field does not appear at all — so the only place it survives is a success body, where it is
    /// always <c>true</c>. Removing it was proposed for this phase and refused: the one consumer we
    /// cannot recompile has an open question about whether 4c's problem-body change already broke him,
    /// and a second breaking change to the same body before he has answered is not a thing to do
    /// unasked. Deprecation says so in the document at no cost to a running client.
    /// </para>
    /// </summary>
    private static void DescribeTapResult(OpenApiSchema schema)
    {
        schema.Example = TapResultExample;

        if (!schema.Properties.TryGetValue("success", out var success)) return;

        success.Deprecated = true;
        success.Description = string.Join('\n',
        [
            "**Deprecated — read `code` instead.** Retained for clients already reading it; the",
            "behaviour is unchanged and it will not be removed without notice.",
            "",
            "It carries no information `code` does not. It is `false` on exactly the outcomes that",
            "arrive as a `4xx`, and a `4xx` is an RFC 7807 problem body that has no `success` member —",
            "so in every body where you can actually see this field it is `true`.",
        ]);
    }

    private static OpenApiObject TapRequestExample => new()
    {
        ["eventId"] = new OpenApiString(EventId),
        ["cardUid"] = new OpenApiString("USA39912"),
        ["deviceId"] = new OpenApiNull(),
        ["deviceTapId"] = new OpenApiString(DeviceTapId),
        ["tappedAt"] = new OpenApiString("2026-07-29T01:15:00Z"),
    };

    private static OpenApiObject TapResultExample => new()
    {
        ["success"] = new OpenApiBoolean(true),
        ["message"] = new OpenApiString("Checked in at 09:15 (Present)."),
        ["record"] = AttendanceExample(AttendanceStatus.Present),
        // The token spelled through the enum rather than as a literal, so a rename moves the example
        // with it instead of leaving one stale spelling behind in the published document.
        ["code"] = new OpenApiString(TapOutcome.Recorded.ToString()),
        ["serverTime"] = new OpenApiString("2026-07-29T05:31:22.117Z"),
    };

    private static OpenApiObject TapBatchRequestExample => new()
    {
        ["clientClockAt"] = new OpenApiString("2026-07-29T09:14:03Z"),
        ["taps"] = new OpenApiArray
        {
            new OpenApiObject
            {
                ["eventId"] = new OpenApiString(EventId),
                ["cardUid"] = new OpenApiString("USA39912"),
                ["deviceTapId"] = new OpenApiString(DeviceTapId),
                ["tappedAt"] = new OpenApiString("2026-07-29T09:02:11Z"),
            },
            new OpenApiObject
            {
                ["eventId"] = new OpenApiString(EventId),
                ["cardUid"] = new OpenApiString("USA40001"),
                ["deviceTapId"] = new OpenApiString("b8c4d2f6-1a37-4e50-8c92-7f3b6d1e04bb"),
                ["tappedAt"] = new OpenApiString("2026-07-29T09:04:47Z"),
            },
        },
    };

    /// <summary>
    /// A mixed flush: one new row, one unknown card, one retry the server had already absorbed.
    ///
    /// <para>
    /// It is the shape of this response rather than its values that is worth an example —
    /// <c>accepted + rejected == results.length</c>, <c>results[i].index == i</c>, a per-row
    /// <c>status</c> under a transport status that is always 200, and <c>record: null</c> on the row
    /// that failed rather than the key being absent.
    /// </para>
    /// </summary>
    private static OpenApiObject TapBatchResultExample => new()
    {
        ["accepted"] = new OpenApiInteger(2),
        ["rejected"] = new OpenApiInteger(1),
        ["serverTime"] = new OpenApiString("2026-07-29T09:14:05.402Z"),
        ["results"] = new OpenApiArray
        {
            new OpenApiObject
            {
                ["index"] = new OpenApiInteger(0),
                ["deviceTapId"] = new OpenApiString(DeviceTapId),
                ["code"] = new OpenApiString("Recorded"),
                ["status"] = new OpenApiInteger(200),
                ["record"] = AttendanceExample(AttendanceStatus.Present),
                ["message"] = new OpenApiString("Checked in at 09:02 (Present)."),
            },
            new OpenApiObject
            {
                ["index"] = new OpenApiInteger(1),
                ["deviceTapId"] = new OpenApiString("b8c4d2f6-1a37-4e50-8c92-7f3b6d1e04bb"),
                ["code"] = new OpenApiString("CardNotFound"),
                ["status"] = new OpenApiInteger(404),
                ["record"] = new OpenApiNull(),
                ["message"] = new OpenApiString("No active card matches that UID."),
            },
            new OpenApiObject
            {
                ["index"] = new OpenApiInteger(2),
                ["deviceTapId"] = new OpenApiString("c9d5e3a7-4b28-4f61-90d3-8a2c7e5f19cc"),
                ["code"] = new OpenApiString("DuplicateIgnored"),
                ["status"] = new OpenApiInteger(200),
                ["record"] = AttendanceExample(AttendanceStatus.Late),
                ["message"] = new OpenApiString("That tap was already recorded."),
            },
        },
    };

    /// <summary>
    /// A first poll — no <c>since</c>, so <c>entries</c> and no <c>changes</c>.
    ///
    /// <para>
    /// The example shows only one of the two collections on purpose: exactly one is ever present, and a
    /// body carrying both keys would leave a client guessing whether to replace its state or merge into
    /// it.
    /// </para>
    /// </summary>
    private static OpenApiObject LiveSnapshotExample => new()
    {
        ["eventId"] = new OpenApiString(EventId),
        ["cursor"] = new OpenApiString("AAAAAAAAB9E"),
        ["counters"] = new OpenApiObject
        {
            ["eventId"] = new OpenApiString(EventId),
            ["eventName"] = new OpenApiString("CICSS General Assembly"),
            ["expected"] = new OpenApiInteger(52),
            ["present"] = new OpenApiInteger(38),
            ["late"] = new OpenApiInteger(4),
            ["absent"] = new OpenApiInteger(0),
            ["excused"] = new OpenApiInteger(0),
            ["unexpected"] = new OpenApiInteger(2),
            ["attendanceRate"] = new OpenApiDouble(80.8),
        },
        ["entries"] = new OpenApiArray
        {
            new OpenApiObject
            {
                ["eventId"] = new OpenApiString(EventId),
                ["studentId"] = new OpenApiString(StudentId),
                ["status"] = new OpenApiString(AttendanceStatus.Present),
                ["checkInAt"] = new OpenApiString("2026-07-29T09:02:11Z"),
                ["presentCount"] = new OpenApiInteger(38),
                ["expectedCount"] = new OpenApiInteger(52),
                ["attendanceId"] = new OpenApiString(AttendanceId),
                ["studentNumber"] = new OpenApiString("USA39912"),
                ["studentName"] = new OpenApiString("Maria Clara Reyes"),
                ["checkOutAt"] = new OpenApiNull(),
                ["captureMethod"] = new OpenApiString(CaptureMethod.Rfid),
            },
        },
        ["serverTime"] = new OpenApiString("2026-07-29T09:14:05.402Z"),
        ["pollAfterSeconds"] = new OpenApiInteger(5),
        ["hasMore"] = new OpenApiBoolean(false),
    };

    private static OpenApiObject AttendanceExample(string status) => new()
    {
        ["id"] = new OpenApiString(AttendanceId),
        ["eventId"] = new OpenApiString(EventId),
        ["studentId"] = new OpenApiString(StudentId),
        ["studentName"] = new OpenApiString("Maria Clara Reyes"),
        ["studentNumber"] = new OpenApiString("USA39912"),
        ["checkInAt"] = new OpenApiString("2026-07-29T09:02:11Z"),
        ["checkOutAt"] = new OpenApiNull(),
        ["status"] = new OpenApiString(status),
        ["captureMethod"] = new OpenApiString(CaptureMethod.Rfid),
    };
}
