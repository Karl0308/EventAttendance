// What `src/studentDraft.ts` accepts, what it refuses, and exactly what it puts on the wire.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and what it does not
// ---------------------------------------------------------------------------------------------
//
// The limits below (`STUDENT_NUMBER_MAX_LENGTH` and friends) are a **hand-written copy** of
// `StudentText` / `RfidCardText` in `backend/EAMS.Domain/DomainValues.cs`; the cleaning rules are a
// copy of `RosterText.Clean` / `CleanName` / `CleanEmail`; and `normalizeCardUid` is a copy of
// `CardUid.Normalize`. `studentDraft.ts` says so itself, above the constants.
//
// **These tests pin the client's copy. They cannot detect server drift.** They import the constants
// rather than restating the numbers, so what they assert is "the limit is applied at its own boundary,
// to the value that will actually be sent" — not "the limit is 50". If §4.3 changes to 40 tomorrow,
// every test here still passes and the form starts accepting student numbers the server refuses.
// Nothing in this repository compares the two, and a frontend unit test is structurally incapable of
// it: the binding artefact is those limits being published into `docs/api/openapi.json`, which is
// scheduled backend work (MDVault #206).
//
// What they *can* detect, and what they exist for:
//
//   1. **The derived-field trap.** `course`, `yearLevel` and `section` are refused by name by the
//      server. `StudentWriteRequest` types them `never` so a spread cannot compile — but a compile
//      guard is a property of the type-check, so the shape of the constructed body is asserted here at
//      runtime as well.
//   2. **The lossy read mapper.** `PUT /students/{id}` is a full replacement, so the round trip
//      through `draftFromStudent` → `validateStudent` has to reproduce every field the student
//      carried. A field dropped from `Student` or from `toStudent` fails here rather than silently
//      blanking a column on the first edit.
//   3. **The cleaning rules**, which decide both what is measured and what is stored. These are the
//      ones the server itself fixed a 500 over, and each has its premise asserted beside it so a test
//      cannot pass for the wrong reason.
//
// No time zone is involved anywhere in this module, unlike `eventDraft`, so nothing here depends on
// the suite's baseline zone.

import { describe, expect, it } from "vitest";

import {
  CARD_LABEL_MAX_LENGTH,
  CARD_UID_MAX_LENGTH,
  EMAIL_MAX_LENGTH,
  EMPTY_CARD_DRAFT,
  EMPTY_STUDENT_DRAFT,
  GENDER_MAX_LENGTH,
  PERSON_NAME_MAX_LENGTH,
  PHOTO_URL_MAX_LENGTH,
  STUDENT_NUMBER_MAX_LENGTH,
  VALIDATED_STUDENT_FIELDS,
  activeCardWithUid,
  cleanEmail,
  cleanName,
  cleanText,
  draftFromStudent,
  knownStatus,
  normalizeCardUid,
  validateCard,
  validateStudent,
} from "../src/studentDraft";
import type {
  CardDraft,
  CardFieldErrors,
  CardValidated,
  StudentDraft,
  StudentFieldErrors,
  StudentValidated,
} from "../src/studentDraft";
import { STUDENT_STATUSES } from "../src/types";
import type { Card, Student, StudentCardRequest, StudentWriteRequest } from "../src/types";

// ---------------------------------------------------------------------------------------------
// The characters that cannot be typed into a test and read back out of it
// ---------------------------------------------------------------------------------------------
//
// Named numerically for the reason `RosterText` gives on the server: a literal zero-width or
// non-breaking character in source is invisible in every diff, every editor and every review — and in
// a *test* that is worse than in production code, because the assertion silently stops being about
// what its name says. Every value below is built from these rather than pasted.

const ZERO_WIDTH_SPACE = "\u200B";
const BYTE_ORDER_MARK = "\uFEFF";
const NO_BREAK_SPACE = "\u00A0";

/** Combining tilde. `n` + this is the decomposed `ñ` a Mac Excel export produces. */
const COMBINING_TILDE = "\u0303";

/**
 * Combining Greek dialytika tonos — the character NFC *expands*. It has a singleton decomposition to
 * U+0308 U+0301 and does not recompose, so one character becomes two and a value that fits the column
 * before composition does not fit after.
 */
const EXPANDING_UNDER_NFC = "\u0344";

// ---------------------------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------------------------

