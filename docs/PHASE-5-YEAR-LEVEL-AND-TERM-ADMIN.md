# Phase 5 — Year-level derivation, dynamic event audiences, and term administration

**Status:** Proposed (2026-08-07) · **Supersedes nothing** · Decisions **D-47 … D-53**

This document turns one operator sentence into a build plan:

> *"The import roster is a list of all students for the whole year. So: import, and the year. Then
> on create event — that's where the groupings. Create event, then select year, select course,
> section, etc., or all. It's dynamic."*

The shape divides cleanly, and the division is the whole design:

| | Responsibility |
|---|---|
| **Import** | Load *everyone* for a school year + semester. Stays dumb — no audience decisions. |
| **Create event** | Where *all* grouping happens, dynamically — year, program, section, course, or everyone. |
| **APK** | Reads one event and the students in it. Nothing else. |

Roughly 80% of that already ships. This document is about the 20% that does not, and it is
deliberately explicit about which is which — the fastest way to waste a phase here is to rebuild
something that exists under a different name.

---

## 1. The operator flow, end to end

| # | Step | State |
|---|---|---|
| 1 | Admin creates a **school year + semester** (a `Term`) | ❌ **no route exists** — §5 |
| 2 | Admin uploads **one roster covering every year level** against that term | ✅ ships |
| 3 | Import fans it out into colleges, programs, courses, sections, students, enrolments | ✅ ships |
| 4 | Import **derives each student's year level** from their home section | ❌ **not built** — §3 |
| 5 | Import projects all of it into invitable `StudentGroup` rows | ⚠️ partial — no year group — §3.4 |
| 6 | Admin creates an event and **builds an audience filter** — any fields, any values | ❌ **not built** — §4 |
| 7 | APK pulls **one event** and **the students in it** | ✅ ships — `GET /events/{id}/manifest` |
| 8 | A card taps and resolves to a student | ❌ **no cards are bound** — §6 |

Steps 2, 3 and 7 are done and tested. This phase closes 1, 4, 5, 6 and 8.

---

## 2. What already ships, named precisely

So that nothing below gets rebuilt by accident.

**The term is already the school year + semester.** `Terms` carries `SchoolYear` ("2025-2026"),
`Semester` ("1st Semester") and an operator-authored `Code` ("2025-2026-1"). `IsCurrent` is capped
at one per school by a filtered unique index. Every academic row is term-scoped through it.

**Whole-institution Excel import ships end to end.** `POST /api/v1/sis/import/upload` takes the
`.xlsx` plus a `termId`; `POST /api/v1/sis/import/{batchId}/run` executes it. Rows stage verbatim
before anything is written, per-row outcomes reconcile to
`Inserted + Updated + Failed + Skipped = TotalRows`, and re-running the same file is a proven no-op.
**Nothing in the importer assumes the file is scoped to one cohort** — it never did, which is why
the operator's correction costs nothing here.

**Re-import already updates in place.** Upload a corrected file against the **same** term and
students, enrolments and term records update by natural key. Nothing duplicates.

**Sections, programs, colleges and courses are already invitable.** Every import ends with
`StudentGroupProjection.SyncTermAsync`, which materialises academic structure into ordinary
`StudentGroup` rows marked `Derived`:

| Group | Built from | `Type` |
|---|---|---|
| College | `StudentTermRecords.CollegeId` | `College` |
| Program | `StudentTermRecords.ProgramId` | `Program` |
| Section | `Enrollments` grouped by `CourseOffering.SectionKey` | `Section` |
| Course offering | `Enrollments` grouped by `CourseOfferingId` | `Course` |

So *"invite BSCRIM 2-A"* works today: `GET /student-groups?sourceType=Derived&termId=…` then
`POST /events/{id}/attendees`. **Year is the only missing axis**, and the picker UI is the only
missing surface.

**The APK's "one event + its students" call ships.** `GET /api/v1/events/{id}/manifest` returns the
event, its groups, and a de-duplicated attendee list with card UIDs, under device-key auth with
ETag revalidation. Its `attendees.length` is contractually equal to the `expected` reported by
`/summary` and `/roster`.

---

## 3. Year level — derived per student, not declared per batch

### 3.1 Why it cannot be declared

An earlier draft had the operator pick a year on the upload form. **That is wrong for a
whole-institution roster**: one file spans every year level, so a single value would mislabel
everyone it did not describe. Year must be resolved **per student**, from the file itself.

`StudentTermRecord.YearLevel` exists as a nullable string, but nothing populates it —
`SisImportService` deliberately leaves it alone, because the export has no year column.

