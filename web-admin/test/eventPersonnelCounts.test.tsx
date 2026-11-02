/** @vitest-environment happy-dom */

// ADR-015 phase F1 (W1): the event screen counts personnel in the expected audience.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// `EventDetail` is rendered whole, with `fetch` stubbed (the harness `liveAttendanceFilters.test.tsx`
// uses), because the figure appears in two panels that read two different endpoints — the summary and
// the audience — and the point is that both say the same thing the server said:
//
//   1. "120 students · 15 personnel · 135 total" on the event itself, from `GET /events/{id}/summary`.
//   2. "0 personnel" is printed when there are none, never hidden: an absent figure reads as "not
//      counted", which is the claim ADR-015 D-102 retired.
//   3. The attendance rate is the SERVER's, shown verbatim. The fixture's rate is deliberately not what
//      `(present + late) / expected` would give, so a client-side recomputation fails here.
//   4. The audience chip carries the same breakdown, from `GET /events/{id}/attendees`.
//   5. Nothing on the screen says personnel are "not counted", even with definitions attached and
//      personnel pre-registered — the two places that used to say it.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import EventDetail from "../src/pages/EventDetail";
import AuthProvider from "../src/components/AuthProvider";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const SESSION_ID = "77777777-7777-4777-8777-777777777777";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["events.read", "events.write"],
};

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const eventJson = () => ({
  id: EVENT_ID,
  name: "Founders Day",
  description: null,
  location: "Gym",
  requireRegistration: false,
  startAt: "2026-09-01T01:00:00Z",
  endAt: "2026-09-01T05:00:00Z",
  attendanceMode: "Single",
  graceMinutes: 15,
  status: "Open",
  issuesCertificates: false,
});

/** 100 present + 5 late over 135 expected would be 77.8; the server says 61.5, and the screen must say 61.5. */
const SERVER_RATE = 61.5;

let summary: Record<string, unknown>;
let audience: Record<string, unknown>;

const summaryOf = (over: Record<string, unknown> = {}) => ({
  eventId: EVENT_ID,
  eventName: "Founders Day",
  expected: 135,
  expectedStudents: 120,
  expectedPersonnel: 15,
  present: 100,
  late: 5,
  absent: 0,
  excused: 0,
  unexpected: 0,
  attendanceRate: SERVER_RATE,
  ...over,
});

const audienceOf = (over: Record<string, unknown> = {}) => ({
  eventId: EVENT_ID,
  status: "Open",
  isFrozen: false,
  expected: 135,
  expectedStudents: 120,
  expectedPersonnel: 15,
  groups: [],
  students: [],
  definitions: [
    {
      audienceDefinitionId: "d-1",
      name: "All Staff",
      audienceType: "Department",
      studentCount: 0,
      personnelCount: 15,
      isActive: true,
    },
  ],
  advisoryPersonnelCount: 15,
  expectedSource: "Audience",
  expectedPersonnelSource: "Audience",
  preRegistration: {
    linkedSessions: [
      {
        preRegistrationSessionId: SESSION_ID,
        name: "Founders pre-reg",
        preRegisteredStudentCount: 0,
        advisoryPersonnelCount: 15,
      },
    ],
    totalPreRegisteredStudentCount: 0,
    totalAdvisoryPersonnelCount: 15,
  },
  ...over,
});

function serve(): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const path = url.pathname;
    if (path === `/api/v1/events/${EVENT_ID}/summary`) return Promise.resolve(json(200, summary));
    if (path === `/api/v1/events/${EVENT_ID}/attendees`) return Promise.resolve(json(200, audience));
    if (path === `/api/v1/events/${EVENT_ID}/scans`) {
      return Promise.resolve(json(200, { eventId: EVENT_ID, totalScans: 0, distinctCards: 0, scans: [] }));
    }
    if (path === `/api/v1/events/${EVENT_ID}/attendance-codes`) return Promise.resolve(json(200, []));
    if (path === "/api/v1/pre-registration/sessions" && (init?.method ?? "GET") === "GET") {
      return Promise.resolve(json(200, []));
    }
    if (path === `/api/v1/events/${EVENT_ID}`) return Promise.resolve(json(200, eventJson()));
    if (path === "/api/v1/students" || path === "/api/v1/attendance") {
      return Promise.resolve(json(200, { items: [], page: 1, pageSize: 200, total: 0, hasMore: false }));
    }
    throw new Error(`the event screen asked for something this stub does not serve: ${path}`);
  });
}

async function show(): Promise<void> {
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

beforeEach(() => {
  summary = summaryOf();
  audience = audienceOf();
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  window.sessionStorage.clear();
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("EventDetail counts personnel in the expected audience", () => {
  it("shows 120 students · 15 personnel · 135 total", async () => {
    await show();

    expect(await screen.findByText("120 students · 15 personnel · 135 total")).toBeTruthy();
  });

  it("shows 0 personnel rather than hiding it", async () => {
    summary = summaryOf({ expected: 120, expectedPersonnel: 0 });
    await show();

    expect(await screen.findByText("120 students · 0 personnel · 120 total")).toBeTruthy();
  });

  it("prints the server's attendance rate verbatim", async () => {
    await show();

    expect(await screen.findByText(String(SERVER_RATE))).toBeTruthy();
    // What (present + late) / expected would have given: a client-side recomputation would print this.
    expect(pageText()).not.toContain("77.8");
  });

  it("shows the same breakdown on the audience chip", async () => {
    await show();

    expect(await screen.findByText("Expected: 120 students · 15 personnel · 135 total")).toBeTruthy();
  });

  it("shows no 'not counted' advisory anywhere on the event screen", async () => {
    await show();
    // Wait for the panels that used to carry it: the definitions line and the pre-registration line.
    // Matched by prefix so the wait does not depend on the wording the assertion below is about.
    await waitFor(() => expect(screen.getAllByText(/^15 personnel/)).toHaveLength(2));

    expect(pageText()).not.toMatch(/not counted/i);
    expect(pageText()).not.toMatch(/eligible —/);
  });
});
