/** @vitest-environment happy-dom */

// Live Attendance's filter bar (client QA Q7) — `EventDetail.tsx`'s grid, filtered by student
// number, name and the card that tapped.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// `GET /attendance` grew three optional query parameters — `studentNumber`, `studentName`,
// `cardUid` — each a fragment match, AND-combined, blank meaning "no filter". A student with no row
// for this event never appears through them, because they narrow rows, never invent them; nothing
// in this screen re-filters that guarantee away, and nothing here re-implements it client-side.
//
// The grid's own read is deliberately separate from `eventDetail()`'s unfiltered one: the tap
// picker's roster is computed from the *unfiltered* rows (`untappedFrom`, in `api.ts`), so an
// operator typing into these boxes must never narrow who the picker still offers (AC11 — pinned
// below, with its own negative control). Every test that inspects requests distinguishes the two
// reads by their query string, not by call order — except the race test, which is white-box about
// mount-time ordering on purpose (see its own comment).
//
// Card fragments are asserted to travel **unnormalized** — case and separators intact — because the
// server does that normalizing itself; a client that also uppercased or stripped separators would be
// a second, possibly-diverging opinion of what counts as the same fragment.
//
// **Fake timers throughout.** `useDebounced`'s 300 ms settle used to be waited out with a real
// `setTimeout(…, 400)`, which is both slower than it needs to be and leaves a 100 ms margin that is
// itself a small, silent assumption about scheduler jitter. `vi.advanceTimersByTimeAsync` advances
// exactly the debounce's own constant and flushes the microtasks a fetch mock settles through, so
// nothing here is timed by how fast the machine running it happens to be. Only `setTimeout`/
// `clearTimeout` are faked (not `Date` or `requestAnimationFrame`) — MUI's transitions and this
// suite's own `Date`-free fixtures have no reason to notice.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import EventDetail from "../src/pages/EventDetail";
import AuthProvider from "../src/components/AuthProvider";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { clearDeviceKey, setDeviceKey } from "../src/deviceKey";
import type { AuthUser } from "../src/types";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["events.read", "events.write"],
};

/** A well-formed device key, so the tap simulator's `POST /attendance/tap` is reachable in tests. */
const DEVICE_KEY_ID = "096b2085a1c3";
const DEVICE_KEY = `eams_dk_${DEVICE_KEY_ID}_${"a".repeat(64)}`;

/** `EventDetail.tsx`'s own `FILTER_SETTLE_MS` — kept in step with it rather than re-guessed, since a
 *  drift between the two would make this file's timer advances silently stop matching the debounce
 *  they exist to settle. */
const FILTER_SETTLE_MS = 300;

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
  present: 3,
  late: 0,
  absent: 0,
  excused: 0,
  unexpected: 0,
  attendanceRate: 75,
};

const AUDIENCE_JSON = {
  eventId: EVENT_ID,
  status: "Frozen",
  isFrozen: false,
  expected: 4,
  groups: [],
  students: [],
  definitions: [],
  advisoryPersonnelCount: 0,
};

const SCANS_JSON = { eventId: EVENT_ID, totalScans: 0, distinctCards: 0, scans: [] };

/** The one student the tap picker can offer — has not tapped, so `untappedFrom` keeps her listed. Her
 *  surname is shared with `ROWS`' "Juan Dela Cruz" on purpose (item 4's hardened tap test needs one
 *  filter that matches both the already-tapped row and the student the tap adds). */
const PICKER_STUDENT = {
  id: "99999999-9999-4999-8999-999999999999",
  studentNumber: "2026-0099",
  fullName: "Nora Cruz",
  firstName: "Nora",
  lastName: "Cruz",
  middleName: null,
  email: null,
  gender: null,
  photoUrl: null,
  course: null,
  yearLevel: null,
  section: null,
  status: "Active",
  cards: [{ id: "card-99", cardUid: "25-99", label: null, isActive: true }],
  classifications: [],
};

/** The tap picker's display text for its one option — `${uid} — ${name}`, per `EventDetail.tsx`. */
const PICKER_OPTION_TEXT = "25-99 — Nora Cruz";

