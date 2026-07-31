// What is in an event form's boxes, the rules those boxes are held to, and the request they become.
//
// A module rather than a section of `NewEventDialog`, because there are two forms now. `POST /events`
// and `PUT /events/{id}` take the *same* body and are checked by the *same* server code
// (`EventService.Validate`), so a second copy of these rules beside the edit dialog would be two
// readings of one contract with nothing binding them — and the day they drift is the day one form
// submits a body the other would have refused. The create dialog's `validate()` was singled out at
// D1a's review as the most reusable thing in the slice; this is that, moved rather than copied.
//
// It is deliberately free of React and of `api.ts`: pure functions over plain values, which is also
// what makes it the first thing worth testing when a runner lands (owed before D2).

import { ATTENDANCE_MODES } from "./types";
import type { AttendanceMode, EventItem, EventWriteRequest } from "./types";

// ---------------------------------------------------------------------------------------------
// The server's rules, restated
// ---------------------------------------------------------------------------------------------
//
// Every limit below is `EventText` in `EAMS.Domain/DomainValues.cs`, checked again here. Not because
// the client is trusted — it is not, and `EventService.Validate` still has the last word — but
// because a round trip to be told a name is 4 characters too long is a bad way to learn it, and the
// server's answer arrives after the user has stopped looking at the field.
//
// Named, because a `200` sitting in a `maxLength` prop is a number nobody can check against anything.
// Restated rather than fetched: there is no endpoint that publishes them, so the honest description
// of these is "a copy that will drift if §4.5 changes" — which is what the citation above is for, and
// which is what publishing them into `docs/api/openapi.json` is scheduled to fix.

export const NAME_MAX_LENGTH = 200;
export const DESCRIPTION_MAX_LENGTH = 2000;
export const LOCATION_MAX_LENGTH = 300;
export const MIN_GRACE_MINUTES = 0;
export const MAX_GRACE_MINUTES = 1440;

/** §4.5's own column default (`GraceMinutes int NOT NULL, default 0`), not a suggestion of ours. */
const DEFAULT_GRACE_MINUTES = "0";

/** §4.5's `AttendanceMode` default, which the server also applies when the field is absent. */
const DEFAULT_ATTENDANCE_MODE: AttendanceMode = "Single";

/** What the mode picker calls each value. The values themselves are the wire's, and are not changed. */
export const ATTENDANCE_MODE_LABELS: Record<AttendanceMode, string> = {
  Single: "Single — one tap records attendance",
  TimeInOut: "Time in / time out — a tap to arrive and a tap to leave",
};

// ---------------------------------------------------------------------------------------------
// The draft
// ---------------------------------------------------------------------------------------------

/**
 * What is in the boxes, which is not what is sent.
 *
 * Every field is the control's own value type — text for the numbers and the datetimes included.
 * Holding `graceMinutes` as a `number` would mean the box could not be empty while the user retypes
 * it, and holding the datetimes as `Date`s would mean a half-typed one had no representation at all.
 * The conversion to `EventWriteRequest` happens once, in `validate`, and only when it can succeed.
 */
export interface Draft {
  name: string;
  description: string;
  location: string;
  /** `datetime-local` — a wall-clock reading with no zone. `instantFrom` gives it one. */
  startAt: string;
  endAt: string;
  attendanceMode: AttendanceMode;
  graceMinutes: string;
  requireRegistration: boolean;
}

export type DraftField = keyof Draft;

export const EMPTY_DRAFT: Draft = {
  name: "",
  description: "",
  location: "",
  startAt: "",
  endAt: "",
  attendanceMode: DEFAULT_ATTENDANCE_MODE,
  graceMinutes: DEFAULT_GRACE_MINUTES,
  requireRegistration: false,
};

/**
 * The fields that can carry an error, **in the order they appear on screen** — which is what makes
 * "focus the first invalid one" land on the first invalid one the user can see rather than the first
 * this file happens to list. `attendanceMode` and `requireRegistration` are absent because a select
 * and a checkbox cannot hold a value outside their own options.
 */
export const VALIDATED_FIELDS = [
  "name",
  "startAt",
  "endAt",
  "location",
  "graceMinutes",
  "description",
] as const;

export type ValidatedField = (typeof VALIDATED_FIELDS)[number];

export type FieldErrors = Partial<Record<ValidatedField, string>>;

export const NO_ERRORS: FieldErrors = {};

