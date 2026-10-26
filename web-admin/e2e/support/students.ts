// Shared driver for the Students grid specs (Tasks 1, 3, 4): mock the roster read and open the page.

import type { Page } from "@playwright/test";

import { onePage } from "../fixtures/page";
import { PERMISSIONS } from "../fixtures/session";
import type { StudentWire } from "../fixtures/students";
import type { MockApi } from "./mockApi";

export const STUDENTS_GRANTS = [PERMISSIONS.studentsRead];

export async function openStudents(
  page: Page,
  api: MockApi,
  rows: readonly StudentWire[],
): Promise<void> {
  api.on("GET /students", { json: onePage(rows) });
  await page.goto("/students");
  await page.getByRole("columnheader", { name: "Student No." }).waitFor();
}

/** The grid's column headers, left to right, as a screen reader names them. */
export async function columnHeaders(page: Page): Promise<string[]> {
  const headers = await page.getByRole("columnheader").allInnerTexts();
  return headers.map((h) => h.trim());
}
