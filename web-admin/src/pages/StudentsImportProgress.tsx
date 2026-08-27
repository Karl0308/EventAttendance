// One roster-import batch, watched — and then read.
//
// ---------------------------------------------------------------------------------------------
// Why this is a route of its own
// ---------------------------------------------------------------------------------------------
//
// `POST /sis/import/{batchId}/run` answers **202** now. The run is detached: the server accepts the
// job, says `Running`, and the work carries on behind the request for as long as it takes. So there is
// no longer a moment where the run's reply *is* the result, and the screen that used to render that
// reply — `StudentsImport`'s third step — was rendering a batch that had barely started.
//
// A URL rather than a step in a wizard, because the thing on this screen now outlives the visit. An
// import takes minutes; an operator reloads, shares the address with whoever asked them to run it,
// comes back to it after lunch, or opens it cold to find out how last night's went. Every one of those
// is a `GET` on a batch id, and a wizard step reachable only by having pressed Run cannot serve any of
// them.
//
// ---------------------------------------------------------------------------------------------
// Why there is no percentage bar and no estimate
// ---------------------------------------------------------------------------------------------
//
// The phases cost wildly unequal amounts and one of them dominates. A bar computed from
// phase-number/phase-count — or from units, which reset every phase — would move quickly to somewhere
// near two thirds, sit there for six minutes, and then finish in a second. That is not a slow bar; it
// is a bar that has told the operator something false about how long is left, at the exact moment they
// are deciding whether to wait or to go and do something else. What is actually known is *which phase*
// and *how far through that phase*, so that is what is shown. See `NO_ESTIMATE_IS_HONEST`.
//
// ---------------------------------------------------------------------------------------------
// Why the clock is not in the live region
// ---------------------------------------------------------------------------------------------
//
// This screen has a `role="status"`, and it carries **the phase and nothing else**. The elapsed time
// and the "last heard from" line tick once a second, and a live region whose text changes every second
// makes a screen reader recite a stopwatch for the entire length of the import — which does not
// merely annoy, it buries the one announcement that matters (the phase changing) under three hundred
// that do not. The clocks are `aria-hidden` and sit outside it. Nothing here is ever `role="alert"`:
// progress is not an alert, and an assertive region would interrupt whatever the user is doing every
// time a phase ticked over.

import { useEffect, useMemo, useRef, useState } from "react";
import { Link as RouterLink, useLocation, useParams } from "react-router-dom";
import {
  Alert,
  AlertTitle,
  Box,
  Breadcrumbs,
  Button,
  Chip,
  CircularProgress,
  Divider,
  MenuItem,
  Link as MuiLink,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
  useMediaQuery,
} from "@mui/material";
import Grid from "@mui/material/Grid2";
import { api, describeApiError } from "../api";
import { advise, isResendUnsafe } from "../apiGuidance";
import { useApiPoll } from "../useApiPoll";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import { WriteFailureAlert } from "../components/WriteFailureAlert";
import {
  LEAVE_THE_TAB_OPEN,
  NO_ESTIMATE_IS_HONEST,
  OUTCOME_UNKNOWN_FROM_HERE,
  POLL_FAILURES_BEFORE_UNKNOWN,
  POLL_INTERVAL_MS,
  RUN_IS_IDEMPOTENT,
  describeImportPhase,
  importOutcomeOf,
} from "../sisImport";
import { SIS_IMPORT_ROW_RESULT, SIS_IMPORT_STATUS } from "../types";
import type { SisImportBatch, SisImportRow } from "../types";

const HEADING_ID = "roster-import-progress-heading";

/** What the results table offers to look at. `Failed` first because it is why the filter exists. */
const ROW_FILTERS = [
  SIS_IMPORT_ROW_RESULT.Failed,
  SIS_IMPORT_ROW_RESULT.Skipped,
  SIS_IMPORT_ROW_RESULT.Inserted,
  SIS_IMPORT_ROW_RESULT.Updated,
] as const;

const NO_SUCH_BATCH =
  "The API has no batch with this id. Either it was removed since it was staged, or the address is " +
  "not one this API knows — start again from the file.";

const NO_BATCH_IN_URL =
  "This address has no batch id in it, so there is nothing to look up. Start the import again from " +
  "the file.";

