// What `src/eventDraft.ts` accepts, what it refuses, and exactly what it puts on the wire.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and what it does not
// ---------------------------------------------------------------------------------------------
//
// The five limits below (`NAME_MAX_LENGTH` and friends) are a **hand-written copy** of `EventText` in
// `backend/EAMS.Domain/DomainValues.cs`, and the `endAt <= startAt` rule is a copy of
// `EventService.Validate`. `eventDraft.ts` says so itself, in the comment above the constants.
//
// **These tests pin the client's copy. They cannot detect server drift.** They import the constants
// rather than restating the numbers, so what they actually assert is "the limit is applied at its own
// boundary, and the request is built from the values that were checked" — not "the limit is 200".
// If §4.5 changes to 150 tomorrow, every test here still passes and the form starts accepting names
// the server refuses. Nothing in this repository compares the two, and a frontend unit test is
// structurally incapable of it: the binding artefact is the limits being published into
// `docs/api/openapi.json`, which is scheduled backend work (MDVault #206).
//
// The zone-dependent tests are a different matter and are genuinely load-bearing — see
// `timeZone.ts` for why the zone is asserted rather than assumed.

import { describe, expect, it } from "vitest";

import {
  DESCRIPTION_MAX_LENGTH,
  EMPTY_DRAFT,
  LOCATION_MAX_LENGTH,
  MAX_GRACE_MINUTES,
  MIN_GRACE_MINUTES,
  NAME_MAX_LENGTH,
  VALIDATED_FIELDS,
  draftFrom,
  knownMode,
  localFrom,
  lockedFor,
  validate,
} from "../src/eventDraft";
import type { Draft, DraftField, DraftOrigin, FieldErrors, Validated } from "../src/eventDraft";
import { ATTENDANCE_MODES } from "../src/types";
import type { EventItem, EventWriteRequest } from "../src/types";
import { FOLD_TIME_ZONE, inTimeZone } from "./timeZone";

// ---------------------------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------------------------

/**
 * A draft that passes, so each test can move exactly one thing and know that is what it measured.
 *
 * The times are wall-clock readings in the suite's baseline zone (Asia/Manila, UTC+8), so
 * 09:00 local is 01:00Z. Those two facts are what the create-path assertions below are built on.
 */
const baseDraft = (overrides: Partial<Draft> = {}): Draft => ({
  ...EMPTY_DRAFT,
  name: "Freshman Orientation",
  startAt: "2026-08-01T09:00",
  endAt: "2026-08-01T11:00",
  ...overrides,
});

/** An event as the server hands it back, for `draftFrom` and for origin round trips. */
const serverEvent = (overrides: Partial<EventItem> = {}): EventItem => ({
  id: "6f2b1c00-0000-4000-8000-000000000001",
  name: "Freshman Orientation",
  description: undefined,
  location: undefined,
  startAt: "2026-08-01T01:00:00Z",
  endAt: "2026-08-01T03:00:00Z",
  attendanceMode: "Single",
  graceMinutes: 15,
  requireRegistration: false,
  status: "Draft",
  ...overrides,
});

const characters = (count: number) => "x".repeat(count);

/** Fails loudly rather than returning a half-answer, so a mis-set-up test cannot read as a pass. */
function errorsOf(result: Validated): FieldErrors {
  if (result.ok) throw new Error("Expected validation to fail; it succeeded.");
  return result.errors;
}

function requestOf(result: Validated): EventWriteRequest {
  if (!result.ok) throw new Error(`Expected validation to pass; it failed: ${JSON.stringify(result.errors)}`);
  return result.request;
}

// ---------------------------------------------------------------------------------------------
// The §4.5 limits
// ---------------------------------------------------------------------------------------------

