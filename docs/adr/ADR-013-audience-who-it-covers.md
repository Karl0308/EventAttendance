# ADR-013: An Event Audience Says Who It Covers (Students, Employees or Both)

**Status**: Proposed (2026-10-26). Awaiting JJ's read-through; JJ then flips it to `Accepted`.
> The *decisions* below are JJ's (2026-10-26, answering QA's "BACKOFFICE Task 5: Add 'Who it Covers'
> Filter to Event Audience UI"). This *document* writes them down and has not yet been read by him. The
> register's rule holds: JoseArch recommends, JJ accepts. **Do not self-accept.**

**Date**: 2026-10-26
**Deciders**: JJ; JoseArch (Team X)
**Relationship to other ADRs**: **Builds on ADR-011 (D-82)**, which is also `Proposed`. **Amends by
reference** ADR-011 **D-82**: an Organization audience still *matches* both rosters, but can now be
narrowed to one. **Restates, unchanged,** ADR-007 **D-69** (definitions are a live link) and **D-70**
(students counted, personnel advisory), and ADR-003 **D-13** (a terminal event's frozen rows are its
denominator). No schema, so no ADR-001 drift. **ADR-006 remains reserved** for the consolidating ADR.

> **Numbering note.** D-88…D-91 are used nowhere in the repository (code, tests or docs; checked
> 2026-10-26). They follow D-87 (ADR-012) and sit clear of the unregistered Phase 6 auth range (MDVault
> #263, D-60…D-84).

## Context

QA: an admin creating or editing an event audience must say whether it targets **Students, Employees or
Both**, "to prevent incorrect audience matching" — a *Computer Science Society* should reach students
only, a *Faculty Club* personnel only. Five facts decide the shape:

1. **The criterion already exists.** `AudienceCriteriaDto.Scope` with the values in `AudienceScope`
   (`Students`, `Employees`, `Both`), stored inside `AudienceDefinitions.CriteriaJson`. Today only
   `UniversityWide` reads it (`AudienceResolution.BuildQueries`); the server already requires it there.
2. **Three types match both rosters.** `Department` (`s.Course` / `p.Department`), `Organization` (both
   since D-82) and `Custom` (each side ANDs the criteria that apply to it; a side with none stays null).
   `Program`, `YearLevel`, `Section` are student-only, `EmployeeClassification` personnel-only, and
   `SpecificIndividuals` is explicit picks.
3. **`BuildQueries` is the single resolver** (ADR-003 D-19): the resolve preview, the live denominator
   (`EventService.DefinitionStudentIdsAsync`, student side only), the attached-audience read and advisory
   count (`ReadAttachedDefinitionsAsync`), and the terminal freeze (same method, `includeDeleted: true`).
4. **An event's link does not copy the criteria.** `EventAudienceDefinition` is `(EventId,
   AudienceDefinitionId)` and nothing else; every read joins `link.AudienceDefinition.CriteriaJson` as it
   stands now (D-69's live link, by design). See R1.
5. **Stored definitions carry no scope for mixed types.** The SPA's `criteriaForType` prunes `scope` from
   every type except `UniversityWide`, so existing rows have none.

## Options Considered

- **(Chosen) A: reuse `scope` on the three mixed types, as a mask after matching.** No column, no
  migration, no new wire field. One `switch` stays the matcher; the mask is a few lines after it.
- **B: a new `Population` column on `AudienceDefinitions`.** Rejected (JJ ruling 1). A migration and a
  second home for a fact `scope` already models, with `UniversityWide` keeping the old one.
- **C: split types (`StudentOrganization`, `EmployeeOrganization`, …).** Rejected. Six new type values,
  a data migration for every stored row, and `AudienceType.All` stops being the documented nine.
- **D: offer the control on every type.** Rejected (ruling 2). On a single-population type two of the
  three answers resolve nobody; a control whose wrong answers are silent is the defect QA is reporting.
- **Baseline: do nothing; admins pick criteria carefully.** Rejected. `Department` and `Organization`
  cannot express one roster at all, so the ask is unmeetable without a change.

---

## Decision

### D-88: `scope` is reused on Department, Organization and Custom. No migration

- **The existing `AudienceCriteriaDto.Scope`**, values `AudienceScope.All`, stored in `CriteriaJson`. No
  column, no migration, no new DTO field. The DTO's doc comment ("University-wide only") is corrected.
- **Mixed types only**: `Department`, `Organization`, `Custom`. `Program`, `YearLevel`, `Section`,
  `EmployeeClassification` and `SpecificIndividuals` neither take nor read it (the SPA keeps pruning it
  for them). `UniversityWide` is unchanged: its scope still selects the side outright.

### D-89: Required on every create and edit; a stored row without one reads as Both

- **On `POST` and `PUT` of a mixed type, `scope` is required, with no default.** A missing or unknown
  value is refused by `AudienceDefinitionService.CriteriaRefusal` (via `AudienceScope.TryNormalize`), and
  the message lists `AudienceScope.All`. The SPA mirrors it: save is blocked until a value is chosen.
- **A stored mixed-type row with no scope, or an unreadable one, resolves as `Both`** — exactly today's
  behaviour, so no live denominator moves on deploy. It stays so **until edited**; the edit form then
  shows no selection and the admin must pick (R3).
- *Why no default on edit:* a pre-filled `Both` would let a re-save launder a legacy row into an explicit
  `Both` nobody chose, and the "until edited" boundary would mean nothing.

### D-90: Scope is a mask applied after matching. It never widens, and a Custom that leaves no side is refused

- **After the per-type `switch`, for mixed types only:** `Students` → personnel side `null`;
  `Employees` → student side `null`; `Both` / missing / unreadable → unchanged. In-memory nulling of a
  query, so no SQL shape changes.
- **It never widens.** Under `Both`, a Custom side with no criterion stays `null`; the mask can only
  remove a side the match produced, never add one.
- **A Custom whose scope leaves no side is refused at save.** `Students` with only `classifications` /
  `personnelIds`; `Employees` with only `programs` / `yearLevels` / `sections` / `studentIds`. Such a
  definition would resolve nobody, silently. `Department` and `Organization` always produce both sides,
  so the rule cannot fire for them.
- **The SPA hides off-scope pickers in Custom**: under `Students`, Classifications; under
  `Employees`, Programs, Year levels, Sections (Custom has no individual pickers). Their values
  are dropped from the saved criteria, so a hidden value can never narrow a query invisibly.
- **D-70 holds.** The student side still feeds the denominator, the freeze and the manifest; the
  personnel side stays advisory. A `Students` scope zeroes a definition's advisory count; an `Employees`
  scope makes it contribute nobody to the denominator.

### D-91: One label list everywhere, University-wide included

- **Field label "Who it covers"; options `Students` / `Employees` / `Both`, in that order**, from the one
  shared `AUDIENCE_SCOPES` list. University-wide's current labels ("Students and employees", "Students
  only", "Employees only") are replaced by the same three. Wire values are unchanged.

### What this amends, by reference

- **ADR-011 D-82.** Organization still matches both rosters with D-82's predicate; the new `scope` can
  narrow it to one (D-90). D-82's R1/R2 (Custom organizations ANDed on both sides) stand, now narrowable.
- **ADR-007 D-69/D-70.** Restated, unchanged: links stay live, personnel stay advisory.
- **ADR-003 D-13.** Restated, unchanged: Closed and Cancelled events are frozen and do not move.

### Technical Plan drift

None. No table, column or route changes; `scope` already exists in the contract and its documented reach
widens from one type to four (a doc-comment change on one DTO field).

---

## Consequences

### Positive
- QA's ask is met with no migration, no new field and no backfill. Rollback is a plain revert.
- Organization and Department audiences can finally express "students only" / "employees only".
- One resolver still: the preview, the denominator, the advisory count and the freeze cannot disagree.

### Negative
- `scope` now means two things: on University-wide it *selects*, on mixed types it *masks*. Same values,
  same label, different mechanics (the never-widens rule only matters for the second).
- Legacy rows stay implicitly `Both` indefinitely if nobody edits them.

### Risks
- **R1 (live link, open events move on edit).** Links copy nothing (Context 4), and `UpdateAsync` has no
  guard for attached definitions. **Editing a definition — now including setting its scope — re-resolves
  immediately for every `Draft` or `Open` event it is attached to**: denominator, attendance rate,
  manifest hash and advisory count all move mid-event. Closed/Cancelled events are untouched (D-13), as is
  an event with a linked pre-registration session (ADR-008 D-71: it resolves no definition). This
  is D-69 working as designed, but the new required pick makes edits of attached definitions likely right
  after deploy. *Mitigation:* tell QA and admins; whether to warn (or refuse) on editing a definition
  attached to an open event is for JJ (follow-up).
- **R2 (denominators drop).** Re-scoping a `Department`/`Organization`/`Custom` to `Employees` removes its
  students from the denominator; to `Students`, removes its personnel from the advisory count. Expected,
  not a defect — but a rate on an open event will jump.
- **R3 (QA must re-pick on edit).** Every legacy mixed-type definition opens with no selection and refuses
  to save until one is chosen. Test plans that edit an existing audience gain a step.
- **R4 (deploy order and rollback).** The old SPA prunes `scope` from mixed types, so against the new
  server every mixed-type save is refused: **deploy FE before BE**, as ADR-009…012. In the FE-first window
  the old server stores the chosen scope (it serializes the whole criteria object) but ignores it, so
  previews show `Both` counts until the BE lands. On rollback, legacy rows are unaffected and new rows'
  scope is again ignored except on University-wide — they silently widen back to `Both`.

### Neutral
- Test fixtures that POST a mixed type without scope gain `scope = Both` and go red by design until they
  do. Seeded-row fixtures are deliberately left scope-less: they pin "no scope reads as Both" (D-89).

## Follow-Up Actions

These are proposals, not authorised by this document. JJ decides.

- [ ] JJ reads and accepts (or amends) this ADR together with ADR-011 and ADR-012.
- [ ] Tell QA, in the Task 5 note: R1 (an edit moves open events), R2 (rates jump on re-scope), R3
      (legacy audiences need a pick on edit), and the new University-wide labels (D-91).
- [ ] JJ to rule whether editing a definition attached to a `Draft`/`Open` event should warn or be
      refused (R1). Today neither happens, before or after this ADR.
- [ ] Pin D-90's never-widens and refusal rules, and D-89's legacy-reads-as-Both, as named tests.
