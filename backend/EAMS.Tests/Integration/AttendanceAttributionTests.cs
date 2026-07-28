using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>AttendanceRecords.RecordedByUserId</c>, populated from <c>ICurrentUser</c> on the manual path.
///
/// <para>
/// <b>Why this is worth testing while the answer is always null.</b> Of everything ADR-001 D-6
/// deferred, attribution is the only piece that cannot be added retroactively. A missing query filter
/// or a missing permission check starts working the day it is written; a row saved today with no
/// author can never be given one. §6.4 calls the manual-override endpoint audited, so every override
/// written between now and Phase 6 is an unattributable edit to an attendance record — and the only
/// thing that limits the size of that hole is wiring the seam early.
/// </para>
///
/// <para>
/// The seam is therefore tested with a non-null user even though production never supplies one. A test
/// that only ever saw null could not tell "populated from ICurrentUser" apart from "nothing writes this
/// column", which is exactly the bug being prevented.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceAttributionTests : IntegrationTest
{
    public AttendanceAttributionTests(SqlServerFixture sql) : base(sql) { }

    private const string Uid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId, Guid UserId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var user = TestData.NewUser(school.Id);
        db.Users.Add(user);
        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, Uid));
        var ev = TestData.NewEvent(school.Id);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id, user.Id);
    }

    [Fact]
    public async Task A_manual_entry_records_who_made_it()
    {
        var world = await ArrangeAsync();
        CurrentUser.UserId = world.UserId;

        await using (var db = NewDbContext())
            await AttendanceOn(db).ManualAsync(world.EventId, world.StudentId, "Excused", "Medical.");

        await using var read = NewDbContext();
        var record = await read.AttendanceRecords.AsNoTracking().SingleAsync();
        Assert.Equal(world.UserId, record.RecordedByUserId);
        Assert.Equal(CaptureMethod.Manual, record.CaptureMethod);
    }

    /// <summary>
    /// An override of a row a tap created. The column answers "who is accountable for this row's
    /// current state", and after an override that is the organizer — the tap's own evidence
    /// (<c>RfidCardId</c>, <c>CheckInAt</c>) is untouched, so nothing is lost by reattributing.
    /// </summary>
    [Fact]
    public async Task An_override_of_a_tapped_record_records_who_overrode_it()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await AttendanceOn(db).TapAsync(new TapRequest(world.EventId, Uid, null, "tap-1", TestData.Now));

        await using (var read = NewDbContext())
            Assert.Null((await read.AttendanceRecords.AsNoTracking().SingleAsync()).RecordedByUserId);

        CurrentUser.UserId = world.UserId;
        await using (var db = NewDbContext())
            await AttendanceOn(db).ManualAsync(world.EventId, world.StudentId, "Excused", "Left early.");

        await using var after = NewDbContext();
        var record = await after.AttendanceRecords.AsNoTracking().SingleAsync();
        Assert.Equal(world.UserId, record.RecordedByUserId);
        Assert.NotNull(record.CheckInAt);
        Assert.NotNull(record.RfidCardId);
    }

    /// <summary>
    /// The shipping configuration. Null is the honest answer — "written before authentication existed"
    /// — and Phase 6 can find these rows and say so. A synthetic "system" user would instead be a
    /// fabricated audit entry indistinguishable from a real one.
    /// </summary>
    [Fact]
    public async Task With_no_authenticated_user_the_column_is_left_null_rather_than_invented()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
            await AttendanceOn(db).ManualAsync(world.EventId, world.StudentId, "Absent", null);

        await using var read = NewDbContext();
        Assert.Null((await read.AttendanceRecords.AsNoTracking().SingleAsync()).RecordedByUserId);
    }

    /// <summary>
    /// A tap is not attributed to a person: the actor is a card at a reader, and §4.9 records that
    /// through <c>RfidCardId</c> and <c>DeviceId</c>. Stamping the logged-in admin who happens to be
    /// looking at the dashboard would be wrong, so the capture path deliberately does not.
    /// </summary>
    [Fact]
    public async Task A_tap_is_not_attributed_to_a_user()
    {
        var world = await ArrangeAsync();
        CurrentUser.UserId = world.UserId;

        await using (var db = NewDbContext())
            await AttendanceOn(db).TapAsync(new TapRequest(world.EventId, Uid, null, "tap-1", TestData.Now));

        await using var read = NewDbContext();
        Assert.Null((await read.AttendanceRecords.AsNoTracking().SingleAsync()).RecordedByUserId);
    }
}
