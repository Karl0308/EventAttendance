/** @vitest-environment happy-dom */

// Task 2 Phase 2: the "Add audience" surface — reusable audience definitions attached to an event,
// beside the sections picker.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// `EventAudienceDefinitionsSection` is rendered directly (it owns the definitions read, the picked set
// and both writes) with `fetch` stubbed, so each test asserts on what actually went over the wire:
//
//   1. The picker offers ACTIVE definitions only, asked for with the event's `eventClassificationId`.
//   2. A new classification re-reads and re-filters — and a pick that no longer exists drops out.
//   3. Attaching posts `audienceDefinitionIds` and asks the page to re-read.
//   4. The personnel advisory is its own labelled line and never moves the expected chip.
//   5. An attached definition can be removed: `DELETE …/attendees/definitions/{id}`.
//
// Plus the two states that must not crash: an event with no classification, and a terminal event.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";

import EventAudienceDefinitionsSection from "../src/components/EventAudienceDefinitionsSection";
import EventAudiencePanel from "../src/components/EventAudiencePanel";
import type { AudienceRead } from "../src/components/EventAudiencePanel";
import type { EventAudience, EventAudienceDefinition } from "../src/types";

const EVENT_ID = "44444444-4444-4444-8444-444444444444";
const INSTITUTIONAL = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const DEPARTMENTAL = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const definitionRow = (id: string, name: string, classificationId: string, isActive = true) => ({
  id,
  name,
  eventClassificationId: classificationId,
  eventClassificationName: classificationId === INSTITUTIONAL ? "Institutional" : "Departmental",
  audienceType: "Department",
  criteria: {},
  isActive,
});

/** What the server holds, by classification. Includes an INACTIVE one to prove it is not offered. */
const CATALOGUE = [
  definitionRow("d-1", "All Freshmen", INSTITUTIONAL),
  definitionRow("d-2", "Faculty Assembly", INSTITUTIONAL),
  definitionRow("d-3", "Retired Roster", INSTITUTIONAL, false),
  definitionRow("d-4", "CICSS Seniors", DEPARTMENTAL),
];

interface Request {
  method: string;
  pathname: string;
  query: URLSearchParams;
  body: unknown;
}
let requests: Request[] = [];

function serve(): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    requests.push({
      method,
      pathname: url.pathname,
      query: url.searchParams,
      body: typeof init?.body === "string" ? (JSON.parse(init.body) as unknown) : undefined,
    });

    if (method === "GET" && url.pathname.endsWith("/event-audiences")) {
      const wanted = url.searchParams.get("eventClassificationId");
      // The server's own default is active-only; honour `includeInactive` the way it would.
      const includeInactive = url.searchParams.get("includeInactive") === "true";
      const items = CATALOGUE.filter(
        (d) => (wanted === null || d.eventClassificationId === wanted) && (includeInactive || d.isActive),
      );
      return Promise.resolve(json(200, { items, page: 1, pageSize: 200, total: items.length, hasMore: false }));
    }
    if (method === "POST" && url.pathname.endsWith(`/events/${EVENT_ID}/attendees`)) {
      return Promise.resolve(
        json(200, {
          eventId: EVENT_ID,
          groupsAttached: 0,
          studentsAttached: 0,
          groupsAlreadyAttached: 0,
          studentsAlreadyAttached: 0,
          definitionsAttached: 2,
          definitionsAlreadyAttached: 0,
          expected: 160,
          warnings: [],
        }),
      );
    }
    if (method === "DELETE" && url.pathname.includes(`/events/${EVENT_ID}/attendees/definitions/`)) {
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    return Promise.resolve(json(404, { title: "not stubbed", status: 404 }));
  });
}

const attached = (over: Partial<EventAudienceDefinition> = {}): EventAudienceDefinition => ({
  audienceDefinitionId: "d-9",
  name: "Already Attached",
  audienceType: "Program",
  studentCount: 40,
  personnelCount: 7,
  isActive: true,
  ...over,
});

const readyAudience = (over: Partial<EventAudience> = {}): AudienceRead => ({
  status: "ready",
  audience: {
    eventId: EVENT_ID,
    status: "Draft",
    isFrozen: false,
    expected: 40,
    groups: [],
    students: [],
    definitions: [],
    advisoryPersonnelCount: 0,
    expectedSource: "Audience",
    preRegistration: null,
    ...over,
  },
});

