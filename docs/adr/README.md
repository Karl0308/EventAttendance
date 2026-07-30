# Architecture Decision Records

**Read this index first.** ADRs here are immutable once `Accepted` — a later ADR amends an earlier
one *by reference*, and the earlier one is never edited to point forward. That rule keeps the
chronology honest (you can tell which decisions were made before the real roster was in hand), but
it creates one hazard this file exists to close:

> **An `Accepted` ADR cannot tell you it has been amended.** Open ADR-001 alone and you will find six
> unticked follow-up boxes, four of which ADR-002 has since closed. The boxes are not the record —
> this index is.

## The register

| ADR | Title | Status | Notes |
|---|---|---|---|
| [001](ADR-001-schema-drift-from-technical-plan.md) | Schema drift from the Technical Plan | **Accepted** | D-1…D-6. **Amended by ADR-002** — do not read its follow-up list as current |
| [002](ADR-002-phase-1-2-schema-and-import-decisions.md) | Phase 1–2 schema and import decisions | **Accepted** (2026-07-28) | D-7…D-11. Amends ADR-001 by reference; closes four of its follow-ups. **Now immutable** |
| [003](ADR-003-phase-3a-event-audience-and-close-freeze.md) | Phase 3a — the event audience and the close-time freeze | **Accepted** (2026-07-29) | D-12…D-21. Amends 001 and 002 by reference; closes GAP 6. The first ADR about *behaviour* rather than schema. **Now immutable** |

Decision numbers run continuously across documents, so `D-9` is unambiguous without naming the ADR.

## Decided, but not yet registered

An index that only lists written ADRs cannot warn you about a decision nobody wrote down. This is
that warning.

Decision numbers run continuously, so a gap in this list is itself a signal. **D-22 through D-43 are
all unwritten.** Twelve of them are cited by number in shipped production code, comments and tests.

> **⚠ D-43 reverses a fact ADR-001 still asserts, and ADR-001 cannot say so itself.** ADR-001 is
> `Accepted` and therefore immutable — it goes on stating that REGNO *is* the card UID at its lines 19,
> 124–125 and 254, and roughly a dozen comments in the codebase now cite ADR-001 for the opposite.
> **This row is the amendment** until the ADR-004 consolidation absorbs it. It is the exact hazard the
> top of this file describes, so it is recorded here rather than by editing an accepted document.

