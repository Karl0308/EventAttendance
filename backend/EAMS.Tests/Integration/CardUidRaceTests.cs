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

        // Maria's rows say why she has no card, and record no card touch — RfidCardRevoked's shape.
        await using var read = NewDbContext();
        var rows = await SisImportOn(read).GetRowsAsync(batchId, result: null);
        foreach (var rowNumber in new[] { 2, 3, 4, 5 })
        {
            var row = rows.Single(r => r.RowNumber == rowNumber);
            Assert.Contains($"[{SisImportWarningCode.RfidCardRevoked}]", row.WarningMessage!, StringComparison.Ordinal);
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
