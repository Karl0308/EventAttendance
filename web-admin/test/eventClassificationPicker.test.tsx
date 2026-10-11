/** @vitest-environment happy-dom */

// The classification select in the event form (create and edit share it through `EventFormFields`).
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// Rendered through the real dialogs with `fetch` stubbed, so the options are whatever the wire gave
// and the wiring from the stored event to the picker is exercised too:
//
//   1. Only ACTIVE classifications are offered (plus "None") — a withdrawn one is never a fresh choice.
//   2. An edited event that already carries a since-deactivated classification keeps it as an option,
//      labelled as deactivated. Dropping it would show the event as "None" and the next save would
//      silently clear it, which is the defect this picker exists to stop.
//   3. The picked id reaches the request; "None" reaches it as null.
//   4. A failed classifications read is said out loud, not rendered as an empty list.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";

import EditEventDialog from "../src/components/EditEventDialog";
import NewEventDialog from "../src/components/NewEventDialog";
import type { EventItem, EventWriteRequest } from "../src/types";

const INSTITUTIONAL = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const DEPARTMENTAL = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const WITHDRAWN = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const classificationRow = (id: string, name: string, isActive: boolean) => ({
  id,
  name,
  nameKey: name.toLowerCase(),
  isActive,
});

/** The vocabulary as the server holds it — including a deactivated row it must not offer by default. */
const VOCABULARY = [
  classificationRow(INSTITUTIONAL, "Institutional", true),
  classificationRow(DEPARTMENTAL, "Departmental", true),
  classificationRow(WITHDRAWN, "Organizational (old)", false),
];

let askedFor: URL[] = [];
let failTheRead = false;
/** When set, every classifications read waits on it — holds a retry in flight so it can be observed. */
let gate: Promise<void> | undefined;

function serve(): void {
  vi.stubGlobal("fetch", async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname.endsWith("/event-classifications")) {
      askedFor.push(url);
      if (gate !== undefined) await gate;
      if (failTheRead) return Promise.resolve(json(500, { title: "boom", status: 500 }));
      // The server's own default: active only unless `includeInactive=true` is asked for.
      const includeInactive = url.searchParams.get("includeInactive") === "true";
      const items = VOCABULARY.filter((c) => includeInactive || c.isActive);
      return Promise.resolve(json(200, { items, page: 1, pageSize: 200, total: items.length, hasMore: false }));
    }
    return Promise.resolve(json(404, { title: "not stubbed", status: 404 }));
  });
}

const storedEvent = (overrides: Partial<EventItem> = {}): EventItem => ({
  id: "44444444-4444-4444-8444-444444444444",
  name: "Freshman Orientation",
  description: undefined,
  location: "Gym",
  startAt: "2026-09-01T01:00:00Z",
  endAt: "2026-09-01T05:00:00Z",
  attendanceMode: "Single",
  graceMinutes: 15,
  requireRegistration: false,
  status: "Draft",
  issuesCertificates: false,
  ...overrides,
});

const PICKER_NAME = /^event classification/i;
const RETRY_NAME = "Retry loading classifications";

function edit(event: EventItem, onSubmit: (r: EventWriteRequest) => void = () => {}) {
  return render(
    <EditEventDialog
      event={event}
      scope="everything"
      onClose={() => {}}
      onSubmit={onSubmit}
      running={false}
      failure={undefined}
    />,
  );
}

/** Waits for the read to settle (the select is enabled), opens it, and returns the option labels. */
async function openedOptionNames(): Promise<string[]> {
  const picker = await screen.findByRole("combobox", { name: PICKER_NAME });
  await waitFor(() => expect(picker.getAttribute("aria-disabled")).not.toBe("true"));
  await act(async () => {
    fireEvent.mouseDown(picker);
  });
  const listbox = await screen.findByRole("listbox");
  return within(listbox)
    .getAllByRole("option")
    .map((o) => o.textContent ?? "");
}