| Pending | Decision | Status | Lives only in |
|---|---|---|---|
| **D-22** | A soft-deleted student's card may be **deactivated**, but a new card may not be **issued** to them. Releasing what a deleted student holds is bookkeeping; issuing to them is a claim | shipped 3b-1 | `StudentService.DeactivateCardAsync`, `StudentSoftDeleteStrandingTests` |
| **D-23** | Device authentication is a real ASP.NET Core auth scheme emitting the claim shape Phase 6's JWT will emit, not bespoke middleware. `ISchoolContext` becomes claims-reading, with one dev fallback Phase 6 deletes | shipped 4b | `DeviceKeyHandler`, `ClaimsSchoolContext` |
| **D-24** | API keys are split tokens — public id indexed, 256-bit secret SHA-256 hashed. Fast hash is correct *because* the secret is server-generated. §4.10's `ApiKey` kept and permanently NULL, not dropped | shipped 4b | `DeviceKey`, `DeviceAuthenticator`, `RowLevelTenancy` migration |
| **D-25** | Key lifecycle: plaintext shown exactly once; hard-cut rotation, no overlap window; `ApiKeyRevokedAt` (burn credential) distinct from `IsActive` (retire device); no key caching | shipped 4b | `DeviceService`, `DeviceLifecycleTests` |
| **D-26** | Device identity comes from the authenticated principal via `IDeviceContext`, never from `TapRequest.DeviceId`. Body field retained as a cross-check; mismatch is `400 DeviceMismatch`, never a silent ignore | shipped 4b | `AttendanceService.TapAsync` |
| **D-27** | An explicit `device.SchoolId == event.SchoolId` check reported as the existing `DeviceNotRegistered` → 404 — no cross-tenant existence disclosure, no wire change. **Closed the cross-school tap defect** | shipped 4b | `AttendanceService`, `KnownDefectTests` |
| **D-28** | Enforcement narrowed to the capture endpoints only; `[HasPermissionNotEnforced]` stays alongside `[Authorize]`; no config off-switch; a Development-seeded device supplies the local key. **ADR-001 D-6's do-not-expose constraint stays in force** | shipped 4b | `Program.cs`, `AuthorizationSeamTests` |
| **D-29** | Live attendance is a cursor-delta polling endpoint. The SignalR hub of §5/§6.4 is **deferred** behind a delta DTO that is a superset of §6.4's declared payload | shipped 4d | `EventService.GetLiveAttendanceAsync`, `AttendanceService.TapBatchAsync` |
| **D-30** | A `rowversion` cursor on `AttendanceRecords`, mapped **non-concurrency** so write semantics are unchanged | shipped 4d | `EventService.GetLiveAttendanceAsync`, `AttendanceService.TapBatchAsync` |
| **D-31** | `POST /attendance/tap/batch`: per-row results correlated by index **and** `deviceTapId`; always HTTP 200 for a well-formed batch, never 207. **Retrying a whole batch after a `5xx` is safe because every row is idempotent — not because the batch is atomic.** Rows are committed individually (D-32), so a partial failure leaves earlier rows written | shipped 4d | `AttendanceService.TapBatchAsync`, `TapBatchRequest`/`TapBatchResult` schemas (D-38) |
| **D-32** | Batch rows processed in ascending `tappedAt`, each in its own transaction, through the **same decision function** as `/tap` | shipped 4d | `EventService.GetLiveAttendanceAsync`, `AttendanceService.TapBatchAsync` |
| **D-33** | `deviceTapId` **required** on the batch path, optional on single tap, and **bounded at the column's 100 characters**. An over-length value is a truncation error rather than a unique violation, so it escapes the recovery filter and surfaces as an unrecoverable batch `5xx` that an offline queue retries forever | shipped 4d | `AttendanceService.DecideAsync`, `TapBatchLimits`, the retained companion doc (D-38) |
| **D-34** | A check-out gets its **own** idempotency key and its own filtered unique index, so each half of a `TimeInOut` pair is independently retryable and the guarantee is index-backed | shipped 4c | `AttendanceService.FindByDeviceTapAsync`, `KnownDefectTests` |
| **D-35** | `SchoolId` denormalized onto `AttendanceRecords` and the idempotency index re-scoped, closing the filter/constraint divergence before a queue drain could make its permanent-500 loop reachable | shipped 4b | `RowLevelTenancy` migration, `AttendanceTenancyTests` |
| **D-36** | `tappedAt` is **never rewritten**. Future taps beyond 5 minutes rejected; `tappedAt` validated against a configurable event window; submission lateness unconstrained | shipped 4c | `TapTimeWindow`, `TapTimeWindowTests` |
| **D-37** | `code` (the outcome member name) ships on every tap body, success and failure; failures become RFC 7807. **Outcome tokens are frozen published contract**, pinned by a test | shipped 4c | `TapOutcomeContractTests`, `TapOutcomeCode` schema (D-38) |
| **D-38** | **The generated OpenAPI document is the contract**, superseding the hand-written `attendance-contract-handoff.md`. That file is retained, cut to what a schema cannot express — queue actions per token, UID normalisation, the never-rewrite-`tappedAt` rule, the open questions. Descriptions are generated from the XML comments, so contract and code cannot drift | shipped 4e | `EamsOpenApi`, `OpenApiDocumentTests`, `docs/api/attendance-contract-handoff.md` |
| **D-39** | The device credential is published as an OpenAPI **`apiKey` in the `Authorization` header**, not as `type: http, scheme: DeviceKey`. The `http` form is semantically exact but its `scheme` is defined against the IANA registry, and generators reject or silently drop an unregistered value — leaving a generated client unable to send the header at all. Cost: the caller supplies the `DeviceKey ` prefix itself | shipped 4e | `EamsOpenApi.DeviceKeyScheme` |
| **D-40** | `TapResult.success` is **deprecated in the schema, not removed**. It is redundant with `code`, but 4c already changed that body once and the mobile developer's question about whether it broke him is unanswered; a second breaking change to the same body before he replies is not ours to make | shipped 4e | `ContractSchemaFilter`, `The_redundant_success_flag_is_deprecated_rather_than_removed` |
| **D-41** | The Swagger **UI is Development-only; the generator is registered unconditionally**, so tooling can build the contract from a production binary while ADR-001 D-6's do-not-expose constraint holds. Collapsing the two is the tidy-up that one test exists to stop | shipped 4e | `Program.cs`, `The_document_generates_in_production_even_though_it_is_not_served` |
| **D-42** | The live endpoint's counters are bounded by the **cursor actually returned**, not by the read ceiling. 4d shipped them unbounded and said so; 4e's first attempt bounded them by the ceiling, which fixed the long-transaction case and left the same defect on every truncated page. **Closes 4d's known-and-accepted counter defect** | shipped 4e | `EventService.SummaryForAsync`, `A_snapshot_is_capped_and_the_remaining_rows_page_through_the_cursor` |
| **D-43** | **REGNO is not the RFID card UID — they are separate columns** (client correction, 2026-07-30, reversing the 2026-07-28 assumption ADR-001 was written on). The serial is a decimal string, ten digits in the sample, **leading zeros significant**; it is what a tap authenticates on, and REGNO is not a tap identity. Consequences: the import maps the serial through a **profile column** (D-4) rather than a hardcoded header, since the client's export carrying it has not arrived; the column is **optional**, so a student with no serial imports with no card and that is the ordinary case, not an error; a batch pinned to a pre-correction profile still runs its own mapping per D-4 but now **warns** (`RfidCardFromLegacyMapping`) instead of minting student-number cards silently; and profile resolution refuses to fall back to an *older* active version, which had made the same defect reachable on new uploads | shipped 2026-07-30 | `SisImportProfileTemplate`, `SisImportService.ReadCardUid` / `IsLegacyCardUidColumn` / `EnsureBuiltInProfileAsync`, `SisRosterColumns.RfidCardSerial`, `SisImportPipelineTests`, `docs/api/attendance-contract-handoff.md` |
| **D-44** | **`academic.read` stays.** The permission code Phase 3b-2 minted for the ADR-001 D-1 academic reads is kept rather than folded into `students.read`. §6's route tables predate the academic layer and assign it nothing, so the code is an *addition* to the plan's permission map and not a contradiction of it — which is the whole of what separates it from D-45. Reusing `students.read` would have pre-answered a question Phase 6 should get to answer on its own | pending gate | `EamsPermissions.AcademicRead`, `AcademicController`, `PermissionRegistryTests` |
| **D-45** | **`GET /student-groups` declares `students.read`, and `groups.read` is deleted outright.** Technical Plan §7.1 line 500 already assigns `students.read` to the frontend's `/groups` page; 3b-2 minted a second code without reading it. The plan is source of truth for the permission map, so the minted code was a contradiction. **Not kept as a synonym** — two codes for one page is the drift the registry exists to stop, and a synonym has to be granted twice by every role Phase 6 writes. Pinned by absence, so re-minting it fails a test rather than passing review | pending gate | `EamsPermissions.StudentsRead`, `StudentGroupsController`, `The_groups_read_code_D_45_deleted_has_not_come_back` |

