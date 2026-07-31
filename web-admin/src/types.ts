// API-shaped types — a consumed subset of the backend DTOs in `docs/api/openapi.json`, plus the
// request bodies the SPA sends back and the one value set those constrain (`ATTENDANCE_MODES`, the
// single runtime export here: a union that must also be enumerable to build a picker from).
//
// Still a subset — `StudentDto`'s `sisExternalId` and friends are not here — but **"nothing renders
// it" is NOT the test for dropping a field.** Both write surfaces are full replacements, so a field
// this client cannot *read* is a field it cannot *send back*, and dropping it silently blanks that
// column on every row edited through the UI:
//
//   - `EventItem.description` / `.requireRegistration` — read since the edit form landed (D1b-1).
//   - `Student.firstName` / `.middleName` / `.lastName` / `.gender` / `.photoUrl` — read since the
//     student edit form landed (D2). `fullName` is composed by the server and cannot be split back
//     into three columns, so it is not a substitute for the name parts: without them an edit form had
//     no way to fill itself, and `StudentDto` says so in its own description in the contract.
//
// See the field-level comments on both, and the contract's notes on `EventDto` and `StudentDto`,
// which say the same thing.
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
  /**
   * Composed by the server from the three name parts below. **Read-only, and not a source for them**
   * — splitting "Maria Cruz Santos" back into first/middle/last is guesswork, and a two-word name and
   * a four-word one guess differently. It stays because every list and picker renders it.
   */
  fullName: string;
  /**
   * The name parts, read for the reason the module header records: `PUT /students/{id}` is a **full
   * replacement**, so a field this client cannot read is a field it cannot preserve. Dropping these
   * would blank `MiddleName` on every student edited through the UI — and refuse the save outright for
   * `FirstName`/`LastName`, which are NOT NULL columns the request must carry.
   */
  firstName: string;
  middleName?: string;
  lastName: string;
  email?: string;
  /** Read for the same reason as the name parts: unsent is unset, on a full replacement. */
  gender?: string;
  photoUrl?: string;
  /**
   * **ADR-001 D-2 derived display cache — read-only, and refused by name on a write.**
   *
   * These three are refreshed from the academic tables (Enrollments, StudentTermRecords), not written
   * through the students endpoint. They are single-valued and 12 of the 52 real students sit in more
   * than one section, so the triple cannot describe them. `StudentWriteRequest` below types them
   * `never` so that a spread of this interface into a request body is a **compile error** rather than
   * a 400 discovered at runtime — see the note there.
   */
  course?: string;
  yearLevel?: string;
  section?: string;
  status: string; // one of StudentStatusName below; `string` on the wire
  /**
   * Every card the student has ever been issued, **including deactivated ones**. `DELETE
   * /students/{id}/cards/{cardId}` clears `isActive` and keeps the row, because ADR-001 D-3 needs a
   * past tap to keep resolving to the physical card that produced it. So a screen asking "which card
   * does this student tap with" must filter on `isActive`, not take `cards[0]`.
   */
  cards: Card[];
}

/**
 * §4.3's three student statuses, verified against `StudentStatus` in `EAMS.Domain/DomainValues.cs`.
 *
 * `Student.status` stays `string` for the reason `AttendanceStatus` records below: a response cannot
 * prove a union, and a status this build has never heard of must render as itself rather than be
 * coerced into one of these. **Read these to compare, never to type a received value.**
 */
export const STUDENT_STATUS = {
  Active: "Active",
  Inactive: "Inactive",
  Graduated: "Graduated",
} as const;

/**
 * The three as a union — for values travelling *out*, the same distinction `AttendanceMode` draws.
 *
 * Unlike `EventWriteRequest`, `StudentWriteRequest` **does** carry a status, and the difference is
 * real rather than an inconsistency: an event's status is a state machine whose only door is
 * `PATCH /events/{id}/status` (which is what makes the roster freeze on close unskippable), while a
 * student's is an ordinary §4.3 column the write surface owns. A status control on a student form
 * does something; on an event form it would not.
 */
export type StudentStatusName = (typeof STUDENT_STATUS)[keyof typeof STUDENT_STATUS];

/** The set as a list, so a picker is built from it rather than from hand-typed `MenuItem`s. */
export const STUDENT_STATUSES: readonly StudentStatusName[] = Object.values(STUDENT_STATUS);

/**
 * The body of `POST /students` and of `PUT /students/{id}` — one type, because the server takes one
 * type (`StudentWriteRequest`, checked by one `StudentService.Validate`).
 *
 * The update is a **full replacement**: every field is written from this body, so an edit form must
 * fill it from the student it is editing rather than from an empty draft. `Student` above reads the
 * name parts, `gender` and `photoUrl` for exactly that reason.
 *
 * The nullable strings are `string | null` rather than optional, as `EventWriteRequest` records: an
 * omitted key and an explicit `null` mean the same thing to the server, but deciding at every
 * construction site is what stops an empty text box being sent as `""`.
 *
 * ---
 *
 * **`course`, `yearLevel` and `section` are typed `never`, and that is the load-bearing part.**
 *
 * They are the ADR-001 D-2 derived cache. The server does not merely ignore them — `StudentWriteRequest`
 * carries `[JsonExtensionData]` specifically so a supplied one is *visible* and can be refused by name
 * with `code: "FieldIsDerived"`, because silently dropping them is what would have a client that read a
 * `StudentDto`, edited the name and PUT the whole object back — **the ordinary shape of an edit form** —
 * get a 200 and believe a section had been saved.
 *
 * Declaring them `never` moves that refusal from runtime to the compiler. `const body: StudentWriteRequest
 * = { ...student }` fails to compile with "Type 'string | undefined' is not assignable to type
 * 'undefined'", naming the field. Excess-property checking alone would **not** have caught it — TypeScript
 * does not apply it to spreads, which was verified rather than assumed — so without these three the one
 * construction shape the server built a whole mechanism to refuse is the one the compiler would have
 * waved through. `undefined` is still assignable, so nothing has to be written to satisfy them, and
 * `JSON.stringify` omits an undefined value: no key reaches the wire either way.
 *
 * It is a compile-time guard on this build, not a proof about the endpoint. The server's refusal is
 * still the one that counts, and `studentDraft.validate` remains the only place that constructs one of
 * these — field by field, never from a spread.
 */
export interface StudentWriteRequest {
  studentNumber: string;
  firstName: string;
  middleName: string | null;
  lastName: string;
  email: string | null;
  gender: string | null;
  photoUrl: string | null;
  /** Null or blank takes §4.3's `Active` default server-side; this client always chooses one. */
  status: StudentStatusName;
  course?: never;
  yearLevel?: never;
  section?: never;
}

/**
 * The body of `POST /students/{id}/cards` — §6.2's `{ cardUid, label }`.
 *
 * `cardUid` is **normalised before it is sent** (uppercase, separators stripped): the server normalises
 * whatever arrives, so sending the raw reading would work, but then the form would be showing a value
 * that is not the value stored, and the filtered unique index that decides a duplicate is over the
 * normalised form. Normalise first, compare second — see `normalizeCardUid` in `studentDraft.ts`.
 */
export interface StudentCardRequest {
  cardUid: string;
  label: string | null;
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