describe("the text limits", () => {
  it("accepts a name of exactly the maximum", () => {
    expect(validate(baseDraft({ name: characters(NAME_MAX_LENGTH) })).ok).toBe(true);
  });

  it("refuses a name one character over, and says how long it actually is", () => {
    const tooLong = NAME_MAX_LENGTH + 1;
    const errors = errorsOf(validate(baseDraft({ name: characters(tooLong) })));
    expect(errors.name).toBeDefined();
    expect(errors.name).toContain(String(tooLong));
  });

  it("accepts a description of exactly the maximum", () => {
    expect(validate(baseDraft({ description: characters(DESCRIPTION_MAX_LENGTH) })).ok).toBe(true);
  });

  it("refuses a description one character over", () => {
    const errors = errorsOf(validate(baseDraft({ description: characters(DESCRIPTION_MAX_LENGTH + 1) })));
    expect(errors.description).toBeDefined();
  });

  it("accepts a location of exactly the maximum", () => {
    expect(validate(baseDraft({ location: characters(LOCATION_MAX_LENGTH) })).ok).toBe(true);
  });

  it("refuses a location one character over", () => {
    const errors = errorsOf(validate(baseDraft({ location: characters(LOCATION_MAX_LENGTH + 1) })));
    expect(errors.location).toBeDefined();
  });

  it("measures after trimming, as the server does", () => {
    // An untrimmed measurement would refuse a name the server would have accepted.
    const padded = ` ${characters(NAME_MAX_LENGTH)} `;
    expect(requestOf(validate(baseDraft({ name: padded }))).name).toBe(characters(NAME_MAX_LENGTH));
  });

  it("treats a whitespace-only name as missing rather than as a name of length three", () => {
    expect(errorsOf(validate(baseDraft({ name: "   " }))).name).toBeDefined();
  });

  it("requires a name", () => {
    expect(errorsOf(validate(baseDraft({ name: "" }))).name).toBeDefined();
  });
});

describe("the grace period", () => {
  it.each([String(MIN_GRACE_MINUTES), "1", "15", String(MAX_GRACE_MINUTES), " 12 "])(
    "accepts %j",
    (graceMinutes) => {
      expect(validate(baseDraft({ graceMinutes })).ok).toBe(true);
    },
  );

  it.each([
    String(MIN_GRACE_MINUTES - 1),
    String(MAX_GRACE_MINUTES + 1),
    "1.5",
    "abc",
    // `Number("")` and `Number("  ")` are both 0, which is inside the range — so an unguarded
    // parse would silently send 0 for an empty box. The guard is what these two pin.
    "",
    "   ",
  ])("refuses %j", (graceMinutes) => {
    expect(errorsOf(validate(baseDraft({ graceMinutes }))).graceMinutes).toBeDefined();
  });

  it("carries the parsed number, not the string, into the request", () => {
    expect(requestOf(validate(baseDraft({ graceMinutes: " 12 " }))).graceMinutes).toBe(12);
  });
});

describe("the attendance mode", () => {
  it.each(ATTENDANCE_MODES)("recognises %s", (mode) => {
    expect(knownMode(mode)).toBe(mode);
  });

  it.each(["single", "TIMEINOUT", "Timeinout", "TimeInOut ", "", "Hybrid"])(
    "refuses to narrow %j",
    (wire) => {
      expect(knownMode(wire)).toBeUndefined();
    },
  );

  it.each(ATTENDANCE_MODES)("carries %s into the request unchanged", (attendanceMode) => {
    expect(requestOf(validate(baseDraft({ attendanceMode }))).attendanceMode).toBe(attendanceMode);
  });
});

