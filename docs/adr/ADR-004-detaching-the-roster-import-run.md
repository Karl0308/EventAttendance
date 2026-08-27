# ADR-004: Detaching the Roster Import Run from the HTTP Request

**Status**: Proposed (2026-08-27) — awaiting JJ's read-through, then flip to `Accepted`
> The *decisions* below are JJ's and were made on 2026-08-27; this *document* has not yet been read
> by him. The register's own rule is "do not self-accept an ADR listing someone else as a decider",
> and the failure it guards against is a document that misstates a decision while carrying the
> decider's name. Flipping this line is a one-line edit; un-flipping it after the fact is not.
**Date**: 2026-08-27
**Deciders**: JJ; JoseArch (Team X)
**Relationship to ADR-001 / ADR-002 / ADR-003**: **Amends all three by reference.** None is edited;
all remain `Accepted` and D-1 through D-21 stand as written. This document continues the decision
numbering at **D-54** — D-22…D-46 are shipped-but-unwritten and D-47…D-53 are proposed in
`../PHASE-5-YEAR-LEVEL-AND-TERM-ADMIN.md`, both tracked in `README.md`.

> **This is not the consolidating ADR.** ADR-002 anticipated one "around ADR-004" and ADR-003's
> follow-up list calls for it *at* ADR-004. It is not this document. JJ's call: the consolidation is a
> large document, and making it a precondition for a production defect fix would block delivery for no
> engineering benefit. **The consolidation is now due at ADR-005.** ADR-003 is immutable and cannot say
> so — `README.md` carries that amendment, which is exactly the hazard the top of that file describes.

## Context

`POST /api/v1/sis/import/{batchId}/run` ran the entire import synchronously inside the HTTP request.
On the deployment VM, a 21,497-row roster took longer than the SPA's 15-second budget. What followed
was not one bug:

1. The browser aborted the request.
2. ASP.NET Core MVC binds an action's `CancellationToken` parameter to
   `HttpContext.RequestAborted`, so the abort propagated into `SisImportService.RunAsync` as
   cancellation — a mechanism nobody chose at the call site and nothing in the signature announces.
3. `RunAsync`'s recovery handler carried a catch *filter* excluding `OperationCanceledException`, on
   the reasoning that a run somebody called off has not "failed". So the `Failed` status was never
   written.

The result was two batches sitting in `Running` — a status that is in neither `Pending` nor `Failed`,
and the re-run guard in `RunAsync` admits only those two. The batches could not be completed, could
not be retried, and were indistinguishable from a run still in progress. They were recovered with
hand-written SQL against production.

