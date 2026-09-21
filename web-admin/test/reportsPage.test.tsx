/** @vitest-environment happy-dom */

// The Reports screen — Technical Plan §6.7, client QA Q15/Q16: a summary of one event and a summary
// of several, admin-only.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// The nav entry and the route are gated on `reports.read` — pinned against the real `<App />`, the
// way `studentsRouteAndNaming.test.tsx` pins `/students`, rather than against a route table this file
// invents itself.
//
// The page itself is rendered directly (`<Reports />` under a bare `MemoryRouter`, session begun on
// the store) — `Reports.tsx` reaches no `useSignedInUser`/`PermissionGuard` of its own, so it needs no
// `<AuthProvider>`. `EventDetail.tsx` is no longer the same: it now reads `useSignedInUser()` itself
// (the certificates toggle's permission gate), so `liveAttendanceFilters.test.tsx` wraps it in one.
//
// **This page always calls the multi-event route, never the single-event one** — the component's own
// module note explains why (one error-handling path for both selection sizes), and `api.ts` no longer
// even exposes the single-event read. So every content test below stubs `GET /reports/events/summary`
// and never `GET /reports/event/{id}/summary`; a stub for the single-event route is deliberately
// absent — if the page ever called it, the fetch stub's catch-all throw would fail every test here
// immediately, which is the negative control for that design decision without a dedicated test for it.
//
// The multi-event totals fixture is chosen to **disagree with a naive client sum** on purpose: each
// row's own `expected`/`attended` is 6/5 (83.3%), so the rows sum to 12/10 (also 83.3%) — and the
// stub's `totals` instead answers 20/9 (45.0%), a value neither row nor an honest sum of the two rows
// produces. If `ReportResult` ever started deriving the totals row from `report.events` instead of
// rendering `report.totals` verbatim, the assertion on 45.0% would fail while 83.3% appeared instead —
// the exact defect the contract's own module note (`EventReportTotalsDto` in `types.ts`) says the
// totals must never invite.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import App from "../src/App";
import AuthProvider from "../src/components/AuthProvider";
import Reports from "../src/pages/Reports";
import { PERMISSIONS } from "../src/permissions";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { MAX_REPORT_EVENTS } from "../src/types";
import type { AuthUser } from "../src/types";

const REPORTS_LABEL = "Reports";
const REPORTS_PATH = "/reports";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "admin@usa.edu.ph",
  fullName: "School Admin",
  permissions: [PERMISSIONS.reportsRead],
};

const everyPermissionExcept = (code: string): readonly string[] =>
  Object.values(PERMISSIONS).filter((p) => p !== code);

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

// ---------------------------------------------------------------------------------------------
// Three events, fixed enough to assert on by name and by number
// ---------------------------------------------------------------------------------------------

const EVENT_A = "11111111-1111-4111-8111-111111111111";
const EVENT_B = "22222222-2222-4222-8222-222222222222";
const EVENT_C = "33333333-3333-4333-8333-333333333333";

const eventDto = (id: string, name: string, startAt: string) => ({
  id,
  name,
  description: null,
  location: "Gym",
  requireRegistration: false,
  startAt,
  endAt: startAt,
  attendanceMode: "Single",
  graceMinutes: 15,
  status: "Open",
  issuesCertificates: false,
});

const FRESHMAN = eventDto(EVENT_A, "Freshman Orientation", "2026-09-01T01:00:00Z");
const FOUNDERS = eventDto(EVENT_B, "Founders Day", "2026-09-02T01:00:00Z");
const AWARDS = eventDto(EVENT_C, "Awards Night", "2026-09-03T01:00:00Z");

/** `n` distinct, individually-selectable events — for the 50-event cap test. */
const manyEventDtos = (n: number) =>
  Array.from({ length: n }, (_, i) =>
    eventDto(
      `44444444-4444-4444-8444-${(444444444444 + i).toString().padStart(12, "0")}`,
      `Bulk Event ${i}`,
      "2026-09-01T01:00:00Z",
    ),
  );

const eventsPage = (items: unknown[]) => ({
  items,
  page: 1,
  pageSize: 200,
  total: items.length,
  hasMore: false,
});

