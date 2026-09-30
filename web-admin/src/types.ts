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

/**
 * `CardMatchDto` — one row of `GET /cards?cardUid=`, the admin's "whose card is this?" lookup.
 *
 * Deliberately its own type rather than `Card` plus a bolted-on student: the card is the subject of
 * this search and the student is a field on it (ADR-001 D-3), which is the opposite of every other
 * read on this API. `cardId` is what a caller would act on (it is `DELETE
 * /students/{id}/cards/{cardId}`'s id); `studentId` is what `GET /students/{id}` takes.
 */
export interface CardMatch {
  cardId: string;
  cardUid: string;
  label?: string;
  /** `false` is **deactivated**, not "not a match" — QA Q5: a withdrawn card must still resolve. */
  isActive: boolean;
  issuedAt: string;
  /** When it was withdrawn, or unset while active. */
  deactivatedAt?: string;
  studentId: string;
  studentNumber: string;
  fullName: string;
  studentStatus: string;
}

/**
 * One page of `GET /cards?cardUid=`. Kept as an envelope, like `StudentPage`, rather than unwrapped at
 * the seam: a UID can legitimately match far more cards than a lookup dialog should hold in memory at
 * once (a re-issued serial, a multi-campus install), so the caller needs `total`/`hasMore` to say so
 * rather than silently showing a partial answer as if it were complete.
 */
export interface CardSearchPage {
  cards: CardMatch[];
  page: number;
  pageSize: number;
  total: number;
}

/**
 * `StudentClassificationDto` — one classification a person holds, on one axis.
 *
 * `isActive` is the *classification's* state, not the assignment's: `false` means the vocabulary
 * entry has been retired since this person was filed under it, and per the contract it **must still
 * be rendered** — retiring a category leaves every existing assignment standing, so a picker that
 * dropped it would blank it on the next save. It is read-only here for the same reason `Student.cards`
 * is: this is what `GET /students/{studentId}/classifications` and every write on that surface return,
 * not something this client can construct.
 */
export interface StudentClassification {
  classificationId: string;
  name: string;
  /** `Student`, `Personnel`, `Friars` or `Special` — see `CLASSIFICATION_AXES`. `string` on the wire. */
  axis: string;
  isActive: boolean;
  assignedAt: string;
}

/**
 * `ClassificationDto` — one entry in the institution's classification vocabulary, as `GET
 * /classifications` lists it. What a picker is built from; not what a student holds — see
 * `StudentClassification` for that.
 */
export interface Classification {
  id: string;
  /** The display form, exactly as authored — `SUPERVISORY/MANAGERIAL`, slash and all. */
  name: string;
  nameKey: string;
  axis: string;
  /** `false` is retired: not offered for new assignments, but still held by whoever already has it. */
  isActive: boolean;
  retiredAt?: string;
  mergedIntoClassificationId?: string;
  studentCount: number;
}

/**
 * The body of a successful `PUT`/`DELETE /students/{studentId}/classifications/{classificationId}` —
 * `StudentClassificationWriteResult`. Both writes answer with the person's **whole** set after the
 * change, not just the row that moved, which is what lets a caller see that the other axes were left
 * alone.
 */
export interface StudentClassificationWriteResult {
  studentId: string;
  classifications: StudentClassification[];
  /** What this assignment displaced on its axis, or unset if the slot was empty. `PUT` only. */
  replacedClassificationId?: string;
  message: string;
}

/**
 * §4's four classification axes, verified against `ClassificationAxis` in
 * `EAMS.Domain/ClassificationEntities.cs`. A person holds at most one classification per axis and may
 * hold several axes at once (QA Q2) — this is the set a picker is built one-per-axis from.
 */
export const CLASSIFICATION_AXES = ["Student", "Personnel", "Friars", "Special"] as const;

export type ClassificationAxisName = (typeof CLASSIFICATION_AXES)[number];

/**
 * `EventClassificationDto` — one entry in the institution's *event*-classification vocabulary
 * (Institutional / Departmental / Organizational, extensible), as `GET /event-classifications` lists it.
 *
 * **Distinct from `Classification` above**, which classifies *people* (the access-control axis). They
 * share the word and nothing else — no rows, no route, no table.
 */
export interface EventClassification {
  id: string;
  /** The display form, exactly as authored. */
  name: string;
  nameKey: string;
  /** The optional description the spec lists as a field, or unset. */
  description?: string;
  /** `false` is deactivated: not offered for new events, but still held by every event recorded under it. */
  isActive: boolean;
  retiredAt?: string;
}

