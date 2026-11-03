/** @vitest-environment happy-dom */

// #706 F2-1 (W2): the Live Attendance grid shows personnel, and can be narrowed to one population.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
//   1. A personnel row prints its OWN number and name under the ID No. / Name columns, with
//      "Personnel" in the Type column. Before F2-1 those columns were bound to `studentNumber` /
//      `studentName`, which a personnel row does not have, so they rendered blank.
//   2. The Show filter (All / Students / Personnel) is sent as `personType`; All sends nothing, because
//      the server reads an absent parameter as both populations.
//   3. The number / name boxes are labelled for both populations and still send the unchanged
//      `studentNumber` / `studentName` parameters (the server matches personnel through them).
//   4. Clear filters puts the Show select back to All.
//
// `fetch` is stubbed with the harness `liveAttendanceFilters.test.tsx` uses; the stub filters on
// `personType` and the two fragments the way the server does, so a request that forgot a parameter
// shows up as the wrong rows, not only as a missing query string.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import EventDetail from "../src/pages/EventDetail";
import AuthProvider from "../src/components/AuthProvider";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const FILTER_SETTLE_MS = 300;

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["events.read", "events.write"],
};

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const EVENT_JSON = {
  id: EVENT_ID,
  name: "Founders Day",
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
  eventName: "Founders Day",
  expected: 2,
  expectedStudents: 1,
  expectedPersonnel: 1,
  present: 2,
  late: 0,
  absent: 0,
  excused: 0,
  unexpected: 0,
  attendanceRate: 100,
};

const AUDIENCE_JSON = {
  eventId: EVENT_ID,
  status: "Frozen",
  isFrozen: true,
  expected: 2,
  groups: [],
  students: [],
  definitions: [],
  expectedStudents: 1,
  expectedPersonnel: 1,
  expectedSource: "Audience",
  expectedPersonnelSource: "Audience",
  preRegistration: null,
};

const STUDENT_ROW = {
  id: "attendance-1",
  eventId: EVENT_ID,
  studentId: "student-1",
  studentName: "Maria Santos",
  studentNumber: "2026-0001",
  personType: "Student",
  personnelId: null,
  personnelNumber: null,
  personnelName: null,
  checkInAt: "2026-09-01T01:05:00Z",
  checkOutAt: null,
  status: "Present",
  captureMethod: "Rfid",
};

const PERSONNEL_ROW = {
  id: "attendance-2",
  eventId: EVENT_ID,
  studentId: null,
  studentName: null,
  studentNumber: null,
  personType: "Personnel",
  personnelId: "personnel-1",
  personnelNumber: "EMP-0042",
  personnelName: "Pedro Reyes",
  checkInAt: "2026-09-01T01:07:00Z",
  checkOutAt: null,
  status: "Present",
  captureMethod: "Rfid",
};

const ALL_ROWS = [STUDENT_ROW, PERSONNEL_ROW];

let asked: string[] = [];

