/** @vitest-environment happy-dom */

// The students screen against a roster too large to hold, which is now the only size it comes in.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// This screen failed in production the first time a real roster was imported. `api.ts` walks every
// page of a list into memory and refuses past `MAX_LIST_ROWS` (2,000); the roster is 21,493, so the
// page rendered "GET /students is too large to show in full … The list needs server-side paging."
// The ceiling behaved correctly — it is written to fail loud rather than hand a grid a truncated
// roster that looks whole. What was missing was the screen asking for a page.
//
// So the fixtures here report `total: 21493` throughout, and that number is the test rather than
// scenery: every assertion below passes trivially against a small roster, and the regression this
// file exists to stop is precisely a read that is correct until the list gets big. If someone routes
// this screen back through `listStudents()`, the first test fails with the production error text.
//
// Four things beyond that, each of which is a way a paged grid quietly lies:
//
// 1. **`rowCount` is the roster, not the page.** A grid handed 25 rows and no total renders as a
//    roster of 25 — the pager has no last page and the user has no reason to think there is more.
// 2. **Search goes to the server.** Filtering the rows in hand can only match within the page in
//    hand, so a surname on page 40 would come back "no matches" from page 1. The assertion is on the
//    request, not on the rendered rows, because rendered rows cannot tell the two implementations
//    apart when the match happens to be on screen.
// 3. **A search resets to the first page**, or the pager says 40 while showing the only page there is.
// 4. **An empty page is not an empty roster.** The two nothings have different copy and the screen
//    has to pick from the query rather than from a row count, which is the same for both.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import Students from "../src/pages/Students";
import { api } from "../src/api";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

/** The roster that broke this screen. */
const ROSTER_TOTAL = 21493;

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["students.read", "students.write"],
};

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const studentJson = (n: number) => ({
  id: `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`,
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
});

/** Every URL this screen asked for, in order, so the assertions can be about the request. */
let asked: string[] = [];

/**
 * Serves `GET /students` as the real endpoint does: a `PagedResult` envelope whose `page` and
 * `pageSize` are echoes of what was asked for, and whose `total` is the count behind the filter
 * rather than the number of rows in the reply.
 *
 * `reply` is given the parsed query so a test can answer differently for a search — which is the
 * only way to prove the search reached the server rather than the array.
 */
function serve(
  reply: (query: URLSearchParams) => { items: unknown[]; total: number; page?: number },
): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(input);
    asked.push(url);
    if (!url.includes("/students")) {
      throw new Error(`the students screen asked for something unexpected: ${url}`);
    }
    const query = new URL(url, "http://localhost").searchParams;
    const answer = reply(query);
    const page = answer.page ?? Number(query.get("page") ?? "1");
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
  });
}

/** A full page of rows, as the server would return for page `page` at `size`. */
const pageOf = (page: number, size: number) =>
  Array.from({ length: size }, (_, i) => studentJson((page - 1) * size + i + 1));

async function show() {
  const rendered = render(
    <MemoryRouter initialEntries={["/students"]}>
      <Students />
    </MemoryRouter>,
  );
  await act(async () => {});
  return rendered;
}

/** The query string of the most recent `GET /students`. */
const lastQuery = () => new URL(asked[asked.length - 1], "http://localhost").searchParams;

const pageText = () => document.body.textContent ?? "";

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

describe("the students grid against a 21,493-row roster", () => {
  it("renders it instead of refusing it, and asks for exactly one page", async () => {
    serve(() => ({ items: pageOf(1, 25), total: ROSTER_TOTAL }));

    await show();

    // The production failure, asserted by absence. `listAll` throws this and `useApiResource` renders
    // it, so a regression to the whole-list read puts this exact sentence on the screen.
    expect(pageText()).not.toMatch(/too large to show in full/);
    expect(pageText()).not.toMatch(/needs server-side paging/);

    expect(screen.getByText("Student 1")).toBeTruthy();

    // One request, not the forty-page walk. This is the assertion that a small-roster fixture could
    // never make, because a walk over 25 rows is also one request.
    expect(asked).toHaveLength(1);
    expect(lastQuery().get("page")).toBe("1");
    expect(lastQuery().get("pageSize")).toBe("25");
  });

  it("tells the user how many students there are, not how many are on screen", async () => {
    serve(() => ({ items: pageOf(1, 25), total: ROSTER_TOTAL }));

    await show();

    // Locale-formatted, because 21493 in a sentence reads as an id rather than a count.
    expect(pageText()).toMatch(/21,493 students/);
  });

  it("asks the server for the next page rather than slicing the rows it holds", async () => {
    serve((query) => ({
      items: pageOf(Number(query.get("page") ?? "1"), 25),
      total: ROSTER_TOTAL,
    }));

    await show();
    expect(asked).toHaveLength(1);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /next page/i }));
    });

    expect(asked).toHaveLength(2);
    expect(lastQuery().get("page")).toBe("2");
    // Row 26 is the first of page 2 and was never in the first reply, so it can only be on screen if
    // the second request happened and its answer was rendered.
    expect(screen.getByText("Student 26")).toBeTruthy();
  });
});

