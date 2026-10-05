/** @vitest-environment happy-dom */

// Task 2 Phase 3: the EventDetail "Pre-registration" section — pre-registration sessions linked to an
// event, and the explanation of why the expected number changed when one is.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// `EventPreRegistrationSection` is rendered directly (it owns the session picker's read and the three
// writes) with `fetch` stubbed, so each write is asserted on what actually went over the wire:
//
//   1. The linked sessions and the total pre-registered count badge are shown.
//   2. `expectedSource === "PreRegistration"` is said in words — expected is not the audience union now.
//   3. Advisory personnel are their own line and never join the expected figure.
//   4. A picked session is linked (POST) and an existing link is unlinked (DELETE).
//   5. On a terminal event link/unlink are disabled with a hint, and a 409 that arrives anyway is an alert.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";

import EventPreRegistrationSection from "../src/components/EventPreRegistrationSection";
import type { AudienceRead } from "../src/components/EventAudiencePanel";
import type { EventAudience } from "../src/types";

const EVENT_ID = "44444444-4444-4444-8444-444444444444";
const LINKED_ID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const OTHER_LINKED_ID = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
const FREE_ID = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const sessionRow = (id: string, name: string) => ({
  id,
  name,
  audienceDefinitionId: "d-1",
  audienceName: "All Freshmen",
  capacity: 100,
  registeredCount: 12,
  isClosed: false,
  isFull: false,
});

/** What `GET /pre-registration/sessions` returns: two already linked, one free to link. */
const CATALOGUE = [
  sessionRow(LINKED_ID, "Orientation pre-reg"),
  sessionRow(OTHER_LINKED_ID, "Late pre-reg"),
  sessionRow(FREE_ID, "Foundation Week pre-reg"),
];

interface Request {
  method: string;
  pathname: string;
  body: unknown;
}
let requests: Request[] = [];

function serve(writeReply?: (method: string) => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    requests.push({
      method,
      pathname: url.pathname,
      body: typeof init?.body === "string" ? (JSON.parse(init.body) as unknown) : undefined,
    });

    if (method === "GET" && url.pathname.endsWith("/pre-registration/sessions")) {
      return Promise.resolve(json(200, CATALOGUE));
    }
    if (url.pathname.includes(`/events/${EVENT_ID}/pre-registration/sessions`)) {
      return Promise.resolve(writeReply ? writeReply(method) : new Response(null, { status: 204 }));
    }
    return Promise.resolve(json(404, { title: "not stubbed", status: 404 }));
  });
}

const linkedSessions = [
  {
    preRegistrationSessionId: LINKED_ID,
    name: "Orientation pre-reg",
    preRegisteredStudentCount: 30,
    advisoryPersonnelCount: 4,
  },
  {
    preRegistrationSessionId: OTHER_LINKED_ID,
    name: "Late pre-reg",
    preRegisteredStudentCount: 5,
    advisoryPersonnelCount: 1,
  },
];

const linkedAudience = (over: Partial<EventAudience> = {}): AudienceRead => ({
  status: "ready",
  audience: {
    eventId: EVENT_ID,
    status: "Draft",
    isFrozen: false,
    expected: 35,
    groups: [],
    students: [],
    definitions: [],
    advisoryPersonnelCount: 0,
    expectedSource: "PreRegistration",
    preRegistration: {
      linkedSessions,
      totalPreRegisteredStudentCount: 35,
      totalAdvisoryPersonnelCount: 5,
    },
    ...over,
  },
});

const unlinkedAudience = (): AudienceRead =>
  linkedAudience({ expected: 120, expectedSource: "Audience", preRegistration: null });

function section(read: AudienceRead, status = "Draft", onChanged = vi.fn(), onAnnounce = vi.fn()) {
  return (
    <EventPreRegistrationSection
      eventId={EVENT_ID}
      eventStatus={status}
      read={read}
      onChanged={onChanged}
      onAnnounce={onAnnounce}
    />
  );
}

const PICKER_NAME = /^pre-registration session$/i;

/** Opens the picker's list once the sessions read has settled, and returns nothing: options are queried. */
async function openPicker(): Promise<void> {
  const picker = await screen.findByRole("combobox", { name: PICKER_NAME });
  await waitFor(() => expect((picker as HTMLInputElement).disabled).toBe(false));
  await waitFor(() =>
    expect(requests.some((r) => r.method === "GET" && r.pathname.endsWith("/pre-registration/sessions"))).toBe(true),
  );
  await act(async () => {
    fireEvent.keyDown(picker, { key: "ArrowDown" });
  });
  await screen.findByRole("listbox");
}

