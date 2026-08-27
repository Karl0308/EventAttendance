/** @vitest-environment happy-dom */

// `/students/import/:batchId` — the roster import while it runs, and then what it did.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// Two things, and they are the two the screen gets wrong if nobody is watching.
//
// **1. Nothing that is only true of a finished batch may be rendered over a running one.** The
// counters are the obvious half; `countersReconcile` is the half that bites. It is `false` for every
// in-flight batch by construction — a run part-way through writing has written some counters and not
// others — so a progress screen that renders that alert ungated tells the operator "the counters do
// not add up, report it rather than re-running" for the whole six minutes of a perfectly healthy
// import. The test below therefore uses a running batch with `countersReconcile: false` on purpose: a
// gate that only worked because the fixture was tidy would pass without it.
//
// **2. `CompletedWithWarnings` is not `Completed`.** The screen used to branch on
// `batch.failedRows > 0`, which makes those two statuses render identically — same heading, same
// green, and a Warned counter sitting in a row of five with nothing to say it is worth reading.
// `types.ts` states that the three `Completed…` values are three different answers to "do I need to go
// and look at the rows?". Three tests below, one per status, with **identical counters**: if the
// screen ever goes back to reading the numbers instead of the status, all three collapse and two of
// them fail.
//
// The transport is a `fetch` stub rather than a mocked `api` module, which is the idiom
// `login.test.tsx` established. It costs nothing and buys the seam: `toImportBatch` has to read seven
// new nullable fields out of these payloads, so a fixture with `progressUnitsTotal: null` in it is
// also a test that a null does not become `0` on the way through.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import StudentsImportProgress from "../src/pages/StudentsImportProgress";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { SIS_IMPORT_STATUS } from "../src/types";
import type { AuthUser } from "../src/types";

const BATCH_ID = "3f6b1c20-0000-4000-8000-000000000001";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["sis.import"],
};

/**
 * `SisImportBatchDto` as the wire carries it, with every progress field explicitly `null`.
 *
 * `null` rather than omitted, and that is the more demanding of the two: an absent key and a JSON
 * `null` both have to reach `undefined` at the seam, and `null` is the one a `typeof` check written
 * carelessly lets through as an object.
 */
const batchJson = (over: Record<string, unknown>) => ({
  id: BATCH_ID,
  termId: "44444444-4444-4444-4444-444444444444",
  termCode: "2026-1",
  source: "Registrar export",
  fileName: "roster.xlsx",
  sourceSheetName: "Sheet1",
  fileHash: "abc123",
  status: SIS_IMPORT_STATUS.Running,
  totalRows: 5400,
  insertedRows: 0,
  updatedRows: 0,
  failedRows: 0,
  skippedRows: 0,
  warningRows: 0,
  startedAt: "2026-08-27T01:00:00Z",
  finishedAt: null,
  countersReconcile: false,
  progressPhase: null,
  progressPhaseNumber: null,
  progressPhaseCount: null,
  progressUnitsDone: null,
  progressUnitsTotal: null,
  progressUpdatedAt: null,
  failureReason: null,
  isTerminal: false,
  ...over,
});

/** The five counters a finished batch reports, identical across the three "Completed…" tests. */
const FINISHED_COUNTERS = {
  totalRows: 12,
  insertedRows: 7,
  updatedRows: 3,
  failedRows: 0,
  skippedRows: 2,
  warningRows: 4,
  countersReconcile: true,
  isTerminal: true,
  finishedAt: "2026-08-27T01:06:00Z",
};

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/**
 * Answers the batch read and the rows read, and nothing else.
 *
 * `batchReply` is a function rather than a value so a test can change what the next poll sees — which
 * is the only way to exercise a screen whose whole subject is a value that changes underneath it.
 */
function serve(batchReply: () => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes("/rows")) return Promise.resolve(json(200, []));
    if (url.includes("/sis/import/")) return Promise.resolve(batchReply());
    throw new Error(`the progress screen asked for something unexpected: ${url}`);
  });
}

function renderAt(batchId: string = BATCH_ID) {
  return render(
    <MemoryRouter initialEntries={[`/students/import/${batchId}`]}>
      <Routes>
        <Route path="/students/import/:batchId" element={<StudentsImportProgress />} />
      </Routes>
    </MemoryRouter>,
  );
}

/** Renders and flushes the first poll, which is the read every one of these tests starts from. */
async function show(batchId?: string) {
  const rendered = renderAt(batchId);
  await act(async () => {});
  return rendered;
}

/** Everything the screen currently says, for the negative assertions. */
const pageText = () => document.body.textContent ?? "";

