using System.Text.Json;
﻿using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Services;

internal sealed class AttendanceService : IAttendanceService
{
    private readonly EamsDbContext _db;

    /// <summary>
    /// Who is performing a manual override. Null for the whole of the pre-auth build — see
    /// <see cref="ICurrentUser"/> for why the seam is here before there is anything to read from it.
    /// </summary>
    private readonly ICurrentUser _currentUser;

    /// <summary>
    /// Which device is making this request, from the authenticated principal (Phase 4a design, D-26).
    /// Null on every path that is not a device: the organizer override, an import, a test that does
    /// not care. See <see cref="IDeviceContext"/> for why the body's <c>deviceId</c> is a cross-check
    /// and not the source of truth.
    /// </summary>
    private readonly IDeviceContext _device;

    /// <summary>
    /// Only ever used to report a §4.13 capture setting this service could not read as a number —
    /// see <see cref="ResolveCaptureSettingsAsync"/> — and a suppressed-scan audit row it could not write. A malformed setting falls back to the published
    /// default, which is the right behaviour and a silent one, so it says so out loud.
    /// </summary>
    private readonly ILogger<AttendanceService> _logger;

    public AttendanceService(
        EamsDbContext db, ICurrentUser currentUser, IDeviceContext device,
        ILogger<AttendanceService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _device = device;
        _logger = logger;
    }

    private static AttendanceDto ToDto(AttendanceRecord a) => new(
        a.Id, a.EventId, a.StudentId,
        a.Student?.FullName ?? "", a.Student?.StudentNumber ?? "",
        a.CheckInAt, a.CheckOutAt, a.Status, a.CaptureMethod);

    /// <inheritdoc cref="IAttendanceService.ListAsync"/>
    /// <remarks>
    /// <para>
    /// Counted and paged through <c>PagedQuery.ToPageAsync</c>, the seam all nine admin lists share,
    /// so the total cannot be answering a different filter than the rows. <c>Include(Student)</c> goes
    /// in the ordering callback and so lands on the page query alone — the count has no use for the
    /// join.
    /// </para>
    ///
    /// <para>
    /// <b><c>ThenBy(Id)</c> is load-bearing here in a way it is not on the other lists.</b>
    /// <c>CheckInAt</c> is nullable and <c>ChangeStatusAsync</c>'s close materializes one
    /// <c>Absent</c>/<c>Import</c> row per un-tapped invitee, all with it null — so a closed
    /// institution-wide event produces thousands of rows whose entire sort key is identical. Without a
    /// unique tiebreaker SQL Server may order that block differently on each request, and consecutive
    /// pages then overlap and skip.
    /// </para>
    ///
    /// <para>
    /// <b>The QA Q7 text filters are each a <c>LIKE '%fragment%'</c>, so none of them can seek an
    /// index</b> — the same trade <c>StudentService.SearchCardsAsync</c> records. It is accepted for the
    /// same reasons: a human types it, it is almost always paired with <c>eventId</c> (which does seek),
    /// and the page is bounded. EF's <c>string.Contains</c> escapes <c>%</c>, <c>_</c> and <c>[</c> in
    /// the parameter itself, so the fragments are passed through untouched. Nothing here needs more
    /// than SQL Server 2012 offers: it is <c>LIKE</c>, <c>+</c> and <c>COALESCE</c>.
    /// </para>
    /// </remarks>
    public Task<PagedResult<AttendanceDto>> ListAsync(
        Guid? eventId, Guid? studentId, string? status, PageRequest page,
        AttendanceListSearch? search = null, CancellationToken ct = default)
    {
        var q = _db.AttendanceRecords.AsQueryable();
        if (eventId is not null) q = q.Where(a => a.EventId == eventId);
        if (studentId is not null) q = q.Where(a => a.StudentId == studentId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status);
        if (search is not null) q = ApplySearch(q, search);

        return q.ToPageAsync(
            ordered => ordered
                .Include(a => a.Student)
                .OrderByDescending(a => a.CheckInAt).ThenBy(a => a.Id),
            ToDto, page, ct);
    }

    /// <summary>
    /// Narrows <paramref name="q"/> by Live Attendance's three text filters (QA Q7), AND-combined.
    /// Every predicate is on the attendance row or its own navigations, so a student without a row can
    /// never be produced here — the filters only remove rows, they never join students in.
    /// </summary>
    private static IQueryable<AttendanceRecord> ApplySearch(
        IQueryable<AttendanceRecord> q, AttendanceListSearch search)
    {
        if (!string.IsNullOrWhiteSpace(search.StudentNumber))
        {
            // Verbatim, not card-normalized: the registrar's value keeps its dashes, and '2023-0001'
            // has to go on matching itself.
            var number = search.StudentNumber.Trim();
            q = q.Where(a => a.Student!.StudentNumber.Contains(number));
        }

        if (!string.IsNullOrWhiteSpace(search.StudentName))
        {
            // Student.FullName is computed in the domain and ignored by the model, so it cannot be
            // queried. Its two possible SQL shapes are spelled out instead: 'First Last' when there is
            // no middle name and 'First Middle Last' when there is. Each part is also matched alone,
            // which the joined forms already cover but which keeps a surname search obviously correct.
            // Case-insensitivity is the column collation's (SQL Server's default is CI), exactly as on
            // GET /students.
            var name = search.StudentName.Trim();
            q = q.Where(a =>
                a.Student!.FirstName.Contains(name)
                || a.Student.LastName.Contains(name)
                || (a.Student.MiddleName != null && a.Student.MiddleName.Contains(name))
                || (a.Student.FirstName + " " + a.Student.LastName).Contains(name)
                || (a.Student.MiddleName != null
                    && (a.Student.FirstName + " " + a.Student.MiddleName + " " + a.Student.LastName)
                        .Contains(name)));
        }

        if (!string.IsNullOrWhiteSpace(search.CardUid))
        {
            // Normalize first, compare second (CLAUDE.md). An empty normal form is refused rather than
            // compared, because Contains("") is true of every carded row: '-' would return every RFID
            // tap looking exactly like a result.
            var uid = CardUid.Normalize(search.CardUid);
            if (uid.Length == 0)
            {
                throw new ArgumentException(
                    $"The card fragment '{search.CardUid}' contains no letter or digit, so it normalizes " +
                    "to nothing and would match every carded row. The HTTP boundary refuses this with a " +
                    "400; a caller reaching here skipped that check.",
                    nameof(search));
            }

            // The card that made THIS tap (JJ's ruling on Q7) — the row's own RfidCardId, not any card
            // the student holds now or held before. A row with no card (a manual entry, an import
            // absence) has nothing to match and drops out, which is the intended answer.
            q = q.Where(a => a.RfidCard != null && a.RfidCard.CardUid.Contains(uid));
        }

        return q;
    }

    // Technical Plan §6.4. The whole capture decision lives here in one place: resolve UID →
    // validate the event window → validate the device → idempotency check → upsert → compute
    // Present/Late. Splitting it behind a repository would scatter a single transactional decision
    // across layers.
    //
    // The public entry point takes no window cache: a single tap resolves its §4.13 settings once and
    // has nothing to reuse them across. TapBatchAsync supplies one — see the overload below.
    public Task<TapResponse> TapAsync(TapRequest req, CancellationToken ct = default) =>
        TapAsync(req, settingsCache: null, ct);

    /// <inheritdoc cref="TapAsync(TapRequest, CancellationToken)"/>
    /// <param name="settingsCache">
    /// A per-call memo of resolved §4.13 capture settings (the D-36 tap window and the B6 minimum tap
    /// interval), keyed by school, or null to resolve every time.
    /// Phase 4d's carry-over from 4c: <see cref="ResolveCaptureSettingsAsync"/> is one settings query per tap
    /// and a 200-row batch would run it 200 times, identically. Supplied by
    /// <see cref="TapBatchAsync"/> and by nothing else.
    ///
    /// <para>
    /// <b>Keyed by school rather than by event, and it must stay that way.</b> The window is a §4.13
    /// per-school setting, so two events in one school share an entry and two schools never do. Keying
    /// on the event would be correct and would miss most of the saving; keying on nothing — resolving
    /// once for the batch and reusing it regardless — would apply one school's window to another
    /// school's event, which is unreachable through an authenticated device today and is exactly the
    /// kind of "unreachable" this service has already been wrong about once.
    /// </para>
    ///
    /// <para>
    /// <b>It is a parameter and not a field.</b> Its correct lifetime is one batch: a field on this
    /// scoped service would outlive the call and start answering with a window an administrator had
    /// since changed, and nothing about that would look wrong at the call site.
    /// </para>
    /// </param>
    private async Task<TapResponse> TapAsync(
        TapRequest req, Dictionary<Guid, CaptureSettings>? settingsCache, CancellationToken ct)
    {
        // Read once, at entry, and used for three things that must agree: the fallback timestamp when
        // the client sends none, the D-36 future-tolerance comparison, and the `serverTime` every
        // response carries. Reading DateTime.UtcNow separately at each would let a rejection quote a
        // clock a few milliseconds away from the one it rejected against — small, and exactly the kind
        // of inconsistency a client computing a clock offset would be entitled to complain about.
        var serverTime = DateTime.UtcNow;

        // TappedAt arrives from JSON, so its Kind depends on how the client wrote the string: "Z"
        // gives Utc, "+08:00" gives Local, a bare date-time gives Unspecified. Comparing a Local
        // value against ev.StartAt (Utc) below would misjudge Present vs Late by the server's
        // offset — eight hours, in Manila. Normalize once, here, at the boundary.
        var when = UtcTime.Normalize(req.TappedAt) ?? serverTime;

        // D-26. The principal wins on the write; the body is only allowed to agree with it.
        //
        // Overwriting a mismatched body value silently would be *safe* — the row would still record
        // the device that actually tapped — and it would still be wrong: the client would go on
        // believing it recorded a tap against a device the row does not name, and the idempotency key
        // it retries on is scoped by device. The same reasoning the students write surface applies to a
        // derived field echoed back on a PUT. Refuse, name the token, let the client fix its bug.
        if (req.DeviceId is { } claimed && _device.DeviceId is { } authenticated && claimed != authenticated)
        {
            return Reject(
                TapOutcome.DeviceMismatch,
                "The deviceId in the request body is not the device this key authenticates. Send the " +
                "authenticated device's id, or omit the field.",
                serverTime);
        }

        // The other half of the same payload guard, and it closes a 500 (Phase 4d review, CRITICAL).
        //
        // DeviceTapId is client-supplied and lands in nvarchar(100). EF sizes an over-length string as
        // nvarchar(max) rather than clipping it, so it reached SQL Server and returned error 8152/2628 —
        // a truncation, not a unique violation, so SaveNewRecordAsync's `when` filter correctly declines
        // it and it propagated unhandled. Identical in mechanism to the Notes truncation this service
        // already closed on the override path; see DeviceTapIds for why the batch endpoint made it
        // urgent rather than tidy.
        //
        // Only the *length* is enforced here. Blank still means "absent" on this endpoint, exactly as
        // before — the single tap has always permitted a caller to omit an idempotency key, and turning
        // an empty string into a rejection would break every client built against that. The batch
        // endpoint applies the whole of DeviceTapIds.IsUsable, because D-33 makes the field required
        // there.
        //
        // The token is the published DeviceTapIdRequired rather than a new one: its documented client
        // instruction is "Stop; bug on your side", which is exactly right for a key we cannot store, and
        // the frozen table needs no revision to say so.
        if (!string.IsNullOrWhiteSpace(req.DeviceTapId) && !DeviceTapIds.IsUsable(req.DeviceTapId))
        {
            return Reject(
                TapOutcome.DeviceTapIdRequired,
                $"deviceTapId is {req.DeviceTapId.Length} characters; the maximum is " +
                $"{DeviceTapIds.MaxLength}. Nothing was recorded. Shorten the key your client " +
                "generates — it is stored as the idempotency key and cannot be truncated without " +
                "silently breaking replay.",
                serverTime);
        }

        // The principal first, deliberately. The two operands are equal or one is null by the time this
        // runs — the guard above is what guarantees it — so the order is behaviourally identical today
        // and reads backwards written the other way round. Relax or move that guard and
        // `req.DeviceId ?? _device.DeviceId` would silently let the body win, which is the one thing
        // D-26 says it must never do. Stating the invariant in the expression means it cannot invert.
        var deviceId = _device.DeviceId ?? req.DeviceId;

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId && !e.IsDeleted, ct);
        if (ev is null)
            return Reject(TapOutcome.EventNotFound, "Event not found.", serverTime);
        if (ev.Status != EventStatus.Open)
            return Reject(TapOutcome.EventNotOpen, $"Event is {ev.Status}, not Open.", serverTime);

