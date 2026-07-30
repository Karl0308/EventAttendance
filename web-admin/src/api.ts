// The single seam to the EAMS .NET API (`EAMS.Api`, base path `/api/v1`). Components import `api`
// and nothing reaches around it.
//
// Shapes are pinned to the committed contract at `docs/api/openapi.json` — camelCase JSON, RFC 7807
// `application/problem+json` on every error. Responses are narrowed field by field on the way in:
// this is a system boundary, so a payload that drifts from the contract fails loud here instead of
// arriving three components deep as `undefined`.

import type {
  Student,
  Card,
  EventItem,
  AttendanceRecord,
  EventSummary,
} from "./types";

// The URL `dotnet run --project backend/EAMS.Api --urls "http://localhost:5080"` serves. Overridden
// per-machine by VITE_API_BASE_URL (see `.env.example`) — the capture device's host is DHCP.
const DEFAULT_BASE_URL = "http://localhost:5080/api/v1";

const baseUrl = (import.meta.env.VITE_API_BASE_URL ?? DEFAULT_BASE_URL).replace(/\/+$/, "");

// ---------------------------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------------------------

/**
 * `network` — never reached the server. `http` — server refused. `malformed` — reply off-contract.
 * `too-large` — the list is real and well-formed but has more pages than this seam will walk; see
 * `MAX_LIST_REQUESTS`. It is a client-side ceiling, not a server fault, and retrying cannot clear it.
 */
export type ApiErrorKind = "network" | "http" | "malformed" | "too-large";

/** RFC 7807 body as this API emits it. `title`/`detail` are prose and are reworded freely. */
export interface ApiProblem {
  status: number;
  type?: string;
  title?: string;
  detail?: string;
  instance?: string;
  /** The correlation handle. On every error body this API produces. Quote it when reporting. */
  traceId?: string;
  /**
   * The stable machine-readable outcome token — **branch on this and on `status`, never on
   * `title`/`detail`**. Present on the attendance, students and devices surfaces and on auth /
   * rate-limit refusals; absent on the events write surface.
   */
  code?: string;
  /** Server UTC clock. Present on tap and manual-override failures. */
  serverTime?: string;
}

export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  /** HTTP status, or 0 when the failure happened before/outside a response. */
  readonly status: number;
  readonly code: string | undefined;
  readonly traceId: string | undefined;
  readonly problem: ApiProblem | undefined;

  constructor(
    kind: ApiErrorKind,
    status: number,
    message: string,
    problem?: ApiProblem,
    cause?: unknown,
  ) {
    super(message, { cause });
    this.name = "ApiError";
    this.kind = kind;
    this.status = status;
    this.code = problem?.code;
    this.traceId = problem?.traceId;
    this.problem = problem;
  }
}

/** Turns anything thrown by this module into one truthful line a component can render. */
export function describeApiError(error: unknown): string {
  if (error instanceof ApiError) {
    return error.traceId ? `${error.message} (traceId ${error.traceId})` : error.message;
  }
  if (error instanceof Error) return error.message;
  return "Unexpected error.";
}

// ---------------------------------------------------------------------------------------------
// Narrowing helpers — the whole boundary check lives here, so no cast hides contract drift
// ---------------------------------------------------------------------------------------------

type Row = Record<string, unknown>;

const isRow = (value: unknown): value is Row =>
  typeof value === "object" && value !== null && !Array.isArray(value);

const offContract = (what: string, detail: string) =>
  new ApiError("malformed", 0, `${what} is off-contract: ${detail}.`);

function asRow(value: unknown, what: string): Row {
  if (!isRow(value)) throw offContract(what, `expected an object, got ${describeType(value)}`);
  return value;
}

function asRows(value: unknown, what: string): Row[] {
  if (!Array.isArray(value)) throw offContract(what, `expected an array, got ${describeType(value)}`);
  return value.map((item, i) => asRow(item, `${what}[${i}]`));
}

const describeType = (value: unknown) => (value === null ? "null" : typeof value);

function reqStr(row: Row, key: string, what: string): string {
  const value = row[key];
  if (typeof value !== "string") {
    throw offContract(what, `\`${key}\` should be a string, got ${describeType(value)}`);
  }
  return value;
}

function reqNum(row: Row, key: string, what: string): number {
  const value = row[key];
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw offContract(what, `\`${key}\` should be a finite number, got ${describeType(value)}`);
  }
  return value;
}

