// `GET /students` rows. The wire carries `organization` / `department` as `null` when unset (the C# DTO
// serialises nulls), whereas the SPA's `Student` omits them - so the wire type is derived from `Student`
// and re-states exactly those two members. A renamed or retyped field in `src/types.ts` breaks `tsc` here.

import type { Student } from "../../src/types";

export type StudentWire = Omit<Student, "organization" | "department"> & {
  organization: string | null;
  department: string | null;
};

const ACTIVE_STATUS = "Active";

export const studentWithBoth: StudentWire = {
  id: "s-0001",
  studentNumber: "2021-0001",
  fullName: "Ana Reyes",
  firstName: "Ana",
  lastName: "Reyes",
  course: "BSIT",
  yearLevel: "3rd Year",
  section: "A",
  organization: "Red Cross Youth",
  department: "College of Computer Studies",
  status: ACTIVE_STATUS,
  cards: [],
};

export const studentWithNeither: StudentWire = {
  id: "s-0002",
  studentNumber: "2021-0002",
  fullName: "Ben Cruz",
  firstName: "Ben",
  lastName: "Cruz",
  course: "BSIT",
  yearLevel: "2nd Year",
  section: "B",
  organization: null,
  department: null,
  status: ACTIVE_STATUS,
  cards: [],
};
