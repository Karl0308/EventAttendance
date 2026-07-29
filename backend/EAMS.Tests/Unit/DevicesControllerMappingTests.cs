using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// Every <see cref="DeviceWriteOutcome"/> maps to an HTTP status explicitly.
///
/// <para>
/// The same gate <c>AttendanceControllerMappingTests</c> and its siblings hold: C# does not treat a
/// fully enumerated enum switch as exhaustive, so the fall-through arm has to exist — and it throws
/// rather than guessing. Without this test, adding an outcome and forgetting to map it is a 500 found
/// in production; with it, it is a red build.
/// </para>
/// </summary>
public class DevicesControllerMappingTests
{
    [Fact]
    public void Every_device_write_outcome_maps_to_a_status_code()
    {
        foreach (var outcome in Enum.GetValues<DeviceWriteOutcome>())
        {
            var status = DevicesController.StatusCodeFor(outcome);

            Assert.InRange(status, 200, 599);
        }
    }

    [Theory]
    [InlineData(DeviceWriteOutcome.Saved, 200)]
    [InlineData(DeviceWriteOutcome.NotFound, 404)]
    [InlineData(DeviceWriteOutcome.ValidationFailed, 400)]
    [InlineData(DeviceWriteOutcome.NoSchoolResolved, 409)]
    [InlineData(DeviceWriteOutcome.KeyCollision, 409)]
    public void The_published_status_for_each_outcome(DeviceWriteOutcome outcome, int expected) =>
        Assert.Equal(expected, DevicesController.StatusCodeFor(outcome));

    /// <summary>
    /// The negative control for the two tests above: an outcome outside the declared set throws
    /// rather than falling through to a cheerful 200. Without this the "every outcome maps" assertion
    /// would pass just as happily against a <c>_ =&gt; 200</c> discard arm, which is the exact defect
    /// this family of tests exists to prevent.
    /// </summary>
    [Fact]
    public void An_undeclared_outcome_throws_rather_than_defaulting_to_success() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DevicesController.StatusCodeFor((DeviceWriteOutcome)999));
}
