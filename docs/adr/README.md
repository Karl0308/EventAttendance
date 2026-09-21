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
| [004](ADR-004-detaching-the-roster-import-run.md) | Detaching the roster import run from the HTTP request | **Accepted** (2026-08-27) | **D-54** and its eight parts (D-54.1…D-54.8). Amends 001/002/003 by reference. **Not the consolidation** — see the note at the bottom of this file. **Now immutable** |
| [005](ADR-005-classifying-the-roster.md) | Classifying the roster — what kind of person each row is | **Proposed** (2026-09-17) | **D-55…D-68**. Amends 001/002/003/004 by reference. **Also not the consolidation** — see the note directly below. Freely editable while `Proposed`. D-68 was added *after* the review gate, which is why it sits apart from D-55…D-67 in the document |

Decision numbers run continuously across documents, so `D-9` is unambiguous without naming the ADR.

> **⚠ ADR-004 took the number the consolidation was reserved for, and ADR-005 has now taken the next
> one. This is the THIRD deferral.** ADR-002 anticipated a consolidating ADR "around ADR-004";
> ADR-003's follow-up list calls for it *at* ADR-004; ADR-004 took 004 for a production defect fix and
> moved it to 005; **ADR-005 has taken 005 for the classification slice and JJ's call is that the
> consolidation moves to ADR-006.** It is said plainly here and in ADR-005's own header rather than
> quietly — three deferrals of one document is a fact about the practice, not a scheduling accident.
> ADR-003 and ADR-004 are `Accepted` and immutable, so their follow-up boxes still say ADR-004 and
> ADR-005 and always will. **This index is the amendment**, which is the hazard the top of this file
> describes, arriving for the **third** time.

## Decided, but not yet registered

An index that only lists written ADRs cannot warn you about a decision nobody wrote down. This is
that warning.

Decision numbers run continuously, so a gap in this list is itself a signal. **D-22 through D-46 are
all unwritten.** Twenty of them are cited by number in shipped production code, comments and tests.

> **⚠ D-43 reverses a fact ADR-001 still asserts, and ADR-001 cannot say so itself.** ADR-001 is
> `Accepted` and therefore immutable — it goes on stating that REGNO *is* the card UID at its lines 19,
> 124–125 and 254, and roughly a dozen comments in the codebase now cite ADR-001 for the opposite.
> **This row is the amendment** until the ADR-006 consolidation absorbs it. It is the exact hazard the
> top of this file describes, so it is recorded here rather than by editing an accepted document.

