using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <b><see cref="TapOutcome"/>'s member names are published contract</b>, frozen as the
/// <c>TapOutcomeCode</c> schema in the generated OpenAPI document and being built against right now by
/// a mobile developer we cannot recompile (Phase 4c, D-37).
///
/// <para>
/// That makes an ordinary, invisible refactor into a breaking change: renaming
/// <c>DuplicateIgnored</c> — or spelling a new outcome one way here and another way in the document —
/// ships a token no client branches on, and the symptom is a queue that silently stops reconciling
/// rather than anything that looks like a failure. The list below is the document, transcribed. It
/// exists so that rename fails the build's test gate instead.
/// </para>
///
/// <para>
/// Same shape and the same reasoning as <see cref="AttendanceControllerMappingTests"/>: enumerate the
/// enum, assert a property of every member, and run it in the unit suite because it needs no host and
/// no database.
/// </para>
/// </summary>
public class TapOutcomeContractTests
{
    /// <summary>
    /// The published table for <c>POST /attendance/tap</c> and <c>POST /attendance/tap/batch</c>,
    /// transcribed. <b>Transcribe, never derive.</b> Generating this from the enum would make the test
    /// assert that the enum equals itself.
    ///
    /// <para>
    /// <b>The status is part of the frozen pair, not context.</b> An earlier version of this file
    /// froze the token alone, which left a real hole: <c>AttendanceControllerMappingTests</c> asserts
    /// only that every outcome maps to <em>something</em> in 200..499, and its 4xx theory names just
    /// the four pre-4b outcomes — so mapping <c>TappedAtOutOfRange</c> to 200 passed every unit test in
    /// the suite. The document publishes <c>(code, HTTP)</c> pairs, and §3 makes each batch row's
    /// <c>status</c> a field the client reads independently of the transport code, so the pair is what
    /// has to be frozen.
    /// </para>
    /// </summary>
    private static readonly (string Token, int Status)[] PublishedOutcomes =
    [
        ("Recorded", StatusCodes.Status200OK),
        ("DuplicateIgnored", StatusCodes.Status200OK),
        ("CheckedOut", StatusCodes.Status200OK),
        ("AlreadyRecorded", StatusCodes.Status200OK),
        ("EventNotFound", StatusCodes.Status404NotFound),
        ("CardNotFound", StatusCodes.Status404NotFound),
        ("DeviceNotRegistered", StatusCodes.Status404NotFound),
        ("EventNotOpen", StatusCodes.Status400BadRequest),
        ("DeviceMismatch", StatusCodes.Status400BadRequest),
        ("DeviceTapIdRequired", StatusCodes.Status400BadRequest),
        ("TappedAtOutOfRange", StatusCodes.Status400BadRequest),
        ("TappedAtOutsideEventWindow", StatusCodes.Status400BadRequest),
        ("BatchTooLarge", StatusCodes.Status400BadRequest),
    ];

    private static string[] PublishedTokens => [.. PublishedOutcomes.Select(o => o.Token)];

    /// <summary>
    /// Published tokens that the code does <b>not</b> produce yet, exempt from
    /// <see cref="Every_published_token_the_api_produces_is_a_declared_outcome"/> and from nothing else.
    ///
    /// <para>
    /// <b>Empty as of Phase 4d, and that is the whole point of the mechanism.</b> It held
    /// <c>DeviceTapIdRequired</c> and <c>BatchTooLarge</c> for exactly one phase, because both belonged
    /// to <c>POST /attendance/tap/batch</c> and the document published them before the endpoint existed.
    /// 4d built the endpoint and declared both outcomes, so the loan is repaid and the list is empty —
    /// which makes the published table and the enum now agree in both directions with no exceptions.
    /// </para>
    ///
    /// <para>
    /// <b>The list is kept rather than deleted, and it is pinned empty.</b> Deleting it would remove the
    /// only record of how a token gets published ahead of its implementation, and the next phase that
    /// needs to do it would either invent the mechanism again or — far more likely — quietly weaken
    /// <see cref="Every_published_token_the_api_produces_is_a_declared_outcome"/> instead.
    /// <see cref="The_exemption_list_is_empty"/> makes adding an entry a deliberate, reviewed edit
    /// rather than a way to publish a token nobody implements.
    /// </para>
    /// </summary>
    private static readonly string[] NotYetOnTheWire = [];

    /// <summary>
    /// The direction that catches a rename. A member renamed, or a new one added without publishing it,
    /// puts a token on the wire that the frozen table does not describe — so the client has no branch
    /// for it, and <c>code</c> stops being the thing it was added to be.
    /// </summary>
    [Fact]
    public void Every_declared_outcome_is_a_published_token()
    {
        foreach (var outcome in Enum.GetValues<TapOutcome>())
        {
            Assert.True(
                PublishedTokens.Contains(outcome.ToString()),
                $"TapOutcome.{outcome} is not in the frozen token list here or in the generated " +
                "OpenAPI document's TapOutcomeCode schema. Either it was renamed — which is a " +
                "breaking change for the mobile client and needs a contract revision, not an edit " +
                "here — or it is new and has not been published yet.");
        }
    }