describe("the window rule", () => {
  it("refuses an end equal to the start", () => {
    // `<=`, matching the server: an event with an empty window can never be attended.
    const errors = errorsOf(
      validate(baseDraft({ startAt: "2026-08-01T09:00", endAt: "2026-08-01T09:00" })),
    );
    expect(errors.endAt).toBeDefined();
  });

  it("refuses an end before the start", () => {
    const errors = errorsOf(
      validate(baseDraft({ startAt: "2026-08-01T09:00", endAt: "2026-08-01T08:00" })),
    );
    expect(errors.endAt).toBeDefined();
  });

  it("accepts an end after the start", () => {
    expect(validate(baseDraft({ startAt: "2026-08-01T09:00", endAt: "2026-08-01T09:01" })).ok).toBe(true);
  });

  it("requires both ends", () => {
    const errors = errorsOf(validate(baseDraft({ startAt: "", endAt: "" })));
    expect(errors.startAt).toBeDefined();
    expect(errors.endAt).toBeDefined();
  });

  it("reports a missing end as missing rather than as out of order", () => {
    const errors = errorsOf(validate(baseDraft({ endAt: "" })));
    expect(errors.endAt).toContain("required");
  });
});

// ---------------------------------------------------------------------------------------------
// The contract of `validate`
// ---------------------------------------------------------------------------------------------

describe("validate's answer", () => {
  it("answers ok:true with a request", () => {
    const result = validate(baseDraft());
    expect(result.ok).toBe(true);
    expect(requestOf(result)).toBeDefined();
  });

  it("answers ok:false with at least one error", () => {
    const result = validate(baseDraft({ name: "" }));
    expect(result.ok).toBe(false);
    expect(Object.keys(errorsOf(result)).length).toBeGreaterThan(0);
  });

  it("only ever reports errors on fields VALIDATED_FIELDS knows about", () => {
    // `VALIDATED_FIELDS` drives "focus the first invalid box". An error on a field missing from it
    // would be an error nothing can focus — the form would refuse to submit and look inert.
    const errors = errorsOf(
      validate({
        name: "",
        description: characters(DESCRIPTION_MAX_LENGTH + 1),
        location: characters(LOCATION_MAX_LENGTH + 1),
        startAt: "",
        endAt: "",
        attendanceMode: "Single",
        graceMinutes: String(MAX_GRACE_MINUTES + 1),
        requireRegistration: false,
      }),
    );

    const reported = Object.keys(errors);
    expect(reported.length).toBeGreaterThan(0);
    for (const field of reported) {
      expect(VALIDATED_FIELDS).toContain(field);
    }
  });

  it("sends null rather than an empty string for an omitted description and location", () => {
    // `""` is a location, and the column stores it as one — after which "has a location" is true of
    // an event that has none.
    const request = requestOf(validate(baseDraft({ description: "", location: "" })));
    expect(request.description).toBeNull();
    expect(request.location).toBeNull();
  });

  it("sends trimmed text when there is text", () => {
    const request = requestOf(validate(baseDraft({ description: "  after class  ", location: "  Gym  " })));
    expect(request.description).toBe("after class");
    expect(request.location).toBe("Gym");
  });

  it("carries requireRegistration through untouched", () => {
    expect(requestOf(validate(baseDraft({ requireRegistration: true }))).requireRegistration).toBe(true);
  });
});

// ---------------------------------------------------------------------------------------------
// The edit scopes
// ---------------------------------------------------------------------------------------------

describe("lockedFor", () => {
  const ATTENDANCE_RULE_FIELDS: DraftField[] = [
    "attendanceMode",
    "endAt",
    "graceMinutes",
    "requireRegistration",
    "startAt",
  ];

  it("locks nothing when everything may be edited", () => {
    expect([...lockedFor("everything")]).toEqual([]);
  });

  it("locks exactly the five fields that decide what an attendance row means", () => {
    expect([...lockedFor("descriptive")].sort()).toEqual(ATTENDANCE_RULE_FIELDS);
  });

  it("never locks the descriptive fields, which a cancelled event may still change", () => {
    const locked = lockedFor("descriptive");
    for (const field of ["name", "description", "location"] as const) {
      expect(locked.has(field)).toBe(false);
    }
  });
});

// ---------------------------------------------------------------------------------------------
// Filling the boxes
// ---------------------------------------------------------------------------------------------