Deferred to the ADR-004 consolidation at JJ's direction, not forgotten.

> **What D-43 does NOT close.** The import layer is corrected; the **binding question is still open** —
> the roster in hand has no RFID column, so every student currently imports with no card and every tap
> against them is `CardNotFound`. Whether cards get bound in bulk from the client's next export or in
> the field (a screen in the mobile app and an endpoint here, neither of which exists) is unanswered and
> is the mobile developer's largest open scope item.

> **D-42 is the one to read if you only read one.** The defect it closes was *documented* by 4d rather
> than fixed, then half-closed by 4e in a way that left three comments — two of which generate into the
> published contract — asserting an invariant that was false on any page of more than 500 rows. A
> generated contract that states something untrue is worse than the hand-written one it replaced,
> because the whole premise is that it cannot be. Found at the review gate, not by the suite.

> **Two of these have the silent-failure shape this file exists to catch.**
>
> **D-36's settings read is deliberately unconditional.** "Check the defaults first, load configuration
> only if that fails" looks like a free win in review. It silently ignores any school that *narrows*
> its window — the direction an administrator tightening a rule moves — and only a school widening it
> would ever notice. `A_school_can_narrow_its_window` is the test that dies under that optimisation,
> and it is the only one that does.
>
> **D-24's fast hash is correct only under its precondition.** SHA-256 rather than a slow KDF is right
> *because* the secret is server-generated with 256 bits of entropy — `DeviceKey.Issue()` takes no
> parameters precisely so no caller can weaken that. The moment anyone lets an operator choose a key,
> the choice becomes wrong, and nothing about the hashing code would look different.

