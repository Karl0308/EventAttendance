# ADR-003: Phase 3a — the Event Audience and the Close-Time Freeze

**Status**: Accepted (2026-07-29, by JJ)
**Date**: 2026-07-28
**Deciders**: JJ; JoseArch (Team X)
**Relationship to ADR-001 / ADR-002**: **Amends both by reference.** Neither is edited; both remain
`Accepted` and D-1 through D-11 stand as written. This document continues the decision numbering at
**D-12** and records ten decisions that Phase 3a made and that neither earlier ADR covers. Where this
document and an earlier one disagree, this one wins; where it is silent, they are unchanged.

## Context

ADR-001 and ADR-002 are about the *data layer* — what the schema is and why it deviates from
Technical Plan §4. Phase 3a is the first phase that is about **behaviour**: what an event *is* over
its lifetime, who it expects, and what happens to those numbers when it ends.

JJ stated the product flow in one sentence:

> *"User creates Events. On that event we can select which Section (Students are included). Via
> tapping RFID or scanning we will record the attendance."*

and the requirement that turned out to be the hard part:

> *"only those who encoded on that event; the new student added/enrolled [after] the event was
> completed [is] not needed."*

Phase 3a delivered the events write surface with a status matrix, `POST /events/{id}/attendees`
writing §4.8 `EventGroups` rows from the derived Section groups the Phase 1 projection produces,
`GET /events/{id}/roster`, a corrected `EventSummaryDto.Expected`, and the close-time freeze.

### The thing this ADR exists to record honestly

**The specification for the freeze was wrong, and the implementation corrected it.**

The brief said: on close, write an `Absent` attendance row for every expected student who has none.
That was built, it worked, and it did not satisfy the requirement above. It freezes the absentee
**list** and not the **denominator**, because "who was expected?" was still answered by walking live
section membership. An event closed at 40 expected / 38 present silently became 41 expected the next
time an import touched the section — the rate moved, the absentee list did not change to explain it,
and nothing anywhere recorded that it had happened.

The freeze therefore has **two halves**, and the second one exists *because the first was shipped
alone first*. A reader six months out needs that chronology: the two-representation design in D-13
looks like over-engineering until you know that the single-representation version passed its entire
test suite while violating the requirement it was written for. Presenting the outcome as though it
were the plan would delete exactly the information that stops someone deleting half of it.

That pattern repeated at the Phase 3a review gate, twice, at smaller scale: the live/frozen
soft-delete rule (D-15) and the cancelled-event denominator (D-16) were each half-implemented and
each invisible in every passing test. Both are recorded here as decisions rather than left as
comments, for the same reason.

## Options Considered

The document shape is not re-litigated — ADR-002 Option 4 (a new ADR amending by reference, with
continuous decision numbering) is settled precedent and is what this document does. The live choice
in Phase 3a was **how a terminal event's numbers stay fixed**.

### Option 1: Do nothing — leave the denominator live forever
The audience is always resolved from group membership, whatever the event's status.
- **Pros**: One code path. Nothing to write, nothing to keep consistent, no second representation.
- **Cons**: Directly contradicts the requirement. A past event's attendance rate changes every time
  the registrar imports a roster, retroactively and invisibly. Every report built on §12 becomes
  non-reproducible: the same query run twice returns different history.
- **Effort**: Zero.

### Option 2 (specified, shipped, insufficient): Materialize `Absent` rows on close, nothing else
- **Pros**: Makes §12's Absentee Report a `WHERE EventId = @e AND Status = 'Absent'` index seek
  instead of a set difference across three tables. That benefit is real and was kept.
- **Cons**: Freezes the numerator's shape and not the denominator. Every plausible test passes —
  "closing marks the absentees" is true — while the number the requirement is about keeps moving.
  The failure is invisible on the day it is written and appears at the next import.
- **Effort**: Low. It is the half that already existed.

### Option 3 (chosen): Also snapshot the resolved audience as individual `EventGroups` student rows
Reaching a terminal status resolves the live audience once, writes it down as `EventGroups` rows
carrying `StudentId`, and a terminal event reads its denominator only from those. The group rows are
kept.
- **Pros**: The denominator becomes rows in a table, immune to any later import. No new table, no
  new concept — it reuses the representation §4.8 already has for "this individual is invited". The
  group rows survive as the record of *which section* was invited.
- **Cons**: Two representations of one audience coexist on a terminal event, and their
  non-contradiction is guaranteed by a *read rule* (nothing resolves group rows for a terminal event)
  rather than by a constraint. That is the standing hazard, and D-13 is written to survive it.
- **Effort**: Low — one query, one loop, one unique index already needed for D-12.

### Option 4: A dedicated `EventRosterSnapshots` table
- **Pros**: The snapshot is unmistakably a snapshot. No ambiguity about which rows are live and which
  are historical; could carry its own `SnapshotAt` and the resolving rule's version.
- **Cons**: A thirteenth table for a set of `(EventId, StudentId)` pairs that §4.8 already models. It
  forces every consumer of "who was expected?" to know the event's status *and* which of two tables to
  read, which is a worse version of the same conditional, spread wider. Rejected on ADR-001 D-1's line:
  additive tables are cheap, but not when an existing table already means the thing.
- **Effort**: Medium.

### Option 5: Denormalize `Events.ExpectedCount` as an integer written at close
- **Pros**: Cheapest possible freeze. One column, one write.
- **Cons**: Freezes the *number* and loses the *set*. A frozen roster you cannot enumerate cannot
  produce §12's Per-Event Roster or Absentee Report, cannot be audited ("which 41 people?"), and
  cannot be reconciled against the attendance rows — so the first disagreement between the stored
  count and the rows would be unresolvable. A count is a projection of the answer, not the answer.
