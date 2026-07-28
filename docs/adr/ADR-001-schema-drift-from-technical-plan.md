# ADR-001: Schema Drift from the Technical Plan

**Status**: Accepted
**Date**: 2026-07-28
**Deciders**: JJ; JoseArch (Team X)

## Context

`Events-Attendance-Monitoring-System-Technical-Plan.md` closes with: *"the API spec (§6) and schema
(§4) are the source of truth for parallel frontend/mobile/backend work."* The repo's `CLAUDE.md`
restates this: match the plan's names and types rather than inventing new ones, and if you deviate,
say so explicitly.

Between the plan being written and the data layer being built, two things happened that the plan
could not have accounted for:

1. **The real USA/CICSS roster arrived.** Its grain is *student × course × section × teacher*, not
   *student*. Twelve of the fifty-two students in the sample sit in more than one section.
2. **REGNO was confirmed to be both the student ID and the RFID card UID** — one per student,
   guaranteed unique by the registrar.

Those two facts break specific §4 tables rather than the plan as a whole. Six deviations follow.
Recording them in one ADR keeps the plan usable as the contract: read §4 together with this
document, and where they disagree, this document wins.

This ADR records **what changed and why**. It does not authorize the implementation — each
deviation lands in its own approved phase.

## Options Considered

### Option 1: Build to §4 literally, absorb the mismatch in application code
Keep the plan's schema untouched; represent multi-section students by duplicating student rows or
by packing multiple values into `Course`/`Section` strings.
- **Pros**: Zero drift from the contract. Frontend and mobile keep coding against §4 unchanged.
- **Cons**: Duplicate student rows break the `UNIQUE(SchoolId, StudentNumber)` index and the
  UID→student hot path (§4.4) — one card UID would resolve to several students, which is
  unrecoverable at tap time. Delimited strings are unqueryable and unjoinable.
- **Effort**: Low now, unbounded later.

### Option 2: Rewrite the Technical Plan document in place
Edit §4 so the plan and the build agree again.
- **Pros**: Single source of truth, no companion document to read alongside it.
- **Cons**: The plan is the client-facing contract that the proposal, timeline, and pricing were
  built on; silently rewriting it destroys the audit trail of what was agreed versus what was
  discovered. Also loses the *reasoning*, which is the part future teams need.
- **Effort**: Medium.

### Option 3 (chosen): Implement the deviations, record them in an ADR, leave the plan intact
Build the corrected schema; keep §4 as the historical contract; this ADR is the errata.
- **Pros**: Preserves both the agreed contract and the reasons it changed. Reviewable as a single
  short document. Frontend/mobile get one place to check for divergence.
- **Cons**: Two documents must be read together. Requires discipline to keep this ADR current.
- **Effort**: Low.

## Decision

We will implement the following six deviations from the Technical Plan, and treat this ADR as
authoritative wherever it contradicts §4.

---

### D-1: Add the academic-structure tables (twelve tables absent from the §4.1 ERD)

**Context.** The §4.1 ERD models students as leaves hanging off `Schools`, with academic placement
flattened into three nullable string columns on `Students` (§4.3: `Course`, `YearLevel`, `Section`).
The real roster's grain is *student × course × section × teacher*. Twelve of fifty-two students in
the sample appear in more than one section. A single-valued `Course`/`YearLevel`/`Section` triple
cannot represent a student enrolled in two sections without either duplicating the student row
(which breaks `UNIQUE(SchoolId, StudentNumber)` and the UID→student lookup) or packing delimited
values into a string column (which is unqueryable).

**Decision.** Introduce a normalized academic structure alongside the §4 core. Confirmed tables:
`Terms`, `Colleges`, `Programs`, `Courses`, `Instructors`, `CourseOfferings`,
`CourseOfferingInstructors`, `Enrollments`, `StudentTermRecords`. Total planned footprint is twelve
tables; the remaining table names are pinned during the schema phase, not by this ADR.
`Enrollments` carries the student × offering grain; `CourseOfferings` carries course × section ×
term; `CourseOfferingInstructors` carries the teacher assignment (kept separate so team-taught
sections do not force a nullable-or-duplicated instructor column on the offering).