**Each of the three decisions is defensible read alone.** Binding the token to `RequestAborted` is
correct for a read — a client that walked away should not keep a query running. Excluding
cancellation from a failure handler is correct about the *word*. A re-run guard that refuses anything
outside `Pending`/`Failed` is correct because a `Completed` batch is a historical record, not a
re-runnable script (`RunAsync`'s guard says so in as many words). Composed, they produce a state with
no exit. That composition is the thing this ADR exists to record, because none of the three will look
wrong to the next reader who meets them one at a time.

### What was already fixed, and why it is not enough

Two interim changes shipped at the incident and are in the tree now:

- The catch filter's cancellation carve-out was **removed**, so a run that stops for any reason writes
  `Failed`. `Failed` is the honest label for a run that did not finish, and it is the only one an
  operator can act on.
- `SisImportController.Run` passes **`CancellationToken.None`** to `RunAsync`, so a client
  disconnect no longer reaches the pipeline mid-write.

Both are right and both stay. Neither addresses the actual complaint: **the operator still stares at a
frozen browser tab for the length of a 21,497-row import, and has no idea whether anything is
happening.** And the `CancellationToken.None` reflex has a cost of its own — the run is now
uncancellable by the client, which is tolerable while the run is bounded by the request and is not a
permanent answer. The controller's own comment names the residual hole: *"This does NOT cover an
app-pool recycle mid-run: that kills the process, no handler runs, and the batch is a zombie again.
Only moving the run off the request"* closes it.

This ADR is that move.

## Options Considered

### Option 1: Do nothing — raise the SPA's timeout
Widen the client budget until the largest roster fits.
- **Pros**: One constant. No server change, no schema change, no new moving part.
- **Cons**: Guesses a ceiling nobody has measured, against a file size the *operator* chooses. Every
  layer between the browser and Kestrel gets a vote — the IIS request timeout, the ASP.NET Core
  Module's `requestTimeout`, any proxy — and none of them is configured by us. It leaves the operator
  with no signal for the whole run, and it leaves the zombie-on-recycle hole exactly where it is.
- **Effort**: Trivial, and it buys the smallest thing.

### Option 2: Hangfire, as Technical Plan §6 names for this endpoint
§6 (line 483) explicitly assigns this endpoint a Hangfire background job; §3 names an `EAMS.Worker`
project to host it.
- **Pros**: Full conformance with the plan. Automatic requeue, a durable job store surviving process
  death, a dashboard, a retry policy, and scheduling for §14's retention job later.
- **Cons**: Roughly ten tables in a schema **outside EF migrations**, in a repo whose stated rule is
  migrations-first (`CLAUDE.md`). A second storage lifecycle to deploy, back up and reason about on a
  SQL Server 2012 box. And the durability it sells is durability this pipeline does not need — see
  D-54.1.
- **Effort**: Medium, and most of it is deployment rather than code.

### Option 3: A separate worker process, per Technical Plan §3
Stand up `EAMS.Worker` and move the run there, coordinating through the database.
- **Pros**: The plan's layout. A recycle of the web app pool no longer touches a run.
- **Cons**: A second deployable on a VM whose deployment script's stated premise is that it needs no
  administrator rights (`scripts/vm-deploy.ps1`). Registering a Windows service or a second app pool is
  precisely the privilege that premise excludes. It also needs a cross-process claim and a
  cross-process signal — the database work of Option 2 without Option 2's ready-made answer.
- **Effort**: High, and the highest deployment risk of the four.

### Option 4: Client-driven chunking — the SPA calls `/run` repeatedly for slices
Keep every call short by making the client the scheduler.
- **Pros**: No background work at all; every request stays inside any timeout.
- **Cons**: Makes the browser tab load-bearing for *correctness* rather than merely for progress, and
  puts a resumable cursor into the API surface for an operation whose passes are not row-sliceable —
  `ExecuteAsync`'s dimension resolution is whole-batch by construction. It converts one long request
  into many chances to leave a batch half-run.
- **Effort**: High, and it is the option most likely to produce a new stuck state.

### Option 5 (chosen): An in-process detached run behind a one-method interface
A `Channel<Guid>` consumed by a single `BackgroundService` in `EAMS.Api`; the controller claims the
batch atomically, enqueues, and returns `202` immediately; the SPA polls the existing
`GET /sis/import/{batchId}`.
- **Pros**: No new storage, no new deployable, no new privilege. The operator gets a response in
  milliseconds and a progress signal for the rest of the run. The recycle hole is closed by a startup
  sweep rather than by a job store. The enqueue seam is one interface with one method, so Option 2
  remains a one-file swap.
- **Cons**: Durability is the *process*, not a table. A hard kill loses the in-flight run — recovered,
  not prevented (D-54.5). Capacity is one queued run. And it puts background work in `EAMS.Api`, which
  is drift from §3 that ADR-003 D-21 already opened.
- **Effort**: Low-to-medium, concentrated in the tenancy fix (D-54.3) rather than in the queue.

---

## Decision

### D-54: The roster import run is detached from the HTTP request onto an in-process queue, and `POST /run` returns `202` after an atomic claim

`POST /api/v1/sis/import/{batchId}/run` validates the request, **atomically claims** the batch by
moving it to `Running` in one statement, enqueues the batch id, and returns **`202 Accepted`** with the
in-flight `SisImportBatchDto`. A single `BackgroundService` hosted in `EAMS.Api` dequeues, opens its
own DI scope, pins the tenant explicitly, and calls the existing `ISisImportService.RunAsync`. Progress
is read by polling the existing `GET /api/v1/sis/import/{batchId}`.

The pipeline itself — `ExecuteAsync`, its passes, its idempotency, its deliberate lack of a wrapping
transaction — is unchanged except for the save boundaries in D-54.8. **This decision is about where the
run runs and how it is observed, not about what it does.**

Eight sub-decisions follow. They are numbered `D-54.n` rather than consuming `D-55`…`D-62`, because
they are one decision's parts and none of them is separable: shipping the queue without the tenancy fix
(D-54.3) is a cross-tenant data corruption, and shipping it without the claim (D-54.4) reopens a race
the synchronous version merely hid. Code may cite `D-54.3` and mean something precise.

---

### D-54.1: An in-process `Channel<Guid>` behind `ISisImportRunQueue`, not Hangfire — and the seam is the point

**Context.** Technical Plan §6 line 483 names Hangfire for this exact endpoint. Choosing otherwise is
drift and has to earn it.

**Decision.** A bounded `Channel<Guid>` with **capacity 1**, exposed through a **one-method interface**,
`ISisImportRunQueue`, and consumed by one `BackgroundService` in `EAMS.Api`. No Hangfire, no job store,
no dashboard.

**Why, and it is not that Hangfire is heavy.** It is that the durability Hangfire sells is durability
this pipeline already has by another route:

- **The import has no wrapping transaction, deliberately.** `SisImportService`'s own documentation says
  so and gives the reasoning: a transaction spanning thousands of row-touches holds locks on the roster
  for the duration, and it cannot be combined with the registered `EnableRetryOnFailure` execution
  strategy without wrapping every phase in `IExecutionStrategy.ExecuteAsync` — which would silently
  replay already-committed phases on a transient fault.
- **Every write is an idempotent upsert.** Running the same roster twice leaves the database identical
  and reports every row `Skipped`. That is not a hope; it is what §10.4 requires and what
  `SisImportPipelineTests` asserts.
- **`Failed` is already an explicitly re-runnable state.** `RunAsync`'s guard admits `Pending` and
  `Failed` and refuses everything else, and says why: *"nothing about it is worth preserving, and
  retrying is the obvious repair."*

So the recovery story is: a recycle kills the run → the sweep marks the batch `Failed` (D-54.5) → the
operator presses Run again → the import completes, reporting the already-written rows as `Skipped`.
**That is a complete recovery, not a degraded one.** What Hangfire adds is *automatic* requeue of a job
that is already safe to retry by hand, and it charges roughly ten tables in a schema outside EF
migrations for it, in a repo whose rule is migrations-first.

**Why the interface is one method.** Because the calculus above changes. Technical Plan §14's retention
job and §13's report export are both scheduled or long-running work with no operator sitting in front
of them, and neither has the import's "just press it again" recovery. At that point Hangfire earns its
tables, and the swap should be a single implementation of `ISisImportRunQueue` plus a registration —
not an excavation of the controller. **The seam is deliberately too small to grow logic in.** If a
second method appears on that interface, the swap has already stopped being one file.

**Capacity 1, on purpose.** One import at a time is the real-world shape — a registrar uploads a roster,
watches it, and does the next thing. A capacity of one makes "a second import is already running" an
immediate, honest answer to the operator instead of a queue that silently absorbs work and delivers it
minutes later.

**Consequences.**
- *Positive*: No new storage, no new deployable, no new privilege, no schema outside EF. The whole
  mechanism is three small types and a registration, reviewable in one sitting.
- *Negative*: **The queue does not survive the process.** An enqueued-but-not-started batch is lost on a
  recycle — it is claimed, so the sweep recovers it (D-54.5), but "lost and recovered as `Failed`" is a
  worse outcome than "still queued", and only a durable store fixes that. This is the single largest
  thing Option 2 would have bought.
- *Negative*: **This is drift from §6, and it is a *partial return*, not a fresh deviation.** The
  synchronous run was itself unrecorded drift from §6 — the plan never specified running the import
  inline; that is simply what got built. This moves back toward the plan's shape (the work leaves the
  request) without adopting its mechanism. Recording it as a partial return matters, because the
  alternative reading is that §6's Hangfire line was rejected, and it was **deferred**.
