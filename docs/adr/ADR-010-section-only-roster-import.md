# ADR-010: The Roster Import Becomes Section-Only (One Placeholder Course per School)

**Status**: Proposed (2026-10-23). Awaiting JJ's read-through; JJ then flips it to `Accepted`.
> The *decision* below is JJ's (2026-10-23, answering QA's "BACKOFFICE Task 2" job order). This
> *document* writes it down and has not yet been read by him. The register's rule holds: JoseArch
> recommends, JJ accepts. **Do not self-accept.**

**Date**: 2026-10-23
**Deciders**: JJ; JoseArch (Team X)
**Relationship to other ADRs**: **Builds on ADR-009 (D-76)**, which is also `Proposed`; JJ accepts the
two together. It takes the built-in profile from ADR-009's version 4 to version 5. **Amends by
reference** ADR-001 **D-2** and **D-4**, ADR-002 **D-11** and the proposed **D-47**
(`docs/PHASE-5-YEAR-LEVEL-AND-TERM-ADMIN.md`). It also touches ADR-001 **D-1**, which none of those
lists: D-1 defines `CourseOfferings` as "course × section × term", and a section-only offering is
section × term behind a placeholder course. ADR-001 and ADR-002 are `Accepted` and **not edited**. The
register in `README.md` records the amendment. Decision numbering continues at **D-77**, as ADR-009
said it would. **ADR-006 remains reserved** for the consolidating ADR.

> **Numbering note.** D-77 is used nowhere in the repository. It falls inside the unregistered Phase 6
> auth draft's range (MDVault #263, D-60…D-84), exactly as D-76 did. ADR-009's numbering note covers
> that overlap. This document follows the registered sequence and does not try to resolve it.

## Context

QA's job order strips seven columns from the student import template: `COURSE_CODE`, `COURSE_NAME`,
`UA_FULLNAME`, `TEACHER FIRST NAME`, `TEACHER LAST NAME`, `TEACHER SUFFIX`, `TEACHER COLLEGE`. The
template keeps `REGNO`, `STUDENT FIRST NAME`, `STUDENT MIDDLE NAME`, `STUDENT LAST NAME`, `FULL_NAME`,
`EMAIL_ID`, `USA_EMAIL`, `COLLEGE_NAME`, `PROGRAM`, `SECTION_NAME`.

Four facts in the code decide how this is built:

1. **A section exists only inside a course.** `CourseOfferings.CourseId` is `NOT NULL` and
   `UX_CourseOfferings_Term_Course_Section` is `(TermId, CourseId, SectionKey)`. Section groups, the
   D-50 Section filter and D-47's year level all read `Enrollments → CourseOfferings.SectionKey`. A roster
   with no course column has nowhere to put a section.
2. **The importer reads course, teacher and section cells from fixed headers, not from the profile.**
   `ParseRows` reads them through the `SisRosterColumns` constants. Only the RFID column and the category
   columns are resolved from the batch's profile (`ResolveMappedColumnsAsync`). The profile's
   `Course.CodeKey` row has been a record, not a switch, since version 1.
3. **`SisRosterColumns.Required` is profile-independent.** It picks the worksheet on every upload
   (`ExcelRosterReader`), and the template appends any `Required` header the pinned profile omits. If the
   list kept `COURSE_CODE`, `COURSE_NAME` and `UA_FULLNAME`, the template would put them back.
4. **`Students.Course` already holds the programme code, not a course.** `RefreshStudentCacheAsync`
   writes `student.Course = programCode`. Every consumer of that cache is untouched by this change.

## Options Considered

- **(Chosen) A: one placeholder course per school.** `Course { Code = "(section)", CodeKey =
  "(section)", CollegeId = null, Title = null }`, created on first need by the importer. Then
  `UX_CourseOfferings_Term_Course_Section` gives one section-only offering per (term, section), and
  every reader of `SectionKey` works unchanged. No migration, no schema change in the OpenAPI document.
- **B: make `CourseOffering.CourseId` nullable.** Rejected. Its `Down` cannot be reversed once NULL rows
  exist. It changes the contract (`CourseOfferingDto.courseId` and the course fields become nullable). It
  rebuilds the unique index on the SQL Server 2012 VM. It also has a NULL-equality trap. Unfiltered,
  SQL Server treats NULLs as equal and admits **one** NULL-course row per (term, section). But EF Core's
  SQL Server provider adds `WHERE [CourseId] IS NOT NULL` to a unique index over a nullable column by
  default, and then NULL-course offerings duplicate freely. This is the trap the attendance index nearly
  fell into. Separately, every `o.CourseId == x` predicate would skip those rows without any error.
