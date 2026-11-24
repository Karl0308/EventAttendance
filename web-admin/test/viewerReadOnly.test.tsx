/** @vitest-environment happy-dom */

// RoleViewRestrictions: a Viewer gets a strictly read-only SPA.
//
// "All Create / New / Import / Record buttons are hidden … The Actions column in all tables is either
// hidden or shows no clickable icons for the Viewer role." The gate is the write permission each button's
// route needs, not the role's name, so what is pinned here is that read-only permissions (the Viewer seed:
// students.read, events.read, attendance.read, academic.read) get none of the write controls, and that the
// same page for a token WITH the write permission still shows them. The second half is what keeps the
// first from passing because a page simply failed to render.
//
// Four grids (Students, Event audiences, Pre-registration, Roles) carry an Actions column that mixes a
// READ affordance (RFID cards, View attendees, View registrants, View permissions) with write icons. A
// Viewer keeps the column and the read affordance and loses only the write icons: hiding the whole
// column would take the read-only view away with them. The other grids' Actions columns only write, so
// a Viewer gets no column at all.
//
// A convenience, not a control: the server refuses every one of these writes without the permission too.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { ReactElement } from "react";

import AuthProvider from "../src/components/AuthProvider";
import Events from "../src/pages/Events";
import EventAudiences from "../src/pages/EventAudiences";
import EventClassifications from "../src/pages/EventClassifications";
import ManualRfidRecords from "../src/pages/ManualRfidRecords";
import PreRegistration from "../src/pages/PreRegistration";
import Roles from "../src/pages/Roles";
import Students from "../src/pages/Students";
import Terms from "../src/pages/Terms";
import { api } from "../src/api";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { PERMISSIONS, type PermissionCode } from "../src/permissions";
import type {
  AudienceDefinition,
  AuthUser,
  EventClassification,
  ManualIdEntry,
  PreRegistrant,
  PreRegistrationSession,
  Role,
  Student,
  Term,
} from "../src/types";

/** What the seeded Viewer role holds (`EamsRoles.cs`). */
const VIEWER: readonly PermissionCode[] = [
  PERMISSIONS.studentsRead,
  PERMISSIONS.eventsRead,
  PERMISSIONS.attendanceRead,
  PERMISSIONS.academicRead,
];

const userWith = (permissions: readonly string[]): AuthUser => ({
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "someone@usa.edu.ph",
  fullName: "Some One",
  permissions: [...permissions],
});

const STUDENT: Student = {
  id: "s-1",
  studentNumber: "2026-0001",
  fullName: "Cruz, Ana",
  firstName: "Ana",
  lastName: "Cruz",
  course: "BSIT",
  yearLevel: "1",
  section: "A",
  status: "Active",
  cards: [{ id: "card-1", cardUid: "04A1B2C3D4", isActive: true }],
};

const CLASSIFICATION: EventClassification = {
  id: "c-1",
  name: "Institutional",
  nameKey: "institutional",
  isActive: true,
};

const AUDIENCE: AudienceDefinition = {
  id: "a-1",
  name: "All Freshmen",
  eventClassificationId: "c-1",
  eventClassificationName: "Institutional",
  audienceType: "UniversityWide",
  criteria: {},
  isActive: true,
};

const SESSION: PreRegistrationSession = {
  id: "ses-1",
  name: "Orientation",
  audienceDefinitionId: "a-1",
  audienceName: "All Freshmen",
  capacity: 10,
  registeredCount: 0,
  isClosed: false,
  isFull: false,
};

const TERM: Term = {
  id: "t-1",
  code: "2026-1",
  schoolYear: "2026-2027",
  semester: "1st",
  isCurrent: true,
};

const MANUAL: ManualIdEntry = {
  id: "m-1",
  cardUid: "04A1B2C3",
  idNumber: "2026-0001",
  personType: "Student",
  isResolved: false,
  recordedAt: "2026-11-01T08:00:00Z",
};

const REGISTRANT: PreRegistrant = {
  id: "reg-1",
  type: "Student",
  attendeeId: "s-1",
  number: "2026-0001",
  fullName: "Cruz, Ana",
  method: "Manual",
  registeredAt: "2026-11-01T08:00:00Z",
};

const ROLE: Role = {
  id: "r-1",
  name: "Registrar",
  isSystem: false,
  userCount: 2,
  permissionCodes: ["students.read"],
};