const reportRow = (eventId: string, eventName: string) => ({
  eventId,
  eventName,
  status: "Open",
  startAt: "2026-09-01T01:00:00Z",
  expected: 6,
  attended: 5,
  present: 4,
  late: 1,
  absent: 1,
  excused: 0,
  unexpected: 0,
  attendanceRate: 83.3,
});

/** Deliberately NOT the sum of the two rows above, and not either row's own rate — see the banner. */
const MISMATCHED_TOTALS = {
  eventCount: 2,
  expected: 20,
  attended: 9,
  present: 7,
  late: 2,
  absent: 3,
  excused: 0,
  unexpected: 0,
  attendanceRate: 45.0,
};

const notFound = (missingEventIds: unknown) => ({
  type: "about:blank",
  title: "Some of the picked events were not found.",
  status: 404,
  detail: "An event was not found.",
  code: "EventNotFound",
  missingEventIds,
});

let events: unknown[] = [FRESHMAN];
/** A single canned response for `GET /reports/events/summary`, used by most tests. */
let multiEventStatus: number | undefined;
let multiEventBody: unknown;
/** Consumed in order, ahead of the single canned response — for tests that need a second request to
 *  answer differently from the first (the 404-recovery test). */
let multiEventQueue: Array<{ status: number; body: unknown }> = [];
/** Every `GET /reports/events/summary` query string asked for, most recent last. */
let reportRequests: string[] = [];
/** How many times `GET /events` has been asked for — the reload the 404 recovery triggers shows up
 *  here as a second request beyond the mount-time one. */
let eventsRequestCount = 0;

function serve() {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(input);
    const parsed = new URL(url, "http://localhost");

    if (parsed.pathname === "/api/v1/events") {
      eventsRequestCount += 1;
      return Promise.resolve(json(200, eventsPage(events)));
    }

    if (parsed.pathname === "/api/v1/reports/events/summary") {
      reportRequests.push(parsed.search);
      if (multiEventQueue.length > 0) {
        const next = multiEventQueue.shift();
        if (next !== undefined) return Promise.resolve(json(next.status, next.body));
      }
      if (multiEventStatus !== undefined) {
        return Promise.resolve(json(multiEventStatus, multiEventBody));
      }
      throw new Error("no /reports/events/summary response was configured for this test");
    }

    // The single-event route is deliberately never stubbed — see the file banner.
    throw new Error(`Reports asked for something this stub does not serve: ${url}`);
  });
}

async function showReportsPage() {
  const rendered = render(
    <MemoryRouter initialEntries={["/reports"]}>
      <Routes>
        <Route path="/reports" element={<Reports />} />
      </Routes>
    </MemoryRouter>,
  );
  await act(async () => {});
  return rendered;
}

function showApp(at: string) {
  return render(
    <MemoryRouter initialEntries={[at]}>
      <AuthProvider>
        <App />
      </AuthProvider>
    </MemoryRouter>,
  );
}

const mainNav = () => screen.getByRole("navigation", { name: "Main" });

const clickCheckbox = (name: RegExp) => fireEvent.click(screen.getByRole("checkbox", { name }));
const clickRun = () => fireEvent.click(screen.getByRole("button", { name: /run report/i }));

