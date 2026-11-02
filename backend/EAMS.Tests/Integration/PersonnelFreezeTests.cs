using System.Data.Common;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Services;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 D-97, against real SQL Server: the freeze writes personnel to <c>EventPersonnel</c> in the one
/// save that closes the event, turns the non-tappers into <c>Absent</c> rows, and survives the two insert
/// races the unique indexes decide. Each race is run for real, from a second connection, at the moment the
/// freeze has finished reading and has not yet saved.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelFreezeTests : IntegrationTest
{
    public PersonnelFreezeTests(SqlServerFixture sql) : base(sql) { }

    private static readonly CaptureCapabilities PersonnelClient = new(Personnel: true);

    private async Task<PersonnelEventWorld> ArrangeAsync(string eventStatus = EventStatus.Open)
    {
        await using var db = NewDbContext();
        return await PersonnelEventWorld.ArrangeAsync(db, eventStatus);
    }

    private async Task<Guid> AddPersonnelAsync(
        Guid schoolId, string number, string status = PersonnelStatus.Active, bool deleted = false)
    {
        await using var db = NewDbContext();
        var person = TestData.NewPersonnel(schoolId, number, status: status);
        person.IsDeleted = deleted;
        db.Personnel.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    private async Task TapPersonnelAsync(PersonnelEventWorld world, string tapId = "p-tap")
    {
        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(world.EventId, PersonnelEventWorld.PersonnelUid, null, tapId, DateTime.UtcNow),
            PersonnelClient);
        Assert.Equal(TapOutcome.Recorded, response.Outcome);
    }

    private async Task TapStudentAsync(PersonnelEventWorld world)
    {
        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(world.EventId, PersonnelEventWorld.StudentUid, null, "s-tap", DateTime.UtcNow));
        Assert.Equal(TapOutcome.Recorded, response.Outcome);
    }

    private async Task SetStatusAsync(Guid eventId, string status)
    {
        await using var db = NewDbContext();
        var response = await EventsOn(db).ChangeStatusAsync(eventId, status);
        Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
    }

    private async Task SetPersonnelStatusAsync(Guid personnelId, string status)
    {
        await using var db = NewDbContext();
        (await db.Personnel.SingleAsync(p => p.Id == personnelId)).Status = status;
        await db.SaveChangesAsync();
    }

    private async Task<HashSet<Guid>> FrozenAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        return (await db.EventPersonnel.AsNoTracking()
            .Where(ep => ep.EventId == eventId)
            .Select(ep => ep.PersonnelId)
            .ToListAsync()).ToHashSet();
    }

    private async Task<List<AttendanceRecord>> PersonnelRowsAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        return await db.AttendanceRecords.AsNoTracking()
            .Where(a => a.EventId == eventId && a.PersonnelId != null)
            .ToListAsync();
    }

    private async Task<EventSummaryDto> SummaryAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        return (await EventsOn(db).GetSummaryAsync(eventId))!;
    }

    // ------------------------------------------------------------------------------- D-97's named tests

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    public async Task A_live_event_holds_no_EventPersonnel_rows(string status)
    {
        var world = await ArrangeAsync(status);
        if (status == EventStatus.Open) await TapPersonnelAsync(world);

        Assert.Empty(await FrozenAsync(world.EventId));
    }

    [Fact]
    public async Task Closing_writes_exactly_the_expected_personnel_and_Absent_rows_for_the_non_tappers()
    {
        var world = await ArrangeAsync();
        await using (var db = NewDbContext())
        {
            var closer = TestData.NewUser(world.SchoolId, "registrar@test.local");
            db.Users.Add(closer);
            await db.SaveChangesAsync();
            CurrentUser.UserId = closer.Id;
        }

        var nonTapper = await AddPersonnelAsync(world.SchoolId, "EMP-0002");
        var neverCountable = await AddPersonnelAsync(world.SchoolId, "EMP-0003", PersonnelStatus.Inactive);
        await TapPersonnelAsync(world);

        await SetStatusAsync(world.EventId, EventStatus.Closed);

        Assert.Equal([world.PersonnelId, nonTapper], await FrozenAsync(world.EventId));
        Assert.DoesNotContain(neverCountable, await FrozenAsync(world.EventId));

        var rows = await PersonnelRowsAsync(world.EventId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(AttendanceStatus.Present, Assert.Single(rows, r => r.PersonnelId == world.PersonnelId).Status);

        var absent = Assert.Single(rows, r => r.PersonnelId == nonTapper);
        Assert.Equal(AttendanceStatus.Absent, absent.Status);
        Assert.Equal(CaptureMethod.Import, absent.CaptureMethod);
        Assert.Null(absent.CheckInAt);
        Assert.Null(absent.OccurrenceId);
        Assert.Null(absent.StudentId);
        Assert.Equal(world.SchoolId, absent.SchoolId);
        Assert.Equal(CurrentUser.UserId, absent.RecordedByUserId);
    }

    /// <summary>D-97's invariant, over both populations, with no row <c>unexpected</c>.</summary>
    [Fact]
    public async Task The_summed_invariant_holds_on_a_mixed_event()
    {
        var world = await ArrangeAsync();
        var excused = await AddPersonnelAsync(world.SchoolId, "EMP-0002");
        var nonTapper = await AddPersonnelAsync(world.SchoolId, "EMP-0003");

        await using (var db = NewDbContext())
        {
            var absentStudent = TestData.NewStudent(world.SchoolId, "2023-0002");
            db.Students.Add(absentStudent);
            db.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentId = absentStudent.Id });
            await db.SaveChangesAsync();
        }

        await TapStudentAsync(world);
        await TapPersonnelAsync(world);
        await using (var db = NewDbContext())
        {
            var manual = await AttendanceOn(db).ManualAsync(
                world.EventId, null, excused, AttendanceStatus.Excused, notes: null);
            Assert.Equal(ManualOutcome.Saved, manual.Outcome);
        }

        await SetStatusAsync(world.EventId, EventStatus.Closed);

        var summary = await SummaryAsync(world.EventId);
        Assert.Equal(5, summary.Expected);
        Assert.Equal((2, 3), (summary.ExpectedStudents, summary.ExpectedPersonnel));
        Assert.Equal(0, summary.Unexpected);
        Assert.Equal(summary.Expected, summary.Present + summary.Late + summary.Absent + summary.Excused);
        Assert.Equal(2, summary.Absent);
        Assert.Contains(nonTapper, await FrozenAsync(world.EventId));

        await using var read = NewDbContext();
        var roster = (await EventsOn(read).GetRosterAsync(world.EventId))!;
        Assert.Equal(summary.Expected, roster.Present + roster.Late + roster.Absent + roster.Excused);
        Assert.Equal(0, roster.NotRecorded);
    }

    /// <summary>JJ, 2026-11-02: the freeze keeps anyone with a row, whatever they have become.</summary>
    [Fact]
    public async Task A_tapper_who_went_inactive_is_frozen_with_their_row()
    {
        var world = await ArrangeAsync();
        await TapPersonnelAsync(world);
        await SetPersonnelStatusAsync(world.PersonnelId, PersonnelStatus.Inactive);

        await SetStatusAsync(world.EventId, EventStatus.Closed);

        Assert.Equal([world.PersonnelId], await FrozenAsync(world.EventId));
        Assert.Equal(AttendanceStatus.Present, Assert.Single(await PersonnelRowsAsync(world.EventId)).Status);

        var summary = await SummaryAsync(world.EventId);
        Assert.Equal(1, summary.ExpectedPersonnel);
        Assert.Equal(0, summary.Unexpected);
    }

    /// <summary>D-97: the same <c>includeDeleted</c> argument as the student freeze.</summary>
    [Fact]
    public async Task A_member_soft_deleted_before_the_close_is_frozen_with_an_Absent_row_like_a_student()
    {
        var world = await ArrangeAsync();
        var deleted = await AddPersonnelAsync(world.SchoolId, "EMP-0002", deleted: true);

        await SetStatusAsync(world.EventId, EventStatus.Closed);

        Assert.Equal([world.PersonnelId, deleted], await FrozenAsync(world.EventId));
        Assert.Equal(AttendanceStatus.Absent,
            Assert.Single(await PersonnelRowsAsync(world.EventId), r => r.PersonnelId == deleted).Status);
    }

    [Fact]
    public async Task Cancelling_freezes_personnel_and_writes_no_absentees()
    {
        var world = await ArrangeAsync();

        await SetStatusAsync(world.EventId, EventStatus.Cancelled);

        Assert.Equal([world.PersonnelId], await FrozenAsync(world.EventId));
        Assert.Empty(await PersonnelRowsAsync(world.EventId));
    }

    /// <summary>Terminal, so the frozen rows answer, whatever the roster does afterwards.</summary>
    [Fact]
    public async Task A_closed_events_numbers_do_not_move_when_the_personnel_roster_changes_afterwards()
    {
        var world = await ArrangeAsync();
        await TapPersonnelAsync(world);
        await SetStatusAsync(world.EventId, EventStatus.Closed);
        var before = await SummaryAsync(world.EventId);

        await AddPersonnelAsync(world.SchoolId, "EMP-0002");
        await SetPersonnelStatusAsync(world.PersonnelId, PersonnelStatus.Inactive);
        await using (var db = NewDbContext())
        {
            (await db.Personnel.SingleAsync(p => p.Id == world.PersonnelId)).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal(before, await SummaryAsync(world.EventId));
    }

    /// <summary>
    /// History does not move: an event closed before this deploy has no rows, so its personnel side stays
    /// empty though its definition still resolves someone.
    /// </summary>
    [Fact]
    public async Task An_event_closed_before_the_freeze_existed_keeps_its_numbers()
    {
        var world = await ArrangeAsync(EventStatus.Closed);

        var summary = await SummaryAsync(world.EventId);

        Assert.Equal((1, 0), (summary.ExpectedStudents, summary.ExpectedPersonnel));
        Assert.Equal(1, summary.Expected);
    }

    // ------------------------------------------------------------------------------- one save, 2012 safe

    /// <summary>ADR-003 D-21: all of it commits with the status, in one save, or none of it does.</summary>
    [Fact]
    public async Task The_status_and_both_populations_commit_in_one_save()
    {
        var world = await ArrangeAsync();
        var capture = new CapturingSaveInterceptor();

        await using (var db = Sql.NewProductionShapedDbContext(School, capture))
        {
            var response = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        var save = Assert.Single(capture.Saves);
        Assert.Equal(1, save.Count<Event>(EntityState.Modified));
        Assert.Equal(1, save.Count<EventPersonnel>(EntityState.Added));
        Assert.Equal(2, save.Count<AttendanceRecord>(EntityState.Added)); // the student and the personnel member
    }

    /// <summary>
    /// A failed freeze leaves the event open with nothing written. The closer names no real user, so the
    /// <c>Absent</c> inserts fail on <c>FK_AttendanceRecords_Users_RecordedByUserId</c> from inside the
    /// database, after the frozen rows are in the same batch. An interceptor refusing the save would pass
    /// with a second save in the freeze too; this does not.
    /// </summary>
    [Fact]
    public async Task A_failed_freeze_writes_no_personnel_row_and_leaves_the_event_open()
    {
        var world = await ArrangeAsync();
        CurrentUser.UserId = Guid.NewGuid();

        await using (var db = Sql.NewProductionShapedDbContext(School))
        {
            await Assert.ThrowsAsync<DbUpdateException>(
                () => EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed));
        }

        Assert.Empty(await FrozenAsync(world.EventId));
        Assert.Empty(await PersonnelRowsAsync(world.EventId));

        await using var read = NewDbContext();
        Assert.Equal(EventStatus.Open, await read.Events.AsNoTracking()
            .Where(e => e.Id == world.EventId).Select(e => e.Status).SingleAsync());
    }

    /// <summary>
    /// SQL Server 2012 at compatibility 110 has no <c>OPENJSON</c>. The definition here takes the list
    /// <c>Contains</c> path, the one that would emit it, and every read of this phase runs over it.
    /// </summary>
    [Fact]
    public async Task The_freeze_and_the_counting_reads_emit_no_OPENJSON_at_the_deployed_compatibility_level()
    {
        var world = await ArrangeAsync();
        await using (var db = NewDbContext())
        {
            var definition = await db.AudienceDefinitions.SingleAsync(d => d.Id == world.DefinitionId);
            definition.AudienceType = AudienceType.Department;
            definition.CriteriaJson = """{"departments":["CICT","CCS"],"scope":"Employees"}""";
            await db.SaveChangesAsync();
        }

        var probe = new CommandProbe();
        await using (var db = Sql.NewProductionShapedDbContext(School, probe))
        {
            var events = EventsOn(db);
            await events.GetSummaryAsync(world.EventId);
            await events.GetRosterAsync(world.EventId);
            await events.GetAudienceAsync(world.EventId);
            Assert.Equal(EventWriteOutcome.Saved,
                (await events.ChangeStatusAsync(world.EventId, EventStatus.Closed)).Outcome);
        }

        Assert.Contains(probe.Commands, c => c.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                                             && c.Contains("EventPersonnel", StringComparison.Ordinal));
        Assert.DoesNotContain(probe.Commands, c => c.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------------------------- query cost

    /// <summary>What one event's figures cost, in statements, on a context that sees nothing else.</summary>
    private async Task<int> FigureStatementsAsync(Guid eventId)
    {
        Event ev;
        await using (var read = NewDbContext())
            ev = await read.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);

        var probe = new CommandProbe();
        await using var db = Sql.NewProductionShapedDbContext(School, probe);
        await ((IEventSummaryFigures)EventsOn(db)).FiguresForAsync(ev, ceiling: null, default);
        return probe.Commands.Count;
    }

    /// <summary>
    /// JJ accepted this cost on 2026-11-02 pending a VM measurement. The report runs this per event, up to
    /// <c>ReportLimits.MaxEventsPerReport</c> times, so a rise here multiplies. Move the number on purpose.
    /// </summary>
    [Fact]
    public async Task Figures_cost_seven_statements_on_a_closed_event()
    {
        var world = await ArrangeAsync();
        await SetStatusAsync(world.EventId, EventStatus.Closed);

        Assert.Equal(7, await FigureStatementsAsync(world.EventId));
    }

    /// <summary>The closed cost, plus each population's selector and definition read.</summary>
    [Fact]
    public async Task Figures_cost_eleven_statements_on_a_live_event()
    {
        var world = await ArrangeAsync();

        Assert.Equal(11, await FigureStatementsAsync(world.EventId));
    }

    // ----------------------------------------------------------------------------------------- the races

    /// <summary>
    /// D-97's named race. A real tap commits on a second connection after the freeze has decided this
    /// person is absent and before it saves, so the pending <c>Absent</c> loses to
    /// <c>UX_Attendance_Event_Personnel_Occurrence</c>. Losing is right: the tap is the truer record.
    /// </summary>
    [Fact]
    public async Task The_close_time_Absent_insert_racing_a_late_personnel_tap_leaves_one_row()
    {
        var world = await ArrangeAsync();
        var race = new BeforeFirstSave(() => TapPersonnelAsync(world, "late-tap"));

        await using (var db = Sql.NewProductionShapedDbContext(School, race))
        {
            var response = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        Assert.True(race.Fired);
        Assert.Equal(2, race.Saves); // the lost save and its retry: proof the insert really collided

        var row = Assert.Single(await PersonnelRowsAsync(world.EventId));
        Assert.Equal(AttendanceStatus.Present, row.Status);
        Assert.Equal([world.PersonnelId], await FrozenAsync(world.EventId));

        var summary = await SummaryAsync(world.EventId);
        Assert.Equal((1, 0), (summary.Present, summary.Unexpected));
    }

    /// <summary>
    /// Two closes at once, both real. The rival commits the whole freeze while this one is about to save,
    /// so this one loses the unique indexes, and its retry must see the rival's rows rather than stage them
    /// again.
    /// </summary>
    [Fact]
    public async Task A_rival_close_that_froze_the_member_first_is_survived_with_one_frozen_row()
    {
        var world = await ArrangeAsync();
        var race = new BeforeFirstSave(async () =>
        {
            await using var rival = NewDbContext();
            var closed = await EventsOn(rival).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, closed.Outcome);
        });

        await using (var db = Sql.NewProductionShapedDbContext(School, race))
        {
            var response = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, response.Outcome);
        }

        Assert.Equal(2, race.Saves);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.EventPersonnel.AsNoTracking().CountAsync(ep => ep.EventId == world.EventId));
        Assert.Equal(AttendanceStatus.Absent, Assert.Single(await PersonnelRowsAsync(world.EventId)).Status);
    }

    /// <summary>Runs <paramref name="race"/> once, as the first save starts, and counts the saves.</summary>
    private sealed class BeforeFirstSave(Func<Task> race) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public int Saves { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Saves++;
            if (!Fired)
            {
                Fired = true;
                await race();
            }

            return result;
        }
    }

    /// <summary>Every command one context sent.</summary>
    private sealed class CommandProbe : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