/**
 * Stable ids, because MUI derives the `<label for>` and the `aria-describedby` that ties an input to
 * its error text from the `id` given to the `TextField`. Generated ids would work for the label and
 * would make the focus-the-first-invalid lookup impossible to write.
 *
 * Built from a prefix rather than fixed, so the create and edit dialogs cannot collide on an id if
 * both are ever mounted at once — duplicate ids silently break `<label for>` for whichever came
 * second, and a label that points at the wrong input is worse than no label.
 */
export const fieldIdsFor = (prefix: string): Record<DraftField, string> => ({
  name: `${prefix}-name`,
  description: `${prefix}-description`,
  location: `${prefix}-location`,
  startAt: `${prefix}-start-at`,
  endAt: `${prefix}-end-at`,
  attendanceMode: `${prefix}-attendance-mode`,
  graceMinutes: `${prefix}-grace-minutes`,
  requireRegistration: `${prefix}-require-registration`,
});

// ---------------------------------------------------------------------------------------------
// Parsing
// ---------------------------------------------------------------------------------------------

/**
 * A date-time string as the instant it names — a `datetime-local` reading **in this browser's zone**,
 * or an offset-bearing instant as itself.
 *
 * The conversion has to happen here. `"2026-08-01T09:00"` carries no offset, so sending it raw would
 * leave the zone to the server's parser, and the server runs in UTC while this school is UTC+8 —
 * the same eight-hour misjudgement `UtcTime` records on the backend, except silent, because an event
 * eight hours out is still a perfectly valid event. `new Date(local)` reads a bare date-time as
 * local time, which is what the user typed; `toISOString()` then names the instant unambiguously. The
 * same call reads `"2026-08-01T01:00:00Z"` as the instant it already is, which is what lets `resolve`
 * below measure a verbatim server string on the same scale as a typed one.
 *
 * Epoch milliseconds rather than a `Date`, so the start/end comparison below is `<=` on two numbers
 * instead of a wrong-looking comparison of two objects.
 */
function instantFrom(local: string): number | undefined {
  if (local === "") return undefined;
  const ms = new Date(local).getTime();
  return Number.isNaN(ms) ? undefined : ms;
}

/** `datetime-local` wants zero-padded parts, and `2026` is four of them where the rest are two. */
const YEAR_DIGITS = 4;
const DATE_PART_DIGITS = 2;

const pad = (value: number, digits: number) => String(value).padStart(digits, "0");

/**
 * The other direction: a server instant as the `datetime-local` reading that names it **in this
 * browser's zone**, so an event stored at 01:00Z shows as 09:00 to someone in Manila rather than
 * being edited an hour after midnight.
 *
 * Built by hand from the local getters rather than by slicing `toISOString()`, which would be the UTC
 * reading — the exact eight-hour error `instantFrom` exists to avoid, arriving through the inverse.
 *
 * `""` for an unparseable instant, which is what an empty box holds: `validate` then asks for a start
 * and an end, which is honest, where a silently wrong date would not be.
 */
export function localFrom(iso: string): string {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return "";
  // `getMonth()` is 0-based; every other getter here is not.
  const date = `${pad(at.getFullYear(), YEAR_DIGITS)}-${pad(at.getMonth() + 1, DATE_PART_DIGITS)}-${pad(at.getDate(), DATE_PART_DIGITS)}`;
  return `${date}T${pad(at.getHours(), DATE_PART_DIGITS)}:${pad(at.getMinutes(), DATE_PART_DIGITS)}`;
}

/** A whole number of minutes inside §4.5's range, or nothing. `Number("")` is 0, hence the guard. */
function wholeMinutesFrom(raw: string): number | undefined {
  const text = raw.trim();
  if (text === "") return undefined;
  const value = Number(text);
  if (!Number.isInteger(value)) return undefined;
  return value >= MIN_GRACE_MINUTES && value <= MAX_GRACE_MINUTES ? value : undefined;
}

/**
 * The boxes filled from an event that already exists — the edit form's starting state.
 *
 * `attendanceMode` is narrowed against the same set the picker is built from rather than asserted:
 * a value outside it cannot be represented in `EventWriteRequest`, so an event carrying one is an
 * event this form cannot round-trip. The fallback here is the last line of defence and must not be
 * the *first* — `EventDetail` refuses to open the form at all in that case, because falling back
 * silently is how an event's mode gets rewritten by a user who came to fix a typo in its name.
 */
