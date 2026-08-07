// What `src/termDraft.ts` accepts, what it refuses, what it *sends* — and the two 409s that are not
// the same 409.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and what it does not
// ---------------------------------------------------------------------------------------------
//
// The rules here are a restatement of the write surface's own, published in
// `docs/api/openapi.json` as "a field is blank, over-length, whitespace-padded, or the dates run
// backwards". **Nothing in this repository compares the two**, so these tests pin the client's
// reading and — like `deviceDraft.test.ts`, `studentDraft.test.ts` and `eventDraft.test.ts` before
// them — are structurally incapable of detecting server drift.
//
// One of the four is deliberately absent from both the module and this file: **over-length**. No
// column limit for `Terms.Code`, `SchoolYear` or `Semester` is published anywhere the client can
// read, and a guessed number would produce a form refusing a value the server accepts with no way
// past it. That is an omission with a reason, not a gap, and it is asserted below so that anyone
// adding a limit has to come and read this note.
//
// What these tests exist for:
//
//   1. **The code is sent exactly as typed.** This is the one field in the app that is deliberately
//      *not* cleaned, and the pressure to "just trim it" is permanent — every neighbouring draft
//      module cleans. The verbatim assertions are the tripwire.
//   2. **Padding is refused rather than fixed**, which is the same decision seen from the other side.
//   3. **The duplicate check blocks on an exact match and only warns on a case-only one.** Blocking
//      on the second would be blocking on a guess about the database's collation.
//   4. **`isTermCodeConflict` is false for `NoSchoolResolved`.** Both are 409 on `POST`, so a test on
//      the status alone would put "this database has never been seeded" on the code field as though
//      the operator had typed a duplicate. This is the sharpest edge in the module.
//   5. **`currentTermConsequence` names the term that will stop being current.** That sentence is the
//      whole reason the confirmation dialog exists, and a dialog is not needed to test it.
//
// No time zone is involved: the dates here are calendar dates compared as text, and `isCalendarDate`
// round-trips through `Date.UTC` precisely so nothing depends on the suite's baseline zone.

import { describe, expect, it } from "vitest";

import { ApiError } from "../src/api";
import {
  EMPTY_TERM_DRAFT,
  VALIDATED_TERM_FIELDS,
  codeCollision,
  currentTermConsequence,
  draftFromTerm,
  isNoSchoolResolved,
  isTermCodeConflict,
  termFieldIdsFor,
  termLabel,
  validateTerm,
} from "../src/termDraft";
import type { TermDraft, TermValidated } from "../src/termDraft";
import type { Term } from "../src/types";

// ---------------------------------------------------------------------------------------------
// Subjects
// ---------------------------------------------------------------------------------------------

const VALID: TermDraft = {
  code: "2025-2026-1",
  schoolYear: "2025-2026",
  semester: "1st Semester",
  startsOn: "",
  endsOn: "",
};

const draft = (patch: Partial<TermDraft> = {}): TermDraft => ({ ...VALID, ...patch });

const term = (id: string, code: string, isCurrent = false): Term => ({
  id,
  code,
  schoolYear: "2025-2026",
  semester: "1st Semester",
  isCurrent,
});

/** Narrows to the refused arm and fails with a readable message when it is the wrong one. */
function refusal(validated: TermValidated) {
  if (validated.ok) throw new Error("expected the draft to be refused, but it validated");
  return validated.errors;
}

/** Narrows to the accepted arm. */
function accepted(validated: TermValidated) {
  if (!validated.ok) {
    throw new Error(`expected the draft to validate, but it was refused: ${JSON.stringify(validated.errors)}`);
  }
  return validated.request;
}

// ---------------------------------------------------------------------------------------------
// The three required fields
// ---------------------------------------------------------------------------------------------