beforeEach(() => {
  resetSessionForTests();
  vi.spyOn(api, "listStudentsPage").mockResolvedValue({
    students: [STUDENT],
    page: 1,
    pageSize: 25,
    total: 1,
  });
  vi.spyOn(api, "listEvents").mockResolvedValue([]);
  vi.spyOn(api, "listEventClassifications").mockResolvedValue([CLASSIFICATION]);
  vi.spyOn(api, "listAudienceDefinitions").mockResolvedValue([AUDIENCE]);
  vi.spyOn(api, "listPreRegistrationSessions").mockResolvedValue([SESSION]);
  vi.spyOn(api, "listTerms").mockResolvedValue([TERM]);
  vi.spyOn(api, "listManualIdEntries").mockResolvedValue([MANUAL]);
  vi.spyOn(api, "listPreRegistrants").mockResolvedValue([REGISTRANT]);
  // The register form's people picker loads these for a writer only.
  vi.spyOn(api, "listStudents").mockResolvedValue([STUDENT]);
  vi.spyOn(api, "listPersonnelPicker").mockResolvedValue([]);
  vi.spyOn(api, "listRoles").mockResolvedValue([ROLE]);
  vi.spyOn(api, "listPermissions").mockResolvedValue(["students.read", "students.write"]);
});

afterEach(() => {
  cleanup();
  resetSessionForTests();
  vi.restoreAllMocks();
});

async function showAs(permissions: readonly string[], page: ReactElement) {
  beginSession("access-token-1", userWith(permissions));
  render(
    <MemoryRouter>
      <AuthProvider>{page}</AuthProvider>
    </MemoryRouter>,
  );
  await act(async () => {});
}

interface WritePage {
  readonly name: string;
  readonly page: () => ReactElement;
  /** Something only the loaded page shows, so "no button" cannot pass on a page that never rendered. */
  readonly loaded: RegExp | string;
  /** Every Create / New / Import / Record control this page offers a writer, by accessible name. */
  readonly buttons: readonly (string | RegExp)[];
  /**
   * What the grid's Actions column holds. `write-only`: it is absent for a Viewer. `mixed`: it stays,
   * with `viewAffordance` still there and every `writeIcons` entry gone. `none`: the grid has no column.
   */
  readonly actionsColumn: "write-only" | "mixed" | "none";
  /** `mixed` only: the read-only action's accessible name, as a Viewer sees it. */
  readonly viewAffordance?: RegExp;
  /** `mixed` only: the write icons a writer sees and a Viewer must not. */
  readonly writeIcons?: readonly RegExp[];
  /** Permissions that unlock the page's write controls. */
  readonly writer: readonly PermissionCode[];
}

const WRITE_PAGES: readonly WritePage[] = [
  {
    name: "Students",
    page: () => <Students />,
    loaded: "2026-0001",
    buttons: [/new student/i, /import roster/i],
    actionsColumn: "mixed",
    viewAffordance: /^rfid cards for cruz, ana$/i,
    writeIcons: [/^edit cruz, ana$/i, /^delete cruz, ana$/i],
    writer: [PERMISSIONS.studentsRead, PERMISSIONS.studentsWrite, PERMISSIONS.sisImport],
  },
  {
    name: "Events",
    page: () => <Events />,
    loaded: /no events have been created yet/i,
    buttons: [/new event/i],
    actionsColumn: "none",
    writer: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite],
  },
  {
    name: "Event classifications",
    page: () => <EventClassifications />,
    loaded: "Institutional",
    buttons: [/create classification/i],
    actionsColumn: "write-only",
    writer: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite],
  },
  {
    name: "Event audiences",
    page: () => <EventAudiences />,
    loaded: "All Freshmen",
    buttons: [/create audience/i],
    actionsColumn: "mixed",
    viewAffordance: /^view attendees of all freshmen$/i,
    writeIcons: [/^edit all freshmen$/i, /^deactivate all freshmen$/i, /^delete all freshmen$/i],
    writer: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite],
  },
  {
    name: "Pre-registration",
    page: () => <PreRegistration />,
    loaded: "Orientation",
    buttons: [/create session/i],
    actionsColumn: "mixed",
    viewAffordance: /^view registrants of orientation$/i,
    writeIcons: [/^manage orientation$/i, /^close orientation$/i],
    writer: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite],
  },
  {
    name: "Terms",
    page: () => <Terms />,
    loaded: "2026-1",
    buttons: [/create term/i],
    actionsColumn: "write-only",
    writer: [PERMISSIONS.academicRead, PERMISSIONS.academicWrite],
  },
  {
    name: "Manual RFID records",
    page: () => <ManualRfidRecords />,
    loaded: "04A1B2C3",
    buttons: [/^record$/i],
    actionsColumn: "none",
    writer: [PERMISSIONS.attendanceRead, PERMISSIONS.attendanceWrite],
  },
];