beforeEach(() => {
  events = [FRESHMAN];
  multiEventStatus = undefined;
  multiEventBody = undefined;
  multiEventQueue = [];
  reportRequests = [];
  eventsRequestCount = 0;
  resetSessionForTests();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("gating", () => {
  it("shows the Reports nav entry only with reports.read", () => {
    vi.stubGlobal("fetch", () => new Promise<Response>(() => {})); // never settles — chrome-only assertions
    beginSession("access-token-1", { ...OPERATOR, permissions: [PERMISSIONS.reportsRead] });

    showApp(REPORTS_PATH);

    const entry = within(mainNav()).getByRole("link", { name: REPORTS_LABEL });
    expect(entry.getAttribute("href")).toBe(REPORTS_PATH);
  });

  it("hides the Reports nav entry, and refuses the route, without reports.read", () => {
    vi.stubGlobal("fetch", () => new Promise<Response>(() => {}));
    beginSession("access-token-1", { ...OPERATOR, permissions: everyPermissionExcept(PERMISSIONS.reportsRead) });

    showApp(REPORTS_PATH);

    expect(within(mainNav()).queryByRole("link", { name: REPORTS_LABEL })).toBeNull();

    const denial = screen.getByRole("alert");
    expect(denial.textContent).toContain(PERMISSIONS.reportsRead);
    expect(screen.queryByRole("heading", { name: REPORTS_LABEL })).toBeNull();
  });
});

describe("running a report", () => {
  it("shows a single event's summary", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN];
    multiEventStatus = 200;
    multiEventBody = { events: [reportRow(EVENT_A, "Freshman Orientation")], totals: MISMATCHED_TOTALS };
    serve();

    await showReportsPage();

    clickCheckbox(/Freshman Orientation/i);
    clickRun();
    await act(async () => {});

    expect(screen.getByText("Freshman Orientation")).toBeTruthy();
    expect(screen.getByText("83.3%")).toBeTruthy();
    // One event selected: no combined-totals row, per the page's own module note.
    expect(screen.queryByText(/Combined across/i)).toBeNull();
    expect(reportRequests).toEqual([`?eventId=${EVENT_A}`]);
  });

  it("shows several events as one row each, plus the server's pooled totals — not a client sum", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN, FOUNDERS];
    multiEventStatus = 200;
    multiEventBody = {
      events: [reportRow(EVENT_A, "Freshman Orientation"), reportRow(EVENT_B, "Founders Day")],
      totals: MISMATCHED_TOTALS,
    };
    serve();

    await showReportsPage();

    clickCheckbox(/Freshman Orientation/i);
    clickCheckbox(/Founders Day/i);
    clickRun();
    await act(async () => {});

    // One row per event.
    expect(screen.getByText("Freshman Orientation")).toBeTruthy();
    expect(screen.getByText("Founders Day")).toBeTruthy();
    expect(screen.getAllByText("83.3%")).toHaveLength(2);

    // The totals row, labelled as combined, and reading exactly the server's numbers — 45.0%, which
    // is neither row's own rate (83.3%) nor what summing the two rows' expected/attended (12/10 →
    // 83.3%) would produce. It can only have come from `totals` verbatim.
    expect(screen.getByText(/Combined across 2 selected events/i)).toBeTruthy();
    expect(screen.getByText("20")).toBeTruthy(); // totals.expected
    expect(screen.getByText("9")).toBeTruthy(); // totals.attended
    expect(screen.getByText("45.0%")).toBeTruthy();

    expect(reportRequests).toEqual([`?eventId=${EVENT_A}&eventId=${EVENT_B}`]);
  });

  it("disables Run with no event selected", async () => {
    beginSession("access-token-1", OPERATOR);
    serve();

    await showReportsPage();

    const runButton = screen.getByRole("button", { name: /run report/i }) as HTMLButtonElement;
    expect(runButton.disabled).toBe(true);
  });

  it("stops selecting past the maximum, with a visible reason", async () => {
    beginSession("access-token-1", OPERATOR);
    events = manyEventDtos(MAX_REPORT_EVENTS + 1);
    serve();

    await showReportsPage();

    const boxes = screen.getAllByRole("checkbox") as HTMLInputElement[];
    expect(boxes.length).toBe(MAX_REPORT_EVENTS + 1);

    for (let i = 0; i < MAX_REPORT_EVENTS; i++) fireEvent.click(boxes[i]);

    expect(screen.queryByRole("status")).toBeNull();
    expect(boxes[MAX_REPORT_EVENTS - 1].checked).toBe(true);

    fireEvent.click(boxes[MAX_REPORT_EVENTS]);

    expect(boxes[MAX_REPORT_EVENTS].checked).toBe(false);
    const reason = screen.getByRole("status");
    expect(reason.textContent).toContain(String(MAX_REPORT_EVENTS));
  });

  it("gives every control an accessible label", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN];
    serve();

    await showReportsPage();

    expect(screen.getByLabelText(/filter events by name/i)).toBeTruthy();
    expect(screen.getByRole("checkbox", { name: /Freshman Orientation/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /run report/i })).toBeTruthy();
  });
});

