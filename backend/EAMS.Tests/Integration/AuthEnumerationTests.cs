using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EAMS.Api.RateLimiting;
using EAMS.Application.Abstractions;
using EAMS.Infrastructure.Identity;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Phase 6d — attacking the two things a sign-in endpoint leaks even when it never returns a
/// different status code: which bucket an attempt lands in, and how long the answer takes.
///
/// <para>
/// <b>The limiter's partition key is a security boundary, not a bookkeeping detail.</b> The account
/// budget is ten attempts per (e-mail, IP) per minute. If <c>Admin@x</c> and <c>admin@x</c> land in
/// separate partitions, the budget is ten attempts per <em>spelling</em>, and an attacker with a
/// case-toggling loop has thousands. The user lookup normalizes; the bucket key has to normalize
/// identically or the two disagree about what "one account" means.
/// </para>
///
/// <para>
/// <b>And a 429 must not answer the question a 401 refuses.</b> The login response is deliberately
/// identical for an unknown address, a wrong password and a deactivated account. A rate-limit
/// refusal that differed between an account that exists and one that does not would hand the whole
/// oracle back — through a status code nobody thinks of as part of the authentication contract.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AuthEnumerationTests : IntegrationTest
{
    public AuthEnumerationTests(SqlServerFixture sql) : base(sql) { }

    private const string Email = "principal@usa.edu.ph";
    private const string Unknown = "nobody-at-all@usa.edu.ph";
    private const string Password = "correct-horse-battery-staple";

    private async Task<Guid> ArrangeAsync()
    {
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        await CreateUserAsync(schoolId, Email, Password);
        return schoolId;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ------------------------------------------------------------------------- limiter evasion

    /// <summary>
    /// Ten guesses spelled ten different ways, then an eleventh in the canonical spelling. If casing
    /// partitioned the bucket, the first ten would each have spent one permit out of ten different
    /// budgets and the eleventh would be answered 401.
    /// </summary>
    [Fact]
    public async Task The_account_budget_is_not_evaded_by_varying_the_casing_of_the_address()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        for (var i = 0; i < AuthAccountLimiter.PerEmailPerIpPermits; i++)
        {
            var spelling = ShoutAt(Email, i);
            Assert.NotEqual(Email, spelling);

            var attempt = await client.LoginAsync(spelling, $"guess-{i}");

            Assert.True(
                attempt.StatusCode == HttpStatusCode.Unauthorized,
                $"Attempt {i} spelled '{spelling}' was answered {(int)attempt.StatusCode}; the " +
                $"budget should not be exhausted yet.");
        }

        var eleventh = await client.LoginAsync(Email, Password);

        Assert.True(
            eleventh.StatusCode == HttpStatusCode.TooManyRequests,
            $"Ten guesses at ten spellings of one address left the budget unspent — the eleventh, " +
            $"with the RIGHT password, was answered {(int)eleventh.StatusCode}. The rate-limit " +
            $"partition key is case-sensitive while the user lookup is not, so an attacker gets ten " +
            $"attempts per spelling instead of ten per account.");
    }

    [Fact]
    public async Task The_account_budget_is_not_evaded_by_padding_the_address_with_whitespace()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        // Leading, trailing, tabs, newlines and a non-breaking space — everything Trim() removes, and
        // everything a form or a copy-paste can attach to an address.
        string[] padding = ["  ", "\t", "\n", "\r\n", "   "];

        for (var i = 0; i < AuthAccountLimiter.PerEmailPerIpPermits; i++)
        {
            var pad = padding[i % padding.Length];
            var spelling = i % 2 == 0 ? pad + Email : Email + pad;

            var attempt = await client.LoginAsync(spelling, $"guess-{i}");
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var eleventh = await client.LoginAsync(Email, Password);

        Assert.True(
            eleventh.StatusCode == HttpStatusCode.TooManyRequests,
            $"Ten guesses at one address padded ten different ways left the budget unspent — the " +
            $"eleventh, with the RIGHT password, was answered {(int)eleventh.StatusCode}. The " +
            $"rate-limit partition key is not trimming the address while the user lookup is, so a " +
            $"trailing space buys a fresh budget.");
    }

    /// <summary>
    /// The same normalization, asserted at the other end: a padded, mixed-case spelling still signs
    /// in. Without this the test above would also pass on a system that simply refused every unusual
    /// spelling outright — which would "fix" the bypass by breaking login.
    /// </summary>
    [Fact]
    public async Task A_padded_mixed_case_address_still_signs_the_same_person_in()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var response = await client.LoginAsync($"  {Email.ToUpperInvariant()}\t", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            Email,
            (await BodyAsync(response)).GetProperty("user").GetProperty("email").GetString());
    }

    // ---------------------------------------------------------------------------- the 429 oracle

    [Fact]
    public async Task A_429_is_indistinguishable_between_an_account_that_exists_and_one_that_does_not()
    {
        await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = new AuthApiClient(factory);

        var real = await ExhaustAsync(client, Email);
        var fake = await ExhaustAsync(client, Unknown);

        var realBody = await BodyAsync(real);
        var fakeBody = await BodyAsync(fake);

        Assert.Equal(real.StatusCode, fake.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, real.StatusCode);

        foreach (var field in new[] { "title", "detail", "code", "status" })
        {
            Assert.True(
                realBody.GetProperty(field).ToString() == fakeBody.GetProperty(field).ToString(),
                $"The 429 body's '{field}' differs between an address that exists and one that does " +
                $"not: '{realBody.GetProperty(field)}' vs '{fakeBody.GetProperty(field)}'. Anything " +
                $"that differs here is an existence oracle the 401 was carefully built to withhold.");
        }

        // Retry-After is what makes the 429 actionable; it must be present for both and must not
        // encode which bucket filled up.
        Assert.NotNull(real.Headers.RetryAfter);
        Assert.NotNull(fake.Headers.RetryAfter);

        // And the header SETS match, so nothing leaks through a header that only one branch adds.
        Assert.Equal(
            real.Headers.Select(h => h.Key).Where(k => k != "Date").Order(StringComparer.OrdinalIgnoreCase),
            fake.Headers.Select(h => h.Key).Where(k => k != "Date").Order(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<HttpResponseMessage> ExhaustAsync(AuthApiClient client, string email)
    {
        for (var i = 0; i <= AuthAccountLimiter.PerEmailPerIpPermits; i++)
        {
            var response = await client.LoginAsync(email, $"guess-{i}");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return response;
        }

        throw new InvalidOperationException(
            $"'{email}' was never rate-limited in {AuthAccountLimiter.PerEmailPerIpPermits + 1} " +
            "attempts, so this test cannot compare two refusals.");
    }

    // ------------------------------------------------------------------------ timing equalisation

    /// <summary>
    /// <b>The decoy hash, asserted rather than trusted.</b> An unknown address must still pay for one
    /// password verification, or the response time separates "no such account" from "wrong password"
    /// with no status code involved at all.
    ///
    /// <para>
    /// This counts the verifications instead of timing them, which is the deterministic half of the
    /// claim: <see cref="An_unknown_address_takes_a_comparable_amount_of_time_to_refuse"/> covers the
    /// wall clock. A count is what survives a slow CI agent, and it is also what actually breaks if
    /// somebody deletes the decoy — the timing test would then fail intermittently on a fast machine
    /// while this one fails every time, on the right line.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unknown_address_still_pays_for_exactly_one_password_verification()
    {
        await ArrangeAsync();

        var counting = new CountingPasswordHasher(Passwords);

        await using var db = NewDbContext();
        var verifier = new UserCredentialVerifier(db, counting, NullLogger<UserCredentialVerifier>.Instance);

        var unknown = await verifier.VerifyAsync(Unknown, Password);
        var unknownCost = counting.Verifications;

        var wrongPassword = await verifier.VerifyAsync(Email, "not-the-password");
        var wrongPasswordCost = counting.Verifications - unknownCost;

        Assert.Equal(UserCredentialOutcome.UnknownEmail, unknown.Outcome);
        Assert.Equal(UserCredentialOutcome.PasswordMismatch, wrongPassword.Outcome);

        Assert.True(
            unknownCost == 1 && wrongPasswordCost == 1,
            $"An unknown address cost {unknownCost} password verification(s) and a wrong password " +
            $"cost {wrongPasswordCost}. They must both be exactly one: the decoy hash in " +
            $"UserCredentialVerifier exists so that a caller cannot tell the two apart by how long " +
            $"the refusal took.");
    }

    /// <summary>
    /// The wall-clock half. Deliberately one-sided and generous — it asks only that refusing an
    /// unknown address is not an ORDER OF MAGNITUDE faster than refusing a wrong password, which is
    /// what a missing decoy looks like (a string comparison against a PBKDF2 verification at 210,000
    /// iterations). A tight bound here would be a flaky test dressed as a security assertion.
    /// </summary>
    [Fact]
    public async Task An_unknown_address_takes_a_comparable_amount_of_time_to_refuse()
    {
        await ArrangeAsync();

        await using var db = NewDbContext();
        var verifier = CredentialsOn(db);

        // One of each first: the lazy decoy hash and EF's query plan are both one-off costs that
        // would otherwise land entirely on whichever path ran first.
        await verifier.VerifyAsync(Unknown, Password);
        await verifier.VerifyAsync(Email, "warm-up");

        var unknown = await MedianAsync(() => verifier.VerifyAsync(Unknown, Password));
        var wrong = await MedianAsync(() => verifier.VerifyAsync(Email, "not-the-password"));

        Assert.True(
            unknown > wrong / 4,
            $"Refusing an unknown address took a median of {unknown:F1}ms against {wrong:F1}ms for a " +
            $"wrong password. A gap that size is a usable existence oracle — it means the unknown " +
            $"path is returning without hashing anything, so the decoy in UserCredentialVerifier is " +
            $"either gone or no longer reached.");
    }

    private static async Task<double> MedianAsync(Func<Task<UserCredentialCheck>> attempt)
    {
        const int Samples = 7;
        var elapsed = new List<double>(Samples);

        for (var i = 0; i < Samples; i++)
        {
            var clock = Stopwatch.StartNew();
            await attempt();
            elapsed.Add(clock.Elapsed.TotalMilliseconds);
        }

        elapsed.Sort();
        return elapsed[Samples / 2];
    }

    // -------------------------------------------------------------------------------- the plumbing

    /// <summary>
    /// Uppercases the first <c>position + 1</c> characters. Progressive rather than one character at
    /// a time so that every spelling is distinct from every other AND from the canonical one — an
    /// index that happened to land on '@' would produce the canonical spelling and quietly spend a
    /// permit from the bucket this test is trying to prove is separate.
    /// </summary>
    private static string ShoutAt(string email, int position) =>
        email[..(position + 1)].ToUpperInvariant() + email[(position + 1)..];

    private sealed class CountingPasswordHasher : IPasswordHasher
    {
        private readonly IPasswordHasher _inner;

        public CountingPasswordHasher(IPasswordHasher inner) => _inner = inner;

        public int Verifications { get; private set; }

        public string Hash(string password) => _inner.Hash(password);

        public PasswordVerification Verify(string storedHash, string presentedPassword)
        {
            Verifications++;
            return _inner.Verify(storedHash, presentedPassword);
        }
    }
}
