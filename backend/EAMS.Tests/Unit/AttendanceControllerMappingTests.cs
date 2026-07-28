using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The outcome→status translation is the part of §6 an external client codes against, and it used to
/// end in <c>_ =&gt; Ok(...)</c>. That discard did two things at once: it shipped any outcome nobody
/// had thought about as HTTP 200, and it suppressed the CS8509 warning that would have said so.
/// <c>TapOutcome.DeviceNotRegistered</c> is exactly the outcome that would have been caught — a
/// rejection, returned as success, to a client whose offline queue (§8.2) deletes anything that
/// comes back 2xx.
///
/// <para>
/// These run in the unit suite deliberately. The mapping is a pure function of the enum, so proving
/// it needs no host and no database — and a guard that costs 50 ms is one that stays in the loop a
/// developer actually runs.
/// </para>
/// </summary>
public class AttendanceControllerMappingTests
{
    /// <summary>
    /// The exhaustiveness check the compiler cannot make. C# does not treat a fully enumerated enum
    /// switch as exhaustive — the underlying integer can hold an undeclared value — so the switch
    /// keeps a throwing fall-through arm and this test is what turns "somebody added an outcome and
    /// did not map it" into a build failure rather than a production 500.
    /// </summary>
    [Fact]
    public void Every_declared_tap_outcome_is_mapped()
    {
        foreach (var outcome in Enum.GetValues<TapOutcome>())
        {
            var status = AttendanceController.StatusCodeFor(outcome);
            Assert.True(
                status is >= 200 and < 500,
                $"{outcome} maps to {status}. Every declared TapOutcome needs a deliberate status " +
                "code — a new one must be added to AttendanceController.StatusCodeFor.");
        }
    }

    [Fact]
    public void Every_declared_manual_outcome_is_mapped()
    {
        foreach (var outcome in Enum.GetValues<ManualOutcome>())
        {
            var status = AttendanceController.StatusCodeFor(outcome);
            Assert.True(
                status is >= 200 and < 500,
                $"{outcome} maps to {status}. Every declared ManualOutcome needs a deliberate " +
                "status code — a new one must be added to AttendanceController.StatusCodeFor.");
        }
    }

    /// <summary>
    /// The §8.2 contract, stated as the property that actually matters to the offline queue rather
    /// than as a list of specific codes. A rejected tap must be a 4xx: a 2xx makes the client delete
    /// a tap that was never recorded, and a 5xx makes it retry a tap that will never be accepted —
    /// which is precisely how a stale device id used to wedge a queue permanently.
    /// </summary>
    [Theory]
    [InlineData(TapOutcome.EventNotFound)]
    [InlineData(TapOutcome.EventNotOpen)]
    [InlineData(TapOutcome.CardNotFound)]
    [InlineData(TapOutcome.DeviceNotRegistered)]
    public void A_rejected_tap_is_a_4xx_so_the_offline_queue_stops_retrying(TapOutcome outcome)
    {
        var status = AttendanceController.StatusCodeFor(outcome);

        Assert.True(
            status is >= 400 and < 500,
            $"{outcome} maps to {status}. §8.2's queue drops 2xx and retries 5xx, so a rejection " +
            "that is neither a 4xx is either silently lost or retried forever.");
    }

    [Theory]
    [InlineData(TapOutcome.Recorded)]
    [InlineData(TapOutcome.DuplicateIgnored)]
    [InlineData(TapOutcome.CheckedOut)]
    [InlineData(TapOutcome.AlreadyRecorded)]
    public void A_tap_that_produced_a_record_is_200(TapOutcome outcome) =>
        Assert.Equal(StatusCodes.Status200OK, AttendanceController.StatusCodeFor(outcome));

    [Fact]
    public void A_manual_override_with_an_invalid_status_is_400() =>
        Assert.Equal(
            StatusCodes.Status400BadRequest,
            AttendanceController.StatusCodeFor(ManualOutcome.InvalidStatus));

    /// <summary>
    /// The fall-through arm throws rather than guessing. An outcome cast from an undeclared integer
    /// is a bug in the caller, and answering it with 200 is how the original defect behaved.
    /// </summary>
    [Fact]
    public void An_undeclared_outcome_throws_rather_than_reporting_success()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AttendanceController.StatusCodeFor((TapOutcome)999));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AttendanceController.StatusCodeFor((ManualOutcome)999));
    }
}
