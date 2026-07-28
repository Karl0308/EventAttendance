using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// Technical Plan §6.3 — the events read and write surface — plus the §6.7/§12 summary and roster.
///
/// <para>
/// <b>The one idea this class is built around: the expected roster is a query, until it is not.</b>
/// While an event is <c>Draft</c> or <c>Open</c>, "who is expected?" is answered by
/// <see cref="ExpectedStudentIds"/>, which reads through §4.8 <c>EventGroups</c> into current section
/// membership every time it is asked — so a student enrolled by a roster import the day before the
/// event is correctly expected, with nobody having to remember to re-attach anything. On the
/// transition to <c>Closed</c> that same query is run once more and its answer is <em>written down</em>
/// as <c>Absent</c> attendance rows. From then on the denominator is rows in a table rather than a
/// join across live data, and no later import can move it.
/// </para>
/// </summary>
internal sealed class EventService : IEventService
{
    /// <summary>
    /// How many times the close will re-diff and retry after losing an insert race to a tap. One retry
    /// is enough: the window is the microseconds between reading which students already have a record
    /// and committing the absentees, and taps stop the moment the <c>Closed</c> status commits. Bounded
    /// rather than looped so a genuinely unexpected violation surfaces as an error instead of spinning.
    /// </summary>
    private const int FreezeRetryLimit = 1;

    private readonly EamsDbContext _db;
    private readonly ISchoolContext _school;

    /// <summary>
    /// Who is organizing, and who closed the event. Null for the whole of the pre-auth build — see
    /// <see cref="ICurrentUser"/> for why the seam is wired before there is anything to read from it.
    /// Attribution is the one deferred thing that cannot be backfilled.
    /// </summary>
    private readonly ICurrentUser _currentUser;

    public EventService(EamsDbContext db, ISchoolContext school, ICurrentUser currentUser)
    {
        _db = db;
        _school = school;
        _currentUser = currentUser;
    }

    private static EventDto ToDto(Event e) => new(
        e.Id, e.Name, e.Description, e.Location, e.StartAt, e.EndAt,
        e.AttendanceMode, e.GraceMinutes, e.RequireRegistration, e.Status);

