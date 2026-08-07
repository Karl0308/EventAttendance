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

    /// <summary>
    /// How many times <see cref="AttachMissingAsync"/> will re-diff and retry after losing an insert to
    /// a concurrent post. One, for the same reason as <see cref="FreezeRetryLimit"/>: the window is the
    /// microseconds between reading what is attached and committing, and after one re-read the winner's
    /// rows are visible, so a second violation is not a race but a wrong assumption about which index
    /// fired — which should surface as an error rather than spin.
    /// </summary>
    private const int AttachRetryLimit = 1;

    private readonly EamsDbContext _db;
    private readonly ISchoolContext _school;

    /// <summary>
    /// Who is organizing, and who closed the event. Null for the whole of the pre-auth build — see
    /// <see cref="ICurrentUser"/> for why the seam is wired before there is anything to read from it.
    /// Attribution is the one deferred thing that cannot be backfilled.
    /// </summary>
    private readonly ICurrentUser _currentUser;

    /// <summary>
    /// The one tunable of the D-29 live endpoint. Read on every response so a configuration change
    /// takes effect on the next restart rather than on the next client release — see
    /// <see cref="AttendanceLiveOptions"/> for why the interval belongs to the server at all.
    /// </summary>
    private readonly AttendanceLiveOptions _live;

    public EventService(
        EamsDbContext db, ISchoolContext school, ICurrentUser currentUser, AttendanceLiveOptions live)
    {
        _db = db;
        _school = school;
        _currentUser = currentUser;
        _live = live;
    }

    private static EventDto ToDto(Event e) => new(
        e.Id, e.Name, e.Description, e.Location, e.StartAt, e.EndAt,
        e.AttendanceMode, e.GraceMinutes, e.RequireRegistration, e.Status);

    // ------------------------------------------------------------------------------------ reads

    /// <inheritdoc cref="IEventService.ListAsync"/>
    /// <remarks>
    /// Counted and paged through <c>PagedQuery.ToPageAsync</c>, the seam all nine admin lists share,
    /// so the total and the rows cannot end up answering different filters. <c>ThenBy(Id)</c> is the
    /// total order: an institution schedules several events at the same start instant (every 8:00 AM
    /// class-hour event), and equal sort keys are the one thing SQL Server may reorder between two
    /// executions of the same query.
    /// </remarks>
    public Task<PagedResult<EventDto>> ListAsync(
        string? status, PageRequest page, CancellationToken ct = default)
    {
        var q = _db.Events.AsNoTracking().Where(e => !e.IsDeleted);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(e => e.Status == status);

        return q.ToPageAsync(
            ordered => ordered.OrderByDescending(e => e.StartAt).ThenBy(e => e.Id),
            ToDto, page, ct);
    }

    public async Task<EventDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        return e is null ? null : ToDto(e);
    }

    /// <summary>
    /// §6.7/§12's summary. Five round trips: the event, the four buckets, the denominator, the
    /// numerator, and the walk-ins. Every one after the first is an aggregate — no attendance row is
    /// materialized at any point.
    ///
    /// <para>
    /// <b>It used to materialize every attendance row, tracked, to compute four integers.</b> On an
    /// institution-wide event that is tens of thousands of entities loaded into the change tracker to
    /// produce eight numbers — and the change tracking was pure cost, because nothing was written. The
    /// counts are now four conditional aggregates in a single statement over
    /// <c>IX_Attendance_EventId_Status</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The numerator is its own query rather than a subtraction, and that is what bounds the rate.</b>
    /// It used to be <c>counts.Present + counts.Late</c> — buckets counted over <em>every</em> row on
    /// the event, walk-ins included — divided by a denominator counting only the invited. Twenty-nine
    /// expected, twenty-nine present and two tapping alumni produced 106.9%: a rate above 100 with every
    /// underlying row truthful, reachable on an open event with taps alone, and the same class of
    /// plausible-but-wrong number this phase exists to remove. It also put the summary and the roster
    /// into open disagreement about one event, because <see cref="GetRosterAsync"/> had always counted
    /// the right thing.
    /// </para>
    /// </summary>
    public async Task<EventSummaryDto?> GetSummaryAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (e is null) return null;

        // No cursor here, so no ceiling: this endpoint publishes the counts as of now. See
        // SummaryForAsync for why the live path passes one and this deliberately does not.
        return await SummaryForAsync(e, ceiling: null, ct);
    }

    /// <summary>
    /// <inheritdoc cref="GetSummaryAsync" path="/summary/para[1]"/>
    ///
    /// <para>
    /// Split out so a caller that has <em>already</em> read the event does not read it again.
    /// <see cref="GetLiveAttendanceAsync"/> is that caller, and it is polled in a loop by every open
    /// dashboard — one redundant round trip on the hottest read in the API is worth removing, and the
    /// alternative (letting the live path build its own counters) is the duplication ADR-003 D-12/D-13
    /// record as failing silently.
    /// </para>
    /// </summary>
    /// <param name="ceiling">
    /// The highest <c>RowVersion</c> the attendance-derived counts may see, or <c>null</c> for "every
    /// committed row" (Phase 4e).
    ///
    /// <para>
    /// <b>A parameter rather than a second method, because the arithmetic underneath is the thing that
    /// must not be copied.</b> ADR-003 D-12/D-13 record that this denominator fails <em>silently</em>
    /// when it is duplicated: a second copy drifts and every number involved stays plausible. So the
    /// live path and <c>GET /events/{id}/summary</c> keep sharing one implementation and differ only in
    /// what they pass here.
    /// </para>
    ///
    /// <para>
    /// <b>Why the live path needs it.</b> The delta withholds every row above
    /// <c>MIN_ACTIVE_ROWVERSION() - 1</c>, because a <c>rowversion</c> is assigned when a row is
    /// written and not when its transaction commits. Counted without the same bound, <c>present</c>
    /// could include a row the delta deliberately held back, and the dashboard would show "5 present"
    /// over a list of four names. Normally that is one poll cycle of skew and heals itself — but
    /// <c>MIN_ACTIVE_ROWVERSION()</c> is <b>database-wide</b>, so one unrelated long transaction (a
    /// roster import, an event freeze) pins the ceiling while the counters go on advancing, and the
    /// discrepancy lasts as long as that transaction does.
    /// </para>
    ///
    /// <para>
    /// <b>Why <c>GET /events/{id}/summary</c> must keep passing <c>null</c>.</b> It has no cursor and
    /// hands out no cursor, so a ceiling there would silently lower a published number — an operator
    /// refreshing a report would watch counts move for reasons that have nothing to do with attendance.
    /// The two answers converge the moment any in-flight write commits.
    /// </para>
    ///
    /// <para>
    /// <b>It bounds the attendance-derived quantities only, and not the denominator.</b>
    /// <c>expected</c> is read from §4.8 <c>EventGroups</c> and section membership, which carry no
    /// <c>rowversion</c> and are not what the cursor is a cursor over. There is nothing to bound: an
    /// uncommitted audience change is invisible to this read anyway.
    /// </para>
    /// </param>
    private async Task<EventSummaryDto> SummaryForAsync(Event e, long? ceiling, CancellationToken ct)
    {
        var id = e.Id;
        var expectedIds = ExpectedStudentIds(id, e.Status);

        var counts = await CountByStatusAsync(id, ceiling, ct);
        var expected = await expectedIds.CountAsync(ct);
        var attended = await expectedIds.Intersect(AttendedStudentIds(id, ceiling)).CountAsync(ct);
        var unexpected = await RecordedStudentIds(id, ceiling).Except(expectedIds).CountAsync(ct);

        return new EventSummaryDto(
            e.Id, e.Name, expected,
            counts.Present, counts.Late, counts.Absent, counts.Excused, unexpected,
            RateOf(attended, expected));
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
    private async Task<StatusCounts> CountByStatusAsync(
        Guid eventId, long? ceiling, CancellationToken ct)
    {
        var counts = await AttendanceOn(eventId, ceiling)
            .GroupBy(_ => 1)
            .Select(g => new StatusCounts(
                g.Count(a => a.Status == AttendanceStatus.Present),
                g.Count(a => a.Status == AttendanceStatus.Late),
                g.Count(a => a.Status == AttendanceStatus.Absent),
                g.Count(a => a.Status == AttendanceStatus.Excused)))
            .FirstOrDefaultAsync(ct);

        return counts;
    }

    /// <summary>
    /// Students with a <c>Present</c> or <c>Late</c> record on this event, invited or not. Intersected
    /// with the expected set to form the rate's numerator.
    ///
    /// <para>
    /// The status comparison happens in SQL, so it uses the database collation and is case-insensitive
    /// by default — matching <see cref="CountByStatusAsync"/> exactly, which is the point. A row stored
    /// as <c>"present"</c> must land in the same place in both, or the rate and the buckets would
    /// disagree for a reason invisible in either.
    /// </para>
    /// </summary>
    private IQueryable<Guid> AttendedStudentIds(Guid eventId, long? ceiling) =>
        AttendanceOn(eventId, ceiling)
            .Where(a => a.Status == AttendanceStatus.Present || a.Status == AttendanceStatus.Late)
            .Select(a => a.StudentId);

    /// <summary>Every student with any attendance row on this event. <c>EXCEPT</c> the expected set gives the walk-ins.</summary>
    private IQueryable<Guid> RecordedStudentIds(Guid eventId, long? ceiling) =>
        AttendanceOn(eventId, ceiling).Select(a => a.StudentId);

    /// <summary>
    /// One event's attendance rows, optionally bounded by the live cursor's ceiling (Phase 4e).
    ///
    /// <para>
    /// <b>The single place the ceiling is applied.</b> Three aggregates feed the summary and the live
    /// counters are all three of them; applying the predicate at each call site would be three chances
    /// to forget one, and forgetting one is invisible — the number stays plausible, it is just counted
    /// over a different set than the rows beside it. See <see cref="SummaryForAsync"/> for what the
    /// ceiling is and why <c>GET /events/{id}/summary</c> passes <c>null</c>.
    /// </para>
    ///
    /// <para>
    /// The bounded form matches <c>IX_Attendance_EventId_RowVersion</c> — <c>EventId</c> equality then
    /// a <c>RowVersion</c> range — which is the same index the delta query uses.
    /// </para>
    /// </summary>
    private IQueryable<AttendanceRecord> AttendanceOn(Guid eventId, long? ceiling) =>
        ceiling is { } max
            ? _db.AttendanceRecords.Where(a => a.EventId == eventId && a.RowVersion <= max)
            : _db.AttendanceRecords.Where(a => a.EventId == eventId);

    /// <summary>
    /// <c>(invited students who attended) / (invited students)</c>, to one decimal place.
    ///
    /// <para>
    /// <b>The numerator is a count of people drawn from the denominator's own set</b>, produced by
    /// <c>Intersect</c> — SQL <c>INTERSECT</c>, distinct on both sides — so it cannot exceed
    /// <paramref name="expected"/> however many rows exist or how they are shaped. That is why there is
    /// no clamp here: a <c>Math.Min</c> would have capped a number that was still being computed wrongly
    /// and hidden the walk-ins that made it wrong. They are reported separately instead.
    /// </para>
    /// </summary>
    private static double RateOf(int attended, int expected) =>
        expected == 0 ? 0 : Math.Round((double)attended / expected * 100, 1);

    // ------------------------------------------------------------------------- the denominator

    /// <summary>
    /// The expected attendees of one event — the denominator — as a composable query over §4.8
    /// <c>EventGroups</c>.
    ///
    /// <para>
    /// <b>It answers the same question two different ways depending on the event's status, and that is
    /// the whole design.</b> While the event is live the audience is <em>resolved</em>: group rows are
    /// followed into current section membership, so a student enrolled by an import tomorrow is
    /// expected tomorrow. Once the event reaches a terminal status the audience is <em>read</em>: only
    /// the individual student rows count, and the transition is what writes them.
    /// </para>
    ///
    /// <para>
    /// <b>Both terminal statuses, not only <c>Closed</c>.</b> <c>Cancelled</c> used to take the live
    /// branch, so an <c>Open → Cancelled</c> event that had already taken taps carried a denominator
    /// that walked with every later import — a rate changing month over month for an event that is
    /// over, and no absentee list to explain it. See <c>EventStatusTransition.SnapshotsAudience</c>.
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
        EventStatusTransition.HasFrozenAudience(status)
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

    // ---------------------------------------------------------------------------- the audience read

    /// <summary>
    /// <inheritdoc cref="IEventService.GetAudienceAsync" path="/summary/para[1]"/>
    ///
    /// <para>
    /// <b>Three round trips, two of them lists that are bounded by what an organizer attached</b> — a
    /// handful of groups and, on a live event, a handful of hand-picked students. The unbounded case is
    /// the one deliberately not served: see <see cref="EventAudienceDto.Students"/>.
    /// </para>
    ///
    /// <para>
    /// <b>The <c>SchoolId</c> predicates are explicit, exactly as
    /// <see cref="AttachAudienceAsync"/>'s are and for the same reason.</b> <c>EventGroups</c> carries no
    /// <c>SchoolId</c> and reaches tenancy through <c>Event</c> (ADR-003 D-12), and the §11 global filter
    /// is inert whenever no tenant is pinned — design time, most of the test suite, any unseeded start.
    /// Attaching a cross-school group is already refused, so a row that fails these predicates can only
    /// be hand-written; the choice here is between disclosing another school's group and student names
    /// through a route that has no way to know it is doing so, and omitting a row that should not exist.
    /// The disclosure is the worse failure.
    /// </para>
    /// </summary>
    public async Task<EventAudienceDto?> GetAudienceAsync(Guid id, CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (ev is null) return null;

        var isFrozen = EventStatusTransition.HasFrozenAudience(ev.Status);

        var groups = await _db.EventGroups.AsNoTracking()
            .Where(eg => eg.EventId == id
                      && eg.StudentGroupId != null
                      && eg.StudentGroup!.SchoolId == ev.SchoolId)
            // Ordered before the projection so the sort is the database's. ThenBy(Id) is the total
            // order for the reason StudentGroupService records: group names are unique per
            // (school, source, term) and not globally, and equal sort keys are what a client sees
            // reorder between two identical requests.
            .OrderBy(eg => eg.StudentGroup!.Name).ThenBy(eg => eg.StudentGroupId)
            .Select(eg => new EventAudienceGroupDto(
                eg.StudentGroupId!.Value,
                eg.StudentGroup!.Name,
                eg.StudentGroup.Type,
                eg.StudentGroup.SourceType,
                eg.StudentGroup.TermId,
                // Optional navigation — null on a manual group, which belongs to no term.
                eg.StudentGroup.Term == null ? null : eg.StudentGroup.Term.Code,
                // A correlated subquery count with the soft-deleted excluded, which is
                // StudentGroupService.ListAsync's definition character for character. The picker and
                // this panel show the same group; if the two counted differently, an organizer would
                // watch "80 students" become "78 students" on attaching it and have nothing to read
                // that explains the change.
                eg.StudentGroup.Members.Count(m => !m.Student!.IsDeleted)))
            .ToListAsync(ct);

        // Empty by contract once the audience is snapshotted — the whole population would be here
        // otherwise. EventAudienceDto.Students carries the reasoning; IsFrozen is what tells a reader
        // which of the two cases this response is.
        IReadOnlyList<EventAudienceStudentDto> students = [];
        if (!isFrozen)
        {
            var rows = await _db.EventGroups.AsNoTracking()
                .Where(eg => eg.EventId == id
                          && eg.StudentId != null
                          && eg.Student!.SchoolId == ev.SchoolId)
                .OrderBy(eg => eg.Student!.LastName)
                    .ThenBy(eg => eg.Student!.FirstName)
                    .ThenBy(eg => eg.Student!.StudentNumber)
                .Select(eg => new
                {
                    StudentId = eg.StudentId!.Value,
                    eg.Student!.StudentNumber,
                    eg.Student.FirstName,
                    eg.Student.MiddleName,
                    eg.Student.LastName,
                    eg.Student.Section,
                })
                .ToListAsync(ct);

            students = rows.Select(r => new EventAudienceStudentDto(
                r.StudentId, r.StudentNumber,
                string.Join(' ', new[] { r.FirstName, r.MiddleName, r.LastName }
                    .Where(p => !string.IsNullOrWhiteSpace(p))),
                r.Section))
                .ToList();
        }

        // The one denominator query, not a fourth opinion of it — see IEventService.GetAudienceAsync.
        var expected = await ExpectedStudentIds(id, ev.Status).CountAsync(ct);

        return new EventAudienceDto(ev.Id, ev.Status, isFrozen, expected, groups, students);
    }

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
            IsFrozen: EventStatusTransition.HasFrozenAudience(ev.Status),
            Expected: entries.Count(e => e.IsExpected),
            Present: entries.Count(e => e.Status == AttendanceStatus.Present),
            Late: entries.Count(e => e.Status == AttendanceStatus.Late),
            Absent: entries.Count(e => e.Status == AttendanceStatus.Absent),
            Excused: entries.Count(e => e.Status == AttendanceStatus.Excused),
            NotRecorded: entries.Count(e => e.IsExpected && e.Status is null),
            // The same people GetSummaryAsync counts as Unexpected, arrived at from the other side: it
            // runs recorded EXCEPT expected in SQL, this counts the rows that survived the same test in
            // the projection above. The two must agree or the roster and the summary describe different
            // events, which is the disagreement CONDITION 1 removed from the rate.
            Unexpected: entries.Count(e => !e.IsExpected),
            entries);
    }

    // ------------------------------------------------------------------- live attendance (D-29/D-30)

    /// <summary>
    /// <inheritdoc cref="IEventService.GetLiveAttendanceAsync" path="/summary/para[1]"/>
    ///
    /// <para>
    /// <b>The two modes are one query with one extra predicate.</b> A snapshot is every row of the event
    /// up to the ceiling; a delta is the same thing with a floor added. Writing them as one method is
    /// what guarantees a client that reconnects and re-snapshots sees exactly the rows its delta stream
    /// would have delivered — two implementations of "which rows count" is precisely how a reconnect
    /// starts disagreeing with a poll.
    /// </para>
    ///
    /// <para>
    /// <b>The ceiling is <c>MIN_ACTIVE_ROWVERSION() - 1</c>, and without it the cursor loses rows
    /// silently.</b> A <c>rowversion</c> is assigned when a row is <em>written</em>, not when its
    /// transaction <em>commits</em>, so this sequence is ordinary rather than exotic — two devices
    /// flushing queues at once produces it: transaction A writes a row and takes version 100; B writes
    /// a row, takes 101 and commits; a poll running now sees 101 (A's row is not yet visible) and moves
    /// its cursor to 101; A commits. A's row is version 100, forever below the cursor, and no later
    /// poll will ever return it. The dashboard is simply missing a student who tapped, which is
    /// indistinguishable from a student who did not — no error, nothing to notice.
    /// <c>MIN_ACTIVE_ROWVERSION()</c> is the lowest version any uncommitted transaction holds (or the
    /// next value to be issued, if there are none), so refusing to advance past one below it means the
    /// cursor never crosses a write that has not landed. It costs one scalar round trip per poll.
    /// </para>
    ///
    /// <para>
    /// <b>The cursor advances even when nothing changed.</b> It is the ceiling, not the maximum
    /// <c>RowVersion</c> of the rows returned — echoing the caller's own cursor back on an empty delta
    /// would leave a quiet event re-scanning the same widening range on every poll forever.
    /// </para>
    ///
    /// <para>
    /// <b>Soft-deleted events are excluded, and the cursor is validated before the event is read.</b>
    /// A malformed cursor is the caller's bug whichever event it names, and reporting it as a 404
    /// because the event also did not exist would send a client hunting for the wrong problem.
    /// </para>
    /// </summary>
    public async Task<LiveAttendanceResponse> GetLiveAttendanceAsync(
        Guid id, string? since, CancellationToken ct = default)
    {
        var serverTime = DateTime.UtcNow;

        // Whitespace is treated as absent: "?since=" with nothing after it is a query string that lost
        // its value, not a corrupted cursor, and a snapshot is the right answer to a client that has
        // nothing to resume from.
        long? floor = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!AttendanceCursor.TryDecode(since, out var decoded))
            {
                return new LiveAttendanceResponse(
                    LiveOutcome.InvalidCursor,
                    $"'{since}' is not a cursor this API issued. Send the cursor from a previous " +
                    "response verbatim, or omit it entirely to receive a full snapshot.",
                    null);
            }

            floor = decoded;
        }

        var ev = await _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (ev is null)
            return new LiveAttendanceResponse(LiveOutcome.EventNotFound, "Event not found.", null);

        var ceiling = await SafeCursorCeilingAsync(ct);

        // A cursor above the ceiling cannot have come from this database in its current state, and the
        // honest answer is a snapshot (Phase 4d review).
        //
        // TryDecode validates shape only — any eight base64url bytes decode — so "a cursor we did not
        // issue is refused" was a stronger claim than the code could make. This is the cheap half of
        // making it true, and it covers the case that actually happens: a database restored from backup,
        // or one rebuilt by a migration Down/Up, where @@DBTS is lower than the cursor a dashboard is
        // still holding. Used as a floor, that cursor selects nothing, forever, and the dashboard
        // silently stops updating. Clamping to a snapshot is self-healing and costs one comparison.
        //
        // Cursor *signing* is the complete answer and is deliberately not built: it buys detection of a
        // forged cursor, and a forged cursor can only ever cause the forger to see a wrong window of an
        // event they were already allowed to read.
        if (floor > ceiling) floor = null;

        var q = _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.EventId == id && a.RowVersion <= ceiling);
        if (floor is { } lowerBound) q = q.Where(a => a.RowVersion > lowerBound);

        // Projected rather than materialized as entities: the delta needs six columns and four of the
        // student's, and Include would pull every column of both tables per row on the hottest read the
        // dashboard makes.
        //
        // Bounded by MaxPageRows, and the bound applies to a snapshot as much as to a delta (Phase 4d
        // review). A five-thousand-attendee convocation used to return five thousand delta objects to
        // every open dashboard on its first poll. Paging costs nothing conceptually here — see the note
        // below the query for why a truncated page is still a correct cursor stream — whereas an
        // unbounded read is unbounded on the one endpoint designed to be called in a loop.
        var rows = await q
            .OrderBy(a => a.RowVersion)
            .Take(AttendanceLiveOptions.MaxPageRows)
            .Select(a => new
            {
                a.Id,
                a.RowVersion,
                a.StudentId,
                a.Status,
                a.CheckInAt,
                a.CheckOutAt,
                a.CaptureMethod,
                a.Student!.StudentNumber,
                a.Student.FirstName,
                a.Student.MiddleName,
                a.Student.LastName,
            })
            .ToListAsync(ct);

        // A full page means there may be more, so the cursor stops at the last row delivered rather than
        // at the ceiling. That is what makes a truncated page correct rather than lossy: everything at
        // or below that row has been sent, and the client's next poll asks for what is above it. A
        // rowversion is unique per database, so no two rows share the boundary value and nothing can be
        // skipped by a tie.
        var hasMore = rows.Count == AttendanceLiveOptions.MaxPageRows;
        var cursor = hasMore ? rows[^1].RowVersion : ceiling;

        // The same object GET /events/{id}/summary returns, from the same method — see the interface
        // docs for why this is not a leaner purpose-built type. Built from the event already read above
        // rather than through GetSummaryAsync, which would read it a second time.
        //
        // CLOSED IN 4e — bounded by the same ceiling as the rows. 4d shipped this unbounded and said so:
        // four aggregates with no RowVersion predicate, run *after* the rows query, so `counters.present`
        // could count a row the delta had deliberately withheld and the dashboard would show a headline
        // count one higher than the list of names under it. A single poll cycle of skew heals itself; the
        // case that does not is that MIN_ACTIVE_ROWVERSION() is DATABASE-wide, so one unrelated long
        // transaction — a roster import, an event freeze — pins the ceiling while the counters advance,
        // and the discrepancy lasts as long as that transaction does.
        //
        // The ceiling is passed as a parameter rather than by giving the live path its own aggregates:
        // ADR-003 D-12/D-13 record that this denominator fails silently when it is duplicated, so
        // sharing the one implementation is what stops a second copy drifting plausibly.
        //
        // Bounded by `cursor` rather than by `ceiling`, and the difference is the whole point. They are
        // the same value on an untruncated page; on a truncated one the cursor stops at the last row
        // DELIVERED, so counting to the ceiling would count the rows this page deliberately withheld and
        // the headline would read 501 over a list of 500 names — the same defect reached by paging
        // instead of by a long transaction. The cost is that a dashboard paging through a large event
        // sees the headline climb with the list instead of jumping to the total; `hasMore` already tells
        // the client it is mid-page, and a number that matches the names beside it is the property the
        // published contract sells.
        //
        // Note what this does NOT make: an atomic read. The rows and the counters are still separate
        // statements against a moving database, so a row committed between them raises the bound for
        // neither — both are bounded by the same value, read once above. That is the property that
        // matters: the counters can no longer describe a wider set than the rows do.
        var counters = await SummaryForAsync(ev, cursor, ct);

        var deltas = rows.Select(r => new AttendanceDeltaDto(
            ev.Id, r.StudentId, r.Status, r.CheckInAt,
            counters.Present, counters.Expected,
            r.Id, r.StudentNumber,
            string.Join(' ', new[] { r.FirstName, r.MiddleName, r.LastName }
                .Where(p => !string.IsNullOrWhiteSpace(p))),
            r.CheckOutAt, r.CaptureMethod))
            .ToList();

        var live = new AttendanceLiveDto(
            ev.Id,
            AttendanceCursor.Encode(cursor),
            counters,
            // Exactly one of the two is populated; the other is omitted from the JSON. A snapshot
            // replaces the client's state and a delta merges into it, so a body that carried both keys
            // would make the client guess which it had received.
            Entries: floor is null ? deltas : null,
            Changes: floor is null ? null : deltas,
            serverTime,
            _live.PollAfterSeconds,
            hasMore);

        return new LiveAttendanceResponse(LiveOutcome.Ok, "Live attendance.", live);
    }

    /// <summary>
    /// The highest <c>rowversion</c> that is guaranteed to have no uncommitted writer at or below it.
    ///
    /// <para>
    /// Raw SQL because there is no LINQ for it: <c>MIN_ACTIVE_ROWVERSION()</c> is a SQL Server intrinsic
    /// with no EF surface. It takes no parameters and no user input, so there is nothing here to
    /// parameterize and nothing to inject into.
    /// </para>
    ///
    /// <para>
    /// With no transactions in flight it returns the next value to be issued, so subtracting one gives
    /// <c>@@DBTS</c> — the newest version actually assigned. On an empty database that is <c>0</c>,
    /// which is <see cref="AttendanceCursor.Beginning"/> and correctly matches nothing.
    /// </para>
    /// </summary>
    private Task<long> SafeCursorCeilingAsync(CancellationToken ct) =>
        _db.Database
            .SqlQuery<long>($"SELECT CONVERT(bigint, MIN_ACTIVE_ROWVERSION()) - 1 AS Value")
            .FirstAsync(ct);

    // --------------------------------------------------------------------- the device manifest (D-46)

    /// <summary>
    /// <inheritdoc cref="IEventService.GetManifestAsync" path="/summary/para[1]"/>
    ///
    /// <para>
    /// <b>Two round trips, and the second one produces every published byte.</b> The first reads the
    /// event's status alone so a refusal costs an index seek rather than a resolved audience — this
    /// endpoint is polled by devices, and composing a five-thousand-attendee body for a closed event
    /// only to throw it away is the kind of cost that is invisible until it is not. The second is the
    /// manifest itself, and it is a <em>single</em> statement: <see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsSingleQuery{TEntity}"/>
    /// collapses what would otherwise be three unsynchronized round trips into one, so the window in
    /// which the underlying rows can move is a statement rather than a conversation.
    /// </para>
    ///
    /// <para>
    /// <b>That is a narrowing, not a guarantee, and the distinction is worth stating plainly.</b> Under
    /// SQL Server's default <c>READ COMMITTED</c> with locking — and nothing in this application
    /// configures otherwise — a single <c>SELECT</c> does not promise a point-in-time view across the
    /// whole statement: a row already read can be updated by another transaction while the scan is still
    /// running further along. What would make the snapshot absolute is row-versioning
    /// (<c>READ_COMMITTED_SNAPSHOT</c>, or <c>ALLOW_SNAPSHOT_ISOLATION</c> with an explicit snapshot
    /// transaction), and both are database-level settings rather than something a feature switches on —
    /// see "why a composed query rather than a transaction" below. One statement is the largest reduction
    /// of the window available to this code, and the residual race is orders of magnitude narrower than
    /// the three-round-trip one it replaces.
    /// </para>
    ///
    /// <para>
    /// <b>Why one snapshot is a correctness requirement and not tidiness.</b> The version is a hash over
    /// the published content. Read the groups, then the attendees, then their cards as three statements
    /// against a moving database, and the composed body can describe a state that never existed at any
    /// instant — a student detached between statement one and statement two appears in neither list, or
    /// in exactly one. The hash then names that impossible state, and two successive pulls can flip
    /// between two such states forever: a device oscillating between versions, each response a
    /// perfectly valid 200, nothing anywhere reporting an error.
    /// </para>
    ///
    /// <para>
    /// <b>Why a composed query rather than a transaction.</b> A transaction would need
    /// <c>Snapshot</c> isolation to give the same guarantee without holding read locks across an
    /// endpoint devices poll — and that is a database-level setting
    /// (<c>ALLOW_SNAPSHOT_ISOLATION</c>), not something a feature gets to switch on. It would also have
    /// to be threaded through <c>CreateExecutionStrategy</c>, because the production registration
    /// enables retry-on-failure and EF refuses user-initiated transactions under a retrying strategy.
    /// The single statement needs neither and works on any server.
    /// </para>
    ///
    /// <para>
    /// <b>The cost being accepted, stated rather than discovered later — and it is larger than the
    /// obvious estimate.</b> Sibling collections under one root row are a cartesian product in
    /// single-query mode, and this query nests two levels: <c>Attendees</c> carries two sibling
    /// collections of its own (<c>GroupIds</c> and <c>CardUids</c>), which multiply against each other
    /// before the whole attendee set multiplies against <c>Groups</c>. The row count is therefore
    /// <c>|groups| × Σᵢ(|groupIdsᵢ| × |cardUidsᵢ|)</c>, not <c>|groups| × |attendees|</c> — at the
    /// <c>EventManifestLimits.MaxAttendees</c> ceiling with five attached groups and students in two
    /// sections holding two cards each, that is roughly 400,000 rows returned to produce a
    /// 20,000-row answer. Today's real numbers are nowhere near it: attached groups are a handful by
    /// construction, most students sit in one section, and every card set is currently empty (D-43), so
    /// both inner factors are 1 or 0 and EF de-duplicates on materialization. This is the number to
    /// reach for when it stops being cheap. When it does, the correct fix is caching the <em>hash</em>
    /// against a real invalidation signal, never splitting the read: split reads are the failure above.
    /// </para>
    ///
    /// <para>
    /// <b>Every ordering happens in memory, deliberately.</b> SQL Server sorts <c>uniqueidentifier</c>
    /// by a byte order that is not <see cref="Guid.CompareTo(Guid)"/>'s, so an <c>ORDER BY</c> would
    /// make the canonical form — and therefore the version — a property of the database engine. Sorting
    /// here makes it a property of the contract, which is what lets two hosts agree.
    /// </para>
    /// </summary>
    public async Task<EventManifestResponse> GetManifestAsync(
        Guid id, CancellationToken ct = default)
    {
        // Status alone: the whole of what decides whether there is anything to compose. Null is "no
        // such event" — including an event in another school, which the device's own school_id claim
        // filters out before this query runs, so D-27's no-existence-disclosure rule holds without this
        // method having to know it is enforcing it.
        var currentStatus = await _db.Events.AsNoTracking()
            .Where(e => e.Id == id && !e.IsDeleted)
            .Select(e => e.Status)
            .FirstOrDefaultAsync(ct);

        if (RefusalFor(currentStatus) is { } refused) return refused;

        var attachedGroupIds = _db.EventGroups
            .Where(eg => eg.EventId == id && eg.StudentGroupId != null)
            .Select(eg => eg.StudentGroupId!.Value);

        // THE denominator query — the same IQueryable GetSummaryAsync, GetRosterAsync and
        // GetAudienceAsync count, not a fourth opinion of it. That is what makes attendees.Count equal
        // the `expected` those three publish for this event, including the ADR-003 D-15 exclusion of
        // soft-deleted students, and ADR-003 D-19 records what a second implementation of it costs.
        // Passed EventStatus.Open rather than the value read above because the refusal check has
        // already established which one this is, and the live branch is the only one reachable here.
        var expectedStudentIds = ExpectedStudentIds(id, EventStatus.Open);

        var composed = await _db.Events.AsNoTracking()
            .Where(e => e.Id == id && !e.IsDeleted)
            .Select(e => new
            {
                e.Name,
                e.StartAt,
                e.EndAt,
                e.GraceMinutes,
                e.AttendanceMode,
                e.Status,
                Groups = _db.EventGroups
                    // The SchoolId predicate is explicit for the reason GetAudienceAsync's is:
                    // EventGroups carries no SchoolId and reaches tenancy through Event (ADR-003 D-12),
                    // and the §11 global filter is inert whenever no tenant is pinned. A row that fails
                    // this can only be hand-written, and publishing another school's group names to a
                    // device is the worse of the two failures.
                    .Where(eg => eg.EventId == e.Id
                              && eg.StudentGroupId != null
                              && eg.StudentGroup!.SchoolId == e.SchoolId)
                    .Select(eg => new
                    {
                        StudentGroupId = eg.StudentGroupId!.Value,
                        eg.StudentGroup!.Name,
                        eg.StudentGroup.Type,
                    })
                    .ToList(),
                Attendees = _db.Students
                    .Where(s => expectedStudentIds.Contains(s.Id))
                    .Select(s => new
                    {
                        s.Id,
                        s.StudentNumber,
                        s.FirstName,
                        s.MiddleName,
                        s.LastName,
                        // Which of the attached groups reach this student. Read from the membership
                        // side so the row set is one index seek per student on
                        // UX_StudentGroupMembers_Group_Student's leading column, and — the part that
                        // matters to the contract — collected onto this student's single row rather
                        // than duplicating the student per group. Twelve of the fifty-two real students
                        // sit in more than one section (ADR-001 D-2); a join-shaped manifest would list
                        // a quarter of them twice.
                        GroupIds = _db.StudentGroupMembers
                            .Where(m => m.StudentId == s.Id
                                     && attachedGroupIds.Contains(m.StudentGroupId))
                            .Select(m => m.StudentGroupId)
                            .ToList(),
                        // Active only: a deactivated card must stop resolving on the device the moment
                        // it stops resolving on the server, and it is a card *set* rather than a card
                        // because a reissue leaves the old row active (ADR-001 D-3).
                        CardUids = s.Cards.Where(c => c.IsActive).Select(c => c.CardUid).ToList(),
                    })
                    .ToList(),
            })
            .AsSingleQuery()
            .FirstOrDefaultAsync(ct);

        // Re-checked against the snapshot the body was actually composed from, not against the status
        // read a moment ago. An event closed between the two reads must not be published as Open, and
        // the honest answer is the refusal the second read supports.
        if (RefusalFor(composed?.Status) is { } refusedOnSnapshot) return refusedOnSnapshot;

        var groups = composed!.Groups
            .Select(g => new EventManifestGroupDto(g.StudentGroupId, g.Name, g.Type))
            .OrderBy(g => g.StudentGroupId)
            .ToList();

        var attendees = composed.Attendees
            .Select(a => new EventManifestAttendeeDto(
                a.Id,
                a.StudentNumber,
                string.Join(' ', new[] { a.FirstName, a.MiddleName, a.LastName }
                    .Where(part => !string.IsNullOrWhiteSpace(part))),
                [.. a.GroupIds.Distinct().Order()],
                // Normalized again on the way out even though every persisted UID is already
                // normalized. Normalize is idempotent, so this costs nothing and makes the published
                // guarantee true of the response rather than true of the write path that produced it —
                // which is the CLAUDE.md rule (normalize first, compare second) applied to the one
                // place a client is told it may compare.
                [.. a.CardUids.Select(CardUid.Normalize).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)]))
            .OrderBy(a => a.StudentId)
            .ToList();

        if (attendees.Count > EventManifestLimits.MaxAttendees)
        {
            // Checked over the list that was actually composed rather than over a cheaper COUNT run
            // first. A separate count is a second definition of this population — the exact drift
            // ADR-003 D-19 records — and it could disagree with the body by the width of one write.
            return new EventManifestResponse(
                ManifestOutcome.ManifestTooLarge,
                $"This event expects {attendees.Count} attendees; a manifest carries at most " +
                $"{EventManifestLimits.MaxAttendees}. Nothing was truncated and nothing partial was " +
                "sent — a short manifest is indistinguishable from a small event, and its consequence " +
                "is invited students showing on the device as unknown cards. Keep the manifest you " +
                "have, tell the operator, and report this event to us.",
                null,
                attendees.Count);
        }

        var @event = new EventManifestEventDto(
            id, composed.Name, composed.StartAt, composed.EndAt,
            composed.GraceMinutes, composed.AttendanceMode, composed.Status);

        // Assembled once, hashed, then given its own version back. The version is a function of the
        // object that carries it, so something has to occupy the field in between; both it and
        // serverTime are dropped structurally before a byte is hashed — see EventManifestVersion. A
        // serverTime inside the hash would make every pull a new version and the conditional GET would
        // never once return 304.
        //
        // Hashing the assembled DTO rather than its parts is what keeps a field added to
        // EventManifestDto inside the version automatically: there is one construction site, and it is
        // this one, so there is nowhere for a new field to be published and not hashed.
        var content = new EventManifestDto(
            @event, groups, attendees,
            ServerTime: DateTime.UtcNow,
            Version: EventManifestVersion.PendingVersion);

        var manifest = content with { Version = EventManifestVersion.Compute(content) };

        return new EventManifestResponse(
            ManifestOutcome.Ok, "Manifest.", manifest, attendees.Count);
    }

    /// <summary>
    /// The status gate, as one total function: <c>null</c> means "serve it".
    ///
    /// <para>
    /// <b>Only <c>Open</c> is served, and <c>Draft</c> is refused as firmly as the terminal statuses.</b>
    /// Serving a draft would hand a device a manifest for an event whose taps
    /// <c>POST /attendance/tap</c> refuses anyway; the guarantee that a device only holds a manifest it
    /// can capture against belongs on our side rather than in an external client.
    /// </para>
    ///
    /// <para>
    /// <b>A status outside §4.5's set falls to <see cref="ManifestOutcome.EventNotOpen"/>, not to
    /// <see cref="ManifestOutcome.EventFrozen"/>, and the normalization is what makes that true.</b>
    /// <c>EventStatusTransition.IsTerminal</c> answers "no status is reachable from here", which is
    /// <c>true</c> for an unrecognised value as well as for the two real terminal ones — so asking it
    /// directly would tell a device that a corrupt row means "the event is over, flush and stop". The
    /// canonical-value guard is what keeps the terminal arm about <c>Closed</c> and <c>Cancelled</c>.
    /// </para>
    /// </summary>
    private static EventManifestResponse? RefusalFor(string? status)
    {
        if (status is null)
        {
            return new EventManifestResponse(
                ManifestOutcome.EventNotFound, "Event not found.", null, 0);
        }

        if (string.Equals(status, EventStatus.Open, StringComparison.Ordinal)) return null;

        if (EventStatus.TryNormalize(status, out var canonical)
            && EventStatusTransition.IsTerminal(canonical))
        {
            return new EventManifestResponse(
                ManifestOutcome.EventFrozen,
                $"Event is {canonical}, and no further attendance can be captured against it. Flush " +
                "any taps still queued before you stop — they are still recorded — and keep the " +
                "cached manifest until that queue is empty. Then tell the operator the event is over.",
                null,
                0);
        }

        return new EventManifestResponse(
            ManifestOutcome.EventNotOpen,
            $"Event is {status}, not {EventStatus.Open}. A manifest is served only for an event that " +
            "has been opened for capture; a tap against any other status is refused as well. Ask the " +
            "organizer to open the event, then pull again.",
            null,
            0);
    }

    // ------------------------------------------------------- the audience resolver (D-50/D-51)

    /// <summary>
    /// One validated filter row, in the shape the field it names actually compares on.
    ///
    /// <para>
    /// Exactly one of <paramref name="Ids"/> and <paramref name="Keys"/> is populated, decided by the
    /// same <see cref="AudienceField"/> value that later decides which predicate is written — so the two
    /// switches are the only place the pairing lives, and a sixth field adds an arm to each rather than
    /// a shape to remember. Neither list is ever empty: an empty value list is dropped before a
    /// condition is built, because D-51 makes an empty row a no-op rather than "match nobody".
    /// </para>
    /// </summary>
    private sealed record AudienceCondition(
        string Field, IReadOnlyList<Guid> Ids, IReadOnlyList<string> Keys);

    /// <inheritdoc cref="IEventService.ResolveAudienceAsync"/>
    /// <remarks>
    /// <para>
    /// <b>Every filter row is validated before anything is read, and the order matters.</b> Field names
    /// and values are checked against the registry with no database access at all, so a request naming
    /// an unregistered field is a 400 whether or not a term is current and whether or not the roster has
    /// been imported. Validating after the term lookup would make the same malformed request answer 400
    /// on one day and an empty 200 on another, which is the least debuggable shape an API can have.
    /// </para>
    ///
    /// <para>
    /// <b>Three round trips, and the first one is the whole reason for the other two.</b> The count is a
    /// <c>COUNT(*)</c> over the composed filter with no ceiling on it; the id list and the sample are
    /// two bounded <c>TOP n</c> reads over the same ordered query. Deriving the count from the id list
    /// instead would be one query and would reintroduce D-42 exactly: a number bounded by the read
    /// ceiling rather than by what the filter matches. Deriving the sample from the ids would need a
    /// second read anyway to get the names, and taking it as a prefix of the same ordering is what makes
    /// "the preview is the head of the list" true by construction.
    /// </para>
    /// </remarks>
    public async Task<AudienceResolveResponse> ResolveAudienceAsync(
        AudienceResolveRequest request, CancellationToken ct = default)
    {
        var conditions = new List<AudienceCondition>();
        IReadOnlyList<AudienceFilterDto> rows = request.Filters ?? [];

        foreach (var row in rows)
        {
            // The field is checked even when the row carries no values. `{"field": "Nickname",
            // "values": []}` is still an attempt to filter on something that does not exist, and
            // answering 200 to it would teach a client that the field is fine and its values were the
            // problem.
            if (!AudienceField.TryNormalize(row.Field, out var field))
            {
                return new AudienceResolveResponse(
                    AudienceResolveOutcome.UnknownAudienceField,
                    $"'{row.Field}' is not a field an audience can be filtered on. The filterable " +
                    $"fields are: {string.Join(", ", AudienceField.All)}. The list is closed on " +
                    "purpose (D-50) — the student record's own course, year-level and section columns " +
                    "are a display cache that is wrong for the roughly one student in four who sits " +
                    "in more than one section, so every field resolves through the enrolment and " +
                    "term-record tables instead.",
                    null);
            }

            IReadOnlyList<string> values = row.Values ?? [];

            // D-51: an empty-but-present row is a half-finished edit, not "match nobody". Resolving it
            // to zero would collapse the count to nothing the moment an operator added a row and before
            // they had chosen anything to put in it.
            if (values.Count == 0) continue;

            if (BuildCondition(field, values) is not { } condition)
            {
                return InvalidFilterValue(field, values);
            }

            conditions.Add(condition);
        }

        // Resolved to an id before the query is composed rather than folded in as an `r.Term.IsCurrent`
        // predicate, for the reason AcademicReferenceService.ListCourseOfferingsAsync records: a
        // predicate that matches nothing and a predicate that is absent are indistinguishable once
        // written that way, so "no term is current" would silently mean "every term".
        var termId = request.TermId ?? await _db.Terms.AsNoTracking()
            .Where(t => t.IsCurrent)
            // FirstOrDefault rather than Single, matching every other current-term read: the filtered
            // unique index is what caps this at one row, and a data problem in one school must not
            // become a 500 on a count that runs while somebody types.
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (termId is null)
        {
            // Empty rather than widened, and `termId: null` is what tells the caller which of the two
            // empties this is. See AudienceResolveRequest.TermId.
            return Resolved(new AudienceResolutionDto(
                TermId: null,
                Count: 0,
                StudentIds: [],
                StudentIdsTruncated: false,
                StudentIdLimit: AudienceResolutionLimits.MaxStudentIds,
                Sample: []));
        }

        var matched = MatchingPlacements(termId.Value, conditions);

        // Ordered once, and both bounded reads below run over this. Student numbers are unique per
        // school, but the unfiltered query an untenanted caller sees spans schools — so ThenBy(Id) is
        // what makes the order total, which is what makes "the sample is a prefix of the ids" a fact
        // rather than a coincidence of how SQL Server happened to return two TOP n reads.
        var ordered = matched
            .OrderBy(r => r.Student!.StudentNumber)
            .ThenBy(r => r.StudentId);

        var count = await matched.CountAsync(ct);

        var studentIds = await ordered
            // One more than the ceiling is deliberately *not* fetched to detect truncation: `count` is
            // already the honest size, so comparing against it needs no probe row.
            .Take(AudienceResolutionLimits.MaxStudentIds)
            .Select(r => r.StudentId)
            .ToListAsync(ct);

        var sample = await ordered
            .Take(AudienceResolutionLimits.SampleSize)
            .Select(r => new
            {
                r.StudentId,
                r.Student!.StudentNumber,
                r.Student.FirstName,
                r.Student.MiddleName,
                r.Student.LastName,
            })
            .ToListAsync(ct);

        return Resolved(new AudienceResolutionDto(
            termId,
            count,
            studentIds,
            // Compared against the unbounded count, never against the ceiling — a filter matching
            // exactly the ceiling is not truncated, and saying it was would send a caller looking for
            // students that are all already in the list.
            StudentIdsTruncated: count > studentIds.Count,
            StudentIdLimit: AudienceResolutionLimits.MaxStudentIds,
            // FullName is a computed CLR property and does not translate, so the parts are projected
            // and joined here — the same composition GetAudienceAsync and the manifest already do.
            Sample: sample.Select(s => new AudienceStudentDto(
                s.StudentId, s.StudentNumber,
                string.Join(' ', new[] { s.FirstName, s.MiddleName, s.LastName }
                    .Where(part => !string.IsNullOrWhiteSpace(part)))))
                .ToList()));
    }

    /// <summary>
    /// The composed filter, as a query over <c>StudentTermRecords</c> — <b>one <c>Where</c> per row, one
    /// <c>Contains</c> per value list</b> (D-51), and no query language anywhere.
    ///
    /// <para>
    /// <b>The root is the term record, and that is what makes a student in two matching sections count
    /// once.</b> <c>UNIQUE(StudentId, TermId)</c> means this query has exactly one row per student
    /// before any filter is applied, and the two enrolment-backed fields are written as <c>EXISTS</c>
    /// rather than as joins, so neither can multiply that row. There is no <c>Distinct</c> here because
    /// there is nothing for one to remove — which is the shape to keep: a <c>Distinct</c> guarding a
    /// join is a fix that stops working the moment someone adds a projection, whereas a grain that
    /// cannot duplicate is a fix that cannot be undone by accident. ADR-001 D-2 is the tripwire: 12 of
    /// 52 real students sit in more than one section.
    /// </para>
    ///
    /// <para>
    /// <b>Soft-deleted students are excluded</b>, matching every other live audience read in this class
    /// (see <see cref="ExpectedStudentIds"/>): a student the roster says does not exist cannot be
    /// invited to anything.
    /// </para>
    ///
    /// <para>
    /// <b>Both enrolment predicates re-state the term.</b> A section key and a course id both repeat
    /// across semesters, so without <c>CourseOffering.TermId == termId</c> a student's enrolment in
    /// <em>last</em> year's <c>BSIT2A</c> would satisfy this year's filter — the same distinct-audiences
    /// -under-identical-names failure the course-offering list's term default closes.
    /// </para>
    ///
    /// <para>
    /// <b>No column name reaches EF from the request.</b> Each arm below writes its own predicate
    /// against a named property; the caller only ever chooses which arm runs. That is what "the
    /// <c>Students</c> cache columns are unreachable by construction" means in D-50, and it is why the
    /// final arm throws instead of falling through — a registered field with no arm must fail loudly
    /// rather than resolve as though the row had never been sent.
    /// </para>
    /// </summary>
    private IQueryable<StudentTermRecord> MatchingPlacements(
        Guid termId, IReadOnlyList<AudienceCondition> conditions)
    {
        var query = _db.StudentTermRecords.AsNoTracking()
            .Where(r => r.TermId == termId && !r.Student!.IsDeleted);

        foreach (var condition in conditions)
        {
            var ids = condition.Ids;
            var keys = condition.Keys;

            query = condition.Field switch
            {
                AudienceField.College =>
                    query.Where(r => r.CollegeId != null && ids.Contains(r.CollegeId.Value)),

                AudienceField.Program =>
                    query.Where(r => r.ProgramId != null && ids.Contains(r.ProgramId.Value)),

                // The derived value (D-47), never Students.YearLevel (D-48). A null year matches no
                // year filter, which is the first-class outcome D-47 designed for — and on today's real
                // roster it is every student, because the programme reads "BSci - Crim" while the
                // sections read "BSCRIM 2-A" and the anchor therefore never fires. That is a registrar
                // question, not a rule to widen.
                AudienceField.YearLevel =>
                    query.Where(r => r.YearLevel != null && keys.Contains(r.YearLevel)),

                AudienceField.Section =>
                    query.Where(r => _db.Enrollments.Any(e =>
                        e.StudentId == r.StudentId
                        && e.CourseOffering!.TermId == termId
                        && keys.Contains(e.CourseOffering!.SectionKey))),

                AudienceField.Course =>
                    query.Where(r => _db.Enrollments.Any(e =>
                        e.StudentId == r.StudentId
                        && e.CourseOffering!.TermId == termId
                        && ids.Contains(e.CourseOffering!.CourseId))),

                _ => throw new ArgumentOutOfRangeException(
                    nameof(conditions), condition.Field,
                    $"'{condition.Field}' is in {nameof(AudienceField)}.{nameof(AudienceField.All)} " +
                    "but has no predicate here. Adding a sixth filterable field is a constant on that " +
                    "registry and an arm in this switch; adding only the first must fail loudly rather " +
                    "than resolve as though the filter row had never been sent."),
            };
        }

        return query;
    }

    /// <summary>
    /// Turns one row's values into the form its field compares on, or <c>null</c> when any of them is
    /// not a value that field can hold.
    ///
    /// <para>
    /// <b>The whole row fails on one bad value; nothing is dropped.</b> Skipping the offending value
    /// would widen the row silently — <c>Program is any of (BSIT, "oops")</c> would resolve as
    /// <c>Program is BSIT</c> and report a count for a filter the operator did not build.
    /// </para>
    ///
    /// <para>
    /// <b>Section keys are normalized before they are compared, never after</b> — the rule the whole
    /// academic layer is built on. The stored <c>SectionKey</c> is what <c>AcademicKey</c> produced at
    /// import, so comparing a raw <c>'BSFS 2-A'</c> against it matches nothing while looking entirely
    /// correct. A value that normalizes onto the <c>(unspecified)</c> sentinel is refused rather than
    /// matched: that key means "the source recorded no section", it is the audience
    /// <see cref="AttachAudienceAsync"/> already refuses as <see cref="EventWriteOutcome.NotACohort"/>,
    /// and a builder that could assemble it would be a way around that refusal. A blank value
    /// normalizes onto the sentinel too, so one check covers both.
    /// </para>
    /// </summary>
    private static AudienceCondition? BuildCondition(string field, IReadOnlyList<string> values)
    {
        switch (field)
        {
            case AudienceField.College:
            case AudienceField.Program:
            case AudienceField.Course:
            {
                var ids = new List<Guid>(values.Count);
                foreach (var value in values)
                {
                    if (!Guid.TryParse(value, out var id)) return null;
                    ids.Add(id);
                }

                return new AudienceCondition(field, ids, []);
            }

            case AudienceField.YearLevel:
            {
                var keys = new List<string>(values.Count);
                foreach (var value in values)
                {
                    // Blank only. A year string outside the digits D-47 can produce is well-formed and
                    // simply matches nobody, which is the answer this layer already gives an unknown
                    // collegeId — see IAcademicReferenceService.ListProgramsAsync.
                    if (string.IsNullOrWhiteSpace(value)) return null;
                    keys.Add(value.Trim());
                }

                return new AudienceCondition(field, [], keys);
            }

            case AudienceField.Section:
            {
                var keys = new List<string>(values.Count);
                foreach (var value in values)
                {
                    // Both spellings of the sentinel are refused, and it takes two checks because
                    // AcademicKey deliberately makes the value unforgeable from a display string. The
                    // literal '(unspecified)' *normalizes* to 'UNSPECIFIED' — the parentheses are
                    // stripped — so a raw comparison is the only thing that catches a caller posting
                    // back the sectionKey GET /academic/course-offerings publishes for a
                    // blank-section offering, which is the likeliest way to send it. A blank value
                    // catches the other way in: it normalizes onto the sentinel.
                    if (string.Equals(value?.Trim(), AcademicKey.Unspecified, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var key = AcademicKey.NormalizeOrUnspecified(value);
                    if (key == AcademicKey.Unspecified) return null;
                    keys.Add(key);
                }

                return new AudienceCondition(field, [], keys);
            }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(field), field,
                    $"'{field}' is in {nameof(AudienceField)}.{nameof(AudienceField.All)} but its " +
                    "values have no reading here. See MatchingPlacements for the other half a sixth " +
                    "field has to add.");
        }
    }

    private static AudienceResolveResponse Resolved(AudienceResolutionDto resolution) =>
        new(AudienceResolveOutcome.Ok, "Resolved.", resolution);

    /// <summary>
    /// The refusal for a value the field cannot hold. It names the field and echoes the values rather
    /// than saying "invalid": the caller is a filter builder assembling a list, and "one of these is
    /// wrong" with the list attached is the difference between a fixable error and a shrug.
    /// </summary>
    private static AudienceResolveResponse InvalidFilterValue(
        string field, IReadOnlyList<string> values) =>
        new(AudienceResolveOutcome.InvalidAudienceFilterValue,
            $"'{field}' cannot be filtered on the values [{string.Join(", ", values)}]. " +
            (field is AudienceField.YearLevel
                ? "A year level is a non-blank value — the bare digit the derivation writes, such as " +
                  "'2'. A year nobody has been derived into is not an error; it simply matches no one."
                : field is AudienceField.Section
                    ? "A section is its key or display name, such as 'BSFS 2-A'. The '(unspecified)' " +
                      "sentinel — which a blank value normalizes onto — is refused: it means the " +
                      "source recorded no section, so it is not a cohort anyone could have meant to " +
                      "invite, and attaching it is refused for the same reason."
                    : "This field is filtered on ids, so every value has to be a GUID. An id that " +
                      "matches nothing is fine and simply resolves to nobody; a value that is not an " +
                      "id at all is refused, because dropping it would quietly widen the filter.") +
            " The whole row is refused rather than the offending value dropped — a filter that " +
            "silently stops filtering reports a count for something nobody built.",
            null);

    // ------------------------------------------------------------------------------- create/edit

    public async Task<EventWriteResponse> CreateAsync(
        EventWriteRequest request, CancellationToken ct = default)
    {
        if (Validate(request) is { } invalid) return invalid;

        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
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

        // A Cancelled event may be renamed and re-described — "cancelled, venue flooded" is the whole
        // point of it being editable at all — but not re-scheduled. See
        // EventStatusTransition.AcceptsAttendanceRuleEdits: an Open -> Cancelled event can already hold
        // taps whose Present-versus-Late was decided by StartAt + GraceMinutes, so moving those is the
        // same silent rewrite of existing rows that a Closed event refuses.
        if (!EventStatusTransition.AcceptsAttendanceRuleEdits(ev.Status)
            && AttendanceRuleChanges(request, ev) is { Count: > 0 } changed)
        {
            return new EventWriteResponse(EventWriteOutcome.EventLocked,
                $"Event is {ev.Status}, so {string.Join(", ", changed)} cannot be changed — those are " +
                "the fields that decide what an attendance row means, and this event may already hold " +
                "rows they were computed from. Its name, description and location can still be edited; " +
                "re-send those with the scheduling fields left as they are.", null);
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

        if (!EventStatusTransition.SnapshotsAudience(current, target))
        {
            await _db.SaveChangesAsync(ct);
            return Saved(ev, $"Event is now {target}.");
        }

        var materializeAbsentees = EventStatusTransition.FreezesRoster(current, target);
        var frozen = await FreezeAsync(ev.Id, ev.SchoolId, materializeAbsentees, ct);

        if (!materializeAbsentees)
        {
            return Saved(ev,
                $"Event is now {target}. Its audience of {frozen.Expected} " +
                $"{(frozen.Expected == 1 ? "student" : "students")} has been written down and can no " +
                "longer move; no attendance was recorded, because nobody was expected to attend an " +
                "event that did not happen.");
        }

        return Saved(ev,
            $"Event is now {target}. {frozen.Absentees} expected " +
            $"{(frozen.Absentees == 1 ? "attendee" : "attendees")} with no record were marked Absent; " +
            "this event's denominator and absentee list are now fixed.");
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
    /// <param name="materializeAbsentees">
    /// True for <c>→ Closed</c>, false for <c>→ Cancelled</c>. The single difference between the two
    /// transitions: both write the audience down, only one turns it into an absentee list. A flag rather
    /// than two near-identical methods because the retry, the detach and the staging are the whole body
    /// and they are shared — duplicating them is how one copy learns about a new race and the other does
    /// not, which is the divergence <c>SqlServerErrors</c> was extracted to prevent.
    /// </param>
    private async Task<FrozenAudience> FreezeAsync(
        Guid eventId, Guid schoolId, bool materializeAbsentees, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Staged together and saved together because they are one statement about the event —
            // "these people were expected, and these of them did not come". Committing the absentees
            // without the snapshot would leave a closed event whose absentee list is fixed and whose
            // denominator still drifts: the two would disagree, and the disagreement would look like an
            // arithmetic bug rather than a missing write.
            var pending = new List<object>();
            var expected = await SnapshotAudienceAsync(eventId, pending, ct);
            var absentees = materializeAbsentees
                ? await StageAbsenteesAsync(eventId, schoolId, expected, pending, ct)
                : 0;

            try
            {
                await _db.SaveChangesAsync(ct);
                return new FrozenAudience(expected.Count, absentees);
            }
            catch (DbUpdateException ex)
                when (attempt < FreezeRetryLimit && SqlServerErrors.IsUniqueViolation(ex))
            {
                // EF leaves a failed insert Added, so without this the next SaveChanges would retry the
                // very rows that just collided — and a tracked Added row with the same key would also
                // shadow the re-read through identity resolution. The same detach the tap flow's
                // ResolveLostInsertRaceAsync performs, for the same reason.
                foreach (var entity in pending)
                    _db.Entry(entity).State = EntityState.Detached;
            }
        }
    }

    /// <summary>What the freeze wrote down: how many were expected, and how many of them were absent.</summary>
    private readonly record struct FrozenAudience(int Expected, int Absentees);

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
    ///
    /// <para>
    /// <b>It resolves with <c>includeDeleted: true</c>, which is deliberately <em>not</em> what the live
    /// read does, and the asymmetry is the whole reason this line is load-bearing.</b> The frozen read
    /// counts every individual §4.8 row regardless of the student's deleted flag — it has to, because
    /// nothing in the schema distinguishes a student deleted before the close from one deleted after,
    /// and the second must never retroactively shrink a past event's denominator. So if the snapshot
    /// resolved with <c>includeDeleted: false</c>, an already-soft-deleted individually-attached student
    /// would get no <c>Absent</c> row while their pre-existing row still counted in the denominator: the
    /// event would close with <c>Present + Late + Absent + Excused</c> permanently one short of
    /// <c>Expected</c>, and <c>NotRecorded</c> reading 1 on a frozen event — which is precisely the
    /// state the freeze exists to make impossible.
    /// </para>
    ///
    /// <para>
    /// Group-reached students are still filtered by <see cref="GroupMemberStudentIds"/>, and that stays
    /// right: a soft-deleted student in an attached section was never in the live denominator and gets
    /// no row here, so the frozen read will not find one either. The two halves agree at the boundary,
    /// which is the property that matters — not that both are permissive.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Guid>> SnapshotAudienceAsync(
        Guid eventId, List<object> pending, CancellationToken ct)
    {
        var expected = await GroupMemberStudentIds(eventId)
            .Union(AttachedStudentIds(eventId, includeDeleted: true))
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
        Guid eventId, Guid schoolId, IReadOnlyList<Guid> expected, List<object> pending,
        CancellationToken ct)
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
                // D-35. The event's school, passed in rather than re-read: this loop runs inside the
                // one SaveChangesAsync the freeze commits through (D-21), and the event is already in
                // hand at the call site.
                SchoolId = schoolId,
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

        var delta = await AttachMissingAsync(id, groupIds, studentIds, ct);

        var warnings = await TermWarningsAsync(ev.SchoolId, groups.Select(g => (g.Name, g.TermId)), ct);
        var expected = await ExpectedStudentIds(id, ev.Status).CountAsync(ct);

        return new EventAudienceResponse(EventWriteOutcome.Saved, "Audience updated.",
            new EventAudienceResultDto(
                id, delta.Groups, delta.Students,
                groupIds.Count - delta.Groups, studentIds.Count - delta.Students,
                expected, warnings));
    }

    /// <summary>How much of the requested selection this call actually inserted.</summary>
    private readonly record struct AudienceDelta(int Groups, int Students);

    /// <summary>
    /// Inserts whichever of the requested attachments are not already there, and survives losing that
    /// insert to a concurrent post.
    ///
    /// <para>
    /// <b>The read-then-insert alone was never the guarantee, and this is the half that was missing.</b>
    /// <c>EamsDbContext</c> adds <c>UX_EventGroups_Event_Group</c> and
    /// <c>UX_EventGroups_Event_Student</c> for exactly this reason — in its own words, idempotency that
    /// lives only in a service's read-then-insert loses to two concurrent posts — and the indexes do
    /// protect the data. But the service did not handle the violation they raise, so the scenario the
    /// schema comment names, an organizer double-submitting a slow form, produced an unhandled
    /// <c>DbUpdateException</c> and a 500 on the request that lost. A sequential re-post of the same
    /// selection has always returned a cheerful <c>alreadyAttached</c>; the concurrent one now returns
    /// the same thing, which is what "idempotent" has to mean to be worth claiming.
    /// </para>
    ///
    /// <para>
    /// <b>The retry re-reads rather than assuming a total loss.</b> Two posts can overlap partially —
    /// one attaches sections A and B, the other B and C — and because the whole <c>SaveChanges</c> is a
    /// single transaction, losing on B rolls back C as well. Reporting everything as already-attached
    /// would silently drop C. So the diff is recomputed against what is now committed and the save is
    /// retried, which converges: whatever the winner wrote is excluded, and what remains is ours alone.
    /// One retry, bounded for the reason <see cref="FreezeRetryLimit"/> gives — a second violation is no
    /// longer a race but a wrong assumption about which index fired, and it should surface.
    /// </para>
    /// </summary>
    private async Task<AudienceDelta> AttachMissingAsync(
        Guid eventId, IReadOnlyList<Guid> groupIds, IReadOnlyList<Guid> studentIds, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var attached = await _db.EventGroups.AsNoTracking()
                .Where(eg => eg.EventId == eventId)
                .Select(eg => new { eg.StudentGroupId, eg.StudentId })
                .ToListAsync(ct);

            var attachedGroups = attached.Where(a => a.StudentGroupId != null)
                .Select(a => a.StudentGroupId!.Value).ToHashSet();
            var attachedStudents = attached.Where(a => a.StudentId != null)
                .Select(a => a.StudentId!.Value).ToHashSet();

            var newGroups = groupIds.Where(g => !attachedGroups.Contains(g)).ToList();
            var newStudents = studentIds.Where(s => !attachedStudents.Contains(s)).ToList();

            if (newGroups.Count == 0 && newStudents.Count == 0) return new AudienceDelta(0, 0);

            var pending = new List<EventGroup>();
            foreach (var groupId in newGroups)
                pending.Add(new EventGroup { EventId = eventId, StudentGroupId = groupId });
            foreach (var studentId in newStudents)
                pending.Add(new EventGroup { EventId = eventId, StudentId = studentId });

            _db.EventGroups.AddRange(pending);

            try
            {
                await _db.SaveChangesAsync(ct);
                return new AudienceDelta(newGroups.Count, newStudents.Count);
            }
            catch (DbUpdateException ex)
                when (attempt < AttachRetryLimit && SqlServerErrors.IsUniqueViolation(ex))
            {
                // EF leaves failed inserts Added. Without detaching, the next SaveChanges would retry
                // the very rows that just collided, and the re-read above would be shadowed by them
                // through identity resolution — so the diff would come back empty and this would report
                // a successful attach that never happened. Same reason as the freeze, same fix.
                foreach (var row in pending)
                    _db.Entry(row).State = EntityState.Detached;
            }
        }
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

    // Which school a new event belongs to: SchoolResolution.ResolveSchoolIdAsync. Shared with the
    // §6.2 student write surface rather than copied, so both agree with the tenant the startup log
    // announced. A null answer becomes EventWriteOutcome.NoSchoolResolved.

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
        ev.AttendanceMode = ResolveMode(request.AttendanceMode);
    }

    /// <summary>
    /// De-duplicates a requested id list. A UI that lets an organizer tick the same section through two
    /// different pickers sends it twice, and hitting a unique index with that would be a 500 where the
    /// obvious thing is to attach it once.
    ///
    /// <para>
    /// <b><c>Guid.Empty</c> used to be filtered out here, and removing that filter is the fix.</b>
    /// Dropping it silently meant a payload of nothing but empty GUIDs became an empty list, and because
    /// the requested count was taken <em>after</em> the drop, the caller was told "Saved, 0 attached"
    /// — a success for a request that named nothing that exists. It now falls through to the same
    /// existence check every other id faces and comes back as <c>UnknownReference</c> naming the zero
    /// GUID, because a client sending one has a bug and being told about it is the point. No special
    /// case is needed for that: <c>Guid.Empty</c> matches no row, since every id in this schema comes
    /// from <c>Guid.NewGuid()</c>.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Guid> Distinct(IReadOnlyList<Guid>? ids) =>
        ids is null ? [] : ids.Distinct().ToList();

    /// <summary>
    /// Which of the attendance-defining fields this request would change. Empty when the request leaves
    /// all of them exactly as they are, which is what a name-and-description edit sent as a full PUT
    /// looks like — so the narrow allowance on a <c>Cancelled</c> event costs a well-behaved caller
    /// nothing.
    ///
    /// <para>
    /// <c>StartAt</c> and <c>EndAt</c> are compared after normalizing, because the same instant arrives
    /// with different <c>Kind</c>s from one JSON body — <c>"2026-08-01T09:00:00Z"</c> and
    /// <c>"2026-08-01T12:00:00+08:00"</c> — and comparing those raw would report a change that is not
    /// one, rejecting an edit that altered nothing. <c>UtcTime</c> records the whole trap.
    /// <c>AttendanceMode</c> is compared after resolving through <see cref="ResolveMode"/> for the same
    /// reason: null and <c>""</c> both mean <c>Single</c>, and only the resolved value is what
    /// <see cref="Apply"/> would store.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> AttendanceRuleChanges(EventWriteRequest request, Event ev)
    {
        var changed = new List<string>();

        if (UtcTime.Normalize(request.StartAt) != ev.StartAt) changed.Add(nameof(Event.StartAt));
        if (UtcTime.Normalize(request.EndAt) != ev.EndAt) changed.Add(nameof(Event.EndAt));
        if (request.GraceMinutes != ev.GraceMinutes) changed.Add(nameof(Event.GraceMinutes));
        if (ResolveMode(request.AttendanceMode) != ev.AttendanceMode) changed.Add(nameof(Event.AttendanceMode));
        if (request.RequireRegistration != ev.RequireRegistration)
            changed.Add(nameof(Event.RequireRegistration));

        return changed;
    }

    /// <summary>
    /// §4.5's <c>AttendanceMode</c> default, in one place so <see cref="Apply"/> and
    /// <see cref="AttendanceRuleChanges"/> cannot disagree about what a null or blank mode resolves to.
    /// If they did, a request omitting the field would read as a change to a locked event and be
    /// refused for a field the caller never mentioned.
    /// </summary>
    private static string ResolveMode(string? requested) =>
        AttendanceMode.TryNormalize(requested, out var mode) ? mode : AttendanceMode.Single;

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
