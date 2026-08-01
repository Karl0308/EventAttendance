using System.Reflection;
using EAMS.Api.Authorization;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The D-46 outcome→status translation and the permission the endpoint demands, guarded exactly as
/// <see cref="EventsControllerMappingTests"/> guards §6.3's — see that file for why a
/// <c>_ =&gt; Ok(...)</c> fall-through is the shape of bug this prevents. Here the consequence is
/// sharper than usual: a refusal shipped as a 200 is a device caching an empty manifest as
/// authoritative and displaying every legitimately-invited student as an unknown card.
/// </summary>
public class EventManifestControllerMappingTests
{
    private static MethodInfo ManifestAction =>
        typeof(EventManifestController).GetMethod(nameof(EventManifestController.Manifest))!;

    [Fact]
    public void Every_declared_manifest_outcome_is_mapped()
    {
        foreach (var outcome in Enum.GetValues<ManifestOutcome>())
        {
            var status = EventManifestController.StatusCodeFor(outcome);
            Assert.True(
                status is >= 200 and < 500,
                $"{outcome} maps to {status}. Every declared ManifestOutcome needs a deliberate " +
                "status code — a new one must be added to EventManifestController.StatusCodeFor.");
        }
    }

    /// <summary>
    /// <b>Ok is the only outcome that may answer 2xx.</b> The controller's own remarks say the final
    /// switch arm throws rather than guessing; this is that promise asserted from outside.
    /// </summary>
    [Theory]
    [InlineData(ManifestOutcome.EventNotFound, StatusCodes.Status404NotFound)]
    [InlineData(ManifestOutcome.EventNotOpen, StatusCodes.Status409Conflict)]
    [InlineData(ManifestOutcome.EventFrozen, StatusCodes.Status409Conflict)]
    [InlineData(ManifestOutcome.ManifestTooLarge, StatusCodes.Status413PayloadTooLarge)]
    public void Every_refusal_maps_to_its_frozen_status(ManifestOutcome outcome, int expected) =>
        Assert.Equal(expected, EventManifestController.StatusCodeFor(outcome));

    [Fact]
    public void Ok_is_the_only_success() =>
        Assert.Equal(StatusCodes.Status200OK, EventManifestController.StatusCodeFor(ManifestOutcome.Ok));

    /// <summary>
    /// An outcome nobody mapped throws rather than falling through to a success. The cast is how a
    /// future enum member reaches the arm without this test having to be rewritten to add one.
    /// </summary>
    [Fact]
    public void An_unmapped_outcome_throws_rather_than_answering_200() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EventManifestController.StatusCodeFor((ManifestOutcome)999));

    // ------------------------------------------------------------------------------- the credential

    /// <summary>
    /// <b>The endpoint demands <c>attendance.capture</c> and nothing else.</b> §11 scopes a device key
    /// to that one code, so the credential a capture device already holds is the credential this
    /// endpoint takes — no second code to grant, which is what D-45 records the cost of.
    /// </summary>
    [Fact]
    public void The_manifest_demands_attendance_capture()
    {
        var authorize = ManifestAction.GetCustomAttributes(inherit: true)
            .OfType<AuthorizeAttribute>()
            .ToList();

        var single = Assert.Single(authorize);
        Assert.Equal(EamsPermissions.AttendanceCapture, single.Policy);
        Assert.Equal(EAMS.Domain.DeviceKey.AuthenticationScheme, single.AuthenticationSchemes);
    }

    /// <summary>
    /// <b><c>events.read</c> is rejected outright as the permission for this, and that is the point of
    /// the test rather than a restatement of the one above.</b> A device holding <c>events.read</c>
    /// could read every event and every roster in the school, which is exactly the blast radius a
    /// stolen kiosk key must not have. Naming the code that must never appear here means a later change
    /// to "just reuse the events permission" fails a test that says why.
    /// </summary>
    [Fact]
    public void The_manifest_never_demands_events_read()
    {
        var declared = ManifestAction.GetCustomAttributes(inherit: true)
            .OfType<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .Concat(ManifestAction.GetCustomAttributes(inherit: true)
                .OfType<HasPermissionNotEnforcedAttribute>()
                .Select(a => a.Permission))
            .ToList();

        Assert.DoesNotContain(EamsPermissions.EventsRead, declared);
    }
}
