// What is in a student form's boxes, the rules those boxes are held to, and the request they become.
//
// `eventDraft.ts`'s sibling, and deliberately the same shape: `POST /students` and `PUT /students/{id}`
// take the *same* body and are checked by the *same* server code (`StudentService.Validate`), so a
// second copy of these rules beside the edit dialog would be two readings of one contract with nothing
// binding them. The card rules live here too, for the same reason — the attach form is the only place
// they are applied, but they are the same kind of thing and belong beside their siblings rather than
// inside a component.
//
// Free of React and of `api.ts`: pure functions over plain values, which is what makes it testable —
// and unlike D1, it ships with the tests (`test/studentDraft.test.ts`).

import { STUDENT_STATUS, STUDENT_STATUSES } from "./types";
import type { Card, Student, StudentCardRequest, StudentStatusName, StudentWriteRequest } from "./types";

// ---------------------------------------------------------------------------------------------
// The server's rules, restated
// ---------------------------------------------------------------------------------------------
//
// Every limit below is `StudentText` / `RfidCardText` in `EAMS.Domain/DomainValues.cs`, checked again
// here. Not because the client is trusted — it is not, and `StudentService.Validate` still has the
// last word — but because a round trip to be told a name is four characters too long is a bad way to
// learn it.
//
// Restated rather than fetched: there is no endpoint that publishes them, so the honest description of
// these is "a copy that will drift if §4.3/§4.4 changes". Publishing them into `docs/api/openapi.json`
// is scheduled backend work (MDVault #206) and is the only thing that could bind the two.

/** `Students.StudentNumber nvarchar(50)`. */
export const STUDENT_NUMBER_MAX_LENGTH = 50;

/** `Students.FirstName / MiddleName / LastName nvarchar(100)` — one width for all three. */
export const PERSON_NAME_MAX_LENGTH = 100;

/** `Students.Email nvarchar(256)`. */
export const EMAIL_MAX_LENGTH = 256;

/** `Students.Gender nvarchar(20)`. */
export const GENDER_MAX_LENGTH = 20;

/** `Students.PhotoUrl nvarchar(1000)`. */
export const PHOTO_URL_MAX_LENGTH = 1000;

/** `RfidCards.CardUid nvarchar(128)`, measured **after** normalisation — see `normalizeCardUid`. */
export const CARD_UID_MAX_LENGTH = 128;

/** `RfidCards.Label nvarchar(100)`. */
export const CARD_LABEL_MAX_LENGTH = 100;

/** §4.3's own column default, which the server also applies when `status` is null or blank. */
const DEFAULT_STUDENT_STATUS: StudentStatusName = STUDENT_STATUS.Active;

/** What the status picker calls each value. The values themselves are the wire's, and are not changed. */
export const STUDENT_STATUS_LABELS: Record<StudentStatusName, string> = {
  Active: "Active — currently enrolled",
  Inactive: "Inactive — not currently enrolled",
  Graduated: "Graduated",
};

// ---------------------------------------------------------------------------------------------
// Cleaning — the rule that decides what is measured, and what is stored
// ---------------------------------------------------------------------------------------------
//
// This is the students-shaped version of `eventDraft`'s "trim before measuring", and it is a bigger
// rule than a trim. `RosterText.Clean` runs on every text field the students write surface accepts,
// **before** its length is checked and **as** the value that is stored, so a client that measured the
// raw box would disagree with the server in both directions:
//
//   - it would refuse a name the server accepts, because `'Maria  Cruz'` (two spaces) stores as
//     `'Maria Cruz'` and is one character shorter than it looks;
//   - it would accept one the server refuses, because NFC composition can *expand* a string —
//     U+0344 becomes U+0308 U+0301, one character into two — so a 100-character decomposed name is
//     101 after composition and the column raises error 2628.
//
// And the case that is not about length at all: a field holding nothing but a zero-width space is not
// empty by `trim()` (U+200B is Unicode category Cf, not whitespace) but cleans to nothing. The server
// treats that as missing; a client that did not would show a valid-looking form and get a 400 naming a
// field the user believes they filled in.
//
// Mirrors `RosterText.Clean` step for step, including the order: zero-width characters go before
// whitespace is folded, because a run of one NBSP and one space is not a run of the same character and
// a naive collapse would leave the invisible one standing.

