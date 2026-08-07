// What is in a term form's boxes, the rules those boxes are held to, the request they become — and
// what the two refusals that are *specific to terms* mean.
//
// `deviceDraft.ts`'s sibling and deliberately the same shape: `POST /academic/terms` and
// `PUT /academic/terms/{id}` take the **same** body (`TermWriteRequest`) and are checked by the same
// server code, so a second copy of these rules beside the edit dialog would be two readings of one
// contract with nothing binding them.
//
// It also carries the `eventAudience.ts` half of the job — mirroring what a specific status *code*
// from this surface means — because the term surface has two conflicts sharing one status, and
// telling them apart is the difference between "fix this box" and "this database has no school row".
//
// Free of React and of MUI: pure functions over plain values. `ApiError` is imported for the conflict
// tests, exactly as `apiGuidance.ts` and `eventAudience.ts` import it.

import { ApiError } from "./api";
import { cleanText } from "./studentDraft";
import type { Term, TermWriteRequest } from "./types";

// ---------------------------------------------------------------------------------------------
// The server's rules, restated — and the one it has that this cannot
// ---------------------------------------------------------------------------------------------
//
// `docs/api/openapi.json` publishes the `400` on both write routes as "a field is blank,
// over-length, whitespace-padded, or the dates run backwards". Three of those four are checked here,
// so the ordinary mistakes cost no round trip.
//
// **Over-length is deliberately not**, and the omission is the honest one: no column limit for
// `Terms.Code`, `SchoolYear` or `Semester` is published anywhere this client can read — unlike
// `DeviceText`'s two, which `deviceDraft.ts` copies and labels as a copy that will drift. Inventing a
// number here would produce a form refusing a code the server would have accepted, with no way past
// it. An over-length value is therefore sent, refused with a 400, and the server's own `detail` is
// what names the limit. If those limits are ever published, they belong beside this note.

/**
 * Whether a value carries leading or trailing whitespace — **a `400`, not something to clean off.**
 *
 * This is the one place this client deliberately does *not* apply `cleanText`. A term code is the one
 * natural key in the academic layer that a person authors rather than a spreadsheet supplies, so the
 * server stores it exactly as typed and refuses padding rather than silently trimming it (the same
 * refusal covers `schoolYear` and `semester`, per that `400`'s own wording). A client that trimmed
 * would be writing a *different* code from the one on screen, and the operator would find out when
 * the code they read in the form does not match the one in a filter six weeks later.
 *
 * So the padding is reported, and the text is left alone for the user to fix.
 */
const isPadded = (value: string): boolean => value !== value.trim();

/** Said the same way for all three authored fields, so one message cannot drift from the other two. */
const PADDED = "Remove the space at the start or end — it is stored exactly as typed, and the API refuses padding rather than trimming it.";

// ---------------------------------------------------------------------------------------------
// The term draft
// ---------------------------------------------------------------------------------------------

/**
 * What is in the boxes, which is not what is sent. Both dates are `string` because an
 * `<input type="date">` holds `""` for "no date" and cannot hold `null`; the conversion happens once,
 * in `validateTerm`.
 *
 * **There is deliberately nothing `isCurrent`-shaped here.** `TermWriteRequest` carries no such field:
 * moving that flag is a two-row transaction under a filtered unique index, so it is
 * `PATCH /academic/terms/{id}/current` and nothing else. A checkbox on this form would be a box whose
 * value has nowhere to go — and worse, one suggesting that saving a typo fix could redefine which
 * semester the institution is in.
 */
export interface TermDraft {
  code: string;
  schoolYear: string;
  semester: string;
  /** `YYYY-MM-DD` or `""`. Normally `""` — the SIS export has no term-date columns. */
  startsOn: string;
  endsOn: string;
}

export type TermDraftField = keyof TermDraft;

export const EMPTY_TERM_DRAFT: TermDraft = {
  code: "",
  schoolYear: "",
  semester: "",
  startsOn: "",
  endsOn: "",
};

/**
 * The fields that can carry an error, **in the order they appear on screen** — which is what makes
 * "focus the first invalid one" land on the first invalid one the user can see.
 */
export const VALIDATED_TERM_FIELDS = ["code", "schoolYear", "semester", "startsOn", "endsOn"] as const;

export type ValidatedTermField = (typeof VALIDATED_TERM_FIELDS)[number];

export type TermFieldErrors = Partial<Record<ValidatedTermField, string>>;

export const NO_TERM_ERRORS: TermFieldErrors = {};

/**
 * Stable ids, because MUI derives `<label for>` and the `aria-describedby` that ties an input to its
 * error text from the `id` given to the `TextField` — and because "focus the field the server just
 * refused" needs something to look the box up by. Built from a prefix so the create and edit dialogs
 * cannot collide.
 */
