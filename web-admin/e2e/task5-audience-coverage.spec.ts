// BACKOFFICE Task 5 - the event-audience editor's "Who it covers" (Students / Employees / Both).
//
// What the SERVER does with the scope when it resolves attendees is the backend's to pin; this spec
// covers only what the form shows, blocks, clears and sends.

import type { Locator, Page } from "@playwright/test";

import { classification, options } from "./fixtures/audiences";
import { onePage } from "./fixtures/page";
import { PERMISSIONS } from "./fixtures/session";
import { expect, test, type MockApi } from "./support/mockApi";
import type { AudienceDefinitionWriteRequest } from "../src/types";

test.use({ grants: [PERMISSIONS.eventsRead, PERMISSIONS.eventsWrite] });

const SCOPE_REQUIRED = "Choose who it covers";
const DEPARTMENT = options.departments[0];

async function openDialog(page: Page, api: MockApi): Promise<void> {
  api.on("GET /event-audiences", { json: onePage([]) });
  api.on("GET /event-classifications", { json: onePage([classification]) });
  api.on("GET /event-audiences/options", { json: options });
  // "Specific individuals" loads the whole roster for its picker; the dialog asks as soon as it is chosen.
  api.on("GET /students", { json: onePage([]) });
  api.on("GET /events/personnel-picker", {
    json: onePage([{ id: "p-1", personnelNumber: "P-001", fullName: "Santos, Maria" }]),
  });
  await page.goto("/event-audiences");
  await page.getByRole("button", { name: "Create audience" }).click();
  await expect(page.getByRole("dialog", { name: "Create an event audience" })).toBeVisible();
}

const dialog = (page: Page): Locator => page.getByRole("dialog");
const scopeSelect = (page: Page): Locator => dialog(page).getByRole("combobox", { name: "Who it covers" });

async function pick(page: Page, select: Locator, option: string): Promise<void> {
  await select.click();
  await page.getByRole("option", { name: option, exact: true }).click();
}

const chooseType = (page: Page, type: string) =>
  pick(page, dialog(page).getByRole("combobox", { name: "Audience type" }), type);

async function fillIdentity(page: Page): Promise<void> {
  await dialog(page).getByRole("textbox", { name: /^Name/ }).fill("Faculty convocation");
  await pick(page, dialog(page).getByRole("combobox", { name: "Event classification" }), classification.name);
}

async function addDepartment(page: Page): Promise<void> {
  const field = dialog(page).getByRole("combobox", { name: "Departments" });
  await field.fill(DEPARTMENT);
  await field.press("Enter");
}

test.describe("Task 5: audience coverage", () => {
  test("should_show_who_it_covers_for_department_organization_and_custom_only_and_empty_by_default", async ({
    page,
    api,
  }) => {
    await openDialog(page, api);

    for (const type of ["Department", "Organization", "Custom"]) {
      await chooseType(page, type);
      await expect(scopeSelect(page), `${type} should ask who it covers`).toBeVisible();
      await expect(scopeSelect(page), `${type} should start with nothing chosen`).not.toContainText(
        /Students|Employees|Both/,
      );
    }

    for (const type of ["Program", "Year level", "Section", "Employee classification", "Specific individuals"]) {
      await chooseType(page, type);
      await expect(scopeSelect(page), `${type} has a fixed audience and should not ask`).toHaveCount(0);
    }
  });

  test("should_block_save_until_who_it_covers_is_chosen_then_send_the_chosen_scope", async ({ page, api }) => {
    api.on("POST /event-audiences", (request) => ({
      status: 201,
      json: {
        id: "a-1",
        eventClassificationName: classification.name,
        isActive: true,
        ...(request.json as AudienceDefinitionWriteRequest),
      },
    }));
    await openDialog(page, api);
    await fillIdentity(page);
    await chooseType(page, "Department");
    await addDepartment(page);

    await dialog(page).getByRole("button", { name: "Create audience" }).click();
    await expect(dialog(page).getByText(SCOPE_REQUIRED)).toBeVisible();
    expect(api.seen("POST /event-audiences")).toHaveLength(0);

    await pick(page, scopeSelect(page), "Employees");
    await expect(dialog(page).getByText(SCOPE_REQUIRED)).toHaveCount(0);
    await dialog(page).getByRole("button", { name: "Create audience" }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);

    const [sent] = api.seen("POST /event-audiences");
    expect(sent.json).toEqual({
      name: "Faculty convocation",
      eventClassificationId: classification.id,
      audienceType: "Department",
      criteria: { scope: "Employees", departments: [DEPARTMENT] },
    } satisfies AudienceDefinitionWriteRequest);
  });

  test("should_clear_the_scope_when_switching_from_university_wide_to_department", async ({ page, api }) => {
    await openDialog(page, api);

    // University-wide defaults to Both; narrow it, so there is a real choice to lose.
    await expect(scopeSelect(page)).toContainText("Both");
    await pick(page, scopeSelect(page), "Employees");
    await expect(scopeSelect(page)).toContainText("Employees");

    await chooseType(page, "Department");
    await expect(scopeSelect(page)).not.toContainText(/Students|Employees|Both/);
  });

  test("should_offer_students_employees_and_both_for_university_wide", async ({ page, api }) => {
    await openDialog(page, api);

    await scopeSelect(page).click();
    await expect(page.getByRole("option")).toHaveText(["Students", "Employees", "Both"]);
  });

  test("should_hide_students_only_criteria_when_a_custom_audience_covers_employees", async ({ page, api }) => {
    await openDialog(page, api);
    await chooseType(page, "Custom");
    await pick(page, scopeSelect(page), "Employees");

    await expect(dialog(page).getByRole("combobox", { name: "Employee classifications" })).toBeVisible();
    for (const hidden of ["Programs", "Year levels", "Sections"]) {
      await expect(dialog(page).getByRole("combobox", { name: hidden })).toHaveCount(0);
    }
  });

  test("should_hide_employee_classifications_when_a_custom_audience_covers_students", async ({ page, api }) => {
    await openDialog(page, api);
    await chooseType(page, "Custom");
    await pick(page, scopeSelect(page), "Students");

    for (const shown of ["Programs", "Year levels", "Sections"]) {
      await expect(dialog(page).getByRole("combobox", { name: shown })).toBeVisible();
    }
    await expect(dialog(page).getByRole("combobox", { name: "Employee classifications" })).toHaveCount(0);
  });

  test("should_show_the_servers_400_detail_when_the_save_is_refused", async ({ page, api }) => {
    const detail = "Scope 'Employees' cannot be combined with the 'programs' criterion.";
    api.on("POST /event-audiences", {
      status: 400,
      json: { status: 400, title: "Validation failed", detail },
    });
    await openDialog(page, api);
    await fillIdentity(page);
    await chooseType(page, "Department");
    await pick(page, scopeSelect(page), "Both");
    await addDepartment(page);

    await dialog(page).getByRole("button", { name: "Create audience" }).click();

    await expect(dialog(page).getByRole("alert")).toContainText(detail);
    await expect(dialog(page)).toBeVisible();
  });
});
