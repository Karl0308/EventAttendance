/** @vitest-environment happy-dom */

// The simulate-tap picker, served by `GET /events/{id}/untapped` — searched and paged on the server.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// The picker used to be composed in the browser (every student minus every attendance row), which could
// not work at 21,493 students. Now:
//
//   - an Open event with a writer asks the server for the first page (50) with no search;
//   - typing sends the *settled* fragment as `search`, once per burst, not per keystroke;
//   - when the server holds more than the page, the picker says "N more — refine search" rather than
//     implying the page is everyone;
//   - a failed read degrades the picker to "unavailable" — and only the picker: the grid and the heading
//     still render, and the search box stays mounted;
//   - a Closed event makes no request at all, and neither does a signed-in user without
//     `attendance.write` (the route would only answer 403);
//   - people with no active card are not offered, since a tap is by card.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import EventDetail from "../src/pages/EventDetail";
import AuthProvider from "../src/components/AuthProvider";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const SETTLE_MS = 300;

const WRITER: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["events.read", "events.write", "attendance.write"],
};
const READER: AuthUser = { ...WRITER, permissions: ["events.read"] };

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

let eventStatus = "Open";

const eventJson = () => ({
  id: EVENT_ID,
  name: "Freshman Orientation",
  description: null,
  location: "Gym",
  requireRegistration: false,
  startAt: "2026-09-01T01:00:00Z",
  endAt: "2026-09-01T05:00:00Z",
  attendanceMode: "Single",
  graceMinutes: 15,
  status: eventStatus,
  issuesCertificates: false,
});

const SUMMARY_JSON = {
  eventId: EVENT_ID,
  eventName: "Freshman Orientation",
  expected: 4,
  expectedStudents: 4,
  expectedPersonnel: 0,
  present: 0,
  late: 0,
  absent: 0,
  excused: 0,
  unexpected: 0,
  attendanceRate: 0,
};

const AUDIENCE_JSON = {
  eventId: EVENT_ID,
  status: "Frozen",
  isFrozen: false,
  expected: 4,
  groups: [],
  students: [],
  definitions: [],
  expectedStudents: 4,
  expectedPersonnel: 0,
  expectedSource: "Audience",
  expectedPersonnelSource: "Audience",
  preRegistration: null,
};

const person = (n: number, over: Record<string, unknown> = {}) => ({
  personType: "Student",
  studentId: `student-${n}`,
  personnelId: null,
  number: `2026-${String(n).padStart(4, "0")}`,
  name: `Person ${n}`,
  cardUid: `CARD-${n}`,
  ...over,
});

/** What the next `GET /events/{id}/untapped` answers with, set per test. */
let untappedReply: () => Response;
let asked: URL[] = [];

function serve() {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    asked.push(url);
    const path = url.pathname;

    if (path === `/api/v1/events/${EVENT_ID}/summary`) return Promise.resolve(json(200, SUMMARY_JSON));
    if (path === `/api/v1/events/${EVENT_ID}/attendees`) return Promise.resolve(json(200, AUDIENCE_JSON));
    if (path === `/api/v1/events/${EVENT_ID}/scans`) {
      return Promise.resolve(json(200, { eventId: EVENT_ID, totalScans: 0, distinctCards: 0, scans: [] }));
    }
    if (path === `/api/v1/events/${EVENT_ID}/attendance-codes`) return Promise.resolve(json(200, []));
    if (path === "/api/v1/pre-registration/sessions") return Promise.resolve(json(200, []));
    if (path === `/api/v1/events/${EVENT_ID}/untapped`) return Promise.resolve(untappedReply());
    if (path === `/api/v1/events/${EVENT_ID}`) return Promise.resolve(json(200, eventJson()));
    if (path === "/api/v1/attendance") {
      return Promise.resolve(json(200, { items: [], page: 1, pageSize: 10, total: 0, hasMore: false }));
    }
    throw new Error(`the event screen asked for something this stub does not serve: ${path}`);
  });
}

