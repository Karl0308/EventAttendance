// The §10 roster import, as the three steps it actually is.
//
// The third step is **not on this screen**. `POST /sis/import/{batchId}/run` answers 202 and the run
// carries on behind the request, so what used to be a results panel rendered from that reply is now
// its own route — `/students/import/:batchId`, `StudentsImportProgress` — which polls the batch and
// then shows what it did. This screen's job ends at handing that route a batch id.
//
// ---------------------------------------------------------------------------------------------
// Why a route and not a dialog
// ---------------------------------------------------------------------------------------------
//
// Choose a term and a file → read what is in the file → run it → read what it did. An operator sits
// with the middle step: the preview is where "this workbook has one course in it" is caught, and
// catching it means comparing numbers on screen against what the registrar said they sent. A modal is
// the wrong container for that — it cannot be left open while something else is checked, Escape throws
// the staged batch out of view, and there is no URL to send anyone.
//
// ---------------------------------------------------------------------------------------------
// Why upload and run are two presses
// ---------------------------------------------------------------------------------------------
//
// `POST /sis/import/upload` stages and writes nothing; `POST /sis/import/{batchId}/run` applies.
// Collapsing them into one button would remove the only moment between "a file was chosen" and "the
// roster of every student in the school was rewritten" — which is the moment the two-step design
// exists to create. So the upload step says out loud that nothing has happened yet, and the run step
// says out loud that this is the one that writes.
//
// ---------------------------------------------------------------------------------------------
// What this screen does not show
// ---------------------------------------------------------------------------------------------
//
// Row contents. §10 reads and writes the full roster — names and institutional e-mail addresses — and
// it is open (ADR-001 D-6: `sis.import` is declared, not enforced). `SisImportRow` does not carry
// `rawData` at the seam, so there is nothing here to leak by accident; what a failed row needs is its
// row number, which is the line an operator opens the workbook and jumps to, and the server's own
// sentence about why.
//
// **One channel does carry row content, and it is deliberate.** `row.errorMessage ?? row.skipReason ??
// row.warningMessage` is server-authored prose that may quote the cell it is about ("no such program
// code 'BSCS-X'", and a name where the server names one). It is necessary — an operator cannot fix a
// row without being told what is wrong with it — and it is bounded by what the server chose to say
// rather than by anything this screen decides. So this screen shows no roster content *of its own*;
// that is not the same as showing none, and a reader should not conclude the stronger claim.

import { useEffect, useRef, useState } from "react";
import { Link as RouterLink, useNavigate } from "react-router-dom";
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
  Step,
  StepLabel,
  Stepper,
  TextField,
  Typography,
} from "@mui/material";
import Grid from "@mui/material/Grid2";
import { api, describeApiError } from "../api";
import { advise, isResendUnsafe } from "../apiGuidance";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import { WriteFailureAlert } from "../components/WriteFailureAlert";
import {
  ROSTER_FILE_EXTENSION,
  RUN_IS_IDEMPOTENT,
  TERM_IS_NOT_INFERRED,
  UPLOAD_WRITES_NOTHING,
  describeSize,
  importProgressPath,
  rosterFileProblem,
  uploadRefusalOf,
} from "../sisImport";
import { SIS_IMPORT_STATUS } from "../types";
import type { SisImportPreview, Term } from "../types";

const TERM_FIELD_ID = "roster-import-term";
const FILE_FIELD_ID = "roster-import-file";
const FILE_HELP_ID = "roster-import-file-help";
const HEADING_ID = "roster-import-heading";

/** The three steps, named once so the Stepper and the headings cannot drift apart. */
const STEPS = ["Choose term and file", "Review what is in it", "Run and check"] as const;

const NO_TERMS =
  "This school has no terms on file, so there is nothing to import a roster into. A term has to exist " +
  "before a batch can be filed against one.";

const HEADING_NOT_UPLOADED = "The workbook was not staged";
const HEADING_MAYBE_UPLOADED = "The workbook may have been staged";
const UPLOAD_RESEND_WITHHELD =
  "Upload is disabled because this build cannot tell whether the file was staged. A staged batch that " +
  "nobody runs writes nothing to the roster, so an extra one is harmless — but reload this page before " +
  "trying again so you are looking at the batch that actually exists.";

