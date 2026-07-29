using System.Net.Http.Headers;
using EAMS.Domain;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// Presents a device key on an <see cref="HttpClient"/>, in the frozen header format.
///
/// <para>
/// The header is built from <see cref="DeviceKey.AuthenticationScheme"/> rather than from the literal
/// string <c>"DeviceKey"</c>: if the scheme name is ever changed, this fixture must break together
/// with the handler rather than keep passing against a name only the tests still use.
/// </para>
/// </summary>
internal static class DeviceKeyClient
{
    /// <summary>Fluent so a test reads <c>factory.CreateClient().WithDeviceKey(key)</c> on one line.</summary>
    public static HttpClient WithDeviceKey(this HttpClient client, string apiKey)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(DeviceKey.AuthenticationScheme, apiKey);
        return client;
    }
}