interface Props {
  classificationId: string | undefined;
  classificationName: string | undefined;
  read: AudienceRead;
  status: string;
}

function section(props: Props, onChanged = vi.fn(), onAnnounce = vi.fn()) {
  return (
    <EventAudienceDefinitionsSection
      eventId={EVENT_ID}
      eventStatus={props.status}
      eventClassificationId={props.classificationId}
      eventClassificationName={props.classificationName}
      read={props.read}
      onChanged={onChanged}
      onAnnounce={onAnnounce}
    />
  );
}

const base: Props = {
  classificationId: INSTITUTIONAL,
  classificationName: "Institutional",
  read: readyAudience(),
  status: "Draft",
};

const PICKER_NAME = /^audience definitions$/i;

/** Opens the multi-select's list and returns the names currently offered. */
async function openedOptionNames(): Promise<string[]> {
  const picker = await screen.findByRole("combobox", { name: PICKER_NAME });
  await waitFor(() => expect((picker as HTMLInputElement).disabled).toBe(false));
  // The list reads settle after the input is enabled; wait for the option fetch before opening.
  await waitFor(() => expect(requests.some((r) => r.pathname.endsWith("/event-audiences"))).toBe(true));
  await act(async () => {
    fireEvent.keyDown(picker, { key: "ArrowDown" });
  });
  const listbox = await screen.findByRole("listbox");
  return within(listbox)
    .queryAllByRole("option")
    .map((o) => o.textContent ?? "");
}

beforeEach(() => {
  requests = [];
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Add audience: the definition picker", () => {
  it("offers active definitions filtered by the event classification", async () => {
    render(section(base));

    const names = await openedOptionNames();

    // Active, and this classification's only: not "Retired Roster" (inactive), not "CICSS Seniors"
    // (another classification).
    expect(names.some((n) => n.includes("All Freshmen"))).toBe(true);
    expect(names.some((n) => n.includes("Faculty Assembly"))).toBe(true);
    expect(names.some((n) => n.includes("Retired Roster"))).toBe(false);
    expect(names.some((n) => n.includes("CICSS Seniors"))).toBe(false);

    const asked = requests.find((r) => r.pathname.endsWith("/event-audiences"));
    expect(asked?.query.get("eventClassificationId")).toBe(INSTITUTIONAL);
    expect(asked?.query.get("includeInactive")).toBe("false");
  });

  it("re-filters when the classification changes", async () => {
    const view = render(section(base));
    const before = await openedOptionNames();
    expect(before.some((n) => n.includes("All Freshmen"))).toBe(true);

    // Pick one, then move the event to another classification: the pick must not survive, because
    // it is no longer among the definitions on offer.
    fireEvent.click(await screen.findByRole("option", { name: /all freshmen/i }));
    expect(screen.getByRole("button", { name: /attach selected/i })).toHaveProperty("disabled", false);

    requests = [];
    view.rerender(section({ ...base, classificationId: DEPARTMENTAL, classificationName: "Departmental" }));

    await waitFor(() =>
      expect(
        requests.some(
          (r) =>
            r.pathname.endsWith("/event-audiences") &&
            r.query.get("eventClassificationId") === DEPARTMENTAL,
        ),
      ).toBe(true),
    );
    const after = await openedOptionNames();

    expect(after.some((n) => n.includes("CICSS Seniors"))).toBe(true);
    expect(after.some((n) => n.includes("All Freshmen"))).toBe(false);
    expect(screen.getByRole("button", { name: /attach selected/i })).toHaveProperty("disabled", true);
  });

  it("attaches selected definitions", async () => {
    const onChanged = vi.fn();
    const onAnnounce = vi.fn();
    render(section(base, onChanged, onAnnounce));

    await openedOptionNames();
    fireEvent.click(await screen.findByRole("option", { name: /all freshmen/i }));
    fireEvent.click(await screen.findByRole("option", { name: /faculty assembly/i }));
    fireEvent.click(screen.getByRole("button", { name: /attach selected/i }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));

    const post = requests.find((r) => r.method === "POST");
    expect(post?.body).toEqual({ audienceDefinitionIds: ["d-1", "d-2"] });
    expect(onAnnounce).toHaveBeenCalledTimes(1);
    expect(onAnnounce.mock.calls[0][0]).toContain("2 audience definitions attached.");
  });

  it("shows a failed attach as an alert and keeps the selection for a retry", async () => {
    vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), "http://localhost");
      if (init?.method === "POST") return Promise.resolve(json(400, { title: "Bad reference", status: 400 }));
      const items = CATALOGUE.filter((d) => d.isActive && d.eventClassificationId === INSTITUTIONAL);
      requests.push({ method: "GET", pathname: url.pathname, query: url.searchParams, body: undefined });
      return Promise.resolve(json(200, { items, page: 1, pageSize: 200, total: items.length, hasMore: false }));
    });
    render(section(base));

    await openedOptionNames();
    fireEvent.click(await screen.findByRole("option", { name: /all freshmen/i }));
    fireEvent.click(screen.getByRole("button", { name: /attach selected/i }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("Those definitions were not attached");
    expect(screen.getByRole("button", { name: /attach selected/i })).toHaveProperty("disabled", false);
  });
});

