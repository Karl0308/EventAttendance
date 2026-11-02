/** @vitest-environment happy-dom */

// ADR-015 F1 fix round FX-1: a `503 WriteLockUnavailable` has to tell the person on the SCREEN how long
// to wait, not only the `advise()` text that some surfaces never render.
//
// The Personnel page's import dialog renders `describeApiError(error)` alone. So the seconds have to be in
// the `ApiError`'s own message, put there at the seam. The page is driven for real (open the dialog, choose
// a CSV, press Import) with `fetch` stubbed to refuse the import with the lock's 503 and `Retry-After: 5`.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";

import { ApiError } from "../src/api";
import { WriteFailureAlert } from "../src/components/WriteFailureAlert";
import PersonnelPage from "../src/pages/Personnel";

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...headers },
  });

const CSV =
  "Personnel ID,RFID UID,Last Name,First Name,Middle Name,Email,Classification,Department,Organization,Status,Position\n" +
  "P-001,0012503326,Santos,Maria,Reyes,maria@usa.edu.ph,ACAD,CICSS,Chess Club,Active,Instructor\n";

let importReply: () => Response;

beforeEach(() => {
  importReply = () =>
    json(
      503,
      { status: 503, title: "The write could not be completed. Try again shortly.", code: "WriteLockUnavailable" },
      { "Retry-After": "5" },
    );
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input), "http://localhost").pathname;
    const method = init?.method ?? "GET";
    if (method === "GET" && path === "/api/v1/personnel") {
      return Promise.resolve(json(200, { items: [], page: 1, pageSize: 200, total: 0, hasMore: false }));
    }
    if (method === "GET" && path === "/api/v1/personnel/organizations") return Promise.resolve(json(200, []));
    if (method === "POST" && path === "/api/v1/personnel/import") return Promise.resolve(importReply());
    throw new Error(`unserved: ${method} ${path}`);
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function pressImportWithAFile(): Promise<void> {
  render(<PersonnelPage />);
  fireEvent.click(await screen.findByRole("button", { name: /import csv/i }));
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error("The import dialog has no file input.");
  fireEvent.change(input, { target: { files: [new File([CSV], "personnel.csv", { type: "text/csv" })] } });
  await screen.findByText(/row\(s\) ready to import/);
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
  });
}

describe("the personnel import dialog on a 503 WriteLockUnavailable", () => {
  it("shows 'Try again in 5 seconds.' from Retry-After", async () => {
    await pressImportWithAFile();

    expect((await screen.findAllByText(/Try again in 5 seconds\./)).length).toBeGreaterThan(0);
  });

  it("says 'Try again shortly.' when the header is missing", async () => {
    importReply = () =>
      json(503, { status: 503, title: "busy", code: "WriteLockUnavailable" });
    await pressImportWithAFile();

    expect((await screen.findAllByText(/Try again shortly\./)).length).toBeGreaterThan(0);
  });

  it("does not promise a wait on a bare 503", async () => {
    importReply = () => json(503, { status: 503, title: "down" }, { "Retry-After": "5" });
    await pressImportWithAFile();

    await screen.findAllByText(/import failed \(503/i);
    expect(document.body.textContent).not.toContain("Try again in");
  });
});

// FX-2: the heading is derived from `advise().serverEffect`, so a lock refusal on an atomic route must
// not tell the person the record "may have been created", and an import must not be told "nothing was saved".
describe("the failure heading on a 503 WriteLockUnavailable", () => {
  const refusal = (mayCommitPartially: boolean) =>
    new ApiError("http", 503, "POST failed (503: busy). Try again in 5 seconds.", {
      shape: "write",
      problem: { status: 503, code: "WriteLockUnavailable", retryAfterSeconds: 5 },
      mayCommitPartially,
    });

  const show = (error: ApiError) =>
    render(
      <WriteFailureAlert
        error={error}
        notApplied="The record was not saved"
        mayHaveApplied="The record may have been created"
        resendWithheld="withheld"
      />,
    );

  it("an atomic route (create, update, add card) is headed 'not saved' with no import clause", () => {
    show(refusal(false));

    expect(screen.getByText("The record was not saved")).toBeTruthy();
    expect(screen.queryByText("The record may have been created")).toBeNull();
    expect(document.body.textContent).not.toMatch(/import|Rows before/i);
    expect(screen.queryByText("withheld")).toBeNull();
  });

  it("an import route is headed 'may have been created' and says earlier rows may be saved", () => {
    show(refusal(true));

    expect(screen.getByText("The record may have been created")).toBeTruthy();
    expect(document.body.textContent).toContain("Rows before the busy one may already be saved.");
    expect(screen.queryByText("withheld")).toBeNull();
  });
});
