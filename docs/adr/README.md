# Architecture Decision Records

**Read this index first.** ADRs here are immutable once `Accepted` — a later ADR amends an earlier
one *by reference*, and the earlier one is never edited to point forward. That rule keeps the
chronology honest (you can tell which decisions were made before the real roster was in hand), but
it creates one hazard this file exists to close:

> **An `Accepted` ADR cannot tell you it has been amended.** Open ADR-001 alone and you will find six
> unticked follow-up boxes, four of which ADR-002 has since closed. The boxes are not the record —
> this index is.

## The register

| ADR | Title | Status | Notes |
|---|---|---|---|
| [001](ADR-001-schema-drift-from-technical-plan.md) | Schema drift from the Technical Plan | **Accepted** | D-1…D-6. **Amended by ADR-002** — do not read its follow-up list as current |
| [002](ADR-002-phase-1-2-schema-and-import-decisions.md) | Phase 1–2 schema and import decisions | **Accepted** (2026-07-28) | D-7…D-11. Amends ADR-001 by reference; closes four of its follow-ups. **Now immutable** |
| [003](ADR-003-phase-3a-event-audience-and-close-freeze.md) | Phase 3a — the event audience and the close-time freeze | **Proposed** | D-12…D-21. Amends 001 and 002 by reference; closes GAP 6. The first ADR about *behaviour* rather than schema |

Decision numbers run continuously across documents, so `D-9` is unambiguous without naming the ADR.

## What is decided where

| Question | Answer lives in |
|---|---|
| Why do we deviate from Technical Plan §4 at all? | ADR-001, Context |
| The academic layer (twelve tables) | ADR-001 **D-1**, table split recorded in ADR-002 **D-8** |
| Why `Students.Course/YearLevel/Section` are a derived cache | ADR-001 **D-2** |
| Which enrollment that cache shows | ADR-002 **D-9** |
| Why `RfidCards.CardUid` uniqueness was rescoped | ADR-001 **D-3** |
| Import mapping config, warning-vs-status semantics | ADR-001 **D-4/D-5**, refined by ADR-002 **D-10** |
| Why auth is deferred and what makes that safe | ADR-001 **D-6** |
| Two email columns | ADR-002 **D-7** |
| **The two natural-key hedges — and their expiry** | ADR-002 **D-11** ⚠ |
| Why `EventGroups` has two filtered unique indexes | ADR-003 **D-12** ⚠ (one of them is denominator arithmetic) |
| **Why a closed event has its audience stored twice** | ADR-003 **D-13** ⚠ |
| Why `Closed` is terminal, and how to correct one anyway | ADR-003 **D-14**, escape hatch in **D-17** ⚠ |
| Why soft-deleted students are in a frozen denominator but not a live one | ADR-003 **D-15** |
| Why cancelling freezes but writes no absentees | ADR-003 **D-16** |
| The event status transition matrix | ADR-003 **D-18** |
| What `Expected` / `AttendanceRate` mean, and why a walk-in is not blocked | ADR-003 **D-19/D-20** |
| Why the freeze is not the plan's Hangfire batch job | ADR-003 **D-21** |

## Items with silent or unrecoverable failure modes

Each of these is a decision that fails **without an error and without a failing test**. They are
listed here because that is exactly the class of thing nobody re-derives from the code.

### ADR-002 D-11 — the course-key hedge (unrecoverable)

`Courses` is keyed `(SchoolId, CodeKey)` on the unverified assumption that course
codes are unique across colleges. Only one college's export has ever been seen. The widening path
(backfill `CollegeId` → `NOT NULL` → re-index) is **data-preserving only while no colliding data has
been imported**; afterwards it becomes a split requiring live-FK re-keying of half of
`CourseOfferings`.

The importer hard-fails a cross-college code collision and its message cites `ADR-002 D-11` by name.
**Do not downgrade that failure to a warning to clear a blocked batch** — that is precisely the
moment the hedge gets silently reversed.

The cheapest way to retire this: ask the registrar whether course codes are unique university-wide.
One question.

### ADR-003 D-13 — a terminal event stores its audience twice, on purpose

The group rows say *which section* was invited; the student rows **are** the denominator. Deleting
either set looks like removing a redundancy and neither deletion breaks a test: drop the group rows
and the event can no longer say who it was for; drop the student rows and the denominator silently
starts drifting with every roster import again — which is the bug the whole phase exists to fix, and
which passed a full green suite once already.

### ADR-003 D-12 — `UX_EventGroups_Event_Student` is arithmetic, not tidiness

The frozen denominator read is deliberately not `Distinct`-wrapped. That index is the only thing
preventing a double-counted student in a closed event's expected count. It reads like a dedupe nicety
and is not one.

### ADR-003 D-17 — `POST /attendance/manual` must keep working on a closed event

It is the only recovery route out of a terminal status (D-14). Adding a status guard to it —
a change that reads as a *safety improvement* in review, since the tap path has one — turns D-14 into
a dead end with no way back. Nothing tests this today.

## Conventions

- Structure: Context → Options Considered → Decision → Consequences (positive / negative / neutral) →
  Follow-ups.
- `Proposed` means written but not yet agreed by the deciders it names. **Do not self-accept** an ADR
  listing someone else as a decider — an `Accepted` record of a decision nobody made is the exact
  failure the practice exists to prevent.
- A `Proposed` ADR is freely editable. An `Accepted` one is not: supersede it instead, and update this
  index.
- A consolidating ADR superseding 001–003 is **now due at ADR-004**, not merely worth considering.
  Four documents is where "read them together" stops being followed. The chronology is still
  load-bearing, so the consolidation must preserve *when* each decision was made and with what in
  hand — particularly ADR-003 D-13, which is only comprehensible in the order it happened.
