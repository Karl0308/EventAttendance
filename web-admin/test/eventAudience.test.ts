// What `src/eventAudience.ts` allows, what it refuses, and — the part that carries the weight — the
// three sentences it must never let a reader confuse.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// `eventAudience.ts` is a **client-side mirror** of two server rules: which statuses accept an
// audience change (`EventStatusTransition.IsTerminal`) and what a 409 from that surface means
// (`EventLocked`). As `eventStatus.test.ts` records about its own subject, **these tests pin the
// client's copy and do not detect server drift** — nothing here compares the two.
//
// The value is the three confusions this module exists to prevent, each of which is a plausible
// rewrite that would pass a less specific test:
//
//   1. "already attached" reported as a failure, or as nothing. The `Already` counters exist so
//      idempotency is *observable*; a UI that renders `{attached: 0, already: 3}` as "nothing
//      happened" has the organizer pressing the button a third time.
//   2. A terminal event's locked audience described as a version mismatch, or vice versa — the same
//      split `eventStatus.ts` draws, and the same cost if it collapses.
//   3. A 409 treated as an ordinary 4xx. `advise()` tells an ordinary 4xx to "send it again if the
//      reason may have cleared"; this reason is one-way and never clears.

import { describe, expect, it } from "vitest";

import { ApiError } from "../src/api";
import {
  AUDIENCE_LOCKED_ELSEWHERE,
  FROZEN_STUDENTS_ELSEWHERE,
  NO_AUDIENCE_EVER,
  NO_AUDIENCE_YET,
  attachSettledText,
  audienceEditability,
  audienceListsState,
  isAudienceLocked,
  removeSectionLabel,
  removeStudentLabel,
} from "../src/eventAudience";
import { EVENT_STATUS } from "../src/types";
import type {
  EventAudience,
  EventAudienceGroup,
  EventAudienceResult,
  EventAudienceStudent,
} from "../src/types";

/** The load-bearing half of each refusal, quoted rather than the whole sentence — as elsewhere. */
const FINALITY_CLAIM = "can no longer be changed";
const VERSION_SKEW_CLAIM = "different versions";

const HTTP_CONFLICT = 409;

const problemAt = (status: number) =>
  new ApiError("http", status, `failed (${status})`, { shape: "write" });

/** A result with every counter at zero, so each test names only the counters it is about. */
const NOTHING_HAPPENED: EventAudienceResult = {
  eventId: "e",
  groupsAttached: 0,
  studentsAttached: 0,
  groupsAlreadyAttached: 0,
  studentsAlreadyAttached: 0,
  expected: 0,
  warnings: [],
};

const resultOf = (patch: Partial<EventAudienceResult>): EventAudienceResult => ({
  ...NOTHING_HAPPENED,
  ...patch,
});

describe("who may change an audience", () => {
  it.each([EVENT_STATUS.Draft, EVENT_STATUS.Open])("lets %s change it", (status) => {
    expect(audienceEditability(status).can).toBe(true);
  });

  it.each([EVENT_STATUS.Closed, EVENT_STATUS.Cancelled])("refuses %s", (status) => {
    expect(audienceEditability(status).can).toBe(false);
  });

  it("refuses Cancelled for the same reason as Closed, not as an afterthought", () => {
    // ADR-003 D-16: the freeze is a property of *terminality*, not of the status being `Closed`. A
    // build that tested `status === "Closed"` would offer a cancelled event controls whose only
    // possible outcome is a 409 — which is exactly the bug D-16 was written to record.
    const cancelled = audienceEditability(EVENT_STATUS.Cancelled);
    const closed = audienceEditability(EVENT_STATUS.Closed);

    expect(cancelled.can).toBe(false);
    expect(closed.can).toBe(false);
    if (cancelled.can || closed.can) throw new Error("both must be locked");
    expect(cancelled.reason).toContain(FINALITY_CLAIM);
    expect(closed.reason).toContain(FINALITY_CLAIM);
    // Same claim, different event: each names its own status rather than sharing one sentence.
    expect(cancelled.reason).toContain(EVENT_STATUS.Cancelled);
    expect(closed.reason).toContain(EVENT_STATUS.Closed);
  });

  it.each(["draft", "OPEN", "Archived", ""])(
    "refuses an unrecognised status (%j) as version skew, never as finality",
    (status) => {
      const decided = audienceEditability(status);
      expect(decided.can).toBe(false);
      if (decided.can) throw new Error("unreachable");
      // The split that matters: telling an organizer their event is over when the build merely does
      // not recognise its status is the one confusion that costs them the event.
      expect(decided.reason).toContain(VERSION_SKEW_CLAIM);
      expect(decided.reason).not.toContain(FINALITY_CLAIM);
    },
  );

  it("tells a locked event where the frozen student list actually is", () => {
    // The panel it appears on shows sections and no students. Without this the emptiness reads as
    // "nobody was attached", directly beneath a non-zero expected count (ADR-003 D-13).
    const decided = audienceEditability(EVENT_STATUS.Closed);
    if (decided.can) throw new Error("unreachable");
    expect(decided.reason).toContain("roster");
  });
});