| Pending | Decision | Status | Lives only in |
|---|---|---|---|
| **D-22** | A soft-deleted student's card may be **deactivated**, but a new card may not be **issued** to them. Releasing what a deleted student holds is bookkeeping; issuing to them is a claim | shipped 3b-1 | `StudentService.DeactivateCardAsync`, `StudentSoftDeleteStrandingTests` |
| **D-23** | Device authentication is a real ASP.NET Core auth scheme emitting the claim shape Phase 6's JWT will emit, not bespoke middleware. `ISchoolContext` becomes claims-reading, with one dev fallback Phase 6 deletes | shipped 4b | `DeviceKeyHandler`, `ClaimsSchoolContext` |
| **D-24** | API keys are split tokens — public id indexed, 256-bit secret SHA-256 hashed. Fast hash is correct *because* the secret is server-generated. §4.10's `ApiKey` kept and permanently NULL, not dropped | shipped 4b | `DeviceKey`, `DeviceAuthenticator`, `RowLevelTenancy` migration |
| **D-25** | Key lifecycle: plaintext shown exactly once; hard-cut rotation, no overlap window; `ApiKeyRevokedAt` (burn credential) distinct from `IsActive` (retire device); no key caching | shipped 4b | `DeviceService`, `DeviceLifecycleTests` |
| **D-26** | Device identity comes from the authenticated principal via `IDeviceContext`, never from `TapRequest.DeviceId`. Body field retained as a cross-check; mismatch is `400 DeviceMismatch`, never a silent ignore | shipped 4b | `AttendanceService.TapAsync` |
| **D-27** | An explicit `device.SchoolId == event.SchoolId` check reported as the existing `DeviceNotRegistered` → 404 — no cross-tenant existence disclosure, no wire change. **Closed the cross-school tap defect** | shipped 4b | `AttendanceService`, `KnownDefectTests` |
| **D-28** | Enforcement narrowed to the capture endpoints only; `[HasPermissionNotEnforced]` stays alongside `[Authorize]`; no config off-switch; a Development-seeded device supplies the local key. **ADR-001 D-6's do-not-expose constraint stays in force** — *superseded 2026-09-17: every admin route is now Bearer-gated, see "What is decided where"* | shipped 4b | `Program.cs`, `AuthorizationCoverageTests` |
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
| **D-46** | **`GET /events/{id}/manifest` — the offline capture cache**, on its own device-authenticated controller rather than as one more action on `EventsController`, because every action there is open under ADR-001 D-6 and this one carries `attendance.capture`. Four choices inside it are the load-bearing ones. **It is the invitation, not the roster**: no attendance state, because a manifest carrying it would read as authoritative on the device. **Offline validation against it is display-only and never gating** — an unknown card must still be queued, or "the cache is stale" becomes "the attendance never happened". **The version is a SHA-256 content hash over the whole published body**, not `max(UpdatedAt)` plus counts: deletes, bulk SQL (the SIS import is exactly that shape) and card deactivations all leave a watermark unmoved, and a hash cannot miss a change because the thing that changed *is* the thing hashed — never `GetHashCode`, which is per-process randomized and looks correct in a single-process dev run. **Never truncated and never paged** — over `MaxAttendees` (20,000) it is a loud `413 ManifestTooLarge`, because a short list is indistinguishable from a small event. Reuses `attendance.capture` rather than minting `events.manifest` (avoiding D-45's shape) and gets a **third rate-limit policy** partitioned by device id, so a cache refresh can never spend a kiosk's tap budget | pending gate | `EventManifestController`, `EventManifestDto`, `EventManifestVersion`, `EventManifestLimits`, `EventService` manifest region, `ConditionalGetOperationFilter`, `CaptureRateLimiting.ManifestPolicyName`, `EventManifestTests` / `EventManifestVersionMovementTests`, `docs/api/attendance-contract-handoff.md` |
| *unnumbered* | **`reports.read` is granted to SuperAdmin and SchoolAdmin only** — Technical Plan §11 also gives "read reports" to Organizer and Viewer; the client's QA answer Q16 scopes the reports module to administrators (JJ-approved). Existing databases get the grant from the data migration, because `RbacSeed` grants only when it *creates* a role and never reconciles one that exists — so any future permission code needs its own grant migration. Organizer and Viewer still read one event's counts through `GET /events/{id}/summary` under `events.read`; what `reports.read` guards is the cross-event view | shipped Task 6 | `EamsPermissions.ReportsRead`, `GrantReportsReadToAdminRoles` migration, `RbacGrantMatrixTests`, `ReportsReadGrantMigrationTests` |
| *unnumbered* | **The multi-event report is a hand-picked list, not §6.7's date-range query.** `GET /reports/events/summary?eventId=…` stands in for `/reports/attendance?from=&to=&groupId=` (client Q15: "a summary of several events"). At least one and at most `ReportLimits.MaxEventsPerReport` (50) distinct ids — bounded because compatibility level 110 inlines `.Contains` as literals; a repeated id counts once; totals are **pooled** (Σattended ÷ Σexpected), never an average of per-event rates; one id outside the caller's school refuses the whole request rather than totalling a subset | shipped Task 6 | `ReportsController`, `ReportService`, `ReportLimits`, `ReportsApiTests` |
| *unnumbered* | **The single-event report adds `attended`, `status` and `startAt`** to the figures §6.7 lists, so a pooled total can be checked by hand and a row names its event. Additive; `GET /events/{id}/summary` is unchanged | shipped Task 6 | `EventReportRowDto` |
| *unnumbered* | **Report exports (§12) are deferred.** JSON only; no CSV, PDF or XLSX yet. An export would render these same routes | shipped Task 6 | — |
| *unnumbered* | **Reports reuse the event summary once per event, in a bounded sequential loop**, instead of a set-based query that would need a second copy of the denominator (live audience while Draft/Open, frozen once Closed/Cancelled) — the copy ADR-003 D-12/D-13 record as failing silently. About four aggregate queries per event, so up to ~200 at the cap (JJ-accepted). If a report is measured slow, the fix is making `FiguresForAsync` set-based for **every** caller, never copying it into the report | shipped Task 6 | `IEventSummaryFigures`, `EventService.FiguresForAsync`, `ReportsApiTests` (report agrees with the summary for Open, Draft, Closed, Cancelled and TimeInOut) |
| *unnumbered* | **`Events.IssuesCertificates bit NOT NULL`, default 0, named `DF_Events_IssuesCertificates`** — additive to Technical Plan §4.5 (client QA Q20: a per-event "issues certificates" setting; certificates themselves are not built). Existing rows read false. Created in raw SQL because EF Core 9 cannot name a default constraint. **`Down` refuses (`THROW 51003`) while any event has the flag set**, under `TABLOCKX, HOLDLOCK`, rather than erase an administrator's setting; clear the flags deliberately, then roll back | shipped P5 | `Event.IssuesCertificates`, `IssuesCertificatesFlag` migration, `EventIssuesCertificatesTests` |
| *unnumbered* | **§6.3: `PUT /events/{id}` is no longer a pure replacement.** An omitted or null `issuesCertificates` keeps the stored value (on `POST` it means false). Clients written before the field send bodies without it, and as a plain boolean every such edit would silently clear the flag with a 200. Required was rejected too: it would 400 every edit from the existing SPA | shipped P5 | `EventWriteRequest.IssuesCertificates`, `EventService.Apply`, `A_put_that_omits_issuesCertificates_does_not_clear_a_stored_true` |
| *unnumbered* | **The Closed lock admits exactly one edit: a PUT that names `issuesCertificates` and changes nothing else** (JJ: the flag is editable in every status because it does not change what any attendance row means). Every other field must arrive as stored — name trimmed then ordinal, the rest ordinal or normalized as `Apply` stores them; re-sending the current value is accepted so a retried toggle succeeds; any other change is still 409 and the refusal names the field | shipped P5 | `EventService.IsCertificatesOnlyEdit` / `NonCertificateChanges`, `A_closed_event_refuses_the_flag_alongside_any_other_change` |
| *unnumbered* | **`GET /sis/import/template` — a downloadable roster template**, not in Technical Plan §6.8 (Task 5; client QA MDVault #470 B1, #472 Q1/Q2: add STUDENT, PERSONNEL, SPECIAL and FRIARS columns to the roster file). Generated, never a committed file: an *Instructions* sheet (placed first) and a *Roster* sheet whose header row is the source columns of the profile version the school's next upload will be pinned to (the shared `SisImportProfileResolution` rule, so it cannot disagree with the importer), plus any `SisRosterColumns.Required` header that profile omits, because the reader picks the sheet by that fixed list. Under the upload's existing `sis.import` permission, no new code; `Cache-Control: no-store` because it lists the school's current vocabulary; REGNO and the card-serial column are formatted as Text so leading zeros survive | shipped P10 | `SisImportController.Template`, `SisImportTemplateService`, `SisImportTemplateWorkbook`, `SisImportProfileResolution`, `SisImportTemplateTests` |
| *unnumbered* | **A category value in another axis's column is refused, not filed on its real axis** — new warning `ClassificationWrongAxis`, ranked directly above `ClassificationUnavailable`. `NAP` under `STUDENT_CATEGORY` is applied on neither axis and changes nothing the person holds; the message names the column it was found in and the column it belongs in. Before this the importer checked "already held" by the column's axis and inserted on the value's, so a second Personnel value (same row, already held, or left by the previous import of the same file) violated `UX_StudentClassifications_Student_Axis` and failed the whole batch with no row number. No migration: `SisImportRows.WarningCode` carries no CHECK constraint | shipped P10 | `SisImportService.ResolveClassificationsAsync` / `WarnOnWrongAxis`, `SisImportWarningCode.ClassificationWrongAxis`, `RowLedger.WarningPrecedence`, `SisImportClassificationWrongAxisTests` |

> **Why the Task 6, P5 and P10 rows carry no number.** The next free decision number should be D-69, but **D-74 is
> cited by number in shipped auth code** (`TokenIssuer`, `AuthService`, `Program.cs`) with no ADR behind
> it, so D-69–D-73 may already be informally claimed by that unwritten auth work. Numbering these rows
> now risks a collision; they get numbers when the auth decisions are registered.

Deferred to the ADR-006 consolidation at JJ's direction, not forgotten.

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
| Why auth is deferred and what makes that safe | ADR-001 **D-6** — the deferral ended 2026-09-17 |
| **How §11 is enforced, and why a device no longer enrols itself** ⚠ gated routes take their tenant from the token, not the pin | Not yet an ADR (2026-09-17). `Program.cs` (a Bearer policy per permission), `AuthorizationCoverageTests`, `AuthEnforcementTests`; `GET /events` shared with device keys (drift from §6.3, open events only) in `DeviceKeyOrBearer`; `docs/api/mobile-changes.md` 2026-09-17 |
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
| **Why a person's classification is a junction and not a column** | ADR-005 **D-55** (ADR-001 **D-2**'s reasoning, second application) |
| Why the `720000` RegNo prefix is a fallback and not the rule | ADR-005 **D-56** ⚠ (deviates from a written QA answer) |
| Why the classification seed runs **above** `SeedData`'s early return | ADR-005 **D-57** ⚠ |
| Why the vocabulary has no hard delete, and what a tombstone is for | ADR-005 **D-58** ⚠ |
| Why a batch on profile version ≤ 2 classifies nobody and says nothing | ADR-005 **D-59** (ADR-001 **D-4** working) |
| **Why an import never overwrites a classification somebody set by hand** | ADR-005 **D-60** ⚠ (first-write-wins) |
| Why one warning remembers what it already said | ADR-005 **D-61** ⚠ (and the open defect in its seam) |
| Why `GET /cards` pages cards rather than students | ADR-005 **D-64** (ADR-001 **D-3** made visible) |
| Why no `cards.read` / `classifications.read` code was minted | ADR-005 **D-65** (D-45 applied prospectively) |
| Why the **import** *is* background work when the freeze is not | ADR-004 **D-54.1** ⚠ (read with D-21 or D-21 looks like a policy) |
| Why `POST /sis/import/{id}/run` returns `202`, and how a batch is claimed | ADR-004 **D-54**, **D-54.4** |
| Why there is no `Queued` import status | ADR-004 **D-54.2** ⚠ |
| How a background scope resolves its tenant | ADR-004 **D-54.3** ⚠ |
| What recovers a `Running` batch after a process death | ADR-004 **D-54.5**, and the accepted limit in **D-54.6** |
| The seven progress columns, and why `NULL` is the only "no progress" | ADR-004 **D-54.7** |

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

### ADR-004 D-54.3 — a background DI scope has no `HttpContext`, and that means "do not filter"

`ClaimsSchoolContext` returns `null` when there is no `HttpContext`, and `ISchoolContext` documents
`null` as *"do not filter"* — correct for the two cases it was written for (startup migration/seeding,
design-time model building). A `BackgroundService` scope hits the same branch, which would have switched
**every** global query filter off and let the roster import upsert across all schools.

**No test in the suite would have failed.** `EAMS.Tests` builds one school, and with one school an
unfiltered query and a filtered one return identical rows for every assertion. The fix (a scoped ambient
tenant that **throws** rather than falling back to unfiltered) is asserted by reading until somebody
builds a two-school fixture. **Do not "simplify" that throw into a fallback.**

### ADR-004 D-54.2 — a new import status value reads as *finished* to every older build

Both terminality predicates — `SisImportBatchDto.IsTerminal` and the SPA's `isTerminalStatus` in
`web-admin/src/sisImport.ts` — are written as *not `Pending` and not `Running`*. That is deliberate and
right. Its corollary is that adding a status (`Queued` was the tempting one) makes a brand-new batch
render as a completed run with five zero counters and a red "counters do not add up" alert. Additive
`nvarchar` value, both predicates changed in the same commit, shipped with a release — or not at all.

### ADR-005 D-61 — a warning suppressed by durable state, with an **open** crash window

`StudentClassification.ReportedRosterValue` makes `ClassificationConflict` warn once per disagreement
instead of on every run. It is the **first warning in this pipeline whose emission is gated by stored
state**, and the failure mode of that gate is silence.

**The window is open in the tree as of 2026-09-17.** The memory is written in the fact pass and flushed
by the fact-pass save; the warning it gates is written by `RowLedger.ApplyInChunks` in later saves
(ADR-004 **D-54.8**'s deliberate split). A process death between them leaves the memory durable with
**no warning written anywhere**; D-54.5's sweep marks the batch `Failed`, and the retry rebuilds
`SisImportRowEntities` but **never touches `StudentClassifications`** — so the disagreement is reported
**zero** times, for ever, while the batch reports `Completed`.

**D-54.8's own defence does not reach this.** Its "the inconsistency is cosmetic" comment is about
`SisImportRow.Result`/`WarningCode`, which the retry *does* rebuild. This is a different table.

Reproduced by `SisImportClassificationCrashRecoveryTests` with a passing negative control.

**FIXED (2026-09-17).** `RowLedger.DeferUntilStaged` holds the memory write until the row is staged, so
it flushes in the **same transaction** as that row's `WarningCode`. A crash now leaves neither written
and the next run announces.

> **⚠ The fix prevents; it does not recover — and the reason it is allowed to stop there expires on
> release.** A row that already carries an orphaned memory stays silent for ever and cannot be detected:
> `StudentClassification` holds only `StudentId`, `ClassificationId`, `Axis` and `ReportedRosterValue`
> plus audit stamps, the memory write does not bump `UpdatedAt`, and so the post-crash state is
> **byte-identical** to the state after a disagreement correctly reported once — which must stay silent.
> No read can tell them apart. That was judged acceptable **only** because no database has ever run this
> code (no classification migration on `main`, slice uncommitted at the time), so no orphaned memory can
> exist. **Once this ships, that argument is dead**: any future crash in the same shape becomes
> permanently unrecoverable. Recovery needs a second durable signal — the cheapest honest shape is an
> additive nullable `ReportedBySisImportBatchId` honoured only for a batch that reached a terminal
> status. **Whoever ships this slice to production owns deciding whether to build it first.**

### ADR-005 D-63 — three mapping faults, only one of which speaks

The axis of a category column rides in a `TargetField` **string** (`StudentClassification.Personnel`),
so an operator can mistype it. All three of the following skip work behind a guard clause.

- **`ClassificationAxisUnknown` — fixed.** A mistyped axis, or a real axis whose profile row names no
  readable source column and which no later row rescues, now warns on **every row of the batch** and
  ranks **first** in precedence. Before this, a batch authored against `StudentClassification.Faculty`
  finished `Completed`, clean, with 21,497 people unclassified on the axis the mapping existed to read
  — and `ClassificationMissing` could not fire either, because `CategoryColumnsPresent` is only set
  from a column that *was* read.
- **The RFID column collapsing to `null` — LOGGED, NOT FIXED.** A misconfigured card mapping is
  indistinguishable from the ordinary, universal case of a file with no RFID column. JJ's call: out of
  slice scope, it touches the tap path. Recorded so the next finder knows it was seen.
- **A second profile column claiming an axis a first one took — DEFERRED.** Skipped silently; the axis
  still works via the first column by ordinal, so the batch is *incomplete rather than wrong*.
  Reachable by design, because the profile's unique index permits one target on two source columns.

### ADR-005 D-58 — the classification name-key hedge (recoverable, unlike D-11)

`UX_Classifications_SchoolId_NameKey` is keyed `(SchoolId, NameKey)` rather than
`(SchoolId, Axis, NameKey)` — **exactly the ADR-002 D-11 shape**: it assumes no two axes ever need the
same word, which is true of all eight seeded values. Unlike D-11 the widening is a **re-index** rather
than a split, so it does not carry D-11's unrecoverable tail — but it has **the same one-question
retirement path**: ask the registrar whether a category name can legitimately mean two different things
on two different axes. One question, and it has been open the same way.

## Proposed, not yet decided

**D-47 … D-53** are *proposed* in
[`../PHASE-5-YEAR-LEVEL-AND-TERM-ADMIN.md`](../PHASE-5-YEAR-LEVEL-AND-TERM-ADMIN.md) and are listed
here so the numbering stays unambiguous — nothing in code may cite them until they are accepted.

> **⚠ That rule is already broken for D-47, and it predates ADR-005.** `D-47` is cited by name in
> shipped Phase-5 code — `YearLevels`, `AudienceField`, `AcademicEntities`, `DomainValues` — while it
> is still only *proposed* here. **Incidental, not caused by the classification slice, and deliberately
> not "fixed" by it**: silently deleting the citations would lose the reasoning, and accepting D-47 to
> match the code is a decision JJ has not made. Noted so that the next reader meets a recorded
> inconsistency rather than an undiscovered one; the ADR-006 consolidation is where it resolves.

| Pending | Decision |
|---|---|
| **D-47** | Year level is **derived** from the student's program-shaped home section (`BSCRIM 2-A` for a BSCRIM student), never from any section containing a digit — subject blocks (`NSTP 2`, `GE 8`) mix year levels. Ambiguity yields `null`, never a guess |
| **D-48** | Derived year writes to `StudentTermRecord` only — never to the ADR-001 D-2 `Student` display cache |
| **D-49** | Year level becomes an invitable audience by projecting into `StudentGroup`, not by extending `EventGroups` — the same trade as ADR-003 |
| **D-50** | The event audience builder exposes a **closed** list of five academic fields (college, program, year, section, course), each resolving through `Enrollments`/`StudentTermRecords`. **The `Students` cache columns are unreachable by construction** — an open "filter any column" builder is the most likely way to reintroduce the ADR-001 D-2 defect, because the wrong columns are the convenient ones |
| **D-51** | Values within a filter field union; fields intersect; separate additions union |
| **D-52** | A multi-filter audience materialises to individual student rows, because `EventGroups` unions its rows and attaching constituents to mean an intersection would over-count the ADR-003 D-12 denominator |
| **D-53** | Terms get an admin write surface; the rest of `/academic` stays read-only because the importer owns those tables |

That document is the plan for the next slice, not an ADR. On implementation these fold into the
ADR-006 consolidation alongside the unwritten D-22…D-46.

**D-54 is not in either list above** — it is written, in [ADR-004](ADR-004-detaching-the-roster-import-run.md),
and is the next number after the proposed block. Its eight parts are numbered `D-54.1`…`D-54.8` rather
than consuming `D-55`…`D-62`, because they are one decision's parts and none is separable.

**`D-55`…`D-67` are likewise not in either list** — they are written, in
[ADR-005](ADR-005-classifying-the-roster.md), and continue from D-54 as ADR-004 said a future ADR
would. **A future ADR continues from D-69.**

## Conventions

- Structure: Context → Options Considered → Decision → Consequences (positive / negative / neutral) →
  Follow-ups.
- `Proposed` means written but not yet agreed by the deciders it names. **Do not self-accept** an ADR
  listing someone else as a decider — an `Accepted` record of a decision nobody made is the exact
  failure the practice exists to prevent.
- A `Proposed` ADR is freely editable. An `Accepted` one is not: supersede it instead, and update this
  index.
- A consolidating ADR superseding 001–005 is **now due at ADR-006**, and is **overdue for the third
  time**: ADR-003 already called four documents the point where "read them together" stops being
  followed, ADR-004 made it five, and ADR-005 makes it **six**. ADR-004 took the 004 number for a
  production defect fix at JJ's direction; **ADR-005 took the 005 number for the classification slice
  at JJ's direction** — in both cases because a large consolidation was not going to gate delivery.
  Each deferral is recorded where it happened rather than absorbed quietly, because a consolidation
  that has slipped three times is information about the practice. The chronology is still
  load-bearing, so the consolidation must preserve *when* each decision was made and with what in
  hand — particularly ADR-003 D-13, which is only comprehensible in the order it happened, and
  ADR-005 D-56/D-57, which are only comprehensible if you know the local fixture was in hand and the
  school's own export was not.