/** A row's fixture-only card fragment, kept out of the JSON the server returns (`AttendanceDto`
 *  carries no `cardUid`) and used only by this file's own stub to decide what `cardUid` matches. */
interface Row {
  id: string;
  studentId: string;
  studentName: string;
  studentNumber: string;
  cardFragment: string;
  status: string;
}

const BASE_ROWS: Row[] = [
  {
    id: "attendance-1",
    studentId: "student-1",
    studentName: "Maria Santos",
    studentNumber: "2026-0001",
    cardFragment: "25-01",
    status: "Present",
  },
  {
    id: "attendance-2",
    studentId: "student-2",
    studentName: "Juan Dela Cruz",
    studentNumber: "2026-0002",
    cardFragment: "25-02",
    status: "Present",
  },
  {
    id: "attendance-3",
    studentId: "student-3",
    studentName: "Ana Reyes",
    studentNumber: "2026-0003",
    cardFragment: "25-03",
    status: "Late",
  },
];

const toAttendanceDto = (row: Row) => ({
  id: row.id,
  eventId: EVENT_ID,
  studentId: row.studentId,
  studentName: row.studentName,
  studentNumber: row.studentNumber,
  checkInAt: "2026-09-01T01:05:00Z",
  checkOutAt: null,
  status: row.status,
  captureMethod: "Rfid",
});

/** Server-side "contains", case-insensitive — a stand-in for `StudentService`'s fragment match. */
const fragmentMatches = (haystack: string, needle: string) =>
  haystack.toLowerCase().includes(needle.toLowerCase());

/**
 * The real refusal text `AttendanceController.List` sends for a card fragment with no letter or
 * digit — copied from `backend/EAMS.Api/Controllers/AttendanceController.cs` rather than invented,
 * so this fixture cannot quietly drift from what the server actually says. `title` is the same
 * literal copy, from the same `ProblemDetailsFactory.CreateProblemDetails` call.
 */
const FRAGMENT_UNUSABLE_TITLE = "That attendance search cannot be run.";
const fragmentUnusableDetail = (fragment: string) =>
  `'${fragment}' contains no letter or digit, so there is no card number to search for. Card serials ` +
  "are stored uppercase with separators stripped and a search is normalized the same way — this one " +
  "normalizes to nothing, which would match every tapped row rather than none. Leave cardUid out to " +
  "list without a card filter.";

/** Every URL this screen asked for, in order — assertions read the request, not the render. */
let asked: string[] = [];

/** Rows currently on the server, mutable so the tap tests can append one after a successful capture. */
let rows: Row[] = [];

/** When set, the next `GET /attendance` carrying this exact `cardUid` is refused with a 400. */
let refuseCardUid: string | undefined;

/**
 * The ordinal of the *n*th `GET /attendance` request overall, across every caller — `detail`'s own
 * unfiltered read (inside `eventDetail()`) is always first at mount, because `EventDetail.tsx` calls
 * `useApiResource` for `detail` before it calls it for `attendance`, and React runs effects in the
 * order their hooks were declared. The grid's own initial (also unfiltered, before anything is
 * typed) read is always second. Two tests below rely on that ordering being stable; both say so.
 */
let attendanceCallSeq = 0;

/** When set, that ordinal's `GET /attendance` fails once with a 500, then the flag clears itself. */
let failAttendanceCallNumber: number | undefined;

/** When set, that ordinal's `GET /attendance` resolves only after `ms` of (fake) time. */
let delayAttendanceCallNumber: { seq: number; ms: number } | undefined;

const delayed = <T,>(value: T, ms: number): Promise<T> =>
  new Promise((resolve) => setTimeout(() => resolve(value), ms));