- **Effort**: Low.

---

## Decision

Ten decisions, numbered continuing from ADR-002.

---

### D-12: `EventGroups` gets two filtered unique indexes — and one of them is denominator arithmetic

**Context.** §4.8 defines `EventGroups` with a CHECK that exactly one of `StudentGroupId` /
`StudentId` is set, and says nothing about duplicates. Until Phase 3a that omission had never cost
anything, because nothing outside test fixtures ever wrote the table. `POST /events/{id}/attendees`
changed that, and it is specified as idempotent — which is a uniqueness claim whether or not a
uniqueness rule exists.

**Decision.** Migration `EventAudienceUniqueness` adds two filtered unique indexes and nothing else:

| Index | Definition |
|---|---|
| `UX_EventGroups_Event_Group` | `UNIQUE (EventId, StudentGroupId) WHERE StudentGroupId IS NOT NULL` |
| `UX_EventGroups_Event_Student` | `UNIQUE (EventId, StudentId) WHERE StudentId IS NOT NULL` |

`IX_EventGroups_EventId` (non-unique, unchanged) is now **declared explicitly** in `EamsDbContext`.

#### `UX_EventGroups_Event_Student` is a correctness constraint, not a tidiness index

This is the part to read before touching either index. **The frozen denominator read is deliberately
not wrapped in `Distinct`.** For a terminal event, `ExpectedStudentIds` returns the student rows
directly — a bare projection over `EventGroups`, counted with `COUNT(*)`. The live branch de-duplicates
because it is a SQL `UNION`; the frozen branch has nothing to `UNION` and does not.

So `UX_EventGroups_Event_Student` is **the only thing standing between a duplicate row and a
double-counted student in a closed event's expected count.** Drop it as a redundant dedupe nicety and
the arithmetic silently breaks — and it breaks asymmetrically, because `GET /roster` computes its
`Expected` from set membership over the entries it lists while `GET /summary` counts rows. The two
endpoints would disagree about the same event, with no third source to say which is right.

Leaving the read undefended and the index load-bearing is the deliberate choice: a `Distinct` in the
query would mask duplicate rows rather than prevent them, and duplicates in a frozen audience are a
data-integrity problem, not a presentation one. The constraint is the right place for it — but that
only holds while the constraint exists.

**Why a constraint and not just the service's read-then-insert.** `AttachAudienceAsync` reads what is
already attached and only inserts the difference — which is correct for one request and loses to two.
"Attach these sections" is a form an organizer submits twice when the first response is slow, and two
concurrent posts both read "not attached" and both insert.

**What the group-side index buys, which is less.** `UX_EventGroups_Event_Group` does not affect any
number — the live denominator `UNION`s, so a section attached twice still counts each student once.
That is exactly what makes it worth constraining: the damage is invisible in every published figure
and surfaces only as an audience list with the same section in it twice, which reads as a UI bug and
gets investigated as one.

**Why two filtered indexes rather than one composite over both columns.** The CHECK constraint makes
exactly one of the pair NULL on every row, so a single composite would be relying on SQL Server's
`NULL = NULL`-inside-a-unique-index semantics to do two different jobs at once — group uniqueness and
student uniqueness — and to keep the two halves from colliding with each other. Two filtered indexes
state the two rules separately. `HasFilter` is written explicitly on both rather than left to the
provider's inferred `IS NOT NULL`: an index that happens to work for a reason nobody chose is one
refactor from not working. Filtered unique indexes are safe to depend on here for the reason ADR-002
recorded when it closed ADR-001 follow-up #3 — the provider is SQL Server and the constraint is
covered by integration tests against a real instance.

**Why `IX_EventGroups_EventId` had to be re-declared.** The first scaffold of this migration opened
with `DROP INDEX IX_EventGroups_EventId`: EF sees two composites leading with `EventId` and calls the
single-column index redundant. It is not. Both composites are filtered, and SQL Server cannot use a
filtered index for a query that does not imply its predicate — and the hottest audience read
(`WHERE EventId = @e`, no null predicate at all) implies neither. Left alone, that read would have
gone to a table scan, silently. This is the same trap `IX_StudentGroups_SchoolId` sprang in the
AcademicLayer migration, sprung again by the indexes added here.

**Consequences.**
- *Positive*: Idempotency is a property of the schema rather than of a method remembering to check,
  and the frozen count is correct by constraint rather than by query hygiene. The migration is two
  `CREATE INDEX` statements — purely additive, no `DROP`, safe on a populated database because
  nothing has ever written the table outside fixtures.
- *Negative*: A constraint carrying arithmetic significance is invisible at the call site. Nothing in
  `ExpectedStudentIds` says "this count is only correct because an index exists"; this ADR is where
  that is written down.
- *Negative*: Two filtered indexes plus one plain one on a small junction table is three indexes to
  maintain on every write, and the plain one exists only to defeat a scaffolder heuristic that will
  propose dropping it again on the next model change. That has now happened twice in this repo.
- *Neutral*: `EventGroups` still carries no `SchoolId`; tenancy reaches it through `Event`.
  `AttachAudienceAsync` additionally predicates `SchoolId` explicitly on the groups and students it
  resolves, because the §11 global filter is inert whenever no tenant is pinned — and an audience is
  the one place a cross-tenant row would be laundered into a denominator.

---

