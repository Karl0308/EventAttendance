// API-shaped types — a consumed subset of the backend DTOs in `docs/api/openapi.json`, plus the
// request bodies the SPA sends back and the one value set those constrain (`ATTENDANCE_MODES`, the
// single runtime export here: a union that must also be enumerable to build a picker from).
//
// Deliberately a subset: `StudentDto` also carries firstName/middleName/lastName/gender/photoUrl,
// none of which the SPA reads, and `api.ts` drops them at the boundary rather than widening these
// types with fields nothing renders.
//
// `EventItem` is the exception, and "nothing renders it" is NOT the test for removing a field from it.
// `description` and `requireRegistration` are read because `PUT /events/{id}` is a full replacement:
// a field this client cannot read is a field it cannot send back, so dropping them blanks the
// description and resets the flag on every event edited through the UI, silently. See their
// field-level comments — and the contract's own note on `EventDto`, which says the same thing.
//
// The `<Dto>PagedResult` envelope the admin lists now return is deliberately NOT here. No component
// consumes it: `api.ts` walks the pages and hands back rows, so paging stays a fact about the wire
// rather than a type spreading through the component tree. Its shape lives in `api.ts` as `PageOf<T>`.
// If a grid ever needs true server-side paging, that is a design change to raise before it is typed.

export interface Card {
  id: string;
  cardUid: string;
  label?: string;
  isActive: boolean;
}

export interface Student {
  id: string;
  studentNumber: string;
  fullName: string;
  email?: string;
  course?: string;
  yearLevel?: string;
  section?: string;
  status: string; // Active/Inactive/Graduated
  cards: Card[];
}

export interface EventItem {
  id: string;
  name: string;
  /**
   * Read even though no screen displays it, and `requireRegistration` below with it.
   *
   * `PUT /events/{id}` is a **full replacement**, so the edit form has to send back every field it is
   * not changing. A field this client cannot read is a field it cannot preserve: dropping these two
   * at the seam would blank the description of every event edited through the UI and reset its
   * registration flag, silently, on a save the user made for an unrelated reason. `EventDto`'s own
   * description in `docs/api/openapi.json` says the same thing — they were added to the contract for
   * exactly this.
   */
  description?: string;
  location?: string;
  startAt: string; // ISO
  endAt: string;
  attendanceMode: string; // Single / TimeInOut
  graceMinutes: number;
  requireRegistration: boolean;
  status: string; // Draft/Open/Closed/Cancelled
}

/**
 * §4.5's two capture shapes, verified against `AttendanceMode.All` in `EAMS.Domain/DomainValues.cs`.
 *
 * A union here where `EventItem.attendanceMode` above stays `string`, and the difference is which
 * way the value is travelling. A *received* value is whatever the wire carried and a response cannot
 * prove a union — that is the same reasoning `AttendanceStatus` records below. A *sent* value is
 * chosen at the call site, and the server refuses anything outside this set with a 400, so the
 * compiler can hold the set and the picker can be built from it instead of from two hand-typed
 * `MenuItem`s that drift.
 */
export const ATTENDANCE_MODES = ["Single", "TimeInOut"] as const;
export type AttendanceMode = (typeof ATTENDANCE_MODES)[number];

/**
 * §4.5's four event statuses, verified against `EventStatus` in `EAMS.Domain/DomainValues.cs`.
 *
 * Named constants rather than string literals scattered through the screens, because three separate
 * decisions now turn on them — whether taps can be simulated, whether the event may be edited, and
 * how much of it may be edited — and a typo in any one of those is a silent wrong answer rather than
 * a compile error.
 *
 * `EventItem.status` stays `string` for the reason `AttendanceStatus` records: a response cannot
 * prove a union, and a status this build has never heard of must render as itself rather than crash
 * or be coerced into one of these. **Read these to compare, never to type a received value.**
 */
export const EVENT_STATUS = {
  Draft: "Draft",
  Open: "Open",
  Closed: "Closed",
  Cancelled: "Cancelled",
} as const;

/**
 * The four statuses as a union — for values travelling *out*, which is the same distinction
 * `AttendanceMode` draws above.
 *
 * `PATCH /events/{id}/status` takes one of these and refuses anything else with a 400, so the
 * compiler can hold the set for a value this client chooses. It stays separate from
 * `EventItem.status`, which is `string` because a response cannot prove a union.
 */
export type EventStatusName = (typeof EVENT_STATUS)[keyof typeof EVENT_STATUS];

/**
 * The body of `POST /events` and of `PUT /events/{id}` — one type, because the server takes one type.
 *
 * The update is a **full replacement**, not a patch: every field is written from this body, so an
 * edit form must fill it from the event it is editing rather than from an empty draft. `EventItem`
 * above carries `description` and `requireRegistration` for that reason.
 *
 * **`status` is deliberately absent**, mirroring `EventWriteRequest` on the server. A new event is
 * always created `Draft` and `PATCH /events/{id}/status` is the only door into that column, which is
 * what makes the roster freeze on close impossible to bypass. A `status` field here would be a
 * second door, and a form offering it would be a control that silently does nothing.
 *
 * The nullable strings are `string | null` rather than optional: an omitted key and an explicit
 * `null` mean the same thing to the server, but making the decision explicit at every construction
 * site is what stops an empty text box being sent as `""` — which is a location, and stores as one.
 */
export interface EventWriteRequest {
  name: string;
  description: string | null;
  location: string | null;
  /** An instant, not a wall-clock reading. The server normalizes to UTC and compares after. */
  startAt: string;
  endAt: string;
  attendanceMode: AttendanceMode;
  graceMinutes: number;
  requireRegistration: boolean;
}

/**
 * The canonical set, verified against the backend's `AttendanceStatus.All` in
 * `EAMS.Domain/DomainValues.cs`. It is documentation, not the wire type: the column is a `string`
 * (see the project's enum-ish-string rule) and `AttendanceDto.status` is declared `string` in the
 * contract, so `AttendanceRecord.status` below stays `string` rather than asserting a union the
 * response cannot prove.
 */
export type AttendanceStatus = "Present" | "Late" | "Absent" | "Excused";

export interface AttendanceRecord {
  id: string;
  eventId: string;
  studentId: string;
  studentName: string;
  studentNumber: string;
  checkInAt?: string;
  checkOutAt?: string;
  status: string; // one of AttendanceStatus above; `string` on the wire
  captureMethod: string; // Rfid/Manual/Import
}

export interface EventSummary {
  eventId: string;
  eventName: string;
  expected: number;
  present: number;
  late: number;
  absent: number;
  excused: number;
  /**
   * Walk-ins: recorded but never invited. Added to match `EventSummaryDto` — the backend moved the
   * over-100% signal out of `attendanceRate` and into this count, so dropping it at the seam would
   * discard the only place that signal now lives. No UI reads it yet.
   */
  unexpected: number;
  /** Share of the *invited* who turned up, as a percentage to one decimal place (0–100). */
  attendanceRate: number;
}
