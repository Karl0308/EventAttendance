/** @vitest-environment happy-dom */

// `MigrateStudentsDialog` — "Move students to Personnel". Preview (dry run) renders the counts and the rows with
// conflicts marked, the confirm step says what the move does before anything runs, the run result lists what was
// left behind, and the page hides the entry point from a user without `personnel.migrate`.
//
// The dialog is driven through its props — the page owns both writes, so `onPreview` / `onRun` are the seam. The
// last test renders the real page to check the permission gate.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import MigrateStudentsDialog from "./MigrateStudentsDialog";
import AuthProvider from "./AuthProvider";
import PersonnelPage from "../pages/Personnel";
import { api } from "../api";
import { PERMISSIONS } from "../permissions";
import { beginSession, resetSessionForTests } from "../authSession";
import type { AuthUser, MigrateFromStudentsResult, MigrateFromStudentsRow } from "../types";

const row = (over: Partial<MigrateFromStudentsRow>): MigrateFromStudentsRow => ({
  studentId: "s-1",
  studentNumber: "72001",
  fullName: "Cruz, Ana",
  rfidUid: "0012503326",
  outcome: "Create",
  message: "Will be created.",
  ...over,
});

const PLAN: MigrateFromStudentsResult = {
  dryRun: true,
  prefix: "7200",
  matched: 3,
  created: 1,
  updated: 1,
  conflicts: 1,
  rows: [
    row({}),
    row({ studentId: "s-2", studentNumber: "72002", fullName: "Lim, Ben", outcome: "Update", message: "Will be updated." }),
    row({
      studentId: "s-3",
      studentNumber: "72003",
      fullName: "Reyes, Cara",
      rfidUid: undefined,
      outcome: "Conflict",
      conflictCode: "ActiveEvent",
      message: "Reyes is checked in to an active event.",
    }),
  ],
};
const DONE: MigrateFromStudentsResult = { ...PLAN, dryRun: false };

type Overrides = Partial<React.ComponentProps<typeof MigrateStudentsDialog>>;

function renderDialog(over: Overrides = {}) {
  const onPreview = vi.fn<(prefix: string) => void>();
  const onRun = vi.fn<(prefix: string) => void>();
  const props = {
    onClose: () => {},
    onPreview,
    onRun,
    previewing: false,
    preview: undefined,
    previewError: undefined,
    running: false,
    result: undefined,
    runError: undefined,
    ...over,
  };
  const view = render(<MigrateStudentsDialog {...props} />);
  return { onPreview, onRun, rerender: (next: Overrides) => view.rerender(<MigrateStudentsDialog {...props} {...next} />) };
}

afterEach(() => {
  cleanup();
  resetSessionForTests();
  vi.restoreAllMocks();
});

