# ADR-005: Classifying the Roster — What Kind of Person Each Row Is

**Status**: Proposed (2026-09-17) — awaiting JJ's read-through, then flip to `Accepted`
> The *decisions* below are JJ's and were made on 2026-09-16; this *document* has not yet been read
> by him. The register's own rule is "do not self-accept an ADR listing someone else as a decider",
> and the failure it guards against is a document that misstates a decision while carrying the
> decider's name. Flipping this line is a one-line edit; un-flipping it after the fact is not.
**Date**: 2026-09-17
**Deciders**: JJ; JoseArch (Team X)
**Relationship to ADR-001 / ADR-002 / ADR-003 / ADR-004**: **Amends all four by reference.** None is
edited; all remain `Accepted` and D-1…D-21 and D-54 stand as written. This document continues the
decision numbering at **D-55**, which is where ADR-004's own Neutral consequences said a future ADR
would continue.

> **⚠ This is not the consolidating ADR either, and that is the third deferral.** ADR-002 anticipated
> one "around ADR-004". ADR-003's follow-up list calls for it *at* ADR-004. ADR-004 took the 004 number
> for a production defect fix and moved the consolidation to ADR-005, which `README.md` records in a
> hazard box headed *"arriving for the second time"*. **This document takes the 005 number for a
> feature slice, and JJ's call is that the consolidation moves to ADR-006.** It is said here, plainly,
> rather than only in the register, because a reader who opens this file alone would otherwise have no
> way to know that the number they were told to look for is not the document they are holding. The
> register is amended in the same change; ADR-003 and ADR-004 are `Accepted` and immutable and will go
> on pointing at ADR-004 and ADR-005 respectively, for ever. That is the exact hazard the top of
> `README.md` describes, arriving for the **third** time — and three deferrals of one document is now
> a fact about the practice rather than an accident of scheduling.

> **Read this before assuming §4 was overridden.** **The Technical Plan is silent on classification
> entirely.** A grep of `Events-Attendance-Monitoring-System-Technical-Plan.md` for *classification*,
> *category*, *friar*, *personnel*, *C2B2* and *CFI* returns **nothing**. So unlike ADR-001, which is
> an errata sheet against a §4 that said something different, almost everything below **extends** §4
> and §6 rather than contradicting them. The real contradictions in this slice are of three other
> things: a **written QA answer** (D-56), and **two documented project rules** (D-57 and D-58). Those
> three are the ones a future reader will otherwise "fix" back.

## Context

Task 5 of `taskscompilation1` asked for the roster to answer a question it had never been asked:
**what kind of person is this row?** The client's access-control export (`Personnel.xlsx`, sheet
`Report`, 21,497 rows) already answers it — in four columns nobody had read — and the answers are:

| Value | Source column | Rows |
|---|---|---|
| `STUDENT` | `STUDENTTEMP` | 20,861 |
| `NAP` | `PERSONNEL` | 292 |
| `ACAD` | `PERSONNEL` | 271 |
| `ANT` | `PERSONNEL` | 7 |
| `SUPERVISORY/MANAGERIAL` | `PERSONNEL` | 1 |
| `USA FRIARS` | `FRIARS` | 13 |
| `C2B2` | `SPECIAL` | 16 |
| `CFI` | `SPECIAL` | 5 |

Three facts about that table decided the whole slice and none of them was in the brief.

1. **The counts do not partition the rows.** Three people hold two values at once — two are
   `STUDENT` + `NAP`, one is `STUDENT` + `C2B2`. Any model that stores one value per person is wrong
   about them, and wrong in the specific way this project has already been bitten by: it answers
   non-empty and plausible. ADR-001 D-2 documents the identical mechanism on `Students.Section`,
   where 23% of students sit in more than one section and the single-valued column silently misses
   about a quarter of any section-filtered query. That column survives only because the SPA binds it
   and dropping a populated column is forbidden. **This one had not been created yet, which is the
   cheapest that lesson has ever been available.**
2. **QA's written rule for finding personnel is measurably wrong.** MDVault #404 Q3 answers that
   personnel carry a registration number shaped `720000XXXX`. Against the real export that rule
   misfiles **22 rows in both directions**: 21 of the 292 `NAP` staff carry numbers like `0020255`,
   `0020240` and `0000000`, and one row that *does* carry `720000` is flagged `STUDENT` and nothing
   else. And where it does fire it names no value — among rows carrying the prefix the personnel
   column holds four different categories, and 12 such rows are `USA FRIARS`.
3. **QA named two of the eight values.** Q2 answers `STUDENT` and `NAP`. The other six are in the
   data whether or not anyone listed them, and a vocabulary that omits them warns on 313 rows it
   could have categorised.

Around that core the slice also picked up the neighbouring tasks the same data made answerable —
finding a person by a card they no longer hold (Tasks 2/3), and two display columns the data was
already carrying (Tasks 1/4/9.4). They are here because they shipped in the same four commits, and
because one of them (`GET /cards`) is ADR-001 D-3 becoming visible in the API for the first time.

### What was in hand when each decision was made

Chronology is load-bearing in this project's ADRs, so: **the local 21,497-row fixture was in hand;
the school's routine export was not, and still is not.** Every count above is measured off the
fixture. Every decision below that turns on a population — the warn-once rule, the seeded eight, the
refusal to default to `STUDENT` — was made against that file and nothing else. The school's routine
export carries **none** of the four category columns, which is why D-67's status line matters more
than it looks.

## Options Considered

The options were about **where a person's category lives**. Everything else in the slice follows from
that answer.

### Option 1: Do nothing — leave the four columns unread
The importer already ignores them; it has since the first roster arrived.
- **Pros**: No schema, no migration, no API change. The four columns keep sitting in a file nobody
  parses, exactly as `Gender` and the other unmapped columns do.
- **Cons**: Answers none of Task 5, and the question is not going away — every downstream ask
  ("invite only personnel", "exclude friars from the attendance rate") needs it.
- **Effort**: Zero, and it buys nothing.

### Option 2: A `string` column on `Students`
The shape every other enum-ish value in this schema takes — `Status`, `AttendanceMode`,
`CaptureMethod` are all `string`s the code owns.
- **Pros**: One additive nullable column, one migration, no join. Consistent with the existing
  convention on its face.
- **Cons**: **Wrong about the three dual-category people, silently.** And the convention does not
  actually apply: those three columns are closed sets *we* decide, where a new member is a code
  change; QA's Q2 says this list is **not** fixed and **an administrator edits it**. A denormalized
  string makes an administrator's rename a mass `UPDATE` over 21,466 rows with nothing to roll back
  to, and makes two spellings of one category indistinguishable from two categories.
- **Effort**: Low, and it is the option that looks right in review.

### Option 3: A vocabulary table plus a scalar `Students.ClassificationId`
Fix the rename problem, keep the single column.
- **Pros**: Renames become one row. Identity is a GUID, so no person loses their category to an edit.
- **Cons**: Still one value per person, so still wrong about the three — and now wrong *behind a
  foreign key*, which reads as rigour. The column would have been populated from a source that
  demonstrably carries two values for some rows, and nothing downstream could tell a person whose
  second category was dropped from a person who only ever had one.
- **Effort**: Medium, and it produces a defect that is expensive to unpick later because a scalar FK
  is what every consumer would be written against.

### Option 4 (chosen): A vocabulary table plus a junction keyed by axis
`Classifications` (the vocabulary) and `StudentClassifications` (who holds what), with the source's
four columns modelled explicitly as four **axes** and a person holding at most one value per axis.
- **Pros**: Says what the data says. A person holding two categories holds two rows; a person holding
  none holds none, which is also a real state (34 rows). Renames are one row. The "one per axis" rule
  is index-backed rather than convention-backed.
- **Cons**: A junction table, a denormalized `Axis` column to make the index expressible, a composite
  foreign key to keep that denormalization honest, and a collection on `StudentDto` where consumers
  might have expected a string. Five §6.2 endpoints change shape, one of which the mobile developer
  consumes.
- **Effort**: Medium-high — and most of it is in the importer, not the schema.

### Option 5 (rejected, and worth recording): derive the category from the registration number
QA's Q3 rule, taken as the rule rather than as a hint.
- **Pros**: No new source columns needed, so it works on the export the school actually sends today.
  That is a genuine advantage and it is the reason this was considered at all.
- **Cons**: Misfiles 22 of 21,497 rows measurably, and names no value even where it fires. It would
  have shipped a number that is *mostly* right, which is the hardest kind of wrong to find later.
- **Effort**: Trivial, and it is the trap.

---

## Decision

### D-55: A person's classification is a **vocabulary table plus a junction keyed by axis**, never a scalar column

`Classifications` holds the institution's own vocabulary — `Id`, `SchoolId`, `Name`, `Axis`,
`NameKey`, `IsActive`, `RetiredAt`, `MergedIntoClassificationId`. `StudentClassifications` holds who
holds what, one row per person per axis, and `UX_StudentClassifications_Student_Axis` says so.

**This is ADR-001 D-2's reasoning applied a second time, and it should be read as one argument
appearing twice rather than as two coincidences.** D-2 records that `Students.Section` cannot describe
a student in two sections and does not fail when asked — it returns one of them. The category columns
have the same shape and a smaller population: three people in 21,497. A scalar
`Students.ClassificationId` would have been right about 21,494 rows and quietly wrong about three,
with nothing downstream able to tell which. D-2's column survives only because the SPA binds it and
the global no-DROP rule forbids removing it. **This one was never created.**