describe("the fields a term cannot be created without", () => {
  it("accepts the ordinary term", () => {
    expect(validateTerm(VALID).ok).toBe(true);
  });

  it("refuses an empty draft on all three text fields at once", () => {
    // All three rather than the first: the form shows every error it has, and focus goes to the
    // first, so a validator that stopped at the first refusal would hide two of the three.
    const errors = refusal(validateTerm(EMPTY_TERM_DRAFT));
    expect(errors.code).toBeDefined();
    expect(errors.schoolYear).toBeDefined();
    expect(errors.semester).toBeDefined();
  });

  it("treats a box holding only invisible characters as empty", () => {
    // U+200B, named numerically because a literal zero-width character in source is invisible in
    // every diff, editor and review — `studentDraft.test.ts` records the same reasoning. `trim()`
    // does not remove it, so a client measuring the raw box would call this filled in and earn a 400
    // naming a field the user believes they typed.
    const zeroWidth = "​";
    expect(zeroWidth.trim()).toBe(zeroWidth);
    expect(refusal(validateTerm(draft({ code: zeroWidth }))).code).toBeDefined();
  });

  it("does not invent a length limit", () => {
    // The deliberate omission, pinned. No limit is published for these columns, so an over-length
    // value is sent and refused by the server with a `detail` that names the real limit. A future
    // change that adds a client-side maximum has to delete this test, which is where the note above
    // will be read.
    const long = "x".repeat(5000);
    expect(validateTerm(draft({ code: long, schoolYear: long, semester: long })).ok).toBe(true);
  });
});

// ---------------------------------------------------------------------------------------------
// Verbatim — the decision this module exists to hold
// ---------------------------------------------------------------------------------------------

describe("the value that is sent is the value that was typed", () => {
  it("does not clean, collapse or re-case the three authored strings", () => {
    // Interior runs, mixed case and punctuation all survive. `cleanText` — which every neighbouring
    // draft module applies — would fold the double space to one, and the stored code would then
    // differ from the one the operator is looking at.
    const typed = draft({
      code: "2025-2026  Sem_1/A",
      schoolYear: "2025 – 2026",
      semester: "1st  Semester",
    });
    const request = accepted(validateTerm(typed));

    expect(request.code).toBe(typed.code);
    expect(request.schoolYear).toBe(typed.schoolYear);
    expect(request.semester).toBe(typed.semester);
  });

  it("refuses padding instead of trimming it", () => {
    // The whole decision in one assertion: the padded value is *refused*, and the refusal is a
    // message on the field. Nothing anywhere returns a trimmed request for this input — a client that
    // trimmed would write a different code from the one on screen.
    const padded = draft({ code: " 2025-2026-1" });
    expect(refusal(validateTerm(padded)).code).toBeDefined();

    expect(refusal(validateTerm(draft({ code: "2025-2026-1 " }))).code).toBeDefined();
    expect(refusal(validateTerm(draft({ schoolYear: " 2025-2026" }))).schoolYear).toBeDefined();
    expect(refusal(validateTerm(draft({ semester: "1st Semester " }))).semester).toBeDefined();
  });

  it("round-trips a term through the edit form unchanged", () => {
    // `PUT` is a full replacement, so anything `draftFromTerm` normalised would be *written* on a
    // save someone made for an unrelated reason.
    const stored: Term = {
      id: "t1",
      code: "2025-2026-1",
      schoolYear: "2025-2026",
      semester: "1st Semester",
      isCurrent: true,
      startsOn: "2025-08-11",
      endsOn: "2025-12-20",
    };

    const request = accepted(validateTerm(draftFromTerm(stored)));

    expect(request).toEqual({
      code: stored.code,
      schoolYear: stored.schoolYear,
      semester: stored.semester,
      startsOn: stored.startsOn,
      endsOn: stored.endsOn,
    });
    // And nothing about `isCurrent` reached the body: moving that flag is its own route, and a `PUT`
    // carrying it would be one resource's payload rewriting a different resource.
    expect(request).not.toHaveProperty("isCurrent");
  });
});

// ---------------------------------------------------------------------------------------------
// Dates
// ---------------------------------------------------------------------------------------------

