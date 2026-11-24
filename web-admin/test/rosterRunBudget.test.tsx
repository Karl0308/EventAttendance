/** @vitest-environment happy-dom */

// The roster import run when the API holds the connection for the whole run.
//
// The API may run the import inside the request: ~9 minutes at 100k rows, not cancellable. The SPA
// gives a write a 15 s budget. What must NOT happen when that budget runs out:
//
//   - a "timed out / may duplicate" error on screen (a resend would only start a second run), or
//   - the operator being able to stage and run a second file while the first is still Running.
//
// So `api.runImport` reads the batch back when the budget expires and hands the caller a real batch to
// watch; `StudentsImport` navigates to the progress route and remembers the run; and the next visit to
// Import roster asks the API whether that run is still `Running` before it offers an upload.
//
// The transport is a `fetch` stub, as `studentsImport.test.tsx` does.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import { ApiError, api } from "../src/api";
import StudentsImport from "../src/pages/StudentsImport";
import StudentsImportProgress from "../src/pages/StudentsImportProgress";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { PERMISSIONS } from "../src/permissions";
import {
  ACTIVE_IMPORT_STORAGE_KEY,
  activeImportBatchId,
  forgetActiveImport,
  forgetActiveImportUnlessRunning,
  rememberActiveImport,
} from "../src/sisImport";
import { SIS_IMPORT_STATUS } from "../src/types";
import type { AuthUser } from "../src/types";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: [PERMISSIONS.sisImport],
};

const BATCH_ID = "3f6b1c20-0000-4000-8000-000000000001";
const TERM_ID = "44444444-4444-4444-4444-444444444444";
const TERM_CODE = "2026-1";
const API_PREFIX = "/api/v1";
const TERMS_PATH = `${API_PREFIX}/academic/terms`;
const UPLOAD_PATH = `${API_PREFIX}/sis/import/upload`;
const RUN_PATH = `${API_PREFIX}/sis/import/${BATCH_ID}/run`;
const BATCH_PATH = `${API_PREFIX}/sis/import/${BATCH_ID}`;

const jsonResponse = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const batchJson = (over: Record<string, unknown>) => ({
  id: BATCH_ID,
  termId: TERM_ID,
  termCode: TERM_CODE,
  source: "Registrar export",
  fileName: "roster.xlsx",
  sourceSheetName: "Sheet1",
  fileHash: "abc123",
  status: SIS_IMPORT_STATUS.Pending,
  totalRows: 100000,
  insertedRows: 0,
  updatedRows: 0,
  failedRows: 0,
  skippedRows: 0,
  warningRows: 0,
  startedAt: null,
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

const RUNNING = batchJson({ status: SIS_IMPORT_STATUS.Running, startedAt: "2026-11-24T01:00:00Z" });
const COMPLETED = batchJson({
  status: SIS_IMPORT_STATUS.Completed,
  isTerminal: true,
  countersReconcile: true,
  insertedRows: 100000,
  finishedAt: "2026-11-24T01:09:00Z",
});

const PREVIEW = {
  batch: batchJson({}),
  columns: ["REGNO"],
  distinctStudents: 52,
  distinctColleges: 3,
  distinctPrograms: 7,
  distinctCourses: 0,
  distinctSections: 9,
  distinctInstructors: 0,
  blankSectionRows: 0,
  placeholderInstructorRows: 0,
  sampleRows: [],
};

const TERM_JSON = {
  id: TERM_ID,
  code: TERM_CODE,
  schoolYear: "2026",
  semester: "1st",
  isCurrent: true,
  startsOn: null,
  endsOn: null,
};

/** What `AbortSignal.timeout` rejects `fetch` with when the budget runs out. */
const budgetExpired = () => new DOMException("The operation timed out.", "TimeoutError");

interface Stub {
  /** What `POST .../run` does: a Response, or a rejection. */
  run: () => Promise<Response>;
  /** What `GET /sis/import/{id}` answers. */
  batch: () => Response;
}

function serve(stub: Stub): { calls: string[] } {
  const calls: string[] = [];
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input), "http://localhost").pathname;
    const method = init?.method ?? "GET";
    calls.push(`${method} ${path}`);
    if (path === TERMS_PATH) {
      return Promise.resolve(
        jsonResponse(200, { items: [TERM_JSON], page: 1, pageSize: 200, total: 1, hasMore: false }),
      );
    }
    if (path === UPLOAD_PATH) return Promise.resolve(jsonResponse(201, PREVIEW));
    if (path === RUN_PATH && method === "POST") return stub.run();
    if (path === BATCH_PATH && method === "GET") return Promise.resolve(stub.batch());
    if (path === `${BATCH_PATH}/rows`) return Promise.resolve(jsonResponse(200, []));
    throw new Error(`unexpected request: ${method} ${path}`);
  });
  return { calls };
}