- **C: a new `SectionEnrollments` table.** Rejected. Membership would have two sources for ever, and
  every section reader (projection, D-50 filter, D-47) would read both. A third grain beside
  `Enrollments` and `StudentGroupMembers` also drifts from §4.7, which names a section only as a group.
- **One placeholder per programme.** Rejected. Section groups already merge offerings by key across
  programmes, so the extra rows buy nothing a reader can see.

---

## Decision

### D-77: The built-in roster import is section-only; sections hang off one placeholder course per school

**1. The built-in profile moves from `BuiltInVersion` 4 to 5.** Version 5 is version 4 without the seven
rows for `COURSE_CODE`, `COURSE_NAME`, `UA_FULLNAME`, `TEACHER FIRST NAME`, `TEACHER LAST NAME`,
`TEACHER SUFFIX` and `TEACHER COLLEGE`.

- The template has **11** columns: QA's 10 plus `RFID`. `RFID` is kept because the card serial is a
  separate column from REGNO (D-43). QA has been asked about it in MDVault #651 (**drafted, not sent**).
  The RFID leading-zero ticket, MDVault #613, is open.
- Versions 1–4 are superseded, not deleted (ADR-001 D-4). **A batch pinned to version 4 or earlier and
  run after the deploy still creates courses, instructors and assignments.** This is D-4 working, as in
  ADR-009.
- No migration: `EnsureBuiltInProfileAsync` writes the version 5 row on a school's next upload.

**2. The placeholder course.** One row per school with `CodeKey = "(section)"`, `Code = "(section)"`,
`CollegeId = null`, `Title = null`. The importer creates it the first time it needs it in section-only
mode. It is defined once in `EAMS.Domain` beside `AcademicKey.Unspecified`, as one constant
(`AcademicKey.SectionPlaceholder`) and **one named predicate** (`AcademicKey.IsSectionPlaceholder`).

- **The predicate covers in-memory checks only.** EF Core cannot translate a method call, so every
  query compares `CodeKey == / != AcademicKey.SectionPlaceholder` inline. The inline sites are
  `AcademicReferenceService` (twice: the `/academic/courses` list and the `/academic/course-offerings`
  list), the `EventService` D-50 Course filter's `EXISTS`, and the placeholder load in
  `SisImportService.ResolveDimensionsAsync`. Only the in-memory sites call `IsSectionPlaceholder`: the
  `StudentGroupProjection` skip and the importer's fan-out (which records no `Course` touch). The
  constant is the single source in both forms.

- **It cannot collide with a real course.** `AcademicKey.Normalize` keeps only letters and digits, so
  no source code can normalize to a key containing parentheses. A course literally called `(section)`
  becomes `SECTION`. `AcademicKey.Unspecified` relies on the same guarantee, and its doc comment says so
  ("unforgeable by construction"). Verified in `AcademicKey.cs`.
- `UX_Courses_SchoolId_CodeKey` keeps it to one per school. `Courses.Code` is `nvarchar(50)`, and
  `Title` is nullable.

**3. Section-only mode comes from the batch's pinned profile, not from the file.**

- A profile that maps no `Course.CodeKey` target is section-only. `ResolveMappedColumnsAsync` gains
  that lookup beside its RFID and category lookups (fact 2 above: this is new, not existing behaviour).
- **Course mode also reads its course cells from the profile now, not only the switch.** The course
  code and its key both come from the `Course.CodeKey` row's source column (one cell, so the two
  cannot disagree). The title comes from the `Course.Title` row's source column; with no such row,
  courses import with no title.
- **Teacher cells are read only if the profile maps an `Instructor.DisplayName` row.** The cells
  themselves still come from the fixed `SisRosterColumns` headers, because the full, first and last
  name columns share that one target and it cannot say which is which.
- **A broken course-mode mapping fails; it is never downgraded.** A `Course.CodeKey` row whose source
  cannot be read (no source column and no source key) fails every row with `MissingRequiredValue` for
  the course code. It does not fall back to section-only: section-only is a property a profile states
  by omitting the row, never one inferred from a mistake in it.
