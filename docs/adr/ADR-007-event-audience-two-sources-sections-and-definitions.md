# ADR-007: An Event's Audience Has Two Sources — Attached Sections and Attached Audience Definitions

**Status**: Proposed (2026-10-04) — awaiting JJ's read-through, then flip to `Accepted`
> The *decision* below is JJ's product direction (MDVault #540 D1, locked in brainstorming
> 2026-10-04); this *document* has not yet been read by him, and the register's rule is "do not
> self-accept an ADR listing someone else as a decider." JoseArch recommends; JJ accepts.
**Date**: 2026-10-04
**Deciders**: JJ; JoseArch (Team X)
**Relationship to ADR-001 … ADR-005**: **Amends ADR-003 by reference.** ADR-003 is `Accepted` and
immutable; it is **not** edited, and D-12 through D-21 stand exactly as written. This document continues
the decision numbering at **D-69** and records two decisions (D-69, D-70) that wire a reusable
`AudienceDefinition` into an event's expected audience — the "additive follow-on" the
`AudienceDefinition` domain doc anticipated. Where this document and ADR-003 disagree, neither does: this
one only *adds a second contributing source* to the audience ADR-003 already governs.

> **⚠ Two numbering corrections, stated plainly because a reader will otherwise trust the brief that
> produced this file.** (1) The originating brief said "continue at D-12 / update ADR-003." D-12 is an
> ADR-003 decision and ADR-003 is immutable — that instruction is impossible and is the reason this is a
> *new* ADR. (2) A first correction said "ADR-006 at D-22." D-22 is long gone: D-22–D-53 live in the
> Phase 3b–5 docs and MDVault, ADR-004 owns D-54 and its parts, ADR-005 owns D-55–D-68, and ADR-005's
> own tail says *"A future ADR continues from D-69."* The correct next decision number is **D-69**, and
> this document uses it.

> **⚠ Filename collision with the consolidating ADR — resolved; JJ's call, now made.** ADR-005
> §(top) records JJ's standing call that **ADR-006 is reserved for the long-deferred consolidating ADR**
> that supersedes 001–005. This feature slice was first drafted as ADR-006 by the dispatching brief,
> which would have been the *fourth* deferral of that consolidation — the exact hazard `README.md`
> describes. JJ renumbered this slice to **ADR-007**, so the **006 slot stays reserved for the
> consolidating ADR** and is no longer claimed here. Status is `Proposed` and nothing is built, so the
> renumber was a one-line edit, not a later migration.

## Context

MDVault #540 D1 (locked) wires two things that exist but are not yet connected:

- An `Event` gains a nullable `EventClassificationId` FK (ray, in flight — **the one migration approved
  so far**).
- Reusable `AudienceDefinition` masters — which already exist, with CRUD and live resolution — attach
  to an event **alongside** its sections, non-destructively. `AttachAudienceAsync`'s request gains
  `AudienceDefinitionIds`, and the event's expected audience becomes the **deduped union of resolved
  sections + resolved definitions**.

#540 D1 states *that* the union happens. It is **silent on how** — and the "how" is precisely the class
of question ADR-003 exists to answer, because getting it wrong corrupts the denominator **silently**, in
the exact shape D-13 was written to prevent. Four facts about today's schema make the "how" load-bearing:

1. **The denominator is student-only.** `ExpectedStudentIds` is the single denominator query (every
   summary, roster, manifest and live read goes through it — see `IEventService`'s own docs on why no
   second copy is allowed to exist). It returns students.
2. **`EventGroups` is student-only.** §4.8 is `StudentGroupId` XOR `StudentId`. There is **no
   `PersonnelId` column**, and ADR-003 D-12's two filtered unique indexes are built on exactly those two
   columns.
3. **Attendance is student-only.** `AttendanceRecord` is keyed on `StudentId` (non-nullable); there is
   **no `PersonnelId`** and no capture path that resolves a card to a personnel attendance row. A
   personnel member physically present at an event cannot, today, become a row anyone counts.