describe("Add audience: an event with no classification, or a locked one", () => {
  it("disables the picker with a hint and fetches nothing, instead of crashing", async () => {
    render(section({ ...base, classificationId: undefined, classificationName: undefined }));

    const picker = await screen.findByRole("combobox", { name: PICKER_NAME });
    expect((picker as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText(/audience definitions are filtered by classification/i)).toBeTruthy();
    expect(requests.some((r) => r.pathname.endsWith("/event-audiences"))).toBe(false);
  });

  it("disables the picker and the remove controls on a Closed event", async () => {
    render(
      section({
        ...base,
        status: "Closed",
        read: readyAudience({ isFrozen: true, definitions: [attached()] }),
      }),
    );

    const picker = await screen.findByRole("combobox", { name: PICKER_NAME });
    expect((picker as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText("Already Attached")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /remove audience definition/i })).toBeNull();
  });
});

describe("Add audience: the attached definitions", () => {
  it("shows advisory personnel count separate from expected", async () => {
    // The sections panel (which owns the expected chip) and this one, fed the same read — the way the
    // page mounts them.
    const read = readyAudience({ expected: 40, definitions: [attached()], advisoryPersonnelCount: 7 });
    render(
      <>
        <EventAudiencePanel
          eventStatus="Draft"
          read={read}
          refreshing={false}
          onRetry={() => {}}
          attach={{ open: () => {}, warnings: [], dismissWarnings: () => {} }}
          detach={{ running: undefined, failure: undefined, group: () => {}, student: () => {} }}
        />
        {section({ ...base, read })}
      </>,
    );

    const advisory = await screen.findByText("7 personnel eligible — not counted in expected");
    const chip = screen.getByText("40 expected");

    // Two different elements, and the denominator is still the students-only figure: 40, not 47.
    expect(advisory).not.toBe(chip);
    expect(chip.contains(advisory)).toBe(false);
    expect(screen.queryByText("47 expected")).toBeNull();
    // The advisory sits in the definitions card, not the sections card that holds the chip.
    expect(chip.closest(".MuiCard-root")?.contains(advisory)).toBe(false);
  });

  it("lets you remove an attached definition", async () => {
    const onChanged = vi.fn();
    const onAnnounce = vi.fn();
    render(
      section(
        { ...base, read: readyAudience({ definitions: [attached(), attached({ audienceDefinitionId: "d-8", name: "Other One" })] }) },
        onChanged,
        onAnnounce,
      ),
    );

    fireEvent.click(await screen.findByRole("button", { name: /remove audience definition already attached/i }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    const del = requests.find((r) => r.method === "DELETE");
    expect(del?.pathname).toMatch(new RegExp(`/events/${EVENT_ID}/attendees/definitions/d-9$`));
    expect(onAnnounce).toHaveBeenCalledWith("Already Attached is no longer part of this event's audience.");
  });

  it("shows a failed removal as an alert", async () => {
    vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), "http://localhost");
      if (init?.method === "DELETE") return Promise.resolve(json(409, { title: "EventLocked", status: 409 }));
      const items = CATALOGUE.filter((d) => d.isActive && d.eventClassificationId === INSTITUTIONAL);
      requests.push({ method: "GET", pathname: url.pathname, query: url.searchParams, body: undefined });
      return Promise.resolve(json(200, { items, page: 1, pageSize: 200, total: items.length, hasMore: false }));
    });
    render(section({ ...base, read: readyAudience({ definitions: [attached()] }) }));

    fireEvent.click(await screen.findByRole("button", { name: /remove audience definition already attached/i }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("This event’s audience is now fixed");
  });
});