export const termFieldIdsFor = (prefix: string): Record<TermDraftField, string> => ({
  code: `${prefix}-code`,
  schoolYear: `${prefix}-school-year`,
  semester: `${prefix}-semester`,
  startsOn: `${prefix}-starts-on`,
  endsOn: `${prefix}-ends-on`,
});

/**
 * The boxes filled from a term that already exists — the edit form's starting state.
 *
 * Verbatim, including any padding a term created before this form existed may carry: showing the
 * stored value is what lets someone see why a save is being refused. Nothing is defaulted, because
 * `PUT` is a **full replacement** — a field this function quietly substituted would be written.
 */
export function draftFromTerm(term: Term): TermDraft {
  return {
    code: term.code,
    schoolYear: term.schoolYear,
    semester: term.semester,
    startsOn: term.startsOn ?? "",
    endsOn: term.endsOn ?? "",
  };
}

/** `code — schoolYear semester`, for a dialog sentence or an announcement naming one term. */
export const termLabel = (term: Term): string =>
  `${term.code} (${term.schoolYear} ${term.semester})`;

// ---------------------------------------------------------------------------------------------
// Dates — calendar dates, and never instants
// ---------------------------------------------------------------------------------------------

const DATE_SHAPE = /^(\d{4})-(\d{2})-(\d{2})$/;

/**
 * Whether a box holds a real calendar date.
 *
 * The shape test alone is not enough: `2026-02-30` matches it and is not a day. Round-tripping
 * through `Date.UTC` is what separates the two — **UTC and not the local constructor**, because these
 * are calendar dates in the school's own timezone with no time of day, and building them as local
 * midnight would make a term boundary shift a day for a browser west of the school. Nothing here
 * compares them to *now*, so no zone is involved beyond that.
 *
 * A native `<input type="date">` cannot produce anything else. A browser that falls back to a text
 * box can, which is the case this exists for.
 */
function isCalendarDate(value: string): boolean {
  const parts = DATE_SHAPE.exec(value);
  if (parts === null) return false;
  const year = Number(parts[1]);
  const month = Number(parts[2]);
  const day = Number(parts[3]);
  const at = new Date(Date.UTC(year, month - 1, day));
  return (
    at.getUTCFullYear() === year && at.getUTCMonth() === month - 1 && at.getUTCDate() === day
  );
}

// ---------------------------------------------------------------------------------------------
// The duplicate code — the error the operator will actually hit
// ---------------------------------------------------------------------------------------------

/**
 * A term already holding a code, and **how closely** — because the two answers earn different
 * treatment and collapsing them would get one of them wrong.
 *
 * `"same"` — the same string. The server's `UNIQUE(SchoolId, Code)` will refuse this, so the form
 * refuses it first and costs no round trip.
 *
 * `"differs-only-by-case"` — a *probable* refusal, not a certain one. SQL Server's default collation
 * is case-insensitive, so `2025-2026-A` and `2025-2026-a` are very likely one code to that index —
 * but the collation is a database setting this client cannot read, and blocking on a guess would
 * leave an operator unable to create a code the server would have accepted, with no way past the
 * form. So it is a **warning that does not block**: they can still press Save, and if the index
 * disagrees the 409 arrives and is shown on the same field.
 */
export type CodeCollision =
  | { kind: "same"; term: Term }
  | { kind: "differs-only-by-case"; term: Term };

/**
 * Whether `code` collides with one of `others`.
 *
 * @param others every term this draft must not collide with — the caller excludes the term being
 *   edited, because a term is not a duplicate of itself and an edit that leaves the code alone must
 *   still be saveable.
 *
 * The client check is a **convenience and never the authority**: the list it reads was fetched
 * moments ago, and a second operator creating the same code in between is decided by the index. That
 * race is why `isTermCodeConflict` below exists as well as this.
 */
export function codeCollision(others: readonly Term[], code: string): CodeCollision | undefined {
  const same = others.find((term) => term.code === code);
  if (same !== undefined) return { kind: "same", term: same };

  const folded = code.toLowerCase();
  const nearly = others.find((term) => term.code.toLowerCase() === folded);
  return nearly === undefined ? undefined : { kind: "differs-only-by-case", term: nearly };
}

/** Shown on the code field, blocking. Names the term holding it so the operator can go and look. */
export const duplicateCodeMessage = (term: Term): string =>
  `“${term.code}” is already the code of ${term.schoolYear} ${term.semester}. Term codes are unique within a school, so this one has to differ.`;

/**
 * Shown on the code field as helper text, **not** as an error — see `CodeCollision`.
 */
export const caseCollisionWarning = (term: Term): string =>
  `This differs from “${term.code}” (${term.schoolYear} ${term.semester}) only by capitalisation. Term codes are usually compared case-insensitively, so the API may still refuse this as a duplicate.`;