function reqBool(row: Row, key: string, what: string): boolean {
  const value = row[key];
  if (typeof value !== "boolean") {
    throw offContract(what, `\`${key}\` should be a boolean, got ${describeType(value)}`);
  }
  return value;
}

/** Nullable-in-contract fields: JSON `null` and an absent key both collapse to `undefined`. */
const optStr = (value: unknown): string | undefined =>
  typeof value === "string" ? value : undefined;

// ---------------------------------------------------------------------------------------------
// Transport
// ---------------------------------------------------------------------------------------------

function buildUrl(path: string, query?: Record<string, string | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query ?? {})) {
    if (value !== undefined && value !== "") params.set(key, value);
  }
  const qs = params.toString();
  return `${baseUrl}${path}${qs ? `?${qs}` : ""}`;
}

async function readProblem(res: Response): Promise<ApiProblem> {
  let body: unknown;
  try {
    body = await res.json();
  } catch {
    // A non-JSON error body (proxy HTML, an empty 502) is itself information, but it is not fatal
    // and it must not mask the status. Nothing is swallowed: the caller still gets an ApiError
    // built from `res.status` below.
    body = undefined;
  }
  const row = isRow(body) ? body : {};
  return {
    status: res.status,
    type: optStr(row.type),
    title: optStr(row.title),
    detail: optStr(row.detail),
    instance: optStr(row.instance),
    traceId: optStr(row.traceId),
    code: optStr(row.code),
    serverTime: optStr(row.serverTime),
  };
}

function httpError(what: string, problem: ApiProblem): ApiError {
  const reason = problem.code ?? problem.title ?? problem.detail ?? "no detail supplied";
  return new ApiError("http", problem.status, `${what} failed (${problem.status}: ${reason}).`, problem);
}

async function send(what: string, path: string, query?: Record<string, string | undefined>) {
  try {
    return await fetch(buildUrl(path, query), {
      headers: { Accept: "application/json" },
    });
  } catch (cause) {
    throw new ApiError(
      "network",
      0,
      `Cannot reach the EAMS API at ${baseUrl} — ${what} was not sent.`,
      undefined,
      cause,
    );
  }
}

async function parseBody(res: Response, what: string): Promise<unknown> {
  try {
    return await res.json();
  } catch (cause) {
    throw new ApiError("malformed", res.status, `${what} returned a body that is not JSON.`, undefined, cause);
  }
}

async function getJson(what: string, path: string, query?: Record<string, string | undefined>) {
  const res = await send(what, path, query);
  if (!res.ok) throw httpError(what, await readProblem(res));
  return parseBody(res, what);
}

/**
 * As `getJson`, but a 404 means "no such row" rather than a failure — the three lookup methods are
 * typed `T | undefined` and the pages already render a loading/empty state for it. Every other
 * non-2xx still throws.
 */
async function getJsonOrMissing(
  what: string,
  path: string,
): Promise<unknown | undefined> {
  const res = await send(what, path);
  if (res.status === 404) return undefined;
  if (!res.ok) throw httpError(what, await readProblem(res));
  return parseBody(res, what);
}

// ---------------------------------------------------------------------------------------------
// Paging — the envelope stops here
// ---------------------------------------------------------------------------------------------
//
// Every §6.2/§6.3 admin list now answers with `<Dto>PagedResult` instead of a bare array:
// `{ items, page, pageSize, total, hasMore }`, `?page=` 1-based, `?pageSize=` defaulting to 50.
// This seam unwraps it and keeps handing components plain arrays (see the per-method notes), so a
// page of 50 must not be mistaken for the whole list: against the dev database `GET /students`
// answers `total: 61, hasMore: true` with 50 rows, and taking that first page as the answer would
// drop eleven students with nothing on screen saying so.
//
// `GET /attendance/live/{eventId}` is deliberately NOT in here. It pages by cursor
// (`since`/`cursor`/`hasMore`/`pollAfterSeconds`) as frozen published contract, its `hasMore` answers
// a different question, and routing it through `listAll` would be wrong twice over. It has no caller
// in this SPA today; if one is added it gets its own reader.

/** One page as it arrives. Internal: no component sees this type. */
interface PageOf<T> {
  items: T[];
  /** The page actually served — echoed back, so a clamp is visible rather than silent. */
  page: number;
  pageSize: number;
  total: number;
  hasMore: boolean;
}