/**
 * Zero-width space / non-joiner / joiner, word joiner, byte-order mark — `RosterText.ZeroWidth`.
 *
 * Spelled as escapes rather than as literal characters, for the reason the server file gives: a
 * literal zero-width character in source is invisible in every diff, every editor and every review,
 * so the one place they are named must be the one place they are readable.
 *
 * An alternation rather than a character class, and not a style choice: `no-misleading-character-
 * class` refuses a class containing U+200D, because a zero-width joiner inside one is nearly always
 * a mis-pasted emoji sequence rather than a deliberate member. Here it is deliberate — the joiner is
 * one of the five characters being stripped — so the pattern is written the way that says so rather
 * than the way that has to be excused.
 */
const ZERO_WIDTH = /\u200B|\u200C|\u200D|\u2060|\uFEFF/gu;

/**
 * What folds to a single space.
 *
 * `\s` already covers NBSP, the U+2000 block, U+202F, U+205F and U+3000, so the ordinary cases
 * agree with `char.IsWhiteSpace` for free. U+0085 (next line) is named separately because it is the
 * one character the two definitions disagree about — .NET counts it as whitespace and JavaScript's
 * `\s` does not — so without it a pasted NEL would fold on the server and survive here, and the
 * length this form measured would not be the length the column checked.
 */
const WHITESPACE_RUN = /[\s\u0085]+/gu;

/**
 * Whether anything survives once punctuation is discarded — `AcademicKey.Normalize(...).Length == 0`,
 * expressed as a test rather than as a transformation. `\p{Nd}` rather than `\p{N}` because
 * `char.IsLetterOrDigit` counts decimal digits only.
 */
const HAS_MEANINGFUL_CHARACTER = /[\p{L}\p{Nd}]/u;

/**
 * A box's contents as the server will store them, or `null` when nothing survives.
 *
 * `null` and never `""`, exactly as `RosterText.Clean` does: a blank box and an absent field are the
 * same fact, and a nullable column should get `NULL` rather than an empty string it would then have to
 * be compared against everywhere.
 */
export function cleanText(value: string): string | null {
  const composed = value.normalize("NFC").replace(ZERO_WIDTH, "");
  const folded = composed.replace(WHITESPACE_RUN, " ").trim();
  return folded === "" ? null : folded;
}

/**
 * `cleanText` for a column that holds a *name*, where the source spells "absent" as a placeholder
 * rather than as a blank — `RosterText.CleanName`.
 *
 * 200 of the sample roster's 536 rows carry a literal `-` for the middle name and another 175 are
 * blank; both mean the same thing, and stored as written the first group renders "Maria - Santos". The
 * test is "does anything remain once punctuation is discarded" rather than a list of known
 * placeholders, so `--`, `.` and `/` are covered without anyone having had to see them first — while
 * `E.`, which is a real initial, keeps its period.
 */
export function cleanName(value: string): string | null {
  const cleaned = cleanText(value);
  if (cleaned === null) return null;
  return HAS_MEANINGFUL_CHARACTER.test(cleaned) ? cleaned : null;
}

/**
 * `cleanText` plus lower-casing — `RosterText.CleanEmail`. The domain half is case-insensitive by RFC
 * and the roster spells the same address both ways, so it is stored lower and compared lower.
 */
export function cleanEmail(value: string): string | null {
  const cleaned = cleanText(value);
  return cleaned === null ? null : cleaned.toLowerCase();
}

/**
 * Anything but a letter or a digit. `CardUid.Normalize` is `Where(char.IsLetterOrDigit).ToUpperInvariant()`,
 * so this is its inverse as a character class.
 */
const NOT_UID_CHARACTER = /[^\p{L}\p{Nd}]/gu;

