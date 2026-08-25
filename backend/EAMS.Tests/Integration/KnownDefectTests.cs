using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Defects and gaps this suite found in Phase 0a–0c code, written as the assertion that <em>should</em>
/// hold and skipped with the reason.
///
/// <para>
/// <b>Why skipped rather than fixed, or deleted.</b> Fixing production code was out of scope for the
/// QA phase, and a defect described only in a report decays into a paragraph nobody reads. A skipped
/// test is executable documentation: the expected behaviour is already written and reviewed, so the
/// fix is "delete one <c>Skip =</c> and make it green" rather than "work out what correct means".
/// Each one names what actually happens today, so a reader can judge severity without re-deriving it.
/// </para>
///
/// <para>
/// <b>None of these is speculative.</b> Every observation below was reproduced against real SQL
/// Server through the same fixture the rest of the suite uses.
/// </para>
///
/// <para>
/// <b>Seven have since been closed</b> and now run — the mechanism worked as designed, so six were
/// un-skipped in place rather than rewritten: DEFECT 1's soft-delete half, both halves of DEFECT 2,
/// DEFECT 3, DEFECT 4, and GAP 6's invited-roster denominator, which Phase 3a built. Each one's
/// comment records what changed.
/// </para>
///
/// <para>
/// <b>The sixth was closed differently and is worth reading as a pattern.</b> DEFECT 1's
/// <c>Status</c> half — a graduated student's tap — was skipped pending "a product decision". Phase
/// 3a did not make that decision; it made it unnecessary, by resolving expected attendees from
/// enrollments so that a graduated student is structurally never in a denominator. The skip was
/// therefore replaced by the assertion that is actually load-bearing rather than deleted or forced
/// green. <b>A skipped test whose premise has dissolved should be rewritten to the surviving rule,
/// not ticked off.</b>
/// </para>
///
/// <para>
/// <b>Nothing is skipped any more.</b> The last one — DEFECT 5, the check-out tap id — was closed by
/// Phase 4c D-34 and, like DEFECT 1's <c>Status</c> half, was <em>rewritten</em> rather than ticked
/// off: one of its two assertions described a fix that would have repaired the check-out's idempotency
/// by breaking the check-in's. The rule this file has now demonstrated twice is worth stating plainly:
/// <b>a skipped test whose premise has dissolved should be rewritten to the surviving rule</b>, and an
/// assertion written before the design existed is evidence about the defect, not about the fix.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class KnownDefectTests : IntegrationTest
{
    public KnownDefectTests(SqlServerFixture sql) : base(sql) { }

    private const string Uid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

    private async Task<World> ArrangeAsync(
        string attendanceMode = "Single", bool studentDeleted = false, string studentStatus = "Active")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var student = TestData.NewStudent(school.Id, status: studentStatus);
        student.IsDeleted = studentDeleted;
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, Uid));
        var ev = TestData.NewEvent(school.Id, attendanceMode: attendanceMode);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id);
    }

    // ------------------------------------------------------------------ DEFECT 1

    /// <summary>
    /// <b>A soft-deleted student can still tap and be marked present.</b>
    ///
    /// <para>
    /// <c>StudentService</c> filters <c>!s.IsDeleted</c> on every read, but the tap path resolves the
    /// student through <c>RfidCards.Include(c =&gt; c.Student)</c> and never applies that filter. The
    /// result is an attendance record for someone the roster says does not exist: the record is
    /// written, counted in the event summary, and returned to the mobile client with the student's
    /// name — while <c>GET /students</c> and the admin grid show nobody by that name. Reproduced:
    /// the tap returns <c>Recorded / "Checked in (Present)."</c> and one row is written.
    /// </para>
    ///
    /// <para>
    /// The card stays active because deactivating cards is a separate registrar action (ADR-001 D-3),
    /// so "delete the student" does not imply "deactivate the card" today. Whether the right fix is
    /// filtering the tap lookup, cascading deactivation on soft delete, or both is a design call —
    /// hence a report, not a patch.
    /// </para>
    ///
    /// <para>
    /// <b>CLOSED.</b> Only the half that is not a design call was taken: <c>TapAsync</c>'s card
    /// lookup now carries <c>!c.Student!.IsDeleted</c>, so the capture path agrees with the three
    /// <c>StudentService</c> reads instead of contradicting them, and the card resolves to nothing —
    /// hence <c>CardNotFound</c>. Whether soft-deleting a student should also deactivate their cards
    /// is still open and still a registrar-workflow decision; it is a data-hygiene improvement on
    /// top of this, not a substitute for it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_student_cannot_tap()
    {
        var world = await ArrangeAsync(studentDeleted: true);

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(new TapRequest(world.EventId, Uid, null, null, TestData.Now));

        Assert.Equal(TapOutcome.CardNotFound, response.Outcome);
        Assert.False(response.Result.Success);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.AttendanceRecords.CountAsync());
    }

    /// <summary>
    /// <b>CLOSED — by dissolving the question, not by answering it.</b>
    ///
    /// <para>
    /// This was <c>A_graduated_student_cannot_tap</c>, skipped as "needs a product decision": the tap
    /// path ignores §4.3's <c>Status</c>, so a <c>Graduated</c> student's card resolves and records
    /// attendance exactly like an active one, and the plan does not say whether that is intended.
    /// </para>
    ///
    /// <para>
    /// Phase 3a made the decision unnecessary. An event's expected attendees are resolved from §4.8
    /// <c>EventGroups</c> into <em>enrollments in a term</em> — a graduated student has none, so they
    /// are in no section group, in no denominator, and in no absentee list. The thing the original
    /// gap was worried about (an alumnus quietly inflating a count) cannot happen regardless of what
    /// the tap path does, which is why blocking the tap would have been the wrong lever: it would
    /// discard evidence that a person was physically present in order to protect a number that no
    /// longer depends on it.
    /// </para>
    ///
    /// <para>
    /// So the assertion is inverted. Rather than "a graduated student cannot tap", the rule now worth
    /// pinning is <b>"a student with no enrollment is not an expected attendee"</b> — and it is
    /// asserted against a real projected section group rather than against the tap path, because that
    /// is where the guarantee actually lives. If they do tap, the roster lists them with
    /// <c>IsExpected = false</c>: a truthful record of an alumnus who turned up, counted in neither
    /// the denominator nor the absentees.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_student_with_no_enrollment_is_not_an_expected_attendee()
    {
        Guid schoolId, eventId, enrolledId, graduatedId, sectionGroupId;

        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);

            var term = TestData.NewTerm(school.Id);
            db.Terms.Add(term);
            var course = TestData.NewCourse(school.Id);
            db.Courses.Add(course);
            var offering = TestData.NewOffering(term.Id, course.Id, "BSCRIM 2-A");
            db.CourseOfferings.Add(offering);

            // Enrolled, and therefore in the cohort.
            var enrolled = TestData.NewStudent(school.Id, "2023-0001", lastName: "Santos");
            db.Students.Add(enrolled);
            db.Enrollments.Add(TestData.NewEnrollment(enrolled.Id, offering.Id));

            // Graduated, still on the roster, still holding an active card — and enrolled in nothing.
            // That last clause is the whole of the reasoning; the Status column is incidental.
            var graduated = TestData.NewStudent(
                school.Id, "2019-0007", lastName: "Alumnus", status: "Graduated");
            db.Students.Add(graduated);
            db.RfidCards.Add(TestData.NewCard(school.Id, graduated.Id, Uid));

            var ev = TestData.NewEvent(school.Id);
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            schoolId = school.Id;
            eventId = ev.Id;
            enrolledId = enrolled.Id;
            graduatedId = graduated.Id;

            await ProjectionOn(db).SyncTermAsync(term.Id);

            sectionGroupId = await db.StudentGroups
                .Where(g => g.SchoolId == schoolId
                         && g.SourceEntityType == GroupSourceEntityType.Section)
                .Select(g => g.Id)
                .SingleAsync();
        }

        await using (var db = NewDbContext())
        {
            var attach = await EventsOn(db).AttachAudienceAsync(
                eventId, new EventAudienceRequest([sectionGroupId], null));
            Assert.Equal(EventWriteOutcome.Saved, attach.Outcome);
        }

        await using (var read = NewDbContext())
        {
            var roster = await EventsOn(read).GetRosterAsync(eventId);

            Assert.NotNull(roster);
            Assert.Equal(1, roster!.Expected);
            Assert.Contains(roster.Entries, e => e.StudentId == enrolledId && e.IsExpected);
            Assert.DoesNotContain(roster.Entries, e => e.StudentId == graduatedId);
        }

        // And the other half, which is what makes this a replacement rather than a deletion: the tap
        // is still accepted. The graduated student appears on the roster as a walk-in and is counted
        // in neither the denominator nor the absentees.
        await using (var db = NewDbContext())
        {
            var tap = await AttendanceOn(db).TapAsync(
                new TapRequest(eventId, Uid, null, null, TestData.Now));
            Assert.Equal(TapOutcome.Recorded, tap.Outcome);
        }

        await using (var read = NewDbContext())
        {
            var roster = await EventsOn(read).GetRosterAsync(eventId);

            Assert.NotNull(roster);
            Assert.Equal(1, roster!.Expected);
            Assert.Equal(1, roster.Present);
            Assert.Contains(roster.Entries, e => e.StudentId == graduatedId && !e.IsExpected);

            // The two are different people, and the roster says so rather than netting them off: the
            // one expected student is still un-recorded, the one present student was never invited.
            Assert.Equal(1, roster.NotRecorded);
            Assert.Equal(2, roster.Entries.Count);

            var summary = await EventsOn(read).GetSummaryAsync(eventId);
            Assert.Equal(1, summary!.Expected);
            Assert.Equal(1, summary.Present);
        }
    }

    // ------------------------------------------------------------------ DEFECT 2

    /// <summary>
    /// <b>An unregistered <c>deviceId</c> returns HTTP 500.</b>
    ///
    /// <para>
    /// <c>AttendanceRecords.DeviceId</c> is a foreign key, and the tap path validates the event and
    /// the card but never the device. A tap carrying a <c>deviceId</c> that is not in <c>Devices</c>
    /// reaches <c>SaveChanges</c> and fails with
    /// <c>FK_AttendanceRecords_Devices_DeviceId</c> — a <c>DbUpdateException</c> that
    /// <c>IsUniqueViolation</c> correctly declines to swallow, so it propagates unhandled to the
    /// pipeline. Reproduced: <c>DbUpdateException</c> from the service; 500 over HTTP.
    /// </para>
    ///
    /// <para>
    /// This matters because §15 makes device registration part of Phase 3, so a mobile client will
    /// hold a <c>deviceId</c> that can go stale — a wiped or re-provisioned handset flushing its
    /// offline queue hits this on <em>every</em> queued tap, and a 500 is exactly the status §8.2's
    /// retry logic treats as retryable. The queue never drains and the taps are never recorded.
    /// </para>
    ///
    /// <para>
    /// <b>CLOSED.</b> <c>TapAsync</c> validates <c>DeviceId</c> against <c>Devices</c> before the
    /// write and returns <c>DeviceNotRegistered</c>, which the controller maps to 404. Nothing is
    /// written, and the status is one §8.2's retry logic stops on rather than retries.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tap_from_an_unregistered_device_is_rejected_rather_than_throwing()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(world.EventId, Uid, Guid.NewGuid(), "queued-0001", TestData.Now));

        Assert.Equal(TapOutcome.DeviceNotRegistered, response.Outcome);
        Assert.False(response.Result.Success);
        Assert.Null(response.Result.Record);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.AttendanceRecords.CountAsync());
    }

    /// <summary>
    /// The same rejection at the HTTP boundary, which is the only place it matters to the caller
    /// §8.2 publishes this contract to. Asserted as a specific 4xx rather than merely "not a 500":
    /// the defect was that an offline queue kept retrying, and only the status code tells it to stop.
    ///
    /// <para>
    /// <b>Both the timestamp and the assertion were tightened in Phase 4c, and the loose version was
    /// the problem.</b> The body carried no <c>tappedAt</c>, so the server stamped it with the real
    /// clock while this file's fixture event is anchored on the frozen <c>TestData.Now</c> — outside
    /// its D-36 window. It passed only because the D-26 mismatch guard runs before the event is read,
    /// which this test neither asserts nor is about. And <c>NotFound or BadRequest</c> was too loose to
    /// notice if that stopped being true: <c>TappedAtOutsideEventWindow</c> is also a 400, so the test
    /// would have gone on passing while testing something else entirely. It now sends a timestamp
    /// inside the window and names the outcome it means.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tap_from_an_unregistered_device_is_not_a_500()
    {
        var world = await ArrangeAsync();

        var apiKey = await IssueDeviceKeyAsync(world.SchoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        // A body deviceId that is neither the authenticated device nor a registered one. It is a
        // DeviceMismatch before it is an unknown device (D-26 checks the principal first), and the
        // point of this test is unchanged either way: a stale id from a re-provisioned handset gets a
        // 4xx that stops §8.2's retry loop rather than a 5xx that feeds it.
        var response = await client.PostAsJsonAsync("/api/v1/attendance/tap", new
        {
            eventId = world.EventId,
            cardUid = Uid,
            deviceId = Guid.NewGuid(),
            deviceTapId = "queued-0001",
            tappedAt = TestData.Now,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DeviceMismatch", body.RootElement.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------ DEFECT 3

    /// <summary>
    /// <b>A device belonging to another school can record a tap.</b>
    ///
    /// <para>
    /// The tap path takes <c>DeviceId</c> from the request and writes it without checking that the
    /// device belongs to the event's school. Reproduced: a device owned by school B successfully
    /// recorded attendance on school A's event.
    /// </para>
    ///
    /// <para>
    /// Harmless in a single-tenant build and invisible today — which is exactly why it is worth
    /// pinning now. ADR-001 D-6 installed the tenant seam early on the argument that a missing
    /// tenant check cannot be retrofitted cheaply, and this is one: the <c>SchoolId</c> query filter
    /// guards <em>reads</em>, and nothing guards a foreign key supplied on a <em>write</em>.
    /// </para>
    ///
    /// <para>
    /// <b>CLOSED</b> (Phase 4b, D-27), and it took two changes rather than one because the thing that
    /// dissolves the problem is not the thing that closes this test.
    /// </para>
    ///
    /// <para>
    /// <b>What dissolves it:</b> device authentication. Once a key identifies the device, the
    /// <c>school_id</c> claim it carries <em>is</em> the tenant, so another school's event is not
    /// visible at all and a foreign tap is <c>EventNotFound</c> long before any device check runs.
    /// That is the ordinary path and it needs no ownership check.
    /// </para>
    ///
    /// <para>
    /// <b>What closes this test:</b> an explicit <c>device.SchoolId == ev.SchoolId</c> guard in
    /// <c>TapAsync</c>, before the write. This test arranges exactly the case the pipeline does not
    /// cover — an <em>unpinned</em> context, calling the service directly, which is also how the §10
    /// import path and any future worker will call it. The general principle DEFECT 2 left open still
    /// applies there: a tenant-owned foreign key arriving on a write must be validated explicitly, not
    /// by whatever query filter happens to be in force.
    /// </para>
    ///
    /// <para>
    /// The refusal reuses <c>DeviceNotRegistered</c> → 404 rather than introducing a 403. A distinct
    /// status would confirm to the caller that the device exists in some other school, which is a
    /// cross-tenant existence disclosure bought for no client benefit — and the published mobile
    /// contract already defines that token as "unknown device, or device belongs to another school".
    /// No new outcome, no wire change, and the assertion below is the one that was already written.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_device_from_another_school_cannot_record_a_tap()
    {
        var world = await ArrangeAsync();

        Guid foreignDeviceId;
        await using (var db = NewDbContext())
        {
            var otherSchool = TestData.NewSchool("CICSS");
            db.Schools.Add(otherSchool);
            var device = TestData.NewDevice(otherSchool.Id, "FOREIGN-DEVICE");
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            foreignDeviceId = device.Id;
        }

        await using var tap = NewDbContext();
        var response = await AttendanceOn(tap).TapAsync(
            new TapRequest(world.EventId, Uid, foreignDeviceId, "foreign-0001", TestData.Now));

        Assert.False(response.Result.Success);
    }

    // ------------------------------------------------------------------ DEFECT 4

    /// <summary>
    /// <b><c>POST /attendance/manual</c> accepts any string as a status.</b>
    ///
    /// <para>
    /// §4.9 defines <c>Status</c> as Present/Late/Absent/Excused. The controller declares
    /// <c>[FromQuery] string status = "Present"</c> and the service writes it unchecked. Reproduced:
    /// <c>status=Banana</c> was saved and returned as <c>Saved / stored=Banana</c>. A row like that
    /// is counted in no bucket of the event summary, so the totals silently stop reconciling.
    /// </para>
    ///
    /// <para>
    /// The same field over 20 characters throws <c>String or binary data would be truncated</c> from
    /// SQL Server — an unvalidated boundary producing a 500 rather than a 400. Both are the same
    /// missing check.
    /// </para>
    ///
    /// <para>
    /// <b>CLOSED.</b> §4.9's set is now a named domain type (<c>EAMS.Domain.AttendanceStatus</c>)
    /// and <c>ManualAsync</c> validates against it before reading or writing anything, returning
    /// <c>InvalidStatus</c> → 400. The guard is in the service rather than the controller so the
    /// next writer of this column inherits it. The wider behaviour — canonical casing, the
    /// over-length case, and the summary reconciliation this protects — is covered in
    /// <see cref="ManualOverrideTests"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Sibling defect, found later and also closed.</b> The over-length case above is about
    /// <c>status</c> (<c>nvarchar(20)</c>), which the set check rejects by construction. The review
    /// pass then found the <em>same class</em> of hole one parameter over: <c>notes</c> is
    /// <c>nvarchar(500)</c> and was written unchecked, so 501 characters reached SQL Server and
    /// returned error 2628 as a 500. A truncation is not a unique violation, so
    /// <c>SaveNewRecordAsync</c> correctly declined to swallow it. Now bounded by
    /// <c>EAMS.Domain.AttendanceNotes</c> in the service and by <c>[StringLength]</c> at the
    /// controller — see <see cref="ManualOverrideTests"/>. The lesson worth keeping: closing a
    /// value-set gap on one column says nothing about the column beside it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_manual_override_rejects_a_status_outside_the_documented_set()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).ManualAsync(world.EventId, world.StudentId, "Banana", null);

        Assert.NotEqual(ManualOutcome.Saved, response.Outcome);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.AttendanceRecords.CountAsync());
    }

    // ------------------------------------------------------------------ DEFECT 5

    /// <summary>
    /// <b>A check-out tap's <c>deviceTapId</c> is discarded.</b>
    ///
    /// <para>
    /// In <c>TimeInOut</c> mode the second tap sets <c>CheckOutAt</c> but leaves <c>DeviceTapId</c>
    /// holding the <em>check-in</em>'s id. Reproduced: after checking in with <c>in-1</c> and out
    /// with <c>out-1</c>, the stored id is still <c>in-1</c>, and replaying <c>out-1</c> returns
    /// <c>AlreadyRecorded</c> rather than <c>CheckedOut</c>.
    /// </para>
    ///
    /// <para>
    /// No data is currently lost — idempotency survives by accident, because <c>CheckOutAt</c> is no
    /// longer null. But it is <em>not</em> keyed on the identifier the plan publishes: §8.2 calls
    /// <c>deviceTapId</c> "the keystone of idempotency", and no unique index covers a check-out tap.
    /// A client that retries a timed-out check-out gets a different outcome and a different message
    /// than the plan describes, and any future change to the check-out branch has no constraint
    /// backing it up. Worth settling before Phase 4 publishes this contract externally.
    /// </para>
    ///
    /// <para>
    /// <b>CLOSED (Phase 4c, D-34), and rewritten rather than merely un-skipped</b> — the second of the
    /// two closures in this file that changed the assertion, and for the same reason DEFECT 1's
    /// <c>Status</c> half did: one of the original assertions turned out to be describing a fix that
    /// was not taken.
    /// </para>
    ///
    /// <para>
    /// <b>What was taken.</b> The problem is structural, not a missing assignment: one row, one
    /// <c>DeviceTapId</c> column, and two taps that each own a key. So the row gained a
    /// <em>second</em> column, <c>CheckOutDeviceTapId</c>, with its own filtered unique index — which
    /// is what the paragraph above is actually complaining about when it says no unique index covers a
    /// check-out tap and any future change has no constraint backing it up. Each half of the pair is
    /// now independently retryable.
    /// </para>
    ///
    /// <para>
    /// <b>What changed in the assertions, and why the original was wrong.</b> The final assertion —
    /// replaying the check-out returns <c>DuplicateIgnored</c> — is untouched and is exactly what this
    /// design produces. The <em>first</em> one (<c>record.DeviceTapId == "out-1"</c>) described the
    /// cheap fix, overwriting the check-in's id with the check-out's, and that fix is worse than the
    /// defect: it would repair the check-out's idempotency by destroying the check-in's, so a client
    /// retrying the first tap of the pair would find no record of it.
    /// <c>TapFlowTests.A_check_in_stays_replayable_after_its_check_out_has_landed</c> is the assertion
    /// that would fail under it. Both columns are asserted below instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_check_out_tap_is_idempotent_on_its_own_deviceTapId()
    {
        var world = await ArrangeAsync(attendanceMode: "TimeInOut");

        await using (var db = NewDbContext())
            await AttendanceOn(db).TapAsync(new TapRequest(world.EventId, Uid, null, "in-1", TestData.Now));

        await using (var db = NewDbContext())
        {
            var checkOut = await AttendanceOn(db).TapAsync(
                new TapRequest(world.EventId, Uid, null, "out-1", TestData.Now.AddHours(1)));

            // The outcome the original defect reported as AlreadyRecorded.
            Assert.Equal(TapOutcome.CheckedOut, checkOut.Outcome);
        }

        await using (var read = NewDbContext())
        {
            var record = await read.AttendanceRecords.AsNoTracking().SingleAsync();
            Assert.Equal("in-1", record.DeviceTapId);
            Assert.Equal("out-1", record.CheckOutDeviceTapId);
        }

        await using var replay = NewDbContext();
        var response = await AttendanceOn(replay).TapAsync(
            new TapRequest(world.EventId, Uid, null, "out-1", TestData.Now.AddHours(1)));

        Assert.Equal(TapOutcome.DuplicateIgnored, response.Outcome);
        Assert.Equal(TestData.Now.AddHours(1), response.Result.Record!.CheckOutAt);
    }

    // ------------------------------------------------------------------ GAP 6

    /// <summary>
    /// <b>The event summary's <c>Expected</c> is the recorded-row count, not the invited roster.</b>
    ///
    /// <para>
    /// Written to the documented intent rather than to current behaviour, per the brief's rule for
    /// ambiguity. §4.5 says <c>RequireRegistration</c> "drives … 'expected attendees' denominators in
    /// reports"; §12 defines the Absentee Report as "expected attendees with no tap" and the roster
    /// as "expected vs actual". Under all three readings, <c>Expected</c> is the invited population —
    /// which is what <c>EventGroups</c> (§4.8) exists to describe.
    /// </para>
    ///
    /// <para>
    /// Today <c>GetSummaryAsync</c> sets <c>expected = records.Count</c>, so an event with one
    /// Present and one Absent row reports <c>Expected = 2</c> and a 50% rate. That is self-consistent
    /// but it can never produce an absentee: a student who never taps has no row, so they are not
    /// counted as expected either, and the attendance rate is structurally incapable of falling
    /// below the share of rows that happen to be marked Absent by hand.
    /// </para>
    ///
    /// <para>
    /// The code says so itself ("No registration list in the core slice"), so this is a known
    /// limitation rather than a regression — but it is a published number, and §12's reports are
    /// built on it.
    /// </para>
    ///
    /// <para>
    /// <b>CLOSED.</b> <c>GetSummaryAsync</c> counts the denominator from §4.8 <c>EventGroups</c> —
    /// members of every attached group plus every individually attached student, de-duplicated in SQL
    /// with <c>UNION</c> and excluding the soft-deleted. The recorded-row count is gone, and with it
    /// the arithmetic that made an absentee impossible to represent. The test below was written to the
    /// documented intent before any of that existed and is unchanged: the same three assertions, now
    /// passing.
    /// </para>
    ///
    /// <para>
    /// The wider behaviour this protects is covered in <see cref="EventRosterTests"/> (de-duplication
    /// across two sections, the walk-in, the zero-audience case) and
    /// <see cref="EventCloseFreezeTests"/> (what the denominator does once the event closes).
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_event_summary_denominator_is_the_invited_roster()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var invitedButAbsent = TestData.NewStudent(world.SchoolId, "2023-0006", lastName: "Flores");
            db.Students.Add(invitedButAbsent);
            db.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentId = world.StudentId });
            db.EventGroups.Add(new EventGroup { EventId = world.EventId, StudentId = invitedButAbsent.Id });
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId,
                EventId = world.EventId, StudentId = world.StudentId,
                CheckInAt = TestData.Now, Status = "Present",
            });
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Expected); // both invited students, only one of whom tapped
        Assert.Equal(1, summary.Present);
        Assert.Equal(50, summary.AttendanceRate);
    }

    // ------------------------------------------------------------------ GAP 7

    /// <summary>
    /// <b><c>[HasPermissionNotEnforced]</c> is applied to nothing.</b>
    ///
    /// <para>
    /// ADR-001 D-6's whole argument for shipping an inert attribute is that endpoints get decorated
    /// "as they are written", so Phase 6 wires enforcement instead of auditing every controller to
    /// work out what each endpoint should have demanded. Nine actions across three controllers exist
    /// and none carries the attribute, so the audit D-6 was meant to avoid is still owed in full.
    /// </para>
    ///
    /// <para>
    /// Not a defect in the attribute — the seam works, as <see cref="ApiContractTests"/> shows
    /// against a decorated test endpoint. It is unfinished application of it, and it is cheap now
    /// and expensive later.
    /// </para>
    ///
    /// <para>
    /// <b>CLOSED.</b> All nine actions across the three controllers now carry the attribute, with the
    /// §6 code for that action. Nothing about the runtime changed — the attribute still implements no
    /// interface and denies nothing (<c>AuthorizationSeamTests</c> proves that structurally, and
    /// <c>ApiContractTests</c> proves it over HTTP) — but the audit D-6 was written to avoid is now
    /// paid off, and this test is what keeps it that way: a tenth action added without a declared
    /// permission fails the build's test gate instead of being noticed in Phase 6.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_api_action_declares_the_permission_it_will_require()
    {
        var actions = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes(inherit: true)
                .Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
            .ToList();

        // AuthController is exempt, and it is the only exemption. Its five actions have no §4.11
        // permission code to declare, because their access rules are not permissions: sign-in and
        // refresh must be reachable by a caller who holds nothing at all, and /me, /logout and
        // /change-password demand "any authenticated user", which is a scheme requirement rather than
        // a grant. Minting a permission code for them would put a row in Permissions that no role
        // could sensibly be denied and that the rename Phase 6's audit walks would have to skip
        // anyway. Named rather than filtered by attribute, so a SIXTH auth action still has to be
        // looked at here.
        string[] exempt =
        [
            "AuthController.ChangePassword",
            "AuthController.Login",
            "AuthController.Logout",
            "AuthController.Me",
            "AuthController.Refresh",
        ];

        Assert.NotEmpty(actions);

        var declaring = actions
            .Where(a => !exempt.Contains($"{a.DeclaringType?.Name}.{a.Name}", StringComparer.Ordinal))
            .ToList();

        Assert.Equal(
            actions.Count - exempt.Length,
            declaring.Count);

        Assert.All(declaring, action => Assert.NotEmpty(
            action.GetCustomAttributes(typeof(EAMS.Api.Authorization.HasPermissionNotEnforcedAttribute), true)));
    }
}
