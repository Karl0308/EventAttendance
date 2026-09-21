/** @vitest-environment happy-dom */

// The roster grid's Email column — that it is there, and what it renders for the roster we actually
// have.
//
// ---------------------------------------------------------------------------------------------
// WHY THE EMPTY CELL IS THE MORE IMPORTANT HALF
// ---------------------------------------------------------------------------------------------
//
// `email` is unset for effectively every one of the 21,497 imported rows — the read DTO serves the
// school address and the importer does not carry one — so the *absent* rendering is not an edge case
// here, it is the production appearance of the column. Three things can come out of that cell and
// only one of them is right: the sentinel dash, a blank (which reads as a broken column rather than
// as "no address on file"), or the string "undefined" (which is what a formatter written as
// `String(v)` produces once `optStr` has turned the wire's `null` into `undefined`).
//
// Before this file `Email` appeared in exactly one test, `studentDraft.test.ts`, and that one is
// about the *form* — the column could be deleted outright and nothing went red.
//
// Assertions reach for `[data-field="email"]` rather than for the text. The dash is shared with the
// Classification and RFID columns, so `getByText("—")` would pass against a row whose email column
// had been removed entirely, matching a neighbour's sentinel instead. Addressing the cell by field is
// what makes the assertion be about this column.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import Students from "../src/pages/Students";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

/** The sentinel the grid uses for every absent value. `Students.tsx` names it `NO_VALUE`. */
const NO_VALUE = "—";

const WITH_EMAIL_ID = "00000000-0000-4000-8000-000000000001";
const WITHOUT_EMAIL_ID = "00000000-0000-4000-8000-000000000002";
const ADDRESS = "maria.santos@usa.edu.ph";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["students.read", "students.write"],
};

/**
 * `StudentDto` as the wire carries it. `email` is `null` rather than omitted for the unset row —
 * that is what the endpoint sends, and it is the value a careless formatter turns into "undefined".
 */
const studentJson = (id: string, n: number, email: string | null) => ({
  id,
  studentNumber: `2026${String(n).padStart(6, "0")}`,
  fullName: `Student ${n}`,
  firstName: "Student",
  lastName: String(n),
  middleName: null,
  email,
  gender: null,
  photoUrl: null,
  course: "BSCS",
  yearLevel: "1",
  section: "A",
  status: "Active",
  cards: [],
  classifications: [],
});

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/** One page of exactly two rows: one with an address on file, one with none. */
function serveTwoRows(): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(input);
    if (!url.includes("/students")) {
      throw new Error(`the students screen asked for something unexpected: ${url}`);
    }
    return Promise.resolve(
      json(200, {
        items: [
          studentJson(WITH_EMAIL_ID, 1, ADDRESS),
          studentJson(WITHOUT_EMAIL_ID, 2, null),
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

/** The `email` cell of one row, addressed by row id and column field rather than by its text. */
function emailCellOf(rowId: string): Element {
  const cell = document.querySelector(`[data-id="${rowId}"] [data-field="email"]`);
  if (cell === null) {
    throw new Error(`row ${rowId} has no email cell — the Email column is not being rendered`);
  }
  return cell;
}

beforeEach(() => {
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  serveTwoRows();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("the roster grid's Email column", () => {
  it("is a column of the grid, under that header", async () => {
    await show();

    const header = document.querySelector('[role="columnheader"][data-field="email"]');
    expect(header).not.toBeNull();
    expect(header?.textContent).toContain("Email");
  });

  it("shows the address of a student who has one", async () => {
    await show();

    expect(emailCellOf(WITH_EMAIL_ID).textContent).toBe(ADDRESS);
    // And it is in the grid rather than only in the DOM somewhere — the row came from the read.
    expect(screen.getByText("Student 1")).toBeTruthy();
  });

  it("shows the absent-value dash for a student who has none, not a blank and not \"undefined\"", async () => {
    await show();

    // The production case: every imported row looks like this one. `email` arrived as JSON `null`,
    // became `undefined` at the seam, and must reach the cell as the same sentinel the RFID and
    // Classification columns use.
    const cell = emailCellOf(WITHOUT_EMAIL_ID);
    expect(cell.textContent).toBe(NO_VALUE);
    expect(cell.textContent).not.toBe("");
    expect(cell.textContent).not.toContain("undefined");
  });
});
