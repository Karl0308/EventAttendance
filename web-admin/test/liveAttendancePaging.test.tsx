/** @vitest-environment happy-dom */

// Live Attendance pages against the server — `EventDetail.tsx`'s grid on an event too big to hold.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// Closing an all-campus event writes an `Absent` row for the whole audience (21,494 at this school).
// The screen used to read all of them through `listAll`, which refuses past 2,000, so a closed event
// rendered "GET /attendance is too large to show in full". The grid now asks for one page at a time:
//
//   - a Closed event holding 21,494 rows renders, with no error, and its heading says 21,494;
//   - the next-page control asks the server for page 2 (it does not slice rows it holds);
//   - a settled filter change sends the pager back to page 1 *in the same read* — never one request
//     for the new filter at the old page;
//   - a Closed event reads attendance only through the paged grid and a one-row count: no 200-row walk
//     and no student list for a picker nobody sees.
//
// Fake timers for the debounce, as `liveAttendanceFilters.test.tsx` explains.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import EventDetail from "../src/pages/EventDetail";
import AuthProvider from "../src/components/AuthProvider";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const TOTAL = 21_494;
const DEFAULT_PAGE_SIZE = 10;
const FILTER_SETTLE_MS = 300;

/** Read-only: this screen is about reading a closed event, and a viewer must still be able to. */
const VIEWER: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "viewer@usa.edu.ph",
  fullName: "View Er",
  permissions: ["events.read"],
};

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const eventJson = {
  id: EVENT_ID,
  name: "Foundation Day",
  description: null,
  location: "Gym",
  requireRegistration: false,
  startAt: "2026-09-01T01:00:00Z",
  endAt: "2026-09-01T05:00:00Z",
  attendanceMode: "Single",
  graceMinutes: 15,
  status: "Closed",
  issuesCertificates: false,
};

const SUMMARY_JSON = {
  eventId: EVENT_ID,
  eventName: "Foundation Day",
  expected: TOTAL,
  expectedStudents: TOTAL,
  expectedPersonnel: 0,
  present: 0,
  late: 0,
  absent: TOTAL,
  excused: 0,
  unexpected: 0,
  attendanceRate: 0,
};

const AUDIENCE_JSON = {
  eventId: EVENT_ID,
  status: "Frozen",
  isFrozen: true,
  expected: TOTAL,
  groups: [],
  students: [],
  definitions: [],
  expectedStudents: TOTAL,
  expectedPersonnel: 0,
  expectedSource: "Audience",
  expectedPersonnelSource: "Audience",
  preRegistration: null,
};

const SCANS_JSON = { eventId: EVENT_ID, totalScans: 0, distinctCards: 0, scans: [] };

/** Row *n* (1-based across the whole table), named so a page is recognisable by its first name. */
const row = (n: number) => ({
  id: `attendance-${n}`,
  eventId: EVENT_ID,
  studentId: `student-${n}`,
  studentName: `Absentee ${n}`,
  studentNumber: `2026-${String(n).padStart(5, "0")}`,
  checkInAt: null,
  checkOutAt: null,
  status: "Absent",
  captureMethod: "Import",
  personType: "Student",
  personnelId: null,
  personnelNumber: null,
  personnelName: null,
});

let asked: URL[] = [];
/** How many rows the server holds behind whatever filter arrives — set per test. */
let matchingTotal = TOTAL;
/** When set, the server clamps any requested page past this one back to it, as the real one does. */
let clampToPage: number | undefined;
/** When set, a *filtered* read answers only after this long (fake time) — the "new filter loading" gap. */
let filteredDelayMs: number | undefined;

function serve() {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    asked.push(url);
    const path = url.pathname;

    if (path === `/api/v1/events/${EVENT_ID}/summary`) return Promise.resolve(json(200, SUMMARY_JSON));
    if (path === `/api/v1/events/${EVENT_ID}/attendees`) return Promise.resolve(json(200, AUDIENCE_JSON));
    if (path === `/api/v1/events/${EVENT_ID}/scans`) return Promise.resolve(json(200, SCANS_JSON));
    if (path === `/api/v1/events/${EVENT_ID}/attendance-codes`) return Promise.resolve(json(200, []));
    if (path === "/api/v1/pre-registration/sessions") return Promise.resolve(json(200, []));
    if (path === `/api/v1/events/${EVENT_ID}`) return Promise.resolve(json(200, eventJson));
    if (path === "/api/v1/attendance") {
      const requested = Number(url.searchParams.get("page"));
      const page = clampToPage !== undefined ? Math.min(requested, clampToPage) : requested;
      const pageSize = Number(url.searchParams.get("pageSize"));
      // An unfiltered request is the whole table; a filtered one is `matchingTotal` rows. Either way the
      // reply is only as big as the page asked for — the real server never sends 21,494 rows at once.
      const filtered = url.searchParams.has("studentName");
      const total = filtered ? matchingTotal : TOTAL;
      const first = (page - 1) * pageSize;
      const last = Math.min(first + pageSize, total);
      const items = Array.from({ length: Math.max(0, last - first) }, (_, i) => row(first + i + 1));
      const reply = json(200, { items, page, pageSize, total, hasMore: last < total });
      if (filtered && filteredDelayMs !== undefined) {
        const ms = filteredDelayMs;
        return new Promise<Response>((resolve) => setTimeout(() => resolve(reply), ms));
      }
      return Promise.resolve(reply);
    }
    throw new Error(`the event screen asked for something this stub does not serve: ${url.pathname}`);
  });
}