## What is decided where

| Question | Answer lives in |
|---|---|
| Why do we deviate from Technical Plan §4 at all? | ADR-001, Context |
| The academic layer (twelve tables) | ADR-001 **D-1**, table split recorded in ADR-002 **D-8** |
| Why `Students.Course/YearLevel/Section` are a derived cache | ADR-001 **D-2** |
| Which enrollment that cache shows | ADR-002 **D-9** |
| Why `RfidCards.CardUid` uniqueness was rescoped | ADR-001 **D-3** |
| Import mapping config, warning-vs-status semantics | ADR-001 **D-4/D-5**, refined by ADR-002 **D-10** |
| Why auth is deferred and what makes that safe | ADR-001 **D-6** |
| Two email columns | ADR-002 **D-7** |
| **The two natural-key hedges — and their expiry** | ADR-002 **D-11** ⚠ |
| Why `EventGroups` has two filtered unique indexes | ADR-003 **D-12** ⚠ (one of them is denominator arithmetic) |
| **Why a closed event has its audience stored twice** | ADR-003 **D-13** ⚠ |
| Why `Closed` is terminal, and how to correct one anyway | ADR-003 **D-14**, escape hatch in **D-17** ⚠ |
| Why soft-deleted students are in a frozen denominator but not a live one | ADR-003 **D-15** |
| Why cancelling freezes but writes no absentees | ADR-003 **D-16** |
| The event status transition matrix | ADR-003 **D-18** |
| What `Expected` / `AttendanceRate` mean, and why a walk-in is not blocked | ADR-003 **D-19/D-20** |
| Why the freeze is not the plan's Hangfire batch job | ADR-003 **D-21** |

## Items with silent or unrecoverable failure modes

Each of these is a decision that fails **without an error and without a failing test**. They are
listed here because that is exactly the class of thing nobody re-derives from the code.

### ADR-002 D-11 — the course-key hedge (unrecoverable)

`Courses` is keyed `(SchoolId, CodeKey)` on the unverified assumption that course
codes are unique across colleges. Only one college's export has ever been seen. The widening path
(backfill `CollegeId` → `NOT NULL` → re-index) is **data-preserving only while no colliding data has
been imported**; afterwards it becomes a split requiring live-FK re-keying of half of
`CourseOfferings`.

The importer hard-fails a cross-college code collision and its message cites `ADR-002 D-11` by name.
**Do not downgrade that failure to a warning to clear a blocked batch** — that is precisely the
moment the hedge gets silently reversed.

The cheapest way to retire this: ask the registrar whether course codes are unique university-wide.
One question.

### ADR-003 D-13 — a terminal event stores its audience twice, on purpose

The group rows say *which section* was invited; the student rows **are** the denominator. Deleting
either set looks like removing a redundancy and neither deletion breaks a test: drop the group rows
and the event can no longer say who it was for; drop the student rows and the denominator silently
starts drifting with every roster import again — which is the bug the whole phase exists to fix, and
which passed a full green suite once already.

### ADR-003 D-12 — `UX_EventGroups_Event_Student` is arithmetic, not tidiness

The frozen denominator read is deliberately not `Distinct`-wrapped. That index is the only thing
preventing a double-counted student in a closed event's expected count. It reads like a dedupe nicety
and is not one.

### ADR-003 D-17 — `POST /attendance/manual` must keep working on a closed event

It is the only recovery route out of a terminal status (D-14). Adding a status guard to it —
a change that reads as a *safety improvement* in review, since the tap path has one — turns D-14 into
a dead end with no way back. Nothing tests this today.

## Conventions

- Structure: Context → Options Considered → Decision → Consequences (positive / negative / neutral) →
  Follow-ups.
- `Proposed` means written but not yet agreed by the deciders it names. **Do not self-accept** an ADR
  listing someone else as a decider — an `Accepted` record of a decision nobody made is the exact
  failure the practice exists to prevent.
- A `Proposed` ADR is freely editable. An `Accepted` one is not: supersede it instead, and update this
  index.
- A consolidating ADR superseding 001–003 is **now due at ADR-004**, not merely worth considering.
  Four documents is where "read them together" stops being followed. The chronology is still
  load-bearing, so the consolidation must preserve *when* each decision was made and with what in
  hand — particularly ADR-003 D-13, which is only comprehensible in the order it happened.