describe("localFrom", () => {
  it("renders an instant in the running zone rather than in UTC", () => {
    // The suite runs in Asia/Manila (UTC+8), so 01:00Z is 09:00 local. Reading it as 01:00 would be
    // the eight-hour error this conversion exists to avoid, arriving through the inverse.
    expect(localFrom("2026-08-01T01:00:00Z")).toBe("2026-08-01T09:00");
  });

  it("zero-pads every part", () => {
    expect(localFrom("2026-01-02T01:05:00+08:00")).toBe("2026-01-02T01:05");
  });

  it.each(["", "not a date", "2026-13-45T99:99"])("answers empty for %j", (iso) => {
    expect(localFrom(iso)).toBe("");
  });
});

describe("draftFrom", () => {
  it("fills the time boxes in the running zone", () => {
    const draft = draftFrom(serverEvent());
    expect(draft.startAt).toBe("2026-08-01T09:00");
    expect(draft.endAt).toBe("2026-08-01T11:00");
  });

  it("turns an absent description and location into empty boxes", () => {
    const draft = draftFrom(serverEvent());
    expect(draft.description).toBe("");
    expect(draft.location).toBe("");
  });

  it("holds the grace period as text, so the box can be emptied while retyping", () => {
    expect(draftFrom(serverEvent({ graceMinutes: 15 })).graceMinutes).toBe("15");
  });

  it("falls back to Single for a mode this form cannot send back", () => {
    // Last line of defence, not the first: `EventDetail` refuses to open the form at all in this
    // case, because falling back silently rewrites an event's mode on a save meant for its name.
    expect(draftFrom(serverEvent({ attendanceMode: "Hybrid" })).attendanceMode).toBe("Single");
  });

  it("round-trips an event through the form without moving anything", () => {
    const event = serverEvent({ description: "After class", location: "Gym", requireRegistration: true });
    const origin: DraftOrigin = { startAt: event.startAt, endAt: event.endAt };
    const request = requestOf(validate(draftFrom(event), origin));

    expect(request.name).toBe(event.name);
    expect(request.description).toBe(event.description);
    expect(request.location).toBe(event.location);
    expect(request.startAt).toBe(event.startAt);
    expect(request.endAt).toBe(event.endAt);
    expect(request.graceMinutes).toBe(event.graceMinutes);
    expect(request.requireRegistration).toBe(event.requireRegistration);
  });
});

// ---------------------------------------------------------------------------------------------
// resolve / unmoved — the verbatim round trip
// ---------------------------------------------------------------------------------------------
//
// The highest-value case in the module. `datetime-local` has minute resolution and an instant does
// not, so a box filled from `09:00:30Z` and read back names `09:00:00Z` — thirty seconds earlier,
// from a form the user never touched.

