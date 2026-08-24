namespace EAMS.Infrastructure.Data;

/// <summary>
/// The hosting-environment names this assembly has to reason about, without taking a dependency on
/// <c>Microsoft.Extensions.Hosting.Abstractions</c> to obtain two strings.
///
/// <para>
/// <b>The values match <c>Microsoft.Extensions.Hosting.Environments</c> exactly, and the comparison
/// matches <c>IHostEnvironment.IsDevelopment()</c> exactly</b> — ordinal, case-insensitive. Both
/// halves matter: this is the second, independent gate on the Development SuperAdmin seed, and a gate
/// that disagreed with the composition root's about what "Development" means would either double-refuse
/// a legitimate host or, far worse, admit one the first gate excluded.
/// </para>
///
/// <para>
/// <b>There is deliberately no <c>IsNotProduction</c>.</b> "Not Production" admits <c>Staging</c> — a
/// real, network-reachable host — and reads as a synonym for "development only" while not being one.
/// That exact mistake shipped once in this codebase, gating a well-known device credential; see
/// <see cref="SeedData.DevelopmentKioskApiKey"/>, which records it rather than quietly correcting it.
/// </para>
/// </summary>
internal static class EamsEnvironments
{
    public const string Development = "Development";
    public const string Production = "Production";

    public static bool IsDevelopment(string? environmentName) =>
        string.Equals(environmentName, Development, StringComparison.OrdinalIgnoreCase);
}