/**
 * The body of `POST /event-classifications` and `PUT /event-classifications/{id}` — one shape for both,
 * because the server checks them with one piece of code.
 *
 * `description` is `string | null` rather than optional, as the other write requests are: a `PUT` is a
 * full replacement, so an omitted description clears the stored one, and deciding at the construction
 * site is what stops an empty text box being sent as `""`. **`isActive` is deliberately absent** — moving
 * that flag is `PATCH /event-classifications/{id}/active`, the same split `TermWriteRequest` draws.
 */
export interface EventClassificationWriteRequest {
  name: string;
  description: string | null;
}

// ---------------------------------------------------------------------------------------------
// §11 User Management (UserWithRBAC.docx) — administrators only
// ---------------------------------------------------------------------------------------------

/** One role a user holds, or one offered by the role picker. */
export interface Role {
  id: string;
  name: string;
  description?: string;
  /** The four built-in roles are system roles; a future role editor refuses to delete one. */
  isSystem: boolean;
  /** Users in this school that hold the role. */
  userCount: number;
  /** The permission codes the role grants — shown so a picker can say what it does. */
  permissionCodes: string[];
}

/** One role reference on a user — an id/name pair so a picker round-trips the selection. */
export interface UserRoleRef {
  id: string;
  name: string;
}

/** `UserDto` — one user, as `GET /users` and `GET /users/{id}` publish them. */
export interface AdminUser {
  id: string;
  email: string;
  fullName: string;
  phone?: string;
  /** `false` is deactivated — cannot sign in; historical rows are kept. */
  isActive: boolean;
  lastLoginAt?: string;
  createdAt: string;
  roles: UserRoleRef[];
}

/**
 * The body of `POST /users`. The initial password is held to the server's length policy (12+). Further
 * roles are assigned after creation via `PUT /users/{id}/roles`.
 */
export interface UserCreateRequest {
  email: string;
  fullName: string;
  phone: string | null;
  roleName: string;
  password: string;
}

/** The body of `PUT /users/{id}` — the editable profile fields (not the e-mail). */
export interface UserUpdateRequest {
  fullName: string;
  phone: string | null;
}

/** The body of `POST /roles` and `PUT /roles/{id}` — one shape, because the server checks them alike. */
export interface RoleWriteRequest {
  name: string;
  description: string | null;
}

// ---------------------------------------------------------------------------------------------
// Clearance Checker (Clearance-Checker-Module.docx)
// ---------------------------------------------------------------------------------------------

/** The student info card at the top of a clearance report. No clearance status is computed. */
export interface ClearanceStudent {
  studentId: string;
  studentNumber: string;
  fullName: string;
  department?: string;
  program?: string;
  college?: string;
  yearLevel?: string;
  section?: string;
}

/** One event on a clearance report. `attendance` is Attended / Missed / Excused / Late. */
export interface ClearanceEvent {
  eventId: string;
  eventName: string;
  eventDate: string;
  attendance: string;
  checkInAt?: string;
  checkOutAt?: string;
}

export interface ClearanceReport {
  student: ClearanceStudent;
  events: ClearanceEvent[];
}

// ---------------------------------------------------------------------------------------------
// Academic Community — Personnel (StudentsEmployees.docx)
// ---------------------------------------------------------------------------------------------

/** `PersonnelDto` — one personnel (faculty/employee) record, as `GET /personnel` publishes it. */
export interface Personnel {
  id: string;
  personnelNumber: string;
  /** Composed by the server (`Last, First Middle`); read-only. */
  fullName: string;
  firstName: string;
  middleName?: string;
  lastName: string;
  email?: string;
  classification?: string;
  department?: string;
  organization?: string;
  position?: string;
  /** Normalized card serial (letters and digits, upper-cased), or unset. */
  rfidUid?: string;
  /** `Active` or `Inactive`. */
  status: string;
}

/** §11's two personnel statuses. Read to compare; `Personnel.status` stays `string` on the wire. */
export const PERSONNEL_STATUSES = ["Active", "Inactive"] as const;
export type PersonnelStatusName = (typeof PERSONNEL_STATUSES)[number];

/**
 * The body of `POST /personnel` and `PUT /personnel/{id}` — one shape, checked by one method. A full
 * replacement on update. Nullable strings are `string | null` (an omitted key and an explicit null mean
 * the same to the server); `rfidUid` is sent as read and normalized server-side.
 */