const HEADING_NOT_RUN = "The import was not started";
const HEADING_MAYBE_RUN = "The import may have been started";
/**
 * Kept, and now much harder to reach — which is the point of the change rather than a leftover.
 *
 * A run whose outcome this client cannot determine no longer stops here at all: the screen holds the
 * batch id, a `GET` on it is `retryable: "safe"`, and going to look is strictly better than telling
 * somebody the answer is unknown before anybody has asked. So `submitRun` navigates to the progress
 * route on an *unknown-effect* failure and this alert is never drawn for one. What is left for it is
 * the failure the server **decided** — a 400, a 409 — where `retryable` is `"safe"` and the withheld
 * sentence below is not rendered either. It stays because `WriteFailureAlert` requires the text for
 * the case where a future error kind is neither, and a component that has to be handed a sentence
 * should not be handed a wrong one.
 */
const RUN_RESEND_WITHHELD =
  "Run is disabled because this build cannot tell whether the run was accepted. Running twice could " +
  "not double-import — a second run reports every row Skipped — but check the batch first, which the " +
  "button below does without changing anything.";

/**
 * What the check found, when what it found is that the run is not over.
 *
 * The results step is written on the precondition that the batch is **terminal** — its counters are the
 * final answer and `countersReconcile` is a statement about a finished run. `Pending` and `Running`
 * break that precondition, and a batch in either state is exactly what the check returns on the path
 * this button exists for: a run that timed out before the server had done anything. Sent to the results
 * step, a `Pending` batch renders "The import finished" in success styling over five zero counters, and
 * `countersReconcile` — false, because 0 ≠ 536 — tells the operator to report it rather than re-run,
 * which is the one thing they must not be told when re-running is the entire fix.
 */
const BATCH_IS_STAGED_NOT_RUN =
  "The batch is staged and has not run — the API reports it as Pending, so the run did not reach the " +
  "server and nothing was written. Run import is safe to press.";

/** Where the flow is. A union rather than a step number, so the data each step needs travels with it. */
type Stage =
  | { name: "choose" }
  /**
   * `termId` is carried on the stage rather than re-read from `preview.batch.termId`, and the
   * difference matters at exactly one moment: the run's `termId` is a **confirmation** the server
   * checks against the batch (ADR-001 D-5). Confirming with a value read back out of the batch would
   * make it check itself and always agree — so the value that goes back is the one the operator chose.
   */
  | { name: "preview"; preview: SisImportPreview; termId: string };

/**
 * Two stages, three steps. The third step belongs to `/students/import/:batchId` and is drawn here
 * only as the label of somewhere this flow is going — which is what a stepper is for.
 */
const stepOf = (stage: Stage): number => (stage.name === "choose" ? 0 : 1);