**`Axis` is denormalized onto the junction so that an index can exist, exactly as `RfidCards.SchoolId`
is under ADR-001 D-3.** A unique index cannot reach through a foreign key to read the parent's column,
so "one classification per person per axis" is only expressible as `UNIQUE(StudentId, Axis)` on the
child table.

**The denormalization is enforced by a composite foreign key, not by convention — and the composite
was built now rather than deferred.** `StudentClassifications(ClassificationId, Axis)` references
`Classifications(Id, Axis)` through a redundant alternate key, `AK_Classifications_Id_Axis`. The
alternate key adds no rule of its own — `Id` is already the primary key, so `(Id, Axis)` cannot repeat
— which is precisely what makes it safe: it exists only to give the child something two-column to
point at. **An axis-mismatched junction row is therefore not merely wrong, it is unwritable**, and it
fails as a 547 at the database rather than as a convention somebody forgot. A single-column reference
plus a comment would have left the invariant resting on every future writer remembering it, which for
a denormalized column is the same as not having one.

**`Classification.Axis` is immutable after creation, and that is what keeps the two in step.** Moving
a classification between axes would have to rewrite every assignment row's axis in the same breath and
could collide with a category the person already holds on the destination axis — a rename that
silently fails for some people and not others. `PUT /classifications/{id}` renames and nothing else; a
value filed under the wrong axis is fixed by creating the right one and merging.

**Axes are `string` with a `CHECK` constraint, on both tables, not a database enum.** Same reasoning
as `AttendanceStatus` and `AttendanceMode`: a real enum makes a fifth axis an `ALTER TYPE`, which is
the data-loss-shaped migration the project's hard rules forbid without a preserving script.

**Consequences.**
- *Positive*: The model says what the source says. Three people keep both categories, 34 keep none,
  and both are first-class states rather than edge cases.
- *Positive*: The migration is purely additive — two `CreateTable`s, one alternate key, five indexes,
  three check constraints. No `DROP`, no `ALTER COLUMN`, no backfill of an existing table.
- *Negative*: Two tables, a denormalized column and a composite foreign key where Option 2 had one
  `nvarchar`. Every reader of this schema has to understand why before they can safely change it,
  which is what this decision is for.
- *Neutral*: Nothing in `Students` changed. The scalar column was never created, so there is no
  deprecation and no migration path to manage.

---

### D-56: Classification is read from the **source's own category columns**; the `720000` prefix is a **report-only fallback**, and neither ever defaults to `STUDENT`

`RosterClassification.Resolve` is three tiers:

1. **The row's four category cells.** These are the truth. A person holds at most one value per axis
   and may hold several axes at once.
2. **`RosterClassification.PersonnelNumberPrefix` (`720000`) on the registration number**, for a row
   that carried no category in any column. It resolves to **no classification** and emits
   `ClassificationRegNoSuggestsPersonnel`.
3. **Neither: no classification, and `ClassificationMissing`.** Never a default.

> **⚠ This is an approved, deliberate deviation from a written client answer, and QA has not been told.**
> MDVault #404 Q3 gives the `720000` prefix as *the* personnel marker. This slice demotes it to a
> fallback that names nothing. **That conversation is owed to QA** and is listed in the Follow-Ups. It
> is flagged this loudly because a deviation from a written answer that nobody relays becomes, three
> months later, a defect report.

**Why the columns beat the prefix, measured rather than argued.** Against the 21,497-row export a
prefix-first derivation misfiles 22 rows — 21 `NAP` staff whose numbers look nothing like `720000`,
and one `720000` row flagged `STUDENT`. The category columns misfile none, **because the source
already answered the question**. Reading the column beats interpreting the number. That is the same
lesson `ClassificationSeedValues` records about inferring an axis from what a word looks like, and the
first pass at this slice got it wrong in exactly that way (see D-57).

**And the prefix cannot name a value even where it fires**, which is why tier 2 reports rather than
resolves. Among rows carrying the prefix the personnel column holds `NAP`, `ACAD`, `ANT` and
`SUPERVISORY/MANAGERIAL`, and 12 such rows are `USA FRIARS`. "This number looks like personnel"
narrows the answer to one of five and is not an answer. Picking among them would store a guess where
a fact is expected.

**Why there is no default to `STUDENT`, which is the tempting repair.** 34 rows carry no category at
all: 4 are junk (`Personnel No` equal to `Last Name`; first name literally `STUDENT`) and 26 look
like students whose flag was never set. Defaulting would be right about roughly 26 and would **invent**
the rest — and nothing downstream could tell an invented `STUDENT` from one of the 20,861 the file
names. A classification is read, never inferred.

**Two codes for the uncategorised, not one**, because the two piles have different fixes:
`ClassificationRegNoSuggestsPersonnel` is a personnel record whose category cell the registrar left
blank — 4 rows, all fixable at source — and `ClassificationMissing` is mostly students whose flag was
never set. An operator filtering a batch by code gets them separately, which is the whole reason a
code exists beside a message.

**Consequences.**
- *Positive*: Zero misfiles on the only real file anyone has, against 22 for the written rule.
- *Positive*: The rule lives in `EAMS.Domain` and is pure, so it is pinned by unit tests with no
  database — and the next caller that is not the importer inherits it.
- *Negative*: **It needs a column the school's routine export does not send.** See D-67.
- *Negative*: A deviation from a written QA answer is now load-bearing in the pipeline and is
  undocumented anywhere QA can see. Until the Follow-Up is discharged, QA's Q3 and this code disagree
  and only this document knows.
- *Neutral*: The prefix constant is named rather than inlined precisely so that a literal `"720000"`
  in the importer would read as a magic number rather than as a rule.

---

### D-57: Seed **all eight** values, with each value's axis read off the source column — and the seed runs **above** `SeedData`'s early return

`ClassificationSeedValues.All` carries all eight values with their populations, ordered by population
rather than alphabetically so the order is a fact about the data. `SUPERVISORY/MANAGERIAL` is seeded
verbatim, slash included — it is the value most likely to break a naive slug or route assumption, and
it is in the seed precisely so a build that made one cannot pass.

**All eight, not QA's two.** JJ's call: the other six exist in the data whether or not anyone listed
them, and a vocabulary that omits them makes the import warn on 313 rows it could have categorised.

**Each value's axis is the column it appears in, tallied — not inferred from the word.** Recorded
because the first attempt at this table reasoned from the strings and their populations and filed
`ANT` and `SUPERVISORY/MANAGERIAL` under `Special` — the latter because a population of **one** does
not look like a personnel rank. Both are `PERSONNEL` values. Nobody needs to know what `ANT`
abbreviates to place it correctly; the source already placed it. *(What `ANT` stands for is still an
open QA question. Its axis is not.)*

> **⚠ CONTRADICTION OF A DOCUMENTED PROJECT RULE — deliberate, and it must not be "fixed" back.**
> The repo `CLAUDE.md` states that the seed is **"all-or-nothing — it returns early if a `School` row
> exists"**. `SeedClassificationsAsync` is called **before** that early return and carries **its own
> per-row guard**, so it runs on every boot against a database that already has a school. That is a
> direct contradiction of the stated rule and it is on purpose.
>
> **Why.** Anything placed below the early return runs only on a database nobody has. That is not a
> hypothetical — it is exactly how this project shipped a missing `Term`: the term block was added to
> the seed after dev databases already held a school, so `if (Schools.Any()) return;` short-circuited
> before ever reaching it, and every carried-over machine had a roster-import page with an empty
> picker while a freshly dropped database looked perfect. `SeedTermAsync` was moved above the return
> for that reason; this is the second step to take the same position, and **anything added to the seed
> later wants its own guard for the same reason.**
>
> **A clean-slate test cannot catch this**, which is why `DevelopmentSeedClassificationTests` boots
> the host **twice against one database** and asserts the second boot as well as the first.

**The guard is per row, not "are there any", and that differs from `SeedTermAsync` on purpose.** A
term is a fixture — one is as good as another. The eight classifications are a *vocabulary*, and a
database holding seven of them is missing one rather than "already seeded": a value added to this list
in a later release has to reach installations that already ran the earlier one. The check is keyed on
`NameKey` rather than `Name`, so re-running after an administrator renames `SUPERVISORY/MANAGERIAL` to
`Supervisory / Managerial` does not helpfully re-add the original spelling alongside their edit.

**Nothing in the seed updates or reactivates an existing row.** A classification an administrator
retired stays retired across restarts; a name they edited stays edited. The seed's job is to make sure
the vocabulary *exists*, not to keep enforcing its opening state against the operator — which would
make the one feature this slice ships (an editable list) silently revert on every deployment.

**Consequences.**
- *Positive*: A carried-over dev database gains the vocabulary on its next boot, which is the failure
  mode the missing-`Term` incident cost a day to.
- *Positive*: A ninth value added in a later release reaches installations that already ran this one.
- *Negative*: `CLAUDE.md`'s description of the seed is now wrong in two places (`Term` and
  `Classifications`) and cannot be fixed from inside this ADR. Recorded as a Follow-Up.