/**
 * Said for a batch that is still `Pending` on a screen reached by pressing Run.
 *
 * It is the honest reading of a specific failure: the run POST did not reach the server, so nothing
 * started and — the part that matters — **nothing was written**. The old flow reported this as an
 * unknown outcome, which sent an operator looking for damage there is none of.
 */
const BATCH_IS_STAGED_NOT_RUN =
  "This batch is staged and has not run. Nothing has been written to the roster. If you pressed Run " +
  "and landed here, the request did not reach the server — go back and press it again.";

const HEADING_NOT_RUN = "The import was not started";
const HEADING_MAYBE_RUN = "The import may have been started";
const RUN_AGAIN_RESEND_WITHHELD =
  "Run again is disabled because this build cannot tell whether the second run was accepted. Running " +
  "twice could not double-import — a second run reports every row Skipped — but reload this page so " +
  "you are reading the batch's real status before pressing it again.";

/**
 * Why Run again is not offered on a `Failed` batch opened cold.
 *
 * The run sends the term as a **confirmation** the server checks against the batch (ADR-001 D-5), and
 * the value sent has to be the one the operator chose — re-reading it from `batch.termId` would have
 * the confirmation check itself and always agree. A page opened from a bookmark, a reload or a shared
 * link has no chosen term behind it, so the button that needs one is not drawn, and the way back to a
 * screen that asks for one is offered instead.
 */
const RUN_AGAIN_NEEDS_THE_TERM =
  "Re-running from here needs the term to be confirmed, and this page was opened without one — a " +
  "reload, a bookmark, or a link from somewhere else. Start from Import roster and choose the term " +
  "deliberately; the batch itself is unharmed, and a re-import reports every row that already landed " +
  "as Skipped.";

const MILLISECONDS_PER_SECOND = 1_000;
const SECONDS_PER_MINUTE = 60;
const CLOCK_TICK_MS = 1_000;

/**
 * The term the operator chose, handed along the navigation rather than looked up again.
 *
 * Carried in the router's `state` and not in the URL: it is a confirmation of something the operator
 * did on the previous screen, and a value in a query string is one anybody can edit into a different
 * term before pressing Run again — which is the exact mistake the confirmation exists to catch.
 */
interface ImportProgressState {
  termId: string;
}

/**
 * `useLocation().state` is `unknown` as far as this file is concerned, and it is narrowed rather than
 * asserted. It is genuinely arbitrary: the browser restores it from session history across a reload,
 * a Back from a page that pushed something else lands here with that something else, and none of that
 * is under this component's control.
 */
function termIdFrom(state: unknown): string | undefined {
  if (typeof state !== "object" || state === null) return undefined;
  const candidate = (state as Partial<ImportProgressState>).termId;
  return typeof candidate === "string" && candidate !== "" ? candidate : undefined;
}

/** `Date.parse` as a value rather than a `NaN`: an unparsable timestamp is absent, not zero. */
function instantOf(iso: string | undefined): number | undefined {
  if (iso === undefined) return undefined;
  const parsed = Date.parse(iso);
  return Number.isFinite(parsed) ? parsed : undefined;
}

/** "4m 12s" / "38s". Whole seconds — a progress clock does not need tenths and reads worse with them. */
function describeDuration(ms: number): string {
  const seconds = Math.max(0, Math.floor(ms / MILLISECONDS_PER_SECOND));
  const minutes = Math.floor(seconds / SECONDS_PER_MINUTE);
  const rest = seconds % SECONDS_PER_MINUTE;
  return minutes === 0 ? `${rest}s` : `${minutes}m ${rest}s`;
}

/**
 * A clock that ticks only while something is running.
 *
 * Stopped on a terminal batch rather than left going, because a finished screen re-rendering once a
 * second forever is a battery cost and a source of spurious `act()` warnings in tests, and neither
 * buys a number anybody is reading.
 */
function useTicker(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!active) return;
    const id = setInterval(() => setNow(Date.now()), CLOCK_TICK_MS);
    return () => clearInterval(id);
  }, [active]);

  return now;
}