export interface PersonnelWriteRequest {
  personnelNumber: string;
  firstName: string;
  middleName: string | null;
  lastName: string;
  email: string | null;
  classification: string | null;
  department: string | null;
  organization: string | null;
  position: string | null;
  rfidUid: string | null;
  status: PersonnelStatusName;
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
  /**
   * What this person is classified as — a collection, because a person holds at most one
   * classification per axis and may hold several axes at once (QA Q2). Read-only, like `cards`:
   * assignment is `PUT`/`DELETE /students/{studentId}/classifications/{classificationId}`, one call
   * per axis, never a field of `StudentWriteRequest` — sending it back on `PUT /students/{id}` is
   * ignored exactly as `id` is.
   *
   * Empty is an ordinary state, not a null to guard against: most people hold exactly one, three
   * people in the sampled roster hold two, and 34 hold none.
   */
  classifications: StudentClassification[];
}

/**
 * One page of the roster, for the screen that shows it a page at a time.
 *
 * The only list type in this file that keeps its envelope. Every other read unwraps to a plain array
 * at the seam because its screen holds the whole set; this one cannot — the first real roster is
 * 21,493 students, an order of magnitude past what `MAX_LIST_ROWS` will hand a client-side grid, and
 * `api.ts` refuses it loudly rather than truncating.
 *
 * So the grid pages against the server, and to do that it needs two facts a bare array cannot carry:
 * which page these rows are, and how many rows exist behind the filter. Without `total` the grid has
 * no scrollbar to size and no last page to reach — it would render 25 rows and imply there are 25.
 */
export interface StudentPage {
  students: Student[];
  /**
   * The page actually served, 1-based, echoed by the server rather than assumed. A page number past
   * the end is clamped, not refused, so the echo is how a caller finds out it asked for page 900 of
   * 860 — reading back the number it sent would hide that.
   */
  page: number;
  /** The size actually applied. The server clamps an over-large request to its own maximum. */
  pageSize: number;
  /**
   * Every student matching the filter, not just the ones on this page. This is the number the grid's
   * `rowCount` needs, and it is the server's own count against the same filter that produced the rows.
   */
  total: number;
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
  /** Per-event grace before start (EventGracePeriod.docx §1), or unset for "no per-event limit". */
  graceBeforeStartMinutes?: number;
  /** Per-event grace after end (§3), or unset. */
  graceAfterEndMinutes?: number;
  requireRegistration: boolean;
  status: string; // Draft/Open/Closed/Cancelled
  /**
   * Whether this event issues certificates of attendance (client QA Q20, `#470` B4). A setting only —
   * nothing here sends a certificate anywhere yet; see `EmailCertificatesButton` in `EventDetail.tsx`.
   * Always present, never null: an event created before the setting existed reads `false` — see
   * `EventDto.IssuesCertificates`'s own note.
   */
  issuesCertificates: boolean;
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
  /**
   * Per-event grace before start / after end (EventGracePeriod.docx), or `null` for "no per-event
   * limit; the school-wide tap window governs". A full-replacement `PUT`, so send what you read.
   */
  graceBeforeStartMinutes: number | null;
  graceAfterEndMinutes: number | null;
  requireRegistration: boolean;
  /**
   * Whether the event issues certificates of attendance (client QA Q20, `#470` B4).
   *
   * The server's own `EventWriteRequest.IssuesCertificates` is `bool?` — omitted or `null` means
   * "leave it alone" on a `PUT` and "`false`" on a `POST` — because the field arrived after clients of
   * the `PUT` already existed, and a plain boolean would have reset every one of them to `false` on
   * their next ordinary save. This client is not one of those clients: every construction site here
   * (`eventDraft.ts`'s `validate` and `requestForCertificatesToggle`) fills it explicitly, so it stays
   * a required `boolean` rather than `boolean | null | undefined` — the omit-means-keep escape hatch
   * exists for callers this SPA is not, and typing it optional here would only invite a construction
   * site that forgets to set it and silently turns certificates off on every edit.
   */
  issuesCertificates: boolean;
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

/**
 * Live Attendance's search (client QA Q7), matching `GET /attendance`'s three optional query
 * parameters exactly. Each is a fragment — a "contains" match — and all three combine with AND; a
 * blank or absent field applies no filter for that column. `cardUid` is sent exactly as the operator
 * typed it: the server normalizes serials itself (uppercase, separators stripped), so normalizing it
 * here too would just be a second, possibly-diverging opinion of what the server already does.
 */
export interface AttendanceFilters {
  studentNumber?: string;
  studentName?: string;
  cardUid?: string;
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
   *
   * **Only meaningful once `isTerminal`.** It is `false` for every in-flight batch by construction —
   * a run half-way through `WritingFacts` has written some counters and not others — so a screen that
   * renders the "the counters do not add up" alert without gating on `isTerminal` shows an operator a
   * report-this-bug banner for the whole six minutes their perfectly healthy import is running.
   */
  countersReconcile: boolean;

