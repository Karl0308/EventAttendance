/** @vitest-environment happy-dom */

// RoleViewRestrictions, the event screen. `viewerReadOnly.test.tsx` covers the list pages; this covers
// `EventDetail` and the three audience panels it hosts, which carry the event's write controls.
//
// Each case is a pair: a Viewer (the seeded read-only permissions) sees none of the write controls, and
// a token holding the write permission still sees them. The second half is what stops the first passing
// because a panel simply failed to render.
//
// A convenience, not a control: the server refuses every one of these writes without the permission too.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import type { ReactElement } from "react";

import AuthProvider from "../src/components/AuthProvider";
import EventAudienceDefinitionsPanel from "../src/components/EventAudienceDefinitionsPanel";
import EventAudiencePanel, { type AudienceRead } from "../src/components/EventAudiencePanel";
import EventPreRegistrationSection from "../src/components/EventPreRegistrationSection";
import EventDetail from "../src/pages/EventDetail";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { PERMISSIONS } from "../src/permissions";
import type { AuthUser, EventAudience } from "../src/types";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";

const VIEWER = [
  PERMISSIONS.studentsRead,
  PERMISSIONS.eventsRead,
  PERMISSIONS.attendanceRead,
  PERMISSIONS.academicRead,
];
const WRITER = [...VIEWER, PERMISSIONS.eventsWrite, PERMISSIONS.attendanceWrite];

const userWith = (permissions: readonly string[]): AuthUser => ({
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "someone@usa.edu.ph",
  fullName: "Some One",
  permissions: [...permissions],
});

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

// ------------------------------------------------------------------------------------ the audience

const AUDIENCE: EventAudience = {
  eventId: EVENT_ID,
  status: "Draft",
  isFrozen: false,
  expected: 40,
  expectedStudents: 40,
  expectedPersonnel: 0,
  groups: [
    {
      studentGroupId: "g-1",
      name: "BSIT 1-A",
      type: "Section",
      sourceType: "Section",
      memberCount: 40,
    },
  ],
  students: [{ studentId: "st-1", studentNumber: "2026-0001", fullName: "Cruz, Ana" }],
  definitions: [
    {
      audienceDefinitionId: "d-1",
      name: "All Freshmen",
      audienceType: "Program",
      studentCount: 40,
      personnelCount: 0,
      isActive: true,
    },
  ],
  expectedSource: "PreRegistration",
  expectedPersonnelSource: "Audience",
  preRegistration: {
    linkedSessions: [
      {
        preRegistrationSessionId: "ses-1",
        name: "Orientation pre-reg",
        preRegisteredStudentCount: 30,
        advisoryPersonnelCount: 4,
      },
    ],
    totalPreRegisteredStudentCount: 30,
    totalAdvisoryPersonnelCount: 4,
  },
};

const READ: AudienceRead = { status: "ready", audience: AUDIENCE };

const NO_OP = () => {};

const panels = (): ReactElement => (
  <>
    <EventAudiencePanel
      eventStatus="Draft"
      read={READ}
      refreshing={false}
      onRetry={NO_OP}
      attach={{ open: NO_OP, warnings: [], dismissWarnings: NO_OP }}
      detach={{ running: undefined, failure: undefined, group: NO_OP, student: NO_OP }}
    />
    <EventAudienceDefinitionsPanel
      eventStatus="Draft"
      classification={{ id: "c-1", name: "Institutional" }}
      options={{ status: "ready", options: [] }}
      onRetryOptions={NO_OP}
      read={READ}
      selected={[]}
      onSelect={NO_OP}
      attach={{ submit: NO_OP, running: false, failure: undefined }}
      detach={{ running: undefined, failure: undefined, remove: NO_OP }}
    />
    <EventPreRegistrationSection
      eventId={EVENT_ID}
      eventStatus="Draft"
      read={READ}
      onChanged={NO_OP}
      onAnnounce={NO_OP}
    />
  </>
);

// ------------------------------------------------------------------------------------ the event page

const EVENT_JSON = {
  id: EVENT_ID,
  name: "Freshman Orientation",
  description: null,
  location: "Gym",
  requireRegistration: false,
  startAt: "2026-09-01T01:00:00Z",
  endAt: "2026-09-01T05:00:00Z",
  attendanceMode: "Single",
  graceMinutes: 15,
  status: "Open",
  issuesCertificates: false,
};

const SUMMARY_JSON = {
  eventId: EVENT_ID,
  eventName: "Freshman Orientation",
  expected: 4,
  expectedStudents: 4,
  expectedPersonnel: 0,
  present: 0,
  late: 0,
  absent: 4,
  excused: 0,
  unexpected: 0,
  attendanceRate: 0,
};