export default function StudentsImportProgress() {
  const { batchId } = useParams();
  const location = useLocation();
  const termId = termIdFrom(location.state);

  /**
   * The watch. `getImportBatch` answers `undefined` for a 404, so `T` is `SisImportBatch | undefined`
   * and the missing-batch case arrives as data rather than as a failure — which is what lets `isDone`
   * stop the poll on it. A 404 does not become a batch by being asked for again.
   */
  const poll = useApiPoll(
    () => (batchId === undefined ? Promise.resolve(undefined) : api.getImportBatch(batchId)),
    [batchId],
    {
      intervalMs: POLL_INTERVAL_MS,
      // `isTerminal` is the server's own answer, never a status list held here. See `SisImportBatch`.
      isDone: (batch) => batch === undefined || batch.isTerminal,
    },
  );

  const batch = poll.data;

  /**
   * Re-running a `Failed` batch, from here rather than from the file step.
   *
   * The run is idempotent and a `Failed` batch is re-runnable, so this is the button that finishes a
   * part-way import — and it is on this screen because this is where an operator finds out the import
   * is part-way. It answers 202 like the first press, so what follows a success is not a result to
   * render but a poll to restart.
   */
  const run = useApiMutation((id: string, term: string) => api.runImport(id, { termId: term }));

  /**
   * Focus follows the arrival, not every poll. Keyed on the batch's *status*, so a keyboard or
   * screen-reader user is put on the heading when the screen becomes a different kind of screen —
   * running to finished — and is left alone for the hundred-odd polls in between, which change numbers
   * and nothing else. Keying it on the data would steal focus every two seconds.
   */
  const heading = useRef<HTMLDivElement>(null);
  const status = batch?.status;
  useEffect(() => {
    heading.current?.focus();
  }, [status]);

  const startRunAgain = () => {
    if (batchId === undefined || termId === undefined) return;
    void run.run(batchId, termId).then((settled) => {
      if (settled.outcome !== "succeeded") return;
      // 202: what came back is a `Running` batch, so the answer to "what happened?" is the poll, not
      // this reply. Restarting it is the whole handling.
      poll.reload();
    });
  };

  return (
    <Box>
      <Breadcrumbs sx={{ mb: 1 }}>
        <MuiLink component={RouterLink} to="/students" underline="hover" color="inherit">
          Students
        </MuiLink>
        <MuiLink component={RouterLink} to="/students/import" underline="hover" color="inherit">
          Import roster
        </MuiLink>
        <Typography color="text.primary">This batch</Typography>
      </Breadcrumbs>

      <Typography
        ref={heading}
        tabIndex={-1}
        id={HEADING_ID}
        variant="h5"
        component="h1"
        fontWeight={700}
        sx={{ mb: 2, outline: "none" }}
      >
        Import roster — {batch !== undefined && !batch.isTerminal ? "running" : "what it did"}
      </Typography>

      {batchId === undefined && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <EmptyState message={NO_BATCH_IN_URL} />
          <Stack direction="row" spacing={2} sx={{ mt: 2 }}>
            <Button variant="contained" component={RouterLink} to="/students/import">
              Import roster
            </Button>
          </Stack>
        </Paper>
      )}

      {batchId !== undefined && poll.status === "loading" && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <LoadingState label="Reading the batch…" />
        </Paper>
      )}

      {/* No data has ever arrived, so there is nothing to keep and the failure is the screen — which
          is exactly `useApiResource`'s reading, and correct at this one moment. The moment it stops
          being correct is when data HAS arrived, and that is the `stale` arm, handled inside the
          panel below rather than here. */}
      {batchId !== undefined && poll.status === "error" && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <ErrorState subject="this import batch" error={poll.error} onRetry={poll.reload} />
        </Paper>
      )}

      {batchId !== undefined && poll.data === undefined && poll.status === "ready" && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <EmptyState message={NO_SUCH_BATCH} />
          <Stack direction="row" spacing={2} sx={{ mt: 2 }}>
            <Button variant="contained" component={RouterLink} to="/students/import">
              Start again from the file
            </Button>
          </Stack>
        </Paper>
      )}

      {batch !== undefined && !batch.isTerminal && (
        <RunningPanel
          batch={batch}
          pollError={poll.status === "stale" ? poll.error : undefined}
          consecutiveFailures={poll.consecutiveFailures}
          paused={poll.paused}
        />
      )}

      {batch !== undefined && batch.isTerminal && (
        <ResultsPanel
          batch={batch}
          termId={termId}
          onRunAgain={startRunAgain}
          running={run.status === "running"}
          failure={run.status === "failed" ? { error: run.error } : undefined}
        />
      )}
    </Box>
  );
}

// ---------------------------------------------------------------------------------------------
// While it runs
// ---------------------------------------------------------------------------------------------