- *Neutral*: Multi-instance deployment is now a question the architecture has an opinion about and no
  answer for — see the open questions.

---

### D-54.2: No `Queued` status — the batch goes straight to `Running` at claim time

**Context.** The obvious modelling is a `Queued` status between `Pending` and `Running`: it is true, it
is informative, and it distinguishes "waiting for the consumer" from "the consumer has it".

**Decision.** **There is no `Queued` status.** The claim writes `Running`, and the batch is `Running`
from the moment `POST /run` returns.

**Why — and this is a compatibility fact, not an aesthetic one.** `web-admin/src/sisImport.ts` defines
the SPA's terminality predicate as *not one of the two live states*:

```ts
export const isTerminalStatus = (status: string): boolean =>
  status !== SIS_IMPORT_STATUS.Pending && status !== SIS_IMPORT_STATUS.Running;
```

`SisImportBatchDto.IsTerminal` on the server is written the same way, for the same stated reason: a
status a build has never heard of is classified **terminal**, which is the safe direction for a poller —
the alternative leaves it waiting forever on a run it will never recognise as finished.

That choice is right, and it has a corollary nobody had to think about until now: **any new status
value is, to every older build, a finished run.** A `Queued` batch served to a deployed SPA renders the
results step — "The import finished" over five zero counters — plus the red *"counters do not add up"*
alert, because `CountersReconcile` is arithmetic over counters that are legitimately zero until the
tally at the end of the run. A brand-new import would present itself to the operator as a completed,
self-contradicting one.

Going straight to `Running` costs nothing real. The window a `Queued` status would describe is the time
between the enqueue and the consumer picking it up, which at capacity 1 with an idle consumer is
sub-millisecond, and which no operator can act on differently.

**When `Queued` becomes legitimate.** If a batch can ever genuinely *wait* — a deeper queue, a
concurrency limit above one, a scheduled run — then the distinction is real, the operator can act on it
("you are second in line"), and it should be added. Two conditions on doing so:

1. It is an **additive `nvarchar` value** in `SisImportStatus`, exactly like `CompletedWithWarnings` and
   `CompletedWithErrors` before it. **Never a `DROP`, never an `ALTER COLUMN`, never a rename** — the
   global hard rule, and `CLAUDE.md`'s note that these enum-ish string columns become a schema
   migration the day anyone makes them real enums.
2. **Both terminality predicates are updated in the same change**, server and SPA. They are the same
   predicate on two sides of the wire; `SisImportBatchDto.IsTerminal` says so in its own documentation.
   A deployed-but-not-refreshed SPA will still misread it during the rollout window, which is an
   argument for adding it with a release rather than with a hotfix.

**Consequences.**
- *Positive*: No status-vocabulary change, so no SPA-version skew, no OpenAPI enum change, and the
  existing polling loop works against the new flow unmodified.
- *Negative*: `Running` now means two things — "queued" and "actually executing" — and the only way to
  tell them apart is that `ProgressPhase` is still `NULL`. That is a real loss of fidelity, recorded
  here so nobody re-derives it as a bug.
- *Neutral*: The reason an unrecognised status reads as terminal is now written in an ADR rather than
  only in two source comments, because it is the constraint governing every future status value on this
  table.

---

### D-54.3: A background scope gets a scoped ambient tenant that **throws** — `IPinnedSchoolContext` is the wrong instrument

**Context — and this is the near-miss, recorded because the fix alone would delete the lesson.**

`ClaimsSchoolContext` resolves the tenant in three steps: a `school_id` claim; failing that, the pinned
development school; failing *that* —

```csharp
var context = _http.HttpContext;
if (context is null) return null;
```

— `null`. And `ISchoolContext` documents `null` as **"do not filter"**, deliberately: with no claims to
read there is no honest tenant answer, and filtering on a default `Guid.Empty` would make every query
return zero rows for reasons invisible at the call site. That is a considered design for the two cases
it was written for — migration and seeding at startup, and design-time model building.

**A `BackgroundService`'s DI scope has no `HttpContext`.** A run executing in one would have taken that
third branch, every EF Core global query filter in `EamsDbContext` would have switched off, and the
import would have resolved students, courses, offerings and enrollments **across all schools**. Upserts
matching rows belonging to another tenant. Not a leak — a write.

**It would have passed the entire test suite.** `EAMS.Tests` builds one school. With one school, an
unfiltered query and a correctly filtered one return the same rows, every time, for every assertion.
There is no test in the suite today whose failure would have announced this, and adding one means
building a second school specifically to observe the difference.

ADR-001 D-6 argued for installing the tenant filter before auth on the grounds that *"a missing tenant
filter is the one thing that cannot be retrofitted cheaply — it means re-reviewing every query ever
written, and getting it wrong is a cross-tenant data leak."* **This is the first place that reasoning
was about to be proven right**, and it arrived from a direction D-6 did not anticipate: not a query
someone forgot to filter, but a *context* in which the filter that was written correctly evaluates to
"filter nothing".

