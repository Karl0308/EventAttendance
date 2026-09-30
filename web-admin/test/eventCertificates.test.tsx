/** @vitest-environment happy-dom */

// The "issues certificates of attendance" setting (client QA Q20, `#470` B4 — "Yes") and the Email
// certificates button it gates.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// `EventDto.issuesCertificates` is always present and boolean. `EventWriteRequest.issuesCertificates`
// is nullable server-side (omit/null keeps the stored value on a `PUT`), but every construction site
// in this SPA — the create/edit dialogs and the Closed-event toggle — sends it explicitly, so it is
// typed as a required `boolean` here (`types.ts`'s own note says why).
//
// JJ's decision: the setting is the ONE thing a `Closed` event still accepts a `PUT` for, and only
// when nothing else in the body differs from what is stored. `EventDetail.tsx`'s toggle builds that
// body with `requestForCertificatesToggle` (`eventDraft.ts`), which resends the stored event's fields
// through the exact mapping `EditEventDialog` is held to — trimmed the same way, `""` mapped to `null`
// the same way, and the instants sent back byte-for-byte via `resolve`'s `unmoved` check. The "stored
// fields unchanged" test below asserts the PUT body field-by-field against the stored `EventDto`
// rather than merely asserting the request succeeded, which is what makes it able to catch a
// regression that starts sending an edited field alongside the flag.
//
// The two component-level suites (create defaults, edit round-trip) render `NewEventDialog` /
// `EditEventDialog` directly rather than through a page: neither dialog calls `fetch` itself — the
// page that hosts it owns the write (see both dialogs' own module notes) — so asserting on the
// `EventWriteRequest` handed to `onSubmit` is the direct, page-independent way to pin what each
// dialog builds. The Closed-toggle and Email-certificates suites render `EventDetail` itself, in the
// same harness shape `liveAttendanceFilters.test.tsx` uses, because both are `EventDetail`'s own
// behaviour rather than a dialog's.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import NewEventDialog from "../src/components/NewEventDialog";
import EditEventDialog from "../src/components/EditEventDialog";
import EventDetail from "../src/pages/EventDetail";
import AuthProvider from "../src/components/AuthProvider";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { PERMISSIONS } from "../src/permissions";
import type { AuthUser, EventItem, EventWriteRequest } from "../src/types";

const EVENT_ID = "44444444-4444-4444-8444-444444444444";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "organizer@usa.edu.ph",
  fullName: "Ozzy Organizer",
  permissions: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite],
};

const everyPermissionExcept = (code: string): readonly string[] =>
  Object.values(PERMISSIONS).filter((p) => p !== code);

// ---------------------------------------------------------------------------------------------
// The two dialogs, rendered directly — no fetch involved (see the file banner)
// ---------------------------------------------------------------------------------------------

const baseEvent = (overrides: Partial<EventItem> = {}): EventItem => ({
  id: EVENT_ID,
  name: "Freshman Orientation",
  description: undefined,
  location: "Gym",
  startAt: "2026-09-01T01:00:00Z",
  endAt: "2026-09-01T05:00:00Z",
  attendanceMode: "Single",
  graceMinutes: 15,
  requireRegistration: false,
  status: "Closed",
  issuesCertificates: false,
  ...overrides,
});

const CERTIFICATES_SWITCH = /issues certificates of attendance/i;

