// The two judgements the roster-import screen has to make that are not "render what the server said",
// kept out of the component so they can be read — and tested — as rules rather than as JSX.
//
// All three are about telling apart states that look alike and are not:
//
//   1. **Before the request** — a file this build can already tell will be refused. Sending a 40 MB
//      video to find out it is not a workbook costs a minute of upload on a school network and returns
//      a 413 or a 422, neither of which says "you picked the wrong file".
//   2. **After the request** — a `400` and a `422` from `POST /sis/import/upload`, which the controller
//      draws deliberately and which send an operator to two different places. Collapsing them into
//      "upload failed" throws away the only part of the answer that says what to do next.
//   3. **After a batch is read back** — whether the status the API returned is a *finished* one. A
//      staged batch and a completed one are both "the API answered", and only one of them may be shown
//      on a screen whose heading is "The import finished".

import { ApiError } from "./api";
import { SIS_IMPORT_STATUS } from "./types";

// ---------------------------------------------------------------------------------------------
// What may be uploaded
// ---------------------------------------------------------------------------------------------

/**
 * `SisImportController.MaxUploadBytes`, mirrored. The real roster is about 51 KB, so this is a
 * decade of growth rather than a limit anyone will meet by accident — which is exactly why a file that
 * *does* meet it is almost certainly the wrong file, and worth saying so about.
 *
 * A mirror of a server constant is a coupling, and it is the cheaper of the two options on offer: the
 * alternative is uploading megabytes to learn the answer, and the server still enforces it — this only
 * decides whether the operator finds out in a second or in a minute.
 */
export const MAX_UPLOAD_BYTES = 10 * 1024 * 1024;

/**
 * **XLSX only, and there is no CSV branch — not here and not planned.**
 *
 * The registrar supplies `.xlsx`; the parser accepts nothing else and answers a `.csv` with a 422,
 * which is correct rather than a gap. This is the value the file input's `accept` is set from and the
 * value the refusal below names, so the screen cannot say one thing and filter for another.
 */
export const ROSTER_FILE_EXTENSION = ".xlsx";

/** Bytes as an operator reads them. One decimal place, which is enough to tell 9.8 MB from 11 MB. */
export function describeSize(bytes: number): string {
  const kb = bytes / 1024;
  if (kb < 1024) return `${kb.toFixed(1)} KB`;
  return `${(kb / 1024).toFixed(1)} MB`;
}

/**
 * Why this file cannot be sent, or `undefined` if it can — checked here rather than left to the
 * server, for the reasons in the module header.
 *
 * It is **not** a claim that a file passing these three checks is a valid roster. Extension is not
 * content: a renamed `.csv` passes here and earns its 422 from the parser, which is the layer that can
 * actually tell. These are the refusals that need no bytes on the wire to make.
 */
export function rosterFileProblem(file: File): string | undefined {
  if (!file.name.toLowerCase().endsWith(ROSTER_FILE_EXTENSION)) {
    return (
      `“${file.name}” is not an ${ROSTER_FILE_EXTENSION} workbook. The roster import reads ` +
      `${ROSTER_FILE_EXTENSION} only — there is no CSV format for it — so export the sheet from Excel ` +
      "and choose that file."
    );
  }
  if (file.size === 0) {
    return `“${file.name}” is empty (0 bytes). The upload would be refused as though no file was sent.`;
  }
  if (file.size > MAX_UPLOAD_BYTES) {
    return (
      `“${file.name}” is ${describeSize(file.size)}, over the ${describeSize(MAX_UPLOAD_BYTES)} ` +
      "upload limit. The real roster is around 50 KB, so a file this large is very unlikely to be one " +
      "— check that it is the registrar's export."
    );
  }
  return undefined;
}

// ---------------------------------------------------------------------------------------------
// Which of the two refusals this is
// ---------------------------------------------------------------------------------------------

const HTTP_BAD_REQUEST = 400;
const HTTP_CONTENT_TOO_LARGE = 413;
const HTTP_UNPROCESSABLE_CONTENT = 422;

/**
 * What a refused upload means and where it sends the operator.
 *
 * A heading and an instruction rather than a code, because the code is what the *screen* must not make
 * the reader translate. `describeApiError` still carries the server's own sentence beside it — the
 * pipeline names the sheet and the column it could not read, and that is more specific than anything
 * this file can say.
 */
export interface UploadRefusal {
  heading: string;
  whatToDo: string;
}