export function draftFrom(event: EventItem): Draft {
  return {
    name: event.name,
    description: event.description ?? "",
    location: event.location ?? "",
    startAt: localFrom(event.startAt),
    endAt: localFrom(event.endAt),
    attendanceMode: knownMode(event.attendanceMode) ?? DEFAULT_ATTENDANCE_MODE,
    graceMinutes: String(event.graceMinutes),
    requireRegistration: event.requireRegistration,
  };
}

/**
 * The wire's mode as one this client can send back, or nothing. `EventItem.attendanceMode` is
 * `string` because a response cannot prove a union; this is where that becomes a decision instead of
 * an assumption.
 */
export const knownMode = (wire: string): AttendanceMode | undefined =>
  ATTENDANCE_MODES.find((mode) => mode === wire);

// ---------------------------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------------------------

/**
 * How much of an event this form may change — the client's reading of the server's two edit gates,
 * `EventStatusTransition.AcceptsEdits` and `AcceptsAttendanceRuleEdits`.
 *
 * There is a third mode the server has and this type does not: `Closed`, which accepts no edit at
 * all. That one is not a scope, it is the absence of one, so it is `EventDetail`'s decision not to
 * open a form rather than a form that renders with every control dead.
 */
export type EditScope = "everything" | "descriptive";

/**
 * The five fields a `Cancelled` event refuses, and the reason they travel together: they are what
 * decides what an attendance row *means*. An `Open → Cancelled` event can already hold taps whose
 * Present-versus-Late was settled by `StartAt + GraceMinutes`, so moving them afterwards rewrites
 * what those rows say without touching the rows — which is why the server refuses it there for the
 * same reason it refuses it on a `Closed` event.
 *
 * Mirrors `EventService.AttendanceRuleChanges` field for field.
 */
const ATTENDANCE_RULE_FIELDS: ReadonlySet<DraftField> = new Set([
  "startAt",
  "endAt",
  "graceMinutes",
  "attendanceMode",
  "requireRegistration",
]);

const NOTHING_LOCKED: ReadonlySet<DraftField> = new Set();

export const lockedFor = (scope: EditScope): ReadonlySet<DraftField> =>
  scope === "everything" ? NOTHING_LOCKED : ATTENDANCE_RULE_FIELDS;

/**
 * The instants the draft was filled from, when it was filled from an existing event.
 *
 * They exist because `datetime-local` has minute resolution and an instant does not. Filling a box
 * from `2026-08-01T09:00:30Z` and reading it back out gives `09:00:00Z` — thirty seconds earlier,
 * from a form the user never touched. On a `Draft` or `Open` event that is a silent shift in what
 * decides Present versus Late; on a `Cancelled` one it is worse, because the server compares the
 * incoming `startAt` against the stored one and refuses the whole save with a 409 naming a field the
 * user did not go near.
 *
 * So: if a box still holds exactly what was put into it, send back exactly what the server gave us.
 * Round-tripping a value verbatim is the only way to be sure it did not move.
 */
export interface DraftOrigin {
  startAt: string;
  endAt: string;
}

/**
 * Either the request, or why there is not one. One function, so the answer to "may this be sent" and
 * the answer to "what exactly is sent" cannot disagree — a separate `isValid` predicate beside a
 * separate `toRequest` builder is two readings of one set of rules, and the day they drift is the day
 * a form submits a body it just told the user was fine.
 */
export type Validated =
  | { ok: true; request: EventWriteRequest }
  | { ok: false; errors: FieldErrors };

/**
 * @param origin the instants this draft was filled from, for an edit. Omitted for a create, where
 *   there is nothing to preserve — see `DraftOrigin`.
 */