/**
 * The ceiling that matters: how many rows this SPA will hold in memory and hand to a client-side
 * grid. Read from `total` on the first page, so one request settles it.
 *
 * It is expressed in rows because rows are what the limit is actually about. An earlier version
 * bounded *requests* instead and claimed that avoided restating the server's page size — it did not,
 * it hid it: a 40-request bound means 40 × whatever `Paging.DefaultPageSize` happens to be, so a
 * server-side change from 50 to 20 would have quietly cut this client's protection from 2000 rows to
 * 800 with no code change and no failing test. A row count means the same thing whatever the server
 * pages by. Crossing it is a design signal — that list has outgrown load-everything and needs
 * server-side paging — so it fails loud instead of truncating.
 */
const MAX_LIST_ROWS = 2000;

/**
 * Asked of every list read. This is a client saying "give me a big page", not a copy of the server's
 * cap: the contract guarantees an over-sized `?pageSize=` is **clamped rather than refused**, and the
 * reply echoes the size actually applied, so the server stays the authority on its own maximum. The
 * alternative — sending no `pageSize` and taking the default — cost four times the round trips to
 * dodge a coupling it did not actually dodge, and those trips are sequential: unnoticeable on a LAN,
 * seconds of blank dashboard over a real network.
 */
const REQUESTED_PAGE_SIZE = 200;

/**
 * Backstop, not the design. `MAX_LIST_ROWS` is what enforces the ceiling; this only stops an
 * unbounded loop if a server ever reports `hasMore` forever, which no correct one does.
 */
const MAX_LIST_REQUESTS = 40;

/** `?page=` is 1-based, per the contract. Named so the offset convention is stated, not remembered. */
const FIRST_PAGE = 1;

/**
 * The smallest page that still carries a `total` — for reads that want the count and not the rows.
 * One row comes back and is thrown away; `items` is still validated, so the shape check is not lost.
 */
const COUNT_ONLY_PAGE_SIZE = 1;

const tooLarge = (what: string, why: string) =>
  new ApiError(
    "too-large",
    0,
    `${what} is too large to show in full: ${why}. The list needs server-side paging.`,
  );

function asPage<T>(
  body: unknown,
  what: string,
  mapItem: (row: Row, what: string) => T,
): PageOf<T> {
  const row = asRow(body, what);
  return {
    items: asRows(row.items, `${what}.items`).map((item, i) =>
      mapItem(item, `${what}.items[${i}]`),
    ),
    page: reqNum(row, "page", what),
    pageSize: reqNum(row, "pageSize", what),
    total: reqNum(row, "total", what),
    hasMore: reqBool(row, "hasMore", what),
  };
}

/**
 * Walks every page of a list and returns the rows, so callers keep the whole-list semantics they had
 * before the envelope landed. Sequential by necessity: `hasMore` for page N only arrives with page N.
 *
 * `request` doubles as the page number rather than stepping from the `page` the server echoed. The
 * echo is the more trusting option in the one way that matters: a server that clamped a page number
 * from above would pin the echo and this loop would ask for the same page until the backstop fired,
 * where a counter of our own runs past the end and terminates on the empty page's `hasMore: false`.
 */
async function listAll<T>(
  what: string,
  path: string,
  query: Record<string, string | undefined>,
  mapItem: (row: Row, what: string) => T,
): Promise<T[]> {
  const rows: T[] = [];

  for (let request = FIRST_PAGE; request <= MAX_LIST_REQUESTS; request++) {
    const page = asPage(
      await getJson(what, path, {
        ...query,
        page: String(request),
        pageSize: String(REQUESTED_PAGE_SIZE),
      }),
      what,
      mapItem,
    );

    // Checked before the rows are kept, and on every page rather than only the first: `total` is the
    // server's own count against this filter, so the refusal costs one request, not forty.
    if (page.total > MAX_LIST_ROWS) {
      throw tooLarge(what, `it holds ${page.total} rows and this screen shows at most ${MAX_LIST_ROWS}`);
    }

    rows.push(...page.items);
    if (!page.hasMore) return rows;
  }

  throw tooLarge(what, `walking it did not reach the end within ${MAX_LIST_REQUESTS} requests`);
}