function serve() {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    asked.push(url);
    const parsed = new URL(url, "http://localhost");
    const path = parsed.pathname;
    const query = parsed.searchParams;

    if (path === `/api/v1/events/${EVENT_ID}/summary`) {
      return Promise.resolve(json(200, SUMMARY_JSON));
    }
    if (path === `/api/v1/events/${EVENT_ID}/attendees`) {
      return Promise.resolve(json(200, AUDIENCE_JSON));
    }
    if (path === `/api/v1/events/${EVENT_ID}/scans`) {
      return Promise.resolve(json(200, SCANS_JSON));
    }
    // The Live Attendance attendance-codes panel reads this on mount; empty is fine for these tests,
    // which are about the filter grid, not the codes.
    if (path === `/api/v1/events/${EVENT_ID}/attendance-codes`) {
      return Promise.resolve(json(200, []));
    }
    if (path === `/api/v1/events/${EVENT_ID}/status` && init?.method === "PATCH") {
      const body = JSON.parse(String(init.body)) as { status: string };
      eventStatus = body.status;
      return Promise.resolve(json(200, eventJson()));
    }
    if (path === `/api/v1/events/${EVENT_ID}`) {
      return Promise.resolve(json(200, eventJson()));
    }
    if (path === "/api/v1/students") {
      return Promise.resolve(
        json(200, { items: [PICKER_STUDENT], page: 1, pageSize: 200, total: 1, hasMore: false }),
      );
    }
    if (path === "/api/v1/attendance") {
      attendanceCallSeq += 1;
      const seq = attendanceCallSeq;

      const cardUid = query.get("cardUid");
      if (cardUid !== null && cardUid === refuseCardUid) {
        return Promise.resolve(
          json(400, {
            type: "https://tools.ietf.org/html/rfc7231#section-6.5.1",
            title: FRAGMENT_UNUSABLE_TITLE,
            status: 400,
            detail: fragmentUnusableDetail(cardUid),
            code: "FragmentUnusable",
          }),
        );
      }

      if (failAttendanceCallNumber === seq) {
        failAttendanceCallNumber = undefined;
        return Promise.resolve(
          json(500, { title: "The database connection pool is exhausted.", status: 500 }),
        );
      }

      const studentNumber = query.get("studentNumber");
      const studentName = query.get("studentName");
      // A blank or whitespace-only fragment is what `IsNullOrWhiteSpace` treats as "not provided" on
      // the real server (`AttendanceController.List`) — it does not narrow, rather than narrowing to
      // nothing. Only `cardUid` can arrive that way; the other two are trimmed before they ever leave
      // `EventDetail.tsx`.
      const matched = rows.filter((row) => {
        if (studentNumber && !fragmentMatches(row.studentNumber, studentNumber)) return false;
        if (studentName && !fragmentMatches(row.studentName, studentName)) return false;
        if (cardUid && cardUid.trim() !== "" && !fragmentMatches(row.cardFragment, cardUid)) {
          return false;
        }
        return true;
      });
      const body = {
        items: matched.map(toAttendanceDto),
        page: 1,
        pageSize: 200,
        total: matched.length,
        hasMore: false,
      };

      if (delayAttendanceCallNumber?.seq === seq) {
        const { ms } = delayAttendanceCallNumber;
        delayAttendanceCallNumber = undefined;
        return delayed(json(200, body), ms);
      }
      return Promise.resolve(json(200, body));
    }
    if (path === "/api/v1/attendance/tap" && init?.method === "POST") {
      const body = JSON.parse(String(init.body)) as { cardUid: string };
      const student = PICKER_STUDENT;
      rows = [
        ...rows,
        {
          id: "attendance-tap-1",
          studentId: student.id,
          studentName: student.fullName,
          studentNumber: student.studentNumber,
          cardFragment: body.cardUid,
          status: "Present",
        },
      ];
      return Promise.resolve(json(200, { success: true, message: "Recorded.", code: "Recorded" }));
    }

    throw new Error(`the event screen asked for something this stub does not serve: ${url}`);
  });
}

