// Personnel form rules, the request they become, and the surface's refusals. `termDraft.ts`'s sibling —
// pure functions, no React or MUI. Create and edit take the same body, so one validator serves both.

import { ApiError } from "./api";
import { cleanText } from "./studentDraft";
import type { Personnel, PersonnelStatusName, PersonnelWriteRequest } from "./types";
import { PERSONNEL_STATUSES } from "./types";

export interface PersonnelDraft {
  personnelNumber: string;
  firstName: string;
  middleName: string;
  lastName: string;
  email: string;
  classification: string;
  department: string;
  organization: string;
  position: string;
  rfidUid: string;
  status: PersonnelStatusName;
}

export const EMPTY_PERSONNEL_DRAFT: PersonnelDraft = {
  personnelNumber: "",
  firstName: "",
  middleName: "",
  lastName: "",
  email: "",
  classification: "",
  department: "",
  organization: "",
  position: "",
  rfidUid: "",
  status: "Active",
};

export function draftFromPersonnel(p: Personnel): PersonnelDraft {
  return {
    personnelNumber: p.personnelNumber,
    firstName: p.firstName,
    middleName: p.middleName ?? "",
    lastName: p.lastName,
    email: p.email ?? "",
    classification: p.classification ?? "",
    department: p.department ?? "",
    organization: p.organization ?? "",
    position: p.position ?? "",
    rfidUid: p.rfidUid ?? "",
    status: (PERSONNEL_STATUSES as readonly string[]).includes(p.status)
      ? (p.status as PersonnelStatusName)
      : "Active",
  };
}

export type PersonnelFieldErrors = Partial<
  Record<"personnelNumber" | "firstName" | "lastName", string>
>;

export type PersonnelValidated =
  | { ok: true; request: PersonnelWriteRequest }
  | { ok: false; errors: PersonnelFieldErrors };

export function validatePersonnel(draft: PersonnelDraft): PersonnelValidated {
  const errors: PersonnelFieldErrors = {};

  if (cleanText(draft.personnelNumber) === null) errors.personnelNumber = "A personnel ID is required.";
  if (cleanText(draft.firstName) === null) errors.firstName = "A first name is required.";
  if (cleanText(draft.lastName) === null) errors.lastName = "A last name is required.";

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  const orNull = (v: string) => (v.trim().length === 0 ? null : v.trim());

  return {
    ok: true,
    request: {
      personnelNumber: draft.personnelNumber.trim(),
      firstName: draft.firstName.trim(),
      middleName: orNull(draft.middleName),
      lastName: draft.lastName.trim(),
      email: orNull(draft.email),
      classification: orNull(draft.classification),
      department: orNull(draft.department),
      organization: orNull(draft.organization),
      position: orNull(draft.position),
      // Sent as read — the server normalizes the UID (letters and digits, upper-cased).
      rfidUid: orNull(draft.rfidUid),
      status: draft.status,
    },
  };
}

const isProblem = (error: unknown, code: string): boolean =>
  error instanceof ApiError && error.kind === "http" && error.code === code;

export const isDuplicateNumber = (error: unknown): boolean => isProblem(error, "DuplicateNumber");
export const isDuplicateRfid = (error: unknown): boolean => isProblem(error, "DuplicateRfid");

export const NUMBER_TAKEN = "Another personnel record in this school already uses this ID.";
export const RFID_TAKEN = "Another active personnel record already uses this card. Use a different card.";