function RunningPanel({
  batch,
  pollError,
  consecutiveFailures,
  paused,
}: {
  batch: SisImportBatch;
  /** The last poll failed over data an earlier one returned. Said beside the data, never instead of it. */
  pollError: unknown;
  consecutiveFailures: number;
  paused: boolean;
}) {
  const now = useTicker(true);

  /**
   * MUI's spinner is a continuous rotation, which is precisely what `prefers-reduced-motion` is set to
   * avoid — and `theme.ts` sets nothing, so there is no global default to inherit and this component
   * has to ask. Asked through `useMediaQuery` rather than an `sx` media query because the answer is
   * *which element to render*, not which style to give one: a `CircularProgress` with its animation
   * switched off is a static three-quarter arc, which reads as a spinner that has frozen — a worse
   * lie than no spinner at all on a screen whose entire subject is whether something is still moving.
   */
  const reduceMotion = useMediaQuery("(prefers-reduced-motion: reduce)");

  const startedAt = instantOf(batch.startedAt);
  const heardFrom = instantOf(batch.progressUpdatedAt);

  const phase = describeImportPhase(batch.progressPhase);
  const phaseNumber = batch.progressPhaseNumber;
  const phaseCount = batch.progressPhaseCount;
  const hasPhaseCount = phaseNumber !== undefined && phaseCount !== undefined;

  /**
   * Both or neither. `progressUnitsDone` on its own is "1,412" with nothing to compare it to, and
   * `progressUnitsTotal` on its own is a denominator over a blank — and the standing rule for these
   * fields is that an absent count renders as *indeterminate*, never as `0`. A run that has done 1,412
   * rows and cannot say of how many is not a run that has done nothing.
   */
  const unitsDone = batch.progressUnitsDone;
  const unitsTotal = batch.progressUnitsTotal;
  const hasUnits = unitsDone !== undefined && unitsTotal !== undefined;

  /**
   * The live region's whole content, and it is memoised on the phase alone.
   *
   * That is not a performance concern — it is what keeps the announcement to one per phase. Everything
   * else on this panel changes every second or every two, and any of it inside `role="status"` would
   * be re-announced each time.
   */
  const announcement = useMemo(
    () => (hasPhaseCount ? `${phase} — phase ${phaseNumber} of ${phaseCount}` : phase),
    [phase, phaseNumber, phaseCount, hasPhaseCount],
  );

  const outOfTouch = consecutiveFailures >= POLL_FAILURES_BEFORE_UNKNOWN;

  return (
    // `aria-busy` says the region is being updated, which is true for as long as this panel is on
    // screen. Not `role="alert"`: progress is not an alert, and an assertive region would interrupt
    // the user every time a phase ticked over.
    <Paper variant="outlined" sx={{ p: 3 }} aria-busy>
      <Typography variant="h6" component="h2" sx={{ mb: 1 }}>
        The import is running
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Batch {batch.id.slice(0, 8)} · term {batch.termCode} · {batch.totalRows} source{" "}
        {batch.totalRows === 1 ? "row" : "rows"}
      </Typography>

      <Stack direction="row" spacing={2} alignItems="center" sx={{ mb: 2 }}>
        {!reduceMotion && <CircularProgress size={28} aria-hidden />}
        {/* Mounted whether or not it has content, so the region exists in the DOM before anything is
            put into it — a live region inserted and populated in the same commit is announced
            unreliably. The same pattern `ChooseStep` uses. */}
        <Box role="status" sx={{ minHeight: 28 }}>
          <Typography variant="subtitle1" component="p" fontWeight={600}>
            {announcement}
          </Typography>
        </Box>
      </Stack>

      {/* Not `aria-hidden`, and not live either: the counts are real information a screen-reader user
          should be able to read, and they change too often to be announced. Reachable by the virtual
          cursor, silent until someone goes to it. */}
      <Typography variant="body2" sx={{ mb: 2 }}>
        {hasUnits
          ? `${unitsDone} of ${unitsTotal} in this phase.`
          : "This phase does not report a count, so there is no number to show for it — the phase " +
            "above is what is known."}
      </Typography>

      {/* The two clocks, `aria-hidden` and outside the live region. They tick once a second; inside
          one, a screen reader would recite a stopwatch for the length of the import. */}
      <Stack spacing={0.5} sx={{ mb: 2 }} aria-hidden>
        {startedAt !== undefined && (
          <Typography variant="body2" color="text.secondary">
            Running for {describeDuration(now - startedAt)}.
          </Typography>
        )}
        {heardFrom !== undefined && (
          <Typography variant="body2" color="text.secondary">
            {/* A different fact from the one above, and the more useful of the two. Elapsed says how
                long this screen has been open; this says whether the RUN is alive. Eight minutes
                elapsed over a four-second heartbeat is healthy; forty seconds elapsed over a
                four-minute heartbeat is not. */}
            Last heard from the run {describeDuration(now - heardFrom)} ago.
          </Typography>
        )}
        {paused && (
          <Typography variant="body2" color="text.secondary">
            Checking is paused while this tab is in the background. It resumes the moment you come
            back to it.
          </Typography>
        )}
      </Stack>

      {batch.status === SIS_IMPORT_STATUS.Pending && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          <AlertTitle>This batch has not started</AlertTitle>
          <Typography variant="body2">{BATCH_IS_STAGED_NOT_RUN}</Typography>
          <Button
            component={RouterLink}
            to="/students/import"
            size="small"
            sx={{ mt: 1 }}
            color="inherit"
          >
            Back to Import roster
          </Button>
        </Alert>
      )}

      {/* One failed poll is not a failed import, and this is where that distinction is rendered. The
          numbers above are still on screen — that is what `useApiPoll`'s `stale` arm is for — and this
          says only that the last look did not answer. */}
      {pollError !== undefined && !outOfTouch && (
        <Alert severity="info" sx={{ mb: 2 }}>
          <AlertTitle>The last check did not answer</AlertTitle>
          <Typography variant="body2">
            {describeApiError(pollError)} The figures above are from the last check that did answer.
            This page tries again every couple of seconds; the run itself does not depend on this page
            hearing back.
          </Typography>
        </Alert>
      )}

      {/* The floor, not the first answer. It took several failures in a row to get here. */}
      {outOfTouch && (
        <Alert severity="warning" role="alert" sx={{ mb: 2 }}>
          <AlertTitle>This page cannot see the run</AlertTitle>
          <Typography variant="body2">{describeApiError(pollError)}</Typography>
          <Typography variant="body2" sx={{ mt: 1 }}>
            {OUTCOME_UNKNOWN_FROM_HERE}
          </Typography>
          <Typography variant="body2" sx={{ mt: 1 }}>
            {advise(pollError).message}
          </Typography>
        </Alert>
      )}

      <Alert severity="warning" sx={{ mb: 2 }}>
        <AlertTitle>Leave this tab open</AlertTitle>
        <Typography variant="body2">{LEAVE_THE_TAB_OPEN}</Typography>
      </Alert>

      <Typography variant="body2" color="text.secondary">
        {NO_ESTIMATE_IS_HONEST}
      </Typography>
    </Paper>
  );
}