### D-13: A terminal event's `EventGroups` **student** rows are its denominator; the group rows become historical

**This is the load-bearing decision of Phase 3a.** Read the whole of it before changing anything in
this area.

#### The failure mode, stated first

The specified implementation was: on the transition to `Closed`, insert an `Absent` attendance row
for every expected student with no record. That is what shipped first. It is not a freeze.

`Absent` rows fix the absentee **list**. They say nothing about the **denominator**, because the
denominator was computed by following `EventGroups` group rows into current `StudentGroupMembers` —
and that membership keeps moving. Concretely: an event closes with 40 expected, 38 present, 2 absent,
95%. The registrar imports a roster the following week that adds one student to the attached section.
The closed event now reports 41 expected, 38 present, 2 absent, 92.7% — a rate that fell with no new
absentee to explain it, on an event nobody touched, for a person who was not in the building.

#### Why a passing test suite did not catch it

Every test asserted the **mechanism named in the brief** — "closing marks the absentees" — and the
mechanism worked. None asserted the **requirement the mechanism was a proxy for**: *a closed event's
numbers do not move*. The distinction only becomes visible if something moves the underlying data
after the close, and no test re-imported.

That is the transferable lesson, and it is not specific to this feature: **a test written against a
specified mechanism cannot detect that the mechanism was the wrong one.** Only a test written against
the requirement can. `EventCloseFreezeTests` is now built that way — every test re-enrols or
re-imports after the close, and there is a deliberate control
(`The_same_enrollment_does_move_an_open_events_numbers`) proving the same import *does* move a live
event, so the freeze cannot pass by nothing having changed.

#### The decision

Reaching a terminal status stages, in one `SaveChangesAsync`, **the audience snapshot**: the live
audience is resolved once and written down as individual `EventGroups` rows carrying `StudentId`.
Individually-attached students are already rows, so only the ones reached through a group are added.
On `→ Closed` it additionally stages **the absentee records** — an `Absent` / `CaptureMethod = Import`
/ `CheckInAt = null` attendance row for every expected student with no record. On `→ Cancelled` it
stages the snapshot only (D-16).

And `ExpectedStudentIds` answers the question two different ways depending on status:

| Event status | "Who is expected?" | Soft-deleted students |
|---|---|---|
| `Draft`, `Open` | group rows resolved into **current** membership, `UNION` individually attached | **excluded** |
| `Closed`, `Cancelled` | the individual **student rows only** — the snapshot | **included** |

**The invariant, in one sentence:** *for a terminal event, the set of `EventGroups` rows carrying a
`StudentId` **is** the denominator, and the rows carrying a `StudentGroupId` are a historical record
of what was invited that no query resolves.*

The soft-delete column of that table is a decision in its own right and is recorded as **D-15** — it
was the half of this design that shipped inconsistent with itself.

#### Why the group rows are kept

They are the only record of **which section** was invited — the question §4.8 exists to answer, and
the one `StudentGroupProjection` refuses to delete groups in order to preserve. The two
representations cannot contradict each other because nothing resolves the group rows for a terminal
event.

**This is the decision most likely to be "simplified" later**, by someone who opens a closed event,
sees a section attached *and* every one of its members attached individually, and concludes that one
of them is redundant. Both deletions are wrong and both fail silently:

- **Delete the group rows on close** → the closed event can no longer say which cohort was invited.
  Every number stays correct, so nothing fails; the loss is only noticed when somebody asks "who was
  this event for?" and the answer is a list of 300 names.
- **Delete the student rows and go back to resolving groups** → this is Option 2 again. Every test
  that does not re-import after the close still passes.

**Alternatives rejected** — Options 4 and 5 above (a dedicated snapshot table; a denormalized
`ExpectedCount` integer), for the reasons given there.

**Consequences.**
- *Positive*: The requirement is met against the mechanism that actually threatens it. §12's Absentee
  Report becomes an index seek on `IX_Attendance_EventId_Status`. Both halves commit in one
  transaction, so an event cannot exist in the "closed with a half-frozen roster" state, which is
  worse than either half alone — the two would disagree and the disagreement would look like an
  arithmetic bug rather than a missing write.
- *Negative*: **A terminal event carries two representations of one audience whose consistency is a
  read rule, not a constraint.** Nothing in the database prevents a future query from resolving group
  rows on a closed event and getting the pre-freeze answer. The protection is `ExpectedStudentIds`
  being the single place that decides, plus this ADR.
- *Negative*: The snapshot is `O(audience)` rows written inside an HTTP request — see D-21.
- *Neutral*: The frozen branch must be selected by the status being **terminal**, not by it being
  `Closed` — see D-16. `EventStatusTransition.IsTerminal` already expresses exactly that predicate.

---

### D-14: `Closed` is terminal — re-open returns 400, and the escape hatch is `POST /attendance/manual`

**Context.** Once D-13 has run, an event's denominator is rows and its absentees are rows. "Re-open
this event" then has no non-destructive implementation.

**Decision.** The transition graph has **no edge out of `Closed`**. `PATCH /events/{id}/status`
naming any other status returns `IllegalTransition` → **400**, with a message that says the status is
terminal and names the alternative. (`Cancelled` is terminal too.)

**Why both alternatives corrupt silently.**

- *Delete the materialized rows and go back to a live audience.* Those rows are not all machine-written
  — an organizer may have edited individual ones by hand between the close and the re-open, and a
  bulk delete cannot distinguish a hand-corrected `Excused` from a materialized `Absent`. It destroys
  records, and it destroys the ones somebody deliberately made.
