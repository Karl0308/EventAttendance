# ADR-008: A Linked Pre-Registration Session Replaces an Event's Expected Audience

**Status**: Proposed (2026-10-05) — awaiting JJ's read-through and the two confirmations in the
boxes below, then flip to `Accepted`.
> The *direction* below is JJ's product pre-decision (relayed in the Phase 3 brief, originating in
> MDVault #540 D3 and the capture-expectation answer to #540 §6.3 Q3); this *document* formalizes it
> and resolves the "how". The register's rule holds: JoseArch recommends; JJ accepts. **Do not
> self-accept.**

**Date**: 2026-10-05
**Deciders**: JJ; JoseArch (Team X)
**Relationship to ADR-003 and ADR-007**: **Amends ADR-003 and ADR-007 by reference. Neither is
edited.** ADR-003 is `Accepted` and immutable (D-12…D-21 stand as written). ADR-007 is `Proposed`
pending JJ's read-through (D-69, D-70); this document builds on D-69/D-70 as the established pattern
for "a new audience source on an event" and, like ADR-007 before it, will have its references firmed
up if ADR-007 changes on acceptance. This document continues the decision numbering at **D-71** (per
ADR-007's tail, "a future ADR continues from D-71"). **ADR-006 remains reserved** for the
long-deferred consolidating ADR; this slice is **ADR-008**. Where this document and an earlier one
disagree, neither does: it only adds a *fourth* way the one audience ADR-003 governs can be resolved.

## Context

MDVault #540 D3 / #541 Phase 3 wire a `PreRegistrationSession` to an `Event`. The approved schema is
a single additive migration: **`PreRegistrationSession.EventId` nullable FK → `Events`** (EF's FK
convention also adds `IX_PreRegistrationSessions_EventId`; both are inside the one approved change).

JJ has pre-decided the behaviour and it is **not re-opened here**:

- **A linked session REPLACES the audience union.** Linked session ⇒ the event's expected set is the
  pre-registered attendees. No linked session ⇒ expected = the resolved section+definition union
  (today's ADR-003/ADR-007 behaviour).
- **Pre-registered STUDENTS → the student denominator. Pre-registered PERSONNEL → an advisory count**,
  not in the denominator, not in `EventGroups`, not in attendance, and **not silently dropped** —
  mirroring ADR-007 D-70.

What remains is the "how", and it is the exact class of question ADR-003 exists to answer: getting it
wrong corrupts the denominator **silently**, in the D-13 shape. Five facts about today's code make the
"how" load-bearing:

1. **`ExpectedStudentIds` is the single denominator query.** Every summary, roster, manifest and live
   read goes through it; `IEventService` forbids a second copy. It answers one way while live (resolve)
   and another way once terminal (read the snapshot) — ADR-003 D-13.
2. **The denominator, `EventGroups`, and `AttendanceRecord` are all student-only.** There is no
   `PersonnelId` in any of them (ADR-007 context, facts 2–3). A `PreRegistration`, by contrast, is a
   `StudentId` XOR `PersonnelId` row — so a linked session resolves to people the denominator cannot
   represent, exactly the problem D-70 solved for definitions.
3. **The freeze (D-13) writes the resolved audience down as `EventGroups.StudentId` rows** and the
   terminal read is those rows (D-15: snapshot and frozen read must resolve identically).
4. **The tap/capture path accepts every tap and flags the uninvited `isExpected = false`** (D-20). It
   does **not** consult the expected set to decide whether to record, and it is idempotent on
   `(DeviceId, DeviceTapId)` via `UX_Attendance_Device_DeviceTapId` (plan §8.2 offline-sync
   foundation).
5. **#540 D3 says "one session ↔ one event"; the Phase 3 FE brief says linked "session(s)".** The two
   source documents disagree on cardinality, and the disagreement has a schema cost (see D-73).

## Options Considered

Document shape is settled precedent (ADR-002 Option 4 / ADR-003 / ADR-007). The live choice was **how
a linked session relates to the existing audience** and **where the "how" lives in code**.

### Option A — Pre-registration is advisory only; the union stays the denominator
A linked session shows a pre-registered count beside the audience, but `Expected` keeps resolving the
section+definition union.
- **Pros**: zero change to `ExpectedStudentIds`; no freeze interaction.
- **Cons**: contradicts JJ's pre-decision (#540 §6.3 Q3, answered: *replace*). It also leaves the
  operator unable to express "only these pre-registered people are expected", which is the whole point
  of a capacity-bounded pre-registration session.
- **Rejected**: by the pre-decision.

### Option B (chosen) — A linked session REPLACES the union, as a read-side branch, frozen like the union
When any session is linked, `ExpectedStudentIds`' **live** branch returns the pre-registered **student**
ids (union across linked sessions, soft-delete excluded) **instead of** the section+definition union.
The section group rows and definition links are **kept** (non-destructive) but **not resolved** while a
session is linked — the same "kept-but-not-resolved" read rule D-13 uses for a terminal event's group
rows, applied to a live-but-superseded state. At the terminal freeze, the pre-registered students
(not the union) are snapshotted into `EventGroups.StudentId` rows with `includeDeleted: true` (D-15
symmetry), and the terminal read is unchanged — it reads those rows.
- **Pros**: the literal implementation of the pre-decision; no new schema beyond the approved FK; the
  frozen read is untouched; the replace lives in exactly two places and nowhere else; personnel
  disposition reuses D-70 verbatim.
- **Cons**: a *fourth* resolution mode for one audience, and its non-contradiction is again a **read
  rule, not a constraint** (the standing D-13 hazard). The live branch and the snapshot must apply the
  *same* "is a session linked?" test — a third instance of the D-15 write/read symmetry trap.
- **Chosen**.

### Option C — Intersect: expected = union ∩ pre-registered
- **Pros**: "pre-registration narrows the audience" has an intuitive reading.
- **Cons**: not what JJ decided (replace, not intersect); surprising (a pre-registered walk-in from
  outside the attached sections would be *excluded* from Expected yet present in the room); and it
  needs both sets resolved on every read. Strictly more complex for a semantics nobody asked for.
- **Rejected**.

The **personnel** sub-question (advisory vs first-class vs silent drop) is **not re-litigated** — it is
ADR-007 D-70's three options with the same answer (advisory, D-72 below). The **cardinality**
sub-question's options are weighed inside D-73.

---

## Decision

Five decisions, continuing from ADR-007.

### D-71: A linked pre-registration session replaces the section+definition union as the live expected set; the attached sections/definitions are kept but not resolved while a session is linked

`ExpectedStudentIds`' **live** branch (Draft/Open) gains a selector at its head:

```
if (any PreRegistrationSession has EventId == this event)   // "linked"
    expected (live) = pre-registered STUDENT ids, union across linked sessions,
                      excluding soft-deleted              // the REPLACE
else
    expected (live) = the existing three-source union      // sections ∪ individuals ∪ definitions
```

The pre-registered student set is read from existing columns only:

```
PreRegistrations
  .Where(p => p.Session.EventId == eventId && p.StudentId != null && !p.Student.IsDeleted)
  .Select(p => p.StudentId).Distinct()
```

**The attached section group rows and `EventAudienceDefinitions` links are KEPT, not deleted** — the
link is non-destructive, exactly as D-13 keeps group rows and D-69 keeps definition links. While a
session is linked they are simply **not resolved**: `ExpectedStudentIds` takes the pre-reg branch and
never touches them. Unlinking the last session restores the union branch, and the kept rows resolve
again — so linking is fully reversible with no data loss, which is the property the nullable FK buys.

**Non-contradiction is guaranteed the same way D-13 guarantees it:** `ExpectedStudentIds` is the single
place that decides, so nothing can resolve the sections while a session drives the denominator.
`GetAudienceAsync` still *lists* the attached sections and definitions (an operator must see what was
attached), but its `Expected` comes from `ExpectedStudentIds` and therefore reflects the pre-reg set.

**A read signal is required so the numbers explain themselves.** Without it, an operator sees 200
students of attached sections and an `Expected` of, say, 50, with nothing saying why — the precise
"lines that do not add up to the total above them" failure this project keeps removing. The event /
audience read gains a signal — recommended shape `ExpectedSource: "PreRegistration" | "Audience"`
(plus the linked session id(s) and the pre-registered counts) — so the FE can render "Expected is
driven by a linked pre-registration" beside the otherwise-ignored sections.

### D-72: Pre-registered personnel are an advisory count; pre-registered students are the denominator — mirroring D-70

A `PreRegistration` is a student XOR a personnel row. The denominator, the freeze and attendance are
student-keyed (fact 2). Therefore, verbatim with D-70:

- The linked session's **student** registrants enter `ExpectedStudentIds`, the freeze and the roster.
- The linked session's **personnel** registrants are **excluded** from the denominator, the freeze and
  attendance, and **surfaced** as an advisory personnel count on the event/audience read — distinct
  across linked sessions (a person pre-registered in two sessions counts once, a `UNION`, as D-70's
  advisory total is):

```
PreRegistrations
  .Where(p => p.Session.EventId == eventId && p.PersonnelId != null && !p.Personnel.IsDeleted)
  .Select(p => p.PersonnelId).Distinct().Count()
```

**The D-70 dependency is inherited.** If a later change lets a personnel card produce an attendance row
without also putting personnel into the denominator, the rate measures numerator and denominator over
different populations — the >100% defect D-19 fixed for walk-ins. Personnel attendance and the
personnel denominator land together, in the future Option-C ADR, or not at all.

### D-73: An event may have many linked sessions, each session links to at most one event; expected = the union of the linked sessions' student registrants

The approved schema — a single nullable `EventId` on `PreRegistrationSession` — already fixes the hard
half: **a session links to at most one event** (one FK column). The only open half is whether an
*event* may have more than one session pointing at it.

- **Recommended — N:1 (many sessions per event).** This is the *natural* reading of the bare FK, needs
  **no schema beyond the approved migration**, and matches the FE brief's "session(s)". Expected is the
  **union** of the linked sessions' student registrants — well-defined and consistent with how sections
  and definitions already union (D-69). The 1:1 case is a subset, so nothing is lost by allowing many.
- **Alternative — strict 1:1 (at most one session per event).** Matches #540 D3's literal wording, but
  enforcing it needs a **second schema object**: `UNIQUE (EventId) WHERE EventId IS NOT NULL` on
  `PreRegistrationSession` (a filtered unique index, D-12's idempotency-by-constraint pattern). That is
  schema beyond the approved FK and therefore a **second migration** (see D-74's storage note and the
  STOP box below).

> **⚠ JJ confirmation #1 (cardinality).** #540 D3 ("one session ↔ one event") and the FE brief
> ("session(s)") disagree. JoseArch recommends **N:1 with a union of registrants** because it stays
> inside the approved schema and is strictly more general. Choosing strict 1:1 instead is a product
> call that **costs a second migration** (the filtered unique index). **Confirm N:1, or accept the
> extra index for 1:1, before Phase 3 implementation.**

### D-74: Storage is the approved FK and nothing more; the capture/expected logic needs no second migration

The replace (D-71), the personnel advisory (D-72) and the freeze (D-75) all run on **existing columns**
— `PreRegistration.StudentId` / `PersonnelId`, `Student.IsDeleted`, `Personnel.IsDeleted`, and the
approved `PreRegistrationSession.EventId`. **No second migration is required** for any of the behaviour
in this ADR, under the recommended D-73 (N:1).

The **only** circumstance that introduces a second migration is choosing strict 1:1 in D-73 (the
`UNIQUE (EventId)` filtered index). That index is additive and reversible (`CREATE INDEX`, no
`DROP`/`ALTER`), but it is **not** covered by the one approved migration and must not be scaffolded
until JJ approves it as a distinct migration.

### D-75: The replace is a denominator/read-side change only — the capture path and `deviceTapId` idempotency are untouched; a non-pre-registered tap on a linked event is an unexpected attendance, never a rejection

This is the guardrail that keeps "capture expects only pre-registered when a session is linked" (#540
D3's phrasing) from being mis-implemented as a capture-time gate.

- "Expects only pre-registered" means the **denominator** is the pre-registered set (D-71). It does
  **not** mean the tap path rejects a tap from someone who is not pre-registered.
- A student who taps on a linked event without being pre-registered is recorded exactly as a walk-in is
  today: an attendance row flagged **`isExpected = false`** (D-20). The denominator still counts only
  the pre-registered, so the uninvited tap cannot inflate the rate — the D-20 reasoning, unchanged.
- The tap write path keys on `(DeviceId, DeviceTapId)` and resolves duplicates through
  `UX_Attendance_Device_DeviceTapId`. **Nothing in this ADR touches it.** Linking a session changes
  `ExpectedStudentIds` and `SnapshotAudienceAsync` and nothing else.

**Why this must be stated loudly.** Turning the pre-reg expectation into a capture-time block would (a)
contradict D-20 (accept-and-flag), (b) discard physical-presence evidence to protect a number that no
longer depends on it, and (c) put a conditional in front of the offline-sync replay path — a tap queued
offline and flushed later would be accepted or rejected depending on the session's link state at flush
time, which is exactly the kind of replay-sensitive capture behaviour §8.2's idempotency design exists
to avoid. The expected set is a read; capture stays a write; they do not meet.

### The freeze interaction (folded into D-71/D-75, stated in full here)

When a **linked** event reaches a terminal status, `SnapshotAudienceAsync` must snapshot the
**pre-registered students**, not the union:

- It applies the **same "is a session linked?" selector** D-71 defines for the live branch. If a
  session is linked, the set resolved and written down as `EventGroups.StudentId` rows is the
  pre-registered students; if not, it is the existing three-source union. **The live branch and the
  snapshot must use one selector** — this is the D-15 trap for a third time (the asymmetry is between
  *live and frozen*, never between *the snapshot and the frozen read*).
- It resolves the pre-registered students with **`includeDeleted: true`**, matching the frozen read,
  because a student registered then soft-deleted must not retroactively shrink a past event's
  denominator (D-15). The live pre-reg branch excludes soft-deleted; the snapshot includes them. This
  inherits D-15's accepted, bounded discontinuity (a student registered then soft-deleted *before* the
  close steps the denominator by one at close) — no new discontinuity is introduced.
- **The terminal read is unchanged.** `ExpectedStudentIds`' frozen branch still reads
  `AttachedStudentIds(includeDeleted: true)` — the written-down rows. It does not know or care that a
  session was linked. The pre-reg link rows, the group rows and the definition links are all **kept as
  historical records that no query resolves on a terminal event** — D-13's read rule, now extended to a
  *third* kind of kept-but-not-resolved link.

> **⚠ JJ confirmation #2 (link lifecycle on a terminal event).** JoseArch recommends the link/unlink
> endpoint **refuse** on a terminal event (409, mirroring `AcceptsEdits` / D-14): once the audience is
> frozen, a new link would be a kept-but-unresolved row that changes no number, which reads as a silent
> no-op. This is a link-endpoint rule, not a denominator rule, but it belongs to JJ's product call on
> how linking behaves after close. Confirm "refuse after terminal", or specify the intended behaviour.

---

## Consequences

### Positive
- The pre-decision is implemented literally, with the denominator kept student-only and
  correct-by-construction. No new denominator query exists, so none can drift from the one
  `IEventService` protects.
- The replace lives in exactly **two** places (the live `ExpectedStudentIds` branch and
  `SnapshotAudienceAsync`) and the frozen read is untouched — the smallest possible surface for a
  change to the system's most load-bearing query.
- No schema beyond the approved FK, under the recommended cardinality. The link is non-destructive and
  reversible: unlinking restores the union with no data loss.
- The personnel disposition and the capture/idempotency guardrail are explicit and surfaced, not
  silent — the failure shape this register exists to catch.

### Negative
- **A fourth resolution mode for one audience**, whose non-contradiction is a read rule, not a
  constraint (the standing D-13 hazard, now one notch larger). The D-15 symmetry must hold across a
  *third* source — the "is a session linked?" selector must be one method, used by both the live branch
  and the snapshot, with the control test asserting it for the pre-reg source specifically.
- **A linked session with no student registrants makes `Expected = 0`**, ignoring attached sections
  that resolve hundreds of students. This is correct under REPLACE and consistent with D-19
  ("`Expected = 0` means no audience is attached"), but it is operationally surprising — hence the D-71
  read signal is not optional.
- **Personnel pre-registered into a linked session are invisible in every count** beyond the advisory
  figure — the same accepted limitation as D-70, and the same trigger for the future Option-C ADR.
- If JJ chooses strict 1:1 (D-73), a second migration (the filtered unique index) is required and is
  not yet approved.

### Neutral
- Decision numbering stays continuous: ADR-001 D-1…D-6; ADR-002 D-7…D-11; ADR-003 D-12…D-21;
  D-22…D-53 across Phase 3b–5 docs/MDVault; ADR-004 D-54 and parts; ADR-005 D-55…D-68; ADR-007
  D-69…D-70; this ADR **D-71…D-75**. A future ADR continues from **D-76**. **ADR-006 remains reserved**
  for the consolidating ADR.
- `GetAudienceAsync`'s read contract extends with the `ExpectedSource` signal and the pre-registered
  student/personnel counts; the attached sections/definitions still come back (listed, not resolved,
  while linked).
- This ADR's Phase 3 **depends on ADR-007 (Phase 2)** landing — the definition arm of the union is the
  "else" branch D-71 falls back to when no session is linked.

## Follow-Up Actions

Ordered by consequence, not effort. **None of these is authorised by this document** — it is a
proposal; JJ decides.

- [ ] **Resolve ⚠ confirmation #1 (cardinality, D-73).** N:1 (recommended, no extra schema) vs strict
      1:1 (a second migration). This gates Phase 3 — the FE affordance and the storage both turn on it.
- [ ] **Resolve ⚠ confirmation #2 (link lifecycle on a terminal event, D-74/freeze).** Recommended:
      refuse link/unlink after terminal (409).
- [ ] **Extract one "is a session linked?" selector** used by both the live `ExpectedStudentIds` branch
      and `SnapshotAudienceAsync` (D-15 symmetry over the pre-reg source). Do not inline the predicate
      twice.
- [ ] **Named tests per MDVault #541 Phase 3 ACs**, plus three this ADR adds: (a) a *liveness/replace
      control* — attach sections resolving N students, link a session with M student registrants, assert
      live `Expected` becomes M (not N, not N∪M) and reverts to N on unlink; (b) a *freeze symmetry
      control* — register a student, soft-delete them before close, close, assert the frozen count
      matches the snapshot exactly (D-15 over pre-reg); (c) a *capture guardrail* — a non-pre-registered
      tap on a linked event records `isExpected = false` and does not change `Expected`, and a replayed
      `deviceTapId` stays idempotent. Integration on SQL Server via Testcontainers, never EF InMemory.
- [ ] **Add the `ExpectedSource` read signal** (+ linked session id(s) and pre-registered
      student/personnel counts) to the event/audience read, so the FE can explain why attached sections
      are not counted.
- [ ] **Validate the link reference** in the link/unlink endpoint: a session from another tenant → 400;
      a session already linked to a different event → the D-73 cardinality rule decides (409 under
      strict 1:1; allowed-and-moved under N:1 only if JJ wants re-link, otherwise refuse).
- [ ] **Decide whether the client needs personnel attendance** (inherited from D-70). If yes, it is the
      Option-C ADR (personnel denominator + capture path landing together); if no, record that the
      pre-reg personnel count stays advisory so a future reader does not "fix" the exclusion back.
- [ ] Carried forward, unchanged: ADR-007's open follow-ups and the still-unwritten consolidating ADR
      (ADR-006).