  // -------------------------------------------------------------------------------------------
  // Progress — all optional, and the optionality is load-bearing
  // -------------------------------------------------------------------------------------------
  //
  // The run is detached: `POST /sis/import/{batchId}/run` answers 202 with a `Running` batch and the
  // work continues on the server, so `GET /sis/import/{batchId}` is what a screen watches. These seven
  // are how that watch is more than a spinner.
  //
  // **Any of them may be absent**, and there are two different reasons, neither of which is drift:
  // the batch ran before this server grew progress reporting, or the phase it is in has no truthful
  // unit count to give. Absent must therefore render as *indeterminate* — "reading the workbook" with
  // no numbers — and **never as 0**, which claims a run has done nothing when what is true is that
  // nobody is counting.

  /** One of `IMPORT_PHASES`; `string` on the wire, for the reason `EventItem.status` records. */
  progressPhase?: string;
  /** Which phase this is, 1-based. Paired with `progressPhaseCount` — one without the other says nothing. */
  progressPhaseNumber?: number;
  /** How many phases the run has. Grows when the server grows a phase; never hard-coded client-side. */
  progressPhaseCount?: number;
  /** Units finished *within the current phase*, not across the run. Resets each phase. */
  progressUnitsDone?: number;
  /** Units the current phase has to do, when the phase can say. Absent means indeterminate. */
  progressUnitsTotal?: number;
  /**
   * When the server last wrote any of the above.
   *
   * A different fact from `startedAt`, and the one that answers "is the *run* alive?" rather than
   * "how long have I been watching?". A screen that has been open eight minutes over a heartbeat
   * thirty seconds old is healthy; the same screen over a heartbeat four minutes old is not.
   */
  progressUpdatedAt?: string;
  /** Why a `Failed` run stopped, in the server's own words. Absent on every other status. */
  failureReason?: string;

  /**
   * Whether the run is over, **as the server says it is** — the poll's stop condition and the gate on
   * every counter this screen prints.
   *
   * Read rather than derived from `status` for the same reason `countersReconcile` is: the server owns
   * the set of statuses, and a client that recomputes terminality against a list it holds locally will
   * eventually meet a status it has never heard of and file it as unfinished — polling a batch that
   * finished, forever. `toImportBatch` does fall back to a local derivation when the field is missing
   * altogether, which is a *compatibility* path for a server that predates it, not a second opinion.
   */
  isTerminal: boolean;
}

/**
 * The phases `SisImportService` reports, in run order.
 *
 * Here rather than in `sisImport.ts` beside their labels because they are contract, not copy: this is
 * the server's list, and the labels are this screen's words for it. **Read these to compare, never to
 * type a received value** — the usual rule in this file, and it matters more than usual here, because
 * a phase this build has not heard of must still render (as itself) rather than fall through to
 * nothing.
 */
export const IMPORT_PHASES = {
  ClearingPreviousRun: "ClearingPreviousRun",
  ParsingRows: "ParsingRows",
  ResolvingDimensions: "ResolvingDimensions",
  ResolvingFacts: "ResolvingFacts",
  WritingFacts: "WritingFacts",
  RecordingRowResults: "RecordingRowResults",
  RefreshingStudentCache: "RefreshingStudentCache",
  SyncingStudentGroups: "SyncingStudentGroups",
  Done: "Done",
} as const;

export type ImportPhaseName = (typeof IMPORT_PHASES)[keyof typeof IMPORT_PHASES];

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
 * §6.7 `EventReportRowDto` — one event's line in the Reports module (client QA Q15/Q16). The same
 * figures `EventSummary` above carries, computed by the same server code, plus what a report needs
 * beside them: the event's `status` and `startAt`, and `attended` (the numerator of `attendanceRate`,
 * published so a multi-event total can be checked by hand).
 *
 * A closed or cancelled event's `expected` is the audience frozen when it closed — a later roster
 * import cannot move a past event's rate.
 */