    /// <summary>
    /// The other direction, which catches a <em>deletion</em>. A token the document promises and the
    /// enum no longer declares is an outcome a client is still branching on and will never receive.
    /// </summary>
    [Fact]
    public void Every_published_token_the_api_produces_is_a_declared_outcome()
    {
        var declared = Enum.GetNames<TapOutcome>();

        foreach (var token in PublishedTokens.Except(NotYetOnTheWire))
        {
            Assert.True(
                declared.Contains(token),
                $"'{token}' is published as a TapOutcomeCode but no TapOutcome declares it. A " +
                "published token that cannot be returned is a branch the mobile client wrote and " +
                "will never reach.");
        }
    }

    /// <summary>
    /// The exemption is a loan, not a licence. Without this, "not built yet" would be a way to publish
    /// any token at all and never implement it — and the list would be the last place anyone looked.
    /// </summary>
    /// <summary>
    /// The other half of the frozen pair: the status a client is promised for a token it receives.
    ///
    /// <para>
    /// This is the assertion that would have caught <c>TappedAtOutOfRange</c> mapped to 200 — a
    /// rejection returned as a success, which §8.2's queue deletes as delivered. That is the same
    /// failure <c>AttendanceControllerMappingTests</c> was written about for
    /// <c>DeviceNotRegistered</c>, reachable again because its 4xx theory is a hand-maintained list of
    /// four outcomes and three have been added since.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_declared_outcome_maps_to_the_status_the_document_publishes()
    {
        foreach (var outcome in Enum.GetValues<TapOutcome>())
        {
            var published = PublishedOutcomes.SingleOrDefault(o => o.Token == outcome.ToString());
            if (published.Token is null) continue; // Reported by Every_declared_outcome_is_a_published_token.

            Assert.True(
                published.Status == AttendanceController.StatusCodeFor(outcome),
                $"TapOutcome.{outcome} maps to {AttendanceController.StatusCodeFor(outcome)}, but " +
                $"the published contract says {published.Status}. The " +
                "(code, HTTP) pair is frozen contract — §8.2's offline queue branches on the status " +
                "and drops anything 2xx, so a rejection mapped to 200 is a tap the client deletes " +
                "and we never wrote.");
        }
    }

    /// <summary>
    /// The exemption was a loan, and Phase 4d repaid it. Every token in the published
    /// <c>TapOutcomeCode</c> schema is now something this API can actually return.
    ///
    /// <para>
    /// Failing here means somebody added an entry to <see cref="NotYetOnTheWire"/>. That is sometimes
    /// the right thing to do — it is how a token gets published ahead of its endpoint, which is exactly
    /// what happened with the two batch tokens in 4c — but it must be a deliberate edit with the ⏳
    /// marker added to the document in the same change, not a way to make a red build green.
    /// </para>
    /// </summary>
    [Fact]
    public void The_exemption_list_is_empty()
    {
        Assert.Empty(NotYetOnTheWire);

        // Still asserted, because an exemption for a token the document does not publish would be
        // exempting nothing from nothing while looking like coverage.
        Assert.All(NotYetOnTheWire, token => Assert.Contains(token, PublishedTokens));
    }

    /// <summary>
    /// The two tokens 4c published with a ⏳ and 4d had to implement, asserted by name.
    ///
    /// <para>
    /// <see cref="Every_published_token_the_api_produces_is_a_declared_outcome"/> already covers them
    /// now that the exemption list is empty — but it covers them <em>because</em> the list is empty, and
    /// the repair for a future red build is to add an entry back. This one cannot be satisfied that way:
    /// it names the two outcomes the batch endpoint is built on, so deleting either fails a test whose
    /// message says what it was for rather than a generic list mismatch.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DeviceTapIdRequired")]
    [InlineData("BatchTooLarge")]
    public void The_batch_endpoints_tokens_are_declared(string token) =>
        Assert.Contains(token, Enum.GetNames<TapOutcome>());

    /// <summary>
    /// <c>TapResult.Code</c> is a projection of <see cref="TapResponse.Outcome"/>, and
    /// <see cref="TapResponse.For"/> is the only place the projection happens. Asserted over every
    /// declared member so a future outcome cannot be added with a hand-written token beside it.
    ///
    /// <para>
    /// This proves the factory, not the service. What proves the service <em>uses</em> the factory is
    /// <c>TapFlowTests</c>, which re-asserts the same invariant on every response the real
    /// <c>AttendanceService</c> produces.
    /// </para>
    /// </summary>
    [Fact]
    public void A_tap_response_carries_its_outcome_as_the_code()
    {
        foreach (var outcome in Enum.GetValues<TapOutcome>())
        {
            var response = TapResponse.For(
                outcome, success: true, "…", record: null, DateTime.UtcNow);

            Assert.Equal(outcome.ToString(), response.Result.Code);
        }
    }

    /// <inheritdoc cref="A_tap_response_carries_its_outcome_as_the_code"/>
    [Fact]
    public void A_manual_response_carries_its_outcome_as_the_code()
    {
        foreach (var outcome in Enum.GetValues<ManualOutcome>())
        {
            var response = ManualResponse.For(
                outcome, success: true, "…", record: null, DateTime.UtcNow);

            Assert.Equal(outcome.ToString(), response.Result.Code);
        }
    }
}
