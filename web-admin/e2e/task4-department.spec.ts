// BACKOFFICE Task 4 - the Students grid shows a Department column, immediately before Organization.

import { studentWithBoth, studentWithNeither } from "./fixtures/students";
import { expect, test } from "./support/mockApi";
import { STUDENTS_GRANTS, columnHeaders, openStudents } from "./support/students";

test.use({ grants: STUDENTS_GRANTS });

const NO_VALUE = "—";

test.describe("Task 4: department column", () => {
  test("should_place_department_immediately_before_organization", async ({ page, api }) => {
    await openStudents(page, api, [studentWithBoth]);

    const headers = await columnHeaders(page);
    const department = headers.indexOf("Department");
    expect(department, `headers were: ${headers.join(" | ")}`).toBeGreaterThanOrEqual(0);
    expect(headers[department + 1]).toBe("Organization");
  });

  test("should_show_the_department_value_and_a_dash_when_it_is_null", async ({ page, api }) => {
    await openStudents(page, api, [studentWithBoth, studentWithNeither]);

    const headers = await columnHeaders(page);
    const department = headers.indexOf("Department");

    const filled = page.getByRole("row", { name: /Ana Reyes/ }).getByRole("gridcell").nth(department);
    await expect(filled).toHaveText(studentWithBoth.department ?? "");

    const empty = page.getByRole("row", { name: /Ben Cruz/ }).getByRole("gridcell").nth(department);
    await expect(empty).toHaveText(NO_VALUE);
  });
});
