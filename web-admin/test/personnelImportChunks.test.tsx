/** @vitest-environment happy-dom */

// Personnel import in chunks of 500.
//
// The server caps a call at 5,000 rows and spends about 7 s per 500 rows with cards, so a 70k-person
// file must not go as one request. What is pinned here:
//
//   - the rows are sent in order, in chunks of at most 500, each chunk one POST;
//   - the results are aggregated, and a reported error row is shifted to its place in the whole file;
//   - the first failing chunk stops the import, nothing after it is sent, and the failure says which
//     chunk failed and how many rows were already saved (and kept);
//   - through the real dialog: progress "chunk n of m" while it runs, and the interrupted message.

import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";

import { ApiError, api } from "../src/api";
import AuthProvider from "../src/components/AuthProvider";
import PersonnelPage from "../src/pages/Personnel";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { PERMISSIONS } from "../src/permissions";
import PersonnelImportDialog from "../src/components/PersonnelImportDialog";
import {
  chunkRows,
  importInChunks,
  mergeImportResult,
  PERSONNEL_IMPORT_CHUNK_SIZE,
  PersonnelImportInterrupted,
} from "../src/personnelImportChunks";
import type { PersonnelImportResult, PersonnelWriteRequest } from "../src/types";

afterEach(() => {
  cleanup();
  resetSessionForTests();
  vi.restoreAllMocks();
});

const row = (n: number): PersonnelWriteRequest => ({
  personnelNumber: `P-${n}`,
  firstName: "A",
  middleName: null,
  lastName: "B",
  email: null,
  classification: null,
  department: null,
  organization: null,
  position: null,
  rfidUid: null,
  status: "Active",
});

const rows = (count: number) => Array.from({ length: count }, (_, i) => row(i + 1));

const result = (over: Partial<PersonnelImportResult>): PersonnelImportResult => ({
  total: 0,
  created: 0,
  updated: 0,
  failed: 0,
  errors: [],
  ...over,
});

/** Every chunk reports its rows created. */
const allCreated = (chunk: PersonnelWriteRequest[]) =>
  Promise.resolve(result({ total: chunk.length, created: chunk.length }));

describe("chunkRows", () => {
  it("splits at 500 and keeps the order", () => {
    const chunks = chunkRows(rows(1201));

    expect(chunks.map((c) => c.length)).toEqual([500, 500, 201]);
    expect(chunks[1][0].personnelNumber).toBe("P-501");
    expect(PERSONNEL_IMPORT_CHUNK_SIZE).toBe(500);
  });

  it("sends a small file as one chunk and an exact multiple without an empty tail", () => {
    expect(chunkRows(rows(3)).map((c) => c.length)).toEqual([3]);
    expect(chunkRows(rows(1000)).map((c) => c.length)).toEqual([500, 500]);
  });
});

describe("mergeImportResult", () => {
  it("adds the counters and shifts error rows by the rows already sent", () => {
    const merged = mergeImportResult(
      result({ total: 500, created: 499, failed: 1, errors: [{ row: 7, message: "x" }] }),
      result({ total: 500, updated: 499, failed: 1, errors: [{ row: 3, personnelNumber: "P-503", message: "y" }] }),
      500,
    );

    expect(merged).toMatchObject({ total: 1000, created: 499, updated: 499, failed: 2 });
    expect(merged.errors.map((e) => e.row)).toEqual([7, 503]);
  });
});

describe("importInChunks", () => {
  it("sends one request per chunk, in order, and aggregates", async () => {
    const send = vi.fn(allCreated);
    const progress = vi.fn();

    const total = await importInChunks(rows(1201), send, progress);

    expect(send.mock.calls.map(([chunk]) => chunk.length)).toEqual([500, 500, 201]);
    expect(total).toMatchObject({ total: 1201, created: 1201, failed: 0 });
    expect(progress.mock.calls.map(([p]) => `${p.chunk}/${p.chunks}`)).toEqual(["1/3", "2/3", "3/3"]);
  });

  it("stops at the first failed chunk, sends nothing after it, and reports what was saved", async () => {
    const boom = new Error("503 busy");
    const send = vi
      .fn<(chunk: PersonnelWriteRequest[]) => Promise<PersonnelImportResult>>()
      .mockImplementationOnce(allCreated)
      .mockRejectedValueOnce(boom);

    const error = await importInChunks(rows(1201), send, () => {}).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(PersonnelImportInterrupted);
    const interrupted = error as PersonnelImportInterrupted;
    expect(interrupted.failedChunk).toBe(2);
    expect(interrupted.chunks).toBe(3);
    expect(interrupted.rowsSaved).toBe(500);
    expect(interrupted.partial.created).toBe(500);
    expect(interrupted.cause).toBe(boom);
    expect(send).toHaveBeenCalledTimes(2);
  });
});

function csvOf(count: number): string {
  const header = "Personnel ID,Last Name,First Name,Status\n";
  return header + Array.from({ length: count }, (_, i) => `P-${i + 1},B,A,Active\n`).join("");
}