describe("MigrateStudentsDialog", () => {
  it("previews with the default prefix, and rejects one under four characters", () => {
    const { onPreview } = renderDialog();
    const field = screen.getByLabelText("Student number prefix") as HTMLInputElement;
    expect(field.value).toBe("7200");

    fireEvent.change(field, { target: { value: "72" } });
    expect((screen.getByRole("button", { name: "Preview" }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(field, { target: { value: " 7200 " } });
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    expect(onPreview).toHaveBeenCalledWith("7200");
  });

  it("renders the preview counts and rows, with the conflict marked and explained", () => {
    renderDialog({ preview: PLAN });

    expect(screen.getByText("1 will be created")).toBeTruthy();
    expect(screen.getByText("1 will be updated")).toBeTruthy();
    expect(screen.getByText("1 conflict")).toBeTruthy();

    const table = screen.getByRole("table", { name: "Students to be moved" });
    const rows = within(table).getAllByRole("row");
    // header + 3 students, conflicts listed first
    expect(rows).toHaveLength(4);
    expect(within(rows[1]).getByText("Conflict")).toBeTruthy();
    expect(within(rows[1]).getByText("Reyes is checked in to an active event.")).toBeTruthy();
    expect(within(table).getAllByText("0012503326")).toHaveLength(2);
  });

  it("disables the move button when nothing is movable", () => {
    renderDialog({ preview: { ...PLAN, created: 0, updated: 0, conflicts: 3 } });
    expect((screen.getByRole("button", { name: "Move 0 students" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("disables the move button once the prefix no longer matches the preview", () => {
    renderDialog({ preview: PLAN });
    const move = screen.getByRole("button", { name: "Move 2 students" }) as HTMLButtonElement;
    expect(move.disabled).toBe(false);

    fireEvent.change(screen.getByLabelText("Student number prefix"), { target: { value: "7300" } });
    expect(move.disabled).toBe(true);
  });

  it("asks for explicit confirmation, then runs with the previewed prefix", () => {
    const { onRun } = renderDialog({ preview: PLAN });

    fireEvent.click(screen.getByRole("button", { name: "Move 2 students" }));
    expect(onRun).not.toHaveBeenCalled();

    const warning = screen.getByRole("alert");
    expect(warning.textContent).toContain("removed from the Students list");
    expect(warning.textContent).toContain("RFID card moves");
    expect(warning.textContent).toContain("Past attendance stays");

    fireEvent.click(screen.getByRole("button", { name: "Yes, move 2 students" }));
    expect(onRun).toHaveBeenCalledTimes(1);
    expect(onRun).toHaveBeenCalledWith("7200");
  });

  it("shows the run result: counts and the students that stayed behind", () => {
    renderDialog({ preview: PLAN, result: DONE });

    expect(screen.getByText("1 created")).toBeTruthy();
    expect(screen.getByText("1 updated")).toBeTruthy();
    expect(screen.getByText("1 conflict")).toBeTruthy();
    const table = screen.getByRole("table", { name: "Students that were processed" });
    expect(within(table).getByText("Reyes is checked in to an active event.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Close" })).toBeTruthy();
  });

  it("tells the user a failed run may have moved some students", () => {
    const { rerender } = renderDialog({ preview: PLAN });
    fireEvent.click(screen.getByRole("button", { name: "Move 2 students" }));
    rerender({ preview: PLAN, runError: new Error("boom") });

    const alerts = screen.getAllByRole("alert");
    expect(alerts.some((a) => a.textContent?.includes("boom"))).toBe(true);
  });
});

describe("Personnel page — Move students to Personnel entry point", () => {
  const userWith = (...permissions: readonly string[]): AuthUser => ({
    id: "11111111-1111-1111-1111-111111111111",
    schoolId: "22222222-2222-2222-2222-222222222222",
    email: "someone@usa.edu.ph",
    fullName: "Some One",
    permissions: [...permissions],
  });

  beforeEach(() => {
    resetSessionForTests();
    vi.spyOn(api, "listPersonnel").mockResolvedValue([]);
    vi.spyOn(api, "listPersonnelOrganizations").mockResolvedValue([]);
  });

  const renderPage = () =>
    render(
      <MemoryRouter>
        <AuthProvider>
          <PersonnelPage />
        </AuthProvider>
      </MemoryRouter>,
    );

  it("is hidden for a user without personnel.migrate, even with personnel.write", async () => {
    beginSession("t", userWith(PERMISSIONS.personnelRead, PERMISSIONS.personnelWrite));
    renderPage();
    await screen.findByRole("button", { name: "Import CSV" });
    expect(screen.queryByRole("button", { name: "Move students to Personnel" })).toBeNull();
  });

  it("opens the dialog, previews with dryRun true and runs with dryRun false", async () => {
    beginSession("t", userWith(PERMISSIONS.personnelRead, PERMISSIONS.personnelMigrate));
    const call = vi
      .spyOn(api, "migrateStudentsToPersonnel")
      .mockImplementation((req) => Promise.resolve(req.dryRun === false ? DONE : PLAN));
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: "Move students to Personnel" }));
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    await screen.findByText("1 will be created");
    expect(call).toHaveBeenLastCalledWith({ prefix: "7200", dryRun: true });

    fireEvent.click(screen.getByRole("button", { name: "Move 2 students" }));
    fireEvent.click(screen.getByRole("button", { name: "Yes, move 2 students" }));
    await screen.findByText("1 created");
    expect(call).toHaveBeenLastCalledWith({ prefix: "7200", dryRun: false });
    // the list is re-read after the run
    await waitFor(() => expect(vi.mocked(api.listPersonnel).mock.calls.length).toBeGreaterThanOrEqual(2));
  });
});