### 3.2 The only available signal, and its trap

The registrar's eighteen columns (`SisRosterColumns.All`) contain no year in any form. The one
column carrying year information is `SECTION_NAME` — and reading it naively is wrong. Real values
from the roster fixtures:

| Section | Shape | The digit means |
|---|---|---|
| `BSCRIM 2-A` | program cohort | **year level** — 2nd year |
| `BSN 1-B` | program cohort | **year level** — 1st year |
| `BSIT 3-A` | program cohort | **year level** — 3rd year |
| `NSTP 2` | subject block | block number — **not** 2nd year |
| `GE 8`, `SSCI 7`, `CHEM 1` | subject block | block number |
| `ROTC` | subject block | no digit at all |

`NSTP 2` and `BSCRIM 2-A` both contain a `2`; only one means "2nd year". A subject block mixes
students from several years, so parsing its digit mislabels every one of them.

This is the same failure mode the codebase already refuses in `Student.Course/YearLevel/Section`
(ADR-001 D-2): **a plausible wrong answer is worse than a null**, because nothing downstream can
tell it apart from a right one.

### 3.3 The rule — **D-47**

A student's **home section** is a section whose key begins with that student's **own program code**.
`BSCRIM 2-A` starts with `BSCRIM`, which is the program on their `StudentTermRecord`. `NSTP 2` and
`ROTC` do not. That test separates the two shapes without a hand-maintained list of subject codes.

```
For each student in the term:
  candidates := their enrolled sections whose SectionKey starts with their own ProgramId's CodeKey
  if exactly one distinct year digit across candidates -> YearLevel = that digit
  otherwise                                            -> YearLevel = null
```

> **D-47 — Year level is derived from the student's program-shaped home section, never from any
> section that contains a digit.** The roster has no year column, and the only derivable signal is
> ambiguous between program cohorts (`BSCRIM 2-A`) and subject blocks (`NSTP 2`). Anchoring on the
> student's own program code separates them without enumerating subject codes, which would rot on
> the first new GE offering. **Ambiguity yields `null`, never a guess** — a student in no year group
> is a visible gap; a student in the wrong year group is an invisible wrong denominator.

**`null` is a first-class outcome.** Students whose only sections are subject blocks get no year and
appear in no year group. They remain invitable by program, section, course or individually. §7 makes
this countable rather than silent.

> **D-48 — A derived `YearLevel` is written only to `StudentTermRecord`, never to
> `Student.YearLevel`.** The `Student` triple stays the ADR-001 D-2 display cache that
> `EamsDbContext` refuses writes to, refreshed only by `RefreshStudentCacheAsync`. Deriving a value
> does not promote it to a join key — every audience and denominator still resolves through
> `Enrollments` / `StudentTermRecords`.

### 3.4 The year-level group — **D-49**

Projection gains a fifth group type so year becomes invitable exactly like a section.

| Group | Built from | `Type` | `SourceKey` |
|---|---|---|---|
| **Year level** | `StudentTermRecords.YearLevel` (non-null) | **`YearLevel`** (new) | the normalised year value |

> **D-49 — Year level becomes an invitable audience by projecting into `StudentGroup`, not by
> teaching `EventGroups` a new target type.** This is the same trade ADR-003 made for sections: the
> alternative is another nullable FK on the hottest reporting path, and every existing audience,
> denominator, freeze and manifest query already understands `StudentGroup`. The cost is one
> `StudentGroupType` constant and one projection block.

Naming follows the existing convention — the term rides in the display name, so a stale `EventGroup`
still reads as the cohort it actually invited: `"2nd Year (2025-2026-1)"`.

**Re-import recomputes year.** Because derivation runs inside the projection, a corrected roster
moves students between year groups on the next run, with no manual step. That is also how a student
progresses between terms.

---

## 4. Dynamic event audiences — a filter builder

Grouping happens **at event creation**, not at import. The operator's requirement:

> *"Can we add groups with this column and this and this? It's dynamic and flexible — with year,
> with course, with sections and other columns."*

So the picker is **not five fixed dropdowns**. It is a builder: add a filter row, choose a field,
choose one or more values, repeat. With D-49 in place this is a **UI and query change only** — no
new event schema, no new join table.

