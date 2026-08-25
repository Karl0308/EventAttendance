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

// ---------------------------------------------------------------------------------------------
// The audience — who an event expects
// ---------------------------------------------------------------------------------------------

/**
 * §4.7 `StudentGroups`, as `GET /student-groups` publishes it — **the thing an event's audience is
 * attached to**. `POST /events/{id}/attendees` takes ids from this list.
 *
 * `type` and `sourceType` stay `string` for the reason `EventItem.status` does: a response cannot
 * prove a union, and a group kind this build has never heard of must render as itself rather than be
 * coerced into one of the six. Compare against `GROUP_TYPE` / `GROUP_SOURCE_TYPE` below; never type a
 * received value with them.
 *
 * `termId`/`termCode`/`lastSyncedAt` are nullable **in the contract, on purpose**: a `Manual` group
 * spans terms by nature and the projection never touches it. So a null there is "this is a hand-made
 * group", not a drift — which is why they are `optStr` at the seam rather than required.
 */
export interface StudentGroup {
  id: string;
  name: string;
  /** `Course` / `Section` / `Org` / `Custom` / `College` / `Program`. */
  type: string;
  /** `Manual` (a person made it) or `Derived` (the ADR-001 D-1 projection owns it). */
  sourceType: string;
  /** Which academic concept a derived group projects, or `None` on a manual one. */
  sourceEntityType: string;
  termId?: string;
  termCode?: string;
  /**
   * Members excluding the soft-deleted, counted in the database. **The number an organizer is really
   * deciding on** — "invite BSCRIM 2-A" is a different decision at 8 students than at 80 — and a
   * derived group showing zero is the visible signal that the projection has not run for its term.
   */
  memberCount: number;
  lastSyncedAt?: string;
}

/** Read these to compare a received `StudentGroup.sourceType`, never to type one. */
export const GROUP_SOURCE_TYPE = {
  Manual: "Manual",
  Derived: "Derived",
} as const;

export type GroupSourceTypeName = (typeof GROUP_SOURCE_TYPE)[keyof typeof GROUP_SOURCE_TYPE];

/**
 * The one `StudentGroup.type` the audience picker offers, named rather than written as a literal at
 * the three places that test for it. JJ's flow is *"on that event we can select which Section"* — the
 * other five kinds are real groups this build simply does not put in this picker yet.
 */
export const GROUP_TYPE_SECTION = "Section";

/**
 * §4 `Terms`, as `GET /academic/terms` publishes it.
 *
 * `startsOn`/`endsOn` are **calendar dates, not instants** (`YYYY-MM-DD`) and both are frequently
 * null — the roster source has no term date columns — which is why nothing here sorts on them.
 * `isCurrent` is carried on the row precisely so a term picker does not need a second request to
 * `GET /academic/terms/current` to mark it.
 */
export interface Term {
  id: string;
  code: string;
  schoolYear: string;
  semester: string;
  /** At most one per school, enforced by a filtered unique index rather than by convention. */
  isCurrent: boolean;
  startsOn?: string;
  endsOn?: string;
}

/**
 * The body of `POST /academic/terms` and `PUT /academic/terms/{id}` — one shape for both, because the
 * server checks them with one piece of code (D-53).
 *
 * **`isCurrent` is deliberately absent.** At most one term per school can be current, guarded by a
 * filtered unique index, so moving that flag is a two-row transaction and lives on
 * `PATCH /academic/terms/{id}/current`. Carrying it here would be one resource's payload rewriting a
 * different resource — the same split `EventWriteRequest` draws against `PATCH /events/{id}/status`.
 *
 * **`schoolId` is absent for the reason it is absent from every other write request**: the tenant
 * comes from the server's school context and never from the client.
 *
 * The three strings are sent **exactly as typed**. `code` in particular is the one natural key in the
 * academic layer a person authors rather than a spreadsheet supplies, and the server answers `400`
 * for a padded value rather than trimming it — see `termDraft.ts`.
 */
export interface TermWriteRequest {
  code: string;
  schoolYear: string;
  semester: string;
  /**
   * A **calendar date** (`YYYY-MM-DD`), not an instant, and normally `null` — the SIS export has no
   * term-date columns. `null` rather than optional: both routes take a full body, and on a
   * replacement a missing member is ambiguous where an explicit null is not.
   */
  startsOn: string | null;
  /** As `startsOn`. If both are supplied, this one may not fall before it. */
  endsOn: string | null;
}

