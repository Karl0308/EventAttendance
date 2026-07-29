using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>AttendanceRecords.RowVersion</c> — the D-30 live cursor's column, asserted against the migrated
/// schema and against the behaviour of the EF mapping rather than against the model.
///
/// <para>
/// <b>The second test in this file is the whole of D-30's caution</b>, and it is the one that would
/// otherwise be discovered in production. EF's <c>.IsRowVersion()</c> is one keystroke shorter than what
/// <c>EamsDbContext</c> writes and differs in exactly one bit — <c>IsConcurrencyToken</c> — and flipping
/// that bit changes nothing about the schema, nothing about the migration, and nothing any schema
/// assertion could see. What it changes is that every <c>UPDATE</c> grows an
/// <c>AND [RowVersion] = @original</c> and starts throwing on a race the tap path already handles.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceRowVersionTests : IntegrationTest
{
    public AttendanceRowVersionTests(SqlServerFixture sql) : base(sql) { }

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
        db.Schools.Add(school);
        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        var ev = TestData.NewEvent(school.Id);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id);
    }

    private static AttendanceRecord NewRecord(World world) => new()
    {
        SchoolId = world.SchoolId,
        EventId = world.EventId,
        StudentId = world.StudentId,
        CheckInAt = TestData.Now,
        Status = AttendanceStatus.Present,
        CaptureMethod = CaptureMethod.Rfid,
    };

    /// <summary>
    /// The column is a real SQL Server <c>rowversion</c>, not a <c>binary(8)</c> the application
    /// happens to write. <c>sys.types</c> reports it as <c>timestamp</c> — <c>rowversion</c> is the
    /// documented synonym — and it is NOT NULL, which the engine enforces whether or not anyone asks.
    ///
    /// <para>
    /// Read from the database rather than from the EF model, per this suite's standing rule: the model
    /// is what we asked for and <c>sys.columns</c> is what we got, and the whole value of the column is
    /// that the <em>database</em> assigns it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_column_is_a_rowversion_and_not_nullable()
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT t.name, c.is_nullable, c.max_length
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('AttendanceRecords') AND c.name = 'RowVersion';
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "AttendanceRecords.RowVersion does not exist.");

        Assert.Equal("timestamp", reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
        Assert.Equal(AttendanceCursor.ByteLength, reader.GetInt16(2));
    }

    /// <summary>
    /// <b>The column is not a concurrency token.</b> Two contexts load the same row, one saves, then the
    /// other saves — which is precisely the shape of two taps of one card a few milliseconds apart, and
    /// of an organizer overriding a student who is at that moment walking through the reader.
    ///
    /// <para>
    /// With <c>.IsRowVersion()</c> the second save raises <c>DbUpdateConcurrencyException</c>, which
    /// nothing in <c>AttendanceService</c> catches — its recovery is built around
    /// <c>DbUpdateException</c> and SQL Server's unique-violation numbers. The result would be a 500 on
    /// the busiest write in the system, introduced by a column that was only ever meant to let a
    /// dashboard read the table in order.
    /// </para>
    ///
    /// <para>
    /// <b>This test is the negative control for the mapping.</b> It fails, with that exception, the
    /// moment <c>IsConcurrencyToken(false)</c> is dropped or <c>.IsRowVersion()</c> is substituted —
    /// which is the "simplification" a reviewer would reach for, since the two lines look equivalent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_stale_write_is_not_refused_because_the_cursor_is_not_a_concurrency_token()
    {
        var world = await ArrangeAsync();

        Guid recordId;
        await using (var db = NewDbContext())
        {
            var record = NewRecord(world);
            db.AttendanceRecords.Add(record);
            await db.SaveChangesAsync();
            recordId = record.Id;
        }

        await using var first = NewDbContext();
        await using var second = NewDbContext();

        var loadedFirst = await first.AttendanceRecords.SingleAsync(a => a.Id == recordId);
        var loadedSecond = await second.AttendanceRecords.SingleAsync(a => a.Id == recordId);

        // The winner. Its save bumps RowVersion in the database, so `loadedFirst` is now stale.
        loadedSecond.CheckOutAt = TestData.Now.AddHours(1);
        await second.SaveChangesAsync();

        // The loser, writing over a row whose version has moved. This must simply succeed.
        loadedFirst.Status = AttendanceStatus.Late;
        var exception = await Record.ExceptionAsync(() => first.SaveChangesAsync());

        Assert.True(
            exception is null,
            "Writing a stale AttendanceRecord raised " + exception?.GetType().Name + ". " +
            "AttendanceRecords.RowVersion has become an optimistic-concurrency token — almost " +
            "certainly because EamsDbContext now says .IsRowVersion() instead of " +
            "ValueGeneratedOnAddOrUpdate() + IsConcurrencyToken(false). D-30 adds a cursor and " +
            "changes no write semantics: AttendanceService recovers from concurrent writes through " +
            "the unique indexes and catches DbUpdateException, so DbUpdateConcurrencyException on " +
            "the TimeInOut check-out is an unhandled 500 on the busiest write in the system.");
    }

    /// <summary>
    /// Assigned on insert and moved on update — the two halves of "every change is visible to a
    /// cursor". Without the update half, a <c>TimeInOut</c> check-out would never reach a dashboard that
    /// had already seen the check-in.
    /// </summary>
    [Fact]
    public async Task The_cursor_moves_on_insert_and_on_update()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var record = NewRecord(world);
        db.AttendanceRecords.Add(record);
        await db.SaveChangesAsync();

        var afterInsert = record.RowVersion;
        Assert.True(afterInsert > AttendanceCursor.Beginning, "RowVersion was not assigned on insert.");

        record.CheckOutAt = TestData.Now.AddHours(1);
        await db.SaveChangesAsync();

        Assert.True(
            record.RowVersion > afterInsert,
            $"RowVersion stayed at {afterInsert} across an update. An update that does not move the " +
            "cursor is a change no polling client can ever see.");
    }

    /// <summary>
    /// The delta read's index. Non-unique — many rows share an event, and a <c>rowversion</c> is unique
    /// on its own anyway — and keyed <c>(EventId, RowVersion)</c> so the equality leads and the range
    /// follows.
    /// </summary>
    [Fact]
    public async Task The_delta_index_exists_over_event_and_cursor()
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = 'IX_Attendance_EventId_RowVersion'
            ORDER BY ic.key_ordinal;
            """,
            connection);

        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        }

        Assert.Equal(["EventId", "RowVersion"], columns);
    }
}
