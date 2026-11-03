/** @vitest-environment happy-dom */

// #706 F2-2 (W3, JJ ruling): "Mark present by hand" — one picker over students AND personnel, the type
// on every option, and `POST /attendance/manual` carrying exactly one id.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
//   1. api: the request is a POST to /attendance/manual whose query carries `eventId`, `status` and
//      exactly ONE of `studentId` / `personnelId`; the reply is narrowed (record included).
//   2. The picker lists a student and a personnel member side by side, each tagged with its type, and a
//      pick sends only the id that belongs to its type. Nothing picked sends nothing.
//   3. Each of the four new refusals (`PersonRequired`, `PersonnelNotFound`, `PersonnelInactive`,
//      `PersonnelNotInAudience`) has its own sentence, keyed on `code`, not on the server's prose.
//   4. The two sources fail independently: students refused, personnel still pickable (and the reverse),
//      with the failure said on screen.
//   5. On the event screen the button needs `attendance.write`, and a successful mark re-reads the grid.
//
// Timers: only setTimeout/clearTimeout are faked, so the 300 ms search debounce is advanced exactly.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import { ApiError, api } from "../src/api";
import AuthProvider from "../src/components/AuthProvider";
import MarkPresentDialog from "../src/components/MarkPresentDialog";
import EventDetail from "../src/pages/EventDetail";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { MANUAL_OUTCOME, manualRefusalText } from "../src/manualAttendance";
import type { ManualAttendanceRequest, AuthUser } from "../src/types";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const STUDENT_ID = "44444444-4444-4444-8444-444444444444";
const PERSONNEL_ID = "55555555-5555-4555-8555-555555555555";
const SEARCH_SETTLE_MS = 300;

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const STUDENT = {
  id: STUDENT_ID,
  studentNumber: "2026-0001",
  fullName: "Maria Cruz",
  firstName: "Maria",
  lastName: "Cruz",
  middleName: null,
  email: null,
  gender: null,
  photoUrl: null,
  course: null,
  yearLevel: null,
  section: null,
  status: "Active",
  cards: [],
  classifications: [],
};

const PERSONNEL = { id: PERSONNEL_ID, personnelNumber: "EMP-0042", fullName: "Cruz, Pedro" };

const SAVED_RECORD = {
  id: "attendance-9",
  eventId: EVENT_ID,
  studentId: null,
  studentName: null,
  studentNumber: null,
  personType: "Personnel",
  personnelId: PERSONNEL_ID,
  personnelNumber: "EMP-0042",
  personnelName: "Cruz, Pedro",
  checkInAt: "2026-09-01T01:30:00Z",
  checkOutAt: null,
  status: "Present",
  captureMethod: "Manual",
};

const SAVED = { success: true, message: "Recorded.", code: "Saved", record: SAVED_RECORD, serverTime: "2026-09-01T01:30:00Z" };

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["events.read", "events.write", "attendance.write", "students.read"],
};

let asked: Array<{ method: string; url: string }> = [];
let manualReply: () => Response;
let studentsReply: () => Response;
let personnelReply: () => Response;

const page = (items: unknown[]) => json(200, { items, page: 1, pageSize: 20, total: items.length, hasMore: false });