/** One section on an event's audience, as `GET /events/{id}/attendees` lists it. */
export interface EventAudienceGroup {
  studentGroupId: string;
  name: string;
  type: string;
  sourceType: string;
  termId?: string;
  termCode?: string;
  memberCount: number;
}

/** One individually-attached student on an event's audience. */
export interface EventAudienceStudent {
  studentId: string;
  studentNumber: string;
  fullName: string;
  /** ADR-002 D-9's display cache. Display only — not a join key, not a filter, not a grouping. */
  section?: string;
}

/**
 * Who an event expects — `GET /events/{id}/attendees`.
 *
 * **`students` being empty on a frozen event is not "nobody was attached".** ADR-003 D-13: reaching a
 * terminal status resolves the live audience once and writes it down as individual `EventGroups`
 * student rows, and those rows are the event's denominator. This endpoint does not republish them —
 * the per-student frozen set is `GET /events/{id}/roster`. So on a terminal event `groups` is the
 * historical record of *which cohort was invited* and `students` comes back empty, and any UI reading
 * that emptiness as "no audience" would contradict the non-zero `expected` printed beside it.
 */
export interface EventAudience {
  eventId: string;
  /** The event's status as the server holds it. `string` for the usual reason. */
  status: string;
  /**
   * ADR-003 D-16: **"this event's audience is snapshotted"**, true for both terminal statuses. It is
   * deliberately *not* a synonym for `Closed` — a cancelled event's numbers are equally fixed.
   */
  isFrozen: boolean;
  /** The invited population (ADR-003 D-19), the same number `GET /events/{id}/summary` reports. */
  expected: number;
  groups: EventAudienceGroup[];
  students: EventAudienceStudent[];
}

/**
 * The body of `POST /events/{id}/attendees`.
 *
 * Both lists are optional and both may be sent at once. Sending neither is a **no-op rather than an
 * error** — it is what "the organizer cleared the form and saved" looks like — so this client never
 * has to defend against an empty submit producing a 400.
 */
export interface EventAudienceRequest {
  studentGroupIds?: string[];
  studentIds?: string[];
}

/**
 * What one attach did — and the `Already` counters are the load-bearing part.
 *
 * They exist so idempotency is **observable** rather than merely true: a re-post answering
 * `{ groupsAttached: 0, groupsAlreadyAttached: 3 }` tells the organizer their earlier request landed.
 * A UI that rendered that as a failure — or as nothing — would leave "did that save?" unanswered,
 * which is exactly what the counters were added to answer without a second round trip.
 */