**Decision.** Introduce a **new scoped ambient tenant** — a small scoped service the background scope
sets before invoking the pipeline, consulted by `ClaimsSchoolContext` when `HttpContext` is `null`.
**When it is consulted and holds no value, it throws.** It does not fall back to unfiltered, and it does
not fall back to the pin.

Throwing is the decision, not an implementation detail. The failure mode it replaces is silent and
unbounded; the failure mode it introduces is a loud exception in a background run, which the sweep and
the `Failed` status already know how to report. **An import that cannot say which school it is for must
not run.**

**Why `IPinnedSchoolContext` was the wrong instrument.** It is registered as a **process singleton** —
`services.AddSingleton<IPinnedSchoolContext>(...)` over `DevelopmentSchoolContext`, in
`EAMS.Infrastructure`'s `DependencyInjection` — and pinned once at startup. Mutating it to carry the
running import's tenant would move the tenant **under every concurrent request in the process**: every
open, unauthenticated endpoint (which under ADR-001 D-6 is most of them) resolves its tenant through
that same singleton via `ClaimsSchoolContext`'s second branch. A cross-tenant write in the import would
have become a cross-tenant read on every page an operator had open at the time. It is also documented as
*"the one line Phase 6 deletes"* — building a second, load-bearing responsibility onto the thing whose
value is that it can be deleted is how a two-file deletion becomes an archaeology exercise.

**Consequences.**
- *Positive*: The tenant becomes a property of the scope, which is what it always should have been. The
  new service is scoped like `ClaimsSchoolContext` itself, so nothing is captured beyond one run.
- *Positive*: Phase 6 is unaffected. Deleting the pin still deletes one branch; this adds a different
  one, for a case Phase 6 does not change.
- *Negative*: `ClaimsSchoolContext` now has **four** answers instead of three, and the ordering between
  them is load-bearing. Its documentation must carry the new branch, or the next reader will go on
  reading the `HttpContext is null` case as meaning "startup".
- *Negative*: **A test that observes the difference requires two schools**, and the suite has one. Until
  such a test exists this fix is asserted by reading rather than by failing — named in the follow-ups
  as the highest-value missing test in this ADR.
- *Neutral*: Startup migration and seeding keep the unfiltered branch unchanged. They run before any
  scope sets an ambient tenant, and they are the case `null` was written for.

---

### D-54.4: The claim is one atomic statement, and a refused enqueue releases it

**Context.** `RunAsync`'s re-run guard is a read, a check, and a write — three statements, no
transaction:

```
load batch → if (batch.Status is not (Pending or Failed)) throw → batch.Status = Running; SaveChanges
```

Two concurrent `POST`s can both load a `Pending` batch, both pass the check, and both proceed. That race
existed before this change and was **hard to reach**, because the first request held the connection for
the entire import — a second operator would have to click inside the window between the load and the
save.

**Detaching makes the window reachable.** `POST /run` now returns in milliseconds, so a double-click on
a slow link, a retried request, or two operators on the same batch are ordinary rather than exotic.
Fixing this is not optional alongside the queue; it is *caused* by the queue.

**Decision.** The claim is **one `ExecuteUpdateAsync`**:

```
UPDATE SisImportBatches
SET    Status = 'Running', StartedAt = <now>, <progress columns reset>
WHERE  Id = @id AND Status IN ('Pending', 'Failed')
```

**Zero rows affected ⇒ `409 Conflict`.** The database's own row lock decides the winner; there is no
window between deciding and writing, because they are the same statement. The 409 keeps the controller's
existing status-code contract — it already returns `409` for "the batch cannot be run" and reserves
`404` for "no such batch", specifically so a caller can tell *"this id never existed"* from *"this batch
has already run"*: two problems with different fixes.

**On a refused enqueue, the claim is released** — the batch returns to the status it was claimed from.
This is the part that is easy to leave out. A batch that has been claimed but never enqueued is
`Running` with nothing running: **a brand-new zombie, of exactly the shape this ADR exists to
eliminate**, produced by the fix for it. The release belongs on a `finally`-shaped path, not on the
happy branch of an `if`.

**Consequences.**
- *Positive*: The double-submit race is closed by a rule the database enforces, not by a code path two
  callers have to reach in the right order. It is also cheaper than what it replaces — one round trip
  instead of three.
- *Positive*: `RunAsync`'s own guard stays where it is. It is now belt-and-braces rather than the primary
  defence, and it still carries the explanation of *why* a `Completed` batch is not re-runnable, which
  the SQL predicate cannot express.
- *Negative*: The claim's `WHERE` clause and `RunAsync`'s guard are **the same rule written twice, in two
  languages**. They can drift. Changing the re-runnable set means editing both, and only one of them
  sits next to the comment explaining the rule.
- *Neutral*: The claim resets the progress columns, so a retried `Failed` batch does not display the
  previous attempt's phase while the new one starts.

---

### D-54.5: Orphan recovery is a startup sweep plus a shutdown hook — and the limit is stated plainly

**Context.** Detaching makes the run survive the **request**. It does not make it survive the
**process**. And it makes that exposure *worse*: previously a recycle mid-run killed a request an
operator was watching, so somebody knew. Now the run is unattended, and the window in which a recycle
can orphan it is the whole run.

**Decision.** Two mechanisms, covering different failures:

1. **A startup sweep.** On host start, any batch in `Running` whose `ProgressUpdatedAt` is stale or
   `NULL` is marked `Failed` with a `FailureReason` saying what happened — that the run was interrupted
   and the batch can be run again. This is what covers a hard kill.
2. **`StopAsync` on the `BackgroundService`.** On graceful shutdown, the in-flight batch is marked
   before the host goes away, so the common case — an ordinary recycle, an app-pool stop, a deploy — is
   reported immediately rather than at the next start.

