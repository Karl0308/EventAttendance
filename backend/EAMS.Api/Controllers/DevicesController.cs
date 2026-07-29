using EAMS.Api.Authorization;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace EAMS.Api.Controllers;

/// <summary>
/// Technical Plan §6.6 — devices (Phase 4a design, D-25).
///
/// <para>
/// <b>Only one action on this controller is gated: <c>heartbeat</c>.</b> It is the one a device calls
/// about itself. Everything else here is an administrator's surface and stays open under ADR-001 D-6
/// with a declared-but-inert <c>devices.*</c> permission, exactly like students and events — Phase 4b
/// narrowed the open surface to four endpoints, it did not roll authorization out.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/devices")]
public class DevicesController : ControllerBase
{
    /// <summary>The machine-readable half of §6's RFC 7807 body, as on <c>StudentsController</c>.</summary>
    internal const string ErrorCodeProperty = "code";

    private readonly IDeviceService _devices;
    public DevicesController(IDeviceService devices) => _devices = devices;

    // ---------------------------------------------------------------------------------- reads

    /// <summary>
    /// <c>GET /devices</c> — every registered device in this school.
    /// </summary>
    /// <remarks>
    /// <b>No key material, ever.</b> The plaintext exists only in the 201 that issued it; this returns
    /// the public key id and the lifecycle timestamps an operator needs to decide whether a credential
    /// is still in use.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The devices, possibly empty.</response>
    [HttpGet]
    [HasPermissionNotEnforced("devices.read")]
    [ProducesResponseType(typeof(IEnumerable<DeviceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<DeviceDto>>> List(CancellationToken ct)
        => Ok(await _devices.ListAsync(ct));

    [HttpGet("{id:guid}")]
    [HasPermissionNotEnforced("devices.read")]
    [ProducesResponseType(typeof(DeviceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceDto>> Get(Guid id, CancellationToken ct)
    {
        var device = await _devices.GetAsync(id, ct);
        return device is null ? NotFound() : Ok(device);
    }

    // --------------------------------------------------------------------------------- writes

    /// <summary>
    /// §6.6 <c>POST /devices</c> — register, and issue the first key.
    ///
    /// <para>
    /// <b>The 201 body is the only time this key is ever readable.</b> The server stores
    /// <c>SHA-256(secret)</c> and nothing else, so there is no "show it again" endpoint to write and
    /// none to forget to protect. An operator who loses it rotates.
    /// </para>
    /// </summary>
    [HttpPost]
    [HasPermissionNotEnforced("devices.write")]
    [ProducesResponseType(typeof(DeviceKeyIssuedDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceKeyIssuedDto>> Register(
        [FromBody] DeviceWriteRequest request, CancellationToken ct)
    {
        var response = await _devices.RegisterAsync(request, ct);
        if (response.Outcome != DeviceWriteOutcome.Saved) return Failure(response);

        return CreatedAtAction(nameof(Get), new { id = response.Device!.Id }, response.IssuedKey);
    }

    /// <summary>
    /// §6.6 <c>PUT /devices/{id}</c> — the device's own fields. Never touches the key; see
    /// <c>Device.ApiKeyRevokedAt</c> for why retiring a device and burning its credential are two
    /// different statements.
    /// </summary>
    [HttpPut("{id:guid}")]
    [HasPermissionNotEnforced("devices.write")]
    [ProducesResponseType(typeof(DeviceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceDto>> Update(
        Guid id, [FromBody] DeviceWriteRequest request, CancellationToken ct)
    {
        var response = await _devices.UpdateAsync(id, request, ct);
        return response.Outcome == DeviceWriteOutcome.Saved ? Ok(response.Device) : Failure(response);
    }

    /// <summary>
    /// §6.6 <c>POST /devices/{id}/regenerate-key</c> — hard cut, no overlap window. The previous key
    /// stops working the moment this returns.
    /// </summary>
    [HttpPost("{id:guid}/regenerate-key")]
    [HasPermissionNotEnforced("devices.write")]
    [ProducesResponseType(typeof(DeviceKeyIssuedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceKeyIssuedDto>> RegenerateKey(Guid id, CancellationToken ct)
    {
        var response = await _devices.RegenerateKeyAsync(id, ct);
        return response.Outcome == DeviceWriteOutcome.Saved ? Ok(response.IssuedKey) : Failure(response);
    }

    /// <summary>
    /// <c>POST /devices/{id}/revoke-key</c> — burn the credential without issuing a replacement.
    ///
    /// <para>
    /// <b>Not defined by §6.6, and added deliberately.</b> §6.6 offers only regeneration, which
    /// conflates "this key is compromised" with "give me a working one" — but the published mobile
    /// contract already distinguishes the two on the wire (<c>401</c> bad key versus <c>403</c> revoked
    /// key), and without this route that 403 is unreachable and <c>Devices.ApiKeyRevokedAt</c> is a
    /// column nothing can write. Recorded as drift.
    /// </para>
    ///
    /// <para>
    /// Idempotent: revoking twice, or revoking a device that never held a key, is a 200. The
    /// postcondition — this device cannot authenticate — holds either way, so a retry after a timeout
    /// is safe on the one operation an operator runs when something has gone wrong.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/revoke-key")]
    [HasPermissionNotEnforced("devices.write")]
    [ProducesResponseType(typeof(DeviceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceDto>> RevokeKey(Guid id, CancellationToken ct)
    {
        var response = await _devices.RevokeKeyAsync(id, ct);
        return response.Outcome == DeviceWriteOutcome.Saved ? Ok(response.Device) : Failure(response);
    }

    /// <summary>
    /// §6.6 <c>POST /devices/{id}/heartbeat</c> — liveness, and one of the four endpoints a device key
    /// authenticates.
    ///
    /// <para>
    /// <b>The route id must be the authenticated device's own</b> — the same D-26 rule the tap path
    /// applies, and it took a second look to see that it belongs here too.
    /// </para>
    ///
    /// <para>
    /// The first version of this action skipped the check, on the reasoning that one kiosk stamping a
    /// sibling's <c>LastSeenAt</c> within its own school is a wrong liveness reading rather than a
    /// data-integrity problem. That undercounts what this writes:
    /// <see cref="IDeviceService.HeartbeatAsync"/> also
    /// sets <c>ApiKeyLastUsedAt</c>, and <em>that</em> column is the signal an operator reads when
    /// deciding whether a credential is still in use and therefore whether it is safe to revoke. A
    /// retired-but-not-revoked kiosk that a neighbour keeps making look active is a key that never gets
    /// burned. The check is shorter than the paragraph that argued against it.
    /// </para>
    ///
    /// <para>
    /// <b>404, not 403.</b> A device asking about a device that is not itself learns only that the URL
    /// names nothing it can see — the same non-disclosure rule D-27 applies when it reuses
    /// <c>DeviceNotRegistered</c> rather than inventing a cross-tenant 403.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/heartbeat")]
    [Authorize(AuthenticationSchemes = DeviceKey.AuthenticationScheme, Policy = EamsPermissions.AttendanceCapture)]
    [EnableRateLimiting(CaptureRateLimiting.PolicyName)]
    [HasPermissionNotEnforced(EamsPermissions.AttendanceCapture)]
    [ProducesResponseType(typeof(DeviceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<DeviceDto>> Heartbeat(
        Guid id, [FromServices] IDeviceContext device, CancellationToken ct)
    {
        if (id != device.DeviceId) return NotFound();

        var response = await _devices.HeartbeatAsync(id, ct);
        return response.Outcome == DeviceWriteOutcome.Saved ? Ok(response.Device) : Failure(response);
    }

    // --------------------------------------------------------------------------------- mapping

    /// <summary>
    /// The §6 status-code contract for the devices surface, as one total function over
    /// <see cref="DeviceWriteOutcome"/>. Every member listed and the fall-through throws, for the
    /// reason <c>AttendanceController.StatusCodeFor</c> records at length — a discard arm once shipped
    /// a rejection as a 200. <c>DevicesControllerMappingTests</c> turns an unmapped outcome into a test
    /// failure rather than a 500.
    /// </summary>
    internal static int StatusCodeFor(DeviceWriteOutcome outcome) => outcome switch
    {
        DeviceWriteOutcome.Saved => StatusCodes.Status200OK,

        DeviceWriteOutcome.NotFound => StatusCodes.Status404NotFound,

        DeviceWriteOutcome.ValidationFailed => StatusCodes.Status400BadRequest,

        // The request is fine; the state of the system forbids it. Same split the students and events
        // surfaces draw: no tenant to file the row under, or a key id already taken.
        DeviceWriteOutcome.NoSchoolResolved
            or DeviceWriteOutcome.KeyCollision => StatusCodes.Status409Conflict,

        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            $"No HTTP status is mapped for this {nameof(DeviceWriteOutcome)}. Every outcome must be " +
            "mapped explicitly — see DevicesControllerMappingTests."),
    };

    private ObjectResult Failure(DeviceWriteResponse response)
    {
        var status = StatusCodeFor(response.Outcome);
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: TitleFor(response.Outcome), detail: response.Message);

        problem.Extensions[ErrorCodeProperty] = response.Outcome.ToString();

        return StatusCode(status, problem);
    }

    private static string TitleFor(DeviceWriteOutcome outcome) => outcome switch
    {
        DeviceWriteOutcome.NotFound => "Device not found.",
        DeviceWriteOutcome.ValidationFailed => "The device could not be saved.",
        DeviceWriteOutcome.NoSchoolResolved => "No school could be resolved.",
        DeviceWriteOutcome.KeyCollision => "The generated key id was already taken.",
        _ => "The request could not be processed.",
    };
}