        // D-36, both halves, and they run here for a reason: after the event is resolved (the window
        // check needs its StartAt/EndAt) and before the card is resolved, which keeps the ordering
        // TapFlowTests already pins — event state is reported before card resolution, so a client with
        // two problems is told about the one it can act on first.
        //
        // Neither of these rewrites `when`. See TapTimeWindow for why clamping was refused.
        if (TapTimeWindow.IsImplausiblyFuture(when, serverTime))
        {
            return Reject(
                TapOutcome.TappedAtOutOfRange,
                $"tappedAt {when:O} is more than {TapTimeWindow.FutureToleranceMinutes} minutes ahead " +
                $"of the server clock ({serverTime:O}). Correct the device clock using the serverTime " +
                "in this response and resend.",
                serverTime);
        }

        var settings = await ResolveCaptureSettingsAsync(ev.SchoolId, settingsCache, ct);
        var window = settings.Window;
        if (!window.Contains(when, ev.StartAt, ev.EndAt))
        {
            var (from, to) = window.BoundsFor(ev.StartAt, ev.EndAt);
            return Reject(
                TapOutcome.TappedAtOutsideEventWindow,
                $"tappedAt {when:O} is outside this event's window ({from:O} to {to:O}).",
                serverTime);
        }

        var uid = CardUid.Normalize(req.CardUid);

        // `!c.Student!.IsDeleted` is the fix for a tap path that disagreed with every other read.
        // StudentService filters soft-deleted students on all three of its queries; this one
        // resolved the student through the card and did not, so a student the roster says does not
        // exist could tap, be counted in the event summary, and have their name handed back to the
        // client. One predicate makes the capture path agree with the reads.
        //
        // Deliberately *not* also filtering on Student.Status, and this is now a settled decision
        // rather than an open one. It used to read "a product decision the plan does not make".
        //
        // Phase 3a dissolved the question instead of answering it. An event's expected attendees come
        // from Enrollments in a term (via the §4.8 audience and the derived section groups), so a
        // graduated student — who has no current-term enrollment — is never in a roster, never in the
        // denominator, and never in an absentee list. Whether their card happens to open a turnstile
        // is a separate matter from whether the institution expected them, and the two were being
        // conflated. If they do tap, the row exists and the roster lists them with
        // isExpected = false, which is a truthful record of an alumnus who turned up.
        //
        // Rejecting the tap here would instead discard evidence that someone was physically present,
        // to protect a number that no longer depends on it. See
        // KnownDefectTests.A_student_with_no_enrollment_is_not_an_expected_attendee.
        var card = await _db.RfidCards.Include(c => c.Student)
            .FirstOrDefaultAsync(c => c.CardUid == uid && c.IsActive && !c.Student!.IsDeleted, ct);
        if (card?.Student is null)
        {
            await LogUnresolvedScanAsync(ev, req, uid, when, serverTime, ct);
            return Reject(TapOutcome.CardNotFound, $"No active card matches UID {uid}.", serverTime);
        }

        var student = card.Student;

        // DeviceId is a foreign key and was previously written unchecked, so an unknown one became
        // an FK violation that IsUniqueViolation correctly declines to swallow — an unhandled 500.
        // §8.2 treats 5xx as retryable, so a re-provisioned handset holding a stale id retried every
        // queued tap forever and the queue never drained. Validated here, before the write, and
        // reported as a rejection the client can act on.
        //
        // D-27 — DEFECT 3, closed here. The check is on the device's *own* SchoolId against the
        // event's, not on whatever the query filter happens to be doing. Device authentication already
        // makes the ordinary case impossible (an authenticated device's school pins the tenant, so a
        // foreign event is simply EventNotFound), but that is a property of the request pipeline, and
        // this method is called directly by the import path and by tests with no tenant pinned at all.
        // Defence in depth: a tenant-owned foreign key arriving on a write is validated explicitly
        // rather than by whichever filter is in force.
        //
        // It reuses DeviceNotRegistered → 404 rather than introducing a 403. A distinct status would
        // confirm to the caller that the device exists in some other school, which is a cross-tenant
        // existence disclosure; and the published mobile contract already defines this token as
        // "unknown device, or device belongs to another school". No new outcome, no wire change.
        if (deviceId is { } tappingDeviceId)
        {
            var deviceSchoolId = await _db.Devices
                .Where(d => d.Id == tappingDeviceId)
                .Select(d => (Guid?)d.SchoolId)
                .FirstOrDefaultAsync(ct);

            if (deviceSchoolId != ev.SchoolId)
            {
                return Reject(
                    TapOutcome.DeviceNotRegistered,
                    $"Device {tappingDeviceId} is not registered.",
                    serverTime);
            }
        }

        // Idempotency (§4.9, §8.2): the same tap replayed by the same device returns the record it
        // already produced. The key is the *pair* (DeviceId, DeviceTapId), matching
        // UX_Attendance_Device_DeviceTapId exactly — see FindByDeviceTapAsync for why both halves
        // matter and why the plan's §1 wording is not the one followed here.
        if (!string.IsNullOrWhiteSpace(req.DeviceTapId))
        {
            var dup = await FindByDeviceTapAsync(deviceId, req.DeviceTapId, ct);
            if (dup is not null) return Duplicate(dup, serverTime);
        }

        var existing = await FindByEventStudentAsync(ev.Id, student.Id, ct);

        if (existing is null)
        {
            var status = when <= ev.StartAt.AddMinutes(ev.GraceMinutes)
                ? AttendanceStatus.Present
                : AttendanceStatus.Late;

            var rec = new AttendanceRecord
            {
                // D-35. Denormalized from the event that was just read, which is the only source it
                // ever has — see AttendanceRecord.SchoolId.
                SchoolId = ev.SchoolId,
                EventId = ev.Id, StudentId = student.Id, RfidCardId = card.Id,
                CheckInAt = when, Status = status, CaptureMethod = CaptureMethod.Rfid,
                DeviceId = deviceId, DeviceTapId = req.DeviceTapId,
                Student = student,
            };
            _db.AttendanceRecords.Add(rec);

            var saved = await SaveNewRecordAsync(rec, deviceId, req.DeviceTapId, ct);
            return saved.Outcome switch
            {
                SaveOutcome.Inserted =>
                    Accept(TapOutcome.Recorded, $"Checked in ({status}).", rec, serverTime),
                SaveOutcome.DuplicateTapWon => Duplicate(saved.Record, serverTime),

                // Insert-race re-dispatch (P7, JJ-approved). Another tap of this card created the row
                // between our read and our insert. In TimeInOut that other tap was the check-in, so this
                // one is a LATER tap and is judged against it exactly as if it had arrived a moment
                // later: inside the interval it is a double tap, beyond it a check-out. Returning
                // AlreadyRecorded here, as this used to, silently lost a genuine check-out.
                _ when ev.AttendanceMode == AttendanceMode.TimeInOut => await ApplyLaterTapAsync(
                    ev, req, uid, deviceId, saved.Record, when, settings.MinTapInterval, serverTime, ct),
                _ => AlreadyRecorded(saved.Record, serverTime),
            };
        }

        // Single mode: a later tap never changes the row, so the interval rule has nothing to guard and
        // the answer is AlreadyRecorded exactly as before - the wire is unchanged for Single events.
        //
        // The replay re-check applies here too (P7 rework, W1): a Single-mode retry whose original
        // committed between the replay check above and the row read is DuplicateIgnored, not
        // AlreadyRecorded - see ReplayOfThisTapAsync.
        if (ev.AttendanceMode != AttendanceMode.TimeInOut)
            return await ReplayOfThisTapAsync(req, deviceId, serverTime, ct) ?? AlreadyRecorded(existing, serverTime);

