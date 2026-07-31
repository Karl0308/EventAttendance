// What `src/eventStatus.ts` offers, and what it says when it offers nothing.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and what it does not
// ---------------------------------------------------------------------------------------------
//
// `eventStatus.ts` is a **hand-written mirror** of `EventStatusTransition.AllowedTargets` in
// `backend/EAMS.Domain/EventStatusTransition.cs`. The two were read side by side when these tests
// were written and they agree today.
//
// **These tests pin the client's copy. They do not detect server drift.** Nothing in this repository
// compares the two tables; if the backend adds an edge tomorrow, every test here still passes and the
// UI simply stops offering a move the server would accept. The binding artefact that would actually
// close that gap is the §4.5 limits and the transition graph being published into
// `docs/api/openapi.json` — scheduled backend work (MDVault #206), not something a frontend unit test
// can substitute for.
//
// So the value here is narrower and real: it stops *this* module regressing, and it pins the two
// decisions that are this module's own rather than the server's — the terminal-versus-version-skew
// notice split, and the ordinality of `knownStatus`.

import { describe, expect, it } from "vitest";

import { isTerminal, knownStatus, statusActionsFor, statusSettledText } from "../src/eventStatus";
import { EVENT_STATUS } from "../src/types";

/** The four statuses this build knows, as a list to sweep. */
const ALL_STATUSES = Object.values(EVENT_STATUS);

/**
 * The load-bearing half of each notice, quoted rather than the whole sentence.
 *
 * Asserting the full prose would make every wording tweak a test failure, which teaches people to
 * update the expectation without reading it. These two phrases are the part that carries the
 * *claim* — "this is over" versus "this build is out of date" — and the claim is the thing that must
 * not be confused.
 */
const FINALITY_CLAIM = "that is final";
const VERSION_SKEW_CLAIM = "different versions";

/** Statuses the wire could carry that this build has no rule for. */
const UNRECOGNISED_STATUSES = ["draft", "OPEN", "Archived", ""];

const targetsFrom = (status: string) => statusActionsFor(status).changes.map((change) => change.target);

describe("the transition table", () => {
  it("offers Open then Cancelled from Draft", () => {
    expect(targetsFrom(EVENT_STATUS.Draft)).toEqual([EVENT_STATUS.Open, EVENT_STATUS.Cancelled]);
  });

  it("offers Closed then Cancelled from Open", () => {
    expect(targetsFrom(EVENT_STATUS.Open)).toEqual([EVENT_STATUS.Closed, EVENT_STATUS.Cancelled]);
  });

  it("offers nothing from Closed", () => {
    expect(targetsFrom(EVENT_STATUS.Closed)).toEqual([]);
  });

  it("offers nothing from Cancelled", () => {
    expect(targetsFrom(EVENT_STATUS.Cancelled)).toEqual([]);
  });

  it("never offers a move back to the status the event already holds", () => {
    for (const from of ALL_STATUSES) {
      expect(targetsFrom(from)).not.toContain(from);
    }
  });

  it("gives every offered move a verb, a progress label, a consequence and a finality sentence", () => {
    // A new edge added without one of these would render a confirmation with a blank in it. The
    // consequence and finality are the whole reason this table is prose rather than an array of
    // status names, so an empty one is a silently worse dialog rather than a crash.
    for (const from of ALL_STATUSES) {
      for (const change of statusActionsFor(from).changes) {
        expect(change.verb.length).toBeGreaterThan(0);
        expect(change.progress.length).toBeGreaterThan(0);
        expect(change.consequence.length).toBeGreaterThan(0);
        expect(change.finality.length).toBeGreaterThan(0);
      }
    }
  });

  it("tells Draft and Open apart when cancelling — the prose is per transition, not per target", () => {
    // Both reach Cancelled and they do different things: the second can already hold taps. A dialog
    // keyed on the target alone would tell one of them the other's story.
    const fromDraft = statusActionsFor(EVENT_STATUS.Draft).changes.find(
      (change) => change.target === EVENT_STATUS.Cancelled,
    );
    const fromOpen = statusActionsFor(EVENT_STATUS.Open).changes.find(
      (change) => change.target === EVENT_STATUS.Cancelled,
    );

    expect(fromDraft).toBeDefined();
    expect(fromOpen).toBeDefined();
    expect(fromDraft?.consequence).not.toBe(fromOpen?.consequence);
    expect(fromDraft?.finality).not.toBe(fromOpen?.finality);
  });
});