export default function StudentsImport() {
  const navigate = useNavigate();
  const terms = useApiResource(() => api.listTerms(), []);

  /**
   * The chosen term. **Deliberately not pre-filled with the current one**, unlike the audience picker.
   *
   * There the default is a convenience over a reversible choice — a section attached to the wrong
   * event is detached again. Here ADR-001 D-5 names the opposite: a batch written under the wrong term
   * looks entirely correct afterwards, because every later query is term-scoped, and only a hand
   * correction undoes it. A pre-filled picker is a field an operator can press past without reading,
   * so this one starts empty and the current term is merely *marked* as such.
   */
  const [termId, setTermId] = useState("");
  const [file, setFile] = useState<File | undefined>(undefined);
  /** Refused before any bytes go out — wrong extension, empty, over the limit. See `sisImport.ts`. */
  const [fileProblem, setFileProblem] = useState<string | undefined>(undefined);

  const [stage, setStage] = useState<Stage>({ name: "choose" });

  const upload = useApiMutation((chosen: File, term: string) => api.uploadRoster(chosen, term));
  const run = useApiMutation((batchId: string, term: string) =>
    api.runImport(batchId, { termId: term }),
  );
  /**
   * A **read**, run from a button — which is what `useApiMutation` is for as much as a write is: it
   * happens because someone pressed something, at most once per press, and its result is an outcome to
   * act on rather than the screen's subject. It is offered only after a run whose outcome is unknown,
   * and it is the honest way out of that: `GET` changes nothing, so it can be pressed freely at the one
   * moment pressing Run again cannot be.
   */
  const check = useApiMutation((batchId: string) => api.getImportBatch(batchId));
  /** Said when the check comes back with no such batch — a 404 is an answer, not a failure. */
  const [checkNote, setCheckNote] = useState<string | undefined>(undefined);

  /**
   * Focus follows the step. Each stage replaces the whole body of the page, so a keyboard user who
   * pressed Upload is otherwise left on a button that no longer exists (focus falls to `document.body`)
   * and a screen-reader user is told nothing about the screenful of new content — WCAG 2.4.3. The
   * heading is a focus destination rather than a tab stop, so `tabIndex={-1}`.
   */
  const heading = useRef<HTMLDivElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, [stage.name]);

  const chooseFile = (chosen: File | undefined) => {
    upload.reset();
    if (chosen === undefined) {
      setFile(undefined);
      setFileProblem(undefined);
      return;
    }
    const problem = rosterFileProblem(chosen);
    // The file is remembered either way, so the refusal can name it and the input is not silently
    // emptied under the user. What the problem does is disable the Upload button, beside the sentence
    // saying why — this codebase's standing rule about controls that grey out without explaining.
    setFile(chosen);
    setFileProblem(problem);
  };

  const submitUpload = () => {
    if (file === undefined || fileProblem !== undefined || termId === "") return;
    void upload.run(file, termId).then((settled) => {
      if (settled.outcome !== "succeeded") return;
      setStage({ name: "preview", preview: settled.data, termId });
    });
  };

  /**
   * Starts the run, and then **leaves** — in both of the two directions that matter.
   *
   * On success the reply is a 202 carrying a `Running` batch, so there is nothing to render here: the
   * answer to "what happened?" is a poll, and the poll lives on the progress route.
   *
   * On failure the decision is finer, and it is the demotion of the old "outcome unknown" copy from a
   * first answer to a floor. `serverEffect === "unknown"` means the request went out and this client
   * cannot say whether the server took it — a timeout, a reset connection, a 502. The old flow stopped
   * here and told the operator so. But this screen *holds the batch id*, and `GET /sis/import/{id}` is
   * `retryable: "safe"`: the outcome is not unknown, it is merely unread. So it goes and reads it, and
   * the unknown-outcome sentence is reached only if polling itself fails repeatedly.
   *
   * A failure the server **decided** — a 400 for a missing term, a 409 for a term mismatch or a batch
   * that has already run — stays put. `serverEffect` is `"none"`, so nothing started, and navigating to
   * watch a batch that is not running would replace a precise refusal with a blank progress panel.
   */
  const submitRun = (batchId: string, term: string) => {
    setCheckNote(undefined);
    // The check's own failure state is cleared too, and not only its note: without this, "The batch
    // could not be read" from a previous press survives beside the next run's outcome, describing a
    // request that is two attempts old.
    check.reset();
    void run.run(batchId, term).then((settled) => {
      // `ignored` is a second press dropped while the first was in flight. Nothing was sent, so
      // nothing has changed and the screen must not move.
      if (settled.outcome === "ignored") return;
      if (settled.outcome === "failed" && advise(settled.error).serverEffect === "none") return;
      navigate(importProgressPath(batchId), { state: { termId: term } });
    });
  };

  /**
   * Reads the batch back, and sends the operator to the progress route unless the answer is that
   * nothing started.
   *
   * `Pending` is the one status that belongs *here*: it is proof the run never reached the server, so
   * nothing was written and the fix is the Run button on this screen. Every other answer — `Running`,
   * or any terminal status — is a batch with a life of its own, and the screen built to show that is
   * the one at `/students/import/{id}`.
   */
  const submitCheck = (batchId: string, term: string) => {
    void check.run(batchId).then((settled) => {
      if (settled.outcome !== "succeeded") return;
      const batch = settled.data;
      if (batch === undefined) {
        setCheckNote(
          "The API has no batch with this id — it was removed since it was staged. The preview above " +
            "is of a batch that no longer exists, so start again from the file.",
        );
        return;
      }
      if (batch.status === SIS_IMPORT_STATUS.Pending) {
        setCheckNote(BATCH_IS_STAGED_NOT_RUN);
        // `Pending` is proof the run did not happen, which is precisely the fact the withheld Run
        // button lacked. Clearing the failure re-enables it — the doubt the alert existed to express
        // has been answered, and answering it is what this button is for.
        run.reset();
        return;
      }
      setCheckNote(undefined);
      run.reset();
      navigate(importProgressPath(batchId), { state: { termId: term } });
    });
  };

  const startOver = () => {
    upload.reset();
    run.reset();
    check.reset();
    setCheckNote(undefined);
    setFile(undefined);
    setFileProblem(undefined);
    setStage({ name: "choose" });
  };

  return (
    <Box>
      <Breadcrumbs sx={{ mb: 1 }}>
        <MuiLink component={RouterLink} to="/students" underline="hover" color="inherit">
          Students
        </MuiLink>
        <Typography color="text.primary">Import roster</Typography>
      </Breadcrumbs>

      {/* The focus destination for every step change, and the page's only `h1`-level heading. */}
      <Typography
        ref={heading}
        tabIndex={-1}
        id={HEADING_ID}
        variant="h5"
        component="h1"
        fontWeight={700}
        sx={{ mb: 2, outline: "none" }}
      >
        Import roster — {STEPS[stepOf(stage)]}
      </Typography>

      {/* `alternativeLabel` keeps the three names readable side by side. The active step is carried by
          text as well as by colour: the heading above names it, so nothing here is colour-only. */}
      <Stepper activeStep={stepOf(stage)} alternativeLabel sx={{ mb: 3 }}>
        {STEPS.map((label) => (
          <Step key={label}>
            <StepLabel>{label}</StepLabel>
          </Step>
        ))}
      </Stepper>

      {stage.name === "choose" && (
        <ChooseStep
          terms={terms}
          termId={termId}
          onTermChange={setTermId}
          file={file}
          fileProblem={fileProblem}
          onFileChange={chooseFile}
          onUpload={submitUpload}
          running={upload.status === "running"}
          failure={upload.status === "failed" ? { error: upload.error } : undefined}
        />
      )}

      {stage.name === "preview" && (
        <PreviewStep
          preview={stage.preview}
          termId={stage.termId}
          onRun={() => submitRun(stage.preview.batch.id, stage.termId)}
          onBack={startOver}
          running={run.status === "running"}
          failure={run.status === "failed" ? { error: run.error } : undefined}
          onCheck={() => submitCheck(stage.preview.batch.id, stage.termId)}
          checking={check.status === "running"}
          checkError={check.status === "failed" ? check.error : undefined}
          checkNote={checkNote}
        />
      )}
    </Box>
  );
}