// ---------------------------------------------------------------------------------------------
// What it did
// ---------------------------------------------------------------------------------------------

/**
 * The counters, what the status means, and the way to the rows behind them.
 *
 * Everything here is a statement about a **terminal** batch and nothing here may be rendered over a
 * running one — which is why the counters and the reconcile alert live in this component rather than
 * beside the progress panel. `countersReconcile` is `false` for every in-flight batch by construction,
 * so an ungated render would put "the counters do not add up — report it rather than re-running" on
 * screen for the entire length of a perfectly healthy import.
 */
function ResultsPanel({
  batch,
  termId,
  onRunAgain,
  running,
  failure,
}: {
  batch: SisImportBatch;
  /** The term the operator chose, if this page was reached by choosing one. See `RUN_AGAIN_NEEDS_THE_TERM`. */
  termId: string | undefined;
  onRunAgain: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}) {
  /**
   * The four terminal statuses as four different answers. Read from `status`, **not** from
   * `failedRows > 0` — which is what this screen used to do, and which rendered
   * `CompletedWithWarnings` identically to `Completed`. See `importOutcomeOf`.
   */
  const outcome = importOutcomeOf(batch.status);

  /**
   * A `Failed` batch has not finished, and it is the one status this screen can offer a way out of.
   * `SisImportService.RunAsync` accepts a run on `Pending` or `Failed`, and `RUN_IS_IDEMPOTENT` says
   * in as many words that re-running is how a part-way failure is finished.
   */
  const isRerunnable = batch.status === SIS_IMPORT_STATUS.Failed;
  const canRunAgain = isRerunnable && termId !== undefined;
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const counters: { label: string; value: number; note: string }[] = [
    { label: "Inserted", value: batch.insertedRows, note: "new to the academic tables" },
    { label: "Updated", value: batch.updatedRows, note: "already there, changed" },
    {
      label: "Skipped",
      value: batch.skippedRows,
      note: "already true — what a re-import reports",
    },
    { label: "Failed", value: batch.failedRows, note: "not imported" },
    { label: "Warned", value: batch.warningRows, note: "imported, with a note" },
  ];

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      {/* The status word is printed as well as coloured: three of these four are "Completed…" and two
          shades of the same icon cannot carry the difference between them. */}
      <Alert severity={outcome.severity} role="status" sx={{ mb: 3 }}>
        <AlertTitle>{outcome.heading}</AlertTitle>
        <Typography variant="body2">
          Batch {batch.id.slice(0, 8)} · term {batch.termCode} · status{" "}
          <strong>{batch.status}</strong>
          {batch.finishedAt !== undefined && ` · finished ${batch.finishedAt}`}
        </Typography>
        <Typography variant="body2" sx={{ mt: 1 }}>
          {outcome.whatItMeans}
        </Typography>
      </Alert>

      {/* The server's own sentence about why the run stopped, when it gave one. Rendered beside the
          standing copy rather than instead of it: the standing copy says what is true of every failed
          run (the applied rows are in the roster, re-running finishes it) and this says what happened
          to this one. */}
      {batch.failureReason !== undefined && (
        <Alert severity="error" sx={{ mb: 3 }}>
          <AlertTitle>What the run reported</AlertTitle>
          <Typography variant="body2" sx={{ wordBreak: "break-word" }}>
            {batch.failureReason}
          </Typography>
        </Alert>
      )}

      {/* Gated on `isTerminal` as well as being inside a component only mounted for terminal batches
          — belt and braces, and the belt is the one that states the rule where a future edit can read
          it. `countersReconcile` is false for every running batch, so this alert is a statement about
          a finished run or it is a lie about a healthy one. */}
      {batch.isTerminal && !batch.countersReconcile && (
        <Alert severity="error" role="alert" sx={{ mb: 3 }}>
          <AlertTitle>The counters do not add up</AlertTitle>
          <Typography variant="body2">
            The API reports that inserted + updated + failed + skipped does not equal{" "}
            {batch.totalRows}. That is the server’s own reconciliation check, not this screen’s
            arithmetic — report it rather than re-running.
          </Typography>
        </Alert>
      )}

      <Typography variant="h6" component="h2" sx={{ mb: 1 }}>
        {batch.totalRows} source {batch.totalRows === 1 ? "row" : "rows"}
      </Typography>

      <Grid container spacing={2} sx={{ mb: 3 }}>
        {counters.map((counter) => (
          <Grid key={counter.label} size={{ xs: 6, sm: 4, md: 2.4 }}>
            <Paper variant="outlined" sx={{ p: 1.5, height: "100%" }}>
              <Typography variant="h6" component="p">
                {counter.value}
              </Typography>
              <Typography variant="body2">{counter.label}</Typography>
              <Typography variant="caption" color="text.secondary">
                {counter.note}
              </Typography>
            </Paper>
          </Grid>
        ))}
      </Grid>

      {isRerunnable && (
        <>
          <Alert severity="info" icon={false} sx={{ mb: 3 }}>
            {RUN_IS_IDEMPOTENT}
          </Alert>
          {!canRunAgain && (
            <Alert severity="info" sx={{ mb: 3 }}>
              <AlertTitle>Re-run from the import screen</AlertTitle>
              <Typography variant="body2">{RUN_AGAIN_NEEDS_THE_TERM}</Typography>
            </Alert>
          )}
          {failure !== undefined && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_RUN}
              mayHaveApplied={HEADING_MAYBE_RUN}
              resendWithheld={RUN_AGAIN_RESEND_WITHHELD}
            />
          )}
        </>
      )}

      <Divider sx={{ my: 3 }} />

      <ImportRowsPanel batch={batch} />

      <Stack direction="row" spacing={2} sx={{ mt: 3 }}>
        {canRunAgain && (
          <Button
            variant="contained"
            onClick={onRunAgain}
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Starting…" : "Run again"}
          </Button>
        )}
        <Button
          variant={canRunAgain ? "outlined" : "contained"}
          component={RouterLink}
          to="/students"
        >
          Back to students
        </Button>
        <Button component={RouterLink} to="/students/import" disabled={running}>
          Import another file
        </Button>
      </Stack>
    </Paper>
  );
}

