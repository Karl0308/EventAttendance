/** @vitest-environment happy-dom */

// The classification wiring in `Students.tsx` itself — everything `studentClassificationsDialog.test.tsx`
// cannot see because its `Harness` is a parallel re-implementation of the page's write handlers, not a
// rendering of them.
//
// ---------------------------------------------------------------------------------------------
// WHY THIS FILE EXISTS
// ---------------------------------------------------------------------------------------------
//
// Only `sisImport`, `studentsImportProgress` and `studentsPaging` import `Students.tsx`, and none of
// them touch classifications beyond a `classifications: []` fixture field. `submitAssignClassification`
// and `submitClearClassification` — the two real handlers — have never been rendered and driven. This
// file renders the real page, opens the real dialog through the real row action, and drives the real
// handlers, so a regression in the page's own wiring (as opposed to the dialog's) goes red here.
//
// Two things this file is built to catch that nothing else can:
//
// 1. **Non-optimism.** `Students.tsx` never keeps a local copy of `classifications` — both submit
//    handlers call `students.reload()` and the dialog's `student` prop is re-derived from the reloaded
//    page (`managingClassifications`). A build that started rendering the chosen value before the
//    response landed would still pass every test that renders only the dialog, because the dialog has
//    no opinion about where its `student` prop comes from. Only rendering the page can catch that.
// 2. **The 404 `NotAssigned` branch** — added, then negative-controlled by deleting it, and the suite
//    stayed green because nothing rendered the page and cleared a classification through it. Live
//    production code with no coverage until this file.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import Students from "../src/pages/Students";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser, Classification, Student, StudentClassification } from "../src/types";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["students.read", "students.write"],
};

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/** The two classifications this file needs — one axis is enough to drive both handlers. */
const V_STUDENT: Classification = {
  id: "v-student",
  name: "STUDENT",
  nameKey: "student",
  axis: "Student",
  isActive: true,
  studentCount: 10,
};
const V_ACAD: Classification = {
  id: "v-acad",
  name: "ACAD",
  nameKey: "acad",
  axis: "Personnel",
  isActive: true,
  studentCount: 5,
};
const VOCAB: Classification[] = [V_STUDENT, V_ACAD];

function held(over: Partial<StudentClassification>): StudentClassification {
  return {
    classificationId: "v-student",
    name: "STUDENT",
    axis: "Student",
    isActive: true,
    assignedAt: "2026-01-01T00:00:00Z",
    ...over,
  };
}

const studentJson = (over: Partial<Student> = {}) => ({
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
  classifications: [],
  ...over,
});

/** Every request this screen and its dialog made, in order — so an assertion can be about the traffic. */
let asked: { method: string; url: string }[] = [];

/**
 * Serves `GET /students` from `studentsReply` (called again on every `reload()`), `GET
 * /classifications` from `vocabulary`, and any write on a classification assignment route from
 * `writeReply`.
 */
function serve(opts: {
  studentsReply: (query: URLSearchParams) => { items: unknown[]; total: number };
  vocabulary: Classification[];
  writeReply?: (method: string, url: string) => Response;
}): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    asked.push({ method, url });

    if (url.includes("/classifications") && !url.includes("/students/")) {
      return Promise.resolve(
        json(200, {
          items: opts.vocabulary,
          page: 1,
          pageSize: 200,
          total: opts.vocabulary.length,
          hasMore: false,
        }),
      );
    }
    if (url.includes("/students/") && url.includes("/classifications/")) {
      if (opts.writeReply === undefined) {
        throw new Error(`the page wrote to ${method} ${url} but this test gave it no write reply`);
      }
      return Promise.resolve(opts.writeReply(method, url));
    }
    if (url.includes("/students")) {
      const query = new URL(url, "http://localhost").searchParams;
      const answer = opts.studentsReply(query);
      const page = Number(query.get("page") ?? "1");
      const pageSize = Number(query.get("pageSize") ?? "25");
      return Promise.resolve(
        json(200, {
          items: answer.items,
          page,
          pageSize,
          total: answer.total,
          hasMore: page * pageSize < answer.total,
        }),
      );
    }
    throw new Error(`the students screen asked for something unexpected: ${method} ${url}`);
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

const pageText = () => document.body.textContent ?? "";

/** Opens the classifications dialog for Ana Cruz through the real row action, and waits for the
 * vocabulary read to settle so the axis pickers are on screen. */
async function openClassifications() {
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: /classifications for ana cruz/i }));
  });
  await act(async () => {});
}

/** Opens the Select for one axis and returns its option listbox. */
function openAxis(axis: string) {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: axis }));
  return within(screen.getByRole("listbox"));
}

/** The closed Select's own rendered value, whitespace stripped — mirrors `studentClassificationDialog.test.tsx`. */
const closedValueOf = (axis: string) =>
  (screen.getByRole("combobox", { name: axis }).textContent ?? "").replace(/[\s​]+/g, "");