describe("a 409 is not an ordinary 4xx", () => {
  it("recognises the conflict the audience surface raises", () => {
    expect(isAudienceLocked(problemAt(HTTP_CONFLICT))).toBe(true);
  });

  it.each([400, 404, 429, 500])("does not claim a %s is a locked audience", (status) => {
    expect(isAudienceLocked(problemAt(status))).toBe(false);
  });

  it("does not claim a network failure or a non-ApiError is one", () => {
    expect(isAudienceLocked(new ApiError("network", 0, "no answer", { shape: "write" }))).toBe(false);
    expect(isAudienceLocked(new Error("something else"))).toBe(false);
    expect(isAudienceLocked(undefined)).toBe(false);
  });

  it("says the change was not applied and that retrying cannot clear it", () => {
    // `advise()` would tell an ordinary 4xx to "send it again if the reason may have cleared". Both
    // terminal statuses are one-way, so this reason cannot — and saying otherwise would have the user
    // pressing a button that can only ever be refused again.
    expect(AUDIENCE_LOCKED_ELSEWHERE).toContain("Nothing was applied");
    expect(AUDIENCE_LOCKED_ELSEWHERE).toContain("will not clear");
  });
});

describe("what one attach did", () => {
  it("reports an ordinary attach", () => {
    const text = attachSettledText(resultOf({ groupsAttached: 2, expected: 84 }));
    expect(text).toContain("2 sections attached");
    expect(text).toContain("84 students are now expected");
  });

  it("says one section, not 1 sections", () => {
    expect(attachSettledText(resultOf({ groupsAttached: 1, expected: 1 }))).toBe(
      "1 section attached. 1 student is now expected.",
    );
  });

  it("reads a fully-idempotent re-post as already there, never as a failure", () => {
    // The whole reason the `Already` counters exist. `{attached: 0, already: 3}` is the server
    // confirming the earlier request landed; reporting it as "nothing happened" leaves the organizer
    // pressing the button a third time, and reporting it as an error has them undoing a save that
    // worked.
    const text = attachSettledText(resultOf({ groupsAlreadyAttached: 3, expected: 84 }));
    expect(text).toContain("already attached");
    expect(text).toContain("nothing changed");
    expect(text).toContain("landed");
    expect(text).not.toContain("Nothing was attached");
  });

  it("keeps the two halves apart when a post attaches some and finds others already there", () => {
    const text = attachSettledText(
      resultOf({ groupsAttached: 2, groupsAlreadyAttached: 1, expected: 84 }),
    );
    expect(text).toContain("2 sections attached");
    expect(text).toContain("1 was already attached");
  });

  it("counts individually-attached students separately from sections", () => {
    const text = attachSettledText(
      resultOf({ groupsAttached: 1, studentsAttached: 2, expected: 42 }),
    );
    expect(text).toContain("1 section attached");
    expect(text).toContain("2 students attached");
  });

  it("says so plainly when an empty request changed nothing", () => {
    // Sending neither list is a no-op rather than an error — it is what "the organizer cleared the
    // form and saved" looks like — so this arm is reachable and must not read as a success.
    expect(attachSettledText(NOTHING_HAPPENED)).toContain("Nothing was attached");
  });

  it("always states the resulting expected count", () => {
    // It comes back on the write precisely so the denominator can move on screen without a follow-up
    // read; dropping it would waste the round trip the contract paid for.
    for (const result of [
      NOTHING_HAPPENED,
      resultOf({ groupsAttached: 1, expected: 7 }),
      resultOf({ groupsAlreadyAttached: 1, expected: 7 }),
    ]) {
      expect(attachSettledText(result)).toContain(`now expected`);
    }
  });
});

describe("the empty-audience copy", () => {
  it("tells an editable event what to do rather than that there is nothing", () => {
    expect(NO_AUDIENCE_YET).toContain("Add the sections");
  });

  it("does not tell a frozen event to add sections it cannot add", () => {
    expect(NO_AUDIENCE_EVER).not.toContain("Add the sections");
  });

  it("never lets an empty frozen student list read as “nobody was attached”", () => {
    // ADR-003 D-13, and the single most misreadable thing on the panel: the per-student frozen set is
    // on `GET /events/{id}/roster`, and this endpoint returning none of it is the expected answer.
    expect(FROZEN_STUDENTS_ELSEWHERE).toContain("not “nobody was attached”");
    expect(FROZEN_STUDENTS_ELSEWHERE).toContain("roster");
  });
});