/**
 * The rows behind the counters, one outcome at a time.
 *
 * A separate component so the read is only ever mounted for a terminal batch — `useApiResource` starts
 * on mount, and a hook at the page level would fetch a batch's rows on every poll of a run that has not
 * produced any yet.
 *
 * **There is no `Warned` filter and there deliberately is not one.** `SisImportRowResult` is
 * `[Inserted, Updated, Failed, Skipped]`; warned is a *second axis* — `warningCode is not null` riding
 * on rows that inserted or updated — so a fifth option here would have to be a value the contract does
 * not have. The right shape is a `?warned=true` filter on the rows endpoint, which is a server change.
 * Until it exists, the warning chip in the table is how a warned row is spotted.
 */
function ImportRowsPanel({ batch }: { batch: SisImportBatch }) {
  /**
   * Opens on `Failed` when there is anything failed to look at, which is the query the endpoint exists
   * for. When there is not, `Skipped` is the next most useful — on a re-import it is every row, and it
   * is how an operator confirms the second run really did change nothing.
   */
  const [result, setResult] = useState<string>(
    batch.failedRows > 0 ? SIS_IMPORT_ROW_RESULT.Failed : SIS_IMPORT_ROW_RESULT.Skipped,
  );

  const rows = useApiResource(() => api.getImportRows(batch.id, result), [batch.id, result]);
  const shown = rows.data ?? [];

  return (
    <Box>
      <Typography variant="h6" component="h2" sx={{ mb: 1 }}>
        Rows
      </Typography>

      <TextField
        select
        size="small"
        label="Show rows that were"
        value={result}
        onChange={(e) => setResult(e.target.value)}
        sx={{ width: 240, mb: 2 }}
      >
        {ROW_FILTERS.map((option) => (
          <MenuItem key={option} value={option}>
            {option}
          </MenuItem>
        ))}
      </TextField>

      {rows.status === "loading" && <LoadingState label="Loading rows…" />}

      {rows.status === "error" && (
        <ErrorState subject="the batch rows" error={rows.error} onRetry={rows.reload} />
      )}

      {rows.status === "ready" && shown.length === 0 && (
        <EmptyState message={`No rows in this batch were ${result.toLowerCase()}.`} />
      )}

      {rows.status === "ready" && shown.length > 0 && (
        <TableContainer component={Paper} variant="outlined" sx={{ overflowX: "auto" }}>
          <Table size="small">
            <caption style={{ captionSide: "bottom", padding: "8px 16px" }}>
              Rows of batch {batch.id.slice(0, 8)} whose result was {result}. Row numbers are lines
              in the source workbook — open it there to fix them.
            </caption>
            <TableHead>
              <TableRow>
                <TableCell component="th" scope="col">
                  Row
                </TableCell>
                <TableCell component="th" scope="col">
                  Result
                </TableCell>
                <TableCell component="th" scope="col">
                  What the import said
                </TableCell>
                <TableCell component="th" scope="col">
                  Entities touched
                </TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {shown.map((row) => (
                <RowLine key={row.id} row={row} />
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}

/**
 * One row.
 *
 * The result is a word in its own cell as well as a chip, so nothing here is carried by colour alone.
 * The message is the server's own sentence — `errorMessage` for a failure, `skipReason` for a skip,
 * `warningMessage` for an annotation — and the row's *contents* are deliberately not available to
 * print. See the header of `StudentsImport.tsx`.
 */
function RowLine({ row }: { row: SisImportRow }) {
  const said = row.errorMessage ?? row.skipReason ?? row.warningMessage;

  return (
    <TableRow>
      <TableCell component="th" scope="row">
        {row.rowNumber}
      </TableCell>
      <TableCell>
        <Stack direction="row" spacing={1} alignItems="center">
          <span>{row.result}</span>
          {row.warningCode !== undefined && (
            <Chip size="small" variant="outlined" label={row.warningCode} />
          )}
        </Stack>
      </TableCell>
      <TableCell sx={{ maxWidth: 520 }}>
        <Typography variant="body2" sx={{ wordBreak: "break-word" }}>
          {said ?? "—"}
        </Typography>
      </TableCell>
      <TableCell>{row.entities.length}</TableCell>
    </TableRow>
  );
}
