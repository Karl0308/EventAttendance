using Microsoft.AspNetCore.Authentication;

namespace EAMS.Api.Authentication;

/// <summary>
/// Options for the <c>DeviceKey</c> authentication scheme. Empty on purpose.
///
/// <para>
/// <c>AddScheme&lt;TOptions, THandler&gt;</c> requires an options type, and there is nothing to
/// configure: the header name is HTTP's, the token format is frozen contract
/// (<see cref="EAMS.Domain.DeviceKey"/>), and there is no toggle — D-28 chose a seeded development
/// device over a configuration off-switch precisely so that "authentication is disabled here" is not
/// a state anything can be put into.
/// </para>
/// </summary>
public sealed class DeviceKeyOptions : AuthenticationSchemeOptions
{
}