**State the limit rather than implying coverage.** `StopAsync` runs on a *graceful* shutdown. A hard
process kill — `Stop-Process`, an OOM, a machine reset, IIS deciding it has waited past its shutdown
time limit — **runs no handler at all.** Nothing marks the batch, and it stays `Running` until the next
start. **The sweep is what covers that case, and the batch is stuck for however long the process stays
down.** Writing "graceful shutdown is handled" without this paragraph would be the same class of
half-truth as the original catch filter: correct about the mechanism, wrong about the consequence.

`FailureReason` (`nvarchar(400)`, D-54.7) is deliberately a sentence an operator can act on rather than
a stack trace — an interrupted run's remedy is "run it again", and that is what it should say.

**Consequences.**
- *Positive*: The zombie state has an automatic exit for the first time. The two production batches that
  started this ADR would have cleared themselves at the next app-pool start.
- *Negative*: **The staleness threshold is a guess against a workload nobody has measured.** Too short
  and the sweep could kill a live run — although the sweep runs only at startup, when by definition no
  run of *this* process is live. Too long and a genuinely dead batch sits `Running` past a restart. The
  number is not set by this ADR; see the open questions.
- *Negative*: A batch swept to `Failed` may have written most of its rows. That is safe — every write is
  an idempotent upsert — but its counters describe a run that did not finish, and an operator reading
  them without re-running will draw the wrong conclusion.
- *Neutral*: The sweep is a natural home for other stuck-state recovery later, and equally a natural
  place to accumulate unrelated startup work. It should stay the import's sweep.

---

### D-54.6: The IIS idle timeout is **accepted, not mitigated** — polling is what keeps a long import alive

**Context.** IIS shuts an application pool down after **20 minutes with no incoming requests**, whatever
it is computing. CPU activity does not reset the idle timer; only requests do. A long import running
with nobody polling it is therefore a run that can be killed by its own quietness.

The mitigation is `idleTimeout="0"` on the application pool, set through IIS Manager or
`applicationHost.config`. **Both need administrator rights on the server**, and `scripts/vm-deploy.ps1`'s
stated premise — the first line of its help — is that the deployment requires none. That premise is what
makes the deploy runnable by the people who actually run it.

**Decision — JJ's, explicitly. The risk is accepted, not mitigated.** No application-pool change is
requested as part of this work.

**The consequence is a real operating rule, so it is documented as one:** the SPA's polling of
`GET /sis/import/{batchId}` is not only how the operator sees progress — **it is what keeps the
application pool alive for the duration of a long import.** An operator who starts a 21,497-row import
and closes the tab has removed the only thing resetting the idle timer. This belongs in the deployment
runbook and in the import page's own guidance, phrased as an instruction ("leave this page open until
the import finishes") rather than as trivia.

**Why accepting is defensible here.** The failure it permits is exactly the failure D-54.5 recovers
from: the pool stops, the batch is swept to `Failed` at the next start, the operator runs it again, and
every already-written row reports `Skipped`. It costs a re-run, not data. Trading that against a
deployment that suddenly needs administrator rights — and therefore a different person, on a different
schedule — is the right trade at this scale.

**What would reopen it.** A scheduled import with no operator present (§14's retention job has the same
shape), or an import long enough that a re-run is not an acceptable remedy. Either turns "leave the tab
open" from guidance into a load-bearing dependency on a human, which is not a design.

**Consequences.**
- *Positive*: The deployment script's no-admin premise survives intact, which is worth more than it
  looks — it is why the deploy is repeatable by whoever is available.
- *Negative*: **A documented human behaviour is now part of the system's reliability story.** That is a
  genuine weakness, recorded as one. It is also fragile in a way the code cannot detect: nothing
  observes that the operator closed the tab.
- *Neutral*: If a keep-alive is ever wanted, it belongs in the SPA's existing poll — which already runs —
  rather than in a server-side timer, because a server pinging itself does not produce the inbound
  request the idle timer counts.

---

### D-54.7: Seven additive nullable progress columns, written phase-granularly — no percentage, no ETA

**Context.** Polling only helps if there is something to poll. The batch row carried nothing between
`StartedAt` and `FinishedAt`.

**Decision.** Migration `SisImportProgress` adds **seven nullable columns** to `dbo.SisImportBatches` and
nothing else — no index, no default, no backfill, no change to any existing column:

`ProgressPhase` · `ProgressPhaseNumber` · `ProgressPhaseCount` · `ProgressUnitsDone` ·
`ProgressUnitsTotal` · `ProgressUpdatedAt` · `FailureReason`

**`NULL` means "this run predates progress reporting", and nothing else.** There is deliberately no
zero-valued alternative: a progress `0` would be three facts at once — the phase has not started, the
phase has no countable units, and the phase has done none of its units yet — and a poller cannot tell
them apart. A backfilled zero would show every historical import as a progress bar stuck at 0%: a
fabricated claim about a run that in fact finished months ago. The six existing row counters on the same
table *are* `DEFAULT 0` and are right to be, because "no rows failed" is a fact about a finished run.

**Progress is phase-granular, not row-granular.** The phases are the named constants in
`EAMS.Domain.SisImportPhase` — `ParsingRows`, `ResolvingDimensions`, `ResolvingFacts`, `WritingFacts`,
`RecordingRowResults`, `RefreshingStudentCache`, `SyncingStudentGroups` and the rest. Progress is written
**between passes**, by `ExecuteUpdateAsync` against the batch row, and **never inside a `SaveChanges`**.
That is not a style preference: the pipeline's saves carry the tracked entity graph for the pass that
just ran, and a progress write folded into one of them would be re-attempted by the recovery handler in
`RunAsync` — the precise failure that handler's own `ExecuteUpdate` comment exists to prevent.

