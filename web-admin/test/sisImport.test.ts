// `src/sisImport.ts` — the three judgements the roster-import screen makes that are not "render what
// the server said", and in two cases the *order* they are made in, which is the whole answer.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and what it does not
// ---------------------------------------------------------------------------------------------
//
// `MAX_UPLOAD_BYTES` is a **hand-written copy** of `SisImportController.MaxUploadBytes`, and
// `sisImport.ts` says so above the constant. As in `deviceDraft.test.ts` and `studentDraft.test.ts`,
// **these tests pin the client's copy and cannot detect server drift** — nothing in this repository
// compares the two, and a frontend unit test is structurally incapable of it. `the ceiling against the
// server` below is the same tripwire those files use: the number read out of the controller by hand on
// 2026-08-01, asserted here so that a change made on the *client* names the file to go and re-read.
//
// What these tests exist for, in descending order of what they would cost to lose:
//
//   1. **`isTerminalStatus` treating an unknown status as finished.** The predicate is deliberately the
//      complement of the two live states rather than a list of the four finished ones. A "tidy this
//      into a list" refactor is behaviour-preserving for every status this build has heard of and
//      silently wrong for every one it has not — so the unknown-status case is pinned harder than the
//      six known ones, because it is the only one that would survive the refactor green.
//   2. **`rosterFileProblem` checking the extension before the size.** True today only by line order.
//      A reorder leaves every single-fault test green and degrades the one sentence that matters: a
//      40 MB `.mp4` would be reported as too large, sending the operator to find a smaller video.
//   3. **The 400/422 split in `uploadRefusalOf` saying two different things.** Non-undefined is not the
//      assertion — a 422 provably cannot clear while the file is unchanged, and a 400 is the one where
//      trying again is reasonable. Both refusals are checked for the sentence that separates them.
//
// No time zone is involved anywhere in this module, so nothing here depends on the suite's baseline
// zone.

import { describe, expect, it } from "vitest";

import { ApiError } from "../src/api";
import {
  MAX_UPLOAD_BYTES,
  ROSTER_FILE_EXTENSION,
  describeSize,
  isTerminalStatus,
  rosterFileProblem,
  uploadRefusalOf,
} from "../src/sisImport";
import { SIS_IMPORT_STATUS } from "../src/types";
import type { SisImportStatusName } from "../src/types";

// ---------------------------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------------------------

const KB = 1024;
const MB = 1024 * KB;

/**
 * Above this, a fixture's bytes are declared rather than allocated.
 *
 * `rosterFileProblem` reads `name` and `size` and never the contents, so a 40 MB `Uint8Array` would
 * buy no fidelity and cost 40 MB per case. Below the ceiling the bytes are real, so the ordinary
 * fixtures are ordinary `File`s; above it the size is defined on the instance and **asserted back**,
 * so a helper that silently stopped taking effect fails here rather than turning every size test into
 * a test of a 4 KB file.
 */
const REAL_BYTES_CEILING = 4 * KB;

function aFile(name: string, size: number): File {
  const allocated = Math.min(size, REAL_BYTES_CEILING);
  const file = new File([new Uint8Array(allocated)], name);
  if (size !== allocated) {
    Object.defineProperty(file, "size", { value: size, configurable: true });
  }
  if (file.size !== size) {
    throw new Error(`Fixture is ${file.size} bytes; the test asked for ${size}.`);
  }
  return file;
}

/** A plausible registrar export. ~51 KB is the real roster's size, per the module's own comment. */
const aRoster = (name = "roster.xlsx", size = 51 * KB) => aFile(name, size);

/** Fails loudly rather than returning a half-answer, so a mis-set-up test cannot read as a pass. */
function problemOf(file: File): string {
  const problem = rosterFileProblem(file);
  if (problem === undefined) {
    throw new Error(`Expected “${file.name}” (${file.size} bytes) to be refused; it was accepted.`);
  }
  return problem;
}

const httpError = (status: number) =>
  new ApiError("http", status, `upload failed (${status}).`, { shape: "write" });

// ---------------------------------------------------------------------------------------------
// The ceiling against the server
// ---------------------------------------------------------------------------------------------