        return await ApplyLaterTapAsync(
            ev, req, uid, deviceId, existing, when, settings.MinTapInterval, serverTime, ct);
    }

    /// <summary>
    /// The replay check, asked again from the database, on the way to any answer that writes nothing
    /// for a row that already exists.
    ///
    /// <para>
    /// The replay check at the top of <c>TapAsync</c> ran before the row was read, so a retry of THIS
    /// tap whose original committed in between was missed there. Judged on the row alone it is then a
    /// zero-distance double tap (TimeInOut) or a second tap (Single). A retry of a counted tap is
    /// <c>DuplicateIgnored</c>, never <c>TooSoonIgnored</c> or <c>AlreadyRecorded</c>. Found by the P7
    /// flakiness runs; pinned by
    /// <c>TapIntervalFlowTests.A_retry_whose_original_commits_during_the_replay_check_is_DuplicateIgnored</c>.
    /// </para>
    /// </summary>
    private async Task<TapResponse?> ReplayOfThisTapAsync(
        TapRequest req, Guid? deviceId, DateTime serverTime, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceTapId)) return null;
        var original = await FindByDeviceTapAsync(deviceId, req.DeviceTapId, ct);
        return original is null ? null : Duplicate(original, serverTime);
    }

    /// <summary>
    /// The upper bound on compare-and-set attempts for one check-out. Not a tuning knob: every lost
    /// attempt means another tap of the same card committed a strictly later check-out, so the number of
    /// losses is bounded by the number of concurrent taps of one card. Reaching this is a bug in that
    /// argument, and it fails loudly rather than looping.
    /// </summary>
    private const int MaxCheckOutAttempts = 16;

    /// <summary>
    /// A tap of a card whose row already exists on a <c>TimeInOut</c> event: the B6 interval guard, then
    /// the Q5 last-tap-wins rule, then a compare-and-set write. The replay check has already run -
    /// a retry of a counted tap never reaches here - which is what keeps <c>DuplicateIgnored</c> ahead
    /// of <c>TooSoonIgnored</c>.
    ///
    /// <para>
    /// <b>1. Too soon (client QA #470 B6).</b> Strictly less than the interval from the row's check-in
    /// or its check-out, measured as an absolute distance so arrival order does not matter. Nothing is
    /// written to attendance; an <c>attendance.scan.suppressed</c> audit row records it.
    /// </para>
    ///
    /// <para>
    /// <b>2. Last tap wins, forward only (client QA #472 Q5).</b> A tap moves <c>CheckOutAt</c> only
    /// when it is later than the latest of the row's check-in and check-out - and, having passed step
    /// 1, therefore at least the interval later. A stale or out-of-order tap never moves it back and is
    /// <c>AlreadyRecorded</c>. So <c>CheckOutAt</c> now means "the latest accepted tap", the last one is
    /// final when capture stops (<c>EventNotOpen</c> once closed, <c>TappedAtOutsideEventWindow</c>
    /// after <c>EndAt</c> + <c>afterEndMinutes</c>), and each move overwrites
    /// <c>CheckOutDeviceTapId</c> - so replaying an <em>intermediate</em> check-out is no longer
    /// recognised by tap id and lands here as <c>AlreadyRecorded</c>, moving nothing. Published.
    /// </para>
    ///
    /// <para>
    /// <b>3. Compare-and-set, not a concurrency token.</b> <c>UPDATE ... WHERE Id = @id AND CheckOutAt
    /// = @observed</c> (or <c>IS NULL</c>): the write lands only if nobody moved the check-out since
    /// this request read it. Zero rows means someone did - so reload and decide again against the
    /// fresh row. Before any answer that writes nothing, the replay check is asked again from the
    /// database, because the one at the top of <c>TapAsync</c> can miss a retry of this very tap whose
    /// original committed a moment later. Making
    /// <c>CheckOutAt</c> or <c>RowVersion</c> a concurrency token instead would reverse D-30 and change
    /// <c>ManualAsync</c>'s write semantics. <c>ExecuteUpdateAsync</c> bypasses the change tracker, so
    /// the tracked row is reloaded after every attempt before anything is built from it.
    /// </para>
    /// </summary>
    private async Task<TapResponse> ApplyLaterTapAsync(
        Event ev, TapRequest req, string uid, Guid? deviceId, AttendanceRecord row,
        DateTime when, TimeSpan interval, DateTime serverTime, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var anchors = TapAnchor.From(row.CheckInAt, row.CheckOutAt);
            if (TapInterval.IsTooSoon(when, anchors, interval, out var anchor))
            {
                if (await ReplayOfThisTapAsync(req, deviceId, serverTime, ct) is { } replayed) return replayed;

                await LogSuppressedScanAsync(ev, req, uid, deviceId, row.StudentId, when, anchor, interval, serverTime, ct);
                return Accept(
                    TapOutcome.TooSoonIgnored,
                    $"Tap ignored: this card was counted {(when - anchor.At).Duration().TotalSeconds:0.###}s " +
                    $"from its {anchor.Kind} tap; the minimum interval is {interval.TotalSeconds:0.###}s.",
                    row, serverTime);
            }

            if (Latest(row.CheckInAt, row.CheckOutAt) is { } latest && when <= latest)
                return await ReplayOfThisTapAsync(req, deviceId, serverTime, ct) ?? AlreadyRecorded(row, serverTime);

            var observed = row.CheckOutAt;
            var target = _db.AttendanceRecords.Where(a => a.Id == row.Id);
            target = observed is { } seen
                ? target.Where(a => a.CheckOutAt == seen)
                : target.Where(a => a.CheckOutAt == null);

            // Read here, not inside the setter: EF translates DateTime.UtcNow in an ExecuteUpdate lambda
            // to the server's GETUTCDATE() - a datetime, not datetime2, on the database's clock - which
            // would make this the one UpdatedAt in the service not stamped by the application.
            var updatedAt = DateTime.UtcNow;

            int written;
            try
            {
                // D-34 still holds: the check-out's own idempotency key goes in its own column, so the
                // check-in's DeviceTapId stays replayable. Last-tap-wins overwrites it on each move.
                written = await target.ExecuteUpdateAsync(set => set
                    .SetProperty(a => a.CheckOutAt, when)
                    .SetProperty(a => a.CheckOutDeviceTapId, req.DeviceTapId)
                    .SetProperty(a => a.UpdatedAt, updatedAt), ct);
            }
            catch (Exception ex) when (SqlServerErrors.IsUniqueViolation(ex))
            {
                // UX_Attendance_Device_CheckOutDeviceTapId: this device already used this tap id as a
                // check-out on another row, landed between the replay check and this write. The
                // statement bypasses SaveChanges, so the provider's SqlException arrives unwrapped -
                // which is why the Exception overload is the one matched.
                var winner = await ResolveLostCheckOutRaceAsync(ex, row.Id, deviceId, req.DeviceTapId, ct);
                return Duplicate(winner, serverTime);
            }

            await _db.Entry(row).ReloadAsync(ct);
            if (written == 1) return Accept(TapOutcome.CheckedOut, "Checked out.", row, serverTime);

            // Lost the compare-and-set: somebody moved the check-out. Decide again against the fresh
            // row. If the tap that moved it was a retry of this one, the next pass lands on a no-write
            // answer (it is zero distance from the check-out it wrote) and the replay re-check there
            // turns it into DuplicateIgnored.
            if (attempt >= MaxCheckOutAttempts)
            {
                throw new InvalidOperationException(
                    $"Recording a check-out on attendance {row.Id} lost the compare-and-set " +
                    $"{MaxCheckOutAttempts} times. Each loss should mean another tap moved the check-out " +
                    "strictly forward, so this many losses breaks that argument.");
            }
        }

        static DateTime? Latest(DateTime? checkInAt, DateTime? checkOutAt) =>
            checkInAt is { } a && checkOutAt is { } b ? (a > b ? a : b) : checkInAt ?? checkOutAt;
    }

    // ---------------------------------------------------------------------- §8.2 batch sync (D-31)

    /// <summary>
    /// <inheritdoc cref="IAttendanceService.TapBatchAsync" path="/summary/para[1]"/>
    ///
    /// <para>
    /// <b>Every row goes through <see cref="TapAsync"/>. There is no batch tap logic.</b> The only two
    /// things this method decides are the two the single endpoint has no opinion about — the order rows
    /// are applied in, and that a batch row must carry an idempotency key. Everything else (event
    /// window, device ownership, duplicate detection, check-in versus check-out, Present versus Late) is
    /// the same code producing the same <see cref="TapResponse"/>, which is what makes "the same tap
    /// through /tap and through /tap/batch produces an identical outcome and an identical row" a
    /// property of the structure rather than of two implementations agreeing.
    /// </para>
    ///
    /// <para>
    /// <b>Per row, not per batch: there is no explicit transaction around this loop.</b> Each
    /// <c>SaveChanges</c> is already its own transaction, so the unit of atomicity is one tap.
    /// </para>
    ///
    /// <para>
    /// <b>The three arguments that read most persuasively for this are all wrong, and they are written
    /// down because the next person will reach for them too.</b> Each was tested by wrapping this loop
    /// in <c>BeginTransactionAsync</c> and running the suite; all nineteen batch tests passed.
    /// <list type="number">
    ///   <item>"A bad row would roll back the good rows." It would not. A rejected row is a
    ///   <em>result</em>, not an exception — <c>CardNotFound</c> never reaches the database — so nothing
    ///   aborts anything. Only a thrown exception rolls a batch back, and that is the <c>5xx</c> path.</item>
    ///   <item>"<see cref="SaveNewRecordAsync"/>'s unique-violation recovery cannot run inside a
    ///   transaction, because the failed statement dooms it." It can. With <c>XACT_ABORT</c> off — the
    ///   default — SQL Server treats a 2601 as a <em>statement</em> abort: <c>XACT_STATE()</c> stays 1,
    ///   the re-read succeeds and the transaction commits. Verified directly against SQL Server 2022
    ///   rather than assumed.</item>
    ///   <item>"Per-row commits are what let a duplicate <c>deviceTapId</c> inside one batch resolve,
    ///   because the second row finds the first already committed." Also not the mechanism. Both rows
    ///   run on one connection inside one transaction, so the second row's pre-check reads the first
    ///   row's uncommitted insert anyway — read-your-own-writes, not commit visibility.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>The reason that survives is lock duration.</b> Under one transaction, every row's exclusive
    /// row lock and its entries in two filtered unique indexes are held until the last of up to two
    /// hundred rows is written — so an ordinary single tap arriving from another device on the same
    /// event blocks behind an entire queue flush. Per-row commits hold each lock for one write. The
    /// secondary reason is that a mid-batch exception under one transaction discards taps that were
    /// recorded correctly, which the client then has to re-send; per row they survive and come back
    /// <c>DuplicateIgnored</c>. Neither is a correctness argument, and neither is claimed as one.
    /// </para>
    ///
    /// <para>
    /// <b>What actually makes the retry safe is idempotency, not atomicity</b> — which is also why the
    /// absence of a batch transaction costs nothing. Every row is keyed by <c>deviceTapId</c> and
    /// guarded by <c>UX_Attendance_Device_DeviceTapId</c>, so resending the whole batch converges on the
    /// same rows however much of it landed the first time.
    /// </para>
    ///
    /// <para>
    /// <b>Honest caveat on the published "a 5xx means nothing was committed".</b> With per-row commits
    /// that is not literally true: an exception escaping at row 50 leaves rows 1–49 committed and
    /// returns a 500. The client's prescribed behaviour — retry the whole batch — is still correct and
    /// still safe, for the reason in the paragraph above, and no tap is lost or duplicated. But the
    /// guarantee the document states is stronger than the one this shape provides, and the two can only
    /// be reconciled by softening the wording or by giving a row a server-error token (there is none in
    /// the frozen table). Raised with the document's owner rather than papered over here.
    /// </para>
    /// </summary>
    public async Task<TapBatchResponse> TapBatchAsync(
        TapBatchRequest req, CancellationToken ct = default)
    {
        // One clock read for the envelope, matching TapAsync's reasoning: a batch that quoted several
        // serverTimes would give a client computing an offset several answers.
        var serverTime = DateTime.UtcNow;
        var taps = req.Taps ?? [];

        if (taps.Count > TapBatchLimits.MaxRows)
        {
            // Nothing is processed and nothing is written. Deliberately checked before the loop rather
            // than by truncating to the first 200: silently dropping the tail would return a 200 whose
            // results say every submitted row landed, and the client would delete the ones that did not.
            return new TapBatchResponse(
                Reject(
                    TapOutcome.BatchTooLarge,
                    $"This batch carries {taps.Count} rows; the limit is {TapBatchLimits.MaxRows}. " +
                    "Nothing was recorded. Split the queue into chunks of at most that many rows and " +
                    "resend.",
                    serverTime),
                [],
                serverTime);
        }

        LogClockSkew(req.ClientClockAt, serverTime, taps.Count);

        // Resolved lazily and shared across the whole loop. Empty when the batch is empty, one entry
        // in every realistic batch — an authenticated device's taps all resolve to its own school.
        var settingsCache = new Dictionary<Guid, CaptureSettings>();

        // Indexed by request position, filled in processing order. That is what makes results[i].index
        // == i without the loop having to run in array order.
        var rows = new TapBatchRow[taps.Count];

        // ---------------------------------------------------------------- the malformed-row floor
        //
        // Every row is shape-checked BEFORE anything is sorted or written, and the ordering of the two
        // passes is the fix rather than an optimization (Phase 4d review). InProcessingOrder reads
        // row.Tap.TappedAt, so a null element in `taps` — which is valid JSON, and which ASP.NET's
        // implicit-required-from-NRT does NOT catch, because that applies to bound parameters and
        // properties and never to collection elements — used to NullReferenceException inside the sort,
        // before a single row had been considered. A batch-level 500, which §8.2 retries forever.
        //
        // Both refusals are per row and both use the published DeviceTapIdRequired: a row that is null
        // has no idempotency key in the most complete sense available, and the token's documented client
        // instruction ("Stop; bug on your side") is the correct one for a row this API will refuse
        // identically on every retry. No new token, no change to the frozen table.
        var wellFormed = new List<(TapRequest Tap, int Index)>(taps.Count);

        for (var index = 0; index < taps.Count; index++)
        {
            if (taps[index] is not { } tap)
            {
                rows[index] = new TapBatchRow(
                    index,
                    DeviceTapId: null,
                    Reject(
                        TapOutcome.DeviceTapIdRequired,
                        "This row is null. Every element of `taps` must be a tap object carrying at " +
                        "least an eventId, a cardUid and a deviceTapId.",
                        serverTime));
                continue;
            }

            // D-33 plus the length bound, as one predicate. Checked here rather than inside TapAsync
            // because only this endpoint makes the field required; TapAsync enforces the length half on
            // its own for the callers that reach it directly.
            if (!DeviceTapIds.IsUsable(tap.DeviceTapId))
            {
                rows[index] = new TapBatchRow(
                    index, tap.DeviceTapId, Reject(TapOutcome.DeviceTapIdRequired, UnusableTapId(tap), serverTime));
                continue;
            }

            wellFormed.Add((tap, index));
        }

        foreach (var (tap, index) in InProcessingOrder(wellFormed, serverTime))
        {
            rows[index] = new TapBatchRow(index, tap.DeviceTapId, await TapAsync(tap, settingsCache, ct));
        }

        return new TapBatchResponse(Refusal: null, rows, serverTime);

        static string UnusableTapId(TapRequest tap) => tap.DeviceTapId is { Length: > DeviceTapIds.MaxLength }
            ? $"deviceTapId is {tap.DeviceTapId.Length} characters; the maximum is " +
              $"{DeviceTapIds.MaxLength}. It is stored as the idempotency key and cannot be truncated " +
              "without silently breaking replay."
            : "Every row of a batch must carry a deviceTapId. A queued tap without an idempotency key " +
              "cannot be safely retried, and this endpoint's whole retry contract is that the batch " +
              "may be resent.";
    }

    /// <summary>
    /// The order rows are applied in: ascending <c>tappedAt</c>, ties broken by array index.
    ///
    /// <para>
    /// <b>Not raw array order, and this is a correctness rule rather than tidiness.</b> An offline queue
    /// can legitimately flush out of insertion order — a retry re-enqueued at the tail, a merge of two
    /// device-local stores, a client that batches by event. If a <c>TimeInOut</c> check-out is applied
    /// before its own check-in, the check-out finds no existing row, takes the <em>create</em> branch,
    /// and writes a check-in stamped at the check-out's time. Nothing errors; the student's arrival time
    /// is simply wrong, by however long they stayed, and it is wrong in the direction that turns a
    /// Present into a Late. Sorting is what makes the endpoint's result independent of the order a queue
    /// happens to hold.
    /// </para>
    ///
    /// <para>
    /// <b>A null <c>tappedAt</c> sorts as <paramref name="serverTime"/>, which is precisely what it
    /// means</b> — "now, on the server" — so it lands after every queued row that named an earlier time
    /// and interleaves correctly with any that named a later one. Inventing a separate nulls-first or
    /// nulls-last rule would be a second definition of the same value.
    /// </para>
    ///
    /// <para>
    /// <b>What sorting cannot fix, stated because the published contract states it:</b> a check-out
    /// whose check-in was in an <em>earlier batch that failed</em> still arrives alone and still lands as
    /// a check-in. Ordering is per request; only flushing in order and stopping at the first retryable
    /// error closes that, and that half belongs to the client.
    /// </para>
    /// </summary>
    /// <param name="taps">
    /// <b>Well-formed rows only.</b> This method dereferences each row, so the caller's shape pass has
    /// to run first — see the malformed-row floor in <see cref="TapBatchAsync"/>, which is where a null
    /// element used to reach this sort and NullReferenceException before any row was processed.
    /// </param>
    private static IEnumerable<(TapRequest Tap, int Index)> InProcessingOrder(
        IReadOnlyList<(TapRequest Tap, int Index)> taps, DateTime serverTime) =>
        taps
            // Normalized first: a row that sent "+08:00" and one that sent "Z" are otherwise sorted
            // eight hours apart from each other for a reason invisible in the payload. Same boundary
            // rule TapAsync applies to the value it stores.
            .OrderBy(row => UtcTime.Normalize(row.Tap.TappedAt) ?? serverTime)
            // LINQ's OrderBy is already stable, so this is a no-op today. It is written because the
            // published contract names the tiebreak explicitly, and a reader should not have to know
            // that stability is guaranteed to see that the rule is honoured.
            .ThenBy(row => row.Index);

    /// <summary>
    /// Records how far the device's clock is from ours, from the one value that measures it cleanly.
    ///
    /// <para>
    /// <b><c>clientClockAt</c> is the only skew signal that is not contaminated by queue latency.</b> A
    /// row's <c>tappedAt</c> is old for two reasons that cannot be told apart — a drifted clock and a
    /// tap that waited three days for a signal — so it can never distinguish a broken device from a
    /// working one. The batch envelope's clock is read at send time, so its difference from ours is skew
    /// and nothing else.
    /// </para>
    ///
    /// <para>
    /// <b>It never refuses anything.</b> The rules that refuse are D-36's, they are per row, and they
    /// apply to <c>tappedAt</c>. Rejecting a batch over its envelope clock would discard rows whose own
    /// timestamps are perfectly acceptable, which is the opposite of what an offline queue needs.
    /// Warning at the same tolerance D-36 uses for the future is not a coincidence: past that point a
    /// device is producing <c>tappedAt</c> values that will start being refused, so this is the log line
    /// that explains the refusals arriving next.
    /// </para>
    /// </summary>
    private void LogClockSkew(DateTime? clientClockAt, DateTime serverTime, int rows)
    {
        if (UtcTime.Normalize(clientClockAt) is not { } clientClock) return;

        var skew = clientClock - serverTime;
        if (Math.Abs(skew.TotalMinutes) <= TapTimeWindow.FutureToleranceMinutes) return;

        _logger.LogWarning(
            "Device clock skew of {SkewSeconds:F0}s on a {Rows}-row batch: the client reported " +
            "{ClientClock:O} while the server clock was {ServerTime:O}. Taps from this device will " +
            "start being refused as {Token} once the skew exceeds what a single tap's own timestamp " +
            "can absorb. The client is expected to correct itself from the serverTime in this response.",
            skew.TotalSeconds, rows, clientClock, serverTime, nameof(TapOutcome.TappedAtOutOfRange));
    }

    // ------------------------------------------------------------------ response shaping (D-37)
    //
    // Every TapResponse in this service is built by one of these four, and none of them takes the
    // token as an argument: TapResponse.For derives `code` from the outcome, so the two fields cannot
    // be made to disagree by a caller passing the wrong string. The two duplicate/already-recorded
    // helpers exist because those bodies are produced from three call sites each and drifted wording
    // between them would be a wire change nobody reviewed.

    private static TapResponse Reject(TapOutcome outcome, string message, DateTime serverTime) =>
        TapResponse.For(outcome, success: false, message, record: null, serverTime);

    private static TapResponse Accept(
        TapOutcome outcome, string message, AttendanceRecord record, DateTime serverTime) =>
        TapResponse.For(outcome, success: true, message, ToDto(record), serverTime);

    private static TapResponse Duplicate(AttendanceRecord record, DateTime serverTime) =>
        Accept(TapOutcome.DuplicateIgnored, "Duplicate tap ignored (idempotent).", record, serverTime);

    private static TapResponse AlreadyRecorded(AttendanceRecord record, DateTime serverTime) =>
        Accept(TapOutcome.AlreadyRecorded, "Already recorded.", record, serverTime);

    /// <summary>
    /// The §4.13 tap-time window for one school (D-36): its own rows if it has them, the global
    /// (<c>NULL SchoolId</c>) rows otherwise, the published defaults otherwise.
    ///
    /// <para>
    /// <b>One query per tap, unconditionally, and the cheap-looking alternative is wrong.</b> Checking
    /// the defaults first and only loading the settings when that check fails would cost nothing on the
    /// happy path — and would silently ignore a school that <em>narrows</em> its window, which is the
    /// direction an administrator tightening a rule would move it. A settings read that only runs when
    /// it would be permissive is not a settings read.
    /// </para>
    ///
    /// <para>
    /// <b>It is a small scan, not a seek, and that is stated rather than glossed.</b> The predicate is
    /// a disjunction on the leading key column of <c>UX_SystemSettings_SchoolId_Key</c>
    /// (<c>SchoolId = @school OR SchoolId IS NULL</c>) and another over <c>Key</c>, so nothing seeks;
    /// §4.13 is a handful of rows per tenant, so it is cheap anyway. Worth saying because "one query"
    /// on its own reads as "one seek".
    /// </para>
    ///
    /// <para>
    /// <b>Phase 4d hoisted it out of the per-row path, which is what 4c said would be needed.</b> A
    /// 200-row batch ran this 200 times, and — per the paragraph above — each run is a small scan
    /// rather than a seek, so "one query per tap" understated it. <paramref name="cache"/> memoizes the
    /// answer per school for the duration of one batch, which collapses those 200 to one in every
    /// realistic batch. The read itself is unchanged: still unconditional, still per school, still
    /// falling through scopes. Only how often it runs moved.
    /// </para>
    ///
    /// <para>
    /// The rows are filtered explicitly on <c>SchoolId</c> rather than relying on the §11 query filter:
    /// this method is reached by the import path and by tests with no tenant pinned, where that filter
    /// is a no-op and every school's settings are visible. Same principle the device-ownership check
    /// above records — a tenant-scoped decision on a write path validates the tenant itself.
    /// </para>
    ///
    /// <para>
    /// A value that is not a non-negative integer falls back to the default <em>and says so</em>. It is
    /// not an exception: a typo in a settings row must not stop every tap on the campus, and the
    /// fallback is the documented behaviour. But a silent fallback would make a mis-typed window
    /// indistinguishable from a working one, which is precisely the class of failure that gets
    /// discovered from an attendance report six weeks later.
    /// </para>
    /// </summary>
    /// <param name="cache">
    /// A per-batch memo, or null on the single-tap path where there is nothing to reuse it across.
    /// Populated on miss, so a school's settings are read at most once per call to
    /// <see cref="TapBatchAsync"/>.
    /// </param>
    private async Task<CaptureSettings> ResolveCaptureSettingsAsync(
        Guid schoolId, Dictionary<Guid, CaptureSettings>? cache, CancellationToken ct)
    {
        if (cache is not null && cache.TryGetValue(schoolId, out var cached)) return cached;

        var resolved = await ReadCaptureSettingsAsync(schoolId, ct);
        cache?.Add(schoolId, resolved);
        return resolved;
    }

    /// <summary>
    /// Everything the capture decision reads from §4.13, resolved together so a batch still reads the
    /// settings table once per school: the D-36 tap window and the B6 minimum tap interval.
    /// </summary>
    private sealed record CaptureSettings(TapTimeWindow Window, TimeSpan MinTapInterval);

    /// <inheritdoc cref="ResolveCaptureSettingsAsync"/>
    private async Task<CaptureSettings> ReadCaptureSettingsAsync(Guid schoolId, CancellationToken ct)
    {
        var rows = await _db.SystemSettings
            .Where(s => (s.SchoolId == schoolId || s.SchoolId == null)
                     && (s.Key == TapTimeWindow.BeforeStartMinutesSettingKey
                      || s.Key == TapTimeWindow.AfterEndMinutesSettingKey
                      || s.Key == TapInterval.MinTapIntervalSecondsSettingKey))
            .Select(s => new { s.SchoolId, s.Key, s.Value })
            .ToListAsync(ct);

        var window = new TapTimeWindow(
            Minutes(TapTimeWindow.BeforeStartMinutesSettingKey, TapTimeWindow.DefaultBeforeStartMinutes),
            Minutes(TapTimeWindow.AfterEndMinutesSettingKey, TapTimeWindow.DefaultAfterEndMinutes));

        // Same precedence and the same loud fall-through as the window, through the domain's pure
        // resolver: school row, then global row, then the default; every unreadable candidate — not a
        // whole number, negative, or above TapInterval's cap — is reported before falling through.
        var interval = TapInterval.Resolve(
            rows.Where(r => r.Key == TapInterval.MinTapIntervalSecondsSettingKey)
                .Select(r => new TapIntervalSettingRow(r.SchoolId.HasValue, r.Value)),
            unreadable => _logger.LogWarning(
                "SystemSettings['{Key}'] for school {SchoolId} is '{Value}', which is not a whole " +
                "number of seconds from 0 to {Max}. Falling through to the next scope, and to the " +
                "published default of {Default}s if there is none.",
                TapInterval.MinTapIntervalSecondsSettingKey,
                unreadable.IsSchoolScope ? (Guid?)schoolId : null,
                unreadable.Value,
                TapInterval.MaxMinTapIntervalSeconds,
                TapInterval.DefaultMinTapIntervalSeconds));

        return new CaptureSettings(window, interval);

        int Minutes(string key, int fallback)
        {
            // The school's own row beats the global one — §4.13's "a NULL SchoolId is the global
            // scope", read as a default rather than as an override. At most two rows reach this loop:
            // UX_SystemSettings_SchoolId_Key allows one per scope.
            //
            // <b>It walks the candidates rather than taking the first one</b>, and that is the whole
            // of the difference between this and the version that shipped to review. Falling straight
            // back to the published constant when the school's row is unreadable *skips the global
            // row that is already in this list* — so a campus that set a global afterEndMinutes of 10
            // to tighten capture, and then fat-fingered one school's override, would silently give
            // that school 60. Six times wider than the rule an administrator deliberately narrowed,
            // and in exactly the permissive direction the unconditional read above exists to prevent.
            // Precedence has to survive a malformed row or it is not precedence.
            foreach (var row in rows
                .Where(r => r.Key == key)
                .OrderByDescending(r => r.SchoolId.HasValue))
            {
                if (TapTimeWindow.TryParseMinutes(row.Value, out var minutes)) return minutes;

                // Every unreadable candidate is reported, not just the last one: "the school row is
                // wrong and we fell through to the global one" and "both are wrong and we fell through
                // to the default" are different operational situations and the log has to tell them
                // apart.
                _logger.LogWarning(
                    "SystemSettings['{Key}'] for school {SchoolId} is '{Value}', which is not a " +
                    "non-negative whole number of minutes. Falling through to the next scope, and to " +
                    "the published default of {Default} if there is none. Taps are being validated " +
                    "against a window nobody configured until the row is corrected.",
                    key, row.SchoolId, row.Value, fallback);
            }

            return fallback;
        }
    }

    // Technical Plan §6.4 — organizer override.
    //
    // This path used to be a second, undefended copy of the tap flow's read-then-write against the
    // same unique index: an inline lookup that forgot `OccurrenceId == null`, and a bare
    // SaveChangesAsync with no unique-violation handling. The race it lost is the *normal* case
    // rather than an exotic one — an organizer overrides a student precisely when that student is
    // walking through the reader — and it surfaced as a 500 with the override discarded. Both write
    // paths now go through FindByEventStudentAsync and SaveNewRecordAsync, so they cannot drift
    // apart again without the divergence being visible in one place.
    public async Task<ManualResponse> ManualAsync(
        Guid eventId, Guid studentId, string status, string? notes, CancellationToken ct = default)
    {
        // Same reasoning as the tap path: one read of the clock, quoted back on every response.
        var serverTime = DateTime.UtcNow;

        // §4.9's value set, validated before anything is read or written. Here rather than in the
        // controller because the service is what writes the column: a guard on the HTTP boundary
        // alone would leave the next caller (the planned import path, §4.12) unprotected.
        if (!AttendanceStatus.TryNormalize(status, out var canonicalStatus))
        {
            return RejectManual(
                ManualOutcome.InvalidStatus,
                $"Status '{status}' is not one of {string.Join(", ", AttendanceStatus.All)}.",
                serverTime);
        }

        // The other half of the same guard. `Notes` is nvarchar(500); an over-length value used to
        // reach SQL Server and come back as error 2628, which surfaced as a 500 because a truncation
        // is not a unique violation and SaveNewRecordAsync rightly declines to swallow it.
        if (!AttendanceNotes.IsValid(notes))
        {
            return RejectManual(
                ManualOutcome.InvalidNotes,
                $"Notes must be {AttendanceNotes.MaxLength} characters or fewer (got {notes!.Length}).",
                serverTime);
        }

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId && !e.IsDeleted, ct);
        if (ev is null)
            return RejectManual(ManualOutcome.EventNotFound, "Event not found.", serverTime);

        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId && !s.IsDeleted, ct);
        if (student is null)
            return RejectManual(ManualOutcome.StudentNotFound, "Student not found.", serverTime);

        var existing = await FindByEventStudentAsync(eventId, studentId, ct);
        if (existing is not null)
        {
            ApplyOverride(existing, canonicalStatus, notes);
            await _db.SaveChangesAsync(ct);
            return Saved(existing);
        }

        var rec = new AttendanceRecord
        {
            // §6.4 calls this endpoint audited, so the row records who wrote it. Null today and
            // permanently null for these rows — nothing can attribute them retroactively once
            // authentication exists, which is exactly why the seam is wired now rather than in
            // Phase 6. See ICurrentUser.
            RecordedByUserId = _currentUser.UserId,
            // D-35, as on the tap path: the tenant comes from the event this row belongs to.
            SchoolId = ev.SchoolId,
            EventId = eventId, StudentId = studentId,
            // §4.9 defines CheckInAt as "First tap (UTC)" and makes it nullable. Absent and Excused
            // are precisely the statuses that assert the student did NOT check in, so stamping a
            // time on them creates a row that contradicts its own status — and the SPA both displays
            // it and orders the attendance list by it. Present and Late are recorded now.
            CheckInAt = RecordsAnArrival(canonicalStatus) ? DateTime.UtcNow : null,
            Status = canonicalStatus, CaptureMethod = CaptureMethod.Manual, Notes = notes,
            Student = student,
        };
        _db.AttendanceRecords.Add(rec);

        // No DeviceTapId on this path, so only UX_Attendance_Event_Student_Occurrence can reject
        // the insert — but reject it it does, whenever a tap lands between the read above and this
        // write.
        var saved = await SaveNewRecordAsync(rec, deviceId: null, deviceTapId: null, ct);
        if (saved.Outcome == SaveOutcome.Inserted) return Saved(rec);

        // Losing the race must not lose the *instruction*. The organizer asked for a specific
        // status; the row that beat this insert is a tap's, carrying Present or Late. Returning it
        // untouched would report "saved" while silently discarding the override — so the override is
        // applied to whichever row won, which is what an override means. CaptureMethod is left
        // alone, matching the existing-record branch above: the row was still captured by RFID.
        ApplyOverride(saved.Record, canonicalStatus, notes);
        await _db.SaveChangesAsync(ct);
        return Saved(saved.Record);

        ManualResponse Saved(AttendanceRecord record) => ManualResponse.For(
            ManualOutcome.Saved, success: true, "Manual entry saved.", ToDto(record), serverTime);
    }

    /// <summary>
    /// <inheritdoc cref="Reject" path="/summary"/>
    /// <para>
    /// The override surface's outcomes are not in the frozen device contract — no device ever calls
    /// this endpoint — but they carry a <c>code</c> for the same reason: the SPA branches on it, and a
    /// second error convention on one controller is how a client ends up parsing prose after all.
    /// </para>
    /// </summary>
    private static ManualResponse RejectManual(
        ManualOutcome outcome, string message, DateTime serverTime) =>
        ManualResponse.For(outcome, success: false, message, record: null, serverTime);

    /// <summary>
    /// Whether this override is recording an arrival we actually observed.
    ///
    /// <para>
    /// <b>The rule is about observation, not about status.</b> <c>CheckInAt</c> records what happened;
    /// <c>Status</c> records a human's judgement about it, and the two are allowed to disagree. This
    /// predicate is consulted only when <em>creating</em> a row, where no tap was seen — so inventing
    /// a timestamp for <c>Absent</c> or <c>Excused</c> would fabricate an observation.
    /// </para>
    ///
    /// <para>
    /// It is deliberately <b>not</b> consulted by <see cref="ApplyOverride"/>. See the note there —
    /// changing an existing row to <c>Absent</c> keeps its <c>CheckInAt</c>, because on that path a
    /// tap really did occur.
    /// </para>
    /// </summary>
    private static bool RecordsAnArrival(string canonicalStatus) =>
        canonicalStatus is AttendanceStatus.Present or AttendanceStatus.Late;

    /// <summary>
    /// Applies an organizer's decision to a row that already exists.
    ///
    /// <para>
    /// <b><c>CheckInAt</c> is deliberately left alone, including when the new status is <c>Absent</c>.</b>
    /// This row exists because a tap was recorded, and that tap is a physical event that happened. An
    /// organizer marking the student absent is overruling what it <em>means</em>, not asserting the
    /// tap never occurred — so erasing the timestamp would destroy the same evidence <c>RfidCardId</c>
    /// is kept for. A row reading <c>Absent</c> with a <c>CheckInAt</c> is therefore correct and
    /// expected; it is not the asymmetry-bug it looks like beside
    /// <see cref="RecordsAnArrival"/>, which governs the create path only.
    /// </para>
    ///
    /// <para>
    /// Pinned by <c>ManualOverrideTests.An_override_to_absent_keeps_the_observed_check_in_time</c>,
    /// so "fixing" this to match the create path fails the suite rather than silently discarding
    /// audit evidence.
    /// </para>
    ///
    /// <para>
    /// <b><c>RecordedByUserId</c> is stamped here too, and it overwrites whatever was there.</b> The
    /// column answers "who is accountable for this row's current state", and after an override that is
    /// the organizer, not the tap that created it — §6.4 calls this endpoint audited and an override
    /// is precisely the action worth auditing. The physical evidence of the original tap is untouched
    /// (<c>RfidCardId</c>, <c>DeviceId</c>, <c>CheckInAt</c>), so nothing is lost by the reattribution.
    /// It writes null for the whole of the pre-auth build; see <see cref="ICurrentUser"/>.
    /// </para>
    ///
    /// <para>
    /// Instance rather than static for that one field. Worth it: a static helper would have meant
    /// threading the user id through both call sites, and the second one is the lost-insert-race
    /// branch, which is exactly the path a future edit is most likely to forget.
    /// </para>
    /// </summary>
    private void ApplyOverride(AttendanceRecord record, string canonicalStatus, string? notes)
    {
        record.Status = canonicalStatus;
        record.Notes = notes;
        record.RecordedByUserId = _currentUser.UserId;
        record.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// The idempotency lookup, keyed on <c>(DeviceId, DeviceTapId)</c> — the same pair as
    /// <c>UX_Attendance_Device_DeviceTapId</c>.
    ///
    /// <para>
    /// <b>Why not <c>DeviceTapId</c> alone,</b> which is what this used to do. Two devices flushing
    /// their offline queues can emit the same tap-id string, and a lookup on the tap id alone treats
    /// the second device's tap as a replay of the first: it returns <em>another student's</em>
    /// attendance record and never writes the real one. Verified against the live database before
    /// the change — device B tapping Gabriel's card with a tap id device A had used came back
    /// "Duplicate tap ignored" carrying Miguel's record, and Gabriel's attendance was silently lost.
    /// The half-key also cannot seek the composite index, so it degraded to a scan as the table grew.
    /// </para>
    ///
    /// <para>
    /// <b>Which section of the plan wins.</b> §1 describes dedupe by
    /// <c>(EventId, StudentId, deviceTapId)</c> while §4.9 and §8.2 both say
    /// <c>(DeviceId, DeviceTapId)</c>. §4.9/§8.2 are followed: two sections agree against one, they
    /// match the index that actually exists, and they are the pair the offline sync contract in §8.2
    /// publishes to the external mobile developer in Phase 4. §1's version is also weaker in a way
    /// that matters — a tap queued before the roster sync knows which student a UID belongs to has
    /// no StudentId yet, so a key containing one cannot dedupe it.
    /// </para>
    ///
    /// <para>
    /// <b>The null-device case.</b> The current mock flow sends no <c>DeviceId</c>, and a filtered
    /// unique index over a nullable column is usually where null rows escape the constraint — but
    /// not here. SQL Server treats NULLs as <em>equal</em> inside a unique index (unlike PostgreSQL),
    /// so <c>(NULL, 'abc')</c> twice is a duplicate-key violation; confirmed by probing the live
    /// index rather than assuming. The index therefore constrains device-less taps as one shared
    /// "unregistered device" bucket, and EF compiles <c>a.DeviceId == null</c> to
    /// <c>[DeviceId] IS NULL</c>, which selects that same bucket. No special-casing of null is
    /// needed, and none was added — a rule like "reject a tap id without a device" would break the
    /// mock and mobile-before-registration flows to solve a problem the index does not have. §8.2
    /// makes <c>deviceTapId</c> a client-generated UUID, so the residual risk (two device-less
    /// clients colliding on one UUID) is negligible; if device registration ever becomes mandatory,
    /// make <c>DeviceId</c> non-nullable and this paragraph goes away.
    /// </para>
    ///
    /// <para>
    /// <b>Query and constraint agree again, and the fix was schema rather than this method.</b> This
    /// lookup runs through the <c>SchoolId</c> global query filter; for a while
    /// <c>UX_Attendance_Device_DeviceTapId</c> was <em>global</em>, because <c>AttendanceRecords</c>
    /// carried no <c>SchoolId</c> of its own to scope it by, so the two selected different row sets as
    /// soon as a second school existed. A tap whose <c>(DeviceId, DeviceTapId)</c> collided with
    /// another tenant's row missed this pre-check, raised 2601 on insert, failed the re-read through
    /// the same filter, and reached <see cref="ResolveLostInsertRaceAsync"/>'s deliberate throw — a 500
    /// that §8.2's offline queue retries straight back into the same collision, permanently, with no
    /// way for the client to escape it. Benign while one school existed; a queue drain is what turns
    /// it into a loop, which is why it was closed before the batch endpoint was built.
    /// </para>
    ///
    /// <para>
    /// Phase 4a design D-35 denormalized <c>SchoolId</c> onto the table, re-scoped the index to
    /// <c>(SchoolId, DeviceId, DeviceTapId)</c>, and pointed the query filter at the same column — so
    /// filter and constraint are now one predicate rather than two that happen to agree.
    /// <c>UX_Attendance_Event_Student_Occurrence</c> was never affected: every row under a given
    /// <c>EventId</c> belongs to that event's school by construction.
    /// </para>
    ///
    /// <para>
    /// <b>Do not reach for <c>IgnoreQueryFilters()</c> here if the two ever diverge again.</b> It would
    /// let this pre-check and the re-read find another tenant's row, and this method's result is
    /// returned to the caller as a successful duplicate — carrying another school's student name and
    /// student number in the response body. That trades a 500 for a cross-tenant disclosure, which is
    /// the worse of the two by a wide margin.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Phase 4c D-34 — it now seeks two columns, and the shape of the query is the decision.</b> A
    /// <c>TimeInOut</c> pair has two tap ids and two indexes to match them
    /// (<c>UX_Attendance_Device_DeviceTapId</c> and <c>UX_Attendance_Device_CheckOutDeviceTapId</c>),
    /// and a client replaying either half must get its own record back.
    ///
    /// <para>
    /// <b>Not <c>WHERE DeviceTapId = @t OR CheckOutDeviceTapId = @t</c>.</b> A disjunction over two
    /// different columns generally cannot seek both filtered indexes — the optimizer's realistic
    /// options are a scan or an index union it has to derive — and this runs on every tap that carries
    /// a tap id, which after Phase 4d's batch endpoint means every row of every queue flush. The
    /// <c>UNION</c> below states the index union explicitly, as two branches in one round trip.
    /// </para>
    ///
    /// <para>
    /// <b>Measured in Phase 4d, and the doubt was justified: it is two index SCANS, not two seeks.</b>
    /// 4c recorded one round trip as a fact and two seeks as an unverified guess, naming the §11 query
    /// filter's disjunction on the leading column as the reason to doubt it, and asked for a plan before
    /// the batch endpoint shipped. Captured on SQL Server 2022 against 40,000 attendance rows across two
    /// tenants and twenty devices, statistics fully updated, parameters passed through
    /// <c>sp_executesql</c> exactly as EF sends them:
    /// <list type="bullet">
    ///   <item><b>As shipped: <c>Index Scan</c> on <c>UX_Attendance_Device_DeviceTapId</c> and
    ///   <c>Index Scan</c> on <c>UX_Attendance_Device_CheckOutDeviceTapId</c></b> — scan count 2,
    ///   <b>1390 logical reads</b>. Identical with a tenant pinned and with none, which is itself the
    ///   tell: the plan cannot depend on a value the disjunction stops it from using.</item>
    ///   <item>The same query with the tenant disjunction removed — what Phase 6 emits once an
    ///   unauthenticated request is impossible — is <b>two <c>Index Seek</c>s and 7 logical reads</b>.
    ///   <b>A factor of about 198.</b></item>
    /// </list>
    /// One correction to 4c's wording while the evidence is here: EF Core 9 does not emit
    /// <c>@school IS NULL</c>. It emits
    /// <c>(@__ef_filter__p_1 = CAST(1 AS bit) OR [SchoolId] = @school)</c> — a bit parameter rather than
    /// a null test. The shape and the consequence are the same; the literal SQL is not what the comment
    /// said.
    /// </para>
    ///
    /// <para>
    /// <b>Does it matter at this row count? Yes — and the batch endpoint is what makes it matter.</b>
    /// One tap paying 1390 logical reads instead of 7 is invisible: the pages are in the buffer pool and
    /// it is well under a millisecond. A 200-row flush pays it two hundred times — roughly
    /// <b>278,000 logical reads per batch</b> against about 1,400 with seeks, per flush, per device.
    /// Worse, the cost scales with the <em>table</em> rather than with the batch: the scan covers every
    /// row carrying a tap id, so it grows with the entire history of captured attendance and never with
    /// anything the client controls.
    /// </para>
    ///
    /// <para>
    /// <b>Not fixed here, and deliberately so.</b> The repair is the one <c>EamsDbContext</c> already
    /// names — drop the query filter's null branch once Phase 6 makes an unauthenticated request
    /// impossible — which changes every tenant-scoped query in the system and belongs to that phase with
    /// its own review. Both local workarounds are worse than the problem: <c>IgnoreQueryFilters()</c>
    /// would let this lookup return another tenant's row <em>as a successful duplicate</em>, carrying
    /// that school's student name and number to the caller (see the paragraph above that forbids it);
    /// and <c>OPTION (RECOMPILE)</c> buys the seek at the price of a compile on the single hottest query
    /// in the API. Recorded with numbers so the Phase 6 decision is made against evidence rather than
    /// against a suspicion.
    /// </para>
    ///
    /// <para>
    /// <b>Ids first, then a load, and that ordering is deliberate rather than incidental.</b> EF cannot
    /// apply <c>Include</c> across a set operation, so the union projects keys and the entity is
    /// fetched only when one is found. The common case on the tap path is a <em>miss</em> — a new tap,
    /// no prior row — and that case costs exactly one query; the second round trip is paid only on a
    /// genuine replay, which is the rare one. Projecting an anonymous type rather than a bare
    /// <c>Guid</c> is what makes "no row" distinguishable from <c>Guid.Empty</c>, and it is where the
    /// arm tiebreak lives — see <see cref="CheckInArm"/>.
    /// </para>
    /// </remarks>
    private async Task<AttendanceRecord?> FindByDeviceTapAsync(
        Guid? deviceId, string deviceTapId, CancellationToken ct)
    {
        var checkIns = _db.AttendanceRecords
            .Where(a => a.DeviceId == deviceId && a.DeviceTapId == deviceTapId)
            .Select(a => new { a.Id, Arm = CheckInArm });

        var checkOuts = _db.AttendanceRecords
            .Where(a => a.DeviceId == deviceId && a.CheckOutDeviceTapId == deviceTapId)
            .Select(a => new { a.Id, Arm = CheckOutArm });

        var match = await checkIns.Union(checkOuts)
            .OrderBy(m => m.Arm).ThenBy(m => m.Id)
            .FirstOrDefaultAsync(ct);

        if (match is null) return null;

        return await _db.AttendanceRecords.Include(a => a.Student)
            .FirstOrDefaultAsync(a => a.Id == match.Id, ct);
    }

    /// <summary>
    /// Which arm of <see cref="FindByDeviceTapAsync"/>'s union a row matched on, and the tiebreak when
    /// it matched on both.
    ///
    /// <para>
    /// <b>The two arms can genuinely both hit, and the ordering is what stops that being a bug.</b> The
    /// two filtered unique indexes are independent: nothing prevents one tap id existing as row A's
    /// <c>DeviceTapId</c> and row B's <c>CheckOutDeviceTapId</c>. The pre-check normally makes that
    /// unreachable — a tap id already in use comes back <c>DuplicateIgnored</c> before it can be
    /// written again — but a lost race skips the pre-check by definition, which is the one situation
    /// this method exists to survive.
    /// </para>
    ///
    /// <para>
    /// Without an <c>ORDER BY</c> the union returns whichever row the plan happens to emit first, so a
    /// replay would hand back a nondeterministically chosen one of <em>two different students'</em>
    /// records, reported as a successful duplicate. That is the exact failure class the two-device
    /// bug in this method's remarks was about, and it is the one this codebase treats as serious.
    /// The check-in arm wins because it is the tap that created the row; <c>Id</c> breaks any residual
    /// tie so the answer is reproducible rather than merely usually-right.
    /// </para>
    /// </summary>
    private const int CheckInArm = 0;

    /// <inheritdoc cref="CheckInArm"/>
    private const int CheckOutArm = 1;

    /// <summary>
    /// The other uniqueness guard, <c>UX_Attendance_Event_Student_Occurrence</c>. <c>OccurrenceId</c>
    /// is part of the index and is always null on this path (the core slice has no recurring
    /// occurrences yet), so it is matched explicitly rather than left out — leaving it out would
    /// silently start matching the wrong rows the day §4.6 occurrences are populated.
    ///
    /// <para>
    /// Used by <em>both</em> write paths. The organizer override used to run its own inline copy of
    /// this query without the <c>OccurrenceId</c> term, which is exactly the drift this comment
    /// warned about, arriving through a second author rather than through time.
    /// </para>
    /// </summary>
    private Task<AttendanceRecord?> FindByEventStudentAsync(
        Guid eventId, Guid studentId, CancellationToken ct) =>
        _db.AttendanceRecords.Include(a => a.Student)
            .FirstOrDefaultAsync(
                a => a.EventId == eventId && a.StudentId == studentId && a.OccurrenceId == null, ct);

    /// <summary>What happened to an attendance row this service tried to insert.</summary>
    private enum SaveOutcome
    {
        /// <summary>The insert succeeded. The record is the one that was passed in.</summary>
        Inserted,

        /// <summary>
        /// <c>UX_Attendance_Device_DeviceTapId</c> rejected the insert and the winner is the same
        /// device's earlier tap — a replay, so the caller's request was already satisfied.
        /// </summary>
        DuplicateTapWon,

        /// <summary>
        /// <c>UX_Attendance_Event_Student_Occurrence</c> rejected the insert: this student already
        /// has a row on this event, written by whichever request got there first.
        /// </summary>
        ExistingRecordWon,
    }

    private readonly record struct GuardedSave(SaveOutcome Outcome, AttendanceRecord Record);

    /// <summary>
    /// The guarded insert, shared by the tap flow and the organizer override.
    ///
    /// <para>
    /// Both idempotency checks are reads, so between them and the write another request can insert
    /// the same row. That is the ordinary case rather than a pathological one: two taps of one card
    /// a few milliseconds apart, the mobile client's retry-on-timeout (§8.2), and an organizer
    /// overriding a student who is at that moment walking through the reader all produce it. Losing
    /// the race is not an error — the other request recorded what this one wanted to record — so the
    /// winner is read back and handed to the caller to interpret.
    /// </para>
    ///
    /// <para>
    /// It is one method rather than one per caller on purpose. The override shipped without any of
    /// this because it was written as a separate copy of the same read-then-write, and a third copy
    /// would arrive the same way; there is now a single place where the defence exists, so a new
    /// write path either calls it or is visibly not calling it.
    /// </para>
    /// </summary>
    private async Task<GuardedSave> SaveNewRecordAsync(
        AttendanceRecord pending, Guid? deviceId, string? deviceTapId, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return new GuardedSave(SaveOutcome.Inserted, pending);
        }
        catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
        {
            return await ResolveLostInsertRaceAsync(ex, pending, deviceId, deviceTapId, ct);
        }
    }

    /// <summary>
    /// Recovers the winner of a concurrent insert. Whichever of the two unique indexes rejected this
    /// insert, the row that beat it is the record the caller asked for, so re-read it and report
    /// which guard fired.
    ///
    /// <para>
    /// The failed entity must be detached first. EF leaves it <c>Added</c> after a failed
    /// <c>SaveChanges</c>, and a tracked <c>Added</c> row with the same key would both shadow the
    /// re-read through identity resolution and be retried by any later save on this context — which
    /// the override path performs, so this is load-bearing rather than tidy.
    /// </para>
    ///
    /// <para>
    /// If the re-read finds nothing, this fails loudly with the original violation attached. A
    /// unique violation with no conflicting row is not a race — it is a wrong assumption about which
    /// constraint fired, and reporting it as a duplicate that does not exist would hide a real bug.
    /// See <see cref="FindByDeviceTapAsync"/> for the one case that is known to reach this throw and
    /// why the fix for it belongs in the schema.
    /// </para>
    /// </summary>
    private async Task<GuardedSave> ResolveLostInsertRaceAsync(
        DbUpdateException violation, AttendanceRecord failed,
        Guid? deviceId, string? deviceTapId, CancellationToken ct)
    {
        var eventId = failed.EventId;
        var studentId = failed.StudentId;

        _db.Entry(failed).State = EntityState.Detached;

        if (!string.IsNullOrWhiteSpace(deviceTapId))
        {
            var byTap = await FindByDeviceTapAsync(deviceId, deviceTapId, ct);
            if (byTap is not null) return new GuardedSave(SaveOutcome.DuplicateTapWon, byTap);
        }

        var winner = await FindByEventStudentAsync(eventId, studentId, ct);
        if (winner is not null) return new GuardedSave(SaveOutcome.ExistingRecordWon, winner);

        throw new InvalidOperationException(
            $"A unique-key violation was raised inserting attendance for event {eventId} / student " +
            $"{studentId}, but no conflicting record could be read back. The constraint that fired " +
            "is not one this method knows how to resolve.", violation);
    }

    /// <summary>
    /// The same recovery for the one <em>update</em> that can now lose a race:
    /// <c>UX_Attendance_Device_CheckOutDeviceTapId</c> rejecting a check-out because this device
    /// already used that tap id somewhere else (Phase 4c, D-34).
    ///
    /// <para>
    /// <b>P7: it now answers the compare-and-set in <see cref="ApplyLaterTapAsync"/>, and it no longer
    /// reloads anything.</b> The check-out used to be a tracked <c>SaveChanges</c> that left the entity
    /// <c>Modified</c> on failure, so this method had to reload it. <c>ExecuteUpdateAsync</c> never
    /// touches the change tracker, so there is nothing to undo here; the lost-update half of the old
    /// race (two check-outs both reading <c>CheckOutAt IS NULL</c>) is now the compare-and-set's zero-row
    /// answer and its re-dispatch, not an exception. What is left is the one index violation an
    /// <c>UPDATE</c> can raise, and the violation arrives as the provider's unwrapped
    /// <c>SqlException</c> — hence <see cref="Exception"/> rather than <see cref="DbUpdateException"/>.
    /// </para>
    ///
    /// <para>
    /// If the winner cannot be read back this fails loudly with the original violation attached, for
    /// the reason the insert-side resolver records: a unique violation with no conflicting row is a
    /// wrong assumption about which constraint fired, and reporting it as a duplicate that does not
    /// exist would hide a real bug.
    /// </para>
    ///
    /// <para>
    /// <b>Here that throw is unreachable, and by a stronger argument than the insert side's.</b> Only
    /// one index can reject this update — <c>UX_Attendance_Device_CheckOutDeviceTapId</c>, keyed
    /// <c>(SchoolId, DeviceId, CheckOutDeviceTapId)</c> — so a violation <em>requires</em> the
    /// conflicting row to carry the same <c>SchoolId</c> as the row we failed to update. That is
    /// exactly the predicate the §11 query filter applies, so the winner is always visible to the
    /// re-read and it cannot come back empty. Contrast <see cref="ResolveLostInsertRaceAsync"/>, where
    /// two indexes can fire and one of them was, until D-35, genuinely capable of rejecting an insert
    /// whose winner the filter then hid. The throw stays because "unreachable" is an argument about
    /// today's schema and the cost of being wrong about it is a duplicate that does not exist.
    /// </para>
    /// </summary>
    private async Task<AttendanceRecord> ResolveLostCheckOutRaceAsync(
        Exception violation, Guid attendanceId,
        Guid? deviceId, string? deviceTapId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(deviceTapId))
        {
            var winner = await FindByDeviceTapAsync(deviceId, deviceTapId, ct);
            if (winner is not null) return winner;
        }

        throw new InvalidOperationException(
            $"A unique-key violation was raised recording a check-out on attendance {attendanceId} with " +
            $"deviceTapId '{deviceTapId}', but no conflicting record could be read back. The " +
            "constraint that fired is not one this method knows how to resolve.", violation);
    }

    // ============================================================ the scan log (D-31 follow-on)

    /// <summary>
    /// Records a scan whose card resolved to nobody, so that it is visible under the event.
    ///
    /// <para>
    /// <b>The gap this closes.</b> <c>CardNotFound</c> is a rejection and not a row, so until now an
    /// unrecognised card left no trace anywhere: the device was told, and that was the end of it.
    /// Afterwards "nobody scanned" and "somebody scanned a card we could not place" were
    /// indistinguishable from any record the institution holds, which is the wrong way round — the
    /// second is the one worth investigating.
    /// </para>
    ///
    /// <para>
    /// <b>Written to <c>AuditLogs</c> rather than to a table of its own.</b> An
    /// <c>AttendanceRecord</c> requires a student and this scan has none, so it cannot go there; and
    /// <c>AuditLogs</c> already carries an <c>(EntityType, EntityId)</c> index, which makes "every
    /// unresolved scan for this event" a single indexed read. No new table, no migration.
    /// </para>
    ///
    /// <para>
    /// <b>A failure here must not fail the tap.</b> The tap is already being rejected; turning that
    /// into a 500 would make the client retry a row that can never succeed, and §8.2 treats 5xx as
    /// retryable — the queue would never drain. So the write is guarded, and a failure is logged at
    /// warning rather than thrown. This is a deliberate exception to the no-silent-catch rule and it
    /// is not silent: the log line names the event and the card.
    /// </para>
    /// </summary>
    private async Task LogUnresolvedScanAsync(
        Event ev, TapRequest req, string uid, DateTime when, DateTime serverTime, CancellationToken ct)
    {
        try
        {
            _db.AuditLogs.Add(new AuditLog
            {
                SchoolId = ev.SchoolId,
                UserId = _currentUser.UserId,
                Action = ScanLog.UnresolvedAction,
                EntityType = ScanLog.EventEntityType,
                EntityId = ev.Id,
                CreatedAt = serverTime,
                Changes = JsonSerializer.Serialize(new UnresolvedScan(
                    uid,
                    req.DeviceTapId,
                    _device.DeviceId ?? req.DeviceId,
                    when,
                    nameof(TapOutcome.CardNotFound),
                    Truncate(req.LocalOutcome, TapRequestLimits.MaxLocalOutcomeLength)), ScanJson),
            });

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Detach it, or the next SaveChanges on this context re-attempts the same failing insert
            // and takes an unrelated write down with it.
            foreach (var entry in _db.ChangeTracker.Entries<AuditLog>().ToList())
                entry.State = EntityState.Detached;

            _logger.LogWarning(
                ex,
                "Could not record the unresolved scan of card {CardUid} on event {EventId}. The tap " +
                "was rejected as CardNotFound either way; only the scan-log row is missing.",
                uid, ev.Id);
        }
    }

    /// <summary>
    /// Records a tap the B6 interval rule ignored, so "why did my second tap not count?" has an answer.
    ///
    /// <para>
    /// <b>An audit row, never attendance.</b> Filed under the event exactly like an unresolved scan,
    /// with <see cref="ScanLog.SuppressedAction"/> — which the Unresolved Scans read does not select, so
    /// a suppressed tap never appears in that panel. No new table, no migration:
    /// <c>IX_AuditLogs_Entity</c> serves it.
    /// </para>
    ///
    /// <para>
    /// <b>A failure here must not fail the tap</b>, for the reason <see cref="LogUnresolvedScanAsync"/>
    /// records, and more so: this tap is a <em>success</em>. Turning it into a 500 would make the queue
    /// retry a row that is already fully handled. Logged at warning, not swallowed.
    /// </para>
    /// </summary>
    private async Task LogSuppressedScanAsync(
        Event ev, TapRequest req, string uid, Guid? deviceId, Guid studentId, DateTime when,
        TapAnchor anchor, TimeSpan interval, DateTime serverTime, CancellationToken ct)
    {
        try
        {
            _db.AuditLogs.Add(new AuditLog
            {
                SchoolId = ev.SchoolId,
                UserId = _currentUser.UserId,
                Action = ScanLog.SuppressedAction,
                EntityType = ScanLog.EventEntityType,
                EntityId = ev.Id,
                CreatedAt = serverTime,
                Changes = JsonSerializer.Serialize(new SuppressedScan(
                    uid,
                    req.DeviceTapId,
                    deviceId,
                    when,
                    studentId,
                    anchor.At,
                    anchor.Kind.ToString(),
                    interval.TotalSeconds,
                    nameof(TapOutcome.TooSoonIgnored)), ScanJson),
            });

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Detach it, or the next SaveChanges on this context - the next row of a batch - re-attempts
            // the same failing insert and takes an unrelated write down with it.
            foreach (var entry in _db.ChangeTracker.Entries<AuditLog>().ToList())
                entry.State = EntityState.Detached;

            _logger.LogWarning(
                ex,
                "Could not record the suppressed tap of card {CardUid} on event {EventId}. The tap was " +
                "answered TooSoonIgnored either way; only the audit row is missing.",
                uid, ev.Id);
        }
    }

    /// <summary>
    /// camelCase, to match every other JSON this API emits.
    ///
    /// <para>
    /// Without it <c>JsonSerializer</c> writes the C# property names as they are spelled, so the row
    /// says <c>CardUid</c> while the reader - and the published DTO, and every other body this API
    /// returns - says <c>cardUid</c>. Nothing throws; the reader simply finds no property of that
    /// name and skips the row, and the report is empty while the writes are all succeeding. That is
    /// exactly how this was first written, and the tests caught it.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions ScanJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>The body of an unresolved-scan audit row. Serialized into <c>AuditLog.Changes</c>.</summary>
    private sealed record UnresolvedScan(
        string CardUid,
        string? DeviceTapId,
        Guid? DeviceId,
        DateTime TappedAt,
        string ServerOutcome,
        string? LocalOutcome);

    /// <summary>
    /// The body of a suppressed-scan audit row (B6). Serialized into <c>AuditLog.Changes</c>, camelCase.
    /// <c>AnchorAt</c>/<c>AnchorKind</c> name the counted tap it was judged against.
    /// </summary>
    private sealed record SuppressedScan(
        string CardUid,
        string? DeviceTapId,
        Guid? DeviceId,
        DateTime TappedAt,
        Guid StudentId,
        DateTime AnchorAt,
        string AnchorKind,
        double IntervalSeconds,
        string ServerOutcome);
}
