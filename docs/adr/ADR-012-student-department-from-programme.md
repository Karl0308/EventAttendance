# ADR-012: A Student's Department Is Its Programme's College, Derived at Read Time

**Status**: Proposed (2026-10-24). Awaiting JJ's read-through; JJ then flips it to `Accepted`.
> The *decisions* below are JJ's (2026-10-24, answering QA's "BACKOFFICE Task 4: Add a column of
> Department and Organization for Student's Display; Department is from the Student's Program"). This
> *document* writes them down and has not yet been read by him. The register's rule holds: JoseArch
> recommends, JJ accepts. **Do not self-accept.**

**Date**: 2026-10-24
**Deciders**: JJ; JoseArch (Team X)
**Relationship to other ADRs**: **Builds on ADR-011 (D-81, D-83)**, which is also `Proposed`. It adds one
field after D-81's `organization` and takes D-83's export from 11 to 12 columns. **Amends by reference**
ADR-011 **D-83** (header list) and restates, unchanged, ADR-001 **D-2** (the single-valued `Course` cache)
and ADR-007 **D-69/D-70** (the audience matcher). No schema, so no ADR-001 drift. **ADR-006 remains
reserved** for the consolidating ADR.

> **Numbering note.** D-84…D-87 are used nowhere in the repository (code, tests or docs; checked
> 2026-10-24). **D-84 is the last number of the unregistered Phase 6 auth draft's range** (MDVault #263,
> D-60…D-84), as D-76…D-83 are; D-85 onward is clear of it. ADR-009's numbering note covers that overlap.
> This document follows the registered sequence and does not try to resolve it.

## Context

QA asks for **Department** and Organization on the student display, with Department "from the Student's
Program". Organization shipped in ADR-011. Five facts decide how Department is built:

1. **`Student.Course` holds the programme's display code.** `SisImportService.RefreshStudentCacheAsync`
   writes the row's `ProgramCode` into it (ADR-001 D-2's cache, single-valued, ADR-002 D-9). It is not
   normalized, so `BS-IT` and `BSIT` are both possible values for one programme.
2. **A programme always has a college.** `AcademicProgram.CollegeId` is required (the roster carries
   `COLLEGE_NAME` on every row), and programmes are unique on `UX_Programs_SchoolId_CodeKey`, where
   `CodeKey = AcademicKey.NormalizeOrUnspecified(Code)`. The section-only import (ADR-010) still reads
   `PROGRAM` and `COLLEGE_NAME`, so the table keeps filling.
3. **EF cannot translate `AcademicKey.Normalize`**, so `Course` cannot be joined to `CodeKey` in SQL
   without a stored key column.
4. **"Department" already means two other things.** The `Department` event audience
   (`AudienceResolution.BuildQueries`) matches students on `s.Course` = the selected value, so for students
   a "department" there is a **programme code**. Clearance (`ClearanceService`) returns `Department` =
   `student.Course` and `College` = the current-term `StudentTermRecords.College.Name`.
5. **Technical Plan has no student department.** The word does not appear in the plan at all; §4.3
   `Students` and §6.2 define nothing of the kind.

## Options Considered

- **(Chosen) A: derive at read time.** Normalize the distinct `Course` values in memory, resolve them to
  `College.Name` in one set-based query, map. No column, no migration, no import change, and the value
  cannot go stale against the programme table.
- **B: a stored `Students.Department` column, filled by the cache refresh.** Rejected. A migration, a
  backfill that only a re-import performs, one more name in `DerivedAcademicPropertyNames`, and a second
  copy of a fact the `Programs` table already holds.
- **C: a stored normalized-course key column, so the join runs in SQL.** Rejected for now. A migration to
  save a query whose input is a page's (or a file's) distinct programmes, a handful of values. It is the
  shape to reach for if a Department **filter** is ever wanted (D-85).
- **D: reuse Clearance's source (current-term `StudentTermRecords.College`).** Rejected by the ticket's
  wording ("from the Student's Program"). It is null for any student with no current-term record, and it
  can disagree with the `Course` cell it would sit beside in the same grid row.
