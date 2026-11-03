/** @vitest-environment happy-dom */

// `EventAudienceFormDialog`'s people picker — B1 gate fix. An Organizer holds `events.write` and
// `students.read` but not `personnel.read` (ADR-015 D-104), so the picker must read personnel from
// `GET /events/personnel-picker`; `GET /personnel` would be refused and take the students down with it.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import EventAudienceFormDialog from "./EventAudienceFormDialog";
import AuthProvider from "./AuthProvider";
import { api } from "../api";
import { PERMISSIONS } from "../permissions";
import { beginSession, resetSessionForTests } from "../authSession";
import type {
  AudienceDefinition,
  AudienceOptions,
  AuthUser,
  PersonnelPickerEntry,
  Student,
} from "../types";

const EMPTY_OPTIONS: AudienceOptions = {
  departments: [],
  programs: [],
  yearLevels: [],
  sections: [],
  classifications: [],
  organizations: [],
};

const SPECIFIC: AudienceDefinition = {
  id: "aud-1",
  name: "Hand-picked",
  eventClassificationId: "cls-1",
  eventClassificationName: "Seminars",
  audienceType: "SpecificIndividuals",
  criteria: { studentIds: [], personnelIds: [] },
  isActive: true,
};

const STUDENT = { id: "s-1", studentNumber: "S-001", fullName: "Cruz, Ana" } as Student;
const STAFF: PersonnelPickerEntry = { id: "p-1", personnelNumber: "P-001", fullName: "Santos, Maria" };

const userWith = (...permissions: readonly string[]): AuthUser => ({
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "someone@usa.edu.ph",
  fullName: "Some One",
  permissions: [...permissions],
});

const ORGANIZER = userWith(PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite, PERMISSIONS.studentsRead);

function showDialog() {
  render(
    <MemoryRouter>
      <AuthProvider>
        <EventAudienceFormDialog
          definition={SPECIFIC}
          classifications={[{ id: "cls-1", name: "Seminars" }]}
          others={[]}
          onClose={() => {}}
          onSubmit={() => {}}
          running={false}
          failure={undefined}
        />
      </AuthProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  resetSessionForTests();
  beginSession("t", ORGANIZER);
  vi.spyOn(api, "listAudienceOptions").mockResolvedValue(EMPTY_OPTIONS);
});

afterEach(() => {
  cleanup();
  resetSessionForTests();
  vi.restoreAllMocks();
});

describe("EventAudienceFormDialog — people picker source", () => {
  it("loads personnel from the picker route and never from GET /personnel", async () => {
    vi.spyOn(api, "listStudents").mockResolvedValue([STUDENT]);
    const picker = vi.spyOn(api, "listPersonnelPicker").mockResolvedValue([STAFF]);
    const full = vi.spyOn(api, "listPersonnel");

    showDialog();

    await waitFor(() => expect(picker).toHaveBeenCalledTimes(1));
    expect(full).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByText("Loading the roster…")).toBeNull());
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("says so, visibly, when the picker route fails", async () => {
    vi.spyOn(api, "listStudents").mockResolvedValue([STUDENT]);
    const picker = vi.spyOn(api, "listPersonnelPicker").mockRejectedValue(new Error("picker down"));

    showDialog();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("could not be loaded");
    expect(alert.textContent).toContain("picker down");
    // A failed load is shown once, not retried on every render.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(picker).toHaveBeenCalledTimes(1);
  });

  it("makes no people call for a Viewer and shows no error", async () => {
    beginSession("t", userWith(PERMISSIONS.eventsRead));
    const students = vi.spyOn(api, "listStudents");
    const picker = vi.spyOn(api, "listPersonnelPicker");
    const full = vi.spyOn(api, "listPersonnel");

    showDialog();
    await waitFor(() => expect(api.listAudienceOptions).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(students).not.toHaveBeenCalled();
    expect(picker).not.toHaveBeenCalled();
    expect(full).not.toHaveBeenCalled();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  // #706 F2-2 carry-over: the Viewer used to see an empty picker and a save button, with no word on why.
  it("shows a Viewer a read-only note instead of an empty picker and a save button", async () => {
    beginSession("t", userWith(PERMISSIONS.eventsRead));
    vi.spyOn(api, "listStudents");
    vi.spyOn(api, "listPersonnelPicker");

    showDialog();
    await waitFor(() => expect(api.listAudienceOptions).toHaveBeenCalled());

    expect(screen.queryByLabelText("Students and employees")).toBeNull();
    expect(screen.queryByRole("button", { name: /save changes|create audience/i })).toBeNull();
    expect(screen.getAllByText(/events\.write permission/i).length).toBeGreaterThan(0);
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeTruthy();
  });

  it("still offers an Organizer the picker and the save button", async () => {
    vi.spyOn(api, "listStudents").mockResolvedValue([STUDENT]);
    vi.spyOn(api, "listPersonnelPicker").mockResolvedValue([STAFF]);

    showDialog();

    expect(await screen.findByLabelText("Students and employees")).toBeTruthy();
    expect(screen.getByRole("button", { name: /save changes/i })).toBeTruthy();
    expect(screen.queryByText(/events\.write permission/i)).toBeNull();
  });
});