export interface EventReportRow {
  eventId: string;
  eventName: string;
  /** `Draft`, `Open`, `Closed` or `Cancelled`. */
  status: string;
  /** When it starts, UTC ISO. */
  startAt: string;
  expected: number;
  /** Expected students with a `Present` or `Late` record — the numerator of `attendanceRate`. */
  attended: number;
  present: number;
  late: number;
  absent: number;
  excused: number;
  unexpected: number;
  attendanceRate: number;
}

/**
 * `EventReportTotalsDto` — the pooled totals of a multi-event report. **Pooled, not averaged**:
 * `attendanceRate` is `attended` ÷ `expected` across every picked event, so a 500-student assembly
 * weighs 500 times what a one-student event does. Reproducible from `MultiEventReport.events` by
 * summing `attended` and `expected` — never recompute it a different way (it is server-rounded to
 * one decimal place the same way each row is).
 */
export interface EventReportTotals {
  /** Distinct events in the report. */
  eventCount: number;
  expected: number;
  attended: number;
  present: number;
  late: number;
  absent: number;
  excused: number;
  unexpected: number;
  attendanceRate: number;
}

/**
 * `MultiEventReportDto` — `GET /reports/events/summary`'s body: one row per distinct event, ordered
 * by start time, and the pooled totals across all of them.
 */
export interface MultiEventReport {
  events: EventReportRow[];
  totals: EventReportTotals;
}

/**
 * `EAMS.Application.Dtos.ReportLimits.MaxEventsPerReport` (`backend/EAMS.Application/Dtos/ReportDtos.cs`),
 * restated here rather than discovered from a refusal: at least one id and at most this many distinct
 * ids may be reported on at once; the server refuses a larger selection with 400 `SelectionTooLarge`
 * rather than truncating it. The Reports page stops an operator selecting a 51st rather than letting
 * them find out from a refusal.
 *
 * **This cap is also bounded by URL length, and the two numbers are coupled.** The ids travel as a
 * repeated `eventId` query parameter (`multiEventReportSummary` in `api.ts`), and IIS refuses a
 * request whose query string exceeds its `maxQueryString` limit — raised to 4096 in the API's
 * `web.config` specifically to clear 50 UUIDs' worth of `&eventId=…` pairs. Raising this constant
 * without also re-checking (and, if needed, raising) that IIS setting reintroduces the "IIS rejects
 * the request before this client's own 400 ever fires" failure the reviewer flagged as CRITICAL.
 */
export const MAX_REPORT_EVENTS = 50;

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

/**
 * One scan that reached the server at an event and resolved to no student —
 * `GET /events/{id}/scans`.
 *
 * Every row here is a scan that appears in no other record. A scan whose card resolves becomes an
 * attendance row and is reported by the roster; this is the remainder, which until recently existed
 * only as an HTTP response nobody kept.
 */
export interface EventScan {
  /** The normalized serial the device sent. It matched no active card at the moment the tap was decided. */
  cardUid: string;
  /**
   * The device's own `tappedAt` — when the person was standing there, not when the queue arrived.
   * For an offline flush the two differ by however long the device was away.
   */
  scannedAt: string;
  /** When the server wrote the row. Compare with `scannedAt` to see flush lag. */
  recordedAt: string;
  /** The device's idempotency key, where it sent one. */
  deviceTapId?: string;
  deviceId?: string;
  /** What the server decided. `CardNotFound` today. */
  serverOutcome: string;
  /**
   * What the *device* claimed, verbatim and often absent. Never authoritative — its value is in
   * disagreeing with `serverOutcome`, which points at a stale manifest, a sync that never ran, or a
   * cloned card.
   */
  localOutcome?: string;
}

/** The unresolved scans for one event, with the counts a reader needs before opening the list. */
export interface EventScanLog {
  eventId: string;
  /** Every unresolved scan, including repeat presentations of one card. */
  totalScans: number;
  /**
   * How many different cards those scans represent. Reported alongside `totalScans` because the gap
   * between them is itself the finding: forty scans over three cards is somebody retrying a card that
   * is not working; forty over forty is a roster that has not been given its RFID column.
   */
  distinctCards: number;
  scans: EventScan[];
}
