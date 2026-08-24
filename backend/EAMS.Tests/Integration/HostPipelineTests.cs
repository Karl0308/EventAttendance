using System.Net;
using System.Text.Json;
using EAMS.Api.Authentication;
using EAMS.Infrastructure;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The composition root's cross-cutting behaviour: what an unhandled exception becomes, what a
/// production host publishes, and what it refuses to start without. None of this is reachable from a
/// service-level test — it is all decided in <c>Program.cs</c>, which had no test at all.
///
/// <para>
/// Every test here runs through <see cref="EamsApiFactory"/>, which boots the host in
/// <see cref="Environments.Production"/>. That is the environment the assertions are about: the
/// interesting failures are all "this is fine on a laptop and wrong on a server".
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class HostPipelineTests : IntegrationTest
{
    public HostPipelineTests(SqlServerFixture sql) : base(sql) { }

    // ---------------------------------------------------------------- RFC 7807 (plan §6)

    /// <summary>
    /// §6's header declares "Errors: RFC 7807 ProblemDetails" and nothing implemented it. Two
    /// failures came out of that, and this covers both: in Production the response was a 500 with an
    /// <em>empty body</em> — nothing for a client to branch on and no handle for an operator to
    /// search logs by — and in Development the same path returned a full stack trace, on endpoints
    /// that are deliberately open to anyone who can reach the port (ADR-001 D-6).
    /// </summary>
    [Fact]
    public async Task An_unhandled_exception_becomes_a_problem_details_body()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/test-only/fault-probe");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(500, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(
            string.IsNullOrWhiteSpace(body.RootElement.GetProperty("title").GetString()),
            "A ProblemDetails body with no title tells a client no more than the empty body did.");
    }

    /// <summary>
    /// The correlation handle. An opaque 500 is only acceptable if the operator can still find the
    /// one log line that explains it — otherwise "it failed" is the whole of the diagnosis.
    /// </summary>
    [Fact]
    public async Task A_problem_details_body_carries_a_trace_id()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/test-only/fault-probe");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(
            body.RootElement.TryGetProperty("traceId", out var traceId),
            "ProblemDetails must carry a traceId — it is the only handle a caller can quote back.");
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));
    }

    /// <summary>
    /// The disclosure half, asserted on the bytes rather than on the shape. The exception message is
    /// written to the log and must not appear in the response; neither must a stack frame. Both used
    /// to be emitted verbatim in Development.
    /// </summary>
    [Fact]
    public async Task An_unhandled_exception_leaks_neither_its_message_nor_a_stack_trace()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/test-only/fault-probe");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(FaultProbeController.Message, body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at EAMS.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The handler must not swallow ordinary results. Cheap to assert, and it is the regression that
    /// would make every one of the tests above pass for the wrong reason.
    /// </summary>
    [Fact]
    public async Task The_exception_handler_leaves_successful_responses_alone()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/students");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The same guarantee in Development, which is where the leak actually lived and the only
    /// environment where the assertion is not vacuous. <c>WebApplication</c> installs the developer
    /// exception page automatically there, at the very front of the pipeline;
    /// <c>UseExceptionHandler</c> is registered as the first user middleware, so it sits
    /// <em>inside</em> that page and handles the exception on the way out before the page ever sees
    /// it. That is an ordering property, and this is what proves the ordering rather than asserting
    /// it in a comment.
    /// </summary>
    [Fact]
    public async Task An_unhandled_exception_in_development_is_still_not_a_stack_trace()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/test-only/fault-probe");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(FaultProbeController.Message, body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at EAMS.", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the Swagger decision: gating it on <c>IsDevelopment()</c> must not take it
    /// away from developers, which is the whole reason it exists. Asserted so a future tightening
    /// of the gate cannot quietly remove the local API explorer the README points people at.
    /// </summary>
    [Fact]
    public async Task Swagger_is_still_served_in_development()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------- Swagger exposure

    /// <summary>
    /// Swagger UI was served unconditionally, at the root, from a host whose endpoints are all open
    /// (ADR-001 D-6) — a complete, unauthenticated description of the API handed to anyone who can
    /// reach the port. It belongs in Development only, and "we will remember not to deploy this
    /// build" is not a control.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task Swagger_is_not_served_outside_development(string route)
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- configuration

    /// <summary>
    /// The base <c>appsettings.json</c> used to carry a <c>ConnectionStrings</c> block pointing at a
    /// developer's <c>.\SQLEXPRESS</c> with <c>TrustServerCertificate=True</c>. Being the base
    /// layer, that made it the <em>production</em> default — and it also disarmed the "not
    /// configured" throw in <see cref="DependencyInjection"/>, which could never fire while a value
    /// was always present. A production host missing its connection string started happily and
    /// connected to nothing useful.
    ///
    /// <para>
    /// This asserts the guard is armed, which is the only way to prove the block is gone that does
    /// not involve parsing a JSON file the running host might not even be reading. It deliberately
    /// boots the real host with no <c>ConnectionStrings__EamsDb</c> in the environment: the throw
    /// happens while the host is still being described, before any connection is opened, so a
    /// regression here fails the assertion rather than touching a database.
    /// </para>
    /// </summary>
    [Fact]
    public void The_host_refuses_to_start_in_production_without_a_configured_connection_string()
    {
        const string connectionVariable = "ConnectionStrings__EamsDb";
        const string environmentVariable = "ASPNETCORE_ENVIRONMENT";

        var previousConnection = Environment.GetEnvironmentVariable(connectionVariable);
        var previousEnvironment = Environment.GetEnvironmentVariable(environmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(connectionVariable, null);
            Environment.SetEnvironmentVariable(environmentVariable, Environments.Production);

            var thrown = Record.Exception(() =>
            {
                using var factory = new UnconfiguredApiFactory();
                using var client = factory.CreateClient(); // Builds the host — and must not get that far.
            });

            Assert.NotNull(thrown);

            // The chain is walked rather than the top exception inspected: the host builder invokes
            // the entry point reflectively, so the guard's own exception can arrive wrapped. What
            // must hold is that the failure names the setting an operator has to go and configure —
            // a connection error that says nothing about what is missing is the failure mode this
            // guard exists to replace.
            Assert.Contains(
                Unwrap(thrown!),
                e => e.Message.Contains(DependencyInjection.ConnectionStringName, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionVariable, previousConnection);
            Environment.SetEnvironmentVariable(environmentVariable, previousEnvironment);
        }
    }

    /// <summary>
    /// <b>The §11 twin of the test above: the host refuses to start without a usable signing key.</b>
    ///
    /// <para>
    /// <b>Why a refusal rather than a generated default.</b> The convenient behaviour is to mint a
    /// random key when none is configured, and it fails in two directions at once. In development it
    /// rotates on every restart, so every issued token dies the moment a file is saved — the symptom
    /// is "I keep getting logged out", which reads as a bug in login and not as a missing setting. In
    /// production, behind more than one instance or an app pool that recycles, tokens minted by one
    /// process are rejected by the next: intermittently, under load, for some users. Both failures are
    /// silent and both point away from their cause.
    /// </para>
    ///
    /// <para>
    /// <b>This asserts the guard is armed, which <c>JwtOptionsTests</c> cannot.</b> That file proves
    /// the validator is right; a validator the composition root never calls passes every test in it
    /// and protects nothing. The connection string is supplied deliberately — without it the host
    /// would throw on <c>ConnectionStrings:EamsDb</c> first, because <c>AddEamsInfrastructure</c> runs
    /// before the JWT resolution on purpose, and this test would pass for entirely the wrong reason.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]                                                          // not configured
    [InlineData("")]                                                            // configured empty
    [InlineData("tooshort")]                                                    // under the 32-byte floor
    [InlineData("ChangeMe-ChangeMe-ChangeMe-ChangeMe-ChangeMe-ChangeMe")]       // a copied placeholder
    public void The_host_refuses_to_start_without_a_usable_jwt_signing_key(string? signingKey)
    {
        var thrown = Record.Exception(() =>
        {
            using var factory = new JwtKeyApiFactory(Sql.ConnectionString, signingKey);
            using var client = factory.CreateClient(); // Builds the host — and must not get that far.
        });

        Assert.NotNull(thrown);

        // The chain is walked for the reason the connection-string test records: the host builder
        // invokes the entry point reflectively, so the guard's own exception can arrive wrapped. What
        // must hold is that the failure names the setting an operator has to go and configure.
        Assert.Contains(
            Unwrap(thrown!),
            e => e.Message.Contains(JwtOptions.SigningKeyPath, StringComparison.Ordinal));
    }

    /// <summary>
    /// The positive control, and it is not redundant. Without it, a host that refused to start for
    /// <em>any</em> reason would satisfy every case above — including a guard that rejects every key
    /// ever configured, which would be a worse defect than the one being guarded against and would
    /// look identical from the theory alone.
    /// </summary>
    [Fact]
    public void A_configured_signing_key_lets_the_host_start()
    {
        using var factory = new JwtKeyApiFactory(Sql.ConnectionString, TestHostConfiguration.SigningKey);
        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    /// <summary>
    /// <b>Ordering: a host missing both settings reports the connection string, not the signing
    /// key.</b> It is the setting an operator configures first, and it is what keeps the
    /// connection-string refusal above failing for its own reason rather than being shadowed by a
    /// guard added later. Pinned because the two guards are five lines apart in <c>Program.cs</c> and
    /// swapping them would be an invisible change.
    /// </summary>
    [Fact]
    public void A_host_missing_both_settings_reports_the_connection_string_first()
    {
        var previousConnection =
            Environment.GetEnvironmentVariable(TestHostConfiguration.ConnectionStringVariable);
        var previousEnvironment =
            Environment.GetEnvironmentVariable(TestHostConfiguration.EnvironmentVariable);
        var previousKey =
            Environment.GetEnvironmentVariable(TestHostConfiguration.SigningKeyVariable);

        try
        {
            Environment.SetEnvironmentVariable(TestHostConfiguration.ConnectionStringVariable, null);
            Environment.SetEnvironmentVariable(TestHostConfiguration.SigningKeyVariable, null);
            Environment.SetEnvironmentVariable(
                TestHostConfiguration.EnvironmentVariable, Environments.Production);

            var thrown = Record.Exception(() =>
            {
                using var factory = new UnconfiguredApiFactory();
                using var client = factory.CreateClient();
            });

            Assert.NotNull(thrown);

            var messages = Unwrap(thrown!).Select(e => e.Message).ToList();

            Assert.Contains(
                messages,
                m => m.Contains(DependencyInjection.ConnectionStringName, StringComparison.Ordinal));
            Assert.DoesNotContain(
                messages,
                m => m.Contains(JwtOptions.SigningKeyPath, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                TestHostConfiguration.ConnectionStringVariable, previousConnection);
            Environment.SetEnvironmentVariable(
                TestHostConfiguration.EnvironmentVariable, previousEnvironment);
            Environment.SetEnvironmentVariable(
                TestHostConfiguration.SigningKeyVariable, previousKey);
        }
    }

    /// <summary>Every exception in a chain, flattening <see cref="AggregateException"/> as it goes.</summary>
    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        yield return exception;

        var inner = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : (IEnumerable<Exception>)(exception.InnerException is { } single ? [single] : []);

        foreach (var child in inner)
            foreach (var descendant in Unwrap(child))
                yield return descendant;
    }
}