- *Keep the rows and re-open anyway.* The event then calls itself live while carrying a frozen roster
  that no longer matches enrolment. `ExpectedStudentIds` would resolve groups again for a `Draft`/
  `Open` event, so the snapshot rows would be `UNION`ed with live membership — the denominator would
  be the union of two different points in time, which is a number with no meaning at all.

**The guarantee actually being kept is narrower than "a closed event cannot change".** It is:

> **A closed event's numbers cannot change *silently*, as a side effect of a later import.**

Individual correction stays possible through `POST /attendance/manual`, which does not require an open
event and never has. That property is load-bearing enough to be its own decision — **D-17** — because
this one is only defensible while it holds.

**Consequences.**
- *Positive*: The strongest property in the system — a past event's numbers are reproducible — holds
  without an audit trail on the freeze itself, because the freeze runs exactly once per event.
- *Negative*: Closing is unrecoverable in bulk. An organizer who closes the wrong event of two
  similarly-named ones has no undo and must correct row by row. This will happen, and there is no
  confirmation step on `PATCH /status` today.
- *Neutral*: Editing a closed event's own fields is separately refused (`AcceptsEdits`), because
  `StartAt` and `GraceMinutes` are what decided Present-versus-Late for every recorded row. Soft-
  **deleting** a closed event *is* allowed: the attendance rows survive untouched (every FK in the
  model is `Restrict`), so nothing is lost and one flag restores it.

---

### D-15: Soft-deleted students are excluded from a live denominator and included in a frozen one — and the snapshot resolves with them included

**Context.** `ExpectedStudentIds` filters soft-deleted students out of the live branch and leaves them
in the frozen branch. That asymmetry is correct, and until the Phase 3a gate it existed only as an XML
comment — while the *implementation* of it was half-done in a way no test could see.

**Why the two branches legitimately differ.** They are answering two different questions.

- **Live**, the question is *who should we expect to attend?* A student the roster says does not exist
  cannot be expected to attend. This is the same reasoning that closed `KnownDefectTests` DEFECT 1 on
  the capture path, and it keeps the live denominator agreeing with every other read in the system.
- **Frozen**, the question is *who did we expect, as recorded then?* A student deleted next year must
  not retroactively shrink a past event's denominator. "Frozen" has to mean frozen against **every**
  later edit, not only against enrolment — otherwise D-13's guarantee has a second door in it, and
  soft-deleting a student becomes a way to quietly improve a historical attendance rate.

**The defect this papers over, found at the gate.** The snapshot resolved the audience with
`includeDeleted: false` while the frozen read reports with `includeDeleted: true`. The two halves
therefore disagreed **exactly at the boundary where one hands over to the other**: an
individually-attached student who was already soft-deleted at close time was excluded from the
resolved set — so no `Absent` row was written for them — while their pre-existing `EventGroups` row
survived and counted afterwards. The closed event's `Expected` jumped by one at the moment of close,
and `Present + Late + Absent + Excused` was permanently one short of it, which `EventRosterDto`
documents as impossible after a close.

**Decision (JJ).** **The snapshot resolves with `includeDeleted: true`,** so the set the close writes
down is exactly the set the frozen read will report.

The rule generalizes past this instance and is the thing to keep:

> The live/frozen asymmetry is between **live and frozen**. It is never between **the snapshot and the
> frozen read** — those two must resolve identically, because they are the write and the read of one
> set.

**Alternatives rejected.**
- *Make the frozen read exclude soft-deleted students instead.* Symmetric, and it destroys D-13: a
  deletion next year would shrink a past denominator, which is the guarantee the whole phase exists
  to provide.
- *Delete the `EventGroups` row for a soft-deleted student during the freeze.* A data-loss write
  performed by the operation whose entire purpose is to preserve a record, and it forecloses undoing
  the student's deletion.

**Consequences.**
- *Positive*: The four buckets reconcile with `Expected` on every closed event, unconditionally. The
  write and the read of the frozen set are now provably the same query.
- *Negative — a bounded discontinuity remains, and it is accepted rather than fixed.* A student who is
  individually attached and *then* soft-deleted **before** the close still raises the denominator by
  one at the moment of close (excluded live, included frozen). They now also get an `Absent` row, so
  nothing fails to reconcile — but the number does step. The rule that would remove the step entirely
  is *"deleted before the close → not expected; deleted after → still expected"*, and that is
  **unimplementable on today's schema**: soft delete is a bare `IsDeleted` flag with no deletion
  timestamp anywhere in §4, so the two cases are indistinguishable at read time. Recording it as a
  known, bounded step is more honest than picking whichever branch happens to look tidier. Adding a
  `DeletedAt` column would settle it and is a follow-up.
- *Neutral*: Students reached only through a group are unaffected in either direction — the group
  resolution filters soft-deleted members on both branches, so they never enter the snapshot at all.

---

### D-16: `Cancelled` snapshots its audience too, but writes no absentees

**Context.** `Cancelled` is terminal (D-18) and its audience is locked, but `ExpectedStudentIds`
selected the frozen branch on `status == Closed`. So a cancelled event's denominator kept walking live
membership. An `Open → Cancelled` event that had already carried taps — the event was running, then
called off — therefore had a rate that changed month over month, with no absentee list to explain it.

**That is the exact failure D-13 exists to prevent, surviving on the other terminal status.** It was
neither documented nor, on the evidence, decided; `Cancelled` simply was not considered when the
frozen branch was keyed to one status name.

