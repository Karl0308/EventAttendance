# ADR-002: Phase 1–2 Schema and Import Decisions

**Status**: Accepted (JJ, 2026-07-28) — immutable from here; amend by superseding ADR only
**Date**: 2026-07-28
**Deciders**: JJ; JoseArch (Team X)
**Relationship to ADR-001**: **Amends** `ADR-001-schema-drift-from-technical-plan.md`. It does not
supersede it. ADR-001 remains Accepted and unedited; D-1 through D-6 still stand as written. This
document adds one new deviation (D-7), pins detail ADR-001 deliberately left open (D-8 through
D-11), and records which of ADR-001's follow-ups are now closed. Where the two disagree, this one
wins; where this one is silent, ADR-001 is unchanged.

## Context

ADR-001 was written before the data layer existed. It named six deviations from the Technical Plan
and closed with six open follow-ups — deliberately, because the answers depended on code that had
not been written yet. Phases 1 and 2 have now shipped. The answers exist. They exist **in the
code**, which is the problem this ADR solves.

JJReviewer flagged it at the Phase 2 gate:

> *"Answering an ADR follow-up in a code comment and leaving the checkbox open is how the ADR stops
> being the source of truth."*

That is the failure mode, precisely stated. An ADR's value is that a reader in six months can
recover the *reasoning* without reading the whole codebase or finding the person who remembers. A
follow-up answered in a comment next to the implementation is answered for whoever is already
reading that file — which is nobody who needs it. The checkbox stays open, so the register says the
question is live when it is not; and the next reader either re-litigates a settled decision or, more
expensively, implements a second answer to it somewhere else.

Two further things happened during Phases 1 and 2 that need recording:

1. **The full roster export arrived** (536 rows, one college — CICSS). It carries a second email
   column the Technical Plan §4.3 does not have. That is new drift, and new drift gets an ADR.
2. **Two natural keys were chosen under an unverified assumption**, with a widening path held in
   reserve. That reserve has an expiry date nobody has written down. Recording it is the single most
   consequential thing in this document (see D-11).

## Options Considered

### Option 1: Answer the follow-ups where they were implemented (code comments, PR threads)
Leave ADR-001's checkboxes as they are; the answers live next to the code that implements them.
- **Pros**: Zero documentation work. The answer is adjacent to the thing it explains.
- **Cons**: This is the flagged failure mode. The drift register stops being the register. Comments
  are invisible to anyone reading the plan or the ADRs, they are deleted by refactors, and they
  carry the *what* without the *why not* — the rejected alternatives vanish entirely.
- **Effort**: Zero now, compounding later.

### Option 2: Edit ADR-001 in place — tick the boxes, add the new decisions
- **Pros**: One document. No cross-referencing.
- **Cons**: ADR-001 is Accepted, and its own Neutral consequence establishes the rule for this repo:
  *"numbered sequentially and immutable once accepted — superseding decisions get a new ADR rather
  than edits here."* Editing it destroys the record of what was known **at the time the six
  deviations were approved**, which is the part that explains why they were shaped that way.
- **Effort**: Low.

### Option 3: Supersede ADR-001 with a rewritten, consolidated ADR-002
- **Pros**: A single current document. No two-document read.
- **Cons**: Most of ADR-001 is still true and still load-bearing. Superseding it forces a full re-read
  of a long document to locate small deltas, and it discards the chronology — a reader can no longer
  tell which decisions were made blind and which were made with the roster in hand. That distinction
  is exactly what tells you which decisions to trust.
- **Effort**: Medium.

### Option 4 (chosen): A new ADR that amends ADR-001 by reference
Continue ADR-001's decision numbering (D-7 onward), state explicitly which follow-ups each item
closes, and close the demonstrably-done ones by citation rather than by editing checkboxes.
- **Pros**: ADR-001 stays immutable and honest about what it knew. Follow-up closure is auditable —
  each closure names its evidence. Decision numbering is continuous, so `D-9` is unambiguous across
  both documents.
- **Cons**: Three documents now (§4, ADR-001, ADR-002) must be read together, and nothing enforces
  it. Each further amendment compounds that. At ADR-004 or so, a consolidation ADR becomes the right
  call — but not yet.