/** A draft that passes, so each test can move exactly one thing and know that is what it measured. */
const baseDraft = (overrides: Partial<StudentDraft> = {}): StudentDraft => ({
  ...EMPTY_STUDENT_DRAFT,
  studentNumber: "USA12345",
  firstName: "Maria",
  lastName: "Santos",
  ...overrides,
});

const aCard = (overrides: Partial<Card> = {}): Card => ({
  id: "9c1e0000-0000-4000-8000-000000000001",
  cardUid: "04A7B8C9",
  label: undefined,
  isActive: true,
  ...overrides,
});

/**
 * A student as the server hands them back — every field populated, including the three derived cache
 * columns, because a fixture that left those undefined could not fail the trap test below.
 */
const serverStudent = (overrides: Partial<Student> = {}): Student => ({
  id: "6f2b1c00-0000-4000-8000-000000000001",
  studentNumber: "USA12345",
  fullName: "Maria Cruz Santos",
  firstName: "Maria",
  middleName: "Cruz",
  lastName: "Santos",
  email: "maria.santos@usa.edu.ph",
  gender: "Female",
  photoUrl: "https://example.test/photos/maria.jpg",
  course: "BSCS",
  yearLevel: "2",
  section: "BSCS 2-A",
  status: "Active",
  cards: [aCard()],
  ...overrides,
});

const characters = (count: number) => "x".repeat(count);

/** Fails loudly rather than returning a half-answer, so a mis-set-up test cannot read as a pass. */
function errorsOf(result: StudentValidated): StudentFieldErrors {
  if (result.ok) throw new Error("Expected validation to fail; it succeeded.");
  return result.errors;
}

function requestOf(result: StudentValidated): StudentWriteRequest {
  if (!result.ok) {
    throw new Error(`Expected validation to pass; it failed: ${JSON.stringify(result.errors)}`);
  }
  return result.request;
}

function cardErrorsOf(result: CardValidated): CardFieldErrors {
  if (result.ok) throw new Error("Expected card validation to fail; it succeeded.");
  return result.errors;
}

function cardRequestOf(result: CardValidated): StudentCardRequest {
  if (!result.ok) {
    throw new Error(`Expected card validation to pass; it failed: ${JSON.stringify(result.errors)}`);
  }
  return result.request;
}

// ---------------------------------------------------------------------------------------------
// THE TRAP — course / yearLevel / section
// ---------------------------------------------------------------------------------------------

describe("the derived academic fields", () => {
  /** Every key `StudentWriteRequest` may carry, and nothing else. */
  const WIRE_FIELDS = [
    "email",
    "firstName",
    "gender",
    "lastName",
    "middleName",
    "photoUrl",
    "status",
    "studentNumber",
  ];

  const DERIVED_FIELDS = ["course", "yearLevel", "section"] as const;

  it("builds a body of exactly the fields the contract models", () => {
    // The positive half of the trap. `StudentWriteRequest` types the three derived fields `never`, so
    // a `{ ...student }` body does not compile — but that guard is a property of the type-check, and
    // this asserts the property of the *value*, which is what actually reaches `JSON.stringify`.
    expect(Object.keys(requestOf(validateStudent(baseDraft()))).sort()).toEqual(WIRE_FIELDS);
  });

  it.each(DERIVED_FIELDS)("never puts %s on the wire, even from a full student", (field) => {
    // The student this is built from carries all three. The server refuses a request that names one —
    // `code: "FieldIsDerived"`, not a silent drop — so a body carrying one is a 400 the user cannot
    // act on, raised by a save they made to a name.
    const request = requestOf(validateStudent(draftFromStudent(serverStudent())));
    expect(Object.hasOwn(request, field)).toBe(false);
  });

  it("does not carry them into the draft either", () => {
    // The layer before: a draft field for one of these would be a box whose value has nowhere to go,
    // and the first person to wire it up would find `validateStudent` quietly ignoring it.
    const draft = draftFromStudent(serverStudent());
    for (const field of DERIVED_FIELDS) {
      expect(Object.hasOwn(draft, field)).toBe(false);
    }
  });
});

// ---------------------------------------------------------------------------------------------
// THE FULL-REPLACEMENT ROUND TRIP — the lossy-mapper guard
// ---------------------------------------------------------------------------------------------