/**
 * The 400/422 split `SisImportController.Upload` draws, as the two sentences it means.
 *
 * **400 — the request was malformed.** No file part, or no term. Nothing about the workbook was
 * examined, so the file is not what is being reported on. In this build the term picker is required
 * and the file part is named by `api.uploadRoster`, so a 400 arriving here is a version disagreement
 * rather than a mistake the operator made — and the sentence says so instead of sending them to look
 * at a file that is probably fine.
 *
 * **422 — the multipart was fine and the content could not be read.** This is the file. Re-sending the
 * identical bytes gets the identical answer, which is precisely what separates it from the 400, so the
 * instruction is to change the file rather than to try again.
 *
 * **413 — the file is over the server's `RequestSizeLimit`.** `rosterFileProblem` refuses these before
 * any bytes go out, so one arriving here means the two limits disagree: a build of this SPA against a
 * server configured lower than `MAX_UPLOAD_BYTES`. It belongs in this function for exactly the reason
 * the 422 does — `advise()` reads it as an ordinary 4xx and says "send it again if the reason may have
 * cleared", and the reason provably cannot clear while the file is the same one.
 *
 * `undefined` for anything else — a network failure, a 500, an off-contract reply. Those are not
 * statements about the workbook and must not be dressed as one; the caller falls back to
 * `WriteFailureAlert`, which is built for exactly that class.
 */
export function uploadRefusalOf(error: unknown): UploadRefusal | undefined {
  if (!(error instanceof ApiError) || error.kind !== "http") return undefined;

  if (error.status === HTTP_BAD_REQUEST) {
    return {
      heading: "The upload request was incomplete",
      whatToDo:
        "The API says the request was missing its file or its term — not that anything is wrong with " +
        "the workbook. Choose the term and the file again; if it repeats, this admin build and the " +
        "API are probably different versions and it needs reporting rather than retrying.",
    };
  }

  if (error.status === HTTP_CONTENT_TOO_LARGE) {
    return {
      heading: "That file is larger than the API accepts",
      whatToDo:
        `The API refused it on size before reading any of it. This screen allows up to ` +
        `${describeSize(MAX_UPLOAD_BYTES)}, so a refusal here means the API is configured lower than ` +
        "this build expects — worth reporting. Sending the same file again will give the same answer; " +
        "nothing was written to the roster.",
    };
  }

  if (error.status === HTTP_UNPROCESSABLE_CONTENT) {
    return {
      heading: "That file could not be read as a roster",
      whatToDo:
        `The upload itself was fine — it is the contents. The import reads ${ROSTER_FILE_EXTENSION} ` +
        "workbooks in the registrar's export layout, so a CSV, a different report, or a sheet with " +
        "renamed columns lands here. Sending the same file again will give the same answer; nothing " +
        "was written to the roster.",
    };
  }

  return undefined;
}

// ---------------------------------------------------------------------------------------------
// Whether the run is over
// ---------------------------------------------------------------------------------------------

/**
 * Whether the batch's own status says the run is over.
 *
 * Expressed as "not one of the two live states" rather than as a list of the four finished ones, and
 * the two are not equivalent under drift: a status this build has never heard of would be filed as
 * *unfinished* by a positive list and left on a step with no way forward. `SIS_IMPORT_STATUS` is
 * verified against `SisImportStatus.All`, so the complement is exactly `Completed`,
 * `CompletedWithWarnings`, `CompletedWithErrors` and `Failed` — and the results step prints the status
 * word verbatim, so an unknown one arrives on screen as itself rather than as a wrong summary.
 *
 * The results step's counters, its "the import finished" heading and its `countersReconcile` alert are
 * all statements about a terminal batch. Advancing a `Pending` one there renders a finished-import
 * screen over five zero counters and a reconcile alert telling the operator to report a bug — at the
 * one moment the correct action is simply to run it.
 */
export const isTerminalStatus = (status: string): boolean =>
  status !== SIS_IMPORT_STATUS.Pending && status !== SIS_IMPORT_STATUS.Running;

// ---------------------------------------------------------------------------------------------
// Standing copy
// ---------------------------------------------------------------------------------------------

/**
 * Said on the run step, and it is not reassurance — it is the fact that decides what an operator does
 * after a partial failure.
 *
 * Running the same roster twice leaves the database identical and reports every row `Skipped`. An
 * operator who does not know that avoids re-running, which is exactly the wrong move at the one moment
 * re-running is the fix.
 */
export const RUN_IS_IDEMPOTENT =
  "Running the same roster again is safe: the import compares every row against what is already " +
  "there, so a second run changes nothing and reports every row as Skipped. If a run fails part-way, " +
  "re-running it is the way to finish it.";

/** Said on the upload step, because "nothing happens yet" is the least believable part of the flow. */
export const UPLOAD_WRITES_NOTHING =
  "Uploading only stages the file and reads what is in it. Nothing is written to the roster until you " +
  "press Run import on the next step.";

/** Why the term is a required field rather than something the screen works out. */
export const TERM_IS_NOT_INFERRED =
  "The workbook does not say which term it is for, and it is never guessed from the filename or the " +
  "date: a batch filed under the wrong term looks correct afterwards, because every later query is " +
  "scoped to a term, and only a hand correction can undo it.";