beforeEach(() => {
  requests = [];
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Pre-registration section: what is linked", () => {
  it("shows the pre-registered count badge and linked sessions", async () => {
    render(section(linkedAudience()));

    // The badge is the total across sessions: 30 + 5.
    expect(await screen.findByText("35 pre-registered")).toBeTruthy();
    // Each linked session, with its own count — not the total.
    expect(screen.getByText("Orientation pre-reg")).toBeTruthy();
    expect(screen.getByText("30 students · 4 personnel")).toBeTruthy();
    expect(screen.getByText("Late pre-reg")).toBeTruthy();
    expect(screen.getByText("5 students · 1 personnel")).toBeTruthy();
  });

  it("indicates expected comes from pre-registration when a session is linked", async () => {
    render(section(linkedAudience()));

    expect(await screen.findByText("Expected from pre-registration")).toBeTruthy();
    expect(screen.queryByText("Expected from audience")).toBeNull();
    // And says what that did to the number, so a drop from the audience union is not read as lost data.
    expect(screen.getByText(/this event expects 35 students/i).textContent).toMatch(
      /replaces the audience sections and definitions/i,
    );
  });

  it("says the expected number comes from the audience when nothing is linked", async () => {
    render(section(unlinkedAudience()));

    expect(await screen.findByText("Expected from audience")).toBeTruthy();
    expect(screen.queryByText("Expected from pre-registration")).toBeNull();
    expect(screen.queryByText(/pre-registered$/)).toBeNull();
  });

  it("shows advisory personnel separate from expected", async () => {
    render(section(linkedAudience()));

    const advisory = await screen.findByText("5 personnel pre-registered — not counted in expected");
    const source = screen.getByText("Expected from pre-registration");
    const badge = screen.getByText("35 pre-registered");

    // Its own line, in none of the elements that carry the student figure — and the students-only total
    // is what the badge reads, not 40.
    expect(advisory).not.toBe(source);
    expect(advisory.contains(badge)).toBe(false);
    expect(badge.contains(advisory)).toBe(false);
    expect(screen.queryByText("40 pre-registered")).toBeNull();
  });
});

describe("Pre-registration section: linking and unlinking", () => {
  it("links a selected session", async () => {
    const onChanged = vi.fn();
    const onAnnounce = vi.fn();
    render(section(linkedAudience(), "Draft", onChanged, onAnnounce));

    await openPicker();
    // Only the session that is not already linked is on offer.
    expect(screen.queryByRole("option", { name: /orientation pre-reg/i })).toBeNull();
    fireEvent.click(await screen.findByRole("option", { name: /foundation week pre-reg/i }));
    fireEvent.click(screen.getByRole("button", { name: /^link pre-registration$/i }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));

    const post = requests.find((r) => r.method === "POST");
    expect(post?.pathname).toMatch(new RegExp(`/events/${EVENT_ID}/pre-registration/sessions$`));
    expect(post?.body).toEqual({ preRegistrationSessionId: FREE_ID });
    expect(onAnnounce).toHaveBeenCalledTimes(1);
    expect(onAnnounce.mock.calls[0][0]).toContain("Foundation Week pre-reg");
  });

  it("unlinks a session", async () => {
    const onChanged = vi.fn();
    const onAnnounce = vi.fn();
    render(section(linkedAudience(), "Draft", onChanged, onAnnounce));

    fireEvent.click(await screen.findByRole("button", { name: /unlink pre-registration session orientation pre-reg/i }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    const del = requests.find((r) => r.method === "DELETE");
    expect(del?.pathname).toMatch(
      new RegExp(`/events/${EVENT_ID}/pre-registration/sessions/${LINKED_ID}$`),
    );
    expect(onAnnounce).toHaveBeenCalledWith("“Orientation pre-reg” is no longer linked to this event.");
  });

  it("creates a session from the event, sending the typed name", async () => {
    const onChanged = vi.fn();
    render(section(unlinkedAudience(), "Draft", onChanged));

    fireEvent.change(await screen.findByRole("textbox", { name: /new session name/i }), {
      target: { value: "Fresh one" },
    });
    fireEvent.click(screen.getByRole("button", { name: /create from event/i }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    const post = requests.find((r) => r.method === "POST");
    expect(post?.pathname).toMatch(new RegExp(`/events/${EVENT_ID}/pre-registration/sessions/from-event$`));
    expect(post?.body).toEqual({ name: "Fresh one" });
  });

  it("shows a failed link as an alert and keeps the selection for a retry", async () => {
    serve((method) =>
      method === "POST" ? json(400, { title: "Bad reference", status: 400 }) : new Response(null, { status: 204 }),
    );
    render(section(linkedAudience()));

    await openPicker();
    fireEvent.click(await screen.findByRole("option", { name: /foundation week pre-reg/i }));
    fireEvent.click(screen.getByRole("button", { name: /^link pre-registration$/i }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("“Foundation Week pre-reg” was not linked");
    expect(screen.getByRole("button", { name: /^link pre-registration$/i })).toHaveProperty("disabled", false);
  });
});

describe("Pre-registration section: a terminal event", () => {
  it("disables link/unlink on a terminal event", async () => {
    render(section(linkedAudience({ isFrozen: true, status: "Closed" }), "Closed"));

    const picker = await screen.findByRole("combobox", { name: PICKER_NAME });
    expect((picker as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByRole("button", { name: /^link pre-registration$/i })).toHaveProperty("disabled", true);
    expect(screen.getByRole("button", { name: /create from event/i })).toHaveProperty("disabled", true);
    expect(
      screen.getByRole("button", { name: /unlink pre-registration session orientation pre-reg/i }),
    ).toHaveProperty("disabled", true);
    // Said, not just greyed out.
    expect(screen.getByText(/this event is closed, so pre-registration sessions can no longer be linked/i)).toBeTruthy();
    // Nothing to pick from, so nothing is fetched.
    expect(requests.some((r) => r.pathname.endsWith("/pre-registration/sessions"))).toBe(false);
  });

  it("shows a 409 that arrives anyway as an alert, and does not call it a generic failure", async () => {
    serve((method) =>
      method === "DELETE" ? json(409, { title: "EventLocked", status: 409 }) : new Response(null, { status: 204 }),
    );
    render(section(linkedAudience()));

    fireEvent.click(await screen.findByRole("button", { name: /unlink pre-registration session orientation pre-reg/i }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("pre-registration is now fixed");
    expect(alert.textContent).toMatch(/closed or cancelled/i);
  });
});