describe("the round trip through the edit form", () => {
  it("reproduces every field the student carried", () => {
    // `PUT /students/{id}` writes every column from this body, so a field `Student` cannot read is a
    // field the edit form cannot preserve. Before D2 the type carried none of the name parts, no
    // `gender` and no `photoUrl` — an edit made to the e-mail address would have blanked the middle
    // name and reset the other two, silently, and been refused outright for the two NOT NULL columns.
    const student = serverStudent();
    const request = requestOf(validateStudent(draftFromStudent(student)));

    expect(request.studentNumber).toBe(student.studentNumber);
    expect(request.firstName).toBe(student.firstName);
    expect(request.middleName).toBe(student.middleName);
    expect(request.lastName).toBe(student.lastName);
    expect(request.email).toBe(student.email);
    expect(request.gender).toBe(student.gender);
    expect(request.photoUrl).toBe(student.photoUrl);
    expect(request.status).toBe(student.status);
  });

  it("turns absent optional fields into empty boxes and back into null", () => {
    const student = serverStudent({
      middleName: undefined,
      email: undefined,
      gender: undefined,
      photoUrl: undefined,
    });
    const draft = draftFromStudent(student);
    expect(draft.middleName).toBe("");

    const request = requestOf(validateStudent(draft));
    expect(request.middleName).toBeNull();
    expect(request.email).toBeNull();
    expect(request.gender).toBeNull();
    expect(request.photoUrl).toBeNull();
  });

  it.each(STUDENT_STATUSES)("round-trips the %s status", (status) => {
    expect(requestOf(validateStudent(draftFromStudent(serverStudent({ status })))).status).toBe(
      status,
    );
  });

  it("falls back to Active for a status this form cannot send back", () => {
    // Last line of defence, not the first: `Students.tsx` refuses to open the form at all in this
    // case, because falling back silently rewrites a student's status on a save meant for their name.
    expect(draftFromStudent(serverStudent({ status: "Withdrawn" })).status).toBe("Active");
  });
});

describe("knownStatus", () => {
  it.each(STUDENT_STATUSES)("recognises %s", (status) => {
    expect(knownStatus(status)).toBe(status);
  });

  it.each(["active", "ACTIVE", "Active ", "", "Withdrawn", "Enrolled"])(
    "refuses to narrow %j",
    (wire) => {
      // Strict, where the server's own `TryNormalize` trims and ignores case. That is not a mismatch:
      // this narrows a value the server *stored*, and the server stores the canonical spelling, so
      // anything else means the two builds disagree about the value set rather than that someone typed
      // it oddly.
      expect(knownStatus(wire)).toBeUndefined();
    },
  );
});

// ---------------------------------------------------------------------------------------------
// Cleaning — what is measured, and what is stored
// ---------------------------------------------------------------------------------------------

describe("cleanText", () => {
  it("answers null rather than an empty string for nothing", () => {
    // `null` and `""` are the same fact to the server (`RosterText.Clean` never returns `""`), and a
    // nullable column should get NULL rather than an empty string every later comparison has to know
    // about.
    expect(cleanText("")).toBeNull();
    expect(cleanText("   ")).toBeNull();
  });

  it("treats a field of nothing but a zero-width space as empty", () => {
    // The premise, asserted rather than assumed — and it is also the negative control: U+200B is
    // Unicode category Cf, NOT whitespace, so a `trim()`-based emptiness check passes it. That gap is
    // what reached a NOT NULL column on the server as SQL error 515, a 500 on input the caller got
    // wrong.
    expect(ZERO_WIDTH_SPACE.trim().length).toBe(1);

    expect(cleanText(ZERO_WIDTH_SPACE)).toBeNull();
    expect(
      errorsOf(validateStudent(baseDraft({ firstName: ZERO_WIDTH_SPACE }))).firstName,
    ).toBeDefined();
  });

  it("strips zero-width characters from inside a value", () => {
    expect(cleanText(`Ma${ZERO_WIDTH_SPACE}ria`)).toBe("Maria");
    expect(cleanText(`Maria${BYTE_ORDER_MARK}`)).toBe("Maria");
  });

  it("folds a run of whitespace to one space", () => {
    // 'CA  2' is the visible half of the same problem the invisible characters are, and the real
    // roster contains it.
    expect(cleanText("CA  2")).toBe("CA 2");
    expect(cleanText("Maria\t\nCruz")).toBe("Maria Cruz");
  });

  it("folds a non-breaking space to an ordinary one", () => {
    // Two values a human reads as identical must not differ by a U+00A0 nobody can see.
    expect(cleanText(`Maria${NO_BREAK_SPACE}Santos`)).toBe("Maria Santos");
  });

  it("trims", () => {
    expect(cleanText("  Maria  ")).toBe("Maria");
  });

  it("composes to NFC, so one decomposed character counts as one", () => {
    // The premise: 'n' plus a combining tilde is two characters and composes to one.
    const decomposed = `n${COMBINING_TILDE}`;
    expect(decomposed.length).toBe(2);
    expect(decomposed.normalize("NFC").length).toBe(1);

    expect(cleanText(decomposed)).toBe(decomposed.normalize("NFC"));
    expect(cleanText(decomposed)).toHaveLength(1);
  });
});