describe("the two optional dates", () => {
  it("sends an empty box as null rather than omitting it", () => {
    // Explicit, because both routes take a full body: on a replacement a missing member is ambiguous
    // where a null is not — this clears the date and says so.
    const request = accepted(validateTerm(VALID));
    expect(request.startsOn).toBeNull();
    expect(request.endsOn).toBeNull();
  });

  it("accepts a term that ends on the day it starts", () => {
    // `>=`, not `>`. A one-day term is odd and is not wrong, and the server's rule is "may not fall
    // before".
    expect(validateTerm(draft({ startsOn: "2025-08-11", endsOn: "2025-08-11" })).ok).toBe(true);
  });

  it("refuses dates that run backwards, on the end date", () => {
    const errors = refusal(validateTerm(draft({ startsOn: "2025-12-20", endsOn: "2025-08-11" })));
    expect(errors.endsOn).toBeDefined();
    // Reported once, on the box more likely to be wrong — not twice, which would read as two faults.
    expect(errors.startsOn).toBeUndefined();
  });

  it("refuses a date shaped like one that is not a day", () => {
    // The reason the check is a round-trip and not a regex: `2026-02-30` matches every shape test.
    expect(refusal(validateTerm(draft({ startsOn: "2026-02-30" }))).startsOn).toBeDefined();
    expect(refusal(validateTerm(draft({ endsOn: "not-a-date" }))).endsOn).toBeDefined();
  });

  it("does not add an ordering error on top of an unreadable date", () => {
    // A half-typed date would otherwise produce two errors about one box, the second of which is
    // meaningless.
    const errors = refusal(validateTerm(draft({ startsOn: "2026-13-01", endsOn: "2025-01-01" })));
    expect(errors.startsOn).toBeDefined();
    expect(errors.endsOn).toBeUndefined();
  });

  it("accepts a leap day and refuses the same date in a common year", () => {
    expect(validateTerm(draft({ startsOn: "2028-02-29" })).ok).toBe(true);
    expect(refusal(validateTerm(draft({ startsOn: "2027-02-29" }))).startsOn).toBeDefined();
  });
});

// ---------------------------------------------------------------------------------------------
// The duplicate — the error the operator will actually hit
// ---------------------------------------------------------------------------------------------

describe("a code another term already holds", () => {
  const EXISTING = [term("t1", "2025-2026-1", true), term("t2", "2024-2025-2")];

  it("blocks an exact duplicate before it is sent", () => {
    expect(refusal(validateTerm(draft({ code: "2025-2026-1" }), EXISTING)).code).toBeDefined();
  });

  it("does not block a code that differs only by case — that is a warning, not a refusal", () => {
    // The collation is a database setting this client cannot read. Blocking would leave an operator
    // unable to create a code the server may well accept, with no way past the form; the 409 catches
    // it if the index disagrees.
    const nearly = draft({ code: "2025-2026-1A" });
    const collision = codeCollision([...EXISTING, term("t3", "2025-2026-1a")], nearly.code);

    expect(collision?.kind).toBe("differs-only-by-case");
    expect(validateTerm(nearly, [...EXISTING, term("t3", "2025-2026-1a")]).ok).toBe(true);
  });

  it("reports an exact match as exact even when a case-only one is also present", () => {
    const collision = codeCollision([term("t3", "2025-2026-1a"), term("t1", "2025-2026-1A")], "2025-2026-1A");
    expect(collision).toEqual({ kind: "same", term: term("t1", "2025-2026-1A") });
  });

  it("finds no collision when there is nothing to compare against", () => {
    // The list can legitimately be empty — a failed re-read leaves the dialog with none — and that
    // leaves the server as the only check rather than producing a false refusal.
    expect(codeCollision([], "2025-2026-1")).toBeUndefined();
    expect(validateTerm(draft({ code: "2025-2026-1" }), []).ok).toBe(true);
  });

  it("does not make a term a duplicate of itself", () => {
    // The edit dialog excludes the term being edited before calling this. Pinned here because the
    // failure it prevents is a form that refuses to save a semester's spelling correction.
    const editingT1 = EXISTING.filter((other) => other.id !== "t1");
    expect(validateTerm(draft({ code: "2025-2026-1" }), editingT1).ok).toBe(true);
  });
});

// ---------------------------------------------------------------------------------------------
// The two 409s
// ---------------------------------------------------------------------------------------------

