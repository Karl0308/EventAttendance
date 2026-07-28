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

## The one item with an unrecoverable failure mode

**ADR-002 D-11.** `Courses` is keyed `(SchoolId, CodeKey)` on the unverified assumption that course
codes are unique across colleges. Only one college's export has ever been seen. The widening path
(backfill `CollegeId` → `NOT NULL` → re-index) is **data-preserving only while no colliding data has
been imported**; afterwards it becomes a split requiring live-FK re-keying of half of
`CourseOfferings`.

The importer hard-fails a cross-college code collision and its message cites `ADR-002 D-11` by name.
**Do not downgrade that failure to a warning to clear a blocked batch** — that is precisely the
moment the hedge gets silently reversed.

The cheapest way to retire this: ask the registrar whether course codes are unique university-wide.
One question.

## Conventions

- Structure: Context → Options Considered → Decision → Consequences (positive / negative / neutral) →
  Follow-ups.
- `Proposed` means written but not yet agreed by the deciders it names. **Do not self-accept** an ADR
  listing someone else as a decider — an `Accepted` record of a decision nobody made is the exact
  failure the practice exists to prevent.
- A `Proposed` ADR is freely editable. An `Accepted` one is not: supersede it instead, and update this
  index.
- Around ADR-004 a consolidating ADR superseding 001–003 becomes worth considering. Not yet — the
  chronology is still load-bearing.
