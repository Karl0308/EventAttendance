// The rules a user form's boxes are held to, the requests they become, and what the surface's specific
// refusals mean. `termDraft.ts`'s sibling: pure functions over plain values, free of React and MUI.
//
// The create and edit forms take different bodies (create carries e-mail, role and password; edit carries
// only the profile), so each has its own draft and validator here.

import { ApiError } from "./api";
import { cleanText } from "./studentDraft";
import type { AdminUser, UserCreateRequest, UserUpdateRequest } from "./types";

/**
 * The server's minimum, restated so the form refuses a short password before a round trip. It is a copy
 * that could drift — `UserProvisioningService.MinimumPassword` is the authority — so the create route's
 * own 400 is what actually enforces it; this is the courtesy.
 */
export const MIN_PASSWORD_LENGTH = 12;

const FULL_NAME_MAX = 200;
const PHONE_MAX = 30;
const EMAIL_MAX = 256;

/** An `@` with something either side and no second `@` — the same shape the server checks, not RFC 5322. */
function isEmailShaped(value: string): boolean {
  const at = value.indexOf("@");
  return at > 0 && at === value.lastIndexOf("@") && at < value.length - 1;
}

// ---------------------------------------------------------------------------------- create draft

export interface UserCreateDraft {
  email: string;
  fullName: string;
  phone: string;
  roleName: string;
  password: string;
}

export const EMPTY_USER_CREATE_DRAFT: UserCreateDraft = {
  email: "",
  fullName: "",
  phone: "",
  roleName: "",
  password: "",
};

export const VALIDATED_CREATE_FIELDS = ["email", "fullName", "phone", "roleName", "password"] as const;
export type ValidatedCreateField = (typeof VALIDATED_CREATE_FIELDS)[number];
export type CreateFieldErrors = Partial<Record<ValidatedCreateField, string>>;

export const createFieldIdsFor = (prefix: string): Record<ValidatedCreateField, string> => ({
  email: `${prefix}-email`,
  fullName: `${prefix}-full-name`,
  phone: `${prefix}-phone`,
  roleName: `${prefix}-role`,
  password: `${prefix}-password`,
});

export type CreateValidated =
  | { ok: true; request: UserCreateRequest }
  | { ok: false; errors: CreateFieldErrors };

export function validateUserCreate(
  draft: UserCreateDraft,
  others: readonly AdminUser[] = [],
): CreateValidated {
  const errors: CreateFieldErrors = {};

  const email = draft.email.trim();
  if (email.length === 0) {
    errors.email = "An e-mail address is required — it is the login identifier.";
  } else if (email.length > EMAIL_MAX) {
    errors.email = `The e-mail address is longer than the ${EMAIL_MAX} characters allowed.`;
  } else if (!isEmailShaped(email)) {
    errors.email = `“${email}” is not an e-mail address.`;
  } else if (others.some((u) => u.email.toLowerCase() === email.toLowerCase())) {
    errors.email = "A user with this e-mail already exists in this school.";
  }

  if (cleanText(draft.fullName) === null) {
    errors.fullName = "A full name is required.";
  } else if (draft.fullName.trim().length > FULL_NAME_MAX) {
    errors.fullName = `The full name is longer than the ${FULL_NAME_MAX} characters allowed.`;
  }

  if (draft.phone.trim().length > PHONE_MAX) {
    errors.phone = `The phone number is longer than the ${PHONE_MAX} characters allowed.`;
  }

  if (cleanText(draft.roleName) === null) {
    errors.roleName = "Choose the role this user starts with. You can change it afterwards.";
  }

  if (draft.password.length < MIN_PASSWORD_LENGTH) {
    errors.password = `The password must be at least ${MIN_PASSWORD_LENGTH} characters.`;
  } else if (draft.password.trim().toLowerCase() === email.toLowerCase()) {
    errors.password = "The password must not be the e-mail address.";
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  return {
    ok: true,
    request: {
      email,
      fullName: draft.fullName.trim(),
      phone: draft.phone.trim().length === 0 ? null : draft.phone.trim(),
      roleName: draft.roleName,
      password: draft.password,
    },
  };
}

// ------------------------------------------------------------------------------------ edit draft

export interface UserEditDraft {
  fullName: string;
  phone: string;
}

export function draftFromUser(user: AdminUser): UserEditDraft {
  return { fullName: user.fullName, phone: user.phone ?? "" };
}

export type EditValidated =
  | { ok: true; request: UserUpdateRequest }
  | { ok: false; errors: { fullName?: string; phone?: string } };

export function validateUserEdit(draft: UserEditDraft): EditValidated {
  const errors: { fullName?: string; phone?: string } = {};

  if (cleanText(draft.fullName) === null) {
    errors.fullName = "A full name is required.";
  } else if (draft.fullName.trim().length > FULL_NAME_MAX) {
    errors.fullName = `The full name is longer than the ${FULL_NAME_MAX} characters allowed.`;
  }

  if (draft.phone.trim().length > PHONE_MAX) {
    errors.phone = `The phone number is longer than the ${PHONE_MAX} characters allowed.`;
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  return {
    ok: true,
    request: {
      fullName: draft.fullName.trim(),
      phone: draft.phone.trim().length === 0 ? null : draft.phone.trim(),
    },
  };
}

// ------------------------------------------------------------------ the refusals, by machine token

const isProblem = (error: unknown, code: string): boolean =>
  error instanceof ApiError && error.kind === "http" && error.code === code;

export const isEmailInUse = (error: unknown): boolean => isProblem(error, "EmailInUse");
export const isUnknownRole = (error: unknown): boolean => isProblem(error, "UnknownRole");
export const isSelfLockout = (error: unknown): boolean => isProblem(error, "SelfLockout");
export const isNoSchoolResolved = (error: unknown): boolean => isProblem(error, "NoSchoolResolved");

export const EMAIL_TAKEN =
  "Another user in this school already uses this e-mail. The list on this page is a moment old, so one " +
  "created since it loaded is not in it. Use a different address, or close this and reload the list.";