export function validate(draft: Draft, origin?: DraftOrigin): Validated {
  const errors: FieldErrors = {};

  // Trimmed before measuring, because the server trims before measuring (`EventText.IsValidName`,
  // `Apply`). Measuring the untrimmed string would refuse a name the server would have accepted.
  const name = draft.name.trim();
  if (name.length === 0) {
    errors.name = "A name is required.";
  } else if (name.length > NAME_MAX_LENGTH) {
    errors.name = `${NAME_MAX_LENGTH} characters at most; this is ${name.length}.`;
  }

  const description = draft.description.trim();
  if (description.length > DESCRIPTION_MAX_LENGTH) {
    errors.description = `${DESCRIPTION_MAX_LENGTH} characters at most; this is ${description.length}.`;
  }

  const location = draft.location.trim();
  if (location.length > LOCATION_MAX_LENGTH) {
    errors.location = `${LOCATION_MAX_LENGTH} characters at most; this is ${location.length}.`;
  }

  // Resolved before they are compared, and that is the fix rather than the tidy-up. See `resolve`.
  const startAt = resolve(draft.startAt, origin?.startAt);
  if (startAt === undefined) errors.startAt = "A start date and time is required.";

  const endAt = resolve(draft.endAt, origin?.endAt);
  if (endAt === undefined) {
    errors.endAt = "An end date and time is required.";
  } else if (startAt !== undefined && endAt.at <= startAt.at) {
    // The server's rule verbatim, and it is `<=` there too: an event whose window is empty or
    // inverted can never be attended, because the tap path decides Present versus Late from the
    // start and the grace period.
    errors.endAt = "The end must be after the start.";
  }

  const graceMinutes = wholeMinutesFrom(draft.graceMinutes);
  if (graceMinutes === undefined) {
    errors.graceMinutes = `A whole number of minutes from ${MIN_GRACE_MINUTES} to ${MAX_GRACE_MINUTES}.`;
  }

  // The three `=== undefined` arms are what narrow the resolved values for the request below; they
  // cannot fire on their own, because each of them set an error above. The `errors` check is the
  // real condition and it is first.
  if (
    Object.keys(errors).length > 0 ||
    startAt === undefined ||
    endAt === undefined ||
    graceMinutes === undefined
  ) {
    return { ok: false, errors };
  }

  return {
    ok: true,
    request: {
      name,
      // `null`, not `""`. An empty box means the organizer did not give a location; an empty string
      // is a location, and the column would store it as one — after which "has a location" is true
      // for an event that has none.
      description: description === "" ? null : description,
      location: location === "" ? null : location,
      startAt: startAt.send,
      endAt: endAt.send,
      attendanceMode: draft.attendanceMode,
      graceMinutes,
      requireRegistration: draft.requireRegistration,
    },
  };
}

/**
 * The server's own instant, when the box still holds exactly the reading it was filled with — and
 * `undefined` when the user has moved it, or when there was nothing to move it from. See
 * `DraftOrigin` for why sending the original string back is not paranoia.
 */
function unmoved(box: string, original: string | undefined): string | undefined {
  if (original === undefined) return undefined;
  return box === localFrom(original) ? original : undefined;
}

/** One end of the window: the string that will be sent, and the instant that string names. */
interface Resolved {
  /** Verbatim from the server when the box has not moved, otherwise this browser's reading of it. */
  send: string;
  /** Epoch milliseconds, for the comparison. */
  at: number;
}

/**
 * The value that will actually be **sent**, resolved once, so the rule the form applies is applied to
 * the request rather than to something adjacent to it.
 *
 * `validate` used to compare `instantFrom(box)` for both ends while the request could carry `unmoved`
 * originals for either — two different pairs, checked and sent. They agree whenever a wall-clock
 * reading names exactly one instant, which is every hour of the year in `Asia/Manila` (no DST since
 * 1978) and all but one in a zone that has it. In that hour they disagree, and both directions are
 * live: at the `America/New_York` fall-back fold a form can pass a pair whose *sent* values are
 * inverted — start verbatim at 01:30 EST, end typed as 01:45 and read as EDT, fifteen minutes
 * earlier — and the server refuses the save with a 400 about a window the user cannot see anything
 * wrong with. This code is zone-generic, so "unreachable at this school" is a fact about the
 * deployment and not about the rule.
 *
 * It also settles a smaller thing that was never DST's fault: `datetime-local` has minute resolution,
 * so an event running 09:00:30Z → 09:00:50Z filled two boxes that both read `09:00` and was refused
 * client-side as "the end must be after the start" — on a save the user made to its *name*. Measuring
 * what is sent measures the seconds the server actually holds.
 *
 * The verbatim string is passed through untouched rather than round-tripped through `toISOString()`.
 * SQL Server's `datetime2` keeps more precision than a JavaScript `Date` does, so normalising it here
 * would shave sub-millisecond digits off a value the user never touched — which is exactly the silent
 * movement `unmoved` exists to prevent, arriving through the function meant to preserve it.
 */
function resolve(box: string, original: string | undefined): Resolved | undefined {
  const verbatim = unmoved(box, original);
  const at = instantFrom(verbatim ?? box);
  if (at === undefined) return undefined;
  return { send: verbatim ?? new Date(at).toISOString(), at };
}