**Consequences.**
- *Positive*: Multi-section students become representable and queryable. Course/section/instructor
  attendance analytics (§12 "Group/Course Analytics") become a join rather than a string parse.
  `EventGroups`/`StudentGroups` (§4.7, §4.8) can later target an offering or program directly.
- *Negative*: Twelve tables is a large addition to a schema the plan sized at roughly fifteen. It
  materially widens the SIS import surface (see D-4) and the seed/test fixture surface. Migration
  and query complexity rise; every roster-facing query gains at least one join.
- *Neutral*: The §4.1 ERD in the plan is now incomplete. Readers must treat this ADR as its errata.

---

### D-2: Demote `Students.Course` / `YearLevel` / `Section` to a derived read-only display cache

**Context.** With D-1 in place, `Enrollments` and `StudentTermRecords` become the authoritative
answer to "what is this student taking?". The three §4.3 string columns would then be a second,
divergent source of truth. They cannot simply be removed: `web-admin` binds them today
(`StudentDto` exposes `course`, `yearLevel`, `section`; the students grid and its course filter read
them), and the global no-DROP rule forbids dropping populated columns without a data-preserving
migration.

**Decision.** Keep the columns. Reclassify them from authoritative to a **derived, read-only display
cache** populated from the academic tables — a denormalization for list rendering and search, never
a write target and never a join key. Writes flow to `Enrollments`/`StudentTermRecords`; the cache is
refreshed from them.

**Consequences.**
- *Positive*: No DROP, no migration risk, no frontend breakage — the SPA keeps working unchanged.
  List/grid queries stay single-table and fast.
- *Negative*: A genuine denormalization that can go stale, and staleness is invisible to the UI. For
  a multi-section student the cached triple is lossy by construction — it can only show one of them,
  so a display rule ("primary enrollment" or similar) must be defined rather than left implicit.
- *Neutral*: The refresh trigger (import-time, on enrollment write, or scheduled) is a Phase 0b
  decision. Until the academic tables are populated these columns continue to behave exactly as
  today, so the change is inert on arrival.

---

### D-3: `RfidCards` uniqueness becomes `UNIQUE(SchoolId, CardUid) WHERE IsActive = 1`

**Context.** §4.4 declares `CardUid` as `NOT NULL, UNIQUE` — globally unique across the table. The
same section's prose says something different: *"A student may have multiple cards over time; only
one active per `CardUid`."* The prose describes a filtered constraint; the column definition
declares an unfiltered one. They cannot both hold.

The roster resolves which one is correct. REGNO **is** the card UID. When a student loses their ID,
the registrar issues a replacement carrying the *same REGNO*. Under a global `UNIQUE(CardUid)`,
inserting the replacement card violates the constraint while the original row still exists — so a
reissue can only be recorded by first erasing or mutating the original card row. That destroys the
issuance history and, with it, the ability to answer which physical card was presented at a past
tap (`AttendanceRecords.RfidCardId`, §4.9, points at that row).

**This is a spec bug, not a preference.** The global constraint makes a routine registrar operation
impossible to record correctly.

**Decision.** Replace the global constraint with a **filtered unique index**:
`UNIQUE(SchoolId, CardUid) WHERE IsActive = 1`. Reissue becomes: deactivate the old card
(`IsActive = 0`, set `DeactivatedAt`), insert the new one. Both rows survive. `SchoolId` is included
so the constraint is tenant-scoped, consistent with §11's multi-tenant guard and with
`UNIQUE(SchoolId, StudentNumber)` on `Students` (§4.3).

**Consequences.**
- *Positive*: Card reissue is recordable without data loss. Historical taps keep resolving to the
  physical card that produced them. Uniqueness now matches §4.4's own prose. The UID→student hot
  path is unaffected — it already filters on `IsActive` (`RfidCards.CardUid == uid && c.IsActive`).
- *Negative*: `RfidCards` gains a denormalized `SchoolId` (it currently reaches school only through
  `Students`), which must be kept consistent with the owning student's. Any lookup that forgets
  `IsActive` can now return multiple rows for one UID — every UID query must filter on it.
  Filtered-index support is provider-specific; this constrains the DB provider choice.
- *Neutral*: Current EF configuration is
  `b.Entity<RfidCard>().HasIndex(c => c.CardUid).IsUnique()`. Changing it is a schema migration, and
  the seed data (one card per student, all active) satisfies the new constraint unchanged.

