// Marking one person present by hand (`POST /attendance/manual`, #706 F2-2): who can be picked, and what
// each refusal says. Pure functions, no React.
//
// A student and a personnel member are picked from ONE list, so an option carries its own type and the
// request is built from it. That keeps "exactly one of studentId / personnelId" a property of the data
// rather than of a form's state: an option is a student or it is personnel, never both and never neither.

import { ApiError } from "./api";
import { PERSON_TYPE } from "./types";
import type { ManualAttendanceRequest, PersonType, PersonnelPickerEntry, Student } from "./types";

/** One choice in the picker. */
export interface ManualAttendee {
  type: PersonType;
  id: string;
  number: string;
  name: string;
}

export const attendeeFromStudent = (student: Student): ManualAttendee => ({
  type: PERSON_TYPE.Student,
  id: student.id,
  number: student.studentNumber,
  name: student.fullName,
});

export const attendeeFromPersonnel = (entry: PersonnelPickerEntry): ManualAttendee => ({
  type: PERSON_TYPE.Personnel,
  id: entry.id,
  number: entry.personnelNumber,
  name: entry.fullName,
});

/** Stable across the two populations: a student and a personnel member may share an id string only by accident. */
export const attendeeKey = (attendee: ManualAttendee): string => `${attendee.type}:${attendee.id}`;

export const attendeeLabel = (attendee: ManualAttendee): string => `${attendee.name} (${attendee.number})`;

/** Fewer characters than this and the student search would return a page of strangers. */
export const MIN_SEARCH_CHARS = 2;

/** Personnel are held whole, so they are matched here on number or name, case-insensitively. */
export function matchesPersonnel(entry: ManualAttendee, search: string): boolean {
  const needle = search.trim().toLowerCase();
  if (needle.length < MIN_SEARCH_CHARS) return false;
  return entry.number.toLowerCase().includes(needle) || entry.name.toLowerCase().includes(needle);
}

/** The request for one picked option: the one id that belongs to its type. */
export function manualRequestFor(eventId: string, attendee: ManualAttendee): ManualAttendanceRequest {
  return attendee.type === PERSON_TYPE.Personnel
    ? { eventId, status: "Present", personnelId: attendee.id }
    : { eventId, status: "Present", studentId: attendee.id };
}

/** `ManualOutcomeCode` values the dialog words itself (the frozen table in the OpenAPI document). */
export const MANUAL_OUTCOME = {
  PersonRequired: "PersonRequired",
  PersonnelNotFound: "PersonnelNotFound",
  PersonnelInactive: "PersonnelInactive",
  PersonnelNotInAudience: "PersonnelNotInAudience",
  StudentNotFound: "StudentNotFound",
  EventNotFound: "EventNotFound",
} as const;

const REFUSAL_TEXT: Record<string, string> = {
  [MANUAL_OUTCOME.PersonRequired]: "Choose one student or personnel member, then try again.",
  [MANUAL_OUTCOME.PersonnelNotFound]:
    "That personnel record could not be found. It may have been removed; search again and pick them from the list.",
  [MANUAL_OUTCOME.PersonnelInactive]:
    "That personnel member is inactive, so they cannot be marked present. Set them Active on the Personnel page, or choose someone else.",
  [MANUAL_OUTCOME.PersonnelNotInAudience]:
    "That personnel member is not in this event's audience. On a closed event the audience is the one frozen when it closed. Add them to the audience, or choose someone else.",
  [MANUAL_OUTCOME.StudentNotFound]:
    "That student could not be found. They may have been removed; search again and pick them from the list.",
  [MANUAL_OUTCOME.EventNotFound]: "This event could not be found. It may have been deleted.",
};

/**
 * The sentence for a refusal the server decided on, or `undefined` for anything else (a network failure,
 * a 5xx, a code this build has not heard of) so the caller falls back to the generic failure alert.
 * Keyed on `ApiError.code`, never on prose.
 */
export function manualRefusalText(error: unknown): string | undefined {
  if (!(error instanceof ApiError) || error.kind !== "http" || error.code === undefined) return undefined;
  return REFUSAL_TEXT[error.code];
}

/** The confirmation line. Uses the name already on screen, nothing the server sent back. */
export const manualSavedText = (attendee: ManualAttendee): string => `${attendee.name} was marked present.`;