export interface EventAudienceResult {
  eventId: string;
  groupsAttached: number;
  studentsAttached: number;
  groupsAlreadyAttached: number;
  studentsAlreadyAttached: number;
  /** The expected count *after* this call, so the denominator can move on screen without a re-read. */
  expected: number;
  /**
   * Non-fatal observations, empty on the ordinary case. A group from a non-current term **warns
   * rather than refuses** (ADR-003, Accepted Context), and this array is the only place that says so
   * — swallowing it is the whole failure the field exists to prevent.
   */
  warnings: string[];
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

// ---------------------------------------------------------------------------------------------
// Devices — §4.10 / §6.6, and the one DTO in this file that carries a credential
// ---------------------------------------------------------------------------------------------

/**
 * A registered RFID reader / kiosk, as `GET /devices` publishes it.
 *
 * **There is deliberately no key-shaped field here, and adding one would be the bug.** `DeviceDto` on
 * the server has none either: the plaintext token exists in exactly two responses (the 201 from
 * `POST /devices` and the 200 from `POST /devices/{id}/regenerate-key`) and is carried only by
 * `DeviceKeyIssued` below. The server stores `SHA-256(secret)` and nothing else, so "show it again"
 * has no implementation rather than a refused one, and a field here would turn every list read into a
 * credential dump — which `DeviceLifecycleTests.No_read_response_ever_carries_the_key` asserts on the
 * serialized bytes.
 *
 * `deviceType` stays `string` for the reason `EventItem.status` records: a response cannot prove a
 * union, and a type this build has never heard of must render as itself. Compare against
 * `DEVICE_TYPES`; never type a received value with it.
 */
export interface Device {
  id: string;
  name: string;
  deviceType: string; // one of DEVICE_TYPES below; `string` on the wire
  readerModel?: string;
  isActive: boolean;
  /**
   * The **public** half of the key — the twelve characters between `eams_dk_` and the secret. Safe to
   * show and to log: it identifies the credential without being one, which is what makes "this
   * kiosk's key id is `a91f…`, and the 401 in the log says `a91f…`" a sentence an operator can say.
   * Absent when the device has never been issued a key.
   */
  apiKeyId?: string;
  /**
   * Whether this device could authenticate **right now**: it holds a key, the key is not revoked, and
   * the device is active. One boolean rather than three, because "why can this kiosk not tap?" is one
   * question. The three columns below say *which* of the three is the answer.
   */
  hasActiveKey: boolean;
  apiKeyIssuedAt?: string;
  /**
   * When the key was last accepted. Written opportunistically and **throttled** server-side, so it is
   * a coarse signal — the column an operator reads to decide whether a credential is still in use and
   * therefore whether revoking it will break something.
   */
  apiKeyLastUsedAt?: string;
  apiKeyRevokedAt?: string;
  lastSeenAt?: string;
}

/**
 * §4.10's three device types, verified against `DeviceTypes.All` in `EAMS.Domain/DeviceEntities.cs`.
 *
 * A union for values travelling *out*, the same distinction `ATTENDANCE_MODES` draws. The server
 * normalises case and takes `Kiosk` for a null or blank one, but this client always chooses one
 * explicitly rather than relying on that default — a picker built from this list cannot drift from the
 * set the server accepts, where two hand-typed `MenuItem`s can.
 */
export const DEVICE_TYPES = ["Kiosk", "Mobile", "Handheld"] as const;
export type DeviceTypeName = (typeof DEVICE_TYPES)[number];

/**
 * The body of `POST /devices` and of `PUT /devices/{id}` — one type, because the server takes one type
 * and checks it with one `DeviceService.Validate`.
 *
 * The update is a **full replacement** of the device's own fields, so an edit form must fill it from
 * the device it is editing. It is *not* a replacement of the key: `UpdateAsync` deliberately touches
 * no `ApiKey*` column, because retiring a device and burning its credential are two different
 * statements. Setting `isActive: false` stops the device authenticating (that is what `hasActiveKey`
 * folds in) without revoking anything, so turning it back on restores the same key.
 *
 * **No key field, and there is no version of this request that could have one.** `DeviceKey.Issue`
 * takes no input specifically so an operator-chosen key cannot exist — the SHA-256-rather-than-Argon2
 * decision is only correct while the secret is 256 bits of server-generated entropy.
 *
 * `readerModel` is `string | null` rather than optional, as the other write requests are: an omitted
 * key and an explicit `null` mean the same thing to the server, but deciding at every construction
 * site is what stops an empty text box being sent as `""`.
 */
export interface DeviceWriteRequest {
  name: string;
  deviceType: DeviceTypeName;
  readerModel: string | null;
  isActive: boolean;
}

/**
 * Just enough of the device to say *which* device the token in front of you belongs to.
 *
 * **Not a `Device`, and deliberately all-optional.** The nested object on `DeviceKeyIssuedDto` is a
 * full `DeviceDto`, but narrowing it as one would make five display fields able to veto the delivery
 * of a credential that has already been minted — see `toDeviceKeyIssued`. The reveal needs a title and
 * a key id; the row itself is re-read from `GET /devices` moments later, which is where anything
 * stricter belongs.
 */
export interface IssuedKeyDevice {
  id?: string;
  name?: string;
  /** The public half — see `Device.apiKeyId`. Safe to show; not a credential. */
  apiKeyId?: string;
}

/**
 * The one and only carrier of a plaintext device key — `DeviceKeyIssuedDto`.
 *
 * **`apiKey` is the only time this value ever exists outside the device that will hold it.** It is
 * returned at issue and at rotation and never afterwards; there is no endpoint that shows it again and
 * there never will be. Anything that receives one of these owes the operator a one-shot reveal they
 * cannot dismiss by accident — see `DeviceKeyDialog`. Do not log it, do not put it in a Snackbar, do
 * not keep it in a list.
 */
export interface DeviceKeyIssued {
  device: IssuedKeyDevice;
  /** The complete `eams_dk_<keyId>_<secret>` token. 85 characters, lower-case, case-significant. */
  apiKey: string;
  /**
   * Why the device beside the token is thinner than it should be, when it is. Present only on drift,
   * and never a reason to withhold the reveal: it is shown *inside* the dialog as a caveat on the
   * identity, because the token is the part that cannot be fetched again.
   */
  deviceDrift?: string;
}

// ---------------------------------------------------------------------------------------------
// The SIS roster import — §10, and the surface that reads and writes every student in the school
// ---------------------------------------------------------------------------------------------
//
// **`SisImportRowDto.rawData` is deliberately absent from `SisImportRow` below, and that omission is
// the point rather than an oversight.** It is the source row as the workbook held it — names,
// institutional e-mail addresses, enrolment — for every student in the file. This surface is open
// (ADR-001 D-6: `sis.import` is declared and not enforced), so the rule the controller states applies
// here too: nothing echoes row contents beyond what the results table needs.
//
// **What the omission buys is retention and reachability, not secrecy.** `res.json()` materialises the
// whole reply — `rawData` included — before the mapper runs, and the raw bytes are in the devtools
// Network panel either way; dropping the field cannot and does not stop that. What it does is end the
// value's life at the mapper: nothing holds it, nothing renders it, and no later code path can reach
// it, so it is not in component state, not in a log line, not in a console dump of a row, and not
// something a future edit can start displaying by adding a column.
//
// It is safe to drop where `EventItem.description` was not, and the difference is the write shape:
// there is no full-replacement PUT anywhere on this surface. The only body this client sends back is
// `SisImportRunRequest`, which carries one term id.

/**
 * §4.12 `SisImportBatches`, as the four §10 endpoints publish it.
 *
 * The five counters are the reconciliation ADR-001 D-5 exists to make possible —
 * `inserted + updated + failed + skipped === total`, always — with `warningRows` orthogonal to the
 * four (a warned row is already counted in one of them).
 */
export interface SisImportBatch {
  id: string;
  termId: string;
  termCode: string;
  /** Where the batch came from. `string` on the wire, for the reason `EventItem.status` records. */
  source: string;
  fileName?: string;
  sourceSheetName?: string;
  /**
   * The fingerprint of the uploaded bytes. Shown truncated, and never used by this client to decide
   * whether two uploads are "the same file" — that is the server's judgement and it makes it during
   * the run, where `Skipped` is the answer.
   */
  fileHash?: string;
  /** One of `SIS_IMPORT_STATUS`; `string` on the wire. */
  status: string;
  totalRows: number;
  insertedRows: number;
  updatedRows: number;
  failedRows: number;
  skippedRows: number;
  warningRows: number;
  startedAt?: string;
  finishedAt?: string;
  /**
   * Whether the counters add up, **as the server computed it**. Read rather than derived here for the
   * reason the DTO gives for exposing it at all: it is the one check an operator runs, and a UI that
   * re-derives it will eventually derive it against a different set of fields than the server did and
   * report a disagreement that does not exist.
   */
  countersReconcile: boolean;
}

/**
 * §4.12 `SisImportBatches.Status`, verified against `SisImportStatus.All` in
 * `EAMS.Domain/SisImportValues.cs`.
 *
 * The three "Completed…" values are three different answers to "do I need to go and look at the
 * rows?", and collapsing them is what this set exists to stop. `Failed` is not one of them: it means
 * the run itself stopped, which is a different problem with a different fix.
 *
 * **Read these to compare, never to type a received value** — the usual rule in this file.
 */
export const SIS_IMPORT_STATUS = {
  Pending: "Pending",
  Running: "Running",
  Completed: "Completed",
  CompletedWithWarnings: "CompletedWithWarnings",
  CompletedWithErrors: "CompletedWithErrors",
  Failed: "Failed",
} as const;

export type SisImportStatusName = (typeof SIS_IMPORT_STATUS)[keyof typeof SIS_IMPORT_STATUS];

/**
 * §4.12 `SisImportRows.Result`, verified against `SisImportRowResult.All`.
 *
 * **`Skipped` means "this row asked for nothing that was not already true", not "this row was
 * ignored"** — it is the expected outcome of *every* row of a re-import, which is what makes running
 * the same roster twice a no-op the operator can see rather than merely be promised.
 *
 * `Pending` is what a staged row carries between upload and run, so it is the result every row of a
 * preview has and no row of a finished batch does.
 */
export const SIS_IMPORT_ROW_RESULT = {
  Pending: "Pending",
  Inserted: "Inserted",
  Updated: "Updated",
  Failed: "Failed",
  Skipped: "Skipped",
} as const;

export type SisImportRowResultName =
  (typeof SIS_IMPORT_ROW_RESULT)[keyof typeof SIS_IMPORT_ROW_RESULT];

/**
 * One entity a staged row touched — the answer to "row 214 says Skipped, against what?", which §4.12's
 * single nullable `StudentId` could not give for a source whose grain spans six entities.
 */
export interface SisImportRowEntity {
  /** `Student`, `Section`, `Course`… `string` on the wire. */
  entityType: string;
  entityId: string;
  /** `Inserted` / `Updated` / `Unchanged`. */
  action: string;
}

/**
 * One staged source row and what became of it — as `GET /sis/import/{batchId}/rows` lists it.
 *
 * `rawData` is not read. See the section header above: this is the whole roster's PII and nothing on
 * this screen needs it. `errorMessage`, `skipReason` and `warningMessage` are the server's own
 * sentences about *why*, which is what a failed-row list is for, and they are the reason a row number
 * is enough to find the line in the workbook without this client holding its contents.
 */
export interface SisImportRow {
  id: string;
  /** 1-based row in the source sheet — what an operator opens the workbook and jumps to. */
  rowNumber: number;
  /** One of `SIS_IMPORT_ROW_RESULT`; `string` on the wire. */
  result: string;
  skipReason?: string;
  warningCode?: string;
  warningMessage?: string;
  errorMessage?: string;
  studentId?: string;
  entities: SisImportRowEntity[];
}

/**
 * What an upload found, **before anything is written to the academic tables** — the 201 body of
 * `POST /sis/import/upload`.
 *
 * The distinct counts are the preview's whole purpose: they are what an operator compares against what
 * they expect the file to hold, and a file with one course in it is a wrong file that is visible here
 * rather than after the run. `blankSectionRows` and `placeholderInstructorRows` are the two known
 * shapes of "the registrar's export has gaps in it", counted so they are a stated fact rather than a
 * surprise in the warning column afterwards.
 */
export interface SisImportPreview {
  batch: SisImportBatch;
  /** The header row as parsed. The check that answers "is this even the roster workbook?". */
  columns: string[];
  distinctStudents: number;
  distinctColleges: number;
  distinctPrograms: number;
  distinctCourses: number;
  distinctSections: number;
  distinctInstructors: number;
  blankSectionRows: number;
  placeholderInstructorRows: number;
  /**
   * A handful of staged rows. Every one of them is `Pending` — nothing has run — so they carry no
   * outcome, and this client renders their row numbers and entity counts rather than their contents.
   */
  sampleRows: SisImportRow[];
}

/**
 * The body of `POST /sis/import/{batchId}/run`.
 *
 * **The term is required here even though the batch already carries one, and it is a confirmation
 * rather than a parameter.** It must match or the run is refused with a 409. ADR-001 D-5 names the
 * failure that guards: a wrong term selection misfiles an entire batch, is invisible afterwards
 * (every downstream query is term-scoped, so the data looks fine — it is simply in the wrong year),
 * and is only recoverable by hand.
 *
 * So this client sends the term the *operator chose at upload*, carried forward through the preview,
 * and never a term re-derived from anything else. Deriving it from the batch would make the
 * confirmation ask the same source twice and answer itself.
 */
export interface SisImportRunRequest {
  termId: string;
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

/**
 * The signed-in person, exactly as `AuthUserDto` describes them — the body of `GET /auth/me` and the
 * `user` member of every `POST /auth/login` and `POST /auth/refresh` reply.
 *
 * **`permissions` is what the presented token carries, not a fresh read of the database**, and the
 * DTO's own remarks are worth restating here because the difference is invisible until the day it
 * looks like a bug: a grant an administrator revoked one minute ago is still honoured by a token
 * minted two minutes ago, and this list still names it. That is deliberate. It makes the UI's model
 * of what it may do exactly the server's model for exactly as long as the token lives — the
 * alternative shows a button the server would refuse. A change lands at the next refresh, which is at
 * most one access-token lifetime away.
 *
 * `readonly string[]` rather than a union of the known codes. The server is authoritative and may
 * grant one this build has never heard of; a union would make that a type error at the boundary and
 * fail a sign-in over a permission the UI does not even use. The codes this SPA *asks about* are
 * named in `permissions.ts`.
 */
export interface AuthUser {
  id: string;
  schoolId: string;
  email: string;
  fullName: string;
  permissions: readonly string[];
}