- **Effort**: Low.

---

## Decision

Five decisions, numbered continuing from ADR-001.

---

### D-7: Add `Students.AlternateEmail` — the roster carries two email columns, §4.3 has one

**Context.** §4.3 defines a single `Email nvarchar(256) NULL` on `Students`. The real export carries
two:

| Source column | Character | Observed in the 536-row export |
|---|---|---|
| `USA_EMAIL` | Institutional. Always `@usa.edu.ph`. Unique per student. | Present throughout |
| `EMAIL_ID` | Personal. Free-provider addresses. | 164 rows carry a gmail address |

These are not two spellings of one fact. The institutional address is issued by the registrar, is
one-to-one with the student, and is a usable lookup key. The personal address is self-declared,
optional, and **genuinely shareable** — siblings routinely give the same guardian's address, and the
export confirms it is not unique. Collapsing both into one column forces a choice between losing a
uniqueness guarantee and losing data.

**Decision.** Map institutional → `Students.Email`; add `Students.AlternateEmail nvarchar(256) NULL`
and map personal → there.

`Email` stays institutional because it is the column that can carry a uniqueness expectation and be
used as a lookup key. `AlternateEmail` is explicitly **not** unique, not a lookup key, and not
validated beyond format — it may be blank, shared between students, or stale.

**Alternatives rejected.**

- *Drop the personal address; import `USA_EMAIL` only.* Rejected. It is the only contact channel
  that survives the institutional account being deactivated at graduation or withdrawal — which is
  when you most need to reach a student. It is present on roughly a third of rows, so it is not
  noise. And the export is a point-in-time snapshot: data discarded at import is not recoverable
  without a fresh export from the registrar, on the registrar's schedule, not ours. The cost of
  keeping it is one nullable column.
- *Pack both into `Email` as a delimited string.* Rejected for the same reason ADR-001 rejected
  delimited `Course`/`Section` values: unqueryable, unjoinable, and it destroys any uniqueness
  constraint on the column.
- *Promote contacts to a `StudentContacts` child table.* Rejected **for now**, not on principle. It
  is the correct shape the moment a third channel appears (mobile number, guardian contact,
  guardian name), and the export already hints that it will. But today there are exactly two known
  addresses with different semantics, and two named columns model that more honestly than a
  type-tagged child table with two rows per student. This is recorded as the migration path, not as
  a rejection: when the third channel arrives, migrate rather than add `AlternateEmail2`.

**Consequences.**
- *Positive*: No data loss from the export. `Email` retains its lookup-key character. The two
  columns carry their difference in their names, so a future notification feature is forced to pick
  deliberately rather than inheriting whichever address happened to win at import.
- *Negative*: Two email columns invite "which one do I send to?" at every call site, and the answer
  differs by purpose (institutional for anything official or auditable; personal only as fallback or
  for post-graduation contact). Nothing in the schema enforces that distinction — it lives here.
  `AlternateEmail` is unconstrained by design, so it will accumulate duplicates and dead addresses.
- *Neutral*: Whether `AlternateEmail` surfaces on `StudentDto` (§6) is **not settled by this ADR**.
  It is an additive change to the API contract if it does; ADR-001's "§6 contracts unchanged" claim
  survives either way. Follow-up below.

---

### D-8: ADR-001 D-1's twelve tables — nine landed in Phase 1, three in Phase 2. None went missing

*Closes ADR-001 follow-up #1 ("pin the three unnamed tables of D-1's twelve").*

**Context.** ADR-001 D-1 committed to a "total planned footprint of twelve tables" but named only
nine, leaving the rest to "the schema phase". Phase 1 shipped those nine. A reader comparing the
Phase 1 migration against D-1 counts nine tables against a promise of twelve and reasonably concludes
three were dropped or forgotten. They were not: they are import-infrastructure tables, and they
landed in Phase 2 with the import pipeline they serve.

**Decision.** Pin the remaining three by name and record the split.