function serve() {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    asked.push({ method, url });
    const parsed = new URL(url, "http://localhost");
    const path = parsed.pathname;

    if (path === "/api/v1/attendance/manual" && method === "POST") return Promise.resolve(manualReply());
    if (path === "/api/v1/students") return Promise.resolve(studentsReply());
    if (path === "/api/v1/events/personnel-picker") return Promise.resolve(personnelReply());
    if (path === "/api/v1/attendance") return Promise.resolve(page([]));
    if (path === `/api/v1/events/${EVENT_ID}/summary`) {
      return Promise.resolve(
        json(200, {
          eventId: EVENT_ID, eventName: "Founders Day", expected: 0, expectedStudents: 0, expectedPersonnel: 0,
          present: 0, late: 0, absent: 0, excused: 0, unexpected: 0, attendanceRate: 0,
        }),
      );
    }
    if (path === `/api/v1/events/${EVENT_ID}/attendees`) {
      return Promise.resolve(
        json(200, {
          eventId: EVENT_ID, status: "Frozen", isFrozen: true, expected: 0, groups: [], students: [], definitions: [],
          expectedStudents: 0, expectedPersonnel: 0, expectedSource: "Audience", expectedPersonnelSource: "Audience",
          preRegistration: null,
        }),
      );
    }
    if (path === `/api/v1/events/${EVENT_ID}/scans`) {
      return Promise.resolve(json(200, { eventId: EVENT_ID, totalScans: 0, distinctCards: 0, scans: [] }));
    }
    if (path === `/api/v1/events/${EVENT_ID}/attendance-codes`) return Promise.resolve(json(200, []));
    if (path === "/api/v1/pre-registration/sessions") return Promise.resolve(json(200, []));
    if (path === `/api/v1/events/${EVENT_ID}`) {
      return Promise.resolve(
        json(200, {
          id: EVENT_ID, name: "Founders Day", description: null, location: "Gym", requireRegistration: false,
          startAt: "2026-09-01T01:00:00Z", endAt: "2026-09-01T05:00:00Z", attendanceMode: "Single",
          graceMinutes: 15, status: "Closed", issuesCertificates: false,
        }),
      );
    }
    throw new Error(`unserved: ${method} ${url}`);
  });
}

const problem = (status: number, code: string) =>
  json(status, { status, title: "Refused", detail: `server prose for ${code}`, code, traceId: "t-1" });

async function apiErrorFrom(call: () => Promise<unknown>): Promise<ApiError> {
  try {
    await call();
  } catch (thrown) {
    if (thrown instanceof ApiError) return thrown;
    throw new Error(`Expected an ApiError; got ${String(thrown)}`);
  }
  throw new Error("Expected the call to fail; it succeeded.");
}

const manualRequests = () => asked.filter((r) => r.method === "POST" && r.url.includes("/attendance/manual"));
const queryOf = (url: string) => new URL(url, "http://localhost").searchParams;

const settle = () =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(SEARCH_SETTLE_MS);
  });

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  asked = [];
  manualReply = () => json(200, SAVED);
  studentsReply = () => page([STUDENT]);
  personnelReply = () => json(200, { items: [PERSONNEL], page: 1, pageSize: 200, total: 1, hasMore: false });
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

// ---------------------------------------------------------------------------------------------
// api
// ---------------------------------------------------------------------------------------------

describe("api.recordManualAttendance", () => {
  it("sends only personnelId for a personnel member", async () => {
    const result = await api.recordManualAttendance({ eventId: EVENT_ID, status: "Present", personnelId: PERSONNEL_ID });

    const [request] = manualRequests();
    expect(queryOf(request.url).get("eventId")).toBe(EVENT_ID);
    expect(queryOf(request.url).get("status")).toBe("Present");
    expect(queryOf(request.url).get("personnelId")).toBe(PERSONNEL_ID);
    expect(queryOf(request.url).has("studentId")).toBe(false);
    expect(result.code).toBe("Saved");
    expect(result.record?.personType).toBe("Personnel");
  });

  it("sends only studentId for a student", async () => {
    await api.recordManualAttendance({ eventId: EVENT_ID, status: "Present", studentId: STUDENT_ID });

    const [request] = manualRequests();
    expect(queryOf(request.url).get("studentId")).toBe(STUDENT_ID);
    expect(queryOf(request.url).has("personnelId")).toBe(false);
  });

  it("surfaces a refusal's code on the ApiError", async () => {
    manualReply = () => problem(404, "PersonnelNotInAudience");

    const error = await apiErrorFrom(() =>
      api.recordManualAttendance({ eventId: EVENT_ID, status: "Present", personnelId: PERSONNEL_ID }),
    );

    expect(error.status).toBe(404);
    expect(error.code).toBe("PersonnelNotInAudience");
  });
});