```
Create Event
────────────────────────────────────────────────────────
Name      [ Foundation Day                            ]
Start     [ 2026-09-12 08:00 ]   End [ 12:00 ]

Audience — build a filter, then add
┌──────────────────────────────────────────────────────┐
│  Year Level  is any of  [2 ×] [3 ×]              [×] │
│  Program     is any of  [BSIT ×]                 [×] │
│  Course      is any of  [GE 8 ×]                 [×] │
│                                                      │
│  [ + Add filter ▾ ]   College · Program · Year ·     │
│                       Section · Course               │
│                                                      │
│  → 137 students match          [ Preview ] [ Add ]   │
└──────────────────────────────────────────────────────┘

Attached:
  • Year 2,3 · BSIT · GE 8  (2025-2026-1)       137   [×]
  • BSN 4-A                 (2025-2026-1)        41   [×]
  Total expected                                 178
```

### 4.1 The five filterable fields — **D-50**

| Field | Resolves through | Values come from |
|---|---|---|
| College | `StudentTermRecords.CollegeId` | `GET /academic/colleges` |
| Program | `StudentTermRecords.ProgramId` | `GET /academic/programs` |
| Year Level | `StudentTermRecords.YearLevel` | distinct derived years (D-47) |
| Section | `Enrollments → CourseOfferings.SectionKey` | `GET /academic/course-offerings` |
| Course | `Enrollments → CourseOfferings.CourseId` | `GET /academic/courses` |

**The field list is closed, and that is the point.** A truly open builder over "any column" would
offer `Students.Course`, `Students.YearLevel` and `Students.Section` — the three columns sitting
right on the student entity, which look ideal for exactly this feature and are **wrong for 23% of
students**. ADR-001 D-2 quantifies it: 12 of 52 students sit in more than one section, and a
single-valued column can only name one, so a section filter reading it silently returns a plausible,
non-empty, incomplete answer.

> **D-50 — The audience builder exposes a closed list of five academic fields, each resolving
> through `Enrollments` / `StudentTermRecords`; the `Students` cache columns are unreachable by
> construction.** A generic "filter on any column" builder is the single most likely way to
> reintroduce the ADR-001 D-2 defect, because the wrong columns are the convenient ones. Adding a
> sixth field is a deliberate one-line change here, not an emergent capability of the UI.

Extending the list later (status, gender, has-card) is cheap and expected — each is one entry in the
field registry plus its value source. Deferred now because none was asked for.

### 4.2 How filters combine — **D-51**

Two levels, and they differ:

- **Within one field: OR.** `Year is any of 2, 3` matches 2nd *or* 3rd years.
- **Across fields: AND.** Adding `Program is BSIT` narrows that to 2nd/3rd-year BSIT students.
- **Across `[ Add ]` presses: OR.** Two separate additions invite two separate cohorts.

```
(Year 2 OR Year 3) AND (Program BSIT) AND (Course GE 8)
```

> **D-51 — Values within a field union; fields intersect; additions union.** This is what "filter"
> means in every DataGrid in the SPA, so it needs no explanation in the UI. It also keeps the
> resolver a plain `IQueryable` composition — one `Where` per filter row, one `Contains` per value
> list — with no query language to parse and no user-authored SQL anywhere near the audience.

**A filter row with no values selected is ignored, not treated as "match nothing."** An
empty-but-present row is a half-finished edit, and resolving it to zero students would make the
count collapse while the operator is still typing.

### 4.3 Attaching the result — **D-52**

A built filter like `Year 2,3 + BSIT + GE 8` is **not** an existing `StudentGroup`; it is an
intersection across three. That leaves two ways to attach it, and only one is correct:

| Option | Behaviour |
|---|---|
| **A — attach the constituent groups** | `EventGroups` gets *Year 2*, *Year 3*, *BSIT*, *GE 8*. Audience resolution **unions** its rows, so this invites everyone in **any** of them — **wildly over-counted.** |
| **B — resolve to students, attach individually** | The 137 matched students attach as individual `EventGroup` student rows. Count exact, wire format unchanged. |

**Option B whenever more than one filter row is present.** A single-field, single-value selection
(`Section = BSIT 2-A` and nothing else) still attaches as its group row, preserving the group's
meaning and staying live to re-imports.

> **D-52 — A multi-filter audience materialises to individual student rows at attach time; a
> single-field selection attaches as a group.** `EventGroups` unions its rows, so attaching
> constituents to mean an intersection would silently over-count the denominator — the exact hazard
> ADR-003 D-12 guards. Materialising is the mechanism the close-time freeze (ADR-003 D-13) already
> uses, so it introduces no new concept.

**The trade, which the UI must state:** a materialised audience does **not** track later re-imports.
A student enrolling into BSIT after the event was built is not added retroactively. The attached row
therefore stores the filter that produced it and the timestamp, so the UI can offer *"re-run this
filter — 4 students now match that were not attached"* rather than making the operator remember.

### 4.4 The resolve endpoint