**Phase 1 — the academic structure (nine, as named in ADR-001 D-1):** `Terms`, `Colleges`,
`Programs`, `Courses`, `Instructors`, `CourseOfferings`, `CourseOfferingInstructors`, `Enrollments`,
`StudentTermRecords`.

**Phase 2 — the import infrastructure (three):**

- **`SisImportRowEntities`** — the row→entity fan-out. §4.12 gives `SisImportRows` a single nullable
  `StudentId`, which encodes the assumption that one source row affects at most one student row.
  Under D-1 that is false: one source row can create or touch up to ten rows across ten tables
  (student, term record, college, program, course, offering, instructor, offering-instructor,
  enrollment, card). A single nullable FK cannot express that, and without expressing it there is no
  per-row provenance — you cannot answer "what did row 214 actually do?", which is the question every
  import dispute reduces to. This table carries one entry per (row, entity touched, action taken).
- **`SisImportProfiles`** and **`SisImportProfileColumns`** — ADR-001 D-4's versioned mapping,
  realized as a profile header plus one row per mapped column with its normalization rules. Each
  edit creates a new version; `SisImportBatches` records the version it executed under, which is what
  makes an old batch explainable months later.

**Consequences.**
- *Positive*: D-1's count is verifiable rather than aspirational. `SisImportRowEntities` is also the
  mechanism that makes the recovery arguments in D-11 possible — without per-row provenance, a bad
  import is only diagnosable by re-deriving it from the raw JSON.
- *Negative*: `SisImportRowEntities` is the highest-cardinality table in the schema by a wide margin
  — up to ten rows per source row per batch, and batches are re-runnable. It will need a retention
  policy sooner than `SisImportRows` does. §14's Hangfire purge covers `SisImportRows`; it does not
  currently know about this table.
- *Neutral*: The twelve-table figure in D-1's *Negative* consequence ("twelve tables is a large
  addition to a schema the plan sized at roughly fifteen") remains accurate. Nothing about the cost
  estimate changed — only the delivery schedule.

---

### D-9: The D-2 display cache resolves to the **modal section**, ties broken by lowest key ordinally

*Closes the "which enrollment shows" half of ADR-001 follow-up #2. The refresh-trigger half remains
open — see follow-ups.*

**Context.** ADR-001 D-2 demoted `Students.Course` / `YearLevel` / `Section` to a derived read-only
display cache, and named the gap it created: *"for a multi-section student the cached triple is lossy
by construction — it can only show one of them, so a display rule ('primary enrollment' or similar)
must be defined rather than left implicit."* Twelve of the fifty-two students in ADR-001's sample are
multi-section, so this is a common case, not an edge.

The obvious rule — "use the primary enrollment" — is not available. The source carries no primary
flag, no credit weighting, and no ordering that means anything. There is nothing in the data that
says which section is the student's home.

**Decision.** The cached triple is the student's **home section**, defined as:

> the **modal** section across the student's enrollments for the term — the section that appears most
> often. Ties are broken by the **lowest section key, ordinally**.

The other two columns of the triple follow **different** rules, and it is worth being precise
because an earlier draft of this ADR claimed they were consistent with `Section` and they are not:

| Column | Rule | Why |
|---|---|---|
| `Section` | modal section, ties by lowest key ordinally | as above |
| `Course` | the **first-seen** `PROGRAM` value in worksheet order | a student's programme is near-constant across their rows, so a modal rule would cost a second grouping to change nothing |
| `YearLevel` | **never written** | the export has no year-level column. Deriving one from a section label (`BSCRIM 2-A` → 2) describes the *class*, not the student — a third-year retaking a first-year subject would be recorded as first-year. A guess stored as a fact is worse than a null |

So the triple is **not** assembled from one winning enrollment, and it can in principle disagree with
itself: a student whose own rows carry two different `PROGRAM` values gets `Course` from one row and
`Section` from another. The fixture already proves the file can carry two programmes under one section
key (`SectionSpansPrograms`), so this is not hypothetical — it is merely rare. "Near-constant" is
exactly the class of assumption **D-11** exists to distrust, and it is recorded here rather than
defended.

**Rationale.** Two properties matter here, and correctness is not the first of them:

1. **Determinism outranks accuracy for a display cache.** The value feeds a grid, a search, and a
   course filter. A rule that could return a different answer on two imports of identical input
   would make grid ordering and filter results flicker with no data change — and because the cache is
   invisible to the UI as a cache, that reads as a bug in the data. The tie-break exists solely to
   guarantee that identical input yields identical output. It has no semantic meaning and must never
   be presented as if it does.
2. **The mode is the best available proxy for "where this student mostly is."** Absent a primary
   flag, frequency across enrollments is the only signal in the data that correlates with it.

**Consequences.**
- *Positive*: The cache is a pure function of the enrollments — recomputable, assertable in tests, and
  identical across environments given identical data. The multi-section lossiness ADR-001 named is now
  a defined loss rather than an accident.
- *Negative*: **A student split evenly across sections gets an arbitrary-but-deterministic answer.**
  Two sections with one enrollment each, or three with two each, and the tie-break decides — producing
  a value that looks authoritative and means nothing. The UI shows it with the same confidence as a
  genuine single-section value, and nothing distinguishes the two. This is the known weakness of the
  rule and it is not mitigated in code today.
- *Negative*: The tie-break is a display convention that must not leak. Any report, export, or
  analytic that filters or groups by section must join `Enrollments`, never read the cache. ADR-001
  D-2 already says the cache is never a join key; D-9 is the reason that rule has teeth.
- *Neutral*: Nothing in `StudentDto` changes. The SPA continues to bind `course`/`yearLevel`/`section`
  exactly as before.

---

### D-10: Warnings are parallel columns, not a new `Result` value; batch `Status` gains two members

*Closes ADR-001 follow-up #4 ("decide the D-5 warning-vs-status semantics — additive only").*

**Context.** ADR-001 D-5 introduced warning columns so a row could import successfully while still
reporting a non-fatal anomaly, and left the semantics open: *"does a warning-only batch report
`Completed` or a new `CompletedWithWarnings`? §4.12's `Status` enum may need a value."* The tempting
simplification — add `Warned` to `SisImportRows.Result` — was not evaluated in ADR-001. It is wrong,
and the reason is a published contract.

**Decision.** Two changes, both additive.