describe("length is measured on the cleaned value", () => {
  it("accepts a name that is over the limit only before folding", () => {
    // Measuring the raw box would refuse a name the server accepts: the doubled space is one character
    // that will never be stored.
    const raw = `${characters(PERSON_NAME_MAX_LENGTH - 3)}  ab`;
    expect(raw.length).toBe(PERSON_NAME_MAX_LENGTH + 1);
    expect(cleanText(raw)).toHaveLength(PERSON_NAME_MAX_LENGTH);

    expect(requestOf(validateStudent(baseDraft({ firstName: raw }))).firstName).toBe(cleanText(raw));
  });

  it("refuses a name that only goes over the limit once NFC expands it", () => {
    // The premise, and the reason the server measures after cleaning too — asserted rather than
    // assumed, because the expansion depends on what the mark is attached to and this test would
    // otherwise pass for the wrong reason. U+0344 decomposes to U+0308 U+0301 and does not recompose,
    // so after a base letter with no precomposed diaeresis form it leaves three characters where the
    // box held two. After an `x` or an `a` it recomposes into two and there is nothing to see, which
    // is why the base letter here is chosen rather than incidental.
    const withCombiningMark = `q${EXPANDING_UNDER_NFC}`;
    expect(withCombiningMark).toHaveLength(2);
    expect(withCombiningMark.normalize("NFC")).toHaveLength(3);

    const raw = characters(PERSON_NAME_MAX_LENGTH - 2) + withCombiningMark;
    expect(raw).toHaveLength(PERSON_NAME_MAX_LENGTH);
    expect(cleanText(raw)).toHaveLength(PERSON_NAME_MAX_LENGTH + 1);

    const errors = errorsOf(validateStudent(baseDraft({ firstName: raw })));
    expect(errors.firstName).toBeDefined();
    // The message quotes what the value actually measures, not what the box holds — otherwise it would
    // report a 100-character name as being 100 characters and too long.
    expect(errors.firstName).toContain(String(PERSON_NAME_MAX_LENGTH + 1));
  });

  it("carries the cleaned value into the request, not the raw one", () => {
    const request = requestOf(
      validateStudent(baseDraft({ firstName: "  Maria  Luisa  ", lastName: " Santos " })),
    );
    expect(request.firstName).toBe("Maria Luisa");
    expect(request.lastName).toBe("Santos");
  });
});

describe("cleanName", () => {
  it.each(["-", "--", ".", "/", " - "])("folds the placeholder %j to nothing", (placeholder) => {
    // 200 of the sample roster's 536 rows spell "no middle name" as a literal dash. Stored as written
    // they render "Maria - Santos" and every search for a middle initial matches a hyphen.
    expect(cleanName(placeholder)).toBeNull();
  });

  it("keeps a real initial, period and all", () => {
    expect(cleanName("E.")).toBe("E.");
  });

  it("sends null for a middle name given as a dash", () => {
    expect(requestOf(validateStudent(baseDraft({ middleName: "-" }))).middleName).toBeNull();
  });

  it("is what the middle name uses, where the other fields use plain cleaning", () => {
    // The asymmetry is the server's: `RosterText.CleanName` for the middle name, `Clean` for the rest.
    // A first name of "-" is therefore not folded away — it is a required column holding a hyphen,
    // which the server accepts too. Odd data, faithfully round-tripped, rather than a silent blank.
    expect(cleanText("-")).toBe("-");
    expect(requestOf(validateStudent(baseDraft({ firstName: "-" }))).firstName).toBe("-");
  });
});