async function show() {
  render(
    <MemoryRouter initialEntries={[`/events/${EVENT_ID}`]}>
      <AuthProvider>
        <Routes>
          <Route path="/events/:id" element={<EventDetail />} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
  await act(async () => {});
}

const pageText = () => document.body.textContent ?? "";
const attendanceRequests = () => asked.filter((u) => u.pathname === "/api/v1/attendance");
const gridRequests = () => attendanceRequests().filter((u) => u.searchParams.get("pageSize") !== "1");
const lastGridQuery = () => gridRequests()[gridRequests().length - 1].searchParams;

const settle = () =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(FILTER_SETTLE_MS);
  });

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  asked = [];
  matchingTotal = TOTAL;
  clampToPage = undefined;
  filteredDelayMs = undefined;
  resetSessionForTests();
  beginSession("access-token-1", VIEWER);
  window.sessionStorage.clear();
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  resetSessionForTests();
});

describe("Live Attendance on a closed event with 21,494 rows", () => {
  it("renders without the too-large error, and the heading carries the server's total", async () => {
    await show();

    expect(pageText()).not.toMatch(/too large to show in full/i);
    expect(pageText()).not.toMatch(/could not be loaded/i);
    expect(pageText()).toMatch(/Live attendance \(21,494\)/);
    // Only the first page is on screen — not 21,494 rows, and not a 2,000-row slice of them.
    expect(screen.getByText("Absentee 1")).toBeTruthy();
    expect(screen.getByText(`Absentee ${DEFAULT_PAGE_SIZE}`)).toBeTruthy();
    expect(screen.queryByText(`Absentee ${DEFAULT_PAGE_SIZE + 1}`)).toBeFalsy();
  });

  it("reads attendance only as a paged grid and a one-row count, and never loads the roster", async () => {
    await show();

    // No 200-row walk anywhere: every read is the grid's own page or the count's single row.
    for (const request of attendanceRequests()) {
      expect(["1", String(DEFAULT_PAGE_SIZE)]).toContain(request.searchParams.get("pageSize"));
    }
    expect(asked.filter((u) => u.pathname === "/api/v1/students")).toHaveLength(0);
  });

  it("asks the server for page 2 on next-page, rather than slicing rows it holds", async () => {
    await show();
    expect(lastGridQuery().get("page")).toBe("1");

    fireEvent.click(screen.getByRole("button", { name: /next page/i }));
    await act(async () => {});

    expect(lastGridQuery().get("page")).toBe("2");
    expect(screen.getByText(`Absentee ${DEFAULT_PAGE_SIZE + 1}`)).toBeTruthy();
    expect(screen.queryByText("Absentee 1")).toBeFalsy();
  });

  it("goes back to page 1 in the same request a filter change makes", async () => {
    await show();
    fireEvent.click(screen.getByRole("button", { name: /next page/i }));
    await act(async () => {});
    expect(lastGridQuery().get("page")).toBe("2");
    const before = gridRequests().length;

    matchingTotal = 37;
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Absentee 1" } });
    await settle();

    // Exactly one new grid read, and it is the filter at page 1 — not the filter at page 2 followed by
    // a correction.
    expect(gridRequests().length - before).toBe(1);
    expect(lastGridQuery().get("studentName")).toBe("Absentee 1");
    expect(lastGridQuery().get("page")).toBe("1");
    expect(pageText()).toMatch(/Live attendance \(37 of 21,494\)/);
  });

  it("goes back to page 1 when the person-type filter changes too", async () => {
    await show();
    fireEvent.click(screen.getByRole("button", { name: /next page/i }));
    await act(async () => {});

    // MUI's select is a combobox button: open it and pick the option by its visible name.
    fireEvent.mouseDown(screen.getByRole("combobox", { name: /^show$/i }));
    fireEvent.click(within(screen.getByRole("listbox")).getByText("Students"));
    await settle();

    expect(lastGridQuery().get("personType")).toBe("Student");
    expect(lastGridQuery().get("page")).toBe("1");
  });

  it("does not keep the previous filter's \"X of N\" in the heading while the next filter loads", async () => {
    await show();
    matchingTotal = 37;
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Absentee 1" } });
    await settle();
    expect(pageText()).toMatch(/Live attendance \(37 of 21,494\)/);

    // The next filter's answer is held open. Its rows are not here yet, and neither is its count: the
    // old filter's 37 must not stand in for it.
    matchingTotal = 5;
    filteredDelayMs = 5_000;
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Absentee 12" } });
    await settle();
    expect(pageText()).not.toMatch(/37 of/);
    expect(pageText()).toMatch(/Live attendance \(…\)/);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(5_000);
    });
    expect(pageText()).toMatch(/Live attendance \(5 of 21,494\)/);
  });

  it("snaps the pager to the page the server actually served when it clamped the request", async () => {
    await show();
    fireEvent.click(screen.getByRole("button", { name: /next page/i }));
    await act(async () => {});
    expect(lastGridQuery().get("page")).toBe("2");

    // Rows vanished under the operator (a manual change, another admin): the server now has one page
    // only and clamps the next request for page 3 back to page 1.
    clampToPage = 1;
    fireEvent.click(screen.getByRole("button", { name: /next page/i }));
    await act(async () => {});
    await act(async () => {});

    // The pager reads page 1, matching the rows it shows — not "page 3" over page 1's rows.
    expect(lastGridQuery().get("page")).toBe("1");
    expect(pageText()).toMatch(/1[–-]10 of 21,?494/);
    expect(screen.getByText("Absentee 1")).toBeTruthy();
  });
});
