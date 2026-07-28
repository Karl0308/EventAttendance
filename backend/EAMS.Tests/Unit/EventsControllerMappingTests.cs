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
}