**No percentage and no ETA are published.** The phases' costs are wildly unequal and one of them
dominates; a percentage derived from a phase index would be a confident-looking number that means
nothing — the same defect ADR-003's accepted context criticises in ADR-002 D-9's evenly-split tie-break.
The client is given the phase, its ordinal, the phase count, and the units within the current phase, and
can render truthfully from those.

**Consequences.**
- *Positive*: Purely additive on any population — seven `AddColumn` calls, no table scan, no lock, safe
  on the production database as it stands.
- *Positive*: `ProgressUpdatedAt` does double duty as the liveness signal the D-54.5 sweep reads. That is
  deliberate: one timestamp, one meaning ("this run last did something"), two consumers.
- *Negative*: **A phase that runs long looks identical to a phase that has hung**, unless it reports
  units — and the dominant phase is exactly the one where that matters most.
- *Negative*: The batch row is now written repeatedly during a run, on a row the poller is also reading.
  D-54.8 exists because of that.
- *Neutral*: The migration's `Down` is scaffolded and unguarded, unlike the import-pipeline migration's
  hand-written guards around `Students.AlternateEmail`. Everything dropped here is telemetry about a run
  and is regenerated by re-running the import. **That reasoning is specific to these seven columns** — a
  future column on this table holding something irreplaceable must not inherit this rollback by analogy.

---

### D-54.8: `ExecuteAsync`'s save boundaries change — the tally splits off the fan-out, which is chunked

**Context.** `ExecuteAsync` currently ends its third pass with one save that does two things at once:

```csharp
ledger.Apply(_db);          // the fan-out: SisImportRowEntity rows, per row per touched entity
Tally(batch, staged);       // the five counters and the final status
batch.FinishedAt = DateTime.UtcNow;
await _db.SaveChangesAsync(ct);
```

At 21,497 rows the fan-out is on the order of **190,000 row-entity inserts**, and they commit in the same
transaction as the `UPDATE` to the batch row. That transaction therefore **holds an exclusive lock on
the batch row for the whole of the longest phase.**

**And the poller reads that row.** Under SQL Server's default `READ COMMITTED` with locking — and nothing
in this application configures otherwise — a reader blocks on an X-locked row. Row-versioning
(`READ_COMMITTED_SNAPSHOT`, or `ALLOW_SNAPSHOT_ISOLATION` with an explicit snapshot transaction) would
avoid it, and `EventService`'s manifest documentation already declines to treat that as a feature's
decision: it is a database-level setting with far more blast radius than one endpoint. **RCSI is off in
production.** So the progress endpoint would hang for the duration of the phase it exists to report on —
a progress bar that freezes exactly when the work is heaviest.

**Decision.** Two changes inside `ExecuteAsync`:

1. **`Tally` and `FinishedAt` split off into their own save**, after the fan-out has committed. The batch
   row is then locked only for its own short update.
2. **The fan-out is chunked at 500 `SisImportRowEntities` per save** — *not* 500 source rows. A clean
   roster row fans out to about ten entities, so a chunk is roughly fifty source rows. Each chunk is a
   bounded transaction, and progress is written between chunks (D-54.7) — which is what makes the
   dominant phase reportable at all.

   **The unit is the point, and an earlier draft of this line said "500 rows", which is ten times
   larger and lands in the wrong place.** SQL Server escalates to a table lock at roughly 5,000
   row/page locks on one object; 500 source rows is ~5,000 entities, i.e. exactly the threshold the
   chunking exists to stay under. 500 entities sits an order of magnitude below it. The chunk boundary
   also falls *between* source rows, never inside one — a row's outcome and its own fan-out always
   commit together, so a part-written fan-out is readable rather than merely inconsistent.

**Consequences.**
- *Positive*: The poll stays responsive during the phase that dominates the run, which is the whole point
  of the feature.
- *Positive*: Lock duration on `SisImportRowEntities` drops from one long transaction to many short ones,
  which is also better for anything else touching that table.
- *Negative — accepted, and this is the real cost.* A failure mid-phase now leaves **some `SisImportRows`
  terminal (`Inserted`/`Updated`/`Failed`/`Skipped`) and some still `Pending`**, where today an
  interrupted run leaves all of them `Pending`. The row ledger is no longer all-or-nothing. Two things
  make it acceptable: **both states repair identically** — `ExecuteAsync` opens by `ExecuteDelete`-ing
  the previous attempt's row entities and rebuilding the fan-out from scratch, so a re-run does not
  double-count either way — and **the inconsistency exists only on a `Failed` batch**, which is a batch
  nobody is meant to read as a record of anything. A `Completed` batch is unchanged.
- *Negative*: 500 is a chosen number, not a measured one. It trades round trips against lock duration and
  against how often progress can advance.
- *Neutral*: `Tally` itself is unchanged, including its status derivation (`CompletedWithErrors` /
  `CompletedWithWarnings` / `Completed`). Only *when* it saves has moved.

---

## Drift from the Technical Plan

Six entries. Two of them are amendments to existing records rather than new drift, and saying which is
which is the point of the section.

### §6 (line 483) — Hangfire is named for this endpoint; this is a partial return, not conformance

Recorded in D-54.1. The important half is the relationship to **ADR-003 D-21**, which decided the
*opposite way* for the close-time freeze: the freeze runs inline, and D-21's stated reasoning includes
that a background job would be worse. **The two must be readable together, or D-21 reads as a standing
policy against background work in this system — which it is not.**

They differ because **the operations differ**, not because D-21 was wrong:

| | ADR-003 D-21 — the close-time freeze | ADR-004 D-54 — the roster import |
|---|---|---|
| Transaction shape | One `SaveChangesAsync` — one transaction, all-or-nothing by construction | **No wrapping transaction, by design** (`SisImportService`) — passes commit independently |
| What a background window would expose | An event `Closed` with its denominator still live — precisely the state ADR-003 D-13 exists to eliminate | Nothing. A partially-imported roster is a valid intermediate state the pipeline already tolerates |
| Recovery from a lost job | The event is permanently in the bad state; there is no operator action that repairs it | Press Run again. Every write is an idempotent upsert; already-written rows report `Skipped` |
| Duration | Bounded by the audience, and the organiser is waiting for the number | Minutes, and the operator cannot be expected to wait |

D-21 chose atomicity because atomicity was available and its absence was unrecoverable. D-54 chooses
detachment because atomicity was **never** available here (deliberately) and there is no consistency
window to protect. Both remain correct. **Neither is precedent for the other.**

D-21's own "Negative" bullet — that the close is `O(audience)` inside an HTTP request, untested above
n=2 — is untouched by this ADR and remains open. The import taught the lesson the close has not yet
had to learn; that the same shape exists there is worth noticing, and it is not fixed here.

### §3 — background work belongs in `EAMS.Worker`; this keeps it in `EAMS.Api`

**This extends ADR-003 D-21's existing drift record. It does not open a new one.** D-21 already noted
that neither the worker project nor Hangfire exists in this build. The `BackgroundService` lands in
`EAMS.Api` for the reason Option 3 was rejected: a second deployable needs privileges the deployment
does not have. If `EAMS.Worker` is ever stood up, this `BackgroundService` and `ISisImportRunQueue`'s
implementation are what move into it, and the interface is what makes that a move rather than a rewrite.

### §4.12 — `SisImportBatches` gains seven columns the plan does not define

Additive, nullable, no defaults, no backfill, no rename, no re-type (D-54.7). **This is the class of
drift ADR-001 exists to register** — the plan's §4 is the historical contract and this document is its
errata, exactly as ADR-001 D-5 did for `TermId` and `SkippedRows` on the same table.

### §14 — "SignalR + import metrics"; this uses polling

**Recorded as a decision, not as an omission.** It is the same trade ADR-003's neighbours already made:
D-29 deferred §5/§6.4's SignalR hub behind a cursor-delta polling endpoint for live attendance. Import
progress is a single row, read by one operator, at human-readable intervals — the case where a hub buys
the least. And here polling **actively helps**: under D-54.6 the poll is what keeps the application pool
from idling out mid-import. A push transport would have removed the thing keeping the process alive.

### D-38 — the generated OpenAPI document is the contract

Two shape changes:

- **`SisImportBatchDto` gains eight members** — the seven progress fields plus the computed `IsTerminal`.
  `docs/api/openapi.json` **has already been regenerated** for this half.
- **`POST /sis/import/{batchId}/run` moves `200` → `202`**, and gains `409` as a documented response for
  a refused claim. Under D-38 the document is the contract, so this is not complete until the endpoint's
  `ProducesResponseType` attributes and the regenerated document both say `202`. A contract asserting
  `200` for an endpoint that returns `202` is the exact failure D-42 was written about: **a generated
  contract that states something untrue is worse than the hand-written one it replaced, because the
  whole premise is that it cannot be.**

### ADR-001 D-6 — tightened, not drifted

D-54.3 makes the tenant filter *stricter* (a background scope that cannot name its school throws rather
than running unfiltered), so nothing about D-6 is contradicted. **The near-miss is recorded anyway**, in
full, because the lesson is not "we tightened a filter" — it is that D-6's central claim was almost
proven by a mechanism D-6 did not foresee, in a way no test in the suite could have detected. Recording
only the fix would delete exactly the information that stops the next person reintroducing it.

## Accepted Context (not drift)

- **Implementation status at the time of writing.** This ADR records decisions; it does not certify that
  all of them are built. Landed already: the `SisImportProgress` migration, the `SisImportPhase`
  constants, the `SisImportBatchDto` progress fields and `IsTerminal`, the regenerated OpenAPI document
  for the DTO, and the two interim fixes described in the Context. Not yet landed at the time of writing:
  `ISisImportRunQueue` and its `BackgroundService`, the ambient tenant of D-54.3, the atomic claim of
  D-54.4, the sweep of D-54.5, the progress *writer*, and the save-boundary change of D-54.8. **Until the
  writer lands, every progress column reads `NULL`** — which is precisely what D-54.7 says `NULL` means.
  This follows ADR-001's line: the ADR records what changed and why; each part lands in its approved
  phase.
- **The 21,497-row roster is the largest input anyone has described, and it is real.** Every duration
  claim in this document comes from that one file on that one VM. Nothing here is measured at any other
  size, and the 500-entity fan-out chunk and the sweep threshold are the two numbers most exposed to that.
- **`RunCommandTimeoutSeconds` (600) is unchanged and still applies.** The per-command budget the import
  raises its connection to is orthogonal to where the run executes: it bounds one command, not the run.
  A ten-minute command is still stuck rather than slow, and should still fail.
- **The re-run guard's asymmetry is deliberate and unchanged.** `Pending` and `Failed` are re-runnable;
  `Completed`, `CompletedWithWarnings` and `CompletedWithErrors` are not, because a finished batch is the
  record of what that run did and re-running it would rewrite its counters. Re-importing means uploading
  the file again, which produces a second batch — and that second batch reporting every row `Skipped` is
  the idempotency proof.

## Consequences (overall)

### Positive
- The operator gets an answer in milliseconds and a truthful progress signal for the rest of the run,
  against the largest roster anyone has produced.
- The unrecoverable `Running` state now has **three** independent exits where it had none: the removed
  catch filter, the graceful-shutdown hook, and the startup sweep.