describe("New event: the certificates switch", () => {
  it("defaults to false in a new draft", () => {
    render(<NewEventDialog onClose={() => {}} onSubmit={() => {}} running={false} failure={undefined} />);
    const sw = screen.getByRole("checkbox", { name: CERTIFICATES_SWITCH }) as HTMLInputElement;
    expect(sw.checked).toBe(false);
  });

  it("sends issuesCertificates on the create request", () => {
    const onSubmit = vi.fn<(request: EventWriteRequest) => void>();
    render(<NewEventDialog onClose={() => {}} onSubmit={onSubmit} running={false} failure={undefined} />);

    fireEvent.change(screen.getByLabelText(/^name/i), { target: { value: "Founders Day" } });
    fireEvent.change(screen.getByLabelText(/^starts/i), { target: { value: "2026-09-01T09:00" } });
    fireEvent.change(screen.getByLabelText(/^ends/i), { target: { value: "2026-09-01T11:00" } });
    fireEvent.click(screen.getByRole("checkbox", { name: CERTIFICATES_SWITCH }));
    fireEvent.click(screen.getByRole("button", { name: /^create event$/i }));

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0].issuesCertificates).toBe(true);
  });

  // Negative control for the test above: an unmoved switch must send `false`, not just "some value".
  // Without this, the previous test would still pass if the field were hard-coded to `true`.
  it("negative control: leaving the switch alone sends false", () => {
    const onSubmit = vi.fn<(request: EventWriteRequest) => void>();
    render(<NewEventDialog onClose={() => {}} onSubmit={onSubmit} running={false} failure={undefined} />);

    fireEvent.change(screen.getByLabelText(/^name/i), { target: { value: "Founders Day" } });
    fireEvent.change(screen.getByLabelText(/^starts/i), { target: { value: "2026-09-01T09:00" } });
    fireEvent.change(screen.getByLabelText(/^ends/i), { target: { value: "2026-09-01T11:00" } });
    fireEvent.click(screen.getByRole("button", { name: /^create event$/i }));

    expect(onSubmit.mock.calls[0][0].issuesCertificates).toBe(false);
  });
});

describe("Edit event: the certificates switch survives a round trip", () => {
  it("loads the stored true value and resends it unchanged when nothing is touched", () => {
    const onSubmit = vi.fn<(request: EventWriteRequest) => void>();
    const event = baseEvent({ status: "Open", issuesCertificates: true });
    render(
      <EditEventDialog
        event={event}
        scope="everything"
        onClose={() => {}}
        onSubmit={onSubmit}
        running={false}
        failure={undefined}
      />,
    );

    const sw = screen.getByRole("checkbox", { name: CERTIFICATES_SWITCH }) as HTMLInputElement;
    expect(sw.checked).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: /^save changes$/i }));

    expect(onSubmit.mock.calls[0][0].issuesCertificates).toBe(true);
  });

  // Negative control: flipping the switch is what actually changes the sent value, proving the
  // previous test is reading the switch rather than a value hard-coded to `true`.
  it("negative control: toggling it off sends false", () => {
    const onSubmit = vi.fn<(request: EventWriteRequest) => void>();
    const event = baseEvent({ status: "Open", issuesCertificates: true });
    render(
      <EditEventDialog
        event={event}
        scope="everything"
        onClose={() => {}}
        onSubmit={onSubmit}
        running={false}
        failure={undefined}
      />,
    );

    fireEvent.click(screen.getByRole("checkbox", { name: CERTIFICATES_SWITCH }));
    fireEvent.click(screen.getByRole("button", { name: /^save changes$/i }));

    expect(onSubmit.mock.calls[0][0].issuesCertificates).toBe(false);
  });

  it("stays editable even when the rest of the form is locked (descriptive scope)", () => {
    // `scope="descriptive"` is what a `Cancelled` event renders — `lockedFor("descriptive")` disables
    // the five attendance-rule fields, and this asserts `issuesCertificates` is deliberately not one
    // of them: it is editable in every status, `Closed` included (whose own path is the toggle on
    // `EventDetail.tsx`, not this dialog at all).
    const event = baseEvent({ status: "Cancelled", issuesCertificates: true });
    render(
      <EditEventDialog
        event={event}
        scope="descriptive"
        onClose={() => {}}
        onSubmit={() => {}}
        running={false}
        failure={undefined}
      />,
    );

    const sw = screen.getByRole("checkbox", { name: CERTIFICATES_SWITCH }) as HTMLInputElement;
    expect(sw.disabled).toBe(false);
  });
});