beforeEach(() => {
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  vi.useFakeTimers();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("the roster import progress screen, while the run is in flight", () => {
  it("renders the phase and the unit counts, and no counters and no reconcile alert", async () => {
    serve(() =>
      json(
        200,
        batchJson({
          progressPhase: "ResolvingFacts",
          progressPhaseNumber: 4,
          progressPhaseCount: 9,
          progressUnitsDone: 1200,
          progressUnitsTotal: 5400,
          progressUpdatedAt: "2026-08-27T01:00:30Z",
        }),
      ),
    );

    await show();

    // The phase, in the operator's words rather than the server's — and it is the live region's whole
    // content, because everything else on the panel changes every second or two.
    const live = screen.getByRole("status");
    expect(live.textContent).toMatch(/Matching sections, students and enrolments/);
    expect(live.textContent).toMatch(/phase 4 of 9/);

    expect(pageText()).toMatch(/1200 of 5400 in this phase/);

    // The counters belong to a finished batch. None of the five may be on this screen.
    expect(screen.queryByText("Inserted")).toBeNull();
    expect(screen.queryByText("Warned")).toBeNull();

    // And this is the one that matters: the fixture says `countersReconcile: false`, which is true of
    // every running batch, and the alert must NOT be drawn from it.
    expect(pageText()).not.toMatch(/counters do not add up/i);

    // Real operating guidance and not decoration: the IIS app pool recycles a quiet server out from
    // under a background run, so the polling this page does is part of what keeps a long import alive.
    expect(pageText()).toMatch(/Leave this tab open/);
  });

  it("says a phase with no unit count is uncounted rather than showing it as zero", async () => {
    serve(() =>
      json(
        200,
        batchJson({
          progressPhase: "RefreshingStudentCache",
          progressPhaseNumber: 7,
          progressPhaseCount: 9,
          // Both null: this phase cannot state a count. Rendering "0 of 0" here would claim a run that
          // has done nothing, which is a different and much worse statement than "nobody is counting".
          progressUnitsDone: null,
          progressUnitsTotal: null,
        }),
      ),
    );

    await show();

    expect(pageText()).toMatch(/does not report a count/);
    expect(pageText()).not.toMatch(/0 of 0/);
  });

  it("names an unknown phase verbatim rather than hiding it behind a catch-all", async () => {
    serve(() => json(200, batchJson({ progressPhase: "ValidatingCards" })));

    await show();

    // A server that grew a tenth phase is telling this screen something true. Printing the raw value
    // is more use than a phrase that fits everything.
    expect(screen.getByRole("status").textContent).toMatch(/ValidatingCards/);
  });

  it("keeps the progress on screen when a poll fails, rather than reporting the import as failed", async () => {
    let answered = false;
    serve(() => {
      if (!answered) {
        answered = true;
        return json(
          200,
          batchJson({ progressPhase: "WritingFacts", progressPhaseNumber: 5, progressPhaseCount: 9 }),
        );
      }
      return json(503, { status: 503, title: "Service unavailable.", detail: "Try again." });
    });

    await show();
    expect(screen.getByRole("status").textContent).toMatch(/Writing sections/);

    await act(async () => void vi.advanceTimersByTime(2_000));

    // The phase is still there. A dropped GET during a six-minute import is not a failed import, and
    // this is the assertion that stops the screen from saying it is.
    expect(screen.getByRole("status").textContent).toMatch(/Writing sections/);
    expect(pageText()).toMatch(/The last check did not answer/);
    // One failure is not enough to reach the unknown-outcome sentence. It is the floor, not the first
    // answer — that demotion is the whole point of polling after a failed run POST.
    expect(pageText()).not.toMatch(/cannot say whether the run is still going/);
  });

  it("reaches the unknown-outcome sentence only after several failed polls in a row", async () => {
    let answered = false;
    serve(() => {
      if (!answered) {
        answered = true;
        return json(200, batchJson({ progressPhase: "WritingFacts" }));
      }
      return json(503, { status: 503, title: "Service unavailable.", detail: "Try again." });
    });

    await show();

    for (let i = 0; i < 3; i += 1) {
      await act(async () => void vi.advanceTimersByTime(2_000));
    }

    expect(pageText()).toMatch(/cannot say whether the run is still going/);
    // Still not an assertive interruption, and still not a claim that anything was lost.
    expect(pageText()).toMatch(/Nothing here changes the roster/);
  });

  it("renders the API's own answer when there is no batch with that id", async () => {
    serve(() => json(404, { status: 404, title: "Not found.", detail: "No such batch." }));

    await show();

    expect(pageText()).toMatch(/has no batch with this id/);
  });
});

describe("the roster import progress screen, once the run is over", () => {
  it("renders the counters and the reconcile check for a terminal batch", async () => {
    serve(() =>
      json(200, batchJson({ status: SIS_IMPORT_STATUS.Completed, ...FINISHED_COUNTERS })),
    );

    await show();

    // `getAllByText` for the four that are also row-filter options — the filter below the counters
    // opens on `Skipped` when nothing failed, so the word is on screen twice and a `getByText` would
    // fail for a reason that has nothing to do with the counters.
    expect(screen.getAllByText("Inserted").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Updated").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Skipped").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Failed").length).toBeGreaterThan(0);
    expect(screen.getByText("Warned")).toBeTruthy();
    expect(pageText()).toMatch(/new to the academic tables/);

    // Reconciled, so no alert — the negative half of the gate the running tests assert the other side
    // of.
    expect(pageText()).not.toMatch(/counters do not add up/i);
  });

  it("draws the reconcile alert on a terminal batch whose counters do not add up", async () => {
    serve(() =>
      json(
        200,
        batchJson({
          status: SIS_IMPORT_STATUS.Completed,
          ...FINISHED_COUNTERS,
          countersReconcile: false,
        }),
      ),
    );

    await show();

    expect(pageText()).toMatch(/counters do not add up/i);
    expect(pageText()).toMatch(/report it rather than re-running/);
  });

  // -------------------------------------------------------------------------------------------
  // The three "Completed…" statuses, on identical counters
  // -------------------------------------------------------------------------------------------
  //
  // Identical is the point. The old screen read `failedRows > 0`, and with `failedRows: 0` in all
  // three fixtures that reading gives one answer for three statuses. Anything that makes these three
  // headings the same again fails two of the three.

  it("says Completed plainly", async () => {
    serve(() =>
      json(200, batchJson({ status: SIS_IMPORT_STATUS.Completed, ...FINISHED_COUNTERS })),
    );

    await show();

    expect(pageText()).toMatch(/The import finished/);
    expect(pageText()).not.toMatch(/carry a warning/);
    expect(pageText()).not.toMatch(/did not import/);
    expect(pageText()).toMatch(/nothing to go and look at/);
  });

  it("says CompletedWithWarnings differently, and says a warned row did import", async () => {
    serve(() =>
      json(
        200,
        batchJson({ status: SIS_IMPORT_STATUS.CompletedWithWarnings, ...FINISHED_COUNTERS }),
      ),
    );

    await show();

    expect(pageText()).toMatch(/The import finished, and some rows carry a warning/);
    // The distinction the counters cannot carry: a warning is a note about a row that imported. An
    // operator who reads it as a failure re-runs an import that needed nothing.
    expect(pageText()).toMatch(/a warning is a note about a row that imported, not a row that did not/);
    expect(pageText()).toMatch(/Nothing needs re-running/);
  });

  it("says CompletedWithErrors differently again, and sends the operator to the rows", async () => {
    serve(() =>
      json(200, batchJson({ status: SIS_IMPORT_STATUS.CompletedWithErrors, ...FINISHED_COUNTERS })),
    );

    await show();

    expect(pageText()).toMatch(/The import finished, and some rows did not import/);
    expect(pageText()).toMatch(/rows counted under Failed were not applied/);
    // The other half of what an operator needs: re-importing is safe and is the fix.
    expect(pageText()).toMatch(/reports them Skipped/);
  });

  it("says a Failed run stopped, that applied rows are in the roster, and why it stopped", async () => {
    serve(() =>
      json(
        200,
        batchJson({
          status: SIS_IMPORT_STATUS.Failed,
          ...FINISHED_COUNTERS,
          failureReason: "Timeout expired while writing section enrolments.",
        }),
      ),
    );

    await show();

    expect(pageText()).toMatch(/The run stopped before it finished/);
    // The sentence that decides what the operator does next. Without it, the safe-looking move is to
    // avoid re-running — which is the one move that leaves the roster half-imported.
    expect(pageText()).toMatch(/are in the roster/);
    expect(pageText()).toMatch(/reported Skipped the second/);
    // And the server's own sentence, which is the only thing that says what actually went wrong.
    expect(pageText()).toMatch(/Timeout expired while writing section enrolments/);
  });

  it("renders a status it has never heard of as itself, claiming nothing about it", async () => {
    serve(() =>
      json(
        200,
        batchJson({ status: "CompletedWithSomethingNew", ...FINISHED_COUNTERS }),
      ),
    );

    await show();

    expect(pageText()).toMatch(/CompletedWithSomethingNew/);
    expect(pageText()).toMatch(/this build does not have a reading for|no reading for/);
  });

  it("withholds Run again on a Failed batch opened without a chosen term", async () => {
    serve(() =>
      json(200, batchJson({ status: SIS_IMPORT_STATUS.Failed, ...FINISHED_COUNTERS })),
    );

    await show();

    // The run confirms the term against the batch (ADR-001 D-5), and a confirmation read back out of
    // the batch checks itself. A page opened cold has no operator-chosen term, so the button that
    // needs one is not drawn — and the screen says why rather than greying something out in silence.
    expect(screen.queryByRole("button", { name: /run again/i })).toBeNull();
    expect(pageText()).toMatch(/needs the term to be confirmed/);
  });
});
