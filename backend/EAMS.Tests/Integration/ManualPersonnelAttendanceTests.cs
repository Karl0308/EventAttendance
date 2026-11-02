using System.Net;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// ADR-015 phase B1c-2: <c>POST /attendance/manual</c> with <c>studentId</c> or <c>personnelId</c>
/// (D-99), held to D-95's refusals and its closed-event rule, through the real host and real SQL Server.
/// <c>ManualOverrideTests</c> holds the student behaviour, which is unchanged.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ManualPersonnelAttendanceTests : IntegrationTest
{
    public ManualPersonnelAttendanceTests(SqlServerFixture sql) : base(sql) { }

    private async Task<PersonnelEventWorld> ArrangeAsync(
        string eventStatus = EventStatus.Open, string personnelStatus = PersonnelStatus.Active, bool inAudience = true)
    {
        await using var db = NewDbContext();
        return await PersonnelEventWorld.ArrangeAsync(db, eventStatus, personnelStatus, inAudience);
    }

    private async Task FreezeAsync(PersonnelEventWorld world)
    {
        await using var db = NewDbContext();
        db.EventPersonnel.Add(new EventPersonnel { EventId = world.EventId, PersonnelId = world.PersonnelId });
        await db.SaveChangesAsync();
    }

    private async Task SetPersonnelStatusAsync(Guid personnelId, string status)
    {
        await using var db = NewDbContext();
        var person = await db.Personnel.SingleAsync(p => p.Id == personnelId);
        person.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task SoftDeleteAsync(Guid personnelId)
    {
        await using var db = NewDbContext();
        var person = await db.Personnel.SingleAsync(p => p.Id == personnelId);
        person.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The row a tap would have left. Direct, because a closed event cannot be tapped and the tests that use
    /// this are about what the override does with a row, not how it got there.
    /// </summary>
    private async Task SeedRowAsync(PersonnelEventWorld world)
    {
        await using var db = NewDbContext();
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            SchoolId = world.SchoolId,
            EventId = world.EventId,
            PersonnelId = world.PersonnelId,
            CheckInAt = TestData.Now,
            Status = AttendanceStatus.Present,
            CaptureMethod = CaptureMethod.Rfid,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<AttendanceRecord>> RowsAsync(Guid eventId)
    {
        await using var db = NewDbContext();
        return await db.AttendanceRecords.AsNoTracking().Where(a => a.EventId == eventId).ToListAsync();
    }

    private static async Task<HttpResponseMessage> ManualAsync(
        HttpClient client, Guid eventId, Guid? studentId = null, Guid? personnelId = null, string status = "Present")
    {
        var query = $"eventId={eventId}&status={status}";
        if (studentId is not null) query += $"&studentId={studentId}";
        if (personnelId is not null) query += $"&personnelId={personnelId}";

        return await client.PostAsync($"/api/v1/attendance/manual?{query}", content: null);
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, ManualOutcome code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code.ToString(), (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------------------------ exactly one

    [Fact]
    public async Task Naming_nobody_is_PersonRequired_and_writes_nothing()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId), HttpStatusCode.BadRequest, ManualOutcome.PersonRequired);

        Assert.Empty(await RowsAsync(world.EventId));
    }

    [Fact]
    public async Task Naming_a_student_and_a_personnel_member_is_PersonRequired_and_writes_nothing()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, world.StudentId, world.PersonnelId),
            HttpStatusCode.BadRequest, ManualOutcome.PersonRequired);

        Assert.Empty(await RowsAsync(world.EventId));
    }

    // ---------------------------------------------------------------------------------------- saved

    [Fact]
    public async Task A_countable_personnel_member_is_saved_as_a_v2_personnel_record()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await ManualAsync(client, world.EventId, personnelId: world.PersonnelId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(nameof(ManualOutcome.Saved), body.GetProperty("code").GetString());

        var record = body.GetProperty("record");
        Assert.Equal(AttendeePersonType.Personnel, record.GetProperty("personType").GetString());
        Assert.Equal(world.PersonnelId, record.GetProperty("personnelId").GetGuid());
        Assert.Equal(PersonnelEventWorld.PersonnelNumber, record.GetProperty("personnelNumber").GetString());
        Assert.Equal(PersonnelEventWorld.PersonnelName, record.GetProperty("personnelName").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("studentId").ValueKind);

        var row = Assert.Single(await RowsAsync(world.EventId));
        Assert.Equal(world.PersonnelId, row.PersonnelId);
        Assert.Null(row.StudentId);
        Assert.Equal(CaptureMethod.Manual, row.CaptureMethod);
        Assert.Equal(AttendanceStatus.Present, row.Status);
        Assert.Equal(world.SchoolId, row.SchoolId);
    }

    /// <summary>The override of a row that exists, as a student's override works: no second row.</summary>
    [Fact]
    public async Task An_override_of_a_personnel_row_updates_it_rather_than_adding_a_second()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal(
            HttpStatusCode.OK, (await ManualAsync(client, world.EventId, personnelId: world.PersonnelId)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await ManualAsync(client, world.EventId, personnelId: world.PersonnelId, status: AttendanceStatus.Excused)).StatusCode);

        Assert.Equal(AttendanceStatus.Excused, Assert.Single(await RowsAsync(world.EventId)).Status);
    }

    /// <summary>The route still does what it did, and a student answers in the v2 shape.</summary>
    [Fact]
    public async Task A_student_is_saved_through_the_same_route_as_a_v2_student_record()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await ManualAsync(client, world.EventId, world.StudentId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = (await BodyOf(response)).GetProperty("record");
        Assert.Equal(AttendeePersonType.Student, record.GetProperty("personType").GetString());
        Assert.Equal(world.StudentId, record.GetProperty("studentId").GetGuid());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("personnelId").ValueKind);
    }

    // ------------------------------------------------------------------------------------- the refusals

    [Fact]
    public async Task An_unknown_id_is_PersonnelNotFound()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: Guid.NewGuid()),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelNotFound);
    }

    [Fact]
    public async Task A_soft_deleted_member_is_PersonnelNotFound()
    {
        var world = await ArrangeAsync();
        await using (var db = NewDbContext())
        {
            var person = await db.Personnel.SingleAsync(p => p.Id == world.PersonnelId);
            person.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: world.PersonnelId),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelNotFound);
    }

    /// <summary>D-99 tenancy: the id is real, in another school, so to this event it is nobody.</summary>
    [Fact]
    public async Task A_member_of_another_school_is_PersonnelNotFound()
    {
        var world = await ArrangeAsync();
        var other = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: other.PersonnelId),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelNotFound);
    }

    /// <summary>D-95's order: <c>Inactive</c> before audience, here with the holder outside the audience too.</summary>
    [Fact]
    public async Task An_inactive_member_on_a_live_event_is_PersonnelInactive_ahead_of_the_audience_check()
    {
        var world = await ArrangeAsync(personnelStatus: PersonnelStatus.Inactive, inAudience: false);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: world.PersonnelId),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelInactive);

        Assert.Empty(await RowsAsync(world.EventId));
    }

    [Fact]
    public async Task A_member_outside_the_audience_is_PersonnelNotInAudience()
    {
        var world = await ArrangeAsync(inAudience: false);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: world.PersonnelId),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelNotInAudience);

        Assert.Empty(await RowsAsync(world.EventId));
    }

    // -------------------------------------------------------------------------- D-95 live, existing row

    /// <summary>
    /// D-95 (JJ, 2026-11-02): the two refusals guard the creation of a row. Someone who tapped and then went
    /// inactive could otherwise never be corrected by hand. The row is updated, and no second one appears.
    /// </summary>
    [Fact]
    public async Task A_tapper_who_went_inactive_can_be_corrected_on_a_live_event()
    {
        var world = await ArrangeAsync();
        await SeedRowAsync(world);
        await SetPersonnelStatusAsync(world.PersonnelId, PersonnelStatus.Inactive);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal(
            HttpStatusCode.OK,
            (await ManualAsync(client, world.EventId, personnelId: world.PersonnelId, status: AttendanceStatus.Excused)).StatusCode);

        Assert.Equal(AttendanceStatus.Excused, Assert.Single(await RowsAsync(world.EventId)).Status);
    }

    /// <inheritdoc cref="A_tapper_who_went_inactive_can_be_corrected_on_a_live_event"/>
    [Fact]
    public async Task A_tapper_who_left_the_audience_can_be_corrected_on_a_live_event()
    {
        var world = await ArrangeAsync(inAudience: false);
        await SeedRowAsync(world);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal(
            HttpStatusCode.OK,
            (await ManualAsync(client, world.EventId, personnelId: world.PersonnelId, status: AttendanceStatus.Excused)).StatusCode);

        Assert.Equal(AttendanceStatus.Excused, Assert.Single(await RowsAsync(world.EventId)).Status);
    }

    /// <summary>D-95: having a row does not bring back someone a live event treats as nobody.</summary>
    [Fact]
    public async Task A_soft_deleted_member_with_a_row_stays_PersonnelNotFound_on_a_live_event()
    {
        var world = await ArrangeAsync();
        await SeedRowAsync(world);
        await SoftDeleteAsync(world.PersonnelId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: world.PersonnelId, status: AttendanceStatus.Excused),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelNotFound);

        Assert.Equal(AttendanceStatus.Present, Assert.Single(await RowsAsync(world.EventId)).Status);
    }

    // --------------------------------------------------------------------------------- D-95 closed event

    /// <summary>D-95 (JJ, 2026-11-02): frozen means frozen, so a frozen member's soft delete changes nothing.</summary>
    [Fact]
    public async Task A_frozen_member_who_was_soft_deleted_can_still_be_marked_on_a_closed_event()
    {
        var world = await ArrangeAsync(eventStatus: EventStatus.Closed);
        await FreezeAsync(world);
        await SoftDeleteAsync(world.PersonnelId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        var response = await ManualAsync(client, world.EventId, personnelId: world.PersonnelId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = (await BodyOf(response)).GetProperty("record");
        Assert.Equal(PersonnelEventWorld.PersonnelNumber, record.GetProperty("personnelNumber").GetString());
        Assert.Equal(world.PersonnelId, Assert.Single(await RowsAsync(world.EventId)).PersonnelId);
    }

    /// <summary>
    /// The soft-deleted row is readable on a terminal event so the frozen test can run, and the frozen test
    /// is still what decides.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_member_outside_the_frozen_set_stays_refused_on_a_closed_event()
    {
        var world = await ArrangeAsync(eventStatus: EventStatus.Closed);
        await SoftDeleteAsync(world.PersonnelId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: world.PersonnelId),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelNotInAudience);

        Assert.Empty(await RowsAsync(world.EventId));
    }

    /// <summary>
    /// D-95: the existing-row rule is a live-event rule. On a terminal event membership is the only test, and
    /// B2's freeze is what will keep every row's owner in the set (D-97).
    /// </summary>
    [Fact]
    public async Task A_row_does_not_stand_in_for_frozen_membership_on_a_closed_event()
    {
        var world = await ArrangeAsync(eventStatus: EventStatus.Closed);
        await SeedRowAsync(world);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: world.PersonnelId, status: AttendanceStatus.Excused),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelNotInAudience);

        Assert.Equal(AttendanceStatus.Present, Assert.Single(await RowsAsync(world.EventId)).Status);
    }

    /// <summary>The frozen set is the whole test on a closed event: current audience does not matter.</summary>
    [Fact]
    public async Task A_frozen_member_can_be_marked_on_a_closed_event()
    {
        var world = await ArrangeAsync(eventStatus: EventStatus.Closed, inAudience: false);
        await FreezeAsync(world);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal(
            HttpStatusCode.OK, (await ManualAsync(client, world.EventId, personnelId: world.PersonnelId)).StatusCode);
        Assert.Equal(world.PersonnelId, Assert.Single(await RowsAsync(world.EventId)).PersonnelId);
    }

    /// <summary>
    /// D-95, ADR-003 D-17: a frozen member who has since gone <c>Inactive</c> can still be marked by hand.
    /// The override is the only route back from a mistake on a closed event.
    /// </summary>
    [Fact]
    public async Task A_frozen_member_who_has_gone_inactive_can_still_be_marked_on_a_closed_event()
    {
        var world = await ArrangeAsync(eventStatus: EventStatus.Closed);
        await FreezeAsync(world);
        await SetPersonnelStatusAsync(world.PersonnelId, PersonnelStatus.Inactive);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        Assert.Equal(
            HttpStatusCode.OK, (await ManualAsync(client, world.EventId, personnelId: world.PersonnelId)).StatusCode);
    }

    /// <summary>
    /// Not frozen means not expected, whatever the current audience says. This is also the known state
    /// until B2's freeze writes rows: a closed event refuses all personnel.
    /// </summary>
    [Fact]
    public async Task A_member_outside_the_frozen_set_is_PersonnelNotInAudience_though_the_live_audience_holds_them()
    {
        var world = await ArrangeAsync(eventStatus: EventStatus.Closed);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: world.PersonnelId),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelNotInAudience);
    }

    /// <summary>On a closed event membership is asked first and alone, so the answer is never Inactive.</summary>
    [Fact]
    public async Task An_inactive_member_outside_the_frozen_set_is_NotInAudience_never_Inactive_on_a_closed_event()
    {
        var world = await ArrangeAsync(eventStatus: EventStatus.Closed, personnelStatus: PersonnelStatus.Inactive);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, world.SchoolId);

        await AssertRefusedAsync(
            await ManualAsync(client, world.EventId, personnelId: world.PersonnelId),
            HttpStatusCode.NotFound, ManualOutcome.PersonnelNotInAudience);
    }
}
