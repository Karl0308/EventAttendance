using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;
using RefreshTokenStoreType = EAMS.Infrastructure.Services.RefreshTokenStore;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b><c>RefreshTokenStore</c> — rotation, replay, expiry and the 30-day family ceiling</b>, exercised
/// against real SQL Server because every one of them is a statement about a row and at least one is a
/// statement about a race.
///
/// <para>
/// <b>Why none of this could be a unit test.</b> The redemption is a compare-and-swap: an UPDATE that
/// filters on <c>RevokedAt IS NULL</c> and is meaningful only because the database decides which of two
/// concurrent callers affects a row. In memory, both would win, the test would pass, and the property
/// the whole replay design rests on would be untested — the same reason
/// <c>SqlServerFixture</c> refuses EF InMemory for the tap flow's races.
/// </para>
///
/// <para>
/// <b>On ageing rows instead of injecting a clock.</b> The expiry tests move a row's timestamps
/// backwards with a direct UPDATE after issuing it through the real store. That is manipulating
/// <em>time</em>, not minting a credential by a rule production does not use — the token itself is
/// always issued by <c>IRefreshTokenStore.IssueAsync</c>, per the principle
/// <c>IntegrationTest.IssueDeviceKeyAsync</c> records.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RefreshTokenRotationTests : IntegrationTest
{
    public RefreshTokenRotationTests(SqlServerFixture sql) : base(sql) { }

    private const string Password = "a sufficiently long passphrase";

    /// <summary>A school and a user, created through the production provisioning path.</summary>
    private async Task<(Guid SchoolId, Guid UserId)> ArrangeUserAsync(string email = "user@test.local")
    {
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        return (schoolId, await CreateUserAsync(schoolId, email, Password));
    }

    /// <summary>
    /// Moves a token's own expiry and its family's ceiling into the past, as a database write rather
    /// than through the store — the store has no way to issue an expired token, which is the point.
    /// </summary>
    private async Task AgeAsync(Guid tokenId, TimeSpan by)
    {
        await using var db = NewDbContext();
        var expiresAt = DateTime.UtcNow - by;

        await db.RefreshTokens
            .Where(t => t.Id == tokenId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, expiresAt));
    }

    private async Task<RefreshToken> ReadAsync(Guid tokenId)
    {
        await using var db = NewDbContext();
        return await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == tokenId);
    }

    private static Guid IdOf(string token)
    {
        Assert.True(RefreshTokenValue.TryParse(token, out var id, out _));
        return id;
    }

    // --------------------------------------------------------------------------------- issuing

    [Fact]
    public async Task Issuing_stores_a_hash_and_never_the_token()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var issued = await RefreshTokensOn(db).IssueAsync(userId, "Firefox/1.0", "203.0.113.7");

        Assert.Equal(RefreshTokenOutcome.Rotated, issued.Outcome);
        Assert.NotNull(issued.Token);

        var row = await ReadAsync(IdOf(issued.Token!));

        Assert.Equal(userId, row.UserId);
        Assert.Null(row.RevokedAt);
        Assert.Null(row.ReplacedByTokenId);
        Assert.Equal("Firefox/1.0", row.UserAgent);
        Assert.Equal("203.0.113.7", row.IpAddress);

        // The stored value is the SHA-256 of the secret half, and the token cannot be reconstructed
        // from it. A store that saved the secret would pass every rotation test in this file.
        Assert.Equal(RefreshTokenValue.HashLength, row.TokenHash.Length);
        Assert.DoesNotContain(row.TokenHash, issued.Token!, StringComparison.OrdinalIgnoreCase);
        Assert.True(RefreshTokenValue.SecretMatches(SecretOf(issued.Token!), row.TokenHash));
    }

    private static string SecretOf(string token)
    {
        Assert.True(RefreshTokenValue.TryParse(token, out _, out var secret));
        return secret;
    }

    [Fact]
    public async Task Issuing_sets_the_family_ceiling_thirty_days_out_and_the_token_fourteen()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var issued = await RefreshTokensOn(db).IssueAsync(userId, null, null);
        var row = await ReadAsync(IdOf(issued.Token!));

        // A minute of slack: the assertion is about the policy, not about clock precision.
        Assert.Equal(
            row.IssuedAt + RefreshTokenPolicy.TokenLifetime, row.ExpiresAt,
            TimeSpan.FromMinutes(1));
        Assert.Equal(
            row.IssuedAt + RefreshTokenPolicy.FamilyLifetime, row.FamilyExpiresAt,
            TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Two_logins_by_one_user_start_two_independent_families()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var laptop = await store.IssueAsync(userId, "laptop", null);
        var phone = await store.IssueAsync(userId, "phone", null);

        Assert.NotEqual(laptop.FamilyId, phone.FamilyId);

        // Revoking one session leaves the other alone — the property that makes "sign out this
        // device" a different operation from "sign out everywhere".
        await store.RevokeFamilyAsync(laptop.FamilyId);

        Assert.Equal(RefreshTokenOutcome.Rotated, (await store.RotateAsync(phone.Token!, null, null)).Outcome);
    }

    // -------------------------------------------------------------------------------- rotation

    /// <summary>
    /// The happy path, and the three things it must leave behind: a new redeemable token, a revoked
    /// predecessor, and a link from one to the other.
    /// </summary>
    [Fact]
    public async Task Rotating_issues_a_successor_and_revokes_the_presented_token()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var first = await store.IssueAsync(userId, "browser", "203.0.113.7");
        var second = await store.RotateAsync(first.Token!, "browser", "203.0.113.7");

        Assert.Equal(RefreshTokenOutcome.Rotated, second.Outcome);
        Assert.NotNull(second.Token);
        Assert.NotEqual(first.Token, second.Token);

        // Same session: the family is what a sign-out revokes, so it must survive rotation.
        Assert.Equal(first.FamilyId, second.FamilyId);
        Assert.Equal(userId, second.UserId);

        var predecessor = await ReadAsync(IdOf(first.Token!));
        Assert.NotNull(predecessor.RevokedAt);
        Assert.Equal(IdOf(second.Token!), predecessor.ReplacedByTokenId);

        var successor = await ReadAsync(IdOf(second.Token!));
        Assert.Null(successor.RevokedAt);
        Assert.Equal(first.FamilyId, successor.FamilyId);
    }

    /// <summary>
    /// A chain of rotations stays one family, and each successor is redeemable exactly once. Ten
    /// rather than two, because an off-by-one in how the family is carried forward shows up on the
    /// third hop and not the second.
    /// </summary>
    [Fact]
    public async Task A_chain_of_rotations_stays_one_family()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var current = await store.IssueAsync(userId, "browser", null);
        var familyId = current.FamilyId;

        for (var hop = 0; hop < 10; hop++)
        {
            current = await store.RotateAsync(current.Token!, "browser", null);
            Assert.Equal(RefreshTokenOutcome.Rotated, current.Outcome);
            Assert.Equal(familyId, current.FamilyId);
        }

        await using var read = NewDbContext();
        var family = await read.RefreshTokens.AsNoTracking()
            .Where(t => t.FamilyId == familyId).ToListAsync();

        Assert.Equal(11, family.Count);
        Assert.Single(family, t => t.RevokedAt is null);
    }

    /// <summary>
    /// <b>The ceiling never moves.</b> A rotation that recomputed <c>FamilyExpiresAt</c> would turn
    /// rotation into unbounded renewal — a session that refreshes daily would never be re-authenticated
    /// — and nothing else in this suite would notice, because every other assertion here is about a
    /// single hop.
    /// </summary>
    [Fact]
    public async Task Rotation_inherits_the_family_ceiling_and_never_extends_it()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var first = await store.IssueAsync(userId, null, null);
        var original = (await ReadAsync(IdOf(first.Token!))).FamilyExpiresAt;

        var current = first;
        for (var hop = 0; hop < 5; hop++)
        {
            current = await store.RotateAsync(current.Token!, null, null);
            Assert.Equal(original, (await ReadAsync(IdOf(current.Token!))).FamilyExpiresAt);
        }
    }

    /// <summary>
    /// A token issued close to the ceiling expires <em>at</em> the ceiling, not fourteen days past it.
    /// Without the clamp the ceiling would be enforced only by a second check that a refactor could
    /// drop, and the last token of a family would outlive the session it belongs to.
    /// </summary>
    [Fact]
    public async Task A_token_issued_near_the_ceiling_never_outlives_it()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var first = await store.IssueAsync(userId, null, null);

        // Pull the family ceiling to two days out — a family 28 days into its 30.
        await using (var age = NewDbContext())
        {
            await age.RefreshTokens
                .Where(t => t.Id == IdOf(first.Token!))
                .ExecuteUpdateAsync(s => s.SetProperty(
                    t => t.FamilyExpiresAt, DateTime.UtcNow.AddDays(2)));
        }

        var second = await store.RotateAsync(first.Token!, null, null);
        var successor = await ReadAsync(IdOf(second.Token!));

        Assert.Equal(successor.FamilyExpiresAt, successor.ExpiresAt);
        Assert.True(
            successor.ExpiresAt < DateTime.UtcNow + RefreshTokenPolicy.TokenLifetime,
            "The successor was given the full token lifetime and so outlives its family's ceiling.");
    }

    // ----------------------------------------------------------------------------------- replay

    /// <summary>
    /// <b>The centrepiece.</b> Presenting an already-rotated token revokes the <em>entire family</em>,
    /// not just that token.
    ///
    /// <para>
    /// The server cannot distinguish a thief replaying a copied token from a legitimate client
    /// retrying after losing the response that carried its successor. Burning the family answers both:
    /// the honest client pays one re-login, the thief's chain dies at the same moment. Tolerating
    /// replay to spare the first case is exactly what would make the second case undetectable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Replaying_a_rotated_token_revokes_the_whole_family()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var first = await store.IssueAsync(userId, "browser", null);
        var second = await store.RotateAsync(first.Token!, "browser", null);
        Assert.Equal(RefreshTokenOutcome.Rotated, second.Outcome);

        // The stolen copy, presented after the legitimate client has already moved on.
        var replay = await store.RotateAsync(first.Token!, "attacker", null);

        Assert.Equal(RefreshTokenOutcome.ReplayDetected, replay.Outcome);
        Assert.Null(replay.Token);

        // And the successor the honest client is holding is dead too. That is the cost, and it is the
        // point — a family that kept its live token would leave the thief with a working chain.
        var afterwards = await store.RotateAsync(second.Token!, "browser", null);
        Assert.Equal(RefreshTokenOutcome.ReplayDetected, afterwards.Outcome);

        await using var read = NewDbContext();
        var family = await read.RefreshTokens.AsNoTracking()
            .Where(t => t.FamilyId == first.FamilyId).ToListAsync();

        Assert.All(family, t => Assert.NotNull(t.RevokedAt));
    }

    /// <summary>
    /// A replay burns one session, not the user's other devices. The failure this guards is a family
    /// revoke written as "revoke everything this user holds", which would turn one stolen token on one
    /// laptop into a global sign-out — punitive, and a denial-of-service anyone holding one old token
    /// could trigger.
    /// </summary>
    [Fact]
    public async Task A_replay_does_not_touch_the_users_other_sessions()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var laptop = await store.IssueAsync(userId, "laptop", null);
        var phone = await store.IssueAsync(userId, "phone", null);

        await store.RotateAsync(laptop.Token!, "laptop", null);
        Assert.Equal(
            RefreshTokenOutcome.ReplayDetected,
            (await store.RotateAsync(laptop.Token!, "attacker", null)).Outcome);

        Assert.Equal(
            RefreshTokenOutcome.Rotated,
            (await store.RotateAsync(phone.Token!, "phone", null)).Outcome);
    }

    /// <summary>The family burn is logged, because a replay is either a client bug or a theft.</summary>
    [Fact]
    public async Task A_family_burn_is_reported_in_the_log()
    {
        var (_, userId) = await ArrangeUserAsync();

        var logger = new CapturingLogger<RefreshTokenStoreType>();
        await using var db = NewDbContext();
        var store = RefreshTokensOn(db, logger);

        var first = await store.IssueAsync(userId, "browser", null);
        await store.RotateAsync(first.Token!, "browser", null);
        await store.RotateAsync(first.Token!, "attacker", null);

        Assert.Contains(
            logger.At(LogLevel.Warning),
            e => e.Message.Contains(first.FamilyId.ToString(), StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The compare-and-swap, raced for real.</b> Two callers redeem one token at the same instant
    /// on two connections. Exactly one may rotate; the other must be treated as a replay — which is
    /// the honest answer, since from the server's side it is indistinguishable from one.
    ///
    /// <para>
    /// Separate contexts on purpose: a shared <c>DbContext</c> is not thread-safe and would serialize
    /// the two calls, which is precisely the race being tested away.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_concurrent_redemptions_of_one_token_produce_exactly_one_successor()
    {
        var (_, userId) = await ArrangeUserAsync();

        RefreshTokenRotation issued;
        await using (var db = NewDbContext())
            issued = await RefreshTokensOn(db).IssueAsync(userId, "browser", null);

        async Task<RefreshTokenRotation> Redeem()
        {
            await using var db = NewDbContext();
            return await RefreshTokensOn(db).RotateAsync(issued.Token!, "browser", null);
        }

        var results = await Task.WhenAll(Redeem(), Redeem());

        Assert.Equal(1, results.Count(r => r.Outcome == RefreshTokenOutcome.Rotated));
        Assert.Equal(1, results.Count(r => r.Outcome == RefreshTokenOutcome.ReplayDetected));

        await using var read = NewDbContext();
        var family = await read.RefreshTokens.AsNoTracking()
            .Where(t => t.FamilyId == issued.FamilyId).ToListAsync();

        // The winner issued a successor; the loser burned the family. Everything is revoked, and there
        // is at most one successor row — never two live tokens sharing a family, which is the state
        // replay detection exists to make impossible.
        Assert.All(family, t => Assert.NotNull(t.RevokedAt));
        Assert.True(family.Count <= 2, $"{family.Count} rows in the family; at most 2 may exist.");
    }

    // ----------------------------------------------------------------------------------- expiry

    [Fact]
    public async Task An_expired_token_is_refused_and_is_not_a_replay()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var issued = await store.IssueAsync(userId, "browser", null);
        await AgeAsync(IdOf(issued.Token!), RefreshTokenPolicy.TokenLifetime + TimeSpan.FromMinutes(1));

        var refused = await store.RotateAsync(issued.Token!, "browser", null);

        Assert.Equal(RefreshTokenOutcome.Expired, refused.Outcome);
        Assert.Null(refused.Token);

        // Expiry is not evidence of anything. Burning the family on it would sign a user out of a
        // session they simply had not used in a fortnight, and would make the Expired member
        // indistinguishable in effect from ReplayDetected.
        var row = await ReadAsync(IdOf(issued.Token!));
        Assert.Null(row.RevokedAt);
    }

    /// <summary>
    /// <b>The 30-day cap.</b> A token still inside its own fourteen days, in a family past its
    /// ceiling, is refused — and refused as <see cref="RefreshTokenOutcome.FamilyExpired"/> rather
    /// than as ordinary expiry, because the two mean different things to an operator reading a log.
    /// </summary>
    [Fact]
    public async Task A_token_whose_family_has_reached_the_thirty_day_cap_is_refused()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var issued = await store.IssueAsync(userId, "browser", null);

        // Only the family ceiling moves. The token's own expiry stays comfortably in the future, so
        // the sole reason this can be refused is the cap.
        await using (var age = NewDbContext())
        {
            await age.RefreshTokens
                .Where(t => t.Id == IdOf(issued.Token!))
                .ExecuteUpdateAsync(s => s.SetProperty(
                    t => t.FamilyExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        var row = await ReadAsync(IdOf(issued.Token!));
        Assert.True(row.ExpiresAt > DateTime.UtcNow, "The token's own expiry must still be in the future.");

        var refused = await store.RotateAsync(issued.Token!, "browser", null);
        Assert.Equal(RefreshTokenOutcome.FamilyExpired, refused.Outcome);
    }

    /// <summary>
    /// A revoked-and-expired token is still reported as a replay. Order matters: checking expiry
    /// first would drop the signal that somebody is holding a copy, on exactly the token most likely
    /// to have been sitting in a log or a backup long enough to be found.
    /// </summary>
    [Fact]
    public async Task A_replayed_token_that_is_also_expired_is_still_reported_as_a_replay()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var first = await store.IssueAsync(userId, "browser", null);
        await store.RotateAsync(first.Token!, "browser", null);
        await AgeAsync(IdOf(first.Token!), RefreshTokenPolicy.TokenLifetime + TimeSpan.FromDays(1));

        Assert.Equal(
            RefreshTokenOutcome.ReplayDetected,
            (await store.RotateAsync(first.Token!, "attacker", null)).Outcome);
    }

    // ------------------------------------------------------------------------- refusals and state

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("eams_rt_short")]
    public async Task A_malformed_token_is_refused_without_a_query(string token)
    {
        await using var db = NewDbContext();

        var refused = await RefreshTokensOn(db).RotateAsync(token, null, null);

        Assert.Equal(RefreshTokenOutcome.Malformed, refused.Outcome);
    }

    [Fact]
    public async Task A_well_formed_token_for_a_row_that_does_not_exist_is_unknown()
    {
        await using var db = NewDbContext();

        var stranger = RefreshTokenValue.Issue().Token;

        Assert.Equal(
            RefreshTokenOutcome.Unknown,
            (await RefreshTokensOn(db).RotateAsync(stranger, null, null)).Outcome);
    }

    /// <summary>
    /// <b>A wrong secret against a real token id leaves the row alone.</b> Revoking on a failed guess
    /// would hand anyone who learned a token id — from a log, a crash dump, a shoulder — a one-request
    /// way to sign the owner out.
    /// </summary>
    [Fact]
    public async Task A_wrong_secret_against_a_real_token_id_is_refused_without_revoking_anything()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var issued = await store.IssueAsync(userId, "browser", null);

        var forged = RefreshTokenValue.TokenPrefix
            + IdOf(issued.Token!).ToString("N") + "_"
            + RefreshTokenValue.Issue().Secret;

        var refused = await store.RotateAsync(forged, "attacker", null);

        Assert.Equal(RefreshTokenOutcome.SecretMismatch, refused.Outcome);
        Assert.Null((await ReadAsync(IdOf(issued.Token!))).RevokedAt);

        // And the real token still works.
        Assert.Equal(
            RefreshTokenOutcome.Rotated,
            (await store.RotateAsync(issued.Token!, "browser", null)).Outcome);
    }

    /// <summary>
    /// Deactivating a user must end their live sessions at the next refresh. Without it, an account
    /// turned off at 09:00 keeps refreshing for another fortnight — the deactivation would apply to
    /// new logins only, which is the opposite of what an operator turning off an account means by it.
    /// </summary>
    [Fact]
    public async Task A_deactivated_user_cannot_refresh_and_the_family_is_burned()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var issued = await store.IssueAsync(userId, "browser", null);

        await using (var deactivate = NewDbContext())
        {
            await deactivate.Users.IgnoreQueryFilters()
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        var refused = await store.RotateAsync(issued.Token!, "browser", null);

        Assert.Equal(RefreshTokenOutcome.UserInactive, refused.Outcome);
        Assert.NotNull((await ReadAsync(IdOf(issued.Token!))).RevokedAt);
    }

    // ----------------------------------------------------------------------------- revoke and list

    [Fact]
    public async Task Revoking_everything_a_user_holds_ends_every_session()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var laptop = await store.IssueAsync(userId, "laptop", null);
        var phone = await store.IssueAsync(userId, "phone", null);

        var revoked = await store.RevokeAllForUserAsync(userId);
        Assert.Equal(2, revoked);

        Assert.Equal(
            RefreshTokenOutcome.ReplayDetected,
            (await store.RotateAsync(laptop.Token!, null, null)).Outcome);
        Assert.Equal(
            RefreshTokenOutcome.ReplayDetected,
            (await store.RotateAsync(phone.Token!, null, null)).Outcome);
    }

    /// <summary>
    /// A session list is one entry per family, not per token — a session that has refreshed forty
    /// times is one session, and forty rows would make "revoke that laptop" a question about which
    /// row.
    /// </summary>
    [Fact]
    public async Task Listing_sessions_returns_one_entry_per_family()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var laptop = await store.IssueAsync(userId, "laptop", "203.0.113.7");
        await store.IssueAsync(userId, "phone", "203.0.113.8");

        // Rotate the laptop three times: still one session.
        var current = laptop;
        for (var hop = 0; hop < 3; hop++)
            current = await store.RotateAsync(current.Token!, "laptop", "203.0.113.7");

        var sessions = await store.ListSessionsAsync(userId);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(2, sessions.Select(s => s.FamilyId).Distinct().Count());
        Assert.Contains(sessions, s => s.UserAgent == "laptop");
        Assert.Contains(sessions, s => s.UserAgent == "phone");
    }

    [Fact]
    public async Task A_revoked_session_is_not_listed()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var laptop = await store.IssueAsync(userId, "laptop", null);
        await store.IssueAsync(userId, "phone", null);

        await store.RevokeFamilyAsync(laptop.FamilyId);

        var sessions = await store.ListSessionsAsync(userId);

        Assert.Single(sessions);
        Assert.Equal("phone", sessions[0].UserAgent);
    }

    /// <summary>
    /// A session list carries nothing redeemable. The failure it guards is the one that would make an
    /// otherwise harmless "your devices" screen a credential dump.
    /// </summary>
    [Fact]
    public async Task A_session_entry_carries_no_token_and_no_hash()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var issued = await store.IssueAsync(userId, "laptop", null);
        var session = Assert.Single(await store.ListSessionsAsync(userId));

        var rendered = System.Text.Json.JsonSerializer.Serialize(session);

        Assert.DoesNotContain(issued.Token!, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            (await ReadAsync(IdOf(issued.Token!))).TokenHash, rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Nothing in this flow ever writes <c>Users.RefreshTokenHash</c>.</b> §4.11's single-column
    /// design is superseded by the <c>RefreshTokens</c> table and the column is kept permanently NULL
    /// — the same decision <c>Device.ApiKey</c> records one phase earlier. Asserted after a full
    /// issue-rotate-revoke cycle, because "nothing writes it" is only interesting once something has
    /// had every opportunity to.
    /// </summary>
    [Fact]
    public async Task The_plan_s_single_RefreshTokenHash_column_is_never_written()
    {
        var (_, userId) = await ArrangeUserAsync();

        await using var db = NewDbContext();
        var store = RefreshTokensOn(db);

        var first = await store.IssueAsync(userId, "browser", null);
        var second = await store.RotateAsync(first.Token!, "browser", null);
        await store.RotateAsync(first.Token!, "attacker", null); // replay, burns the family
        await store.IssueAsync(userId, "browser", null);
        await store.RevokeAllForUserAsync(userId);

        Assert.Equal(RefreshTokenOutcome.Rotated, second.Outcome);

        await using var read = NewDbContext();
        var written = await read.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.RefreshTokenHash != null)
            .Select(u => u.Email)
            .ToListAsync();

        Assert.True(
            written.Count == 0,
            $"Users.RefreshTokenHash was written for: {string.Join(", ", written)}. §4.11's single " +
            "column cannot express rotation, replay detection or more than one session, so the " +
            "RefreshTokens table supersedes it and the column stays permanently NULL. Writing it " +
            "would create a second, disagreeing source of truth about which token is live.");
    }
}
