// `GET /attendance` read one page at a time, and `eventDetail()` no longer loading every row.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// Closing an all-campus event writes an `Absent` row for the whole audience (21,494 at this school),
// and the unpaged read this screen used to make refused past `MAX_LIST_ROWS` (2,000) — so the closed
// event's screen failed with "GET /attendance is too large to show in full". Two things keep that from
// recurring:
//
//   - `listAttendancePage` asks for one page, parses it, and has no ceiling (it returns `pageSize`
//     rows whatever `total` says);
//   - `eventDetail()` reads attendance only as a one-row count, and fails as a whole when any of its
//     three views of the event (event, summary, count) cannot be read.
//
// The tap picker's own read (`GET /events/{id}/untapped`) is pinned in `untappedApi.test.ts`.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "../src/api";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const CLOSED_EVENT_ROWS = 21_494;

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const attendanceRow = (n: number) => ({
  id: `attendance-${n}`,
  eventId: EVENT_ID,
  studentId: `student-${n}`,
  studentName: `Student ${n}`,
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

const eventJson = (status: string) => ({
  id: EVENT_ID,
  name: "Foundation Day",
  description: null,
  location: "Gym",
  requireRegistration: false,
  startAt: "2026-09-01T01:00:00Z",
  endAt: "2026-09-01T05:00:00Z",
  attendanceMode: "Single",
  graceMinutes: 15,
  status,
  issuesCertificates: false,
});

const SUMMARY_JSON = {
  eventId: EVENT_ID,
  eventName: "Foundation Day",
  expected: CLOSED_EVENT_ROWS,
  expectedStudents: CLOSED_EVENT_ROWS,
  expectedPersonnel: 0,
  present: 0,
  late: 0,
  absent: CLOSED_EVENT_ROWS,
  excused: 0,
  unexpected: 0,
  attendanceRate: 0,
};

/** Every URL asked for, in order — assertions read the requests, not the render. */
let asked: URL[] = [];

/**
 * A server holding `attendanceTotal` rows for the event: honours `page`/`pageSize` as the real one does
 * (so the response is as large as the page asked for, never the whole table).
 */
function serve(options: { status: string; attendanceTotal: number }): void {
  const { status, attendanceTotal } = options;
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    asked.push(url);

    if (url.pathname === `/api/v1/events/${EVENT_ID}`) return Promise.resolve(json(200, eventJson(status)));
    if (url.pathname === `/api/v1/events/${EVENT_ID}/summary`) return Promise.resolve(json(200, SUMMARY_JSON));
    if (url.pathname === "/api/v1/attendance") {
      const page = Number(url.searchParams.get("page"));
      const pageSize = Number(url.searchParams.get("pageSize"));
      const first = (page - 1) * pageSize;
      const last = Math.min(first + pageSize, attendanceTotal);
      const items = Array.from({ length: Math.max(0, last - first) }, (_, i) =>
        attendanceRow(first + i + 1000),
      );
      return Promise.resolve(
        json(200, { items, page, pageSize, total: attendanceTotal, hasMore: last < attendanceTotal }),
      );
    }
    throw new Error(`unexpected request: ${url.pathname}`);
  });
}

const attendanceRequests = () => asked.filter((u) => u.pathname === "/api/v1/attendance");

afterEach(() => {
  asked = [];
  vi.unstubAllGlobals();
});

describe("api.listAttendancePage", () => {
  it("asks for the page it was given, with the filters, and returns the server's total", async () => {
    serve({ status: "Closed", attendanceTotal: CLOSED_EVENT_ROWS });

    const result = await api.listAttendancePage(EVENT_ID, { studentName: "Reyes", personType: "Student" }, 2, 25);

    const [request] = attendanceRequests();
    expect(request.searchParams.get("eventId")).toBe(EVENT_ID);
    expect(request.searchParams.get("page")).toBe("2");
    expect(request.searchParams.get("pageSize")).toBe("25");
    expect(request.searchParams.get("studentName")).toBe("Reyes");
    expect(request.searchParams.get("personType")).toBe("Student");

    expect(result.items).toHaveLength(25);
    expect(result.items[0].id).toBe("attendance-1025");
    expect(result.page).toBe(2);
    expect(result.pageSize).toBe(25);
    expect(result.total).toBe(CLOSED_EVENT_ROWS);
    // The page envelope's `hasMore` is not handed on — a grid needs the count, not "is there a next".
    expect(result).not.toHaveProperty("hasMore");
  });

  it("is not subject to a row ceiling: a 21,494-row event answers its first page", async () => {
    serve({ status: "Closed", attendanceTotal: CLOSED_EVENT_ROWS });

    await expect(api.listAttendancePage(EVENT_ID, undefined, 1, 10)).resolves.toMatchObject({
      total: CLOSED_EVENT_ROWS,
    });
  });

  it("fails loud on an envelope without a total instead of rendering a pager over nothing", async () => {
    vi.stubGlobal("fetch", () =>
      Promise.resolve(json(200, { items: [], page: 1, pageSize: 10, hasMore: false })),
    );

    await expect(api.listAttendancePage(EVENT_ID, undefined, 1, 10)).rejects.toBeInstanceOf(ApiError);
  });
});

describe("api.eventDetail", () => {
  it("loads a closed 21,494-row event with one count read and no row walk", async () => {
    serve({ status: "Closed", attendanceTotal: CLOSED_EVENT_ROWS });

    const detail = await api.eventDetail(EVENT_ID);

    expect(detail.recordCount).toBe(CLOSED_EVENT_ROWS);
    expect(detail.summary?.absent).toBe(CLOSED_EVENT_ROWS);
    // The only attendance read is the one-row count, never a 200-row walk.
    const reads = attendanceRequests();
    expect(reads).toHaveLength(1);
    expect(reads[0].searchParams.get("pageSize")).toBe("1");
    // And the picker's input is not this read's business any more.
    expect(asked.map((u) => u.pathname)).not.toContain("/api/v1/students");
    expect(asked.map((u) => u.pathname)).not.toContain(`/api/v1/events/${EVENT_ID}/untapped`);
    expect(detail).not.toHaveProperty("records");
    expect(detail).not.toHaveProperty("roster");
  });

  // The three views of one fact settle together: any one failing fails the read as a whole, so the
  // screen cannot render a half-truth whose halves contradict each other.
  type Part = "event" | "summary" | "count";
  const serveWith = (failing: Part) =>
    vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://localhost");
      const down = json(500, { title: "The database is down.", status: 500 });
      if (url.pathname === `/api/v1/events/${EVENT_ID}`) {
        return Promise.resolve(failing === "event" ? down : json(200, eventJson("Closed")));
      }
      if (url.pathname === `/api/v1/events/${EVENT_ID}/summary`) {
        return Promise.resolve(failing === "summary" ? down : json(200, SUMMARY_JSON));
      }
      if (url.pathname === "/api/v1/attendance") {
        return Promise.resolve(
          failing === "count"
            ? down
            : json(200, { items: [], page: 1, pageSize: 1, total: 0, hasMore: false }),
        );
      }
      throw new Error(`unexpected request: ${url.pathname}`);
    });

  it.each<Part>(["event", "summary", "count"])("fails as a whole when the %s read fails", async (part) => {
    serveWith(part);

    await expect(api.eventDetail(EVENT_ID)).rejects.toBeInstanceOf(ApiError);
  });
});
