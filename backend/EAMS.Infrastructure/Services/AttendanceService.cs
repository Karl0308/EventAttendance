using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

internal sealed class AttendanceService : IAttendanceService
{
    private readonly EamsDbContext _db;

    /// <summary>
    /// Who is performing a manual override. Null for the whole of the pre-auth build — see
    /// <see cref="ICurrentUser"/> for why the seam is here before there is anything to read from it.
    /// </summary>
    private readonly ICurrentUser _currentUser;

    public AttendanceService(EamsDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
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
    public async Task<TapResponse> TapAsync(TapRequest req, CancellationToken ct = default)
    {
        // TappedAt arrives from JSON, so its Kind depends on how the client wrote the string: "Z"
        // gives Utc, "+08:00" gives Local, a bare date-time gives Unspecified. Comparing a Local
        // value against ev.StartAt (Utc) below would misjudge Present vs Late by the server's
        // offset — eight hours, in Manila. Normalize once, here, at the boundary.
        var when = UtcTime.Normalize(req.TappedAt) ?? DateTime.UtcNow;

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId && !e.IsDeleted, ct);
        if (ev is null)
            return new TapResponse(TapOutcome.EventNotFound, new TapResult(false, "Event not found.", null));
        if (ev.Status != EventStatus.Open)
            return new TapResponse(TapOutcome.EventNotOpen,
                new TapResult(false, $"Event is {ev.Status}, not Open.", null));

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
            return new TapResponse(TapOutcome.CardNotFound,
                new TapResult(false, $"No active card matches UID {uid}.", null));

        var student = card.Student;

        // DeviceId is a foreign key and was previously written unchecked, so an unknown one became
        // an FK violation that IsUniqueViolation correctly declines to swallow — an unhandled 500.
        // §8.2 treats 5xx as retryable, so a re-provisioned handset holding a stale id retried every
        // queued tap forever and the queue never drained. Validated here, before the write, and
        // reported as a rejection the client can act on. The lookup runs through the SchoolId query
        // filter, so a device belonging to another tenant is "not registered" to this one.
        if (req.DeviceId is { } deviceId
            && !await _db.Devices.AnyAsync(d => d.Id == deviceId, ct))
        {
            return new TapResponse(TapOutcome.DeviceNotRegistered,
                new TapResult(false, $"Device {deviceId} is not registered.", null));
        }

        // Idempotency (§4.9, §8.2): the same tap replayed by the same device returns the record it
        // already produced. The key is the *pair* (DeviceId, DeviceTapId), matching
        // UX_Attendance_Device_DeviceTapId exactly — see FindByDeviceTapAsync for why both halves
        // matter and why the plan's §1 wording is not the one followed here.
        if (!string.IsNullOrWhiteSpace(req.DeviceTapId))
        {
            var dup = await FindByDeviceTapAsync(req.DeviceId, req.DeviceTapId, ct);
            if (dup is not null)
                return new TapResponse(TapOutcome.DuplicateIgnored,
                    new TapResult(true, "Duplicate tap ignored (idempotent).", ToDto(dup)));
        }

        var existing = await FindByEventStudentAsync(ev.Id, student.Id, ct);

        if (existing is null)
        {
            var status = when <= ev.StartAt.AddMinutes(ev.GraceMinutes)
                ? AttendanceStatus.Present
                : AttendanceStatus.Late;

            var rec = new AttendanceRecord
            {
                EventId = ev.Id, StudentId = student.Id, RfidCardId = card.Id,
                CheckInAt = when, Status = status, CaptureMethod = CaptureMethod.Rfid,
                DeviceId = req.DeviceId, DeviceTapId = req.DeviceTapId,
                Student = student,
            };
            _db.AttendanceRecords.Add(rec);

            var saved = await SaveNewRecordAsync(rec, req.DeviceId, req.DeviceTapId, ct);
            return saved.Outcome switch
            {
                SaveOutcome.Inserted => new TapResponse(TapOutcome.Recorded,
                    new TapResult(true, $"Checked in ({status}).", ToDto(rec))),
                SaveOutcome.DuplicateTapWon => new TapResponse(TapOutcome.DuplicateIgnored,
                    new TapResult(true, "Duplicate tap ignored (idempotent).", ToDto(saved.Record))),
                _ => new TapResponse(TapOutcome.AlreadyRecorded,
                    new TapResult(true, "Already recorded.", ToDto(saved.Record))),
            };
        }

        // Already present: in TimeInOut mode, a second tap records check-out.
        if (ev.AttendanceMode == AttendanceMode.TimeInOut && existing.CheckOutAt is null)
        {
            existing.CheckOutAt = when;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new TapResponse(TapOutcome.CheckedOut, new TapResult(true, "Checked out.", ToDto(existing)));
        }

        return new TapResponse(TapOutcome.AlreadyRecorded,
            new TapResult(true, "Already recorded.", ToDto(existing)));
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
        // §4.9's value set, validated before anything is read or written. Here rather than in the
        // controller because the service is what writes the column: a guard on the HTTP boundary
        // alone would leave the next caller (the planned import path, §4.12) unprotected.
        if (!AttendanceStatus.TryNormalize(status, out var canonicalStatus))
        {
            return new ManualResponse(ManualOutcome.InvalidStatus, new TapResult(
                false,
                $"Status '{status}' is not one of {string.Join(", ", AttendanceStatus.All)}.",
                null));
        }

        // The other half of the same guard. `Notes` is nvarchar(500); an over-length value used to
        // reach SQL Server and come back as error 2628, which surfaced as a 500 because a truncation
        // is not a unique violation and SaveNewRecordAsync rightly declines to swallow it.
        if (!AttendanceNotes.IsValid(notes))
        {
            return new ManualResponse(ManualOutcome.InvalidNotes, new TapResult(
                false,
                $"Notes must be {AttendanceNotes.MaxLength} characters or fewer (got {notes!.Length}).",
                null));
        }

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId && !e.IsDeleted, ct);
        if (ev is null)
            return new ManualResponse(ManualOutcome.EventNotFound, new TapResult(false, "Event not found.", null));

        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId && !s.IsDeleted, ct);
        if (student is null)
            return new ManualResponse(ManualOutcome.StudentNotFound, new TapResult(false, "Student not found.", null));

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

        static ManualResponse Saved(AttendanceRecord record) =>
            new(ManualOutcome.Saved, new TapResult(true, "Manual entry saved.", ToDto(record)));
    }

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
    /// <b>Where query and constraint stop agreeing, and what that costs.</b> This paragraph used to
    /// claim the lookup "cannot miss a record the index would go on to reject". That was true when
    /// written and is no longer: the <c>SchoolId</c> global query filter installed in the same phase
    /// applies to <c>AttendanceRecords</c> (through <c>Event.SchoolId</c>), so this lookup and the
    /// re-read in <see cref="ResolveLostInsertRaceAsync"/> both see only the pinned tenant's rows —
    /// while <c>UX_Attendance_Device_DeviceTapId</c> is <em>global</em>, because
    /// <c>AttendanceRecords</c> carries no <c>SchoolId</c> of its own to scope it by. The real
    /// condition is therefore narrower than the old claim: <b>query and constraint agree only while
    /// at most one school's rows are visible</b> — which is every deployment today, and is why
    /// nothing is currently broken.
    /// </para>
    ///
    /// <para>
    /// With a second school the divergence is reachable and its failure is ugly. A tap whose
    /// <c>(DeviceId, DeviceTapId)</c> collides with a row belonging to another tenant misses this
    /// pre-check, raises 2601 on insert, fails the re-read through the same filter, and reaches
    /// <see cref="ResolveLostInsertRaceAsync"/>'s deliberate throw — a 500 that the offline queue
    /// retries straight back into the same collision, so it is permanent for that tap.
    /// <c>UX_Attendance_Event_Student_Occurrence</c> is <em>not</em> affected: every row under a
    /// given <c>EventId</c> belongs to that event's school by construction, so its lookup and its
    /// index select the same rows under any tenant.
    /// </para>
    ///
    /// <para>
    /// <b>The fix is schema, and it is scheduled — not this method.</b> Denormalize <c>SchoolId</c>
    /// onto <c>AttendanceRecords</c> and make the index <c>(SchoolId, DeviceId, DeviceTapId)</c>, so
    /// the constraint is scoped exactly as the filter is. Do <em>not</em> reach for
    /// <c>IgnoreQueryFilters()</c> to make the two agree instead: it would let the pre-check and the
    /// re-read find the other tenant's row, and this method's result is returned to the caller as a
    /// successful duplicate — carrying another school's student name and student number in the
    /// response body. That trades a 500 for a cross-tenant disclosure, which is the worse of the two
    /// by a wide margin.
    /// </para>
    /// </summary>
    private Task<AttendanceRecord?> FindByDeviceTapAsync(
        Guid? deviceId, string deviceTapId, CancellationToken ct) =>
        _db.AttendanceRecords.Include(a => a.Student)
            .FirstOrDefaultAsync(a => a.DeviceId == deviceId && a.DeviceTapId == deviceTapId, ct);

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
}