const NONE_TEXT = "—none—";

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

describe("the page never renders a classification write it has not confirmed", () => {
  it("leaves the axis showing nothing held after the server refuses the assign with a 409", async () => {
    // The row starts holding nothing on Student, and — because the write below always answers 409 —
    // it is served unchanged on every re-read too. If `Students.tsx` ever kept a local copy of
    // `classifications` and painted it in before the response, this fixture would not catch it; the
    // fence here is that the *reload* is the only source the dialog is ever allowed to show, and the
    // reload always tells the truth.
    serve({
      studentsReply: () => ({ items: [studentJson({ classifications: [] })], total: 1 }),
      vocabulary: VOCAB,
      writeReply: (method) =>
        method === "PUT"
          ? json(409, { status: 409, title: "Conflict", code: "ConcurrentClassificationChange" })
          : json(200, { studentId: "x", classifications: [], message: "cleared" }),
    });

    await show();
    await openClassifications();

    expect(closedValueOf("Student")).toBe(NONE_TEXT);

    const listbox = openAxis("Student");
    await act(async () => {
      fireEvent.click(listbox.getByText("STUDENT"));
    });

    // The write was refused, so the reload it triggers must still say nothing is held — and the
    // dialog, reading only from that reload, must still show nothing selected.
    expect(closedValueOf("Student")).toBe(NONE_TEXT);

    // The grid's own Classification column must agree with the dialog — no chip appeared there either.
    expect(screen.queryByText("STUDENT")).toBeNull();

    // The failure was surfaced, not swallowed — the dialog is open, so it renders in place rather than
    // in the Snackbar.
    expect(screen.getByRole("alert")).toBeTruthy();
    expect(pageText()).toMatch(/not assigned/i);
  });
});

describe("clearing a classification a second operator already cleared (404 NotAssigned)", () => {
  it("announces it as the success it is, with no failure alert alongside it", async () => {
    let reloaded = false;
    serve({
      studentsReply: () => ({
        items: [
          studentJson({
            // Before the clear: still holding STUDENT. After the reload the DELETE handler below
            // triggers, it is gone — matching what a second operator having cleared it first would
            // leave on the server.
            classifications: reloaded ? [] : [held({})],
          }),
        ],
        total: 1,
      }),
      vocabulary: VOCAB,
      writeReply: (method) => {
        if (method !== "DELETE") throw new Error(`unexpected write: ${method}`);
        reloaded = true;
        return json(404, {
          status: 404,
          title: "Not found",
          detail: "This person does not currently hold that classification.",
          code: "NotAssigned",
        });
      },
    });

    await show();
    await openClassifications();

    expect(closedValueOf("Student")).toBe("STUDENT");

    const listbox = openAxis("Student");
    await act(async () => {
      fireEvent.click(listbox.getByText("— none —"));
    });

    // Announced as success — the exact sentence `submitClearClassification` writes for this branch.
    expect(pageText()).toMatch(/"STUDENT" \(Student\) was already cleared\./);

    // And not also as a failure: `clearClassification.reset()` must have cleared the mutation's own
    // failure state, or `WriteFailureAlert` would render "The classification was not cleared" right
    // beside a Snackbar that just said the opposite.
    expect(screen.queryAllByRole("alert")).toHaveLength(0);
    expect(pageText()).not.toMatch(/not cleared/i);

    // The reload already shows the axis empty, which is why this is announced as success at all.
    expect(closedValueOf("Student")).toBe(NONE_TEXT);
  });
});

describe("the rest of the page's classification wiring", () => {
  it("shows every axis a person holds as its own chip, for someone holding two", async () => {
    serve({
      studentsReply: () => ({
        items: [
          studentJson({
            classifications: [held({}), held({ classificationId: "v-acad", name: "ACAD", axis: "Personnel" })],
          }),
        ],
        total: 1,
      }),
      vocabulary: VOCAB,
    });

    await show();

    expect(screen.getByText("STUDENT")).toBeTruthy();
    expect(screen.getByText("ACAD")).toBeTruthy();
  });

  it("opens the card lookup dialog from the Find by card button", async () => {
    serve({
      studentsReply: () => ({ items: [studentJson()], total: 1 }),
      vocabulary: VOCAB,
    });

    await show();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /find by card/i }));
    });

    expect(screen.getByRole("heading", { name: /find a person by card/i })).toBeTruthy();
  });

  it("opens the classifications dialog for the right student from the row action", async () => {
    serve({
      studentsReply: () => ({ items: [studentJson()], total: 1 }),
      vocabulary: VOCAB,
    });

    await show();
    await openClassifications();

    expect(screen.getByRole("heading", { name: /classifications — ana cruz/i })).toBeTruthy();
  });
});