**Decision (JJ).** The transition to `Cancelled` **snapshots the audience** exactly as `Closed` does,
and writes **no attendance rows**. The frozen branch is selected by the status being terminal, not by
it being `Closed`.

**Why no absentees.** Nobody was expected to attend an event that did not happen. Materializing
`Absent` rows would put a whole cohort on record as having missed something nobody held — a permanent,
per-student, exportable claim about people's conduct, derived from an event's cancellation. That is
the same reasoning that refuses `Draft → Closed` (D-18).

**Consequence on the wire: `EventRosterDto.IsFrozen` widened.** It originally keyed off `Closed`
alone. Once a cancelled event's audience is snapshotted, its numbers are equally fixed, so a flag
meaning "closed" would report `false` for an event whose denominator can no longer move. The field
now means **"this event's audience is snapshotted"** and is true for both terminal statuses. Named
`HasFrozenAudience` internally, so the thing it asserts is the thing it is called. Phase 3b renders
this flag; it must not be read as a synonym for `Closed`.

**Why snapshot at all — the rejected alternative.** The tempting simplification is to report
`Expected = 0` (or null) for a cancelled event and skip the write: nobody was expected, so there is no
denominator. Rejected on two grounds. First, a cancelled event can already carry taps — students who
arrived before it was called off — so a zero denominator gives a rate that is undefined or infinite
over a real numerator. Second, and more importantly, it discards the **record of who was invited**,
which is precisely what §4.8 exists to hold and is the most useful artifact a cancellation leaves
behind: the first question after calling an event off is *who do we need to notify?* Keeping the
invite record costs one query and answers that question forever; discarding it saves nothing.

**Consequences.**
- *Positive*: The freeze guarantee is a property of **terminality**, not of one status string. A future
  fifth status that is terminal inherits it by construction instead of by somebody remembering.
