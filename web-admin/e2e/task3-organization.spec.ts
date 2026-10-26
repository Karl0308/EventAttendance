// BACKOFFICE Task 3 - Organization on the grid, the CSV export, and the import warning label.

import { BATCH_ID, finishedBatch, organizationTooLongRow } from "./fixtures/imports";
import { PERMISSIONS } from "./fixtures/session";
import { studentWithBoth, studentWithNeither } from "./fixtures/students";
import { expect, test } from "./support/mockApi";
import { columnHeaders, openStudents } from "./support/students";

const NO_VALUE = "—";
const EXPORT_FILENAME = "EAMS-students-e2e.csv";

test.describe("Task 3: organization", () => {
  test.describe("students grid and export", () => {
    test.use({ grants: [PERMISSIONS.studentsRead] });

    test("should_show_the_organization_value_and_a_dash_when_it_is_null", async ({ page, api }) => {
      await openStudents(page, api, [studentWithBoth, studentWithNeither]);

      const organization = (await columnHeaders(page)).indexOf("Organization");
      expect(organization).toBeGreaterThanOrEqual(0);

      await expect(
        page.getByRole("row", { name: /Ana Reyes/ }).getByRole("gridcell").nth(organization),
      ).toHaveText(studentWithBoth.organization ?? "");
      await expect(
        page.getByRole("row", { name: /Ben Cruz/ }).getByRole("gridcell").nth(organization),
      ).toHaveText(NO_VALUE);
    });

    test("should_request_the_csv_export_with_the_current_search_and_show_a_running_state", async ({
      page,
      api,
    }) => {
      let release: () => void = () => undefined;
      const held = new Promise<void>((resolve) => {
        release = resolve;
      });
      api.on("GET /students/export.csv", {
        body: "StudentNo,Name\n2021-0001,Ana Reyes\n",
        headers: {
          "content-type": "text/csv",
          "content-disposition": `attachment; filename="${EXPORT_FILENAME}"`,
        },
        hold: held,
      });
      await openStudents(page, api, [studentWithBoth]);

      await page.getByLabel("Search name, student no. or card").fill("Reyes");
      // The export carries the SETTLED search, so wait for the debounced list read to carry it too.
      await expect.poll(() => api.seen("GET /students").some((r) => r.query.get("search") === "Reyes")).toBe(true);

      const download = page.waitForEvent("download");
      await page.getByRole("button", { name: "Export CSV of the current search" }).click();

      // Withheld reply = the in-flight state is observable without a timer.
      await expect(page.getByRole("button", { name: "Exporting…" })).toBeDisabled();
      await expect.poll(() => api.seen("GET /students/export.csv").length).toBe(1);

      release();
      expect((await download).suggestedFilename()).toBe(EXPORT_FILENAME);
      await expect(page.getByRole("button", { name: "Export CSV of the current search" })).toBeEnabled();

      const [sent] = api.seen("GET /students/export.csv");
      expect(sent.query.get("search")).toBe("Reyes");
    });
  });

  test.describe("import results", () => {
    test.use({ grants: [PERMISSIONS.sisImport] });

    test("should_label_an_organization_too_long_warning_in_the_import_rows", async ({ page, api }) => {
      api.on(`GET /sis/import/${BATCH_ID}`, { json: finishedBatch });
      api.on(`GET /sis/import/${BATCH_ID}/rows`, { json: [organizationTooLongRow] });

      await page.goto(`/students/import/${BATCH_ID}`);

      // The friendly label, not the wire code the chip would print for an unmapped warning.
      await expect(page.getByText("Organization too long (kept the stored one)")).toBeVisible();
      await expect(page.getByText("OrganizationTooLong", { exact: true })).toHaveCount(0);
    });
  });
});
