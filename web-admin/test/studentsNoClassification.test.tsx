/** @vitest-environment happy-dom */

// Student classification is gone from the roster screen (ADR-009, D-76, QA job order AC1: "no
// Classification column in the Students table"). This file is the AC's named, auto-runnable test —
// an AC that lives only in a QA spreadsheet can be put back by any later clean merge without anything
// going red.
//
// Every "no X" assertion below sits beside a positive control that proves the thing it would have
// caught is actually rendered (the Name header, the Edit action), so an empty grid cannot pass for a
// grid without a classification column.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import Students from "../src/pages/Students";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["students.read", "students.write"],
};

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const studentJson = (extra: Record<string, unknown> = {}) => ({
  id: "00000000-0000-4000-8000-000000000001",
  studentNumber: "2026000001",
  fullName: "Ana Cruz",
  firstName: "Ana",
  lastName: "Cruz",
  middleName: null,
  email: null,
  gender: null,
  photoUrl: null,
  course: "BSCS",
  yearLevel: "1",
  section: "A",
  status: "Active",
  cards: [],
  ...extra,
});

/** Every URL the screen asked for, in order, so an assertion can be about the traffic. */
let asked: string[] = [];

/** Serves `GET /students` with the given rows; any other request fails the test loudly. */
function serve(items: unknown[]): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(input);
    asked.push(url);
    if (!url.includes("/students")) {
      throw new Error(`the students screen asked for something unexpected: ${url}`);
    }
    return Promise.resolve(
      json(200, { items, page: 1, pageSize: 25, total: items.length, hasMore: false }),
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

beforeEach(() => {
  asked = [];
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("the roster grid carries no classification", () => {
  it("renders no Classification column header", async () => {
    serve([studentJson()]);
    await show();

    const headers = Array.from(document.querySelectorAll('[role="columnheader"]'));
    // Positive control: the grid did render its headers, so "no classification" is not "no grid".
    expect(headers.some((h) => h.textContent?.includes("Name"))).toBe(true);

    expect(headers.some((h) => /classification/i.test(h.textContent ?? ""))).toBe(false);
    expect(document.querySelector('[data-field="classifications"]')).toBeNull();
  });

  it("renders no Classifications action on any row", async () => {
    serve([studentJson(), studentJson({ id: "00000000-0000-4000-8000-000000000002", fullName: "Ben Reyes" })]);
    await show();

    // Positive control: the row actions that remain are there.
    expect(screen.getByRole("button", { name: "Edit Ana Cruz" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Edit Ben Reyes" })).toBeTruthy();

    expect(screen.queryAllByRole("button", { name: /classification/i })).toHaveLength(0);
  });

  it("never requests GET /classifications when the page loads", async () => {
    serve([studentJson()]);
    await show();

    expect(asked.length).toBeGreaterThan(0);
    expect(asked.filter((url) => url.includes("/classifications"))).toEqual([]);
  });

  it("renders a row normally when the server still sends a classifications array", async () => {
    // An older server, or the dormant endpoint behind a proxy cache, may still send the field.
    serve([
      studentJson({
        classifications: [
          {
            classificationId: "v-student",
            name: "STUDENT",
            axis: "Student",
            isActive: true,
            assignedAt: "2026-01-01T00:00:00Z",
          },
        ],
      }),
    ]);
    await show();

    expect(screen.getByText("Ana Cruz")).toBeTruthy();
    expect(screen.getByText("2026000001")).toBeTruthy();
    // The held classification's name is not painted anywhere — the page neither reads nor shows it.
    expect(screen.queryByText("STUDENT")).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