One new read endpoint backs both the live count and `[ Preview ]`:

```
POST /api/v1/events/audience/resolve
{
  "termId": "…",
  "filters": [
    { "field": "YearLevel", "values": ["2", "3"] },
    { "field": "Program",   "values": ["<programId>"] },
    { "field": "Course",    "values": ["<courseId>"] }
  ]
}
→ 200 { "count": 137, "studentIds": [...], "sample": [ …first 25 for preview… ] }
```

`POST` rather than `GET` because the filter set is a structured body, not a query string, and it will
outgrow URL length as fields are added. It is **read-only and idempotent** despite the verb —
resolving never writes. An unknown `field` is `400 UnknownAudienceField`, never silently dropped:
a filter that vanishes is a wrong count nobody sees.

---

## 5. Term administration — **D-53**

### 5.1 The problem

The operator's flow *starts* with "import against a school year and sem". Nothing in the product can
create one. `AcademicController` is read-only by design, the importer takes `TermId` as input, and
`CLAUDE.md` documents the workaround as hand-written SQL. A fresh database gets one from the seed; an
older one has none, and the import page then shows an empty picker and refuses to stage a batch.

### 5.2 The design

A **narrow, additive write surface on terms only** — colleges, programs, courses and offerings stay
read-only, because the importer owns those and hand-authored rows there are silently overwritten by
key matching.

| Verb | Route | Purpose |
|---|---|---|
| `POST` | `/api/v1/academic/terms` | Create a term |
| `PUT` | `/api/v1/academic/terms/{id}` | Edit its display fields |
| `PATCH` | `/api/v1/academic/terms/{id}/current` | Make it the current term |

> **D-53 — Terms get an admin write surface; the rest of `/academic` stays read-only.** A term is the
> one academic row a human authors rather than imports — it has no source in the roster file, and
> every import requires one to exist first. The read-only rule that protects importer-owned tables
> does not apply to a table the importer only ever reads.

**Uniqueness — the operator's "cannot be duplicate."** `Terms` already carries
`UNIQUE(SchoolId, Code)`. The create route surfaces a violation as `409 TermCodeExists` rather than a
500, and the SPA blocks the obvious case client-side. `Code` stays operator-authored and deliberately
un-normalised.

**Deletion is not offered.** A term with a batch imported against it cannot be removed without data
loss, which the global no-DROP rule forbids. Retiring a term means clearing `IsCurrent`.

**`PATCH /current` is its own route** for the same reason `PATCH /events/{id}/status` is: `IsCurrent`
is guarded by a filtered unique index, so moving it is a two-row transaction, not a field write.

---

## 6. Card binding — **from the roster export**

**Nothing about attendance works until this is closed.** No export has carried an RFID column, so no
`RfidCard` rows exist and **every tap today returns `CardNotFound`.**

Binding comes from the roster export. The importer already resolves the RFID column per batch from
the ADR-001 D-4 profile, so the column can arrive under any header with no code change. No in-field
registration endpoint or APK screen is built.

Three things must hold in the first real export — the acceptance criteria for it:

1. **The serial is the card's own UID, not `REGNO`.** Different numbers entirely.
2. **It is text, and leading zeros are significant.** `0012503301` ≠ `12503301`. A cell Excel stores
   as a *number* loses them — the column must be formatted as text
   (`RosterText.FormatNumericCell` exists for this hazard).
3. **The header may be anything.** `RFID` is only the built-in profile's default.

Until that file arrives, `POST /students/{id}/cards` is the only binding route — per-student,
back-office, fine for a pilot cohort.

---

## 7. What this phase deliberately does not do

| Not in scope | Why |
|---|---|
| **Human auth (JWT + RBAC)** | Phase 6. Every non-capture endpoint stays open — keep this API on a trusted network. New term routes carry `[HasPermissionNotEnforced("academic.write")]` so Phase 6 wires them without re-deriving intent (ADR-001 D-6). |
| **Removing students dropped from a re-import** | Confirmed out of scope. Re-import updates and inserts; it never removes. |
| **A year column in the export** | Not required. If the registrar ever adds one, the D-4 profile maps it and it supersedes D-47's derivation without unwinding it. |
| **Materialised audiences tracking re-imports** | Explicitly not tracked (D-52). The UI states it and offers a re-run; a re-add is the remedy. |
| **Filter fields beyond the academic five** | Status, gender and has-card were considered and deferred — none was asked for. Each is one entry in the D-50 registry when wanted. |
| **Saved / reusable audience filters** | A filter is attached to one event. Naming and reusing one across events is a natural follow-up, not this phase. |
| **`YearLevel` as an entity or enum** | Stays a string, like `Status` and `CaptureMethod`. Promoting it is a migration under the no-rename rule. |
| **Recurrence / occurrences** | `EventSchedule` exists; nothing materialises occurrences and the manifest is event-scoped. Unchanged. |
| **Reports (§12)** | Untouched by this phase. |