async function show() {
  const rendered = render(
    <MemoryRouter initialEntries={[`/events/${EVENT_ID}`]}>
      <AuthProvider>
        <Routes>
          <Route path="/events/:id" element={<EventDetail />} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
  await act(async () => {});
  return rendered;
}

const pageText = () => document.body.textContent ?? "";

/** Every `GET /attendance` request, most recent last. */
const attendanceRequests = () =>
  asked.filter((u) => new URL(u, "http://localhost").pathname === "/api/v1/attendance");

const attendanceRequestsWith = (param: string, value: string) =>
  attendanceRequests().filter((u) => new URL(u, "http://localhost").searchParams.get(param) === value);

const lastAttendanceQuery = () => {
  const list = attendanceRequests();
  return new URL(list[list.length - 1], "http://localhost").searchParams;
};

/** Lets the debounce settle, deterministically — see the file banner for why fake timers. */
const settle = () => act(async () => {
  await vi.advanceTimersByTimeAsync(FILTER_SETTLE_MS);
});

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  asked = [];
  rows = BASE_ROWS.map((r) => ({ ...r }));
  refuseCardUid = undefined;
  eventStatus = "Open";
  attendanceCallSeq = 0;
  failAttendanceCallNumber = undefined;
  delayAttendanceCallNumber = undefined;
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
  clearDeviceKey();
});