- *Negative*: The seed does two different things either side of one `if`, and the reason is
  historical. A reader tidying the function into one shape breaks a boot path no clean-slate test
  covers.
- *Neutral*: The eight values live in `EAMS.Domain` rather than in `SeedData`, because the tests
  assert on them too and a second copy in the test project is the drift `SeedData.DevelopmentCardUid`
  already records being bitten by.

---

### D-58: The vocabulary has **no hard delete** — retire, or merge and leave a tombstone that itself cannot be deleted

Three operations, and only one of them is always available:

- **Retire** (`IsActive = false`, `RetiredAt` set). The value disappears from pickers and **every
  student already carrying it keeps carrying it**. Always available.
- **Merge** (`POST /classifications/{id}/merge`). Repoints every holder onto the survivor in one
  transaction, retires the loser, and sets `MergedIntoClassificationId` as a **tombstone**. Refused
  across axes.
- **Delete** (`DELETE /classifications/{id}`). Refused with **409** while anything references the row
  — and **a merge tombstone is itself a reference**, so a merged-away classification can never be
  hard-deleted. It is checked before any count is taken, because after a merge the loser has zero
  assignments (they moved) and zero tombstones pointing *at* it, so a count-based guard would find it
  empty and let the record of the merge be erased.

> **⚠ CONTRADICTION OF A DOCUMENTED PROJECT RULE — deliberate.** Every other admin resource in this
> codebase deletes. This one does not, and a reviewer reading `DELETE` returning 409 on a row with no
> visible children will read it as an over-strict guard.
>
> **Why.** A delete that cascaded, or that nulled the referring column to make itself succeed, would
> be a data-loss migration wearing a CRUD costume — 20,861 people silently uncategorised by one click,
> with nothing to restore from. That is the global hard rule arriving through an HTTP verb instead of
> through SQL. And the tombstone is the only thing that distinguishes "this category was merged into
> that one" from "somebody retired this category", after the fact, for a vocabulary seeded from a
> source messy enough that merges are expected rather than exceptional.

**`CK_Classifications_NoSelfMerge` and `CK_Classifications_MergedIsRetired`** hold the tombstone
honest at the database: a row never points at itself, and a merged row is always retired. Application
code may check `IsActive` alone and be accidentally correct today because of the second constraint —
`ClassificationAssignment.IsAssignable` checks both anyway, because accidental correctness stops being
true the moment somebody relaxes a constraint.

**`UX_Classifications_SchoolId_NameKey` is keyed `(SchoolId, NameKey)`, not `(SchoolId, Axis, NameKey)`
— and this is a hedge of exactly the ADR-002 D-11 shape.** All eight seeded names are distinct across
all four axes, so the school-wide rule is true of the real data and is the stricter of the two
candidates. It assumes **no two axes ever need the same word**. If that assumption breaks, widening to
the three-column form is a re-index — cheap while no colliding data exists, and it is a *widening*
rather than a split, so it does not carry D-11's unrecoverable tail. **It has D-11's one-question
retirement path: ask the registrar whether a category name can legitimately mean two different things
on two different axes.** One question, same as the course-code one, and it has been open the same way.

**Uniqueness is on `NameKey` rather than on `Name`** because the source is a messy access-control
export: `SUPERVISORY/MANAGERIAL`, `Supervisory / Managerial` and `supervisory-managerial` are one
category spelled three ways and all normalize to `SUPERVISORYMANAGERIAL`. Keying on the display name
would let an administrator create the second and third by hand and split one population across three
rows — which is the mess this slice exists to let them clean up rather than to reproduce. A name that
normalizes to nothing (`'///'`, `'   -'`) is refused at the boundary rather than filed under a
sentinel, and a name is **never silently trimmed**: a trimmed `' NAP'` and `'NAP'` would be two rows
that render identically in a picker, so a leading or trailing space is a refusal, exactly as `TermText`
already does.

**Consequences.**
- *Positive*: There is no click in this product that uncategorises a population.
- *Positive*: A merge is legible afterwards. Retirement and absorption are distinguishable.
- *Negative*: The vocabulary accumulates. A typo created and then merged away is permanent, and the
  only tidy-up is retirement. That is the price of the guarantee above and it is accepted.
- *Negative*: The name-uniqueness hedge is an assumption about an institution nobody has asked.
- *Neutral*: `Restrict` on every foreign key here — as everywhere else in this model — makes the
  delete guard belt-and-braces rather than sole: a held classification cannot be removed whatever the
  service believes.

---

### D-59: Four **optional** `*_CATEGORY` roster columns, named by us; profile `BuiltInVersion` 2 → 3; the **axis rides in the `TargetField` string**

`SisRosterColumns` grows from 18 to **22** columns: `STUDENT_CATEGORY`, `PERSONNEL_CATEGORY`,
`FRIARS_CATEGORY`, `SPECIAL_CATEGORY`, one per axis. `SisImportProfileTemplate.BuiltInVersion` goes to
**3** and maps each to `StudentClassification.<Axis>`.

