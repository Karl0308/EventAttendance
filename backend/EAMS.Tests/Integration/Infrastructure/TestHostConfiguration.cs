namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// The configuration every booted test host needs, and the environment-variable names it arrives
/// under.
///
/// <para>
/// <b>Environment variables rather than a test-registered configuration source, for the reason
/// <see cref="EamsApiFactory"/> documents at length</b>: under minimal hosting <c>Program.cs</c> reads
/// both of these while the host is still being <em>described</em>, before <c>builder.Build()</c>, and
/// every <c>ConfigureAppConfiguration</c> callback a factory registers is replayed later, during
/// <c>Build()</c>. A source added there arrives after the value has been read and is simply ignored.
/// </para>
/// </summary>
internal static class TestHostConfiguration
{
    public const string ConnectionStringVariable = "ConnectionStrings__EamsDb";
    public const string EnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    /// <summary>The environment form of <c>JwtOptions.SigningKeyPath</c>.</summary>
    public const string SigningKeyVariable = "Jwt__SigningKey";

    /// <summary>
    /// A signing key for the test host. <b>Not a placeholder</b> — it is deliberately long, random
    /// and free of any word on <c>JwtOptions</c>' known-placeholder list, because a test host that
    /// could not start would fail every assertion in the suite for a reason that has nothing to do
    /// with what is being tested.
    ///
    /// <para>
    /// Publishing it here is harmless for the reason the seeded kiosk key is: it exists only inside a
    /// test run, against a database created and dropped by that run. It signs nothing that outlives
    /// the process.
    /// </para>
    /// </summary>
    public const string SigningKey =
        "9f2c7a41d80b6e35c14fa9037be25d8c6a1e4f70b93d2685cf07a4e1d9b3520867ac41fe";
}
