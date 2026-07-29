namespace EAMS.Api.Cors;

/// <summary>
/// The browser-origin policy for the admin SPA, and nothing more.
///
/// <para>
/// <b>Scope, stated so it is not mistaken for a deployment story.</b> The only origin this build knows
/// about is the Vite dev server on <c>http://localhost:5173</c>. There is no production origin and no
/// GitHub Pages origin here on purpose: the SPA is not wired to this API yet (<c>CLAUDE.md</c>), so any
/// hostname written down now would be a guess that outlives the guessing. When a deployed front end
/// exists, it is configuration — <c>Cors:AllowedOrigins</c> — not a code change.
/// </para>
///
/// <para>
/// <b>Credentials are never allowed, and that is a design constraint rather than an omission.</b> The
/// policy allows any header and any method, which is only safe <em>because</em> the browser will not
/// attach cookies or Authorization to these requests; combining a wide policy with
/// <c>AllowCredentials</c> is the classic CORS hole, and ASP.NET Core refuses the worst version of it
/// (<c>AllowAnyOrigin</c> + credentials) at runtime rather than at review. Phase 6's JWT is a bearer
/// token the SPA sends explicitly, so it does not need credentialed CORS either.
/// </para>
/// </summary>
internal static class LocalDevelopmentCors
{
    /// <summary>
    /// Named rather than a default policy, so <c>UseCors()</c> at the call site says which policy it
    /// is applying and a second policy can be added later without silently changing this one.
    /// </summary>
    public const string PolicyName = "eams-local-development";

    /// <summary>Configuration path — a JSON array of origin strings. Colon-delimited, so it also reads from <c>Cors__AllowedOrigins__0</c> in the environment.</summary>
    public const string ConfigurationSection = "Cors:AllowedOrigins";

    /// <summary>The Vite dev server the admin SPA runs on (<c>npm run dev</c>).</summary>
    public const string ViteDevServerOrigin = "http://localhost:5173";

    /// <summary>
    /// The origins the policy will admit.
    ///
    /// <para>
    /// <b>The <see cref="ViteDevServerOrigin"/> fallback is Development-only, and the gate matters.</b>
    /// A dev default that survives into a deployed environment means that host permanently trusts a
    /// page served from the viewer's own machine — a small hole today, since every endpoint is open
    /// anyway (ADR-001 D-6), but one that quietly outlives the thing that made it small. Outside
    /// Development an unconfigured API therefore admits <em>no</em> origin: the policy still exists and
    /// still runs, it simply matches nothing, which fails visibly in a browser rather than silently in
    /// a security review.
    /// </para>
    ///
    /// <para>
    /// Blank entries are dropped rather than passed through: <c>WithOrigins("")</c> is accepted by the
    /// builder and matches nothing, so a stray comma in configuration would otherwise look configured
    /// and behave unconfigured.
    /// </para>
    /// </summary>
    public static string[] ResolveOrigins(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration.GetSection(ConfigurationSection).Get<string[]>();

        if (configured is { Length: > 0 })
        {
            var cleaned = configured
                .Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Select(origin => origin.Trim())
                .ToArray();

            if (cleaned.Length > 0) return cleaned;
        }

        return environment.IsDevelopment() ? [ViteDevServerOrigin] : [];
    }
}
