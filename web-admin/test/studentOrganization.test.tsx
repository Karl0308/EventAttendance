/** @vitest-environment happy-dom */

// The roster grid's Organization column and its Export CSV button (ADR-011 D-81 and D-83).
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// 1. **The Organization column renders what the student carries, and a dash when there is none.**
//    Addressed by `[data-field="organization"]` rather than by text, as `studentsGridEmail.test.tsx`
//    does and for the same reason: the dash is shared with other columns, so a text match would pass
//    against a grid whose Organization column had been deleted.
// 2. **Export CSV asks `/students/export.csv` for what the grid is filtered to.** The assertion is on
//    the request's query, not on a rendered result — the file is the point, and the page cannot show it.
//    The same settled `search` the list read sent must be on the export request, or the file holds a
//    different set from the total the page is showing.
// 3. **The button is a running state while the request is out, and a failure is announced** rather than
//    swallowed — the page's own notice pattern, an `alert`.
//
// Transport is a `fetch` stub, not a mocked `api`: `api.ts` is the single seam, and mocking it would
// test the page's wiring to a fake. happy-dom lacks `URL.createObjectURL`, so it is stubbed, along with
// the anchor click `saveBlob` fires, in the idiom `rosterTemplateDownload.test.tsx` uses.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import Students from "../src/pages/Students";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const NO_VALUE = "—";

const WITH_ORG_ID = "00000000-0000-4000-8000-000000000001";
const WITHOUT_ORG_ID = "00000000-0000-4000-8000-000000000002";
const ORGANIZATION = "Computer Society";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["students.read", "students.write"],
};

/** `organization` is `null` rather than omitted for the unset row — what the endpoint sends. */
const studentJson = (id: string, n: number, organization: string | null) => ({
  id,
  studentNumber: `2026${String(n).padStart(6, "0")}`,
  fullName: `Student ${n}`,
  firstName: "Student",
  lastName: String(n),
  middleName: null,
  email: null,
  gender: null,
  photoUrl: null,
  course: "BSCS",
  yearLevel: "1",
  section: "A",
  status: "Active",
  cards: [],
  organization,
});

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const problem = (status: number, detail: string) =>
  new Response(
    JSON.stringify({ type: "about:blank", title: "Failed.", status, detail, traceId: "trace-1" }),
    { status, headers: { "Content-Type": "application/problem+json" } },
  );

const EXPORT_PATH = "/api/v1/students/export.csv";

/** Every URL requested, in order, so the assertions can be about the request. */
let asked: string[] = [];
let exportReply: () => Response | Promise<Response>;

const csvOk = () =>
  new Response("﻿\"Student No.\"\r\n", {
    status: 200,
    headers: {
      "Content-Type": "text/csv; charset=utf-8",
      "Content-Disposition": "attachment; filename=EAMS-students-2026-10-24.csv",
    },
  });

function serve(): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(input);
    asked.push(url);
    const path = new URL(url, "http://localhost").pathname;
    if (path.endsWith(EXPORT_PATH)) return Promise.resolve(exportReply());
    if (!path.endsWith("/api/v1/students")) {
      throw new Error(`the students screen asked for something unexpected: ${url}`);
    }
    return Promise.resolve(
      json(200, {
        items: [
          studentJson(WITH_ORG_ID, 1, ORGANIZATION),
          studentJson(WITHOUT_ORG_ID, 2, null),
        ],
        page: 1,
        pageSize: 25,
        total: 2,
        hasMore: false,
      }),
    );
  });
}

async function show() {
  const rendered = render(
    <MemoryRouter initialEntries={["/students"]}>
      <Students />
    </MemoryRouter>,
  );
  await act(async () => {});
  return rendered;
}

function organizationCellOf(rowId: string): Element {
  const cell = document.querySelector(`[data-id="${rowId}"] [data-field="organization"]`);
  if (cell === null) {
    throw new Error(`row ${rowId} has no organization cell — the Organization column is not rendered`);
  }
  return cell;
}

const exportButton = () => screen.getByRole("button", { name: /export csv/i });

/** Requests for the CSV, as parsed URLs. */
const exportRequests = () =>
  asked.map((u) => new URL(u, "http://localhost")).filter((u) => u.pathname.endsWith(EXPORT_PATH));

let createObjectURL: ReturnType<typeof vi.fn>;

beforeEach(() => {
  asked = [];
  exportReply = csvOk;
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  serve();
  createObjectURL = vi.fn(() => "blob:mock-url");
  (URL as unknown as { createObjectURL: typeof createObjectURL }).createObjectURL = createObjectURL;
  (URL as unknown as { revokeObjectURL: () => void }).revokeObjectURL = () => {};
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("the roster grid's Organization column", () => {
  it("renders an Organization column with the student's organization", async () => {
    await show();

    const header = document.querySelector('[role="columnheader"][data-field="organization"]');
    expect(header).not.toBeNull();
    expect(header?.textContent).toContain("Organization");
    expect(organizationCellOf(WITH_ORG_ID).textContent).toBe(ORGANIZATION);
  });

  it("shows the absent-value dash for a student with no organization, not a blank or \"undefined\"", async () => {
    await show();

    const cell = organizationCellOf(WITHOUT_ORG_ID);
    expect(cell.textContent).toBe(NO_VALUE);
    expect(cell.textContent).not.toContain("undefined");
  });
});

describe("the Export CSV button", () => {
  it("requests /students/export.csv with the current search", async () => {
    await show();

    await act(async () => {
      fireEvent.change(screen.getByLabelText(/search name, student no\. or card/i), {
        target: { value: "Santos" },
      });
      // Debounced: the settled value is owed to a pause rather than to the keystroke.
      await new Promise((resolve) => setTimeout(resolve, 400));
    });

    await act(async () => {
      fireEvent.click(exportButton());
    });

    const requests = exportRequests();
    expect(requests).toHaveLength(1);
    expect(requests[0].searchParams.get("search")).toBe("Santos");
    // The file is handed to the browser, not just fetched.
    expect(createObjectURL).toHaveBeenCalledTimes(1);
  });

  it("sends no search parameter when the search box is empty", async () => {
    await show();

    await act(async () => {
      fireEvent.click(exportButton());
    });

    const requests = exportRequests();
    expect(requests).toHaveLength(1);
    expect(requests[0].searchParams.has("search")).toBe(false);
  });

  it("is disabled and says it is exporting while the request is out", async () => {
    let release: (response: Response) => void = () => {};
    exportReply = () => new Promise<Response>((resolve) => (release = resolve));
    await show();

    await act(async () => {
      fireEvent.click(exportButton());
    });

    const running = screen.getByRole("button", { name: /exporting/i });
    expect((running as HTMLButtonElement).disabled).toBe(true);

    await act(async () => {
      release(csvOk());
    });
    expect((exportButton() as HTMLButtonElement).disabled).toBe(false);
  });

  it("announces a failed export to the user as an alert", async () => {
    exportReply = () => problem(403, "You do not have the students.read permission.");
    await show();

    await act(async () => {
      fireEvent.click(exportButton());
    });

    const alert = screen.getByRole("alert");
    expect(alert.textContent).toMatch(/students\.read permission/);
    expect(createObjectURL).not.toHaveBeenCalled();
  });
});
