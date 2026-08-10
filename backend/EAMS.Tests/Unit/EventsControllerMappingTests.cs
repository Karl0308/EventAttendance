using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The §6.3 outcome→status translation, guarded exactly as
/// <see cref="AttendanceControllerMappingTests"/> guards §6.4's — see that file for why a
/// <c>_ =&gt; Ok(...)</c> fall-through is the specific shape of bug this prevents.
///
/// <para>
/// In the unit suite because the mapping is a pure function of the enum: no host, no database, and it
/// stays in the loop a developer actually runs.
/// </para>
/// </summary>
public class EventsControllerMappingTests
{
    [Fact]
    public void Every_declared_event_write_outcome_is_mapped()
    {
        foreach (var outcome in Enum.GetValues<EventWriteOutcome>())
        {
            var status = EventsController.StatusCodeFor(outcome);
            Assert.True(
                status is >= 200 and < 500,
                $"{outcome} maps to {status}. Every declared EventWriteOutcome needs a deliberate " +
                "status code — a new one must be added to EventsController.StatusCodeFor.");
        }
    }

    /// <summary>
    /// The property that matters more than any individual code: <b>a write that did not happen must
    /// not answer 2xx.</b> A caller that reads a 200 stops asking, so a refused status change reported
    /// as success leaves a UI showing an event as Closed while the database says Open — and the
    /// operator finds out from a report.
    /// </summary>
    [Theory]
    [InlineData(EventWriteOutcome.NotFound)]
    [InlineData(EventWriteOutcome.ValidationFailed)]
    [InlineData(EventWriteOutcome.IllegalTransition)]
    [InlineData(EventWriteOutcome.EventLocked)]
    [InlineData(EventWriteOutcome.UnknownReference)]
    [InlineData(EventWriteOutcome.NotACohort)]
    [InlineData(EventWriteOutcome.NoSchoolResolved)]
    public void Every_outcome_other_than_saved_is_a_4xx(EventWriteOutcome outcome)
    {
        var status = EventsController.StatusCodeFor(outcome);

        Assert.True(
            status is >= 400 and < 500,
            $"{outcome} maps to {status}. Nothing but Saved may answer 2xx: a caller that reads a " +
            "success stops asking, and a refused write reported as one is invisible until a report " +
            "is wrong.");
    }

    [Fact]
    public void Saved_is_the_only_success() =>
        Assert.Equal(StatusCodes.Status200OK, EventsController.StatusCodeFor(EventWriteOutcome.Saved));

    /// <summary>
    /// The 400/409 split, pinned because it is a judgement call and therefore the thing most likely to
    /// be "tidied" into consistency later. A bad transition is a request that is wrong under any
    /// circumstances; a locked event refuses a payload that is entirely well formed and would be
    /// accepted against the same event in another state. Same distinction <c>SisImportController</c>
    /// already draws between a malformed run request and a batch that cannot be run.
    /// </summary>
    [Fact]
    public void A_bad_request_is_400_and_a_state_conflict_is_409()
    {
        Assert.Equal(StatusCodes.Status400BadRequest,
            EventsController.StatusCodeFor(EventWriteOutcome.IllegalTransition));
        Assert.Equal(StatusCodes.Status400BadRequest,
            EventsController.StatusCodeFor(EventWriteOutcome.ValidationFailed));
        Assert.Equal(StatusCodes.Status400BadRequest,
            EventsController.StatusCodeFor(EventWriteOutcome.UnknownReference));
        Assert.Equal(StatusCodes.Status400BadRequest,
            EventsController.StatusCodeFor(EventWriteOutcome.NotACohort));

        Assert.Equal(StatusCodes.Status409Conflict,
            EventsController.StatusCodeFor(EventWriteOutcome.EventLocked));
        Assert.Equal(StatusCodes.Status409Conflict,
            EventsController.StatusCodeFor(EventWriteOutcome.NoSchoolResolved));
    }

    [Fact]
    public void An_undeclared_outcome_throws_rather_than_reporting_success() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EventsController.StatusCodeFor((EventWriteOutcome)999));

    // ------------------------------------------------------- D-50 the audience resolver's mapping

    /// <summary>
    /// The same exhaustiveness guard one enum over, because <see cref="AudienceResolveOutcome"/> is a
    /// second, independent enum with its own <c>StatusCodeFor</c> overload — the guard above cannot see
    /// it, so a sixth outcome added there would compile, ship, and hit the throwing arm at runtime with
    /// nothing in the suite noticing first.
    /// </summary>
    [Fact]
    public void Every_declared_audience_resolve_outcome_is_mapped()
    {
        foreach (var outcome in Enum.GetValues<AudienceResolveOutcome>())
        {
            var status = EventsController.StatusCodeFor(outcome);
            Assert.True(
                status is >= 200 and < 500,
                $"{outcome} maps to {status}. Every declared AudienceResolveOutcome needs a " +
                "deliberate status code — a new one must be added to EventsController.StatusCodeFor.");
        }
    }

    /// <summary>
    /// <b><c>Ok</c> is the only 200, and this is the assertion that matters more than either code.</b> A
    /// filter builder reads the count it gets back and shows it to an operator; a refused filter
    /// answered 2xx would publish the count of a <em>different</em>, wider filter — the one the refused
    /// row was supposed to narrow — with nothing anywhere saying the row had been dropped. That is the
    /// exact failure D-50's "refused rather than skipped" exists to prevent, one layer up.
    /// </summary>
    [Fact]
    public void Ok_is_the_only_audience_outcome_that_may_answer_2xx()
    {
        Assert.Equal(
            StatusCodes.Status200OK, EventsController.StatusCodeFor(AudienceResolveOutcome.Ok));

        foreach (var outcome in Enum.GetValues<AudienceResolveOutcome>()
                     .Where(o => o != AudienceResolveOutcome.Ok))
        {
            var status = EventsController.StatusCodeFor(outcome);
            Assert.True(
                status is >= 400 and < 500,
                $"{outcome} maps to {status}. Nothing but Ok may answer 2xx: a refusal reported as a " +
                "success publishes the count of a filter nobody built.");
        }
    }

    /// <summary>
    /// Both refusals are 400 and neither is 409, unlike the write surface above. Resolving reads and
    /// writes nothing, so there is no resource whose state could forbid it — every failure here is a
    /// request that will be just as malformed on retry. Pinned because "make it consistent with the
    /// sibling mapping" is a plausible-sounding change that would be wrong.
    /// </summary>
    [Fact]
    public void Both_audience_refusals_are_400_rather_than_a_state_conflict()
    {
        Assert.Equal(StatusCodes.Status400BadRequest,
            EventsController.StatusCodeFor(AudienceResolveOutcome.UnknownAudienceField));
        Assert.Equal(StatusCodes.Status400BadRequest,
            EventsController.StatusCodeFor(AudienceResolveOutcome.InvalidAudienceFilterValue));
    }

    [Fact]
    public void An_undeclared_audience_outcome_throws_rather_than_reporting_success() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EventsController.StatusCodeFor((AudienceResolveOutcome)999));
}
