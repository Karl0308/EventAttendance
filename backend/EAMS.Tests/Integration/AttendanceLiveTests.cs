using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>GET /attendance/live/{eventId}</c> — the D-29 cursor-delta poll that stands in for the §5/§6.4
/// SignalR hub, and the D-30 <c>rowversion</c> cursor underneath it.
///
/// <para>
/// <b>Everything that can go wrong here is silent.</b> A cursor that skips a row does not raise
/// anything; the dashboard simply never shows a student who tapped, which is indistinguishable from a
/// student who did not tap. So the tests are written against the property — "no committed row is ever
/// passed over" — rather than against the happy path, and the sharpest of them
/// (<see cref="The_cursor_never_advances_past_an_uncommitted_write"/>) arranges the exact interleaving
/// that a naive high-water-mark cursor loses a row to.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceLiveTests : IntegrationTest
{
    public AttendanceLiveTests(SqlServerFixture sql) : base(sql) { }

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId, Guid SecondStudentId);

    private async Task<World> ArrangeAsync(string status = EventStatus.Open)
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool($"USA-{Guid.NewGuid():N}"[..12]);
        db.Schools.Add(school);

        var first = TestData.NewStudent(school.Id, "2023-0001");
        var second = TestData.NewStudent(school.Id, "2023-0002", lastName: "Flores");
        db.Students.AddRange(first, second);

        var ev = TestData.NewEvent(school.Id, status);
        db.Events.Add(ev);

        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, first.Id, second.Id);
    }

    private async Task<Guid> RecordAsync(
        World world, Guid studentId, string status = AttendanceStatus.Present)
    {
        await using var db = NewDbContext();
        var record = new AttendanceRecord
        {
            SchoolId = world.SchoolId,
            EventId = world.EventId,
            StudentId = studentId,
            CheckInAt = TestData.Now,
            Status = status,
            CaptureMethod = CaptureMethod.Rfid,
        };
        db.AttendanceRecords.Add(record);
        await db.SaveChangesAsync();
        return record.Id;
    }

    /// <summary>
    /// A poll that is expected to succeed. Each one uses a fresh context, deliberately: a cached
    /// first-level cache is the classic way a cursor test passes against memory rather than against the
    /// database, and a cursor is exactly a claim about what the database has.
    /// </summary>
    private async Task<AttendanceLiveDto> LiveAsync(Guid eventId, string? since = null)
    {
        await using var db = NewDbContext();
        var response = await EventsOn(db).GetLiveAttendanceAsync(eventId, since);
        Assert.Equal(LiveOutcome.Ok, response.Outcome);
        return response.Live!;
    }

    /// <summary>
    /// The same poll, read under SNAPSHOT isolation. Used by exactly one test — see
    /// <see cref="The_cursor_never_advances_past_an_uncommitted_write"/> for why lock behaviour has to
    /// be taken out of that experiment and why this is not a claim about the endpoint.
    ///
    /// <para>
    /// The isolation level is set on the session of an explicitly opened connection rather than by
    /// beginning a transaction. That is not a style choice: <c>AddEamsInfrastructure</c> configures
    /// <c>EnableRetryOnFailure</c>, and EF refuses a user-initiated transaction under a retrying
    /// execution strategy — <c>BeginTransactionAsync</c> throws rather than returning one. Holding the
    /// connection open keeps every query EF issues on this context inside the same session, so each
    /// implicit transaction inherits the level.
    /// </para>
    /// </summary>
    private async Task<AttendanceLiveDto> SnapshotLiveAsync(Guid eventId, string? since)
    {
        await using var db = NewDbContext();
        await db.Database.OpenConnectionAsync();

        try
        {
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL SNAPSHOT;");

            var response = await EventsOn(db).GetLiveAttendanceAsync(eventId, since);
            Assert.Equal(LiveOutcome.Ok, response.Outcome);
            return response.Live!;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// <c>GET /events/{id}/summary</c>'s read, under the same SNAPSHOT isolation as
    /// <see cref="SnapshotLiveAsync"/> and for the same fixture reason. Used by exactly one test — the
    /// one that has to compare the two endpoints' counters while a transaction is held open, which is
    /// the only condition under which they are allowed to differ.
    /// </summary>
    private async Task<EventSummaryDto> SnapshotSummaryAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        await db.Database.OpenConnectionAsync();

        try
        {
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL SNAPSHOT;");

            var summary = await EventsOn(db).GetSummaryAsync(eventId);
            Assert.NotNull(summary);
            return summary!;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Permits explicit snapshot transactions on the test database.
    ///
    /// <para>
    /// <c>ALLOW_SNAPSHOT_ISOLATION</c>, deliberately <em>not</em> <c>READ_COMMITTED_SNAPSHOT</c>: this
    /// one only makes the level available to a session that asks for it and changes the behaviour of no
    /// other query in the suite, so the rest of these tests keep exercising the isolation the
    /// application actually runs under. Run before any transaction is opened, because the ALTER waits
    /// for active ones to drain.
    /// </para>
    /// </summary>
    private async Task AllowSnapshotIsolationAsync()
    {
        var database = new SqlConnectionStringBuilder(Sql.ConnectionString).InitialCatalog;

        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"ALTER DATABASE [{database}] SET ALLOW_SNAPSHOT_ISOLATION ON;", connection);
        await command.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------------------------ snapshot

    /// <summary>
    /// No cursor means a snapshot: every row of the event, under <c>entries</c>, with <c>changes</c>
    /// absent entirely. The split is not cosmetic — a snapshot <em>replaces</em> the client's state and
    /// a delta <em>merges</em> into it, so a body carrying both keys would leave the client guessing.
    /// </summary>
    [Fact]
    public async Task A_snapshot_carries_every_row_and_no_changes_list()
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);
        await RecordAsync(world, world.SecondStudentId, AttendanceStatus.Late);

        var live = await LiveAsync(world.EventId);

        Assert.NotNull(live.Entries);
        Assert.Null(live.Changes);
        Assert.Equal(2, live.Entries.Count);
        Assert.Equal(world.EventId, live.EventId);
        Assert.NotEmpty(live.Cursor);
    }

    /// <summary>
    /// A query string that lost its value — <c>?since=</c> — is a client with nothing to resume from,
    /// not a corrupted cursor. It gets a snapshot.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_cursor_is_treated_as_absent(string since)
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);

        var live = await LiveAsync(world.EventId, since);

        Assert.NotNull(live.Entries);
        Assert.Null(live.Changes);
    }

    /// <summary>An event nobody has tapped is an empty snapshot with a usable cursor, not an error.</summary>
    [Fact]
    public async Task An_event_with_no_attendance_is_an_empty_snapshot()
    {
        var world = await ArrangeAsync();

        var live = await LiveAsync(world.EventId);

        Assert.NotNull(live.Entries);
        Assert.Empty(live.Entries);
        Assert.True(AttendanceCursor.TryDecode(live.Cursor, out _));
    }

    // --------------------------------------------------------------------------------------- delta

    [Fact]
    public async Task A_delta_carries_only_what_changed_after_the_cursor()
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);

        var snapshot = await LiveAsync(world.EventId);
        Assert.Single(snapshot.Entries!);

        await RecordAsync(world, world.SecondStudentId);

        var delta = await LiveAsync(world.EventId, snapshot.Cursor);

        Assert.Null(delta.Entries);
        Assert.NotNull(delta.Changes);
        Assert.Equal(world.SecondStudentId, Assert.Single(delta.Changes).StudentId);
    }

    /// <summary>
    /// <b>An update reappears.</b> A <c>TimeInOut</c> check-out modifies a row a dashboard has already
    /// seen, and the cursor has to carry it — the <c>rowversion</c> moving on <c>UPDATE</c> as well as
    /// on <c>INSERT</c> is what makes that true, and it is the half a <c>CreatedAt</c>-based cursor
    /// would have missed entirely.
    /// </summary>
    [Fact]
    public async Task An_updated_row_comes_back_in_a_later_delta()
    {
        var world = await ArrangeAsync();
        var recordId = await RecordAsync(world, world.StudentId);

        var snapshot = await LiveAsync(world.EventId);

        await using (var db = NewDbContext())
        {
            var record = await db.AttendanceRecords.SingleAsync(a => a.Id == recordId);
            record.CheckOutAt = TestData.Now.AddHours(1);
            await db.SaveChangesAsync();
        }

        var delta = await LiveAsync(world.EventId, snapshot.Cursor);

        var changed = Assert.Single(delta.Changes!);
        Assert.Equal(recordId, changed.AttendanceId);
        Assert.NotNull(changed.CheckOutAt);
    }

    [Fact]
    public async Task A_delta_with_nothing_new_is_empty()
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);

        var snapshot = await LiveAsync(world.EventId);
        var delta = await LiveAsync(world.EventId, snapshot.Cursor);

        Assert.NotNull(delta.Changes);
        Assert.Empty(delta.Changes);
    }

    /// <summary>
    /// <b>The cursor is a ceiling, not the high-water mark of the rows returned.</b> Another event takes
    /// a tap; this event's delta is empty and its cursor still moves.
    ///
    /// <para>
    /// Returning <c>max(RowVersion of rows returned)</c> instead — the obvious implementation — leaves a
    /// quiet event's cursor pinned wherever its last tap was, so every poll re-scans a range that grows
    /// with the whole database's write volume and never returns anything. Nothing fails; the endpoint
    /// just gets slower for the rest of the event.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_cursor_advances_even_when_this_event_saw_nothing()
    {
        var quiet = await ArrangeAsync();
        var busy = await ArrangeAsync();
        await RecordAsync(quiet, quiet.StudentId);

        var snapshot = await LiveAsync(quiet.EventId);

        // A write somewhere else in the database, which must move the ceiling without producing a change
        // for this event.
        await RecordAsync(busy, busy.StudentId);

        var delta = await LiveAsync(quiet.EventId, snapshot.Cursor);

        Assert.Empty(delta.Changes!);

        Assert.True(AttendanceCursor.TryDecode(snapshot.Cursor, out var before));
        Assert.True(AttendanceCursor.TryDecode(delta.Cursor, out var after));
        Assert.True(
            after > before,
            $"The cursor stayed at {before} while the database advanced. It must report the ceiling " +
            "that was read, not the maximum RowVersion of the rows returned — otherwise a quiet " +
            "event re-scans a widening range on every poll, forever.");
    }

    /// <summary>
    /// <b>The subtlest correctness property in the phase, and the one no happy-path test reaches.</b>
    ///
    /// <para>
    /// A <c>rowversion</c> is assigned when a row is <em>written</em>, not when its transaction
    /// <em>commits</em>. So: transaction A writes a row and takes version 100 but has not committed; B
    /// writes a row, takes 101 and commits; a poll now sees 101 and not 100, and moves its cursor to
    /// 101; A commits. Row 100 is below the cursor forever and no later poll returns it. Two devices
    /// flushing queues at once produces exactly this, and the symptom is a student missing from the
    /// dashboard.
    /// </para>
    ///
    /// <para>
    /// The fix is the ceiling: <c>MIN_ACTIVE_ROWVERSION() - 1</c> is below every uncommitted write, so
    /// the cursor cannot cross one. The cost is that B's committed row is <em>withheld</em> until A
    /// lands, which is what the first assertion below checks — a poll may be a moment late, and may
    /// never be wrong.
    /// </para>
    ///
    /// <para>
    /// <b>The poll runs under SNAPSHOT isolation, and that is a fixture concession rather than a
    /// property of the endpoint.</b> An earlier version put the uncommitted row in a <em>different</em>
    /// event and claimed the polled event's index range therefore never touched it. That claim was
    /// wrong, and provably so: the counters include
    /// <c>RecordedStudentIds(id).Except(expectedIds).Count()</c>, whose plan on a table holding a
    /// handful of rows is a <em>scan</em> — so the poll took a shared lock on the held row and waited
    /// out the 30-second command timeout. It passed in isolation and failed inside its own class, which
    /// is the plan cache and statistics deciding, not the code.
    /// </para>
    ///
    /// <para>
    /// Snapshot isolation removes lock behaviour from the experiment without weakening it: the
    /// uncommitted row is still invisible to the reader, the committed one is still visible, and the
    /// ceiling is still the only thing that can withhold it. It is applied by setting the session's
    /// isolation level on an explicitly opened connection rather than by starting a transaction,
    /// because <c>EnableRetryOnFailure</c> makes EF refuse user-initiated transactions outright.
    /// </para>
    ///
    /// <para>
    /// <b>The blocking it sidesteps is real and is reported rather than fixed here:</b> under the
    /// default READ COMMITTED, a live poll's counters can wait on an in-flight attendance write. That
    /// is a pre-existing property of the shared summary aggregates — <c>GET /events/{id}/summary</c>
    /// does the same thing — not something this phase introduced, and the remedy (database-level
    /// <c>READ_COMMITTED_SNAPSHOT</c>) is an operational decision with far more blast radius than one
    /// endpoint.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_cursor_never_advances_past_an_uncommitted_write()
    {
        var polled = await ArrangeAsync();
        var elsewhere = await ArrangeAsync();

        await AllowSnapshotIsolationAsync();

        var baseline = await LiveAsync(polled.EventId);

        // Transaction A: a row written and deliberately left uncommitted.
        await using var held = new SqlConnection(Sql.ConnectionString);
        await held.OpenAsync();
        await using var uncommitted = (SqlTransaction)await held.BeginTransactionAsync();

        await using (var insert = new SqlCommand(
            """
            INSERT INTO [AttendanceRecords]
                ([Id], [SchoolId], [EventId], [StudentId], [Status], [CaptureMethod], [CreatedAt], [UpdatedAt])
            VALUES (@id, @school, @event, @student, 'Present', 'Rfid', SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            held, uncommitted))
        {
            insert.Parameters.AddWithValue("@id", Guid.NewGuid());
            insert.Parameters.AddWithValue("@school", elsewhere.SchoolId);
            insert.Parameters.AddWithValue("@event", elsewhere.EventId);
            insert.Parameters.AddWithValue("@student", elsewhere.StudentId);
            await insert.ExecuteNonQueryAsync();
        }

        // Transaction B: a later row, committed, on the event being polled.
        var lateButCommitted = await RecordAsync(polled, polled.StudentId);

        var whileHeld = await SnapshotLiveAsync(polled.EventId, baseline.Cursor);

        Assert.True(
            whileHeld.Changes!.Count == 0,
            "A committed row above an in-flight write was returned, and the cursor moved past it. " +
            "The ceiling must be MIN_ACTIVE_ROWVERSION() - 1: without it, the uncommitted row's " +
            "lower version lands below the client's cursor when it finally commits and is never " +
            "delivered — a student who tapped, missing from the dashboard, with nothing logged.");

        await uncommitted.CommitAsync();

        var afterCommit = await LiveAsync(polled.EventId, whileHeld.Cursor);

        Assert.Equal(lateButCommitted, Assert.Single(afterCommit.Changes!).AttendanceId);
    }

    /// <summary>
    /// <b>The live counters honour the same ceiling as the rows — and <c>GET /events/{id}/summary</c>
    /// deliberately does not</b> (Phase 4e, carried from 4d where it was documented rather than fixed).
    ///
    /// <para>
    /// The counters are aggregates run <em>after</em> the rows query. Unbounded, they can count a row
    /// the delta deliberately withheld, and the dashboard shows a headline "1 present" over a list of
    /// no names. On its own that is one poll cycle of skew and heals — but
    /// <c>MIN_ACTIVE_ROWVERSION()</c> is <b>database-wide</b>, so one unrelated long transaction (a
    /// roster import, an event freeze — modelled here by a held insert on a different school's event)
    /// pins the ceiling while the counters go on advancing, and the discrepancy lasts as long as that
    /// transaction does.
    /// </para>
    ///
    /// <para>
    /// <b>The second half of the assertion is the one that is easy to lose.</b>
    /// <c>GET /events/{id}/summary</c> has no cursor and hands out none, so bounding it would silently
    /// lower a published number for reasons that have nothing to do with attendance. It must keep
    /// counting every committed row — which is exactly what makes it the control here: same database,
    /// same instant, same isolation, and it sees the row the live poll withholds.
    /// </para>
    ///
    /// <para>
    /// SNAPSHOT isolation for the reason <see cref="The_cursor_never_advances_past_an_uncommitted_write"/>
    /// records at length: it removes lock behaviour from the experiment without weakening it. The
    /// aggregates would otherwise take a shared lock on the held row and wait out the command timeout.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_live_counters_honour_the_ceiling_and_the_summary_endpoint_still_does_not()
    {
        var polled = await ArrangeAsync();
        var elsewhere = await ArrangeAsync();

        await AllowSnapshotIsolationAsync();

        var baseline = await LiveAsync(polled.EventId);
        Assert.Equal(0, baseline.Counters.Present);

        // The unrelated long-running transaction. It pins MIN_ACTIVE_ROWVERSION() database-wide, which
        // is the whole point — it touches neither the polled event nor its school.
        await using var held = new SqlConnection(Sql.ConnectionString);
        await held.OpenAsync();
        await using var uncommitted = (SqlTransaction)await held.BeginTransactionAsync();

        await using (var insert = new SqlCommand(
            """
            INSERT INTO [AttendanceRecords]
                ([Id], [SchoolId], [EventId], [StudentId], [Status], [CaptureMethod], [CreatedAt], [UpdatedAt])
            VALUES (@id, @school, @event, @student, 'Present', 'Rfid', SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            held, uncommitted))
        {
            insert.Parameters.AddWithValue("@id", Guid.NewGuid());
            insert.Parameters.AddWithValue("@school", elsewhere.SchoolId);
            insert.Parameters.AddWithValue("@event", elsewhere.EventId);
            insert.Parameters.AddWithValue("@student", elsewhere.StudentId);
            await insert.ExecuteNonQueryAsync();
        }

        // A committed row on the polled event, above the pinned ceiling. The rows query withholds it.
        await RecordAsync(polled, polled.StudentId);

        var whileHeld = await SnapshotLiveAsync(polled.EventId, baseline.Cursor);

        Assert.Empty(whileHeld.Changes!);

        Assert.True(
            whileHeld.Counters.Present == 0,
            $"The delta withheld every row and the counters still reported " +
            $"{whileHeld.Counters.Present} present. The dashboard would show a headline count over a " +
            "list of no names — and because MIN_ACTIVE_ROWVERSION() is database-wide, one unrelated " +
            "long transaction keeps it that way rather than it healing on the next poll. The summary " +
            "aggregates must take the same ceiling the rows query takes.");

        Assert.True(
            whileHeld.Counters.Unexpected == 0,
            "Unexpected is counted from the same attendance rows and must take the same ceiling — a " +
            "walk-in that the delta has not delivered is not yet a walk-in the dashboard can name.");

        // The control: the summary endpoint has no cursor, so it must still see the committed row.
        var summary = await SnapshotSummaryAsync(polled.EventId);

        Assert.True(
            summary.Present == 1,
            $"GET /events/{{id}}/summary reported {summary.Present} present for a committed row. It " +
            "has no cursor and no ceiling, and bounding it would silently lower a published number " +
            "whenever any unrelated transaction happened to be open.");

        // And once the unrelated transaction lands, the two agree again with no client action.
        await uncommitted.CommitAsync();

        var afterCommit = await LiveAsync(polled.EventId, whileHeld.Cursor);

        Assert.Single(afterCommit.Changes!);
        Assert.Equal(1, afterCommit.Counters.Present);
    }

    // ------------------------------------------------------------------- cursor clamp and paging

    /// <summary>
    /// <b>A cursor above the current ceiling falls back to a snapshot</b> (Phase 4d review).
    ///
    /// <para>
    /// <c>TryDecode</c> validates shape only — any eight base64url bytes decode — so "a cursor we did
    /// not issue is refused" was a stronger claim than the code could make. The case that actually
    /// happens is a database restored from backup, or one rebuilt by a migration <c>Down</c>/<c>Up</c>,
    /// where <c>@@DBTS</c> is now lower than a cursor a dashboard is still holding. Used as a floor,
    /// that cursor selects nothing forever and the dashboard silently stops updating, with no error
    /// anywhere.
    /// </para>
    ///
    /// <para>
    /// The clamp is self-healing: one snapshot, and the client is back on a cursor this database issued.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cursor_from_the_future_falls_back_to_a_snapshot()
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);

        var live = await LiveAsync(world.EventId, AttendanceCursor.Encode(long.MaxValue));

        Assert.NotNull(live.Entries);
        Assert.Null(live.Changes);
        Assert.Single(live.Entries);

        // And the cursor handed back is a real one, so the next poll is an ordinary delta.
        Assert.True(AttendanceCursor.TryDecode(live.Cursor, out var reissued));
        Assert.True(reissued < long.MaxValue);
    }

    /// <summary>
    /// A cursor exactly at the ceiling is <em>not</em> clamped — it is the ordinary steady-state case,
    /// the value the previous poll handed out, and treating it as suspect would make every quiet event
    /// re-snapshot on every poll.
    /// </summary>
    [Fact]
    public async Task A_cursor_exactly_at_the_ceiling_is_still_a_delta()
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);

        var snapshot = await LiveAsync(world.EventId);
        var delta = await LiveAsync(world.EventId, snapshot.Cursor);

        Assert.Null(delta.Entries);
        Assert.NotNull(delta.Changes);
        Assert.Empty(delta.Changes);
    }

    /// <summary>
    /// <b>A snapshot is capped and pages through the cursor.</b> Before the cap, a five-thousand-
    /// attendee convocation returned five thousand delta objects to every open dashboard on its first
    /// poll — on the one endpoint designed to be called in a loop.
    ///
    /// <para>
    /// The assertion that makes the cap safe rather than lossy is the second half: draining the pages
    /// yields every row exactly once. A truncated page's cursor is the last row delivered, so the next
    /// poll continues from it — a rowversion is unique per database, so no two rows share the boundary
    /// and nothing can be skipped by a tie.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_snapshot_is_capped_and_the_remaining_rows_page_through_the_cursor()
    {
        var world = await ArrangeAsync();

        // One row more than a page, built directly rather than through RecordAsync so the test is not
        // 501 round trips.
        const int Total = AttendanceLiveOptions.MaxPageRows + 1;
        await using (var db = NewDbContext())
        {
            for (var i = 0; i < Total; i++)
            {
                var student = TestData.NewStudent(world.SchoolId, $"2024-{i:0000}");
                db.Students.Add(student);
                db.AttendanceRecords.Add(new AttendanceRecord
                {
                    SchoolId = world.SchoolId,
                    EventId = world.EventId,
                    StudentId = student.Id,
                    CheckInAt = TestData.Now,
                    Status = AttendanceStatus.Present,
                    CaptureMethod = CaptureMethod.Rfid,
                });
            }

            await db.SaveChangesAsync();
        }

        var first = await LiveAsync(world.EventId);

        Assert.Equal(AttendanceLiveOptions.MaxPageRows, first.Entries!.Count);
        Assert.True(first.HasMore, "A truncated page must say so, or a client waits pollAfterSeconds per page.");

        var second = await LiveAsync(world.EventId, first.Cursor);

        Assert.False(second.HasMore);
        Assert.Single(second.Changes!);

        // Every row, exactly once, across the two pages.
        var delivered = first.Entries.Concat(second.Changes!).Select(d => d.AttendanceId).ToList();
        Assert.Equal(Total, delivered.Count);
        Assert.Equal(Total, delivered.Distinct().Count());
    }

    /// <summary>An ordinary response is not truncated and says so.</summary>
    [Fact]
    public async Task A_response_within_the_page_cap_reports_no_more()
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);

        Assert.False((await LiveAsync(world.EventId)).HasMore);
    }

    // -------------------------------------------------------------------------------- the payload

    /// <summary>
    /// <b>The delta is a superset of §6.4's declared hub payload</b>, and its two count fields are the
    /// summary's own numbers. That is the seam that makes deferring SignalR reversible: a hub, if it is
    /// ever added, emits this object unchanged.
    ///
    /// <para>
    /// The counters being <c>GET /events/{id}/summary</c>'s object rather than a purpose-built one is
    /// what stops the dashboard header disagreeing with the summary page — the denominator behind it is
    /// ADR-003 D-12/D-13 arithmetic, which that ADR records as failing silently when duplicated.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_delta_carries_the_summary_counters()
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);
        await RecordAsync(world, world.SecondStudentId, AttendanceStatus.Late);

        await using var db = NewDbContext();
        var events = EventsOn(db);

        var summary = await events.GetSummaryAsync(world.EventId);
        var live = (await events.GetLiveAttendanceAsync(world.EventId, null)).Live!;

        Assert.Equal(summary, live.Counters);
        Assert.All(live.Entries!, d =>
        {
            Assert.Equal(summary!.Present, d.PresentCount);
            Assert.Equal(summary.Expected, d.ExpectedCount);
            Assert.Equal(world.EventId, d.EventId);
        });
    }

    /// <summary>
    /// The student's identity travels with the delta. A reducer that had to fetch a name per row would
    /// make the "cheap poll" argument for D-29 false.
    /// </summary>
    [Fact]
    public async Task A_delta_names_the_student_it_is_about()
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);

        var live = await LiveAsync(world.EventId);

        var delta = Assert.Single(live.Entries!);
        Assert.Equal("2023-0001", delta.StudentNumber);
        Assert.Equal("Maria Reyes Santos", delta.StudentName);
        Assert.Equal(AttendanceStatus.Present, delta.Status);
        Assert.Equal(CaptureMethod.Rfid, delta.CaptureMethod);
        Assert.NotNull(delta.CheckInAt);
    }

    /// <summary>
    /// The poll interval is configuration, not a constant — that is the only lever a polling design has
    /// for backing clients off under load without shipping a client release.
    /// </summary>
    [Fact]
    public async Task The_poll_interval_comes_from_configuration()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();

        var standard = await EventsOn(db).GetLiveAttendanceAsync(world.EventId, null);
        Assert.Equal(
            AttendanceLiveOptions.DefaultPollAfterSeconds, standard.Live!.PollAfterSeconds);

        var backedOff = await EventsOn(db, new AttendanceLiveOptions(45))
            .GetLiveAttendanceAsync(world.EventId, null);
        Assert.Equal(45, backedOff.Live!.PollAfterSeconds);
    }

    // ------------------------------------------------------------------------------------ refusals

    /// <summary>
    /// <b>An unrecognised cursor is refused, not silently downgraded to a snapshot.</b> The friendlier
    /// behaviour is the wrong one: the client asked for a delta, so it would merge a complete row set
    /// into state that already holds those rows, and the only symptom is a dashboard that occasionally
    /// duplicates every entry.
    /// </summary>
    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("AAAA")]
    [InlineData("0")]
    public async Task An_unrecognised_cursor_is_refused(string since)
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await EventsOn(db).GetLiveAttendanceAsync(world.EventId, since);

        Assert.Equal(LiveOutcome.InvalidCursor, response.Outcome);
        Assert.Null(response.Live);
    }

    /// <summary>
    /// The cursor is checked before the event, so a client with two problems is told about the one it
    /// can act on — the same ordering rule the tap path follows for event state versus card resolution.
    /// </summary>
    [Fact]
    public async Task A_bad_cursor_is_reported_even_when_the_event_is_also_unknown()
    {
        await using var db = NewDbContext();
        var response = await EventsOn(db).GetLiveAttendanceAsync(Guid.NewGuid(), "not-a-cursor");

        Assert.Equal(LiveOutcome.InvalidCursor, response.Outcome);
    }

    [Fact]
    public async Task An_unknown_event_is_not_found()
    {
        await using var db = NewDbContext();
        var response = await EventsOn(db).GetLiveAttendanceAsync(Guid.NewGuid(), null);

        Assert.Equal(LiveOutcome.EventNotFound, response.Outcome);
        Assert.Null(response.Live);
    }

    /// <summary>
    /// A soft-deleted event is gone as far as every other read is concerned, and this one agrees. A live
    /// view of an event the calendar says does not exist is the same defect the capture path closed when
    /// it started filtering deleted students.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_event_is_not_found()
    {
        var world = await ArrangeAsync();
        await RecordAsync(world, world.StudentId);

        await using (var db = NewDbContext())
        {
            var ev = await db.Events.SingleAsync(e => e.Id == world.EventId);
            ev.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var response = await EventsOn(read).GetLiveAttendanceAsync(world.EventId, null);

        Assert.Equal(LiveOutcome.EventNotFound, response.Outcome);
    }

    /// <summary>
    /// §11's tenant filter reaches this endpoint too. Asserted because the delta query is new and its
    /// <c>RowVersion</c> predicate is the one shape in the system that could plausibly have been written
    /// as raw SQL, which is where a query filter goes missing.
    /// </summary>
    [Fact]
    public async Task Another_schools_event_is_not_visible()
    {
        var theirs = await ArrangeAsync();
        var ours = await ArrangeAsync();
        await RecordAsync(theirs, theirs.StudentId);

        await using var db = NewDbContext(new TestSchoolContext { CurrentSchoolId = ours.SchoolId });
        var response = await EventsOn(db).GetLiveAttendanceAsync(theirs.EventId, null);

        Assert.Equal(LiveOutcome.EventNotFound, response.Outcome);
    }
}
