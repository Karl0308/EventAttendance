/** @vitest-environment happy-dom */

// `PreRegistration`'s manual picker — B1 gate fix. Pre-registration opens on `events.read`, but the manual
// picker is a write: an Organizer (`events.write`, `students.read`, no `personnel.read`) must get its people
// from `GET /events/personnel-picker`, a failure must be visible while tapping keeps working, and a Viewer
// must trigger no people load at all.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import PreRegistration from "./PreRegistration";
import AuthProvider from "../components/AuthProvider";
import { api } from "../api";
import { PERMISSIONS } from "../permissions";
import { beginSession, resetSessionForTests } from "../authSession";
import type { AuthUser, PersonnelPickerEntry, PreRegistrationSession, Student } from "../types";

const SESSION: PreRegistrationSession = {
  id: "ses-1",
  name: "Orientation",
  audienceDefinitionId: "aud-1",
  audienceName: "Freshmen",
  capacity: 10,
  registeredCount: 0,
  isClosed: false,
  isFull: false,
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

/** Opens the manage dialog for the one session. */
async function openManage() {
  render(
    <MemoryRouter>
      <AuthProvider>
        <PreRegistration />
      </AuthProvider>
    </MemoryRouter>,
  );
  fireEvent.click(await screen.findByRole("button", { name: "Manage Orientation" }));
  await screen.findByRole("button", { name: "Add" });
}

beforeEach(() => {
  resetSessionForTests();
  vi.spyOn(api, "listPreRegistrationSessions").mockResolvedValue([SESSION]);
  vi.spyOn(api, "listAudienceDefinitions").mockResolvedValue([]);
  vi.spyOn(api, "listPreRegistrants").mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
  resetSessionForTests();
  vi.restoreAllMocks();
});

describe("PreRegistration manual picker", () => {
  it("loads for an Organizer without personnel.read, from the picker route", async () => {
    beginSession("t", userWith(PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite, PERMISSIONS.studentsRead));
    vi.spyOn(api, "listStudents").mockResolvedValue([STUDENT]);
    const picker = vi.spyOn(api, "listPersonnelPicker").mockResolvedValue([STAFF]);
    const full = vi.spyOn(api, "listPersonnel");

    await openManage();

    await waitFor(() => expect(picker).toHaveBeenCalledTimes(1));
    expect(full).not.toHaveBeenCalled();
    expect(screen.queryByText(/could not be loaded/)).toBeNull();
  });

  it("shows a visible error when the picker fails, and tapping still works", async () => {
    beginSession("t", userWith(PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite, PERMISSIONS.studentsRead));
    vi.spyOn(api, "listStudents").mockResolvedValue([STUDENT]);
    vi.spyOn(api, "listPersonnelPicker").mockRejectedValue(new Error("picker down"));

    await openManage();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("could not be loaded");
    expect(alert.textContent).toContain("picker down");

    // Tapping does not depend on the people list.
    fireEvent.click(screen.getByRole("button", { name: "Tap a card" }));
    fireEvent.change(screen.getByLabelText("Card UID"), { target: { value: "04A1B2C3" } });
    expect((screen.getByRole("button", { name: "Register" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("makes no people call for a Viewer and shows no error", async () => {
    beginSession("t", userWith(PERMISSIONS.eventsRead));
    const students = vi.spyOn(api, "listStudents");
    const picker = vi.spyOn(api, "listPersonnelPicker");
    const full = vi.spyOn(api, "listPersonnel");

    await openManage();

    expect(students).not.toHaveBeenCalled();
    expect(picker).not.toHaveBeenCalled();
    expect(full).not.toHaveBeenCalled();
    expect(screen.queryByText(/could not be loaded/)).toBeNull();
  });
});