4. **An `AudienceDefinition` resolves to a *mixed* person-set** — students **and** personnel
   (`ResolvedAudienceDto` carries both `StudentCount` and `PersonnelCount`; `AudienceCriteriaDto` carries
   both `StudentIds` and `PersonnelIds`; University-wide scope is `Students | Employees | Both`).

So a definition resolves to people the event's denominator, snapshot and attendance cannot represent.
The question is two questions wearing one coat: **(A) how is the link stored and resolved**, and **(B)
what happens to the personnel a definition resolves to.** They are answered as D-69 and D-70.

One in-repo precedent is worth naming before the options: `PreRegistrationSession`'s attendees already
model a person as nullable `StudentId` XOR nullable `PersonnelId`. The codebase therefore already has a
shape for "this row is a student or a personnel member" — which is the shape Option C would push into
the denominator, and the reason Option C is *possible* rather than merely imaginable.

## Options Considered

The document-shape question is settled precedent (ADR-002 Option 4 / ADR-003): a new ADR amending by
reference with continuous numbering. The live choice is storage + resolution + personnel disposition.

### Option A — Materialize into `EventGroups` at attach time; drop personnel

At attach, resolve each `AudienceDefinitionId` to its **student** members and write them as individual
`EventGroups.StudentId` rows. Store no link to the definition itself. Personnel resolved by the
definition are discarded.

- **Storage**: Reuse — no new table, no new column. The definition's student output becomes §4.8 rows.
- **Second migration beyond `Events.EventClassificationId`?** **No.**
- **Liveness**: **Snapshot at attach.** The definition is flattened the moment it is attached; later
  roster changes do not flow through until — nothing. Sections resolve live until the terminal freeze;
  definitions here would be frozen from attach, a different lifecycle for the same audience.
- **Personnel**: **Silently dropped.** No `PersonnelId` anywhere to receive them; the personnel half of
  the resolution is discarded at attach with nothing recording that it happened.
- **ADR-003 invariants**: D-12 — rows pass through `UX_EventGroups_Event_Student`, so a student in both
  a section and a definition dedups automatically (good). **D-13 — broken in spirit**: this is Option 2
  of ADR-003 (freeze the wrong thing, early) reappearing on a new source. The definition's contribution
  stops tracking live enrolment the instant it is attached, which is the pre-freeze behaviour D-13 exists
  to forbid for sections. D-15 — attach-time resolution uses live rules while the frozen read uses
  `includeDeleted: true`; the same write/read asymmetry D-15 was written to close would reopen at the
  attach/close boundary.
- **Why rejected**: contradicts #540 D1's word *"resolved"* (which, read against ADR-003, means live
  like sections), silently drops personnel, loses the record of *which* definition was invited, and
  re-introduces the D-13 failure mode on a new source. Cheapest, and wrong in the way this register
  keeps catching.

### Option B (chosen) — A link table `EventAudienceDefinitions`; resolve live; student half into the denominator; freeze like sections

Store the **link** (`EventId`, `AudienceDefinitionId`) in a new additive junction, parallel to how
`EventGroups` stores a `StudentGroupId` link. The audience's live branch unions three sources: group rows
→ current members, individually-attached students, and **attached definitions → their resolved student
ids**. At the terminal freeze, the definition-resolved students are flattened into `EventGroups.StudentId`
rows exactly as section-resolved students are (D-13), and the link rows are kept as the historical record
of which definition was invited.

- **Storage**: **New link table** `EventAudienceDefinitions (EventId, AudienceDefinitionId)` + an unfiltered
  unique index `UNIQUE (EventId, AudienceDefinitionId)` (both columns are NOT NULL — unlike the
  `EventGroups` XOR pair, no filter is needed), mirroring D-12's idempotency-by-constraint.
  Definitions are a link (by id), not pre-resolved rows — the same relationship §4.8 has to a
  `StudentGroup`.
- **Second migration beyond `Events.EventClassificationId`?** **YES — and this is the loud flag.** A
  second additive migration creates `EventAudienceDefinitions`. Only `Events.EventClassificationId` is
  approved so far; **this table needs separate JJ approval before any scaffold.** Additive and reversible
  (a `CREATE TABLE` + `CREATE INDEX`, no `DROP`/`ALTER`), safe on a populated database because nothing
  has ever written it.