beforeEach(() => {
  askedFor = [];
  failTheRead = false;
  gate = undefined;
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("The event form's classification picker", () => {
  it("offers only active classifications", async () => {
    render(<NewEventDialog onClose={() => {}} onSubmit={() => {}} running={false} failure={undefined} />);

    const names = await openedOptionNames();

    expect(names).toEqual(["None", "Institutional", "Departmental"]);
    // Asked for without `includeInactive=true`: the withdrawn row never came over the wire.
    expect(askedFor.every((u) => u.searchParams.get("includeInactive") !== "true")).toBe(true);
  });

  it("keeps the event's deactivated classification, labelled", async () => {
    edit(storedEvent({ eventClassificationId: WITHDRAWN, eventClassificationName: "Organizational (old)" }));

    const names = await openedOptionNames();

    expect(names).toEqual(["None", "Institutional", "Departmental", "Organizational (old) (deactivated)"]);
    // And it is the selected one, so an untouched save re-sends it rather than clearing it.
    const kept = screen.getByRole("option", { name: "Organizational (old) (deactivated)" });
    expect(kept.getAttribute("aria-selected")).toBe("true");
  });

  it("does not label an active stored classification as deactivated, nor list it twice", async () => {
    edit(storedEvent({ eventClassificationId: INSTITUTIONAL, eventClassificationName: "Institutional" }));

    const names = await openedOptionNames();

    expect(names).toEqual(["None", "Institutional", "Departmental"]);
  });

  it("sends the chosen classification, and null for None", async () => {
    const onSubmit = vi.fn<(request: EventWriteRequest) => void>();
    edit(storedEvent({ eventClassificationId: INSTITUTIONAL, eventClassificationName: "Institutional" }), onSubmit);

    await openedOptionNames();
    fireEvent.click(screen.getByRole("option", { name: "Departmental" }));
    fireEvent.click(screen.getByRole("button", { name: /^save changes$/i }));
    expect(onSubmit.mock.calls[0][0].eventClassificationId).toBe(DEPARTMENTAL);

    const picker = screen.getByRole("combobox", { name: PICKER_NAME });
    await act(async () => {
      fireEvent.mouseDown(picker);
    });
    fireEvent.click(await screen.findByRole("option", { name: "None" }));
    fireEvent.click(screen.getByRole("button", { name: /^save changes$/i }));
    expect(onSubmit.mock.calls[1][0].eventClassificationId).toBeNull();
  });

  it("says so when the classifications cannot be loaded, and keeps the stored one", async () => {
    failTheRead = true;
    edit(storedEvent({ eventClassificationId: WITHDRAWN, eventClassificationName: "Organizational (old)" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/classifications could not be loaded/i);
    expect(screen.getByRole("button", { name: RETRY_NAME })).toBeTruthy();
    // Not blanked: the event still shows the classification it holds, so a save does not clear it.
    expect(screen.getByRole("combobox", { name: PICKER_NAME }).textContent).toContain("Organizational (old)");
  });

  it("announces a failed load in its own element, not by promoting the standing hint to an alert", async () => {
    failTheRead = true;
    edit(storedEvent());

    const alert = await screen.findByRole("alert");
    // The hint stays a plain helper; the alert is a separate node that mounted when the read failed.
    const hint = screen.getByText(/reusable audience definitions are filtered by classification/i);
    expect(hint.getAttribute("role")).toBeNull();
    expect(alert).not.toBe(hint);
    expect(hint.contains(alert)).toBe(false);
  });

  it("keeps Retry mounted and disabled while it re-reads, then hands focus on", async () => {
    failTheRead = true;
    edit(storedEvent());

    const retry = await screen.findByRole("button", { name: RETRY_NAME });
    retry.focus();
    expect(document.activeElement).toBe(retry);

    // Hold the second read in flight and let it succeed once released.
    let release: () => void = () => {};
    gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    failTheRead = false;
    fireEvent.click(retry);

    // Mid-flight: the very same button node is still in the document (not unmounted, so focus is not
    // dropped to <body>), and it is disabled so a second press cannot stack a read.
    await waitFor(() => expect(askedFor.length).toBe(2));
    const during = screen.getByRole("button", { name: RETRY_NAME });
    expect(during).toBe(retry);
    expect(retry.isConnected).toBe(true);
    expect((retry as HTMLButtonElement).disabled).toBe(true);

    await act(async () => {
      release();
    });

    // Landed: the button has done its job, and focus is on the select rather than lost with it.
    await waitFor(() => expect(screen.queryByRole("button", { name: RETRY_NAME })).toBeNull());
    const picker = screen.getByRole("combobox", { name: PICKER_NAME });
    await waitFor(() => expect(document.activeElement).toBe(picker));
    expect(screen.queryByRole("alert")).toBeNull();
  });
});

describe("The classification picker in the edit scopes", () => {
  it("is enabled in the descriptive scope of a Cancelled event", async () => {
    render(
      <EditEventDialog
        event={storedEvent({ status: "Cancelled", eventClassificationId: INSTITUTIONAL, eventClassificationName: "Institutional" })}
        scope="descriptive"
        onClose={() => {}}
        onSubmit={() => {}}
        running={false}
        failure={undefined}
      />,
    );

    const picker = await screen.findByRole("combobox", { name: PICKER_NAME });
    // Past the load (enabled by "not loading" alone would pass vacuously while it reads), then still enabled.
    await waitFor(() => expect(askedFor.length).toBe(1));
    await act(async () => {});
    expect(picker.getAttribute("aria-disabled")).not.toBe("true");
    // Meanwhile the schedule really is locked, so this is the descriptive scope and not a vacuous one.
    expect((screen.getByLabelText(/^grace after start/i) as HTMLInputElement).disabled).toBe(true);
  });

  it("tells a Cancelled event's editor that the classification is still editable", () => {
    render(
      <EditEventDialog
        event={storedEvent({ status: "Cancelled" })}
        scope="descriptive"
        onClose={() => {}}
        onSubmit={() => {}}
        running={false}
        failure={undefined}
      />,
    );

    const notice = screen.getByText(/so its schedule, grace period, attendance mode/i).textContent ?? "";
    expect(notice).toMatch(/classification/i);
    expect(notice).toMatch(/certificates/i);
  });

  it("round-trips the stored classification id on an untouched save", async () => {
    const onSubmit = vi.fn<(request: EventWriteRequest) => void>();
    edit(storedEvent({ eventClassificationId: WITHDRAWN, eventClassificationName: "Organizational (old)" }), onSubmit);

    const picker = await screen.findByRole("combobox", { name: PICKER_NAME });
    await waitFor(() => expect(picker.getAttribute("aria-disabled")).not.toBe("true"));
    fireEvent.click(screen.getByRole("button", { name: /^save changes$/i }));

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0].eventClassificationId).toBe(WITHDRAWN);
  });
});