- A batch with a null `ImportProfileId` stays in course mode at the fixed headers, with teachers,
  matching today's `NoMappedColumns`.
- Under version 5, an old-format file reads **no** course or teacher cell. A student who appears on
  several course rows in the same section gets one enrollment, and the later rows report
  `Skipped`/`NoChange`.
- `SisRosterColumns.Required` shrinks to `REGNO`, `STUDENT FIRST NAME`, `STUDENT LAST NAME`,
  `COLLEGE_NAME`, `PROGRAM`, `SECTION_NAME`. This is required by fact 3, not just tidy.

**4. Fan-out under version 5:** College, Program, CourseOffering, Student, RfidCard, StudentTermRecord,
Enrollment. That is **7 for a clean row with a card, 6 without**. The placeholder records no `Course`
touch, and there is no `Instructor` or `CourseOfferingInstructor` touch. Today the same row records
**10** (`SisImportPipelineTests.Every_entity_a_row_touched_is_recorded_with_what_it_did_to_it`, which
assumes a card and a named teacher), 9 without a card and 8 with a TBA teacher and no card.

**5. Consumers that exclude the placeholder** (by the predicate in memory, by the constant inside an EF
query; see point 2).

| Consumer | Behaviour under D-77 |
|---|---|
| `StudentGroupProjection` | Projects no `Course`-type group for a section-only offering. Section groups and D-47 year level are unchanged |
| `EventService` D-50 Course filter | Refuses the placeholder id with `InvalidAudienceFilterValue` (see the note below) |
| `AcademicReferenceService` | `GET /academic/courses` hides the placeholder; `GET /academic/course-offerings` excludes section-only offerings (see the note below) |

**Unchanged:** `AudienceResolution`, `ClearanceService`, `AudienceDefinitionService`,
`AttendanceAnalyticsService` and `PreRegistrationService` read `Students.Course`, which is the programme
code (fact 4). The Students grid and mobile `by-card` read the same cache. **The D-50 Section filter
needs no change**: it resolves through `Enrollments → CourseOfferings.SectionKey` and matches v5
students as it matches v4 ones.

> **Two notes for the implementer.** (a) `BuildCondition` validates ids by syntax only, and an unknown
> GUID "matches nobody" by design. Refusing the placeholder needs the school's placeholder id, which
> means a database read on a path that has none today. (b) `AudienceField.Section`'s documentation names
> `GET /academic/course-offerings` as the source of Section values. Excluding section-only offerings
> removes every v5 section from that source. The SPA does not call it (it picks sections from
> `GET /student-groups`), so nothing visible breaks, but the documented source becomes wrong for an API
> consumer. **Decided by JJ (2026-10-23):** keep the exclusion, and point `AudienceField.Section`'s
> documentation at the `Section` groups of `GET /student-groups`. (The `AudienceField` doc comment
> already says so in the working tree.)
>
> **Caveat on that source.** A section group's `name` is the section label plus a ` (TermCode)`
> suffix (`BSIT 2-A (<TermCode>)`, `StudentGroupProjection.NameFor`), and the whole name passes through
> `GroupName.Clamp`, so it may be clamped to the group-name limit.
> `StudentGroupDto` carries no `sourceKey`. An API consumer that feeds group names to the Section
> filter must therefore strip the suffix itself, and a clamped name may not round-trip. Adding a
> `sourceKey` to `StudentGroupDto` is the follow-up fix (below).

**6. Course audiences stop growing** for v5-imported students. An event that invites a course offering
reaches only v4-imported enrolments. Accepted.

**7. A blank `SECTION_NAME` under version 5** writes no offering and no enrollment, and warns
`SectionUnspecified` with v5 wording. The value stays optional; the header is required. (Under version 4
a blank section is filed under `(unspecified)`. A section-only `(unspecified)` offering would be one
institution-wide pile per term with no course to separate it.)

**8. Warnings.**

- **Unreachable under version 5:** the warnings `CourseTitleAlias`, `CourseCollegeAdopted` and
  `InstructorPlaceholder`; the skip reason `SisImportSkipReason.InstructorPlaceholder`; and the
  **failure** `COURSE_COLLEGE_COLLISION` (`SisImportFailureCode.CourseCollegeCollision`, a failure, not
  a warning). A row that failed with it under version 4 imports under version 5.