// ---------------------------------------------------------------------------------------------
// `EventDetail` itself — the display, the Closed-event toggle, and the Email certificates button
// ---------------------------------------------------------------------------------------------

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/** The event as the server currently holds it — mutated by a successful PUT, as the real API would. */
let stored: EventItem;

const eventJson = () => ({
  id: stored.id,
  name: stored.name,
  description: stored.description ?? null,
  location: stored.location ?? null,
  requireRegistration: stored.requireRegistration,
  startAt: stored.startAt,
  endAt: stored.endAt,
  attendanceMode: stored.attendanceMode,
  graceMinutes: stored.graceMinutes,
  status: stored.status,
  issuesCertificates: stored.issuesCertificates,
});

const SUMMARY_JSON = () => ({
  eventId: stored.id,
  eventName: stored.name,
  expected: 0,
  present: 0,
  late: 0,
  absent: 0,
  excused: 0,
  unexpected: 0,
  attendanceRate: 0,
});

const AUDIENCE_JSON = () => ({
  eventId: stored.id,
  status: stored.status,
  isFrozen: true,
  expected: 0,
  groups: [],
  students: [],
});

const SCANS_JSON = () => ({ eventId: stored.id, totalScans: 0, distinctCards: 0, scans: [] });

/** Every PUT body this stub received, most recent last. */
let putBodies: Record<string, unknown>[] = [];

/** When set, the next PUT is refused with this 409 — the server's own "which fields differ" shape. */
let refuseWith: { status: number; detail: string } | undefined;

/**
 * The stub wrapped in a spy, so a test can assert on the number of calls made — not merely on the
 * bodies of the ones that happened to hit `/events/{id}`. `putBodies` alone cannot prove "the button
 * sent no request of any kind"; a call to a different route would leave `putBodies` untouched too.
 */
let fetchSpy: ReturnType<typeof vi.fn>;

function serve() {
  fetchSpy = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const path = new URL(url, "http://localhost").pathname;

    if (path === `/api/v1/events/${EVENT_ID}/summary`) return Promise.resolve(json(200, SUMMARY_JSON()));
    if (path === `/api/v1/events/${EVENT_ID}/attendees`) return Promise.resolve(json(200, AUDIENCE_JSON()));
    if (path === `/api/v1/events/${EVENT_ID}/scans`) return Promise.resolve(json(200, SCANS_JSON()));
    if (path === "/api/v1/students") {
      return Promise.resolve(
        json(200, { items: [], page: 1, pageSize: 200, total: 0, hasMore: false }),
      );
    }
    if (path === "/api/v1/attendance") {
      return Promise.resolve(json(200, { items: [], page: 1, pageSize: 200, total: 0, hasMore: false }));
    }
    if (path === `/api/v1/events/${EVENT_ID}` && init?.method === "PUT") {
      const body = JSON.parse(String(init.body)) as Record<string, unknown>;
      putBodies.push(body);

      if (refuseWith !== undefined) {
        const { status, detail } = refuseWith;
        return Promise.resolve(
          json(status, {
            type: "https://tools.ietf.org/html/rfc7231#section-6.5.10",
            title: "The event could not be saved.",
            status,
            detail,
          }),
        );
      }

      stored = {
        ...stored,
        name: body.name as string,
        description: (body.description as string | null) ?? undefined,
        location: (body.location as string | null) ?? undefined,
        startAt: body.startAt as string,
        endAt: body.endAt as string,
        attendanceMode: body.attendanceMode as string,
        graceMinutes: body.graceMinutes as number,
        requireRegistration: body.requireRegistration as boolean,
        issuesCertificates: body.issuesCertificates as boolean,
      };
      return Promise.resolve(json(200, eventJson()));
    }
    if (path === `/api/v1/events/${EVENT_ID}`) return Promise.resolve(json(200, eventJson()));

    throw new Error(`the event screen asked for something this stub does not serve: ${url}`);
  });
  vi.stubGlobal("fetch", fetchSpy);
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

