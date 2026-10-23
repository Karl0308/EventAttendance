# ADR-009: Withdrawing Student Classification from the Product Surface (the Schema Stays)

**Status**: Proposed (2026-10-23). Awaiting JJ's read-through; JJ then flips it to `Accepted`.
> The *decision* below is JJ's (2026-10-23, answering a QA job order). This *document* writes it down
> and has not yet been read by him. The register's rule holds: JoseArch recommends, JJ accepts.
> **Do not self-accept.**

**Date**: 2026-10-23
**Deciders**: JJ; JoseArch (Team X)
**Relationship to ADR-005**: **Supersedes ADR-005 D-59 (as to the built-in profile) and ADR-005 D-65
(as to the `StudentDto` field).** ADR-005 is `Proposed`, but it is **not edited**: the reversal is
recorded here, and the register row for ADR-005 points at this document. This leaves ADR-005 readable
as the record of what was built and why, and this document as the record of why that work was taken
off the screen. D-55…D-58, D-60…D-63 and D-68 stand as written. They are **dormant** rather than
reversed (see D-76). **D-64 is not dormant. It is live**: `GET /cards` (cards paged, not students) is
what the SPA's card lookup calls (`searchCards` in `web-admin/src/api.ts`, used by `CardLookupDialog`).
D-65 is superseded only as to the `StudentDto` field. Its other rulings stand (see D-76). Decision numbering continues at **D-76**, where ADR-008's tail said
the next ADR would continue. **ADR-006 remains reserved** for the long-deferred consolidating ADR, so
this slice is **ADR-009**.

> **Numbering note (read before citing D-76).** D-76 appears nowhere in the repository except in
> ADR-008's "continues from D-76" line. It **is** used in one unregistered place: the Phase 6 auth
> draft (MDVault #263) numbers its decisions D-60…D-84, and its D-76 is the dev SuperAdmin seed guard.
> That draft's whole range already overlaps ADR-005 (D-55…D-68), ADR-007 (D-69…D-70) and ADR-008
> (D-71…D-75). Its D-74 is cited in shipped auth code (`TokenIssuer`, `AuthService`, `Program.cs`), so
> the D-74 collision with ADR-008 is real. This document follows the registered sequence, as ADR-007
> and ADR-008 did. The overlap is resolved when the auth decisions are registered (see `README.md`'s
> note on the unnumbered rows) or in the ADR-006 consolidation, not here.

## Context

A QA job order (2026-10-23) asked for Classification to be removed from three places: the Students
table, the roster import template, and the student model's display and requirements.

ADR-005 built classification as a vocabulary (`Classifications`) plus a per-axis junction
(`StudentClassifications`), fed by four optional `*_CATEGORY` columns in built-in import profile
version 3 (D-59). It is shown on every read that returns a student, the device `by-card` contract
included (D-65). Three facts decide how far a removal can go:

1. **The tables hold data, and the hard rule forbids losing it.** `DROP` or `ALTER` without
   data-preserving SQL is out (global hard rule; repo `CLAUDE.md`). Removing classification from the
   *schema* would need a preserving migration and buys QA nothing they can see.
2. **The import profile is versioned (ADR-001 D-4).** Each mapping change is a new version, and a batch
   runs under the version it was uploaded with. Removing the category columns is therefore a new
   built-in version, not an edit to version 3.
3. **Technical Plan §4.3 (`Students`) never defined classification.** ADR-005 recorded the concept as
   additive drift. Taking it off the product brings the product closer to the plan.

## Options Considered

- **Remove it from the schema too (drop the tables).** Rejected: data loss under the hard rule, and
  irreversible. QA asked about what users see, not about storage.
- **Hide it in the SPA only, keep the DTO and the profile.** Rejected: the template would still ask
  for category columns, and every API consumer would keep receiving a field the product no longer
  stands behind.
- **(Chosen) Withdraw it from the product surface and keep the schema and the code paths dormant.**
  Fully reversible, needs no migration, and every QA point is covered.

---

## Decision

### D-76: Student classification is withdrawn from the product surface, not from the schema

**What is withdrawn**

- **The built-in roster import profile moves from `BuiltInVersion` 3 to 4, and v4 maps no
  `*_CATEGORY` column. This supersedes D-59's profile.** Versions 1–3 stay, superseded, per ADR-001
  D-4. A batch pinned to v3 is still explained by v3, and the rows it wrote stand. **No migration is
  needed**, because the built-in profile row is written when a school uploads
  (`EnsureBuiltInProfileAsync`), not by a migration. D-59's mechanism (the axis in the dotted
  `TargetField` string, four optional columns) is not deleted. It is simply not mapped by v4.
