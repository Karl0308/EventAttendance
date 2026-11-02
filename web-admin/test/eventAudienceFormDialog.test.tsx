/** @vitest-environment happy-dom */

// Task 5: the "Who it covers" control in the create/edit Event Audience dialog.
//
// The dialog is rendered directly with `fetch` stubbed (it only reads `/event-audiences/options`), so the
// assertions are on what the admin sees and on what `onSubmit` receives.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";

import { ApiError } from "../src/api";
import AuthProvider from "../src/components/AuthProvider";
import EventAudienceFormDialog from "../src/components/EventAudienceFormDialog";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AudienceDefinition, AudienceDefinitionWriteRequest, AuthUser } from "../src/types";

// The dialog reads the signed-in user (it only loads the people picker for `events.write`), so it has to
// be rendered under the provider like everything else below `RequireAuth`. This file had no session, so
// every test in it threw "useAuth was called outside <AuthProvider>" before F1 touched it.
const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["events.read", "events.write"],
};

const CLASSIFICATION = { id: "c-1", name: "Institutional" };
const SCOPE_NAME = /who it covers/i;
const SCOPE_REFUSAL =
  "Choose who this audience covers: Students, Employees, Both.";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

beforeEach(() => {
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  vi.stubGlobal("fetch", () =>
    Promise.resolve(
      json(200, {
        departments: [],
        programs: [],
        yearLevels: [],
        sections: [],
        classifications: [],
        organizations: [],
      }),
    ),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

function renderDialog(over: { definition?: AudienceDefinition; failure?: { error: unknown } } = {}) {
  const onSubmit = vi.fn<(request: AudienceDefinitionWriteRequest) => void>();
  render(
    <AuthProvider>
      <EventAudienceFormDialog
        definition={over.definition}
        classifications={[CLASSIFICATION]}
        others={[]}
        onClose={() => {}}
        onSubmit={onSubmit}
        running={false}
        failure={over.failure}
      />
    </AuthProvider>,
  );
  return onSubmit;
}

/** Opens an MUI Select by its accessible name and clicks the option with the given text. */
function choose(selectName: RegExp, optionText: string): void {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: selectName }));
  fireEvent.click(within(screen.getByRole("listbox")).getByRole("option", { name: optionText }));
}

const scopeCombobox = () => screen.queryByRole("combobox", { name: SCOPE_NAME });

/** What the Select shows: MUI paints a zero-width space for an empty value. */
const shownValue = (el: HTMLElement): string => (el.textContent ?? "").replace(/​/g, "").trim();

function addValue(label: RegExp, value: string): void {
  const input = screen.getByRole("combobox", { name: label });
  fireEvent.change(input, { target: { value } });
  fireEvent.keyDown(input, { key: "Enter" });
}

function fillBasics(): void {
  fireEvent.change(screen.getByLabelText(/^name/i), { target: { value: "CS Society Members" } });
  choose(/event classification/i, CLASSIFICATION.name);
}

describe("the Who it covers control", () => {
  it("is offered for Department, Organization and Custom, and for no single-population type", () => {
    renderDialog();

    for (const label of ["Department", "Organization", "Custom"]) {
      choose(/audience type/i, label);
      expect(scopeCombobox(), label).not.toBeNull();
    }
    for (const label of ["Program", "Year level", "Section", "Employee classification"]) {
      choose(/audience type/i, label);
      expect(scopeCombobox(), label).toBeNull();
    }
  });

  it("offers Students, Employees and Both, University-wide included, with no preselection on a mixed type", () => {
    renderDialog();

    // University-wide keeps its own default, in the same shared labels.
    expect(shownValue(screen.getByRole("combobox", { name: SCOPE_NAME }))).toBe("Both");

    choose(/audience type/i, "Department");
    const scope = screen.getByRole("combobox", { name: SCOPE_NAME });
    expect(shownValue(scope)).toBe("");

    fireEvent.mouseDown(scope);
    const labels = within(screen.getByRole("listbox"))
      .getAllByRole("option")
      .map((o) => o.textContent);
    expect(labels).toEqual(["Students", "Employees", "Both"]);
  });

  it("starts empty after switching from University-wide's default Both to a mixed type", () => {
    renderDialog();
    expect(shownValue(screen.getByRole("combobox", { name: SCOPE_NAME }))).toBe("Both");

    choose(/audience type/i, "Organization");

    expect(shownValue(screen.getByRole("combobox", { name: SCOPE_NAME }))).toBe("");
  });

  it("blocks save until it is chosen, then sends the chosen scope", async () => {
    const onSubmit = renderDialog();
    fillBasics();
    choose(/audience type/i, "Department");
    addValue(/^departments/i, "CICSS");

    fireEvent.click(screen.getByRole("button", { name: /create audience/i }));
    expect(onSubmit).not.toHaveBeenCalled();
    expect(await screen.findByText("Choose who it covers")).toBeTruthy();

    choose(SCOPE_NAME, "Students");
    fireEvent.click(screen.getByRole("button", { name: /create audience/i }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0][0]).toMatchObject({
      audienceType: "Department",
      criteria: { scope: "Students", departments: ["CICSS"] },
    });
  });

  it("shows a legacy row with no scope as unselected and keeps save blocked", async () => {
    const legacy: AudienceDefinition = {
      id: "d-1",
      name: "Legacy Dept",
      eventClassificationId: CLASSIFICATION.id,
      eventClassificationName: CLASSIFICATION.name,
      audienceType: "Department",
      criteria: { departments: ["CICSS"] },
      isActive: true,
    };
    const onSubmit = renderDialog({ definition: legacy });

    expect(shownValue(screen.getByRole("combobox", { name: SCOPE_NAME }))).toBe("");
    fireEvent.click(screen.getByRole("button", { name: /save changes/i }));

    expect(onSubmit).not.toHaveBeenCalled();
    expect(await screen.findByText("Choose who it covers")).toBeTruthy();
  });
});

