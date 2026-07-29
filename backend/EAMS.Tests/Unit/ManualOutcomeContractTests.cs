using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// <b><see cref="ManualOutcome"/>'s member names are published contract too</b>, and until Phase 4e
/// nothing said so and nothing checked it.
///
/// <para>
/// <b>The gap this closes.</b> <see cref="TapOutcome"/> has been pinned since 4c because a mobile
/// developer we cannot recompile branches on it. <see cref="ManualOutcome"/> travels on the wire
/// through the <em>same</em> <c>code</c> field, on success bodies and RFC 7807 problem bodies alike; it
/// is named by the frozen handoff document; <c>ApiContractTests</c> asserts <c>"InvalidStatus"</c> as a
/// literal string; and the admin SPA is the consumer that branches on it when an organizer's override
/// is refused. Renaming <c>InvalidNotes</c> would have broken that SPA exactly as renaming
/// <c>DuplicateIgnored</c> breaks the mobile client — and it would have shipped through a full green
/// suite, because an enum member rename is the most ordinary refactor there is.
/// </para>
///
/// <para>
/// Same shape, same reasoning and the same <b>transcribe, never derive</b> rule as
/// <see cref="TapOutcomeContractTests"/>: generating the table below from the enum would make the test
/// assert that the enum equals itself. The pair — token <em>and</em> status — is what is frozen; the
/// status half is what a client branches on before it ever looks at the token.
/// </para>
/// </summary>
public class ManualOutcomeContractTests
{
    /// <summary>
    /// The published table for <c>POST /attendance/manual</c>, transcribed.
    ///
    /// <para>
    /// <c>EventNotFound</c> is deliberately spelled the same as <see cref="TapOutcome.EventNotFound"/>
    /// and carries the same status. One token, one meaning, whichever endpoint produced it — a client
    /// holding a single <c>code</c>→handler map is the whole reason the field is called <c>code</c> on
    /// both surfaces.
    /// </para>
    /// </summary>
    private static readonly (string Token, int Status)[] PublishedOutcomes =
    [
        ("Saved", StatusCodes.Status200OK),
        ("EventNotFound", StatusCodes.Status404NotFound),
        ("StudentNotFound", StatusCodes.Status404NotFound),
        ("InvalidStatus", StatusCodes.Status400BadRequest),
        ("InvalidNotes", StatusCodes.Status400BadRequest),
    ];

    private static string[] PublishedTokens => [.. PublishedOutcomes.Select(o => o.Token)];

    /// <summary>
    /// The direction that catches a rename or an unpublished addition — a token on the wire that the
    /// published table does not describe, so no client has a branch for it.
    /// </summary>
    [Fact]
    public void Every_declared_outcome_is_a_published_token()
    {
        foreach (var outcome in Enum.GetValues<ManualOutcome>())
        {
            Assert.True(
                PublishedTokens.Contains(outcome.ToString()),
                $"ManualOutcome.{outcome} is not in the frozen token list here or in the generated " +
                "OpenAPI document's ManualOutcomeCode schema. Either it was renamed — a breaking " +
                "change for the admin SPA, which needs a contract revision rather than an edit to " +
                "this list — or it is new and has not been published yet.");
        }
    }

    /// <summary>
    /// The other direction, which catches a <em>deletion</em>: a token the document promises and the
    /// enum no longer declares is a branch a client wrote and will never reach.
    ///
    /// <para>
    /// There is no <c>NotYetOnTheWire</c> exemption list here, and that is deliberate rather than an
    /// omission. <see cref="TapOutcomeContractTests"/> needed one because 4c published two batch tokens
    /// before 4d built the endpoint; this table describes an endpoint that has shipped since Phase 2,
    /// so a token in it that no member declares is simply wrong. Adding the mechanism "in case" would
    /// hand a future red build the easiest possible way to go green.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_published_token_is_a_declared_outcome()
    {
        var declared = Enum.GetNames<ManualOutcome>();

        foreach (var token in PublishedTokens)
        {
            Assert.True(
                declared.Contains(token),
                $"'{token}' is published as a ManualOutcome code but no member declares it. A " +
                "published token that cannot be returned is a branch the admin SPA wrote and will " +
                "never reach.");
        }
    }

    /// <summary>
    /// The other half of the frozen pair. <c>AttendanceControllerMappingTests</c> asserts only that
    /// every outcome maps to <em>something</em>, so mapping <see cref="ManualOutcome.InvalidNotes"/> to
    /// 200 would pass every other test in the suite — and a 200 carrying an unsaved override is a UI
    /// that reports success for a write that did not happen.
    /// </summary>
    [Fact]
    public void Every_declared_outcome_maps_to_the_status_the_document_publishes()
    {
        foreach (var outcome in Enum.GetValues<ManualOutcome>())
        {
            var published = PublishedOutcomes.SingleOrDefault(o => o.Token == outcome.ToString());
            if (published.Token is null) continue; // Reported by Every_declared_outcome_is_a_published_token.

            Assert.True(
                published.Status == AttendanceController.StatusCodeFor(outcome),
                $"ManualOutcome.{outcome} maps to {AttendanceController.StatusCodeFor(outcome)}, but " +
                $"the published contract says {published.Status}. The (code, HTTP) pair is frozen: a " +
                "client reads the status before it reads the token, so a refusal mapped to 200 is an " +
                "override the operator believes was recorded.");
        }
    }

    /// <summary>
    /// The four members the frozen document names by name, asserted individually.
    ///
    /// <para>
    /// <see cref="Every_published_token_is_a_declared_outcome"/> already covers them — but it covers
    /// them through a list that a future red build could be made green by editing. This one cannot be
    /// satisfied that way: it names the tokens, so deleting one fails a test whose message says what it
    /// was for rather than reporting a generic list mismatch.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Saved")]
    [InlineData("InvalidStatus")]
    [InlineData("InvalidNotes")]
    [InlineData("StudentNotFound")]
    public void The_documented_tokens_are_declared(string token) =>
        Assert.Contains(token, Enum.GetNames<ManualOutcome>());
}