// ---------------------------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------------------------

/**
 * Either the request, or why there is not one. One function, so "may this be sent" and "what exactly
 * is sent" cannot disagree.
 */
export type TermValidated =
  | { ok: true; request: TermWriteRequest }
  | { ok: false; errors: TermFieldErrors };

/**
 * The write routes' rules, applied to the values that will actually be written.
 *
 * **Every string is sent verbatim** — see `isPadded`. `cleanText` appears only in the *required*
 * test, where it answers "is there anything in this box at all": a field holding nothing but a
 * zero-width space is not empty by `trim()` and is blank to the server, so measuring the raw box
 * would show a valid-looking form and earn a 400 naming a field the user believes they filled in.
 * What is measured and what is sent differ here on purpose, and that is the whole difference between
 * this module and `deviceDraft`.
 *
 * @param others the terms whose codes this one may not collide with; `[]` when the caller has no list
 *   to compare against, which leaves the server as the only check — correct, just slower.
 */
export function validateTerm(draft: TermDraft, others: readonly Term[] = []): TermValidated {
  const errors: TermFieldErrors = {};

  if (cleanText(draft.code) === null) {
    errors.code = "A term code is required — the operator-authored name for this school year and semester, such as “2025-2026-1”.";
  } else if (isPadded(draft.code)) {
    errors.code = PADDED;
  } else {
    const collision = codeCollision(others, draft.code);
    // Only `"same"` blocks. The case-only near-match is a warning the form renders as helper text.
    if (collision?.kind === "same") errors.code = duplicateCodeMessage(collision.term);
  }

  if (cleanText(draft.schoolYear) === null) {
    errors.schoolYear = "A school year is required, such as “2025-2026”.";
  } else if (isPadded(draft.schoolYear)) {
    errors.schoolYear = PADDED;
  }

  if (cleanText(draft.semester) === null) {
    errors.semester = "A semester is required, such as “1st Semester”.";
  } else if (isPadded(draft.semester)) {
    errors.semester = PADDED;
  }

  // Both dates are optional and normally absent, so an empty box is never an error here.
  if (draft.startsOn !== "" && !isCalendarDate(draft.startsOn)) {
    errors.startsOn = "This is not a date. Use the picker, or type it as YYYY-MM-DD.";
  }
  if (draft.endsOn !== "" && !isCalendarDate(draft.endsOn)) {
    errors.endsOn = "This is not a date. Use the picker, or type it as YYYY-MM-DD.";
  }

  // Compared as strings, which is exact for `YYYY-MM-DD` and needs no `Date` at all: the format is
  // fixed-width and zero-padded, so lexical order *is* chronological order. Guarded on both boxes
  // being well-formed, so a half-typed date cannot produce a second error about the pair.
  if (
    errors.startsOn === undefined &&
    errors.endsOn === undefined &&
    draft.startsOn !== "" &&
    draft.endsOn !== "" &&
    draft.endsOn < draft.startsOn
  ) {
    // On `endsOn` rather than `startsOn`: the end is the box that was typed second and is the one the
    // operator is more likely to have got wrong. The server refuses the same pair with a 400.
    errors.endsOn = "The term cannot end before it starts.";
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  // Field by field, never a spread: a body's contents are a decision rather than an inheritance, and
  // `PUT` is a full replacement, so anything carried in by accident is written.
  return {
    ok: true,
    request: {
      code: draft.code,
      schoolYear: draft.schoolYear,
      semester: draft.semester,
      // `null`, never omitted. Both routes take a full body and a missing member on a replacement is
      // ambiguous where an explicit null is not: this clears the date, and says so.
      startsOn: draft.startsOn === "" ? null : draft.startsOn,
      endsOn: draft.endsOn === "" ? null : draft.endsOn,
    },
  };
}

// ---------------------------------------------------------------------------------------------
// The two 409s, which are not the same 409
// ---------------------------------------------------------------------------------------------

/**
 * `POST /academic/terms` answers **409 for two unrelated things**: the code is taken, or no school
 * could be resolved to file the term under. That is why these tests read `ApiError.code` — the stable
 * machine token — and not the status.
 *
 * A test on `status === 409` alone would put "this installation has no school row" on the code field
 * as if the operator had typed a duplicate, sending them to rename a code that was never the problem.
 * `eventAudience.ts` can test its 409 on the status because that surface has exactly one conflict;
 * this one does not, and the difference is worth the extra field.
 *
 * The cost of being strict is stated rather than hidden: a 409 whose `code` extension is missing —
 * an intermediary rewriting the body, a version skew — matches neither test and falls through to the
 * generic write alert, which shows the server's own `detail`. That is the honest failure, and it is
 * the cheaper one: a wrong attribution reads as a fact.
 */
const TERM_CODE_EXISTS = "TermCodeExists";
const NO_SCHOOL_RESOLVED = "NoSchoolResolved";

const isTermProblem = (error: unknown, code: string): boolean =>
  error instanceof ApiError && error.kind === "http" && error.code === code;

/** The server's authoritative duplicate-code refusal. The client check above is only a shortcut. */
export const isTermCodeConflict = (error: unknown): boolean =>
  isTermProblem(error, TERM_CODE_EXISTS);

/** No school row exists to file a term under — nothing about the form can fix this. */
export const isNoSchoolResolved = (error: unknown): boolean =>
  isTermProblem(error, NO_SCHOOL_RESOLVED);

/**
 * What to say when the server refuses the code — which happens *despite* the client check, and the
 * message says why so it does not read as the form having failed to notice.
 */
export const TERM_CODE_TAKEN =
  "Another term in this school already uses this code. The list on this page is a moment old, so a " +
  "term created since it loaded — by someone else, or in another tab — is not in it. Choose a " +
  "different code, or close this and reload the list to see what is there.";

/** The other 409, which no edit to this form can clear. */
export const NO_SCHOOL_TO_FILE_UNDER =
  "The API could not resolve a school to file this term under, so nothing was created. This is a " +
  "database that has never been seeded rather than anything wrong with what you typed — no change " +
  "here will clear it, and it needs someone with database access.";

// ---------------------------------------------------------------------------------------------
// Moving the current flag — the consequence, said before the click
// ---------------------------------------------------------------------------------------------

/**
 * `PATCH /current` in the two directions it has. There is no third: **D-53 offers no deletion**,
 * because a term with a batch imported against it cannot be removed without data loss, so retiring
 * *is* clearing the flag.
 */
export type CurrentTermIntent = "make-current" | "retire";

/** What the confirmation says, so the page renders prose rather than composing it. */
export interface CurrentTermConsequence {
  title: string;
  /** What this does. */
  effect: string;
  /** What it does to something the operator is not looking at — the part that would be a surprise. */
  consequence: string;
  confirmLabel: string;
  /** Announced after it lands, in the past tense. */
  settled: string;
}

/**
 * Why the outgoing term has to be named **before** the press: at most one term per school can be
 * current (a filtered unique index, not a convention), so making one current *silently clears
 * another*. The operator pressed a button about term B and term A changed. An admin who does that
 * mid-semester has moved what the roster-import page defaults to and what every "current term" read
 * answers with, and nothing on screen would have told them which term they gave up.
 *
 * Retiring gets the same treatment from the other side: it leaves the school with **no** current
 * term, which is an ordinary state the API answers for (`GET /academic/terms/current` 404s) and an
 * unhelpful one to arrive at by accident.
 *
 * @param terms every term, so the one currently flagged can be found. The caller passes the list it
 *   rendered, and a `target` that is itself current is handled: making it current again is a no-op
 *   the server answers `200` to, and the sentence says so rather than naming the target as its own
 *   predecessor.
 */
export function currentTermConsequence(
  intent: CurrentTermIntent,
  target: Term,
  terms: readonly Term[],
): CurrentTermConsequence {
  const label = termLabel(target);

  if (intent === "retire") {
    return {
      title: `Retire ${target.code}?`,
      effect: `${label} stops being the current term. Nothing is deleted — this school's terms, its imported rosters and its events all stay exactly as they are, and this term can be made current again at any time.`,
      consequence:
        "The school is then left with no current term at all. That is an ordinary state, but until " +
        "another term is made current the roster-import page has nothing to default to and anything " +
        "asking for “the current term” gets no answer.",
      confirmLabel: "Retire this term",
      settled: `${label} is no longer the current term.`,
    };
  }

  const outgoing = terms.find((term) => term.isCurrent);

  if (outgoing !== undefined && outgoing.id === target.id) {
    return {
      title: `${target.code} is already current`,
      effect: `${label} is already this school's current term, so this changes nothing and the API answers with the term unchanged.`,
      consequence: "No other term is affected.",
      confirmLabel: "Set it current anyway",
      settled: `${label} is the current term.`,
    };
  }

  return {
    title: `Make ${target.code} the current term?`,
    effect: `${label} becomes this school's current term. It is what the roster-import page will default to, and what every read asking for “the current term” will answer with.`,
    consequence:
      outgoing === undefined
        ? "This school has no current term at the moment, so nothing else changes."
        : `${termLabel(outgoing)} stops being current at the same moment — only one term per school can hold it, so this is one action, not two. Nothing about that term is deleted, and it can be made current again.`,
    confirmLabel: "Make it current",
    settled: `${label} is now the current term.`,
  };
}