describe("a stale result does not outlive the selection it describes", () => {
  it("changing the selection clears the previous result", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN, FOUNDERS];
    multiEventStatus = 200;
    multiEventBody = { events: [reportRow(EVENT_A, "Freshman Orientation")], totals: MISMATCHED_TOTALS };
    serve();

    await showReportsPage();

    clickCheckbox(/Freshman Orientation/i);
    clickRun();
    await act(async () => {});
    expect(screen.getByRole("table")).toBeTruthy();

    // The selection changes — a second event is added — and the table computed for the old,
    // one-event selection must not keep looking authoritative for this new, two-event one.
    clickCheckbox(/Founders Day/i);

    expect(screen.queryByRole("table")).toBeNull();
  });

  it("deselecting every event clears the previous result", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN];
    multiEventStatus = 200;
    multiEventBody = { events: [reportRow(EVENT_A, "Freshman Orientation")], totals: MISMATCHED_TOTALS };
    serve();

    await showReportsPage();

    clickCheckbox(/Freshman Orientation/i);
    clickRun();
    await act(async () => {});
    expect(screen.getByRole("table")).toBeTruthy();

    clickCheckbox(/Freshman Orientation/i); // unchecks it — nothing selected now

    expect(screen.queryByRole("table")).toBeNull();
    const runButton = screen.getByRole("button", { name: /run report/i }) as HTMLButtonElement;
    expect(runButton.disabled).toBe(true);
  });
});

describe("refusals rendered by status, not guessed at", () => {
  it("renders a 400 SelectionTooLarge refusal with the server's message, not the missing-events branch", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN];
    multiEventStatus = 400;
    multiEventBody = {
      type: "about:blank",
      title: "Too many events were picked.",
      status: 400,
      detail: "At most 50 events may be reported on at once.",
      code: "SelectionTooLarge",
    };
    serve();

    await showReportsPage();
    clickCheckbox(/Freshman Orientation/i);
    clickRun();
    await act(async () => {});

    const alert = screen.getByRole("alert");
    expect(alert.textContent).toContain("At most 50 events may be reported on at once.");
    expect(screen.queryByRole("button", { name: /remove the missing events/i })).toBeNull();
    expect(alert.textContent).not.toContain("could not be found");
  });

  it("renders a 400 SelectionEmpty refusal with the server's message, not the missing-events branch", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN];
    multiEventStatus = 400;
    multiEventBody = {
      type: "about:blank",
      title: "No events were picked.",
      status: 400,
      detail: "At least one event must be selected.",
      code: "SelectionEmpty",
    };
    serve();

    await showReportsPage();
    clickCheckbox(/Freshman Orientation/i);
    clickRun();
    await act(async () => {});

    const alert = screen.getByRole("alert");
    expect(alert.textContent).toContain("At least one event must be selected.");
    expect(screen.queryByRole("button", { name: /remove the missing events/i })).toBeNull();
    expect(alert.textContent).not.toContain("could not be found");
  });

  it("renders a 403 refusal without offering a pointless Retry", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN];
    multiEventStatus = 403;
    multiEventBody = {
      type: "about:blank",
      title: "Forbidden",
      status: 403,
      detail: "Your account is not permitted to do this.",
    };
    serve();

    await showReportsPage();
    clickCheckbox(/Freshman Orientation/i);
    clickRun();
    await act(async () => {});

    // `advise()`'s own 403 arm: `retryable: false`, and this exact sentence — a signed-in account
    // that lacks the permission, for whom "sign in again" is not the fix.
    const alert = screen.getByRole("alert");
    expect(alert.textContent).toContain(
      "Your account is not permitted to do this, so the API refused it and did not apply it.",
    );
    expect(within(alert).queryByRole("button", { name: /retry/i })).toBeNull();
  });
});