describe("Live Attendance filters", () => {
  it("filters by student number", async () => {
    await show();
    const before = attendanceRequests().length;

    fireEvent.change(screen.getByLabelText(/student number/i), { target: { value: "2026-0002" } });
    await settle();

    expect(attendanceRequests().length).toBe(before + 1);
    expect(lastAttendanceQuery().get("studentNumber")).toBe("2026-0002");
    expect(screen.getByText("Juan Dela Cruz")).toBeTruthy();
    expect(screen.queryByText("Maria Santos")).toBeFalsy();
  });

  it("filters by name", async () => {
    await show();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Reyes" } });
    await settle();

    expect(lastAttendanceQuery().get("studentName")).toBe("Reyes");
    expect(screen.getByText("Ana Reyes")).toBeTruthy();
    expect(screen.queryByText("Maria Santos")).toBeFalsy();
  });

  it("sends the card fragment exactly as typed, not normalized", async () => {
    await show();

    // A separator the server, not this client, is responsible for normalizing, plus edge whitespace —
    // the one thing an eager `.trim()` would silently remove without failing a same-value test.
    fireEvent.change(screen.getByLabelText(/card serial/i), { target: { value: " 25:01 " } });
    await settle();

    expect(lastAttendanceQuery().get("cardUid")).toBe(" 25:01 ");
  });

  it("combines all three filters into one request", async () => {
    await show();

    fireEvent.change(screen.getByLabelText(/student number/i), { target: { value: "2026-0001" } });
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Maria" } });
    fireEvent.change(screen.getByLabelText(/card serial/i), { target: { value: "25-01" } });
    await settle();

    const q = lastAttendanceQuery();
    expect(q.get("studentNumber")).toBe("2026-0001");
    expect(q.get("studentName")).toBe("Maria");
    expect(q.get("cardUid")).toBe("25-01");
  });

  it("clearing all filters restores the full list", async () => {
    await show();

    fireEvent.change(screen.getByLabelText(/student number/i), { target: { value: "2026-0002" } });
    await settle();
    expect(screen.queryByText("Maria Santos")).toBeFalsy();

    fireEvent.click(screen.getByRole("button", { name: /^clear filters$/i }));
    await settle();

    expect(lastAttendanceQuery().has("studentNumber")).toBe(false);
    expect(screen.getByText("Maria Santos")).toBeTruthy();
    expect(screen.getByText("Juan Dela Cruz")).toBeTruthy();
    expect(screen.getByText("Ana Reyes")).toBeTruthy();
  });

  it("moves focus to the student number field after Clear filters, never to the page body", async () => {
    await show();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Reyes" } });
    await settle();

    fireEvent.click(screen.getByRole("button", { name: /^clear filters$/i }));
    await settle();

    expect(document.activeElement).toBe(screen.getByLabelText(/student number/i));
    expect(document.activeElement).not.toBe(document.body);
  });

  it("tells a no-match filter apart from an event with no taps at all", async () => {
    // First: this event has taps, and a filter that matches none of them.
    await show();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Nobody Here" } });
    await settle();

    expect(pageText()).toMatch(/No taps match these filters/);
    expect(pageText()).not.toMatch(/No taps recorded for this event yet/);
    cleanup();

    // Second: a different render, an event with no taps recorded and no filter typed at all.
    asked = [];
    rows = [];
    await show();

    expect(pageText()).toMatch(/No taps recorded for this event yet/);
    expect(pageText()).not.toMatch(/No taps match these filters/);
  });

  it("does not count a whitespace-only card fragment toward the header's filtered total", async () => {
    await show();

    fireEvent.change(screen.getByLabelText(/card serial/i), { target: { value: "   " } });
    await settle();

    // Sent through exactly as typed — the client still does not normalize it...
    expect(lastAttendanceQuery().get("cardUid")).toBe("   ");
    // ...but the server treats it as no filter at all, so the header must not claim a narrower
    // count for it, and none of the three rows are actually narrowed away.
    expect(pageText()).not.toMatch(/of 3\)/);
    expect(screen.getByText("Maria Santos")).toBeTruthy();
    expect(screen.getByText("Juan Dela Cruz")).toBeTruthy();
    expect(screen.getByText("Ana Reyes")).toBeTruthy();
  });

  it("shows the server's message when a filter is refused, rather than inventing one", async () => {
    refuseCardUid = "??";
    await show();

    fireEvent.change(screen.getByLabelText(/card serial/i), { target: { value: "??" } });
    await settle();

    // The real sentence `AttendanceController.List` sends, not a paraphrase this build invented.
    expect(pageText()).toMatch(/contains no letter or digit/);
    expect(pageText()).not.toMatch(/Unexpected error/);

    // Retry is unconditional (item 1) and Clear Filters stays offered because a filter is set — one
    // Clear Filters in the toolbar, a second inside the alert itself.
    expect(screen.getByRole("button", { name: /^retry$/i })).toBeTruthy();
    expect(screen.getAllByRole("button", { name: /clear filters/i })).toHaveLength(2);
  });

  it("offers Retry when the grid's read fails with no filter, and Retry recovers", async () => {
    // The grid's own initial (unfiltered) read is always the second `GET /attendance` overall — see
    // `attendanceCallSeq`'s own comment for why that ordering is stable.
    failAttendanceCallNumber = 2;
    await show();

    expect(pageText()).toMatch(/Live attendance could not be loaded/);
    // No filter is set, so the alert offers no Clear Filters of its own — the only one on screen is
    // the toolbar's, disabled because both boxes are empty, not the alert's second action.
    const clearButtons = screen.getAllByRole("button", { name: /clear filters/i });
    expect(clearButtons).toHaveLength(1);
    expect((clearButtons[0] as HTMLButtonElement).disabled).toBe(true);
    const retry = screen.getByRole("button", { name: /^retry$/i });

    fireEvent.click(retry);
    await act(async () => {});

    expect(pageText()).not.toMatch(/could not be loaded/);
    expect(screen.getByText("Maria Santos")).toBeTruthy();
  });

  it("gives every filter input an accessible label", async () => {
    await show();

    expect(screen.getByLabelText(/student number/i)).toBeTruthy();
    expect(screen.getByLabelText(/^name$/i)).toBeTruthy();
    expect(screen.getByLabelText(/card serial/i)).toBeTruthy();
  });

  it("filter inputs stop at the server's maximum length", async () => {
    await show();

    // `AttendanceListSearch.StudentNumberMaxLength` / `.StudentNameMaxLength` / `.CardUidMaxLength` in
    // `EAMS.Application/Dtos/AttendanceListSearch.cs`, restated in `docs/api/openapi.json`'s
    // `GET /attendance` parameters as `maxLength: 50` / `302` / `256`.
    expect((screen.getByLabelText(/student number/i) as HTMLInputElement).maxLength).toBe(50);
    expect((screen.getByLabelText(/^name$/i) as HTMLInputElement).maxLength).toBe(302);
    expect((screen.getByLabelText(/card serial/i) as HTMLInputElement).maxLength).toBe(256);
  });

  it("does not spend a request per keystroke", async () => {
    await show();
    const before = attendanceRequests().length;

    const field = screen.getByLabelText(/^name$/i);
    for (const value of ["A", "An", "Ana"]) {
      fireEvent.change(field, { target: { value } });
    }
    await settle();

    expect(attendanceRequests().length - before).toBe(1);
  });

  it("a filter typed while the initial load is still in flight settles on the filtered result", async () => {
    // The grid's own initial (unfiltered) read — the second `GET /attendance` overall — is held open.
    // `detail`'s own unfiltered read (the first) resolves immediately, so the filter bar is on screen
    // to type into well before this one ever answers.
    delayAttendanceCallNumber = { seq: 2, ms: 5 * FILTER_SETTLE_MS };
    await show();

    expect(screen.getByLabelText(/^name$/i)).toBeTruthy();

    // Typed and settled before the delayed, stale, unfiltered read has come back at all.
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Cruz" } });
    await settle();
    expect(lastAttendanceQuery().get("studentName")).toBe("Cruz");
    expect(screen.getByText("Juan Dela Cruz")).toBeTruthy();
    expect(screen.queryByText("Maria Santos")).toBeFalsy();

    // Now let the stale unfiltered read actually land. `useApiResource` cleans up the effect that
    // started it the moment `deps` changed underneath it (marking that attempt's own `live` flag
    // false), so its arrival here must change nothing on screen.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5 * FILTER_SETTLE_MS);
    });

    expect(lastAttendanceQuery().get("studentName")).toBe("Cruz");
    expect(screen.getByText("Juan Dela Cruz")).toBeTruthy();
    expect(screen.queryByText("Maria Santos")).toBeFalsy();
    expect(screen.queryByText("Ana Reyes")).toBeFalsy();
  });

  it("a typed filter never narrows the tap picker", async () => {
    await show();
    expect(screen.getByText(PICKER_OPTION_TEXT)).toBeTruthy();

    // A name fragment that matches nobody who has tapped.
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Nobody Here" } });
    await settle();

    expect(pageText()).toMatch(/No taps match these filters/);
    // The picker is computed from the unfiltered roster and must not have narrowed with the grid.
    expect(screen.getByText(PICKER_OPTION_TEXT)).toBeTruthy();
    expect(screen.getByRole("button", { name: /^tap$/i })).toBeTruthy();
  });

  it("the grid catches up with the tapped student while the filter that matched her is still applied", async () => {
    setDeviceKey(DEVICE_KEY);
    await show();

    // "Cruz" matches the already-tapped Juan Dela Cruz *and* the untapped Nora Cruz the tap is about
    // to add — the one filter that can actually prove the grid caught up, rather than merely that a
    // request went out.
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Cruz" } });
    await settle();
    expect(screen.getByText("Juan Dela Cruz")).toBeTruthy();
    expect(screen.queryByText("Nora Cruz")).toBeFalsy();
    const cruzRequestsBeforeTap = attendanceRequestsWith("studentName", "Cruz").length;

    fireEvent.click(screen.getByRole("button", { name: /^tap$/i }));
    await act(async () => {});

    // Counted only among requests that actually carried the typed filter — not every `GET /attendance`
    // the tap causes, since `simulateTap` also reloads `detail`'s unfiltered read.
    expect(attendanceRequestsWith("studentName", "Cruz").length).toBeGreaterThan(
      cruzRequestsBeforeTap,
    );
    expect(screen.getByText("Nora Cruz")).toBeTruthy();
    expect(screen.getByText("Juan Dela Cruz")).toBeTruthy();
    expect((screen.getByLabelText(/^name$/i) as HTMLInputElement).value).toBe("Cruz");
  });

  it("closing the event reloads the filtered grid", async () => {
    await show();

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "Cruz" } });
    await settle();
    const cruzRequestsBeforeClose = attendanceRequestsWith("studentName", "Cruz").length;

    fireEvent.click(screen.getByRole("button", { name: /^close event$/i }));
    await act(async () => {});
    fireEvent.click(screen.getByRole("button", { name: /^close this event$/i }));
    await act(async () => {});

    expect(attendanceRequestsWith("studentName", "Cruz").length).toBeGreaterThan(
      cruzRequestsBeforeClose,
    );
    expect((screen.getByLabelText(/^name$/i) as HTMLInputElement).value).toBe("Cruz");
  });
});