/**
 * A card UID as it will actually be stored: uppercase, every separator stripped. `04:a7:b8:c9`,
 * `04-A7-B8-C9` and `04 a7 b8 c9` are one card, stored as `04A7B8C9`.
 *
 * Normalising **before** sending rather than leaving it to the server, even though the server
 * normalises whatever arrives, for two reasons that are not politeness. The uniqueness index that
 * decides whether a UID is already taken is over the normalised form, so a client comparing raw
 * readings against a student's existing cards would disagree with the database about what a duplicate
 * is. And the form can then show what will be stored — a user who types `04:a7:b8:c9` and later sees
 * `04A7B8C9` in the list has been surprised by the system rather than told by it.
 *
 * Honest about its edges: this agrees with the server for every UID a reader emits, which is hex text.
 * It can differ from .NET for characters no reader produces — JavaScript's `toUpperCase` maps `ß` to
 * `SS` where `ToUpperInvariant` leaves it — and where they differ the server's stored form is the one
 * the list shows after the write. The server is the authority; this is a faithful preview of it, not a
 * second implementation of the truth.
 */
export const normalizeCardUid = (raw: string): string =>
  raw.replace(NOT_UID_CHARACTER, "").toUpperCase();

// ---------------------------------------------------------------------------------------------
// The student draft
// ---------------------------------------------------------------------------------------------

/**
 * What is in the boxes, which is not what is sent.
 *
 * Every field is the control's own value type: `string` for the text boxes including the optional
 * ones, because a box cannot hold `null` while it is being retyped. The conversion to
 * `StudentWriteRequest` happens once, in `validateStudent`, and only when it can succeed.
 *
 * **There is deliberately no `course`, `yearLevel` or `section` here.** They are the ADR-001 D-2
 * derived cache; a draft field for one would be a box whose value has nowhere to go. The form shows
 * them, read-only and labelled as to where they come from — see `StudentFormFields`.
 */
export interface StudentDraft {
  studentNumber: string;
  firstName: string;
  middleName: string;
  lastName: string;
  email: string;
  gender: string;
  photoUrl: string;
  status: StudentStatusName;
}

export type StudentDraftField = keyof StudentDraft;

export const EMPTY_STUDENT_DRAFT: StudentDraft = {
  studentNumber: "",
  firstName: "",
  middleName: "",
  lastName: "",
  email: "",
  gender: "",
  photoUrl: "",
  status: DEFAULT_STUDENT_STATUS,
};

/**
 * The fields that can carry an error, **in the order they appear on screen** — which is what makes
 * "focus the first invalid one" land on the first invalid one the user can see. `status` is absent
 * because a select cannot hold a value outside its own options.
 */
export const VALIDATED_STUDENT_FIELDS = [
  "studentNumber",
  "firstName",
  "middleName",
  "lastName",
  "email",
  "gender",
  "photoUrl",
] as const;

export type ValidatedStudentField = (typeof VALIDATED_STUDENT_FIELDS)[number];

export type StudentFieldErrors = Partial<Record<ValidatedStudentField, string>>;

export const NO_STUDENT_ERRORS: StudentFieldErrors = {};

/**
 * Stable ids, because MUI derives `<label for>` and the `aria-describedby` that ties an input to its
 * error text from the `id` given to the `TextField` — and because "focus the first invalid box" needs
 * something to look the box up by.
 *
 * Built from a prefix so the create and edit dialogs cannot collide if both are ever mounted at once.
 */
export const studentFieldIdsFor = (prefix: string): Record<StudentDraftField, string> => ({
  studentNumber: `${prefix}-student-number`,
  firstName: `${prefix}-first-name`,
  middleName: `${prefix}-middle-name`,
  lastName: `${prefix}-last-name`,
  email: `${prefix}-email`,
  gender: `${prefix}-gender`,
  photoUrl: `${prefix}-photo-url`,
  status: `${prefix}-status`,
});

/**
 * The wire's status as one this client can send back, or nothing. `Student.status` is `string` because
 * a response cannot prove a union; this is where that becomes a decision instead of an assumption.
 */
export const knownStatus = (wire: string): StudentStatusName | undefined =>
  STUDENT_STATUSES.find((status) => status === wire);

