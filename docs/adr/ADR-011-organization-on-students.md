# ADR-011: Students Carry an Organization (Import, Export, Audience)

**Status**: Proposed (2026-10-23). Awaiting JJ's read-through; JJ then flips it to `Accepted`.
> The *decisions* below are JJ's (2026-10-23, answering QA's "BACKOFFICE Task 3: Add Organization to
> Students" job order). This *document* writes them down and has not yet been read by him. The register's
> rule holds: JoseArch recommends, JJ accepts. **Do not self-accept.**

**Date**: 2026-10-23
**Deciders**: JJ; JoseArch (Team X)
**Relationship to other ADRs**: **Builds on ADR-010 (D-77)**, which is also `Proposed`. It takes the
built-in profile from ADR-010's version 5 to version 6 and the template from 11 to 12 columns. **Amends by
reference** ADR-010 **D-77**, ADR-007 **D-70** (restated, unchanged), ADR-001 **D-2** and **D-4**, and the
QA ruling MDVault #543 **Option A** (organization normalization), which until now covered personnel only.
ADR-001 is `Accepted` and **not edited**; the register in `README.md` records the amendment. **ADR-006
remains reserved** for the consolidating ADR.

> **Numbering note.** D-78…D-83 are used nowhere in the repository (code, tests or docs; checked
> 2026-10-23). They fall inside the unregistered Phase 6 auth draft's range (MDVault #263, D-60…D-84), as
> D-76 and D-77 do. ADR-009's numbering note covers that overlap. This document follows the registered
> sequence and does not try to resolve it.

## Context

QA's job order asks for two things: Organization on students, **imported and exported**, and Organization
**readable as an event audience**.

Five facts in the code decide how this is built:

1. **Organization exists today on personnel only.** `Personnel.Organization nvarchar(150) NULL`
   (`PersonnelText.OrganizationMaxLength`). Its rules live in `OrganizationText`, which sits in
   `PersonnelEntities.cs`: `Normalize` (trim, collapse internal whitespace, keep casing) and `MatchKey`
   (`Normalize`, upper-cased invariant). `Students` has no such column, and Technical Plan §4.3 defines
   none.
2. **Before this change there were two "distinct organizations" rules.** `PersonnelService.OrganizationsAsync`
   took a SQL `Distinct()`, then normalized, grouped case-insensitively, kept the ordinally-first spelling
   and sorted. `AudienceDefinitionService.OptionsAsync` took a plain SQL `Distinct().OrderBy()`, so a
   variant that only the normalization folds (a leading space, an internal double space) showed twice
   there. *Both have since changed (D-78): neither now takes a SQL `Distinct()`, and both fold through
   `OrganizationText.DistinctForDisplay`. This fact records the starting point.*
3. **The audience matcher is one function.** `AudienceResolution.BuildQueries` serves the resolve read,
   the live denominator and the terminal freeze (ADR-007 D-69/D-70). Today `AudienceType.Organization`
   returns **no student query**. In `Custom`, `organizations` filters personnel only. `departments` is
   already the one criterion that applies to both rosters.
4. **Optional roster cells keep a stored value when blank.** `ResolveStudents` writes MiddleName, Email
   and AlternateEmail through `AssignIfPresent`. A blank cell means "this file does not say".
5. **`Student.DerivedAcademicPropertyNames` is a write guard.** `EamsDbContext.GuardDerivedAcademicFields`
   throws on any change to a listed property outside the cache-refresh scope. The importer writes
   students outside that scope.

## Options Considered

- **(Chosen) A: a nullable free-text column on `Students`, sharing personnel's rules.** One additive
  migration. The SIS file is the source, and the same normalization and the same match key apply on both
  rosters. The audience gets one more `Where` in the one matcher.
- **B: an `Organizations` master table with an FK from both rosters.** Rejected. QA #543 Option A already
  ruled that organization has no master entity, FK or seed. This option would reopen that ruling, add a
  second migration on personnel, and need a vocabulary screen nobody asked for.
