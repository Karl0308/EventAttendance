// BACKOFFICE Task 1 - the Students grid no longer shows or offers a Classification.

import { studentWithBoth } from "./fixtures/students";
import { expect, test } from "./support/mockApi";
import { STUDENTS_GRANTS, columnHeaders, openStudents } from "./support/students";

test.use({ grants: STUDENTS_GRANTS });

test.describe("Task 1: students grid has no classification", () => {
  test("should_not_offer_a_classification_column_on_the_students_grid", async ({ page, api }) => {
    await openStudents(page, api, [studentWithBoth]);

    const headers = await columnHeaders(page);
    // The positive half: the grid did render its columns, so the absence below is not a blank page.
    expect(headers).toContain("Student No.");
    expect(headers.filter((h) => /classif/i.test(h))).toEqual([]);
  });

  test("should_not_offer_a_classify_action_on_a_student_row", async ({ page, api }) => {
    await openStudents(page, api, [studentWithBoth]);

    const row = page.getByRole("row", { name: /Ana Reyes/ });
    await expect(row.getByRole("button", { name: `Edit ${studentWithBoth.fullName}` })).toBeVisible();
    await expect(row.getByRole("button", { name: /classif/i })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /classif/i })).toHaveCount(0);
  });
});
