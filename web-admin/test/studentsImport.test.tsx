/** @vitest-environment happy-dom */

// `/students/import` — the preview step, after a workbook has been staged.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// ADR-010: the built-in profile (v5) is section-only — its template has no course or teacher columns —
// so the API's preview reports **0** distinct courses, 0 distinct instructors and 0 placeholder-
// instructor rows for it. The DTO is unchanged, and a batch still pinned to v4 reports real counts.
//
// The preview used to draw every count unconditionally, which for a v5 workbook means a "0 Courses"
// tile and a "0 Instructors" tile — figures that read, to the operator comparing them against what the
// registrar sent, as though the file were missing something it was never meant to have. So the two
// tiles are **not rendered** (rather than hidden with CSS, which would leave them in the accessibility
// tree) when their count is 0, and rendered as before when it is not. The second test is the other
// half: it is what stops "hide when zero" from drifting into "hide always" and quietly removing the
// counts a v4 batch's operator still needs.
//
// The transport is a `fetch` stub, the idiom `rosterTemplateDownload.test.tsx` and
// `studentsImportProgress.test.tsx` use, so the preview is reached the way an operator reaches it —
// choose a term, choose a file, press Upload — through the real `api.uploadRoster` and `toImportPreview`.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import StudentsImport from "../src/pages/StudentsImport";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { PERMISSIONS } from "../src/permissions";
import { SIS_IMPORT_STATUS } from "../src/types";
import type { AuthUser } from "../src/types";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: [PERMISSIONS.sisImport],
};

const TERM_ID = "44444444-4444-4444-4444-444444444444";
const TERM_CODE = "2026-1";
const TERMS_PATH = "/api/v1/academic/terms";
const UPLOAD_PATH = "/api/v1/sis/import/upload";

const TERM_JSON = {
  id: TERM_ID,
  code: TERM_CODE,
  schoolYear: "2026",
  semester: "1st",
  isCurrent: true,
  startsOn: null,
  endsOn: null,
};

const jsonResponse = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/** A staged batch, as `SisImportBatchDto` carries it. Only the identity matters to these tests. */
const BATCH_JSON = {
  id: "3f6b1c20-0000-4000-8000-000000000001",
  termId: TERM_ID,
  termCode: TERM_CODE,
  source: "Registrar export",
  fileName: "roster.xlsx",
  sourceSheetName: "Sheet1",
  fileHash: "abc123",
  status: SIS_IMPORT_STATUS.Pending,
  totalRows: 12,
  insertedRows: 0,
  updatedRows: 0,
  failedRows: 0,
  skippedRows: 0,
  warningRows: 0,
  startedAt: null,
  finishedAt: null,
  countersReconcile: false,
  isTerminal: false,
};

/** `SisImportPreviewDto` for a section-only (v5) workbook: no course or teacher columns, so all zero. */
const SECTION_ONLY_PREVIEW = {
  batch: BATCH_JSON,
  columns: ["REGNO", "LASTNAME", "FIRSTNAME", "COLLEGE", "PROGRAM", "SECTION"],
  distinctStudents: 52,
  distinctColleges: 3,
  distinctPrograms: 7,
  distinctCourses: 0,
  distinctSections: 9,
  distinctInstructors: 0,
  blankSectionRows: 0,
  placeholderInstructorRows: 0,
  sampleRows: [],
};

/** The same shape for a v4-pinned batch, which still reports real course and instructor counts. */
const V4_PREVIEW = {
  ...SECTION_ONLY_PREVIEW,
  columns: [...SECTION_ONLY_PREVIEW.columns, "COURSE", "INSTRUCTOR"],
  distinctCourses: 14,
  distinctInstructors: 6,
  placeholderInstructorRows: 2,
};

function serve(previewBody: unknown): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const path = new URL(String(input), "http://localhost").pathname;
    if (path === TERMS_PATH) {
      return Promise.resolve(
        jsonResponse(200, { items: [TERM_JSON], page: 1, pageSize: 200, total: 1, hasMore: false }),
      );
    }
    if (path === UPLOAD_PATH) return Promise.resolve(jsonResponse(201, previewBody));
    throw new Error(`the import page asked for something this stub does not serve: ${String(input)}`);
  });
}

/** Choose the term, choose a workbook, press Upload, and flush until the preview step is on screen. */
async function reachPreview(): Promise<void> {
  render(
    <MemoryRouter initialEntries={["/students/import"]}>
      <Routes>
        <Route path="/students/import" element={<StudentsImport />} />
      </Routes>
    </MemoryRouter>,
  );
  await act(async () => {});

  fireEvent.mouseDown(await screen.findByRole("combobox", { name: /term this roster is for/i }));
  fireEvent.click(screen.getByRole("option", { name: new RegExp(TERM_CODE) }));

  const workbook = new File([new Uint8Array([1, 2, 3])], "roster.xlsx");
  fireEvent.change(screen.getByLabelText(/roster workbook/i), { target: { files: [workbook] } });

  fireEvent.click(screen.getByRole("button", { name: /upload and preview/i }));
  await act(async () => {});
  await screen.findByRole("heading", { name: /what the file holds/i });
}

/** The label of every count tile on the preview, read from the same `<p>` the tile draws it in. */
const tileLabels = (): string[] =>
  ["Students", "Colleges", "Programs", "Courses", "Sections", "Instructors"].filter(
    (label) => screen.queryByText(label, { exact: true }) !== null,
  );

beforeEach(() => {
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("the roster import preview", () => {
  // AC-22 (ADR-010 D-77)
  it("hides course and instructor tiles for a section-only preview", async () => {
    serve(SECTION_ONLY_PREVIEW);
    await reachPreview();

    // Not rendered, as opposed to hidden: a CSS-hidden tile would still be read out as "0 Courses".
    expect(screen.queryByText("Courses", { exact: true })).toBeNull();
    expect(screen.queryByText("Instructors", { exact: true })).toBeNull();
    expect(document.body.textContent ?? "").not.toMatch(/placeholder instructor/i);

    // The tiles that are still true of a section-only file are all there, so the test cannot pass by
    // the whole grid having failed to draw.
    expect(tileLabels()).toEqual(["Students", "Colleges", "Programs", "Sections"]);
    expect(screen.getByText("52")).toBeTruthy();
  });

  it("shows course and instructor tiles when the preview has courses", async () => {
    serve(V4_PREVIEW);
    await reachPreview();

    expect(tileLabels()).toEqual([
      "Students",
      "Colleges",
      "Programs",
      "Courses",
      "Sections",
      "Instructors",
    ]);
    // The numbers, not just the labels: a tile that drew "Courses" over the wrong figure would pass the
    // label check and be exactly the mistake an operator comparing against the registrar cannot catch.
    expect(screen.getByText("14")).toBeTruthy();
    expect(screen.getByText("6")).toBeTruthy();
    expect(document.body.textContent ?? "").toMatch(/2 rows name a placeholder instructor/);
  });
});