const findLoaded = (page: WritePage) => screen.findByText(page.loaded);

describe.each(WRITE_PAGES)("$name", (page) => {
  it("shows a Viewer no Create / New / Import / Record button and no write action", async () => {
    await showAs(VIEWER, page.page());
    await findLoaded(page);

    for (const name of page.buttons) {
      expect(screen.queryByRole("button", { name }), `${String(name)} for a Viewer`).toBeNull();
      expect(screen.queryByRole("link", { name }), `${String(name)} link for a Viewer`).toBeNull();
    }

    if (page.actionsColumn === "mixed") {
      // The column stays, because the read-only view lives in it.
      expect(screen.queryByRole("columnheader", { name: "Actions" })).not.toBeNull();
      expect(
        screen.queryByRole("button", { name: page.viewAffordance }),
        "the read-only view for a Viewer",
      ).not.toBeNull();
      for (const icon of page.writeIcons ?? []) {
        expect(screen.queryByRole("button", { name: icon }), `${String(icon)} for a Viewer`).toBeNull();
      }
    } else {
      expect(screen.queryByRole("columnheader", { name: "Actions" })).toBeNull();
    }
  });

  it("still shows a token with the write permission its controls", async () => {
    await showAs(page.writer, page.page());
    await findLoaded(page);

    for (const name of page.buttons) {
      const control = screen.queryByRole("button", { name }) ?? screen.queryByRole("link", { name });
      expect(control, `${String(name)} for a writer`).not.toBeNull();
    }
    if (page.actionsColumn !== "none") {
      expect(screen.queryByRole("columnheader", { name: "Actions" })).not.toBeNull();
    }
    for (const icon of page.writeIcons ?? []) {
      expect(screen.queryByRole("button", { name: icon }), `${String(icon)} for a writer`).not.toBeNull();
    }
  });
});

// ------------------------------------------------------------------ the read-only views a Viewer opens

