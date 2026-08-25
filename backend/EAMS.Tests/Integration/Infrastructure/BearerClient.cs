using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// The Bearer counterpart of <see cref="DeviceKeyClient"/>. Separate from
/// <see cref="AuthApiClient"/> on purpose: that class models a browser holding a session, and every
/// token it carries came from a login. These tests carry tokens that never did.
/// </summary>
internal static class BearerClient
{
    public static HttpClient WithBearer(this HttpClient client, string token)
    {
        // TryAddWithoutValidation, because some of these tokens are deliberately not well-formed and
        // the typed AuthenticationHeaderValue would refuse them before the server ever saw one.
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"{JwtBearerDefaults.AuthenticationScheme} {token}");

        return client;
    }

    /// <summary>
    /// Both credentials on one request, as two separate <c>Authorization</c> header values — the
    /// shape a client gets when it sets one and a proxy or an SDK adds another.
    /// </summary>
    public static HttpClient WithBoth(this HttpClient client, string bearerToken, string deviceKey)
    {
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"{JwtBearerDefaults.AuthenticationScheme} {bearerToken}");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"{EAMS.Domain.DeviceKey.AuthenticationScheme} {deviceKey}");

        return client;
    }

    public static AuthenticationHeaderValue? Challenge(this HttpResponseMessage response) =>
        response.Headers.WwwAuthenticate.FirstOrDefault();
}
