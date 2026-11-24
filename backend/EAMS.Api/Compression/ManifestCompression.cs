using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;

namespace EAMS.Api.Compression;

/// <summary>
/// Gzip for <c>GET /events/{id}/manifest</c>, and for nothing else.
///
/// <para>
/// <b>Why the manifest.</b> It is the one response a capture device must download in full before it can
/// work, it is never paged (<c>EventManifestLimits</c>), and it is fetched over the venue's Wi-Fi. Measured
/// on 2026-11-24 for a 100,000-attendee event: 19.1 MB of JSON, 4.4 MB gzipped at the fastest level for
/// 0.11 s of CPU. Nothing in it is reflected from the request, so the BREACH class of attack (a secret
/// and attacker-chosen text compressed together) has nothing to work with, and HTTPS is enabled for the
/// day the VM gets a certificate.
/// </para>
///
/// <para>
/// <b>Why only the manifest.</b> Compression is not free on the server, and the other large responses
/// are read by the SPA over the LAN. Scoping it to one route keeps every other response byte-for-byte what
/// it was. A client that sends no <c>Accept-Encoding: gzip</c> — the test suite's own HttpClient among
/// them — still gets the identity body; React Native's fetch sends it and decompresses transparently.
/// </para>
///
/// <para>
/// <b>Not IIS dynamic compression</b>, because that is an optional IIS feature this deployment cannot be
/// assumed to have installed, and an unconfigured one fails silently by sending the full 19 MB.
/// </para>
/// </summary>
internal static class ManifestCompression
{
    private const string EventsPrefix = "/api/v1/events";
    private const string ManifestSuffix = "/manifest";

    /// <summary>The manifest route, matched on the path after any <c>PathBase</c>.</summary>
    public static bool Applies(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method)
        && context.Request.Path.StartsWithSegments(EventsPrefix, StringComparison.OrdinalIgnoreCase)
        && context.Request.Path.Value is { } path
        && path.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddManifestCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ["application/json"];
        });

        // Fastest: 4.4 MB in 0.11 s against 3.8 MB in 0.25 s at the default level. The extra 0.6 MB is
        // worth less to a device than doubling the server's CPU on a response it serves per device.
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }

    public static IApplicationBuilder UseManifestCompression(this IApplicationBuilder app) =>
        app.UseWhen(Applies, branch => branch.UseResponseCompression());
}