- **C: organization as a `StudentGroup` projected from the import (the D-49 pattern).** Rejected. Groups
  are term-scoped section and year projections. Organization is a property of the person, not of an
  enrollment. It would also give personnel and students two different audience mechanisms for one word.
- **D: a stored match-key column with an index.** Deferred, not rejected (D-79). About 21k students per
  school is a cheap scan, so nothing has been measured that needs it.

---

## Decision

### D-78: One set of organization rules

- **Move `OrganizationText` from `PersonnelEntities.cs` to a new `EAMS.Domain/OrganizationText.cs`**, in
  the same namespace, so it no longer reads as a personnel type. `using System.Text;` moves with it (the
  `StringBuilder` is its only user in that file).
- **Add `OrganizationText.MaxLength = 150`.** `PersonnelText.OrganizationMaxLength` points at it, so the
  personnel column and the student column cannot drift apart.
- **Add `OrganizationText.DistinctForDisplay`.** This is the fold `PersonnelService.OrganizationsAsync`
  already does: normalize, drop blanks, group with `OrdinalIgnoreCase`, keep the ordinally-first spelling
  in each group, sort with `OrdinalIgnoreCase`.
- **Both lists use it.** `GET /personnel/organizations` stays sourced from personnel only (it feeds the
  personnel form's suggestions). `GET /event-audiences/options` merges the two sources (D-82). The plain
  SQL `Distinct` in `OptionsAsync` goes.
- **No SQL `Distinct()` on either list.** `OptionsAsync` and `PersonnelService.OrganizationsAsync` both
  read the raw non-null values and fold them in memory. The reason is collation: under the database's
  case-insensitive collation a SQL `Distinct()` keeps **one arbitrary spelling** of `CICT` / `cict `, so
  `DistinctForDisplay`'s ordinally-first pick would depend on row order. `OrganizationsAsync` kept its
  `Distinct()` at first. It was removed late in the change, after a nondeterministic result surfaced in
  testing.

### D-79: The column

- **`Students.Organization nvarchar(150) NULL`**, in one additive migration, `StudentOrganization`.
  Configured as `HasMaxLength(OrganizationText.MaxLength)`.
- **No index.** The match predicate (`Organization.Trim().ToUpper()`, which EF emits as
  `UPPER(LTRIM(RTRIM(...)))`) cannot seek. About 21k rows per school is a cheap scan. Revisit only on a
  measurement, through a stored key column in a separate ADR (option D).
- **`Down` does `DropColumn`**, following the `PreRegistrationEventLink` precedent. **This is a data-losing
  rollback. It is JJ's explicit exception (2026-10-23) to the global no-DROP hard rule, and it is an
  exception only once ratified: accepting ADR-011 ratifies it, and until then it stands on JJ's call
  alone.** A rollback
  loses the Organization values imported since `Up`. A re-import restores them, because the SIS file is
  the source. The rejected alternative is the `IssuesCertificatesFlag` shape, where `Down` refuses
  (`THROW`) while data exists. That shape fits a value an administrator typed. It does not fit a value
  any re-import rewrites. A reviewer should read this paragraph before flagging the `Down` as CRITICAL.
- **SQL Server 2012.** Adding a nullable column with no default is metadata-only. Compatibility level 110
  is already pinned (`DependencyInjection.SqlServerCompatibilityLevel`), and the column introduces no new
  query shape.
- Additive drift against §4.3 (table below), on the same footing as ADR-002 D-7's `AlternateEmail`.

### D-80: Import, profile version 6

**1. The built-in profile moves from `BuiltInVersion` 5 to 6.** Version 6 is version 5 plus one row:
`new(SisRosterColumns.Organization, "Student.Organization", "OrganizationText.Normalize", IsRequired:
false)`, with `SisRosterColumns.Organization = "ORGANIZATION"`. It is **not** added to
`SisRosterColumns.Required`: a file without it must still select the worksheet, and Task 2 files must
still import.

**2. It is the last row, so the template has 12 columns:** QA's 10, then `RFID`, then `ORGANIZATION`.
`RFID` sits second in file order, as in version 5. **Task 3 overrides Task 2's "only these 10 columns."**
QA is told in MDVault #651 (**drafted, not sent**).

**3. The mode comes from the pinned profile**, as for RFID, the category columns and the course columns.
`ResolveMappedColumnsAsync` gains a `Student.Organization` target lookup beside its others, and
`MappedColumns` gains the source key. **A batch pinned to version 5 or earlier, or a batch with no profile,
reads no Organization cell**, even from a file that carries the column (ADR-001 D-4 working).
`NoMappedColumns` maps none.

**4. Cell rules.**

| Cell | Effect on the stored value |
|---|---|
| Non-blank, different (ordinal, on the normalized value) | Overwritten. The SIS is the truth |
| Non-blank, equal | Unchanged |
| Blank | **Kept**, through the existing `AssignIfPresent` path in `ResolveStudents` |
| Over 150 characters after normalization | Treated as **blank**: not applied, not truncated. New **warning** `OrganizationTooLong`; the row still imports |

The over-150 cell is blank for point 5 as well. `SisImportService.ReadOrganization` returns `null` for it,
so a later valid row for the same student supplies the value. Only when no row carries a usable value is
the stored organization kept.

A case-only difference (`CICT` stored, `cict` in the file) is an ordinal difference, so it **overwrites**.
The audience match is unaffected either way.

**5. A student on several rows** takes the **first non-blank** value in worksheet order
(`FirstCarryingOrganization`; blank includes an over-150 cell, point 4). Every other row whose
organization's `MatchKey` differs from the kept value is reported in its `StudentIdentityConflict`
warning, as the lowercase line `organization 'x' vs 'y'` (this row's value, then the kept value). A
case-only or whitespace-only variant is not a conflict, and a blank row is never one. Where the line goes
depends on which row was kept:

- **The kept row is the first row.** The line joins the other differences in the existing sentence:
  "REGNO … is described differently here than on row N: …; organization 'x' vs 'y'. The first row's
  values were kept."
- **The kept row is a later row.** A separate sentence names it, so the first row's sentence still says
  only what it means: "It also differs from row K: organization 'x' vs 'y'. Row K's organization was
  kept, as the first of this student's rows to carry one." If the row has no other difference, that
  sentence opens with "REGNO … is described differently here than on row K:" instead.

**6. Fan-out is unchanged** (7 for a clean row with a card, 6 without; ADR-010 D-77 point 4).
Organization is a `Student` column, so it records no touch of its own. An Organization-only change moves
the row's Student touch from `Unchanged` to `Updated` and bumps `UpdatedAt`.

**7. Organization must NOT join `Student.DerivedAcademicPropertyNames`.** It is not derived. Listing it
would make the importer's own write throw `AcademicCacheWriteException` (fact 5).

**8. `OrganizationTooLong` needs a rank in `RowLedger.WarningPrecedence`** (an unlisted code sorts last),
and a label in `web-admin/src/sisImport.ts`. `SisImportRows.WarningCode` is `nvarchar(50)` with no CHECK
constraint, so no migration is needed.

**9. The template's Instructions sheet gains one line**, only when the profile maps `Student.Organization`
(found by the same target lookup the importer uses). After the mapped header, it reads: "is optional, up
to 150 characters. A blank cell keeps the organization already stored for the student; a filled-in cell
replaces it, and a longer value is not applied and is reported. For a student on several rows, the first
filled-in cell is used." (`SisImportTemplateWorkbook.OrganizationRuleTail`.)

### D-81: Contract

- **`StudentDto` gains a nullable `Organization`, last in the record** (after `Cards`). This reaches every
  response that carries a `StudentDto`: list, get, create, update, and the device-authenticated `GET
  /students/by-card/{cardUid}`.
- **A `docs/api/mobile-changes.md` entry:** additive, no release needed. Phrase it like the
  `issuesCertificates` entry: no release needed unless the client rejects unknown members.
- **Not editable on `PUT`.** `StudentWriteRequest` is unchanged, and `StudentService.Apply` does not touch
  the column. The rationale is the one `StudentWriteRequest` records for `AlternateEmail`: the column
  belongs to the import pipeline, and a manual edit must not blank the importer's work. (Unlike
  `AlternateEmail`, Organization is also *read*. A client echoing it back on `PUT` lands it in
  `UnmappedFields`, which is ignored for every member except the three D-2 cache columns.)
- **No organization filter on `GET /students`** in this ticket.
- `docs/api/openapi.json` changes (one schema field and one route, D-83). It is generated (D-38).

### D-82: Audience

- **`AudienceType.Organization` matches students as well as personnel**, with the same predicate:
  `s.Organization != null && keys.Contains(s.Organization.Trim().ToUpper())`, keys from `MatchKey`.
- **In `Custom`, `organizations` becomes a criterion on both rosters**, ANDed with the other student
  criteria. This follows the `departments` precedent. **JJ accepted the meaning change.** Two consequences
  follow from the precedent and are stated so nobody mistakes them for bugs:
  - A Custom definition with a student criterion **and** organizations now needs both on the student side
    (R1).
  - A Custom definition whose other criteria are personnel-only (`classifications`, `personnelIds`) now
    also resolves the students **of that organization alone**, just as `departments` + `classifications`
    resolves students by department alone today (R2).
- **The options list merges** student and personnel organizations through `DistinctForDisplay` (D-78).
- **`AudienceField.All`, the D-50 event filter, is unchanged.** Organization is not a sixth field.
- **D-70 holds.** Students count toward the denominator, the freeze and the manifest. Personnel stay
  advisory. Closed and Cancelled events are frozen and do not move. An event with a linked pre-registration
  session resolves no definition (ADR-008 D-71), so it is unaffected.
- All of this is one change in `AudienceResolution.BuildQueries`. The denominator, the freeze and the
  resolve read follow from it, because it is the only implementation (ADR-003 D-19).

### D-83: Export

- **`GET /students/export.csv`**, gated by `EamsPermissions.StudentsRead` (the `[Authorize(...)]` plus
  `[HasPermissionNotEnforced(...)]` pair, or `AuthorizationCoverageTests` fails).
  `GET /personnel/export.csv` is the sibling precedent.
- **Filters `search`, `course` and `status`, through one filtered query shared with the list.**
  `ListAsync` builds its filter inline today, so it has to be extracted, as `PersonnelService.FilteredQuery`
  is. The same ordering applies (`LastName`, then `Id`), and soft-deleted students are excluded.
- **Built as a projection**, not `Include` plus `ToDto`. It goes through a new `EAMS.Api/Reports/StudentCsv.cs`
  on top of `EventReportCsv`, as `PersonnelCsv` is. Every cell is quoted (commit `a8aa92e`), formulas are
  neutralized, the file is UTF-8 with a BOM, the response is `Cache-Control: no-store`, and the filename
  is `EAMS-students-yyyy-MM-dd.csv`.
- **Display headers, in order:** Student No., RFID UID (active), Last Name, First Name, Middle Name, Email,
  Course, Year Level, Section, Organization, Status.
- **Course, Year Level and Section are the single-valued ADR-001 D-2 cache.** The export therefore shows
  one section for a student in several, and its `course` filter is as lossy as the grid's. Both match the
  screen it exports, which is the point of the shared query.
- **It is not re-importable.** The import takes XLSX only (JJ, 2026-08-01), and these are display headers,
  not the template's.

> **Decided (JJ, 2026-10-23): every active card, joined.** A student can hold more than one active card:
> `UX_RfidCards_SchoolId_CardUid_Active` is unique per serial, not per student, and `AddCardAsync` does not
> deactivate a student's other cards. "RFID UID (active)" therefore lists all active UIDs in ordinal
> order, joined by `"; "`, so no card is hidden.

### What this amends, by reference

- **ADR-010 D-77.** Profile v5 → v6, template 11 → 12 columns. Everything else in D-77 stands.
- **ADR-001 D-2.** A new `Students` column that is **not** part of the derived cache and must never join
  `DerivedAcademicPropertyNames`.
- **ADR-001 D-4.** Another profile-decided read: the `Student.Organization` row is load-bearing, as the
  RFID row is.
- **ADR-007 D-70.** Restated, unchanged: students counted, personnel advisory.
- **QA #543 Option A.** Normalization and trim+case-insensitive matching now cover students as well as
  personnel, from one `OrganizationText`.

### Technical Plan drift

| Plan § | Drift | Kind |
|---|---|---|
| §4.3 `Students` | `Organization nvarchar(150) NULL`, no index | Additive (one column, one migration) |
| §6.2 Students | `GET /students/export.csv` under `students.read`. §6.7's `POST /reports/export` is not used | Additive (one route) |

---

## Consequences

### Positive
- QA's two asks are met with one additive column, no backfill, and no contract removal.
- One organization rule set across both rosters, the options list and the matcher.
- The audience change is one function, so the denominator, the freeze and the manifest cannot disagree.

### Negative
- A data-losing `Down` (D-79), accepted by JJ.
- `Custom` changes meaning for stored definitions (R1, R2).
- The export inherits the D-2 cache's single-valued columns.

### Risks
- **R1 (high).** Stored Custom definitions that combine a student criterion with `organizations` resolve
  to **0 students** from the BE deploy until a v6 import fills Organization. Open and Draft events using
  one lose their denominator.
  - *Mitigation:* the pre-deploy audit below. JJ rules on each definition found before the BE deploys.
  - *Rollback:* revert the `AudienceResolution` change alone. No schema is involved.
- **R2.** After the first v6 import, events with an Organization definition, or a Custom one with
  organizations, **gain students**. The denominator rises, the manifest version moves, and devices re-sync.
  This is intended, and it is noted for QA.
- **R3.** Excel strips leading zeros from Student No. and RFID cells in the CSV, even when quoted. This is
  MDVault #613's territory and is not fixed here.
- **R4.** Deploy order is FE before BE (ADR-009/010). Until the BE is up, the Export button 404s loudly,
  through `ApiError`. The FE must parse `organization` as optional, because the old BE omits it.
- **R5.** `SisImportService` gains one more mode-dependent read. This adds to ADR-010's SOLID follow-up
  (the `MappedColumns` seam); it is not extracted here.
- **R6.** About 21k rows are scanned per live poll of an event with an Organization definition. Measure
  only if someone complains (D-79).

### Neutral
- `CategoryMappingProfile`, the test fixture, seeds at `BuiltInVersion + 1` from the live entries, so it
  becomes version 7 and maps ORGANIZATION. Tests that pin a column count see 12.
- Pending v5 batches read no Organization after the deploy. That is D-4 working.

### Before the backend deploy (release note)

1. **Deploy the FE before the BE**, as in ADR-009/010.
2. **Run the R1 audit** and settle every row with JJ. Names were checked against `EamsDbContextModelSnapshot`.
   `CriteriaJson` is written by `JsonSerializerDefaults.Web` (camelCase, compact, nulls written as `null`).
   SQL Server 2012 has no JSON functions, so the query uses `LIKE`, where `[[]` is a literal `[`.
   ```sql
   SELECT d.Id, d.SchoolId, d.Name, d.IsActive, d.RetiredAt, d.CriteriaJson,
          CASE WHEN d.CriteriaJson LIKE '%"departments":[[]"%' OR d.CriteriaJson LIKE '%"programs":[[]"%'
                 OR d.CriteriaJson LIKE '%"yearLevels":[[]"%'  OR d.CriteriaJson LIKE '%"sections":[[]"%'
                 OR d.CriteriaJson LIKE '%"studentIds":[[]"%' THEN 1 ELSE 0 END AS HasStudentCriterion,
          (SELECT COUNT(*) FROM dbo.EventAudienceDefinitions ead JOIN dbo.Events e ON e.Id = ead.EventId
            WHERE ead.AudienceDefinitionId = d.Id AND e.IsDeleted = 0 AND e.Status IN ('Draft', 'Open')
              AND NOT EXISTS (SELECT 1 FROM dbo.PreRegistrationSessions s WHERE s.EventId = e.Id)) AS LiveEvents
   FROM dbo.AudienceDefinitions d
   WHERE d.AudienceType = 'Custom' AND d.CriteriaJson LIKE '%"organizations":[[]"%';
   ```
   `HasStudentCriterion = 1` with `LiveEvents > 0` is R1. `HasStudentCriterion = 0` is R2 (gains students).

   *Optional companion (R2, for QA's notice).* The query above covers `Custom` only. This one lists the
   live events using an `Organization` definition, whose denominators rise after the first v6 import.
   Same tables, columns and live-event filter; SQL Server 2012 compatible.
   ```sql
   SELECT e.Id AS EventId, e.SchoolId, e.Name AS EventName, e.Status, e.StartAt,
          d.Id AS DefinitionId, d.Name AS DefinitionName, d.IsActive, d.RetiredAt, d.CriteriaJson
   FROM dbo.AudienceDefinitions d
   JOIN dbo.EventAudienceDefinitions ead ON ead.AudienceDefinitionId = d.Id
   JOIN dbo.Events e ON e.Id = ead.EventId
   WHERE d.AudienceType = 'Organization' AND e.IsDeleted = 0 AND e.Status IN ('Draft', 'Open')
     AND NOT EXISTS (SELECT 1 FROM dbo.PreRegistrationSessions s WHERE s.EventId = e.Id)
   ORDER BY e.SchoolId, e.StartAt, e.Name;
   ```
3. **Check for an existing v6 profile row:**
   `SELECT SchoolId, Name, NameKey, Version, IsActive FROM dbo.SisImportProfiles WHERE Version >= 6;`
   Before this change `BuiltInVersion` was 5, so any row returned is operator-authored. If its `NameKey` is
   the built-in key (`CICSSFACULTYEVALUATIONREPORT`), `FindCurrentAsync` takes it as built-in v6 and
   uploads pin to its mapping. Settle it with JJ first.
4. **Pending v5 batches read no Organization.** That is correct behaviour (D-80 point 3). Re-upload the
   file after the deploy if the Organization values are wanted.
5. **If Task 2 ships in the same release, run ADR-010's checklist as well.** Its `Version >= 5` query then
   covers step 3. A school goes from v4 or v3 straight to v6, and no v5 row is ever written, which is fine
   (`FindCurrentAsync` matches the exact version).

## Follow-Up Actions

These are proposals, not authorised by this document. JJ decides.

- [ ] JJ reads and accepts (or amends) this ADR together with ADR-009 and ADR-010.
- [x] JJ ruled on the multiple-active-card cell (D-83): all active UIDs, joined by `"; "` (2026-10-23).
- [ ] Send MDVault #651 to QA (RFID **and** ORGANIZATION in the template). It is drafted, not sent.
- [ ] Tell QA about R2 (denominators rise after the first v6 import) and that a blank ORGANIZATION cell
      keeps the stored value.
- [x] Add the `mobile-changes.md` entry (D-81) in the same change as the DTO.
- [ ] Measure the R6 scan before any index work. Option D is the shape if it is ever needed.
- [ ] Fold the Organization read into ADR-010's `MappedColumns` seam follow-up (R5).
