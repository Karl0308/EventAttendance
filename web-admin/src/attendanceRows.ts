// How an attendance record reads on screen, for both populations (ADR-015 D-99, #706 F2-1).
//
// A personnel record carries no `student*` fields and a student record carries no `personnel*` ones,
// so a grid column bound straight to `studentNumber` goes blank on every personnel row. These pick the
// pair that belongs to the record's own `personType`, and never fall through to the other pair: a
// student row with a missing number shows a dash, not a personnel field that is not there.

import { PERSON_TYPE } from "./types";
import type { AttendanceRecord, PersonType } from "./types";

/** What the Type column prints for each population. */
export const PERSON_TYPE_LABEL: Record<PersonType, string> = {
  [PERSON_TYPE.Student]: "Student",
  [PERSON_TYPE.Personnel]: "Personnel",
};

/** The live grid's population filter: both, or one. */
export type PersonTypeFilter = "All" | PersonType;

export const PERSON_TYPE_FILTER_OPTIONS: ReadonlyArray<{ value: PersonTypeFilter; label: string }> = [
  { value: "All", label: "All" },
  { value: PERSON_TYPE.Student, label: "Students" },
  { value: PERSON_TYPE.Personnel, label: "Personnel" },
];

/** Narrows a select's string to a filter choice; anything unrecognised falls back to `All`. */
export const toPersonTypeFilter = (value: string): PersonTypeFilter =>
  PERSON_TYPE_FILTER_OPTIONS.find((option) => option.value === value)?.value ?? "All";

/** The `personType` query value for a filter choice; `All` sends nothing. */
export const personTypeParam = (filter: PersonTypeFilter): PersonType | undefined =>
  filter === "All" ? undefined : filter;

/** The record's student number, or its personnel number on a personnel record. */
export const attendeeNumber = (record: AttendanceRecord): string | undefined =>
  record.personType === PERSON_TYPE.Personnel ? record.personnelNumber : record.studentNumber;

/** The record's student name, or its personnel name on a personnel record. */
export const attendeeName = (record: AttendanceRecord): string | undefined =>
  record.personType === PERSON_TYPE.Personnel ? record.personnelName : record.studentName;
