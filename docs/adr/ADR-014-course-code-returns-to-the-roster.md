# ADR-014: The Course Code Returns to the Roster Import (Code Only)

**Status**: Proposed (2026-10-26). Awaiting JJ's read-through; JJ then flips it to `Accepted`.
> The *decisions* below are JJ's (2026-10-26, on QA's answers to MDVault #651). This *document* writes
> them down and has not yet been read by him. The register's rule holds: JoseArch recommends, JJ
> accepts. **Do not self-accept.**

**Date**: 2026-10-26
**Deciders**: JJ; JoseArch (Team X)
**Relationship to other ADRs**: **Builds on ADR-010 (D-77) and ADR-011 (D-80)**, both `Proposed`; JJ
accepts the three together. **Amends by reference** ADR-010 **D-77**: the built-in profile is course
mode again, with the code only. **Restates, unchanged,** ADR-001 **D-4** (a batch runs under the profile
version it is pinned to), ADR-002 **D-11** (the course-collision hard failure) and D-43 (RFID is its own
column). No schema, so no ADR-001 drift. **ADR-006 remains reserved** for the consolidating ADR.

> **Numbering note.** D-92…D-94 are used nowhere in the repository (checked 2026-10-26). They follow
> D-91 (ADR-013).

## Context

QA answered MDVault #651 (the RFID question ADR-010 left drafted): **(1)** `RFID` stays in the
template; **(2)** `COURSE_CODE` comes back, **code only**, with no `COURSE_NAME`, `UA_FULLNAME` or
teacher columns; **(3)** Task 3 overrides Task 2's 10-column list, so `ORGANIZATION` stays. Four facts
decide the shape:

1. **The `Course.CodeKey` row is the mode switch** (D-77.3). With it, a batch is course mode; the title
   is read only if a `Course.Title` row exists, teachers only if an `Instructor.DisplayName` row does.
   Code-only course mode is therefore already a supported pipeline state, not new code.
2. **Profiles are versioned and lazily written.** `EnsureBuiltInProfileAsync` writes the current
   `BuiltInVersion` on a school's next upload and deactivates the older row. No migration.
3. **The importer only adds.** Nothing in the SIS pipeline deletes an `Enrollment` or `CourseOffering`;
   the one `ExecuteDelete` clears a retried batch's own `SisImportRowEntities`.
4. **`FindCurrentAsync` defers to a newer active version** of the built-in profile (D-43's
   no-older-fallback rule). That shapes rollback (R4).

## Options Considered

- **(Chosen) A: built-in v7 = v6 plus the two `COURSE_CODE` rows.** One version bump, no migration, no
  contract change; the pipeline already reads code-only course mode.