*Batch level.* `SisImportBatches.Status` gains **`CompletedWithWarnings`** and
**`CompletedWithErrors`**, alongside §4.12's `Pending`/`Running`/`Completed`/`Failed`. `Completed`
now means *clean* — no warnings, no failed rows — which is a narrowing of its meaning but not a
rename, and it makes the status alone answer the question an operator actually asks ("do I need to
look at this batch?"). `Failed` is retained for a batch that did not finish;`CompletedWithErrors` is
a batch that finished with some rows failed.

*Row level.* `SisImportRows` gains **`WarningCode`** and **`WarningMessage`** as columns **parallel
to** `Result`. A warned row still imports and still reports `Result = Inserted` or `Updated`.
`WarningCode` is a stable machine token, not prose, so warnings can be aggregated, counted, and
asserted in tests; `WarningMessage` carries the human-readable detail.

**Why not extend the `Result` enum.** §4.12 fixes `Result ∈ {Inserted, Updated, Failed, Skipped}`
and §6.8 publishes a filter over it: `GET /sis/import/{batchId}/rows?result=Failed`. Adding `Warned`
as a fifth member breaks two things at once:

1. A row that inserted *and* warned would have to report `Warned` instead of `Inserted` — silently
   changing the meaning of an existing filter value. Any client asking for `result=Inserted` would
   stop seeing rows that were, in fact, inserted. That is a breaking change to a published contract
   dressed as an additive one.
2. It breaks the reconciliation D-5 exists to provide. ADR-001 D-5's whole argument is that
   `Inserted + Updated + Failed + Skipped` must equal `TotalRows` so an operator can reconcile the
   batch summary against the row detail. A fifth member that overlaps the first two makes the sum
   under-count, reintroducing the exact defect D-5 fixed.

Warnings are a second, orthogonal dimension: *what happened to the row* (`Result`) and *was anything
suspicious about it* (`WarningCode`). Two dimensions need two columns.

**Consequences.**
- *Positive*: §4.12's `Result` contract and §6.8's filter are untouched. Count reconciliation holds.
  A warned-but-imported row is visible as both — imported *and* flagged — which is what it is.
  Warning codes are aggregatable, so "this batch produced 40 unmatched-course-code warnings" is a
  query, not a log grep.
- *Negative*: A client wanting "everything needing attention" must now query two dimensions —
  `Result = Failed OR WarningCode IS NOT NULL`. §6.8's rows endpoint has no warning filter, so that
  is a client-side filter or an unfiltered fetch until one is added. Adding one is additive; it is
  not done, and it is a follow-up below.
- *Neutral*: Both changes are additive under the global no-rename rule. Per `CLAUDE.md`, `Status` is
  a `nvarchar` column carrying enum-ish strings, so adding members is not a schema change at all —
  which also means nothing at the database level prevents an unrecognized value being written. The
  status vocabulary is enforced in code or not at all.

---

### D-11: Two natural keys are hedges against an unverified assumption — **and the hedge expires**

**Context.** Phase 1 had to choose natural keys for `Courses` and `CourseOfferings` with only one
college's export in hand (CICSS). Both keys assume **institution-wide course-code uniqueness** — that
`IT101` means the same course in every college of the university. Nobody has verified this. It is
plausible and it is common; it is also exactly the kind of assumption that turns out to be false the
first time a second college's data arrives.

The keys as built:

| Table | Unique key | Hedge |
|---|---|---|
| `Courses` | `(SchoolId, CodeKey)` | nullable `CollegeId` column, reserved and unused |
| `CourseOfferings` | `(TermId, CourseId, SectionKey)` | none needed — see below |

**Decision.** Keep both keys. Reserve `Courses.CollegeId` as nullable against a future widening.
Make the importer's collision behaviour **asymmetric**:

- a **course-code collision across colleges is a hard failure** — the batch stops;
- a **section-label collision is a warning** — the batch continues.

**Why the asymmetry is correct, not an inconsistency.**

`CourseOfferings` has `CourseId` *inside* its key. Two colleges both labelling a section `"A"`
cannot collide unless they are also the same course — and a same-code course from a different college
is caught upstream by the hard failure. So under the fail-on-course rule, a section-label collision is
always *within one genuine course*: two offerings of the same course in the same term sharing a
display label. That is either the same offering seen twice or a real data-entry error, and in both
cases the raw rows and their `SisImportRowEntities` provenance make it separable after the fact. A
warning is proportionate.

`Courses` has **nothing** in its key to distinguish colleges. A collision there is not a labelling
clash — it is two different real-world courses being **merged into one row**, silently, at import.
Every offering, enrollment, and downstream attendance figure from both colleges then hangs off a
single `CourseId`, with nothing in the `Courses` row recording that it represents two things. That is
why it must fail rather than warn: the hard failure on `Courses` is precisely what makes the warning
on `CourseOfferings` safe. Remove the former and the latter becomes negligence.

#### The expiry — read this part

The widening path exists and is data-preserving:

1. backfill `Courses.CollegeId` from each course's offerings (college is derivable through the
   offering's enrollments and the students' programs);
2. set `CollegeId` NOT NULL once the backfill verifies complete;
3. drop the unique index on `(SchoolId, CodeKey)` and create it on `(SchoolId, CollegeId, CodeKey)`.

None of these steps loses data — an index swap is not data loss, and NOT NULL after a verified
backfill is safe — so the global no-DROP rule does not block the widening. A future engineer should
not read that rule as a reason to avoid it.

**But the path is data-preserving only while it is executed BEFORE colliding data is imported.**

After a silent merge, this stops being a widening and becomes a **split**: one `Courses` row must be
divided into N rows, and roughly half of the affected `CourseOfferings` must be **re-keyed** onto the
new `CourseId`s — a mutation of live foreign keys with `Enrollments`, `CourseOfferingInstructors`,
and any attendance analytics already resolved against them. The information needed to perform the
split correctly may no longer exist in the `Courses` table at all; it would have to be re-derived
from import provenance, which only holds for as long as `SisImportRowEntities` is retained.