beforeEach(() => {
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  resetSessionForTests();
  localStorage.clear();
});

describe("api.runImport when the budget runs out", () => {
  const REQUEST = { termId: TERM_ID };

  it("returns the batch it reads back, instead of a timeout error", async () => {
    const { calls } = serve({ run: () => Promise.reject(budgetExpired()), batch: () => jsonResponse(200, RUNNING) });

    const batch = await api.runImport(BATCH_ID, REQUEST);

    expect(batch.status).toBe(SIS_IMPORT_STATUS.Running);
    expect(batch.isTerminal).toBe(false);
    // One run POST, never a second: a resend is exactly what must not happen.
    expect(calls.filter((c) => c.startsWith("POST") && c.endsWith("/run"))).toHaveLength(1);
  });

  it("still raises the timeout when the batch cannot be read back either", async () => {
    serve({ run: () => Promise.reject(budgetExpired()), batch: () => jsonResponse(503, { title: "down" }) });

    const error = await api.runImport(BATCH_ID, REQUEST).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    // The reason the batch could not be confirmed travels on the timeout as its cause.
    expect((error as ApiError).cause).toBeInstanceOf(ApiError);
    expect(((error as ApiError).cause as ApiError).status).toBe(503);
  });

  it("leaves a refusal the server decided untouched (409 is not a timeout)", async () => {
    const { calls } = serve({
      run: () => Promise.resolve(jsonResponse(409, { title: "Conflict", status: 409 })),
      batch: () => jsonResponse(200, RUNNING),
    });

    const error = await api.runImport(BATCH_ID, REQUEST).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(409);
    expect(calls.some((c) => c === `GET ${BATCH_PATH}`)).toBe(false);
  });
});

function renderImport() {
  return render(
    <MemoryRouter initialEntries={["/students/import"]}>
      <Routes>
        <Route path="/students/import" element={<StudentsImport />} />
        <Route path="/students/import/:batchId" element={<StudentsImportProgress />} />
      </Routes>
    </MemoryRouter>,
  );
}

async function reachPreview() {
  renderImport();
  await act(async () => {});
  fireEvent.mouseDown(await screen.findByRole("combobox", { name: /term this roster is for/i }));
  fireEvent.click(screen.getByRole("option", { name: new RegExp(TERM_CODE) }));
  const workbook = new File([new Uint8Array([1, 2, 3])], "roster.xlsx");
  fireEvent.change(screen.getByLabelText(/roster workbook/i), { target: { files: [workbook] } });
  fireEvent.click(screen.getByRole("button", { name: /upload and preview/i }));
  await act(async () => {});
  await screen.findByRole("heading", { name: /what the file holds/i });
}

describe("pressing Run on the import screen", () => {
  it("moves to the progress screen with no timeout error, and remembers the run", async () => {
    serve({ run: () => Promise.reject(budgetExpired()), batch: () => jsonResponse(200, RUNNING) });
    await reachPreview();

    fireEvent.click(screen.getByRole("button", { name: /run import/i }));
    await act(async () => {});

    expect(await screen.findByRole("heading", { name: /import roster — running/i })).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/may have been started|did not finish answering/i);
    expect(localStorage.getItem(ACTIVE_IMPORT_STORAGE_KEY)).toBe(BATCH_ID);
  });
});