describe("the upload ceiling", () => {
  // A tripwire and NOT a contract test — see this file's header. `MaxUploadBytes` in
  // `backend/EAMS.Api/Controllers/SisImportController.cs` read `10 * 1024 * 1024` on 2026-08-01, and it
  // is applied to the endpoint by `[RequestSizeLimit(MaxUploadBytes)]`. Nothing here can see that
  // number change; what it can see is *this* build's copy changing, which is the moment to go and
  // re-read the controller.
  const SERVER_MAX_UPLOAD_BYTES = 10 * 1024 * 1024;

  it("copies SisImportController.MaxUploadBytes", () => {
    expect(MAX_UPLOAD_BYTES).toBe(SERVER_MAX_UPLOAD_BYTES);
  });
});

// ---------------------------------------------------------------------------------------------
// rosterFileProblem — the three refusals that need no bytes on the wire
// ---------------------------------------------------------------------------------------------

describe("a file that may be sent", () => {
  it("accepts an ordinary roster", () => {
    expect(rosterFileProblem(aRoster())).toBeUndefined();
  });

  it("accepts one byte", () => {
    // The lower boundary of the emptiness check. A one-byte file is not a workbook and will earn its
    // 422 from the parser — which is the layer that can actually tell, and the reason this function's
    // own comment refuses to claim more than it can prove.
    expect(rosterFileProblem(aRoster("roster.xlsx", 1))).toBeUndefined();
  });

  it("accepts a file of exactly the limit", () => {
    // `RequestSizeLimit` refuses what is *over* the limit, so a file of exactly it is one the server
    // accepts. Refusing it here would be this build inventing a stricter rule than the API's and
    // telling the operator the API would have said no.
    expect(rosterFileProblem(aRoster("roster.xlsx", MAX_UPLOAD_BYTES))).toBeUndefined();
  });

  it.each([".xlsx", ".XLSX", ".XlSx"])("accepts %s, whatever case it is written in", (extension) => {
    // Windows Explorer, Excel's own Save As and a macOS export disagree about the case, and none of the
    // three is the operator's doing.
    expect(rosterFileProblem(aRoster(`roster${extension}`))).toBeUndefined();
  });

  it("accepts a name with a dot in it", () => {
    expect(rosterFileProblem(aRoster("CICSS roster 2026-08-01 v2.final.xlsx"))).toBeUndefined();
  });
});

describe("a file with the wrong extension", () => {
  const wrongExtension = [
    // The one that is not a mistake so much as an assumption: every other import tool in the world
    // takes CSV, and this one does not — deliberately, and there is no branch planned.
    "roster.csv",
    "roster.xls",
    "roster.xlsm",
    "roster.pdf",
    "roster.xlsx.txt",
    // No extension at all, and the one that would pass a naive `includes` rather than `endsWith`.
    "roster",
    "roster.xlsx.zip",
  ];

  it.each(wrongExtension)("refuses %s", (name) => {
    expect(problemOf(aRoster(name))).toBeDefined();
  });

  it("says it is not a workbook, and names the file", () => {
    const problem = problemOf(aRoster("roster.csv"));
    expect(problem).toContain("roster.csv");
    expect(problem).toContain(ROSTER_FILE_EXTENSION);
  });

  it("tells a CSV that there is no CSV format rather than leaving it to be tried again", () => {
    // The refusal that has to say more than "wrong extension". An operator who reads only that renames
    // the file to `.xlsx`, uploads it, and gets a 422 from the parser — a second round trip to learn
    // the thing this sentence could have told them for free.
    expect(problemOf(aRoster("roster.csv"))).toContain("no CSV format");
  });
});

describe("an empty file", () => {
  it("refuses a zero-byte workbook", () => {
    expect(problemOf(aRoster("roster.xlsx", 0))).toBeDefined();
  });

  it("says it is empty rather than that it is unreadable", () => {
    // A zero-byte file is a failed export or a file still being written by Excel, not a bad roster —
    // and "0 bytes" is the fact that makes that recognisable.
    const problem = problemOf(aRoster("roster.xlsx", 0));
    expect(problem).toContain("empty");
    expect(problem).toContain("0 bytes");
  });
});