- **A batch pinned to v3 still classifies and still warns, after the deploy as well as before it.** The
  run reads the profile the *batch* points at (`ImportProfileId`, read by
  `SisImportService.ResolveMappedColumnsAsync`), not the active one. So a v3 batch uploaded before the
  backend deploy and run afterwards, or a `Failed` v3 batch retried afterwards, still writes
  `StudentClassifications` rows and still raises the `Classification*` warnings, although no screen
  shows a student's classification any more. This is ADR-001 D-4 working as designed, not a leak. It
  is also why the SPA keeps the `Classification*` warning-chip labels in `web-admin/src/sisImport.ts`,
  and a code comment there says so: v3 batches carry those codes in their history, and any profile that
  maps category columns can still raise them. The release step is in the
  [before-backend-deploy checklist](#before-the-backend-deploy-release-note).
- **`StudentDto` no longer carries `classifications` on any read: list, detail, the write responses,
  or the `by-card` device contract. This supersedes D-65's DTO half.** The change is recorded for the
  mobile developer in `docs/api/mobile-changes.md` (2026-10-23 entry). **D-65's other rulings stand.**
  No `cards.read` or `classifications.read` code is minted: `CardsController` (live, D-64) and the
  dormant `ClassificationsController` / `StudentClassificationsController` still declare
  `students.read` / `students.write`. D-65's write-surface ruling (no axis in the URL or the body; the
  replace is one guarded `ExecuteUpdate`) and its deadlock-as-409 ruling also stand, **dormant** with
  the `/students/{id}/classifications` routes they govern.
- **The roster template lists category columns and their vocabulary only when the profile the
  school's next upload will be pinned to maps a category column.** That is
  `SisImportProfileResolution.FindCurrentAsync`, the rule the importer itself uses, and it is not "the
  active profile". v4 maps none, so the template carries no category columns and no category vocabulary
  in either sheet. This includes a school whose v3 is still the active row: its next upload will
  create v4 and pin to it, so the template correctly shows v4, not v3.
- **The SPA Students grid has no Classification column and no per-row classify action.** The FE
  classification dialog and its client methods are deleted. Git keeps them.

**What is kept dormant, unchanged**

- The `Classifications` and `StudentClassifications` tables and every row in them. No `DROP`, no
  `ALTER`.
- `/students/{id}/classifications` and the `/classifications` vocabulary endpoints.
- The D-57 seed, D-58's retire/merge/tombstone lifecycle, and D-68's `SeedProtected` delete refusal.
- The importer's category resolution (D-56), first-write-wins (D-60), warn-once memory (D-61), and the
  D-62/D-63 warning codes. **None of these can be reached under v4**, because no category column is
  mapped. All of them come back for any profile that maps category columns. Their tests move to a
  **category-profile fixture one version above the built-in** (`BuiltInVersion + 1`: version 5 when
  this was written, version 6 since ADR-010 moved the built-in to 5), so the path stays covered while no shipped profile uses it. The
  fixture is `CategoryMappingProfile` (`backend/EAMS.Tests/Integration/Infrastructure/`). It seeds an
  active operator profile at `BuiltInVersion + 1` that maps the four category columns, and
  `SisImportClassificationTests` and the other classification import suites run on it. On that fixture,
  `SisImportClassificationTests.Every_StudentClassification_touch_names_a_row_in_StudentClassifications`
  checks that every `StudentClassification` fan-out touch the importer records, `Inserted` and
  `Unchanged` both, names a row in `StudentClassifications`. The pipeline suite's whole-table id
  check can no longer reach that type, because the built-in v4 roster records no such touch.

**What is not affected.** `Personnel.Classification`, event classifications (`EventClassifications`)
and the `EmployeeClassification` audience criterion. None of them reads `StudentClassifications`.

**Technical Plan.** §4.3 never defined classification, so this reduces recorded drift. The dormant
`Student.Classifications` navigation and the two tables remain as drift ADR-005 already registered,
now with no product surface over them.

---

## Consequences

### Positive
- All three QA points are met with no migration, no data loss and no new schema.
- Fully reversible. **Reversal** is a later built-in profile version that re-adds the four category rows, plus
  the `StudentDto` field. The data is still there.
- ADR-005's importer logic, its warning codes and its tests are kept. A school whose export does carry
  category columns gets it back as a profile version, not a rebuild.
- D-65's GUID-literal batch load, which it recorded as **unmeasured at roster scale**, is now off the
  hot path. No student read calls `StudentClassificationReads.ForStudentsAsync` with a page of ids any
  more. Its one remaining caller is the dormant `GET /students/{id}/classifications`, which takes the
  single-id branch (a parameterized equality, no literals). The students grid no longer pays that cost.