describe("cleanEmail", () => {
  it("lower-cases, because the server stores it lower", () => {
    expect(cleanEmail("  Maria.Santos@USA.EDU.PH ")).toBe("maria.santos@usa.edu.ph");
  });

  it("answers null for a blank", () => {
    expect(cleanEmail("   ")).toBeNull();
  });

  it("applies no format rule at all", () => {
    // Deliberate: the server checks this column's length and nothing else, so a client-side format
    // rule would refuse an address the API would have accepted.
    expect(validateStudent(baseDraft({ email: "not an address" })).ok).toBe(true);
  });
});

// ---------------------------------------------------------------------------------------------
// The §4.3 limits
// ---------------------------------------------------------------------------------------------

describe("the field limits", () => {
  const cases = [
    ["studentNumber", STUDENT_NUMBER_MAX_LENGTH],
    ["firstName", PERSON_NAME_MAX_LENGTH],
    ["middleName", PERSON_NAME_MAX_LENGTH],
    ["lastName", PERSON_NAME_MAX_LENGTH],
    ["email", EMAIL_MAX_LENGTH],
    ["gender", GENDER_MAX_LENGTH],
    ["photoUrl", PHOTO_URL_MAX_LENGTH],
  ] as const;

  it.each(cases)("accepts a %s of exactly the maximum", (field, max) => {
    expect(validateStudent(baseDraft({ [field]: characters(max) })).ok).toBe(true);
  });

  it.each(cases)("refuses a %s one character over, and says how long it is", (field, max) => {
    const errors = errorsOf(validateStudent(baseDraft({ [field]: characters(max + 1) })));
    expect(errors[field]).toBeDefined();
    expect(errors[field]).toContain(String(max + 1));
  });
});

describe("the required fields", () => {
  const required = ["studentNumber", "firstName", "lastName"] as const;

  it.each(required)("requires %s", (field) => {
    expect(errorsOf(validateStudent(baseDraft({ [field]: "" })))[field]).toBeDefined();
  });

  it.each(required)("treats a whitespace-only %s as missing", (field) => {
    expect(errorsOf(validateStudent(baseDraft({ [field]: "   " })))[field]).toBeDefined();
  });

  it("does not require the optional ones", () => {
    const request = requestOf(
      validateStudent(baseDraft({ middleName: "", email: "", gender: "", photoUrl: "" })),
    );
    expect(request.middleName).toBeNull();
    expect(request.email).toBeNull();
    expect(request.gender).toBeNull();
    expect(request.photoUrl).toBeNull();
  });
});

describe("validateStudent's answer", () => {
  it("answers ok:true with a request", () => {
    const result = validateStudent(baseDraft());
    expect(result.ok).toBe(true);
    expect(requestOf(result)).toBeDefined();
  });

  it("answers ok:false with at least one error", () => {
    const result = validateStudent(baseDraft({ firstName: "" }));
    expect(result.ok).toBe(false);
    expect(Object.keys(errorsOf(result)).length).toBeGreaterThan(0);
  });

  it("only ever reports errors on fields VALIDATED_STUDENT_FIELDS knows about", () => {
    // `VALIDATED_STUDENT_FIELDS` drives "focus the first invalid box". An error on a field missing from
    // it would be an error nothing can focus — the form would refuse to submit and look inert.
    const errors = errorsOf(
      validateStudent({
        studentNumber: "",
        firstName: "",
        middleName: characters(PERSON_NAME_MAX_LENGTH + 1),
        lastName: "",
        email: characters(EMAIL_MAX_LENGTH + 1),
        gender: characters(GENDER_MAX_LENGTH + 1),
        photoUrl: characters(PHOTO_URL_MAX_LENGTH + 1),
        status: "Active",
      }),
    );

    const reported = Object.keys(errors);
    expect(reported.length).toBeGreaterThan(0);
    for (const field of reported) {
      expect(VALIDATED_STUDENT_FIELDS).toContain(field);
    }
  });

  it("starts a new student as Active", () => {
    expect(EMPTY_STUDENT_DRAFT.status).toBe("Active");
  });
});

// ---------------------------------------------------------------------------------------------
// Cards
// ---------------------------------------------------------------------------------------------

describe("normalizeCardUid", () => {
  it.each(["04:a7:b8:c9", "04-A7-B8-C9", "04 a7 b8 c9", "04a7b8c9", "  04:A7:b8:C9  "])(
    "reads %j as one card",
    (raw) => {
      expect(normalizeCardUid(raw)).toBe("04A7B8C9");
    },
  );

  it("keeps letters and digits and nothing else", () => {
    expect(normalizeCardUid("#$%")).toBe("");
  });

  it("preserves leading zeros", () => {
    // Load-bearing throughout this system: a serial that loses its leading zeros stops resolving, and
    // `RosterText.FormatNumericCell` exists on the server because of one student's REGNO.
    expect(normalizeCardUid("0012503326")).toBe("0012503326");
  });

  it("is idempotent", () => {
    const once = normalizeCardUid("04:a7:b8:c9");
    expect(normalizeCardUid(once)).toBe(once);
  });
});