describe("a file over the limit", () => {
  it("refuses one byte over", () => {
    // The upper boundary, one byte from the accepted case above. Together they pin the comparison as
    // `>` rather than `>=`, which is the direction `RequestSizeLimit` uses.
    expect(problemOf(aRoster("roster.xlsx", MAX_UPLOAD_BYTES + 1))).toBeDefined();
  });

  it("says how big it is and what the limit is", () => {
    const problem = problemOf(aRoster("roster.xlsx", 40 * MB));
    expect(problem).toContain(describeSize(40 * MB));
    expect(problem).toContain(describeSize(MAX_UPLOAD_BYTES));
  });

  it("says the real roster is around 50 KB, which is what makes the number mean something", () => {
    // "Over the limit" invites finding a way to send it. The comparison against the real roster's size
    // is what says the file is probably the wrong one, which is the far more likely truth.
    expect(problemOf(aRoster("roster.xlsx", 40 * MB))).toContain("50 KB");
  });
});

// ---------------------------------------------------------------------------------------------
// THE ORDER — which true thing is reported when more than one is
// ---------------------------------------------------------------------------------------------
//
// True today only because of the order of the `if`s, and nothing else in the module states it. Every
// test above moves one thing at a time and would stay green through a reorder; these are the ones that
// would not.

describe("the order the refusals are asked in", () => {
  it("reports a 40 MB .mp4 as not a workbook, not as too large", () => {
    // The load-bearing one. Both rules are broken, and only one of the two sentences is useful: "too
    // large" sends the operator to compress a video or to find a smaller one, and they can succeed at
    // that and still have no roster. "Not an .xlsx workbook" sends them to Excel, which is where the
    // file they actually want is.
    const video = aFile("assembly-recording.mp4", 40 * MB);
    expect(video.size).toBeGreaterThan(MAX_UPLOAD_BYTES);
    expect(video.name.toLowerCase().endsWith(ROSTER_FILE_EXTENSION)).toBe(false);

    const problem = problemOf(video);
    expect(problem).toContain("not an");
    expect(problem).not.toContain("upload limit");
  });

  it("reports an empty .csv as not a workbook, not as empty", () => {
    // The other pair, for the same reason: renaming a zero-byte CSV to `.xlsx` fixes nothing, and
    // "empty" is the instruction to go and re-export the same wrong format.
    const empty = aFile("roster.csv", 0);
    expect(empty.size).toBe(0);

    const problem = problemOf(empty);
    expect(problem).toContain("not an");
    expect(problem).not.toContain("empty");
  });

  it("still reports too large for an oversize file that IS a workbook", () => {
    // The negative control for the pair above. Without it, "not a workbook" could be the answer to
    // everything and both tests would pass for the wrong reason.
    const problem = problemOf(aRoster("roster.xlsx", 40 * MB));
    expect(problem).toContain("upload limit");
    expect(problem).not.toContain("not an");
  });

  it("still reports empty for a zero-byte file that IS a workbook", () => {
    const problem = problemOf(aRoster("roster.xlsx", 0));
    expect(problem).toContain("empty");
    expect(problem).not.toContain("not an");
  });
});

// ---------------------------------------------------------------------------------------------
// describeSize
// ---------------------------------------------------------------------------------------------