- **Baseline: relabel `Course` as Department.** Rejected. It is a programme code, which is the very
  meaning clash this ADR records (D-87); QA asked for the department the programme belongs to.

---

## Decision

### D-84: Department is the programme's college, derived when the student is read

- **Department = `College.Name` of the `AcademicProgram` whose `(SchoolId, CodeKey)` equals the
  student's `(SchoolId, AcademicKey.Normalize(Course))`.** Implemented in `StudentService.DepartmentsAsync`,
  `DepartmentOf` and `ProgramKey`.
- **No column, no migration, no import change.** Nothing is written, so `DerivedAcademicPropertyNames` and
  the cache-refresh guard are untouched.
- **Null in three cases**, all deliberate:

  | `Course` | Why null |
  |---|---|
  | null, empty, blank or punctuation-only | Its key would be `AcademicKey.Unspecified`, where *every* blank-coded programme files, so it names none in particular |
  | `(section)` or `(unspecified)` stored verbatim | A placeholder sentinel. Checked **raw, before normalizing**, because `(section)` normalizes to `SECTION`, which a real programme could be coded |
  | Names no programme in the student's school | Nothing to resolve to. Never guessed |

- **Keyed by school as well as code.** `UX_Programs_SchoolId_CodeKey` is per tenant, and with no tenant
  pinned (ADR-001 D-6) a read can span schools; a code-only key would pick an arbitrary college.
- **Set-based, never per row.** One programmes query per list page, one per export file, one per single
  read or write response. The `IN` lists are the distinct school ids and keys involved, which stays small
  under compatibility level 110's literal inlining.
- **It follows the programme table as it stands at read time, and the single-valued `Course` cache as it
  stands** (ADR-001 D-2). A student in two programmes shows the one the cache shows.

### D-85: Contract, display only