- *Negative*: A cancelled event now reports a stable non-zero `Expected` against (usually) no
  attendance — a 0% rate that reads as catastrophic attendance rather than as a non-event. Any
  cross-event analytic (§12's Group/Course Analytics) must filter cancelled events out explicitly, and
  nothing forces it to. Follow-up below.
- *Neutral*: `EventRosterDto.IsFrozen` currently keys off `Closed` alone. Under this decision a
  cancelled event's numbers are equally fixed, so either that flag's meaning widens to "terminal" or
  the DTO needs a second signal. **Not settled here** — it is a published contract field and deserves
  its own call.
- *Neutral*: `Draft → Cancelled` now snapshots an audience for an event that never opened. That is
  cheap and correct: it records who *was going to be* invited, which is the same question with the
  same answer.

---

### D-17: `POST /attendance/manual` is the sanctioned post-close mutation path and must keep working on a terminal event

**Context.** D-14 makes `Closed` terminal. That is only defensible because a narrower correction path
exists — and it does: `ManualAsync` deliberately carries no open-event guard, while the tap path
requires `Status == Open`. Today that contract exists **only as prose**, spread across two components:
`EventStatusTransition` names the path in its terminal-status error message, and `AttendanceService`
omits the guard. Neither file alone shows the contract, and nothing tests it.

**Decision.** `POST /attendance/manual` works regardless of event status, deliberately. It is the
sanctioned route for correcting a terminal event, one student at a time, attributed through
`RecordedByUserId` and visible in the audit trail.

**The dependency, stated explicitly because it is the point of recording this.** If manual override is
later tightened to require an open event, **D-14 silently becomes a dead end with no recovery route at
all.** And that tightening would look like a safety improvement in review — "why does the override
path not check event status like the tap path does?" is a reasonable review comment with a wrong
answer. Anyone adding a status guard to `ManualAsync` must read D-14 first.

**Why the asymmetry with the tap path is correct and not an inconsistency.** A tap is an automated
capture with no human judgement behind it, no attribution, and an offline queue that can replay it
days later; letting those land on a closed event would move its numbers exactly as an import would. A
manual override is one identified person deciding about one identified student, recorded as such. The
escape hatch is safe *precisely because* it is slow, deliberate and attributable — those properties
are the safety mechanism, not friction to be optimized away.

**Consequences.**
- *Positive*: "Closed is terminal" costs no recoverability at the individual level. The strong
  guarantee and the practical escape hatch are separable, and each is enforced where it belongs.
- *Negative*: **The escape hatch is operationally thin at scale.** An assembly closed twenty minutes
  early with 200 students still queuing means 200 individual calls and no bulk path. The gate endorsed
  keeping `Closed` terminal and flagged the need as real and unmet; it is an open product question for
  JJ below, not something resolved here.
- *Neutral*: Nothing pins this behaviour in a test today — `ManualOverrideTests` does not exercise a
  closed event. A test is the only thing that turns "someone must read D-14" into "the build fails".
  Follow-up.

---

### D-18: The status graph is a named domain type, not conditionals in the service

**Context.** §4.5 lists `Draft` / `Open` / `Closed` / `Cancelled` and says nothing about how an event
moves between them. Before `EventStatusTransition` existed, the answer was whatever the last writer to
touch the column decided.

**Decision.** The matrix, as shipped:

```
             → Draft   → Open   → Closed   → Cancelled
  Draft         (=)      yes       no          yes
  Open          no       (=)       yes         yes
  Closed        no       no        (=)         no
  Cancelled     no       no        no          (=)
```

**Why it is a domain type and not a few `if`s.** The transition is also the **trigger for the
freeze**. A status write that bypassed the graph would therefore be a *silent* correctness bug: an
event could reach a terminal status with no snapshot, and nothing downstream could tell that from an
event whose numbers happen not to have moved yet. `EventWriteRequest` deliberately carries no `Status`
field and `POST /events` always writes `Draft`, so `PATCH /events/{id}/status` is the only door into
the column — which is what makes the freeze unskippable rather than merely usual.

**`Draft → Closed` is refused.** Closing something that never opened would mark its entire audience
`Absent` for an event nobody held — a roster of absentees for a thing that did not happen, written to
the attendance table as fact. `Cancelled` is the honest terminal state for that case and is reachable.

**`Open → Draft` is refused.** Taps can exist by then. Reverting to a status meaning "not yet
published" while attendance rows sit under it makes `Draft` ambiguous, and the only thing it buys is
an edit that `PUT /events/{id}` already allows on an open event.

**`(=)` is a no-op, not a self-transition.** A `PATCH` retried after a client timeout names the status
the event already holds. It succeeds and changes nothing — and critically, `FreezesRoster` returns
`false` when `from == to`. Without that clause, the retry would insert an `Absent` row for every
student who joined an attached section since the first close: the precise movement the freeze exists
to prevent, arriving through the mechanism meant to stop it.

**Consequences.**
- *Positive*: The lifecycle is one readable table with one owner, and illegal transitions produce a
  400 that lists what *is* reachable rather than a silent write. `IsTerminal` gives D-16 its predicate
  for free.
- *Negative*: The graph is enforced in code only. The `Status` column is `nvarchar` carrying enum-ish
  strings (per `CLAUDE.md`), so nothing at the database level prevents a direct `UPDATE` from putting
  an event in `Closed` with no frozen roster. Any future bulk-status tooling must go through this type.
- *Neutral*: `IllegalTransition` maps to **400** while `EventLocked` maps to **409**, following the
  split `SisImportController` already draws — a bad transition is wrong under any circumstances; a
  locked event rejects a well-formed payload that would be accepted in another state.

---

### D-19: `EventSummaryDto.Expected` is the invited population — a published number changed meaning

*Closes `KnownDefectTests` GAP 6.*

**Context.** `Expected` used to be the count of attendance **rows** on the event. That made the
attendance rate structurally incapable of falling below the share of rows somebody had marked `Absent`
by hand, and made an absentee unrepresentable: a student who never taps has no row, so they were not
counted as expected either. It was always about 100% and it meant nothing.

**Decision.** `Expected` is the invited population — every student in an attached §4.8 group plus
every individually attached student, de-duplicated in SQL, excluding the soft-deleted (and, once
terminal, read from the D-13 snapshot under D-15's rule). The field is **not** renamed; its meaning
changed.

`AttendanceRate` is `(Present + Late) ∩ Expected / Expected` — **the numerator is intersected with the
invited population**, so the rate cannot exceed 100. Walk-ins are not discarded: they surface as a
new `Unexpected` count on both `EventSummaryDto` and `EventRosterDto`.

> **This reverses an earlier draft of this decision, and the reversal is the point.** The first
> version left the rate uncapped and argued that a rate over 100 was a self-describing signal that
> the audience was wrong, which narrowing the numerator would hide. The Phase 3a gate rejected that:
> the numerator counted *every* row on the event while the denominator counted *only* invited
> students, so 29 expected + 29 present + 2 alumni taps read **106.9%** — and `GET /roster` already
> computed the correct figure, so the two endpoints disagreed about the same event with no third
> source to adjudicate. A phase whose stated purpose was ending meaningless ~100% rates cannot ship a
> route to a meaningless >100% one.
>
> The signal survives, relocated: `Unexpected` says exactly what the uncapped rate was gesturing at,
> without corrupting the headline number. The bound is **structural, not clamped** — a server-side
> SQL `INTERSECT` makes the numerator a subset of the denominator by construction. A `Math.Min` was
> deliberately rejected: it would cap a still-wrong number and hide the walk-ins, which is the
> failure the original reasoning was right to fear.

`Present`/`Late`/`Absent`/`Excused` still count **every** row, so the buckets keep reconciling with
`GET /attendance?eventId=`. Note the units differ by design: the buckets are rows, while `Unexpected`
and the numerator are people. Identical today because `UX_Attendance_Event_Student_Occurrence`
forces 1:1 — divergent once §4.6 occurrences populate, and the people-based figures are the ones
that keep the rate bounded when that happens.

`Expected = 0` means **no audience is attached** — there is deliberately no fallback to the old row
count, because a denominator that silently changes definition depending on whether a table is empty
is worse than one that is honestly absent.

**Why this is not inconsistent with ADR-002 D-10**, which refused to change the meaning of a published
`Result` value for exactly this kind of convenience. The distinction is whether the old meaning was
*correct*. D-10's `Inserted` was right and would have been made wrong by the change; a client relying
on it was relying on something true. Here, `Expected` was wrong under every reading of §4.5, §6.7 and
§12 — there is no reading of the plan under which the recorded-row count was the documented number.
Fixing a number that never matched its contract is a fix; redefining one that did is a breaking
change. Both decisions follow the same rule and land on opposite answers because the inputs differ.

**Consequences.**
- *Positive*: `Expected`, the roster header, and `GET /attendance?eventId=` all reconcile, because the
  four status buckets are counted over every attendance row on the event — including rows belonging to
  students the audience does not name. The summary is not a fifth opinion.
- *Negative*: Any consumer that read `expected` before Phase 3a gets a different number for the same
  event with no version signal. In this build that is `web-admin`'s mock facade and nothing else, so
  the cost is being paid at the only moment it is cheap.
- *Neutral*: The counts moved from C# comparison to SQL, so bucket matching now uses the database
  collation and is case-insensitive. A row stored as `"present"` now lands in the Present bucket
  instead of no bucket. Strictly better, and not a licence to store whatever —
  `AttendanceStatus.TryNormalize` still canonicalizes on every write.

---

### D-20: A non-enrolled student's tap is accepted and surfaced as `isExpected: false`, not blocked

**Context.** With D-19, the denominator is the invited population resolved from enrolments in a term.
That dissolved an open product question rather than answering it: the tap path ignores §4.3's
`Status`, so a `Graduated` student's card resolves and records attendance like an active one, and the
plan does not say whether that is intended.

**Decision.** The tap is **accepted**. `GET /events/{id}/roster` lists the union of the expected and
the recorded, and a student with a record but no invitation appears flagged `IsExpected = false`.

**Why not block it.** An alumnus cannot inflate any count regardless of what the tap path does — they
have no current-term enrolment, so they are in no section group, no denominator and no absentee list.
Blocking the tap would therefore **discard evidence that a person was physically present, in order to
protect a number that no longer depends on it.** Whether a card opens a turnstile and whether the
institution expected that person are two different questions, and they were being conflated.

**Why the roster lists them rather than hiding them.** A student who tapped without being invited has
an attendance row that the summary counts. Leaving them off the roster would produce a page whose
lines do not add up to the totals printed above them — the same class of plausible-but-wrong number
this phase exists to remove. For the same reason there is no `IsDeleted` filter on the recorded half:
a student soft-deleted *after* being recorded still has a row the summary counts.

**Consequences.**
- *Positive*: The system records what happened and separately records what was expected, and never
  nets one off against the other. `KnownDefectTests` DEFECT 3's product question is closed by
  dissolution, and the guarantee is asserted where it actually lives (the projection and the audience)
  rather than at the tap path.
- *Negative*: An operator watching a live event sees roster lines for people who were not invited,
  with no visual separation in the DTO beyond the flag. At an open campus event that could be a
  material fraction of the list.
- *Neutral — and this is unimplemented, not decided.* §4.5's `RequireRegistration` ("Restrict to
  associated students") is stored, echoed on `EventDto`, round-tripped by `PUT`, and **read by
  nothing**. D-20 is a decision about the *default*; it is not a decision that the flag should stay
  inert. A future reader must not take §4.5 at face value here. Follow-up below.

---

### D-21: The freeze runs synchronously inside `PATCH /status`, not as the plan's batch job

**Context.** The plan's §11.4 status logic says: *"No tap by `EndAt` for an expected attendee →
`Absent` (**batch job at event close**)"*, and §3's project layout names an `EAMS.Worker` project for
Hangfire jobs. Neither the worker project nor Hangfire exists in this build.

**Decision.** The freeze runs **inline, inside the status change**, in a single `SaveChangesAsync`.

**Why, and it is not only that Hangfire is absent.** One `SaveChangesAsync` is one transaction, so the
terminal status, the audience snapshot and every `Absent` row commit together or not at all. A
background job would introduce a window — however short — in which an event is `Closed` while its
denominator is still live, which is precisely the state D-13 exists to eliminate; and a job that fails
or is lost leaves that state permanently. No explicit transaction is opened by hand: under the
production registration's retry-on-failure strategy, EF requires user-initiated transactions to be
threaded through `CreateExecutionStrategy`, which is real complexity bought for nothing here.

The read-then-write race that the missing explicit transaction leaves open is handled where it
happens. A tap can land between reading who already has a record and committing — the organizer
closes while the last student is walking through the reader, which is ordinary rather than exotic. The
insert loses to `UX_Attendance_Event_Student_Occurrence`, and **losing is the right answer**: the row
about to be written as `Absent` now exists as `Present`, which is the truer record. The pending rows
are detached, the diff is recomputed, and the save is retried **once** — bounded rather than looped,
so a genuinely unexpected violation surfaces as an error instead of spinning.

**Consequences.**
- *Positive*: Atomicity, and the organizer gets the count in the response ("N expected attendees with
  no record were marked Absent") instead of a job id and a page to refresh.
- *Negative*: **The close is `O(audience)` inside an HTTP request, and it is untested above n=2.** An
  institution-wide event staged from several section groups writes up to one `EventGroups` row *and*
  one `AttendanceRecord` per expected student in one transaction. At a few thousand students that is a
  multi-second request holding locks on the two busiest tables in the system. Nothing today bounds it,
  times it, or reports progress. This is the decision in Phase 3a most likely to need revisiting.
- *Neutral*: Moving to a job later is possible but is **not** a drop-in: it requires making the status
  write and the freeze atomic some other way — most plausibly an intermediate `Closing` status that
  the tap path rejects and the denominator treats as already frozen. That is a schema-visible change to
  §4.5's status vocabulary and would need its own ADR.

---

## Accepted Context (not drift)

- **The roster's `Section` column is ADR-002 D-9's display cache, read for display only.** It is not a
  join key, not a filter and not a grouping — so D-9's rule holds. But this is the first
  operator-facing surface to render it, and two of D-9's known weaknesses now show up in front of a
  user: a multi-section student's line can display a section *other than the one that invited them*,
  and D-9's evenly-split tie-break produces a confident-looking value that means nothing. Neither is
  new; both are newly visible.
- **Everything asserted about the freeze is asserted at n=2.** `EventCloseFreezeTests` arranges one
  section and two students. The correctness arguments are sound at any size; the *performance*
  arguments in D-21 are unevidenced above that.
- **`EventGroups` has no `SchoolId` and reaches tenancy through `Event`.** That is consistent with the
  §11 dependent-filter pattern, and `AttachAudienceAsync` carries an explicit `SchoolId` predicate on
  top because the global filter is inert when no tenant is pinned.
- **A group from a non-current term warns rather than refuses**, while a cross-school reference and the
  `(unspecified)` section sentinel are hard refusals. `Events` has no `TermId`, so "another term" can
  only be measured against a flag that moves under the event's feet — refusing would make an identical
  request start failing for an event nobody touched. This follows ADR-001 D-5's line: warn on
  anomalies, hard-fail only where the alternative is unrecoverable.

## Consequences (overall)

### Positive
- The product requirement — *"only those who encoded on that event"* — is met against the mechanism
  that actually threatens it (a later import), and is tested that way rather than tested against the
  mechanism that was specified.
- The reason the design has two halves is now recorded with its chronology, so the half that is
  invisible in every number has a written defence against being deleted as redundant.
- The freeze is now a property of **terminality** rather than of one status name, and its read/write
  symmetry (D-15) is stated as a rule rather than left to two call sites agreeing by luck.
- Two published numbers (`Expected`, `AttendanceRate`) now mean what §4.5, §6.7 and §12 say they mean.

### Negative
- Four documents to read together now (§4, ADR-001, ADR-002, ADR-003), and nothing enforces it. The
  consolidation ADR that ADR-002 anticipated "around ADR-004" is now due at ADR-004, not optional.
- D-13's non-contradiction is a read rule enforced by one method and this document. That is a weaker
  mitigation than a constraint, and no constraint is available — the two representations are both
  legal rows in the same table by design.
- Three of these ten decisions (D-15, D-16, D-17) were found at a review gate rather than at design
  time, each having survived a green test suite. The common shape is worth naming: **every one was a
  rule that existed correctly in prose and incompletely in code.** That is the failure mode this
  register is meant to catch, and it caught them late rather than early.
- D-21's close is unbounded in size inside a request, with no evidence above two students.

### Neutral
- Decision numbering remains continuous (ADR-001: D-1..D-6; ADR-002: D-7..D-11; ADR-003: D-12..D-21).
  A future ADR continues from **D-22**.
- §6's published API shape is unchanged except additively: `POST /events/{id}/attendees` and
  `GET /events/{id}/roster` are both defined in §6.3, and the two `DELETE` sub-resources for detaching
  are new — the plan defines no removal at all.

## Follow-Up Actions

Ordered by consequence, not by effort.

- [ ] **Pin `POST /attendance/manual` on a closed event with a test** (D-17). It is the only recovery
      route out of a terminal status and nothing currently fails if someone adds a status guard to
      `ManualAsync`. This is the cheapest high-value item in the list.
- [ ] **Open product question for JJ: bulk correction after a close** (D-17). An assembly closed early
      with 200 students queuing means 200 manual calls. The gate endorsed keeping `Closed` terminal
      *and* judged the escape hatch operationally thin. Options worth costing: a bulk manual-override
      endpoint taking a student list; a time-boxed "reopen window" that is itself audited; or accepting
      the cost and solving it in the UI. **Not resolved here** — it needs a product call, not an
      architectural one.
- [ ] **Decide what `EventRosterDto.IsFrozen` means for a cancelled event** (D-16). Its numbers are now
      equally fixed but the flag keys off `Closed`. Published contract field; widen its meaning or add a
      second signal, but do not leave it ambiguous.
- [ ] **Make §12's cross-event analytics exclude cancelled events explicitly** (D-16). A cancelled
      event now reports a real denominator against no attendance, which reads as a 0% rate in any
      report that groups by course or section.
- [ ] **Decide what `RequireRegistration` does** (D-20), or record that it stays inert. It is currently
      a stored, round-tripped, user-settable flag that changes nothing, which is the worst of the three
      options because §4.5 says otherwise.
- [ ] **Load-test the close** (D-21). Establish the audience size at which `PATCH /status` becomes
      unacceptable, and decide the mitigation before an institution-wide event is closed in
      production rather than after.
- [ ] **Consider a `DeletedAt` timestamp on soft-deleted entities** (D-15). It is what would let the
      frozen denominator distinguish "deleted before this event closed" from "deleted afterwards" and
      remove the accepted discontinuity. Additive; affects more than `Students`, so it deserves its own
      decision rather than being smuggled in.
- [ ] **Paginate or bound `GET /events/{id}/roster`.** It returns every entry in one response, and for a
      live event `IsExpected` is evaluated as a correlated subquery over the `UNION` per candidate row.
      Fine at 50 students; unmeasured at 5,000.
- [ ] **Decide whether a multi-section student's roster line should show the inviting section** rather
      than ADR-002 D-9's home-section cache. Related to the still-open D-9 UI-signal follow-up, and
      now more visible than when that was written.
- [ ] **Add a confirmation step to closing** (D-14). The action is irreversible in bulk and is one
      `PATCH` away from an adjacent event in a list.
- [ ] **Write ADR-004 as the consolidating ADR** superseding 001–003, as ADR-002 anticipated. Four
      documents is where the "read them together" convention stops being followed.
- [ ] Carried forward, unchanged by this ADR: ADR-001 D-6's un-stubbing of §11 auth in full; ADR-002
      D-11's registrar question and the `Courses.CollegeId` widening deadline; the D-2 display-cache
      refresh trigger; D-4's mapping-version retention; the §14 retention policy for
      `SisImportRowEntities`.