describe("describeSize", () => {
  it.each([
    [0, "0.0 KB"],
    [512, "0.5 KB"],
    [KB, "1.0 KB"],
    [51 * KB, "51.0 KB"],
    // The transition itself: 1023.5 KB rounds up in the KB branch, and only 1024 KB exactly leaves it.
    [MB - KB, "1023.0 KB"],
    [MB, "1.0 MB"],
    [MAX_UPLOAD_BYTES, "10.0 MB"],
    [40 * MB, "40.0 MB"],
  ])("describes %i bytes as %s", (bytes, expected) => {
    expect(describeSize(bytes)).toBe(expected);
  });

  it("keeps one decimal place at every magnitude", () => {
    // The whole point of the format: 9.8 MB and 11 MB have to be distinguishable either side of a
    // 10 MB limit, which "10 MB" for both would not be.
    for (const bytes of [0, 1, KB, MB, MAX_UPLOAD_BYTES]) {
      expect(describeSize(bytes)).toMatch(/^\d+\.\d (KB|MB)$/);
    }
  });

  it("rounds a byte under a megabyte to 1024.0 KB rather than to 1.0 MB", () => {
    // Not a defect and pinned as a *description* rather than as a requirement: the branch is taken on
    // the unrounded value and the display is rounded after, so there is a one-byte-wide window that
    // reads as "1024.0 KB". Recorded here so a future change to the format is a deliberate one and not
    // a surprise — no operator-facing string in this module is near the window.
    expect(describeSize(MB - 1)).toBe("1024.0 KB");
  });

  it("describes a non-empty file of a few bytes as 0.0 KB", () => {
    // The same class of artefact at the other end, and the reason `rosterFileProblem` checks
    // `size === 0` rather than reading this string: a 5-byte file is not empty and this would say it
    // was. Pinned so that a reader of the emptiness check knows why it is written on the number.
    expect(describeSize(5)).toBe("0.0 KB");
    expect(rosterFileProblem(aRoster("roster.xlsx", 5))).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------------------------
// uploadRefusalOf — the four arms
// ---------------------------------------------------------------------------------------------

describe("uploadRefusalOf", () => {
  const HTTP_BAD_REQUEST = 400;
  const HTTP_CONTENT_TOO_LARGE = 413;
  const HTTP_UNPROCESSABLE_CONTENT = 422;

  const REFUSED = [
    ["400", HTTP_BAD_REQUEST],
    ["413", HTTP_CONTENT_TOO_LARGE],
    ["422", HTTP_UNPROCESSABLE_CONTENT],
  ] as const;

  it.each(REFUSED)("answers a refusal for %s", (_label, status) => {
    expect(uploadRefusalOf(httpError(status))).toBeDefined();
  });

  it.each(REFUSED)("gives %s a heading and an instruction, both non-empty", (_label, status) => {
    const refusal = uploadRefusalOf(httpError(status));
    expect(refusal?.heading.trim().length).toBeGreaterThan(0);
    expect(refusal?.whatToDo.trim().length).toBeGreaterThan(0);
  });

  it("gives each status a different heading AND a different instruction", () => {
    // The assertion this function exists for. Three arms that all return *something* is the collapse
    // into "upload failed" the module header refuses — each of the three sends the operator somewhere
    // else, and identical prose would mean they do not.
    const headings = REFUSED.map(([, status]) => uploadRefusalOf(httpError(status))?.heading);
    const instructions = REFUSED.map(([, status]) => uploadRefusalOf(httpError(status))?.whatToDo);

    expect(new Set(headings).size).toBe(REFUSED.length);
    expect(new Set(instructions).size).toBe(REFUSED.length);
  });

  it("tells a 400 the workbook is not what is being reported on", () => {
    // The 400 is the arm where the file is *not* the subject: the multipart never got as far as the
    // content. Sending the operator to look at a file that is probably fine is the failure mode, and
    // the sentence has to actively say so.
    const refusal = uploadRefusalOf(httpError(HTTP_BAD_REQUEST));
    expect(refusal?.whatToDo).toContain("not that anything is wrong with the workbook");
    expect(refusal?.heading).toContain("incomplete");
  });

  it("tells a 400 that a repeat means a version disagreement rather than a mistake", () => {
    // In this build the term picker is required and `api.uploadRoster` names the file part, so there is
    // no operator action that produces a 400. Saying "report it" rather than "try harder" is the
    // difference between a bug getting filed and someone re-picking the same file five times.
    expect(uploadRefusalOf(httpError(HTTP_BAD_REQUEST))?.whatToDo).toContain("different versions");
  });

  it("tells a 422 the upload was fine and it is the contents", () => {
    const refusal = uploadRefusalOf(httpError(HTTP_UNPROCESSABLE_CONTENT));
    expect(refusal?.whatToDo).toContain("it is the contents");
    expect(refusal?.heading).toContain("could not be read");
  });

  it.each([
    ["413", HTTP_CONTENT_TOO_LARGE],
    ["422", HTTP_UNPROCESSABLE_CONTENT],
  ])("tells a %s that sending the same file again gives the same answer", (_label, status) => {
    // The load-bearing half of the 400/422 split, and the reason this function exists rather than
    // `advise()` being left to answer. `advise()` reads a 4xx as "the server decided, so pressing it
    // again is safe" — which is true and useless: it is safe *and* it provably cannot succeed while the
    // file is the same one. Only this function knows that.
    const refusal = uploadRefusalOf(httpError(status));
    expect(refusal?.whatToDo).toContain("same file again will give the same answer");
  });

  it.each(REFUSED)("tells %s that nothing was written to the roster", (_label, status) => {
    // Upload stages; only Run writes. A refused upload that reads as though it might have written
    // something sends the operator to check a roster that cannot have changed.
    const refusal = uploadRefusalOf(httpError(status));
    if (status === HTTP_BAD_REQUEST) {
      // The 400's own wording says the request never reached the workbook at all, which carries the
      // same fact by a different route — asserted as the sentence it actually uses rather than forced
      // into the other two's phrasing.
      expect(refusal?.whatToDo).toContain("missing its file or its term");
      return;
    }
    expect(refusal?.whatToDo).toContain("nothing was written to the roster");
  });

  it("tells a 413 that the two limits disagree, and names this build's own", () => {
    // A 413 is unreachable through this screen — `rosterFileProblem` refuses oversize files before any
    // bytes go out — so one arriving is a statement about the *deployment*, not about the operator.
    const refusal = uploadRefusalOf(httpError(HTTP_CONTENT_TOO_LARGE));
    expect(refusal?.whatToDo).toContain(describeSize(MAX_UPLOAD_BYTES));
    expect(refusal?.whatToDo).toContain("reporting");
  });
});

describe("what uploadRefusalOf declines to explain", () => {
  const HTTP_NOT_FOUND = 404;
  const HTTP_CONFLICT = 409;
  const HTTP_UNSUPPORTED_MEDIA_TYPE = 415;
  const HTTP_SERVER_ERROR = 500;
  /** `ApiError.status` when the failure happened before or outside a response. */
  const NO_STATUS = 0;

  it.each([
    ["404", HTTP_NOT_FOUND],
    ["409", HTTP_CONFLICT],
    // Adjacent to the three that ARE handled, and deliberately not one of them: the controller does not
    // draw it, so dressing it as a statement about the workbook would be an invention.
    ["415", HTTP_UNSUPPORTED_MEDIA_TYPE],
    ["500", HTTP_SERVER_ERROR],
  ])("says nothing about a %s, so it reaches WriteFailureAlert", (_label, status) => {
    expect(uploadRefusalOf(httpError(status))).toBeUndefined();
  });

  it.each([
    ["network", "network", NO_STATUS],
    // A `malformed` carrying a status the http arm WOULD have answered. This is the case that makes the
    // `kind` guard non-redundant: `writeJson` raises `malformed` with the *response's* status, so a
    // 422-shaped `malformed` exists and it is a statement about the reply body, not about the workbook.
    ["malformed with a 422 on it", "malformed", 422],
  ] as const)("says nothing about a %s failure", (_label, kind, status) => {
    const error = new ApiError(kind, status, "the upload could not be completed.", {
      shape: "write",
    });
    expect(uploadRefusalOf(error)).toBeUndefined();
  });

  it("says nothing about a too-large error, which is this client's own ceiling", () => {
    expect(
      uploadRefusalOf(new ApiError("too-large", NO_STATUS, "too big", { shape: "read" })),
    ).toBeUndefined();
  });

  it.each([
    ["a plain Error", new Error("boom")],
    ["a string", "boom"],
    ["null", null],
    ["undefined", undefined],
    ["an object shaped like one", { kind: "http", status: 422 }],
  ])("says nothing about %s", (_label, thrown) => {
    // The last case is the one worth having: a duck-typed lookalike must not be read as an `ApiError`,
    // because `instanceof` is what the rest of this codebase's error handling branches on too.
    expect(uploadRefusalOf(thrown)).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------------------------
// isTerminalStatus — the predicate a shipped defect was missing
// ---------------------------------------------------------------------------------------------
//
// Lifted out of `pages/StudentsImport.tsx` into `sisImport.ts` to be testable, which is the same shape
// as `eventStatus.ts` / `eventAudience.ts`: a pure rule the page imports rather than holds.
//
// The defect it fixes, for the record: "Check this batch" advanced an un-run `Pending` batch to the
// results step, which rendered "The import finished" in success styling over five zero counters while
// `countersReconcile` — false, because 0 ≠ 536 — fired an alert telling the operator to report a bug.
// The correct action at that moment was simply to press Run.

describe("isTerminalStatus", () => {
  const LIVE: SisImportStatusName[] = [SIS_IMPORT_STATUS.Pending, SIS_IMPORT_STATUS.Running];

  const FINISHED: SisImportStatusName[] = [
    SIS_IMPORT_STATUS.Completed,
    SIS_IMPORT_STATUS.CompletedWithWarnings,
    SIS_IMPORT_STATUS.CompletedWithErrors,
    SIS_IMPORT_STATUS.Failed,
  ];

  it.each(LIVE)("answers false for %s — the run is not over", (status) => {
    expect(isTerminalStatus(status)).toBe(false);
  });

  it.each(FINISHED)("answers true for %s", (status) => {
    expect(isTerminalStatus(status)).toBe(true);
  });

  it("answers false for Pending, which is the case the shipped defect got wrong", () => {
    // Singled out from the table above because it is the specific one that reached a user: a batch
    // staged and never run, shown as a finished import with a bug report attached.
    expect(isTerminalStatus(SIS_IMPORT_STATUS.Pending)).toBe(false);
  });

  it("covers every status the contract names, and splits them 2/4", () => {
    // The premise, asserted rather than assumed. `SIS_IMPORT_STATUS` is verified against
    // `SisImportStatus.All`, so a seventh value arriving in the contract without a decision about which
    // side of this predicate it falls on fails here rather than being filed as terminal by default.
    const named = Object.values(SIS_IMPORT_STATUS);
    expect([...LIVE, ...FINISHED].sort()).toEqual([...named].sort());
    expect(named.filter((status) => isTerminalStatus(status))).toHaveLength(FINISHED.length);
  });
});

// ---------------------------------------------------------------------------------------------
// THE COMPLEMENT — what happens to a status this build has never heard of
// ---------------------------------------------------------------------------------------------

describe("a status this build does not know", () => {
  const UNKNOWN = [
    // A seventh value the server adds. The realistic one.
    "Cancelled",
    "PartiallyCompleted",
    // Case drift on a value that IS known — a serializer setting away, and not the same string.
    "pending",
    "PENDING",
    "Completed ",
    // Degenerate, and reachable: `status` is `string` on the wire and narrowed with `reqStr`, which
    // narrows the type and not the value.
    "",
  ];

  it.each(UNKNOWN)("treats %j as terminal", (status) => {
    // **The assertion a "tidy this into a positive list" refactor would break, and the only one that
    // would.** Every known-status test above passes under both spellings of this predicate. Under a
    // positive list, an unknown status is non-terminal, and a non-terminal status strands the operator
    // on the check step with a note about a state the API never reported and no way forward — while the
    // results step, which prints the status word verbatim, would have shown them what the server
    // actually said.
    expect(isTerminalStatus(status)).toBe(true);
  });

  it("is the complement of the two live states, not a list of the four finished ones", () => {
    // The property, said once as a property rather than only as a table of examples: for any string at
    // all, terminal means "not Pending and not Running".
    const anything = [...Object.values(SIS_IMPORT_STATUS), ...UNKNOWN, "🙂", "null", "undefined"];
    for (const status of anything) {
      expect(isTerminalStatus(status)).toBe(
        status !== SIS_IMPORT_STATUS.Pending && status !== SIS_IMPORT_STATUS.Running,
      );
    }
  });

  it("treats exactly two strings as non-terminal, and they are the two live ones", () => {
    // The negative control for the section: if the predicate were widened, the tests above would still
    // pass — every one of them asserts `true`. This is the one that fails when it is.
    const notTerminal = [...Object.values(SIS_IMPORT_STATUS), ...UNKNOWN].filter(
      (status) => !isTerminalStatus(status),
    );
    expect(notTerminal).toEqual([SIS_IMPORT_STATUS.Pending, SIS_IMPORT_STATUS.Running]);
  });
});