---

### D-4: SIS import column mapping moves from `SystemSettings` K/V to a versioned mapping table

**Context.** §10.2 stores the source→target column mapping "per school in `SystemSettings`", whose
shape is `Key`/`Value`/`DataType` (§4.13) — a flat string key/value store. The actual mapping is a
17-column map that fans out across multiple entities (student identity, program, course, section,
term, instructor — see D-1) and carries normalization rules per column (trimming, case folding,
REGNO canonicalization, course-code matching). That is a structured document, not a string.

Separately, a stored mapping is not enough on its own: when an old import batch is questioned months
later, the mapping in `SystemSettings` reflects whatever it was last edited to, not what actually
ran. There is no way to prove which rules produced a given batch.

**Decision.** Store the mapping in a dedicated **versioned** table rather than in `SystemSettings`.
Each edit creates a new version; `SisImportBatches` records the mapping version it executed under.

**Consequences.**
- *Positive*: The mapping becomes a real schema with real constraints instead of a stringly-typed
  blob. Historical batches are explainable and reproducible — a prerequisite for §10.4's
  "idempotent and re-runnable" claim to mean anything. Editing a mapping can no longer silently
  change the interpretation of past imports.
- *Negative*: New tables and a version-resolution path in the import pipeline. The mapping UI (§10.2)
  gains version awareness — listing, diffing, and choosing a version is more UI than a settings form.
  Versions accumulate and need a retention answer eventually.
- *Neutral*: `SystemSettings` remains for what it is good at (reader defaults, grace minutes, report
  branding, retention policy, per §4.13). This narrows its scope; it does not replace it.

---

### D-5: `SisImportBatches` gains a required `TermId`; row results gain `SkippedRows` / warning columns

**Context.** Two gaps in §4.12.

*Term.* Every academic row imported under D-1 belongs to a term, but the source file has no term
column — the term is context the operator holds, not data the file carries. Without it, imported
enrollments cannot be placed in time and re-importing the same roster for a new term would collide
with the previous one.

*Counters.* `SisImportRows.Result` is defined as `Inserted/Updated/Failed/**Skipped**`, but
`SisImportBatches` has counters for only `TotalRows`, `InsertedRows`, `UpdatedRows`, `FailedRows`.
`Skipped` is a defined outcome with nowhere to be counted, so
`Inserted + Updated + Failed ≠ TotalRows` whenever any row is skipped. The batch summary cannot be
reconciled against the row detail — the exact check an operator runs to decide whether an import
succeeded.

**Decision.** Add a **required `TermId`** to `SisImportBatches`, declared by the operator at upload
time (the UI prompts for it; it is not inferred from the file). Add `SkippedRows` to the batch
counters, plus warning columns so a row can succeed while still reporting a non-fatal anomaly.

**Consequences.**
- *Positive*: Batch counts balance to `TotalRows` and can be asserted in tests. Imports are
  term-scoped, so the same roster file can be imported for successive terms without collision.
  Warnings surface data-quality problems (unmatched course code, malformed REGNO) that today would
  either fail a row outright or pass silently.
- *Negative*: One more required input before an import can start, and it is operator-declared — a
  wrong selection misfiles an entire batch under the wrong term. That is a recoverable error but
  only if batch-level rollback exists, which makes rollback a harder requirement than the plan
  assumed.
- *Neutral*: Warning semantics need pinning (does a warning-only batch report `Completed` or a new
  `CompletedWithWarnings`?). §4.12's `Status` enum may need a value — under the global rule, adding
  one is additive and safe; renaming existing ones is not.

---

### D-6: Auth/RBAC sequenced after the data layer, not at build-order position #2

**Context.** §15's dependency-driven build order puts "Auth + RBAC (unblocks everything)" at
position #2, immediately after scaffolding and before Students/Events/Attendance. D-1 through D-5
mean the data layer is substantially larger and less settled than the plan assumed. Building
permission enforcement over a schema still in motion means reworking policies and multi-tenant
filters each time the roster model shifts, and it front-loads the slowest-to-verify work onto the
least stable foundation.