describe("the read-only views a Viewer opens from the Actions column", () => {
  it("RFID cards: lists the card, with no Detach and no attach form", async () => {
    await showAs(VIEWER, <Students />);
    await screen.findByText("2026-0001");
    fireEvent.click(screen.getByRole("button", { name: /^rfid cards for cruz, ana$/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("04A1B2C3D4")).toBeTruthy();
    expect(within(dialog).queryByRole("button", { name: /detach/i })).toBeNull();
    expect(within(dialog).queryByRole("textbox")).toBeNull();
  });

  it("RFID cards: a writer still gets Detach and the attach form", async () => {
    await showAs([PERMISSIONS.studentsRead, PERMISSIONS.studentsWrite], <Students />);
    await screen.findByText("2026-0001");
    fireEvent.click(screen.getByRole("button", { name: /^rfid cards for cruz, ana$/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByRole("button", { name: /^detach card 04a1b2c3d4$/i })).not.toBeNull();
    expect(within(dialog).queryAllByRole("textbox").length).toBeGreaterThan(0);
  });

  it("Pre-registration: lists the registrants, with no Remove and no register form", async () => {
    await showAs(VIEWER, <PreRegistration />);
    await screen.findByText("Orientation");
    fireEvent.click(screen.getByRole("button", { name: /^view registrants of orientation$/i }));

    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByText(/Cruz, Ana \(2026-0001\)/)).toBeTruthy();
    expect(within(dialog).queryByRole("button", { name: /^remove/i })).toBeNull();
    expect(within(dialog).queryByRole("button", { name: /^(add|register)$/i })).toBeNull();
    expect(within(dialog).queryByRole("combobox")).toBeNull();
  });

  it("Pre-registration: a writer still gets Remove and the register form", async () => {
    await showAs([PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite], <PreRegistration />);
    await screen.findByText("Orientation");
    fireEvent.click(screen.getByRole("button", { name: /^manage orientation$/i }));

    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByRole("button", { name: /^remove cruz, ana$/i })).toBeTruthy();
    expect(within(dialog).queryByRole("button", { name: /^add$/i })).not.toBeNull();
  });
});

describe("Roles: a reader without roles.write", () => {
  const READER = [PERMISSIONS.rolesRead];
  const WRITER_ROLES = [PERMISSIONS.rolesRead, PERMISSIONS.rolesWrite];

  it("sees View permissions, not Edit / Delete / Configure, and no Create role button", async () => {
    await showAs(READER, <Roles />);
    await screen.findByText("Registrar");

    expect(screen.queryByRole("button", { name: /create role/i })).toBeNull();
    expect(screen.queryByRole("columnheader", { name: "Actions" })).not.toBeNull();
    expect(screen.queryByRole("button", { name: /^view permissions for registrar$/i })).not.toBeNull();
    expect(screen.queryByRole("button", { name: /^edit registrar/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /^delete registrar/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /^configure permissions for/i })).toBeNull();
  });

  it("opens the permissions read-only: boxes fixed, no Save", async () => {
    await showAs(READER, <Roles />);
    await screen.findByText("Registrar");
    fireEvent.click(screen.getByRole("button", { name: /^view permissions for registrar$/i }));

    const dialog = await screen.findByRole("dialog");
    const box = within(dialog).getByRole("checkbox", { name: "students.read" }) as HTMLInputElement;
    expect(box.checked).toBe(true);
    expect(box.disabled).toBe(true);
    expect(within(dialog).queryByRole("button", { name: /save permissions/i })).toBeNull();
    expect(within(dialog).queryByRole("button", { name: /^close$/i })).not.toBeNull();
  });

  it("still gives a token with roles.write Edit / Configure / Delete", async () => {
    await showAs(WRITER_ROLES, <Roles />);
    await screen.findByText("Registrar");

    expect(screen.queryByRole("button", { name: /create role/i })).not.toBeNull();
    expect(screen.queryByRole("button", { name: /^edit registrar/i })).not.toBeNull();
    expect(screen.queryByRole("button", { name: /^configure permissions for registrar/i })).not.toBeNull();
    expect(screen.queryByRole("button", { name: /^delete registrar/i })).not.toBeNull();
    expect(screen.queryByRole("button", { name: /^view permissions for/i })).toBeNull();
  });
});

// ------------------------------------------------------------ Students: read controls stay usable

describe("Students for a Viewer: search, pagination and Export CSV stay usable", () => {
  const SETTLE_MS = 300;
  const TOTAL = 60;
  const PAGE_SIZE = 25;

  it("sends the search to the server", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      const list = vi.mocked(api.listStudentsPage);
      await showAs(VIEWER, <Students />);
      await act(async () => {});

      const field = screen.getByLabelText(/search name, student no\. or card/i) as HTMLInputElement;
      expect(field.disabled).toBe(false);
      fireEvent.change(field, { target: { value: "Cruz" } });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(SETTLE_MS);
      });

      expect(list.mock.calls.some(([filter]) => filter.search === "Cruz")).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it("asks the server for the next page", async () => {
    const list = vi.mocked(api.listStudentsPage);
    list.mockResolvedValue({ students: [STUDENT], page: 1, pageSize: PAGE_SIZE, total: TOTAL });
    await showAs(VIEWER, <Students />);
    await screen.findByText("2026-0001");

    const next = screen.getByRole("button", { name: /next page/i }) as HTMLButtonElement;
    expect(next.disabled).toBe(false);
    fireEvent.click(next);
    await act(async () => {});

    expect(list.mock.calls.some(([, page]) => page === 2)).toBe(true);
  });

  it("downloads the CSV", async () => {
    const exported = vi
      .spyOn(api, "exportStudentsCsv")
      .mockResolvedValue({ blob: new Blob(["a"]), filename: "EAMS-students.csv" });
    // happy-dom has no object-URL support, which the browser save needs.
    (URL as unknown as { createObjectURL: () => string }).createObjectURL = () => "blob:mock";
    (URL as unknown as { revokeObjectURL: () => void }).revokeObjectURL = () => {};
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});

    await showAs(VIEWER, <Students />);
    await screen.findByText("2026-0001");

    const button = screen.getByRole("button", {
      name: /export csv of the current search/i,
    }) as HTMLButtonElement;
    expect(button.disabled).toBe(false);
    fireEvent.click(button);
    await act(async () => {});

    expect(exported).toHaveBeenCalledTimes(1);
  });
});