describe("the verbatim round trip", () => {
  /** SQL Server `datetime2(7)` keeps more precision than a JavaScript `Date` does. */
  const SUB_SECOND_START = "2026-08-01T09:00:30.1234567Z";
  const ORIGIN_END = "2026-08-01T11:00:00Z";

  const originOf = (startAt: string, endAt: string): DraftOrigin => ({ startAt, endAt });

  it("sends an untouched box back verbatim, sub-second digits intact", () => {
    // Asserted rather than assumed: if `localFrom` ever drifts, the box would no longer match the
    // origin, `unmoved` would answer undefined, and this test would silently stop testing verbatim.
    expect(localFrom(SUB_SECOND_START)).toBe("2026-08-01T17:00");
    expect(localFrom(ORIGIN_END)).toBe("2026-08-01T19:00");

    const request = requestOf(
      validate(
        baseDraft({ name: "Renamed", startAt: "2026-08-01T17:00", endAt: "2026-08-01T19:00" }),
        originOf(SUB_SECOND_START, ORIGIN_END),
      ),
    );

    expect(request.startAt).toBe(SUB_SECOND_START);
    expect(request.endAt).toBe(ORIGIN_END);
  });

  it("never suppresses a genuine edit — a moved box sends the new instant", () => {
    const request = requestOf(
      validate(
        baseDraft({ startAt: "2026-08-01T18:00", endAt: "2026-08-01T19:00" }),
        originOf(SUB_SECOND_START, ORIGIN_END),
      ),
    );

    expect(request.startAt).not.toBe(SUB_SECOND_START);
    expect(request.startAt).toBe("2026-08-01T10:00:00.000Z");
  });

  it("resolves each end independently", () => {
    const request = requestOf(
      validate(
        baseDraft({ startAt: "2026-08-01T17:00", endAt: "2026-08-01T20:00" }),
        originOf(SUB_SECOND_START, ORIGIN_END),
      ),
    );

    expect(request.startAt).toBe(SUB_SECOND_START);
    expect(request.endAt).toBe("2026-08-01T12:00:00.000Z");
  });

  it("accepts a sub-minute event on a name-only edit", () => {
    // 09:00:30Z → 09:00:50Z is twenty seconds long, so both boxes read the same minute. Comparing
    // the boxes refuses this as "the end must be after the start", on a save made to its name.
    const origin = originOf("2026-08-01T09:00:30Z", "2026-08-01T09:00:50Z");
    expect(localFrom(origin.startAt)).toBe(localFrom(origin.endAt));

    const request = requestOf(
      validate(
        baseDraft({ name: "Renamed", startAt: localFrom(origin.startAt), endAt: localFrom(origin.endAt) }),
        origin,
      ),
    );

    expect(request.startAt).toBe(origin.startAt);
    expect(request.endAt).toBe(origin.endAt);
  });

  it("ignores an origin whose reading no longer matches the box", () => {
    const request = requestOf(
      validate(
        baseDraft({ startAt: "2026-08-02T17:00", endAt: "2026-08-02T19:00" }),
        originOf(SUB_SECOND_START, ORIGIN_END),
      ),
    );

    expect(request.startAt).toBe("2026-08-02T09:00:00.000Z");
    expect(request.endAt).toBe("2026-08-02T11:00:00.000Z");
  });

  it("behaves as a create when there is no origin: this browser's reading, ISO-normalised", () => {
    const request = requestOf(validate(baseDraft()));
    expect(request.startAt).toBe("2026-08-01T01:00:00.000Z");
    expect(request.endAt).toBe("2026-08-01T03:00:00.000Z");
  });

  it("still refuses an inverted window on the create path", () => {
    const errors = errorsOf(validate(baseDraft({ startAt: "2026-08-01T11:00", endAt: "2026-08-01T09:00" })));
    expect(errors.endAt).toBeDefined();
  });

  it("refuses a sub-minute window that was typed rather than preserved", () => {
    // The create path has nothing to preserve, so two boxes reading the same minute *are* the same
    // instant. The sub-minute acceptance above is a property of the origin, not of the rule.
    const errors = errorsOf(validate(baseDraft({ startAt: "2026-08-01T09:00", endAt: "2026-08-01T09:00" })));
    expect(errors.endAt).toBeDefined();
  });
});

// ---------------------------------------------------------------------------------------------
// The DST fall-back fold
// ---------------------------------------------------------------------------------------------