- A cross-tenant write that would have passed the entire test suite was found before it shipped, and the
  reasoning is recorded rather than only the fix.
- A race that pre-existed this work — the three-statement re-run guard — is closed by a single statement,
  not by hoping the window stays small.
- The Hangfire question is now *answered with conditions* rather than left implicit, and the answer is
  reversible for the price of one file.

### Negative
- **Durability is the process.** A hard kill loses the in-flight run and any queued one. This is the
  system's weakest point after this change, and it is exactly what Option 2 would have bought.
- **A documented human behaviour — leaving the tab open — is part of the reliability story** (D-54.6),
  and nothing detects its absence.
- **Two numbers in this design are guesses**: the 500-entity fan-out chunk and the sweep's staleness threshold.
  Neither is measured; both are cheap to change and neither has a test that would notice a bad value.
- **`Running` lost fidelity** (D-54.2): it now covers both "queued" and "executing", distinguishable only
  by a `NULL` `ProgressPhase`.
- **The re-runnable-status rule is now written in two languages** (D-54.4), in two files, and only one of
  them carries the explanation.
- Five documents to read together (§4, ADR-001, ADR-002, ADR-003, ADR-004). ADR-003 already called the
  consolidation due at four; this is five, and the consolidation is now overdue rather than due.

### Neutral
- Decision numbering remains continuous: ADR-001 D-1…D-6; ADR-002 D-7…D-11; ADR-003 D-12…D-21;
  D-22…D-46 shipped-but-unwritten and D-47…D-53 proposed, both tracked in `README.md`; ADR-004 **D-54**.
  A future ADR continues from **D-55**.
- §6's published API shape changes in exactly two ways: one response code on one endpoint, and eight
  additive members on one DTO. No route, verb, or request body changes.
- The consolidating ADR moves to **ADR-005**. `README.md` is the amendment; ADR-003's follow-up box still
  says ADR-004 and cannot say otherwise.

## Open Questions

Things this ADR deliberately does **not** decide. None is a blocker for D-54; each will need an answer.

1. **What "stale" means for the sweep** (D-54.5). A concrete threshold against `ProgressUpdatedAt`,
   chosen against the slowest phase of the largest real roster — not guessed.
2. **What a caller sees when the queue refuses an enqueue** (D-54.1, D-54.4). The claim is released; the
   status code is not decided here. `409` (a conflicting run is in progress) and `503` (temporarily
   unable to accept work) tell the operator different things about whether to retry.
3. **Multi-instance and IIS web-garden behaviour.** The queue is per-process, so two worker processes are
   two independent queues with two independent sweeps. The atomic claim (D-54.4) makes that *safe* — two
   processes cannot both run one batch — but nothing about the design is *correct* under scale-out, and
   nothing today prevents an app-pool from being configured with more than one worker process. Worth
   deciding before anyone reaches for scale-out as a performance answer.
4. **Whether the dominant phase reports units.** D-54.7 provides the columns; which phases populate them
   is an implementation choice with a real consequence (a long phase and a hung phase look identical
   without them).
5. **Whether a keep-alive belongs in the SPA poll** (D-54.6). Accepting the idle timeout is decided;
   whether the import page should keep polling on a slower cadence after the operator navigates away
   within the SPA is not.
6. **What the import page shows for a `Running` batch with `NULL` progress** — i.e. the D-54.2 window
   where "queued" and "executing" are indistinguishable. A UI decision, not an architectural one, but it
   is the first thing an operator sees.

## Follow-Up Actions

Ordered by consequence, not by effort.

- [ ] **Build the two-school test for D-54.3.** The suite has one school, and with one school the
      unfiltered and filtered queries are indistinguishable. This is the highest-value missing test in
      this ADR: without it, the fix for a cross-tenant write is asserted by reading.
- [ ] **Pin D-54.4's claim with a concurrency test** — two simultaneous `POST /run` calls on one batch,
      asserting exactly one `202` and one `409`. Negative-control it: revert the `ExecuteUpdate` to the
      read-check-write and watch it fail for the right reason.
- [ ] **Finish the D-38 contract change**: `POST /run` declares `202` (and `409`), and
      `docs/api/openapi.json` is regenerated for it. The DTO half is already done; a contract that still
      says `200` is the D-42 failure.
- [ ] **Write the D-54.6 operating guidance** into `docs/DEPLOY-RUNBOOK.md` and onto the import page:
      leave the page open until the import finishes, because the poll is what keeps the app pool alive.
- [ ] **Choose the sweep threshold and the chunk size against the 21,497-row roster** rather than leaving
      both as guesses (open questions 1 and 4).
- [ ] **Pin `SisImportBatchDto.IsTerminal` and the SPA's `isTerminalStatus` against each other** with a
      test that fails if either learns a value the other has not — D-54.2's whole argument rests on them
      staying identical.
- [ ] **Decide the enqueue-refused status code** (open question 2) before the SPA has to guess.
- [ ] **Record the D-54.3 near-miss as a `feedback_*.md` memory** for the project, proposed to JJ: *a
      background DI scope has no `HttpContext`, and an `HttpContext`-derived tenant that returns `null`
      means "unfiltered", not "unknown".* It is a pattern, not a one-off, and the next background service
      will meet it.
- [ ] **Write ADR-005 as the consolidating ADR** superseding 001–004 and absorbing D-22…D-46 — now
      overdue rather than due. The chronology is still load-bearing.
- [ ] Carried forward, unchanged by this ADR: ADR-003 D-21's load-test of the close; ADR-001 D-6's
      un-stubbing of §11 auth in full; ADR-002 D-11's registrar question and the `Courses.CollegeId`
      widening deadline; D-4's mapping-version retention; the §14 retention policy for
      `SisImportRowEntities`.
