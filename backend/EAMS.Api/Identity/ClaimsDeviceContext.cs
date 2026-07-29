using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;

namespace EAMS.Api.Identity;

/// <summary>
/// The authenticated device, read from the request's claims (Phase 4a design, D-26). The twin of
/// <c>ClaimsSchoolContext</c>, reading a claim the same <c>DeviceKey</c> ticket wrote.
///
/// <para>
/// Returns <c>null</c> on every request that is not a device's: an organizer's manual override, an
/// admin listing students, a migration. Null is not an error state here; it is the answer for almost
/// every request the API serves. See <see cref="IDeviceContext"/> for why the device's school is
/// deliberately not exposed alongside it.
/// </para>
/// </summary>
internal sealed class ClaimsDeviceContext : IDeviceContext
{
    private readonly IHttpContextAccessor _http;

    public ClaimsDeviceContext(IHttpContextAccessor http) => _http = http;

    public Guid? DeviceId
    {
        get
        {
            var value = _http.HttpContext?.User.FindFirst(EamsClaimTypes.DeviceId)?.Value;
            return Guid.TryParse(value, out var parsed) ? parsed : null;
        }
    }
}
