using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The §11 login schema, asserted <b>against the migrated database rather than against the EF
/// model</b>.
///
/// <para>
/// <b>Why that distinction is the whole file.</b> EF will silently attach a
/// <c>WHERE [Column] IS NOT NULL</c> predicate to a unique index over a nullable column — the SQL
/// Server provider does it by default — and a unique index whose filter excludes every row constrains
/// nothing while appearing present in every schema diff and in every model snapshot.
/// <c>UX_Attendance_Event_Student_Occurrence</c> shipped in exactly that state and left the whole
/// attendance table unconstrained; <c>SchemaConstraintTests</c> exists because of it. A model-level
/// assertion would have agreed with the broken schema, which is why every check below reads
/// <c>sys.indexes</c> and <c>sys.columns</c>.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RbacSchemaTests : IntegrationTest
{
    public RbacSchemaTests(SqlServerFixture sql) : base(sql) { }

    private static readonly int[] UniqueViolation = [2601, 2627];

    private sealed record IndexInfo(
        bool IsUnique, bool HasFilter, string? FilterDefinition, string Columns);

    /// <summary>
    /// Index metadata straight from the catalogue views, including the key columns in order — the
    /// order matters for <c>UX_Users_Email</c>, where the interesting failure is a <c>SchoolId</c>
    /// appearing in front of the address.
    /// </summary>
    private async Task<IndexInfo> ReadIndexAsync(string indexName)
    {
        const string sql = """
            SELECT
                i.is_unique,
                i.has_filter,
                i.filter_definition,
                (
                    SELECT STRING_AGG(CAST(c.name AS NVARCHAR(MAX)), ',')
                           WITHIN GROUP (ORDER BY ic.key_ordinal)
                    FROM sys.index_columns AS ic
                    JOIN sys.columns AS c
                      ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE ic.object_id = i.object_id
                      AND ic.index_id = i.index_id
                      AND ic.is_included_column = 0
                ) AS key_columns
            FROM sys.indexes AS i
            WHERE i.name = @name;
            """;

        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@name", indexName);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Index {indexName} does not exist in the migrated schema.");

        return new IndexInfo(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? "" : reader.GetString(3));
    }

    private sealed record ColumnInfo(string Type, bool IsNullable, int MaxLength);

    private async Task<ColumnInfo?> ReadColumnAsync(string table, string column)
    {
        const string sql = """
            SELECT t.name, c.is_nullable, c.max_length
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@table) AND c.name = @column;
            """;

        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new ColumnInfo(reader.GetString(0), reader.GetBoolean(1), reader.GetInt16(2));
    }

    private async Task<Guid> ArrangeUserAsync(string email = "schema@test.local")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();

        return await CreateUserAsync(school.Id, email, "a sufficiently long passphrase");
    }

    // -------------------------------------------------------- UX_RefreshTokens_TokenHash

    /// <summary>
    /// <b>Unique, and unfiltered.</b> <c>TokenHash</c> is <c>NOT NULL</c> today, so the provider
    /// attaches no automatic predicate — but a later change making the column nullable would acquire
    /// one silently, and the index would then constrain nothing while still appearing in the schema.
    /// That is not hypothetical in this codebase; it is what
    /// <c>UX_Attendance_Event_Student_Occurrence</c> shipped as.
    /// </summary>
    [Fact]
    public async Task The_refresh_token_hash_index_is_unique_and_unfiltered()
    {
        var index = await ReadIndexAsync("UX_RefreshTokens_TokenHash");

        Assert.True(index.IsUnique, "UX_RefreshTokens_TokenHash is not unique.");
        Assert.False(
            index.HasFilter,
            "UX_RefreshTokens_TokenHash is filtered on " + index.FilterDefinition +
            ". A filtered unique index over the token hash would leave every excluded row " +
            "unconstrained, and two rows sharing a hash means two live tokens redeeming each " +
            "other's family.");
        Assert.Null(index.FilterDefinition);
        Assert.Equal("TokenHash", index.Columns);
    }

    /// <summary>
    /// The column is sized to a SHA-256 in lower-case hex exactly, and is NOT NULL. A hash that does
    /// not fit is not a SHA-256, and SQL Server says so rather than storing a truncated one that still
    /// looks plausible in a row dump — the same reasoning §4.12's <c>FileHash</c> and §4.10's
    /// <c>ApiKeyHash</c> are sized by.
    /// </summary>
    [Fact]
    public async Task The_refresh_token_hash_column_is_sized_to_a_sha256_and_is_not_nullable()
    {
        var column = await ReadColumnAsync("dbo.RefreshTokens", "TokenHash");

        Assert.NotNull(column);
        Assert.False(column!.IsNullable);
        Assert.Equal("nvarchar", column.Type);
        Assert.Equal(RefreshTokenValue.HashLength * 2, column.MaxLength); // max_length is bytes for nvarchar
    }

    /// <summary>The behaviour the index exists for, exercised against SQL Server rather than inferred.</summary>
    [Fact]
    public async Task Two_refresh_tokens_cannot_share_a_hash()
    {
        var userId = await ArrangeUserAsync();
        var shared = RefreshTokenValue.Issue();

        static RefreshToken Row(Guid userId, string hash) => new()
        {
            UserId = userId,
            FamilyId = Guid.NewGuid(),
            TokenHash = hash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            FamilyExpiresAt = DateTime.UtcNow.AddDays(30),
        };

        await using (var first = NewDbContext())
        {
            first.RefreshTokens.Add(Row(userId, shared.Hash));
            await first.SaveChangesAsync();
        }

        await using var second = NewDbContext();
        second.RefreshTokens.Add(Row(userId, shared.Hash));

        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        var sql = Assert.IsType<SqlException>(rejected.InnerException);

        Assert.Contains(sql.Number, UniqueViolation);
        Assert.Contains("UX_RefreshTokens_TokenHash", sql.Message);
    }

    /// <summary>
    /// The family index exists and is <em>not</em> unique. Both halves matter: a replay revokes every
    /// live token in a family, which is a range update on the busiest table the auth system has, and a
    /// unique index there would make a family of more than one token impossible — which is every
    /// family after its first rotation.
    /// </summary>
    [Fact]
    public async Task The_family_index_exists_and_is_not_unique()
    {
        var index = await ReadIndexAsync("IX_RefreshTokens_FamilyId");

        Assert.False(
            index.IsUnique,
            "IX_RefreshTokens_FamilyId is unique. A family holds one row per rotation, so a unique " +
            "index over it would fail on the first refresh of every session.");
        Assert.Equal("FamilyId", index.Columns);
    }

    [Fact]
    public async Task The_user_session_index_leads_with_the_user()
    {
        var index = await ReadIndexAsync("IX_RefreshTokens_UserId_IssuedAt");

        Assert.False(index.IsUnique);
        Assert.Equal("UserId,IssuedAt", index.Columns);
    }

    // ------------------------------------------------------------------- UX_Users_Email

    /// <summary>
    /// <b><c>UX_Users_Email</c> is globally unique, over <c>Email</c> alone, with no <c>SchoolId</c>
    /// in the key. Login depends on it, and this test is the only thing that would notice it
    /// changing.</b>
    ///
    /// <para>
    /// <b>The change this exists to catch would arrive looking like a tenancy fix.</b> Every other
    /// unique index in this schema leads with <c>SchoolId</c>, and ADR-001 D-3 is an explicit
    /// precedent for narrowing a plan-level global unique to a per-school one —
    /// <c>RfidCards.CardUid</c> was rescoped for exactly that reason. A reviewer noticing that
    /// <c>Users</c> is the odd one out would have a real-looking argument and a precedent to cite.
    /// </para>
    ///
    /// <para>
    /// <b>It is not the same case, and the difference is who is asking.</b> A card is scanned by a
    /// reader that already knows its school; an e-mail is typed into a login box by somebody the
    /// system has not identified yet. <c>UserCredentialVerifier</c> looks a user up by address and
    /// nothing else, before any tenant exists to scope by, so a <c>(SchoolId, Email)</c> key would
    /// make that lookup ambiguous the moment a second school existed — and <b>nothing else in this
    /// suite would fail</b>. Every login test would pass on a single-school database, which is every
    /// database this project has today.
    /// </para>
    ///
    /// <para>
    /// If multi-tenant address reuse ever becomes a requirement, it is a redesign of how a user
    /// identifies themselves at login — a tenant selector, or an address that carries its school —
    /// not an index change. Register it as an ADR decision rather than widening this key.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_users_email_index_is_globally_unique_over_the_address_alone()
    {
        var index = await ReadIndexAsync("UX_Users_Email");

        Assert.True(index.IsUnique, "UX_Users_Email is not unique — login has no single answer.");

        Assert.Equal("Email", index.Columns);
        Assert.False(
            index.Columns.Contains("SchoolId", StringComparison.OrdinalIgnoreCase),
            "UX_Users_Email now includes SchoolId. Read this test's remarks before changing it: a " +
            "password is presented before any tenant is resolved, so the lookup by e-mail must have " +
            "exactly one answer. This is deliberately unlike RfidCards.CardUid (ADR-001 D-3), which " +
            "is read by a device that already knows its school.");

        Assert.False(
            index.HasFilter,
            "UX_Users_Email is filtered on " + index.FilterDefinition +
            ". Email is NOT NULL, so a filter here excludes rows the uniqueness must cover.");
    }

    /// <summary>
    /// The behaviour, across two schools. This is the assertion a rescope to
    /// <c>(SchoolId, Email)</c> would break — and the only one.
    /// </summary>
    [Fact]
    public async Task One_address_cannot_belong_to_two_users_even_in_different_schools()
    {
        const string email = "shared@test.local";

        Guid firstSchool, secondSchool;
        await using (var db = NewDbContext())
        {
            var a = TestData.NewSchool("AAA");
            var b = TestData.NewSchool("BBB");
            db.Schools.AddRange(a, b);
            await db.SaveChangesAsync();
            (firstSchool, secondSchool) = (a.Id, b.Id);
        }

        await CreateUserAsync(firstSchool, email, "a sufficiently long passphrase");

        await using var second = NewDbContext();
        second.Users.Add(new User
        {
            SchoolId = secondSchool,
            Email = email,
            FullName = "Second School User",
            PasswordHash = "irrelevant",
        });

        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        var sql = Assert.IsType<SqlException>(rejected.InnerException);

        Assert.Contains(sql.Number, UniqueViolation);
        Assert.Contains("UX_Users_Email", sql.Message);
    }

    // ---------------------------------------------------- Users.RefreshTokenHash: kept, never written

    /// <summary>
    /// <b>§4.11's <c>RefreshTokenHash</c> column still exists.</b> The <c>RefreshTokens</c> table
    /// supersedes it — one column cannot express rotation, replay detection or more than one session —
    /// but dropping it would be data-loss SQL under the global rule, and it is the plan's own column,
    /// so removing it would make §4.11 and the schema disagree with nothing recording why. Exactly the
    /// decision <c>Device.ApiKey</c> records one phase earlier.
    /// </summary>
    [Fact]
    public async Task The_plan_s_RefreshTokenHash_column_is_kept_and_nullable()
    {
        var column = await ReadColumnAsync("dbo.Users", "RefreshTokenHash");

        Assert.True(
            column is not null,
            "Users.RefreshTokenHash has been dropped. §4.11 declares it, the RefreshTokens table " +
            "supersedes it, and it is kept permanently NULL rather than removed — dropping a column " +
            "is data-loss SQL under the global rule and would make the schema disagree with the plan.");

        Assert.True(column!.IsNullable);
        Assert.Equal("nvarchar", column.Type);
        Assert.Equal(-1, column.MaxLength); // nvarchar(max), per §4.11
    }

    /// <summary>
    /// And nothing writes it. Asserted after provisioning a user through the real path — the one
    /// place in this phase that composes a <c>Users</c> row — because "nothing writes it" only means
    /// something once something has had the opportunity.
    /// </summary>
    [Fact]
    public async Task Provisioning_a_user_leaves_the_plan_s_RefreshTokenHash_column_null()
    {
        var userId = await ArrangeUserAsync("kept-null@test.local");

        await using var db = NewDbContext();
        var stored = await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => u.RefreshTokenHash)
            .SingleAsync();

        Assert.Null(stored);
    }

    // ------------------------------------------------------------------- AuditLogs.SchoolId

    /// <summary>
    /// The nullable tenant column §11's audit filter was deferred for. Nullable because a
    /// system-generated entry belongs to no school, and a NULL must stay visible under every tenant
    /// when that filter is eventually written — the same three-way shape <c>SystemSettings</c> uses.
    /// </summary>
    [Fact]
    public async Task Audit_logs_carry_a_nullable_school_id()
    {
        var column = await ReadColumnAsync("dbo.AuditLogs", "SchoolId");

        Assert.NotNull(column);
        Assert.Equal("uniqueidentifier", column!.Type);
        Assert.True(
            column.IsNullable,
            "AuditLogs.SchoolId is NOT NULL. A system-generated entry belongs to no school, and a " +
            "required column here would make those entries unwritable.");
    }

    /// <summary>
    /// A null tenant is accepted — the state every audit row written before this migration is in, and
    /// the state a system-generated entry will always be in.
    /// </summary>
    [Fact]
    public async Task An_audit_row_with_no_school_is_accepted()
    {
        await using var db = NewDbContext();

        db.AuditLogs.Add(new AuditLog { Action = "system.started", SchoolId = null, UserId = null });

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.SchoolId == null));
    }
}