describe("the America/New_York fall-back fold", () => {
  // 2026-11-01: US DST ends at 02:00 EDT, which becomes 01:00 EST. Local 01:00–01:59 therefore
  // happens twice — once at UTC-4 and once at UTC-5 — so one `datetime-local` reading names two
  // different instants. `Asia/Manila` cannot express this at all (no DST since 1978), which is why
  // these tests change zone rather than reusing the baseline.
  const EDT_PASS = "2026-11-01T05:30:00Z";
  const EST_PASS = "2026-11-01T06:30:00Z";
  const AFTER_FOLD = "2026-11-01T07:00:00Z";

  const AMBIGUOUS_BOX = "2026-11-01T01:30";
  const TYPED_END = "2026-11-01T01:45";

  const EDT_OFFSET_MINUTES = 240;
  const EST_OFFSET_MINUTES = 300;

  it("proves the fold is actually in force, rather than assuming the zone took", () => {
    inTimeZone(FOLD_TIME_ZONE, () => {
      // 1. The label ICU reports.
      expect(Intl.DateTimeFormat().resolvedOptions().timeZone).toBe(FOLD_TIME_ZONE);

      // 2. The behaviour that actually matters, which the label alone does not establish: two
      //    distinct instants that a `datetime-local` box cannot tell apart. In UTC or in Manila
      //    these read 05:30/06:30 and 13:30/14:30 respectively, so this assertion is what stops the
      //    scenario below being vacuous.
      expect(new Date(EDT_PASS).getTime()).not.toBe(new Date(EST_PASS).getTime());
      expect(localFrom(EDT_PASS)).toBe(AMBIGUOUS_BOX);
      expect(localFrom(EST_PASS)).toBe(AMBIGUOUS_BOX);

      // 3. The hour really is repeated — the offsets either side of the fold differ by 60 minutes.
      expect(new Date(EDT_PASS).getTimezoneOffset()).toBe(EDT_OFFSET_MINUTES);
      expect(new Date(EST_PASS).getTimezoneOffset()).toBe(EST_OFFSET_MINUTES);

      // 4. V8 resolves an ambiguous wall-clock reading to the earlier, still-DST offset. The whole
      //    scenario turns on that, so it is pinned here — a failure names the cause instead of
      //    surfacing as a mysterious "expected an error" three tests down.
      expect(new Date(TYPED_END).toISOString()).toBe("2026-11-01T05:45:00.000Z");
    });
  });

  it("validates the resolved pair, so a verbatim start after a re-read end is refused", () => {
    inTimeZone(FOLD_TIME_ZONE, () => {
      // Start: untouched, so sent verbatim as 06:30Z (01:30 EST, the second pass).
      // End:   typed, so read in this zone as 05:45Z (01:45 EDT, the first pass) — 45 minutes
      //        EARLIER than the start, while the boxes read 01:30 → 01:45 and look ordered.
      const origin: DraftOrigin = { startAt: EST_PASS, endAt: AFTER_FOLD };
      expect(localFrom(AFTER_FOLD)).toBe("2026-11-01T02:00");

      const errors = errorsOf(
        validate(baseDraft({ startAt: AMBIGUOUS_BOX, endAt: TYPED_END }), origin),
      );

      expect(errors.endAt).toBeDefined();
    });
  });

  it("would have passed had the boxes been compared instead of what is sent", () => {
    // The negative control, expressed rather than asserted about deleted code: this reproduces the
    // pre-fix comparison and shows it accepting the pair the test above refuses. Without this, the
    // test above passes for any reason at all — including a validator that refuses everything.
    inTimeZone(FOLD_TIME_ZONE, () => {
      const boxStart = new Date(AMBIGUOUS_BOX).getTime();
      const boxEnd = new Date(TYPED_END).getTime();
      expect(boxEnd).toBeGreaterThan(boxStart);

      // What is actually sent, and it is the other way round.
      expect(new Date(EST_PASS).getTime()).toBeGreaterThan(boxEnd);
    });
  });

  it("accepts the same edit when the end is left untouched too", () => {
    // The refusal above is about a re-read end, not about the fold making every edit impossible.
    inTimeZone(FOLD_TIME_ZONE, () => {
      const origin: DraftOrigin = { startAt: EST_PASS, endAt: AFTER_FOLD };
      const request = requestOf(
        validate(
          baseDraft({ name: "Renamed", startAt: AMBIGUOUS_BOX, endAt: localFrom(AFTER_FOLD) }),
          origin,
        ),
      );

      expect(request.startAt).toBe(EST_PASS);
      expect(request.endAt).toBe(AFTER_FOLD);
    });
  });

  it("restores the baseline zone afterwards", () => {
    expect(Intl.DateTimeFormat().resolvedOptions().timeZone).not.toBe(FOLD_TIME_ZONE);
    expect(localFrom("2026-08-01T01:00:00Z")).toBe("2026-08-01T09:00");
  });
});