### Negative
- **This is a field removal from a mobile-consumed body, which ADR-005 D-65 said it must not be used as
  precedent for.** D-65 justified *adding* `classifications` to `by-card` by contrast with ADR-004
  D-40, which refused a *removal* from a mobile body. D-76 is a removal. A tolerant reader ignores a
  missing optional member. A client that requires the field breaks. That is why the change goes
  through `docs/api/mobile-changes.md` before release, and it should be confirmed with the mobile
  developer rather than assumed harmless.
- **Deploy order: FE before BE.** The pre-change SPA's `toStudent` requires `classifications`, so a
  BE-first deploy breaks the Students page for anyone still on the old bundle.
- **Old v3 templates still carry the category columns, and those columns are now silently ignored**
  by a batch uploaded under v4. This is D-4 working as designed (the same shape D-59 recorded for
  version ≤ 2), and it looks like a bug to anyone who does not know the profile moved.
- Classification code now lives in the tree with no shipped caller of its import path. A reader may
  delete it as dead code. This document and the category-profile fixture tests are what argue against that.
- ADR-005 D-61's open recovery question ("decide recovery before this ships") goes quiet rather than
  away. It cannot fire under v4, and it returns with any profile that maps category columns.

### Neutral
- QA's "CSV import" was read as the **roster XLSX template**, since the roster import is XLSX only. The
  **Personnel CSV import keeps its Classification column**. That is a separate field
  (`Personnel.Classification`), out of scope by JJ's call.
- `docs/api/openapi.json` changes, because `StudentDto` loses a member (ADR-004 D-38: the generated
  document is the contract).
- Decision numbering stays continuous: ADR-001 D-1…D-6; ADR-002 D-7…D-11; ADR-003 D-12…D-21;
  D-22…D-53 across Phase 3b–5 docs/MDVault; ADR-004 D-54 and its parts; ADR-005 D-55…D-68; ADR-007
  D-69…D-70; ADR-008 D-71…D-75; this ADR **D-76**. The next ADR continues from **D-77**. **ADR-006
  remains reserved** for the consolidating ADR. See the numbering note at the top for the MDVault #263
  overlap.

### Before the backend deploy (release note)

1. **Deploy the FE before the BE** (see Negative above).
2. **Check for an existing v4 profile row on the VM:**
   `SELECT SchoolId, Name, Version, IsActive FROM dbo.SisImportProfiles WHERE Version >= 4;`
   Before this change `BuiltInVersion` was 3, so no build before this one can have written a built-in
   row this returns. Any row it returns is operator-authored. If its `Name` is the built-in name
   (`CICSS Faculty Evaluation Report`), the importer takes it as the built-in v4, because
   `FindCurrentAsync` matches on name key and version only. Uploads would then pin to that row's
   mapping, category columns included if it maps any. Settle any such row with JJ before deploying.
3. **Run or set aside every `Pending` or `Failed` v3 batch.** After the deploy they still classify and
   warn (D-76, "A batch pinned to v3"). There is no discard route: setting a batch aside means agreeing
   it will not be run or retried. To list them:
   `SELECT b.Id, b.SchoolId, b.Status, p.Version FROM dbo.SisImportBatches b JOIN dbo.SisImportProfiles p ON p.Id = b.ImportProfileId WHERE p.Version = 3 AND b.Status IN ('Pending', 'Failed');`
4. **Expect a possible 500 on concurrent first uploads.** A school's first upload after the deploy
   creates v4 (`EnsureBuiltInProfileAsync`). Two uploads racing for one school can both find no v4 and
   both insert it. The loser fails on `UX_SisImportProfiles_School_Name_Version` (or `…_Active`) and
   gets a 500. A retry finds v4 and succeeds. This race is pre-existing: every built-in version bump,
   and every school's first-ever upload, has had it. A retry wrapper is a separate ticket.

## Follow-Up Actions

These are proposals, not authorised by this document. JJ decides.

- [ ] JJ reads and accepts (or amends) this ADR.
- [ ] Confirm with the mobile developer that dropping `classifications` from `by-card` breaks nothing
      on the device (the D-40 caution, now for a removal).
- [ ] Work through the before-backend-deploy checklist below, and put it in the release note.
- [ ] Tell QA that a v3 template's category columns are ignored under v4, so "I filled them in and
      nothing happened" is expected behaviour.
- [ ] Carried forward, unchanged: ADR-005's open follow-ups (now dormant with the path), and the
      still-unwritten consolidating ADR (ADR-006), which should also settle the MDVault #263
      numbering overlap.