describe("Custom: pickers follow the chosen population", () => {
  const pickerNames = {
    programs: /^programs/i,
    yearLevels: /^year levels/i,
    sections: /^sections/i,
    classifications: /^employee classifications/i,
  };
  const has = (name: RegExp) => screen.queryByRole("combobox", { name }) !== null;

  it("hides Employee classifications under Students and Programs, Year levels, Sections under Employees", () => {
    renderDialog();
    choose(/audience type/i, "Custom");

    choose(SCOPE_NAME, "Students");
    expect(has(pickerNames.classifications)).toBe(false);
    expect(has(pickerNames.programs) && has(pickerNames.yearLevels) && has(pickerNames.sections)).toBe(true);

    choose(SCOPE_NAME, "Employees");
    expect(has(pickerNames.programs)).toBe(false);
    expect(has(pickerNames.yearLevels)).toBe(false);
    expect(has(pickerNames.sections)).toBe(false);
    expect(has(pickerNames.classifications)).toBe(true);

    choose(SCOPE_NAME, "Both");
    expect(has(pickerNames.classifications) && has(pickerNames.programs)).toBe(true);
  });

  it("does not send the values of a picker that was hidden", async () => {
    const onSubmit = renderDialog();
    fillBasics();
    choose(/audience type/i, "Custom");
    addValue(/^programs/i, "BSIT");
    addValue(/^employee classifications/i, "Faculty");
    choose(SCOPE_NAME, "Employees");

    fireEvent.click(screen.getByRole("button", { name: /create audience/i }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const sent = onSubmit.mock.calls[0][0].criteria;
    expect(sent.scope).toBe("Employees");
    expect(sent.classifications).toEqual(["Faculty"]);
    expect(sent.programs).toEqual([]);
  });

  it("refuses, on screen, a scope that leaves no side", async () => {
    const onSubmit = renderDialog();
    fillBasics();
    choose(/audience type/i, "Custom");
    addValue(/^employee classifications/i, "Faculty");
    choose(SCOPE_NAME, "Students");

    fireEvent.click(screen.getByRole("button", { name: /create audience/i }));

    expect(onSubmit).not.toHaveBeenCalled();
    expect(await screen.findByText(/Students need at least one/)).toBeTruthy();
  });
});

describe("the Who it covers hint says personnel are counted (ADR-015 D-102, W6)", () => {
  const hintOf = (): string | null =>
    document.getElementById(screen.getByRole("combobox", { name: SCOPE_NAME }).getAttribute("aria-describedby") ?? "")
      ?.textContent ?? null;

  it("hints that Employees are counted in the expected audience", () => {
    renderDialog();
    choose(/audience type/i, "Department");
    choose(SCOPE_NAME, "Employees");

    expect(hintOf()).toBe("Employees are counted in the event’s expected audience.");
  });

  it("hints that Both counts students and employees", () => {
    renderDialog();
    choose(/audience type/i, "Department");
    choose(SCOPE_NAME, "Both");

    expect(hintOf()).toBe("Students and employees are both counted in the event’s expected audience.");
  });

  it("shows no personnel-counting hint under Students", () => {
    renderDialog();
    choose(/audience type/i, "Department");
    choose(SCOPE_NAME, "Students");

    expect(screen.getByRole("combobox", { name: SCOPE_NAME }).getAttribute("aria-describedby")).toBeNull();
    expect(screen.queryByText(/are counted in the event/i)).toBeNull();
    expect(screen.queryByText(/both counted/i)).toBeNull();
  });
});

describe("a refused save", () => {
  it("shows the server's detail text on a 400, not a generic line", () => {
    const error = new ApiError("http", 400, `Create audience failed (400: ${SCOPE_REFUSAL}).`, {
      shape: "write",
      problem: { status: 400, code: "ValidationFailed", detail: SCOPE_REFUSAL },
    });
    renderDialog({ failure: { error } });

    const alert = screen.getByRole("alert");
    expect(alert.textContent).toContain(SCOPE_REFUSAL);
  });
});