// ---------------------------------------------------------------------------------------------
// DTO mappers
// ---------------------------------------------------------------------------------------------

function toCard(row: Row, what: string): Card {
  return {
    id: reqStr(row, "id", what),
    cardUid: reqStr(row, "cardUid", what),
    label: optStr(row.label),
    isActive: reqBool(row, "isActive", what),
  };
}

function toStudent(row: Row, what: string): Student {
  // StudentDto also carries firstName / middleName / lastName / gender / photoUrl. Nothing in the
  // SPA reads them, so they are dropped here rather than widened into `Student`.
  return {
    id: reqStr(row, "id", what),
    studentNumber: reqStr(row, "studentNumber", what),
    fullName: reqStr(row, "fullName", what),
    email: optStr(row.email),
    course: optStr(row.course),
    yearLevel: optStr(row.yearLevel),
    section: optStr(row.section),
    status: reqStr(row, "status", what),
    cards: asRows(row.cards, `${what}.cards`).map((card, i) => toCard(card, `${what}.cards[${i}]`)),
  };
}

function toEvent(row: Row, what: string): EventItem {
  // EventDto also carries description / requireRegistration — unused by the SPA today.
  return {
    id: reqStr(row, "id", what),
    name: reqStr(row, "name", what),
    location: optStr(row.location),
    startAt: reqStr(row, "startAt", what),
    endAt: reqStr(row, "endAt", what),
    attendanceMode: reqStr(row, "attendanceMode", what),
    graceMinutes: reqNum(row, "graceMinutes", what),
    status: reqStr(row, "status", what),
  };
}

function toAttendance(row: Row, what: string): AttendanceRecord {
  return {
    id: reqStr(row, "id", what),
    eventId: reqStr(row, "eventId", what),
    studentId: reqStr(row, "studentId", what),
    studentName: reqStr(row, "studentName", what),
    studentNumber: reqStr(row, "studentNumber", what),
    checkInAt: optStr(row.checkInAt),
    checkOutAt: optStr(row.checkOutAt),
    status: reqStr(row, "status", what),
    captureMethod: reqStr(row, "captureMethod", what),
  };
}

function toSummary(row: Row, what: string): EventSummary {
  return {
    eventId: reqStr(row, "eventId", what),
    eventName: reqStr(row, "eventName", what),
    expected: reqNum(row, "expected", what),
    present: reqNum(row, "present", what),
    late: reqNum(row, "late", what),
    absent: reqNum(row, "absent", what),
    excused: reqNum(row, "excused", what),
    unexpected: reqNum(row, "unexpected", what),
    attendanceRate: reqNum(row, "attendanceRate", what),
  };
}

// ---------------------------------------------------------------------------------------------
// Methods
// ---------------------------------------------------------------------------------------------

async function listStudents(filter?: {
  search?: string;
  course?: string;
  status?: string;
}): Promise<Student[]> {
  // WARNING — `course` is a LOSSY filter. It maps to `GET /students?course=`, which filters
  // `Students.Course`: the ADR-001 D-2 derived, read-only cache. That column is single-valued, so
  // for a student enrolled in more than one section it can only name one of them. The filter
  // therefore returns a plausible, non-empty, INCOMPLETE answer — 12 of 52 students in the real
  // roster (~23%) sit in 2+ sections. Wired because the signature has always had it; do not build
  // UI that treats the result as the full set for a course.
  //
  // Unwrapped at the seam: `Students.tsx` filters and pages its grid client-side over the rows it
  // holds, and `Dashboard.tsx` counts them, so both need the whole set rather than a window into it.
  const what = "GET /students";
  return listAll(
    what,
    "/students",
    { search: filter?.search, course: filter?.course, status: filter?.status },
    toStudent,
  );
}

/**
 * How many students there are, without the students.
 *
 * `total` is the server's own count against the same filter that would have produced the rows, so
 * this is one request and no DTO mapping — where `listStudents()` walks every page and maps every
 * row, each with its `cards` array, to produce the same integer. It returns a `number`, so the
 * Dashboard's KPI gets its value without paging following it into the component.
 */
async function countStudents(): Promise<number> {
  const what = "GET /students (count)";
  const body = await getJson(what, "/students", {
    page: String(FIRST_PAGE),
    pageSize: String(COUNT_ONLY_PAGE_SIZE),
  });
  return asPage(body, what, () => null).total;
}

