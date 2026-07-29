using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <b><see cref="TapOutcome"/>'s member names are published contract</b>, frozen in
/// <c>docs/api/attendance-contract-handoff.md</c> §2 and being built against right now by a mobile
/// developer we cannot recompile (Phase 4c, D-37).
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
    /// The two published tokens that <b>4c does not produce</b>, because both belong to
    /// <c>POST /attendance/tap/batch</c>, which Phase 4d builds:
    /// <c>DeviceTapIdRequired</c> (the batch endpoint makes <c>deviceTapId</c> mandatory; the single
    /// endpoint does not) and <c>BatchTooLarge</c> (a batch-level refusal with no single-tap meaning).
    ///
    /// <para>
    /// <b>How the absence is tolerated:</b> they are exempt from
    /// <see cref="Every_published_token_that_4c_produces_is_a_declared_outcome"/> and from nothing else.
    /// The direction that matters — <see cref="Every_declared_outcome_is_a_published_token"/> — is
    /// unconditional, so when 4d declares them they are already required to be spelled exactly as they
    /// are here, and adding them fails nothing. Nothing asserts they are <em>absent</em>: a test that
    /// broke when the feature arrived would just be deleted, which is not a guard.
    /// </para>
    ///
    /// <para>
    /// <b>Delete an entry from this list when 4d ships its endpoint</b>, and the exemption narrows on
    /// its own — <see cref="The_exemption_list_names_only_the_batch_endpoints_tokens"/> is what stops it
    /// quietly growing into a way to publish a token nobody implements.
    /// </para>
    /// </summary>
    private static readonly string[] NotYetOnTheWire =
    [
        "DeviceTapIdRequired",
        "BatchTooLarge",
    ];

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
                $"TapOutcome.{outcome} is not in the frozen token list in " +
                "docs/api/attendance-contract-handoff.md §2. Either it was renamed — which is a " +
                "breaking change for the mobile client and needs a contract revision, not an edit " +
                "here — or it is new and the document has not been updated yet.");
        }
    }

    /// <summary>
    /// The other direction, which catches a <em>deletion</em>. A token the document promises and the
    /// enum no longer declares is an outcome a client is still branching on and will never receive.
    /// </summary>
    [Fact]
    public void Every_published_token_that_4c_produces_is_a_declared_outcome()
    {
        var declared = Enum.GetNames<TapOutcome>();

        foreach (var token in PublishedTokens.Except(NotYetOnTheWire))
        {
            Assert.True(
                declared.Contains(token),
                $"'{token}' is published in docs/api/attendance-contract-handoff.md §2 but no " +
                "TapOutcome declares it. A published token that cannot be returned is a branch the " +
                "mobile client wrote and will never reach.");
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
                $"docs/api/attendance-contract-handoff.md §2 publishes {published.Status}. The " +
                "(code, HTTP) pair is frozen contract — §8.2's offline queue branches on the status " +
                "and drops anything 2xx, so a rejection mapped to 200 is a tap the client deletes " +
                "and we never wrote.");
        }
    }

    [Fact]
    public void The_exemption_list_names_only_the_batch_endpoints_tokens()
    {
        Assert.Equal(
            new[] { "BatchTooLarge", "DeviceTapIdRequired" },
            NotYetOnTheWire.OrderBy(t => t, StringComparer.Ordinal));

        Assert.All(NotYetOnTheWire, token => Assert.Contains(token, PublishedTokens));
    }

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
