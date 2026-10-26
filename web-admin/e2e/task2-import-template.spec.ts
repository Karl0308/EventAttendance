// BACKOFFICE Task 2 - the roster-import screen: template download, and which preview tiles are shown.
//
// The workbook's CONTENT (13 columns incl. COURSE_CODE) is the backend's to pin (SisImportTemplateTests);
// this spec only proves the SPA asks the right endpoint and saves what comes back under the server's name.

import type { Page } from "@playwright/test";

import { onePage } from "./fixtures/page";
import {
  XLSX_CONTENT_TYPE,
  previewWithCoursesOnly,
  previewWithoutCoursesOrInstructors,
  term,
} from "./fixtures/imports";
import { PERMISSIONS } from "./fixtures/session";
import { expect, test, type MockApi } from "./support/mockApi";
import type { SisImportPreview } from "../src/types";

test.use({ grants: [PERMISSIONS.sisImport] });

const TEMPLATE_FILENAME = "EAMS-roster-template-e2e.xlsx";

async function openImport(page: Page, api: MockApi): Promise<void> {
  api.on("GET /academic/terms", { json: onePage([term]) });
  await page.goto("/students/import");
  await expect(page.getByRole("button", { name: "Download template" })).toBeVisible();
}

async function uploadFor(page: Page, api: MockApi, preview: SisImportPreview): Promise<void> {
  api.on("POST /sis/import/upload", { json: preview });
  await openImport(page, api);

  await page.getByLabel("Term this roster is for").click();
  await page.getByRole("option", { name: /2026-1/ }).click();
  await page.getByLabel(/Roster workbook/).setInputFiles({
    name: "roster-e2e.xlsx",
    mimeType: XLSX_CONTENT_TYPE,
    buffer: Buffer.from("not a real workbook - the upload is mocked"),
  });
  await page.getByRole("button", { name: "Upload and preview" }).click();
  await expect(page.getByRole("heading", { name: /Review what is in it/ })).toBeVisible();
}

test.describe("Task 2: roster import", () => {
  test("should_request_the_template_from_the_template_endpoint_and_save_it_under_the_server_name", async ({
    page,
    api,
  }) => {
    api.on("GET /sis/import/template", {
      body: "stand-in bytes",
      headers: {
        "content-type": XLSX_CONTENT_TYPE,
        "content-disposition": `attachment; filename="${TEMPLATE_FILENAME}"`,
      },
    });
    await openImport(page, api);

    const download = page.waitForEvent("download");
    await page.getByRole("button", { name: "Download template" }).click();

    expect((await download).suggestedFilename()).toBe(TEMPLATE_FILENAME);
    expect(api.seen("GET /sis/import/template")).toHaveLength(1);
  });

  test("should_hide_the_courses_and_instructors_tiles_when_the_file_has_neither", async ({ page, api }) => {
    await uploadFor(page, api, previewWithoutCoursesOrInstructors);

    // Positive control first: the tiles that always show are there, so the page did render the counts.
    for (const label of ["Students", "Colleges", "Programs", "Sections"]) {
      await expect(page.getByText(label, { exact: true })).toBeVisible();
    }
    await expect(page.getByText("Courses", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Instructors", { exact: true })).toHaveCount(0);
  });

  test("should_show_courses_but_keep_instructors_hidden_for_a_v7_style_file", async ({ page, api }) => {
    await uploadFor(page, api, previewWithCoursesOnly);

    await expect(page.getByText("Courses", { exact: true })).toBeVisible();
    await expect(page.getByText(String(previewWithCoursesOnly.distinctCourses), { exact: true })).toBeVisible();
    await expect(page.getByText("Instructors", { exact: true })).toHaveCount(0);
  });

  test("should_show_the_instructors_tile_when_the_file_has_instructors", async ({ page, api }) => {
    // Positive control: proves the tile CAN render, so the hidden-tile checks above are not vacuous.
    await uploadFor(page, api, { ...previewWithCoursesOnly, distinctInstructors: 1 });

    await expect(page.getByText("Instructors", { exact: true })).toBeVisible();
  });
});