async function chooseCsv(onImport: (r: PersonnelWriteRequest[]) => void, extra: Partial<{
  running: boolean;
  progress: { chunk: number; chunks: number };
  error: unknown;
}> = {}) {
  const { container } = render(
    <PersonnelImportDialog
      onClose={() => {}}
      onImport={onImport}
      running={extra.running ?? false}
      progress={extra.progress}
      result={undefined}
      error={extra.error}
    />,
  );
  const input = container.ownerDocument.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error("no file input");
  fireEvent.change(input, { target: { files: [new File([csvOf(1201)], "p.csv", { type: "text/csv" })] } });
  await screen.findByText(/row\(s\) ready to import/);
}

describe("PersonnelImportDialog", () => {
  it("says which chunk is being sent while it runs", async () => {
    await chooseCsv(() => {}, { running: true, progress: { chunk: 2, chunks: 3 } });

    expect(screen.getByRole("status").textContent).toMatch(/chunk 2 of 3/i);
  });

  it("says which chunk failed and that the earlier rows were kept", async () => {
    const partial = result({ total: 500, created: 400, updated: 100 });
    const timeout = new ApiError("network", 0, "The API is busy.", { shape: "write" });
    const interrupted = new PersonnelImportInterrupted(2, 3, 500, partial, timeout);
    await chooseCsv(() => {}, { error: interrupted });

    const alert = screen.getByText(/stopped at chunk/i).closest('[role="alert"]');
    if (alert === null) throw new Error("the interruption is not announced as an alert");
    expect(alert.textContent).toMatch(/stopped at chunk 2 of 3/i);
    expect(alert.textContent).toMatch(/500 rows before this chunk were processed and what was saved is kept/i);
    // A timeout is not a server-decided refusal, so the failed chunk may have been applied.
    expect(alert.textContent).toMatch(/rows in chunk 2 may also have been saved/i);
    expect(alert.textContent).toMatch(/the api is busy/i);
    expect(alert.textContent).toMatch(/importing the same file again is safe/i);
  });

  it("still sends the whole parsed file to onImport (chunking is the page's job)", async () => {
    const onImport = vi.fn();
    await chooseCsv(onImport);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Import" }));
    });

    expect(onImport).toHaveBeenCalledTimes(1);
    expect(onImport.mock.calls[0][0]).toHaveLength(1201);
  });
});

describe("an interrupted import, through the Personnel page", () => {
  const failure = (status: number) =>
    status === 0
      ? new ApiError("network", 0, "The EAMS API did not finish answering POST /personnel/import.", {
          shape: "write",
        })
      : new ApiError("http", status, "Refused.", { shape: "write" });

  async function importThrough(error: ApiError) {
    beginSession("access-token-1", {
      id: "11111111-1111-1111-1111-111111111111",
      schoolId: "22222222-2222-2222-2222-222222222222",
      email: "registrar@usa.edu.ph",
      fullName: "Reg Istrar",
      permissions: [PERMISSIONS.personnelRead, PERMISSIONS.personnelWrite],
    });
    const list = vi.spyOn(api, "listPersonnel").mockResolvedValue([]);
    vi.spyOn(api, "listPersonnelOrganizations").mockResolvedValue([]);
    vi.spyOn(api, "importPersonnel").mockRejectedValue(error);

    render(
      <AuthProvider>
        <PersonnelPage />
      </AuthProvider>,
    );
    await act(async () => {});
    fireEvent.click(await screen.findByRole("button", { name: /import csv/i }));

    const input = document.querySelector<HTMLInputElement>('input[type="file"]');
    if (input === null) throw new Error("no file input");
    fireEvent.change(input, { target: { files: [new File([csvOf(1201)], "p.csv", { type: "text/csv" })] } });
    await screen.findByText(/row\(s\) ready to import/);

    const readsBefore = list.mock.calls.length;
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Import" }));
    });
    return { list, readsBefore };
  }

  it("chunk 1 timing out re-reads the list and says the chunk may have been saved", async () => {
    const { list, readsBefore } = await importThrough(failure(0));

    expect(list.mock.calls.length).toBeGreaterThan(readsBefore);
    const alert = (await screen.findByText(/stopped at chunk 1 of 3/i)).closest('[role="alert"]');
    expect(alert?.textContent).toMatch(/rows in chunk 1 may also have been saved/i);
    expect(alert?.textContent).toMatch(/nothing was saved before this chunk/i);
  });

  it("a refusal the server decided says the chunk saved nothing, and still re-reads", async () => {
    const { list, readsBefore } = await importThrough(failure(400));

    expect(list.mock.calls.length).toBeGreaterThan(readsBefore);
    const alert = (await screen.findByText(/stopped at chunk 1 of 3/i)).closest('[role="alert"]');
    expect(alert?.textContent).toMatch(/nothing in chunk 1 was saved/i);
  });
});