// ---------------------------------------------------------------------------------------------
// Step 1 — the term and the file
// ---------------------------------------------------------------------------------------------

/**
 * Exactly what `useApiResource` hands back for this read, rather than a hand-written subset of it: a
 * looser prop type would keep compiling while the hook's shape moved underneath it, which is the drift
 * this codebase narrows at the seam specifically to avoid.
 */
type TermsResource = ReturnType<typeof useApiResource<Term[]>>;

function ChooseStep({
  terms,
  termId,
  onTermChange,
  file,
  fileProblem,
  onFileChange,
  onUpload,
  running,
  failure,
}: {
  terms: TermsResource;
  termId: string;
  onTermChange: (id: string) => void;
  file: File | undefined;
  fileProblem: string | undefined;
  onFileChange: (file: File | undefined) => void;
  onUpload: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}) {
  const rows = terms.data ?? [];
  const refusal = failure === undefined ? undefined : uploadRefusalOf(failure.error);
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);
  const ready = termId !== "" && file !== undefined && fileProblem === undefined;

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Alert severity="info" sx={{ mb: 3 }}>
        {UPLOAD_WRITES_NOTHING}
      </Alert>

      {/* The 400/422 split, rendered instead of the generic write alert and not beside it. `advise()`
          cannot draw it — to that taxonomy both are ordinary 4xx refusals whose advice is "send it
          again if the reason may have cleared", and for a 422 the reason provably cannot clear while
          the file is the same one. Anything outside those two falls through to `WriteFailureAlert`,
          which is built for failures that are not statements about the workbook. */}
      {refusal !== undefined && (
        <Alert severity="error" role="alert" sx={{ mb: 3 }}>
          <AlertTitle>{refusal.heading}</AlertTitle>
          <Typography variant="body2">{describeApiError(failure?.error)}</Typography>
          <Typography variant="body2" sx={{ mt: 1 }}>
            {refusal.whatToDo}
          </Typography>
        </Alert>
      )}

      {failure !== undefined && refusal === undefined && (
        <WriteFailureAlert
          error={failure.error}
          notApplied={HEADING_NOT_UPLOADED}
          mayHaveApplied={HEADING_MAYBE_UPLOADED}
          resendWithheld={UPLOAD_RESEND_WITHHELD}
        />
      )}

      {terms.status === "loading" && <LoadingState label="Loading terms…" />}

      {terms.status === "error" && (
        <ErrorState subject="the terms" error={terms.error} onRetry={terms.reload} />
      )}

      {terms.status === "ready" && rows.length === 0 && <EmptyState message={NO_TERMS} />}

      {terms.status === "ready" && rows.length > 0 && (
        <>
          <TextField
            id={TERM_FIELD_ID}
            select
            required
            label="Term this roster is for"
            value={termId}
            onChange={(e) => onTermChange(e.target.value)}
            helperText={TERM_IS_NOT_INFERRED}
            disabled={running}
            // No `autoFocus`: the page heading takes focus on every step change, including the first
            // paint, and two things competing for it means whichever effect runs last wins — a race
            // that reads as a cursor jumping. The heading is the correct destination here, because the
            // step name is what changed.
            fullWidth
            sx={{ mb: 3 }}
          >
            {rows.map((term) => (
              <MenuItem key={term.id} value={term.id}>
                {term.code}
                {term.isCurrent ? " (current)" : ""}
              </MenuItem>
            ))}
          </TextField>

          {/* A real `<input type="file">` with a real `<label>`, not a styled drop zone. The native
              control is in the tab order, opens on Enter and Space, announces its own "no file
              selected" state and is the only thing a screen reader will describe as a file picker.
              MUI has no file field, and a `<Button component="label">` hides the input — which works
              until it does not, and costs the announcement for nothing this screen needs. */}
          <Typography
            component="label"
            htmlFor={FILE_FIELD_ID}
            variant="subtitle2"
            sx={{ display: "block", mb: 1 }}
          >
            Roster workbook ({ROSTER_FILE_EXTENSION}) *
          </Typography>
          <input
            id={FILE_FIELD_ID}
            type="file"
            accept={ROSTER_FILE_EXTENSION}
            aria-describedby={FILE_HELP_ID}
            disabled={running}
            onChange={(e) => onFileChange(e.target.files?.[0])}
            style={{ display: "block", marginBottom: 8 }}
          />
          <Typography id={FILE_HELP_ID} variant="body2" color="text.secondary">
            {ROSTER_FILE_EXTENSION} only — there is no CSV format for this import. The registrar’s
            export is around 50 KB.
          </Typography>

          {/* Mounted whether or not there is anything to say, so the region exists in the DOM before
              content is put into it — one inserted and populated in the same commit is announced
              unreliably. */}
          <Box role="status" sx={{ minHeight: 24, mt: 2 }}>
            {file !== undefined && fileProblem === undefined && (
              <Typography variant="body2" color="text.secondary">
                {file.name} — {describeSize(file.size)}
              </Typography>
            )}
          </Box>

          {fileProblem !== undefined && (
            <Alert severity="warning" role="alert" sx={{ mt: 1 }}>
              <AlertTitle>That file cannot be uploaded</AlertTitle>
              <Typography variant="body2">{fileProblem}</Typography>
            </Alert>
          )}

          <Stack direction="row" spacing={2} sx={{ mt: 3 }}>
            <Button
              variant="contained"
              onClick={onUpload}
              disabled={!ready || running || resendUnsafe}
              startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
            >
              {running ? "Uploading…" : "Upload and preview"}
            </Button>
            <Button component={RouterLink} to="/students">
              Cancel
            </Button>
          </Stack>

          {/* The button above can be disabled for three different reasons and a greyed control that
              says nothing sends the user hunting. The file problem has its own alert; these two do
              not. */}
          {!ready && fileProblem === undefined && (
            <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
              {termId === ""
                ? "Choose the term this roster belongs to."
                : "Choose the roster workbook."}
            </Typography>
          )}
        </>
      )}
    </Paper>
  );
}