- **Liveness**: **Live on every read**, identical to sections. Matches #540 D1's "resolved union" and,
  crucially, matches ADR-003's live-until-terminal model so a definition is **not a special case** with
  its own lifecycle.
- **Personnel**: the definition's **student half** enters `ExpectedStudentIds`; the **personnel half is
  excluded** from the student denominator and surfaced as an advisory `PersonnelCount` on the audience
  read (the DTO already carries it). Personnel never enter `EventGroups`, the freeze, or attendance —
  consistent with their having no representation there. This disposition is D-70.
- **ADR-003 invariants** — all preserved, by extension rather than by exception:
  - **D-12**: the live union already dedups (it is a SQL `UNION`), so a student reached by both a section
    and a definition counts once. At the freeze, the flattened rows pass through
    `UX_EventGroups_Event_Student`, which now dedups a **third** source against the other two — the index
    becomes load-bearing for one more contributor, and that is stated here so it is not dropped as a
    "redundant dedupe nicety" (D-12's own warning).
  - **D-13**: the invariant *"a terminal event's `EventGroups` student rows ARE the denominator"* holds
    unchanged. The freeze now resolves **three** sources into those rows; the `EventAudienceDefinitions`
    link rows join the group rows as a historical record of what was invited that **no query resolves on
    a terminal event** — the same read rule, extended to a second kind of link.
  - **D-15**: the definition's resolution **at the snapshot must use the same `includeDeleted: true`
    rule as the frozen read**, so the write and the read of the frozen set remain provably one query.
    D-15's generalisation ("the asymmetry is between live and frozen, never between the snapshot and the
    frozen read") now spans the definition source too.
- **Why chosen**: it is the literal implementation of #540 D1, it makes definitions behave exactly like
  sections (the single most important property for not re-opening D-13), it keeps the denominator
  student-only and correct-by-construction, and it makes the personnel exclusion an explicit, surfaced
  decision rather than a silent drop.

### Option C — Make personnel first-class in the denominator

Option B's link table **plus** a `PersonnelId` path into `EventGroups` (or a parallel personnel junction),
into the freeze snapshot, and into `AttendanceRecord`, with a capture path that resolves a personnel card
to a personnel attendance row. Personnel resolved by a definition become real expected attendees who can
tap, be marked `Absent`, and appear in the freeze.

- **Storage**: new link table **and** a `PersonnelId`-carrying denominator/snapshot/attendance, touching
  the frozen capture contract.
- **Second migration beyond `Events.EventClassificationId`?** **YES — several, large and cross-cutting**,
  including changes to `EventGroups`, `AttendanceRecord`, the D-12 indexes (a personnel-side filtered
  unique index), and the capture path. Its own ADR, its own phase.
- **Liveness**: live, as B.
- **Personnel**: **fully first-class** — no drop, no exclusion; `PersonnelCount` becomes a real
  denominator contributor.
- **ADR-003 invariants**: touches nearly all of them deeply — D-12 (new index), D-13 (snapshot must
  carry personnel), D-15 (symmetry over personnel), D-19/D-20 and the attendance uniqueness constraint
  (`UX_Attendance_Event_Student_Occurrence` is student-keyed). This re-opens the denominator and capture
  contract ADR-003 spent ten decisions stabilising.
- **Why rejected (for now)**: it is the only option that honours `AudienceDefinition`'s true semantics
  end-to-end, and it is the right destination **if** the client needs personnel attendance — but there is
  no such requirement in Task 2, personnel attendance **capture does not exist**, and #540 D1 is
  explicitly "non-destructive, additive." Doing C under a drift note would be the largest unapproved
  change in the project landing as a footnote. Recorded as the sanctioned future path, not taken here.

---

## Decision

Two decisions, numbered continuing from ADR-005.

### D-69: An event's expected audience is the deduped union of two sources — attached sections and attached audience definitions — and definitions are stored as a live link, frozen at terminal like sections

A reusable `AudienceDefinition` attaches to an event **alongside** its §4.8 sections, non-destructively:
`AttachAudienceAsync`'s request gains `AudienceDefinitionIds`, and the attach stores a **link**
(`EventId`, `AudienceDefinitionId`) in a new additive junction `EventAudienceDefinitions`, mirroring the
relationship `EventGroups` already has to a `StudentGroup`. The link carries an unfiltered unique index
`UNIQUE (EventId, AudienceDefinitionId)` (both columns are NOT NULL, so no filter is needed — unlike the
`EventGroups` XOR pair whose D-12 indexes must be filtered) so idempotency is a property of the schema,
not of the method remembering to check — exactly D-12's reasoning.

`ExpectedStudentIds`' **live** branch unions **three** sources: group rows resolved into current
membership, individually-attached students, and **attached definitions resolved to their student ids** —
all de-duplicated in SQL, all excluding the soft-deleted. The definition resolves **live on every read**,
identical to a section; it is not a special case with its own lifecycle.

At the transition to a terminal status, the freeze (D-13) resolves all three sources once and writes the
result down as individual `EventGroups.StudentId` rows. The `EventAudienceDefinitions` link rows are
**kept** as the historical record of which definition was invited — the §4.8-group analogue — and, like
the group rows on a terminal event, **no query resolves them**. The D-13 invariant is unchanged: a
terminal event's `EventGroups` student rows **are** its denominator.

**This requires a second additive migration (`EventAudienceDefinitions`) beyond
`Events.EventClassificationId`, which is NOT yet approved.** It is additive and reversible (`CREATE
TABLE` + `CREATE INDEX`, no `DROP`/`ALTER`, safe on populated data). It must not be scaffolded until JJ
approves it as a distinct migration.

**Why a link and not materialised-at-attach (Option A).** Materialising freezes the definition's
contribution at attach, which is ADR-003's Option 2 — freeze the wrong thing, early — reappearing on a
new source. A test that attaches, then re-enrols, then closes would catch it; a test that only attaches
and closes would not, which is exactly the D-13 blind spot. The link keeps definitions live until the one
freeze, so there is one lifecycle for the whole audience.

### D-70: Personnel resolved by an attached definition are excluded from the student denominator and surfaced as an advisory count — not silently dropped, and not (yet) first-class

A definition resolves to students **and** personnel. The denominator, the freeze snapshot and
`AttendanceRecord` are all student-keyed and carry no `PersonnelId`. Therefore:

- The definition's **student** half enters `ExpectedStudentIds`, the freeze, and the roster.
- The definition's **personnel** half is **excluded** from the student denominator and from the freeze,
  because there is nowhere for it to go that any count reads — and is **surfaced** as the advisory
  `PersonnelCount` the audience read already carries, so the exclusion is visible rather than silent.

**Why not silently drop (Option A) and why not make them first-class now (Option C).** A silent drop is
the failure shape this register exists to catch: an operator attaches a "Faculty Senate" definition,
sees it accepted, and never learns the 40 personnel it resolved are in no denominator. Making personnel
first-class is the correct end state **if the client needs personnel attendance** — but that is a
denominator-and-capture rewrite (new `PersonnelId` columns, a personnel-side D-12 index, a personnel
capture path, D-19/D-20 revisited), unapproved, unscoped, and with no feature asking for it today.
Surfacing the count is the honest middle: it records what the definition resolved to, counts what the
system can actually count, and leaves a visible seam for the future Option-C ADR instead of a silent one.

**The dependency, stated because it is the point of recording this.** If a later change lets a personnel
card produce an attendance row **without** also putting personnel into the denominator, the rate's
numerator and denominator would be measured over different populations — a >100% or nonsensical rate, the
same class of defect D-19 fixed for walk-ins. Personnel attendance and the personnel denominator must
land together, in the Option-C ADR, or not at all.

---

## Consequences

### Positive
- The audience is one concept with one lifecycle: sections and definitions both resolve live and both
  freeze at the terminal transition. A reader does not have to learn a second set of rules for
  definitions, and D-13's guarantee covers the new source by construction.
- The denominator stays student-only and correct-by-construction: the live `UNION` dedups the third
  source, and `UX_EventGroups_Event_Student` dedups it again at the freeze. No new denominator query
  exists, so none can drift from the one `IEventService` protects.
- The personnel disposition is explicit and visible (D-70), not a silent discard — and the future path to
  first-class personnel is named rather than foreclosed.
- The migration is additive and reversible, and the link table preserves the record of *which* definition
  was invited, the same artifact D-13 fought to keep for groups.

### Negative
- **A second migration is required and is not yet approved.** Only `Events.EventClassificationId` is
  approved; `EventAudienceDefinitions` needs its own JJ sign-off before scaffold. This is the gating
  item.
- **A third audience source means three places must agree on the soft-delete rule** at the freeze (D-15).
  The symmetry held between two sources by two call sites agreeing; it now needs three, and the control
  test must assert it for the definition source specifically.
- **Personnel attached via a definition are invisible in every count** beyond the advisory
  `PersonnelCount`. For a definition whose point *is* personnel (an employee-classification audience),
  the event's denominator will look empty or wrong to an operator who does not read the advisory figure.
  This is accepted for Task 2 and is the trigger for the Option-C ADR.
- `AttachAudienceAsync` now has a second kind of reference to validate — a definition id from another
  tenant or outside the event's classification is an `UnknownReference` (400) exactly as a cross-school
  group is, and must be covered.

### Neutral
- Decision numbering remains continuous: ADR-001 D-1…D-6; ADR-002 D-7…D-11; ADR-003 D-12…D-21;
  D-22…D-53 across the Phase 3b–5 docs/MDVault; ADR-004 D-54 and parts; ADR-005 D-55…D-68; this ADR
  **D-69…D-70**. A future ADR continues from **D-71**.
- `GetAudienceAsync`'s read contract extends: attached definitions come back as definitions (links),
  including on a terminal event where — like group rows — nothing resolves them. The individually-resolved
  students remain the frozen set, enumerated via the roster.
- #540 D1's phrase "resolved union" is now defined: *resolved* means live, per source, on every read,
  frozen once at the terminal transition — the ADR-003 semantics, extended.

## Follow-Up Actions

Ordered by consequence, not effort. **None of these is authorised by this document** — it is a proposal;
JJ decides.

- [ ] **Get JJ approval for the second migration** (`EventAudienceDefinitions` + unfiltered unique index).
      Hard gate: no scaffold until approved. Additive/reversible; scaffold from `EAMS.Infrastructure`
      alone (design-time factory) per project rule.
- [x] **ADR numbering decided** (see the box at the top): JJ renumbered this slice to **ADR-007**, so the
      **006 slot stays reserved for the still-unwritten consolidating ADR**.
- [ ] **Extend `ExpectedStudentIds`' live branch with the definition arm** (student-only,
      soft-delete-excluded, de-duped), and the D-13 freeze to flatten definitions into `EventGroups`
      student rows with `includeDeleted: true` (D-15 symmetry over the third source).
- [ ] **Named tests per MDVault #541 Phase 2 ACs**, plus two this ADR adds: (a) a *liveness control* —
      attach a definition, re-enrol a resolved student, then close, and assert the live number moved
      before the freeze and is fixed after (the D-13 re-import control, applied to definitions); (b) a
      *dedupe* test — a student reached by both a section and a definition counts once, live and frozen
      (the D-12 index). Integration on SQL Server via Testcontainers, never EF InMemory.
- [ ] **Validate the new reference kind** in `AttachAudienceAsync`: a definition id from another tenant or
      outside the event's classification → `UnknownReference` (400); cover it.
- [ ] **Decide whether the client needs personnel attendance** (D-70). If yes, it is the Option-C ADR
      (personnel denominator + capture path landing together), not a drift note. If no, record that
      `PersonnelCount` stays advisory so a future reader does not "fix" the exclusion back.
- [ ] Carried forward, unchanged by this ADR: ADR-003's open follow-ups (close load-test D-21, bulk
      post-close correction D-17, `IsFrozen` for cancelled events D-16, `RequireRegistration` D-20) and
      the still-unwritten **consolidating ADR**.
