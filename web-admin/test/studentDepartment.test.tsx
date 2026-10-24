/** @vitest-environment happy-dom */

// The roster grid's Department column (BACKOFFICE Task 4): the college of the student's programme,
// derived by the server and shown read-only, immediately before Organization.
//
// Addressed by `[data-field="department"]` rather than by text, as `studentOrganization.test.tsx` does
// and for the same reason: the dash is shared with other columns, so a text match would pass against a
// grid whose Department column had been deleted. Transport is a `fetch` stub, not a mocked `api`.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import Students from "../src/pages/Students";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const NO_VALUE = "—";

const WITH_DEPT_ID = "00000000-0000-4000-8000-000000000001";
const WITHOUT_DEPT_ID = "00000000-0000-4000-8000-000000000002";
const DEPARTMENT = "College of Engineering";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["students.read", "students.write"],
};

/** `department` is `null` rather than omitted for the unset row — what the endpoint sends. */
const studentJson = (id: string, n: number, department: string | null) => ({
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
  organization: null,
  department,
});

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function serve(): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(input);
    const path = new URL(url, "http://localhost").pathname;
    if (!path.endsWith("/api/v1/students")) {
      throw new Error(`the students screen asked for something unexpected: ${url}`);
    }
    return Promise.resolve(
      json(200, {
        items: [
          studentJson(WITH_DEPT_ID, 1, DEPARTMENT),
          studentJson(WITHOUT_DEPT_ID, 2, null),
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

function departmentCellOf(rowId: string): Element {
  const cell = document.querySelector(`[data-id="${rowId}"] [data-field="department"]`);
  if (cell === null) {
    throw new Error(`row ${rowId} has no department cell — the Department column is not rendered`);
  }
  return cell;
}

beforeEach(() => {
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("the roster grid's Department column", () => {
  it("renders a Department column with the department the server derived", async () => {
    await show();

    const header = document.querySelector('[role="columnheader"][data-field="department"]');
    expect(header).not.toBeNull();
    expect(header?.textContent).toContain("Department");
    expect(departmentCellOf(WITH_DEPT_ID).textContent).toBe(DEPARTMENT);
  });

  it("shows the absent-value dash for a student with no department, not a blank or \"undefined\"", async () => {
    await show();

    const cell = departmentCellOf(WITHOUT_DEPT_ID);
    expect(cell.textContent).toBe(NO_VALUE);
    expect(cell.textContent).not.toContain("undefined");
  });

  it("sits immediately before the Organization column", async () => {
    await show();

    const fields = Array.from(document.querySelectorAll('[role="columnheader"][data-field]')).map((h) =>
      h.getAttribute("data-field"),
    );
    const department = fields.indexOf("department");
    expect(department).toBeGreaterThanOrEqual(0);
    expect(fields[department + 1]).toBe("organization");
  });
});