function serve() {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(input);
    asked.push(url);
    const parsed = new URL(url, "http://localhost");
    const path = parsed.pathname;
    const query = parsed.searchParams;

    if (path === `/api/v1/events/${EVENT_ID}/summary`) return Promise.resolve(json(200, SUMMARY_JSON));
    if (path === `/api/v1/events/${EVENT_ID}/attendees`) return Promise.resolve(json(200, AUDIENCE_JSON));
    if (path === `/api/v1/events/${EVENT_ID}/scans`) {
      return Promise.resolve(json(200, { eventId: EVENT_ID, totalScans: 0, distinctCards: 0, scans: [] }));
    }
    if (path === `/api/v1/events/${EVENT_ID}/attendance-codes`) return Promise.resolve(json(200, []));
    if (path === "/api/v1/pre-registration/sessions") return Promise.resolve(json(200, []));
    if (path === `/api/v1/events/${EVENT_ID}`) return Promise.resolve(json(200, EVENT_JSON));
    if (path === "/api/v1/attendance") {
      const personType = query.get("personType");
      const number = query.get("studentNumber");
      const name = query.get("studentName");
      const matched = ALL_ROWS.filter((row) => {
        if (personType !== null && row.personType !== personType) return false;
        const rowNumber = row.personType === "Personnel" ? row.personnelNumber : row.studentNumber;
        const rowName = row.personType === "Personnel" ? row.personnelName : row.studentName;
        if (number && !(rowNumber ?? "").toLowerCase().includes(number.toLowerCase())) return false;
        if (name && !(rowName ?? "").toLowerCase().includes(name.toLowerCase())) return false;
        return true;
      });
      return Promise.resolve(
        json(200, { items: matched, page: 1, pageSize: 200, total: matched.length, hasMore: false }),
      );
    }
    throw new Error(`the event screen asked for something this stub does not serve: ${url}`);
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

const attendanceRequests = () =>
  asked.filter((u) => new URL(u, "http://localhost").pathname === "/api/v1/attendance");
const lastAttendanceQuery = () => {
  const list = attendanceRequests();
  return new URL(list[list.length - 1], "http://localhost").searchParams;
};

const settle = () =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(FILTER_SETTLE_MS);
  });

/** MUI's select renders a combobox button; open it and pick an option by its visible name. */
async function chooseShow(option: string) {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: /^show$/i }));
  fireEvent.click(within(screen.getByRole("listbox")).getByText(option));
  await settle();
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  asked = [];
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  window.sessionStorage.clear();
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  resetSessionForTests();
});

describe("Live Attendance shows personnel (#706 F2-1)", () => {
  it("prints a personnel row's own number and name, and its Type", async () => {
    await show();

    expect(screen.getByRole("columnheader", { name: "Type" })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: "ID No." })).toBeTruthy();
    const personnelRow = screen.getByText("EMP-0042").closest("[role='row']") as HTMLElement;
    expect(within(personnelRow).getByText("Pedro Reyes")).toBeTruthy();
    expect(within(personnelRow).getByText("Personnel")).toBeTruthy();

    const studentRow = screen.getByText("2026-0001").closest("[role='row']") as HTMLElement;
    expect(within(studentRow).getByText("Maria Santos")).toBeTruthy();
    expect(within(studentRow).getByText("Student")).toBeTruthy();
  });

  it("sends personType=Personnel and shows only personnel when Personnel is chosen", async () => {
    await show();

    await chooseShow("Personnel");

    expect(lastAttendanceQuery().get("personType")).toBe("Personnel");
    expect(screen.getByText("Pedro Reyes")).toBeTruthy();
    expect(screen.queryByText("Maria Santos")).toBeFalsy();
  });

  it("sends personType=Student when Students is chosen, and nothing when All is", async () => {
    await show();

    await chooseShow("Students");
    expect(lastAttendanceQuery().get("personType")).toBe("Student");
    expect(screen.queryByText("Pedro Reyes")).toBeFalsy();

    await chooseShow("All");
    expect(lastAttendanceQuery().has("personType")).toBe(false);
    expect(screen.getByText("Pedro Reyes")).toBeTruthy();
  });

  it("finds a personnel member through the number box, which says so", async () => {
    await show();

    // Labelled for both populations: a box that says "Student number" would tell an operator
    // personnel cannot be searched here.
    expect(screen.queryByLabelText(/student number/i)).toBeFalsy();
    fireEvent.change(screen.getByLabelText(/id number/i), { target: { value: "EMP-" } });
    await settle();

    expect(lastAttendanceQuery().get("studentNumber")).toBe("EMP-");
    expect(screen.getByText("Pedro Reyes")).toBeTruthy();
    expect(screen.queryByText("Maria Santos")).toBeFalsy();
  });

  it("puts Show back to All on Clear filters", async () => {
    await show();
    await chooseShow("Personnel");

    fireEvent.click(screen.getAllByRole("button", { name: /clear filters/i })[0]);
    await settle();

    expect(lastAttendanceQuery().has("personType")).toBe(false);
    expect(screen.getByText("Maria Santos")).toBeTruthy();
  });
});