const page = (items: unknown[], over: Record<string, unknown> = {}) =>
  json(200, { items, page: 1, pageSize: 50, total: items.length, hasMore: false, ...over });

async function show(user: AuthUser = WRITER) {
  resetSessionForTests();
  beginSession("access-token-1", user);
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

const untappedRequests = () => asked.filter((u) => u.pathname === `/api/v1/events/${EVENT_ID}/untapped`);
const pageText = () => document.body.textContent ?? "";
const settle = () =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(SETTLE_MS);
  });

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  asked = [];
  eventStatus = "Open";
  untappedReply = () => page([person(1), person(2)]);
  window.sessionStorage.clear();
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  resetSessionForTests();
});

describe("Simulate-tap picker", () => {
  it("asks the server for the first page of 50, with no search, on an open event", async () => {
    await show();

    expect(untappedRequests()).toHaveLength(1);
    const query = untappedRequests()[0].searchParams;
    expect(query.get("page")).toBe("1");
    expect(query.get("pageSize")).toBe("50");
    expect(query.has("search")).toBe(false);
    expect(screen.getByRole("button", { name: /^tap$/i })).toBeTruthy();
  });

  it("sends the settled fragment as `search`, once per burst of typing", async () => {
    await show();
    const before = untappedRequests().length;

    const box = screen.getByLabelText(/search people/i);
    for (const value of ["R", "Re", "Rey"]) fireEvent.change(box, { target: { value } });
    await settle();

    expect(untappedRequests().length - before).toBe(1);
    expect(untappedRequests()[untappedRequests().length - 1].searchParams.get("search")).toBe("Rey");
  });

  it("says how many more there are when the server holds more than the page", async () => {
    untappedReply = () => page([person(1), person(2)], { total: 120 });
    await show();

    expect(pageText()).toMatch(/118 more — refine search/);
  });

  it("says nothing is left, and not 'more', when everyone has tapped in", async () => {
    untappedReply = () => page([]);
    await show();

    expect(pageText()).toMatch(/Everyone has tapped in/);
    expect(pageText()).not.toMatch(/more — refine search/);
  });

  it("tells a search that matched nobody apart from everyone having tapped in", async () => {
    await show();
    untappedReply = () => page([]);

    fireEvent.change(screen.getByLabelText(/search people/i), { target: { value: "Nobody" } });
    await settle();

    expect(pageText()).toMatch(/No one yet to tap matches this search/);
    expect(pageText()).not.toMatch(/Everyone has tapped in/);
  });

  it("does not offer a person with no active card, since a tap is by card", async () => {
    untappedReply = () => page([person(1), person(2, { cardUid: null })], { total: 2 });
    await show();

    fireEvent.mouseDown(screen.getByRole("combobox", { name: /card \/ student/i }));
    const options = screen.getAllByRole("option").map((o) => o.textContent);

    expect(options).toEqual(["CARD-1 — Person 1"]);
  });

  it("degrades to unavailable on a failed read, without taking the grid or the search box down", async () => {
    untappedReply = () => json(500, { title: "The database is down.", status: 500 });
    await show();

    expect(pageText()).toMatch(/Simulate RFID tap is unavailable/);
    // Not mistaken for a full house.
    expect(pageText()).not.toMatch(/Everyone has tapped in/);
    expect(screen.queryByRole("button", { name: /^tap$/i })).toBeNull();
    // The rest of the screen is unaffected, and the box is still there to type into.
    expect(pageText()).toMatch(/Live attendance \(0\)/);
    expect(screen.getByLabelText(/search people/i)).toBeTruthy();
  });

  it("makes no untapped request on a closed event", async () => {
    eventStatus = "Closed";
    await show();

    expect(pageText()).toMatch(/Freshman Orientation/);
    expect(untappedRequests()).toHaveLength(0);
    expect(screen.queryByLabelText(/search people/i)).toBeNull();
  });

  it("makes no untapped request for a user who cannot write attendance", async () => {
    await show(READER);

    expect(pageText()).toMatch(/Freshman Orientation/);
    expect(untappedRequests()).toHaveLength(0);
  });
});
