using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>POST /attendance/manual</c> — the organizer override (§6.4). It writes the same table, through
/// the same two unique indexes, as the tap flow, and it shipped with none of the tap flow's
/// defences: an inline event/student lookup that omitted <c>OccurrenceId</c>, and a bare
/// <c>SaveChangesAsync</c> with no unique-violation handling.
///
/// <para>
/// <b>Why that mattered more than a rare-race argument suggests.</b> The two writers collide in the
/// ordinary case, not a pathological one — an organizer overrides a student <em>because</em> that
/// student is at the reader, so the tap and the override are seconds apart by construction. The
/// failure was a 500 with the organizer's instruction discarded.
/// </para>
///
/// <para>
/// The race tests below do not rely on timing. They open the window explicitly with EF's
/// <c>SavingChanges</c> event, which fires after the service has done its lookup and before the
/// insert reaches SQL Server — so the conflicting row is guaranteed to land inside the window rather
/// than probably landing there. A timing-based version of this test would pass on a fast machine
/// while the defect was still present.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ManualOverrideTests : IntegrationTest
{
    public ManualOverrideTests(SqlServerFixture sql) : base(sql) { }

    private const string Uid = "04A7B8C9";

    private sealed record World(Guid SchoolId, Guid EventId, Guid StudentId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var student = TestData.NewStudent(school.Id);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, Uid));
        var ev = TestData.NewEvent(school.Id);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return new World(school.Id, ev.Id, student.Id);
    }

    private async Task<List<AttendanceRecord>> AllRecordsAsync()
    {
        await using var db = NewDbContext();
        return await db.AttendanceRecords.AsNoTracking().ToListAsync();
    }

    // ---------------------------------------------------------------- the lost insert race

    /// <summary>
    /// The defect, reproduced deterministically: a tap inserts the student's row while the override
    /// is mid-flight. Before the fix this was an unhandled <c>DbUpdateException</c> — HTTP 500, and
    /// the organizer's decision thrown away.
    ///
    /// <para>
    /// The assertion that carries the weight is the <em>stored status</em>, not the outcome. Simply
    /// not throwing would be easy to achieve by returning the winning row untouched, and that would
    /// report "saved" while silently discarding an instruction a human deliberately gave. An
    /// override that loses a race must still be an override, so it is applied to whichever row won —
    /// and <c>CaptureMethod</c> staying <c>Rfid</c> is what proves the surviving row really is the
    /// tap's rather than a second row quietly written alongside it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_override_that_loses_the_insert_race_still_applies_to_the_winning_row()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var raced = false;
        db.SavingChanges += (_, _) =>
        {
            if (raced) return; // Only the first save — the override's own follow-up save must land.
            raced = true;
            using var tap = NewDbContext();
            tap.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId,
                EventId = world.EventId,
                StudentId = world.StudentId,
                CheckInAt = TestData.Now,
                Status = AttendanceStatus.Present,
                CaptureMethod = CaptureMethod.Rfid,
            });
            tap.SaveChanges();
        };

        var response = await AttendanceOn(db).ManualAsync(
            world.EventId, world.StudentId, AttendanceStatus.Excused, "Medical");

        Assert.True(raced, "The race never fired, so this test proved nothing about it.");
        Assert.Equal(ManualOutcome.Saved, response.Outcome);
        Assert.True(response.Result.Success);

        var record = Assert.Single(await AllRecordsAsync());
        Assert.Equal(AttendanceStatus.Excused, record.Status);
        Assert.Equal("Medical", record.Notes);
        Assert.Equal(CaptureMethod.Rfid, record.CaptureMethod);
    }

    /// <summary>
    /// The same window, from the tap flow's side, to show the two paths now behave alike rather than
    /// one being defended and the other not. The tap loses to a manual row and reports
    /// <c>AlreadyRecorded</c> — a success carrying the row that exists — instead of throwing.
    /// </summary>
    [Fact]
    public async Task A_tap_that_loses_the_insert_race_to_an_override_reports_the_existing_record()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var raced = false;
        db.SavingChanges += (_, _) =>
        {
            if (raced) return;
            raced = true;
            using var manual = NewDbContext();
            manual.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId,
                EventId = world.EventId,
                StudentId = world.StudentId,
                CheckInAt = TestData.Now,
                Status = AttendanceStatus.Excused,
                CaptureMethod = CaptureMethod.Manual,
            });
            manual.SaveChanges();
        };

        var response = await AttendanceOn(db).TapAsync(
            new TapRequest(world.EventId, Uid, null, null, TestData.Now));

        Assert.True(raced, "The race never fired, so this test proved nothing about it.");
        Assert.Equal(TapOutcome.AlreadyRecorded, response.Outcome);
        Assert.True(response.Result.Success);
        Assert.NotNull(response.Result.Record);

        var record = Assert.Single(await AllRecordsAsync());
        Assert.Equal(AttendanceStatus.Excused, record.Status);
    }

    /// <summary>
    /// Both writers under real contention, with no interception — the belt to the deterministic
    /// tests' braces. Eight callers, half taps and half overrides, all released at once: every one
    /// must succeed and exactly one row must exist. The <see cref="TaskCompletionSource"/> gate is
    /// load-bearing for the same reason as in <c>TapFlowTests</c> — without it the tasks start as
    /// the enumerable is materialized and the first wins comfortably.
    /// </summary>
    [Fact]
    public async Task Taps_and_overrides_racing_each_other_all_succeed_and_write_exactly_one_record()
    {
        var world = await ArrangeAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = NewDbContext();
            var service = AttendanceOn(db);

            if (i % 2 == 0)
            {
                var tap = await service.TapAsync(
                    new TapRequest(world.EventId, Uid, null, $"tap-{i}", TestData.Now));
                return (tap.Result.Success, Describe: $"tap → {tap.Outcome}: {tap.Result.Message}");
            }

            var manual = await service.ManualAsync(
                world.EventId, world.StudentId, AttendanceStatus.Excused, $"override-{i}");
            return (manual.Result.Success, Describe: $"manual → {manual.Outcome}: {manual.Result.Message}");
        })).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.All(results, r => Assert.True(r.Success, $"A concurrent write failed with: {r.Describe}"));
        Assert.Single(await AllRecordsAsync());
    }

    // ------------------------------------------------------- the post-close correction path

    /// <summary>
    /// <b>An override still applies to a <c>Closed</c> event, and this is the assumption the terminal
    /// status rests on.</b>
    ///
    /// <para>
    /// <c>EventStatusTransition</c> has no edge out of <c>Closed</c>, and the reasoning it records for
    /// refusing a re-open is explicitly that this path remains open: "any individual student's status
    /// can still be corrected afterwards — deliberately, one row at a time, attributed through
    /// <c>RecordedByUserId</c>". Recorded as ADR-003 D-17, manual override is the sanctioned post-close
    /// mutation path and the entire counterweight to <c>Closed</c> being a dead end.
    /// </para>
    ///
    /// <para>
    /// Nothing proved it. <see cref="ManualAsync"/> checks that the event exists and is not deleted and
    /// never looks at its status, so the behaviour is correct today by omission rather than by
    /// intention — and a later, entirely plausible "tighten the override to require an open event"
    /// would turn the terminal status into a trap with no recovery route at all, while every test
    /// stayed green. This converts the assumption into something the suite defends.
    /// </para>
    ///
    /// <para>
    /// The correction is applied to the <c>Absent</c> row the close materialized, which is the real
    /// shape of the scenario: a student who was marked absent by the freeze turns out to have had a
    /// medical certificate.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_override_still_corrects_a_student_on_a_closed_event()
    {
        var world = await ArrangeAsync();

        Guid closerId;
        await using (var db = NewDbContext())
        {
            var user = TestData.NewUser(world.SchoolId, "registrar@test.local");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            closerId = user.Id;

            // Invite the student, so the close materializes an Absent row for them to correct.
            var attach = await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest(null, [world.StudentId]));
            Assert.Equal(EventWriteOutcome.Saved, attach.Outcome);
        }

        await using (var db = NewDbContext())
        {
            var closed = await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, closed.Outcome);
        }

        await using (var read = NewDbContext())
        {
            var frozen = Assert.Single(await read.AttendanceRecords.AsNoTracking().ToListAsync());
            Assert.Equal(AttendanceStatus.Absent, frozen.Status);
        }

        CurrentUser.UserId = closerId;

        await using (var db = NewDbContext())
        {
            var response = await AttendanceOn(db).ManualAsync(
                world.EventId, world.StudentId, AttendanceStatus.Excused, "Medical certificate.");

            Assert.Equal(ManualOutcome.Saved, response.Outcome);
            Assert.True(response.Result.Success);
        }

        var record = Assert.Single(await AllRecordsAsync());
        Assert.Equal(AttendanceStatus.Excused, record.Status);
        Assert.Equal("Medical certificate.", record.Notes);
        // §6.4 calls this endpoint audited, and an override on a closed event is precisely the action
        // somebody will later want explained.
        Assert.Equal(closerId, record.RecordedByUserId);

        // The correction moves the buckets, and the denominator does not move with it — which is what
        // makes this a safe recovery route rather than a hole in the freeze.
        await using var after = NewDbContext();
        var summary = await EventsOn(after).GetSummaryAsync(world.EventId);

        Assert.Equal(1, summary!.Expected);
        Assert.Equal(0, summary.Absent);
        Assert.Equal(1, summary.Excused);
    }

    /// <summary>
    /// The same path on a <c>Cancelled</c> event. Both terminal statuses need the recovery route, and
    /// cancelling now snapshots its audience — so this also pins that a correction does not disturb the
    /// frozen denominator.
    /// </summary>
    [Fact]
    public async Task An_override_still_corrects_a_student_on_a_cancelled_event()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            await EventsOn(db).AttachAudienceAsync(
                world.EventId, new EventAudienceRequest(null, [world.StudentId]));
            await EventsOn(db).ChangeStatusAsync(world.EventId, EventStatus.Cancelled);
        }

        await using (var db = NewDbContext())
        {
            var response = await AttendanceOn(db).ManualAsync(
                world.EventId, world.StudentId, AttendanceStatus.Present, "Attended the rescheduled run.");
            Assert.Equal(ManualOutcome.Saved, response.Outcome);
        }

        await using var after = NewDbContext();
        var summary = await EventsOn(after).GetSummaryAsync(world.EventId);

        Assert.Equal(1, summary!.Expected);
        Assert.Equal(1, summary.Present);
        Assert.Equal(0, summary.Unexpected);
    }

    // ---------------------------------------------------------------- the shared lookup

    /// <summary>
    /// The other half of the divergence. <c>FindByEventStudentAsync</c> matches
    /// <c>OccurrenceId == null</c> explicitly, and its comment says leaving that out "would silently
    /// start matching the wrong rows the day §4.6 occurrences are populated" — which is exactly what
    /// the override's own inline copy did.
    ///
    /// <para>
    /// So this arranges that day: a record already exists against a materialized occurrence. An
    /// override with no occurrence must not reach through and rewrite it — it belongs to a different
    /// session of the same event. Before the fix the occurrence's <c>Present</c> row was silently
    /// overwritten to <c>Excused</c> and no new row was written at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_override_does_not_reach_through_to_a_recurring_occurrences_record()
    {
        var world = await ArrangeAsync();

        Guid occurrenceId;
        await using (var db = NewDbContext())
        {
            var occurrence = new EventSchedule
            {
                EventId = world.EventId,
                OccurrenceStartAt = TestData.Now,
                OccurrenceEndAt = TestData.Now.AddHours(3),
            };
            db.EventSchedules.Add(occurrence);
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = world.SchoolId,
                EventId = world.EventId,
                StudentId = world.StudentId,
                OccurrenceId = occurrence.Id,
                CheckInAt = TestData.Now,
                Status = AttendanceStatus.Present,
                CaptureMethod = CaptureMethod.Rfid,
            });
            await db.SaveChangesAsync();
            occurrenceId = occurrence.Id;
        }

        await using var service = NewDbContext();
        var response = await AttendanceOn(service).ManualAsync(
            world.EventId, world.StudentId, AttendanceStatus.Excused, null);

        Assert.Equal(ManualOutcome.Saved, response.Outcome);

        var records = await AllRecordsAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(AttendanceStatus.Present, records.Single(r => r.OccurrenceId == occurrenceId).Status);
        Assert.Equal(AttendanceStatus.Excused, records.Single(r => r.OccurrenceId is null).Status);
    }

    // ---------------------------------------------------------------- §4.9's value set

    [Theory]
    [InlineData(AttendanceStatus.Present)]
    [InlineData(AttendanceStatus.Late)]
    [InlineData(AttendanceStatus.Absent)]
    [InlineData(AttendanceStatus.Excused)]
    public async Task Every_documented_status_is_accepted(string status)
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).ManualAsync(world.EventId, world.StudentId, status, null);

        Assert.Equal(ManualOutcome.Saved, response.Outcome);
        Assert.Equal(status, Assert.Single(await AllRecordsAsync()).Status);
    }

    /// <summary>
    /// A status outside §4.9's set writes nothing at all — the guard runs before the event and
    /// student are even read, so a bad status cannot half-succeed.
    /// </summary>
    [Theory]
    [InlineData("Banana")]
    [InlineData("Presnt")]
    [InlineData("")]
    public async Task A_status_outside_the_documented_set_is_rejected_and_writes_nothing(string status)
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).ManualAsync(world.EventId, world.StudentId, status, null);

        Assert.Equal(ManualOutcome.InvalidStatus, response.Outcome);
        Assert.False(response.Result.Success);
        Assert.Empty(await AllRecordsAsync());
    }

    /// <summary>
    /// The truncation half of the same defect. <c>Status</c> is <c>nvarchar(20)</c>, so this string
    /// used to reach SQL Server and come back as "String or binary data would be truncated" — a 500
    /// for a bad request. It is now rejected by the ordinary set check, before any SQL is issued.
    /// </summary>
    [Fact]
    public async Task A_status_longer_than_the_column_is_a_rejection_rather_than_a_truncation_error()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).ManualAsync(
            world.EventId, world.StudentId, new string('x', 500), null);

        Assert.Equal(ManualOutcome.InvalidStatus, response.Outcome);
        Assert.Empty(await AllRecordsAsync());
    }

    /// <summary>
    /// Casing is accepted liberally and stored canonically. This is not politeness: the summary
    /// buckets in <c>EventService</c> are counted in memory with C# <c>==</c>, which is ordinal, so
    /// a row stored as <c>"present"</c> would land in no bucket and reproduce the reconciliation
    /// failure the whole validation exists to prevent. SQL Server's case-insensitive collation would
    /// hide it from a <c>WHERE Status = 'Present'</c> spot-check.
    /// </summary>
    [Theory]
    [InlineData("present", AttendanceStatus.Present)]
    [InlineData("EXCUSED", AttendanceStatus.Excused)]
    [InlineData("  Late  ", AttendanceStatus.Late)]
    public async Task A_status_is_stored_in_its_canonical_spelling(string sent, string stored)
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).ManualAsync(world.EventId, world.StudentId, sent, null);

        Assert.Equal(ManualOutcome.Saved, response.Outcome);
        Assert.Equal(stored, Assert.Single(await AllRecordsAsync()).Status);
    }

    /// <summary>
    /// The invariant all of the above is in service of, asserted directly: every stored row is
    /// counted by exactly one summary bucket, so <c>Present + Late + Absent + Excused</c> reconciles
    /// with the number of records. A single <c>status=Banana</c> row used to break this silently —
    /// no error anywhere, just a total that no longer added up.
    /// </summary>
    [Fact]
    public async Task Every_status_the_override_can_store_lands_in_a_summary_bucket()
    {
        var world = await ArrangeAsync();

        var studentIds = new List<Guid> { world.StudentId };
        await using (var db = NewDbContext())
        {
            foreach (var (number, last) in new[] { ("2023-0002", "Cruz"), ("2023-0003", "Lim"), ("2023-0004", "Tan") })
            {
                var extra = TestData.NewStudent(world.SchoolId, number, lastName: last);
                db.Students.Add(extra);
                studentIds.Add(extra.Id);
            }
            await db.SaveChangesAsync();
        }

        foreach (var (studentId, status) in studentIds.Zip(AttendanceStatus.All))
        {
            await using var db = NewDbContext();
            var response = await AttendanceOn(db).ManualAsync(world.EventId, studentId, status, null);
            Assert.Equal(ManualOutcome.Saved, response.Outcome);
        }

        await using var read = NewDbContext();
        var summary = await EventsOn(read).GetSummaryAsync(world.EventId);

        Assert.NotNull(summary);
        Assert.Equal(
            (await AllRecordsAsync()).Count,
            summary!.Present + summary.Late + summary.Absent + summary.Excused);
    }

    // ---------------------------------------------------------------- notes length

    /// <summary>
    /// <c>Notes</c> is <c>nvarchar(500)</c> and was written unchecked, so 501 characters reached SQL
    /// Server and came back as error 2628 — a 500 on input the caller got wrong.
    ///
    /// <para>
    /// This is the same class of hole as the unvalidated <c>status</c>, one parameter over, and it
    /// survived that fix: closing a value-set gap on one column says nothing about the column beside
    /// it. The narrowed <c>IsUniqueViolation</c> catch was right to decline it — a truncation is not
    /// a race — which is exactly why it surfaced unhandled.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Notes_longer_than_the_column_are_rejected_rather_than_truncating()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).ManualAsync(
            world.EventId, world.StudentId, AttendanceStatus.Present,
            new string('x', AttendanceNotes.MaxLength + 1));

        Assert.Equal(ManualOutcome.InvalidNotes, response.Outcome);
        Assert.Empty(await AllRecordsAsync());
    }

    /// <summary>
    /// The boundary itself is valid — an off-by-one here would reject legitimate notes, which is a
    /// quieter failure than the 500 it replaced and would be blamed on the client.
    /// </summary>
    [Fact]
    public async Task Notes_exactly_at_the_column_length_are_accepted()
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var notes = new string('x', AttendanceNotes.MaxLength);
        var response = await AttendanceOn(db).ManualAsync(
            world.EventId, world.StudentId, AttendanceStatus.Present, notes);

        Assert.Equal(ManualOutcome.Saved, response.Outcome);
        Assert.Equal(notes, Assert.Single(await AllRecordsAsync()).Notes);
    }

    // ---------------------------------------------------------------- CheckInAt vs status

    /// <summary>
    /// §4.9 defines <c>CheckInAt</c> as "First tap (UTC)" and makes it nullable. <c>Absent</c> and
    /// <c>Excused</c> assert the student did <em>not</em> turn up, so stamping a check-in time on
    /// them produces a row that contradicts its own status — and the SPA both displays that time and
    /// orders the attendance list by it, so the contradiction is visible to end users.
    ///
    /// <para>
    /// Latent until <c>Absent</c> and <c>Excused</c> became reachable: before the value set existed
    /// nothing validated the status, so nothing drew attention to what the other two members meant.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(AttendanceStatus.Absent)]
    [InlineData(AttendanceStatus.Excused)]
    public async Task A_status_that_denies_attendance_stores_no_check_in_time(string status)
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).ManualAsync(world.EventId, world.StudentId, status, null);

        Assert.Equal(ManualOutcome.Saved, response.Outcome);
        Assert.Null(Assert.Single(await AllRecordsAsync()).CheckInAt);
    }

    /// <summary>
    /// The asymmetry, pinned on purpose. Overriding an <em>existing</em> row to <c>Absent</c> keeps
    /// its <c>CheckInAt</c>, unlike creating one.
    ///
    /// <para>
    /// This looks inconsistent with the two tests above and is not. The rule is about observation,
    /// not status: this row exists because a tap physically happened, and an organizer marking the
    /// student absent overrules what that tap <em>meant</em> — they are not asserting it never
    /// occurred. Erasing the timestamp would destroy the same evidence <c>RfidCardId</c> is retained
    /// for.
    /// </para>
    ///
    /// <para>
    /// Without this test, a reader who saw an <c>Absent</c> row carrying a check-in time would
    /// reasonably conclude it was a bug, null it in <c>ApplyOverride</c>, and lose audit evidence
    /// with the whole suite still green.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_override_to_absent_keeps_the_observed_check_in_time()
    {
        var world = await ArrangeAsync();

        await using (var seed = NewDbContext())
        {
            var tap = await AttendanceOn(seed).TapAsync(new TapRequest(world.EventId, Uid, null, "tap-1", null));
            Assert.True(tap.Result.Success);
        }

        var observed = Assert.Single(await AllRecordsAsync()).CheckInAt;
        Assert.NotNull(observed);

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).ManualAsync(
            world.EventId, world.StudentId, AttendanceStatus.Absent, "Left before the roll call.");

        Assert.Equal(ManualOutcome.Saved, response.Outcome);

        var stored = Assert.Single(await AllRecordsAsync());
        Assert.Equal(AttendanceStatus.Absent, stored.Status);
        Assert.Equal(observed, stored.CheckInAt);
        Assert.Equal(CaptureMethod.Rfid, stored.CaptureMethod);
    }

    /// <summary>The converse, so the fix above cannot pass by nulling every check-in time.</summary>
    [Theory]
    [InlineData(AttendanceStatus.Present)]
    [InlineData(AttendanceStatus.Late)]
    public async Task A_status_that_asserts_attendance_stores_a_check_in_time(string status)
    {
        var world = await ArrangeAsync();

        await using var db = NewDbContext();
        var response = await AttendanceOn(db).ManualAsync(world.EventId, world.StudentId, status, null);

        Assert.Equal(ManualOutcome.Saved, response.Outcome);
        var stored = Assert.Single(await AllRecordsAsync());
        Assert.NotNull(stored.CheckInAt);
        Assert.Equal(DateTimeKind.Utc, stored.CheckInAt!.Value.Kind);
    }
}
