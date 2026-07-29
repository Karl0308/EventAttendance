using EAMS.Application.Abstractions;
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
    /// Only ever used to report a §4.13 tap-window setting this service could not read as a number —
    /// see <see cref="ResolveTapWindowAsync"/>. A malformed setting falls back to the published
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

    public async Task<IReadOnlyList<AttendanceDto>> ListAsync(
        Guid? eventId, Guid? studentId, string? status, CancellationToken ct = default)
    {
        var q = _db.AttendanceRecords.Include(a => a.Student).AsQueryable();
        if (eventId is not null) q = q.Where(a => a.EventId == eventId);
        if (studentId is not null) q = q.Where(a => a.StudentId == studentId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status);
        var list = await q.OrderByDescending(a => a.CheckInAt).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    // Technical Plan §6.4. The whole capture decision lives here in one place: resolve UID →
    // validate the event window → validate the device → idempotency check → upsert → compute
    // Present/Late. Splitting it behind a repository would scatter a single transactional decision
    // across layers.
    //
    // The public entry point takes no window cache: a single tap resolves its §4.13 settings once and
    // has nothing to reuse them across. TapBatchAsync supplies one — see the overload below.
    public Task<TapResponse> TapAsync(TapRequest req, CancellationToken ct = default) =>
        TapAsync(req, windows: null, ct);

    /// <inheritdoc cref="TapAsync(TapRequest, CancellationToken)"/>
    /// <param name="windows">
    /// A per-call memo of resolved §4.13 tap windows, keyed by school, or null to resolve every time.
    /// Phase 4d's carry-over from 4c: <see cref="ResolveTapWindowAsync"/> is one settings query per tap
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
        TapRequest req, Dictionary<Guid, TapTimeWindow>? windows, CancellationToken ct)
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

        var window = await ResolveTapWindowAsync(ev.SchoolId, windows, ct);
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
            return Reject(TapOutcome.CardNotFound, $"No active card matches UID {uid}.", serverTime);

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
                _ => AlreadyRecorded(saved.Record, serverTime),
            };
        }

        // Already present: in TimeInOut mode, a second tap records check-out.
        if (ev.AttendanceMode == AttendanceMode.TimeInOut && existing.CheckOutAt is null)
        {
            existing.CheckOutAt = when;

            // D-34. The check-out's own idempotency key, kept rather than discarded — which is the
            // whole of the defect this closes. It goes in its own column because the check-in's id is
            // still load-bearing in DeviceTapId: overwriting that to record this one would fix the
            // check-out's idempotency by breaking the check-in's, so a replayed check-in would then
            // write a second row.
            existing.CheckOutDeviceTapId = req.DeviceTapId;
            existing.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync(ct);
                return Accept(TapOutcome.CheckedOut, "Checked out.", existing, serverTime);
            }
            catch (DbUpdateException ex) when (SqlServerErrors.IsUniqueViolation(ex))
            {
                // UX_Attendance_Device_CheckOutDeviceTapId rejected the update: this device already
                // used this tap id to check somebody out. The pre-check above catches the ordinary
                // replay, so reaching here means the winner landed between that read and this write —
                // the same race SaveNewRecordAsync recovers from, arriving on an UPDATE instead of an
                // INSERT, and it exists only because this column is now constrained at all.
                var winner = await ResolveLostCheckOutRaceAsync(ex, existing, deviceId, req.DeviceTapId, ct);
                return Duplicate(winner, serverTime);
            }
        }

        return AlreadyRecorded(existing, serverTime);
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
        var windows = new Dictionary<Guid, TapTimeWindow>();

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
            rows[index] = new TapBatchRow(index, tap.DeviceTapId, await TapAsync(tap, windows, ct));
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
    private async Task<TapTimeWindow> ResolveTapWindowAsync(
        Guid schoolId, Dictionary<Guid, TapTimeWindow>? cache, CancellationToken ct)
    {
        if (cache is not null && cache.TryGetValue(schoolId, out var cached)) return cached;

        var resolved = await ReadTapWindowAsync(schoolId, ct);
        cache?.Add(schoolId, resolved);
        return resolved;
    }

    /// <inheritdoc cref="ResolveTapWindowAsync"/>
    private async Task<TapTimeWindow> ReadTapWindowAsync(Guid schoolId, CancellationToken ct)
    {
        var rows = await _db.SystemSettings
            .Where(s => (s.SchoolId == schoolId || s.SchoolId == null)
                     && (s.Key == TapTimeWindow.BeforeStartMinutesSettingKey
                      || s.Key == TapTimeWindow.AfterEndMinutesSettingKey))
            .Select(s => new { s.SchoolId, s.Key, s.Value })
            .ToListAsync(ct);

        return new TapTimeWindow(
            Minutes(TapTimeWindow.BeforeStartMinutesSettingKey, TapTimeWindow.DefaultBeforeStartMinutes),
            Minutes(TapTimeWindow.AfterEndMinutesSettingKey, TapTimeWindow.DefaultAfterEndMinutes));

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
    /// <b>The reload is load-bearing, not tidiness.</b> The entity is left <c>Modified</c> with a
    /// <c>CheckOutAt</c> that was never committed, and it is a row this context is still tracking —
    /// so any later save would retry the rejected update, and, worse, the record handed back to the
    /// caller would report a check-out time the database does not have. Reloading restores the honest
    /// answer: our change did not happen. <see cref="ResolveLostInsertRaceAsync"/> detaches instead
    /// because its entity was never in the table at all.
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
        DbUpdateException violation, AttendanceRecord failed,
        Guid? deviceId, string? deviceTapId, CancellationToken ct)
    {
        await _db.Entry(failed).ReloadAsync(ct);

        if (!string.IsNullOrWhiteSpace(deviceTapId))
        {
            var winner = await FindByDeviceTapAsync(deviceId, deviceTapId, ct);
            if (winner is not null) return winner;
        }

        throw new InvalidOperationException(
            $"A unique-key violation was raised recording a check-out on attendance {failed.Id} with " +
            $"deviceTapId '{deviceTapId}', but no conflicting record could be read back. The " +
            "constraint that fired is not one this method knows how to resolve.", violation);
    }
}