async function getStudent(id: string): Promise<Student | undefined> {
  const what = "GET /students/{id}";
  const body = await getJsonOrMissing(what, `/students/${encodeURIComponent(id)}`);
  return body === undefined ? undefined : toStudent(asRow(body, what), what);
}

async function listEvents(status?: string): Promise<EventItem[]> {
  // Unwrapped at the seam. The sort below is the reason it has to be: ordering one page of a paged
  // list would put "newest" only within that page, which looks right and is not.
  const what = "GET /events";
  const events = await listAll(what, "/events", { status }, toEvent);
  // Newest first — the order the dashboard and the events list have always rendered. The contract
  // makes no ordering promise, so it stays a client concern rather than an assumption.
  return events.sort((a, b) => b.startAt.localeCompare(a.startAt));
}

async function getEvent(id: string): Promise<EventItem | undefined> {
  const what = "GET /events/{id}";
  const body = await getJsonOrMissing(what, `/events/${encodeURIComponent(id)}`);
  return body === undefined ? undefined : toEvent(asRow(body, what), what);
}

async function listAttendance(eventId: string): Promise<AttendanceRecord[]> {
  // Unwrapped at the seam. `EventDetail.tsx` prints `Live attendance ({records.length})` next to the
  // grid, so a partial page would render a count that contradicts the event summary beside it.
  const what = "GET /attendance";
  return listAll(what, "/attendance", { eventId }, toAttendance);
}

async function eventSummary(eventId: string): Promise<EventSummary | undefined> {
  const what = "GET /events/{id}/summary";
  const body = await getJsonOrMissing(what, `/events/${encodeURIComponent(eventId)}/summary`);
  return body === undefined ? undefined : toSummary(asRow(body, what), what);
}

/**
 * Explanation of why an admin-browser tap is refused. One string, one place — the message the
 * Snackbar shows and the reason in the report are the same text.
 */
const TAP_UNAVAILABLE =
  "Simulated taps are disabled against the real API: POST /attendance/tap is the capture surface " +
  "and requires a DeviceKey, which the admin SPA deliberately does not hold.";

/**
 * NOT WIRED — deliberately, and this is a finding rather than an oversight.
 *
 * `POST /api/v1/attendance/tap` is secured by the `DeviceKey` scheme
 * (`Authorization: DeviceKey eams_dk_<keyId>_<secret>`), a credential the plan's §11 scopes to
 * `attendance.capture` and nothing else. Putting one in this bundle would hand a page that can only
 * *display* attendance a key that can *write* it — the precise inversion the contract rejects in its
 * own note on `GET /attendance/live/{eventId}` — and a Vite env var is inlined at build time, so the
 * GitHub Pages artefact would publish a write credential as a static asset.
 *
 * Returns a truthful refusal rather than throwing, so `EventDetail`'s existing Snackbar says
 * something honest instead of the button appearing to do nothing. `POST /attendance/manual` is the
 * admin-scoped alternative (open, no device key) but records `captureMethod: Manual` and skips the
 * grace-period logic, so substituting it silently would change behaviour — that needs JJ's call.
 */
async function tap(_eventId: string, _cardUid: string): Promise<{ ok: boolean; message: string }> {
  return { ok: false, message: TAP_UNAVAILABLE };
}

/**
 * No single endpoint answers this, so it is composed from two real ones rather than an invented
 * third. `GET /events/{id}/roster` knows who is *expected*, which would be the better source, but
 * `EventRosterEntryDto` carries no card UID at all — it cannot drive a UID picker.
 */
async function untappedCards(eventId: string): Promise<{ uid: string; name: string }[]> {
  const [students, recorded] = await Promise.all([listStudents(), listAttendance(eventId)]);
  const tapped = new Set(recorded.map((record) => record.studentId));
  return students.flatMap((student) => {
    if (tapped.has(student.id)) return [];
    // Only active cards: an inactive UID is a guaranteed `CardNotFound`, so offering it would put a
    // choice in the picker that cannot succeed.
    const card = student.cards.find((c) => c.isActive);
    return card ? [{ uid: card.cardUid, name: student.fullName }] : [];
  });
}

export const api = {
  listStudents,
  countStudents,
  getStudent,
  listEvents,
  getEvent,
  listAttendance,
  eventSummary,
  tap,
  untappedCards,
};