describe("manualRefusalText: the four new outcomes each say their own thing", () => {
  const refusal = (code: string, status: number) =>
    new ApiError("http", status, "x", { shape: "write", problem: { status, code } });

  it.each([
    [MANUAL_OUTCOME.PersonRequired, 400, /choose one student or personnel/i],
    [MANUAL_OUTCOME.PersonnelNotFound, 404, /could not be found/i],
    [MANUAL_OUTCOME.PersonnelInactive, 404, /inactive/i],
    [MANUAL_OUTCOME.PersonnelNotInAudience, 404, /not in this event's audience/i],
  ])("%s", (code, status, expected) => {
    expect(manualRefusalText(refusal(code, status))).toMatch(expected);
  });

  it("returns nothing for a code it does not word, so the generic alert takes over", () => {
    expect(manualRefusalText(refusal("SomethingNew", 400))).toBeUndefined();
    expect(manualRefusalText(new Error("boom"))).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------------------------
// the dialog
// ---------------------------------------------------------------------------------------------

function showDialog(
  onSubmit: (request: ManualAttendanceRequest) => void = () => {},
  failure: { error: unknown } | undefined = undefined,
) {
  render(
    <MarkPresentDialog
      eventId={EVENT_ID}
      eventName="Founders Day"
      onClose={() => {}}
      onSubmit={onSubmit}
      running={false}
      failure={failure}
    />,
  );
}

const picker = () => screen.getByRole("combobox", { name: /student or personnel member/i });

async function search(text: string) {
  fireEvent.change(picker(), { target: { value: text } });
  await settle();
  await act(async () => {});
}

describe("MarkPresentDialog", () => {
  it("lists a student and a personnel member together, each tagged with its type", async () => {
    showDialog();

    await search("cruz");

    const options = screen.getAllByRole("option");
    expect(options).toHaveLength(2);
    const student = options.find((o) => within(o).queryByText("Maria Cruz"));
    const staff = options.find((o) => within(o).queryByText("Cruz, Pedro"));
    expect(student && within(student).getByText("Student")).toBeTruthy();
    expect(staff && within(staff).getByText("Personnel")).toBeTruthy();
    // The student search is the server's: the fragment is sent as `search`.
    const studentRequest = asked.find((r) => new URL(r.url, "http://localhost").pathname === "/api/v1/students");
    expect(queryOf(studentRequest?.url ?? "").get("search")).toBe("cruz");
  });

  it("sends exactly personnelId when a personnel member is picked", async () => {
    const onSubmit = vi.fn<(request: ManualAttendanceRequest) => void>();
    showDialog(onSubmit);

    await search("cruz");
    fireEvent.click(screen.getByRole("option", { name: /Cruz, Pedro/ }));
    fireEvent.click(screen.getByRole("button", { name: "Mark present" }));

    expect(onSubmit).toHaveBeenCalledTimes(1);
    const sent = onSubmit.mock.calls[0][0];
    expect(sent).toEqual({ eventId: EVENT_ID, status: "Present", personnelId: PERSONNEL_ID });
    expect("studentId" in sent).toBe(false);
  });

  it("sends exactly studentId when a student is picked", async () => {
    const onSubmit = vi.fn<(request: ManualAttendanceRequest) => void>();
    showDialog(onSubmit);

    await search("cruz");
    fireEvent.click(screen.getByRole("option", { name: /Maria Cruz/ }));
    fireEvent.click(screen.getByRole("button", { name: "Mark present" }));

    const sent = onSubmit.mock.calls[0][0];
    expect(sent).toEqual({ eventId: EVENT_ID, status: "Present", studentId: STUDENT_ID });
    expect("personnelId" in sent).toBe(false);
  });

  it("sends nothing and says so when nobody is picked", async () => {
    const onSubmit = vi.fn();
    showDialog(onSubmit);

    fireEvent.click(screen.getByRole("button", { name: "Mark present" }));

    expect(onSubmit).not.toHaveBeenCalled();
    expect(screen.getByText("Pick one person from the list.")).toBeTruthy();
  });

  it("does not search students until enough is typed", async () => {
    showDialog();

    await search("c");

    expect(asked.some((r) => new URL(r.url, "http://localhost").pathname === "/api/v1/students")).toBe(false);
    expect(screen.getByText(/type at least 2 characters/i)).toBeTruthy();
  });

  it("words a PersonnelNotInAudience refusal, not the server's prose", () => {
    const error = new ApiError("http", 404, "POST failed", {
      shape: "write",
      problem: { status: 404, code: "PersonnelNotInAudience", detail: "server prose for PersonnelNotInAudience" },
    });

    showDialog(() => {}, { error });

    expect(screen.getByText(/not in this event's audience/i)).toBeTruthy();
    expect(screen.queryByText(/server prose/i)).toBeNull();
  });

  it("still offers personnel when the student search is refused, and says why", async () => {
    studentsReply = () => problem(403, "Forbidden");
    showDialog();

    await search("cruz");

    expect(screen.getByRole("option", { name: /Cruz, Pedro/ })).toBeTruthy();
    expect(screen.queryByRole("option", { name: /Maria Cruz/ })).toBeNull();
    expect(screen.getByText(/students could not be searched/i)).toBeTruthy();
  });

  it("still offers students when the personnel list is refused, and says why", async () => {
    personnelReply = () => problem(403, "Forbidden");
    showDialog();

    await search("cruz");

    expect(screen.getByRole("option", { name: /Maria Cruz/ })).toBeTruthy();
    expect(screen.getByText(/personnel could not be loaded/i)).toBeTruthy();
  });
});

// ---------------------------------------------------------------------------------------------
// the event screen
// ---------------------------------------------------------------------------------------------

async function showEvent() {
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

describe("the event screen's Mark present by hand", () => {
  it("is offered only with attendance.write", async () => {
    beginSession("access-token-2", { ...OPERATOR, permissions: ["events.read"] });
    await showEvent();

    expect(screen.queryByRole("button", { name: /mark present by hand/i })).toBeNull();
  });

  it("records a personnel member, announces it, and re-reads the grid (works on a Closed event)", async () => {
    await showEvent();
    const gridReadsBefore = asked.filter((r) => new URL(r.url, "http://localhost").pathname === "/api/v1/attendance").length;

    fireEvent.click(screen.getByRole("button", { name: /mark present by hand/i }));
    await search("cruz");
    fireEvent.click(screen.getByRole("option", { name: /Cruz, Pedro/ }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Mark present" }));
    });
    await act(async () => {});

    const [request] = manualRequests();
    expect(queryOf(request.url).get("personnelId")).toBe(PERSONNEL_ID);
    expect(queryOf(request.url).has("studentId")).toBe(false);
    // getBy, not findBy: findBy polls on the faked setTimeout and would never fire.
    expect(screen.getByText("Cruz, Pedro was marked present.")).toBeTruthy();
    expect(screen.queryByRole("dialog", { name: /mark present by hand/i })).toBeNull();
    const gridReadsAfter = asked.filter((r) => new URL(r.url, "http://localhost").pathname === "/api/v1/attendance").length;
    expect(gridReadsAfter).toBeGreaterThan(gridReadsBefore);
  });

  it("keeps the dialog open and words the refusal when the server says PersonnelInactive", async () => {
    manualReply = () => problem(404, "PersonnelInactive");
    await showEvent();

    fireEvent.click(screen.getByRole("button", { name: /mark present by hand/i }));
    await search("cruz");
    fireEvent.click(screen.getByRole("option", { name: /Cruz, Pedro/ }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Mark present" }));
    });
    await act(async () => {});

    expect(screen.getByText(/that personnel member is inactive/i)).toBeTruthy();
    expect(screen.getByRole("dialog", { name: /mark present by hand/i })).toBeTruthy();
  });
});
