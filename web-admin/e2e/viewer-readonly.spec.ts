// RoleViewRestrictions - a Viewer gets a strictly read-only SPA.
//
// "Logging in as a Viewer results in a strictly read-only interface. All Create / New / Import / Record
// buttons are hidden or visibly disabled on all pages. The Actions column in all tables is either hidden
// or shows no clickable icons for the Viewer role."
//
// "Signed in as a Viewer" here is what the SPA sees of one: the permission codes the seeded Viewer role
// holds (EamsRoles.cs: students.read, events.read, attendance.read, academic.read), served by the mocked
// `POST /auth/refresh` (see playwright.config.ts for what this suite does and does not prove). The gate is
// the write permission, not the role name, so the same page is also opened with the write code as a control:
// without it a "no button" assertion would pass on a page that simply never rendered.

import type { Page } from "@playwright/test";

import { onePage } from "./fixtures/page";
import { studentWithBoth } from "./fixtures/students";
import { expect, test, type MockApi } from "./support/mockApi";

const VIEWER_GRANTS = ["students.read", "events.read", "attendance.read", "academic.read"] as const;

const CLASSIFICATION = {
  id: "c-0001",
  name: "Institutional",
  nameKey: "institutional",
  isActive: true,
};

const AUDIENCE = {
  id: "a-0001",
  name: "All Freshmen",
  eventClassificationId: "c-0001",
  eventClassificationName: "Institutional",
  audienceType: "UniversityWide",
  criteria: {},
  isActive: true,
};

const PRE_REGISTRATION_SESSION = {
  id: "ses-0001",
  name: "Orientation",
  audienceDefinitionId: "a-0001",
  audienceName: "All Freshmen",
  capacity: 10,
  registeredCount: 0,
  isClosed: false,
  isFull: false,
};

const TERM = {
  id: "t-0001",
  code: "2026-1",
  schoolYear: "2026-2027",
  semester: "1st",
  isCurrent: true,
};

const MANUAL_ENTRY = {
  id: "m-0001",
  cardUid: "04A1B2C3",
  idNumber: "2021-0001",
  personType: "Student",
  isResolved: false,
  recordedAt: "2026-11-01T08:00:00Z",
};

interface ReadOnlyPage {
  readonly name: string;
  readonly path: string;
  /** Registers every read the page makes. */
  readonly mock: (api: MockApi) => void;
  /** Something only the loaded page shows. */
  readonly loaded: string | RegExp;
  /** Every Create / New / Import / Record control the page offers a writer, by accessible name. */
  readonly controls: readonly RegExp[];
  readonly hasActionsColumn: boolean;
  /** What a writer holds in addition to the Viewer set. */
  readonly writeGrants: readonly string[];
}

const PAGES: readonly ReadOnlyPage[] = [
  {
    name: "Students",
    path: "/students",
    mock: (api) => api.on("GET /students", { json: onePage([studentWithBoth]) }),
    loaded: "2021-0001",
    controls: [/new student/i, /import roster/i],
    hasActionsColumn: true,
    writeGrants: ["students.write", "sis.import"],
  },
  {
    name: "Events",
    path: "/events",
    mock: (api) => api.on("GET /events", { json: onePage([]) }),
    loaded: /no events have been created yet/i,
    controls: [/new event/i],
    hasActionsColumn: false,
    writeGrants: ["events.write"],
  },
  {
    name: "Event classifications",
    path: "/event-classifications",
    mock: (api) => api.on("GET /event-classifications", { json: onePage([CLASSIFICATION]) }),
    loaded: "Institutional",
    controls: [/create classification/i],
    hasActionsColumn: true,
    writeGrants: ["events.write"],
  },
  {
    name: "Event audiences",
    path: "/event-audiences",
    mock: (api) => {
      api.on("GET /event-audiences", { json: onePage([AUDIENCE]) });
      api.on("GET /event-classifications", { json: onePage([CLASSIFICATION]) });
    },
    loaded: "All Freshmen",
    controls: [/create audience/i],
    hasActionsColumn: true,
    writeGrants: ["events.write"],
  },
  {
    name: "Pre-registration",
    path: "/pre-registration",
    mock: (api) => {
      api.on("GET /pre-registration/sessions", { json: [PRE_REGISTRATION_SESSION] });
      api.on("GET /event-audiences", { json: onePage([AUDIENCE]) });
    },
    loaded: "Orientation",
    controls: [/create session/i],
    hasActionsColumn: true,
    writeGrants: ["events.write"],
  },
  {
    name: "Terms",
    path: "/terms",
    mock: (api) => api.on("GET /academic/terms", { json: onePage([TERM]) }),
    loaded: "2026-1",
    controls: [/create term/i],
    hasActionsColumn: true,
    writeGrants: ["academic.write"],
  },
  {
    name: "Manual RFID records",
    path: "/manual-scans",
    mock: (api) => api.on("GET /scans/manual-id", { json: onePage([MANUAL_ENTRY]) }),
    loaded: "04A1B2C3",
    controls: [/^record$/i],
    hasActionsColumn: false,
    writeGrants: ["attendance.write"],
  },
];

const writeControl = (page: Page, name: RegExp) =>
  page.getByRole("button", { name }).or(page.getByRole("link", { name }));

for (const target of PAGES) {
  test.describe(`${target.name} as a Viewer`, () => {
    test.use({ grants: VIEWER_GRANTS });

    test(`should_offer_no_write_control_or_actions_column_on_${target.path.slice(1)}`, async ({
      page,
      api,
    }) => {
      target.mock(api);
      await page.goto(target.path);
      await expect(page.getByText(target.loaded).first()).toBeVisible();

      for (const control of target.controls) {
        await expect(writeControl(page, control)).toHaveCount(0);
      }
      await expect(page.getByRole("columnheader", { name: "Actions" })).toHaveCount(0);
    });
  });

  test.describe(`${target.name} with the write permission`, () => {
    test.use({ grants: [...VIEWER_GRANTS, ...target.writeGrants] });

    test(`should_still_offer_its_controls_on_${target.path.slice(1)}`, async ({ page, api }) => {
      target.mock(api);
      await page.goto(target.path);
      await expect(page.getByText(target.loaded).first()).toBeVisible();

      for (const control of target.controls) {
        await expect(writeControl(page, control).first()).toBeVisible();
      }
      if (target.hasActionsColumn) {
        await expect(page.getByRole("columnheader", { name: "Actions" })).toBeVisible();
      }
    });
  });
}