- **`SectionSpansPrograms` keeps firing, with v5 wording.** Its current message says nothing merges
  because the course is in the key. Under version 5, two programmes that share a section key **share one
  offering**. Section audiences do not change, because section groups already merged by key.
- The SPA keeps every warning label in `web-admin/src/sisImport.ts`: v4 history carries those codes.

**9. The import preview follows the pinned profile.** Distinct courses and instructors count 0 under
version 5, and the FE hides those tiles. `BuildPreviewAsync` reads fixed headers today, so this is new
work.

**10. Re-importing in the same term only adds.** v4 enrollments, offerings and instructor assignments
are never touched. A student moved to another section by a v5 file sits in **both** sections. Their
D-47 year level becomes `null` **only if the two sections imply different years** (`BSIT 2-A` and
`BSIT 3-A`). A move within one year (`2-A` to `2-B`) keeps it. This already happens with any corrected v4
roster. Retiring stale enrollments is a separate ticket.

**11. Placeholder creation race.** If two first section-only runs for one school overlap, both can
insert the placeholder. The loser hits `UX_Courses_SchoolId_CodeKey`, `RunAsync` marks its batch
`Failed` and rethrows, and a retry finds the row and succeeds. Accepted and documented. This is the same
class as ADR-009 step 4, and the same race already exists for every new real course code.

### What D-77 amends, by reference

- **ADR-001 D-1 / D-2.** Under version 5, `Enrollments` says which section a student is in, not which
  course they take. The `Students` display cache is unchanged.
- **ADR-001 D-4.** The profile now decides the import *mode*. The `Course.CodeKey` row becomes
  load-bearing, as the RFID row did under D-43.