describe("the name filter narrows what is offered, never what is selected", () => {
  it("an event hidden by the name filter is still counted and still sent", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN, FOUNDERS];
    multiEventStatus = 200;
    multiEventBody = { events: [reportRow(EVENT_A, "Freshman Orientation")], totals: MISMATCHED_TOTALS };
    serve();

    await showReportsPage();

    clickCheckbox(/Freshman Orientation/i);
    expect(screen.getByText(/1 of 50 selected/i)).toBeTruthy();

    fireEvent.change(screen.getByLabelText(/filter events by name/i), {
      target: { value: "Founders" },
    });

    // The checkbox is gone from the list — filtered out — but the count and the eventual request
    // still carry it.
    expect(screen.queryByRole("checkbox", { name: /Freshman Orientation/i })).toBeNull();
    expect(screen.getByText(/1 of 50 selected/i)).toBeTruthy();

    clickRun();
    await act(async () => {});

    expect(reportRequests).toEqual([`?eventId=${EVENT_A}`]);
  });

  it("the name filter hides non-matching events", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN, FOUNDERS];
    serve();

    await showReportsPage();

    fireEvent.change(screen.getByLabelText(/filter events by name/i), {
      target: { value: "Founders" },
    });

    expect(screen.queryByRole("checkbox", { name: /Freshman Orientation/i })).toBeNull();
    expect(screen.getByRole("checkbox", { name: /Founders Day/i })).toBeTruthy();
  });

  it("the name filter shows its no-match message", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN];
    serve();

    await showReportsPage();

    fireEvent.change(screen.getByLabelText(/filter events by name/i), {
      target: { value: "no such event anywhere" },
    });

    expect(screen.getByText(/No event name matches/i)).toBeTruthy();
  });
});

describe("recovering from a 404", () => {
  it("names the missing events on a 404, by name rather than by id", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN, FOUNDERS];
    multiEventStatus = 404;
    multiEventBody = notFound([EVENT_B]);
    serve();

    await showReportsPage();

    clickCheckbox(/Freshman Orientation/i);
    clickCheckbox(/Founders Day/i);
    clickRun();
    await act(async () => {});

    const alert = screen.getByRole("alert");
    expect(alert.textContent).toContain("Founders Day");
    expect(alert.textContent).not.toContain("Freshman Orientation");
    expect(alert.textContent).not.toContain(EVENT_B);
  });

  it("remove-the-missing-events keeps the rest of the selection and runs again", async () => {
    beginSession("access-token-1", OPERATOR);
    events = [FRESHMAN, FOUNDERS, AWARDS];
    multiEventQueue = [
      { status: 404, body: notFound([EVENT_B]) },
      {
        status: 200,
        body: {
          events: [reportRow(EVENT_A, "Freshman Orientation"), reportRow(EVENT_C, "Awards Night")],
          totals: MISMATCHED_TOTALS,
        },
      },
    ];
    serve();

    await showReportsPage();

    clickCheckbox(/Freshman Orientation/i);
    clickCheckbox(/Founders Day/i);
    clickCheckbox(/Awards Night/i);
    clickRun();
    await act(async () => {});

    expect(screen.getByRole("alert").textContent).toContain("Founders Day");
    const eventsReadsBefore = eventsRequestCount;

    fireEvent.click(screen.getByRole("button", { name: /remove the missing events and run again/i }));
    await act(async () => {});

    // The event list was reloaded, and the rerun kept the rest of the selection — Founders Day
    // dropped, Freshman Orientation and Awards Night both still sent, in that order.
    expect(eventsRequestCount).toBeGreaterThan(eventsReadsBefore);
    expect(reportRequests).toEqual([
      `?eventId=${EVENT_A}&eventId=${EVENT_B}&eventId=${EVENT_C}`,
      `?eventId=${EVENT_A}&eventId=${EVENT_C}`,
    ]);
    expect(screen.getByText("Freshman Orientation")).toBeTruthy();
    expect(screen.getByText("Awards Night")).toBeTruthy();
    expect(screen.queryByText("Founders Day")).toBeNull();
  });
});

describe("an off-contract missingEventIds", () => {
  it("falls back to the generic alert (non-array, or a non-string item)", async () => {
    const badShapes: unknown[] = ["not-an-array", [123, 456]];

    for (const missingEventIds of badShapes) {
      beginSession("access-token-1", OPERATOR);
      events = [FRESHMAN];
      multiEventStatus = 404;
      multiEventBody = notFound(missingEventIds);
      reportRequests = [];
      serve();

      await showReportsPage();
      clickCheckbox(/Freshman Orientation/i);
      clickRun();
      await act(async () => {});

      const alert = screen.getByRole("alert");
      // The generic branch — `describeApiError`/`advise()` — not the "could not be found" one, and
      // no recovery button, because there is no trustworthy list of ids to prune.
      expect(alert.textContent).toContain("An event was not found.");
      expect(alert.textContent).not.toContain("could not be found");
      expect(screen.queryByRole("button", { name: /remove the missing events/i })).toBeNull();

      cleanup();
      vi.unstubAllGlobals();
      resetSessionForTests();
    }
  });
});