const EMPTY_PAGE = { items: [], page: 1, pageSize: 200, total: 0, hasMore: false };

function serveEvent() {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const path = new URL(String(input), "http://localhost").pathname;
    if (path === `/api/v1/events/${EVENT_ID}/summary`) return Promise.resolve(json(200, SUMMARY_JSON));
    if (path === `/api/v1/events/${EVENT_ID}/attendees`) return Promise.resolve(json(200, AUDIENCE));
    if (path === `/api/v1/events/${EVENT_ID}/scans`) {
      return Promise.resolve(json(200, { eventId: EVENT_ID, totalScans: 0, distinctCards: 0, scans: [] }));
    }
    if (path === `/api/v1/events/${EVENT_ID}/attendance-codes`) return Promise.resolve(json(200, []));
    if (path === "/api/v1/pre-registration/sessions") return Promise.resolve(json(200, []));
    if (path === `/api/v1/events/${EVENT_ID}`) return Promise.resolve(json(200, EVENT_JSON));
    if (path === "/api/v1/students" || path === "/api/v1/attendance" || path === "/api/v1/event-audiences") {
      return Promise.resolve(json(200, EMPTY_PAGE));
    }
    throw new Error(`the event screen asked for something this stub does not serve: ${path}`);
  });
}

async function showAs(permissions: readonly string[], ui: ReactElement) {
  beginSession("access-token-1", userWith(permissions));
  render(
    <MemoryRouter initialEntries={[`/events/${EVENT_ID}`]}>
      <AuthProvider>
        <Routes>
          <Route path="/events/:id" element={ui} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
  await act(async () => {});
}

const button = (name: RegExp) => screen.queryByRole("button", { name });

beforeEach(() => {
  resetSessionForTests();
  serveEvent();
});

afterEach(() => {
  cleanup();
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("EventDetail", () => {
  it("shows a Viewer no status, Edit, Delete or simulate-tap control", async () => {
    await showAs(VIEWER, <EventDetail />);
    await screen.findByRole("heading", { name: "Freshman Orientation" });
    // The Open event's own chip is the proof the page rendered, and what the status buttons would sit by.
    expect(screen.getAllByText("Open").length).toBeGreaterThan(0);

    expect(button(/^close event$/i)).toBeNull();
    expect(button(/^cancel event$/i)).toBeNull();
    expect(button(/^edit$/i)).toBeNull();
    expect(button(/^delete$/i)).toBeNull();
    expect(screen.queryByText(/simulate rfid tap/i)).toBeNull();
    expect(button(/mark present by hand/i)).toBeNull();
  });

  it("still shows a token with the write permissions those controls", async () => {
    await showAs(WRITER, <EventDetail />);
    await screen.findByRole("heading", { name: "Freshman Orientation" });

    expect(button(/^close event$/i)).not.toBeNull();
    expect(button(/^cancel event$/i)).not.toBeNull();
    expect(button(/^edit$/i)).not.toBeNull();
    expect(button(/^delete$/i)).not.toBeNull();
    expect(screen.queryByText(/simulate rfid tap/i)).not.toBeNull();
    expect(button(/mark present by hand/i)).not.toBeNull();
  });
});

describe("the audience panels on the event screen", () => {
  it("show a Viewer what is expected, with no Add / Remove / Attach / Link / Unlink / Create control", async () => {
    await showAs(VIEWER, panels());

    // Read content is still there.
    expect(screen.getByText("BSIT 1-A")).toBeTruthy();
    expect(screen.getByText("Cruz, Ana")).toBeTruthy();
    expect(screen.getByText("All Freshmen")).toBeTruthy();
    expect(screen.getByText("Orientation pre-reg")).toBeTruthy();

    expect(button(/add sections/i)).toBeNull();
    expect(button(/^remove/i)).toBeNull();
    expect(button(/attach selected/i)).toBeNull();
    expect(screen.queryByRole("combobox")).toBeNull();
    expect(button(/^link pre-registration$/i)).toBeNull();
    expect(button(/^unlink/i)).toBeNull();
    expect(button(/create from event/i)).toBeNull();
  });

  it("still give a token with events.write those controls", async () => {
    await showAs(WRITER, panels());

    expect(button(/add sections/i)).not.toBeNull();
    expect(screen.getAllByRole("button", { name: /^remove/i }).length).toBeGreaterThanOrEqual(3);
    expect(button(/attach selected/i)).not.toBeNull();
    expect(screen.getAllByRole("combobox").length).toBeGreaterThan(0);
    expect(button(/^link pre-registration$/i)).not.toBeNull();
    expect(button(/^unlink/i)).not.toBeNull();
    expect(button(/create from event/i)).not.toBeNull();
  });
});