/**
 * The boxes filled from a student that already exists — the edit form's starting state.
 *
 * `status` is narrowed against the same set the picker is built from rather than asserted. The
 * fallback is the last line of defence and must not be the *first*: `Students.tsx` refuses to open the
 * form at all for a status this build does not recognise, because falling back silently is how a
 * graduated student is quietly set back to Active by someone who came to fix a typo in their name.
 *
 * Nothing here reads `course`, `yearLevel` or `section`. That is not an omission — see `StudentDraft`.
 */
export function draftFromStudent(student: Student): StudentDraft {
  return {
    studentNumber: student.studentNumber,
    firstName: student.firstName,
    middleName: student.middleName ?? "",
    lastName: student.lastName,
    email: student.email ?? "",
    gender: student.gender ?? "",
    photoUrl: student.photoUrl ?? "",
    status: knownStatus(student.status) ?? DEFAULT_STUDENT_STATUS,
  };
}

// ---------------------------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------------------------

/**
 * Either the request, or why there is not one. One function, so "may this be sent" and "what exactly
 * is sent" cannot disagree — a separate `isValid` predicate beside a separate builder is two readings
 * of one set of rules, and the day they drift is the day a form submits a body it just called fine.
 */
export type StudentValidated =
  | { ok: true; request: StudentWriteRequest }
  | { ok: false; errors: StudentFieldErrors };

/** One phrasing for every length refusal, quoting the limit and what the *cleaned* value measures. */
const tooLong = (max: number, actual: number) => `${max} characters at most; this is ${actual}.`;

/**
 * §4.3's column rules, applied to the values that will actually be written.
 *
 * Every check runs on the **cleaned** value and the request carries that same string, which is the
 * whole design and mirrors the server's `StoredStudent`: cleaning once and carrying the result through
 * is what stops "the value that was checked" and "the value that will be sent" being two different
 * strings. The server fixed a 500 by making that unrepresentable; this is the client side of the same
 * fix, and it is why a required field is judged on `cleanText(...) === null` rather than on
 * `.trim() === ""`.
 *
 * The three derived cache columns have no line here. They cannot: `StudentWriteRequest` types them
 * `never`, so writing one would not compile — see that type's note for why the compiler holds this
 * rather than a convention.
 */
export function validateStudent(draft: StudentDraft): StudentValidated {
  const errors: StudentFieldErrors = {};

  // Cleaned, never uppercased and deliberately NOT normalised the way a card UID is: a student number
  // is the registrar's value verbatim, and a client that normalised it differently would produce a
  // student the next roster import cannot match.
  const studentNumber = cleanText(draft.studentNumber);
  if (studentNumber === null) {
    errors.studentNumber = "A student number is required.";
  } else if (studentNumber.length > STUDENT_NUMBER_MAX_LENGTH) {
    errors.studentNumber = tooLong(STUDENT_NUMBER_MAX_LENGTH, studentNumber.length);
  }

  const firstName = cleanText(draft.firstName);
  if (firstName === null) {
    errors.firstName = "A first name is required.";
  } else if (firstName.length > PERSON_NAME_MAX_LENGTH) {
    errors.firstName = tooLong(PERSON_NAME_MAX_LENGTH, firstName.length);
  }

  // `cleanName`, not `cleanText`: it additionally folds the roster's `-` placeholder to nothing, so a
  // manual entry and an import agree about "no middle name" instead of rendering "Maria - Santos".
  const middleName = cleanName(draft.middleName);
  if (middleName !== null && middleName.length > PERSON_NAME_MAX_LENGTH) {
    errors.middleName = tooLong(PERSON_NAME_MAX_LENGTH, middleName.length);
  }

  const lastName = cleanText(draft.lastName);
  if (lastName === null) {
    errors.lastName = "A last name is required.";
  } else if (lastName.length > PERSON_NAME_MAX_LENGTH) {
    errors.lastName = tooLong(PERSON_NAME_MAX_LENGTH, lastName.length);
  }

  const email = cleanEmail(draft.email);
  if (email !== null && email.length > EMAIL_MAX_LENGTH) {
    errors.email = tooLong(EMAIL_MAX_LENGTH, email.length);
  }

  const gender = cleanText(draft.gender);
  if (gender !== null && gender.length > GENDER_MAX_LENGTH) {
    errors.gender = tooLong(GENDER_MAX_LENGTH, gender.length);
  }

  const photoUrl = cleanText(draft.photoUrl);
  if (photoUrl !== null && photoUrl.length > PHOTO_URL_MAX_LENGTH) {
    errors.photoUrl = tooLong(PHOTO_URL_MAX_LENGTH, photoUrl.length);
  }

  // The three `=== null` arms are what narrow the required values for the request below; they cannot
  // fire on their own, because each of them set an error above. The `errors` check is the real
  // condition and it is first.
  if (
    Object.keys(errors).length > 0 ||
    studentNumber === null ||
    firstName === null ||
    lastName === null
  ) {
    return { ok: false, errors };
  }

  // Field by field, never `{ ...student }`. The compiler would refuse the spread — that is what the
  // `never` fields on `StudentWriteRequest` are for — but this is also simply the honest way to build a
  // body whose contents are a decision rather than an inheritance.
  return {
    ok: true,
    request: {
      studentNumber,
      firstName,
      middleName,
      lastName,
      email,
      gender,
      photoUrl,
      status: draft.status,
    },
  };
}

