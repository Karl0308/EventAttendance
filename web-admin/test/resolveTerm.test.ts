// Which term the audience picker shows, given every term and whatever it asked for.
//
// `resolveTerm` is the only branching decision in `api.ts`, and each of its arms is reachable in
// ordinary use rather than only in theory: first open (nothing requested), a term the user chose, a
// term deleted or re-keyed under an open dialog, and a school whose roster import has never run.
//
// **What makes the "not found" arm worth pinning is what the caller does with it.** `sectionChoices`
// reads the sections for the requested term *concurrently* with the terms themselves — one round of
// latency instead of two — so by the time this function says the term does not exist, a page of rows
// has already arrived for it. Those rows belong to a term the picker cannot name, and the two arms
// `requested` / `default` are what tell the caller to throw them away and re-read rather than relabel
// them under the fallback's heading. A single `term` field would have lost that distinction, and the
// resulting bug — last semester's sections under this semester's label — is precisely the one the
// term scoping exists to prevent and the one that does not announce itself.

import { describe, expect, it } from "vitest";

import { resolveTerm } from "../src/api";
import type { Term } from "../src/types";

const term = (id: string, code: string, isCurrent = false): Term => ({
  id,
  code,
  schoolYear: "2025-2026",
  semester: "1",
  isCurrent,
});

/** As the contract orders them: current first, then newest first. */
const CURRENT = term("t2", "2025-2026-1", true);
const OLDER = term("t1", "2024-2025-2");
const TERMS = [CURRENT, OLDER];

describe("resolveTerm", () => {
  it("gives the current term when nothing was asked for", () => {
    expect(resolveTerm(TERMS, undefined)).toEqual({ kind: "default", term: CURRENT });
  });

  it("gives the term that was asked for, and says it was the one asked for", () => {
    // `requested` is what tells `sectionChoices` the rows it already fetched are usable.
    expect(resolveTerm(TERMS, "t1")).toEqual({ kind: "requested", term: OLDER });
  });

  it("does not report a fallback as the requested term", () => {
    // The distinction the caller acts on: `default` means "throw away what you fetched and re-read".
    // Collapsing these two arms is how last semester's sections end up under this semester's label.
    const resolved = resolveTerm(TERMS, "gone");
    expect(resolved).toEqual({ kind: "default", term: CURRENT });
    expect(resolved.kind).not.toBe("requested");
  });

  it("falls back to the first row when no term is flagged current", () => {
    // Between semesters, before an administrator has moved the flag. The list is already ordered
    // newest-first, which is what makes the first row the right guess — and why nothing here sorts on
    // `startsOn`, which is null on most real rows.
    const undated = [OLDER, term("t0", "2023-2024-1")];
    expect(resolveTerm(undated, undefined)).toEqual({ kind: "default", term: OLDER });
  });

  it("prefers the current term over the first row when they differ", () => {
    const currentIsSecond = [OLDER, CURRENT];
    expect(resolveTerm(currentIsSecond, undefined)).toEqual({ kind: "default", term: CURRENT });
  });

  it("still honours an explicit choice that is not the current term", () => {
    // The whole point of the control: an organizer staging an event for a term other than this one
    // must not have the picker quietly drag them back to the current cohort.
    expect(resolveTerm(TERMS, OLDER.id)).toEqual({ kind: "requested", term: OLDER });
  });

  it("answers `none` for a school with no terms, asked for or not", () => {
    // Distinct from "the term you asked for is gone": there is nothing to fall back to, so the picker
    // says the roster import has never run rather than showing an empty section list under a blank
    // term box.
    expect(resolveTerm([], undefined)).toEqual({ kind: "none" });
    expect(resolveTerm([], "t1")).toEqual({ kind: "none" });
  });

  it("matches term ids exactly — not trimmed, not case-folded", () => {
    // Ids are uuids off the wire. A near-miss must fall back visibly rather than resolve to something
    // that merely looks like it.
    for (const near of ["T1", " t1", "t1 "]) {
      expect(resolveTerm(TERMS, near).kind).toBe("default");
    }
  });
});
