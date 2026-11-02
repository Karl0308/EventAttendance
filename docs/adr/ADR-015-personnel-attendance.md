# ADR-015: Personnel Attendance — Personnel Are Counted, Captured and Frozen Like Students

**Status**: Accepted (JJ, 2026-10-27; revision 2, JJReviewer APPROVE).
> The *product rulings* below are QA's answers Q1–Q8 (MDVault #703) and QA's pre-registration ruling, all
> dated 2026-10-27. JJ supplied the capability-header direction and seven design rulings, also 2026-10-27.
> This *document* writes the design down. Revision 1 applies JJReviewer's gate (REQUEST CHANGES:
> 7 WARNING, 6 NIT) and those rulings. The register's rule holds: JoseArch recommends, JJ accepts.
> **Do not self-accept.**
> **The capture contract in D-99/D-100 is what the mobile handoff (MDVault #707) is generated from.
> Once accepted, it changes only with a new `docs/api/mobile-changes.md` entry.**

**Date**: 2026-10-27
**Deciders**: JJ; JoseArch (Team X)
**Relationship to other ADRs**: **This is the "Option-C ADR" that ADR-007 D-70 and ADR-008 D-72 named.**
- **Supersedes** ADR-007 **D-70** and ADR-008 **D-72** (personnel advisory, not counted).
- **Amends by reference:**
  - ADR-008 **D-71**: the replace becomes per population (D-103);
  - ADR-013 **D-90**: its "D-70 holds" bullet;
  - ADR-003 **D-12/D-13/D-21**: a second filtered attendance index; the freeze writes a second table and
    personnel Absent rows.
- **Restates, unchanged,** ADR-003 **D-19** (rate over one population), **D-15** (live and frozen reads
  agree) and ADR-008 **D-75** (`deviceTapId` idempotency untouched).
- Superseded ADRs are **not edited**. **ADR-006 remains reserved** for the consolidation.

> **Numbering note.** D-95…D-104 follow D-94 (ADR-014) and are used nowhere in the repository (checked
> 2026-10-27). D-103 and D-104 were added in revision 1 for the two genuinely new decisions. A future ADR
> continues from **D-105**.

## Context

Personnel (faculty, employees) can be an event's audience today but cannot be counted:
- `AttendanceRecords` is keyed on a non-null `StudentId`;
- the terminal freeze writes only `EventGroups.StudentId` rows;
- a personnel card at the reader is `CardNotFound`.

D-70 kept the personnel half of an audience as an advisory count, and recorded a dependency: **personnel
attendance and the personnel denominator land together, or the rate measures two populations** (the D-19
class). QA now wants personnel attendance (#703):

| Q | Ruling | | Q | Ruling |
|---|---|---|---|---|
| Q1 | Two numbers plus a combined total | | Q5 | Manual attendance accepts personnel |
| Q2 | One card, one person | | Q6 | Pre-registered personnel count |
| Q3 | **Refuse** personnel outside the audience | | Q7 | One `RfidUid` per personnel, no card table, for now |
| Q4 | **Refuse** inactive personnel | | Q8 | Codes, certificates, clearance stay student-only |

Six facts constrain the shape:

1. **`UX_Attendance_Event_Student_Occurrence` is deliberately unfiltered** (`HasFilter(null)`).
   - It relies on SQL Server treating NULLs as equal inside a unique index, which is what constrains a
     non-recurring row (`OccurrenceId` NULL).
   - EF silently appends `IS NOT NULL` for every nullable key column unless the filter is stated. That
     defect shipped once on this exact index.
2. **No unique index can span `RfidCards` and `Personnel.RfidUid`.**
   - `PersonnelService.RfidTakenAsync` checks only other personnel, so shared UIDs may already exist.
   - The roster import has **no wrapping transaction, by design** (`SisImportService`'s class remarks).
3. **`openapi.json` publishes closed schemas** (`additionalProperties: false`), and the app sends no version
   signal. A strict client generated from it fails on *any* new field, so the server cannot send a new
   shape unless the client asks for it.
4. **The replay lookup matches `(DeviceId, DeviceTapId)` before identity** (`FindByDeviceTapAsync`, on the
   single, batch and later-tap paths). A replay can therefore return a row belonging to a different person
   type than the card just read.
5. **Production is SQL Server 2012** (the VM); tests run 2022. A 2022 probe relaxed `StudentId` with its
   indexes and FK in place; 2012's `ALTER COLUMN` rules are stricter.
6. **The app migrates on startup, up only.** A `Down` is run by hand, and an older binary meeting the new
   schema reads `AttendanceRecord.StudentId` as a non-nullable `Guid`.

## Options Considered

**Row shape**
- **(Chosen) A: one table, `StudentId` XOR `PersonnelId`.** The capture state machine stays one
  implementation: TimeInOut, the B6 interval, `deviceTapId` replay, the insert race, and the live
  `RowVersion` cursor. `PreRegistrations` already models a person this way.
- **B: a separate `PersonnelAttendanceRecords` table.** Rejected. It duplicates the capture path, which is
  where this codebase's defects have lived, and the idempotency key would have to be unique across two
  tables.

**Freeze snapshot:** see D-97's table. **Card uniqueness:** see D-98.

**Old-client safety**
- **(Chosen) A capability header** (JJ, 2026-10-27), with **separate v1 and v2 response types** (D-99).
- **A global switch.** Rejected. It is all-or-nothing on event day, and it ties our release to the app's.
- **`/api/v2` routes.** Rejected. They double the device surface for one feature.
- **Lenient clients.** Rejected. Our own closed schemas make strict clients the documented case.
- **One superset DTO, with new members nulled for old clients.** Rejected at the gate (W2). `personType`
  would still serialize as `"Student"` on a student record, so old clients would receive a field they
  cannot parse.

---

## Decision

### D-95: Personnel are first-class attendees, and only eligible personnel are countable (supersedes D-70, D-72)

- **One resolver, `ExpectedPersonnelIds(event)`**, mirrors `ExpectedStudentIds`.
  - On a terminal event it returns the frozen `EventPersonnel` rows (D-97).
  - On a live event it returns the pre-registered personnel when D-103 says pre-registration governs the
    personnel side. Otherwise it returns the personnel side of the attached definitions, after ADR-013's
    scope mask.
  - Live sets keep only **not-deleted, `Active`** rows.
- **A personnel member is countable iff they are in that set.** The manifest, the tap, the event card
  lookup, manual entry, the summary and the freeze all call this one resolver (D-15 symmetry).
- **Tap refusals (Q3, Q4), in this order:**
  - the holder is `Inactive` → `PersonnelInactive`;
  - otherwise, the holder is not in the set → `PersonnelNotInAudience`.
- **Manual entry applies the same refusals** (JJ, 2026-10-27, decision 1).
  - **On a closed event, the test is membership in the frozen `EventPersonnel` set only, not the current
    `Status`.** A frozen member who has since gone inactive can still be marked by hand (ADR-003 D-17).
    That holds for a frozen member who has since been soft-deleted too (JJ, 2026-11-02): frozen means
    frozen.
  - **On a live event, an existing attendance row can always be corrected** (JJ, 2026-11-02). The two
    refusals guard the creation of a row. Without this, someone who tapped and then went `Inactive`
    could never be corrected by hand. A soft-deleted member on a live event stays `PersonnelNotFound`.
- **The asymmetry with students is deliberate and recorded.**
  - A student card resolves regardless of audience or `Status`. A walk-in is recorded, reported
    `unexpected`, and kept out of the rate (D-19, `KnownDefectTests.A_student_with_no_enrollment_is_not_an_expected_attendee`).
    The reason: the roster is the SIS's, and refusing would discard evidence that someone was present.
  - For personnel, QA ruled the opposite (Q3/Q4, against our recommendation): the chosen audience is the
    authority.
  - **The cost:** a refused personnel tap is not attendance. It is kept as scan-log evidence instead (D-100).
  - **The gain:** personnel `unexpected` stays near zero. On a live event it arises only when a live
    definition edit removes someone who has already tapped. On a terminal event it cannot arise, because
    the freeze also keeps anyone with an attendance row (D-97).

### D-96: The attendance row is `StudentId` XOR `PersonnelId`; the migration is data-preserving

**Entity changes.**
- `StudentId` becomes `Guid?`. Add `Guid? PersonnelId` (FK `Personnel`, `Restrict`).
- **A personnel row may have a null card link** (JJ, 2026-10-27, decision 5): `RfidCardId` stays NULL
  (Q7, no card table).
  - For a *refused* personnel tap, the tapped UID lives in the scan-log row (D-100).
  - For a *recorded* one, no scan-log row is written, so the UID is not kept beyond `Personnel.RfidUid` as
    it stands. **JJ accepted this as a known limit (2026-10-27)**, tied to Q7. The row still records who,
    which device and when. Personnel card history (the Q7 "later") is where the tapped UID gets a home;
    no extra write or column is added now.

**Migration `PersonnelAttendance`.** Scaffold it, then replace the `Up`/`Down` bodies with the SQL below.
Each block is its own `migrationBuilder.Sql` call, so it runs as its own batch; EF keeps the model snapshot.

**Up**
```sql
-- U1
ALTER TABLE dbo.AttendanceRecords ADD PersonnelId uniqueidentifier NULL;
-- U2
ALTER TABLE dbo.AttendanceRecords WITH CHECK ADD CONSTRAINT FK_AttendanceRecords_Personnel_PersonnelId
    FOREIGN KEY (PersonnelId) REFERENCES dbo.Personnel (Id);
CREATE INDEX IX_AttendanceRecords_PersonnelId ON dbo.AttendanceRecords (PersonnelId);
-- U3: relax StudentId. Indexes and FK are dropped and re-created around ALTER COLUMN: the 2012-safe form
DROP INDEX UX_Attendance_Event_Student_Occurrence ON dbo.AttendanceRecords;
DROP INDEX IX_AttendanceRecords_StudentId ON dbo.AttendanceRecords;
ALTER TABLE dbo.AttendanceRecords DROP CONSTRAINT FK_AttendanceRecords_Students_StudentId;
ALTER TABLE dbo.AttendanceRecords ALTER COLUMN StudentId uniqueidentifier NULL;
ALTER TABLE dbo.AttendanceRecords WITH CHECK ADD CONSTRAINT FK_AttendanceRecords_Students_StudentId
    FOREIGN KEY (StudentId) REFERENCES dbo.Students (Id);
CREATE INDEX IX_AttendanceRecords_StudentId ON dbo.AttendanceRecords (StudentId);
CREATE UNIQUE INDEX UX_Attendance_Event_Student_Occurrence
    ON dbo.AttendanceRecords (EventId, StudentId, OccurrenceId) WHERE [StudentId] IS NOT NULL;
CREATE UNIQUE INDEX UX_Attendance_Event_Personnel_Occurrence
    ON dbo.AttendanceRecords (EventId, PersonnelId, OccurrenceId) WHERE [PersonnelId] IS NOT NULL;
ALTER TABLE dbo.AttendanceRecords WITH CHECK ADD CONSTRAINT CK_AttendanceRecords_StudentOrPersonnel
    CHECK (([StudentId] IS NOT NULL AND [PersonnelId] IS NULL) OR ([StudentId] IS NULL AND [PersonnelId] IS NOT NULL));
-- U4: the D-97 snapshot table
CREATE TABLE dbo.EventPersonnel (
    Id          uniqueidentifier NOT NULL CONSTRAINT PK_EventPersonnel PRIMARY KEY,
    EventId     uniqueidentifier NOT NULL CONSTRAINT FK_EventPersonnel_Events_EventId REFERENCES dbo.Events (Id),
    PersonnelId uniqueidentifier NOT NULL CONSTRAINT FK_EventPersonnel_Personnel_PersonnelId REFERENCES dbo.Personnel (Id));
CREATE UNIQUE INDEX UX_EventPersonnel_Event_Personnel ON dbo.EventPersonnel (EventId, PersonnelId);
CREATE INDEX IX_EventPersonnel_PersonnelId ON dbo.EventPersonnel (PersonnelId);
```
- **No row is touched.** Every existing row keeps its `StudentId` and passes the `CHECK`, which `WITH
  CHECK` validates.
- **The EF model must state both filters exactly:** `HasFilter("[StudentId] IS NOT NULL")` and
  `HasFilter("[PersonnelId] IS NOT NULL")`. Neither may mention `OccurrenceId`.
- **Valid on 2012.** Filtered indexes date from 2008 and `THROW` from 2012. No 2016+ syntax is used.

**Down.** It refuses while any personnel data exists. Once the guard proves there is none, it drops what Up
added.
```sql
-- D1 (lock, then guard)
IF EXISTS (SELECT 1 FROM dbo.AttendanceRecords WITH (TABLOCKX, HOLDLOCK) WHERE PersonnelId IS NOT NULL)
   OR EXISTS (SELECT 1 FROM dbo.EventPersonnel WITH (TABLOCKX, HOLDLOCK))
    THROW 50015, N'ADR-015 Down refused: personnel attendance or frozen personnel rows exist. Roll forward.', 1;
-- D2: restore the pre-ADR-015 constraints exactly
ALTER TABLE dbo.AttendanceRecords DROP CONSTRAINT CK_AttendanceRecords_StudentOrPersonnel;
DROP INDEX UX_Attendance_Event_Personnel_Occurrence ON dbo.AttendanceRecords;
DROP INDEX UX_Attendance_Event_Student_Occurrence ON dbo.AttendanceRecords;
DROP INDEX IX_AttendanceRecords_StudentId ON dbo.AttendanceRecords;
ALTER TABLE dbo.AttendanceRecords DROP CONSTRAINT FK_AttendanceRecords_Students_StudentId;
ALTER TABLE dbo.AttendanceRecords ALTER COLUMN StudentId uniqueidentifier NOT NULL;
ALTER TABLE dbo.AttendanceRecords WITH CHECK ADD CONSTRAINT FK_AttendanceRecords_Students_StudentId
    FOREIGN KEY (StudentId) REFERENCES dbo.Students (Id);
CREATE INDEX IX_AttendanceRecords_StudentId ON dbo.AttendanceRecords (StudentId);
CREATE UNIQUE INDEX UX_Attendance_Event_Student_Occurrence
    ON dbo.AttendanceRecords (EventId, StudentId, OccurrenceId);   -- unfiltered, as before
-- D3: drop the now provably empty additions (JJ's approved exception, below)
ALTER TABLE dbo.AttendanceRecords DROP CONSTRAINT FK_AttendanceRecords_Personnel_PersonnelId;
DROP INDEX IX_AttendanceRecords_PersonnelId ON dbo.AttendanceRecords;
ALTER TABLE dbo.AttendanceRecords DROP COLUMN PersonnelId;
DROP TABLE dbo.EventPersonnel;
```
- **D3 is JJ's approved exception to the no-DROP rule** (2026-10-27, decision 2), with ADR-011 D-79 as
  precedent. It runs only after D1 has proven that the column is all NULL and the table is empty, so no
  datum is lost. Without D1's guard, D3 would be a data-loss migration.

**Running Up or Down by hand** (on the VM, or for the R1 rehearsal):
- Use `sqlcmd -b -I`. `-b` stops at the first error; without it, later batches would run outside the
  rolled-back transaction. `-I` sets `QUOTED_IDENTIFIER ON`, which filtered indexes require.
- Begin the script with `SET XACT_ABORT ON; SET ANSI_NULLS ON; BEGIN TRAN;`, end it with `COMMIT`, and keep
  the blocks as separate `GO` batches.

**Named tests.**
- `SchemaConstraintTests.The_event_student_occurrence_index_is_unique_and_unfiltered` is **replaced, not
  deleted**. Its successor asserts `filter_definition` is exactly `([StudentId] IS NOT NULL)`, and a twin
  asserts `([PersonnelId] IS NOT NULL)`.
- Two personnel rows for one event with `OccurrenceId` NULL → 2601. The same holds for students.
- Neither id, or both → 547.
- `Down` throws when one personnel row exists.

### D-97: The freeze writes personnel to a sibling table `EventPersonnel`, plus Absent rows

| Dimension | Extend `EventGroups` + its CHECK | **Sibling `EventPersonnel` (chosen)** |
|---|---|---|
| Existing readers | Clearance, attach/detach, roster and freeze read it; some read `StudentId!.Value` | Untouched |
| `CK_EventGroups_GroupOrStudent` | Rewritten as one-of-three | Byte-identical |
| §4.8 drift | Stops being "group XOR student" | One additive table |
| Q1 / Q8 | Filter by column; every reader learns to skip personnel | One table per population; clearance cannot see personnel |

- **Constraint.** `UX_EventPersonnel_Event_Personnel` is UNIQUE (EventId, PersonnelId), unfiltered, and the
  model states `HasFilter(null)`.
- **Tenant filter** through `Event`, exactly as `EventGroup`'s:
  `x => _school.CurrentSchoolId == null || x.Event!.SchoolId == _school.CurrentSchoolId`.
- **Writes happen only in `SnapshotAudienceAsync`,** in the freeze's one save (ADR-003 D-21) alongside the
  student rows.
  - It writes `ExpectedPersonnelIds` as it stands at the transition, using D-103's per-population selector
    and the same `includeDeleted` argument as the student freeze.
  - **It also writes every personnel member who already has an attendance row on the event** (JJ,
    2026-11-02), whether or not they are still `Active` or in the audience. Otherwise a tapper who went
    `Inactive` would fall out of the frozen set, and their row could never be corrected on the closed
    event.
- **Absent rows at close** (JJ, 2026-10-27, decision 6; resolves W4).
  - `StageAbsenteesAsync` also inserts `Status = Absent`, `CaptureMethod = Import`, `OccurrenceId = NULL`
    rows for every frozen personnel member with no row, just as it does for students.
  - Its insert race resolves through `UX_Attendance_Event_Personnel_Occurrence`, exactly as the student
    one resolves through its twin.
  - When no row is `unexpected`, `Present + Late + Absent + Excused = expected` holds over both
    populations, as the student tests already pin.
- **Named tests.**
  - A Draft or Open event has zero `EventPersonnel` rows. Close writes exactly the expected set, plus
    Absent rows for the non-tappers.
  - The close-time Absent insert racing a late personnel tap leaves one row, not two.
  - The summed invariant holds on a mixed event.

### D-98: Card uniqueness across both tables by a transaction-owned app lock on the UID

**Lock.** Every write that makes a UID *claimed* runs inside `CreateExecutionStrategy().ExecuteAsync` plus a
transaction, and takes:
```sql
EXEC @rc = sp_getapplock @Resource = N'eams/card-uid/{schoolId:N}/{normalizedUid}',
     @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
```
- **Any `@rc < 0` throws a typed exception that names the code.** That covers -1 timeout, -2 cancelled,
  **-3 deadlock victim** and -999.
  - -3 needs this rule most: it arrives as a return code, not as error 1205, so no retry strategy ever
    sees it.
- **After the lock, it re-checks both claims, writes and commits.**
- *Claimed* uses each table's own unique-index predicate:
  - students: `RfidCards.IsActive = 1`;
  - personnel: `Personnel.IsDeleted = 0 AND RfidUid IS NOT NULL`;
  - both also match on `SchoolId` and the normalized UID.
- Releasing a claim needs no lock.

**Deadlock rule.** A writer takes **all** of its UID locks before its first write, in **ordinal** UID order.
A single-UID writer takes **exactly one**.

**Writers.**
- **Personnel create and update,** when `RfidUid` is set or changed. They take one lock.
  - **The personnel XLSX import inherits the lock:** `PersonnelService.ImportAsync` runs each row through
    `CreateAsync`/`UpdateAsync`.
- **`StudentService` card issue:** one lock.
- **Roster import** (W3). The import keeps its no-wrapping-transaction design. Its **card pass** becomes
  one short `ExecuteAsync` unit of its own, which:
  1. takes a lock on every UID the pass will **insert**, in ordinal order, in one batch, before any write;
  2. re-checks both claims under those locks. A UID found claimed turns its planned insert into a row
     warning;
  3. inserts the remaining cards;
  4. saves with `SaveChangesAsync(acceptAllChangesOnSuccess: false)` and commits;
  5. calls `AcceptAllChanges()` only after the commit.
  - **The unit's save must contain only its own card inserts.** Today the resolver adds issued cards to the
    import's shared change tracker (`SisImportService`, the card resolver's `_db.RfidCards.Add(issued)`).
    **Requirement, asserted in code:** the unit checks that the tracker holds no pending entities other than
    its own card inserts before it saves, and fails loudly if it does.
    - Without that, the save would commit other passes' entities while holding the card locks.
    - Because `AcceptAllChanges` is deferred, a failed attempt leaves its cards marked `Added`, and the
      retried save writes those same entities again. The unit therefore calls `Add` exactly once per card,
      never again on retry. Alternatively, on retry it detaches its cards and rebuilds them before
      re-locking.
  - A UID held by personnel becomes a row warning, and no card is created, like `RfidCardRevoked`.
  - A transient fault replays the whole unit: it locks again, re-checks, then writes.
- **Any future restore or reactivation path** must use the same mechanism.

The refusal names the holder **to admins only**, through the existing `CardUidInUse` and `DuplicateRfid`
outcomes.

**Why not the alternatives.**
- **A shared registry table** would be the strongest guarantee. It conflicts with Q7, it needs four writers
  kept in sync, and its backfill fails on the very collisions the audit hunts. It becomes the answer if
  Q7 changes.
- **`SERIALIZABLE` range locks** deadlock read-then-insert writers.
- **Triggers** need the same locking and hide it.

**Proving test: `CardUidRaceTests`, on real SQL Server, deterministic.**
- Writer A pauses at a test-only barrier between its check and its write. Writer B, on the other table,
  must block on the lock. When A commits, B is refused.
- Pairs: personnel vs student card issue, and personnel vs the roster-import card pass.
- **Negative control:** remove the lock, and both commit → red.

**Pre-go-live audit.** Run it per school and hand the list to QA. Until it is clean, a collided UID resolves
to the **student**, because the tap reads student cards first, unchanged.
```sql
SELECT c.SchoolId, c.CardUid, c.Id AS RfidCardId, s.StudentNumber, s.IsDeleted AS StudentDeleted,
       p.Id AS PersonnelId, p.PersonnelNumber, p.Status AS PersonnelStatus
FROM dbo.RfidCards c
JOIN dbo.Students  s ON s.Id = c.StudentId
JOIN dbo.Personnel p ON p.SchoolId = c.SchoolId AND p.RfidUid = c.CardUid
WHERE c.IsActive = 1 AND p.IsDeleted = 0 AND p.RfidUid IS NOT NULL
ORDER BY c.SchoolId, c.CardUid;
```

### D-99: The capture contract (frozen; device payloads carry id, number, name and card UIDs only)

**Two families of response types, chosen at the API boundary from `CaptureCapabilities`** (resolves W2).
- **v1 is today's records, kept byte-identical in the schema and never extended.** They are `AttendanceDto`,
  `TapResult`, `TapBatchRowResult`, `TapBatchResult`, `EventManifestDto` and `EventManifestAttendeeDto`.
- **v2** is `AttendanceDtoV2`, `TapResultV2`, `TapBatchRowResultV2`, `TapBatchResultV2`, `EventManifestDtoV2`
  and `EventManifestPersonnelDto`.
- Services receive `CaptureCapabilities` (D-100) and apply the no-header rules themselves, the
  personnel-replay rule below included. They return a person-agnostic result; the boundary then picks a
  family, and its v1 mapper is the fail-closed backstop.
- **In `openapi.json`,** each device operation's documented `200` stays the v1 schema, so a client
  regenerated without opting in is unchanged.
  - The v2 schemas are published as components, referenced through an `x-eams-capability-response`
    extension on each capture operation.
  - **Both are generated, never hand-edited.** They come from a named Swashbuckle operation filter,
    **`CaptureCapabilityOperationFilter`** in `EAMS.Api/OpenApi`, registered in `EamsOpenApi`. The reason:
    `OpenApiDocumentTests.The_committed_contract_is_the_generated_one` fails on any hand edit.
  - **Code generators ignore `x-` extensions.** A client that opts in still gets each operation's return
    type generated as v1 `TapResult` (`additionalProperties: false`), which fails on `personType`. **An
    opted-in client must deserialize v2 itself:** the generated v2 *models* are usable, the generated
    *operation return type* is not. #707 must say so.
- **Admin (JWT) routes** return v2 directly: list and manual. The SPA ships with the server.
  - **Live keeps `AttendanceDeltaDto`, with the same additions** (JJ, 2026-11-02): `personType`,
    `personnelId`, `personnelNumber`, `personnelName`, and nullable student fields. It stays a delta shape
    rather than becoming `AttendanceDtoV2`.
- **A v2 tap or batch response sends `Cache-Control: private, no-cache`** (JJ, 2026-11-02), because it
  carries `personnelName`.

**`AttendanceDtoV2`** is every v1 member plus the following.
- `personType`: `"Student"` or `"Personnel"`, always present.
- `personnelId`, `personnelNumber`, `personnelName`: always present, `null` on a student record.
- `studentId`, `studentName`, `studentNumber`: nullable, `null` on a personnel record.

**Fail-closed v1 mapping** (resolves W1).
- The v1 mapper given a personnel record returns **`CardNotFound`** (404, today's body) and never builds a
  record.
- The service applies the same rule first: a no-header request whose `deviceTapId` replay hits a personnel
  row is `CardNotFound`, and a flagged scan-log row is written. The mapper is the backstop.
- **Named test:**
  1. A device stores a personnel tap *with* the header under `deviceTapId` X.
  2. It then sends a *student* card under X *without* the header, on tap, on batch, and on the TimeInOut
     later-tap path.
  3. The answer is `CardNotFound`, with no `personType` and no personnel member anywhere in the body.
- *Struck from revision 0:* the claim that "the no-header path never resolves to a personnel record" was
  false, because replay matches on the device key before identity (Context 4).

**Outcomes.**
- Added to `TapOutcomeCode`: `PersonnelNotInAudience` → **404** and `PersonnelInactive` → **404**.
- Each is an RFC 7807 body with `code` and `serverTime` and a generic `detail`. **It carries no name and no
  id.**
- *Why 404:* both are poison rows that must be shown, so a capable queue that branches on status first
  handles them exactly like `CardNotFound`. 403 means "stop and re-enrol" on device routes, and 409 means
  "delivered, drop" in §8.2.
- **Replay precedes refusal** on the capable path. A replay of a stored tap is `DuplicateIgnored` even if
  the person has since gone inactive or left the audience.
  - **The exception is a card that no longer resolves** (JJ, 2026-11-02). If the holder was soft-deleted or
    their UID changed, the replay is `CardNotFound`, because card resolution precedes replay. Students
    behave the same way.

**`EventManifestDtoV2`** is every v1 member plus `personnel` (`EventManifestPersonnelDto[]`, always present,
possibly `[]`).
- It holds only `Active`, not-deleted personnel who are in the audience.
- Each item is exactly `{ personnelId, personnelNumber, fullName, cardUids: [string] }`. **No email,
  position, classification, department, organization or status.**
- `MaxAttendees` counts students plus personnel.
- `version` is computed over the v2 body. Because `personnel` is always present, it never equals a v1
  version.
- **Personnel are read in a second statement, after the composed one** (JJ, 2026-11-02). This is a
  deliberate exception to D-46's one-snapshot note, to avoid a cartesian product. A concurrent write
  converges on the next pull.

**Event card lookup: `GET /api/v1/events/{id}/by-card/{cardUid}`.**
- Auth: DeviceKey + `AttendanceCapture`. It sits under the device-capture rate limit and sends
  `Cache-Control: private, no-cache`.
- **200:** `EventCardHolderDto { personType, studentId, studentNumber, personnelId, personnelNumber, fullName,
  cardUids }`, every member present and `null` where it does not apply.
  - A student is returned regardless of audience (walk-in).
  - A personnel member is returned only if countable.
- **A personnel card that a tap would refuse, as inactive or outside the audience, answers `CardNotFound`
  (404)** (JJ, 2026-10-27, decision 7; resolves W6). The lookup is a read and must not be a state oracle.
  Only taps carry the two distinct refusals, and only taps write scan-log rows for them.
- Other refusals: `EventNotFound` (404) and `EventNotOpen` (400).
- There is never a school-wide personnel lookup. `GET /students/by-card/{cardUid}` and its `StudentDto` are
  unchanged.

**Manual** (`POST /attendance/manual`, admin) takes `studentId` **or** `personnelId` (query), exactly one.
New `ManualOutcome`s:
- `PersonRequired` (400);
- `PersonnelNotFound`, `PersonnelInactive`, `PersonnelNotInAudience` (404), subject to D-95's closed-event
  rule.

**Tenancy (defence in depth, resolves the tenant item).**
- Every personnel query on these paths carries an explicit `SchoolId == event.SchoolId`, because the global
  filter is inert when no tenant is pinned (D-27's precedent).
- **The tap's existing student card read gains `c.SchoolId == ev.SchoolId`.** It changes no shape and no
  outcome.

**Contract tests, deliberately updated in B1.**
- `TapOutcomeContractTests` gains the two new codes.
- `CaptureContractTests` freezes the v1 shapes. Its fixture's `code` gains an `enum` of today's tokens, and
  it also freezes `StudentDto` for `/students/by-card`.
- Negative control: add a member to v1 → red.

### D-100: `X-EAMS-Capabilities` changes response types only, never access

**Syntax.**
- The field name is `X-EAMS-Capabilities`. HTTP field names are case-insensitive.
- The value is a comma-separated `#token` list (RFC 9110). Whitespace around each element is trimmed, empty
  elements are ignored, and repeated lines merge into one list.
- The token is **`personnel`**, matched **ordinal and case-sensitive**:
  - `personnel ` (with a trailing space) matches after trimming;
  - `PERSONNEL`, `Personnel` and `personel` do not match.
- Unknown tokens are ignored, and the header never causes a 400.
- *Why case-sensitive:* the risky direction is switching v2 **on**, so only the exact token does.

**Where it applies.**
- It is read once at the API boundary into a `CaptureCapabilities` value. Services never read headers.
- It is honoured on `POST /attendance/tap`, `/tap/batch` (every row) and `GET /events/{id}/manifest`, and
  ignored elsewhere.

**`Vary`.**
- The manifest sends `Vary: Authorization, X-EAMS-Capabilities` on every response an action produces,
  including the 304 and refusals. Pipeline 401, 403 and 429 responses never reach the action and do not
  carry it (JJ, 2026-11-02). It is built with `Headers.Append`, because today's code assigns
  `Authorization` with `=` and an assignment would overwrite.
- POST tap and batch send no `Vary`: POST responses are not cacheable.

**Access is identical with and without the header:** scheme, policy, tenant, rate-limit partition and
eligibility.
- **The one exception is the 413 `ManifestTooLarge`.** It counts what is sent, so an event can fit without
  personnel and exceed the limit with them.

**Without the header:** same status, same v1 schema; prose may differ.
- **A personnel card on tap or batch → `CardNotFound` (404).** No attendance row is written, and the
  `detail` may say the app needs updating, with no name and no id.
- The manifest is v1, and its `version` and ETag are computed exactly as today.

**Scan-log evidence for every personnel refusal on tap and batch.**
- It goes to `AuditLogs`, `Action = attendance.scan.unresolved`, so the Unresolved Scans panel shows it.
- The JSON gains `personnelId`, plus `reason`: `"PersonnelClientNotCapable"` without the header, `null`
  otherwise. `serverOutcome` is the code the device received.
- `EventScanDto` (admin) gains `personnelId` and `reason`.
- The panel now means "scans that produced no row".
- Logs carry ids and the normalized UID, never names.

### D-101: Readers of a null `StudentId`

- **The compiler is the sweep.** `StudentId` becomes `Guid?`, so every non-null assumption stops
  type-checking. Examples:
  - `AttendedStudentIds` and `StageAbsenteesAsync` both project `Select(a => a.StudentId)`.
  - Every `.Value` or cast must follow a `StudentId != null` predicate in the same query.
  - Equality filters, such as Clearance's, are already safe.
- **Student-only (Q8): attendance codes, certificates, clearance.** Each filters `StudentId != null` at the
  query root and behaves as if personnel rows were absent. Named test per feature: the same result with a
  personnel row present, and no exception.
- **Cross-event analytics (`AttendanceAnalyticsService`) stay student-only**, filtered at the root (JJ,
  2026-10-27, decision 3). Without the filter a null would count as one extra "student".
- **Event-level numbers include personnel (Q1).** That covers the summary, roster, live, list, event
  report, CSV, and the Reports page's per-event rows and their totals, which sum event-level figures.
  - These reads carry `personType`.
  - The CSV ID column carries `personnelNumber`. Names go through the student export's quoting
    (`a8aa92e`), and the formula-prefix check is a named test.
- **`StudentGroupProjection` is unaffected.** It reads `Enrollments` and `StudentGroupMembers`, never
  attendance.

### D-102: One population per ratio; every `Expected` moves together (resolves W5)

**Formulas.**
- `E_s` and `E_p` are the expected students and the expected personnel. *Attended* means `Present` or `Late`.
- **`attendanceRate = (|attended R_s ∩ E_s| + |attended R_p ∩ E_p|) / (|E_s| + |E_p|)`.** Each term is
  bounded by its own denominator, so the rate never exceeds 100%.
- `unexpected = |R_s \ E_s| + |R_p \ E_p|`.
- `present`, `late`, `absent` and `excused` are row counts over both populations.

**Every `Expected`** becomes `|E_s| + |E_p|`, and its DTO gains `expectedStudents` and `expectedPersonnel`:
- `EventSummaryDto`;
- `EventAudienceResultDto.Expected` (attach and detach result);
- `EventAudienceDto.Expected`;
- `EventRosterDto.Expected`. The gate's list omitted this one; it moves with the others.
- **There is no `expectedTotal`.** This departs from the backend task's Q1 wording (#705): `expected` already
  is the total, and a second name for it would invite the two to drift.

**What each "advisory" field becomes.** Each keeps its wire name, and the word "advisory" leaves its doc
comment.
- `EventAudienceDto.AdvisoryPersonnelCount`: **deprecated**, equals `expectedPersonnel`; the SPA stops
  reading it.
- `EventPreRegistrationDto.TotalAdvisoryPersonnelCount`: unchanged value (distinct not-deleted personnel
  registrants, any `Status`). It is a display count, **not** D-103's selector: an event whose only staff
  registrant is Inactive shows 1 here, yet its personnel side still comes from the audience.
- Per-session `AdvisoryPersonnelCount`: unchanged value.
- `TotalPreRegisteredStudentCount`: unchanged value. It now equals `expectedStudents` only when
  pre-registration governs the student side.
- `EventAudienceDto.ExpectedSource` describes the student side. A new `expectedPersonnelSource` (same
  vocabulary, `PreRegistration` or `Audience`) describes the personnel side.

**SPA text to change.**
- `eventAudience.ts` line 157 says "*N* students are now expected"; it must count personnel too.
- `eventAudience.ts` lines 283–287: `advisoryPersonnelText` says "not counted in expected". Remove it or
  reword it.
- `EventPreRegistrationSection.tsx` lines 104 and 265, `EventAudienceDefinitionsPanel.tsx` line 243, and
  `EventAudiencePanel.tsx` line 136.
- `Reports.tsx` lines 374 and 395, and `ReportService`'s `expected` sum.
- **CSV** (`EventReportCsv`): the "Expected" row is now the total, with new rows "Expected students" and
  "Expected personnel".

**History does not move.** Events closed before deploy have no `EventPersonnel` rows, so their numbers are
unchanged.

**Named test:** a mixed audience with a student walk-in, plus a personnel member who taps and is then
removed from the definition while the event is Open. The rate stays ≤ 100% and the personnel member is
`unexpected`. **Negative control:** drop `∩ E_p` → red.

### D-103: A linked pre-registration session replaces the expected set per population (QA, 2026-10-27, option A; resolves W7)

- **A population is replaced by its pre-registered members only if the event's linked sessions hold at
  least one *countable* registrant from that population.** Otherwise that population still comes from the
  event's audience.
  - Example: an audience of 100 students and 10 staff, with 60 students pre-registered and no staff, gives
    expected = 60 students + 10 staff.
- **The one predicate** (decided by the orchestrator in line with Q3/Q4):
  - **a countable student registrant** is not deleted;
  - **a countable personnel registrant** is not deleted **and** has `Status = Active`.
  - The selector, the replacing set it yields, the freeze and the named tests all use this same predicate.
    No raw registration count, such as `TotalAdvisoryPersonnelCount`, decides it.
- **This amends ADR-008 D-71 for students too.**
  - Before: a linked session with zero *student* registrants made `expectedStudents` 0.
  - Now: the student side falls back to the audience.
  - A linked session with no registrants at all therefore no longer zeroes `Expected`.
- **One selector serves both readers.** It answers "does pre-registration govern population P?", and the
  live read and `SnapshotAudienceAsync` both use it (D-15).
- **Named tests:**
  - QA's example;
  - its mirror (staff registered, no students);
  - a session whose only staff registrant is Inactive: the personnel side still comes from the audience;
  - a session with no registrants, live and after close.
- **Deliberately updated, not deleted:** `PreRegistrationEventLinkControlTests` and
  `PreRegistrationEventLinkApiTests`. They pin the old D-71 behaviour, where any linked session replaces
  the whole set.

### D-104: Personnel admin routes get their own permissions (JJ, 2026-10-27, decision 4)

- **New codes `personnel.read` and `personnel.write`** (`EamsPermissions.PersonnelRead` /
  `PersonnelWrite`), built in B1.
  - Every personnel admin route moves off `StudentsRead`/`StudentsWrite`.
  - Each route keeps the `[Authorize]` + `[HasPermissionNotEnforced]` pair, so `AuthorizationCoverageTests`
    stays green.
- **Grant migration `GrantPersonnelPermissionsToAdminRoles`.** It is data-preserving, following
  `GrantReportsReadToAdminRoles` and `GrantUserAdminToAdminRoles`:
  - insert-if-missing for the two `Permissions` rows;
  - grant to `SuperAdmin` and `SchoolAdmin`;
  - `RbacSeed` gains the same rows;
  - **`Down` copies the precedent's guard** (`GrantUserAdminToAdminRoles`, `THROW 51003`). It refuses if
    either code is granted to any role other than those two, so it never revokes a grant an admin made by
    hand.
- **Places that change together:**
  - `permissions.ts`;
  - `App.tsx` and `Layout.tsx`, which still gate `/personnel` on `studentsRead`, and `Layout.test.tsx`;
  - `PermissionRegistryTests`.
- **`AuthorizationCoverageTests` stays honest only if each personnel route's `Policy` and its
  `[HasPermissionNotEnforced]` code change in the same edit.** A route that changes one and not the other
  passes on a stale pair.
- **Consequence:** a non-admin role that holds `students.read` today loses personnel access. That is the
  point of the split.
- **Event authors keep a narrow personnel picker** (JJ, 2026-11-02; found at the B1 gate). The split cost
  Organizer its staff choices in the audience dialog and Pre-registration, and those screens need them.
  - `GET /api/v1/events/personnel-picker` is gated by `EventsWrite` and returns only id, number and full
    name.
  - Organizer is **not** granted `personnel.read`.

### Technical Plan drift

| § | Plan | Now |
|---|---|---|
| §4.9 | `StudentId` NOT NULL; `UNIQUE(EventId, StudentId, OccurrenceId)` | Nullable; `+PersonnelId`; XOR `CHECK`; two filtered unique indexes |
| §4 | — | `+EventPersonnel` (frozen expected personnel) |
| §6 | — | `GET /events/{id}/by-card/{cardUid}`; `personnelId` on manual; `X-EAMS-Capabilities` |
| §11 | — | `personnel.read`, `personnel.write` |

---

## Consequences

### Positive
- D-70's dependency is honoured: personnel attendance and the personnel denominator land together.
- Old apps keep working with no deadline. The v1 types cannot grow, and the frozen contract test proves it
  on every build.
- One resolver and one per-population selector mean the manifest, the refusal, the rate and the freeze
  cannot disagree.

### Negative
- **Rollback becomes roll-forward once a personnel row exists.** `Down` refuses, and an older binary would
  fail to read a null `StudentId`.
- A recorded personnel tap keeps no card reference (decision 5; accepted by JJ 2026-10-27 as a known limit
  until personnel card history lands).
- A capable device can tell from a tap's refusal code that a UID is a personnel card, and roughly its
  state. The lookup no longer reveals this (decision 7). Taps are authenticated and rate-limited, and
  nothing identifies the person.
- There are twice as many capture response types to maintain (v1 and v2).
- D-103 changes student behaviour on events with linked sessions but no student registrants.

### Risks
- **R1: SQL Server 2012.** U3 is written to 2012's rules but was proven only on 2022. Mitigation: rehearse
  on a restored copy of the VM database under 2012 before deploy, as a deploy gate, using the hand-run
  wrapper in D-96.
- **R2:** rates on Open events drop at deploy. Mitigation: deploy outside event hours and tell QA.
  - Also under D-103, an Open event whose linked session is empty resolves its whole audience as
    expected. On close, it marks that whole audience Absent (D-97). Before ADR-015 it froze nobody.
- **R3:** a writer that skips the lock reopens Q2. Mitigation: one shared helper, plus a race test per
  writer.
- **R4:** collisions that already exist resolve to the student until the audit is cleared. If the student
  is soft-deleted, the tap skips them and the UID reaches the personnel member (JJ, 2026-11-02).

## Answered since revision 0 (recorded, not open)

| Question | Answer | By |
|---|---|---|
| Manual entry and Q3/Q4 | Yes; closed event = frozen membership only | JJ, 2026-10-27 (D-95) |
| `Down` and the empty additions | Drop them after the guard; approved exception | JJ, 2026-10-27 (D-96) |
| Analytics | Cross-event student-only; event-level includes personnel | JJ, 2026-10-27 (D-101) |
| Personnel permissions | Built in B1 | JJ, 2026-10-27 (D-104) |
| Null card link | Allowed | JJ, 2026-10-27 (D-96) |
| Absent rows at close | Yes, as students | JJ, 2026-10-27 (D-97) |
| Lookup of a refusable card | `CardNotFound` | JJ, 2026-10-27 (D-99) |
| Pre-registration with no staff registered | Per population (option A) | QA, 2026-10-27 (D-103) |
| D-103 applied to students too, including a linked session with no registrants | Accepted, as option A reads | JJ, 2026-10-27 |
| Tapped UID on a recorded personnel tap | Not kept; known limit tied to Q7 | JJ, 2026-10-27 (D-96) |
| Reports page totals | Include personnel (sum of event-level figures); `AttendanceAnalyticsService` stays student-only | JJ, 2026-10-27 (D-101) |
| Tapper who later went `Inactive` | Frozen with their row; existing rows stay correctable on a live event | JJ, 2026-11-02 (D-95, D-97) |
| Soft-deleted frozen member on a closed event | Still markable; frozen means frozen | JJ, 2026-11-02 (D-95) |
| Soft-deleted member outside the frozen set on a closed event | `PersonnelNotInAudience`, not `PersonnelNotFound` | JJ, 2026-11-02 (D-95) |
| Organizer's staff picker after the split | Narrow route on `EventsWrite`, no `personnel.read` | JJ, 2026-11-02 (D-104) |
| Live DTO, second manifest statement, `Vary` scope | Accepted as built | JJ, 2026-11-02 (D-99, D-100) |
| `Cache-Control` on tap and batch v2 | `private, no-cache` | JJ, 2026-11-02 (D-99) |
| Old-client test 3, "both modes" | With and without the header | JJ, 2026-11-02 |
| From-event session capacity | Seeded from `expectedStudents + expectedPersonnel` | JJ, 2026-11-02 (D-102) |
| Summary query cost | About 7 per closed event and 11 per live event, accepted for now; measure on the VM before deploy | JJ, 2026-11-02 (D-102) |
| Roster `Entries` | Student-only until B3, which adds personnel so header and entries add up | JJ, 2026-11-02 (D-101, D-102) |
| `ExpectedSource` and `expectedPersonnelSource` on a terminal event | Describe the sessions as they are now, so the label may move after close and the frozen numbers do not. No migration; the SPA hides the label on frozen events | JJ, 2026-11-02 (D-103) |
| Soft-deleted personnel matched at close | Frozen with Absent rows, as D-97 orders and as students are, so `Expected` can rise at close | JJ, 2026-11-02 (D-97) |

## Open questions

None. Every question raised in the first draft and in revision 1 is answered in the table above.

## Follow-Up Actions

These are proposals, not authorised by this document. JJ decides.

- [ ] JJ reads and accepts or amends this ADR. Then #707 is finalised from D-99/D-100.
- [ ] #707 must tell the mobile developer that an opted-in client deserializes v2 itself. The generated v2
      models are usable; the generated operation return types stay v1 (D-99).
- [ ] Amendments for ADR-007, ADR-008 and ADR-013 (their bodies are not edited):
  - ADR-007 and ADR-008: a status note that D-70/D-72 are superseded by D-95. Close their "does the client
    need personnel attendance?" follow-ups as answered yes. Mark ADR-008 D-71 as amended by D-103.
  - ADR-013: D-90's "D-70 holds" bullet and R2's "advisory count" wording are superseded by D-95/D-102.
- [ ] Tell QA, as an FYI in the test notes, that D-103 also covers students: a linked session with no
      registrants no longer zeroes `Expected`. JJ accepted this as option A reads (2026-10-27).
- [ ] Run the D-98 audit and hand the list to QA. Rehearse on 2012 (R1).
- [ ] Old-client safety tests 1–6 and the security guardrails as named tests (MDVault #705).