describe("what the panel's body should be", () => {
  const SECTION: EventAudienceGroup = {
    studentGroupId: "g1",
    name: "BSFS 2-A (2025-2026-1)",
    type: "Section",
    sourceType: "Derived",
    termId: "t1",
    termCode: "2025-2026-1",
    memberCount: 40,
  };

  const STUDENT: EventAudienceStudent = {
    studentId: "s1",
    studentNumber: "2021-00042",
    fullName: "Maria Cruz Santos",
    section: "BSFS 2-A",
  };

  const audienceOf = (patch: Partial<EventAudience>): EventAudience => ({
    eventId: "e",
    status: EVENT_STATUS.Open,
    isFrozen: false,
    expected: 0,
    groups: [],
    students: [],
    ...patch,
  });

  /** A terminal event, where `students` is empty **by contract** whatever was attached. */
  const frozen = (patch: Partial<EventAudience>): EventAudience =>
    audienceOf({ status: EVENT_STATUS.Closed, isFrozen: true, students: [], ...patch });

  it("never tells a frozen individuals-only event that it expected nobody", () => {
    // THE REGRESSION. Reachable through the shipped UI: attach a section and a student, remove the
    // section, close. The read comes back `groups: []`, `students: []` (empty by contract), and
    // `expected: 1`. A list-length test calls that "nothing invited" and prints "expected nobody"
    // directly beneath the panel's own "1 expected" chip — and the false statement is the
    // reassuring one.
    const state = audienceListsState(frozen({ expected: 1 }), false);

    expect(state.kind).toBe("lists");
    if (state.kind !== "lists") throw new Error("unreachable");
    // And the empty student list must read as "ask the roster", not as an absence.
    expect(state.students).toBe("elsewhere");
  });

  it("calls a frozen audience empty only when the denominator is zero AND no cohort is recorded", () => {
    // The two independent ways an invitation leaves a trace on a terminal event: the number of people
    // it expected, and the record of which cohort it invited. Only when both are absent did nothing
    // happen.
    const state = audienceListsState(frozen({ expected: 0 }), false);
    expect(state).toEqual({ kind: "nothing-invited", message: NO_AUDIENCE_EVER });
  });

  it("does not call a frozen event empty when a cohort is on record, even at zero expected", () => {
    // A section whose projection produced nobody was still invited, and that record is exactly what
    // ADR-003 D-13 keeps the group rows for.
    const state = audienceListsState(frozen({ expected: 0, groups: [SECTION] }), false);
    expect(state.kind).toBe("lists");
  });

  it("renders the sections of a frozen event that has them", () => {
    const state = audienceListsState(frozen({ expected: 40, groups: [SECTION] }), false);
    expect(state).toEqual({ kind: "lists", students: "elsewhere" });
  });

  it("shows a frozen event's students if the server ever sends any", () => {
    // Empty is the contract, so this arm should not occur — but showing what arrived beats hiding it
    // behind a sentence asserting it cannot exist.
    const state = audienceListsState(
      frozen({ expected: 1, students: [STUDENT] }),
      false,
    );
    expect(state).toEqual({ kind: "lists", students: "list" });
  });

  it("keeps the list-length test on a live event, where students really is the attached set", () => {
    // The live/frozen asymmetry is deliberate and is the same one ADR-003 D-15 records: the two
    // branches answer different questions from different sources.
    const state = audienceListsState(audienceOf({}), true);
    expect(state).toEqual({ kind: "nothing-invited", message: NO_AUDIENCE_YET });
  });

  it("tells a live event that can still be edited what to do about it", () => {
    const editable = audienceListsState(audienceOf({}), true);
    const not = audienceListsState(audienceOf({}), false);

    if (editable.kind !== "nothing-invited" || not.kind !== "nothing-invited") {
      throw new Error("both must be empty");
    }
    expect(editable.message).toContain("Add the sections");
    expect(not.message).not.toContain("Add the sections");
  });

  it("renders a live event's lists once anything is attached", () => {
    expect(audienceListsState(audienceOf({ expected: 40, groups: [SECTION] }), true)).toEqual({
      kind: "lists",
      students: "none-attached",
    });
    expect(audienceListsState(audienceOf({ expected: 1, students: [STUDENT] }), true)).toEqual({
      kind: "lists",
      students: "list",
    });
  });

  it("never says “ask the roster” about a live event", () => {
    // `elsewhere` claims the students were written down and are enumerable at `GET /roster`, which is
    // only true once the audience is frozen. Saying it about a live event would send an organizer to
    // a page that does not hold what they were promised.
    for (const audience of [
      audienceOf({}),
      audienceOf({ expected: 40, groups: [SECTION] }),
      audienceOf({ expected: 1, students: [STUDENT] }),
    ]) {
      const state = audienceListsState(audience, true);
      if (state.kind === "lists") expect(state.students).not.toBe("elsewhere");
    }
  });
});

describe("the remove controls name what they remove", () => {
  it("names the section", () => {
    // A screen reader reads a button's label out of context; a panel of six would otherwise announce
    // "Remove, Remove, Remove".
    expect(removeSectionLabel("BSFS 2-A (2025-2026-1)")).toContain("BSFS 2-A (2025-2026-1)");
  });

  it("names the student and their number", () => {
    const label = removeStudentLabel("Maria Cruz Santos", "2021-00042");
    expect(label).toContain("Maria Cruz Santos");
    expect(label).toContain("2021-00042");
  });
});