- **ADR-002 D-11.** (a) The asymmetry argument ("a section-label collision is always within one genuine
  course") does not hold for section-only offerings, which is why `SectionSpansPrograms` is reworded.
  (b) The widening path's step 2 (`Courses.CollegeId NOT NULL` after a verified backfill) **cannot
  complete while the placeholder exists**, because it belongs to no college. Any widening must exclude
  it. **The course-collision hard failure stays for course-mode batches.**
- **D-47 (proposed).** The rule is unchanged. Its input, the enrolled section keys, now comes from
  section-only offerings for v5 students.

### Technical Plan drift

| Plan § | Drift | Kind |
|---|---|---|
| §4 / ADR-001 D-1 (`Courses`) | One placeholder `Courses` row per school, `CodeKey = "(section)"`, a stand-in rather than a course. `Courses` is not a §4 table; it is ADR-001 D-1's addition, so this is drift on the drift | Additive (a row, no column, no migration) |

---

## Consequences

### Positive
- QA's template is met with no migration, no schema change, no contract schema change and no data loss.
- Every section reader (projection, D-50 Section filter, D-47) works unchanged.
- Reversible: a version 6 that maps the course columns again restores course mode. The placeholder and
  its offerings stay as harmless history.
- Rows that failed `COURSE_COLLEGE_COLLISION` import under version 5.

### Negative
- A sentinel row lives in `Courses`. Every reader that lists courses must exclude it, by the predicate
  in memory or by comparing against the constant in an EF query. A missed one shows `(section)` as a
  course. One named constant, with one predicate over it, is the mitigation; the EF sites cannot share
  the predicate, so a grep for `SectionPlaceholder` is how a reviewer finds them all.
- Course audiences stop growing (D-77, point 6), and v5 sections drop out of `GET /academic/course-offerings`
  (note (b) above).
- Two programmes sharing a section key now share one offering.
- It blocks ADR-002 D-11's widening as written. The widening needs a placeholder exclusion first.
- A v5 re-import leaves a moved student in both sections (D-77, point 10).
- `CategoryMappingProfile`, the ADR-009 test fixture, is seeded at `BuiltInVersion + 1` and copies
  `SisImportProfileTemplate.Entries` by default. It therefore becomes **version 6 and section-only**.
  ADR-009's fixture wording is now version-relative for that reason. Tests that also assert on courses
  or instructors use a frozen copy of the v4 mapping instead of the live template. That work is done:
  `CourseMappingProfile` (`backend/EAMS.Tests/Integration/Infrastructure/`) is the frozen v4 copy,
  and `CategoryMappingProfile.SeedCategoryMappingProfileAsync` takes a `frozenCourseMapping` flag that
  starts from it instead of `SisImportProfileTemplate.Entries`.

### Neutral
- **Deploy order follows ADR-009: FE before BE.** If both ship in one release, a school goes from v3
  straight to v5 and no v4 row is ever written. That is fine: `FindCurrentAsync` matches the exact
  version.
- Because the contract does not change, the FE can only hide tiles on a count of 0. That also hides the
  instructor tile on a v4 preview of a file with no teacher column.
- `docs/api/openapi.json` gains no schema change. It changes only if the XML comments on the academic
  reads are edited, because descriptions are generated from them (D-38).

### Before the backend deploy (release note)

1. **Deploy the FE before the BE**, as in ADR-009.
2. **Check for an existing v5 profile row on the VM:**
   `SELECT SchoolId, Name, NameKey, Version, IsActive FROM dbo.SisImportProfiles WHERE Version >= 5;`
   (Columns checked against `EamsDbContextModelSnapshot`.) Before this change `BuiltInVersion` was 4, so
   any row returned is operator-authored. If its `NameKey` is the built-in key
   (`CICSSFACULTYEVALUATIONREPORT`), `FindCurrentAsync` takes it as built-in v5, or prefers it as a newer
   active version, and uploads pin to its mapping. Settle any such row with JJ before deploying.
3. **Run or set aside every `Pending` or `Failed` batch whose profile maps courses.** That means every
   batch on version ≤ 4, plus any batch with no profile. **Not just v4:** if ADR-009 has not shipped
   first, v3 batches exist too. After the deploy they still create courses, instructors and
   assignments. There is no discard route. To list them:
   `SELECT b.Id, b.SchoolId, b.Status, p.Name, p.Version FROM dbo.SisImportBatches b LEFT JOIN dbo.SisImportProfiles p ON p.Id = b.ImportProfileId WHERE b.Status IN ('Pending', 'Failed') AND (b.ImportProfileId IS NULL OR EXISTS (SELECT 1 FROM dbo.SisImportProfileColumns c WHERE c.ProfileId = b.ImportProfileId AND c.TargetField = 'Course.CodeKey'));`
4. **Expect a possible failed run on concurrent first section-only imports** (D-77, point 11). Retry the
   `Failed` batch. ADR-009 step 4's upload race also applies, now for the creation of v5.

## Follow-Up Actions

These are proposals, not authorised by this document. JJ decides.

- [ ] JJ reads and accepts (or amends) this ADR together with ADR-009.
- [x] Note (b) decided by JJ (2026-10-23): keep the `/course-offerings` exclusion; `AudienceField.Section`'s
      documented value source is `GET /student-groups`.
- [ ] Add a `sourceKey` to `StudentGroupDto`, so a Section-filter consumer does not have to strip the
      ` (TermCode)` suffix from a possibly clamped group name (note (b) caveat). A contract addition.
- [ ] Send MDVault #651 (RFID in the template) to QA. It is drafted, not sent.
- [ ] Tell QA: an old template's course and teacher columns are ignored under v5. A moved student
      shows in both sections until stale enrollments are retired.
- [ ] Separate tickets: retire stale same-term enrollments, and a retry wrapper for the
      profile-creation and placeholder-creation races.
- [ ] Bundle with that race/retry ticket (D-77, point 11): `RunAsync`'s catch marks the batch `Failed`
      and sets `FinishedAt` but never writes `FailureReason`, so a failed run says nothing about why.
      Pre-existing; not introduced by this change.
- [ ] Follow-up ticket (JJ waived the structural part of a JJReviewer SOLID warning, 2026-10-23): the
      section-only flag is threaded through about 20 sites in `SisImportService`. Extract the placement
      mode and the `MappedColumns` resolution behind one seam. The duplicated blank-section rule was
      already collapsed into one predicate in this change.
- [ ] Before any ADR-002 D-11 widening, design the placeholder exclusion.
- [x] ADR-009's fixture wording is now version-relative (`BuiltInVersion + 1`, version 6 at the time
      of writing).

**Recorded, not a follow-up.** The section-only template's instruction reads "One row per student per
section", not "one row per student", because a student in several sections (a block section plus NSTP
or ROTC, for example) needs one row for each. Course mode keeps "One row per student per class".