// ---------------------------------------------------------------------------------------------
// The card draft
// ---------------------------------------------------------------------------------------------

/** What is in the attach-card form's two boxes. */
export interface CardDraft {
  cardUid: string;
  label: string;
}

export type CardDraftField = keyof CardDraft;

export const EMPTY_CARD_DRAFT: CardDraft = { cardUid: "", label: "" };

/** Both card fields can carry an error, and they are already in screen order. */
export const VALIDATED_CARD_FIELDS = ["cardUid", "label"] as const;

export type ValidatedCardField = (typeof VALIDATED_CARD_FIELDS)[number];

export type CardFieldErrors = Partial<Record<ValidatedCardField, string>>;

export const NO_CARD_ERRORS: CardFieldErrors = {};

export const cardFieldIdsFor = (prefix: string): Record<CardDraftField, string> => ({
  cardUid: `${prefix}-card-uid`,
  label: `${prefix}-card-label`,
});

export type CardValidated =
  | { ok: true; request: StudentCardRequest }
  | { ok: false; errors: CardFieldErrors };

/**
 * §4.4's rules for an assignment, applied to the normalised UID — which is what
 * `RfidCardText.IsValidNormalizedCardUid` measures, and for the reason it records: a reader that sends
 * `04:A7:B8:C9` must be judged on the eight characters that land in the column, not on the eleven it
 * typed.
 *
 * A UID that normalises to nothing is refused rather than stored blank. The server says why and it is
 * worth repeating: an empty UID would occupy the one active slot for the empty UID in its school and
 * match no tap ever — a silent, permanent hole in the roster.
 */
export function validateCard(draft: CardDraft): CardValidated {
  const errors: CardFieldErrors = {};

  const cardUid = normalizeCardUid(draft.cardUid);
  if (cardUid.length === 0) {
    errors.cardUid =
      "A card UID is required, and it must contain at least one letter or digit — separators alone " +
      "are stripped and leave nothing to store.";
  } else if (cardUid.length > CARD_UID_MAX_LENGTH) {
    errors.cardUid = tooLong(CARD_UID_MAX_LENGTH, cardUid.length);
  }

  const label = cleanText(draft.label);
  if (label !== null && label.length > CARD_LABEL_MAX_LENGTH) {
    errors.label = tooLong(CARD_LABEL_MAX_LENGTH, label.length);
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  return { ok: true, request: { cardUid, label } };
}

/**
 * The student's active card holding this UID, if they already hold it.
 *
 * Normalise first, compare second: the caller passes a UID that has already been through
 * `normalizeCardUid`, and `Card.cardUid` arrives from the server in the stored — normalised — form, so
 * the comparison is between two values in the same shape. Used to tell the user *before* they press
 * Attach that this card is already theirs, because the server answers that case with a 201 and a
 * message rather than a refusal, and a success that appears to have done nothing is the confusing one.
 */
export const activeCardWithUid = (
  cards: readonly Card[],
  normalizedUid: string,
): Card | undefined =>
  cards.find((card) => card.isActive && card.cardUid === normalizedUid);
