using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Services;
using EAMS.Infrastructure.Sis;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 D-98's proving test: one card UID identifies one person in a school, across
/// <c>RfidCards</c> and <c>Personnel.RfidUid</c>, and the transaction-owned app lock is what makes that
/// true under concurrency. Neither table's own unique index can see the other, so without the lock two
/// writers on different tables both pass their check and both commit.
///
/// <para>
/// <b>Deterministic, not a timing race.</b> Writer A is built with a <see cref="HoldingBarrier"/>, the
/// <see cref="ICardUidWriteBarrier"/> seam every D-98 writer awaits between its re-check and its write
/// (production registers the no-op). A parks there holding the lock; writer B, on the other table, is
/// then started and must be seen <em>waiting on an APPLICATION lock</em> in
/// <c>sys.dm_tran_locks</c>; only then is A released. When A commits, B's re-check finds A's claim and B
/// is refused. With the lock removed, B is never seen waiting, commits while A is parked, and A then
/// commits too — two claims on one UID, which is the first assertion each race test makes.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CardUidRaceTests : IntegrationTest
{
    public CardUidRaceTests(SqlServerFixture sql) : base(sql) { }

    private const string Uid = "04A7B8C9";
    private const string StudentNumber = "2023-0001";
    private const string PersonnelNumber = "P-0001";

    /// <summary>A ceiling on every wait, so a broken seam fails the test instead of hanging it.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // ======================================================================= personnel vs student card

    [Fact]
    public async Task A_student_card_issue_holding_the_lock_makes_a_personnel_create_wait_and_then_refuses_it()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        var race = await RaceAsync(
            async barrier =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db, barrier).AddCardAsync(studentId, new StudentCardRequest(Uid, null));
            },
            async () =>
            {
                await using var db = NewDbContext();
                return await PersonnelOn(db, schoolId, NoCardUidWriteBarrier.Instance)
                    .CreateAsync(PersonnelRequest(PersonnelNumber, Uid));
            });

        await AssertExactlyOneClaimAsync(schoolId, Uid);
        Assert.True(race.BWaitedOnLock, "The personnel create never waited on the card-UID app lock.");
        Assert.Equal(StudentWriteOutcome.Saved, race.A.Outcome);
        Assert.Equal(PersonnelWriteOutcome.DuplicateRfid, race.B.Outcome);
        Assert.Contains($"student '{StudentNumber}'", race.B.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_personnel_create_holding_the_lock_makes_a_student_card_issue_wait_and_then_refuses_it()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        var race = await RaceAsync(
            async barrier =>
            {
                await using var db = NewDbContext();
                return await PersonnelOn(db, schoolId, barrier).CreateAsync(PersonnelRequest(PersonnelNumber, Uid));
            },
            async () =>
            {
                await using var db = NewDbContext();
                return await StudentsOn(db, NoCardUidWriteBarrier.Instance)
                    .AddCardAsync(studentId, new StudentCardRequest(Uid, null));
            });

        await AssertExactlyOneClaimAsync(schoolId, Uid);
        Assert.True(race.BWaitedOnLock, "The student card issue never waited on the card-UID app lock.");
        Assert.Equal(PersonnelWriteOutcome.Saved, race.A.Outcome);
        Assert.Equal(StudentWriteOutcome.CardUidInUse, race.B.Outcome);
        Assert.Contains($"personnel record '{PersonnelNumber}'", race.B.Message, StringComparison.Ordinal);
    }

    // ================================================================ personnel vs roster-import card pass

    [Fact]
    public async Task A_personnel_create_holding_the_lock_makes_the_import_card_pass_wait_and_then_warn_instead_of_issuing()
    {
        var world = await ArrangeImportWorldAsync();
        var batchId = await UploadAsync(world.TermId);

        var race = await RaceAsync(
            async barrier =>
            {
                await using var db = NewDbContext();
                return await PersonnelOn(db, world.SchoolId, barrier)
                    .CreateAsync(PersonnelRequest(PersonnelNumber, SyntheticRoster.MariaRfid));
            },
            async () =>
            {
                await using var db = NewDbContext();
                return await ImportOn(db, NoCardUidWriteBarrier.Instance).RunAsync(batchId, world.TermId);
            });

        await AssertExactlyOneClaimAsync(world.SchoolId, SyntheticRoster.MariaRfid);
        Assert.True(race.BWaitedOnLock, "The import's card pass never waited on the card-UID app lock.");
        Assert.Equal(PersonnelWriteOutcome.Saved, race.A.Outcome);
        Assert.NotEqual(SisImportStatus.Failed, race.B.Status);

        // Maria's rows say why she has no card, under their own code (JJ's #4), and record no card touch
        // — RfidCardRevoked's shape.
        await using var read = NewDbContext();
        var rows = await SisImportOn(read).GetRowsAsync(batchId, result: null);
        foreach (var rowNumber in new[] { 2, 3, 4, 5 })
        {
            var row = rows.Single(r => r.RowNumber == rowNumber);
            Assert.Contains(
                $"[{SisImportWarningCode.RfidCardHeldByPersonnel}]", row.WarningMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain(SisImportWarningCode.RfidCardRevoked, row.WarningMessage!, StringComparison.Ordinal);
            Assert.Contains($"personnel record '{PersonnelNumber}'", row.WarningMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain(row.Entities, e => e.EntityType == SisImportEntityType.RfidCard);
        }

        // Everyone else still got a card: the refusal is per UID.
        Assert.Equal(ExpectedImportedStudents - 1,
            await read.RfidCards.IgnoreQueryFilters().CountAsync(c => c.SchoolId == world.SchoolId && c.IsActive));
    }

    [Fact]
    public async Task The_import_card_pass_holding_its_locks_makes_a_personnel_create_wait_and_then_refuses_it()
    {
        var world = await ArrangeImportWorldAsync();
        var batchId = await UploadAsync(world.TermId);

        var race = await RaceAsync(
            async barrier =>
            {
                await using var db = NewDbContext();
                return await ImportOn(db, barrier).RunAsync(batchId, world.TermId);
            },
            async () =>
            {
                await using var db = NewDbContext();
                return await PersonnelOn(db, world.SchoolId, NoCardUidWriteBarrier.Instance)
                    .CreateAsync(PersonnelRequest(PersonnelNumber, SyntheticRoster.MariaRfid));
            });

        await AssertExactlyOneClaimAsync(world.SchoolId, SyntheticRoster.MariaRfid);
        Assert.True(race.BWaitedOnLock, "The personnel create never waited on the import's card-UID app lock.");
        Assert.NotEqual(SisImportStatus.Failed, race.A.Status);
        Assert.Equal(PersonnelWriteOutcome.DuplicateRfid, race.B.Outcome);
        Assert.Contains($"student '{SyntheticRoster.MariaRegNo}'", race.B.Message, StringComparison.Ordinal);
    }

    // ============================================================ the personnel paths the lock now covers

    /// <summary>
    /// The pre-D-98 defect: <c>RfidTakenAsync</c> looked only at other personnel, so a UID on an active
    /// student card was accepted. Through <c>ImportAsync</c>, which runs each row through
    /// <c>CreateAsync</c> — the proof that the XLSX import inherits the lock and the cross-table re-check.
    /// </summary>
    [Fact]
    public async Task A_personnel_import_row_carrying_a_student_held_uid_is_refused_naming_the_student()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        await using (var arrange = NewDbContext())
        {
            arrange.RfidCards.Add(TestData.NewCard(schoolId, studentId, Uid));
            await arrange.SaveChangesAsync();
        }

        await using var db = NewDbContext();
        var result = await PersonnelOn(db, schoolId, NoCardUidWriteBarrier.Instance).ImportAsync(
        [
            PersonnelRequest(PersonnelNumber, "04-a7-b8-c9"),
            PersonnelRequest("P-0002", "11223344"),
        ]);

        Assert.Equal(1, result.Created);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PersonnelNumber, error.PersonnelNumber);
        Assert.Contains($"student '{StudentNumber}'", error.Message, StringComparison.Ordinal);
        await AssertExactlyOneClaimAsync(schoolId, Uid);
    }

    [Fact]
    public async Task A_personnel_update_onto_a_student_held_uid_is_refused_and_leaves_the_record_unchanged()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        await using (var arrange = NewDbContext())
        {
            arrange.RfidCards.Add(TestData.NewCard(schoolId, studentId, Uid));
            await arrange.SaveChangesAsync();
        }

        Guid personnelId;
        await using (var create = NewDbContext())
        {
            var created = await PersonnelOn(create, schoolId, NoCardUidWriteBarrier.Instance)
                .CreateAsync(PersonnelRequest(PersonnelNumber, "11223344"));
            Assert.Equal(PersonnelWriteOutcome.Saved, created.Outcome);
            personnelId = created.Personnel!.Id;
        }

        await using (var db = NewDbContext())
        {
            var updated = await PersonnelOn(db, schoolId, NoCardUidWriteBarrier.Instance)
                .UpdateAsync(personnelId, PersonnelRequest(PersonnelNumber, Uid));
            Assert.Equal(PersonnelWriteOutcome.DuplicateRfid, updated.Outcome);
        }

        await using var read = NewDbContext();
        var stored = await read.Personnel.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == personnelId);
        Assert.Equal("11223344", stored.RfidUid);
        await AssertExactlyOneClaimAsync(schoolId, Uid);
    }

    /// <summary>
    /// JJ's #8. <c>LostIndexRaceAsync</c> asked <c>row.Id == default</c>, which is never true because
    /// <c>Entity</c> assigns <c>NewGuid</c>, so a create that lost the number index was marked
    /// <c>Unchanged</c>: a tracked row with no database row behind it, on a context
    /// <c>ImportAsync</c> goes on saving through. Deterministic through the barrier: the create is parked
    /// after its pre-check while the same number commits, and only then released into the index.
    /// </summary>
    [Fact]
    public async Task A_create_that_loses_the_number_index_race_leaves_nothing_tracked()
    {
        var schoolId = await ArrangeSchoolAsync();
        var barrier = new HoldingBarrier();

        await using var db = NewDbContext();
        var create = Task.Run(() => PersonnelOn(db, schoolId, barrier)
            .CreateAsync(PersonnelRequest(PersonnelNumber, Uid)));

        await Task.WhenAny(barrier.Reached.Task, create).WaitAsync(Patience);
        Assert.True(barrier.Reached.Task.IsCompleted, "The create finished without ever reaching its barrier.");

        await using (var winner = NewDbContext())
        {
            winner.Personnel.Add(TestData.NewPersonnel(schoolId, PersonnelNumber));
            await winner.SaveChangesAsync();
        }

        barrier.Release();
        var response = await create.WaitAsync(Patience);

        Assert.Equal(PersonnelWriteOutcome.DuplicateNumber, response.Outcome);
        Assert.Empty(db.ChangeTracker.Entries<Personnel>());
    }

    // ================================================================ the 409's title (JJ's #7)

    /// <summary>
    /// JJ's #7. The title used to say "another student" whoever held the card, which was false the moment
    /// D-98 let a personnel record hold one. The detail is what names the holder, as built (#6).
    /// </summary>
    [Fact]
    public async Task A_student_held_card_conflict_has_a_title_that_names_no_kind_of_holder()
    {
        var schoolId = await ArrangeSchoolAsync();
        var holderId = await ArrangeStudentAsync(schoolId);
        Guid otherStudentId;
        await using (var arrange = NewDbContext())
        {
            arrange.RfidCards.Add(TestData.NewCard(schoolId, holderId, Uid));
            var other = TestData.NewStudent(schoolId, "2023-0002");
            arrange.Students.Add(other);
            await arrange.SaveChangesAsync();
            otherStudentId = other.Id;
        }

        var body = await RefusedCardIssueAsync(schoolId, otherStudentId);

        Assert.Equal(CardUidInUseTitle, body.GetProperty("title").GetString());
        Assert.Contains($"student '{StudentNumber}'", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_personnel_held_card_conflict_does_not_say_another_student()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        await using (var arrange = NewDbContext())
        {
            arrange.Personnel.Add(TestData.NewPersonnel(schoolId, PersonnelNumber, rfidUid: Uid));
            await arrange.SaveChangesAsync();
        }

        var body = await RefusedCardIssueAsync(schoolId, studentId);

        Assert.Equal(CardUidInUseTitle, body.GetProperty("title").GetString());
        Assert.DoesNotContain("student", body.GetProperty("title").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"personnel record '{PersonnelNumber}'", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    private const string CardUidInUseTitle = "That card UID is already in use.";

    private async Task<JsonElement> RefusedCardIssueAsync(Guid schoolId, Guid studentId)
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/students/{studentId}/cards", new { cardUid = Uid, label = "Replacement" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(nameof(StudentWriteOutcome.CardUidInUse), document.RootElement.GetProperty("code").GetString());
        return document.RootElement.Clone();
    }

    // ======================================================================== sp_getapplock return codes

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    public void Every_negative_applock_return_code_throws_a_typed_exception_naming_the_code(int returnCode)
    {
        var resource = CardUidClaims.LockResource(Guid.NewGuid(), Uid);

        var thrown = Assert.Throws<CardUidLockException>(
            () => CardUidClaims.ThrowIfNotGranted(returnCode, resource));

        Assert.Equal(returnCode, thrown.ReturnCode);
        Assert.Equal(resource, thrown.Resource);
        Assert.Contains($"returned {returnCode} (", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_granted_applock_return_code_does_not_throw(int returnCode) =>
        CardUidClaims.ThrowIfNotGranted(returnCode, CardUidClaims.LockResource(Guid.NewGuid(), Uid));

    [Fact]
    public async Task A_lock_held_elsewhere_surfaces_as_return_code_minus_1_not_as_a_silent_pass()
    {
        var schoolId = Guid.NewGuid();
        var resource = CardUidClaims.LockResource(schoolId, Uid);

        await using var holder = new SqlConnection(Sql.ConnectionString);
        await holder.OpenAsync();
        await using var held = (SqlTransaction)await holder.BeginTransactionAsync();
        Assert.True(await GetAppLockAsync(holder, held, resource) >= 0);

        await using var db = NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var thrown = await Assert.ThrowsAsync<CardUidLockException>(
            () => CardUidClaims.LockAsync(db, schoolId, [Uid], timeoutMilliseconds: 0, CancellationToken.None));

        Assert.Equal(-1, thrown.ReturnCode);
        Assert.Equal(resource, thrown.Resource);
    }

    /// <summary>
    /// JJ's decision 5, end to end on a real route: another session holds the UID's lock for the whole
    /// D-98 wait, so <c>POST /personnel</c> blocks for <see cref="CardUidClaims.LockTimeoutMilliseconds"/>
    /// and is then refused. It is a 503 with <c>Retry-After</c> rather than a 500, the body names neither
    /// the UID nor the school, nothing was written, and the same request succeeds once the lock is free,
    /// which is what makes the advice on the wire true.
    /// </summary>
    [Fact]
    public async Task A_card_uid_lock_refusal_is_a_503_with_retry_after_and_the_same_request_then_succeeds()
    {
        const string Number = "EMP-LOCKED";
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        var request = new
        {
            personnelNumber = Number,
            firstName = "Jose",
            lastName = "Rizal",
            email = "jose@usa.edu.ph",
            classification = "ACAD",
            department = "CICT",
            rfidUid = Uid,
            status = PersonnelStatus.Active,
        };

        await using var holder = new SqlConnection(Sql.ConnectionString);
        await holder.OpenAsync();
        await using var held = (SqlTransaction)await holder.BeginTransactionAsync();
        Assert.True(await GetAppLockAsync(holder, held, CardUidClaims.LockResource(schoolId, Uid)) >= 0);

        var refused = await client.PostAsJsonAsync("/api/v1/personnel", request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(
            TimeSpan.FromSeconds(WriteLockUnavailableException.RetryAfterSeconds),
            refused.Headers.RetryAfter?.Delta);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);

        var text = await refused.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(text))
        {
            Assert.Equal(503, body.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(WriteLockUnavailableException.Code, body.RootElement.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
        }

        Assert.DoesNotContain(Uid, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(schoolId.ToString("N"), text, StringComparison.OrdinalIgnoreCase);

        await using (var read = NewDbContext())
            Assert.False(await read.Personnel.AnyAsync(p => p.PersonnelNumber == Number));

        await held.RollbackAsync();

        var retried = await client.PostAsJsonAsync("/api/v1/personnel", request);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }

    /// <summary>
    /// -3 is the code D-98 singles out: a deadlock victim arrives as a <em>return value</em>, not as
    /// error 1205, so nothing but this check would ever notice it. Two sessions each hold one lock and
    /// ask for the other's; the EF session is made the victim with <c>DEADLOCK_PRIORITY LOW</c>.
    /// </summary>
    [Fact]
    public async Task A_deadlock_victim_surfaces_as_return_code_minus_3()
    {
        var schoolId = Guid.NewGuid();
        const string First = "AAAA0001";
        const string Second = "BBBB0002";

        await using var other = new SqlConnection(Sql.ConnectionString);
        await other.OpenAsync();
        await using var otherTx = (SqlTransaction)await other.BeginTransactionAsync();
        Assert.True(await GetAppLockAsync(other, otherTx, CardUidClaims.LockResource(schoolId, First)) >= 0);

        await using var db = NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY LOW;");
        await CardUidClaims.LockAsync(db, schoolId, [Second], CancellationToken.None);

        // The other session now waits on Second, which the EF session holds...
        var otherWaits = GetAppLockAsync(other, otherTx, CardUidClaims.LockResource(schoolId, Second), timeoutMs: 30_000);
        Assert.True(await WaitUntilSomeoneWaitsOnAnAppLockAsync(otherWaits),
            "The other session never waited on the second lock, so no deadlock could form.");

        // ...and the EF session asks for First, which the other holds: a cycle.
        var thrown = await Assert.ThrowsAsync<CardUidLockException>(
            () => CardUidClaims.LockAsync(db, schoolId, [First], timeoutMilliseconds: 30_000, CancellationToken.None));

        Assert.Equal(-3, thrown.ReturnCode);
        await transaction.RollbackAsync();
        Assert.True(await otherWaits >= 0, "The surviving session should be granted once the victim releases.");
    }

    // ======================================================================== the import unit's tracker

    /// <summary>
    /// D-98: the card unit's save must contain only its own card inserts. A barrier that slips a stray
    /// entity into the shared change tracker, inside the unit, must stop the run loudly — and nothing,
    /// neither the cards nor the stray, may be committed.
    /// </summary>
    [Fact]
    public async Task The_import_card_unit_refuses_to_save_when_the_tracker_holds_anything_but_its_own_card_inserts()
    {
        var world = await ArrangeImportWorldAsync();
        var batchId = await UploadAsync(world.TermId);
        const string StrayCode = "STRAY";

        await using (var db = NewDbContext())
        {
            var stray = new StrayEntityBarrier(() => db.Schools.Add(TestData.NewSchool(StrayCode)));

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ImportOn(db, stray).RunAsync(batchId, world.TermId));

            Assert.True(stray.Fired, "The barrier never ran, so the assertion was not exercised.");
            Assert.Contains("not its own card inserts", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("School (Added)", thrown.Message, StringComparison.Ordinal);
        }

        await using var read = NewDbContext();
        Assert.False(await read.Schools.AnyAsync(s => s.Code == StrayCode));
        Assert.False(await read.RfidCards.IgnoreQueryFilters().AnyAsync(c => c.SchoolId == world.SchoolId));
        Assert.Equal(SisImportStatus.Failed,
            (await read.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == batchId)).Status);
    }

    // ================================================================================ a refused roster run

    /// <summary>
    /// The roster's half of JJ's decision 5, end to end. Another session holds one UID the card pass needs
    /// for the whole D-98 wait, so the run is refused as a 503, not a 500, and stops at <c>Failed</c> with no
    /// card written. That is what makes "running it again is safe and converges" true: the same run, once
    /// the lock is free, issues every card.
    /// </summary>
    [Fact]
    public async Task A_roster_run_refused_the_card_lock_is_a_503_and_the_same_run_then_succeeds()
    {
        var world = await ArrangeImportWorldAsync();
        var batchId = await UploadAsync(world.TermId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);
        var route = $"/api/v1/sis/import/{batchId}/run";
        var request = new { termId = world.TermId };

        await using var holder = new SqlConnection(Sql.ConnectionString);
        await holder.OpenAsync();
        await using var held = (SqlTransaction)await holder.BeginTransactionAsync();
        Assert.True(await GetAppLockAsync(
            holder, held, CardUidClaims.LockResource(world.SchoolId, SyntheticRoster.MariaRfid)) >= 0);

        var refused = await client.PostAsJsonAsync(route, request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(
            TimeSpan.FromSeconds(WriteLockUnavailableException.RetryAfterSeconds),
            refused.Headers.RetryAfter?.Delta);

        var text = await refused.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(text))
            Assert.Equal(WriteLockUnavailableException.Code, body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(SyntheticRoster.MariaRfid, text, StringComparison.Ordinal);

        await using (var read = NewDbContext())
        {
            Assert.Equal(SisImportStatus.Failed,
                (await read.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == batchId)).Status);
            Assert.False(await read.RfidCards.IgnoreQueryFilters().AnyAsync(c => c.SchoolId == world.SchoolId));
        }

        await held.RollbackAsync();

        var retried = await client.PostAsJsonAsync(route, request);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        await using var after = NewDbContext();
        Assert.NotEqual(SisImportStatus.Failed,
            (await after.SisImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == batchId)).Status);
        Assert.Equal(ExpectedImportedStudents,
            await after.RfidCards.IgnoreQueryFilters().CountAsync(c => c.SchoolId == world.SchoolId && c.IsActive));
    }

    // ==================================================================== D-98's replay, on a retrying context

    // A transient fault replays the whole unit: lock, re-check, write. Every test below runs on the context
    // production registers (retry strategy, compatibility level 110) and takes one fault at the unit's commit.

    private const string OtherUid = "0A0B0C0D";

    /// <summary>A replay writes once, and it writes at all: <c>AcceptAllChanges</c> waits for the commit.</summary>
    [Fact]
    public async Task A_personnel_create_replayed_after_a_transient_fault_writes_once_on_the_2012_dialect()
    {
        var schoolId = await ArrangeSchoolAsync();
        var (db, probe, fault) = FaultingContext();

        await using (db)
        {
            var response = await PersonnelOn(db, schoolId, NoCardUidWriteBarrier.Instance)
                .CreateAsync(PersonnelRequest(PersonnelNumber, Uid));

            Assert.Equal(PersonnelWriteOutcome.Saved, response.Outcome);
        }

        AssertReplayed(probe, fault);
        await AssertExactlyOneClaimAsync(schoolId, Uid);
    }

    /// <summary>
    /// The replay's re-check is a real decision: the UID was claimed between the attempts. The refused row
    /// must not survive into the context, or the next save writes it. <see cref="PersonnelService.ImportAsync"/>
    /// is exactly that next save.
    /// </summary>
    [Fact]
    public async Task A_personnel_create_whose_uid_is_claimed_before_the_replay_is_refused_and_rides_along_on_nothing()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        var (db, probe, fault) = FaultingContext(() => ClaimOnStudentCardAsync(schoolId, studentId, Uid));

        await using (db)
        {
            var personnel = PersonnelOn(db, schoolId, NoCardUidWriteBarrier.Instance);

            var refused = await personnel.CreateAsync(PersonnelRequest(PersonnelNumber, Uid));
            Assert.Equal(PersonnelWriteOutcome.DuplicateRfid, refused.Outcome);
            AssertReplayed(probe, fault);

            var next = await personnel.CreateAsync(PersonnelRequest("P-0002", OtherUid));
            Assert.Equal(PersonnelWriteOutcome.Saved, next.Outcome);
        }

        await AssertExactlyOneClaimAsync(schoolId, Uid);

        await using var read = NewDbContext();
        Assert.False(await read.Personnel.AnyAsync(p => p.PersonnelNumber == PersonnelNumber));
    }

    /// <summary>The update's half: attempt one changed a tracked row, so the refusal has to put it back.</summary>
    [Fact]
    public async Task A_personnel_update_whose_uid_is_claimed_before_the_replay_is_refused_and_keeps_the_stored_record()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        Guid personnelId;
        await using (var arrange = NewDbContext())
        {
            var person = TestData.NewPersonnel(schoolId, PersonnelNumber, rfidUid: OtherUid);
            arrange.Personnel.Add(person);
            await arrange.SaveChangesAsync();
            personnelId = person.Id;
        }

        var (db, probe, fault) = FaultingContext(() => ClaimOnStudentCardAsync(schoolId, studentId, Uid));

        await using (db)
        {
            var response = await PersonnelOn(db, schoolId, NoCardUidWriteBarrier.Instance)
                .UpdateAsync(personnelId, PersonnelRequest(PersonnelNumber, Uid));
            Assert.Equal(PersonnelWriteOutcome.DuplicateRfid, response.Outcome);

            await db.SaveChangesAsync();
        }

        AssertReplayed(probe, fault);
        await AssertExactlyOneClaimAsync(schoolId, Uid);

        await using var read = NewDbContext();
        Assert.Equal(OtherUid, (await read.Personnel.SingleAsync(p => p.Id == personnelId)).RfidUid);
    }

    [Fact]
    public async Task A_student_card_issue_replayed_after_a_transient_fault_writes_once_on_the_2012_dialect()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        var (db, probe, fault) = FaultingContext();

        await using (db)
        {
            var response = await StudentsOn(db, NoCardUidWriteBarrier.Instance)
                .AddCardAsync(studentId, new StudentCardRequest(Uid, null));

            Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
        }

        AssertReplayed(probe, fault);
        await AssertExactlyOneClaimAsync(schoolId, Uid);
    }

    /// <summary>The student writer's refusal: an earlier attempt's <c>Add</c> must not survive it.</summary>
    [Fact]
    public async Task A_student_card_issue_whose_uid_is_claimed_before_the_replay_is_refused_and_rides_along_on_nothing()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        var (db, probe, fault) = FaultingContext(() => ClaimOnPersonnelAsync(schoolId, Uid));

        await using (db)
        {
            var response = await StudentsOn(db, NoCardUidWriteBarrier.Instance)
                .AddCardAsync(studentId, new StudentCardRequest(Uid, null));
            Assert.Equal(StudentWriteOutcome.CardUidInUse, response.Outcome);

            await db.SaveChangesAsync();
        }

        AssertReplayed(probe, fault);
        await AssertExactlyOneClaimAsync(schoolId, Uid);
    }

    /// <summary>
    /// D-98's "a transient fault replays the whole unit", for the import, whose save holds many cards. Many
    /// UIDs is also what shows the 2012 translation: a list of one becomes a plain equality, so the literal
    /// <c>IN</c> can only be asserted here.
    /// </summary>
    [Fact]
    public async Task The_import_card_unit_replayed_after_a_transient_fault_issues_each_card_once_through_a_literal_IN()
    {
        var world = await ArrangeImportWorldAsync();
        var batchId = await UploadAsync(world.TermId);
        var (db, probe, fault) = FaultingContext();

        await using (db)
        {
            var batch = await ImportOn(db, NoCardUidWriteBarrier.Instance).RunAsync(batchId, world.TermId);
            Assert.NotEqual(SisImportStatus.Failed, batch.Status);
        }

        AssertReplayed(probe, fault);
        Assert.Contains(probe.Commands, sql =>
            sql.Contains("[CardUid] IN (", StringComparison.Ordinal)
            && sql.Contains(SyntheticRoster.MariaRfid, StringComparison.Ordinal));

        await using var read = NewDbContext();
        Assert.Equal(ExpectedImportedStudents,
            await read.RfidCards.IgnoreQueryFilters().CountAsync(c => c.SchoolId == world.SchoolId && c.IsActive));
    }

    /// <summary>
    /// The replay's re-check narrows the unit: the card it had already added in attempt one is detached, and
    /// every other card is still issued, against the same entities.
    /// </summary>
    [Fact]
    public async Task The_import_card_unit_replay_detaches_a_card_claimed_between_attempts_and_issues_the_rest()
    {
        var world = await ArrangeImportWorldAsync();
        var batchId = await UploadAsync(world.TermId);
        var (db, probe, fault) = FaultingContext(() => ClaimOnPersonnelAsync(world.SchoolId, SyntheticRoster.MariaRfid));

        await using (db)
        {
            var batch = await ImportOn(db, NoCardUidWriteBarrier.Instance).RunAsync(batchId, world.TermId);
            Assert.NotEqual(SisImportStatus.Failed, batch.Status);
        }

        AssertReplayed(probe, fault);
        await AssertExactlyOneClaimAsync(world.SchoolId, SyntheticRoster.MariaRfid);

        await using var read = NewDbContext();
        Assert.Equal(ExpectedImportedStudents - 1,
            await read.RfidCards.IgnoreQueryFilters().CountAsync(c => c.SchoolId == world.SchoolId && c.IsActive));

        var rows = await SisImportOn(read).GetRowsAsync(batchId, result: null);
        Assert.Contains(
            $"[{SisImportWarningCode.RfidCardHeldByPersonnel}]",
            rows.Single(r => r.RowNumber == 2).WarningMessage!, StringComparison.Ordinal);
    }

    private (EamsDbContext Db, UnitProbe Probe, FaultAtUnitCommit Fault) FaultingContext(Func<Task>? betweenAttempts = null)
    {
        var probe = new UnitProbe();
        var fault = new FaultAtUnitCommit(probe, betweenAttempts ?? (() => Task.CompletedTask));
        return (Sql.NewProductionShapedDbContext(School, probe, fault), probe, fault);
    }

    /// <summary>
    /// The fault landed, the unit took its locks again, and neither attempt spoke a dialect the deployed
    /// server does not have.
    /// </summary>
    private static void AssertReplayed(UnitProbe probe, FaultAtUnitCommit fault)
    {
        Assert.True(fault.Fired, "No fault was raised, so nothing here was a replay.");
        Assert.Equal(2, probe.LockCommands);
        Assert.DoesNotContain(probe.Commands, sql => sql.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A rival that owes the lock nothing: the point is what the replay's re-check then finds.</summary>
    private async Task ClaimOnStudentCardAsync(Guid schoolId, Guid studentId, string uid)
    {
        await using var db = NewDbContext();
        db.RfidCards.Add(TestData.NewCard(schoolId, studentId, uid));
        await db.SaveChangesAsync();
    }

    /// <inheritdoc cref="ClaimOnStudentCardAsync"/>
    private async Task ClaimOnPersonnelAsync(Guid schoolId, string uid)
    {
        await using var db = NewDbContext();
        db.Personnel.Add(TestData.NewPersonnel(schoolId, "P-RIVAL", rfidUid: uid));
        await db.SaveChangesAsync();
    }

    /// <summary>Records one context's commands, and how many of them took card-UID locks.</summary>
    private sealed class UnitProbe : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public int LockCommands { get; private set; }

        private void Record(DbCommand command)
        {
            Commands.Add(command.CommandText);
            if (command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal)) LockCommands++;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Raises one transient fault at the first commit after a card-UID lock: the attempt has saved, nothing
    /// is durable, and <see cref="TimeoutException"/> is what the SQL Server strategy retries. Anything
    /// <paramref name="betweenAttempts"/> writes is committed by the time the replay re-checks.
    /// </summary>
    private sealed class FaultAtUnitCommit(UnitProbe probe, Func<Task> betweenAttempts) : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Fired || probe.LockCommands == 0) return result;

            Fired = true;
            await betweenAttempts();
            throw new TimeoutException("Injected: a transient fault at the card unit's commit.");
        }
    }

    // =================================================================================== plumbing

    /// <summary>Students the synthetic roster imports — one card each.</summary>
    private const int ExpectedImportedStudents = 8;

    private sealed record Race<TA, TB>(TA A, TB B, bool BWaitedOnLock);

    /// <summary>
    /// Runs writer A until it parks at its barrier — between its re-check and its write, holding the card
    /// lock — then starts writer B, waits until B is seen waiting on an app lock (or finishes, which is
    /// what it does with the lock removed), and only then lets A go.
    /// </summary>
    private async Task<Race<TA, TB>> RaceAsync<TA, TB>(
        Func<ICardUidWriteBarrier, Task<TA>> writerA, Func<Task<TB>> writerB)
    {
        var barrier = new HoldingBarrier();
        var a = Task.Run(() => writerA(barrier));

        var first = await Task.WhenAny(barrier.Reached.Task, a).WaitAsync(Patience);
        if (first == a)
        {
            await a; // surfaces A's exception if it threw before reaching the barrier
            throw new InvalidOperationException("Writer A finished without ever reaching its barrier.");
        }

        var b = Task.Run(writerB);
        var waited = await WaitUntilSomeoneWaitsOnAnAppLockAsync(b);

        barrier.Release();
        return new Race<TA, TB>(await a.WaitAsync(Patience), await b.WaitAsync(Patience), waited);
    }

    /// <summary>
    /// Polls <c>sys.dm_tran_locks</c> for an APPLICATION lock request in WAIT on this database. True once
    /// one is seen; false if <paramref name="until"/> finishes first — i.e. it never had to wait.
    /// </summary>
    private async Task<bool> WaitUntilSomeoneWaitsOnAnAppLockAsync(Task until)
    {
        await using var probe = new SqlConnection(Sql.ConnectionString);
        await probe.OpenAsync();

        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = N'APPLICATION' " +
                "AND request_status = N'WAIT' AND resource_database_id = DB_ID();", probe);
            if ((int)(await command.ExecuteScalarAsync())! > 0) return true;
            if (until.IsCompleted) return false;
            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<int> GetAppLockAsync(
        SqlConnection connection, SqlTransaction transaction, string resource, int timeoutMs = 0)
    {
        await using var command = new SqlCommand(
            "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @res, @LockMode = N'Exclusive', " +
            "@LockOwner = N'Transaction', @LockTimeout = @timeout; SELECT @r;", connection, transaction)
        {
            CommandTimeout = 60,
        };
        command.Parameters.AddWithValue("@res", resource);
        command.Parameters.AddWithValue("@timeout", timeoutMs);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Fails with a message that says what the negative control looks like: both tables claiming the UID.
    /// </summary>
    private async Task AssertExactlyOneClaimAsync(Guid schoolId, string uid)
    {
        await using var db = NewDbContext();
        var cards = await db.RfidCards.IgnoreQueryFilters()
            .CountAsync(c => c.SchoolId == schoolId && c.IsActive && c.CardUid == uid);
        var people = await db.Personnel.IgnoreQueryFilters()
            .CountAsync(p => p.SchoolId == schoolId && !p.IsDeleted && p.RfidUid == uid);

        Assert.True(cards + people == 1,
            $"Card UID '{uid}' must be claimed exactly once across RfidCards and Personnel (ADR-015 D-98), " +
            $"but {cards} active student card(s) and {people} personnel record(s) hold it.");
    }

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> ArrangeStudentAsync(Guid schoolId)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, StudentNumber);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private sealed record ImportWorld(Guid SchoolId, Guid TermId);

    /// <summary>The world <c>SisImportPipelineTests</c> imports the synthetic roster into.</summary>
    private async Task<ImportWorld> ArrangeImportWorldAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        db.Classifications.AddRange(TestData.ClassificationVocabulary(school.Id));
        await db.SaveChangesAsync();
        await CourseMappingProfile.SeedCourseMappingProfileAsync(db, school.Id);
        return new ImportWorld(school.Id, term.Id);
    }

    private async Task<Guid> UploadAsync(Guid termId)
    {
        await using var content = SyntheticRoster.Build();
        await using var db = NewDbContext();
        var preview = await SisImportOn(db).UploadAsync(
            new SisImportUploadRequest(content, "card-uid-race.xlsx", termId));
        return preview.Batch.Id;
    }

    private static PersonnelWriteRequest PersonnelRequest(string number, string? rfidUid) =>
        new(number, "Jose", null, "Rizal", null, null, null, null, null, rfidUid, null);

    private static IPersonnelService PersonnelOn(EamsDbContext db, Guid schoolId, ICardUidWriteBarrier barrier) =>
        new PersonnelService(db, new TestSchoolContext { CurrentSchoolId = schoolId }, barrier,
            NullLogger<PersonnelService>.Instance);

    private IStudentService StudentsOn(EamsDbContext db, ICardUidWriteBarrier barrier) =>
        new StudentService(db, School, barrier, NullLogger<StudentService>.Instance);

    private ISisImportService ImportOn(EamsDbContext db, ICardUidWriteBarrier barrier) =>
        new SisImportService(db, new StudentGroupProjection(db), CurrentUser, barrier);

    /// <summary>Parks the writer at the seam until <see cref="Release"/>.</summary>
    private sealed class HoldingBarrier : ICardUidWriteBarrier
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task BetweenCheckAndWriteAsync(CancellationToken ct)
        {
            Reached.TrySetResult();
            await _release.Task.WaitAsync(Patience, ct);
        }
    }

    /// <summary>Runs one action at the seam — here, slipping a stray entity into the tracker.</summary>
    private sealed class StrayEntityBarrier(Action atSeam) : ICardUidWriteBarrier
    {
        public bool Fired { get; private set; }

        public Task BetweenCheckAndWriteAsync(CancellationToken ct)
        {
            Fired = true;
            atSeam();
            return Task.CompletedTask;
        }
    }
}