// ---------------------------------------------------------------------------------------------
// Step 2 — what is in the file
// ---------------------------------------------------------------------------------------------

/**
 * The counts an operator compares against what the registrar said they sent.
 *
 * This is the whole reason the flow has a middle step: a file with one course in it, or with three
 * hundred students where the roster has fifty-two, is a wrong file — and here it is visible *before*
 * anything is written rather than afterwards as a batch to unpick by hand.
 */
function PreviewStep({
  preview,
  termId,
  onRun,
  onBack,
  running,
  failure,
  onCheck,
  checking,
  checkError,
  checkNote,
}: {
  preview: SisImportPreview;
  termId: string;
  onRun: () => void;
  onBack: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
  onCheck: () => void;
  checking: boolean;
  checkError: unknown;
  checkNote: string | undefined;
}) {
  const { batch } = preview;
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);
  // The term the operator chose, confirmed against what the server filed the batch under. They agree
  // in every ordinary case; when they do not, the run is about to be refused with a 409 and saying so
  // here is more use than the refusal is.
  //
  // Compared case-insensitively: both sides are serialized GUIDs and .NET writes them lower-case, so
  // they agree today — but this comparison *blocks* the run, and a build that refused a perfectly good
  // batch over a casing change would be a worse failure than the one it guards against.
  const termMismatch = batch.termId.toLowerCase() !== termId.toLowerCase();

  const counts: { label: string; value: number }[] = [
    { label: "Students", value: preview.distinctStudents },
    { label: "Colleges", value: preview.distinctColleges },
    { label: "Programs", value: preview.distinctPrograms },
    { label: "Courses", value: preview.distinctCourses },
    { label: "Sections", value: preview.distinctSections },
    { label: "Instructors", value: preview.distinctInstructors },
  ];

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Alert severity="warning" sx={{ mb: 3 }}>
        <AlertTitle>Nothing has been written yet</AlertTitle>
        <Typography variant="body2">
          {batch.fileName ?? "The workbook"} was staged as batch {batch.id.slice(0, 8)} for term{" "}
          <strong>{batch.termCode}</strong>. Running it is what writes to the roster.
        </Typography>
      </Alert>

      {termMismatch && (
        <Alert severity="error" role="alert" sx={{ mb: 3 }}>
          <AlertTitle>This batch is filed under a different term</AlertTitle>
          <Typography variant="body2">
            The API filed it under {batch.termCode}, which is not the term this page sent. The run
            confirms the term and would be refused. Press “Choose a different file” below to start
            again, and pick the term deliberately on the way back through.
          </Typography>
        </Alert>
      )}

      {failure !== undefined && (
        <WriteFailureAlert
          error={failure.error}
          notApplied={HEADING_NOT_RUN}
          mayHaveApplied={HEADING_MAYBE_RUN}
          resendWithheld={RUN_RESEND_WITHHELD}
        />
      )}

      <Typography variant="h6" component="h2" sx={{ mb: 1 }}>
        What the file holds
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        {batch.totalRows} source {batch.totalRows === 1 ? "row" : "rows"}. Check these against what
        the registrar sent — a file with one course in it is a wrong file, and this is where that
        shows.
      </Typography>

      <Grid container spacing={2} sx={{ mb: 3 }}>
        {counts.map((count) => (
          <Grid key={count.label} size={{ xs: 6, sm: 4, md: 2 }}>
            <Paper variant="outlined" sx={{ p: 1.5 }}>
              <Typography variant="h6" component="p">
                {count.value}
              </Typography>
              <Typography variant="body2" color="text.secondary">
                {count.label}
              </Typography>
            </Paper>
          </Grid>
        ))}
      </Grid>

      {(preview.blankSectionRows > 0 || preview.placeholderInstructorRows > 0) && (
        <Alert severity="info" sx={{ mb: 3 }}>
          <AlertTitle>Gaps in the export</AlertTitle>
          <Typography variant="body2">
            {preview.blankSectionRows > 0 &&
              `${preview.blankSectionRows} ${
                preview.blankSectionRows === 1 ? "row has" : "rows have"
              } no section. `}
            {preview.placeholderInstructorRows > 0 &&
              `${preview.placeholderInstructorRows} ${
                preview.placeholderInstructorRows === 1 ? "row names" : "rows name"
              } a placeholder instructor. `}
            These import; they are counted here so they are a stated fact rather than a surprise in
            the warning column afterwards.
          </Typography>
        </Alert>
      )}

      <Typography variant="h6" component="h2" sx={{ mb: 1 }}>
        Columns read from the sheet
      </Typography>
      {preview.columns.length === 0 ? (
        <Alert severity="warning" sx={{ mb: 3 }}>
          No columns were read from this workbook. That is almost certainly the wrong sheet —
          running it would stage nothing useful.
        </Alert>
      ) : (
        <Stack direction="row" spacing={1} useFlexGap flexWrap="wrap" sx={{ mb: 3 }}>
          {preview.columns.map((column) => (
            <Chip key={column} label={column} size="small" variant="outlined" />
          ))}
        </Stack>
      )}

      <Divider sx={{ my: 3 }} />

      <Alert severity="success" icon={false} sx={{ mb: 3 }}>
        {RUN_IS_IDEMPOTENT}
      </Alert>

      <Stack direction="row" spacing={2} alignItems="center">
        <Button
          variant="contained"
          onClick={onRun}
          disabled={running || resendUnsafe || termMismatch}
          startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
        >
          {running ? "Running…" : `Run import (${batch.totalRows} rows)`}
        </Button>
        <Button onClick={onBack} disabled={running}>
          Choose a different file
        </Button>
        {/* Offered only once a run has failed, and it is the way out of a withheld Run: a `GET`
            changes nothing, so it can be pressed at the exact moment pressing Run again cannot be. */}
        {failure !== undefined && (
          <Button
            onClick={onCheck}
            disabled={checking}
            startIcon={checking ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {checking ? "Checking…" : "Check this batch"}
          </Button>
        )}
      </Stack>

      <Box role="status" sx={{ minHeight: 24, mt: 2 }}>
        {checkNote !== undefined && (
          <Typography variant="body2" color="text.secondary">
            {checkNote}
          </Typography>
        )}
      </Box>

      {/* Through `advise()` like every other failure in this slice, and not `describeApiError` alone.
          The check is a `GET`, so the taxonomy's answer is `retryable: "safe"` — pressing it again is
          free — and that is the sentence the operator needs beside the server's own. Rendering the
          server's sentence without it leaves them holding a failure with no stated next move at the
          one moment the whole point of the button was to give them one. */}
      {checkError !== undefined && (
        <Alert severity="error" role="alert" sx={{ mt: 1 }}>
          <AlertTitle>The batch could not be read</AlertTitle>
          <Typography variant="body2">{describeApiError(checkError)}</Typography>
          <Typography variant="body2" sx={{ mt: 1 }}>
            {advise(checkError).message}
          </Typography>
        </Alert>
      )}
    </Paper>
  );
}