describe("telling the term surface's two conflicts apart", () => {
  const HTTP_CONFLICT = 409;

  const conflict = (code: string | undefined) =>
    new ApiError("http", HTTP_CONFLICT, "refused", {
      shape: "write",
      problem: code === undefined ? { status: HTTP_CONFLICT } : { status: HTTP_CONFLICT, code },
    });

  it("recognises the duplicate code", () => {
    expect(isTermCodeConflict(conflict("TermCodeExists"))).toBe(true);
    expect(isNoSchoolResolved(conflict("TermCodeExists"))).toBe(false);
  });

  it("does NOT read “no school could be resolved” as a duplicate code", () => {
    // The sharpest edge in the module. `POST /academic/terms` answers 409 for both, so a test on the
    // status alone would put a database-seeding problem on the code field and send the operator to
    // rename a code that was never the problem.
    const noSchool = conflict("NoSchoolResolved");
    expect(isNoSchoolResolved(noSchool)).toBe(true);
    expect(isTermCodeConflict(noSchool)).toBe(false);
  });

  it("claims neither when the code extension is missing", () => {
    // Stated rather than hidden: a 409 with no machine token falls through to the generic write
    // alert, which shows the server's own `detail`. An honest fallback beats a confident guess.
    const bare = conflict(undefined);
    expect(isTermCodeConflict(bare)).toBe(false);
    expect(isNoSchoolResolved(bare)).toBe(false);
  });

  it("claims nothing for something that did not come from the seam", () => {
    expect(isTermCodeConflict(new Error("TermCodeExists"))).toBe(false);
    expect(isTermCodeConflict(undefined)).toBe(false);
  });
});

// ---------------------------------------------------------------------------------------------
// The consequence of moving the flag
// ---------------------------------------------------------------------------------------------

describe("what the confirmation says before the click", () => {
  const CURRENT = term("t1", "2025-2026-1", true);
  const OTHER = term("t2", "2024-2025-2");
  const TERMS = [CURRENT, OTHER];

  it("names the term that will stop being current", () => {
    // The requirement in one assertion: only one term per school can hold the flag, so making OTHER
    // current retires CURRENT — and an operator who pressed a button about one term and demoted
    // another has no way of learning which one they gave up.
    const { consequence } = currentTermConsequence("make-current", OTHER, TERMS);
    expect(consequence).toContain(CURRENT.code);
  });

  it("says nothing else is affected when no term is current", () => {
    const { consequence } = currentTermConsequence("make-current", OTHER, [OTHER]);
    expect(consequence).not.toContain(CURRENT.code);
    expect(consequence).toContain("no current term");
  });

  it("does not name a term as its own predecessor", () => {
    // Reachable: the action is offered on every row, and a list read a moment ago can disagree with
    // the server about which term is current.
    const { consequence, title } = currentTermConsequence("make-current", CURRENT, TERMS);
    expect(title).toContain("already current");
    expect(consequence).toBe("No other term is affected.");
  });

  it("says retiring leaves the school with none, and that nothing is deleted", () => {
    // Both halves matter. There is no delete on this surface, so an operator reaching for one has to
    // find "retire" here and be told it is not a deletion.
    const { effect, consequence, settled } = currentTermConsequence("retire", CURRENT, TERMS);
    expect(effect).toContain("Nothing is deleted");
    expect(consequence).toContain("no current term");
    expect(settled).toContain("no longer the current term");
  });
});

// ---------------------------------------------------------------------------------------------
// The small structural guarantees the form leans on
// ---------------------------------------------------------------------------------------------

describe("what the form leans on", () => {
  it("orders the validated fields the way the form renders them", () => {
    // "Focus the first invalid field" only lands on the first one the *user* can see if this order
    // matches the screen's.
    expect([...VALIDATED_TERM_FIELDS]).toEqual([
      "code",
      "schoolYear",
      "semester",
      "startsOn",
      "endsOn",
    ]);
  });

  it("gives every field a unique id, per prefix", () => {
    // MUI derives `<label for>` and the `aria-describedby` tying an input to its error from these,
    // and "focus the field the server refused" looks the box up by one. Two dialogs are mounted from
    // the same page, so the prefixes must not collide either.
    const ids = Object.values(termFieldIdsFor("new-term"));
    expect(new Set(ids).size).toBe(ids.length);

    const other = Object.values(termFieldIdsFor("edit-term"));
    expect(ids.some((id) => other.includes(id))).toBe(false);
  });

  it("labels a term by code, year and semester", () => {
    expect(termLabel(term("t1", "2025-2026-1"))).toBe("2025-2026-1 (2025-2026 1st Semester)");
  });
});
