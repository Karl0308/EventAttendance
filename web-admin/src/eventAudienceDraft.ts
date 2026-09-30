// What is in an Event Audience form's boxes, the rules those boxes are held to, the request they become,
// and what the surface's specific refusals mean.
//
// `eventClassificationDraft.ts`'s sibling: `POST /event-audiences` and `PUT /event-audiences/{id}` take
// the same body (`AudienceDefinitionWriteRequest`) and are checked by the same server code, so one copy of
// the rules serves both the create and edit paths of the one dialog.
//
// Free of React and MUI: pure functions over plain values. `ApiError` is imported for the conflict tests,
// exactly as `eventClassificationDraft.ts` imports it.

import { ApiError } from "./api";
import { cleanText } from "./studentDraft";
import type {
  AudienceCriteria,
  AudienceDefinition,
  AudienceDefinitionWriteRequest,
  AudienceScope,
  AudienceType,
} from "./types";

const isPadded = (value: string): boolean => value !== value.trim();

const PADDED =
  "Remove the space at the start or end — it is stored exactly as typed, and the API refuses padding " +
  "rather than trimming it.";

/** The nine audience types, with a label and a one-line description, in the order the picker offers them. */
export const AUDIENCE_TYPES: ReadonlyArray<{
  value: AudienceType;
  label: string;
  hint: string;
}> = [
  { value: "UniversityWide", label: "University-wide", hint: "Everyone, or all students / all employees." },
  { value: "Department", label: "Department", hint: "Students and employees in the chosen departments." },
  { value: "Program", label: "Program", hint: "Students in the chosen programs." },
  { value: "YearLevel", label: "Year level", hint: "Students in the chosen year levels." },
  { value: "Section", label: "Section", hint: "Students in the chosen sections." },
  {
    value: "EmployeeClassification",
    label: "Employee classification",
    hint: "Employees of the chosen classifications.",
  },
  { value: "Organization", label: "Organization", hint: "Employees in the chosen organizations." },
  {
    value: "SpecificIndividuals",
    label: "Specific individuals",
    hint: "A hand-picked list of students and/or employees.",
  },
  { value: "Custom", label: "Custom", hint: "Combine several criteria (all must match)." },
];

export const AUDIENCE_SCOPES: ReadonlyArray<{ value: AudienceScope; label: string }> = [
  { value: "Both", label: "Students and employees" },
  { value: "Students", label: "Students only" },
  { value: "Employees", label: "Employees only" },
];

export const labelForAudienceType = (type: AudienceType): string =>
  AUDIENCE_TYPES.find((t) => t.value === type)?.label ?? type;

export interface AudienceDraft {
  name: string;
  eventClassificationId: string;
  audienceType: AudienceType;
  criteria: AudienceCriteria;
}

export const EMPTY_AUDIENCE_DRAFT: AudienceDraft = {
  name: "",
  eventClassificationId: "",
  audienceType: "UniversityWide",
  criteria: { scope: "Both" },
};

export const VALIDATED_AUDIENCE_FIELDS = [
  "name",
  "eventClassificationId",
  "audienceType",
  "criteria",
] as const;

export type ValidatedAudienceField = (typeof VALIDATED_AUDIENCE_FIELDS)[number];

export type AudienceFieldErrors = Partial<Record<ValidatedAudienceField, string>>;

export const NO_AUDIENCE_ERRORS: AudienceFieldErrors = {};

/** The boxes filled from a definition that already exists — the edit form's starting state. Verbatim. */
export function draftFromAudience(definition: AudienceDefinition): AudienceDraft {
  return {
    name: definition.name,
    eventClassificationId: definition.eventClassificationId,
    audienceType: definition.audienceType,
    criteria: { ...definition.criteria },
  };
}

// -------------------------------------------------------------- the duplicate name, caught on screen

const normalizeKey = (value: string): string => value.replace(/[^0-9a-z]/gi, "").toUpperCase();

export function nameCollision(
  others: readonly AudienceDefinition[],
  name: string,
): AudienceDefinition | undefined {
  const key = normalizeKey(name);
  if (key.length === 0) return undefined;
  return others.find((d) => normalizeKey(d.name) === key);
}

export const duplicateNameMessage = (definition: AudienceDefinition): string =>
  `“${definition.name}” already exists in this school. Names are compared with punctuation, spacing and ` +
  `case removed, so this one has to differ.`;

// ------------------------------------------------------------------------------------- validation

const nonEmpty = (values: string[] | undefined): string[] =>
  (values ?? []).map((v) => v.trim()).filter((v) => v.length > 0);

/**
 * Reduces the draft criteria to only the fields the chosen type reads, dropping the rest. The server
 * interprets criteria per type, but sending a `Program` audience the `sections` a user typed before
 * switching type would store dead data — so the shape is pruned to the type on the way out.
 */
