// What is in an event-classification form's boxes, the rules those boxes are held to, the request they
// become, and what the surface's specific refusals mean.
//
// `termDraft.ts`'s sibling and the same shape: `POST /event-classifications` and
// `PUT /event-classifications/{id}` take the same body (`EventClassificationWriteRequest`) and are
// checked by the same server code, so one copy of the rules serves both dialogs.
//
// Free of React and MUI: pure functions over plain values. `ApiError` is imported for the conflict
// tests, exactly as `termDraft.ts` imports it.

import { ApiError } from "./api";
import { cleanText } from "./studentDraft";
import type { EventClassification, EventClassificationWriteRequest } from "./types";

/**
 * Whether a value carries leading or trailing whitespace — a `400`, not something to clean off. The
 * server stores the name exactly as typed and refuses padding rather than trimming it (the same
 * refusal `termDraft.isPadded` covers for a term code), so a client that trimmed would be writing a
 * different name from the one on screen.
 */
const isPadded = (value: string): boolean => value !== value.trim();

const PADDED =
  "Remove the space at the start or end — it is stored exactly as typed, and the API refuses padding " +
  "rather than trimming it.";

export interface EventClassificationDraft {
  name: string;
  description: string;
}

export const EMPTY_EVENT_CLASSIFICATION_DRAFT: EventClassificationDraft = {
  name: "",
  description: "",
};

/** The fields that can carry an error, in the order they appear on screen. */
export const VALIDATED_EVENT_CLASSIFICATION_FIELDS = ["name", "description"] as const;

export type ValidatedEventClassificationField =
  (typeof VALIDATED_EVENT_CLASSIFICATION_FIELDS)[number];

export type EventClassificationFieldErrors = Partial<
  Record<ValidatedEventClassificationField, string>
>;

export const NO_EVENT_CLASSIFICATION_ERRORS: EventClassificationFieldErrors = {};

/** Stable ids, built from a prefix so the create and edit dialogs cannot collide. */
export const eventClassificationFieldIdsFor = (
  prefix: string,
): Record<ValidatedEventClassificationField, string> => ({
  name: `${prefix}-name`,
  description: `${prefix}-description`,
});

/**
 * The boxes filled from a classification that already exists — the edit form's starting state. Verbatim,
 * nothing defaulted, because `PUT` is a full replacement.
 */
export function draftFromEventClassification(
  classification: EventClassification,
): EventClassificationDraft {
  return {
    name: classification.name,
    description: classification.description ?? "",
  };
}

// -------------------------------------------------------------- the duplicate name, caught on screen

/**
 * Whether `name` collides with one of `others` once punctuation, spacing and case are removed — the
 * same normalization the server keys `UX_EventClassifications_SchoolId_NameKey` on. A convenience, never
 * the authority: the list was fetched moments ago, so a name created since is decided by the index and
 * surfaces as `isNameConflict` below.
 */
const normalizeKey = (value: string): string =>
  value.replace(/[^0-9a-z]/gi, "").toUpperCase();

export function nameCollision(
  others: readonly EventClassification[],
  name: string,
): EventClassification | undefined {
  const key = normalizeKey(name);
  if (key.length === 0) return undefined;
  return others.find((c) => normalizeKey(c.name) === key);
}

export const duplicateNameMessage = (classification: EventClassification): string =>
  `“${classification.name}” already exists in this school. Names are compared with punctuation, spacing ` +
  `and case removed, so this one has to differ.`;

// ------------------------------------------------------------------------------------- validation

export type EventClassificationValidated =
  | { ok: true; request: EventClassificationWriteRequest }
  | { ok: false; errors: EventClassificationFieldErrors };

/**
 * The write routes' rules, applied to the values that will actually be written. The name is measured
 * with `cleanText` (is there anything in the box at all) and sent verbatim; over-length is left to the
 * server, whose `detail` names the limit — the same choice `termDraft` makes.
 *
 * @param others the classifications whose names this one may not collide with; `[]` leaves the server
 *   as the only check.
 */
export function validateEventClassification(
  draft: EventClassificationDraft,
  others: readonly EventClassification[] = [],
): EventClassificationValidated {
  const errors: EventClassificationFieldErrors = {};

  if (cleanText(draft.name) === null) {
    errors.name = "A name is required, such as “Institutional Events”.";
  } else if (isPadded(draft.name)) {
    errors.name = PADDED;
  } else {
    const clash = nameCollision(others, draft.name);
    if (clash !== undefined) errors.name = duplicateNameMessage(clash);
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  return {
    ok: true,
    request: {
      name: draft.name,
      // `null`, never omitted — a `PUT` full replacement, so an empty box clears the stored description.
      description: cleanText(draft.description) === null ? null : draft.description,
    },
  };
}

// ------------------------------------------------------------------ the refusals, by machine token

const NAME_EXISTS = "NameExists";
const NO_SCHOOL_RESOLVED = "NoSchoolResolved";
const IN_USE = "InUse";
const SEED_PROTECTED = "SeedProtected";

const isProblem = (error: unknown, code: string): boolean =>
  error instanceof ApiError && error.kind === "http" && error.code === code;

/** The server's authoritative duplicate-name refusal — the client check above is only a shortcut. */
export const isNameConflict = (error: unknown): boolean => isProblem(error, NAME_EXISTS);

/** No school row exists to file a classification under — nothing about the form can fix this. */
export const isNoSchoolResolved = (error: unknown): boolean => isProblem(error, NO_SCHOOL_RESOLVED);

/** A delete refused because an event references the classification. Deactivate it instead. */
export const isInUse = (error: unknown): boolean => isProblem(error, IN_USE);

/** A delete refused because the classification is one of the seeded three. Deactivate it instead. */
export const isSeedProtected = (error: unknown): boolean => isProblem(error, SEED_PROTECTED);

export const NAME_TAKEN =
  "Another event classification in this school already uses this name. The list on this page is a " +
  "moment old, so one created since it loaded — by someone else, or in another tab — is not in it. " +
  "Choose a different name, or close this and reload the list.";

export const NO_SCHOOL_TO_FILE_UNDER =
  "The API could not resolve a school to file this classification under, so nothing was created. This " +
  "is a database that has never been seeded rather than anything wrong with what you typed.";
