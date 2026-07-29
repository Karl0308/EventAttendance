using EAMS.Application.Abstractions;

namespace EAMS.Infrastructure.Identity;

/// <summary>
/// The <see cref="IDeviceContext"/> for every host that is not an HTTP one — a migration, a console
/// tool, a test that constructs a service directly. There is no request, so there is no device.
///
/// <para>
/// It is registered by <c>AddEamsInfrastructure</c> and <b>replaced</b> in <c>EAMS.Api</c>'s
/// composition root by the claims-reading implementation, exactly as <see cref="ISchoolContext"/> is.
/// Registering a null-returning default here rather than leaving the interface unbound is what keeps
/// <c>new ServiceCollection().AddEamsInfrastructure(...)</c> resolvable — the failure mode that
/// <c>CompositionRootTests</c> exists to catch, and the one a web host would never reproduce.
/// </para>
/// </summary>
internal sealed class UnauthenticatedDeviceContext : IDeviceContext
{
    public Guid? DeviceId => null;

    public Guid? SchoolId => null;
}