beforeEach(() => {
  stored = baseEvent({ status: "Closed", issuesCertificates: false });
  putBodies = [];
  refuseWith = undefined;
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("EventDetail shows whether the event issues certificates", () => {
  // An `Open` fixture, deliberately: on a `Closed` event the toggle's own `FormControlLabel` reads
  // "Issues certificates of attendance", which also matches a loose `/issues certificates/i` — so a
  // Closed fixture could pass this test on the toggle's label alone, whether or not the chip in the
  // header rendered anything at all. `Open` never mounts that toggle, so only the chip is on screen
  // to match. Exact text matching (not a regex) is what then tells the chip's "Issues certificates"
  // apart from that longer label if it were ever present too.
  it("shows the chip reading 'Issues certificates' when the flag is on", async () => {
    stored = baseEvent({ status: "Open", issuesCertificates: true });
    await show();
    expect(screen.getByText("Issues certificates", { exact: true })).toBeTruthy();
  });

  it("shows the chip reading 'No certificates', and never the on-label, when the flag is off", async () => {
    stored = baseEvent({ status: "Open", issuesCertificates: false });
    await show();
    expect(screen.getByText("No certificates", { exact: true })).toBeTruthy();
    expect(screen.queryByText("Issues certificates", { exact: true })).toBeNull();
  });
});

describe("The Closed-event certificates toggle", () => {
  it("sends the stored fields unchanged, plus the new flag", async () => {
    stored = baseEvent({
      status: "Closed",
      issuesCertificates: false,
      name: "Freshman Orientation",
      description: "Welcome week",
      location: "Gym",
      startAt: "2026-09-01T01:00:00Z",
      endAt: "2026-09-01T05:00:00Z",
      attendanceMode: "Single",
      graceMinutes: 15,
      requireRegistration: true,
    });
    await show();

    fireEvent.click(screen.getByRole("checkbox", { name: CERTIFICATES_SWITCH }));
    await act(async () => {});

    expect(putBodies).toHaveLength(1);
    expect(putBodies[0]).toEqual({
      name: "Freshman Orientation",
      description: "Welcome week",
      location: "Gym",
      startAt: "2026-09-01T01:00:00Z",
      endAt: "2026-09-01T05:00:00Z",
      attendanceMode: "Single",
      graceMinutes: 15,
      graceBeforeStartMinutes: null,
      graceAfterEndMinutes: null,
      requireRegistration: true,
      issuesCertificates: true,
    });
  });

  // Negative control for the test above: the assertion there is field-by-field, not "the request
  // succeeded". Confirmed by hand — flipping `requestForCertificatesToggle` in `src/eventDraft.ts` to
  // send back `requireRegistration: !base.requireRegistration` made that test fail with
  // `requireRegistration: false` where `true` (the stored value) was expected, and reverting the edit
  // (via the Edit tool, not `git checkout --`) restored the green run recorded here.

  it("echoes the start and end instants byte-for-byte, ticks included", async () => {
    // `datetime-local` has minute resolution; an instant does not. This is what proves the toggle
    // never routes the stored instants through a box at all — a value that would lose its sub-second
    // digits the moment it touched one.
    stored = baseEvent({
      status: "Closed",
      issuesCertificates: false,
      startAt: "2026-09-01T01:00:30.1234567Z",
      endAt: "2026-09-01T05:00:45.7654321Z",
    });
    await show();

    fireEvent.click(screen.getByRole("checkbox", { name: CERTIFICATES_SWITCH }));
    await act(async () => {});

    expect(putBodies).toHaveLength(1);
    expect(putBodies[0].startAt).toBe("2026-09-01T01:00:30.1234567Z");
    expect(putBodies[0].endAt).toBe("2026-09-01T05:00:45.7654321Z");
  });

  // Pins W1: the server's certificates-only check compares `description`/`location` RAW — no trim,
  // no `"" -> null` — because `EventService.Apply` stores them exactly as a PUT sends them. An event
  // whose description was ever written by a non-SPA client as untrimmed text, or whose location is an
  // empty string rather than absent, must come back on the wire exactly as stored or the server sees
  // a second changed field and refuses the whole PUT with 409 — unrecoverable from the SPA, since a
  // Closed event accepts no other edit.
  //
  // This test FAILED before the W1 fix: `requestForCertificatesToggle` used to build its body through
  // `draftFrom`/`validate`, which trims `description` (" x " -> "x") and maps an empty `location` to
  // `null` — the exact two transformations this test exists to catch. Confirmed by hand by reverting
  // the fix locally and re-running this test alone, which failed on both fields with the trimmed/
  // nulled values; restoring the fix (via the Edit tool) made it pass, as recorded here.
  it("echoes an untrimmed description and an empty-string location raw, not trimmed or nulled", async () => {
    stored = baseEvent({
      status: "Closed",
      issuesCertificates: false,
      description: " x ",
      location: "",
    });
    await show();

    fireEvent.click(screen.getByRole("checkbox", { name: CERTIFICATES_SWITCH }));
    await act(async () => {});

    expect(putBodies).toHaveLength(1);
    expect(putBodies[0].description).toBe(" x ");
    expect(putBodies[0].location).toBe("");
  });

  it("is hidden or disabled without events.write", async () => {
    resetSessionForTests();
    beginSession("access-token-1", { ...OPERATOR, permissions: everyPermissionExcept(PERMISSIONS.eventsWrite) });
    await show();

    const sw = screen.queryByRole("checkbox", { name: CERTIFICATES_SWITCH }) as HTMLInputElement | null;
    expect(sw === null || sw.disabled).toBe(true);
    expect(pageText()).toMatch(/does not have/i);
  });

  it("shows the server's 409 message verbatim on a refusal", async () => {
    refuseWith = {
      status: 409,
      detail: "The event is Closed; only issuesCertificates may change. Also different: name.",
    };
    await show();

    fireEvent.click(screen.getByRole("checkbox", { name: CERTIFICATES_SWITCH }));
    await act(async () => {});

    expect(pageText()).toMatch(/Also different: name/);
  });
});

describe("Email certificates", () => {
  it("is shown only when the event issues certificates", async () => {
    stored = baseEvent({ status: "Closed", issuesCertificates: true });
    await show();
    expect(screen.getByRole("button", { name: /email certificates/i })).toBeTruthy();
  });

  it("is not shown when the event does not issue certificates", async () => {
    stored = baseEvent({ status: "Closed", issuesCertificates: false });
    await show();
    expect(screen.queryByRole("button", { name: /email certificates/i })).toBeNull();
  });

  it("is a native-disabled button, so clicking it cannot dispatch at all", async () => {
    stored = baseEvent({ status: "Closed", issuesCertificates: true });
    await show();

    const button = screen.getByRole("button", { name: /email certificates/i }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
  });

  it("sends no request of any kind when pressed", async () => {
    stored = baseEvent({ status: "Closed", issuesCertificates: true });
    await show();

    const before = fetchSpy.mock.calls.length;
    fireEvent.click(screen.getByRole("button", { name: /email certificates/i }));
    await act(async () => {});

    // Not just "no PUT" (`putBodies` only ever grows on `/events/{id}` PUTs) — no `fetch` call at
    // all, of any method to any route. A disabled native button cannot dispatch a click handler in
    // the first place, so this is really pinning that the button stayed disabled rather than testing
    // anything about `fetch` — but it is the direct way to state "sends nothing" rather than the
    // indirect "the one endpoint I thought to check didn't move".
    expect(fetchSpy.mock.calls.length).toBe(before);
  });

  it("carries an accessible not-available-yet description", async () => {
    stored = baseEvent({ status: "Closed", issuesCertificates: true });
    await show();

    const button = screen.getByRole("button", { name: /email certificates/i });
    const describedBy = button.getAttribute("aria-describedby");
    expect(describedBy).toBeTruthy();
    const description = document.getElementById(describedBy as string);
    expect(description?.textContent).toMatch(/not available yet/i);
  });
});