**Four columns, not the two QA asked for** (JJ's ruling 14): two columns cannot express a person who
is both, which is the whole premise of D-55.

**Named by us rather than copied from the client's `STUDENTTEMP` / `PERSONNEL` / `FRIARS` / `SPECIAL`.**
These ship in the import **template the client fills in**, so `_CATEGORY` reads as a category to
anyone, where `STUDENTTEMP` is an internal spelling nobody outside their access-control system can
explain. A client who bolts their own headers on instead is not stuck: header matching is by
`AcademicKey` and the profile (ADR-001 D-4) resolves the source column per batch, so a different
header is a **new profile version rather than a code change** — the same seam `RfidCardSerial`
documents under D-43.

**All four are optional, and that is load-bearing.** Every roster file that exists today lacks all
four; a `Required` addition would reject the only file anyone has. A person with no category is a
first-class outcome that reports itself (D-56), not an error that loses the row.

**The axis is encoded in the dotted `TargetField` rather than in a column of its own.**
`SisImportProfileColumns` has no axis column and adding one would be a migration in service of a
single mapping; dotted targets are already how that table names a destination (`Student.StudentNumber`,
`Course.CodeKey`), and the four axis names are a closed set the domain validates, so the encoding is
parseable rather than conventional. **It has a cost and D-63 is the price paid back for it.**

> **The version bump has a consequence worth stating on its own: a batch pinned to profile version ≤ 2
> maps no category column, reads no category cell, and classifies nobody — from a file carrying all
> four — and says nothing about it.** That is **ADR-001 D-4 working exactly as designed**, not a gap:
> a batch re-run after a mapping change must execute the rules it was uploaded under. A roster
> imported in August is still explained by August's rules, and re-importing the file is what brings it
> under the new ones. It is recorded here because "the file has the columns and nobody got classified"
> is otherwise indistinguishable from a bug.

**Consequences.**
- *Positive*: Adapting to a client who spells the headers differently is a profile version, not a
  release.
- *Positive*: Historical batches stay explainable, which is the entire point of D-4.
- *Negative*: The silent-on-old-version behaviour is correct and looks like a defect.
- *Negative*: An axis in a string is an axis a typo can break — see D-63.
- *Neutral*: `RfidCardSerial`'s position in the 22-column order is still a guess, because no export
  carrying an RFID column has ever been seen. Only the fixture and the upload preview read that order.

---

### D-60: **First-write-wins** — the importer is not authoritative for the classification it writes, and it never mints vocabulary

Three rules, and the first is the most consequential decision in this slice.

**1. An import never overwrites an assignment that is already there.** If the file names a category on
an axis where the person already holds a *different* one, the **stored assignment is kept**, the file's
value is **not** applied, and `ClassificationConflict` is raised naming both values.

**2. A blank cell never clears a stored assignment.** A blank means "this file does not say", never
"clear what you have". Nothing in this pass ever deletes or moves an assignment.

**3. The importer never creates a classification.** A category the vocabulary does not have raises
`ClassificationUnavailable` and the person is imported unclassified on that axis.

> **⚠ This is the decision most likely to be reversed by a future reader, and it is the one to argue
> with before changing.** Every other dimension in this pipeline — colleges, programmes, courses,
> sections, offerings — is *minted from the file and updated from the file*. The importer is
> authoritative for all of them. **Here it is not**, and a reviewer who has internalised the rest of
> the pipeline will read rules 1 and 3 as omissions.

**Why the import yields (rule 1).** A classification can now be set by hand
(`PUT /students/{id}/classifications/{id}`), and **nothing on the junction row records which writer put
it there** — so "overwrite unless a human set it" is not a rule this schema can express. Of the two
rules it *can* express: import-wins silently reverts every manual correction on the next run, for ever,
which makes the back-office surface a formality. First-write-wins instead leaves a genuine source
correction unapplied — a real cost, and precisely why it is **not silent**: the warning names both
values so an operator can see the disagreement and settle it. **Stale and visible beats fresh and
destructive.** This is JJ's ruling 12, and it is recorded at this length because the cheap-looking fix
("just let the import win, the file is the source of truth") is the one that deletes people's work.

It cannot fire on an ordinary re-import: a person holding the value the file names is `Unchanged`, not
a conflict. And "already holds exactly this" is asked **before** the row's availability is checked —
required rather than incidental. The first cut asked in the other order and got an ordinary case
wrong: somebody holds `NAP`, an administrator retires `NAP` (every holder keeps it, by design), and
the next import of the unchanged file reported that person as not classified on that axis. They were,
and nothing had changed.

**Why the importer mints nothing (rule 3).** Two reasons, and neither applies to colleges or courses.
QA's Q2 says **an administrator owns this list**. And `UX_Classifications_SchoolId_NameKey` plus D-58's
no-hard-delete makes a row created from a mis-keyed cell **permanent** — there is no delete while
anything references it, only a merge somebody has to perform. A roster whose `STUDENT_CATEGORY` column
is filled with `'NA'`, which is exactly what a hastily generated export does, would otherwise mint a
category called `NA` and file 21,497 people under it. **Reporting costs one warning; minting costs an
administrator a merge and leaves the population mis-filed in the meantime.**

**A *retired* value reports rather than reactivating**, for the same reason a revoked card does under
`RfidCardRevoked`: retiring is a decision a person made, and the roster does not get to reverse it.

**Per axis, the first row of the person's group that carries a value wins — not the first row's whole
set.** A person named on four course rows may carry `STUDENT` on one and `NAP` on another, and taking
one row's set entire would drop the second axis for somebody who genuinely holds both. Two *different*
values on one axis is a source contradiction and is reported by D-62's identity-conflict path.

**Consequences.**
- *Positive*: A back-office correction survives every subsequent import, for ever. The admin surface
  means something.
- *Positive*: A mis-keyed export cannot permanently pollute a vocabulary that has no delete.
- *Negative*: **A genuine source correction is not applied** until somebody acts on a warning. The
  file and the record can disagree indefinitely, by design.
- *Negative*: The importer now behaves differently for classifications than for every other dimension
  it touches, and only this document says why.
- *Neutral*: The asymmetry is recorded in the warning codes' own remarks as well as here, so a reader
  who meets it in code finds the argument without this file.

---

### D-61: **Warn once per disagreement** — `StudentClassification.ReportedRosterValue` is the first warning in this pipeline whose emission is suppressed by stored state

`ReportedRosterValue` is a nullable `nvarchar(100)` on the junction row holding **the roster value last
reported as disagreeing with this assignment**. `ClassificationConflict` fires the first time, stays
silent while the file keeps saying the same thing, and **fires again the moment the file says something
else**. Comparison is by `ClassificationText.KeyFor`, so a re-spelling of an already-reported category
is not a new disagreement. It is reset to `null` whenever the assignment itself changes.

**Why a column exists at all.** Under D-60 the file and the record go on disagreeing for as long as the
source is not fixed — so a warning with no memory re-announces it on every run, for every corrected
person, for ever. After a few hundred corrections `CompletedWithWarnings` is the permanent status of
every import and the genuine warnings — an unknown category, a person with none — are buried in the
pile. That is the same failure `ParseRows` already refuses for a missing RFID column, arriving through
a different door.

**It stores the value, not a flag**, because the distinction the rule turns on needs one: a boolean can
say "already told you" but cannot tell *still* `ACAD` from *now* `ANT`, and the second is a new
disagreement an operator has never seen.

**Only this code got a memory, and the asymmetry is the argument.** `ClassificationUnavailable` and
`ClassificationMissing` are *actionable and self-clearing* — add the category, or fill the cell, and
they stop by themselves, so a batch that keeps raising them is reporting a problem that genuinely still
exists. `ClassificationRegNoSuggestsPersonnel` is the same shape. The conflict is the one that is
**unresolvable by design**: a correct, deliberate back-office decision keeps disagreeing with an unfixed
source for ever, and a warning nobody can discharge trains an operator to ignore the whole column.
**The memory exists for that asymmetry and should not be extended to a code that can simply be fixed.**

**Every writer that re-points a junction row must clear it**, not only
`StudentClassificationService.ReplaceAsync`. `ClassificationService.MergeAsync` is the other one and it
was **missed on the first cut**: a merge moves a person onto the survivor by exactly the measure a
replacement does — a different `ClassificationId` on the same row — so a stale value there suppresses
an announcement for a disagreement nobody has been told about. Adding, removing and the importer's own
insert need nothing: a new row starts `null` and a deleted one is gone.

**Writing it bumps neither `UpdatedAt` nor a row outcome** — the same reasoning as
`Student.LastSyncedAt`. It records what an operator has been told, not a change to who the person is,
and counting it as work would make a re-import report updates it did not make.

> **⚠ A CONFIRMED DEFECT LIVED IN THIS DECISION'S SEAM. IT IS FIXED (2026-09-17) — read this section
> for the shape of it, and the box at the end for what the fix does and does not reach.**
>
> As originally written, `ReportedRosterValue` was written inside the **fact pass** and flushed by the
> fact-pass `SaveChangesAsync`. The `ClassificationConflict` warning it gates is written by
> `RowLedger.ApplyInChunks` in **later, separately chunked saves** — a split ADR-004 **D-54.8**
> introduced deliberately, for lock-duration reasons that are still correct.
>
> A process death in that window leaves `ReportedRosterValue` durably set while **no warning about it
> was ever written anywhere**. ADR-004 **D-54.5**'s sweep then marks the orphaned batch `Failed`; the
> retry re-runs `ExecuteAsync`, which deletes and rebuilds **`SisImportRowEntities`** — *not*
> `StudentClassifications`. So the retry reads back the pre-crash memory, the warn-once comparison
> matches, and the disagreement is **reported zero times, forever quiet**, while the batch reports
> `Completed`.
>
> **D-54.8's own defence does not cover this.** Its comment — *"the inconsistency is cosmetic… only
> visible on a batch that already says `Failed`"* — is about `SisImportRow.Result` / `WarningCode` /
> `WarningMessage`, which the retry's delete-and-rebuild **does** erase and redo unconditionally. It
> says nothing about a column on a **different table the retry never touches**. That asymmetry is the
> defect, and it is the first time this pipeline has had durable state gating a warning.
>
> **Reproduced by `SisImportClassificationCrashRecoveryTests` with a passing negative control**
> (`Control_the_same_disagreement_warns_when_memory_was_never_pre_written`), per the project's
> negative-control rule.
>
> **The decision: co-locate the memory with the warning, so that a crash leaves neither written.**
> The memory must not become durable in a save that is not also the save carrying the announcement it
> suppresses. **See "Accepted Context" below for the implementation status of this specific item — it
> is the one part of this ADR that was not in the tree when this document was written.**

**Consequences.**
- *Positive*: `CompletedWithWarnings` keeps meaning something after the hundredth manual correction.
- *Positive*: A file that changes its mind is announced again, which is the case an operator cares
  about most.
- *Negative*: **Durable state now gates whether a warning is emitted.** Nothing else in this pipeline
  works that way, and the failure mode is silence — the hardest thing to notice. Any future
  save-boundary change has to ask whether it has re-opened the window above.
- *Negative*: Two writers must remember to clear it and one of them already forgot once.
- *Neutral*: The column is nullable and unindexed on purpose — it is only ever read through a row
  already located by `(StudentId, Axis)`, and nothing filters or joins on it.

---

### D-62: Four new warning codes, a new fan-out entity type, and a **reordered** — not appended — precedence list

`SisImportWarningCode` gains `ClassificationUnavailable`, `ClassificationConflict`,
`ClassificationMissing` and `ClassificationRegNoSuggestsPersonnel` (D-63 adds a fifth).
`SisImportEntityType` gains `StudentClassification`. `RowLedger`'s fan-out ceiling per source row goes
from **10 to 14** — ten, plus one junction row per axis.

**These are additive and are NOT a contradiction.** ADR-001 **D-5** created the warning columns for
exactly this — "a row can succeed while still reporting a non-fatal anomaly" — and ADR-002 **D-10**
refined the warning-vs-status semantics. A new code is a new member of an open set the schema already
carries as `nvarchar`, and a new fan-out entity type is the same. Recorded because the register lists
warning semantics as decided in D-4/D-5/D-10, and a reader checking whether this slice overrode them
should find the answer to be *no*.

> **`RowLedger.WarningPrecedence` was REORDERED, not appended to — and that changes the headline code an
> operator sees on rows that have nothing to do with classification.** The list decides which single
> code a multiply-warned row reports. The four new codes were **interleaved**: the two that describe a
> category the file named and the system would not apply rank with the source bugs, above
> `CourseCollegeAdopted` / `CourseTitleAlias`; the two that describe a category the file did **not**
> name rank below them and above the file simply being incomplete. A row that previously reported
> `CourseTitleAlias` can now report `ClassificationUnavailable` instead. **Existing surfaces that
> filter or group by `warningCode` will see a distribution they have not seen before**, on batches
> whose data has not changed.

**Per-axis identity-conflict detection is folded into the existing `StudentIdentityConflict` rather
than given a code of its own — i.e. a category is treated as identity, not as enrollment data.** Two
rows for one REGNO disagreeing about an axis is the same class of source defect as two rows disagreeing
about a surname: both describe the *person* rather than the *enrollment*. Only a **contradiction**
counts — one row naming `NAP` where another is blank is the ordinary shape of a file whose grain is the
enrollment, and treating that as a conflict would warn on most of the roster.

**Consequences.**
- *Positive*: An operator sees the classification findings in the same batch report, under the same
  mechanism, as every other data-quality finding.
- *Negative*: The precedence reorder is a behaviour change on existing rows and nothing outside this
  paragraph announces it.
- *Neutral*: The fan-out ceiling stays below `RowLedger`'s chunk size, so the save-boundary guarantee
  D-54.8 rests on — a row's outcome and its own fan-out are never written by different saves — still
  holds at 14.
- *Neutral*: **The generated OpenAPI document needs no regeneration for any of this.** `warningCode` is
  declared `type: string, nullable: true` with no enumeration anywhere in the document; verified
  against `docs/api/openapi.json`. Under ADR-004 **D-38** the document is the contract, so this was
  checked rather than assumed.

---

### D-63: `ClassificationAxisUnknown` — two silent mapping faults made loud, and two left silent on purpose

The axis lives in a string (D-59), so an operator can mistype it. `StudentClassification.Faculty` in a
profile row is not a category the vocabulary refuses — **it is a column the importer never reads at
all**, and until this slice it was silent in *both* directions: no column was read for that axis, and
because `CategoryColumnsPresent` is only set from a column that *was* read, `ClassificationMissing`
could not fire for it either. **A batch authored against `Faculty` finished `Completed`, clean, with
21,497 people unclassified on the axis the whole mapping existed to read.** That is the cost of D-59's
encoding, and this code is the price paid back.

**One code, two faults.** It also covers a target whose axis is *real* but whose profile row names no
readable source column — neither a column name nor a key — **and which no later row for that axis
rescues**. The post-loop filter is what makes that last clause true: an unusable first row followed by
a usable second one classifies everybody and correctly stays silent. The message distinguishes the two;
the code does not, because nothing an operator does with them differs — both are one profile row that
reads nothing and files nobody, and both are repaired by correcting that row and running the batch
again. A second code would split one pile in the batch report for a distinction that changes no action.

**It warns rather than refusing the run.** ADR-001 **D-4** makes a batch execute the mapping it was
uploaded under, and `RfidCardFromLegacyMapping` (D-43) already settled what to do with a mapping this
system disagrees with: let it run, and stop being quiet about it. Refusing would spend an entire roster
on one mistyped word in a supplementary column, and would turn the re-run of a historical batch into a
failure — the one thing D-4 exists to prevent.

**It is raised on *every row* of the batch, where `ClassificationMissing` deliberately is not**, and it
is ranked **first** in D-62's precedence. The standing objection to a warning on 100% of a batch's rows
is an objection about a condition that is the *normal* case; this one cannot arise from a correctly
authored profile at all, so every batch it fires on is a batch that needs looking at. And the fault is
a property of the **batch** rather than of any row — `WarningRows == TotalRows` is the signature that
says "this is the mapping, not the data" from the counters alone. It is the batch-level report this
schema has nowhere else to put: `FailureReason` is meaningful only on a run that did not finish, and a
batch-level warning column would be a migration for one sentence.

> **Two neighbouring faults stay silent, and both are decisions rather than oversights.**
>
> **Left silent, LOGGED NOT FIXED — the RFID column that collapses to `null`.** The RFID mapping
> resolves as *"the first RFID row by ordinal decides, and an empty key means no card"*. A misconfigured
> card mapping therefore produces the same `null` as the ordinary, universal case of a file with no RFID
> column — and is indistinguishable from it. **JJ's call: out of this slice's scope, because it touches
> the tap path.** Recorded so the next person to find it knows it was seen. *Not* fixed here.
>
> **Deferred — a second profile column claiming an axis a first column already took.** The later column
> is skipped with no warning. Lower severity, and JJ agreed: the axis still works via the first column
> by ordinal, so the batch is **incomplete rather than wrong**. It is reachable by design, because the
> profile's own unique index permits one target on two source columns.

**Consequences.**
- *Positive*: The one fault in this pipeline that was silent in both directions now announces itself
  on every row it affects.
- *Negative*: On an affected batch it takes the single code slot on every row. The messages are all
  still in `WarningMessage`, and the alternative is worse: a batch-wide fault ranked last is displaced
  on exactly the rows that have something else wrong with them, and so disappears from a filter by
  code.
- *Neutral*: It cannot fire at all on a correctly mapped batch, so it can displace nothing there.

---

### D-64: `GET /api/v1/cards` pages **cards**, not students — ADR-001 D-3 becoming visible in the API

A card-serial search that returns **every** matching card, active or withdrawn, with the holder as a
field on each row.

**Why it is `/cards` and not a branch of `/students`, which is the decision the endpoint turns on.** A
route under the roster would say the student is the answer, and **ADR-001 D-3 is explicit that it is
not**: uniqueness is `UNIQUE(SchoolId, CardUid) WHERE IsActive = 1`, so *inactive* rows are
deliberately unconstrained and one serial can name several cards held by several people. QA's Q5
requires a withdrawn card to still resolve, which puts those rows in scope and makes the lookup
multi-valued **by construction**. The thing that always has exactly one answer is the **card**, so the
card is the resource.

**It does not replace `GET /students/by-card/{cardUid}` and must not be tidied into it.** That route is
the kiosk's — a device key, a rate limit, a whole UID, active cards only, one student or a 404. Its
single-answer shape is correct there (only one card taps at a time) and it is published contract the
mobile client is already built against. Both contracts are pinned in one test so neither can be folded
into the other.

**Partial matches, normalized fragment, significant leading zeros.** QA's Q6 requires `2503` to surface
`0012503326`, so it is a substring search. The fragment is normalized the way stored serials are —
uppercase, separators stripped — so `25-03`, `25:03` and `2503` are one search. An unusable fragment
(one that normalizes to empty) is **refused**, not answered: `LIKE '%%'` matches the whole school, and
an empty result would claim something about the roster when the truth is about the request.

**Consequences.**
- *Positive*: "Whose card is this one I found on the floor" is answerable, including for a card the
  holder no longer has.
- *Positive*: D-3's filtered uniqueness is now visible in the API shape rather than only in a migration,
  which makes it harder to forget.
- *Negative*: Two routes now resolve a card UID and they answer differently on purpose. Only a test
  and this paragraph stop them converging.
- *Neutral*: It takes `IStudentService` rather than a service of its own, because cards are part of the
  student aggregate and always have been — a second service over the same table would be two places for
  D-3's rules to disagree.

---

### D-65: **Every** read that returns a student carries their classifications — including the `by-card` device contract — and **no new permission code is minted**

`StudentDto` gains `Classifications`, an ordered `IReadOnlyList<StudentClassificationDto>`, populated
on **all five** §6.2 student reads. It is read-only on the DTO; assignment is
`PUT /students/{studentId}/classifications/{classificationId}`, and sending the collection back on a
`PUT /students/{id}` is ignored exactly as `id` and `cards` are.

**All five, not the grid alone.** A field that came back `[]` on the detail read while the grid showed
two categories is the same silently-wrong answer in a new place — and worse here, because the edit form
opens from a grid row and would save the empty version back. **An always-empty field on one route is
its own lie** (JJ's ruling 10).

> **⚠ One of the five is `GET /students/by-card/{cardUid}` — the device/mobile contract — and ADR-004
> **D-40** carries a standing caution about changing a mobile-consumed body.** D-40 declined to *remove*
> a redundant field from `TapResult` because 4c had already changed that body once and the mobile
> developer's question about whether it broke him is unanswered. **This change is additive** — a new
> member on a JSON object, which a tolerant reader ignores — where D-40's was a removal. That is the
> distinction that makes it acceptable, and it is stated here so that the *next* person does not read
> this as precedent for a breaking change to a mobile-consumed body. **The developer should still be
> told.** It is a Follow-Up.

**No new permission code was minted, and this is recorded as a positive decision rather than as an
absence.** All three new controllers — `CardsController`, `ClassificationsController`,
`StudentClassificationsController` — declare `students.read` / `students.write`. Not `cards.read`, not
`classifications.read`. **This is deliberate ADR-001 D-45 compliance**: D-45 deleted a minted
`groups.read` outright because the plan already assigned `students.read` to that page, and recorded
that two codes for one page is the drift the registry exists to stop. Cards and classifications exist
only to identify and describe the people in the roster; the administrator who curates one curates the
other; and minting a code here would change the approved RBAC grant matrix (`RbacSeedTests` pins
11 / 11 / 6 / 4) as a side effect of adding a lookup. **Written down so that nobody re-mints
`cards.read` believing it was simply forgotten.** The attributes enforce nothing today, per ADR-001
D-6.

**The write surface carries no axis anywhere in the URL or the body.** The axis is read off the
classification itself, so a caller-supplied axis could only ever agree with the row or be a constraint
violation — it cannot be expressed. **The replace is one guarded `ExecuteUpdate`** whose predicate names
the classification the caller read, so two operators editing one person cannot both be told they
succeeded (JJ's ruling 11). It is an untracked `ExecuteUpdate` rather than a tracked mutation because
the affected-row count *is* the answer: a tracked update whose row has vanished raises
`DbUpdateConcurrencyException`, which carries no inner `SqlException` and so matches none of this
class's catch filters — a caller-level conflict that would have shipped as a 500. Here that case is
simply `0`.

**Deadlock is answered as 409, and closing that path fixed a latent defect that predates this slice.**
Assign and clear reach the same row through different indexes, so two operators working on one person
can deadlock (SQL Server 1205). Both paths are wrapped, **reads included** — the escape came from a
read, and a net over the writes alone would have looked complete. A deadlocked caller is told nothing
happened and to retry, which is true, rather than being told the person holds no classification, which
is not. **The latent defect:** `ExecuteUpdateAsync` does **not** wrap in `DbUpdateException`, so the
pre-existing `SqlServerErrors` predicates matched nothing and a constraint violation from any
`ExecuteUpdate` anywhere escaped as a 500. Both predicates now accept either shape and delegate, which
leaves every existing caller unchanged.

**Consequences.**
- *Positive*: The classification a person holds is visible everywhere a person is, so no surface can
  disagree with another about it.
- *Positive*: The RBAC grant matrix is unchanged by a slice that added three controllers.
- *Negative*: Five §6.2 response bodies changed shape in one slice, one of them consumed by a client
  we do not build.
- *Negative*: Classifications are batch-loaded by the ids actually served, which keeps a page at three
  statements whatever its size — but the batch load inlines GUID literals, because
  `SqlServerCompatibilityLevel` is pinned for the deployed SQL Server 2012 and EF must not emit
  `OPENJSON`. That trade is taken knowingly and is **unmeasured at roster scale**.
- *Neutral*: `docs/api/openapi.json` and `docs/api/endpoints.md` were regenerated with the committed
  half of the slice.

---

### D-66: The product says "Academic Community"; the schema, the routes and the permission codes still say **"Student"**

The side navigation, page header and two breadcrumbs now read *Academic Community*. **The route is
still `/students`, the component is still `Students`, the permission code is still `students.read`,
the table is still `Students` and the DTO is still `StudentDto`.**

**No rename to "Attendee" anywhere** (JJ's ruling 7). Confirmed with QA: the task asks for the label
and explicitly not for the routes, so a URL that still reads `/students` is **not a missed rename**.

**Two places keep saying "Students" on purpose**: the import preview's row count and the dashboard KPI
tile. Both label a *count of student records* rather than the section of the product, and renaming a
tally to "Academic Community" would make it read as a link to somewhere.

**Why this is a decision and not a caption.** A rename that reached the schema would be a table rename
and a permission-code rename — the first is a data-migration under the global hard rule, the second is
exactly D-45's drift arriving from the opposite direction. **A display label is free; a rename is a
migration.** The two are separated here so that a future reader finding `/students` under a heading
that says something else knows it is the decision rather than the leftover.

**Consequences.**
- *Positive*: The product reads correctly for a roster that is no longer only students, at the cost of
  one label file.
- *Negative*: Vocabulary now differs between the UI and the code, permanently. Anyone searching the
  codebase for "Academic Community" finds one constant.
- *Neutral*: `Email` and `Time Out` columns landed in the same commit; both render fields that were
  already on their DTOs and neither adds a query.

---

### D-67: Two attendance rulings are **recorded but not built** — Q13's "Both" average is a stated assumption, and Q11's last-tap-out-wins is not implemented

**Q13 — what "average duration" means for a `TimeInOut` event.** The ruling is: **average over
complete In/Out pairs only**, with a **"Without Time Out" count shown beside it** so the denominator is
never hidden. A student who tapped in and never out is excluded from the average and counted in the
adjacent figure.

> **⚠ This is a STATED ASSUMPTION, not a QA answer.** QA has not ruled on it. It is written down here
> so that it is an assumption on the record rather than an arithmetic choice buried in a component —
> the failure mode being an average that silently means one of two different things depending on who
> implemented it. **It is also not built.** Nothing in `web-admin/src` renders "Without Time Out"
> today; the `Time Out` column exists, the pairing arithmetic does not.

**Q11 — last-tap-out-wins is NOT built.** The ruling is to **ship the display and send three questions
back to QA**. The `Time Out` column is spread into the `TimeInOut` grid's column array rather than
rendered conditionally, so a `Single` event's five columns are unchanged in content and order — QA
asked for those to be untouched, and the cheapest way to keep that promise is to leave nothing to
hide. A student who tapped in and not out shows the grid's ordinary absent-value dash: an ordinary
state, not an error.

> **Q11 collides with `deviceTapId`, and that is why it is a question rather than a change.** The tap
> flow is idempotent by `deviceTapId` and ADR-004's D-31/D-33/D-34 build the offline-queue guarantees
> on top of it — D-34 in particular gives a check-out its **own** idempotency key and its **own**
> filtered unique index, so each half of a pair is independently retryable. A "last tap out wins" rule
> rewrites a recorded check-out, which is a different statement from recording one, and the interaction
> with the existing keys and with D-36's never-rewrite-`tappedAt` rule has not been worked out. It is
> recorded in `project_eams_qa_404_rulings` and is not decided here.

**Consequences.**
- *Positive*: The data that was already being recorded is now visible, without committing to arithmetic
  nobody has approved.
- *Negative*: An assumption is on the record with no code and no test behind it, so the next
  implementer can still choose differently unless they read this.
- *Neutral*: Three questions are owed to QA and are listed in the Follow-Ups.

---

## Drift from the Technical Plan

### §4 — the plan defines no classification concept at all

**Two new tables, one new column, and no contradiction.** A grep of the Technical Plan for
*classification*, *category*, *friar*, *personnel*, *C2B2* and *CFI* returns nothing, so §4's ERD is
silent rather than different. This is **the class of drift ADR-001 exists to register** — additive
structure the plan does not define — and it is registered the same way ADR-001 D-5 registered `TermId`
and `SkippedRows`. It is **not** the class ADR-001 D-2 and D-3 registered, where the plan said
something and we did otherwise.

Migrations are `20260915060630_ClassificationsAndAssignments` (two `CreateTable`s, one alternate key,
five indexes, three check constraints) and `20260916063403_ClassificationReportedRosterValue` (one
additive nullable column). **No `DROP`, no `ALTER COLUMN TYPE`, no rename, no backfill** — the global
hard rule is not engaged.

### §6.2 — five student reads gain a member; three new route families appear

`GET /students`, `GET /students/{id}`, `GET /students/by-card/{cardUid}` and the two write responses
all carry `classifications`. New: `GET /cards`, `/classifications` (list/get/create/rename/retire/
delete/merge) and `/students/{id}/classifications` (list/assign/clear). **No existing route, verb or
request body changed.** Under ADR-004 **D-38** the generated document is the contract, and it was
regenerated with the committed half.

### §7.1 / §11 — the permission map is untouched

Three controllers, zero new codes. See D-65. This is D-45's rule applied prospectively rather than as a
correction.

### §10.2 / §10.4 — the import profile grows four rows and a version

D-59. The versioning mechanism is ADR-001 **D-4**'s and is unchanged; this slice is the second thing to
use it after D-43's RFID correction, and the "a version 2 batch classifies nobody" behaviour is D-4's
guarantee rather than a gap.

### ADR-001 D-2 — cited, not amended

D-55 applies D-2's *reasoning* to a new column that was never built. D-2 itself is untouched and its
`Course`/`YearLevel`/`Section` cache is unaffected. Nothing here writes to those columns.

### ADR-001 D-3 — made visible, not changed

D-64's route exists in the shape it does *because* of D-3's filtered uniqueness. The index, the rule
and the reissue semantics are unchanged.

### ADR-001 D-5 / ADR-002 D-10 — extended additively

D-62. New warning codes are members of an open set those decisions created. The precedence **reorder**
is the part that is not purely additive and it is called out there.

### ADR-004 D-40 — the caution is honoured, not overridden

D-65. D-40 declined a *removal* from a mobile-consumed body; this is an *addition* to one. The
distinction is the whole justification and is stated in D-65 so it is not read as precedent.

### ADR-004 D-54.8 — its save split is where D-61's defect lives

The split is still correct for the reasons D-54.8 gives. What D-54.8 could not anticipate is durable
state on a *different table* that gates a warning written on the far side of the boundary. D-61 records
the interaction; D-54.8 is not amended.

### The repo `CLAUDE.md` — two statements in it are now wrong

*"The seed is all-or-nothing — it returns early if a `School` row exists."* Two steps now run above
that return (`SeedTermAsync`, and `SeedClassificationsAsync` per D-57). And the hard-delete-by-default
convention no longer holds for `Classifications` (D-58). **Both are deliberate and both are listed as
Follow-Ups**, because a project rule that is silently false is worse than one that is absent.

## Accepted Context (not drift)

- **Implementation status.** This ADR records decisions; it does not certify that all of them are
  built. That is ADR-001's line and ADR-004's, and it applies here.

  **Landed and committed** (`03eed52`, `c885857`, `6c79588`, `033c1f9`): the vocabulary table, the
  junction, the composite FK and alternate key, the check constraints and indexes, the retire/merge/
  tombstone lifecycle, the eight-value seed above the early return, `GET /cards`, the classification
  admin CRUD, the student-classification write routes, `StudentDto.Classifications` on all five reads,
  the deadlock-as-409 handling and the `ExecuteUpdate` unwrap fix, the "Academic Community" labelling,
  and the `Email` and `Time Out` columns.

  **Landed but uncommitted** at the time of writing: `RosterClassification`, the four `*_CATEGORY`
  template columns, `BuiltInVersion` 3, the five warning codes, the `StudentClassification` fan-out
  type, the precedence reorder, `ReportedRosterValue` and its migration, the warn-once rule, the
  per-axis identity-conflict fold, and `ClassificationAxisUnknown` with its post-loop filter.

  > **✅ LANDED, later the same day — and it delivers less than this document first claimed.** The
  > note that stood here said the D-61 fix was outstanding. It was: the orchestrator's brief asserted
  > a fix that had been *approved* but never dispatched, and JoseArch correctly refused to describe
  > code it could not see. The fix has since landed. `RowLedger` gained a `DeferUntilStaged` hook and
  > `ResolveClassificationsAsync` hands the memory write to it, so the write flushes in the **same
  > transaction** as that row's `WarningCode`/`WarningMessage` rather than in the earlier fact-pass
  > save. A death before that chunk commits now leaves **neither** on disk, and the next run
  > announces.
  >
  > **⚠ What it does NOT do, which matters more than what it does.** The fix delivers *prevention* —
  > the corrupt state can no longer be created. It does **not** deliver *recovery*: a row that already
  > carries an orphaned memory stays silent for ever, and is undetectable by construction. That is not
  > an oversight, it is a property of the schema. `StudentClassification` carries only `StudentId`,
  > `ClassificationId`, `Axis` and `ReportedRosterValue` plus audit timestamps, and the memory write
  > deliberately does not bump `UpdatedAt` — so the state left by a crash is **byte-identical** to the
  > state left by a disagreement that was correctly reported once. One of those must warn and the
  > other must stay silent, and no function of (assignment row, file value) can return both answers.
  > Recovery needs a second durable signal the schema does not carry; the cheapest honest shape is an
  > additive nullable `ReportedBySisImportBatchId` honoured only for a batch that reached a terminal
  > status. **Not built, deliberately** — see the next paragraph for why that is defensible rather
  > than merely cheaper.
  >
  > **Why prevention is sufficient here and would not be later.** No database can carry an orphaned
  > memory, because none has ever run this code: `git ls-tree -r main` finds no classification
  > migration, the slice has never been on `main`, and Task 5 was uncommitted when the fix landed.
  > Recovery would be solving for a state that cannot exist. **That argument expires the moment this
  > ships.** Once a production database has run an import under this code, a crash in any *future*
  > window of the same shape becomes unrecoverable, and the reasoning above stops applying — so a
  > reader meeting this after release must not reuse it.
  >
  > The test that proved the defect was re-pointed at the co-location property the fix actually
  > delivers, rather than deleted, and is negative-controlled against reverting the deferral.

- **Every population in this document comes from one file.** `Personnel.xlsx`, sheet `Report`, 21,497
  rows, sampled once. The eight values, the 34 uncategorised, the three dual-category people, the 22
  rows the `720000` rule misfiles, the 313 rows QA's two-value vocabulary would have warned on — all
  of it is that file. Nothing here is measured against any other export, and no export from the school
  containing the four category columns has ever been seen.

- **`NAP` is 292 and also 271 depending on the question, and both are right.** 271 counts everyone the
  personnel column says `NAP` about including the 2 dual `NAP`+`STUDENT` rows; 269 is the
  `NAP`-**only** stratum, which is what a cross-tabulation prints. An earlier draft said 269 by leaving
  the dual rows out. **Any tally that partitions rows undercounts every value the 3 dual rows touch**,
  which is the whole premise of the axis model arriving as an arithmetic trap.

- **Performance is unmeasured at roster scale.** The classification pass adds a fourth inlined
  `IN`-list to an importer that already issues three, because `SqlServerCompatibilityLevel` is pinned
  for SQL Server 2012 and EF must not emit `OPENJSON`. Students this run *created* are excluded from
  the list — a provably-empty lookup — which is the only mitigation applied. None of the four has been
  run against the real 21,497-row file.

- **`ANT` is still an open QA question.** What it abbreviates is unknown. **Its axis is not** — it is a
  `PERSONNEL` value because that is the column it appears in, and nothing about the abbreviation is
  needed to place it.

## Consequences (overall)

### Positive
- The roster answers "what kind of person is this" from the source's own answer, with **zero misfiles**
  on the only real file anyone has, against 22 for the written rule it deviates from.
- Three people who are two things stay two things, and 34 people who are nothing stay nothing — both by
  construction rather than by care.
- An axis-mismatched junction row is **unwritable at the database**, not merely unwritten.
- There is no operation in this product that silently uncategorises a population: no hard delete, no
  cascading delete, no import overwrite, no blank-cell clear.
- A back-office correction survives every subsequent import for ever, and the resulting permanent
  disagreement is announced **once** rather than on every run.
- The one fault in this pipeline that was silent in both directions now announces itself on every row
  it affects — and the two faults left silent are recorded as decisions rather than discovered later as
  defects.
- Three new controllers were added with **no change to the RBAC grant matrix**, which is D-45's lesson
  applied before the mistake rather than after it.
- ADR-001 D-2's failure mode was recognised *before* the column existed. That is the first time on this
  project the lesson has been available at zero migration cost.

### Negative
- **Task 5 is "ready", not "delivered".** The school's routine export carries **none** of the four
  category columns, so an import from them classifies nobody — and says nothing, correctly, per D-59.
  There is also **no admin surface anywhere for authoring a profile version** (verified: no profile
  endpoint in `EAMS.Api`, no profile call in `web-admin/src/api.ts`), so adapting to the client's own
  spelling of the headers requires **a developer with database access**. That is the honest status.
- **The D-61 defect is fixed, but only in the prevention direction.** The corrupt state can no longer
  be created; a row that already carries an orphaned memory stays silent for ever and is undetectable,
  because the post-crash row is byte-identical to a correctly-reported-once row. That was accepted only
  because no database has ever run this code — **an argument that expires the day this ships**. See the
  box at the end of D-61.
- **First-write-wins makes the importer non-authoritative for a field it writes**, which contradicts
  how it treats every other dimension. It is the decision in this slice most likely to be reversed by
  someone who has not read this document.
- The warning precedence reorder changes the headline code on rows whose data has not changed.
- Five §6.2 bodies changed in one slice, one of them consumed by a developer we do not control and have
  not told.
- Two statements in the repo `CLAUDE.md` are now false.
- **Six documents to read together** (§4, ADR-001…ADR-005). ADR-003 called the consolidation due at
  four, ADR-004 said five and called it overdue, and this is six.
- A deviation from a **written client answer** (QA Q3) is load-bearing in the pipeline and QA has not
  been told.

### Neutral

> **Two known-and-accepted failure modes, found at the 2026-09-17 review gate and logged at JJ's
> direction rather than fixed.** Both are **loud** — they fail a batch with an error rather than
> quietly producing a wrong answer — which is why they were judged loggable where the silent ones in
> D-63 were not. Recorded here with their triggers so nobody has to re-derive them.
>
> **(i) A back-office assignment landing mid-import fails the whole batch.** The classification pass
> snapshots `existing` once at its start (`SisImportService.cs:1729-1734`) and inserts unguarded
> (`:1872-1873`). `PUT /students/{id}/classifications/{id}` writes the same `(StudentId, Axis)` slot.
> A registrar assigning Pedro a Personnel classification forty seconds into a 21,497-row import hits
> `UX_StudentClassifications_Student_Axis` (error 2601) at the fact-pass `SaveChangesAsync` (`:393`),
> which carries no recovery filter — `ExecuteAsync` marks the batch `Failed`, rethrows, and the run is
> discarded. A retry succeeds, so it is recoverable, but loud and expensive.
>
> **What makes this newly reachable is D-60 itself.** First-write-wins exists precisely because the
> back office and the importer are *both* legitimate writers of this table. Every other
> importer-written table has effectively one writer, which is why this shape has not appeared before.
> The fix, when it is wanted, is to catch `SqlServerErrors.IsUniqueViolation` around the insert and
> treat it as the "already holds something" case — i.e. exactly the first-write-wins rule, arriving a
> few milliseconds later than expected.
>
> **(ii) An over-long file cell kills the batch with an error naming neither row nor column.**
> `category.Value` is `RosterText.Clean(cell)` — trimmed and whitespace-collapsed, **not
> length-checked** (`:1862-1866`) — while the column is `nvarchar(100)` (`EamsDbContext.cs:726-731`).
> Matching goes through `AcademicKey.Normalize`, which strips every non-alphanumeric character, so a
> cell of `N-A-P` padded with a hundred further separators normalizes to `NAP`, matches the vocabulary,
> reaches the conflict path, and assigns a >100-character string to a 100-character column. Because
> D-61 moved that write into the fan-out chunk save, the resulting SQL Server 2628 now kills the batch
> **mid-fan-out**, deterministically, on every retry.
>
> **This one is inconsistent with a rule this repo already wrote down.** `AcademicKey.IsWithinLength`
> exists, and its comment reads *"Checked here rather than left to SQL Server, whose truncation error
> (2628) names neither the row nor the column."* One guard — clamp the memory, or skip writing it —
> restores consistency. Contrived to reach; deterministic once reached.

- Decision numbering remains continuous: ADR-001 D-1…D-6; ADR-002 D-7…D-11; ADR-003 D-12…D-21;
  D-22…D-46 shipped-but-unwritten and D-47…D-53 proposed, both tracked in `README.md`; ADR-004 **D-54**
  and its parts; ADR-005 **D-55…D-68**. A future ADR continues from **D-69**.
- The consolidating ADR moves to **ADR-006**, for the third time. `README.md` is the amendment; ADR-003
  and ADR-004 are immutable and will go on pointing elsewhere.
- No existing route, verb or request body changed. The generated OpenAPI document needs no regeneration
  for the warning codes.
- Nothing in `Students`, `Enrollments` or `StudentTermRecords` changed. The classification model sits
  beside the academic layer rather than inside it.

## Open Questions

Things this ADR deliberately does **not** decide.

1. **Can one category name legitimately mean two different things on two axes?** D-58's
   `UX_Classifications_SchoolId_NameKey` hedge assumes not. **One question to the registrar**, exactly
   like ADR-002 D-11's course-code question — and it has been open the same way.
2. **What does `ANT` stand for?** Open with QA. Its axis is settled.
3. **Q11 — does a later tap-out replace an earlier one?** And what does that do to `deviceTapId`
   idempotency (D-31/D-33/D-34) and to D-36's never-rewrite-`tappedAt` rule? Three questions, owed to
   QA.
4. **Does QA accept D-56's deviation from their Q3 answer?** The code already assumes yes.
5. **Who authors a profile version when the client's headers differ?** Today: a developer with database
   access. Whether that becomes an admin screen, a seeded second version, or a documented SQL runbook
   is undecided — and it is what stands between "ready" and "delivered".
6. **Should `ReportedRosterValue`'s warn-once mechanism generalise?** D-61 argues explicitly that it
   should not. If a fifth code ever wants one, that argument is the thing to re-examine first.
7. **Does the classification pass survive the real 21,497-row file?** Four inlined `IN` lists, none
   measured.

---

## D-68 — A seeded vocabulary value cannot be deleted; it is retired instead

**Added 2026-09-17, after the review gate.** D-58 records that a classification cannot be hard-deleted
once anything references it. This decision covers the case D-58 does not reach: a seeded value that
**nothing** references.

**The problem is an interaction neither D-57 nor D-58 noticed.** `DELETE /classifications/{id}` succeeds
when a row has zero assignments and zero tombstones — which, on a fresh install before any import, is
true of all eight seeded values. And D-57's seed guard is **per row, keyed on `NameKey`**, so it re-adds
any seeded name it does not find. An administrator who deletes `USA FRIARS` ("we have no friars") finds
it back in their picker after the next restart or deploy, with nothing anywhere explaining why. Each
decision reasons soundly alone; together they produce a delete that silently un-does itself.

**Decision: `DELETE` on a row whose `NameKey` is one of the eight seeded keys returns `409` with
`errorCode = "SeedProtected"`**, and a detail naming `PATCH /classifications/{id}/active`. The guard sits
**after** the tombstone and in-use checks, so a referenced seeded row still answers the more informative
`InUse`. `ClassificationSeedValues.IsSeededKey` is keyed on `NameKey` over the seed's own set, so the
guard and the seed cannot drift apart.

**Why refuse rather than narrate or accept.** The two alternatives — return 200 and warn that it will
come back, or accept the resurrection and document it — both leave the product doing something nobody
wants and merely describe it. Retiring delivers what the operator actually asked for: withdrawn from
every picker, no new assignments, existing holders kept. And the seed already leaves a retired row alone
for ever (D-57), so retiring is durable in a way deleting is not. Recording a *deletion* durably would
mean storing the absence of a row — a migration, to support an operation retirement already covers.

**Deliberately not gated on hosting environment.** The service has no business reading it, and this
project's deployment VM runs in `Development`, so the seed does run there.

**Consequences.** A seeded row renamed past its key (`ACAD` → `Academic`) loses protection, and the seed
re-adds `ACAD` beside it — pre-existing D-57 behaviour, unchanged. A rename the key survives
(`SUPERVISORY/MANAGERIAL` → `Supervisory / Managerial`) stays protected. `SeedProtected` is a new member
of `ClassificationWriteOutcome`; the controller's total-function switch would have thrown without the new
arm, which is the check that caught it. **No contract regeneration needed** — `errorCode` is an
unenumerated string in `docs/api/openapi.json`, verified, same as `warningCode` under D-62.

**Two refinements landed at the same gate and are recorded where they belong rather than as new
decisions:** the `WarningMessage` join now orders by `WarningPrecedence` so the message matching
`WarningCode` is never the one the clamp destroys (**D-62**, which previously claimed nothing was lost —
it was, up to 614 characters, measured); and `ClassificationUnavailable` now branches its remedy for a
merge tombstone, naming the survivor to point the export at, because reactivation is refused and a
same-name replacement collides (**D-63**; this closes the hole in D-61's "actionable and self-clearing"
asymmetry argument for that sub-case).

## Follow-Up Actions

Ordered by consequence, not by effort.

- [x] **Land the D-61 fix** — **DONE 2026-09-17.** `RowLedger.DeferUntilStaged` holds the memory write
      until the row is staged, so it flushes in the same transaction as that row's `WarningCode`; a
      crash leaves **neither** written. Negative-controlled as required: reverting the deferral to the
      direct assignment made the save-boundary assertion fail with `Assert.Contains() Failure` — the
      save carried only `["StudentId"]` and no `WarningCode` — then the deferral was restored and the
      file's hash confirmed back to baseline.
- [ ] **Decide recovery before this ships.** The fix above prevents; it does not recover. An orphaned
      memory already on disk stays silent for ever and cannot be detected, and the "no database has ever
      run this code" argument that made that acceptable **dies on release**. Cheapest honest shape: an
      additive nullable `ReportedBySisImportBatchId`, honoured only for a batch that reached a terminal
      status. **Whoever ships this slice owns this decision.** This is the item in this ADR whose failure
      mode is permanent silence.
- [ ] **Pin the D-62 precedence order with a test.** The reorder changes the headline `WarningCode` on
      rows that have nothing to do with classification, and nothing today would notice a careless insert.
- [ ] **Guard the unique-violation race** between a running import and a concurrent back-office
      assignment — catch `SqlServerErrors.IsUniqueViolation` around the insert and treat it as the
      "already holds something" case. Logged at the review gate, not fixed. See Neutral (i).
- [ ] **Length-guard `ReportedRosterValue`** before writing a file cell into `nvarchar(100)`, for
      consistency with `AcademicKey.IsWithinLength`'s own stated rule. Logged, not fixed. See Neutral (ii).
- [ ] **Correct two now-false statements in the repo `CLAUDE.md`**: the seed is no longer all-or-nothing
      (D-57), and classifications have no hard delete (D-58). That file is loaded into every session on
      this repo, so a wrong line there propagates further than a wrong line anywhere else.
- [ ] **Ask the registrar one question**: can a single word ever name categories on two different axes?
      A "no" retires the `UX_Classifications_SchoolId_NameKey` hedge in D-58 permanently. This is the
      same one-question retirement path ADR-002 **D-11** has carried unasked since July — a second hedge
      now waiting on a second unasked question is a pattern, not a coincidence.
- [ ] **Tell QA about the D-56 deviation** — their Q3 `720000` rule is demoted to a report-only
      fallback, measured against 22 misfiled rows. An approved deviation nobody relays becomes a defect
      report in three months.
- [ ] **Send QA the three Q11 questions** and the D-67 "Both"-average assumption for ruling. Per the
      SOP, *vaulted is not sent* — track them as drafted-but-unsent until they are actually in front of
      QA.
- [ ] **Ask the registrar the D-58 name-uniqueness question.** One question, and it retires a hedge
      before any colliding data can exist.
- [ ] **Tell the mobile developer that `GET /students/by-card/{cardUid}` gained an additive
      `classifications` member.** It is additive and should be harmless; D-40's caution is about not
      finding that out afterwards.
- [ ] **Correct the two now-false statements in the repo `CLAUDE.md`**: the seed is no longer
      all-or-nothing, and `Classifications` has no hard delete.
- [ ] **Build or document the profile-authoring path** (open question 5). Until then Task 5 is "ready",
      not "delivered", and the status line should say so wherever it is reported.
- [ ] **Pin the D-62 precedence order with a test** that fails if a code is inserted without a
      deliberate rank — the reorder changes what operators see on rows unrelated to classification, and
      nothing today would notice a careless insert.
- [ ] **Measure the importer's four inlined `IN` lists against the real 21,497-row roster** (open
      question 7), which is the same "two numbers in this design are guesses" debt ADR-004 opened.
- [ ] **Decide whether D-63's two silent neighbours stay silent**: the RFID-column collapse (logged,
      not fixed, because it touches the tap path) and the second column claiming a taken axis
      (deferred). Both are recorded; neither has an owner.
- [ ] **Write ADR-006 as the consolidating ADR** superseding 001–005 and absorbing D-22…D-46 — deferred
      for the **third** time. The chronology is still load-bearing, and it is now six documents.
- [ ] Carried forward, unchanged by this ADR: ADR-004's full Follow-Up list (the two-school D-54.3 test,
      the D-54.4 concurrency pin, the `202`/`409` contract finish, the sweep threshold and chunk size,
      the `IsTerminal` pinning, the enqueue-refused status code, the D-54.3 feedback memory); ADR-003
      D-21's load-test of the close; ADR-001 D-6's un-stubbing of §11 auth in full; ADR-002 D-11's
      registrar question and the `Courses.CollegeId` widening deadline; D-4's mapping-version retention;
      the §14 retention policy for `SisImportRowEntities`.
