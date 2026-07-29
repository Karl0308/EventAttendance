using EAMS.Application.Abstractions;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// A settable <see cref="IDeviceContext"/>. Null by default, matching the production context on every
/// request that is not a device's — so a test that does not care about device identity exercises
/// exactly what an organizer override or an import does.
///
/// <para>
/// The D-26 tests set it, and they are the only ones that need to: a service-level test can then
/// arrange "a key that authenticates as device A" without booting a host, and assert the mismatch
/// refusal at the layer that makes the decision rather than only through HTTP.
/// </para>
/// </summary>
public sealed class TestDeviceContext : IDeviceContext
{
    public Guid? DeviceId { get; set; }
}