- **`StudentDto` gains a nullable `Department`, last in the record** (after D-81's `Organization`). It
  reaches every response that carries a `StudentDto`: list, get, create, update, delete, and the
  device-authenticated `GET /students/by-card/{cardUid}`.
- **Read-only.** `StudentWriteRequest` is unchanged; a value echoed back on a write is ignored.
- **No Department filter** on `GET /students` or the export. A filter would need option C's stored key to
  run in SQL; it is not asked for.
- **A `docs/api/mobile-changes.md` entry (2026-10-24):** additive, no release needed unless the client is
  strict. `docs/api/openapi.json` changes by one schema field; it is generated (D-38).

### D-86: Export, Department before Organization

- **`GET /students/export.csv` gains `Department` immediately before `Organization`.** Headers, in
  order: Student No., RFID UID (active), Last Name, First Name, Middle Name, Email, Course, Year Level,
  Section, **Department**, Organization, Status. **12 columns**, amending D-83's list of 11.
- Resolved by the same lookup as D-84, once over the file's distinct programmes, and written through the
  same quoted, formula-neutralized `EventReportCsv` path. A null department is an empty cell.
- **The grid gets the column in the same position, before Organization** (FE phase in progress at the
  time of writing).

### D-87: "Department" now means three things, and only the new one moves

Stated plainly, because the product now uses one word for three different values:

| Where | Field | What it holds for a student |
|---|---|---|
| Event audience, `AudienceType.Department` (and `Custom` `departments`) | the selected values | **The programme code** (`s.Course`), e.g. `BSIT`. The options list offers student `Course` values merged with personnel `Department`s |
| Clearance (`ClearanceService`) | `Department` | **The programme code** (`student.Course`) |
| Clearance | `College` | **The current-term record's college** (`StudentTermRecords.College.Name`) |
| Students list, get, writes, by-card, export (this ADR) | `department` | **The programme's college** (`Programs → College.Name`) |

- **The audience is deliberately not changed** (JJ, 2026-10-24). Changing what `Department` matches would
  change the meaning of every stored Department and Custom definition and move live denominators
  (ADR-007 D-70). QA is told instead (R1).
- **Clearance is deliberately not changed.** Its `College` and the new `department` can disagree (R2); a
  follow-up ticket owns reconciling them.
- *For completeness:* the audience builder's proposed `College` field (D-50) resolves through
  `StudentTermRecords.CollegeId`, the same source as Clearance's `College`, not this ADR's.

### What this amends, by reference

- **ADR-011 D-83.** Export header list 11 → 12 columns (D-86). Everything else in D-83 stands.
- **ADR-011 D-81.** Restated: `Organization` stays where it is; `Department` follows it.
- **ADR-001 D-2.** Restated, unchanged: the new value is derived *from* the cache and is never part of it.
- **ADR-007 D-69/D-70.** Restated, unchanged: the matcher is untouched.

### Technical Plan drift

| Plan § | Drift | Kind |
|---|---|---|
| §6.2 Students (and §4.3 by implication) | Student responses carry a derived `department` the plan does not define anywhere. No column | Additive (one response field, no schema) |

---

## Consequences

### Positive
- QA's ask is met with no migration, no backfill and no import change. Rollback is a plain revert.
- The value cannot go stale: it is read from the programme table every time.
- `Department` agrees with the `Course` cell beside it, because it is derived from that cell.

### Negative
- One word, three meanings (D-87), recorded rather than resolved.
- One more programmes query per single read and per write response.
- Inherits the D-2 cache's single-valued `Course`.

### Risks
- **R1 (meaning clash, QA).** A tester who reads `College of …` in the grid's Department column and then
  opens an event's Department audience will find programme codes (`BSIT`) there, not colleges. Expected
  behaviour, not a defect. *Mitigation:* say so in the QA note for Task 4.
- **R2 (Clearance divergence).** Clearance's `College` comes from the current-term record; the new
  `department` comes from the cached `Course`'s programme. They differ when the student has no
  current-term record (Clearance `College` is null, `department` is not), when the cache was written by a
  different term's import, or when a programme's college changed. Clearance's `Department` is a programme
  code and will never equal the new `department`. *Accepted;* follow-up ticket.
- **R3 (CSV shape shift).** `Department` is inserted **mid-row**, so Organization moves from index 9 to 10
  and Status from 10 to 11. Any consumer that reads the file by position (a saved spreadsheet formula, a
  script) shifts silently. This is the second shape change to this file in two days (D-83 shipped
  2026-10-23). The file is not re-importable, so no EAMS reader is affected. *Mitigation:* tell QA.
- **R4 (strict mobile parsers).** `StudentDto` is a closed schema (`additionalProperties: false`). A client
  generated from the previous `openapi.json` with strict deserialization fails to parse `by-card` until
  regenerated, exactly as with `organization` on 2026-10-23. Additive and nullable otherwise.
- **R5 (deploy order).** FE before BE, as in ADR-009/010/011. The FE must treat `department` as optional,
  because the old BE omits it.

### Neutral
- The two CSV tests that pin column positions and counts (`StudentExportApiTests`,
  `CsvExportSpaceSplittingTests`) move from 11 to 12 columns and shift their indexes by one.
- Behaviour is pinned in `StudentDepartmentContractTests` (uncommitted at the time of writing): resolution,
  normalized-key matching, every null case, the cross-school guard, every read and write response, one
  programmes query per page, and the export's column position.

## Follow-Up Actions

These are proposals, not authorised by this document. JJ decides.

- [ ] JJ reads and accepts (or amends) this ADR together with ADR-011.
- [ ] Tell QA, in the Task 4 note: R1 (Department means the college here, the programme code in the
      audience picker), R3 (CSV columns shift), and that blank or unknown programmes show an empty
      Department.
- [ ] Open the follow-up ticket for Clearance's `Department` / `College` versus the new `department` (R2).
- [ ] If a Department filter or audience is ever wanted, write a new ADR for option C's stored key first.
- [x] `mobile-changes.md` entry (D-85) added in the same change as the DTO.