export function criteriaForType(type: AudienceType, criteria: AudienceCriteria): AudienceCriteria {
  switch (type) {
    case "UniversityWide":
      return { scope: criteria.scope ?? "Both" };
    case "Department":
      return { departments: nonEmpty(criteria.departments) };
    case "Program":
      return { programs: nonEmpty(criteria.programs) };
    case "YearLevel":
      return { yearLevels: nonEmpty(criteria.yearLevels) };
    case "Section":
      return { sections: nonEmpty(criteria.sections) };
    case "EmployeeClassification":
      return { classifications: nonEmpty(criteria.classifications) };
    case "Organization":
      return { organizations: nonEmpty(criteria.organizations) };
    case "SpecificIndividuals":
      return {
        studentIds: nonEmpty(criteria.studentIds),
        personnelIds: nonEmpty(criteria.personnelIds),
      };
    case "Custom":
      return {
        departments: nonEmpty(criteria.departments),
        programs: nonEmpty(criteria.programs),
        yearLevels: nonEmpty(criteria.yearLevels),
        sections: nonEmpty(criteria.sections),
        classifications: nonEmpty(criteria.classifications),
        organizations: nonEmpty(criteria.organizations),
      };
    default:
      return {};
  }
}

/** Whether pruned criteria carry at least one selection the type can act on — mirrors the server's guard. */
function criteriaSatisfied(type: AudienceType, c: AudienceCriteria): boolean {
  switch (type) {
    case "UniversityWide":
      return c.scope === "Students" || c.scope === "Employees" || c.scope === "Both";
    case "Department":
      return (c.departments ?? []).length > 0;
    case "Program":
      return (c.programs ?? []).length > 0;
    case "YearLevel":
      return (c.yearLevels ?? []).length > 0;
    case "Section":
      return (c.sections ?? []).length > 0;
    case "EmployeeClassification":
      return (c.classifications ?? []).length > 0;
    case "Organization":
      return (c.organizations ?? []).length > 0;
    case "SpecificIndividuals":
      return (c.studentIds ?? []).length > 0 || (c.personnelIds ?? []).length > 0;
    case "Custom":
      return (
        (c.departments ?? []).length > 0 ||
        (c.programs ?? []).length > 0 ||
        (c.yearLevels ?? []).length > 0 ||
        (c.sections ?? []).length > 0 ||
        (c.classifications ?? []).length > 0 ||
        (c.organizations ?? []).length > 0
      );
    default:
      return false;
  }
}

const CRITERIA_MESSAGE: Record<AudienceType, string> = {
  UniversityWide: "Choose who the audience covers.",
  Department: "Select at least one department.",
  Program: "Select at least one program.",
  YearLevel: "Select at least one year level.",
  Section: "Select at least one section.",
  EmployeeClassification: "Select at least one employee classification.",
  Organization: "Select at least one organization.",
  SpecificIndividuals: "Select at least one student or employee.",
  Custom: "A custom audience needs at least one criterion.",
};

export type AudienceValidated =
  | { ok: true; request: AudienceDefinitionWriteRequest }
  | { ok: false; errors: AudienceFieldErrors };

/**
 * The write routes' rules, applied to the values that will actually be written. The name is sent verbatim
 * (over-length is left to the server, whose `detail` names the limit — the choice the sibling drafts make);
 * criteria are pruned to the type and required to carry a selection the type can act on.
 */
export function validateAudience(
  draft: AudienceDraft,
  others: readonly AudienceDefinition[] = [],
): AudienceValidated {
  const errors: AudienceFieldErrors = {};

  if (cleanText(draft.name) === null) {
    errors.name = "A name is required, such as “All BSIT Students”.";
  } else if (isPadded(draft.name)) {
    errors.name = PADDED;
  } else {
    const clash = nameCollision(others, draft.name);
    if (clash !== undefined) errors.name = duplicateNameMessage(clash);
  }

  if (cleanText(draft.eventClassificationId) === null) {
    errors.eventClassificationId = "Choose the event classification this audience belongs to.";
  }

  const criteria = criteriaForType(draft.audienceType, draft.criteria);
  if (!criteriaSatisfied(draft.audienceType, criteria)) {
    errors.criteria = CRITERIA_MESSAGE[draft.audienceType];
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  return {
    ok: true,
    request: {
      name: draft.name,
      eventClassificationId: draft.eventClassificationId,
      audienceType: draft.audienceType,
      criteria,
    },
  };
}

// ------------------------------------------------------------------ the refusals, by machine token

const NAME_EXISTS = "NameExists";
const NO_SCHOOL_RESOLVED = "NoSchoolResolved";
const CLASSIFICATION_UNAVAILABLE = "ClassificationUnavailable";

const isProblem = (error: unknown, code: string): boolean =>
  error instanceof ApiError && error.kind === "http" && error.code === code;

/** The server's authoritative duplicate-name refusal — the client check above is only a shortcut. */
export const isNameConflict = (error: unknown): boolean => isProblem(error, NAME_EXISTS);

/** No school row exists to file an audience under — nothing about the form can fix this. */
export const isNoSchoolResolved = (error: unknown): boolean => isProblem(error, NO_SCHOOL_RESOLVED);

/** The chosen classification does not exist, is deactivated, or belongs to another school. */
export const isClassificationUnavailable = (error: unknown): boolean =>
  isProblem(error, CLASSIFICATION_UNAVAILABLE);

export const NAME_TAKEN =
  "Another event audience in this school already uses this name. The list on this page is a moment old, " +
  "so one created since it loaded — by someone else, or in another tab — is not in it. Choose a different " +
  "name, or close this and reload the list.";

export const CLASSIFICATION_GONE =
  "The event classification you chose is no longer available — it may have been deactivated or removed " +
  "since this form opened. Pick another classification, or close this and reload.";

export const NO_SCHOOL_TO_FILE_UNDER =
  "The API could not resolve a school to file this audience under, so nothing was created. This is a " +
  "database that has never been seeded rather than anything wrong with what you typed.";