describe("why there is nothing to offer — the notice split", () => {
  it("marks noneBecause present exactly when there is nothing to offer", () => {
    for (const status of ALL_STATUSES) {
      const { changes, noneBecause } = statusActionsFor(status);
      expect(noneBecause === undefined).toBe(changes.length > 0);
    }
  });

  it.each([EVENT_STATUS.Closed, EVENT_STATUS.Cancelled])(
    "tells %s it is over, and does not blame a version mismatch",
    (status) => {
      const notice = statusActionsFor(status).noneBecause;
      expect(notice).toContain(FINALITY_CLAIM);
      expect(notice).not.toContain(VERSION_SKEW_CLAIM);
    },
  );

  it.each(UNRECOGNISED_STATUSES)(
    "tells an unrecognised status (%j) it is a version mismatch, and never that the event is over",
    (status) => {
      const { changes, noneBecause } = statusActionsFor(status);

      expect(changes).toEqual([]);
      expect(noneBecause).toBeDefined();
      // The whole point of the split: conflating these tells a user their event is finished when
      // the build merely does not recognise its status.
      expect(noneBecause).toContain(VERSION_SKEW_CLAIM);
      expect(noneBecause).not.toContain(FINALITY_CLAIM);
    },
  );

  it("never gives an unrecognised status the same sentence as a terminal one", () => {
    const terminalNotices = [EVENT_STATUS.Closed, EVENT_STATUS.Cancelled].map(
      (status) => statusActionsFor(status).noneBecause,
    );

    for (const status of UNRECOGNISED_STATUSES) {
      expect(terminalNotices).not.toContain(statusActionsFor(status).noneBecause);
    }
  });

  it("quotes the unrecognised status back, so the reader can see what arrived", () => {
    expect(statusActionsFor("Archived").noneBecause).toContain("Archived");
  });
});

describe("knownStatus is an ordinal comparison", () => {
  it.each(ALL_STATUSES)("accepts the exact value %s", (status) => {
    expect(knownStatus(status)).toBe(status);
  });

  it.each(["Draft ", " Draft", "draft", "DRAFT", "Archived", ""])(
    "rejects %j — not trimmed, not case-folded",
    (wire) => {
      expect(knownStatus(wire)).toBeUndefined();
    },
  );
});

describe("isTerminal", () => {
  it.each([
    [EVENT_STATUS.Draft, false],
    [EVENT_STATUS.Open, false],
    [EVENT_STATUS.Closed, true],
    [EVENT_STATUS.Cancelled, true],
  ])("answers %s with %s", (status, expected) => {
    expect(isTerminal(status)).toBe(expected);
  });

  it("agrees with there being nothing to offer", () => {
    for (const status of ALL_STATUSES) {
      expect(isTerminal(status)).toBe(statusActionsFor(status).changes.length === 0);
    }
  });

  it("makes every offered move except Draft → Open a one-way one", () => {
    // `ChangeEventStatusDialog` colours its confirm button `error` when `isTerminal(change.target)`,
    // so this list is exactly the set of confirmations that are NOT red. Pinning it here pins the
    // fact the colour is derived from; it does not render the dialog and does not check the colour.
    const leadsSomewhereReversible: string[] = [];
    for (const from of ALL_STATUSES) {
      for (const change of statusActionsFor(from).changes) {
        if (!isTerminal(change.target)) leadsSomewhereReversible.push(`${from} → ${change.target}`);
      }
    }

    expect(leadsSomewhereReversible).toEqual(["Draft → Open"]);
  });
});

describe("statusSettledText", () => {
  const EVENT_NAME = "Freshman Orientation";

  /** What every arm opens with, and all the unknown arm produces. */
  const bareFact = (status: string) => `“${EVENT_NAME}” is now ${status}.`;

  it("tells an opened event that taps now count", () => {
    const text = statusSettledText(EVENT_NAME, EVENT_STATUS.Open);
    expect(text).toContain(bareFact(EVENT_STATUS.Open));
    expect(text).toContain("Card taps are recorded");
  });

  it("tells a closed event its absentees are written and its figures fixed", () => {
    const text = statusSettledText(EVENT_NAME, EVENT_STATUS.Closed);
    expect(text).toContain(bareFact(EVENT_STATUS.Closed));
    expect(text).toContain("marked Absent");
  });

  it("describes the closed event's state rather than what this request did", () => {
    // Load-bearing on a retry: the no-op arm does no work, and this sentence is still true because
    // it describes the event. An action-describing rewrite would narrate a freeze that did not run.
    expect(statusSettledText(EVENT_NAME, EVENT_STATUS.Closed)).toContain("has been marked Absent");
  });

  it("tells a cancelled event its audience has stopped moving", () => {
    const text = statusSettledText(EVENT_NAME, EVENT_STATUS.Cancelled);
    expect(text).toContain(bareFact(EVENT_STATUS.Cancelled));
    expect(text).toContain("audience");
  });

  it("gives Draft and an unrecognised status the same fall-through: the fact, and no claim", () => {
    // `Draft` is unreachable through this function today — nothing transitions *to* Draft — so it
    // shares the unknown arm deliberately rather than by omission. Both must state what happened
    // and claim nothing about what it did.
    expect(statusSettledText(EVENT_NAME, EVENT_STATUS.Draft)).toBe(bareFact(EVENT_STATUS.Draft));
    expect(statusSettledText(EVENT_NAME, "Archived")).toBe(bareFact("Archived"));
  });

  it("echoes the status verbatim, so a near-miss is not read as the status it resembles", () => {
    // "OPEN" is not `Open`: `knownStatus` is ordinal, so this must fall through rather than claim
    // taps are being recorded.
    const text = statusSettledText(EVENT_NAME, "OPEN");
    expect(text).toBe(bareFact("OPEN"));
    expect(text).not.toContain("Card taps are recorded");
  });
});
