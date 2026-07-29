using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>AttendanceRecords.SchoolId</c> and the re-scoped idempotency index (Phase 4a design, D-35).
///
/// <para>
/// <b>The defect this closes, stated as the thing that actually breaks.</b>
/// <c>UX_Attendance_Device_DeviceTapId</c> was global while the §11 query filter on this table was
/// per-tenant, so <c>AttendanceService.FindByDeviceTapAsync</c> and the constraint selected different
/// row sets as soon as a second school existed. A tap whose <c>(DeviceId, DeviceTapId)</c> collided
/// with another tenant's row missed the pre-check, raised 2601 on insert, failed the re-read through
/// the same filter, and reached the deliberate throw in <c>ResolveLostInsertRaceAsync</c> — a 500 that
/// §8.2's offline queue retries straight back into the same collision. Permanent, per-tap, and
/// inescapable from the client.
/// </para>
///
/// <para>
/// <b>Why it had to land now rather than with the batch endpoint.</b> It is benign at one school, and
/// it stops being benign the moment a queue drains: a batch flush is precisely where a device replays
/// many keys at once, and a permanently-500ing row inside a batch stalls the whole queue behind it.
/// Closing it before <c>POST /attendance/tap/batch</c> exists means the batch endpoint is built on a
/// constraint that agrees with its own lookup.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceTenancyTests : IntegrationTest
{
    public AttendanceTenancyTests(SqlServerFixture sql) : base(sql) { }

    private const string TapIdIndex = "UX_Attendance_Device_DeviceTapId";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string PreviousMigration = "EventAudienceUniqueness";

    // ------------------------------------------------------------------- the index, as built

    /// <summary>
    /// The index metadata itself, in column order. Behaviour alone would not catch a right-looking
    /// index built on the wrong columns — the same reason <c>SchemaConstraintTests</c> asserts on
    /// <c>sys.indexes</c> beside every behavioural test.
    /// </summary>
    [Fact]
    public async Task The_tap_id_index_is_scoped_by_school_first()
    {
        var columns = await IndexColumnsAsync(Sql.ConnectionString, TapIdIndex);

        Assert.Equal(new[] { "SchoolId", "DeviceId", "DeviceTapId" }, columns);
    }

    /// <summary>
    /// The filter survived the swap. A unique index over a nullable column picks up the SQL Server
    /// provider's automatic <c>IS NOT NULL</c> unless one is stated, and here the correct filter and
    /// the accidental one happen to look alike — which is exactly the case worth asserting, because a
    /// missing filter would collapse every tap-id-less row into one shared bucket.
    /// </summary>
    [Fact]
    public async Task The_tap_id_index_is_still_filtered_to_rows_that_have_a_tap_id()
    {
        var filter = await IndexFilterAsync(Sql.ConnectionString, TapIdIndex);

        Assert.NotNull(filter);
        Assert.Contains("DeviceTapId", filter);
        Assert.DoesNotContain("SchoolId", filter);
    }

    /// <summary>
    /// The foreign-key index that <c>DeviceId</c> used to get for free by leading the composite. It is
    /// the same trap <c>IX_StudentGroups_SchoolId</c> and <c>IX_EventGroups_EventId</c> record, sprung
    /// this time by a column being <em>prepended</em> to an existing index rather than by a new index
    /// arriving.
    /// </summary>
    [Fact]
    public async Task The_device_foreign_key_keeps_an_index_of_its_own()
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_AttendanceRecords_DeviceId';", connection);

        Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
    }

    // ------------------------------------------------------- the behaviour the scoping buys

    /// <summary>
    /// <b>The defect, reproduced and closed.</b> Two schools, two devices, the same
    /// <c>deviceTapId</c> string. Under the old global index the second insert was a duplicate-key
    /// violation; under the tenant-scoped one both rows exist and each device's replay resolves to its
    /// own.
    ///
    /// <para>
    /// The tenant is pinned on each half, so the query filter is live — which is the condition the
    /// whole defect depended on. With no tenant pinned the pre-check would see both rows and the test
    /// would prove nothing about the divergence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_schools_may_use_the_same_device_tap_id()
    {
        var usa = await ArrangeTenantAsync("USA", "USA0001");
        var cicss = await ArrangeTenantAsync("CICSS", "CIC0001");

        const string sharedTapId = "queued-0001";

        foreach (var tenant in new[] { usa, cicss })
        {
            var pinned = new TestSchoolContext { CurrentSchoolId = tenant.SchoolId };
            Device.DeviceId = tenant.DeviceId;

            await using var db = NewDbContext(pinned);
            var response = await AttendanceOn(db).TapAsync(
                new TapRequest(tenant.EventId, tenant.CardUid, tenant.DeviceId, sharedTapId, TestData.Now));

            Assert.Equal(TapOutcome.Recorded, response.Outcome);
        }

        await using var read = NewDbContext(new TestSchoolContext());
        var rows = await read.AttendanceRecords.AsNoTracking()
            .Where(a => a.DeviceTapId == sharedTapId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new[] { usa.SchoolId, cicss.SchoolId }.Order().ToList(),
            rows.Select(r => r.SchoolId).Order().ToList());
    }

    /// <summary>
    /// The control for the test above, and the reason it is not merely "the constraint got weaker":
    /// <b>within one school the same pair is still a replay</b>, which is the property §8.2's whole
    /// offline design rests on. Widening the key must not have widened it past the point where
    /// idempotency stops working.
    /// </summary>
    [Fact]
    public async Task Within_one_school_the_same_device_tap_id_is_still_a_replay()
    {
        var usa = await ArrangeTenantAsync("USA", "USA0001");
        var pinned = new TestSchoolContext { CurrentSchoolId = usa.SchoolId };

        Device.DeviceId = usa.DeviceId;

        await using (var db = NewDbContext(pinned))
        {
            var first = await AttendanceOn(db).TapAsync(
                new TapRequest(usa.EventId, usa.CardUid, usa.DeviceId, "queued-0001", TestData.Now));
            Assert.Equal(TapOutcome.Recorded, first.Outcome);
        }

        await using (var db = NewDbContext(pinned))
        {
            var replay = await AttendanceOn(db).TapAsync(
                new TapRequest(usa.EventId, usa.CardUid, usa.DeviceId, "queued-0001", TestData.Now));
            Assert.Equal(TapOutcome.DuplicateIgnored, replay.Outcome);
        }

        await using var read = NewDbContext(new TestSchoolContext());
        Assert.Equal(1, await read.AttendanceRecords.CountAsync());
    }

    /// <summary>
    /// The two write paths this test drives — the tap and the organizer override — stamp the tenant,
    /// and it always equals the event's. Nothing derives it any other way, and a row where the two
    /// disagree would put the query filter and the unique index back out of step.
    ///
    /// <para>
    /// <b>The third writer, the close-time freeze, is not exercised here</b> and is covered
    /// incidentally rather than deliberately: <c>EventCloseFreezeTests</c> materializes <c>Absent</c>
    /// rows through <c>StageAbsenteesAsync</c>, and the <c>FK_AttendanceRecords_Schools_SchoolId</c>
    /// foreign key means an unstamped one fails the save outright. That is real coverage but it is a
    /// side effect of the constraint, not an assertion about the column — a freeze that stamped the
    /// <em>wrong</em> school would satisfy the FK and pass. Worth its own case if the freeze ever stops
    /// taking the school from the event it just loaded.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_write_path_stamps_the_event_s_school()
    {
        var usa = await ArrangeTenantAsync("USA", "USA0001");
        var pinned = new TestSchoolContext { CurrentSchoolId = usa.SchoolId };

        await using (var db = NewDbContext(pinned))
        {
            await AttendanceOn(db).TapAsync(
                new TapRequest(usa.EventId, usa.CardUid, null, "queued-0001", TestData.Now));
        }

        await using (var db = NewDbContext(pinned))
        {
            var manual = await AttendanceOn(db).ManualAsync(
                usa.EventId, usa.SecondStudentId, AttendanceStatus.Excused, "Medical");
            Assert.Equal(ManualOutcome.Saved, manual.Outcome);
        }

        await using var read = NewDbContext(new TestSchoolContext());
        var rows = await read.AttendanceRecords.AsNoTracking().ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(usa.SchoolId, row.SchoolId));
    }

    // ------------------------------------------------------------------------- the backfill

    /// <summary>
    /// The migration applied to a database that already holds attendance rows written before the
    /// column existed. That is the only population the backfill has to survive, and it is the one no
    /// create-from-scratch run exercises.
    ///
    /// <para>
    /// Two rows on two events belonging to two different schools, deliberately: a backfill that
    /// stamped a constant, or one that took the first school it found, would pass against a
    /// single-tenant fixture and be exactly the bug this migration exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_migration_backfills_every_existing_row_from_its_event()
    {
        var databaseName = $"EAMS_Tenancy_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            // Written the way the pre-D-35 application wrote them: raw SQL, because the EF model always
            // describes the *head* schema and would insist on a column this database does not have yet.
            // AcademicLayerMigrationTests records the same constraint and the same reason.
            await ExecuteAsync(connectionString, PreTenancyPopulation);

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            await using var read = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());

            var rows = await read.AttendanceRecords.AsNoTracking()
                .Include(a => a.Event)
                .ToListAsync();

            Assert.Equal(3, rows.Count);
            Assert.All(rows, row => Assert.Equal(row.Event!.SchoolId, row.SchoolId));

            // And they did not all land in one school, which is what makes the assertion above mean
            // "derived from the event" rather than "stamped with a constant".
            Assert.Equal(2, rows.Select(r => r.SchoolId).Distinct().Count());
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// The other half of the migration: it must apply to a database whose existing rows share a
    /// <c>(DeviceId, DeviceTapId)</c> pair with nothing, and must widen the key without rejecting
    /// anything. Widening a unique key can never fail on existing data — any pair the new index would
    /// reject was already rejected by the old one — and this is the test that says so out loud rather
    /// than leaving it as an argument in a comment.
    /// </summary>
    [Fact]
    public async Task The_index_swap_applies_to_a_database_that_already_has_tap_ids()
    {
        var databaseName = $"EAMS_IndexSwap_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            await ExecuteAsync(connectionString, PreTenancyPopulation);

            await using (var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext()))
                await db.Database.MigrateAsync();

            Assert.Equal(
                new[] { "SchoolId", "DeviceId", "DeviceTapId" },
                await IndexColumnsAsync(connectionString, TapIdIndex));

            // The rows that carried tap ids are all still there. An index swap loses no data, and this
            // is the assertion that distinguishes "swapped" from "rebuilt over a truncated table".
            await using var read = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
            Assert.Equal(2, await read.AttendanceRecords.CountAsync(a => a.DeviceTapId != null));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    // ---------------------------------------------------------------------------- fixtures

    private sealed record Tenant(
        Guid SchoolId, Guid EventId, Guid DeviceId, string CardUid, Guid SecondStudentId);

    private async Task<Tenant> ArrangeTenantAsync(string code, string cardUid)
    {
        Guid schoolId, eventId, secondStudentId;

        await using (var db = NewDbContext(new TestSchoolContext()))
        {
            var school = TestData.NewSchool(code);
            db.Schools.Add(school);

            var student = TestData.NewStudent(school.Id, $"{code}-0001");
            db.Students.Add(student);
            db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, cardUid));

            var second = TestData.NewStudent(school.Id, $"{code}-0002", lastName: "Flores");
            db.Students.Add(second);

            var ev = TestData.NewEvent(school.Id);
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            schoolId = school.Id;
            eventId = ev.Id;
            secondStudentId = second.Id;
        }

        await IssueDeviceKeyAsync(schoolId, $"{code} Kiosk");

        await using var read = NewDbContext(new TestSchoolContext());
        var deviceId = await read.Devices.AsNoTracking()
            .Where(d => d.SchoolId == schoolId).Select(d => d.Id).SingleAsync();

        return new Tenant(schoolId, eventId, deviceId, cardUid, secondStudentId);
    }

    /// <summary>
    /// Attendance rows as the pre-D-35 schema held them: no <c>SchoolId</c> column, two schools, and
    /// two rows carrying tap ids so the index swap has something to widen over.
    /// </summary>
    private const string PreTenancyPopulation = """
        DECLARE @usa    UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111111';
        DECLARE @cicss  UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111112';
        DECLARE @s1     UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222221';
        DECLARE @s2     UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222222';
        DECLARE @s3     UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222223';
        DECLARE @e1     UNIQUEIDENTIFIER = '33333333-3333-3333-3333-333333333331';
        DECLARE @e2     UNIQUEIDENTIFIER = '33333333-3333-3333-3333-333333333332';
        DECLARE @now    DATETIME2 = SYSUTCDATETIME();

        INSERT INTO Schools (Id, Name, Code, TimeZone, IsActive, CreatedAt, UpdatedAt)
        VALUES (@usa,   N'University of San Agustin', N'USA',   N'Asia/Manila', 1, @now, @now),
               (@cicss, N'CICSS',                     N'CICSS', N'Asia/Manila', 1, @now, @now);

        INSERT INTO Students
            (Id, SchoolId, StudentNumber, FirstName, LastName, Status, IsDeleted, CreatedAt, UpdatedAt)
        VALUES (@s1, @usa,   N'2023-0001', N'Maria',  N'Santos', N'Active', 0, @now, @now),
               (@s2, @usa,   N'2023-0002', N'Juan',   N'Cruz',   N'Active', 0, @now, @now),
               (@s3, @cicss, N'2023-0003', N'Andrea', N'Lim',    N'Active', 0, @now, @now);

        INSERT INTO Events
            (Id, SchoolId, Name, StartAt, EndAt, AttendanceMode, GraceMinutes,
             RequireRegistration, Status, IsDeleted, CreatedAt, UpdatedAt)
        VALUES (@e1, @usa,   N'Convocation', @now, DATEADD(HOUR, 2, @now), N'Single', 15, 0, N'Open', 0, @now, @now),
               (@e2, @cicss, N'Orientation', @now, DATEADD(HOUR, 2, @now), N'Single', 15, 0, N'Open', 0, @now, @now);

        INSERT INTO AttendanceRecords
            (Id, EventId, StudentId, CheckInAt, Status, CaptureMethod, DeviceTapId, CreatedAt, UpdatedAt)
        VALUES (NEWID(), @e1, @s1, @now, N'Present', N'Rfid', N'queued-0001', @now, @now),
               (NEWID(), @e1, @s2, @now, N'Late',    N'Rfid', N'queued-0002', @now, @now),
               (NEWID(), @e2, @s3, @now, N'Present', N'Rfid', NULL,           @now, @now);
        """;

    // ----------------------------------------------------------------------------- helpers

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> IndexColumnsAsync(
        string connectionString, string indexName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT c.name
            FROM   sys.indexes       AS i
            JOIN   sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN   sys.columns       AS c  ON c.object_id  = ic.object_id AND c.column_id = ic.column_id
            WHERE  i.name = @name AND ic.is_included_column = 0
            ORDER  BY ic.key_ordinal;
            """, connection);
        command.Parameters.AddWithValue("@name", indexName);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));

        Assert.NotEmpty(columns); // empty means the index itself is missing
        return columns;
    }

    private static async Task<string?> IndexFilterAsync(string connectionString, string indexName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT filter_definition FROM sys.indexes WHERE name = @name;", connection);
        command.Parameters.AddWithValue("@name", indexName);

        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return value is DBNull ? null : (string)value;
    }
}