    // ------------------------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<EventDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var q = _db.Events.AsNoTracking().Where(e => !e.IsDeleted);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(e => e.Status == status);
        var list = await q.OrderByDescending(e => e.StartAt).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<EventDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        return e is null ? null : ToDto(e);
    }

    /// <summary>
    /// §6.7/§12's summary. Three round trips: the event, the four buckets, the denominator.
    ///
    /// <para>
    /// <b>It used to materialize every attendance row, tracked, to compute four integers.</b> On an
    /// institution-wide event that is tens of thousands of entities loaded into the change tracker to
    /// produce eight numbers — and the change tracking was pure cost, because nothing was written. The
    /// counts are now four conditional aggregates in a single statement over
    /// <c>IX_Attendance_EventId_Status</c>, and no row is materialized at all.
    /// </para>
    /// </summary>
    public async Task<EventSummaryDto?> GetSummaryAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (e is null) return null;

        var counts = await CountByStatusAsync(id, ct);
        var expected = await ExpectedStudentIds(id, e.Status).CountAsync(ct);

        return new EventSummaryDto(
            e.Id, e.Name, expected,
            counts.Present, counts.Late, counts.Absent, counts.Excused,
            RateOf(counts, expected));
    }

    /// <summary>The four §4.9 buckets for one event, as one aggregate.</summary>
    private readonly record struct StatusCounts(int Present, int Late, int Absent, int Excused);

    /// <summary>
    /// <c>SELECT COUNT(CASE …)</c> ×4 in a single statement.
    ///
    /// <para>
    /// <b>The comparisons moved from C# to SQL, and that changes one thing worth stating.</b> The old
    /// in-memory version compared with C# <c>==</c> — ordinal and case-sensitive — so a row stored as
    /// <c>"present"</c> landed in no bucket and the totals silently stopped reconciling. In SQL the
    /// comparison uses the database collation, which is case-insensitive by default, so such a row now
    /// lands in the Present bucket. That is strictly better, and it does not weaken anything:
    /// <c>AttendanceStatus.TryNormalize</c> still canonicalizes on every write, so the case is
    /// defence in depth rather than a licence to store whatever.
    /// </para>
    ///
    /// <para>
    /// A value outside §4.9's set entirely — the <c>"Banana"</c> that <c>KnownDefectTests</c> DEFECT 4
    /// records — is still counted by no bucket, exactly as before. It also cannot be written any more.
    /// </para>
    ///
    /// <para>
    /// <c>GroupBy(_ =&gt; 1)</c> is the EF idiom for "aggregate the whole set". It yields no row when
    /// the event has no attendance at all, which is why the result is nullable and falls back to zeros.
    /// </para>
    /// </summary>
    private async Task<StatusCounts> CountByStatusAsync(Guid eventId, CancellationToken ct)
    {
        var counts = await _db.AttendanceRecords
            .Where(a => a.EventId == eventId)
            .GroupBy(_ => 1)
            .Select(g => new StatusCounts(
                g.Count(a => a.Status == AttendanceStatus.Present),
                g.Count(a => a.Status == AttendanceStatus.Late),
                g.Count(a => a.Status == AttendanceStatus.Absent),
                g.Count(a => a.Status == AttendanceStatus.Excused)))
            .FirstOrDefaultAsync(ct);

        return counts;
    }

    private static double RateOf(StatusCounts counts, int expected) =>
        expected == 0 ? 0 : Math.Round((double)(counts.Present + counts.Late) / expected * 100, 1);

    // ------------------------------------------------------------------------- the denominator

    /// <summary>
    /// The expected attendees of one event — the denominator — as a composable query over §4.8
    /// <c>EventGroups</c>.
    ///
    /// <para>
    /// <b>It answers the same question two different ways depending on the event's status, and that is
    /// the whole design.</b> While the event is live the audience is <em>resolved</em>: group rows are
    /// followed into current section membership, so a student enrolled by an import tomorrow is
    /// expected tomorrow. Once the event is <c>Closed</c> the audience is <em>read</em>: only the
    /// individual student rows count, and the close is what writes them.
    /// </para>
    ///
    /// <para>
    /// <b>Materializing the Absent records alone does not freeze anything, which is the mistake this
    /// signature exists to make impossible.</b> Absent rows fix the <em>absentee list</em>; they say
    /// nothing about the denominator, because a denominator computed by following group rows keeps
    /// moving with the group. An event closed with 40 expected and 38 present quietly became 41
    /// expected the moment the next import added somebody to the section — the rate dropped, the
    /// absentee list did not change to explain it, and nothing anywhere recorded that it had happened.
    /// <see cref="SnapshotAudienceAsync"/> is the other half.
    /// </para>
    ///
    /// <para>
    /// <b>Soft-deleted students are excluded from the live query and <em>not</em> from the frozen
    /// one.</b> Live, a student the roster says does not exist cannot be expected to attend — the same
    /// reasoning that closed <c>KnownDefectTests</c> DEFECT 1 on the capture path. Frozen, the opposite
    /// applies with more force: a student deleted next year must not retroactively shrink a past
    /// event's denominator. "Frozen" has to mean frozen against every later edit, not only against
    /// enrollment.
    /// </para>
    ///
    /// <para>
    /// Returned as <c>IQueryable</c> and never materialized here: the summary only needs
    /// <c>COUNT(*)</c> over it, and pulling a section's worth of GUIDs into memory to count them is the
    /// same mistake this class just removed from the bucket counts.
    /// </para>
    /// </summary>
    private IQueryable<Guid> ExpectedStudentIds(Guid eventId, string status) =>
        status == EventStatus.Closed
            ? AttachedStudentIds(eventId, includeDeleted: true)
            : GroupMemberStudentIds(eventId).Union(AttachedStudentIds(eventId, includeDeleted: false));

    /// <summary>
    /// Students reached through an attached group's current membership. The live half.
    ///
    /// <para>
    /// <b>The de-duplication is <c>UNION</c> at the call site, and it is the point.</b> Twelve of the
    /// fifty-two students in the real roster sit in more than one section (ADR-001 D-2), so attaching
    /// two sections of the same programme double-counts every one of them under a naive join —
    /// inflating the denominator and depressing the rate by an amount nobody could explain. LINQ's
    /// <c>Union</c> compiles to SQL <c>UNION</c>, which is distinct by definition; <c>Concat</c> would
    /// compile to <c>UNION ALL</c> and reintroduce exactly that.
    /// </para>
    /// </summary>
    private IQueryable<Guid> GroupMemberStudentIds(Guid eventId) =>
        _db.EventGroups
            .Where(eg => eg.EventId == eventId && eg.StudentGroupId != null)
            .SelectMany(eg => eg.StudentGroup!.Members)
            .Where(m => !m.Student!.IsDeleted)
            .Select(m => m.StudentId);

    /// <summary>
    /// Students named individually on §4.8 rows. Both the ones an organizer attached by hand and — once
    /// the event is closed — the entire frozen roster written by <see cref="SnapshotAudienceAsync"/>.
    /// </summary>
    private IQueryable<Guid> AttachedStudentIds(Guid eventId, bool includeDeleted) =>
        _db.EventGroups
            .Where(eg => eg.EventId == eventId
                      && eg.StudentId != null
                      && (includeDeleted || !eg.Student!.IsDeleted))
            .Select(eg => eg.StudentId!.Value);

    // ------------------------------------------------------------------------------ the roster

    /// <summary>
    /// §6.3's "expected vs present roster", in one round trip after the event lookup.
    ///
    /// <para>
    /// <b>It lists the union of the expected and the recorded, not just the expected.</b> A student who
    /// tapped without being invited has an attendance row that the summary counts; leaving them off the
    /// roster would produce a page whose lines do not add up to the totals printed above them, which is
    /// the same class of plausible-but-wrong number this phase exists to remove. They appear flagged
    /// <c>IsExpected = false</c>.
    /// </para>
    ///
    /// <para>
    /// <b>No <c>IsDeleted</c> filter on the recorded half, deliberately.</b> The expected half already
    /// excludes soft-deleted students, so they cannot enter the denominator. But a student soft-deleted
    /// <em>after</em> being recorded still has a row the summary counts, and hiding it here would make
    /// the roster stop reconciling with the summary for a reason nobody looking at either could see.
    /// </para>
    ///
    /// <para>
    /// The attendance lookup is a correlated subquery per student rather than a second round trip and a
    /// client-side join, and it matches <c>OccurrenceId == null</c> for the reason
    /// <c>AttendanceService.FindByEventStudentAsync</c> records: that is the shape of
    /// <c>UX_Attendance_Event_Student_Occurrence</c>, and omitting the term would start matching the
    /// wrong rows the day §4.6 occurrences are populated.
    /// </para>
    /// </summary>
    public async Task<EventRosterDto?> GetRosterAsync(Guid id, CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (ev is null) return null;

        var expected = ExpectedStudentIds(id, ev.Status);
        var recorded = _db.AttendanceRecords.Where(a => a.EventId == id).Select(a => a.StudentId);

        var rows = await _db.Students.AsNoTracking()
            .Where(s => expected.Contains(s.Id) || recorded.Contains(s.Id))
            .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.StudentNumber)
            .Select(s => new
            {
                s.Id,
                s.StudentNumber,
                s.FirstName,
                s.MiddleName,
                s.LastName,
                s.Section,
                IsExpected = expected.Contains(s.Id),
                Record = _db.AttendanceRecords
                    .Where(a => a.EventId == id && a.StudentId == s.Id && a.OccurrenceId == null)
                    .Select(a => new { a.Status, a.CheckInAt, a.CheckOutAt, a.CaptureMethod })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var entries = rows.Select(r => new EventRosterEntryDto(
            r.Id, r.StudentNumber,
            string.Join(' ', new[] { r.FirstName, r.MiddleName, r.LastName }
                .Where(p => !string.IsNullOrWhiteSpace(p))),
            r.Section, r.IsExpected,
            r.Record?.Status, r.Record?.CheckInAt, r.Record?.CheckOutAt, r.Record?.CaptureMethod))
            .ToList();

        return new EventRosterDto(
            ev.Id, ev.Name, ev.Status,
            IsFrozen: ev.Status == EventStatus.Closed,
            Expected: entries.Count(e => e.IsExpected),
            Present: entries.Count(e => e.Status == AttendanceStatus.Present),
            Late: entries.Count(e => e.Status == AttendanceStatus.Late),
            Absent: entries.Count(e => e.Status == AttendanceStatus.Absent),
            Excused: entries.Count(e => e.Status == AttendanceStatus.Excused),
            NotRecorded: entries.Count(e => e.IsExpected && e.Status is null),
            entries);
    }

    // ------------------------------------------------------------------------------- create/edit

    public async Task<EventWriteResponse> CreateAsync(
        EventWriteRequest request, CancellationToken ct = default)
    {
        if (Validate(request) is { } invalid) return invalid;

        var schoolId = await ResolveSchoolIdAsync(ct);
        if (schoolId is null)
        {
            return new EventWriteResponse(EventWriteOutcome.NoSchoolResolved,
                "No school could be resolved for this event. In the pre-auth build the tenant is the " +
                "single seeded school (ADR-001 D-6); with none or several, there is nothing to file " +
                "the event under.", null);
        }

        var ev = new Event
        {
            SchoolId = schoolId.Value,
            // §4.5's default, and the only status a new event can have. See EventWriteRequest for why
            // the caller does not get to choose: PATCH /status is the sole door into the column, and
            // that is what makes the roster freeze unskippable.
            Status = EventStatus.Draft,
            // §4.5 calls this the responsible organizer. Null for the whole of the pre-auth build and
            // permanently null for these rows — nothing can attribute them retroactively, which is
            // exactly why the seam is read now rather than in Phase 6. See ICurrentUser.
            OrganizerUserId = _currentUser.UserId,
        };
        Apply(request, ev);

        _db.Events.Add(ev);
        await _db.SaveChangesAsync(ct);

        return Saved(ev, "Event created.");
    }

    public async Task<EventWriteResponse> UpdateAsync(
        Guid id, EventWriteRequest request, CancellationToken ct = default)
    {
        if (Validate(request) is { } invalid) return invalid;

        var ev = await FindAsync(id, ct);
        if (ev is null) return NotFound();

        if (!EventStatusTransition.AcceptsEdits(ev.Status))
        {
            return new EventWriteResponse(EventWriteOutcome.EventLocked,
                $"Event is {ev.Status}. Its window and grace period decided Present versus Late for " +
                "every row already recorded, so editing them now would change what those rows mean " +
                "without changing the rows.", null);
        }

        Apply(request, ev);
        ev.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Saved(ev, "Event updated.");
    }

    /// <summary>
    /// Soft delete (§4.5 <c>IsDeleted</c>). Allowed from any status, including <c>Closed</c>: the
    /// attendance rows survive untouched, so nothing is lost and the event can be restored by clearing
    /// one flag. A second delete is a 404 — by then the event is invisible to every read in the system,
    /// and reporting success for a row the caller can no longer see would be the misleading answer.
    /// </summary>
    public async Task<EventWriteResponse> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var ev = await FindAsync(id, ct);
        if (ev is null) return NotFound();

        ev.IsDeleted = true;
        ev.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new EventWriteResponse(EventWriteOutcome.Saved, "Event deleted.", ToDto(ev));
    }

    // ------------------------------------------------------------------------------- status

    public async Task<EventWriteResponse> ChangeStatusAsync(
        Guid id, string status, CancellationToken ct = default)
    {
        if (!EventStatus.TryNormalize(status, out var target))
        {
            return Invalid(
                $"Status '{status}' is not one of {string.Join(", ", EventStatus.All)}.");
        }

        var ev = await FindAsync(id, ct);
        if (ev is null) return NotFound();

        var current = ev.Status;

        // The no-op arm, and it must come before anything else. A PATCH retried after a timeout names
        // the status the event already reached; re-running the close from here would insert an Absent
        // row for every student who has joined an attached section since, which is the precise movement
        // the freeze exists to prevent, arriving through the mechanism meant to stop it.
        if (string.Equals(current, target, StringComparison.Ordinal))
            return Saved(ev, $"Event is already {target}.");

        if (!EventStatusTransition.IsAllowed(current, target))
        {
            var allowed = EventStatusTransition.AllowedFrom(current);
            return new EventWriteResponse(EventWriteOutcome.IllegalTransition,
                allowed.Count == 0
                    ? $"Event is {current}, which is terminal — no status change is possible. " +
                      "Correct an individual student with POST /attendance/manual instead; that path " +
                      "is audited and does not move the denominator."
                    : $"Event is {current}. It can only become {string.Join(" or ", allowed)}, " +
                      $"not {target}.",
                null);
        }

        ev.Status = target;
        ev.UpdatedAt = DateTime.UtcNow;

        if (!EventStatusTransition.FreezesRoster(current, target))
        {
            await _db.SaveChangesAsync(ct);
            return Saved(ev, $"Event is now {target}.");
        }

        var frozen = await CloseAndFreezeAsync(ev, ct);
        return Saved(ev,
            $"Event is now {target}. {frozen} expected {(frozen == 1 ? "attendee" : "attendees")} " +
            "with no record were marked Absent; this event's denominator and absentee list are now " +
            "fixed.");
    }

    /// <summary>
    /// Writes the status change and the materialized absentees as one unit.
    ///
    /// <para>
    /// <b>There is no explicit transaction, and that is not an oversight.</b> A single
    /// <c>SaveChangesAsync</c> is already one transaction, so the <c>Closed</c> status and every
    /// <c>Absent</c> row commit together or not at all — an event cannot end up closed with a
    /// half-frozen roster. Opening one by hand would additionally have to be threaded through
    /// <c>CreateExecutionStrategy</c>, because the production registration enables retry-on-failure and
    /// EF refuses user-initiated transactions under a retrying strategy; that is real complexity bought
    /// for nothing.
    /// </para>
    ///
    /// <para>
    /// <b>What the missing transaction does leave open is a read-then-write race,</b> and that is
    /// handled where it happens. Between reading which students already have a record and committing,
    /// a tap can insert one — the organizer closes the event while the last student is walking through
    /// the reader, which is the ordinary case rather than an exotic one. The insert then loses to
    /// <c>UX_Attendance_Event_Student_Occurrence</c>. Losing is not an error: the row we were about to
    /// write as <c>Absent</c> now exists as <c>Present</c>, which is the truer record. So the pending
    /// absentees are dropped, the diff is recomputed against what is now in the table, and the save is
    /// retried once.
    /// </para>
    /// </summary>
    private async Task<int> CloseAndFreezeAsync(Event ev, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var staged = await StageFrozenRosterAsync(ev.Id, ct);
            try
            {
                await _db.SaveChangesAsync(ct);
                return staged.Absentees;
            }
            catch (DbUpdateException ex)
                when (attempt < FreezeRetryLimit && SqlServerErrors.IsUniqueViolation(ex))
            {
                // EF leaves a failed insert Added, so without this the next SaveChanges would retry the
                // very rows that just collided — and a tracked Added row with the same key would also
                // shadow the re-read through identity resolution. The same detach the tap flow's
                // ResolveLostInsertRaceAsync performs, for the same reason.
                foreach (var entity in staged.Pending)
                    _db.Entry(entity).State = EntityState.Detached;
            }
        }
    }

    /// <summary>What one attempt at the freeze staged, so a lost race can detach exactly that.</summary>
    private readonly record struct StagedFreeze(int Absentees, IReadOnlyList<object> Pending);

    /// <summary>
    /// Stages both halves of the freeze: the audience snapshot and the absentee records.
    ///
    /// <para>
    /// They are staged together and saved together because they are one statement about the event —
    /// "these people were expected, and these of them did not come". Committing the absentees without
    /// the snapshot would leave a closed event whose absentee list is fixed and whose denominator still
    /// drifts, which is a worse state than either alone: the two would disagree, and the disagreement
    /// would look like an arithmetic bug rather than a missing write.
    /// </para>
    /// </summary>
    private async Task<StagedFreeze> StageFrozenRosterAsync(Guid eventId, CancellationToken ct)
    {
        var pending = new List<object>();

        var expected = await SnapshotAudienceAsync(eventId, pending, ct);
        var absentees = await StageAbsenteesAsync(eventId, expected, pending, ct);

        return new StagedFreeze(absentees, pending);
    }

    /// <summary>
    /// Resolves the live audience once and writes it down as individual §4.8 rows.
    ///
    /// <para>
    /// <b>This is what actually freezes the denominator.</b> After it, "who was expected?" is a set of
    /// rows rather than a walk through group membership that keeps changing —
    /// <see cref="ExpectedStudentIds"/> reads only these rows for a closed event.
    /// </para>
    ///
    /// <para>
    /// <b>The group rows are deliberately kept.</b> Replacing them would be tidier and would lose the
    /// only record of <em>which sections</em> were invited — the question §4.8 exists to answer, and
    /// the one <c>StudentGroupProjection</c> refuses to delete groups in order to preserve. So a closed
    /// event carries both: the group rows say what the organizer chose, the student rows say who that
    /// resolved to at the moment it stopped mattering. They cannot contradict each other because
    /// nothing reads the group rows for a closed event.
    /// </para>
    ///
    /// <para>
    /// Individually-attached students are already present as rows, so only the ones reached through a
    /// group are added; <c>UX_EventGroups_Event_Student</c> would reject a second copy anyway, which is
    /// what makes this safe to retry.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Guid>> SnapshotAudienceAsync(
        Guid eventId, List<object> pending, CancellationToken ct)
    {
        var expected = await GroupMemberStudentIds(eventId)
            .Union(AttachedStudentIds(eventId, includeDeleted: false))
            .ToListAsync(ct);

        var alreadyAttached = await AttachedStudentIds(eventId, includeDeleted: true).ToListAsync(ct);
        var missing = expected.Except(alreadyAttached).ToList();

        foreach (var studentId in missing)
        {
            var row = new EventGroup { EventId = eventId, StudentId = studentId };
            _db.EventGroups.Add(row);
            pending.Add(row);
        }

        return expected;
    }

    /// <summary>
    /// Adds an <c>Absent</c> attendance row for every expected student who has none.
    ///
    /// <para>
    /// <b><c>CaptureMethod = Import</c>, not <c>Manual</c>.</b> §4.9's <c>Manual</c> means an organizer
    /// made a judgement about one student; these rows are a bulk derivation with no such judgement
    /// behind them, and labelling them <c>Manual</c> would make a real override indistinguishable from
    /// a materialized absence in every report that shows the column.
    /// </para>
    ///
    /// <para>
    /// <b><c>CheckInAt</c> stays null.</b> §4.9 defines it as "first tap"; these students did not tap.
    /// The same rule <c>AttendanceService.RecordsAnArrival</c> applies to a manual <c>Absent</c>:
    /// stamping a time would fabricate an observation.
    /// </para>
    ///
    /// <para>
    /// Beyond the freeze, this is what turns §12's Absentee Report from a set difference computed
    /// across three tables into <c>WHERE EventId = @e AND Status = 'Absent'</c> — an index seek on
    /// <c>IX_Attendance_EventId_Status</c>.
    /// </para>
    /// </summary>
    private async Task<int> StageAbsenteesAsync(
        Guid eventId, IReadOnlyList<Guid> expected, List<object> pending, CancellationToken ct)
    {
        if (expected.Count == 0) return 0;

        // OccurrenceId == null matches UX_Attendance_Event_Student_Occurrence exactly, which is the
        // index these inserts must not collide with. See AttendanceService.FindByEventStudentAsync.
        var recorded = await _db.AttendanceRecords
            .Where(a => a.EventId == eventId && a.OccurrenceId == null)
            .Select(a => a.StudentId)
            .ToListAsync(ct);

        var missing = expected.Except(recorded).ToList();

        foreach (var studentId in missing)
        {
            var record = new AttendanceRecord
            {
                EventId = eventId,
                StudentId = studentId,
                OccurrenceId = null,
                CheckInAt = null,
                Status = AttendanceStatus.Absent,
                CaptureMethod = CaptureMethod.Import,
                RecordedByUserId = _currentUser.UserId,
            };

            // Added through the DbSet rather than a navigation collection: the Entity base assigns an
            // Id in its initializer, and EF can classify such a graph member as an existing row and
            // turn the insert into an UPDATE that matches nothing. StudentGroupProjection.DiffMembers
            // records the same trap.
            _db.AttendanceRecords.Add(record);
            pending.Add(record);
        }

        return missing.Count;
    }

    // ----------------------------------------------------------------------------- the audience

    public async Task<EventAudienceResponse> AttachAudienceAsync(
        Guid id, EventAudienceRequest request, CancellationToken ct = default)
    {
        var ev = await FindAsync(id, ct);
        if (ev is null) return AudienceNotFound();

        if (!EventStatusTransition.AcceptsAudienceChanges(ev.Status))
            return AudienceLocked(ev.Status);

        var groupIds = Distinct(request.StudentGroupIds);
        var studentIds = Distinct(request.StudentIds);

        var groups = await _db.StudentGroups.AsNoTracking()
            // The SchoolId predicate is explicit rather than left to the global query filter, which is
            // inert whenever no tenant is pinned — design time, most of the test suite, any unseeded
            // start. StudentGroupProjection carries the same guard on every read for the same reason:
            // an audience is the one place a cross-tenant row would be silently laundered into a
            // denominator.
            .Where(g => groupIds.Contains(g.Id) && g.SchoolId == ev.SchoolId)
            .Select(g => new { g.Id, g.Name, g.SourceEntityType, g.SourceKey, g.TermId })
            .ToListAsync(ct);

        if (groups.Count != groupIds.Count)
        {
            var found = groups.Select(g => g.Id).ToHashSet();
            return UnknownReference("student group", groupIds.Where(g => !found.Contains(g)));
        }

        // The projection deliberately never creates this group — see StudentGroupProjection's note on
        // why (unspecified) is essential at the offering level and meaningless at the section level.
        // Rejected here anyway: a hand-written row is representable, and attaching it would put every
        // student whose section cell happened to be blank, across every programme, into one denominator.
        var notACohort = groups
            .Where(g => g.SourceEntityType == GroupSourceEntityType.Section
                     && g.SourceKey == AcademicKey.Unspecified)
            .ToList();

        if (notACohort.Count > 0)
        {
            return new EventAudienceResponse(EventWriteOutcome.NotACohort,
                $"{string.Join(", ", notACohort.Select(g => $"'{g.Name}'"))} is not a cohort. It is " +
                "the sentinel for offerings whose section was left blank in the roster, so its only " +
                "shared property is that a field was empty. Invite the specific course offering " +
                "instead.", null);
        }

        var students = await _db.Students.AsNoTracking()
            .Where(s => studentIds.Contains(s.Id) && s.SchoolId == ev.SchoolId && !s.IsDeleted)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (students.Count != studentIds.Count)
        {
            var found = students.ToHashSet();
            return UnknownReference("student", studentIds.Where(s => !found.Contains(s)));
        }

        var attached = await _db.EventGroups.AsNoTracking()
            .Where(eg => eg.EventId == id)
            .Select(eg => new { eg.StudentGroupId, eg.StudentId })
            .ToListAsync(ct);

        var attachedGroups = attached.Where(a => a.StudentGroupId != null)
            .Select(a => a.StudentGroupId!.Value).ToHashSet();
        var attachedStudents = attached.Where(a => a.StudentId != null)
            .Select(a => a.StudentId!.Value).ToHashSet();

        var newGroups = groupIds.Where(g => !attachedGroups.Contains(g)).ToList();
        var newStudents = studentIds.Where(s => !attachedStudents.Contains(s)).ToList();

        foreach (var groupId in newGroups)
            _db.EventGroups.Add(new EventGroup { EventId = id, StudentGroupId = groupId });
        foreach (var studentId in newStudents)
            _db.EventGroups.Add(new EventGroup { EventId = id, StudentId = studentId });

        if (newGroups.Count > 0 || newStudents.Count > 0)
            await _db.SaveChangesAsync(ct);

        var warnings = await TermWarningsAsync(ev.SchoolId, groups.Select(g => (g.Name, g.TermId)), ct);
        var expected = await ExpectedStudentIds(id, ev.Status).CountAsync(ct);

        return new EventAudienceResponse(EventWriteOutcome.Saved, "Audience updated.",
            new EventAudienceResultDto(
                id, newGroups.Count, newStudents.Count,
                groupIds.Count - newGroups.Count, studentIds.Count - newStudents.Count,
                expected, warnings));
    }

    /// <summary>
    /// Flags attached groups that belong to a term other than the school's current one.
    ///
    /// <para>
    /// A warning and not a refusal — <see cref="IEventService.AttachAudienceAsync"/> carries the full
    /// reasoning. In short: <c>Events</c> has no <c>TermId</c>, so this can only be measured against a
    /// flag that moves under the event's feet, and the same request would start failing for an event
    /// nobody touched.
    /// </para>
    ///
    /// <para>
    /// Silent when the school has no current term: there is nothing to compare against, and a warning
    /// that fires on every attach because the registrar has not flagged a term yet is a warning
    /// everybody learns to ignore.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> TermWarningsAsync(
        Guid schoolId, IEnumerable<(string Name, Guid? TermId)> groups, CancellationToken ct)
    {
        var scoped = groups.Where(g => g.TermId is not null).ToList();
        if (scoped.Count == 0) return [];

        var currentTermId = await _db.Terms.AsNoTracking()
            .Where(t => t.SchoolId == schoolId && t.IsCurrent)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (currentTermId is null) return [];

        return scoped
            .Where(g => g.TermId != currentTermId)
            .Select(g => $"'{g.Name}' belongs to a term that is not the current one. Its membership " +
                         "is that term's cohort, which is probably not who you meant to invite — the " +
                         "term is in the group's name. Detach it if it is wrong.")
            .ToList();
    }

    public Task<EventAudienceResponse> DetachGroupAsync(
        Guid id, Guid studentGroupId, CancellationToken ct = default) =>
        DetachAsync(id, eg => eg.StudentGroupId == studentGroupId, ct);

    public Task<EventAudienceResponse> DetachStudentAsync(
        Guid id, Guid studentId, CancellationToken ct = default) =>
        DetachAsync(id, eg => eg.StudentId == studentId, ct);

    private async Task<EventAudienceResponse> DetachAsync(
        Guid id,
        System.Linq.Expressions.Expression<Func<EventGroup, bool>> match,
        CancellationToken ct)
    {
        var ev = await FindAsync(id, ct);
        if (ev is null) return AudienceNotFound();

        if (!EventStatusTransition.AcceptsAudienceChanges(ev.Status))
            return AudienceLocked(ev.Status);

        // ExecuteDeleteAsync rather than load-then-remove: one statement, nothing tracked, and
        // EventGroups carries none of the guarded columns that make bypassing the change tracker
        // dangerous elsewhere in this model (the ADR-001 D-2 cache guard lives on Students).
        await _db.EventGroups.Where(eg => eg.EventId == id).Where(match).ExecuteDeleteAsync(ct);

        var expected = await ExpectedStudentIds(id, ev.Status).CountAsync(ct);

        return new EventAudienceResponse(EventWriteOutcome.Saved, "Audience updated.",
            new EventAudienceResultDto(id, 0, 0, 0, 0, expected, []));
    }

    // ------------------------------------------------------------------------------- plumbing

    private Task<Event?> FindAsync(Guid id, CancellationToken ct) =>
        _db.Events.FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted, ct);

    /// <summary>
    /// Which school a new event belongs to.
    ///
    /// <para>
    /// The pinned tenant when there is one. Otherwise the only school, if there is exactly one — the
    /// same rule <c>DependencyInjection.PinDevelopmentSchoolAsync</c> already applies at startup, so
    /// this agrees with what the log said rather than inventing a second answer. With zero or several
    /// and nothing pinned there is no honest choice, and guessing would file an event under a school at
    /// random; the caller gets <see cref="EventWriteOutcome.NoSchoolResolved"/> instead.
    /// </para>
    ///
    /// <para>
    /// Phase 6 makes the fallback dead code: the tenant arrives in the claims and an unauthenticated
    /// request never reaches here.
    /// </para>
    /// </summary>
    private async Task<Guid?> ResolveSchoolIdAsync(CancellationToken ct)
    {
        if (_school.CurrentSchoolId is { } pinned) return pinned;

        var candidates = await _db.Schools.AsNoTracking()
            .OrderBy(s => s.Code).Select(s => s.Id).Take(2).ToListAsync(ct);

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// §4.5's column rules, checked before anything is read or written. In the service rather than the
    /// controller for the reason <c>AttendanceService.ManualAsync</c> records: the service is what
    /// writes the columns, so a guard on the HTTP boundary alone would leave the next non-HTTP caller
    /// unprotected. Returns null when the request is good.
    /// </summary>
    private static EventWriteResponse? Validate(EventWriteRequest request)
    {
        if (!EventText.IsValidName(request.Name))
        {
            return Invalid(
                $"Name is required and must be {EventText.NameMaxLength} characters or fewer.");
        }

        if (!EventText.IsValidDescription(request.Description))
        {
            return Invalid(
                $"Description must be {EventText.DescriptionMaxLength} characters or fewer " +
                $"(got {request.Description!.Length}).");
        }

        if (!EventText.IsValidLocation(request.Location))
        {
            return Invalid(
                $"Location must be {EventText.LocationMaxLength} characters or fewer " +
                $"(got {request.Location!.Length}).");
        }

        if (!EventText.IsValidGraceMinutes(request.GraceMinutes))
        {
            return Invalid(
                $"GraceMinutes must be between {EventText.MinGraceMinutes} and " +
                $"{EventText.MaxGraceMinutes} (got {request.GraceMinutes}).");
        }

        if (request.AttendanceMode is not null
            && !string.IsNullOrWhiteSpace(request.AttendanceMode)
            && !AttendanceMode.TryNormalize(request.AttendanceMode, out _))
        {
            return Invalid(
                $"AttendanceMode '{request.AttendanceMode}' is not one of " +
                $"{string.Join(", ", AttendanceMode.All)}.");
        }

        // Compared after normalizing both, because the two can arrive with different Kinds from one
        // JSON body — "2026-08-01T09:00:00Z" and "2026-08-01T12:00:00+08:00" — and comparing those
        // raw is off by the server's offset. UtcTime records the whole trap.
        if (UtcTime.Normalize(request.EndAt) <= UtcTime.Normalize(request.StartAt))
        {
            return Invalid(
                "EndAt must be after StartAt. An event whose window is empty or inverted can never " +
                "be attended: the tap path decides Present versus Late from StartAt and the grace " +
                "period, so every arrival would be misjudged with nothing to explain it.");
        }

        return null;
    }

    /// <summary>
    /// Copies a validated request onto the entity. Deliberately does not touch <c>Status</c>,
    /// <c>SchoolId</c>, <c>OrganizerUserId</c> or <c>IsDeleted</c> — one method, so create and update
    /// cannot drift about which fields a caller owns.
    /// </summary>
    private static void Apply(EventWriteRequest request, Event ev)
    {
        ev.Name = request.Name.Trim();
        ev.Description = request.Description;
        ev.Location = request.Location;
        ev.StartAt = UtcTime.Normalize(request.StartAt);
        ev.EndAt = UtcTime.Normalize(request.EndAt);
        ev.GraceMinutes = request.GraceMinutes;
        ev.RequireRegistration = request.RequireRegistration;
        ev.AttendanceMode = AttendanceMode.TryNormalize(request.AttendanceMode, out var mode)
            ? mode
            : AttendanceMode.Single;
    }

    private static IReadOnlyList<Guid> Distinct(IReadOnlyList<Guid>? ids) =>
        ids is null ? [] : ids.Where(id => id != Guid.Empty).Distinct().ToList();

    private static EventWriteResponse Saved(Event ev, string message) =>
        new(EventWriteOutcome.Saved, message, ToDto(ev));

    private static EventWriteResponse NotFound() =>
        new(EventWriteOutcome.NotFound, "Event not found.", null);

    private static EventWriteResponse Invalid(string message) =>
        new(EventWriteOutcome.ValidationFailed, message, null);

    private static EventAudienceResponse AudienceNotFound() =>
        new(EventWriteOutcome.NotFound, "Event not found.", null);

    private static EventAudienceResponse AudienceLocked(string status) =>
        new(EventWriteOutcome.EventLocked,
            status == EventStatus.Closed
                ? "Event is Closed. Its expected roster has been materialized as attendance rows, so " +
                  "changing who was invited would contradict the records already written — that is " +
                  "what a closed event's numbers not moving means."
                : $"Event is {status}. Who was invited to it is a historical record, not a working " +
                  "list.",
            null);

    private static EventAudienceResponse UnknownReference(string kind, IEnumerable<Guid> ids) =>
        new(EventWriteOutcome.UnknownReference,
            $"No {kind} in this event's school matches {string.Join(", ", ids)}.", null);
}
