// Role form rules, the request they become, the surface's refusals, and the grouping the permission
// editor renders. Pure functions over plain values; `termDraft.ts`'s sibling.

import { ApiError } from "./api";
import { cleanText } from "./studentDraft";
import type { Role, RoleWriteRequest } from "./types";

const NAME_MAX = 50;
const DESCRIPTION_MAX = 300;

export interface RoleDraft {
  name: string;
  description: string;
}

export const EMPTY_ROLE_DRAFT: RoleDraft = { name: "", description: "" };

export function draftFromRole(role: Role): RoleDraft {
  return { name: role.name, description: role.description ?? "" };
}

export type RoleFieldErrors = { name?: string; description?: string };

export type RoleValidated =
  | { ok: true; request: RoleWriteRequest }
  | { ok: false; errors: RoleFieldErrors };

export function validateRole(
  draft: RoleDraft,
  others: readonly Role[] = [],
): RoleValidated {
  const errors: RoleFieldErrors = {};

  const name = draft.name.trim();
  if (cleanText(draft.name) === null) {
    errors.name = "A role name is required.";
  } else if (name.length > NAME_MAX) {
    errors.name = `The name is longer than the ${NAME_MAX} characters allowed.`;
  } else if (others.some((r) => r.name.toLowerCase() === name.toLowerCase())) {
    errors.name = `A role named “${name}” already exists.`;
  }

  if (draft.description.trim().length > DESCRIPTION_MAX) {
    errors.description = `The description is longer than the ${DESCRIPTION_MAX} characters allowed.`;
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  return {
    ok: true,
    request: {
      name,
      description: draft.description.trim().length === 0 ? null : draft.description.trim(),
    },
  };
}

// ------------------------------------------------------------------ the refusals, by machine token

const isProblem = (error: unknown, code: string): boolean =>
  error instanceof ApiError && error.kind === "http" && error.code === code;

export const isRoleNameConflict = (error: unknown): boolean => isProblem(error, "NameExists");
export const isSystemRoleProtected = (error: unknown): boolean => isProblem(error, "SystemRoleProtected");
export const isRoleInUse = (error: unknown): boolean => isProblem(error, "InUse");

export const ROLE_NAME_TAKEN =
  "Another role already uses this name. The list on this page is a moment old, so one created since it " +
  "loaded is not in it. Use a different name, or close this and reload the list.";

// ------------------------------------------------------------------ the permission editor's grouping

/**
 * A permission code is `<module>.<action>` (e.g. `events.read`). The editor groups by module so the
 * matrix reads as a table of modules and actions rather than a flat list. A code with no dot falls under
 * a "General" group so nothing is dropped.
 */
export interface PermissionGroup {
  module: string;
  codes: string[];
}

const GENERAL = "General";

export function groupPermissions(codes: readonly string[]): PermissionGroup[] {
  const byModule = new Map<string, string[]>();
  for (const code of codes) {
    const dot = code.indexOf(".");
    const module = dot > 0 ? code.slice(0, dot) : GENERAL;
    const list = byModule.get(module);
    if (list) list.push(code);
    else byModule.set(module, [code]);
  }
  return [...byModule.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([module, groupCodes]) => ({ module, codes: [...groupCodes].sort() }));
}

/** The action half of a code (`events.read` -> `read`), for a compact checkbox label. */
export const actionOf = (code: string): string => {
  const dot = code.indexOf(".");
  return dot > 0 ? code.slice(dot + 1) : code;
};