describe("validateCard", () => {
  const cardDraft = (overrides: Partial<CardDraft> = {}): CardDraft => ({
    ...EMPTY_CARD_DRAFT,
    cardUid: "04:a7:b8:c9",
    ...overrides,
  });

  it("sends the normalised UID, not what was typed", () => {
    expect(cardRequestOf(validateCard(cardDraft())).cardUid).toBe("04A7B8C9");
  });

  it("requires a UID", () => {
    expect(cardErrorsOf(validateCard(cardDraft({ cardUid: "" }))).cardUid).toBeDefined();
  });

  it.each([":::", "  ", "- -"])("refuses %j, which normalises to nothing", (cardUid) => {
    // A blank UID would occupy the one active slot for the empty UID in its school and match no tap
    // ever — a silent, permanent hole in the roster.
    expect(normalizeCardUid(cardUid)).toBe("");
    expect(cardErrorsOf(validateCard(cardDraft({ cardUid }))).cardUid).toBeDefined();
  });

  it("measures the UID after normalising, not before", () => {
    // A reader that sends `04:A7:B8:C9` must be judged on the characters that land in the column. This
    // raw value is well over the limit and the stored one is exactly at it.
    const raw = Array.from({ length: CARD_UID_MAX_LENGTH }, () => "a").join(":");
    expect(raw.length).toBeGreaterThan(CARD_UID_MAX_LENGTH);
    expect(normalizeCardUid(raw)).toHaveLength(CARD_UID_MAX_LENGTH);

    expect(cardRequestOf(validateCard(cardDraft({ cardUid: raw }))).cardUid).toHaveLength(
      CARD_UID_MAX_LENGTH,
    );
  });

  it("refuses a UID one character over once normalised", () => {
    const errors = cardErrorsOf(
      validateCard(cardDraft({ cardUid: characters(CARD_UID_MAX_LENGTH + 1) })),
    );
    expect(errors.cardUid).toBeDefined();
    expect(errors.cardUid).toContain(String(CARD_UID_MAX_LENGTH + 1));
  });

  it("accepts a label of exactly the maximum and refuses one over", () => {
    expect(validateCard(cardDraft({ label: characters(CARD_LABEL_MAX_LENGTH) })).ok).toBe(true);
    expect(
      cardErrorsOf(validateCard(cardDraft({ label: characters(CARD_LABEL_MAX_LENGTH + 1) }))).label,
    ).toBeDefined();
  });

  it("sends null rather than an empty string for an omitted label", () => {
    expect(cardRequestOf(validateCard(cardDraft({ label: "  " }))).label).toBeNull();
  });

  it("cleans the label the way every other text field is cleaned", () => {
    expect(cardRequestOf(validateCard(cardDraft({ label: " ID  2026 " }))).label).toBe("ID 2026");
  });
});

describe("activeCardWithUid", () => {
  it("finds an active card the student already holds", () => {
    expect(activeCardWithUid([aCard()], "04A7B8C9")?.cardUid).toBe("04A7B8C9");
  });

  it("ignores a detached card with the same UID", () => {
    // A detached row is kept — that is ADR-001 D-3 — so a match on it would tell the user they already
    // hold a card they cannot tap with, and suppress the attach that would give it back to them.
    expect(activeCardWithUid([aCard({ isActive: false })], "04A7B8C9")).toBeUndefined();
  });

  it("answers undefined when the student holds nothing like it", () => {
    expect(activeCardWithUid([aCard()], "DEADBEEF")).toBeUndefined();
  });

  it("compares normalised against normalised", () => {
    // The caller normalises before asking. Passing a raw reading is the mistake this pins the shape
    // of: the stored value has no separators, so a raw reading cannot match.
    expect(activeCardWithUid([aCard()], "04:a7:b8:c9")).toBeUndefined();
    expect(activeCardWithUid([aCard()], normalizeCardUid("04:a7:b8:c9"))?.cardUid).toBe("04A7B8C9");
  });
});