**Decision.** Sequence Auth/RBAC **after** the data layer stabilizes. To keep this a deferral rather
than a hole, Phase 0c installs two seams up front:
- the **`SchoolId` EF Core global query filter** (§11's multi-tenant guard), wired with a tenant
  provider that is a fixed single school today and reads from claims later; and
- a **no-op `[HasPermission]` attribute** matching §11's signature, applied to endpoints as they are
  written.

**Consequences.**
- *Positive*: Endpoints are decorated and queries are tenant-filtered from day one, so enabling real
  auth is a change of two implementations rather than an audit of every controller and query. The
  filter is the piece that is genuinely expensive to retrofit — a missed filter is a cross-tenant
  data leak, and retrofitting means re-reviewing every query ever written. Installing it early
  captures most of §11's value at a fraction of the cost.
- *Negative*: **The API stays open for longer than the plan intended.** A no-op `[HasPermission]`
  looks exactly like a real one at a glance, and that is a real hazard: reviewers can read a
  decorated endpoint as protected when it is not. The attribute must be unmistakably marked as
  inert, and the system must not be exposed beyond local/dev use until the real implementation lands.
- *Neutral*: `CLAUDE.md` already records auth as deliberately stubbed ("Don't 'fix' this
  incidentally"). This ADR sets the condition for un-stubbing it: data layer stable, then §11 in
  full.

---

## Accepted Context (not drift)

Recorded here because both facts shape the schema and are easy to misread as errors later. Neither
contradicts the plan.

- **REGNO is simultaneously the student ID and the RFID card UID.** One per student, guaranteed
  unique by the registrar. The plan models `Students.StudentNumber` and `RfidCards.CardUid` as
  independent fields (§4.3, §4.4), and that separation is **kept deliberately** — they are equal in
  value today but not the same concept, and collapsing them would make the reissue case of D-3
  unrepresentable and would couple the identity model to the card technology. Expect the two columns
  to hold identical values; do not "clean this up" by merging them.
- **`Students.SisExternalId` is deliberately left NULL.** §4.3 defines it as the SIS primary key for
  sync, and §10.3 makes it the preferred upsert key. There is no Mastersoft PK available yet, so the
  column is reserved for it and the upsert falls back to §10.3's documented alternative
  (`StudentNumber` within school). A NULL `SisExternalId` is the expected state, not missing data.
  Note that the current mock seed populates it with synthetic `SIS-{number}` values; that is seed
  convenience and must not be read as a real external identifier.

## Consequences (overall)

### Positive
- The schema can represent the roster that actually exists, including multi-section students and
  card reissues — two cases the plan's schema makes impossible rather than merely awkward.
- Import batches become auditable and reconcilable, which is what makes the pipeline trustworthy.
- The plan survives as the agreed contract; divergence is in one reviewable place.

### Negative
- Roughly a dozen extra tables and a versioned mapping subsystem. This is a real increase in scope
  and in the surface that must be migrated, seeded, and tested — the largest cost in this ADR.
- §4 can no longer be read alone. Anyone working from the plan must read this ADR with it, and
  nothing enforces that.
- Auth arrives later than planned, with a deliberately inert attribute in the codebase in the
  meantime (D-6).

### Neutral
- `docs/adr/` is established as the ADR home for this repo, numbered sequentially and immutable once
  accepted — superseding decisions get a new ADR rather than edits here.
- Frontend and mobile contracts (§6) are **unchanged** by this ADR. D-2 is specifically designed to
  keep `StudentDto` stable.

## Follow-Up Actions

- [ ] Pin the three unnamed tables of D-1's twelve during the schema phase; amend by superseding ADR
      if the count changes materially.
- [ ] Define the D-2 display-cache refresh trigger and the "which enrollment shows" rule for
      multi-section students.
- [ ] Confirm the target DB provider supports filtered unique indexes as D-3 requires, before the
      provider decision is locked.
- [ ] Decide the D-5 warning-vs-status semantics (new `Status` value, or warnings orthogonal to
      status) — additive only.
- [ ] Make the D-6 no-op `[HasPermission]` unmistakably inert (naming, XML doc, and a test asserting
      it denies nothing) so it cannot be mistaken for enforcement at review time.
- [ ] Close the `Api → Infrastructure` layering violation left in place by the Phase 0a restructure
      (controllers inject `EamsDbContext` directly); tracked separately from this ADR.
