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
// A convenience, not a control: the server refuses every one of these writes without the permission too.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { ReactElement } from "react";

import AuthProvider from "../src/components/AuthProvider";
import Events from "../src/pages/Events";
import EventAudiences from "../src/pages/EventAudiences";
import EventClassifications from "../src/pages/EventClassifications";
import ManualRfidRecords from "../src/pages/ManualRfidRecords";
import PreRegistration from "../src/pages/PreRegistration";
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
  PreRegistrationSession,
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
  cards: [],
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
  /** The grid carries an Actions column for a writer. */
  readonly hasActionsColumn: boolean;
  /** Permissions that unlock the page's write controls. */
  readonly writer: readonly PermissionCode[];
}

const WRITE_PAGES: readonly WritePage[] = [
  {
    name: "Students",
    page: () => <Students />,
    loaded: "2026-0001",
    buttons: [/new student/i, /import roster/i],
    hasActionsColumn: true,
    writer: [PERMISSIONS.studentsRead, PERMISSIONS.studentsWrite, PERMISSIONS.sisImport],
  },
  {
    name: "Events",
    page: () => <Events />,
    loaded: /no events have been created yet/i,
    buttons: [/new event/i],
    hasActionsColumn: false,
    writer: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite],
  },
  {
    name: "Event classifications",
    page: () => <EventClassifications />,
    loaded: "Institutional",
    buttons: [/create classification/i],
    hasActionsColumn: true,
    writer: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite],
  },
  {
    name: "Event audiences",
    page: () => <EventAudiences />,
    loaded: "All Freshmen",
    buttons: [/create audience/i],
    hasActionsColumn: true,
    writer: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite],
  },
  {
    name: "Pre-registration",
    page: () => <PreRegistration />,
    loaded: "Orientation",
    buttons: [/create session/i],
    hasActionsColumn: true,
    writer: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite],
  },
  {
    name: "Terms",
    page: () => <Terms />,
    loaded: "2026-1",
    buttons: [/create term/i],
    hasActionsColumn: true,
    writer: [PERMISSIONS.academicRead, PERMISSIONS.academicWrite],
  },
  {
    name: "Manual RFID records",
    page: () => <ManualRfidRecords />,
    loaded: "04A1B2C3",
    buttons: [/^record$/i],
    hasActionsColumn: false,
    writer: [PERMISSIONS.attendanceRead, PERMISSIONS.attendanceWrite],
  },
];

const findLoaded = (page: WritePage) => screen.findByText(page.loaded);

describe.each(WRITE_PAGES)("$name", (page) => {
  it("shows a Viewer no Create / New / Import / Record button and no Actions column", async () => {
    await showAs(VIEWER, page.page());
    await findLoaded(page);

    for (const name of page.buttons) {
      expect(screen.queryByRole("button", { name }), `${String(name)} for a Viewer`).toBeNull();
      expect(screen.queryByRole("link", { name }), `${String(name)} link for a Viewer`).toBeNull();
    }
    expect(screen.queryByRole("columnheader", { name: "Actions" })).toBeNull();
  });

  it("still shows a token with the write permission its controls", async () => {
    await showAs(page.writer, page.page());
    await findLoaded(page);

    for (const name of page.buttons) {
      const control = screen.queryByRole("button", { name }) ?? screen.queryByRole("link", { name });
      expect(control, `${String(name)} for a writer`).not.toBeNull();
    }
    if (page.hasActionsColumn) {
      expect(screen.queryByRole("columnheader", { name: "Actions" })).not.toBeNull();
    }
  });
});