---

## 8. Build order

Dependency-driven; each step independently shippable.

| # | Step | Where | Blocks |
|---|---|---|---|
| 1 | `POST/PUT/PATCH` term routes + service | `AcademicController`, `EAMS.Application` | 2 |
| 2 | Term admin page (create / edit / set current) | `web-admin` | operator self-service |
| 3 | Home-section year derivation in the projection | `StudentGroupProjection` | 4 |
| 4 | `StudentGroupType.YearLevel` + projection block | `DomainValues`, `StudentGroupProjection` | 5, 6 |
| 5 | `type` / `search` filters on `GET /student-groups` | `StudentGroupsController` | 7 |
| 6 | **Audience field registry** — the closed five, each with its resolver and value source (D-50) | `EAMS.Application` | 7 |
| 7 | `POST /events/audience/resolve` — filters in, count + ids + sample out | `EventsController` | 8, 9 |
| 8 | Audience **filter builder** UI — add/remove rows, multi-select values, live count, preview | `web-admin` | 9 |
| 9 | Attach: materialise on multi-filter, group row on single-field (D-52); store the filter + timestamp | `EAMS.Application`, `web-admin` | — |
| 10 | Unresolved-year visibility (count + list of students with `null` year) | `web-admin` | — |
| 11 | RFID column acceptance test against the first real export | `EAMS.Tests` | live taps |

**Migrations.** Step 4 needs **no schema change** — `StudentGroup.Type` is already a string, and
`StudentTermRecord.YearLevel` already exists as `nvarchar(50)`. Nothing is added, dropped or renamed.
This phase is expected to ship **without a migration**; if one proves necessary, scaffold from
Infrastructure alone:

```bash
dotnet ef migrations add <Name> --project backend/EAMS.Infrastructure
```

**Tests that must exist before this phase is done.**

- `BSCRIM 2-A` for a BSCRIM student derives `YearLevel = 2`.
- **`NSTP 2` never derives `YearLevel = 2`** — the D-47 tripwire, and the reason this phase exists.
- `ROTC` (no digit) and a student whose only sections are blocks both derive `null`.
- A student with conflicting program-shaped sections (`BSIT 2-A` *and* `BSIT 3-A`) derives `null`,
  not an arbitrary pick.
- A `null`-year student joins **no** year group and stays invitable by program and section.
- Derivation writes `StudentTermRecord` only — the `Student` cache guard still trips on a direct
  write (D-48).
- `Year is any of 2,3` **unions** within the field; adding `Program = BSIT` **intersects** across
  fields; the resolved count matches the students actually attached.
- **A student in two sections is counted once** by the resolver — the ADR-001 D-2 tripwire, and the
  reason the builder may not read `Students.Section`.
- A filter row with an empty value list is **ignored**, not resolved to zero students.
- An unknown `field` returns `400 UnknownAudienceField` rather than being dropped.
- A single-field selection attaches as a **group row**; a multi-filter one **materialises** (D-52).
- A materialised audience's `attendees.length` still equals `expected` on `/summary` and `/roster`
  (ADR-003 D-19).
- `POST /academic/terms` with a duplicate `Code` returns `409`, not `500`.
- `PATCH /current` leaves exactly one current term per school.

---

## 9. Decisions registered here

| # | Decision |
|---|---|
| **D-47** | Year level is derived from the student's program-shaped home section; ambiguity yields `null`, never a guess |
| **D-48** | Derived year writes to `StudentTermRecord` only — never to the `Student` display cache |
| **D-49** | Year level becomes invitable by projecting into `StudentGroup` as a new `Type`, not by extending `EventGroups` |
| **D-50** | The audience builder exposes a **closed** list of five academic fields resolving through `Enrollments`/`StudentTermRecords`; the `Students` cache columns are unreachable by construction |
| **D-51** | Values within a field union; fields intersect; separate additions union |
| **D-52** | A multi-filter audience materialises to individual student rows; a single-field selection attaches as a group |
| **D-53** | Terms get an admin write surface; the rest of `/academic` stays read-only |

> These are **proposed**, not accepted. On implementation they fold into ADR-004 with the D-22…D-46
> backlog the ADR index already flags as unwritten. Numbering continues from D-46 so citations in
> code stay unambiguous.