The hard failure on course-code collision is the mechanism that keeps the expiry from passing
unnoticed. **It must not be downgraded to a warning for convenience when the second college's export
first fails to import.** That moment — an operator blocked by a failing batch, under time pressure,
with a one-line change available that makes the error go away — is exactly when this decision will be
reversed by someone who has not read this ADR. This is the single most consequential unresolved item
in the project.

**Consequences.**
- *Positive*: Today's schema is simple: no `CollegeId` in the key, no compound joins, no nullable
  qualifier to reason about in every course query. If institution-wide uniqueness holds, the hedge
  costs one unused nullable column and is retired for free.
- *Negative*: A live, dated assumption with an unrecoverable failure mode on the far side of it, and
  a reserved nullable column whose purpose is invisible outside this document. The hard failure will
  present to an operator as an unexplained blocked import; the error message must name this ADR, or
  the reasoning is lost at exactly the moment it matters.
- *Neutral*: The cheapest way to retire the hedge entirely is to **ask the registrar** whether course
  codes are unique across colleges. If yes, `CollegeId` can be dropped from the plan (not from the
  table — the no-DROP rule applies) and this decision closes. If no, the widening runs immediately,
  while it is still a widening. Either answer is better than holding the hedge indefinitely.

---

## ADR-001 Follow-Ups Closed by Demonstration

Recorded here rather than by ticking ADR-001's boxes, per Option 4. Each is closed against evidence
in the Phase 1–2 code, not against an intention.

- **ADR-001 follow-up #5 — make the D-6 `[HasPermission]` attribute unmistakably inert.** Closed. The
  attribute exists, is marked and documented as enforcing nothing, and is now **applied to all nine
  controller actions**. Blanket application matters more than it looks: a decorated-everywhere
  surface means enabling real auth is switching one implementation, whereas a partially-decorated one
  requires an audit to find the gaps — and gaps in an inert attribute are invisible until they are
  security holes.
  **This does not mean auth exists.** ADR-001 D-6's negative consequence is still fully live: the API
  is open, and the system must not be exposed beyond local/dev use.
- **ADR-001 follow-up #6 — close the `Api → Infrastructure` layering violation.** Closed. Every type
  in `EAMS.Infrastructure` is `internal` except the composition-root extension; `EamsDbContext` is
  unreachable from `EAMS.Api`. Controllers depend on `EAMS.Application.Abstractions` and speak DTOs.
  The enforcement is the compiler (CS0122), not a convention or a review checklist — which is the
  only kind of layering rule that survives contact with a deadline.
- **ADR-001 follow-up #3 — confirm filtered-unique-index provider support before locking the
  provider.** Closed. The provider is SQL Server; it supports filtered unique indexes; D-3's
  `UNIQUE(SchoolId, CardUid) WHERE IsActive = 1` is implemented and covered by integration tests
  running against real SQL Server (Testcontainers). Per `CLAUDE.md`, those tests must **not** be moved
  to EF InMemory — filtered indexes and SQL Server's NULL-equality semantics inside a unique index
  simply do not exist there, so an InMemory suite would pass while the constraint was absent.
- **ADR-001 D-6's two seams exist.** The `SchoolId` EF Core global query filter and the `ICurrentUser`
  tenant seam are in place. This is the part of D-6 that was genuinely expensive to retrofit — a
  missed filter is a cross-tenant leak, and retrofitting means re-reviewing every query ever written.
  Installing it early was the load-bearing half of the deferral bargain, and it held.