- **B: per-row mode (a blank `COURSE_CODE` cell files the row under the placeholder).** Rejected. It
  makes one batch both modes, contradicts D-77.3 ("section-only is stated by omitting the row, never
  inferred"), and lets a missing cell silently move a student out of every course audience.
- **C: restore v4 (code, title, teachers).** Rejected by QA's answer (2): code only.
- **Baseline: stay on v6.** Rejected. QA asked for the column; by-course audiences stay frozen.

---

## Decision

### D-92: Built-in profile v7 is v6 plus `COURSE_CODE`: course mode again, code only

- **`BuiltInVersion` 6 → 7.** Version 7 is version 6 plus the two rows version 4 had for `COURSE_CODE`:
  `Course.Code` (verbatim) and `Course.CodeKey` (key), both required. **No** `Course.Title`, no
  `Instructor.DisplayName` row. The template has **13** columns: v6's 12 plus `COURSE_CODE`. `RFID`
  (D-43) and `ORGANIZATION` (D-80) stay. Headers are read by name, so column order is cosmetic.
- **Course mode, without a title or teachers.** A course first created by a v7 run has
  `Title = null`; an existing course keeps its title (the importer fills a title only when it is null).
  No instructor or `CourseOfferingInstructor` is written. The template's Instructions sheet returns to
  the course-mode line, "One row per student per class".
- **No migration.** `EnsureBuiltInProfileAsync` writes v7 on each school's next upload; v6 and earlier
  are superseded, not deleted (D-4).

### D-93: v5/v6 batches stay section-only; the placeholder stays

- **A batch pinned to v5 or v6 runs section-only after the deploy** (D-4 working). Its placeholder
  enrollments are not migrated onto real courses.
- **The `(section)` placeholder course and every D-77.5 exclusion stay**: the projection's Course-group
  skip, the D-50 Course filter refusal, the `GET /academic/courses` and `/course-offerings` exclusions.
  They now serve history rather than every new upload.

### D-94: Under v7, `COURSE_CODE` is required on every row and is never downgraded

- **A blank cell fails the row `MissingRequiredValue`** (the existing course-mode check). **A file with
  no `COURSE_CODE` column fails every row** the same way; it is not read as section-only (D-77.3).
- **ADR-002 D-11's `COURSE_COLLEGE_COLLISION` is reachable again**: a code already held by another
  college fails the row. A row that imported under v5/v6 can fail under v7. Do not downgrade it.

### What this amends, by reference

- **ADR-010 D-77.** Point 1 (v5 section-only) and point 6 (course audiences stop growing) end for new
  uploads. Points 2, 5, 10 and 11 stand for the placeholder history. Point 9 (the preview follows the
  pinned profile) now shows courses again.

### Technical Plan drift

None. No table, column or route changes.

---

## Consequences

### Positive
- QA's three answers met with no migration, no contract change and no new pipeline mode.
- By-course audiences (Course groups, the D-50 Course filter) reach newly imported students again.
- The import preview shows a course count again; instructors stay 0, and the FE already hides a
  0-count tile.

### Negative
- Courses created by v7 have no title; any screen that shows `Course.Title` shows nothing for them.
- A row needs a course code now. Files built from the v6 (12-column) template fail every row.

### Risks
- **R1 (mixed history).** A school's sections now sit on the placeholder (v5/v6 uploads) **and** on real
  courses (v7 uploads). Course audiences reach only the second set. A term imported only under v5/v6
  stays unreachable by course until re-imported.
- **R2 (re-importing a v6-era term under v7 leaves stale placeholder enrollments).** The importer only
  adds (Context 3). Each student keeps their placeholder enrollment and gains one per real course in
  the same section. **Section membership does not double**: the projection builds section groups as a
  per-key set, year level reads `Distinct` section keys, and the D-50 Section filter is an `EXISTS`. The
  placeholder offering and its enrollments remain, invisible to every course reader. **A student the
  v7 file moves to another section stays in the old one through the stale placeholder enrollment**
  (D-77.10's double-section shape, now also across modes). Retiring stale enrollments is still the
  separate ticket ADR-010 named.
- **R3 (deploy order and pre-deploy checks).** No contract change and the FE already reacts to counts,
  so this ADR adds no FE/BE ordering. Before the BE deploy: (a) check for an operator-authored row
  `WHERE Version >= 7` on the built-in `NameKey`, as ADR-010's release note did for v5; (b) `Pending`
  or `Failed` batches pinned to v6 run section-only after the deploy, by design; re-upload to get
  course mode; (c) tell QA to download a fresh template (D-94).
- **R4 (rollback is not a plain revert).** After a revert to `BuiltInVersion = 6`, `FindCurrentAsync`
  finds the school's v6 row inactive and **defers to the newer active v7** (Context 4), so every school
  that uploaded after the deploy keeps pinning to v7 and course mode continues. The data-safe rollback
  is a **roll-forward**: v8 = v6's mapping, which `EnsureBuiltInProfileAsync` writes and which
  supersedes v7. The v7 rows stay as history (D-4); deleting them is never the rollback.

### Neutral
- Tests pinning v6's column list and version (`SisImportTemplateTests`, `SisImportOrganizationTests`)
  move to v7. `SisImportSectionOnlyTests` keeps pinning section-only through a v5/v6 profile, and the
  frozen v4 `CourseMappingProfile` stays for title/teacher course-mode tests.

## Follow-Up Actions

These are proposals, not authorised by this document. JJ decides.

- [ ] JJ reads and accepts (or amends) this ADR together with ADR-010 and ADR-011.
- [ ] JJ to confirm D-94: `COURSE_CODE` required on every row (no per-row placeholder fallback).
- [ ] JJ to confirm R4's roll-forward as the rollback path (it also applies to ADR-010/011's bumps).
- [ ] Tell QA: fresh template (13 columns, one row per student per class), R1, R2, and that a v7 course
      shows no title.
- [ ] Pin as named tests: v7 is course mode without title or teachers; a missing `COURSE_CODE` column
      fails rather than going section-only; a v6-era term re-imported under v7 keeps each student in
      one section group.