describe("search", () => {
  it("goes to the server, and does not filter the page in hand", async () => {
    serve((query) =>
      query.get("search") === null
        ? { items: pageOf(1, 25), total: ROSTER_TOTAL }
        : { items: [studentJson(9001)], total: 1 },
    );

    await show();

    await act(async () => {
      fireEvent.change(screen.getByLabelText(/search name or student no/i), {
        target: { value: "Santos" },
      });
      // The field is debounced, so the request is owed to a pause rather than to the keystroke.
      await new Promise((resolve) => setTimeout(resolve, 400));
    });

    expect(lastQuery().get("search")).toBe("Santos");
    expect(screen.getByText("Student 9001")).toBeTruthy();
  });

  it("goes back to the first page, so the pager cannot claim a page the result does not have", async () => {
    serve((query) =>
      query.get("search") === null
        ? { items: pageOf(Number(query.get("page") ?? "1"), 25), total: ROSTER_TOTAL }
        : { items: [studentJson(9001)], total: 1 },
    );

    await show();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /next page/i }));
    });
    expect(lastQuery().get("page")).toBe("2");

    await act(async () => {
      fireEvent.change(screen.getByLabelText(/search name or student no/i), {
        target: { value: "Santos" },
      });
      await new Promise((resolve) => setTimeout(resolve, 400));
    });

    expect(lastQuery().get("page")).toBe("1");
  });

  it("does not spend a request per keystroke", async () => {
    serve(() => ({ items: pageOf(1, 25), total: ROSTER_TOTAL }));

    await show();
    const before = asked.length;

    await act(async () => {
      const field = screen.getByLabelText(/search name or student no/i);
      for (const value of ["S", "Sa", "San", "Sant", "Santo", "Santos"]) {
        fireEvent.change(field, { target: { value } });
      }
      await new Promise((resolve) => setTimeout(resolve, 400));
    });

    expect(asked.length - before).toBe(1);
  });
});

describe("the two nothings", () => {
  it("says the roster is empty when there is no search", async () => {
    serve(() => ({ items: [], total: 0 }));

    await show();

    expect(pageText()).toMatch(/No students are on file yet/);
  });

  it("says nothing matched when there is one", async () => {
    serve((query) =>
      query.get("search") === null
        ? { items: pageOf(1, 25), total: ROSTER_TOTAL }
        : { items: [], total: 0 },
    );

    await show();

    await act(async () => {
      fireEvent.change(screen.getByLabelText(/search name or student no/i), {
        target: { value: "Nobody" },
      });
      await new Promise((resolve) => setTimeout(resolve, 400));
    });

    expect(pageText()).toMatch(/No student matches that search/);
    // The distinction is the point: an empty page under a search is not an empty roster, and both
    // arrive here as zero rows.
    expect(pageText()).not.toMatch(/No students are on file yet/);
  });
});

describe("api.listStudentsPage", () => {
  it("does not apply the whole-list ceiling to a page of a large list", async () => {
    serve(() => ({ items: pageOf(1, 25), total: ROSTER_TOTAL }));

    // Directly at the seam, because this is where the production failure was thrown. `MAX_LIST_ROWS`
    // must not be consulted here: it bounds what is accumulated in memory, and this accumulates one
    // page whatever `total` says. Refusing on `total` would refuse to show page 1 *because* the
    // roster is large, which is the bug rather than the guard.
    const page = await api.listStudentsPage({}, 1, 25);

    expect(page.total).toBe(ROSTER_TOTAL);
    expect(page.students).toHaveLength(25);
  });

  it("reports the page the server served, not the page that was asked for", async () => {
    // An out-of-range page is clamped rather than refused, and the echo is how a caller finds out.
    serve(() => ({ items: pageOf(860, 25), total: ROSTER_TOTAL, page: 860 }));

    const page = await api.listStudentsPage({}, 900, 25);

    expect(page.page).toBe(860);
  });

  it("omits an absent filter rather than sending it empty", async () => {
    serve(() => ({ items: [], total: 0 }));

    await api.listStudentsPage({}, 1, 25);

    expect(lastQuery().has("search")).toBe(false);
    expect(lastQuery().has("course")).toBe(false);
    expect(lastQuery().has("status")).toBe(false);
  });
});