**Still open from ADR-001, unchanged by this ADR:** the D-2 refresh trigger (half of follow-up #2),
D-4's version-retention answer, and D-6's un-stubbing of §11 auth in full.

## Accepted Context (not drift)

- **The evidence base for D-7 and D-11 is one college.** The Phase 2 export is 536 rows from CICSS.
  ADR-001 cites an earlier, smaller extract ("fifty-two students"); the two numbers are different
  extracts at different times, not a contradiction — and per ADR-001 D-1 the export's grain is
  *student × course × section*, so row count is not student count. Every statement in this ADR about
  what the data "always" does is a statement about **one college's export at one point in time**.
  D-11 exists entirely because of that limitation.
- **`AlternateEmail` being null is the expected state, not missing data** — the same status
  ADR-001 gives `SisExternalId`. Roughly two-thirds of rows have no personal address. Do not build a
  data-quality alert on its absence.

## Consequences (overall)

### Positive
- ADR-001's open questions are answered **in the register**, where the next reader looks, with their
  rejected alternatives attached. The failure mode JJReviewer flagged is closed for these five items.
- The most dangerous thing in the schema — a silent, unrecoverable course merge — is now written down
  with its trigger, its remedy, and its deadline, instead of living in one person's memory of a Phase
  1 conversation.
- Two published contracts (§4.12's `Result`, §6.8's filter) survived a feature that pressured both.

### Negative
- Three documents to read together now (§4, ADR-001, ADR-002), and nothing enforces it. ADR-001
  already carried this cost; this ADR increases it. Around ADR-004 a consolidation ADR becomes the
  right call.
- D-11's hedge is live and undated in code. This document is the only place its expiry is recorded,
  which means the mitigation for "the reasoning gets lost" is "someone reads this file." That is a
  weak mitigation and it is the best one available short of an automated check.
- D-9's home-section rule produces confident-looking nonsense for evenly-split students, and nothing
  in the UI distinguishes that case from a real single-section value.

### Neutral
- Decision numbering is continuous across ADRs in this repo (ADR-001: D-1..D-6; ADR-002: D-7..D-11).
  A future ADR continues from D-12.
- §6 API contracts remain unchanged by this ADR as shipped. Two additive changes are proposed but not
  taken (`AlternateEmail` on `StudentDto`, a warning filter on §6.8's rows endpoint).

## Follow-Up Actions

Ordered by consequence, not by effort.

- [ ] **Ask the registrar whether course codes are unique across colleges.** One question; it retires
      or triggers D-11's entire hedge. Cheapest high-value action in this list.
- [ ] **Execute or formally abandon the `Courses.CollegeId` widening BEFORE any second college's
      export is imported.** Trigger: a new college roster arriving. After colliding data lands, this
      becomes a split with live-FK re-keying (D-11).
- [x] **Make the course-code collision failure message cite ADR-002 D-11 by name**, so the operator
      hitting it — and the engineer tempted to downgrade it to a warning — reach the reasoning.
      **Done** — `SisImportService.cs` (course collision), and the section-collision warning cites it
      too, explaining why *that* one is safe to warn on so the asymmetry does not read as an
      inconsistency worth "fixing". Ticked here rather than left open: an item this register asserts
      is live while it is closed is the same failure this ADR was written to correct, and this
      document is `Proposed`, so it is free to edit.
- [ ] Confirm and record the **D-2 display-cache refresh trigger** (import-time / on enrollment write
      / scheduled). Half of ADR-001 follow-up #2 is still open; D-9 answered only the other half.
- [ ] Decide whether **`AlternateEmail` surfaces on `StudentDto`** (§6). Additive either way; needs a
      decision so it is not answered twice differently.
- [ ] Add an **additive warning filter to §6.8's rows endpoint** (`hasWarning` / `warningCode`), or
      record that clients filter client-side. Today "show me everything needing attention" is two
      queries or an unfiltered fetch.
- [ ] Decide whether the **evenly-split-student case needs a UI signal** (a multi-section indicator on
      the grid), so the cached section is not read as authoritative when D-9's tie-break decided it.
- [ ] Extend the **§14 retention policy to `SisImportRowEntities`** — highest-cardinality table in the
      schema, currently uncovered by the Hangfire purge that covers `SisImportRows`. Note the tension:
      that provenance is also what makes D-11's post-merge recovery possible, so retention must
      outlive the D-11 hedge.
- [ ] **Un-stub §11 auth in full.** Open since ADR-001 D-6, unchanged here. The API is open; the
      condition ADR-001 set (data layer stable) is now substantially met.
- [ ] Answer **D-4's mapping-version retention** question. Open since ADR-001; versions accumulate.
