// Roster-import wire shapes: terms, the upload preview, the batch, and a batch's rows. Typed against
// `src/types.ts`; the preview is the `SisImportPreview` the page narrows with `toImportPreview`.

import type { SisImportBatch, SisImportPreview, SisImportRow, Term } from "../../src/types";
import { SIS_IMPORT_STATUS } from "../../src/types";

export const term: Term = {
  id: "t-2026-1",
  code: "2026-1",
  schoolYear: "2026-2027",
  semester: "First",
  isCurrent: true,
};

export const BATCH_ID = "b0b0b0b0-0000-4000-8000-000000000001";

export const batch: SisImportBatch = {
  id: BATCH_ID,
  termId: term.id,
  termCode: term.code,
  source: "Registrar",
  fileName: "roster-e2e.xlsx",
  status: SIS_IMPORT_STATUS.Pending,
  totalRows: 12,
  insertedRows: 0,
  updatedRows: 0,
  failedRows: 0,
  skippedRows: 0,
  warningRows: 0,
  countersReconcile: true,
  isTerminal: false,
};

export const finishedBatch: SisImportBatch = {
  ...batch,
  status: SIS_IMPORT_STATUS.CompletedWithWarnings,
  insertedRows: 11,
  updatedRows: 1,
  warningRows: 1,
  isTerminal: true,
};

const previewOf = (
  counts: Pick<SisImportPreview, "distinctCourses" | "distinctInstructors">,
): SisImportPreview => ({
  batch,
  columns: ["REGNO", "COURSE_CODE", "ORGANIZATION"],
  distinctStudents: 12,
  distinctColleges: 2,
  distinctPrograms: 3,
  distinctSections: 4,
  blankSectionRows: 0,
  placeholderInstructorRows: 0,
  sampleRows: [],
  ...counts,
});

/** The post-Task-2 template: no course or teacher columns, so the importer reports neither. */
export const previewWithoutCoursesOrInstructors = previewOf({
  distinctCourses: 0,
  distinctInstructors: 0,
});

/** Profile v7: COURSE_CODE is back (courses > 0) but there is still no instructor column. */
export const previewWithCoursesOnly = previewOf({ distinctCourses: 7, distinctInstructors: 0 });

export const organizationTooLongRow: SisImportRow = {
  id: "row-1",
  rowNumber: 4,
  result: "Updated",
  warningCode: "OrganizationTooLong",
  warningMessage: "ORGANIZATION exceeds the column length; the stored value was kept.",
  entities: [],
};

export const XLSX_CONTENT_TYPE =
  "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