describe("Import roster while a run is going", () => {
  it("offers no upload, only a way to watch the running import", async () => {
    localStorage.setItem(ACTIVE_IMPORT_STORAGE_KEY, BATCH_ID);
    serve({ run: () => Promise.reject(new Error("must not run")), batch: () => jsonResponse(200, RUNNING) });

    renderImport();
    await act(async () => {});

    expect(await screen.findByText(/an import is already running/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /upload and preview/i })).toBeNull();
    const watch = screen.getByRole("link", { name: /watch the running import/i });
    expect(watch.getAttribute("href")).toBe(`/students/import/${BATCH_ID}`);
  });

  it("is open again once the remembered run has finished, and forgets it", async () => {
    localStorage.setItem(ACTIVE_IMPORT_STORAGE_KEY, BATCH_ID);
    serve({ run: () => Promise.reject(new Error("must not run")), batch: () => jsonResponse(200, COMPLETED) });

    renderImport();
    await act(async () => {});

    expect(await screen.findByRole("button", { name: /upload and preview/i })).toBeTruthy();
    expect(screen.queryByText(/an import is already running/i)).toBeNull();
    expect(localStorage.getItem(ACTIVE_IMPORT_STORAGE_KEY)).toBeNull();
  });

  it("is open for a browser that remembers nothing, without asking the API", async () => {
    const { calls } = serve({ run: () => Promise.reject(new Error("must not run")), batch: () => jsonResponse(200, RUNNING) });

    renderImport();
    await act(async () => {});

    expect(await screen.findByRole("button", { name: /upload and preview/i })).toBeTruthy();
    expect(calls.some((c) => c === `GET ${BATCH_PATH}`)).toBe(false);
  });
});

describe("the progress screen releases the guard", () => {
  it("forgets the run once its batch is terminal", async () => {
    localStorage.setItem(ACTIVE_IMPORT_STORAGE_KEY, BATCH_ID);
    serve({ run: () => Promise.reject(new Error("must not run")), batch: () => jsonResponse(200, COMPLETED) });

    render(
      <MemoryRouter initialEntries={[`/students/import/${BATCH_ID}`]}>
        <Routes>
          <Route path="/students/import/:batchId" element={<StudentsImportProgress />} />
        </Routes>
      </MemoryRouter>,
    );
    await act(async () => {});
    await screen.findByText(/what it did/i);

    expect(localStorage.getItem(ACTIVE_IMPORT_STORAGE_KEY)).toBeNull();
  });
});

describe("releasing the guard after a refused run", () => {
  it("keeps it when the refused batch is in fact Running (a 409 on a live run)", async () => {
    localStorage.setItem(ACTIVE_IMPORT_STORAGE_KEY, BATCH_ID);
    serve({ run: () => Promise.reject(new Error("unused")), batch: () => jsonResponse(200, RUNNING) });

    await forgetActiveImportUnlessRunning(BATCH_ID, api.getImportBatch);

    expect(localStorage.getItem(ACTIVE_IMPORT_STORAGE_KEY)).toBe(BATCH_ID);
  });

  it("releases it when the batch is not Running", async () => {
    localStorage.setItem(ACTIVE_IMPORT_STORAGE_KEY, BATCH_ID);
    serve({ run: () => Promise.reject(new Error("unused")), batch: () => jsonResponse(200, batchJson({})) });

    await forgetActiveImportUnlessRunning(BATCH_ID, api.getImportBatch);

    expect(localStorage.getItem(ACTIVE_IMPORT_STORAGE_KEY)).toBeNull();
  });

  it("keeps it, and says so, when the batch cannot be re-read", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    localStorage.setItem(ACTIVE_IMPORT_STORAGE_KEY, BATCH_ID);
    serve({ run: () => Promise.reject(new Error("unused")), batch: () => jsonResponse(503, { title: "down" }) });

    await forgetActiveImportUnlessRunning(BATCH_ID, api.getImportBatch);

    expect(localStorage.getItem(ACTIVE_IMPORT_STORAGE_KEY)).toBe(BATCH_ID);
    expect(warn).toHaveBeenCalled();
  });
});

describe("when browser storage throws", () => {
  it("never throws out of the guard helpers, and warns each time", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const refuse = () => {
      throw new DOMException("denied", "SecurityError");
    };
    vi.stubGlobal("localStorage", { setItem: refuse, getItem: refuse, removeItem: refuse });

    expect(() => rememberActiveImport(BATCH_ID)).not.toThrow();
    expect(activeImportBatchId()).toBeUndefined();
    expect(() => forgetActiveImport(BATCH_ID)).not.toThrow();
    expect(warn).toHaveBeenCalledTimes(3);
  });
});
